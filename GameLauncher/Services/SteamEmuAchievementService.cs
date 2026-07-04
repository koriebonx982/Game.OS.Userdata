using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace GameLauncher.Services
{
    /// <summary>
    /// Locates and reads unlocked achievement IDs from the save files written by all
    /// major Steam emulators, replicating the path knowledge of Achievement Watcher.
    ///
    /// Supported emulators / crack groups:
    ///   Goldberg · GBE · GSE Saves · Codex · Plaza · Rune · EMPRESS ·
    ///   Online Fix · Skidrow · Ali213 / ColdAPI Steam · Voices38 ·
    ///   Smart Steam Emu (SSE) · SSE-R
    /// </summary>
    public static class SteamEmuAchievementService
    {
        // ── Known achievement file names ──────────────────────────────────────
        private static readonly HashSet<string> AchievementFileNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "achievements.json",
                "achievement.json",
                "stats.json",
                "achievements.ini",
                "stats.ini",
                "profile.ini",
                "ColdClientStats.json",     // Ali213 / ColdAPI
            };

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the set of unlocked achievement IDs for the given game, scanning all
        /// known Steam emulator save locations.
        /// </summary>
        /// <param name="exePath">Full path to the game's launch executable (used to locate the game folder).</param>
        /// <param name="steamAppId">Steam AppID, or 0 if unknown.  When &gt;0, AppID-keyed paths are also searched.</param>
        public static HashSet<string> ReadUnlockedIds(string exePath, int steamAppId)
        {
            var unlocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in EnumerateAchievementFiles(exePath, steamAppId))
            {
                try
                {
                    IEnumerable<string> ids;
                    string ext = Path.GetExtension(file);
                    string name = Path.GetFileName(file);

                    if (string.Equals(ext, ".ini", StringComparison.OrdinalIgnoreCase))
                        ids = ParseIni(file);
                    else if (string.Equals(name, "ColdClientStats.json", StringComparison.OrdinalIgnoreCase))
                        ids = ParseColdClientStats(file);
                    else
                        ids = ParseJson(file);

                    foreach (var id in ids)
                        if (!string.IsNullOrWhiteSpace(id))
                            unlocked.Add(id.Trim());
                }
                catch { /* best-effort per file */ }
            }
            return unlocked;
        }

        /// <summary>
        /// Builds a mapping from raw API achievement name (e.g. <c>ACH_WIN_GAME</c>) to
        /// its human-readable display name (e.g. <c>Win The Game</c>) by reading the
        /// <c>steam_settings/achievements.json</c> definitions file placed by GBE,
        /// Goldberg, and other Steam-emulator forks next to the game executable.
        ///
        /// <para>
        /// The definitions file is distinct from the unlock-state files read by
        /// <see cref="ReadUnlockedIds"/>; it contains no unlock information, only
        /// the achievement catalogue with names, descriptions and icons.
        /// </para>
        /// </summary>
        /// <param name="exePath">Full path to the game's launch executable.</param>
        /// <param name="steamAppId">
        /// Steam AppID.  Currently unused by this method — kept for API symmetry with
        /// <see cref="ReadUnlockedIds"/> and <see cref="EnumerateAchievementFiles"/> so
        /// all three can be called with the same pair of arguments.
        /// </param>
        /// <returns>
        /// A case-insensitive dictionary mapping <c>name</c> → <c>displayName</c>.
        /// Returns an empty dictionary when no definitions file is found or can be parsed.
        /// </returns>
#pragma warning disable CA1801   // steamAppId intentionally unused (API symmetry — see doc)
        public static Dictionary<string, string> TryBuildAchievementNameMap(string exePath, int steamAppId)
#pragma warning restore CA1801
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string? gameDir = null;
            try { gameDir = Path.GetDirectoryName(exePath); } catch { }
            if (string.IsNullOrEmpty(gameDir)) return map;

            // Candidate definition-file locations (game dir and parent dir, both casing variants)
            var candidates = new List<string>
            {
                Path.Combine(gameDir,  "steam_settings", "achievements.json"),
                Path.Combine(gameDir,  "SteamSettings",  "achievements.json"),
            };
            string? parent = null;
            try { parent = Directory.GetParent(gameDir)?.FullName; } catch { }
            if (!string.IsNullOrEmpty(parent))
            {
                candidates.Add(Path.Combine(parent, "steam_settings", "achievements.json"));
                candidates.Add(Path.Combine(parent, "SteamSettings",  "achievements.json"));
            }

            foreach (var defPath in candidates)
            {
                if (!File.Exists(defPath)) continue;
                try
                {
                    ParseAchievementDefinitions(defPath, map);
                    if (map.Count > 0) break; // stop at the first file that yields entries
                }
                catch { /* best-effort */ }
            }

            return map;
        }

        // ── Internal: achievement-definitions parser ──────────────────────────

        /// <summary>
        /// Parses a <c>steam_settings/achievements.json</c> definitions file into
        /// <paramref name="map"/>, adding raw <c>name</c> → <c>displayName</c> pairs.
        ///
        /// Handles two common layouts:
        /// <list type="bullet">
        ///   <item>Array: <c>[{ "name": "ACH_ID", "displayName": "Clean Name", … }]</c></item>
        ///   <item>Object: <c>{ "ACH_ID": { "displayName": "Clean Name", … } }</c></item>
        /// </list>
        /// </summary>
        private static void ParseAchievementDefinitions(string filePath, Dictionary<string, string> map)
        {
            using var stream = File.OpenRead(filePath);
            using var doc    = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                // [ { "name": "ACH_ID", "displayName": "Clean Name", … }, … ]
                foreach (var item in root.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;

                    string? rawName     = null;
                    string? displayName = null;

                    foreach (var prop in item.EnumerateObject())
                    {
                        if (string.Equals(prop.Name, "name",         StringComparison.OrdinalIgnoreCase) &&
                            prop.Value.ValueKind == JsonValueKind.String)
                            rawName = prop.Value.GetString()?.Trim();

                        if ((string.Equals(prop.Name, "displayName",  StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(prop.Name, "display_name", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(prop.Name, "title",        StringComparison.OrdinalIgnoreCase)) &&
                            prop.Value.ValueKind == JsonValueKind.String)
                            displayName = prop.Value.GetString()?.Trim();
                    }

                    if (!string.IsNullOrWhiteSpace(rawName) && !string.IsNullOrWhiteSpace(displayName))
                        map.TryAdd(rawName, displayName);
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                // { "ACH_ID": { "displayName": "Clean Name", … }, … }
                foreach (var prop in root.EnumerateObject())
                {
                    string rawName = prop.Name.Trim();
                    if (string.IsNullOrWhiteSpace(rawName)) continue;

                    if (prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var inner in prop.Value.EnumerateObject())
                        {
                            if ((string.Equals(inner.Name, "displayName",  StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(inner.Name, "display_name", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(inner.Name, "title",        StringComparison.OrdinalIgnoreCase)) &&
                                inner.Value.ValueKind == JsonValueKind.String)
                            {
                                string? dn = inner.Value.GetString()?.Trim();
                                if (!string.IsNullOrWhiteSpace(dn))
                                    map.TryAdd(rawName, dn);
                                break;
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Enumerates every achievement/stats file from all known Steam-emulator save paths
        /// that are relevant to the supplied game.
        /// </summary>
        public static IEnumerable<string> EnumerateAchievementFiles(string exePath, int steamAppId)
        {
            var candidates = BuildCandidatePaths(exePath, steamAppId);

            foreach (var path in candidates)
            {
                if (!File.Exists(path)) continue;
                yield return path;
            }

            // Also do a recursive scan of the emulator root dirs — catches setups where
            // a Steam user-ID subdirectory sits between the root and the AppID folder.
            foreach (var root in BuildScanRoots(exePath, steamAppId))
            {
                if (!Directory.Exists(root)) continue;

                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories); }
                catch { continue; }

                foreach (var file in files)
                {
                    if (candidates.Contains(file)) continue; // already emitted above

                    string fname = Path.GetFileName(file);

                    if (AchievementFileNames.Contains(fname))
                    {
                        bool matchesAppId = steamAppId <= 0 || PathContainsSegment(file, steamAppId.ToString());
                        if (!matchesAppId) continue;
                        yield return file;
                        continue;
                    }

                    // Also match any .json/.ini that contains the AppID in its path
                    if (steamAppId > 0 &&
                        (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                         file.EndsWith(".ini",  StringComparison.OrdinalIgnoreCase)))
                    {
                        if (!PathContainsSegment(file, steamAppId.ToString())) continue;
                        yield return file;
                    }
                }
            }
        }

        // ── Path builders ─────────────────────────────────────────────────────

        /// <summary>
        /// Builds the explicit per-emulator per-AppID file paths.
        /// These are tried first (O(1) existence check rather than full dir scan).
        /// </summary>
        private static HashSet<string> BuildCandidatePaths(string exePath, int steamAppId)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (steamAppId <= 0) return set;

            string appId = steamAppId.ToString();
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string local   = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string common  = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
            string docs    = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            void AddDir(string dir, params string[] names)
            {
                foreach (var n in names)
                    set.Add(Path.Combine(dir, n));
            }

            // ── Goldberg ──────────────────────────────────────────────────────
            // %APPDATA%/Goldberg SteamEmu Saves/{appid}/achievements.json
            // (also scanned recursively via BuildScanRoots to cover Steam-user-ID sub-dirs)
            if (!string.IsNullOrEmpty(roaming))
            {
                var gbRoot = Path.Combine(roaming, "Goldberg SteamEmu Saves");
                AddDir(Path.Combine(gbRoot, appId), "achievements.json", "stats.json");

                // ── GBE / GBE Fork ────────────────────────────────────────────
                // %APPDATA%/GBE Saves/{appid}/  (legacy name)
                var gbeRoot = Path.Combine(roaming, "GBE Saves");
                AddDir(Path.Combine(gbeRoot, appId), "achievements.json", "stats.json");

                // ── GSE Saves (GBE renamed) ───────────────────────────────────
                // %APPDATA%/GSE Saves/{appid}/
                var gseRoot = Path.Combine(roaming, "GSE Saves");
                AddDir(Path.Combine(gseRoot, appId), "achievements.json", "stats.json");
            }
            if (!string.IsNullOrEmpty(local))
            {
                var gbLocalRoot = Path.Combine(local, "Goldberg SteamEmu Saves");
                AddDir(Path.Combine(gbLocalRoot, appId), "achievements.json", "stats.json");
            }

            // ── Public Documents / Steam / {Group} ────────────────────────────
            // %PUBLIC%/Documents/Steam/{Group}/{appid}/achievements.ini
            if (!string.IsNullOrEmpty(common))
            {
                string steamCommon = Path.Combine(common, "Steam");

                // Codex
                AddDir(Path.Combine(steamCommon, "CODEX",      appId), "achievements.ini", "stats.ini", "achievements.json");
                // Plaza
                AddDir(Path.Combine(steamCommon, "PLAZA",      appId), "achievements.ini", "stats.ini", "achievements.json");
                // Rune — canonical: %PUBLIC%/Documents/Steam/RUNE/{appid}/
                AddDir(Path.Combine(steamCommon, "RUNE",        appId), "achievements.ini", "stats.ini", "achievements.json");
                // Online Fix
                AddDir(Path.Combine(steamCommon, "ONLINE_FIX",  appId), "achievements.ini", "stats.ini", "achievements.json");
                AddDir(Path.Combine(steamCommon, "OnlineFix",   appId), "achievements.ini", "stats.ini", "achievements.json");
                // Skidrow
                AddDir(Path.Combine(steamCommon, "Skidrow",     appId), "achievements.ini", "stats.ini", "achievements.json");
                AddDir(Path.Combine(steamCommon, "SKIDROW",     appId), "achievements.ini", "stats.ini", "achievements.json");

                // ── EMPRESS ───────────────────────────────────────────────────
                // %PUBLIC%/Documents/EMPRESS/{appid}/  (NOT under Steam/)
                var empressRoot = Path.Combine(common, "EMPRESS", appId);
                AddDir(empressRoot, "achievements.ini", "stats.ini", "achievements.json");
            }

            // ── %PUBLIC%\steam\{Group}\{appid}\ ──────────────────────────────
            // Some crack groups store saves directly under %PUBLIC%\steam\ (without
            // the \Documents\ subfolder).  Rune in particular uses this layout:
            //   C:\Users\Public\steam\rune\{appid}\achievements.ini
            string? publicRoot = Environment.GetEnvironmentVariable("PUBLIC");
            if (string.IsNullOrEmpty(publicRoot) && !string.IsNullOrEmpty(common))
                publicRoot = Directory.GetParent(common)?.FullName; // parent of %PUBLIC%\Documents
            if (!string.IsNullOrEmpty(publicRoot))
            {
                string pubSteam = Path.Combine(publicRoot, "steam");
                AddDir(Path.Combine(pubSteam, "rune",       appId), "achievements.ini", "stats.ini", "achievements.json");
                AddDir(Path.Combine(pubSteam, "RUNE",       appId), "achievements.ini", "stats.ini", "achievements.json");
                AddDir(Path.Combine(pubSteam, "codex",      appId), "achievements.ini", "stats.ini", "achievements.json");
                AddDir(Path.Combine(pubSteam, "CODEX",      appId), "achievements.ini", "stats.ini", "achievements.json");
                AddDir(Path.Combine(pubSteam, "plaza",      appId), "achievements.ini", "stats.ini", "achievements.json");
                AddDir(Path.Combine(pubSteam, "PLAZA",      appId), "achievements.ini", "stats.ini", "achievements.json");
                AddDir(Path.Combine(pubSteam, "skidrow",    appId), "achievements.ini", "stats.ini", "achievements.json");
                AddDir(Path.Combine(pubSteam, "SKIDROW",    appId), "achievements.ini", "stats.ini", "achievements.json");
                AddDir(Path.Combine(pubSteam, "online_fix", appId), "achievements.ini", "stats.ini", "achievements.json");
                AddDir(Path.Combine(pubSteam, "onlinefix",  appId), "achievements.ini", "stats.ini", "achievements.json");
            }

            // ── CPY (EMPRESS/Codex) variant: Documents/CPY_SAVES ──────────────
            if (!string.IsNullOrEmpty(docs))
            {
                var cpyRoot = Path.Combine(docs, "CPY_SAVES", appId);
                AddDir(cpyRoot, "achievements.ini", "stats.ini", "achievements.json");

                // Some builds use Documents/Steam/CODEX directly
                var docsCodexRoot = Path.Combine(docs, "Steam", "CODEX", appId);
                AddDir(docsCodexRoot, "achievements.ini", "stats.ini", "achievements.json");
            }

            if (!string.IsNullOrEmpty(roaming))
            {
                // ── Rune (legacy %APPDATA% layout kept as fallback) ───────────
                var runeRoot = Path.Combine(roaming, "Rune", appId);
                AddDir(runeRoot, "achievements.ini", "stats.ini", "achievements.json");

                // ── Ali213 / ColdAPI Steam ────────────────────────────────────
                // ColdAPI saves ColdClientStats.json in %APPDATA%/ColdAPI Steam/{appid}/
                var coldApiRoot = Path.Combine(roaming, "ColdAPI Steam", appId);
                AddDir(coldApiRoot,
                    "ColdClientStats.json", "achievements.json", "achievements.ini", "stats.ini");

                var ali213Root = Path.Combine(roaming, "Ali213", appId);
                AddDir(ali213Root,
                    "ColdClientStats.json", "achievements.json", "achievements.ini");

                // ── Voices38 ──────────────────────────────────────────────────
                var v38Root = Path.Combine(roaming, "Voices38", appId);
                AddDir(v38Root, "achievements.ini", "stats.ini", "achievements.json");

                // ── Smart Steam Emu (SSE) / SSE-R ─────────────────────────────
                // Flat: %APPDATA%/SmartSteamEmu/{appid}/
                // Profile: %APPDATA%/SmartSteamEmu/{Profile}/{appid}/  — covered by recursive scan
                var sseRoot = Path.Combine(roaming, "SmartSteamEmu", appId);
                AddDir(sseRoot, "achievements.ini", "stats.ini", "profile.ini");

                var sseRRoot = Path.Combine(roaming, "SSE-R", appId);
                AddDir(sseRRoot, "achievements.ini", "stats.ini", "profile.ini");

                var sseAltRoot = Path.Combine(roaming, "SSE", appId);
                AddDir(sseAltRoot, "achievements.ini", "stats.ini", "profile.ini");
            }
            if (!string.IsNullOrEmpty(local))
            {
                var sseLocalRoot = Path.Combine(local, "SmartSteamEmu", appId);
                AddDir(sseLocalRoot, "achievements.ini", "stats.ini", "profile.ini");
            }

            // ── Game-folder-relative paths ────────────────────────────────────
            string? gameDir = null;
            try { gameDir = Path.GetDirectoryName(exePath); } catch { }

            if (!string.IsNullOrEmpty(gameDir))
            {
                // SSE / generic: Profile/ subfolder in game dir
                AddDir(Path.Combine(gameDir, "Profile"),
                    "achievements.ini", "stats.ini", "profile.ini");

                // Goldberg / generic: steam_settings in game dir or parent
                AddDir(Path.Combine(gameDir, "steam_settings"),
                    "achievements.json", "achievement.json", "stats.json");
                AddDir(Path.Combine(gameDir, "SteamSettings"),
                    "achievements.json", "achievement.json", "stats.json");

                string? parent = null;
                try { parent = Directory.GetParent(gameDir)?.FullName; } catch { }
                if (!string.IsNullOrEmpty(parent))
                {
                    AddDir(Path.Combine(parent, "steam_settings"),
                        "achievements.json", "achievement.json", "stats.json");
                    AddDir(Path.Combine(parent, "SteamSettings"),
                        "achievements.json", "achievement.json", "stats.json");
                }
            }

            return set;
        }

        /// <summary>
        /// Returns the root directories to scan recursively (covers Steam-user-ID subdirs
        /// inside Goldberg / GBE roots, SSE profile subdirs, and other catch-all locations).
        /// </summary>
        private static IEnumerable<string> BuildScanRoots(string exePath, int steamAppId)
        {
            string roaming    = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string local      = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string docs       = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string commonDocs = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
            string progData   = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

            if (!string.IsNullOrEmpty(roaming))
            {
                yield return Path.Combine(roaming, "Goldberg SteamEmu Saves");
                yield return Path.Combine(roaming, "GBE Saves");
                yield return Path.Combine(roaming, "GSE Saves");
                yield return Path.Combine(roaming, "Rune");
                yield return Path.Combine(roaming, "ColdAPI Steam");
                yield return Path.Combine(roaming, "Ali213");
                yield return Path.Combine(roaming, "Voices38");
                // SmartSteamEmu root is scanned recursively so that profile subdirectories
                // (%APPDATA%/SmartSteamEmu/{Profile}/{AppID}/) are discovered automatically.
                yield return Path.Combine(roaming, "SmartSteamEmu");
                yield return Path.Combine(roaming, "SSE-R");
                yield return Path.Combine(roaming, "SSE");
            }
            if (!string.IsNullOrEmpty(local))
            {
                yield return Path.Combine(local, "Goldberg SteamEmu Saves");
                yield return Path.Combine(local, "SmartSteamEmu");
            }
            if (!string.IsNullOrEmpty(commonDocs))
            {
                // Covers CODEX, RUNE, ONLINE_FIX, Skidrow, Plaza, etc. under Steam/
                yield return Path.Combine(commonDocs, "Steam");
                // EMPRESS lives directly under Public Documents (not under Steam/)
                yield return Path.Combine(commonDocs, "EMPRESS");
            }
            // Also scan %PUBLIC%\steam\ (some emulators omit the \Documents\ subfolder)
            string? publicDir = Environment.GetEnvironmentVariable("PUBLIC");
            if (string.IsNullOrEmpty(publicDir) && !string.IsNullOrEmpty(commonDocs))
                publicDir = Directory.GetParent(commonDocs)?.FullName;
            if (!string.IsNullOrEmpty(publicDir))
                yield return Path.Combine(publicDir, "steam");
            if (!string.IsNullOrEmpty(docs))
            {
                yield return Path.Combine(docs, "Steam");
                yield return Path.Combine(docs, "CPY_SAVES");
            }
            if (!string.IsNullOrEmpty(progData))
            {
                yield return Path.Combine(progData, "SteamEmu");
            }
        }

        // ── Parsers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Parses ColdAPI Steam's ColdClientStats.json format:
        /// <code>{ "ACH_ID": { "achieved": 1, "CurProgress": 0 }, … }</code>
        /// </summary>
        private static IEnumerable<string> ParseColdClientStats(string path)
        {
            var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var stream = File.OpenRead(path);
            using var doc    = JsonDocument.Parse(stream);

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return results;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                string id = prop.Name;
                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var inner in prop.Value.EnumerateObject())
                    {
                        if ((string.Equals(inner.Name, "achieved",  StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(inner.Name, "unlocked",  StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(inner.Name, "earned",    StringComparison.OrdinalIgnoreCase)) &&
                            IsPositive(inner.Value))
                        {
                            results.Add(id);
                            break;
                        }
                    }
                }
            }
            return results;
        }

        /// <summary>
        /// Generic JSON parser that handles:
        /// - Goldberg: <c>{ "ACH": { "earned": true } }</c>
        /// - Top-level array: <c>[ { "name": "ACH", "achieved": 1 } ]</c>
        /// - Nested objects with any common unlock field name.
        /// </summary>
        private static IEnumerable<string> ParseJson(string path)
        {
            var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var stream = File.OpenRead(path);
            using var doc    = JsonDocument.Parse(stream);
            VisitElement(doc.RootElement, null, results);
            return results;
        }

        private static void VisitElement(
            JsonElement element, string? contextId, HashSet<string> results)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                string? idCandidate = contextId;
                bool unlocked       = false;
                bool sawUnlockField = false;

                foreach (var prop in element.EnumerateObject())
                {
                    string key = prop.Name;
                    if (IsIdField(key) && prop.Value.ValueKind == JsonValueKind.String)
                        idCandidate = prop.Value.GetString();

                    if (IsUnlockField(key))
                    {
                        sawUnlockField = true;
                        if (IsPositive(prop.Value)) unlocked = true;
                    }
                }

                if (sawUnlockField && unlocked && !string.IsNullOrWhiteSpace(idCandidate))
                    results.Add(idCandidate);

                foreach (var prop in element.EnumerateObject())
                    VisitElement(prop.Value, prop.Name, results);
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                    VisitElement(item, contextId, results);
            }
        }

        /// <summary>
        /// Parses INI-style achievement files written by Codex/Rune/Plaza/SSE/etc.
        ///
        /// Supported layouts:
        /// <code>
        /// ; Layout A – section per achievement
        /// [ACH_NAME]
        /// Achieved=1
        ///
        /// ; Layout B – key=value under [Achievements]
        /// [Achievements]
        /// ACH_NAME=1
        ///
        /// ; Layout C – Smart Steam Emu profile.ini
        /// [ACH_NAME]
        /// earned=1
        /// earn_time=1234567890
        /// </code>
        /// </summary>
        private static IEnumerable<string> ParseIni(string path)
        {
            var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? section = null;

            foreach (var rawLine in File.ReadLines(path))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || line[0] == ';' || line[0] == '#')
                    continue;

                // Section header
                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    section = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();

                bool isTrue = val == "1" ||
                              string.Equals(val, "true",     StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(val, "yes",      StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(val, "unlocked", StringComparison.OrdinalIgnoreCase) ||
                              (long.TryParse(val, out var n) && n > 0);

                if (!isTrue) continue;

                if (IsUnlockField(key))
                {
                    // Layout A / C: section name is the achievement ID
                    if (!string.IsNullOrWhiteSpace(section))
                        results.Add(section);
                }
                else if (section != null &&
                         (string.Equals(section, "Achievements", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(section, "ACHIEVEMENTS", StringComparison.OrdinalIgnoreCase)))
                {
                    // Layout B: key is the achievement ID
                    results.Add(key);
                }
                else if (key.StartsWith("ach", StringComparison.OrdinalIgnoreCase) ||
                         key.Contains("achievement", StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(key);
                }
            }
            return results;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static bool IsIdField(string key) =>
            string.Equals(key, "id",          StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "name",         StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "apiName",      StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "achievement",  StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "stat",         StringComparison.OrdinalIgnoreCase);

        private static bool IsUnlockField(string key) =>
            string.Equals(key, "achieved",    StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "unlocked",    StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "unlock",      StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "unlocktime",  StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "earned",      StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "earn_time",   StringComparison.OrdinalIgnoreCase);

        private static bool IsPositive(JsonElement v)
        {
            return v.ValueKind switch
            {
                JsonValueKind.True   => true,
                JsonValueKind.Number => v.TryGetInt64(out var n) && n > 0,
                JsonValueKind.String =>
                    v.GetString() is string s &&
                    (s.Equals("true",     StringComparison.OrdinalIgnoreCase) ||
                     s.Equals("yes",      StringComparison.OrdinalIgnoreCase) ||
                     s.Equals("unlocked", StringComparison.OrdinalIgnoreCase) ||
                     (long.TryParse(s, out var p) && p > 0)),
                _ => false,
            };
        }

        private static bool PathContainsSegment(string path, string segment)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(segment))
                return false;

            var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var part in parts)
            {
                if (part.Equals(segment, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
