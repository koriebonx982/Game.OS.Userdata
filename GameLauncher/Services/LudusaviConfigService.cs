using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace GameLauncher.Services
{
    /// <summary>
    /// Maintains a Game.OS-owned secondary ludusavi manifest
    /// (<c>gameos-emulator-saves.yaml</c>) that is written into ludusavi's own
    /// config directory and registered once as a secondary manifest URL in
    /// ludusavi's <c>config.yaml</c>.
    ///
    /// <para>
    /// This lets Game.OS teach ludusavi about emulator game save paths
    /// (Xbox 360/Xenia, PS3/RPCS3, Switch/Ryujinx, etc.) by pointing it at
    /// the per-game folder resolved by <see cref="EmulatorSavePathResolver"/>.
    /// Once registered, ludusavi handles the backup/restore — and its own cloud
    /// sync (OneDrive, Dropbox, etc.) — exactly as it does for PC games.
    /// </para>
    ///
    /// <para>
    /// The manifest file is written in the stable ludusavi manifest YAML format:
    /// <code>
    /// "Game Title":
    ///   files:
    ///     "C:\\path\\to\\saves\\**":
    ///       tags:
    ///         - save
    /// </code>
    /// </para>
    /// </summary>
    public static class LudusaviConfigService
    {
        // ── Constants ──────────────────────────────────────────────────────────

        private const string ManifestFileName = "gameos-emulator-saves.yaml";

        // ── Public API ─────────────────────────────────────────────────────────

        /// <summary>
        /// Registers <paramref name="gameTitle"/> → <paramref name="saveFolderPath"/>
        /// in the Game.OS secondary ludusavi manifest, then ensures that manifest is
        /// listed as a secondary URL in ludusavi's <c>config.yaml</c>.
        /// </summary>
        /// <returns>
        /// <see langword="true"/> when the manifest was written and the config was
        /// patched (or already contained the entry); <see langword="false"/> when
        /// the ludusavi config directory could not be found (e.g. ludusavi is not
        /// installed) or an I/O error occurred.
        /// </returns>
        public static bool TryRegisterGameSave(string gameTitle, string saveFolderPath)
        {
            try
            {
                string? configDir = FindLudusaviConfigDir();
                if (configDir == null)
                {
                    DevLogService.Log("[LudusaviConfig] Config directory not found — skipping manifest registration.");
                    return false;
                }

                string manifestPath = Path.Combine(configDir, ManifestFileName);

                // Write / update the manifest file with this game's save path.
                UpdateGameOsManifest(manifestPath, gameTitle, saveFolderPath);

                // Patch config.yaml to include this manifest as a secondary source.
                EnsureSecondaryManifestRegistered(configDir, manifestPath);

                DevLogService.Log(
                    $"[LudusaviConfig] Registered \"{gameTitle}\" → \"{saveFolderPath}\" in {manifestPath}");

                return true;
            }
            catch (Exception ex)
            {
                DevLogService.Log($"[LudusaviConfig] Registration failed: {ex.Message}");
                return false;
            }
        }

        // ── Config directory discovery ─────────────────────────────────────────

        /// <summary>
        /// Returns the first ludusavi config directory found on the current system,
        /// or <see langword="null"/> when ludusavi does not appear to be installed.
        /// </summary>
        public static string? FindLudusaviConfigDir()
        {
            var candidates = new List<string>();

            // Windows: %APPDATA%\ludusavi
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(roaming))
                candidates.Add(Path.Combine(roaming, "ludusavi"));

            // Linux: $XDG_CONFIG_HOME/ludusavi  (default ~/.config/ludusavi)
            string? xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (!string.IsNullOrEmpty(xdgConfig))
                candidates.Add(Path.Combine(xdgConfig, "ludusavi"));

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                // Linux default
                candidates.Add(Path.Combine(home, ".config", "ludusavi"));
                // macOS: ~/Library/Application Support/ludusavi
                candidates.Add(Path.Combine(home, "Library", "Application Support", "ludusavi"));
            }

            // Also check next to the ludusavi executable if one is configured
            try
            {
                string exe = LudusaviService.GetLudusaviExePath();
                if (!string.Equals(exe, "ludusavi", StringComparison.OrdinalIgnoreCase)
                    && File.Exists(exe))
                {
                    string? exeDir = Path.GetDirectoryName(exe);
                    if (!string.IsNullOrEmpty(exeDir))
                        candidates.Add(Path.Combine(exeDir, "config"));
                }
            }
            catch { /* best-effort */ }

            foreach (var candidate in candidates)
            {
                if (Directory.Exists(candidate) &&
                    File.Exists(Path.Combine(candidate, "config.yaml")))
                    return candidate;
            }

            return null;
        }

        // ── Manifest file writer ───────────────────────────────────────────────

        /// <summary>
        /// Reads the existing <c>gameos-emulator-saves.yaml</c> manifest (if any),
        /// updates or adds the entry for <paramref name="gameTitle"/>, then writes
        /// the file back.
        /// </summary>
        private static void UpdateGameOsManifest(
            string manifestPath, string gameTitle, string saveFolderPath)
        {
            // Canonicalize the save path → a recursive glob pattern for the folder.
            string saveGlob = saveFolderPath.TrimEnd('\\', '/') + "/**";

            // Read existing entries so we don't lose other games.
            var entries = ReadManifestEntries(manifestPath);
            entries[gameTitle] = saveGlob;

            WriteManifestEntries(manifestPath, entries);
        }

        /// <summary>
        /// Parses the simple YAML format generated by <see cref="WriteManifestEntries"/>
        /// and returns a dictionary mapping game title → save glob path.
        /// </summary>
        private static Dictionary<string, string> ReadManifestEntries(string manifestPath)
        {
            var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(manifestPath)) return entries;

            string? currentTitle = null;
            foreach (string rawLine in File.ReadLines(manifestPath))
            {
                string line = rawLine;

                // Skip comments and blank lines
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                    continue;

                // Game title line: "Game Title":
                var titleMatch = Regex.Match(line, @"^""((?:[^""\\]|\\.)*)"":\s*$");
                if (titleMatch.Success)
                {
                    currentTitle = titleMatch.Groups[1].Value
                        .Replace("\\\"", "\"")
                        .Replace("\\\\", "\\");
                    continue;
                }

                // Save path line (indented):  "C:\\path\\**":
                if (currentTitle != null)
                {
                    var pathMatch = Regex.Match(line, @"^\s+""((?:[^""\\]|\\.)*)"":\s*$");
                    if (pathMatch.Success)
                    {
                        string rawPath = pathMatch.Groups[1].Value
                            .Replace("\\\\", "\\")
                            .Replace("\\\"", "\"");
                        entries[currentTitle] = rawPath;
                        currentTitle = null;
                    }
                }
            }

            return entries;
        }

        /// <summary>
        /// Writes all <paramref name="entries"/> to <paramref name="manifestPath"/>
        /// in the stable ludusavi manifest YAML format.
        /// </summary>
        private static void WriteManifestEntries(
            string manifestPath, Dictionary<string, string> entries)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Auto-generated by Game.OS — DO NOT EDIT MANUALLY");
            sb.AppendLine("# Emulator save paths registered with ludusavi for cloud backup.");
            sb.AppendLine();

            foreach (var (title, saveGlob) in entries)
            {
                // Escape backslashes and double-quotes for YAML double-quoted strings.
                string yamlTitle = title.Replace("\\", "\\\\").Replace("\"", "\\\"");
                string yamlPath  = saveGlob
                    .Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("/", "/");   // forward slashes are fine in YAML paths

                sb.AppendLine($"\"{yamlTitle}\":");
                sb.AppendLine("  files:");
                sb.AppendLine($"    \"{yamlPath}\":");
                sb.AppendLine("      tags:");
                sb.AppendLine("        - save");
                sb.AppendLine();
            }

            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            File.WriteAllText(manifestPath, sb.ToString(), Encoding.UTF8);
        }

        // ── config.yaml registration ───────────────────────────────────────────

        /// <summary>
        /// Ensures that the Game.OS manifest file is listed in ludusavi's
        /// <c>config.yaml</c> under <c>manifest.secondary</c>.
        /// Uses targeted regex replacements so the rest of the config is preserved.
        /// </summary>
        private static void EnsureSecondaryManifestRegistered(
            string configDir, string manifestPath)
        {
            string configPath = Path.Combine(configDir, "config.yaml");
            if (!File.Exists(configPath)) return;

            string fileUrl = PathToFileUrl(manifestPath);
            string config  = File.ReadAllText(configPath);

            // Already registered? Nothing to do.
            if (config.Contains(fileUrl, StringComparison.OrdinalIgnoreCase)) return;

            string newEntry  = $"\n    - url: \"{fileUrl}\"\n      etag: ~";
            string updated   = TryInjectSecondaryEntry(config, newEntry);

            if (updated == config)
            {
                // Could not find a safe injection point — log and skip rather than
                // risk corrupting the user's config.
                DevLogService.Log(
                    "[LudusaviConfig] Could not auto-patch config.yaml — " +
                    $"please add the secondary manifest manually: {fileUrl}");
                return;
            }

            File.WriteAllText(configPath, updated, Encoding.UTF8);
            DevLogService.Log($"[LudusaviConfig] Patched config.yaml with secondary manifest: {fileUrl}");
        }

        /// <summary>
        /// Tries to inject <paramref name="newEntry"/> into the
        /// <c>manifest.secondary</c> list in <paramref name="config"/>.
        ///
        /// Handles these existing layouts:
        /// <list type="bullet">
        ///   <item><c>secondary: []</c>           → replaced with single-entry list</item>
        ///   <item><c>secondary:\n    - url: …</c>→ entry appended after the last item</item>
        ///   <item><c>secondary:</c> alone         → entry appended immediately after</item>
        /// </list>
        ///
        /// Returns the original <paramref name="config"/> string unchanged when no
        /// safe injection point is found.
        /// </summary>
        private static string TryInjectSecondaryEntry(string config, string newEntry)
        {
            // ── Case 1: secondary: []  →  replace with multi-line block ────────
            var emptyList = new Regex(@"([ \t]*secondary:)[ \t]*\[\]", RegexOptions.Multiline);
            var m1 = emptyList.Match(config);
            if (m1.Success)
                return config[..m1.Index] + m1.Groups[1].Value + newEntry + config[(m1.Index + m1.Length)..];

            // ── Case 2: secondary: followed by indented list items ─────────────
            // Find the last "- url:" line in the secondary block and insert after it.
            var blockStart = new Regex(@"([ \t]*secondary:)([ \t]*\n(?:[ \t]+-[^\n]*\n(?:[ \t]+[^\n]*\n)*)*)",
                RegexOptions.Multiline);
            var m2 = blockStart.Match(config);
            if (m2.Success)
            {
                // Append our entry at the end of the matched block
                int insertAt = m2.Index + m2.Length;
                return config[..insertAt] + newEntry + "\n" + config[insertAt..];
            }

            // ── Case 3: secondary: with nothing after (or end of line) ─────────
            var bare = new Regex(@"([ \t]*secondary:)([ \t]*)(\r?\n)", RegexOptions.Multiline);
            var m3 = bare.Match(config);
            if (m3.Success)
            {
                int insertAt = m3.Index + m3.Length;
                return config[..insertAt] + newEntry + "\n" + config[insertAt..];
            }

            return config; // no safe injection point found
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        /// <summary>
        /// Converts a local filesystem path to a <c>file:///</c> URL using
        /// forward slashes (required by ludusavi on all platforms).
        /// </summary>
        private static string PathToFileUrl(string path)
        {
            // Normalise to forward slashes and ensure the URL starts with file:///
            string normalized = path.Replace('\\', '/');
            if (!normalized.StartsWith('/'))
                normalized = "/" + normalized; // Windows: "/C:/path/..."
            return "file://" + normalized;
        }
    }
}
