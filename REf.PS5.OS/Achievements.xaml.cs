using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Net.Http;
using System.Collections.Generic;
using System.Xml.Linq;

namespace PS5_OS
{
    public partial class AchievementsControl : UserControl, INotifyPropertyChanged
    {
        private GameItem? _game;
        public GameItem? Game
        {
            get => _game;
            set
            {
                if (_game == value) return;
                _game = value;
                OnPropertyChanged(nameof(Game));
                _ = LoadForGameAsync(_game);
            }
        }

        public ObservableCollection<AchievementEntry> Achievements { get; } = new();

        public AchievementsControl()
        {
            InitializeComponent();
            DataContext = this;
        }

        // --- Public entry point (keeps previous API) ---
        public Task<List<AchievementEntry>> GetSteamAchievementsAsync(GameItem game) => GetOrUpdateSteamAchievementsAsync(game);

        public static Task<List<AchievementEntry>> FetchSteamAchievementsForGame(GameItem game)
        {
            var ctl = new AchievementsControl();
            return ctl.GetSteamAchievementsAsync(game);
        }

        // --- Primary loader used by UI ---
        private async Task LoadForGameAsync(GameItem? game)
        {
            Achievements.Clear();
            if (game == null) return;

            try
            {
                var accountFolder = GetAccountFolder();
                var platformFolder = ResolvePlatformFolderName(game.Platform);
                var gameDir = Path.Combine(accountFolder, platformFolder, SanitizeForPath(game.Title));
                var gameFile = Path.Combine(gameDir, SanitizeForPath(game.Title) + ".json");

                if (File.Exists(gameFile))
                {
                    var json = await File.ReadAllTextAsync(gameFile).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                        var list = JsonSerializer.Deserialize<AchievementEntry[]>(json, options) ?? Array.Empty<AchievementEntry>();
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            foreach (var a in list.OrderByDescending(x => x.IsUnlocked).ThenBy(x => x.Name))
                                Achievements.Add(a);
                        });
                    }

                    return; // only read canonical file if present
                }

                // Fallback: platform-separated cache (Xbox-style)
                var platformList = LoadAchievementsForPlatform(game);
                if (platformList != null && platformList.Count > 0)
                {
                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        foreach (var a in platformList.OrderByDescending(x => x.IsUnlocked).ThenBy(x => x.Name))
                            Achievements.Add(a);
                    });
                    return;
                }

                // Keep UI empty if no file found (project convention)
                return;
            }
            catch
            {
                // ignore errors — UI remains usable
            }
        }

        // --- Platform helpers (consolidated from Xbox app) ---

        // Resolve platform folder name used by canonical per-account path.
        private static string ResolvePlatformFolderName(string? platform)
        {
            if (string.IsNullOrWhiteSpace(platform)) return "Unknown";
            if (string.Equals(platform, "PC", StringComparison.OrdinalIgnoreCase)) return "PC (Windows)";
            return SanitizeForPath(platform);
        }

        // Keep older name for compatibility with other files that call GetPlatformFolderName
        private static string GetPlatformFolderName(string? platform) => ResolvePlatformFolderName(platform);

        // Return true when platform represents PC/Windows so emulator merges only run for PC.
        private static bool IsPlatformPc(string? platform)
        {
            if (string.IsNullOrWhiteSpace(platform)) return false;
            var p = platform.Trim();
            if (string.Equals(p, "PC", StringComparison.OrdinalIgnoreCase)) return true;
            if (p.IndexOf("windows", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (p.IndexOf("pc", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // Determine platform token for a GameItem using emulator executable and folder heuristics.
        private string GetPlatform(GameItem? game)
        {
            if (game == null) return "PC (Windows)";

            string? emulator = null;
            string? folder = null;
            try
            {
                var t = game.GetType();
                var emProp = t.GetProperty("Emulator");
                if (emProp != null) emulator = emProp.GetValue(game) as string;

                var fProp = t.GetProperty("Folder") ?? t.GetProperty("InstallationFolder") ?? t.GetProperty("Path");
                if (fProp != null) folder = fProp.GetValue(game) as string;
            }
            catch { emulator = null; folder = null; }

            if (!string.IsNullOrWhiteSpace(emulator))
            {
                var exe = Path.GetFileNameWithoutExtension(emulator).ToLowerInvariant();
                if (exe.Contains("dolphin")) return "Nintendo - GameCube";
                if (exe.Contains("yuzu") || exe.Contains("ryujinx")) return "Nintendo - Switch";
                if (exe.Contains("xemu")) return "Microsoft - Xbox";
                if (exe.Contains("duckstation")) return "Sony - PlayStation";
                if (exe.Contains("pcsx2")) return "Sony - PlayStation 2";
                if (exe.Contains("rpcs3")) return "Sony - PlayStation 3";
                if (exe.Contains("ppsspp")) return "Sony - PlayStation Portable";
                if (exe.Contains("cemu")) return "Nintendo - Wii U";
                if (exe.Contains("lime 3ds") || exe.Contains("citra")) return "Nintendo - 3DS";
                if (exe.Contains("melon") || exe.Contains("desmume")) return "Nintendo - DS";
                if (exe.Contains("vita3k")) return "Sony - PlayStation Vita";
                if (exe.Contains("xenia")) return "Microsoft - Xbox 360";
                if (exe.Contains("cxbx")) return "Microsoft - Xbox";
                if (exe.Contains("mame")) return "Arcade";
                if (exe.Contains("snes9x") || exe.Contains("zsnes")) return "Nintendo - SNES";
                if (exe.Contains("fceux") || exe.Contains("nestopia")) return "Nintendo - NES";
                if (exe.Contains("mgba") || exe.Contains("visualboyadvance")) return "Nintendo - GameBoy Advance";
                if (exe.Contains("mednafen") || exe.Contains("pcsx")) return "Sony - PlayStation";
                if (exe.Contains("openbor")) return "OpenBOR";
                if (exe.Contains("retroarch")) return "RetroArch";
            }

            if (!string.IsNullOrWhiteSpace(folder))
            {
                var f = folder.Replace('/', '\\');
                if (f.Contains(@"\Repacks\", StringComparison.OrdinalIgnoreCase) ||
                    f.Contains(@"\Games\", StringComparison.OrdinalIgnoreCase) ||
                    f.Contains(@"\PC Games\", StringComparison.OrdinalIgnoreCase))
                    return "PC (Windows)";
                if (f.Contains(@"\Wii U\", StringComparison.OrdinalIgnoreCase)) return "Nintendo - Wii U";
                if (f.Contains(@"\Wii\", StringComparison.OrdinalIgnoreCase)) return "Nintendo - WII";
                if (f.Contains(@"\Switch\", StringComparison.OrdinalIgnoreCase)) return "Nintendo - Switch";
                if (f.Contains(@"\Xbox 360\", StringComparison.OrdinalIgnoreCase)) return "Microsoft - Xbox 360";
                if (f.Contains(@"\Xbox One\", StringComparison.OrdinalIgnoreCase)) return "Microsoft - Xbox One";
                if (f.Contains(@"\Xbox Series\", StringComparison.OrdinalIgnoreCase)) return "Microsoft - Xbox Series";
                if (f.Contains(@"\Xbox Live Arcade\", StringComparison.OrdinalIgnoreCase)) return "Microsoft - Xbox Live Arcade";
                if (f.Contains(@"\Xbox Live Indie\", StringComparison.OrdinalIgnoreCase)) return "Microsoft - Xbox Live Indie";
                if (f.Contains(@"\Xbox\", StringComparison.OrdinalIgnoreCase)) return "Microsoft - Xbox";
                if (f.Contains(@"\PlayStation 3\", StringComparison.OrdinalIgnoreCase) || f.Contains(@"\PS3\", StringComparison.OrdinalIgnoreCase)) return "Sony - PlayStation 3";
                if (f.Contains(@"\PlayStation 4\", StringComparison.OrdinalIgnoreCase) || f.Contains(@"\PS4\", StringComparison.OrdinalIgnoreCase)) return "Sony - PlayStation 4";
                if (f.Contains(@"\PlayStation 5\", StringComparison.OrdinalIgnoreCase) || f.Contains(@"\PS5\", StringComparison.OrdinalIgnoreCase)) return "Sony - PlayStation 5";
                if (f.Contains(@"\PlayStation Vita\", StringComparison.OrdinalIgnoreCase) || f.Contains(@"\PSV\", StringComparison.OrdinalIgnoreCase)) return "Sony - PlayStation Vita";
                if (f.Contains(@"\PlayStation Portable\", StringComparison.OrdinalIgnoreCase) || f.Contains(@"\PSP\", StringComparison.OrdinalIgnoreCase)) return "Sony - PlayStation Portable";
                if (f.Contains(@"\PlayStation 2\", StringComparison.OrdinalIgnoreCase) || f.Contains(@"\PS2\", StringComparison.OrdinalIgnoreCase)) return "Sony - PlayStation 2";
                if (f.Contains(@"\PlayStation\", StringComparison.OrdinalIgnoreCase) || f.Contains(@"\PS1\", StringComparison.OrdinalIgnoreCase)) return "Sony - PlayStation";
                if (f.Contains(@"\Nintendo - 3DS\", StringComparison.OrdinalIgnoreCase)) return "Nintendo - 3DS";
                if (f.Contains(@"\DSi\", StringComparison.OrdinalIgnoreCase)) return "Nintendo - DSi";
                if (f.Contains(@"\DS\", StringComparison.OrdinalIgnoreCase)) return "Nintendo - DS";
                if (f.Contains(@"\GameCube\", StringComparison.OrdinalIgnoreCase)) return "Nintendo - GameCube";
                if (f.Contains(@"\SNES\", StringComparison.OrdinalIgnoreCase) || f.Contains(@"\Snes\", StringComparison.OrdinalIgnoreCase)) return "Nintendo - SNES";
                if (f.Contains(@"\NES\", StringComparison.OrdinalIgnoreCase)) return "Nintendo - NES";
                if (f.Contains(@"\GameBoy Advance\", StringComparison.OrdinalIgnoreCase)) return "Nintendo - GameBoy Advance";
                if (f.Contains(@"\GameBoy Color\", StringComparison.OrdinalIgnoreCase)) return "Nintendo - GameBoy Color";
                if (f.Contains(@"\GameBoy\", StringComparison.OrdinalIgnoreCase)) return "Nintendo - GameBoy";
                if (f.Contains(@"\Arcade\", StringComparison.OrdinalIgnoreCase)) return "Arcade";
                if (f.Contains(@"\OpenBOR\", StringComparison.OrdinalIgnoreCase)) return "OpenBOR";
                if (f.Contains(@"\RetroArch\", StringComparison.OrdinalIgnoreCase)) return "RetroArch";
            }

            return "PC (Windows)";
        }

        // --- Platform-cache loader (Xbox-style) ---

        private List<AchievementEntry> LoadPlatformAchievements(string accountName, string platform, string gameName)
        {
            try
            {
                string achievementsPath = Path.Combine(
                    AppContext.BaseDirectory,
                    "Data", "Accounts", accountName, "Achievements", platform, gameName, $"{gameName}.json");

                if (!File.Exists(achievementsPath))
                    return new List<AchievementEntry>();

                using var stream = File.OpenRead(achievementsPath);
                using var doc = JsonDocument.Parse(stream);

                var list = new List<AchievementEntry>();

                if (doc.RootElement.TryGetProperty("achievements", out var achArray) && achArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in achArray.EnumerateArray())
                    {
                        var date = item.TryGetProperty("DateUnlocked", out var d) ? d.GetString() : null;
                        var unlocked = !string.IsNullOrEmpty(date);
                        list.Add(new AchievementEntry
                        {
                            Name = item.GetProperty("name").GetString(),
                            Description = item.TryGetProperty("desc", out var desc) ? desc.GetString() : null,
                            IconUri = item.TryGetProperty("icon", out var icon) ? icon.GetString() : null,
                            IsUnlocked = unlocked,
                            UnlockedAt = unlocked ? date : null
                        });
                    }
                }
                else if (doc.RootElement.TryGetProperty("Items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        var date = item.TryGetProperty("DateUnlocked", out var d) ? d.GetString() : null;
                        var unlocked = (item.TryGetProperty("IsUnlocked", out var iu) && iu.ValueKind == JsonValueKind.True) || !string.IsNullOrEmpty(date);
                        list.Add(new AchievementEntry
                        {
                            Name = item.GetProperty("Name").GetString(),
                            Description = item.TryGetProperty("Description", out var desc) ? desc.GetString() : null,
                            IconUri = item.TryGetProperty("UrlUnlocked", out var url) ? url.GetString() : null,
                            IsUnlocked = unlocked,
                            UnlockedAt = unlocked ? date : null
                        });
                    }
                }

                return list;
            }
            catch
            {
                return new List<AchievementEntry>();
            }
        }

        private List<AchievementEntry> LoadAchievementsForPlatform(GameItem game)
        {
            string accountName = "Guest";
            try
            {
                if (Application.Current?.Properties["LoggedInAccount"] is string n && !string.IsNullOrWhiteSpace(n))
                    accountName = n;
                else
                {
                    var acctFolder = GetAccountFolder();
                    accountName = Path.GetFileName(acctFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? "Guest";
                }
            }
            catch { accountName = "Guest"; }

            var platform = GetPlatform(game);
            return LoadPlatformAchievements(accountName, platform, SanitizeForPath(game.Title));
        }

        // --- Steam emulator merging (only for PC) ---

        public static void UpdateAchievementsWithSteamEmulators(GameItem game, List<AchievementEntry> merged)
        {
            if (game == null || merged == null || merged.Count == 0) return;
            if (!IsPlatformPc(game.Platform)) return;

            try
            {
                var steamAppId = GetSteamAppIdFromMetadata(game);
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var sanitizedTitle = SanitizeForPath(game.Title);

                var emuRoots = new[]
                {
                    Path.Combine(appData, "GSE Saves"),
                    Path.Combine(appData, "Goldberg SteamEmu Saves"),
                    Path.Combine(appData, "SmartSteamEmu"),
                    Path.Combine(appData, "Steam", "Codex"),
                    Path.Combine(appData, "Steam", "NoCD"),
                    Path.Combine(appData, "Steam", "Crack")
                };

                var candidateFiles = new List<string>(capacity: 32);
                candidateFiles.Add(Path.Combine(appData, "SmartSteamEmu", "achievements.json"));
                candidateFiles.Add(Path.Combine(appData, "GSE Saves", "achievements.json"));
                candidateFiles.Add(Path.Combine(appData, "Goldberg SteamEmu Saves", "achievements.json"));

                foreach (var root in emuRoots)
                {
                    try
                    {
                        if (!Directory.Exists(root)) continue;

                        candidateFiles.Add(Path.Combine(root, sanitizedTitle, "achievements.json"));
                        candidateFiles.Add(Path.Combine(root, sanitizedTitle + ".json"));
                        candidateFiles.Add(Path.Combine(root, sanitizedTitle, sanitizedTitle + ".json"));

                        foreach (var sub in Directory.EnumerateDirectories(root))
                        {
                            try
                            {
                                candidateFiles.Add(Path.Combine(sub, "achievements.json"));
                                candidateFiles.Add(Path.Combine(sub, sanitizedTitle + ".json"));
                                candidateFiles.Add(Path.Combine(sub, Path.GetFileName(sub) + ".json"));
                                candidateFiles.Add(Path.Combine(sub, sanitizedTitle, "achievements.json"));
                            }
                            catch { }
                        }
                    }
                    catch { }
                }

                // also probe near launch/base directories
                try
                {
                    var roots = new List<string>();
                    if (!string.IsNullOrWhiteSpace(game.LaunchPath))
                    {
                        var dir = Path.HasExtension(game.LaunchPath) ? Path.GetDirectoryName(game.LaunchPath) : game.LaunchPath;
                        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)) roots.Add(dir!);

                        var cur = roots.FirstOrDefault() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(cur))
                        {
                            for (var i = 0; i < 3; i++)
                            {
                                try
                                {
                                    var parent = Path.GetDirectoryName(cur);
                                    if (string.IsNullOrWhiteSpace(parent)) break;
                                    if (!roots.Contains(parent)) roots.Add(parent);
                                    cur = parent;
                                }
                                catch { break; }
                            }
                        }
                    }

                    var baseDir = AppContext.BaseDirectory;
                    if (!string.IsNullOrWhiteSpace(baseDir) && Directory.Exists(baseDir) && !roots.Contains(baseDir)) roots.Add(baseDir);

                    foreach (var r in roots)
                    {
                        try
                        {
                            candidateFiles.Add(Path.Combine(r, "achievements.json"));
                            candidateFiles.Add(Path.Combine(r, sanitizedTitle + ".json"));

                            foreach (var sub in Directory.EnumerateDirectories(r))
                            {
                                try
                                {
                                    candidateFiles.Add(Path.Combine(sub, "achievements.json"));
                                    candidateFiles.Add(Path.Combine(sub, sanitizedTitle + ".json"));
                                    candidateFiles.Add(Path.Combine(sub, sanitizedTitle, "achievements.json"));
                                }
                                catch { }
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var candidate in candidateFiles.Where(s => !string.IsNullOrWhiteSpace(s)))
                {
                    try
                    {
                        if (!tried.Add(candidate)) continue;
                        if (!File.Exists(candidate)) continue;

                        var candidateDir = Path.GetDirectoryName(candidate) ?? string.Empty;
                        var candidateContainsAppId = !string.IsNullOrWhiteSpace(steamAppId) &&
                                                    candidateDir.IndexOf(steamAppId, StringComparison.OrdinalIgnoreCase) >= 0;

                        if (!string.IsNullOrWhiteSpace(steamAppId))
                        {
                            var parentName = Path.GetFileName(Path.GetDirectoryName(candidate) ?? string.Empty) ?? string.Empty;
                            var parentLooksLikeAppId = Regex.IsMatch(parentName, @"^\d+$");
                            if (parentLooksLikeAppId && !string.Equals(parentName, steamAppId, StringComparison.OrdinalIgnoreCase))
                                continue;

                            if (!candidateContainsAppId && parentLooksLikeAppId) continue;
                        }

                        string json = File.ReadAllText(candidate);
                        if (string.IsNullOrWhiteSpace(json)) continue;

                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        // only add new achievements when file explicitly targets this AppID
                        bool allowAddNew = candidateContainsAppId;

                        if (root.ValueKind == JsonValueKind.Object)
                        {
                            bool handledKeyed = false;
                            foreach (var prop in root.EnumerateObject())
                            {
                                if (prop.Value.ValueKind == JsonValueKind.Object &&
                                    (prop.Value.TryGetProperty("earned", out var _) || prop.Value.TryGetProperty("earned_time", out var _)))
                                {
                                    handledKeyed = true;
                                    var key = prop.Name;
                                    bool earned = prop.Value.TryGetProperty("earned", out var ep) && ep.ValueKind == JsonValueKind.True;
                                    long earnedTime = prop.Value.TryGetProperty("earned_time", out var tp) && tp.TryGetInt64(out var v) ? v : 0;

                                    var match = merged.FirstOrDefault(a => string.Equals(a.Name, key, StringComparison.OrdinalIgnoreCase));
                                    if (match != null && earned)
                                    {
                                        match.IsUnlocked = true;
                                        if (earnedTime > 0) match.UnlockedAt = DateTimeOffset.FromUnixTimeSeconds(earnedTime).ToString("g");
                                    }
                                    else if (match == null && earned && allowAddNew)
                                    {
                                        merged.Add(new AchievementEntry
                                        {
                                            Name = key,
                                            IsUnlocked = true,
                                            UnlockedAt = earnedTime > 0 ? DateTimeOffset.FromUnixTimeSeconds(earnedTime).ToString("g") : null
                                        });
                                    }
                                }
                            }

                            if (handledKeyed) continue;

                            var maybe = MapJsonElementToAchievement(root);
                            if (maybe != null)
                            {
                                var match = merged.FirstOrDefault(a => string.Equals(a.Name, maybe.Name, StringComparison.OrdinalIgnoreCase));
                                if (match != null)
                                {
                                    if (maybe.IsUnlocked) match.IsUnlocked = true;
                                    if (!string.IsNullOrWhiteSpace(maybe.UnlockedAt)) match.UnlockedAt = maybe.UnlockedAt;
                                }
                                else if (allowAddNew)
                                {
                                    merged.Add(maybe);
                                }
                                continue;
                            }
                        }

                        if (root.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in root.EnumerateArray())
                            {
                                try
                                {
                                    var name = TryGetStringProperty(item, new[] { "name", "title", "label", "id", "Name" });
                                    if (string.IsNullOrWhiteSpace(name)) continue;

                                    var earned = TryGetBoolProperty(item, new[] { "earned", "achieved", "unlocked", "IsUnlocked" });
                                    var earnedTime = 0L;
                                    if (item.ValueKind == JsonValueKind.Object)
                                    {
                                        if (item.TryGetProperty("earned_time", out var tprop) && tprop.ValueKind == JsonValueKind.Number && tprop.TryGetInt64(out var v)) earnedTime = v;
                                        else if (item.TryGetProperty("unlocktime", out var ut) && ut.ValueKind == JsonValueKind.Number && ut.TryGetInt64(out var v2)) earnedTime = v2;
                                    }

                                    var match = merged.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
                                    if (match != null)
                                    {
                                        if (earned) match.IsUnlocked = true;
                                        if (earnedTime > 0) match.UnlockedAt = DateTimeOffset.FromUnixTimeSeconds(earnedTime).ToString("g");
                                    }
                                    else if (earned && allowAddNew)
                                    {
                                        merged.Add(new AchievementEntry
                                        {
                                            Name = name,
                                            Description = TryGetStringProperty(item, new[] { "description", "desc", "details" }),
                                            IsUnlocked = earned,
                                            UnlockedAt = earnedTime > 0 ? DateTimeOffset.FromUnixTimeSeconds(earnedTime).ToString("g") : null
                                        });
                                    }
                                }
                                catch { }
                            }

                            continue;
                        }
                    }
                    catch { }
                }
            }
            catch { /* ignore top-level */ }
        }

        // --- Caching / Steam schema merging ---

        private void CacheFullSteamAchievements(GameItem game, List<AchievementEntry> achievements)
        {
            if (game == null || achievements == null) return;

            try
            {
                var accountFolder = GetAccountFolder();
                var platformFolderName = ResolvePlatformFolderName(game.Platform);
                var finalDir = Path.Combine(accountFolder, platformFolderName, SanitizeForPath(game.Title));
                Directory.CreateDirectory(finalDir);
                var finalFile = Path.Combine(finalDir, SanitizeForPath(game.Title) + ".json");
                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(finalFile, JsonSerializer.Serialize(achievements, options));
            }
            catch { /* non-critical */ }
        }

        private async Task<List<AchievementEntry>> GetOrUpdateSteamAchievementsAsync(GameItem game)
        {
            var result = new List<AchievementEntry>();
            if (game == null) return result;

            try
            {
                var accountFolder = GetAccountFolder();
                var canonicalDir = Path.Combine(accountFolder, ResolvePlatformFolderName(game.Platform), SanitizeForPath(game.Title));
                var canonicalFile = Path.Combine(canonicalDir, $"{SanitizeForPath(game.Title)}.json");

                string? steamAppId = null;

                try
                {
                    var platformFolder = ResolvePlatformFolderName(game.Platform);
                    var metaFileNew = Path.Combine(accountFolder, "Metadata", platformFolder, SanitizeForPath(game.Title), SanitizeForPath(game.Title) + ".json");
                    var metaFileOld = Path.Combine(accountFolder, "Metadata", SanitizeForPath(game.Title) + ".json");

                    string? metaJson = null;
                    if (File.Exists(metaFileNew)) metaJson = File.ReadAllText(metaFileNew);
                    else if (File.Exists(metaFileOld)) metaJson = File.ReadAllText(metaFileOld);

                    if (!string.IsNullOrWhiteSpace(metaJson))
                    {
                        try
                        {
                            var metaDict = JsonSerializer.Deserialize<Dictionary<string, string>>(metaJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (metaDict != null && metaDict.TryGetValue("SteamAppId", out var id) && !string.IsNullOrWhiteSpace(id))
                                steamAppId = id.Trim();
                        }
                        catch { }

                        if (string.IsNullOrWhiteSpace(steamAppId))
                        {
                            try
                            {
                                using var doc = JsonDocument.Parse(metaJson);
                                foreach (var prop in doc.RootElement.EnumerateObject())
                                {
                                    if (string.Equals(prop.Name, "SteamAppId", StringComparison.OrdinalIgnoreCase))
                                    {
                                        var v = prop.Value;
                                        if (v.ValueKind == JsonValueKind.String) steamAppId = v.GetString()?.Trim();
                                        else if (v.ValueKind == JsonValueKind.Number) steamAppId = v.GetRawText().Trim();
                                        else steamAppId = v.ToString()?.Trim();
                                        break;
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }

                if (string.IsNullOrWhiteSpace(steamAppId))
                {
                    if (File.Exists(canonicalFile))
                    {
                        var cached = File.ReadAllText(canonicalFile);
                        return ParseSteamAchievementsJson(cached);
                    }

                    return result;
                }

                using var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("PS5_OS_Achievements/1.0 (+https://example)");

                string? schemaJson = null;

                try
                {
                    var globalUrl = $"https://api.steampowered.com/ISteamUserStats/GetGlobalAchievementPercentagesForApp/v2/?gameid={Uri.EscapeDataString(steamAppId)}";
                    var gResp = await client.GetAsync(globalUrl).ConfigureAwait(false);
                    if (gResp.IsSuccessStatusCode)
                    {
                        var gJson = await gResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(gJson))
                        {
                            using var gDoc = JsonDocument.Parse(gJson);
                            var root = gDoc.RootElement;
                            var achList = new List<Dictionary<string, object>>();
                            if (root.TryGetProperty("achievementpercentages", out var ap) && ap.ValueKind == JsonValueKind.Object &&
                                ap.TryGetProperty("achievements", out var aarr) && aarr.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var it in aarr.EnumerateArray())
                                {
                                    var name = it.TryGetProperty("name", out var n) ? n.GetString() : null;
                                    if (string.IsNullOrWhiteSpace(name)) continue;
                                    var obj = new Dictionary<string, object>
                                    {
                                        ["name"] = name!,
                                        ["description"] = string.Empty,
                                        ["icon"] = null
                                    };
                                    achList.Add(obj);
                                }
                            }

                            if (achList.Count > 0)
                            {
                                var schemaObj = new Dictionary<string, object>
                                {
                                    ["availableGameStats"] = new Dictionary<string, object>
                                    {
                                        ["achievements"] = achList
                                    }
                                };
                                schemaJson = JsonSerializer.Serialize(schemaObj);
                            }
                        }
                    }
                }
                catch { schemaJson = null; }

                if (string.IsNullOrWhiteSpace(schemaJson))
                {
                    var steamDbList = await FetchSteamDbAchievementsAsync(steamAppId).ConfigureAwait(false);
                    if (steamDbList != null && steamDbList.Count > 0)
                    {
                        try { CacheFullSteamAchievements(game, steamDbList); } catch { }
                        return steamDbList;
                    }

                    if (File.Exists(canonicalFile))
                    {
                        var cached = File.ReadAllText(canonicalFile);
                        return ParseSteamAchievementsJson(cached);
                    }

                    return result;
                }

                try
                {
                    Directory.CreateDirectory(canonicalDir);
                    await File.WriteAllTextAsync(canonicalFile, schemaJson).ConfigureAwait(false);
                }
                catch { }

                var schemaList = ParseSteamAchievementsJson(schemaJson) ?? new List<AchievementEntry>();

                try { UpdateAchievementsWithSteamEmulators(game, schemaList); } catch { }

                try { CacheFullSteamAchievements(game, schemaList); } catch { }

                return schemaList;
            }
            catch { }

            return result;
        }

        private async Task<List<AchievementEntry>> FetchSteamDbAchievementsAsync(string appId)
        {
            var result = new List<AchievementEntry>();
            if (string.IsNullOrWhiteSpace(appId)) return result;

            try
            {
                using var client = new HttpClient();
                var url = $"https://steamdb.info/api/SteamAchievementSchema/?appid={Uri.EscapeDataString(appId)}";
                var resp = await client.GetAsync(url).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return result;

                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json)) return result;

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        try
                        {
                            var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                            var desc = item.TryGetProperty("desc", out var d) ? d.GetString() : null;
                            if (string.IsNullOrWhiteSpace(name)) continue;
                            result.Add(new AchievementEntry
                            {
                                Name = name,
                                Description = desc,
                                IsUnlocked = false,
                                UnlockedAt = null,
                                IconUri = item.TryGetProperty("icon", out var ic) ? ic.GetString() : null
                            });
                        }
                        catch { }
                    }
                }
                else if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in root.EnumerateArray())
                    {
                        try
                        {
                            var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                            var desc = item.TryGetProperty("desc", out var d) ? d.GetString() : null;
                            if (string.IsNullOrWhiteSpace(name)) continue;
                            result.Add(new AchievementEntry
                            {
                                Name = name,
                                Description = desc,
                                IsUnlocked = false,
                                UnlockedAt = null,
                                IconUri = item.TryGetProperty("icon", out var ic) ? ic.GetString() : null
                            });
                        }
                        catch { }
                    }
                }
            }
            catch { }

            return result;
        }

        private List<AchievementEntry> ParseSteamAchievementsJson(string json)
        {
            var achievements = new List<AchievementEntry>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                JsonElement? achArray = null;
                if (root.TryGetProperty("game", out var gameElem) && gameElem.ValueKind == JsonValueKind.Object)
                {
                    if (gameElem.TryGetProperty("availableGameStats", out var ags) && ags.ValueKind == JsonValueKind.Object && ags.TryGetProperty("achievements", out var achs) && achs.ValueKind == JsonValueKind.Array)
                        achArray = achs;
                }

                if (achArray == null && root.TryGetProperty("availableGameStats", out var ags2) && ags2.ValueKind == JsonValueKind.Object && ags2.TryGetProperty("achievements", out var achs2) && achs2.ValueKind == JsonValueKind.Array)
                    achArray = achs2;

                if (achArray != null)
                {
                    foreach (var item in achArray.Value.EnumerateArray())
                    {
                        try
                        {
                            string? name = null;
                            string? desc = null;
                            string? icon = null;
                            if (item.TryGetProperty("displayName", out var dn) && dn.ValueKind == JsonValueKind.String) name = dn.GetString();
                            else if (item.TryGetProperty("display_name", out var dn2) && dn2.ValueKind == JsonValueKind.String) name = dn2.GetString();
                            else if (item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) name = n.GetString();

                            if (item.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String) desc = d.GetString();
                            if (item.TryGetProperty("icon", out var ic) && ic.ValueKind == JsonValueKind.String) icon = ic.GetString();
                            else if (item.TryGetProperty("icongray", out var icg) && icg.ValueKind == JsonValueKind.String) icon = icg.GetString();

                            if (string.IsNullOrWhiteSpace(name)) continue;
                            achievements.Add(new AchievementEntry
                            {
                                Name = name,
                                Description = desc,
                                IsUnlocked = false,
                                UnlockedAt = null,
                                IconUri = icon
                            });
                        }
                        catch { }
                    }
                    return achievements;
                }

                if (!root.TryGetProperty("playerstats", out var playerstats)) return achievements;
                if (!playerstats.TryGetProperty("achievements", out var achArray2)) return achievements;

                foreach (var ach in achArray2.EnumerateArray())
                {
                    var title = ach.TryGetProperty("apiname", out var apiname) ? apiname.GetString() : ach.TryGetProperty("name", out var name) ? name.GetString() : null;
                    var desc = ach.TryGetProperty("description", out var d) ? d.GetString() : null;
                    var achieved = ach.TryGetProperty("achieved", out var a) && a.ValueKind == JsonValueKind.Number && a.GetInt32() == 1;
                    var unlocktime = ach.TryGetProperty("unlocktime", out var ut) && ut.ValueKind == JsonValueKind.Number ? ut.GetInt32() : 0;

                    if (string.IsNullOrWhiteSpace(title)) continue;

                    achievements.Add(new AchievementEntry
                    {
                        Name = title,
                        Description = desc,
                        IsUnlocked = achieved,
                        UnlockedAt = unlocktime > 0 ? DateTimeOffset.FromUnixTimeSeconds(unlocktime).ToString("g") : null
                    });
                }
            }
            catch { }

            return achievements;
        }

        // --- Generic discovery / parsing helpers ---

        private static List<AchievementEntry> DiscoverAchievementsFromEmuFolders(GameItem game)
        {
            var result = new List<AchievementEntry>();
            if (game == null) return result;

            try
            {
                var tokens = new[] { "CODEX", "GOLDBERG", "RUNE", "CPY", "SKIDROW", "RELOADED", "EMPRESS", "PLA", "DOGE", "3DM" };
                var extFilter = new[] { ".json", ".txt", ".log", ".cfg", ".ini", ".xml", ".dat" };

                var roots = new List<string>();

                try
                {
                    if (!string.IsNullOrWhiteSpace(game.LaunchPath))
                    {
                        var dir = Path.HasExtension(game.LaunchPath) ? Path.GetDirectoryName(game.LaunchPath) : game.LaunchPath;
                        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                            roots.Add(dir!);
                    }
                }
                catch { }

                var tryFolder = roots.FirstOrDefault() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(tryFolder))
                {
                    var cur = tryFolder;
                    for (var i = 0; i < 3; i++)
                    {
                        try
                        {
                            var parent = Path.GetDirectoryName(cur);
                            if (string.IsNullOrWhiteSpace(parent)) break;
                            if (!roots.Contains(parent)) roots.Add(parent);
                            cur = parent;
                        }
                        catch { break; }
                    }
                }

                try
                {
                    var baseDir = AppContext.BaseDirectory;
                    if (!string.IsNullOrWhiteSpace(baseDir) && Directory.Exists(baseDir))
                        roots.Add(baseDir);
                }
                catch { }

                roots = roots.Where(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r))
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .ToList();

                foreach (var root in roots)
                {
                    try
                    {
                        InspectFolderForAchievementFiles(root, extFilter, result);

                        foreach (var sub in Directory.EnumerateDirectories(root))
                        {
                            try
                            {
                                var name = Path.GetFileName(sub) ?? string.Empty;
                                if (tokens.Any(t => name.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0))
                                {
                                    InspectFolderForAchievementFiles(sub, extFilter, result, maxDepth: 3);
                                }
                                else
                                {
                                    var subName = name.ToLowerInvariant();
                                    if (subName.Contains("crack") || subName.Contains("scene") || subName.Contains("no-cd") || subName.Contains("no_cd"))
                                    {
                                        InspectFolderForAchievementFiles(sub, extFilter, result, maxDepth: 3);
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }

                var distinct = result.GroupBy(x => (x.Name ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase)
                                     .Select(g => g.First())
                                     .ToList();
                return distinct;
            }
            catch
            {
                return result;
            }
        }

        private static void InspectFolderForAchievementFiles(string folder, string[] extensions, List<AchievementEntry> accumulator, int maxDepth = 2)
        {
            try
            {
                foreach (var file in EnumerateFilesLimitedDepth(folder, extensions, maxDepth))
                {
                    try
                    {
                        var ext = Path.GetExtension(file);
                        if (string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase))
                        {
                            ParseJsonAchievementFile(file, accumulator);
                        }
                        else if (string.Equals(ext, ".xml", StringComparison.OrdinalIgnoreCase))
                        {
                            ParseXmlAchievementFile(file, accumulator);
                        }
                        else
                        {
                            ParseTextAchievementFile(file, accumulator);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static IEnumerable<string> EnumerateFilesLimitedDepth(string folder, string[] extensions, int maxDepth)
        {
            var results = new List<string>(capacity: 64);
            var pending = new Queue<(string path, int depth)>();
            pending.Enqueue((folder, 0));
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while (pending.Count > 0)
            {
                var (path, depth) = pending.Dequeue();
                if (depth > maxDepth) continue;

                try
                {
                    foreach (var f in Directory.EnumerateFiles(path))
                    {
                        var ext = Path.GetExtension(f);
                        if (extensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)))
                        {
                            if (set.Add(f)) results.Add(f);
                        }
                    }

                    if (depth < maxDepth)
                    {
                        foreach (var d in Directory.EnumerateDirectories(path))
                        {
                            pending.Enqueue((d, depth + 1));
                        }
                    }
                }
                catch { }
            }

            return results;
        }

        private static void ParseJsonAchievementFile(string filePath, List<AchievementEntry> accumulator)
        {
            try
            {
                var json = File.ReadAllText(filePath);
                if (string.IsNullOrWhiteSpace(json)) return;

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in root.EnumerateArray())
                    {
                        var parsed = MapJsonElementToAchievement(item);
                        if (parsed != null) accumulator.Add(parsed);
                    }
                }
                else if (root.ValueKind == JsonValueKind.Object)
                {
                    bool added = false;
                    foreach (var prop in root.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var item in prop.Value.EnumerateArray())
                            {
                                var parsed = MapJsonElementToAchievement(item);
                                if (parsed != null) accumulator.Add(parsed);
                                added = true;
                            }
                        }
                    }

                    if (!added)
                    {
                        var maybe = MapJsonElementToAchievement(root);
                        if (maybe != null) accumulator.Add(maybe);
                        else
                        {
                            foreach (var prop in root.EnumerateObject())
                            {
                                var name = prop.Name;
                                bool earned = prop.Value.TryGetProperty("earned", out var e) && e.ValueKind == JsonValueKind.True;
                                long time = prop.Value.TryGetProperty("earned_time", out var t) && t.TryGetInt64(out var v) ? v : 0;
                                if (!string.IsNullOrWhiteSpace(name))
                                {
                                    accumulator.Add(new AchievementEntry
                                    {
                                        Name = name,
                                        IsUnlocked = earned,
                                        UnlockedAt = time > 0 ? DateTimeOffset.FromUnixTimeSeconds(time).ToString("g") : null
                                    });
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private static void ParseXmlAchievementFile(string filePath, List<AchievementEntry> accumulator)
        {
            try
            {
                var doc = XDocument.Load(filePath);
                if (doc.Root == null) return;

                var nodes = doc.Descendants().Where(x => x.Name.LocalName.IndexOf("ach", StringComparison.OrdinalIgnoreCase) >= 0);
                foreach (var n in nodes)
                {
                    try
                    {
                        var name = n.Elements().FirstOrDefault(e => e.Name.LocalName.IndexOf("name", StringComparison.OrdinalIgnoreCase) >= 0)?.Value
                                   ?? n.Attribute("name")?.Value
                                   ?? n.Value;

                        var desc = n.Elements().FirstOrDefault(e => e.Name.LocalName.IndexOf("desc", StringComparison.OrdinalIgnoreCase) >= 0)?.Value
                                   ?? n.Attribute("desc")?.Value;

                        var unlockedAttr = n.Elements().FirstOrDefault(e => e.Name.LocalName.IndexOf("unlock", StringComparison.OrdinalIgnoreCase) >= 0)?.Value
                                   ?? n.Attribute("unlocked")?.Value;

                        bool unlocked = false;
                        if (!string.IsNullOrWhiteSpace(unlockedAttr) && (unlockedAttr.Equals("1") || unlockedAttr.Equals("true", StringComparison.OrdinalIgnoreCase)))
                            unlocked = true;

                        if (string.IsNullOrWhiteSpace(name)) continue;

                        accumulator.Add(new AchievementEntry
                        {
                            Name = name,
                            Description = desc,
                            IsUnlocked = unlocked,
                            IconUri = n.Attribute("icon")?.Value
                        });
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static void ParseTextAchievementFile(string filePath, List<AchievementEntry> accumulator)
        {
            try
            {
                var text = File.ReadAllText(filePath);
                if (string.IsNullOrWhiteSpace(text)) return;

                var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var raw in lines)
                {
                    var line = raw.Trim();
                    if (line.Length < 3) continue;

                    var lower = line.ToLowerInvariant();
                    if (!(lower.Contains("ach") || lower.Contains("unlock") || lower.Contains("trophy") || lower.Contains("achievement")))
                        continue;

                    var m = Regex.Match(line, "\"([^\"]{3,})\"");
                    string? name = null;
                    if (m.Success) name = m.Groups[1].Value.Trim();
                    else
                    {
                        var parts = line.Split(new[] { ':', '-' }, 2);
                        if (parts.Length >= 2) name = parts[1].Trim();
                        else name = line.Length > 120 ? line.Substring(0, 120) : line;
                    }

                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var isUnlocked = lower.Contains("unlocked") || lower.Contains("[x]") || lower.Contains("(unlocked)") || lower.Contains("achieved");

                    accumulator.Add(new AchievementEntry
                    {
                        Name = name,
                        Description = null,
                        IsUnlocked = isUnlocked
                    });
                }
            }
            catch { }
        }

        private static AchievementEntry? MapJsonElementToAchievement(JsonElement elem)
        {
            try
            {
                if (elem.ValueKind != JsonValueKind.Object) return null;

                string? name = null;
                string? desc = null;
                bool isUnlocked = false;
                DateTime? unlockedAt = null;
                string? icon = null;

                foreach (var p in elem.EnumerateObject())
                {
                    var pn = p.Name.Trim().ToLowerInvariant();
                    if (pn.Contains("name") || pn.Contains("title") || pn.Contains("displayname") || pn.Contains("display_name"))
                        name = p.Value.GetString() ?? name;
                    else if (pn.Contains("desc") || pn.Contains("description") || pn.Contains("details"))
                        desc = p.Value.GetString() ?? desc;
                    else if (pn.Contains("unlock") || pn.Contains("achieved") || pn.Contains("isunlocked"))
                    {
                        if (p.Value.ValueKind == JsonValueKind.True) isUnlocked = true;
                        else if (p.Value.ValueKind == JsonValueKind.False) isUnlocked = false;
                        else if (p.Value.ValueKind == JsonValueKind.Number)
                        {
                            if (p.Value.TryGetInt32(out var n)) isUnlocked = n != 0;
                        }
                        else
                        {
                            var s = p.Value.GetString();
                            if (!string.IsNullOrWhiteSpace(s) && (s.Equals("1") || s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("yes", StringComparison.OrdinalIgnoreCase)))
                                isUnlocked = true;
                        }
                    }
                    else if (pn.Contains("date") || pn.Contains("time"))
                    {
                        var s = p.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(s) && DateTime.TryParse(s, out var dt)) unlockedAt = dt;
                        else if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt64(out var unix))
                            unlockedAt = DateTimeOffset.FromUnixTimeSeconds(unix).DateTime;
                    }
                    else if (pn.Contains("icon") || pn.Contains("image"))
                        icon = p.Value.GetString();
                }

                if (string.IsNullOrWhiteSpace(name)) return null;

                return new AchievementEntry
                {
                    Name = name,
                    Description = desc,
                    IsUnlocked = isUnlocked,
                    UnlockedAt = unlockedAt.HasValue ? unlockedAt.Value.ToString("g") : null,
                    IconUri = icon
                };
            }
            catch { return null; }
        }

        // --- Small JSON helpers used across methods ---

        private static string? TryGetStringProperty(JsonElement elt, string[] candidates)
        {
            if (elt.ValueKind != JsonValueKind.Object) return null;
            foreach (var prop in elt.EnumerateObject())
            {
                foreach (var c in candidates)
                {
                    if (string.Equals(prop.Name, c, StringComparison.OrdinalIgnoreCase))
                    {
                        var p = prop.Value;
                        if (p.ValueKind == JsonValueKind.String) return p.GetString();
                        try { return p.ToString(); } catch { return null; }
                    }
                }
            }
            return null;
        }

        private static bool TryGetBoolProperty(JsonElement elt, string[] candidates)
        {
            if (elt.ValueKind != JsonValueKind.Object) return false;
            foreach (var prop in elt.EnumerateObject())
            {
                foreach (var c in candidates)
                {
                    if (string.Equals(prop.Name, c, StringComparison.OrdinalIgnoreCase))
                    {
                        var p = prop.Value;
                        if (p.ValueKind == JsonValueKind.True) return true;
                        if (p.ValueKind == JsonValueKind.False) return false;
                        if (p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var n)) return n != 0;
                        if (p.ValueKind == JsonValueKind.String && bool.TryParse(p.GetString(), out var b)) return b;
                        return false;
                    }
                }
            }
            return false;
        }

        private static double TryGetDoubleProperty(JsonElement elt, string[] candidates)
        {
            if (elt.ValueKind != JsonValueKind.Object) return 0.0;
            foreach (var prop in elt.EnumerateObject())
            {
                foreach (var c in candidates)
                {
                    if (string.Equals(prop.Name, c, StringComparison.OrdinalIgnoreCase))
                    {
                        var p = prop.Value;
                        if (p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var d)) return d;
                        if (p.ValueKind == JsonValueKind.String && double.TryParse(p.GetString(), out var dd)) return dd;
                    }
                }
            }
            return 0.0;
        }

        // --- Account / path helpers ---

        private string GetAccountFolder()
        {
            try
            {
                if (Application.Current?.Properties["LoggedInAccountPath"] is string p && !string.IsNullOrWhiteSpace(p))
                {
                    if (!Directory.Exists(p)) Directory.CreateDirectory(p);
                    return p;
                }

                if (Application.Current?.Properties["LoggedInAccount"] is string name && !string.IsNullOrWhiteSpace(name))
                {
                    var folder = Path.Combine(AppContext.BaseDirectory, "Data", "Accounts", name);
                    if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                    return folder;
                }

                var guest = Path.Combine(AppContext.BaseDirectory, "Data", "Accounts", "Guest");
                if (!Directory.Exists(guest)) Directory.CreateDirectory(guest);
                return guest;
            }
            catch
            {
                var safe = Path.Combine(AppContext.BaseDirectory, "Data", "Accounts", "Guest");
                Directory.CreateDirectory(safe);
                return safe;
            }
        }   

        private static string SanitizeForPath(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "Unknown";
            var s = input.Trim();
            if ((s.StartsWith("\"") && s.EndsWith("\"")) || (s.StartsWith("'") && s.EndsWith("'")))
                s = s.Substring(1, s.Length - 2).Trim();

            s = s.Replace(":", " - ");
            s = s.Replace("／", " - ").Replace("/", " - ").Replace("\\", " - ");
            s = s.Replace("–", "-").Replace("—", "-");
            s = s.Replace('_', ' ');

            var invalid = Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var ch in s) sb.Append(invalid.Contains(ch) ? ' ' : ch);
            s = sb.ToString();

            s = Regex.Replace(s, @"\s*-\s*", " - ");
            s = Regex.Replace(s, @"\s{2,}", " ").Trim();
            s = Regex.Replace(s, @"-+", "-");
            s = s.Trim(' ', '-', '_');
            s = Regex.Replace(s, @"\s{2,}", " ").Trim();

            return string.IsNullOrWhiteSpace(s) ? "Unknown" : s;
        }

        // Read Steam AppId from per-game metadata (returns null if not found)
        private static string? GetSteamAppIdFromMetadata(GameItem? game)
        {
            if (game == null) return null;
            try
            {
                var accountFolder = (Application.Current?.Properties["LoggedInAccountPath"] as string) ??
                                    Path.Combine(AppContext.BaseDirectory, "Data", "Accounts", (Application.Current?.Properties["LoggedInAccount"] as string) ?? "Guest");

                var platformFolder = ResolvePlatformFolderName(game.Platform);
                var metaFileNew = Path.Combine(accountFolder, "Metadata", platformFolder, SanitizeForPath(game.Title), SanitizeForPath(game.Title) + ".json");
                var metaFileOld = Path.Combine(accountFolder, "Metadata", SanitizeForPath(game.Title) + ".json");

                string? metaJson = null;
                if (File.Exists(metaFileNew)) metaJson = File.ReadAllText(metaFileNew);
                else if (File.Exists(metaFileOld)) metaJson = File.ReadAllText(metaFileOld);

                if (string.IsNullOrWhiteSpace(metaJson)) return null;

                try
                {
                    var dict = JsonSerializer.Deserialize<Dictionary<string, object?>>(metaJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (dict != null && dict.TryGetValue("SteamAppId", out var raw) && raw != null)
                        return raw.ToString()?.Trim();
                }
                catch { }

                try
                {
                    using var doc = JsonDocument.Parse(metaJson);
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (string.Equals(prop.Name, "SteamAppId", StringComparison.OrdinalIgnoreCase))
                        {
                            var v = prop.Value;
                            if (v.ValueKind == JsonValueKind.String) return v.GetString()?.Trim();
                            if (v.ValueKind == JsonValueKind.Number) return v.GetRawText().Trim();
                            return v.ToString()?.Trim();
                        }
                    }
                }
                catch { }
            }
            catch { }

            return null;
        }

        // --- INotifyPropertyChanged ---
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            // Fire-and-forget refresh; LoadForGameAsync handles exceptions internally.
            _ = LoadForGameAsync(Game);
        }
    }

    public sealed class AchievementEntry
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public bool IsUnlocked { get; set; }
        public string? UnlockedAt { get; set; }
        public string? IconUri { get; set; }
    }
}