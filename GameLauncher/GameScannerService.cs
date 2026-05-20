using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GameLauncher.Models;
using GameLauncher.Services;

namespace GameLauncher;

/// <summary>
/// Scans all mounted drives for Games/$GameFolder and Repacks directories,
/// detects valid executables and repack archives, watches for live changes,
/// and caches results locally.
/// </summary>
public sealed class GameScannerService : IDisposable
{
    // ── Events ────────────────────────────────────────────────────────────────
    public event Action<List<LocalGame>>?   GamesUpdated;
    public event Action<List<LocalRepack>>? RepacksUpdated;
    public event Action<List<LocalRom>>?    RomsUpdated;

    // ── Internal state ────────────────────────────────────────────────────────
    private readonly List<LocalGame>          _games   = new();
    private readonly List<LocalRepack>        _repacks = new();
    private readonly List<LocalRom>           _roms    = new();
    private readonly List<FileSystemWatcher>  _watchers= new();
    private readonly SemaphoreSlim            _lock    = new(1, 1);
    private CancellationTokenSource?          _debounceCts;
    // Lifetime CTS — cancelled in Dispose() to stop the periodic rescan loop.
    private readonly CancellationTokenSource  _lifetimeCts = new();
    // Task returned by PeriodicRescanAsync — stored so Dispose can wait for it.
    private Task?                             _periodicTask;

    // How often the background timer rescans all drives.  Two minutes is short
    // enough to notice a freshly-mounted USB drive without hammering the disk.
    private static readonly TimeSpan PeriodicInterval = TimeSpan.FromMinutes(2);

    // ── Cache paths ───────────────────────────────────────────────────────────
    private static readonly string CacheDir  = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GameOS");
    private static readonly string GameCache  = Path.Combine(CacheDir, "detected_games.json");
    private static readonly string RepackCache= Path.Combine(CacheDir, "detected_repacks.json");
    private static readonly string RomCache   = Path.Combine(CacheDir, "detected_roms.json");

    // ── Public snapshots ──────────────────────────────────────────────────────
    public IReadOnlyList<LocalGame>   Games   => _games;
    public IReadOnlyList<LocalRepack> Repacks => _repacks;
    public IReadOnlyList<LocalRom>    Roms    => _roms;

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Performs an initial scan (with cache fallback), starts background watchers,
    /// and kicks off a periodic background rescan to detect newly mounted drives.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        try
        {
            DevLogService.Log("[Scanner] StartAsync — beginning scan.");
            // Try loading from cache first for faster startup
            if (TryLoadCache())
            {
                DevLogService.Log($"[Scanner] Cache loaded: {_games.Count} games, {_repacks.Count} repacks, {_roms.Count} ROMs.");
                GamesUpdated?.Invoke(new List<LocalGame>(_games));
                RepacksUpdated?.Invoke(new List<LocalRepack>(_repacks));
                RomsUpdated?.Invoke(new List<LocalRom>(_roms));
            }
            else
            {
                DevLogService.Log("[Scanner] No cache found — starting fresh scan.");
            }

            // Always do a fresh scan to stay current
            await ScanAllDrivesAsync(ct).ConfigureAwait(false);
            StartWatchers();

            // Kick off the periodic rescan loop (uses the lifetime CTS so it stops on Dispose).
            _periodicTask = PeriodicRescanAsync(_lifetimeCts.Token);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            DevLogService.Log($"[Scanner] StartAsync failed: {ex.Message}");
        }
    }

    /// <summary>Re-scans all drives on demand.</summary>
    public async Task RescanAsync(CancellationToken ct = default)
    {
        await ScanAllDrivesAsync(ct);
    }

    /// <summary>
    /// Background loop that rescans all drives every <see cref="PeriodicInterval"/> so
    /// that drives mounted after startup (e.g. USB sticks or newly shared folders) are
    /// discovered automatically without requiring the user to restart the app.
    /// Also refreshes file-system watchers so any new game/repack/ROM directories are
    /// monitored for live changes.
    /// <para>
    /// The loop is sequential: the <see cref="PeriodicInterval"/> delay begins AFTER
    /// the previous scan completes, so concurrent scans never accumulate.
    /// </para>
    /// </summary>
    private async Task PeriodicRescanAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(PeriodicInterval, ct).ConfigureAwait(false);
                    DevLogService.Log("[Scanner] Periodic rescan triggered.");
                    await ScanAllDrivesAsync(ct).ConfigureAwait(false);
                    // Re-create watchers so any directories that now exist (but didn't at
                    // startup) are monitored for live file-system changes.
                    StartWatchers();
                }
                catch (OperationCanceledException)
                {
                    // Intentional shutdown via Dispose() — return cleanly without logging
                    // "terminated unexpectedly", which is reserved for genuine errors.
                    return;
                }
                catch (Exception ex)
                {
                    DevLogService.Log($"[Scanner] Periodic rescan error: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            DevLogService.Log($"[Scanner] PeriodicRescanAsync terminated unexpectedly: {ex.Message}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Drive detection (cross-platform)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Returns all drive/volume root paths for the current OS.</summary>
    internal static IEnumerable<string> GetDriveRoots()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            DriveInfo[] drives;
            try { drives = DriveInfo.GetDrives(); }
            catch { yield break; }

            foreach (var drive in drives)
            {
                bool isReady = false;
                try { isReady = drive.IsReady; } catch { }
                if (isReady)
                    yield return drive.RootDirectory.FullName; // e.g. C:\
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // macOS: volumes live under /Volumes
            yield return "/";
            if (Directory.Exists("/Volumes"))
            {
                IEnumerable<string> volumes;
                try { volumes = Directory.EnumerateDirectories("/Volumes"); }
                catch { yield break; }
                foreach (var v in volumes)
                    yield return v;
            }
        }
        else
        {
            // Linux: root, /mnt, /media, and home directory.
            yield return "/";

            foreach (var mountRoot in new[] { "/mnt", "/media" })
            {
                if (!Directory.Exists(mountRoot)) continue;

                IEnumerable<string> level1;
                try
                {
                    // ToList() forces eager evaluation so the directory access (and any
                    // resulting exception) happens inside this try block, not lazily
                    // inside the foreach below.
                    level1 = Directory.EnumerateDirectories(mountRoot).ToList();
                }
                catch (UnauthorizedAccessException) { continue; }
                catch (IOException) { continue; }

                foreach (var sub in level1)
                {
                    yield return sub; // e.g. /mnt/disk1 or /media/username

                    // On Ubuntu/Debian removable drives are mounted at
                    // /media/<username>/<label> (two levels deep), so we must
                    // descend one extra level for /media/* entries.
                    if (!string.Equals(mountRoot, "/media", StringComparison.Ordinal))
                        continue;

                    IEnumerable<string> level2;
                    try
                    {
                        // Same eager-evaluation rationale as level1 above.
                        level2 = Directory.EnumerateDirectories(sub).ToList();
                    }
                    catch (UnauthorizedAccessException) { continue; }
                    catch (IOException) { continue; }

                    foreach (var sub2 in level2)
                        yield return sub2; // e.g. /media/username/MyUSBDrive
                }
            }

            // Also include the user's home directory so ~/Games etc. are found.
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home) && Directory.Exists(home))
                yield return home;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Scanning
    // ─────────────────────────────────────────────────────────────────────────

    private async Task ScanAllDrivesAsync(CancellationToken ct)
    {
        var foundGamesRaw = new List<LocalGame>();
        var foundRepacks  = new List<LocalRepack>();
        var foundRomsRaw  = new List<LocalRom>();

        var driveRoots = GetDriveRoots().ToList();
        DevLogService.Log($"[Scanner] Scanning {driveRoots.Count} drive root(s): {string.Join(", ", driveRoots)}");
        await Task.Run(() =>
        {
            foreach (var driveRoot in driveRoots)
            {
                ct.ThrowIfCancellationRequested();
                ScanGamesDir(driveRoot, foundGamesRaw);
                ScanStorefrontDirs(driveRoot, foundGamesRaw);
                ScanRepacksDir(driveRoot, foundRepacks);
                ScanRomsDir(driveRoot, foundRomsRaw);
            }

            // ── Normalize abbreviated folder names found in Games/ directories ──
            // Steam uses short install-directory names (e.g. "LHPCR" for
            // "LEGO® Harry Potter™ Collection") that differ from the game's
            // display name.  ACF manifest scanning (ScanSteamAcfManifests) runs
            // alongside ScanGamesDir and records the proper name for each
            // installdir.  Build a folder-name → proper-title lookup from those
            // ACF-discovered entries (Source = "Steam") and apply it to any
            // Games/-folder entries (Source = "Local") whose title matches a raw
            // Steam install directory name.
            var steamNameByFolder = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in foundGamesRaw)
            {
                if (g.Source != "Steam" || string.IsNullOrEmpty(g.FolderPath)) continue;
                string folderKey = Path.GetFileName(g.FolderPath);
                if (!steamNameByFolder.ContainsKey(folderKey))
                    steamNameByFolder[folderKey] = g.Title;
            }

            foreach (var g in foundGamesRaw)
            {
                if (g.Source != "Local" || string.IsNullOrEmpty(g.FolderPath)) continue;
                string folderName = Path.GetFileName(g.FolderPath);
                if (steamNameByFolder.TryGetValue(folderName, out var properName) &&
                    !string.Equals(g.Title, properName, StringComparison.OrdinalIgnoreCase))
                {
                    DevLogService.Log(
                        $"[Scanner] Normalized local game title: \"{g.Title}\" → \"{properName}\"");
                    g.Title = properName;
                }
            }
        }, ct);

        // Group same-title games found on multiple drives into a single LocalGame.
        // Use NormalizeGameTitle as the group key so "Call of Duty - Ghosts" and
        // "Call of Duty: Ghosts" are treated as the same game.
        var foundGames = new List<LocalGame>();
        foreach (var grp in foundGamesRaw.GroupBy(
            g => NormalizeGameTitle(g.Title), System.StringComparer.OrdinalIgnoreCase))
        {
            var items = grp.ToList();
            var primary = items[0];
            // Normalize the stored title so "Call of Duty - Ghosts" displays as
            // "Call of Duty: Ghosts" consistently across all sources.
            primary.Title = NormalizeGameTitle(primary.Title);
            primary.DriveInstances = items.Select(g => new Models.LocalGameDriveEntry
            {
                DriveRoot      = g.DriveRoot,
                FolderPath     = g.FolderPath,
                ExecutablePath = g.ExecutablePath,
                ExecutableType = g.ExecutableType,
            }).ToList();
            foundGames.Add(primary);
        }

        // Merge ROMs with identical base title + platform into a single entry,
        // aggregating regions and additional file paths.
        var foundRoms = new List<LocalRom>();
        foreach (var grp in foundRomsRaw.GroupBy(
            r => $"{r.Platform}|{r.Title}", StringComparer.OrdinalIgnoreCase))
        {
            var items   = grp.ToList();
            var primary = items[0];
            // Use a HashSet for O(1) region deduplication during merging.
            var regionSet = new HashSet<string>(primary.Regions, StringComparer.OrdinalIgnoreCase);
            foreach (var extra in items.Skip(1))
            {
                foreach (var region in extra.Regions)
                    if (regionSet.Add(region))
                        primary.Regions.Add(region);
                primary.AdditionalPaths.Add(extra.FilePath);
                primary.SizeBytes += extra.SizeBytes;
            }
            foundRoms.Add(primary);
        }

        // Mark repacks whose cleaned title matches an installed game as "Installed".
        // Build the set with multiple normalized variants so matching works even when
        // symbols differ (e.g. "LEGO® Harry Potter™ Collection" vs "LEGO Harry Potter Collection")
        // or subtitle separators differ ("Call of Duty - Ghosts" vs "Call of Duty: Ghosts").
        var gameTitleSet = BuildFuzzyTitleSet(foundGames.Select(g => g.Title));
        foreach (var repack in foundRepacks)
            repack.IsInstalledGame = RepackMatchesInstalledTitle(repack.Title, gameTitleSet);

        await _lock.WaitAsync(ct);
        try
        {
            _games.Clear();
            _games.AddRange(foundGames);
            _repacks.Clear();
            _repacks.AddRange(foundRepacks);
            _roms.Clear();
            _roms.AddRange(foundRoms);
        }
        finally
        {
            _lock.Release();
        }

        SaveCache();
        DevLogService.Log($"[Scanner] Scan complete: {_games.Count} games, {_repacks.Count} repacks, {_roms.Count} ROMs.");
        GamesUpdated?.Invoke(new List<LocalGame>(_games));
        RepacksUpdated?.Invoke(new List<LocalRepack>(_repacks));
        RomsUpdated?.Invoke(new List<LocalRom>(_roms));
    }

    /// <summary>Scan <paramref name="driveRoot"/>/Games for game folders.</summary>
    private static void ScanGamesDir(string driveRoot, List<LocalGame> results)
    {
        string gamesPath = Path.Combine(driveRoot, "Games");
        if (!Directory.Exists(gamesPath)) return;

        int beforeCount = results.Count;
        int folderCount = 0;

        try
        {
            foreach (var gameFolder in Directory.EnumerateDirectories(gamesPath))
            {
                folderCount++;
                // Try top-level first, then fall back to two levels deep for games
                // whose main executable is inside a sub-directory (e.g. Binaries/Win64/).
                var exe = FindExecutable(gameFolder)
                       ?? FindExecutableDeep(gameFolder, maxDepth: 2);
                if (exe is null) continue;

                // Prefer the real display name stored in .gameos-title (written by the
                // launcher when a repack is installed) over the raw folder name.
                string title = ReadGameOsTitle(gameFolder) ?? Path.GetFileName(gameFolder);

                results.Add(new LocalGame
                {
                    Title          = title,
                    ExecutablePath = exe.FullPath,
                    ExecutableType = exe.Type,
                    FolderPath     = gameFolder,
                    DriveRoot      = driveRoot,
                    Source         = "Local",
                });

                DevLogService.LogGamesAdvanced($"[Games] Found: \"{title}\" ({gameFolder})");
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        int found = results.Count - beforeCount;
        DevLogService.LogGames(
            $"[Games] Drive \"{driveRoot}\" — path: \"{gamesPath}\" | " +
            $"Total Folders: {folderCount}, Games Found: {found}");
    }

    /// <summary>
    /// Scans the default installation directories used by major PC game storefronts
    /// (Steam, Epic Games, GOG Galaxy, EA/Origin, Ubisoft Connect, Xbox) on the
    /// given drive root.  Any game folder found here is added to
    /// <paramref name="results"/> using the same <see cref="LocalGame"/> shape as
    /// games found under <c>{driveRoot}/Games</c>.
    /// <para>
    /// On Windows the canonical locations checked per drive are:
    /// <list type="bullet">
    ///   <item>Steam — <c>Program Files (x86)\Steam\steamapps\common\</c><br/>
    ///     Additional Steam library folders are discovered from
    ///     <c>libraryfolders.vdf</c> so per-drive Steam libraries are also found.</item>
    ///   <item>Epic Games — <c>Program Files\Epic Games\</c></item>
    ///   <item>GOG Galaxy — <c>Program Files (x86)\GOG Galaxy\Games\</c></item>
    ///   <item>EA / Origin — <c>Program Files (x86)\Origin Games\</c>,
    ///     <c>Program Files\EA Games\</c></item>
    ///   <item>Ubisoft Connect — <c>Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games\</c></item>
    ///   <item>Xbox / Game Pass — <c>XboxGames\</c></item>
    /// </list>
    /// </para>
    /// On non-Windows platforms only the Xbox path is skipped; the others are
    /// checked under their Linux/macOS equivalents where applicable.
    /// </summary>
    private static void ScanStorefrontDirs(string driveRoot, List<LocalGame> results)
    {
        // ── Helper: scan one flat storefront directory ─────────────────────
        // Skips any folder already present in results (e.g. added by an ACF or .item
        // manifest with the proper display name) so manifest-based names take priority
        // over raw folder names such as "LHPCR" (LEGO® Harry Potter™ Collection).
        //
        // acfNames: optional installdir→name lookup built by BuildAcfInstallDirNames.
        // When provided, the ACF display name is used in preference to the raw folder
        // name for games that the StateFlags-filtered ACF scan skipped (e.g. a game
        // whose ACF has StateFlags=2 meaning "download queued" but whose folder and
        // executable already exist on disk).
        //
        // acfNamesOnly: when true, only adds folders that appear in acfNames.
        // Intended for use with Steam steamapps/common/ directories, where this
        // mirrors the Game Store reference approach: only surfaces folders that have
        // a corresponding ACF installdir entry.  Steam utility folders with no ACF
        // file — "Steamworks Common Redistributables", "SteamVR", "steam_settings"
        // sub-directories, etc. — are therefore never added as fake game cards.
        // Requires acfNames to be non-null and non-empty; if acfNames is null when
        // acfNamesOnly is true, a warning is logged and the parameter is ignored.
        static void ScanDir(string path, List<LocalGame> results, string driveRoot,
                            string source = "Local",
                            IReadOnlyDictionary<string, string>? acfNames = null,
                            bool acfNamesOnly = false)
        {
            if (!Directory.Exists(path)) return;

            // Guard against misconfiguration: acfNamesOnly without an acfNames map
            // would silently skip every folder.  Log a warning and fall back to the
            // standard (non-strict) scan so no games are lost.
            bool strictMode = acfNamesOnly;
            if (strictMode && acfNames == null)
            {
                DevLogService.LogLocalSteam(
                    $"[LocalSteam/{source}] WARNING: acfNamesOnly=true but acfNames is null for \"{path}\" — falling back to non-strict scan");
                strictMode = false;
            }

            int beforeCount = results.Count;
            try
            {
                // Pre-build the existing folder-path set (O(n)) so each per-folder
                // duplicate check is O(1) instead of O(n), avoiding O(n²) in large libraries.
                var existingPaths = new HashSet<string>(
                    results.Select(g => g.FolderPath), StringComparer.OrdinalIgnoreCase);

                foreach (var gameFolder in Directory.EnumerateDirectories(path))
                {
                    // Skip if a manifest scan already added this folder with a proper name
                    if (existingPaths.Contains(gameFolder))
                        continue;

                    string folderName = Path.GetFileName(gameFolder);

                    // When strictMode is true (acfNamesOnly for steamapps/common),
                    // skip any folder that has no ACF manifest. This filters Steam
                    // utility packages (Steamworks Common Redistributables, SteamVR,
                    // internal steam_settings sub-folders, etc.).
                    if (strictMode && !acfNames!.ContainsKey(folderName))
                        continue;

                    var exe = FindExecutable(gameFolder);
                    if (exe is null) continue;

                    // Priority: .gameos-title > ACF display name > raw folder name.
                    // The ACF name is used even when the ACF scan skipped the game due
                    // to StateFlags (e.g. "download queued"), so the user sees a proper
                    // display name instead of a cryptic installdir like "LHPCR".
                    string title = ReadGameOsTitle(gameFolder)
                                ?? (acfNames?.TryGetValue(folderName, out var acfName) == true
                                        ? acfName
                                        : null)
                                ?? folderName;

                    results.Add(new LocalGame
                    {
                        Title          = title,
                        ExecutablePath = exe.FullPath,
                        ExecutableType = exe.Type,
                        FolderPath     = gameFolder,
                        DriveRoot      = driveRoot,
                        Source         = source,
                    });
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            int added = results.Count - beforeCount;
            if (added > 0)
                DevLogService.LogLocalSteam(
                    $"[LocalSteam/{source}] Dir \"{path}\" — {added} game(s) added via folder scan");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // ── Steam ──────────────────────────────────────────────────────
            string steamApps = Path.Combine(driveRoot, "Program Files (x86)", "Steam", "steamapps");
            string steamCommon = Path.Combine(steamApps, "common");
            // Build ACF installdir→name map BEFORE running ACF scan so the folder-scan
            // fallback can use the proper display name for any game whose ACF has
            // StateFlags that don't include the "fully installed" bit (e.g. StateFlags=2
            // = download queued).  This map is built from ALL ACF files regardless of
            // StateFlags and is therefore a superset of what ScanSteamAcfManifests adds.
            var steamAcfNames = BuildAcfInstallDirNames(steamApps);
            // Run ACF manifest scan FIRST so games get their proper display names
            // (e.g. "LEGO® Harry Potter™ Collection" instead of raw folder name "LHPCR").
            // ScanDir runs afterwards as a fallback and skips folders already added.
            ScanSteamAcfManifests(steamApps, results);
            ScanDir(steamCommon, results, driveRoot, "Steam", steamAcfNames, acfNamesOnly: true);

            // Additional Steam library folders declared in libraryfolders.vdf
            string vdfPath = Path.Combine(steamApps, "libraryfolders.vdf");
            foreach (var libPath in ParseSteamLibraryFolders(vdfPath))
            {
                string libSteamApps = Path.Combine(libPath, "steamapps");
                string libCommon    = Path.Combine(libSteamApps, "common");
                var libAcfNames = BuildAcfInstallDirNames(libSteamApps);
                ScanSteamAcfManifests(libSteamApps, results);
                ScanDir(libCommon, results, libPath, "Steam", libAcfNames, acfNamesOnly: true);
            }

            // ── Epic Games — manifest-based discovery ──────────────────────
            // Epic stores a .item JSON manifest per installed game under
            // %ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\.  Read those
            // first so DisplayName is used as the title instead of folder name,
            // and any non-default install location is found automatically.
            string epicManifestsWin = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Epic", "EpicGamesLauncher", "Data", "Manifests");
            ScanEpicManifests(epicManifestsWin, results);
            // Also scan the default install folder for games without manifests
            ScanDir(Path.Combine(driveRoot, "Program Files", "Epic Games"),
                    results, driveRoot, "Epic");

            // ── GOG Galaxy ─────────────────────────────────────────────────
            ScanDir(Path.Combine(driveRoot,
                    "Program Files (x86)", "GOG Galaxy", "Games"),
                    results, driveRoot, "GOG");

            // ── EA / Origin ────────────────────────────────────────────────
            ScanDir(Path.Combine(driveRoot,
                    "Program Files (x86)", "Origin Games"),
                    results, driveRoot, "EA");
            ScanDir(Path.Combine(driveRoot, "Program Files", "EA Games"),
                    results, driveRoot, "EA");

            // ── Ubisoft Connect ────────────────────────────────────────────
            ScanDir(Path.Combine(driveRoot, "Program Files (x86)",
                    "Ubisoft", "Ubisoft Game Launcher", "games"),
                    results, driveRoot, "Ubisoft");

            // ── Xbox / Game Pass ───────────────────────────────────────────
            ScanDir(Path.Combine(driveRoot, "XboxGames"), results, driveRoot, "Xbox");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // Steam on macOS installs to ~/Library/Application Support/Steam/steamapps/common
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string macSteamApps = Path.Combine(home, "Library", "Application Support",
                    "Steam", "steamapps");
            var macAcfNames = BuildAcfInstallDirNames(macSteamApps);
            ScanSteamAcfManifests(macSteamApps, results);
            ScanDir(Path.Combine(macSteamApps, "common"), results, home, "Steam", macAcfNames, acfNamesOnly: true);

            // Epic Games Launcher on macOS — manifest-based discovery
            string epicManifestsMac = Path.Combine(home, "Library", "Application Support",
                    "Epic", "EpicGamesLauncher", "Data", "Manifests");
            ScanEpicManifests(epicManifestsMac, results);
        }
        else
        {
            // Linux — Steam (Flatpak and native paths)
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            string nativeSteamApps  = Path.Combine(home, ".steam", "steam", "steamapps");
            string localSteamApps   = Path.Combine(home, ".local", "share", "Steam", "steamapps");
            string flatpakSteamApps = Path.Combine(home, ".var", "app",
                "com.valvesoftware.Steam", "data", "Steam", "steamapps");

            // ACF manifests first (proper names), then ScanDir as fallback
            var nativeAcfNames  = BuildAcfInstallDirNames(nativeSteamApps);
            var localAcfNames   = BuildAcfInstallDirNames(localSteamApps);
            var flatpakAcfNames = BuildAcfInstallDirNames(flatpakSteamApps);
            ScanSteamAcfManifests(nativeSteamApps,  results);
            ScanSteamAcfManifests(localSteamApps,   results);
            ScanSteamAcfManifests(flatpakSteamApps, results);
            ScanDir(Path.Combine(nativeSteamApps,  "common"), results, home, "Steam", nativeAcfNames,  acfNamesOnly: true);
            ScanDir(Path.Combine(localSteamApps,   "common"), results, home, "Steam", localAcfNames,   acfNamesOnly: true);
            ScanDir(Path.Combine(flatpakSteamApps, "common"), results, home, "Steam", flatpakAcfNames, acfNamesOnly: true);

            // Additional Steam library folders from the VDF in the native location
            string vdfLinux = Path.Combine(home, ".local", "share",
                "Steam", "steamapps", "libraryfolders.vdf");
            foreach (var libPath in ParseSteamLibraryFolders(vdfLinux))
            {
                string libSteamApps = Path.Combine(libPath, "steamapps");
                var libAcfNames = BuildAcfInstallDirNames(libSteamApps);
                ScanSteamAcfManifests(libSteamApps, results);
                ScanDir(Path.Combine(libSteamApps, "common"), results, libPath, "Steam", libAcfNames, acfNamesOnly: true);
            }

            // Heroic Games Launcher (Epic/GOG on Linux)
            ScanDir(Path.Combine(home, "Games", "Heroic", "Installed"),
                    results, home, "Heroic");
        }
    }

    /// <summary>
    /// Parses a Steam <c>libraryfolders.vdf</c> file and returns the paths of
    /// all additional library root folders declared in it.  Returns an empty
    /// enumerable if the file does not exist or cannot be parsed.
    /// <para>
    /// VDF excerpt that this handles:
    /// <code>
    /// "libraryfolders"
    /// {
    ///     "1"  { "path"  "D:\\SteamLibrary" }
    ///     "2"  { "path"  "E:\\Games\\Steam" }
    /// }
    /// </code>
    /// </para>
    /// </summary>
    private static IEnumerable<string> ParseSteamLibraryFolders(string vdfPath)
    {
        if (!File.Exists(vdfPath)) return Array.Empty<string>();
        try
        {
            // Simple line-by-line scan — avoids a full VDF parser dependency.
            // Looks for lines of the form:  "path"  "D:\\SteamLibrary"
            var pathLineRegex = new Regex(
                @"^\s*""path""\s+""(?<p>[^""]+)""",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

            var folders = new List<string>();
            foreach (var line in File.ReadLines(vdfPath))
            {
                var m = pathLineRegex.Match(line);
                if (!m.Success) continue;

                // VDF uses \\ for backslash on Windows
                string folder = m.Groups["p"].Value.Replace(@"\\", @"\");
                if (Directory.Exists(folder))
                    folders.Add(folder);
            }
            return folders;
        }
        catch { /* malformed VDF — skip gracefully */ }
        return Array.Empty<string>();
    }

    /// <summary>
    /// Scans an Epic Games Launcher <c>Manifests</c> directory for installed games.
    /// Epic stores one JSON <c>.item</c> file per installed game at:
    /// <list type="bullet">
    ///   <item>Windows — <c>%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\</c></item>
    ///   <item>macOS   — <c>~/Library/Application Support/Epic/EpicGamesLauncher/Data/Manifests/</c></item>
    /// </list>
    /// Each file contains <c>InstallLocation</c> (absolute path), <c>DisplayName</c>,
    /// and <c>LaunchExecutable</c> (relative path to the main exe).
    /// </summary>
    internal static void ScanEpicManifests(string manifestsDir, List<LocalGame> results)
    {
        if (!Directory.Exists(manifestsDir)) return;
        try
        {
            foreach (var manifestFile in Directory.EnumerateFiles(manifestsDir, "*.item"))
            {
                try
                {
                    string json = File.ReadAllText(manifestFile);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    // Skip incomplete installs and non-game entries
                    if (root.TryGetProperty("bIsIncompleteInstall", out var incomplete) &&
                        incomplete.ValueKind == JsonValueKind.True) continue;

                    if (!root.TryGetProperty("InstallLocation", out var installLocProp)) continue;
                    string? installLocation = installLocProp.GetString();
                    if (string.IsNullOrEmpty(installLocation) ||
                        !Directory.Exists(installLocation)) continue;

                    // Prefer DisplayName; fall back to the install folder name
                    string displayName = "";
                    if (root.TryGetProperty("DisplayName", out var nameProp))
                        displayName = nameProp.GetString() ?? "";
                    if (string.IsNullOrEmpty(displayName))
                        displayName = Path.GetFileName(installLocation);

                    // Use the launcher-specified executable when available
                    string executablePath = "";
                    string exeType = "exe";
                    if (root.TryGetProperty("LaunchExecutable", out var exeProp))
                    {
                        string? relExe = exeProp.GetString();
                        if (!string.IsNullOrEmpty(relExe))
                        {
                            string fullExe = Path.Combine(installLocation, relExe);
                            if (File.Exists(fullExe))
                            {
                                executablePath = fullExe;
                                string ext = Path.GetExtension(fullExe).ToLowerInvariant();
                                exeType = ext == ".exe" ? "exe" : ext == ".app" ? "app" : "elf";
                            }
                        }
                    }

                    // Fall back to auto-detection if the declared exe was not found
                    if (string.IsNullOrEmpty(executablePath))
                    {
                        var exe = FindExecutable(installLocation);
                        if (exe is null) continue;
                        executablePath = exe.FullPath;
                        exeType = exe.Type;
                    }

                    string driveRoot = Path.GetPathRoot(installLocation) ?? "/";

                    // Skip if the same folder was already added by the directory scan
                    if (results.Any(g => string.Equals(
                            g.FolderPath, installLocation,
                            StringComparison.OrdinalIgnoreCase)))
                        continue;

                    results.Add(new LocalGame
                    {
                        Title          = displayName,
                        ExecutablePath = executablePath,
                        ExecutableType = exeType,
                        FolderPath     = installLocation,
                        DriveRoot      = driveRoot,
                        Source         = "Epic",
                    });
                }
                catch { /* skip malformed manifest */ }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    /// <summary>
    /// Reads Steam <c>appmanifest_*.acf</c> files from <paramref name="steamAppsDir"/> and
    /// adds fully-installed games to <paramref name="results"/>.
    /// <para>
    /// ACF files reliably provide the canonical display name and install folder for every
    /// Steam game, including games whose main executable is not at the top level of the
    /// install folder (e.g. games that launch through a sub-folder launcher or via a
    /// Steam bootstrapper).
    /// </para>
    /// <para>
    /// Only entries with <c>StateFlags == 4</c> (fully installed) are included.
    /// Games already added by the common-folder directory scan are skipped.
    /// </para>
    /// </summary>
    internal static void ScanSteamAcfManifests(string steamAppsDir, List<LocalGame> results)
    {
        if (!Directory.Exists(steamAppsDir)) return;

        string commonDir = Path.Combine(steamAppsDir, "common");
        if (!Directory.Exists(commonDir)) return;

        DevLogService.LogLocalSteam($"[LocalSteam/ACF] Scanning \"{steamAppsDir}\"");
        int beforeCount = results.Count;

        try
        {
            foreach (var acfFile in Directory.EnumerateFiles(steamAppsDir, "appmanifest_*.acf"))
            {
                try
                {
                    string content = File.ReadAllText(acfFile);

                    string? appId      = ExtractAcfValue(content, "appid");
                    string? name       = ExtractAcfValue(content, "name");
                    string? installDir = ExtractAcfValue(content, "installdir");
                    string? stateFlags = ExtractAcfValue(content, "StateFlags");

                    // Only games where the "Fully Installed" bit (4) is set in the StateFlags bitmask.
                    // StateFlags is a bitmask: bit 2 (value 4) = fully installed.
                    // Accept any value with that bit set (e.g. "4", "6" = installed+update required,
                    // "516" = installed+update paused) so games are not hidden when Steam flags them
                    // for an update.
                    if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(installDir)) continue;
                    if (!string.IsNullOrEmpty(stateFlags) &&
                        (!int.TryParse(stateFlags, out int stateInt) || (stateInt & 4) == 0)) continue;

                    string fullPath = Path.Combine(commonDir, installDir);
                    if (!Directory.Exists(fullPath)) continue;

                    // Skip if the same install folder was already added by the directory scan
                    if (results.Any(g => string.Equals(
                            g.FolderPath, fullPath,
                            StringComparison.OrdinalIgnoreCase)))
                        continue;

                    // Search for an executable: top-level first, then up to two levels deep
                    var exe = FindExecutable(fullPath)
                           ?? FindExecutableDeep(fullPath, maxDepth: 2);
                    // If still no exe, record the game with the folder as a placeholder so
                    // it appears in the library and the user can set the path in settings.
                    string driveRoot = Path.GetPathRoot(fullPath) ?? "/";
                    results.Add(new LocalGame
                    {
                        Title          = name,
                        ExecutablePath = exe?.FullPath ?? fullPath,
                        ExecutableType = exe?.Type     ?? "folder",
                        FolderPath     = fullPath,
                        DriveRoot      = driveRoot,
                        Source         = "Steam",
                        SteamAppId     = int.TryParse(appId, out int parsedId) ? parsedId : 0,
                    });
                }
                catch { /* skip malformed ACF */ }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        int added = results.Count - beforeCount;
        DevLogService.LogLocalSteam(
            $"[LocalSteam/ACF] \"{steamAppsDir}\" — {added} game(s) added from ACF manifests");
    }

    /// <summary>
    /// Builds an <c>installdir → display name</c> lookup from <em>all</em>
    /// <c>appmanifest_*.acf</c> files in <paramref name="steamAppsDir"/>,
    /// regardless of <c>StateFlags</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ScanSteamAcfManifests"/> only adds games whose
    /// <c>StateFlags</c> bitmask has the fully-installed flag (value 4) set.
    /// A game can be on disk and fully playable yet have <c>StateFlags</c> that
    /// don't satisfy this check (e.g. <c>2</c> = "download queued" if Steam
    /// re-queued an update after the game was already playable).  In that case
    /// the directory scanner falls back to the raw installdir name (e.g.
    /// "LHPCR").  This method supplies the ACF display name so the fallback
    /// path can still show a human-readable title instead of the cryptic
    /// installdir.
    /// </para>
    /// <para>
    /// The returned dictionary uses <see cref="StringComparer.OrdinalIgnoreCase"/>
    /// so it matches regardless of capitalisation differences between the ACF
    /// value and the actual directory name as returned by the file system.
    /// </para>
    /// </remarks>
    internal static Dictionary<string, string> BuildAcfInstallDirNames(string steamAppsDir)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(steamAppsDir)) return map;
        try
        {
            foreach (var acfFile in Directory.EnumerateFiles(steamAppsDir, "appmanifest_*.acf"))
            {
                try
                {
                    string content    = File.ReadAllText(acfFile);
                    string? name       = ExtractAcfValue(content, "name");
                    string? installDir = ExtractAcfValue(content, "installdir");
                    if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(installDir)
                            && !map.ContainsKey(installDir))
                        map[installDir] = name;
                }
                catch { /* skip malformed ACF */ }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
        return map;
    }

    /// <summary>
    /// Extracts a single string value from a VDF/ACF file given its key.
    /// Handles the <c>"key"  "value"</c> format used by Steam.
    /// Uses a general field-capture regex and filters by key at match time to avoid
    /// allocating a new <see cref="Regex"/> per field on every call.
    /// </summary>
    /// <remarks>
    /// <c>_acfValueRegex</c> captures all key/value pairs in one scan; <c>ExtractAcfValue</c>
    /// then iterates the matches to find the requested key (case-insensitive).
    /// </remarks>
    private static readonly Regex _acfValueRegex =
        new(@"""(\w+)""\s+""([^""]*)""", RegexOptions.Compiled);

    private static string? ExtractAcfValue(string content, string key)
    {
        foreach (Match m in _acfValueRegex.Matches(content))
        {
            if (string.Equals(m.Groups[1].Value, key, StringComparison.OrdinalIgnoreCase))
                return m.Groups[2].Value;
        }
        return null;
    }

    /// <summary>
    /// Like <see cref="FindExecutable"/> but searches up to <paramref name="maxDepth"/>
    /// levels of sub-directories before giving up.  Used as a fallback for Steam games
    /// whose main executable is nested one level below the install folder.
    /// </summary>
    private static ExeInfo? FindExecutableDeep(string folder, int maxDepth)
    {
        if (maxDepth <= 0) return null;
        try
        {
            foreach (var subDir in Directory.EnumerateDirectories(folder))
            {
                var exe = FindExecutable(subDir);
                if (exe != null) return exe;
                // Recurse one more level if depth allows
                if (maxDepth > 1)
                {
                    exe = FindExecutableDeep(subDir, maxDepth - 1);
                    if (exe != null) return exe;
                }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
        return null;
    }
    ///                   or Roms/{PlatformName}/Games/{GameName}/{RomFile}
    ///                   or Roms/{PlatformName}/Games/{TitleID}/  (PS3/PS4/Switch folder-based)
    /// </summary>
    private static void ScanRomsDir(string driveRoot, List<LocalRom> results)
    {
        string romsPath = Path.Combine(driveRoot, "Roms");
        if (!Directory.Exists(romsPath)) return;

        try
        {
            foreach (var platformDir in Directory.EnumerateDirectories(romsPath))
            {
                string platform = NormalizePlatform(Path.GetFileName(platformDir));
                string gamesDir = Path.Combine(platformDir, "Games");
                if (!Directory.Exists(gamesDir)) continue;

                // ── Folder-based games (e.g. PS3/PS4 $TitleID folders that contain game data) ──
                // Scan top-level sub-folders that look like a TitleID and contain no ROM file at their root.
                try
                {
                    foreach (var subDir in Directory.EnumerateDirectories(gamesDir))
                    {
                        string folderName = Path.GetFileName(subDir);
                        string? titleId   = ExtractTitleId(folderName, platform);

                        // Only treat as a "folder game" when the entire sub-folder is a TitleID
                        // AND the folder itself has no ROM file at its immediate level.
                        // (Folders that happen to contain a ROM will be picked up below.)
                        if (titleId == null) continue;

                        bool hasRomInRoot = Directory
                            .EnumerateFiles(subDir, "*", SearchOption.TopDirectoryOnly)
                            .Any(f => IsRomFile(Path.GetExtension(f).ToLowerInvariant()));
                        if (hasRomInRoot) continue; // will be scanned as a normal ROM file below

                        long size = 0;
                        try
                        {
                            size = new DirectoryInfo(subDir)
                                .EnumerateFiles("*", SearchOption.AllDirectories)
                                .Sum(f => { try { return f.Length; } catch { return 0L; } });
                        }
                        catch { }

                        // Use the folder name as a placeholder title; MainViewModel's enrichment
                        // will replace it with the real game name via TitleID database lookup.
                        results.Add(new LocalRom
                        {
                            Title    = folderName,
                            TitleId  = titleId,
                            Platform = platform,
                            FilePath = subDir,
                            FileType = "folder",
                            SizeBytes= size,
                            Regions  = new List<string>(),
                        });
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }

                try
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(
                                 gamesDir, "*", SearchOption.AllDirectories))
                    {
                        if (Directory.Exists(entry)) continue; // skip sub-folders
                        string ext = Path.GetExtension(entry).ToLowerInvariant();
                        if (!IsRomFile(ext)) continue;

                        long size = 0;
                        try { size = new FileInfo(entry).Length; } catch { }

                        // Prefer the parent folder name as the title when the file
                        // is inside a named sub-directory; fall back to the filename.
                        string parent   = Path.GetDirectoryName(entry) ?? gamesDir;
                        string rawTitle = string.Equals(
                                              Path.GetFullPath(parent),
                                              Path.GetFullPath(gamesDir),
                                              StringComparison.OrdinalIgnoreCase)
                            ? Path.GetFileNameWithoutExtension(entry)
                            : Path.GetFileName(parent);

                        // When the immediate parent folder IS a TitleID (e.g. CUSA00207 inside
                        // "Blood Borne/CUSA00207/"), use the grandparent folder as the game title
                        // so the real game name is shown instead of the raw TitleID.
                        string? parentTitleId = ExtractTitleId(rawTitle, platform);
                        if (parentTitleId != null)
                        {
                            string grandParent = Path.GetDirectoryName(parent) ?? "";
                            string grandParentName = Path.GetFileName(grandParent);
                            if (!string.IsNullOrWhiteSpace(grandParentName) &&
                                !string.Equals(Path.GetFullPath(grandParent),
                                               Path.GetFullPath(gamesDir),
                                               StringComparison.OrdinalIgnoreCase))
                            {
                                // Use grandparent (game name folder) as the display title
                                rawTitle = grandParentName;
                            }
                        }

                        // Strip region tags (e.g. "(Europe)", "(USA)") from the title
                        // and collect them as separate metadata, just like platform tags.
                        var (cleanTitle, regions) = ParseRomTitle(rawTitle);

                        // Detect if the raw title (pre-region-strip) looks like a TitleID
                        string? titleId = parentTitleId
                                       ?? ExtractTitleId(rawTitle, platform)
                                       ?? ExtractTitleId(cleanTitle, platform);

                        results.Add(new LocalRom
                        {
                            Title    = string.IsNullOrWhiteSpace(cleanTitle) ? rawTitle : cleanTitle,
                            TitleId  = titleId,
                            Platform = platform,
                            FilePath = entry,
                            FileType = ext.TrimStart('.'),
                            SizeBytes= size,
                            Regions  = regions,
                        });
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    // ── TitleID detection ──────────────────────────────────────────────────

    // PS3 / PS4 / PSP TitleID: four uppercase letters + five digits (e.g. BLUS30305, CUSA00572)
    private static readonly Regex _ps3TitleIdRegex =
        new(@"^[A-Z]{4}\d{5}$", RegexOptions.Compiled);

    // Switch TitleID: 16 hex digits, optionally wrapped in brackets (e.g. 0100ADC022586000 or [0100ADC022586000])
    private static readonly Regex _switchTitleIdRegex =
        new(@"^\[?([0-9A-Fa-f]{16})\]?$", RegexOptions.Compiled);

    // Switch ROM filenames that embed a TitleID: "Game Title [0100ADC022586000][v0]"
    private static readonly Regex _switchEmbeddedTitleIdRegex =
        new(@"\[([0-9A-Fa-f]{16})\]", RegexOptions.Compiled);

    /// <summary>
    /// Returns the TitleID if <paramref name="name"/> is (or contains) a platform-specific
    /// TitleID, or <see langword="null"/> if no TitleID is detected.
    /// </summary>
    internal static string? ExtractTitleId(string name, string platform)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        string trimmed = name.Trim();

        // PS3 / PS4: standalone TitleID folder/file name
        if ((string.Equals(platform, "PS3",  StringComparison.OrdinalIgnoreCase) ||
             string.Equals(platform, "PS4",  StringComparison.OrdinalIgnoreCase) ||
             string.Equals(platform, "PSP",  StringComparison.OrdinalIgnoreCase) ||
             string.Equals(platform, "PS Vita", StringComparison.OrdinalIgnoreCase)) &&
            _ps3TitleIdRegex.IsMatch(trimmed))
        {
            return trimmed.ToUpperInvariant();
        }

        // Switch: standalone TitleID (with or without brackets)
        if (string.Equals(platform, "Switch", StringComparison.OrdinalIgnoreCase))
        {
            var m = _switchTitleIdRegex.Match(trimmed);
            if (m.Success) return m.Groups[1].Value.ToUpperInvariant();

            // Also extract embedded TitleID from "Game Title [0100ADC022586000][v0]" style names
            var em = _switchEmbeddedTitleIdRegex.Match(trimmed);
            if (em.Success) return em.Groups[1].Value.ToUpperInvariant();
        }

        return null;
    }

    // ── ROM extension list ─────────────────────────────────────────────────

    private static readonly HashSet<string> _romExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Generic archive / disc formats
        ".zip", ".7z", ".rar",
        // Sony / Microsoft
        ".iso", ".bin", ".cue", ".xex", ".xiso",
        // Xbox 360 archive format
        ".zar",
        // Nintendo Switch
        ".nsp", ".xci", ".nca", ".nsz", ".xcz",
        // Nintendo (other)
        ".gb", ".gbc", ".gba", ".nes", ".snes", ".ds", ".nds", ".3ds", ".nro",
        // Other
        ".elf", ".img", ".chd", ".pbp", ".pkg",
    };

    private static bool IsRomFile(string ext) => _romExtensions.Contains(ext);

    // ── Repack marker stripping ────────────────────────────────────────────

    // Matches "[Repack]", "[FitGirl Repack]", "[DODI Repack]", etc.
    private static readonly Regex _repackMarkerRegex =
        new(@"\[[\w\s]*[Rr]epack[\w\s]*\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Removes common repack annotation patterns from a folder/file name so
    /// the clean game title can be matched against the Games.Database.
    /// </summary>
    internal static string StripRepackMarkers(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return _repackMarkerRegex.Replace(name, "").Trim();
    }

    /// <summary>Scan <paramref name="driveRoot"/>/Repacks recursively.</summary>
    private static void ScanRepacksDir(string driveRoot, List<LocalRepack> results)
    {
        string repacksPath = Path.Combine(driveRoot, "Repacks");
        if (!Directory.Exists(repacksPath)) return;

        try
        {
            // Top-level archive files
            foreach (var file in Directory.EnumerateFiles(repacksPath, "*", SearchOption.TopDirectoryOnly))
            {
                if (IsRepackArchive(file))
                    results.Add(MakeRepack(file, false));
            }

            // Sub-folders (e.g. Repacks/$RepackFolder)
            foreach (var sub in Directory.EnumerateDirectories(repacksPath))
            {
                // Detect an "Update" sub-directory within this repack folder.
                string? updatePath = FindUpdateDir(sub);

                bool foundAny = false;
                try
                {
                    foreach (var file in Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories))
                    {
                        // Skip files inside the Update sub-directory; they belong to the update,
                        // not to the main repack archive.
                        if (updatePath != null &&
                            file.StartsWith(updatePath, StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (IsRepackArchive(file))
                        {
                            var repack = MakeRepack(file, true);
                            repack.HasUpdate  = updatePath != null;
                            repack.UpdatePath = updatePath;
                            results.Add(repack);
                            foundAny = true;
                        }
                    }
                    // If the folder itself is a repack folder with no archive, add the folder
                    if (!foundAny)
                    {
                        // Check for an installer (Setup.exe) within the folder
                        string? setupExe = FindSetupExe(sub);
                        results.Add(new LocalRepack
                        {
                            Title     = _fileSizeAnnotationRegex.Replace(StripRepackMarkers(Path.GetFileName(sub)), "").Trim(),
                            FilePath  = setupExe ?? sub,
                            FileType  = setupExe != null ? "setup" : "folder",
                            SizeBytes = GetDirectorySize(sub),
                            HasUpdate = updatePath != null,
                            UpdatePath= updatePath,
                        });
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private sealed record ExeInfo(string FullPath, string Type);

    /// <summary>
    /// Reads the <c>.gameos-title</c> metadata file written by the launcher when a
    /// repack is installed to a folder.  Returns the stored title string, or
    /// <see langword="null"/> when the file does not exist or cannot be read.
    /// </summary>
    internal static string? ReadGameOsTitle(string folder)
    {
        try
        {
            string path = Path.Combine(folder, ".gameos-title");
            if (!File.Exists(path)) return null;
            string title = File.ReadAllText(path, System.Text.Encoding.UTF8).Trim();
            return string.IsNullOrEmpty(title) ? null : title;
        }
        catch { return null; }
    }

    /// <summary>
    /// Looks for a setup/install executable inside a repack folder.
    /// Returns the full path of the first Setup*.exe found (case-insensitive),
    /// or null if none is found.
    /// </summary>
    private static string? FindSetupExe(string folder)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;
        try
        {
            return Directory.EnumerateFiles(folder, "setup*.exe", SearchOption.AllDirectories)
                            .FirstOrDefault()
                ?? Directory.EnumerateFiles(folder, "install*.exe", SearchOption.AllDirectories)
                            .FirstOrDefault();
        }
        catch { return null; }
    }

    private static ExeInfo? FindExecutable(string folder)
    {
        try
        {
            // .exe (Windows) — preferred over batch scripts
            var exe = Directory.EnumerateFiles(folder, "*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (exe != null) return new ExeInfo(exe, "exe");

            // .bat (Windows batch launcher — common in repacks and mod clients)
            var bat = Directory.EnumerateFiles(folder, "*.bat", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (bat != null) return new ExeInfo(bat, "bat");

            // .app bundle (macOS)
            var app = Directory.EnumerateDirectories(folder, "*.app", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (app != null) return new ExeInfo(app, "app");

            // ELF binary (Linux) — a file without extension that is marked executable
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ||
                RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly))
                {
                    if (Path.GetExtension(f) != "") continue;
                    try
                    {
                        var info = new FileInfo(f);
                        if (info.Exists && IsExecutable(f))
                            return new ExeInfo(f, "elf");
                    }
                    catch { }
                }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
        return null;
    }

    private static bool IsExecutable(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        try
        {
            // Check ELF magic number
            using var fs = File.OpenRead(path);
            var header = new byte[4];
            int read = fs.Read(header, 0, 4);
            if (read == 4 && header[0] == 0x7f && header[1] == (byte)'E' &&
                header[2] == (byte)'L' && header[3] == (byte)'F')
                return true;
        }
        catch { }

        try
        {
            // Check Unix execute permission bits (File.GetUnixFileMode available since .NET 7;
            // this project targets .NET 8 so this is always supported)
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch { }

        return false;
    }

    private static bool IsRepackArchive(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".zip" or ".rar" or ".7z" or ".iso" or ".tar" or ".gz";
    }

    private static LocalRepack MakeRepack(string filePath, bool fromSubfolder)
    {
        long size = 0;
        try { size = new FileInfo(filePath).Length; } catch { }

        // For top-level archive files, the raw title is the filename without extension.
        // Apply NormalizeArchiveTitle to convert "A-Way-Out-SteamRIP" → "A Way Out".
        // For sub-folder repacks, keep the parent folder name as-is (it's usually already clean).
        string rawTitle = fromSubfolder
            ? $"{Path.GetFileName(Path.GetDirectoryName(filePath))} / {Path.GetFileName(filePath)}"
            : NormalizeArchiveTitle(Path.GetFileNameWithoutExtension(filePath));

        return new LocalRepack
        {
            Title    = StripRepackMarkers(rawTitle),
            FilePath = filePath,
            FileType = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant(),
            SizeBytes= size,
        };
    }

    private static long GetDirectorySize(string path)
    {
        try
        {
            return new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
        catch { return 0; }
    }

    // ── ROM region tag parsing ─────────────────────────────────────────────

    // Token alternation used inside the region regex — listed most-specific first.
    private const string _regionToken =
        @"Europe|USA|Japan|Jap|World|Korea|Australia|France|Germany|Spain|Italy|China|Brazil|" +
        @"Rev\s*[\w.]+|v\d[\d.]*|Beta|Proto|Demo|Sample|Unl|Asia|" +
        @"En|Ja|Jp|De|Fr|Es|It|Nl|Pt|Sv|No|Da|Fi|Ko|Zh|Ru|Pl|Ar|Tr|Cs|Hu|He|Ro|Sk|Uk";

    // Matches parenthetical ROM tags that contain one or more region/language codes.
    // Handles both single tags  "(USA)"  and comma-separated lists  "(En,Ja,Fr,De,Es,It)".
    // The outer group captures the full comma-separated content for later splitting.
    private static readonly Regex _romRegionRegex = new(
        @"\s*\((" +
        @"(?:" + _regionToken + @")" +
        @"(?:[,\s]+(?:" + _regionToken + @"))*" +
        @")\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Strips common ROM region/language tags from a raw ROM title and returns
    /// the clean title plus the list of extracted region strings.
    /// Handles both single tags "(USA)" and comma-separated lists "(En,Ja,Fr,De,Es,It)".
    /// Also strips trademark/copyright/registered symbols (™, ®, ©) so that a filename
    /// such as "Super Mario Odyssey™.nca" yields the display title "Super Mario Odyssey".
    /// </summary>
    internal static (string CleanTitle, List<string> Regions) ParseRomTitle(string rawTitle)
    {
        var regions = new List<string>();
        var matches = _romRegionRegex.Matches(rawTitle);
        foreach (Match m in matches)
        {
            // Group 1 may contain comma-separated or space-separated codes
            foreach (var code in m.Groups[1].Value.Split(
                new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = code.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                    regions.Add(trimmed);
            }
        }
        string clean = _romRegionRegex.Replace(rawTitle, "").Trim();
        // Strip trademark, registered, and copyright symbols so that display titles
        // match the site catalog (e.g. "Super Mario Odyssey™" → "Super Mario Odyssey").
        clean = PlatformHelper.StripSpecialSymbols(clean);
        return (clean, regions);
    }

    // ── Archive filename normalization ─────────────────────────────────────

    // Matches common scene/RIP group suffixes at the end of archive filename stems.
    // Requires at least one whitespace character before the suffix so that embedded
    // words such as "FakeRepack" are not incorrectly stripped ("Fake").
    // This is applied AFTER hyphens and underscores have already been replaced with spaces,
    // so "A-Way-Out-SteamRIP" becomes "A Way Out SteamRIP" before this runs.
    // Note: DARKSIDERS listed once; case-insensitive matching handles all case variants.
    // SteamRIP.com / Steam RIP.com variants are listed before the plain SteamRIP entry.
    private static readonly Regex _archiveSuffixRegex = new(
        @"\s+(SteamRIP\.com|Steam\.RIP\.com|Steam\s+RIP|SteamRIP|Steam\.RIP|GOG|TENOKE|EMPRESS|CODEX|PLAZA|FLT|SKIDROW|" +
        @"RELOADED|PROPHET|RAZOR1911|RAZOR|CPY|HOODLUM|DARKSIDERS|TiNYiSO|P2P|" +
        @"DODI|FitGirl|ElAmigos|KaOs|Goldberg|RIP|RePack)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches trailing ".com", ".net", ".org", ".io" domain suffixes left after separator replacement
    // (e.g. "SteamRIP.com" → the domain part was not handled by the suffix regex above).
    private static readonly Regex _domainSuffixRegex = new(
        @"\.(com|net|org|io)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Matches parenthetical file-size annotations such as "(4.67 GB)", "(12 GB)", "(850 MB)".
    // These appear in folder/file names created by some repacking tools and should be stripped
    // so the clean game title can be matched against the Games.Database.
    private static readonly Regex _fileSizeAnnotationRegex = new(
        @"\s*\(\d+(?:\.\d+)?\s*(?:GB|MB|KB|TB)\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Normalises an archive filename stem into a human-readable game title.
    /// <list type="bullet">
    ///   <item>Replaces hyphens and underscores with spaces ("A-Way-Out" → "A Way Out").</item>
    ///   <item>Strips trailing domain suffixes (.com/.net/.org/.io) left by site-tagged filenames.</item>
    ///   <item>Strips common scene/RIP group suffixes ("A Way Out SteamRIP" → "A Way Out").</item>
    ///   <item>Strips parenthetical file-size annotations ("Assassin's Creed (4.67 GB)" → "Assassin's Creed").</item>
    ///   <item>Collapses repeated spaces.</item>
    /// </list>
    /// </summary>
    internal static string NormalizeArchiveTitle(string stem)
    {
        if (string.IsNullOrEmpty(stem)) return stem;
        // Replace separators with spaces
        string result = stem.Replace('-', ' ').Replace('_', ' ');
        // Strip trailing .com / .net / .org domain suffixes before the scene-group regex runs
        result = _domainSuffixRegex.Replace(result, "").TrimEnd();
        // Strip trailing scene/RIP markers
        result = _archiveSuffixRegex.Replace(result, "").TrimEnd();
        // Strip parenthetical file-size annotations (e.g. "(4.67 GB)", "(12 GB)")
        result = _fileSizeAnnotationRegex.Replace(result, "").TrimEnd();
        // Collapse multiple spaces
        result = Regex.Replace(result, @"\s{2,}", " ").Trim();
        return result;
    }

    // ── Game title normalisation (mirrors MainViewModel.NormalizeGameTitle) ──

    // Compiled once — replaces the first " - " with ": " for subtitle resolution.
    private static readonly Regex _titleNormRegex =
        new(@"^(.+?) - (.+)$", RegexOptions.Compiled);

    /// <summary>
    /// Converts a Windows-safe folder/file name to its canonical game title.
    /// "Call of Duty - Black Ops II" → "Call of Duty: Black Ops II"
    /// </summary>
    internal static string NormalizeGameTitle(string title)
    {
        if (string.IsNullOrEmpty(title)) return title;
        return _titleNormRegex.Replace(title, "$1: $2");
    }

    /// <summary>
    /// Builds a <see cref="HashSet{T}"/> of fuzzy title variants from <paramref name="titles"/>
    /// for efficient O(1) deduplication.  Each title is stored in four forms:
    /// <list type="bullet">
    ///   <item>Raw (as-is)</item>
    ///   <item>Subtitle-normalized (" - " → ": ")</item>
    ///   <item>Symbol-stripped (™/®/© removed)</item>
    ///   <item>Normalized then symbol-stripped</item>
    /// </list>
    /// This allows matching across common name variants, e.g.
    /// "LEGO® Harry Potter™ Collection" ↔ "LEGO Harry Potter Collection",
    /// "Call of Duty - Ghosts" ↔ "Call of Duty: Ghosts".
    /// </summary>
    internal static HashSet<string> BuildFuzzyTitleSet(IEnumerable<string> titles)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in titles)
        {
            set.Add(t);
            set.Add(NormalizeGameTitle(t));
            set.Add(PlatformHelper.StripSpecialSymbols(t));
            set.Add(PlatformHelper.StripSpecialSymbols(NormalizeGameTitle(t)));
        }
        return set;
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="repackTitle"/>, after stripping
    /// repack markers and normalizing, matches any entry in <paramref name="titleSet"/>.
    /// Use together with <see cref="BuildFuzzyTitleSet"/> for efficient deduplication.
    /// </summary>
    internal static bool RepackMatchesInstalledTitle(string repackTitle,
                                                      HashSet<string> titleSet)
    {
        string stripped      = StripRepackMarkers(repackTitle).Trim();
        string normalized    = NormalizeGameTitle(stripped);
        string symbolStripped = PlatformHelper.StripSpecialSymbols(normalized);
        return titleSet.Contains(repackTitle)   ||
               titleSet.Contains(stripped)      ||
               titleSet.Contains(normalized)    ||
               titleSet.Contains(symbolStripped);
    }

    /// <summary>
    /// Maps verbose RetroArch/Libretro-style platform folder names to the canonical
    /// Games.Database platform identifiers used in URL paths and the C# model.
    /// <para>
    /// Examples: "Microsoft - Xbox 360" → "Xbox 360", "Nintendo - Switch" → "Switch",
    /// "Sony - PlayStation 3" → "PS3".
    /// </para>
    /// If the name is not a known verbose alias the original value is returned unchanged,
    /// so canonical names like "Xbox 360" and "Switch" pass through unmodified.
    /// </summary>
    internal static string NormalizePlatform(string platform)
        => Models.PlatformHelper.NormalizePlatform(platform);

    /// <summary>
    /// Returns the path to an "Update" sub-directory inside a repack folder,
    /// or <see langword="null"/> if none is found.
    /// Matches any sub-directory whose name starts with "Update" (case-insensitive).
    /// </summary>
    private static string? FindUpdateDir(string repackFolder)
    {
        try
        {
            return Directory
                .EnumerateDirectories(repackFolder, "Update*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // File-system watchers
    // ─────────────────────────────────────────────────────────────────────────

    private void StartWatchers()
    {
        DisposeWatchers();

        foreach (var driveRoot in GetDriveRoots())
        {
            TryWatch(Path.Combine(driveRoot, "Games"));
            TryWatch(Path.Combine(driveRoot, "Repacks"));
            TryWatch(Path.Combine(driveRoot, "Roms"));

            // Watch storefront installation directories so newly installed games
            // are detected without requiring a manual rescan.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string steamApps = Path.Combine(driveRoot, "Program Files (x86)", "Steam", "steamapps");
                TryWatch(Path.Combine(steamApps, "common"));
                // Also watch additional Steam library folders declared in libraryfolders.vdf
                // so games installed to non-default Steam libraries are detected instantly.
                string vdfPath = Path.Combine(steamApps, "libraryfolders.vdf");
                foreach (var libPath in ParseSteamLibraryFolders(vdfPath))
                    TryWatch(Path.Combine(libPath, "steamapps", "common"));

                TryWatch(Path.Combine(driveRoot, "Program Files", "Epic Games"));
                TryWatch(Path.Combine(driveRoot, "Program Files (x86)", "GOG Galaxy", "Games"));
                TryWatch(Path.Combine(driveRoot, "Program Files (x86)", "Origin Games"));
                TryWatch(Path.Combine(driveRoot, "Program Files", "EA Games"));
                TryWatch(Path.Combine(driveRoot, "Program Files (x86)", "Ubisoft", "Ubisoft Game Launcher", "games"));
                TryWatch(Path.Combine(driveRoot, "XboxGames"));
            }
        }
    }

    private void TryWatch(string dir)
    {
        if (!Directory.Exists(dir)) return;
        try
        {
            var w = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = true,
                NotifyFilter          = NotifyFilters.FileName
                                      | NotifyFilters.DirectoryName,
                EnableRaisingEvents   = true,
            };
            w.Created += OnFileSystemChanged;
            w.Deleted += OnFileSystemChanged;
            w.Renamed += OnFileSystemChanged;
            _watchers.Add(w);
        }
        catch { /* Directory not accessible or not supported */ }
    }

    private void OnFileSystemChanged(object sender, FileSystemEventArgs e)
    {
        // Cancel any pending debounced scan and start a fresh one
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _debounceCts = cts;
        _ = Task.Delay(500, cts.Token).ContinueWith(async t =>
        {
            if (t.IsCanceled) return;
            try
            {
                await ScanAllDrivesAsync(CancellationToken.None);
                // Refresh watchers so any new subdirectories are also monitored.
                StartWatchers();
            }
            catch { }
        }, TaskScheduler.Default);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Cache
    // ─────────────────────────────────────────────────────────────────────────

    private bool TryLoadCache()
    {
        try
        {
            if (!File.Exists(GameCache) && !File.Exists(RepackCache))
                return false;

            if (File.Exists(GameCache))
            {
                var g = JsonSerializer.Deserialize<List<LocalGame>>(File.ReadAllText(GameCache));
                if (g != null) { _games.Clear(); _games.AddRange(g); }
            }
            if (File.Exists(RepackCache))
            {
                var r = JsonSerializer.Deserialize<List<LocalRepack>>(File.ReadAllText(RepackCache));
                if (r != null) { _repacks.Clear(); _repacks.AddRange(r); }
            }
            if (File.Exists(RomCache))
            {
                var rom = JsonSerializer.Deserialize<List<LocalRom>>(File.ReadAllText(RomCache));
                if (rom != null) { _roms.Clear(); _roms.AddRange(rom); }
            }
            return _games.Count > 0 || _repacks.Count > 0 || _roms.Count > 0;
        }
        catch { return false; }
    }

    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(GameCache,   JsonSerializer.Serialize(_games,   opts));
            File.WriteAllText(RepackCache, JsonSerializer.Serialize(_repacks, opts));
            File.WriteAllText(RomCache,    JsonSerializer.Serialize(_roms,    opts));
        }
        catch { }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IDisposable
    // ─────────────────────────────────────────────────────────────────────────

    private void DisposeWatchers()
    {
        foreach (var w in _watchers) { try { w.Dispose(); } catch { } }
        _watchers.Clear();
    }

    public void Dispose()
    {
        DisposeWatchers();
        _lifetimeCts.Cancel();
        // Give the periodic task a brief window to observe the cancellation and exit.
        // We use Wait(timeout) rather than GetAwaiter().GetResult() to avoid an
        // indefinite block; PeriodicRescanAsync uses ConfigureAwait(false) so it is
        // not tied to the UI thread and should exit promptly once the token is cancelled.
        try { _periodicTask?.Wait(TimeSpan.FromMilliseconds(200)); } catch { }
        _lifetimeCts.Dispose();
        _debounceCts?.Cancel();
        _debounceCts?.Dispose();
        _lock.Dispose();
    }
}
