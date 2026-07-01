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
    /// Ludusavi project: https://github.com/mtkennerly/ludusavi
    /// </summary>
    public static class LudusaviService
    {
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
        /// </summary>
        /// <param name="platform">Platform name, e.g. "PC", "Switch".</param>
        /// <param name="gameTitle">Display title of the game.</param>
        /// <param name="username">Logged-in Game.OS username — determines the target save path.</param>
        public static async Task<LudusaviResult> SyncAsync(
            string platform, string gameTitle, string username)
        {
            if (string.IsNullOrWhiteSpace(username))
                return LudusaviResult.Error("No user is logged in.");

            if (string.IsNullOrWhiteSpace(gameTitle))
                return LudusaviResult.Error("Game title is required.");

            string ludusaviExe = GetLudusaviExePath();

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

                // Wait up to 5 minutes for the backup to complete
                using var cts = new System.Threading.CancellationTokenSource(
                    TimeSpan.FromMinutes(5));
                await proc.WaitForExitAsync(cts.Token);

                string stdout = stdoutBuf.ToString();
                string stderr = stderrBuf.ToString();
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
                return LudusaviResult.Error("Sync timed out after 5 minutes.");
            }
            catch (Exception ex)
            {
                return LudusaviResult.Error(ex.Message);
            }
        }

        // ── Helpers ────────────────────────────────────────────────────────────

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
