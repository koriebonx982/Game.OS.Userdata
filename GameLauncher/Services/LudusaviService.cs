using System;
using System.Diagnostics;
using System.IO;
using System.Text;
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

        // ── Result discriminated union ─────────────────────────────────────────

        public enum ResultKind { Synced, NoSaveFound, NotInstalled, Error }

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
            public static LudusaviResult Error(string msg) =>
                new(ResultKind.Error, msg ?? "Unknown error");
        }

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
            if (!string.IsNullOrWhiteSpace(sourceOverridePath)
                && Directory.Exists(sourceOverridePath))
            {
                return await CopyDirectoryAsync(sourceOverridePath, gameSavePath, gameTitle);
            }

            // ── Ludusavi fallback ──────────────────────────────────────────────
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

            // Build arguments: backup --path "<savePath>" "<gameTitle>"
            string args = $"backup --path \"{EscapeArg(gameSavePath)}\" \"{EscapeArg(gameTitle)}\"";

            var psi = new ProcessStartInfo
            {
                FileName               = ludusaviExe,
                Arguments              = args,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };

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
                    $"[Ludusavi] exit={exitCode} game=\"{gameTitle}\" path=\"{gameSavePath}\"" +
                    (string.IsNullOrWhiteSpace(stdout) ? "" : $"\n  stdout: {stdout.Trim()}") +
                    (string.IsNullOrWhiteSpace(stderr) ? "" : $"\n  stderr: {stderr.Trim()}"));

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

                return LudusaviResult.Error(detail);
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
                return LudusaviResult.Error($"Sync timed out after {BackupTimeout.TotalMinutes:0} minutes.");
            }
            catch (Exception ex)
            {
                return LudusaviResult.Error(ex.Message);
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

            string args = $"restore --force --path \"{EscapeArg(gameSavePath)}\" \"{EscapeArg(gameTitle)}\"";

            var psi = new ProcessStartInfo
            {
                FileName               = ludusaviExe,
                Arguments              = args,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };

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
                    $"[Ludusavi] restore exit={exitCode} game=\"{gameTitle}\" path=\"{gameSavePath}\"" +
                    (string.IsNullOrWhiteSpace(stdout) ? "" : $"\n  stdout: {stdout.Trim()}") +
                    (string.IsNullOrWhiteSpace(stderr) ? "" : $"\n  stderr: {stderr.Trim()}"));

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

                return LudusaviResult.Error(detail);
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
                return LudusaviResult.Error(ex.Message);
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

        /// <summary>
        /// Escapes a value for safe inclusion as a double-quoted Windows command-line argument.
        /// Follows the Windows CRT quoting rules so that backslashes immediately before a
        /// double-quote (or at the trailing end of the string, where they would precede the
        /// closing <c>"</c>) are properly doubled.
        /// </summary>
        private static string EscapeArg(string value)
        {
            // Implementation follows the standard Windows command-line quoting algorithm:
            //   - N backslashes followed by '"'   → 2N backslashes + '\"'
            //   - N backslashes at end of string  → 2N backslashes  (they precede closing '"')
            //   - Other characters pass through unchanged.
            var sb = new StringBuilder(value.Length + 4);
            int backslashCount = 0;

            foreach (char c in value)
            {
                if (c == '\\')
                {
                    backslashCount++;
                }
                else if (c == '"')
                {
                    // Double all accumulated backslashes, then escape the quote
                    sb.Append('\\', backslashCount * 2);
                    backslashCount = 0;
                    sb.Append('\\');
                    sb.Append('"');
                }
                else
                {
                    if (backslashCount > 0)
                    {
                        sb.Append('\\', backslashCount);
                        backslashCount = 0;
                    }
                    sb.Append(c);
                }
            }

            // Trailing backslashes precede the closing '"' — must be doubled
            sb.Append('\\', backslashCount * 2);
            return sb.ToString();
        }
    }
}
