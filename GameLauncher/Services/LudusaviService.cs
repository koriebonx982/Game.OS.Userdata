using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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
    /// When a <paramref name="sourceOverridePath"/> is supplied to
    /// <see cref="SyncAsync"/> (resolved by <see cref="EmulatorSavePathResolver"/>
    /// from the game's TitleID), the service copies files directly from that
    /// folder instead of relying on ludusavi's manifest lookup.  This lets
    /// emulator saves for Switch, PS3, Xbox 360, etc. be backed up reliably
    /// using the per-game TitleID sub-folder the emulator already creates.
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
            string? sourceOverridePath = null)
        {
            if (string.IsNullOrWhiteSpace(username))
                return LudusaviResult.Error("No user is logged in.");

            if (string.IsNullOrWhiteSpace(gameTitle))
                return LudusaviResult.Error("Game title is required.");

            var approval = await EnsureOperationApprovedAsync("backup", platform, gameTitle, username);
            if (!approval.Approved)
                return LudusaviResult.Cancelled(approval.Message);

            // Build the per-user per-game save path and ensure the directory exists
            string platformSavesRoot = UserDataService.GetGameSavesPath(username, platform);
            string safeGameTitle     = StorageHelpers.SanitiseName(gameTitle);
            string gameSavePath      = Path.Combine(platformSavesRoot, safeGameTitle);

            try
            {
                Directory.CreateDirectory(gameSavePath);
            }
            catch (Exception ex)
            {
                return LudusaviResult.Error($"Cannot create save directory: {ex.Message}");
            }

            // ── TitleID-based direct copy ──────────────────────────────────────
            // When the caller has already resolved the exact emulator save folder,
            // copy files directly rather than relying on ludusavi's manifest lookup.
            if (!string.IsNullOrWhiteSpace(sourceOverridePath))
            {
                if (Directory.Exists(sourceOverridePath))
                    return await CopyDirectoryAsync(sourceOverridePath, gameSavePath, gameTitle);

                // Path was resolved for a non-PC emulator game but doesn't exist yet —
                // no saves to back up; do NOT fall through to ludusavi for emulator games.
                return LudusaviResult.NoSaveFound;
            }

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
            string? targetOverridePath = null)
        {
            if (string.IsNullOrWhiteSpace(username))
                return LudusaviResult.Error("No user is logged in.");

            if (string.IsNullOrWhiteSpace(gameTitle))
                return LudusaviResult.Error("Game title is required.");

            var approval = await EnsureOperationApprovedAsync("restore", platform, gameTitle, username);
            if (!approval.Approved)
                return LudusaviResult.Cancelled(approval.Message);

            // Locate the Game.OS backup folder for this game
            string platformSavesRoot = UserDataService.GetGameSavesPath(username, platform);
            string safeGameTitle     = StorageHelpers.SanitiseName(gameTitle);
            string gameSavePath      = Path.Combine(platformSavesRoot, safeGameTitle);

            if (!Directory.Exists(gameSavePath))
                return LudusaviResult.NoSaveFound;

            // ── TitleID-based direct restore ───────────────────────────────────
            // When the caller resolved the exact emulator save folder, copy files
            // from the Game.OS backup back into that folder.
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

                return await CopyDirectoryAsync(gameSavePath, targetOverridePath, gameTitle);
            }

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
                || lower.Contains("no known game saves");
        }

    }
}
