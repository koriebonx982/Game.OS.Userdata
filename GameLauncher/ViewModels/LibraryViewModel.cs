using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher;
using GameLauncher.Models;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

public partial class LibraryViewModel : ViewModelBase
{
    private List<Game> _allGames = new();
    private List<LocalGame>   _allLocalGames  = new();
    private List<LocalRepack> _allRepacks     = new();
    private List<LocalRom>    _allRoms        = new();

    // ── Unified "My Games" list (LocalGames + Repacks + ROMs) ─────────────
    private List<LocalGameCardVm> _allMyGames = new();

    [ObservableProperty] private string _filterPlatform = "All";
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private int    _totalGames;
    /// <summary>
    /// Install-status filter: "All" (default), "Installed" (only locally-installed games),
    /// or "Not Installed" (only cloud/Steam games without a local copy).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstallFilterAll))]
    [NotifyPropertyChangedFor(nameof(IsInstallFilterInstalled))]
    [NotifyPropertyChangedFor(nameof(IsInstallFilterNotInstalled))]
    private string _filterInstallStatus = "All";

    public bool IsInstallFilterAll          => FilterInstallStatus == "All";
    public bool IsInstallFilterInstalled    => FilterInstallStatus == "Installed";
    public bool IsInstallFilterNotInstalled => FilterInstallStatus == "Uninstalled";

    // ── Cloud library ──────────────────────────────────────────────────────
    public ObservableCollection<Game>   FilteredGames { get; } = new();
    public ObservableCollection<string>         Platforms     { get; } = new();
    /// <summary>Rich platform chips with icon, count, and selected state for the UI.</summary>
    public ObservableCollection<PlatformChipVm> PlatformChips { get; } = new();

    // ── Local drive detection ──────────────────────────────────────────────
    [ObservableProperty] private bool _hasLocalGames;
    [ObservableProperty] private bool _hasRepacks;
    [ObservableProperty] private bool _hasRoms;
    [ObservableProperty] private bool _hasMyGames;
    // Raw (unfiltered) sources — kept so filter can re-apply on the full list
    public ObservableCollection<LocalGame>   LocalGames     { get; } = new();
    public ObservableCollection<LocalRepack> ReadyToInstall { get; } = new();
    public ObservableCollection<LocalRom>    LocalRoms      { get; } = new();
    // Filtered views shown in the UI
    public ObservableCollection<LocalGame>         FilteredLocalGames  { get; } = new();
    public ObservableCollection<LocalRepack>        FilteredRepacks     { get; } = new();
    public ObservableCollection<LocalRom>           FilteredRoms        { get; } = new();
    /// <summary>Unified filtered list combining LocalGames + Repacks + ROMs for "My Games".</summary>
    public ObservableCollection<LocalGameCardVm>    FilteredMyGames     { get; } = new();

    /// <summary>Invoked when the user clicks a cloud game card.</summary>
    public Action<Game>?        OnOpenDetail       { get; set; }
    /// <summary>Invoked when the user clicks a local/detected game card.</summary>
    public Action<LocalGame>?   OnOpenLocalDetail  { get; set; }
    /// <summary>Invoked when the user clicks a ready-to-install repack card.</summary>
    public Action<LocalRepack>? OnOpenRepackDetail { get; set; }
    /// <summary>Invoked when the user clicks a ROM card.</summary>
    public Action<LocalRom>?    OnOpenRomDetail    { get; set; }
    /// <summary>Invoked when the user clicks any card in the unified My Games section.</summary>
    public Action<LocalGameCardVm>? OnOpenMyGameDetail { get; set; }

    // ── Debounce flag to avoid triple-rebuild when all three scanner events fire ──
    // Only ever read or written inside Dispatcher.UIThread.Post callbacks, so all
    // accesses are sequentially serialised on the UI thread — no locking needed.
    private bool _rebuildScheduled = false;

    public void Load(List<Game> games)
    {
        _allGames = games;

        // Rebuild the platform filter list from all game types combined
        RebuildPlatforms();
        ApplyFilter();
    }

    /// <summary>Called by MainViewModel when the scanner emits new results.</summary>
    public void UpdateLocalGames(IReadOnlyList<LocalGame> games)
    {
        var newGames = games.ToList();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _allLocalGames = newGames;
            LocalGames.Clear();
            foreach (var g in newGames) LocalGames.Add(g);
            HasLocalGames = LocalGames.Count > 0;
            ScheduleRebuild();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>Called by MainViewModel when the scanner emits new repacks.</summary>
    public void UpdateRepacks(IReadOnlyList<LocalRepack> repacks)
    {
        var newRepacks = repacks.ToList();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _allRepacks = newRepacks;
            ReadyToInstall.Clear();
            foreach (var r in newRepacks) ReadyToInstall.Add(r);
            HasRepacks = ReadyToInstall.Count > 0;
            ScheduleRebuild();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>Called by MainViewModel when the scanner emits new ROMs.</summary>
    public void UpdateRoms(IReadOnlyList<LocalRom> roms)
    {
        var newRoms = roms.ToList();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _allRoms = newRoms;
            LocalRoms.Clear();
            foreach (var r in newRoms) LocalRoms.Add(r);
            HasRoms = LocalRoms.Count > 0;
            ScheduleRebuild();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// Invoked on the UI thread after each scheduled rebuild completes so that
    /// MainViewModel can trigger cover-art enrichment on the freshly built cards.
    /// </summary>
    public Action? OnMyGamesRebuilt { get; set; }

    /// <summary>
    /// Schedules a single deferred rebuild (RebuildMyGames + RebuildPlatforms + ApplyFilter).
    /// Multiple rapid calls collapse into one background pass so the three scanner events
    /// that fire together never trigger more than one expensive rebuild.
    /// The heavy list-building computation runs on a thread-pool thread to avoid
    /// freezing the UI when thousands of games are present.
    /// Fires <see cref="OnMyGamesRebuilt"/> after the rebuild so callers can enrich cover
    /// art once <c>_allMyGames</c> is fully populated.
    /// </summary>
    private void ScheduleRebuild()
    {
        if (_rebuildScheduled) return;
        _rebuildScheduled = true;
        // Take snapshots of the source lists so the background thread doesn't race
        // with UI-thread updates that might swap the list references.
        var localGamesSnap = _allLocalGames.ToList();
        var repacksSnap    = _allRepacks.ToList();
        var romsSnap       = _allRoms.ToList();
        var allGamesSnap   = _allGames.ToList();

        System.Threading.Tasks.Task.Run(() =>
        {
            // Heavy computation on a background thread
            var newMyGames = BuildMyGamesList(allGamesSnap, localGamesSnap, repacksSnap, romsSnap);
            var newPlatforms = BuildPlatformList(allGamesSnap, localGamesSnap, repacksSnap, romsSnap);

            // Switch to UI thread only for collection updates
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _rebuildScheduled = false;

                _allMyGames.Clear();
                foreach (var c in newMyGames) _allMyGames.Add(c);

                // Update Platforms list and chips
                var current = FilterPlatform;
                Platforms.Clear();
                Platforms.Add("All");
                foreach (var p in newPlatforms) Platforms.Add(p);
                FilterPlatform = Platforms.Contains(current) ? current : "All";
                RebuildPlatformChips();

                TotalGames = newMyGames.Count;

                ApplyFilter();
                OnMyGamesRebuilt?.Invoke();
            }, Avalonia.Threading.DispatcherPriority.Background);
        });
    }

    /// <summary>
    /// Called by MainViewModel after background cover-art enrichment updates a card's
    /// CoverUrl and CoverGradient from the Games.Database.
    /// </summary>
    public LocalGameCardVm? FindMyGameCard(string title, string platform)
    {
        return _allMyGames.FirstOrDefault(c =>
            string.Equals(c.Title, title, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.Platform, platform, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Returns a snapshot of all platform/title/titleId tuples in the My Games list so
    /// MainViewModel can enrich cover art without accessing private fields.
    /// </summary>
    public IReadOnlyList<(string Title, string Platform, string? TitleId)> GetMyGameSources()
    {
        return _allMyGames
            .Select(c => (c.Title, c.Platform, c.SourceRom?.TitleId ?? c.SourceCloudGame?.TitleId))
            .Distinct()
            .ToList();
    }

    partial void OnFilterPlatformChanged(string value)
    {
        // Update chip selection state
        foreach (var chip in PlatformChips)
            chip.IsSelected = string.Equals(chip.Name, value, StringComparison.OrdinalIgnoreCase);
        ApplyFilter();
    }
    partial void OnSearchTextChanged(string value)          => ApplyFilter();
    partial void OnFilterInstallStatusChanged(string value) => ApplyFilter();

    [RelayCommand]
    private void SetInstallFilter(string status) => FilterInstallStatus = status;

    [RelayCommand]
    private void SetPlatform(string platform) => FilterPlatform = platform;

    [RelayCommand]
    private void OpenGameDetail(Game? game)
    {
        if (game != null) OnOpenDetail?.Invoke(game);
    }

    [RelayCommand]
    private void OpenLocalGameDetail(LocalGame? game)
    {
        if (game != null) OnOpenLocalDetail?.Invoke(game);
    }

    [RelayCommand]
    private void OpenRepackDetail(LocalRepack? repack)
    {
        if (repack != null) OnOpenRepackDetail?.Invoke(repack);
    }

    [RelayCommand]
    private void OpenRomDetail(LocalRom? rom)
    {
        if (rom != null) OnOpenRomDetail?.Invoke(rom);
    }

    [RelayCommand]
    private void OpenMyGameDetail(LocalGameCardVm? card)
    {
        if (card != null) OnOpenMyGameDetail?.Invoke(card);
    }

    // ── Private helpers ────────────────────────────────────────────────────

    /// <summary>Rebuilds _allMyGames from the current three source lists.</summary>
    private void RebuildMyGames()
    {
        _allMyGames.Clear();

        // Build a normalized installed-title set for fuzzy repack deduplication.
        var installedTitles = GameScannerService.BuildFuzzyTitleSet(
            _allLocalGames.Select(g => g.Title));

        var cloudByPlatform = _allGames
            .GroupBy(g => GameLauncher.Models.PlatformHelper.NormalizePlatform(g.Platform),
                     StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                grp => grp.Key,
                grp => new HashSet<string>(grp.Select(g => g.Title), StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

        var cloudSteamAppIds = new HashSet<long>(
            _allGames
                .Where(g => g.SteamAppId.HasValue && g.SteamAppId.Value > 0)
                .Select(g => g.SteamAppId!.Value));

        cloudByPlatform.TryGetValue("PC", out var cloudPcTitles);
        var cloudPcStripped = cloudPcTitles != null
            ? new HashSet<string>(
                cloudPcTitles.Select(PlatformHelper.StripSpecialSymbols),
                StringComparer.OrdinalIgnoreCase)
            : null;

        // Build install-status lookup sets for local PC games
        var localSteamAppIds = new HashSet<long>(
            _allLocalGames.Where(g => g.SteamAppId > 0).Select(g => (long)g.SteamAppId));
        var localTitlesStripped = new HashSet<string>(
            _allLocalGames.Select(g => PlatformHelper.StripSpecialSymbols(g.Title)),
            StringComparer.OrdinalIgnoreCase);

        // Cloud library games
        foreach (var g in _allGames)
        {
            string normalizedPlatform = GameLauncher.Models.PlatformHelper.NormalizePlatform(g.Platform);
            bool isInstalled = false;
            if (string.Equals(normalizedPlatform, "PC", StringComparison.OrdinalIgnoreCase))
            {
                isInstalled = (g.SteamAppId > 0 && localSteamAppIds.Contains(g.SteamAppId.Value))
                           || localTitlesStripped.Contains(PlatformHelper.StripSpecialSymbols(g.Title));
            }
            _allMyGames.Add(new LocalGameCardVm
            {
                Title            = g.Title,
                Platform         = normalizedPlatform,
                CoverUrl         = g.CoverUrl,
                CoverGradient    = g.CoverGradient ?? "#0d1117,#1f2937",
                SourceCloudGame  = g,
                IsInstalledLocal = isInstalled,
                PlaytimeLabel    = FormatPlaytime(g.PlaytimeMinutes),
            });
        }

        // Local PC games NOT already represented in the cloud library
        foreach (var g in _allLocalGames)
        {
            if (cloudPcTitles != null &&
                (cloudPcTitles.Contains(g.Title) ||
                 cloudPcStripped!.Contains(PlatformHelper.StripSpecialSymbols(g.Title))))
                continue;
            if (g.SteamAppId > 0 && cloudSteamAppIds.Contains(g.SteamAppId))
                continue;

            _allMyGames.Add(new LocalGameCardVm
            {
                Title         = g.Title,
                Platform      = "PC",
                CoverGradient = "#0d2137,#163d5e",
                SourceGame    = g,
                PlaytimeLabel = FormatPlaytime(Services.PlaytimeService.GetTotalMinutes("PC", g.Title)),
            });
        }

        // Repacks → platform = "PC"
        // Skip repacks whose game is already installed (detected in the Games folder).
        // Use the shared fuzzy helper so name variants (symbols, "–" vs ":", spacing)
        // don't create duplicate cards for the same title.
        // Also skip repacks already represented in the cloud library — clicking the
        // cloud card handles the repack install path via OpenDetailFromGame.
        foreach (var r in _allRepacks)
        {
            if (GameScannerService.RepackMatchesInstalledTitle(r.Title, installedTitles))
                continue;

            if (cloudPcTitles != null &&
                (cloudPcTitles.Contains(r.Title) ||
                 cloudPcStripped!.Contains(PlatformHelper.StripSpecialSymbols(r.Title))))
                continue;

            _allMyGames.Add(new LocalGameCardVm
            {
                Title          = r.Title,
                Platform       = "PC",
                CoverGradient  = r.IsInstalledGame ? "#0d2137,#163d5e" : "#2d1b00,#5c3800",
                SourceRepack   = r,
            });
        }

        // Build a lookup of cloud library games by (normalizedPlatform, titleId) for
        // deduplication of folder-based ROMs (PS3/PS4/Switch) that use TitleID as folder name.
        var cloudByPlatformTitleId = _allGames
            .Where(g => !string.IsNullOrEmpty(g.TitleId))
            .GroupBy(g => GameLauncher.Models.PlatformHelper.NormalizePlatform(g.Platform),
                     StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                grp => grp.Key,
                grp => new HashSet<string>(grp.Select(g => g.TitleId!), StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

        // ROMs → platform from the ROM itself
        // Skip ROMs whose title + platform already exist in the cloud library to avoid
        // showing the same game twice (once from the library JSON, once from the local scan).
        // Use fuzzy comparison (strip ™/®/© symbols) to handle official titles like
        // "Mario Kart™ 8 Deluxe" (cloud) vs "Mario Kart 8 Deluxe" (local folder).
        // Also deduplicate by TitleID for PS3/PS4/Switch folder-based ROMs where
        // the ROM folder name is the TitleID (e.g. "CUSA00572") not the game title.
        foreach (var r in _allRoms)
        {
            // Deduplicate by TitleID when the ROM has one (folder-based PS3/PS4/Switch)
            if (!string.IsNullOrEmpty(r.TitleId) &&
                cloudByPlatformTitleId.TryGetValue(r.Platform, out var cloudTitleIds) &&
                cloudTitleIds.Contains(r.TitleId))
                continue;

            if (cloudByPlatform.TryGetValue(r.Platform, out var cloudTitles) &&
                (cloudTitles.Contains(r.Title) ||
                 cloudTitles.Any(ct => string.Equals(
                     PlatformHelper.StripSpecialSymbols(ct),
                     PlatformHelper.StripSpecialSymbols(r.Title),
                     StringComparison.OrdinalIgnoreCase))))
                continue;

            _allMyGames.Add(new LocalGameCardVm
            {
                Title          = r.Title,
                Platform       = r.Platform,
                CoverGradient  = "#0d1f3c,#1a3264",
                SourceRom      = r,
            });
        }
    }

    /// <summary>
    /// Pure (no side-effects) version of <see cref="RebuildMyGames"/> that works on
    /// snapshot lists passed in as parameters.  Safe to call from a background thread.
    /// </summary>
    private static List<LocalGameCardVm> BuildMyGamesList(
        List<GameLauncher.Models.Game> allGames,
        List<LocalGame> localGames,
        List<LocalRepack> repacks,
        List<LocalRom> roms)
    {
        var result = new List<LocalGameCardVm>();

        var installedTitles = GameScannerService.BuildFuzzyTitleSet(
            localGames.Select(g => g.Title));

        var cloudByPlatform = allGames
            .GroupBy(g => PlatformHelper.NormalizePlatform(g.Platform), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                grp => grp.Key,
                grp => new HashSet<string>(grp.Select(g => g.Title), StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

        cloudByPlatform.TryGetValue("PC", out var cloudPcTitles);
        var cloudPcStripped = cloudPcTitles != null
            ? new HashSet<string>(cloudPcTitles.Select(PlatformHelper.StripSpecialSymbols), StringComparer.OrdinalIgnoreCase)
            : null;

        var cloudSteamAppIds = new HashSet<long>(
            allGames
                .Where(g => g.SteamAppId.HasValue && g.SteamAppId.Value > 0)
                .Select(g => g.SteamAppId!.Value));

        // ── Build install-status lookup sets for local PC games ───────────────
        // These let us mark cloud PC entries as Installed/Not Installed.
        var localSteamAppIds = new HashSet<long>(
            localGames.Where(g => g.SteamAppId > 0).Select(g => (long)g.SteamAppId));
        var localTitlesStripped = new HashSet<string>(
            localGames.Select(g => PlatformHelper.StripSpecialSymbols(g.Title)),
            StringComparer.OrdinalIgnoreCase);

        // ── ALL cloud library games (first, so they sort to the top before dedup below) ──
        foreach (var g in allGames)
        {
            string normalizedPlatform = PlatformHelper.NormalizePlatform(g.Platform);
            bool isInstalled = false;
            if (string.Equals(normalizedPlatform, "PC", StringComparison.OrdinalIgnoreCase))
            {
                // A cloud PC game counts as locally installed when a matching local game is
                // found by SteamAppId (most reliable) or by stripped title as a fallback.
                isInstalled = (g.SteamAppId > 0 && localSteamAppIds.Contains(g.SteamAppId.Value))
                           || localTitlesStripped.Contains(PlatformHelper.StripSpecialSymbols(g.Title));
            }

            result.Add(new LocalGameCardVm
            {
                Title            = g.Title,
                Platform         = normalizedPlatform,
                CoverUrl         = g.CoverUrl,
                CoverGradient    = g.CoverGradient ?? "#0d1117,#1f2937",
                SourceCloudGame  = g,
                IsInstalledLocal = isInstalled,
                PlaytimeLabel    = FormatPlaytime(g.PlaytimeMinutes),
            });
        }

        // ── Local PC games NOT already represented in the cloud library ───────
        foreach (var g in localGames)
        {
            if (cloudPcTitles != null &&
                (cloudPcTitles.Contains(g.Title) ||
                 cloudPcStripped!.Contains(PlatformHelper.StripSpecialSymbols(g.Title))))
                continue;

            if (g.SteamAppId > 0 && cloudSteamAppIds.Contains(g.SteamAppId))
                continue;

            result.Add(new LocalGameCardVm
            {
                Title         = g.Title,
                Platform      = "PC",
                CoverGradient = "#0d2137,#163d5e",
                SourceGame    = g,
                PlaytimeLabel = FormatPlaytime(Services.PlaytimeService.GetTotalMinutes("PC", g.Title)),
            });
        }

        foreach (var r in repacks)
        {
            if (GameScannerService.RepackMatchesInstalledTitle(r.Title, installedTitles))
                continue;

            // Also skip if the cloud library already has a matching PC entry — clicking the
            // cloud card already handles the repack install path via OpenDetailFromGame.
            if (cloudPcTitles != null &&
                (cloudPcTitles.Contains(r.Title) ||
                 cloudPcStripped!.Contains(PlatformHelper.StripSpecialSymbols(r.Title))))
                continue;

            result.Add(new LocalGameCardVm
            {
                Title         = r.Title,
                Platform      = "PC",
                CoverGradient = r.IsInstalledGame ? "#0d2137,#163d5e" : "#2d1b00,#5c3800",
                SourceRepack  = r,
            });
        }

        var cloudByPlatformTitleId = allGames
            .Where(g => !string.IsNullOrEmpty(g.TitleId))
            .GroupBy(g => PlatformHelper.NormalizePlatform(g.Platform), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                grp => grp.Key,
                grp => new HashSet<string>(grp.Select(g => g.TitleId!), StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

        foreach (var r in roms)
        {
            if (!string.IsNullOrEmpty(r.TitleId) &&
                cloudByPlatformTitleId.TryGetValue(r.Platform, out var cloudTitleIds) &&
                cloudTitleIds.Contains(r.TitleId))
                continue;

            if (cloudByPlatform.TryGetValue(r.Platform, out var cloudTitles) &&
                (cloudTitles.Contains(r.Title) ||
                 cloudTitles.Any(ct => string.Equals(
                     PlatformHelper.StripSpecialSymbols(ct),
                     PlatformHelper.StripSpecialSymbols(r.Title),
                     StringComparison.OrdinalIgnoreCase))))
                continue;

            result.Add(new LocalGameCardVm
            {
                Title         = r.Title,
                Platform      = r.Platform,
                CoverGradient = "#0d1f3c,#1a3264",
                SourceRom     = r,
                PlaytimeLabel = FormatPlaytime(Services.PlaytimeService.GetTotalMinutes(r.Platform, r.Title)),
            });
        }

        return result;
    }

    /// <summary>
    /// Pure (no side-effects) version of platform-list building.
    /// Returns an ordered, distinct list of platform names (without "All").
    /// Safe to call from a background thread.
    /// </summary>
    private static List<string> BuildPlatformList(
        List<GameLauncher.Models.Game> allGames,
        List<LocalGame> localGames,
        List<LocalRepack> repacks,
        List<LocalRom> roms)
    {
        return allGames
            .Select(g => PlatformHelper.NormalizePlatform(g.Platform))
            .Concat(roms.Select(r => r.Platform))
            .Concat(localGames.Select(_ => "PC"))
            .Concat(repacks.Select(_ => "PC"))
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p)
            .ToList();
    }

    /// <summary>Rebuilds the Platforms filter list from all game sources combined.</summary>
    private void RebuildPlatforms()
    {
        var current = FilterPlatform;

        var platforms = _allGames
            .Select(g => GameLauncher.Models.PlatformHelper.NormalizePlatform(g.Platform))
            .Concat(_allRoms.Select(r => r.Platform))
            .Concat(_allLocalGames.Select(_ => "PC"))
            .Concat(_allRepacks.Select(_ => "PC"))
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p)
            .ToList();

        Platforms.Clear();
        Platforms.Add("All");
        foreach (var p in platforms)
            Platforms.Add(p);

        // Restore or reset the filter selection
        FilterPlatform = Platforms.Contains(current) ? current : "All";

        // Rebuild rich platform chips with game counts
        RebuildPlatformChips();

        // Update total count: cloud + local games + repacks + roms
        TotalGames = _allGames.Count + _allLocalGames.Count + _allRepacks.Count + _allRoms.Count;
    }

    /// <summary>Rebuilds PlatformChips from the current Platforms list with counts.</summary>
    private void RebuildPlatformChips()
    {
        // Build a count map: how many games per platform
        var countMap = _allGames
            .GroupBy(g => GameLauncher.Models.PlatformHelper.NormalizePlatform(g.Platform),
                     StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (var r in _allRoms)
        {
            countMap.TryGetValue(r.Platform, out int existing);
            countMap[r.Platform] = existing + 1;
        }
        int pcCount = _allLocalGames.Count + _allRepacks.Count;
        if (pcCount > 0)
        {
            countMap.TryGetValue("PC", out int existingPc);
            countMap["PC"] = existingPc + pcCount;
        }

        int total = countMap.Values.Sum();

        PlatformChips.Clear();
        PlatformChips.Add(new PlatformChipVm("All", total, FilterPlatform == "All"));
        foreach (var p in Platforms.Skip(1))
        {
            countMap.TryGetValue(p, out int count);
            PlatformChips.Add(new PlatformChipVm(p, count, FilterPlatform == p));
        }
    }

    private void ApplyFilter()
    {
        var search         = SearchText;
        var plat           = FilterPlatform;
        var installStatus  = FilterInstallStatus;

        // ── Cloud games section is now part of the unified list ───────────────
        // FilteredGames is kept for backwards compat but is always empty.
        FilteredGames.Clear();

        // ── Local installed games — kept for legacy detail-view routing ───────
        FilteredLocalGames.Clear();
        if (plat == "All" || string.Equals(plat, "PC", StringComparison.OrdinalIgnoreCase))
        {
            var localResults = _allLocalGames.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(search))
                localResults = localResults.Where(g =>
                    g.Title.Contains(search, StringComparison.OrdinalIgnoreCase));
            foreach (var g in localResults.OrderBy(g => g.Title))
                FilteredLocalGames.Add(g);
        }
        HasLocalGames = FilteredLocalGames.Count > 0;

        // ── Repacks — kept for legacy routing ────────────────────────────────
        FilteredRepacks.Clear();
        if (plat == "All" || string.Equals(plat, "PC", StringComparison.OrdinalIgnoreCase))
        {
            var repackResults = _allRepacks.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(search))
                repackResults = repackResults.Where(r =>
                    r.Title.Contains(search, StringComparison.OrdinalIgnoreCase));
            foreach (var r in repackResults.OrderBy(r => r.Title))
                FilteredRepacks.Add(r);
        }
        HasRepacks = FilteredRepacks.Count > 0;

        // ── ROMs — kept for legacy routing ────────────────────────────────────
        FilteredRoms.Clear();
        var romResults = _allRoms.AsEnumerable();
        if (plat != "All")
            romResults = romResults.Where(r =>
                string.Equals(r.Platform, plat, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(search))
            romResults = romResults.Where(r =>
                r.Title.Contains(search, StringComparison.OrdinalIgnoreCase));
        foreach (var r in romResults.OrderBy(r => r.Title))
            FilteredRoms.Add(r);
        HasRoms = FilteredRoms.Count > 0;

        // ── Unified My Games (Cloud + LocalGames + Repacks + ROMs) ───────────
        // Refresh playtime labels on cloud game cards so they reflect the latest
        // PlaytimeMinutes values (which may have been updated by ApplyCloudPlaytimeAsync
        // after the cards were initially built). Only cloud cards need this — local game
        // and ROM cards source their playtime from PlaytimeService directly.
        foreach (var card in _allMyGames)
        {
            if (card.SourceCloudGame != null)
                card.PlaytimeLabel = FormatPlaytime(card.SourceCloudGame.PlaytimeMinutes);
        }

        FilteredMyGames.Clear();
        var myResults = _allMyGames.AsEnumerable();
        if (plat != "All")
            myResults = myResults.Where(c =>
                string.Equals(c.Platform, plat, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(search))
            myResults = myResults.Where(c =>
                c.Title.Contains(search, StringComparison.OrdinalIgnoreCase));

        // Apply install-status filter.
        // Non-PC cloud games (Xbox 360, PS3, etc.) always pass — they cannot be
        // "locally installed" the same way as PC games and should always be shown.
        if (installStatus is "Installed" or "Uninstalled")
        {
            bool wantInstalled = installStatus == "Installed";
            myResults = myResults.Where(c =>
            {
                if (c.SourceCloudGame != null &&
                    !string.Equals(c.Platform, "PC", StringComparison.OrdinalIgnoreCase))
                    return true; // non-PC cloud entries always shown regardless of filter
                return wantInstalled ? c.IsInstalledLocal : !c.IsInstalledLocal;
            });
        }

        foreach (var c in myResults.OrderBy(c => c.Title, StringComparer.OrdinalIgnoreCase))
            FilteredMyGames.Add(c);
        HasMyGames = FilteredMyGames.Count > 0;

        TotalGames = _allMyGames.Count;
    }

    /// <summary>
    /// Formats a playtime value (minutes) into a short human-readable label.
    /// Returns an empty string when minutes is zero or negative.
    /// </summary>
    private static string FormatPlaytime(int minutes)
    {
        if (minutes <= 0) return "";
        if (minutes < 60) return $"{minutes}m";
        int hours = minutes / 60;
        int mins  = minutes % 60;
        return mins > 0 ? $"{hours}h {mins}m" : $"{hours}h";
    }
}

/// <summary>A platform filter chip shown above the library grid.</summary>
public partial class PlatformChipVm : ViewModelBase
{
    public string Name       { get; }
    public string Icon       { get; }
    public bool   HasIcon    => !string.IsNullOrEmpty(Icon);
    public int    GameCount  { get; }

    [ObservableProperty] private bool _isSelected;

    public PlatformChipVm(string name, int count, bool selected = false)
    {
        Name       = name;
        GameCount  = count;
        IsSelected = selected;
        Icon       = PlatformChipVm.GetIcon(name);
    }

    public static string GetIcon(string platform) => platform switch
    {
        "All"       => "🌐",
        "PC"        => "🖥",
        "PS1"       => "🎮",
        "PS2"       => "🎮",
        "PS3"       => "🎮",
        "PS4"       => "🎮",
        "PS5"       => "🎮",
        "PSP"       => "🎮",
        "PS Vita"   => "🎮",
        "Xbox 360"  => "🟢",
        "Xbox One"  => "🟢",
        "Switch"    => "🕹",
        _           => "",
    };
}
