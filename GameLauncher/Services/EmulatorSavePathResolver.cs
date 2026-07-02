using System;
using System.Collections.Generic;
using System.IO;

namespace GameLauncher.Services
{
    /// <summary>
    /// Resolves the on-disk save-data folder for an emulated game by combining
    /// the user-configured <c>SaveDataPath</c> (the emulator's save root) with a
    /// platform-specific sub-path pattern that contains the game's TitleID.
    ///
    /// <para>
    /// Most emulators store saves as <c>{saveRoot}/{titleId}/</c>.  A few use
    /// a deeper structure (e.g. RPCS3, Vita3K) which is captured in
    /// <see cref="_platformPatterns"/>.
    /// </para>
    /// </summary>
    public static class EmulatorSavePathResolver
    {
        // ── Pattern table ──────────────────────────────────────────────────────
        //
        // Key   : normalised platform name (lower-case).
        // Value : path segments relative to SaveDataPath; the last segment that
        //         equals "{titleId}" is substituted at resolve time.
        //
        // Emulator-specific overrides are checked first when the emulator name is
        // recognised; the platform-level default is used otherwise.

        private static readonly Dictionary<string, string[]> _platformPatterns =
            new(StringComparer.OrdinalIgnoreCase)
        {
            // Nintendo Switch — Ryujinx: <saveRoot>/<titleId>/
            ["switch"]         = new[] { "{titleId}" },

            // Xbox 360 — Xenia: <saveRoot>/<titleId>/
            ["xbox 360"]       = new[] { "{titleId}" },

            // PS3 — RPCS3: <saveRoot>/dev_hdd0/home/00000001/savedata/<titleId>/
            ["ps3"]            = new[] { "dev_hdd0", "home", "00000001", "savedata", "{titleId}" },

            // PS Vita — Vita3K: <saveRoot>/ux0/user/00/savedata/<titleId>/
            ["ps vita"]        = new[] { "ux0", "user", "00", "savedata", "{titleId}" },

            // PS4 — RPCS4 / shadPS4: <saveRoot>/<titleId>/  (simple layout)
            ["ps4"]            = new[] { "{titleId}" },

            // Nintendo 3DS — Citra: <saveRoot>/<titleId>/
            ["nintendo - 3ds"] = new[] { "{titleId}" },
        };

        // Emulator-name overrides (take precedence over the platform default).
        // Keys: lower-case emulator name fragment (substring match).
        private static readonly Dictionary<string, string[]> _emulatorNamePatterns =
            new(StringComparer.OrdinalIgnoreCase)
        {
            // Ryujinx portable layout: <saveRoot>/<titleId>/
            ["ryujinx"]        = new[] { "{titleId}" },

            // RPCS3: same as PS3 platform default
            ["rpcs3"]          = new[] { "dev_hdd0", "home", "00000001", "savedata", "{titleId}" },

            // Xenia has multiple layouts (canary / legacy). Resolved via special-case logic.
            ["xenia"]          = new[] { "{titleId}" },

            // Vita3K: same as PS Vita platform default
            ["vita3k"]         = new[] { "ux0", "user", "00", "savedata", "{titleId}" },
        };

        // ── Public API ─────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the fully-resolved path to the game's save folder, or
        /// <see langword="null"/> when the required inputs are missing / the
        /// platform pattern is unknown.
        /// </summary>
        /// <param name="platform">Platform name as stored in the game entry (e.g. "Switch", "PS3").</param>
        /// <param name="emulatorName">Emulator label from <see cref="Models.EmulatorSettings.EmulatorName"/>; may be empty.</param>
        /// <param name="saveDataPath">Root save folder from <see cref="Models.EmulatorSettings.SaveDataPath"/>; may be empty.</param>
        /// <param name="titleId">Platform-specific title ID for the game (e.g. "0100ADC022586000" for Switch).</param>
        /// <param name="profileId">
        /// Emulator user profile ID; required for Xenia where the save path is
        /// <c>content/{profileId}/{titleId}/000100000/{profileId}/</c>
        /// (e.g. "E03000003D7E0695").  Pass <see langword="null"/> or empty for
        /// emulators that do not use a profile ID.
        /// </param>
        /// <returns>
        /// The resolved folder path, which may or may not exist on disk.
        /// Returns <see langword="null"/> when any required input is missing or
        /// no pattern is registered for the platform / emulator combination.
        /// </returns>
        public static string? Resolve(
            string  platform,
            string? emulatorName,
            string? saveDataPath,
            string? titleId,
            string? profileId = null)
        {
            if (string.IsNullOrWhiteSpace(saveDataPath)) return null;
            if (string.IsNullOrWhiteSpace(titleId))      return null;

            // Trim once and reuse throughout the method.
            string safeRoot    = NormalizeSaveRoot(saveDataPath);
            string safeTitleId = titleId.Trim();
            if (string.IsNullOrWhiteSpace(safeRoot)) return null;

            // Xenia layouts vary by version/configuration:
            //  - {saveRoot}/{titleId}/
            //  - {saveRoot}/content/{profileId}/{titleId}/
            //  - legacy: .../{titleId}/00000001 or .../000100000/{profileId}
            // Prefer existing folders, then fall back to best guess.
            if ((emulatorName ?? "").Contains("xenia", StringComparison.OrdinalIgnoreCase) ||
                (platform ?? "").Contains("xbox 360", StringComparison.OrdinalIgnoreCase))
            {
                return ResolveXeniaPath(safeRoot, safeTitleId, profileId);
            }

            string[] segments = ResolvePattern(platform, emulatorName);
            if (segments.Length == 0) return null;

            // If the pattern requires a profileId but none was supplied, attempt
            // to auto-detect it by scanning the emulator's content directory for
            // a profile folder that contains the game's titleId subfolder.
            bool needsProfile = Array.Exists(segments, s =>
                string.Equals(s, "{profileId}", StringComparison.OrdinalIgnoreCase));
            if (needsProfile && string.IsNullOrWhiteSpace(profileId))
            {
                profileId = TryDetectXeniaProfileId(safeRoot, safeTitleId);
                if (string.IsNullOrWhiteSpace(profileId)) return null;
            }

            // Build the path by substituting {titleId} and {profileId} in each segment
            string safeProfileId  = profileId?.Trim() ?? "";
            var parts = new string[segments.Length + 1];
            parts[0] = safeRoot;
            for (int i = 0; i < segments.Length; i++)
            {
                if (string.Equals(segments[i], "{titleId}", StringComparison.OrdinalIgnoreCase))
                    parts[i + 1] = safeTitleId;
                else if (string.Equals(segments[i], "{profileId}", StringComparison.OrdinalIgnoreCase))
                    parts[i + 1] = safeProfileId;
                else
                    parts[i + 1] = segments[i];
            }

            return Path.Combine(parts);
        }

        private static string NormalizeSaveRoot(string saveDataPath)
        {
            string root = (saveDataPath ?? "").Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(root)) return "";

            // Users sometimes paste/select the emulator executable; use its folder instead.
            if (LooksLikeExecutablePath(root))
            {
                string? dir = Path.GetDirectoryName(root);
                if (!string.IsNullOrWhiteSpace(dir))
                    return dir.Trim();
            }

            return root;
        }

        private static bool LooksLikeExecutablePath(string path)
        {
            string ext = Path.GetExtension(path) ?? "";
            return ext.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                   || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase)
                   || ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                   || ext.Equals(".sh", StringComparison.OrdinalIgnoreCase)
                   || ext.Equals(".appimage", StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveXeniaPath(string saveRoot, string titleId, string? profileId)
        {
            string safeProfileId = (profileId ?? "").Trim();
            string directTitlePath = Path.Combine(saveRoot, titleId);
            if (Directory.Exists(directTitlePath))
                return directTitlePath;

            string contentRoot = Path.Combine(saveRoot, "content");
            if (!Directory.Exists(contentRoot))
                return directTitlePath;

            if (!string.IsNullOrWhiteSpace(safeProfileId))
            {
                string profileTitlePath = Path.Combine(contentRoot, safeProfileId, titleId);
                if (Directory.Exists(profileTitlePath))
                    return profileTitlePath;

                string legacy00000001 = Path.Combine(profileTitlePath, "00000001");
                if (Directory.Exists(legacy00000001))
                    return legacy00000001;

                string legacy000100000 = Path.Combine(profileTitlePath, "000100000", safeProfileId);
                if (Directory.Exists(legacy000100000))
                    return legacy000100000;

                return profileTitlePath;
            }

            string? detectedProfile = TryDetectXeniaProfileId(saveRoot, titleId);
            if (!string.IsNullOrWhiteSpace(detectedProfile))
                return Path.Combine(contentRoot, detectedProfile, titleId);

            return directTitlePath;
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        /// <summary>
        /// Scans <c>{saveDataPath}/content/</c> for a profile sub-directory that
        /// contains a <c>{titleId}</c> folder, returning the first match.
        /// This lets Xenia saves be located even when the user has not manually
        /// entered their profile ID in the emulator settings.
        /// </summary>
        private static string? TryDetectXeniaProfileId(string saveDataPath, string titleId)
        {
            try
            {
                string contentDir = Path.Combine(saveDataPath, "content");
                if (!Directory.Exists(contentDir)) return null;

                foreach (string profileDir in Directory.EnumerateDirectories(contentDir))
                {
                    string candidate = Path.Combine(profileDir, titleId);
                    if (Directory.Exists(candidate))
                        return Path.GetFileName(profileDir);
                }
            }
            catch { /* best-effort */ }

            return null;
        }

        private static string[] ResolvePattern(string platform, string? emulatorName)
        {
            // 1. Try emulator-name override (substring, case-insensitive)
            if (!string.IsNullOrWhiteSpace(emulatorName))
            {
                foreach (var kvp in _emulatorNamePatterns)
                {
                    if (emulatorName.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                        return kvp.Value;
                }
            }

            // 2. Fall back to platform-level default
            if (_platformPatterns.TryGetValue(platform ?? "", out var pattern))
                return pattern;

            return Array.Empty<string>();
        }
    }
}
