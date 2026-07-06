using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using GameLauncher.Models;

namespace GameLauncher.Services
{
    /// <summary>
    /// Wraps the ludusavi CLI to back up / restore game saves into the
    /// per-user Game.OS save folder:
    ///   <c>Data/{username}/GameSaves/{platform}/{gameTitle}/</c>
    ///
    /// <para>
    /// For emulator games (Xbox 360/Xenia, PS3/RPCS3, Switch/Ryujinx, etc.)
    /// whose save folder is resolved by <see cref="EmulatorSavePathResolver"/>,
    /// the service first registers the game in a Game.OS-owned secondary ludusavi
    /// manifest via <see cref="LudusaviConfigService.TryRegisterGameSave"/>.
    /// Ludusavi then handles backup/restore — and its own cloud-sync features —
    /// exactly as it does for PC games.  If ludusavi is not installed the service
    /// falls back to a plain directory copy.
    /// </para>
    ///
    /// Ludusavi project: https://github.com/mtkennerly/ludusavi
    /// </summary>
    public static class LudusaviService
    {
        // ── Constants ─────────────────────────────────────────────────────────

        /// <summary>Maximum time to wait for a single ludusavi backup operation.</summary>
        private static readonly TimeSpan BackupTimeout = TimeSpan.FromMinutes(5);
        // Keep fallback window short so accidental second presses do not auto-approve much later.
        private static readonly TimeSpan ApprovalFallbackWindow = TimeSpan.FromSeconds(20);
        private static readonly ConcurrentDictionary<string, DateTimeOffset> PendingApprovalTokens = new();
        private static readonly string GamesDbCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GameOS", "GamesDbCache");

        // ── Result discriminated union ─────────────────────────────────────────

        public enum ResultKind { Synced, NoSaveFound, NotInstalled, Cancelled, Error }

        public sealed class LudusaviResult
        {
            public ResultKind Kind    { get; }
            public string     Message { get; }

            private LudusaviResult(ResultKind kind, string message)
            {
                Kind    = kind;
                Message = message;
            }

            public static LudusaviResult Synced       => new(ResultKind.Synced,       "");
            public static LudusaviResult NoSaveFound  => new(ResultKind.NoSaveFound,  "");
            public static LudusaviResult NotInstalled => new(ResultKind.NotInstalled, "");
            public static LudusaviResult Cancelled(string msg = "Operation cancelled.") =>
                new(ResultKind.Cancelled, msg);
            public static LudusaviResult Error(string msg) =>
                new(ResultKind.Error, msg ?? "Unknown error");
        }

        /// <summary>
        /// Optional platform-native confirmation callback.
        /// Return <see langword="true"/> to approve, <see langword="false"/> to deny,
        /// or <see langword="null"/> when native confirmation is unavailable.
        /// </summary>
        public static Func<string, string, Task<bool?>>? RequestNativeConfirmationAsync { get; set; }

        // ── Public API ─────────────────────────────────────────────────────────

        /// <summary>
        /// Looks for a configured ludusavi executable first in <see cref="AppSettings.LudusaviPath"/>,
        /// then attempts a plain <c>ludusavi</c> command that relies on PATH.
        /// Returns the resolved executable path, or <c>"ludusavi"</c> as a PATH fallback.
        /// </summary>
        public static string GetLudusaviExePath()
        {
            var settings = AppSettingsService.Load();
            if (!string.IsNullOrWhiteSpace(settings.LudusaviPath)
                && File.Exists(settings.LudusaviPath))
                return settings.LudusaviPath.Trim();

            if (!string.IsNullOrWhiteSpace(settings.LudusaviPath))
                DevLogService.Log($"[Ludusavi] Configured executable not found: {settings.LudusaviPath}");

            // Fall back to relying on PATH
            return "ludusavi";
        }

        /// <summary>
        /// Runs <c>ludusavi backup --path "&lt;savePath&gt;" "&lt;gameTitle&gt;"</c> and
        /// backs up the game's saves into the per-user Game.OS save directory.
        ///
        /// <para>
        /// When <paramref name="sourceOverridePath"/> is provided and the directory
        /// exists on disk, the method copies save files directly from that folder
        /// (bypassing ludusavi's manifest lookup entirely).  If ludusavi is not
        /// installed, a plain <see cref="Directory"/> copy is used as the fallback.
        /// </para>
        /// </summary>
        /// <param name="platform">Platform name, e.g. "PC", "Switch".</param>
        /// <param name="gameTitle">Display title of the game.</param>
        /// <param name="username">Logged-in Game.OS username — determines the target save path.</param>
        /// <param name="sourceOverridePath">
        ///   Optional: the resolved emulator save folder for the specific game
        ///   (from <see cref="EmulatorSavePathResolver.Resolve"/>).  When set and
        ///   the folder exists, files are copied directly instead of calling
        ///   ludusavi's title-name lookup.
        /// </param>
        public static async Task<LudusaviResult> SyncAsync(
            string  platform,
            string  gameTitle,
            string  username,
            string? sourceOverridePath = null,
            string? titleId            = null)
        {
            if (string.IsNullOrWhiteSpace(username))
                return LudusaviResult.Error("No user is logged in.");

            if (string.IsNullOrWhiteSpace(gameTitle))
                return LudusaviResult.Error("Game title is required.");

            var approval = await EnsureOperationApprovedAsync("backup", platform, gameTitle, username);
            if (!approval.Approved)
                return LudusaviResult.Cancelled(approval.Message);

            // Build the per-user per-game save path and ensure the directory exists.
            // When a TitleID is supplied the saves are stored in a TitleID sub-folder so
            // that multiple games with the same title (or the same game on different
            // emulators) cannot collide:
            //   Data/{username}/GameSaves/{platform}/{gameTitle}/{titleId}/
            //
            // For Xbox 360 / Xenia games: if no explicit TitleID was provided but
            // sourceOverridePath was resolved (e.g. Content/{profile}/{titleId}/00000001/),
            // extract the TitleID from that path so the backup is always placed in a
            // per-TitleID sub-folder.  This prevents collisions when multiple games share
            // the same title but use different TitleIDs.
            string platformSavesRoot = UserDataService.GetGameSavesPath(username, platform);
            string safeGameTitle     = StorageHelpers.SanitiseName(gameTitle);

            if (string.IsNullOrWhiteSpace(titleId) &&
                !string.IsNullOrWhiteSpace(sourceOverridePath))
            {
                string? extracted = TryExtractXeniaTitleId(sourceOverridePath);
                if (!string.IsNullOrWhiteSpace(extracted))
                    titleId = extracted;
            }

            var backupTitleIds = ResolveKnownTitleIds(platform, gameTitle, titleId);
            string gameSavePath = backupTitleIds.Count > 0
                ? Path.Combine(platformSavesRoot, safeGameTitle, backupTitleIds[0])
                : !string.IsNullOrWhiteSpace(titleId)
                    ? Path.Combine(platformSavesRoot, safeGameTitle, titleId.Trim())
                    : Path.Combine(platformSavesRoot, safeGameTitle);

            try
            {
                Directory.CreateDirectory(gameSavePath);
            }
            catch (Exception ex)
            {
                return LudusaviResult.Error($"Cannot create save directory: {ex.Message}");
            }

            // ── TitleID-based emulator save registration ───────────────────────
            // When the caller has resolved the exact emulator save folder,
            // register the game in ludusavi's secondary manifest so that ludusavi
            // can find the saves on its own — enabling its cloud-sync features.
            // Then let ludusavi run normally.  If ludusavi is not installed we
            // fall back to a plain directory copy.
            if (!string.IsNullOrWhiteSpace(sourceOverridePath))
            {
                string effectiveSourceOverridePath = sourceOverridePath;
                if (!Directory.Exists(effectiveSourceOverridePath) &&
                    ShouldMirrorXbox360TitleIds(platform, backupTitleIds))
                {
                    string? altXboxPath = TryResolveAlternateXbox360SavePath(
                        effectiveSourceOverridePath, backupTitleIds);
                    if (!string.IsNullOrWhiteSpace(altXboxPath))
                        effectiveSourceOverridePath = altXboxPath;
                }

                if (Directory.Exists(effectiveSourceOverridePath))
                {
                    if (ShouldMirrorXbox360TitleIds(platform, backupTitleIds))
                        return await CopyDirectoryToTitleIdFoldersAsync(
                            effectiveSourceOverridePath, platformSavesRoot, safeGameTitle, backupTitleIds, gameTitle);

                    // Register so ludusavi knows where this emulator game's saves live.
                    LudusaviConfigService.TryRegisterGameSave(gameTitle, effectiveSourceOverridePath);

                    // Let ludusavi perform the backup (it now has a manifest entry).
                    var result = await RunLudusaviBackupAsync(gameTitle, gameSavePath);

                    // Prefer ludusavi when it succeeds.
                    if (result.Kind == ResultKind.Synced)
                    {
                        if (DirectoryHasAnyFiles(gameSavePath))
                            return result;

                        // Some emulator entries can return a successful ludusavi exit without
                        // actually copying any files. Fall back to direct copy when source files exist.
                        if (DirectoryHasAnyFiles(effectiveSourceOverridePath))
                        {
                            DevLogService.Log(
                                "[Ludusavi] backup reported success but destination is empty; falling back to direct copy.");
                            return await CopyDirectoryAsync(effectiveSourceOverridePath, gameSavePath, gameTitle);
                        }

                        return LudusaviResult.NoSaveFound;
                    }

                    // If ludusavi is unavailable or cannot identify the game by title,
                    // copy directly from the resolved emulator save folder.
                    if (result.Kind is ResultKind.NotInstalled or ResultKind.NoSaveFound)
                    {
                        DevLogService.Log(
                            $"[Ludusavi] backup returned {result.Kind}; falling back to direct copy.");
                        return await CopyDirectoryAsync(effectiveSourceOverridePath, gameSavePath, gameTitle);
                    }

                    // For other failures (permission/path/process), surface the error.
                    return result;
                }

                // Path was resolved but the folder doesn't exist yet —
                // no saves to back up; do NOT fall through to ludusavi for emulator games.
                return LudusaviResult.NoSaveFound;
            }

            // ── Non-PC emulator games without a resolved save path ─────────────
            // Ludusavi's manifest only covers PC game saves.  For emulator games
            // (Xbox 360, PS3, Switch, etc.) with no TitleID or save-data root
            // configured, calling ludusavi would produce a useless "No info for
            // these games" error.  Direct the user to configure the emulator
            // settings instead.
            if (IsEmulatorPlatform(platform))
                return LudusaviResult.Error(
                    "Set the emulator save folder in ⚙ Settings to back up saves for this game.");

            // ── Ludusavi fallback (PC games without a resolved emulator path) ──
            return await RunLudusaviBackupAsync(gameTitle, gameSavePath);
        }

        /// <summary>
        /// Restores a game's saves from the per-user Game.OS backup directory back
        /// to the emulator's save folder (or via <c>ludusavi restore</c> for PC).
        ///
        /// <para>
        /// When <paramref name="targetOverridePath"/> is provided the method copies
        /// files directly from the Game.OS backup folder into that path, which is
        /// the exact emulator save directory resolved by
        /// <see cref="EmulatorSavePathResolver.Resolve"/>.  This makes restore work
        /// reliably for emulators like Xenia, RPCS3, and Ryujinx without needing the
        /// game to be in the ludusavi manifest.
        /// </para>
        /// </summary>
        /// <param name="platform">Platform name, e.g. "Xbox 360", "Switch".</param>
        /// <param name="gameTitle">Display title of the game.</param>
        /// <param name="username">Logged-in Game.OS username — determines the source backup path.</param>
        /// <param name="targetOverridePath">
        ///   Optional: the resolved emulator save folder for the specific game
        ///   (from <see cref="EmulatorSavePathResolver.Resolve"/>).  When set,
        ///   files are copied directly into that folder instead of calling
        ///   <c>ludusavi restore</c>.
        /// </param>
        public static async Task<LudusaviResult> RestoreAsync(
            string  platform,
            string  gameTitle,
            string  username,
            string? targetOverridePath = null,
            string? titleId            = null)
        {
            if (string.IsNullOrWhiteSpace(username))
                return LudusaviResult.Error("No user is logged in.");

            if (string.IsNullOrWhiteSpace(gameTitle))
                return LudusaviResult.Error("Game title is required.");

            var approval = await EnsureOperationApprovedAsync("restore", platform, gameTitle, username);
            if (!approval.Approved)
                return LudusaviResult.Cancelled(approval.Message);

            // Locate the Game.OS backup folder for this game.
            // Prefer the TitleID-scoped sub-folder; fall back to the legacy flat folder
            // for saves that were backed up before this layout was introduced.
            //
            // For Xbox 360 / Xenia games: if no explicit TitleID was provided but
            // targetOverridePath was resolved (e.g. Content/{profile}/{titleId}/00000001/),
            // extract the TitleID from that path so the correct per-TitleID backup folder
            // is located during restore.
            string platformSavesRoot = UserDataService.GetGameSavesPath(username, platform);
            string safeGameTitle     = StorageHelpers.SanitiseName(gameTitle);

            if (string.IsNullOrWhiteSpace(titleId) &&
                !string.IsNullOrWhiteSpace(targetOverridePath))
            {
                string? extracted = TryExtractXeniaTitleId(targetOverridePath);
                if (!string.IsNullOrWhiteSpace(extracted))
                    titleId = extracted;
            }

            var restoreTitleIds = ResolveKnownTitleIds(platform, gameTitle, titleId);
            string? gameSavePath = ResolveRestoreSourcePath(platformSavesRoot, safeGameTitle, restoreTitleIds);
            if (string.IsNullOrWhiteSpace(gameSavePath) || !Directory.Exists(gameSavePath))
                return LudusaviResult.NoSaveFound;

            // ── TitleID-based emulator save registration ───────────────────────
            // Register the game in the secondary manifest so ludusavi knows where
            // the live save folder is, then let ludusavi perform the restore.
            // If ludusavi is not installed we fall back to a plain directory copy.
            if (!string.IsNullOrWhiteSpace(targetOverridePath))
            {
                try
                {
                    Directory.CreateDirectory(targetOverridePath);
                }
                catch (Exception ex)
                {
                    return LudusaviResult.Error($"Cannot create restore target: {ex.Message}");
                }

                // Register so ludusavi knows where this emulator game's saves live.
                LudusaviConfigService.TryRegisterGameSave(gameTitle, targetOverridePath);

                // Let ludusavi perform the restore (it now has a manifest entry).
                var result = await RunLudusaviRestoreAsync(gameTitle, gameSavePath);

                if (result.Kind == ResultKind.Synced)
                    return result;

                // Ludusavi not installed or game not found in its manifest (common for
                // emulator titles like Xbox 360/Xenia that are not in the built-in database)
                // — fall back to a direct file copy from the Game.OS backup folder.
                if (result.Kind is ResultKind.NotInstalled or ResultKind.NoSaveFound)
                {
                    string reason = result.Kind == ResultKind.NotInstalled
                        ? "ludusavi not installed"
                        : "game not found in ludusavi manifest";
                    DevLogService.Log(
                        $"[Ludusavi] restore fallback ({reason}); copying directly to \"{targetOverridePath}\".");
                    return await CopyDirectoryAsync(gameSavePath, targetOverridePath, gameTitle);
                }

                // For other failures (permission/path/process), surface the error.
                return result;
            }

            // ── Non-PC emulator games without a resolved save path ─────────────
            // Ludusavi only knows about PC saves; for emulator games with no save
            // root configured, direct the user to set up the emulator settings.
            if (IsEmulatorPlatform(platform))
                return LudusaviResult.Error(
                    "Set the emulator save folder in ⚙ Settings to restore saves for this game.");

            // ── Ludusavi restore fallback (PC games) ───────────────────────────
            return await RunLudusaviRestoreAsync(gameTitle, gameSavePath);
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        /// <summary>
        /// Launches ludusavi to back up saves for <paramref name="gameTitle"/>
        /// into <paramref name="gameSavePath"/>.
        /// </summary>
        private static async Task<LudusaviResult> RunLudusaviBackupAsync(
            string gameTitle, string gameSavePath)
        {
            string ludusaviExe = GetLudusaviExePath();
            var psi = CreateLudusaviStartInfo(ludusaviExe, "backup", gameSavePath, gameTitle, force: true);

            try
            {
                using var proc = new Process { StartInfo = psi };
                var stdoutBuf = new StringBuilder();
                var stderrBuf = new StringBuilder();

                proc.OutputDataReceived += (_, e) =>
                {
                    if (e.Data != null) stdoutBuf.AppendLine(e.Data);
                };
                proc.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null) stderrBuf.AppendLine(e.Data);
                };

                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                // Wait for the backup to complete (bounded by BackupTimeout)
                using var cts = new System.Threading.CancellationTokenSource(BackupTimeout);
                await proc.WaitForExitAsync(cts.Token);

                string stdout  = stdoutBuf.ToString();
                string stderr  = stderrBuf.ToString();
                int    exitCode = proc.ExitCode;

                DevLogService.Log(
                    $"[Ludusavi] command=\"{SanitiseForLog(psi.FileName)}\" args=\"{BuildSafeArgsForLog(psi)}\" cwd=\"{SanitiseForLog(psi.WorkingDirectory)}\"");
                DevLogService.Log(
                    $"[Ludusavi] backup exit={exitCode} game=\"{gameTitle}\" path=\"{gameSavePath}\"" +
                    (string.IsNullOrWhiteSpace(stdout) ? "" : $"\n  stdout: {SummarizeOutput(stdout)}") +
                    (string.IsNullOrWhiteSpace(stderr) ? "" : $"\n  stderr: {SummarizeOutput(stderr)}"));

                if (exitCode == 0)
                {
                    // Check for zero-files-backed-up patterns in the output
                    if (IsNoSaveFoundOutput(stdout) || IsNoSaveFoundOutput(stderr))
                        return LudusaviResult.NoSaveFound;

                    return LudusaviResult.Synced;
                }

                // Non-zero exit code
                if (IsNoSaveFoundOutput(stdout) || IsNoSaveFoundOutput(stderr))
                    return LudusaviResult.NoSaveFound;

                string detail = !string.IsNullOrWhiteSpace(stderr)
                    ? stderr.Trim()
                    : (!string.IsNullOrWhiteSpace(stdout) ? stdout.Trim() : $"exit code {exitCode}");

                // Truncate to avoid an oversized notification
                if (detail.Length > 200)
                    detail = detail[..200] + "…";

                return LudusaviResult.Error($"Backup failed ({ClassifyFailure(detail)}): {detail}");
            }
            catch (Exception ex) when (
                ex is System.ComponentModel.Win32Exception ||
                ex is FileNotFoundException ||
                ex.Message.Contains("No such file", StringComparison.OrdinalIgnoreCase))
            {
                return LudusaviResult.NotInstalled;
            }
            catch (OperationCanceledException)
            {
                return LudusaviResult.Error($"Backup timed out after {BackupTimeout.TotalMinutes:0} minutes.");
            }
            catch (Exception ex)
            {
                return LudusaviResult.Error($"Backup failed ({ClassifyFailure(ex.Message)}): {ex.Message}");
            }
        }

        /// <summary>
        /// Launches ludusavi to restore saves for <paramref name="gameTitle"/>
        /// from <paramref name="gameSavePath"/> back to the game's default location.
        /// </summary>
        private static async Task<LudusaviResult> RunLudusaviRestoreAsync(
            string gameTitle, string gameSavePath)
        {
            string ludusaviExe = GetLudusaviExePath();
            var psi = CreateLudusaviStartInfo(ludusaviExe, "restore", gameSavePath, gameTitle, force: true);

            try
            {
                using var proc = new Process { StartInfo = psi };
                var stdoutBuf = new StringBuilder();
                var stderrBuf = new StringBuilder();

                proc.OutputDataReceived += (_, e) =>
                {
                    if (e.Data != null) stdoutBuf.AppendLine(e.Data);
                };
                proc.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null) stderrBuf.AppendLine(e.Data);
                };

                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                using var cts = new System.Threading.CancellationTokenSource(BackupTimeout);
                await proc.WaitForExitAsync(cts.Token);

                string stdout   = stdoutBuf.ToString();
                string stderr   = stderrBuf.ToString();
                int    exitCode = proc.ExitCode;

                DevLogService.Log(
                    $"[Ludusavi] command=\"{SanitiseForLog(psi.FileName)}\" args=\"{BuildSafeArgsForLog(psi)}\" cwd=\"{SanitiseForLog(psi.WorkingDirectory)}\"");
                DevLogService.Log(
                    $"[Ludusavi] restore exit={exitCode} game=\"{gameTitle}\" path=\"{gameSavePath}\"" +
                    (string.IsNullOrWhiteSpace(stdout) ? "" : $"\n  stdout: {SummarizeOutput(stdout)}") +
                    (string.IsNullOrWhiteSpace(stderr) ? "" : $"\n  stderr: {SummarizeOutput(stderr)}"));

                if (exitCode == 0)
                {
                    if (IsNoSaveFoundOutput(stdout) || IsNoSaveFoundOutput(stderr))
                        return LudusaviResult.NoSaveFound;

                    return LudusaviResult.Synced;
                }

                if (IsNoSaveFoundOutput(stdout) || IsNoSaveFoundOutput(stderr))
                    return LudusaviResult.NoSaveFound;

                string detail = !string.IsNullOrWhiteSpace(stderr)
                    ? stderr.Trim()
                    : (!string.IsNullOrWhiteSpace(stdout) ? stdout.Trim() : $"exit code {exitCode}");

                if (detail.Length > 200)
                    detail = detail[..200] + "…";

                return LudusaviResult.Error($"Restore failed ({ClassifyFailure(detail)}): {detail}");
            }
            catch (Exception ex) when (
                ex is System.ComponentModel.Win32Exception ||
                ex is FileNotFoundException ||
                ex.Message.Contains("No such file", StringComparison.OrdinalIgnoreCase))
            {
                return LudusaviResult.NotInstalled;
            }
            catch (OperationCanceledException)
            {
                return LudusaviResult.Error($"Restore timed out after {BackupTimeout.TotalMinutes:0} minutes.");
            }
            catch (Exception ex)
            {
                return LudusaviResult.Error($"Restore failed ({ClassifyFailure(ex.Message)}): {ex.Message}");
            }
        }

        private static ProcessStartInfo CreateLudusaviStartInfo(
            string ludusaviExe,
            string action,
            string gameSavePath,
            string gameTitle,
            bool force)
        {
            var psi = new ProcessStartInfo
            {
                FileName               = ludusaviExe,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                WorkingDirectory       = ResolveWorkingDirectory(ludusaviExe),
            };

            // Normalize the title before passing it to ludusavi so that games
            // with trademark/registered symbols (e.g. "LEGO® The Lord of the
            // Rings™") are still matched in ludusavi's manifest database.
            string normalizedTitle = NormalizeTitleForLudusavi(gameTitle);

            psi.ArgumentList.Add(action);
            if (force) psi.ArgumentList.Add("--force");
            psi.ArgumentList.Add("--path");
            psi.ArgumentList.Add(gameSavePath);
            psi.ArgumentList.Add(normalizedTitle);
            return psi;
        }

        /// <summary>
        /// Strips decorative Unicode symbols commonly appended to game titles —
        /// registered trademark (®), trademark (™), copyright (©) — and collapses
        /// any resulting double spaces before returning the cleaned title.
        ///
        /// <para>
        /// Also normalises article-at-end suffixes so that titles stored in databases
        /// as "Simpsons Game, The" are looked up in ludusavi's manifest as the canonical
        /// form "The Simpsons Game".  Articles handled: The, A, An.
        /// </para>
        ///
        /// <para>
        /// Ludusavi's manifest database uses clean titles without these symbols,
        /// so passing the raw display title causes a "no info for these games"
        /// lookup failure for titles like "LEGO® The Lord of the Rings™".
        /// </para>
        /// </summary>
        internal static string NormalizeTitleForLudusavi(string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return title;

            // Remove registered/trademark/copyright glyphs in a single pass,
            // then collapse any double-spaces left after removal and trim.
            string cleaned = Regex.Replace(title, @"[®©™\u00AE\u00A9\u2122\u2120]", "");
            cleaned = Regex.Replace(cleaned, @"  +", " ").Trim();

            // Normalise "Title, The" → "The Title", "Title, A" → "A Title", etc.
            // This handles databases (e.g. No-Intro) that store the article at the end.
            cleaned = NormalizeLeadingArticle(cleaned);

            return cleaned;
        }

        /// <summary>
        /// Converts "Some Title, The" → "The Some Title" (and likewise for "A" and "An").
        /// Only the trailing article suffix form is normalised; titles already starting
        /// with the article are returned unchanged.
        /// </summary>
        private static readonly Regex _articleSuffixRegex =
            new(@"^(.*?),\s+(The|A|An)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static string NormalizeLeadingArticle(string title)
        {
            // Match: any text, then ", " then an article at the very end.
            var m = _articleSuffixRegex.Match(title);
            if (!m.Success) return title;

            string article = m.Groups[2].Value;
            // Preserve original casing of the article ("the" stays "the", "The" stays "The").
            return article + " " + m.Groups[1].Value.Trim();
        }

        private static string ResolveWorkingDirectory(string ludusaviExe)
        {
            try
            {
                if (Path.IsPathRooted(ludusaviExe))
                {
                    string? configuredDir = Path.GetDirectoryName(ludusaviExe);
                    if (!string.IsNullOrWhiteSpace(configuredDir) && Directory.Exists(configuredDir))
                        return configuredDir;
                }
            }
            catch { /* best-effort */ }

            return AppContext.BaseDirectory;
        }

        private static async Task<(bool Approved, string Message)> EnsureOperationApprovedAsync(
            string operation,
            string platform,
            string gameTitle,
            string username)
        {
            CleanupExpiredApprovalTokens();
            string label = operation.Equals("restore", StringComparison.OrdinalIgnoreCase) ? "restore" : "backup";
            string key = $"{username}|{platform}|{gameTitle}|{label}".ToLowerInvariant();
            string title = label == "restore" ? "Restore cloud save" : "Backup cloud save";
            string prompt = $"{title} for '{gameTitle}' on {platform}?";
            var settings = AppSettingsService.Load();

            if (!settings.RequireCloudSaveConfirmation)
            {
                DevLogService.Log($"[Ludusavi] {label} auto-approved by settings.");
                PendingApprovalTokens.TryRemove(key, out _);
                return (true, "");
            }

            if (RequestNativeConfirmationAsync != null)
            {
                try
                {
                    bool? native = await RequestNativeConfirmationAsync(title, prompt);
                    if (native.HasValue)
                    {
                        PendingApprovalTokens.TryRemove(key, out _);
                        DevLogService.Log($"[Ludusavi] native confirmation result for {label}: {native.Value}");
                        return native.Value
                            ? (true, "")
                            : (false, "Cloud save operation was cancelled by the user.");
                    }

                    DevLogService.Log($"[Ludusavi] native confirmation unavailable for {label}; trying fallback.");
                }
                catch (Exception ex)
                {
                    DevLogService.Log($"[Ludusavi] native confirmation failed for {label}: {ex.Message}. Trying fallback.");
                }
            }

            if (!settings.AllowCloudSaveInAppFallbackConfirmation)
            {
                PendingApprovalTokens.TryRemove(key, out _);
                return (false, "Cloud save confirmation is required, but no native confirmation UI is available.");
            }

            var now = DateTimeOffset.UtcNow;
            if (PendingApprovalTokens.TryGetValue(key, out var expiresAt) && expiresAt >= now)
            {
                PendingApprovalTokens.TryRemove(key, out _);
                DevLogService.Log($"[Ludusavi] fallback confirmation approved for {label}.");
                return (true, "");
            }

            PendingApprovalTokens[key] = now.Add(ApprovalFallbackWindow);
            return (false, $"Confirm {label}: run the action again within {ApprovalFallbackWindow.TotalSeconds:0} seconds.");
        }

        private static void CleanupExpiredApprovalTokens()
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var item in PendingApprovalTokens.ToList())
            {
                if (item.Value < now)
                    PendingApprovalTokens.TryRemove(item.Key, out _);
            }
        }

        /// <summary>
        /// Returns <see langword="true"/> for platforms that are backed by an emulator
        /// (e.g. "Xbox 360", "PS3", "Switch") and therefore cannot use ludusavi's
        /// PC-game manifest as a fallback.
        /// </summary>
        private static bool IsEmulatorPlatform(string platform)
        {
            if (string.IsNullOrWhiteSpace(platform)) return false;
            return !string.Equals(platform.Trim(), "PC", StringComparison.OrdinalIgnoreCase);
        }

        private static List<string> ResolveKnownTitleIds(string platform, string gameTitle, string? titleId)
        {
            var ids = new List<string>();

            void Add(string? value)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                string normalized = value.Trim().ToUpperInvariant();
                if (!ids.Contains(normalized))
                    ids.Add(normalized);
            }

            Add(titleId);

            if (string.Equals(platform.Trim(), "Xbox 360", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string cachedTitleId in TryGetCachedTitleIds(platform, gameTitle))
                    Add(cachedTitleId);
            }

            return ids;
        }

        private static bool ShouldMirrorXbox360TitleIds(string platform, IReadOnlyCollection<string> titleIds)
            => string.Equals(platform?.Trim(), "Xbox 360", StringComparison.OrdinalIgnoreCase)
            && titleIds.Count > 1;

        private static List<string> TryGetCachedTitleIds(string platform, string gameTitle)
        {
            if (string.IsNullOrWhiteSpace(platform) || string.IsNullOrWhiteSpace(gameTitle))
                return new();

            try
            {
                string cachePath = Path.Combine(
                    GamesDbCacheDir,
                    $"{Models.PlatformHelper.NormalizePlatform(platform)}.json");
                if (!File.Exists(cachePath))
                    return new();

                var games = JsonSerializer.Deserialize<List<DatabaseGame>>(
                    File.ReadAllText(cachePath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (games == null || games.Count == 0)
                    return new();

                string normalized = Regex.Replace(gameTitle, @"^(.+?) - (.+)$", "$1: $2");
                string stripped = Models.PlatformHelper.StripSpecialSymbols(gameTitle);

                bool Matches(string? candidate)
                {
                    if (string.IsNullOrWhiteSpace(candidate)) return false;
                    return string.Equals(candidate, gameTitle, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(
                            Models.PlatformHelper.StripSpecialSymbols(candidate),
                            stripped,
                            StringComparison.OrdinalIgnoreCase);
                }

                return games
                    .Where(g => Matches(g.Title) || (g.AlternateNames?.Any(Matches) ?? false))
                    .Select(g => g.TitleId?.Trim())
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Cast<string>()
                    .ToList();
            }
            catch
            {
                return new();
            }
        }

        /// <summary>
        /// Attempts to extract the Xbox 360 / Xenia TitleID from a resolved emulator
        /// save path.  Xenia save paths take the form:
        /// <c>{saveRoot}/Content/{profileId}/{titleId}/00000001</c>
        ///
        /// <para>
        /// The method walks up the path segments looking for a hex folder name immediately
        /// followed by a "00000001" segment.  Standard Xbox 360 TitleIDs are exactly 8 hex
        /// characters (e.g. <c>4D5307E6</c>).  The method accepts 6–8 hex characters to
        /// accommodate rare edge-case titles whose IDs are shorter than 8 digits.
        /// </para>
        /// <para>
        /// Returns <see langword="null"/> when no such pattern is found.
        /// </para>
        /// </summary>
        private static string? TryExtractXeniaTitleId(string savePath)
        {
            if (string.IsNullOrWhiteSpace(savePath)) return null;

            try
            {
                // Normalise separators and split into parts
                var parts = savePath
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                    .Split(Path.DirectorySeparatorChar);

                // Look for a segment that looks like a TitleID (8-char hex) followed by
                // "00000001" (the Xenia save-slot directory).  The TitleID may also be
                // longer (up to 8 chars is typical for Xbox 360 titles, but we accept
                // any 6–8 char hex name to be safe).
                for (int i = 0; i + 1 < parts.Length; i++)
                {
                    string candidate = parts[i];
                    string next      = parts[i + 1];

                    if (candidate.Length >= 6 && candidate.Length <= 8 &&
                        IsHexString(candidate) &&
                        string.Equals(next, "00000001", StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate.ToUpperInvariant();
                    }
                }

                // Fallback: if the path ends in a hex folder (no trailing 00000001),
                // accept the last segment if it looks like a TitleID.
                if (parts.Length >= 1)
                {
                    string last = parts[parts.Length - 1];
                    if (last.Length >= 6 && last.Length <= 8 && IsHexString(last))
                        return last.ToUpperInvariant();
                }
            }
            catch { /* best-effort */ }

            return null;
        }

        private static string? TryResolveAlternateXbox360SavePath(
            string sourceOverridePath,
            IReadOnlyCollection<string> titleIds)
        {
            if (string.IsNullOrWhiteSpace(sourceOverridePath) || titleIds.Count == 0)
                return null;

            try
            {
                string normalizedPath = sourceOverridePath
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                string? currentTitleId = TryExtractXeniaTitleId(normalizedPath);
                if (string.IsNullOrWhiteSpace(currentTitleId))
                    return null;

                string? slotFolder = Path.GetFileName(normalizedPath);
                string? titleFolder = Path.GetFileName(Path.GetDirectoryName(normalizedPath) ?? "");
                string? profileFolder = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(normalizedPath) ?? "") ?? "");
                string? contentRoot = Path.GetDirectoryName(
                    Path.GetDirectoryName(Path.GetDirectoryName(normalizedPath) ?? "") ?? "");

                if (string.IsNullOrWhiteSpace(slotFolder) ||
                    string.IsNullOrWhiteSpace(titleFolder) ||
                    string.IsNullOrWhiteSpace(profileFolder) ||
                    string.IsNullOrWhiteSpace(contentRoot))
                    return null;

                foreach (string titleId in titleIds)
                {
                    if (string.Equals(titleId, currentTitleId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string candidate = Path.Combine(contentRoot, profileFolder, titleId, slotFolder);
                    if (Directory.Exists(candidate))
                        return candidate;
                }
            }
            catch { /* best-effort */ }

            return null;
        }

        private static string? ResolveRestoreSourcePath(
            string platformSavesRoot,
            string safeGameTitle,
            IReadOnlyList<string> preferredTitleIds)
        {
            string gameRoot = Path.Combine(platformSavesRoot, safeGameTitle);

            foreach (string titleId in preferredTitleIds)
            {
                string titleScopedPath = Path.Combine(gameRoot, titleId);
                if (DirectoryHasAnyFiles(titleScopedPath))
                    return titleScopedPath;
            }

            if (DirectoryHasDirectFiles(gameRoot))
                return gameRoot;

            try
            {
                if (Directory.Exists(gameRoot))
                {
                    foreach (string childDir in Directory.EnumerateDirectories(gameRoot).OrderBy(Path.GetFileName))
                    {
                        if (DirectoryHasAnyFiles(childDir))
                            return childDir;
                    }
                }
            }
            catch { /* best-effort */ }

            return Directory.Exists(gameRoot) ? gameRoot : null;
        }

        private static bool IsHexString(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            foreach (char c in value)
            {
                if (!Uri.IsHexDigit(c)) return false;
            }
            return true;
        }

        private static string ClassifyFailure(string detail)
        {
            if (string.IsNullOrWhiteSpace(detail)) return "unknown";
            string lower = detail.ToLowerInvariant();
            if (lower.Contains("permission") || lower.Contains("access is denied") || lower.Contains("unauthorized"))
                return "permission";
            if (lower.Contains("no such file") || lower.Contains("not found"))
                return "path";
            if (lower.Contains("timeout"))
                return "timeout";
            if (lower.Contains("manifest") || lower.Contains("no games found") || lower.Contains("no known game saves"))
                return "game-not-found";
            return "process";
        }

        private static string SummarizeOutput(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return "";
            string trimmed = output.Trim();
            return trimmed.Length <= 300 ? trimmed : trimmed[..300] + "…";
        }

        private static string SanitiseForLog(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return value;
            return Path.GetFileName(value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        private static string BuildSafeArgsForLog(ProcessStartInfo psi)
        {
            var args = new List<string>();
            foreach (string arg in psi.ArgumentList)
            {
                bool looksLikePath =
                    Path.IsPathRooted(arg) ||
                    arg.Contains(Path.DirectorySeparatorChar) ||
                    arg.Contains(Path.AltDirectorySeparatorChar);
                args.Add(looksLikePath ? SanitiseForLog(arg) : arg);
            }
            return string.Join(" ", args);
        }

        private static async Task<LudusaviResult> CopyDirectoryToTitleIdFoldersAsync(
            string sourceDir,
            string platformSavesRoot,
            string safeGameTitle,
            IReadOnlyCollection<string> titleIds,
            string gameTitle)
        {
            try
            {
                if (!DirectoryHasAnyFiles(sourceDir))
                    return LudusaviResult.NoSaveFound;

                await Task.Run(() =>
                {
                    foreach (string titleId in titleIds)
                    {
                        string destDir = Path.Combine(platformSavesRoot, safeGameTitle, titleId);
                        CopyDirectoryCore(sourceDir, destDir);
                    }
                });

                DevLogService.Log(
                    $"[Ludusavi] mirrored Xbox 360 saves for game=\"{gameTitle}\" ids=\"{string.Join(",", titleIds)}\"");
                return LudusaviResult.Synced;
            }
            catch (Exception ex)
            {
                DevLogService.Log(
                    $"[Ludusavi] mirrored-copy failed game=\"{gameTitle}\": {ex.Message}");
                return LudusaviResult.Error($"Save copy failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Copies all files and sub-directories from <paramref name="sourceDir"/>
        /// into <paramref name="destDir"/>, mirroring the directory structure.
        /// Returns <see cref="LudusaviResult.Synced"/> on success or
        /// <see cref="LudusaviResult.Error"/> when an I/O exception occurs.
        /// </summary>
        private static async Task<LudusaviResult> CopyDirectoryAsync(
            string sourceDir, string destDir, string gameTitle)
        {
            try
            {
                if (!DirectoryHasAnyFiles(sourceDir))
                    return LudusaviResult.NoSaveFound;

                await Task.Run(() => CopyDirectoryCore(sourceDir, destDir));

                DevLogService.Log(
                    $"[Ludusavi] direct-copy game=\"{gameTitle}\" src=\"{sourceDir}\" dest=\"{destDir}\"");

                return LudusaviResult.Synced;
            }
            catch (Exception ex)
            {
                DevLogService.Log(
                    $"[Ludusavi] direct-copy failed game=\"{gameTitle}\": {ex.Message}");
                return LudusaviResult.Error($"Save copy failed: {ex.Message}");
            }
        }

        /// <summary>Recursively copies <paramref name="source"/> into <paramref name="dest"/>.</summary>
        private static void CopyDirectoryCore(string source, string dest)
        {
            Directory.CreateDirectory(dest);

            foreach (string file in Directory.GetFiles(source))
            {
                string destFile = Path.Combine(dest, Path.GetFileName(file));
                File.Copy(file, destFile, overwrite: true);
            }

            foreach (string subDir in Directory.GetDirectories(source))
            {
                string destSubDir = Path.Combine(dest, Path.GetFileName(subDir));
                CopyDirectoryCore(subDir, destSubDir);
            }
        }

        /// <summary>
        /// Heuristically checks whether ludusavi's output indicates that no saves
        /// were found for the requested game title.
        /// </summary>
        private static bool IsNoSaveFoundOutput(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return false;
            string lower = output.ToLowerInvariant();
            return lower.Contains("no games found")
                || lower.Contains("nothing to do")
                || lower.Contains("0 games")
                || lower.Contains("0 files")
                || lower.Contains("found 0")
                || lower.Contains("no known game saves")
                || lower.Contains("no info for these games")
                || lower.Contains("no info found for this game");
        }

        /// <summary>
        /// Returns true when <paramref name="path"/> exists and contains at least one file
        /// (including in nested subdirectories); returns false for missing/inaccessible paths.
        /// </summary>
        private static bool DirectoryHasAnyFiles(string path)
        {
            if (!Directory.Exists(path)) return false;
            try
            {
                return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Any();
            }
            catch
            {
                return false;
            }
        }

        private static bool DirectoryHasDirectFiles(string path)
        {
            if (!Directory.Exists(path)) return false;
            try
            {
                return Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly).Any();
            }
            catch
            {
                return false;
            }
        }

    }
}
