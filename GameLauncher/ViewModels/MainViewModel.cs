using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Models;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

/// <summary>
/// Root view-model that owns navigation and the shared session state.
/// </summary>
public partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly GameOsClient            _client;
    private readonly GameScannerService      _scanner;
    private readonly SessionCacheService     _sessionCache;
    private readonly OfflineDataCacheService _offlineCache   = new();
    private readonly PendingChangesService   _pendingChanges = new();
    private readonly PlaytimeService         _playtimeSvc    = new();

    // ── Message polling ────────────────────────────────────────────────────
    /// <summary>Fires every 60 seconds after login to check for new direct messages.</summary>
    private Timer? _messagePoller;
    /// <summary>Latest known message timestamp per friend (sender username → ISO timestamp).</summary>
    private readonly Dictionary<string, string> _lastKnownMessageAt =
        new(StringComparer.OrdinalIgnoreCase);

    // ── Presence heartbeat ─────────────────────────────────────────────────
    /// <summary>
    /// Fires every 2 minutes to refresh the logged-in user's presence timestamp,
    /// mirroring the web app's setInterval(updatePresence, 2 * 60 * 1000) heartbeat.
    /// A user is considered Online when their lastSeen is &lt;5 min old.
    /// </summary>
    private Timer? _presenceTimer;

    // ── Offline reconnect timer ────────────────────────────────────────────
    /// <summary>
    /// Fires every 5 minutes while the app is in Offline Mode to check whether
    /// a live server connection is available.  Stopped and disposed as soon as
    /// the app transitions back to Online Mode.
    /// </summary>
    private Timer? _offlineReconnectTimer;

    // ── Periodic sync timer ────────────────────────────────────────────────
    /// <summary>
    /// Fires every 5 minutes while the app is Online to check whether the remote
    /// user data (games, achievements) has changed and refresh the local cache if
    /// so.  Also re-runs the Games.Database update check to invalidate any stale
    /// platform caches.  Stopped when the app goes Offline.
    /// </summary>
    private Timer? _syncCheckTimer;

    // ── Session data ───────────────────────────────────────────────────────
    private UserProfile     _profile      = new();
    private List<Game>      _library      = new();
    private List<Achievement> _achievements = new();

    // ── Child view models ──────────────────────────────────────────────────
    public LoginViewModel     LoginVm          { get; }
    public DashboardViewModel DashboardVm      { get; }
    public LibraryViewModel   LibraryVm        { get; }
    public StoreViewModel     StoreVm          { get; }
    public ProfileViewModel   ProfileVm        { get; }
    public ProfileViewModel   FriendProfileVm  { get; }
    public FriendsViewModel   FriendsVm        { get; }
    public SettingsViewModel  SettingsVm       { get; }
    public GameDetailViewModel DetailVm        { get; }

    // ── Navigation state ───────────────────────────────────────────────────
    [ObservableProperty] private bool _showLogin         = true;
    [ObservableProperty] private bool _showMain          = false;
    [ObservableProperty] private bool _showDetail        = false;
    [ObservableProperty] private bool _showFriendProfile = false;
    [ObservableProperty] private string _activePage      = "dashboard";
    /// <summary>True when the left nav sidebar overlay is visible.</summary>
    [ObservableProperty] private bool _isNavExpanded     = false;
    /// <summary>Username of the friend currently being viewed (shown in the friend-profile overlay).</summary>
    [ObservableProperty] private string _viewingFriendName = "";
    /// <summary>
    /// True when the user is logged in via a locally-cached session because the
    /// server is unreachable.  Drives the Offline/Online mode badge in the UI.
    /// </summary>
    [ObservableProperty] private bool _isOfflineMode = false;

    public bool IsHome        => ActivePage == "dashboard";
    public bool IsLibrary     => ActivePage == "library";
    public bool IsStore       => ActivePage == "store";
    public bool IsProfile     => ActivePage == "profile";
    public bool IsFriends     => ActivePage == "friends";
    public bool IsSettings    => ActivePage == "settings";

    partial void OnActivePageChanged(string value)
    {
        OnPropertyChanged(nameof(IsHome));
        OnPropertyChanged(nameof(IsLibrary));
        OnPropertyChanged(nameof(IsStore));
        OnPropertyChanged(nameof(IsProfile));
        OnPropertyChanged(nameof(IsFriends));
        OnPropertyChanged(nameof(IsSettings));
    }

    public MainViewModel()
    {
        _client       = new GameOsClient();
        _sessionCache = new SessionCacheService();

        LoginVm        = new LoginViewModel(_client, _sessionCache, _offlineCache);
        DashboardVm    = new DashboardViewModel();
        LibraryVm      = new LibraryViewModel();
        StoreVm        = new StoreViewModel();
        ProfileVm      = new ProfileViewModel();
        FriendProfileVm= new ProfileViewModel();
        FriendsVm      = new FriendsViewModel();
        SettingsVm     = new SettingsViewModel();
        DetailVm       = new GameDetailViewModel();

        DetailVm.OnClose = () => ShowDetail = false;

        // Wire playtime tracking: when a game is launched from the detail view,
        // pass the process to the PlaytimeService to record the session, then
        // immediately refresh the dashboard so "Continue Playing" shows the game.
        DetailVm.OnRequestPlaytimeTracking = (proc, title, platform) =>
        {
            _playtimeSvc.TrackProcess(proc, title, platform, _library);
            // Refresh dashboard immediately so the game appears in "Continue Playing"
            RefreshDashboardLocalGames();
        };

        // When a tracked game session ends, refresh the dashboard one more time
        // so the stored playtime and status reflect the completed session.
        PlaytimeService.SessionCompleted += (platform, title) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => RefreshDashboardLocalGames());

        LoginVm.OnLoginSuccess = OnLoginSuccess;

        // Wire up OpenDetail from child VMs
        DashboardVm.OnOpenDetail      = OpenDetailFromGame;
        DashboardVm.OnOpenStoreDetail = OpenDetailFromStoreGame;
        DashboardVm.OnOpenLocalDetail = OpenDetailFromMyGameCard;
        DashboardVm.OnContinuePlaying = LaunchFromCard;
        LibraryVm.OnOpenDetail        = OpenDetailFromGame;
        LibraryVm.OnOpenLocalDetail   = OpenDetailFromLocalGame;
        LibraryVm.OnOpenRepackDetail  = OpenDetailFromLocalRepack;
        LibraryVm.OnOpenRomDetail     = OpenDetailFromLocalRom;
        LibraryVm.OnOpenMyGameDetail  = OpenDetailFromMyGameCard;
        StoreVm.OnOpenDetail          = OpenDetailFromStoreGame;
        FriendsVm.OnViewFriendProfile = OpenFriendProfile;

        // Start background scanner regardless of login state
        _scanner = new GameScannerService();
        // Wire the rebuild-complete callback so cover enrichment always runs after
        // _allMyGames is fully populated — eliminates the race condition where
        // EnrichMyGamesListAsync ran before ScheduleRebuild had finished.
        LibraryVm.OnMyGamesRebuilt = () => _ = EnrichMyGamesListAsync();
        _scanner.GamesUpdated   += games   => { LibraryVm.UpdateLocalGames(games); RefreshDashboardLocalGames(); };
        _scanner.RepacksUpdated += repacks => { LibraryVm.UpdateRepacks(repacks);  RefreshDashboardLocalGames(); };
        _scanner.RomsUpdated    += roms    => { LibraryVm.UpdateRoms(roms);        RefreshDashboardLocalGames(); };
        _ = _scanner.StartAsync();

        // On startup, check if any cached platform JSON files are outdated and
        // invalidate stale ones so the next database fetch pulls fresh data.
        // Mirrors the web app always fetching with ?t=Date.now() cache-busting.
        // Respects the user's AutoUpdate preference from app settings.
        if (Services.AppSettingsService.Load().AutoUpdate)
            _ = Services.GitHubDataService.CheckForUpdatesAsync();

        // Attempt silent auto-login from cached session (mirrors web localStorage restore)
        _ = LoginVm.TryAutoLoginAsync();

    }

    /// <summary>
    /// Enters demo / screenshot mode: skips authentication and pre-populates
    /// every page with rich, realistic sample data so screenshots can be taken
    /// on any machine without a live backend connection.
    /// Called by <see cref="App.OnFrameworkInitializationCompleted"/> when
    /// <see cref="DemoMode.IsEnabled"/> is true.
    /// </summary>
    public void LoadDemo()
    {
        // ── Demo profile ─────────────────────────────────────────────────
        _profile = new UserProfile
        {
            Username  = "Koriebonx98",
            Email     = "koriebonx98@gameos.io",
            CreatedAt = "2023-09-12T08:00:00Z",
        };

        // ── Demo library (mix of platforms, realistic dates) ─────────────
        _library = new List<Game>
        {
            new() { Title = "Cyberpunk 2077",          Platform = "PC",     Genre = "RPG",        Rating = 9.1, AddedAt = "2025-01-10T12:00:00Z",
                    CoverUrl = "https://media.rawg.io/media/games/26d/26d4437715bee60138dab4a7c8c59c92.jpg",
                    CoverColor = "#1a1a2e", CoverGradient = "#1a1a2e,#16213e",
                    Description = "An open-world action RPG set in Night City." },
            new() { Title = "Elden Ring",               Platform = "PC",     Genre = "Action RPG", Rating = 9.6, AddedAt = "2025-02-14T09:30:00Z",
                    CoverUrl = "https://media.rawg.io/media/games/b45/b45575f34285f2c4479c9a5f719d972e.jpg",
                    CoverColor = "#1c0a00", CoverGradient = "#1c0a00,#6e2400" },
            new() { Title = "Baldur's Gate 3",          Platform = "PC",     Genre = "RPG",        Rating = 9.8, AddedAt = "2025-03-01T15:00:00Z",
                    CoverUrl = "https://media.rawg.io/media/games/618/618c2031a07bbff6b4f611f10b6bcdbc.jpg",
                    CoverColor = "#0d1b2a", CoverGradient = "#0d1b2a,#1b4332" },
            new() { Title = "God of War Ragnarök",      Platform = "PS5",    Genre = "Action",     Rating = 9.7, AddedAt = "2025-02-05T08:00:00Z",
                    CoverUrl = "https://media.rawg.io/media/games/fc1/fc1307a2774506b5bd65d7e8424664a7.jpg",
                    CoverColor = "#1a0a00", CoverGradient = "#1a0a00,#8b0000" },
            new() { Title = "The Last of Us Part II",   Platform = "PS4",    Genre = "Action",     Rating = 9.5, AddedAt = "2025-01-22T10:00:00Z",
                    CoverUrl = "https://cdn2.steamgriddb.com/grid/3a96a1164364c063f40ce33aaf971783.png",
                    CoverColor = "#0a1a08", CoverGradient = "#0a1a08,#1a3a10" },
            new() { Title = "Mario Kart 8 Deluxe",      Platform = "Switch", Genre = "Racing",     Rating = 9.7, AddedAt = "2025-06-01T10:00:00Z",
                    CoverUrl = "https://cdn2.steamgriddb.com/grid/9cd6d894098e748716960bfcf9dbe115.png",
                    CoverColor = "#c00000", CoverGradient = "#c00000,#ff6b00" },
            new() { Title = "Halo Infinite",             Platform = "Xbox",   Genre = "FPS",        Rating = 8.5, AddedAt = "2025-01-20T11:00:00Z",
                    CoverUrl = "https://media.rawg.io/media/games/3ea/3ea3c9bbd940b6cb7f2139e42d3d443f.jpg",
                    CoverColor = "#003153", CoverGradient = "#003153,#0056a8" },
            new() { Title = "Hogwarts Legacy",           Platform = "PC",     Genre = "RPG",        Rating = 8.7, AddedAt = "2025-04-01T10:00:00Z",
                    CoverUrl = "https://media.rawg.io/media/games/5ec/5ecac5cb026ec26a56efcc546364e348.jpg",
                    CoverColor = "#1e0a2a", CoverGradient = "#1e0a2a,#4a0080" },
            new() { Title = "Zelda: Tears of the Kingdom", Platform = "Switch", Genre = "Adventure", Rating = 9.9, AddedAt = "2025-04-15T14:00:00Z",
                    CoverColor = "#0a1628", CoverGradient = "#0a1628,#1a4a6e" },
            new() { Title = "God of War",                Platform = "PS4",    Genre = "Action",     Rating = 9.6, AddedAt = "2025-01-18T07:30:00Z",
                    CoverUrl = "https://cdn2.steamgriddb.com/grid/368b80128d9e529adf93f7ce84dfaca0.jpg",
                    CoverColor = "#1a0500", CoverGradient = "#1a0500,#5c1500" },
        };

        // ── Demo achievements ─────────────────────────────────────────────
        _achievements = new List<Achievement>
        {
            new() { Name = "Night City Legend",   GameTitle = "Cyberpunk 2077",         UnlockedAt = "2025-01-15T14:00:00Z" },
            new() { Name = "Elden Lord",           GameTitle = "Elden Ring",              UnlockedAt = "2025-02-20T11:30:00Z" },
            new() { Name = "Illithid Powers",      GameTitle = "Baldur's Gate 3",         UnlockedAt = "2025-03-10T16:00:00Z" },
            new() { Name = "Platinum Kart Racer",  GameTitle = "Mario Kart 8 Deluxe",     UnlockedAt = "2025-06-05T09:00:00Z" },
            new() { Name = "Muspelheim Conquered", GameTitle = "God of War Ragnarök",     UnlockedAt = "2025-02-10T18:00:00Z" },
        };

        // ── Demo local games (scanner results replaced with richer entries) ─
        var demoLocalGames = new List<LocalGame>
        {
            new() { Title = "Cyberpunk 2077",    ExecutablePath = "/Games/Cyberpunk 2077/Cyberpunk2077.elf",    ExecutableType = "elf", DriveRoot = "/Games" },
            new() { Title = "The Witcher 3",     ExecutablePath = "/Games/The Witcher 3/witcher3.elf",          ExecutableType = "elf", DriveRoot = "/Games" },
            new() { Title = "Grand Theft Auto V",ExecutablePath = "/Games/Grand Theft Auto V/GTAV.elf",         ExecutableType = "elf", DriveRoot = "/Games" },
        };

        var demoRepacks = new List<LocalRepack>
        {
            new() { Title = "Elden Ring",         FilePath = "/Repacks/Elden Ring [FitGirl Repack].iso",       FileType = "iso",  SizeBytes = 30_000_000_000L },
            new() { Title = "Baldur's Gate 3",    FilePath = "/Repacks/Baldurs Gate 3 [DODI Repack].zip",      FileType = "zip",  SizeBytes = 64_000_000_000L },
            new() { Title = "Resident Evil 4",    FilePath = "/Repacks/Resident Evil 4 [Repack].rar",          FileType = "rar",  SizeBytes = 12_500_000_000L },
        };

        var demoRoms = new List<LocalRom>
        {
            new() { Title = "Halo 3",             Platform = "Xbox 360", FilePath = "/Roms/Xbox 360/Games/Halo 3.iso",              FileType = "iso",  SizeBytes = 6_800_000_000L  },
            new() { Title = "Gears of War",       Platform = "Xbox 360", FilePath = "/Roms/Xbox 360/Games/Gears of War.iso",        FileType = "iso",  SizeBytes = 7_200_000_000L  },
            new() { Title = "Forza Motorsport 4", Platform = "Xbox 360", FilePath = "/Roms/Xbox 360/Games/Forza Motorsport 4.iso",  FileType = "iso",  SizeBytes = 8_100_000_000L  },
            new() { Title = "God of War III",     Platform = "PS3",      FilePath = "/Roms/PS3/Games/God of War III.iso",           FileType = "iso",  SizeBytes = 35_000_000_000L },
            new() { Title = "Uncharted 2",        Platform = "PS3",      FilePath = "/Roms/PS3/Games/Uncharted 2.iso",              FileType = "iso",  SizeBytes = 25_000_000_000L },
            new() { Title = "Breath of the Wild", Platform = "Switch",   FilePath = "/Roms/Switch/Games/Breath of the Wild.nsp",    FileType = "nsp",  SizeBytes = 14_200_000_000L },
            new() { Title = "Red Dead Redemption", Platform = "Xbox 360",FilePath = "/Roms/Xbox 360/Games/Red Dead Redemption.iso", FileType = "iso",  SizeBytes = 7_600_000_000L  },
        };

        // Push local data into LibraryViewModel directly (bypasses file-system scanner)
        LibraryVm.UpdateLocalGames(demoLocalGames);
        LibraryVm.UpdateRepacks(demoRepacks);
        LibraryVm.UpdateRoms(demoRoms);

        // ── Load child view models ─────────────────────────────────────────
        DashboardVm.Load(_profile, _library, _achievements);
        LibraryVm.Load(_library);
        StoreVm.Load(GameCatalog.Store, _library, _profile, _client, false);
        ProfileVm.Load(_profile, _library, _achievements, false);
        FriendsVm.LoadDemo();

        // Pre-fetch cover art for the unified My Games cards
        _ = EnrichMyGamesListAsync();

        // Open the inline conversation for screenshot purposes
        FriendsVm.OpenConversationDemo();

        // Skip the login screen
        ShowLogin = false;
        ShowMain  = true;
        ActivePage = "dashboard";
    }

    private void OnLoginSuccess(UserProfile profile, List<Game> library,
                                List<Achievement> achievements, bool isOffline)
    {
        _profile      = profile;
        _library      = library;
        _achievements = achievements;

        IsOfflineMode = isOffline;

        bool isAdmin = _client.IsAdmin;

        // Create the per-user data folder hierarchy beneath the executable
        UserDataService.CreateUserFolders(profile.Username);

        // Apply stored playtime data to the library so the dashboard shows accurate totals
        PlaytimeService.ApplyStoredPlaytime(library);

        // Cross-reference the user's unlocked achievements with their cloud library so that
        // opening any game detail view immediately shows the achievements they have earned
        // (covers Switch, PS3, Xbox 360, and all other platforms, not just PC/Steam).
        foreach (var game in library)
        {
            var matching = achievements
                .Where(a => string.Equals(a.Platform, game.Platform, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(a.GameTitle, game.Title,    StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matching.Count > 0)
                game.GameAchievements = matching;
        }

        // Save data to local cache so the next launch can restore it offline.
        // Only save when we actually fetched from the server (isOffline = false).
        if (!isOffline)
        {
            _offlineCache.Save(profile.Username, profile, library, achievements);

            // Flush any pending offline changes now that we are back online
            _ = FlushPendingChangesAsync(profile.Username);

            // Update presence immediately on login so the user appears “Online” to friends,
            // then start a background heartbeat that refreshes it every 2 minutes — mirroring
            // the web app's updatePresence() call at startup + setInterval every 2 minutes.
            _ = _client.UpdatePresenceAsync();
            StartPresenceHeartbeat();

            // Start periodic sync check (every 5 minutes while online): re-fetches remote
            // user data and invalidates stale Games.Database caches.
            StartSyncCheckTimer();
        }
        else
        {
            System.Diagnostics.Debug.WriteLine(
                $"[MainViewModel] Offline mode — loaded cached data for '{profile.Username}'.");

            // Start the reconnect timer so the app automatically transitions back online
            // as soon as a live server connection becomes available.
            StartOfflineReconnectTimer();
        }

        var localCards = LibraryVm.GetMyGameSources()
            .Select(s => LibraryVm.FindMyGameCard(s.Title, s.Platform))
            .Where(c => c != null)
            .Cast<LocalGameCardVm>()
            .ToList();

        DashboardVm.Load(profile, library, achievements, localCards);
        LibraryVm.Load(library);
        StoreVm.Load(GameCatalog.Store, library, profile, _client, isAdmin);
        ProfileVm.Load(profile, library, achievements, isAdmin);
        FriendsVm.Load(_client, profile.Username);

        if (!isOffline)
        {
            // Asynchronously enrich library games with cover/desc/trailer from Games.Database.
            _ = EnrichLibraryFromDatabaseAsync(library);

            // Pre-fetch cover art for the unified My Games cards (scanner may already
            // have found games before login, so enrich what's there right away).
            _ = EnrichMyGamesListAsync();

            // Start background message polling (checks for new DMs every 60 seconds)
            StartMessagePolling();
        }

        ShowLogin = false;
        ShowMain  = true;
        ActivePage = "dashboard";
    }

    /// <summary>
    /// Replays queued offline mutations (AddGame / RemoveGame) against the live
    /// backend once the user is back online.  Runs silently in the background.
    /// </summary>
    private async Task FlushPendingChangesAsync(string username)
    {
        if (!_pendingChanges.HasPending(username)) return;

        var changes = _pendingChanges.GetAll(username);
        System.Diagnostics.Debug.WriteLine(
            $"[PendingChanges] Flushing {changes.Count} offline change(s) for '{username}'.");

        bool allOk = true;
        foreach (var change in changes)
        {
            try
            {
                switch (change.Kind)
                {
                    case Services.PendingChangeKind.AddGame when change.GameData != null:
                        await _client.AddGameAsync(change.GameData);
                        System.Diagnostics.Debug.WriteLine(
                            $"[PendingChanges] Synced AddGame '{change.GameData.Title}'.");
                        break;

                    case Services.PendingChangeKind.RemoveGame
                        when change.Platform != null && change.Title != null:
                        await _client.RemoveGameAsync(change.Platform, change.Title);
                        System.Diagnostics.Debug.WriteLine(
                            $"[PendingChanges] Synced RemoveGame '{change.Title}'.");
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[PendingChanges] Failed to sync change ({change.Kind}): {ex.Message}");
                allOk = false;
            }
        }

        if (allOk)
        {
            _pendingChanges.Clear(username);
            System.Diagnostics.Debug.WriteLine(
                $"[PendingChanges] All changes synced and queue cleared for '{username}'.");
        }
    }

    /// <summary>
    /// Refreshes the dashboard's "Continue Playing" local games section after
    /// the scanner detects new ROMs or local games.
    /// </summary>
    private void RefreshDashboardLocalGames()
    {
        if (!ShowMain) return; // not logged in yet
        var localCards = LibraryVm.GetMyGameSources()
            .Select(s => LibraryVm.FindMyGameCard(s.Title, s.Platform))
            .Where(c => c != null)
            .Cast<LocalGameCardVm>()
            .ToList();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            DashboardVm.Load(_profile, _library, _achievements, localCards));
    }

    [RelayCommand]
    private void Navigate(string page)
    {
        // Auto-collapse the nav sidebar after a page is selected (console-like)
        IsNavExpanded = false;
        ShowDetail = false;
        ActivePage = page;
        if (page == "library")
            LibraryVm.Load(_library);
        if (page == "friends")
            FriendsVm.Load(_client, _profile.Username);
        if (page == "profile")
            ProfileVm.Load(_profile, _library, _achievements, _client.IsAdmin);
    }

    private void OpenDetailFromGame(Game game)
    {
        // Only match local PC games when the library game is also a PC game.
        // Without this check, a cloud library entry like "God of War" (PS4) would
        // incorrectly be shown as "installed" if the user has a PC folder called "God of War".
        LocalGame? localGame = string.Equals(game.Platform, "PC", StringComparison.OrdinalIgnoreCase)
            ? LibraryVm.LocalGames
                .FirstOrDefault(lg => lg.Title.Equals(game.Title, StringComparison.OrdinalIgnoreCase))
            : null;
        LocalRepack? repack = null;
        if (localGame == null && string.Equals(game.Platform, "PC", StringComparison.OrdinalIgnoreCase))
            repack = LibraryVm.ReadyToInstall
                .FirstOrDefault(r => r.Title.Equals(game.Title, StringComparison.OrdinalIgnoreCase));

        // For non-PC platforms, check whether a matching ROM is found on a local drive
        // so the detail view shows a Play button even when the game was added to the
        // cloud library manually via the web interface.
        LocalRom? localRom = null;
        if (localGame == null && repack == null &&
            !string.Equals(game.Platform, "PC", StringComparison.OrdinalIgnoreCase))
            localRom = FindMatchingRom(game.Title, game.Platform, game.TitleId);

        DetailVm.LoadFromGame(game, localGame, repack, localRom);
        ShowDetail = true;

        // Enrich achievements for non-PC library games whose AchievementsUrl may not
        // have been stored when the game was added to the library.
        if (!string.Equals(game.Platform, "PC", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrEmpty(game.AchievementsUrl))
            _ = EnrichGameAchievementsAsync(game);
    }

    private void OpenDetailFromStoreGame(StoreGame game)
    {
        // Only match local PC games when the store game is also a PC game.
        // Non-PC store games should never show a PC local game as "installed".
        LocalGame? localGame = string.Equals(game.Platform, "PC", StringComparison.OrdinalIgnoreCase)
            ? LibraryVm.LocalGames
                .FirstOrDefault(lg => lg.Title.Equals(game.Title, StringComparison.OrdinalIgnoreCase))
            : null;
        LocalRepack? repack = null;
        if (localGame == null && string.Equals(game.Platform, "PC", StringComparison.OrdinalIgnoreCase))
            repack = LibraryVm.ReadyToInstall
                .FirstOrDefault(r => r.Title.Equals(game.Title, StringComparison.OrdinalIgnoreCase));

        // For non-PC platforms, check whether a matching ROM is on a local drive
        LocalRom? localRom = null;
        if (localGame == null && repack == null &&
            !string.Equals(game.Platform, "PC", StringComparison.OrdinalIgnoreCase))
            localRom = FindMatchingRom(game.Title, game.Platform, null);

        DetailVm.LoadFromStoreGame(game, localGame, repack, localRom);
        ShowDetail = true;
    }

    /// <summary>
    /// Opens the friend-profile overlay for the specified username.
    /// Fetches the friend's profile and library from the backend, then displays
    /// them in the <see cref="FriendProfileVm"/> overlay — mirroring the web
    /// app navigating to <c>profile.html?user=username</c>.
    /// </summary>
    private void OpenFriendProfile(string friendUsername)
    {
        if (string.IsNullOrEmpty(friendUsername)) return;
        ViewingFriendName  = friendUsername;
        ShowFriendProfile  = true;
        // Load placeholder data immediately, then enrich asynchronously
        FriendProfileVm.LoadPlaceholder(friendUsername);
        _ = LoadFriendProfileAsync(friendUsername);
    }

    [RelayCommand]
    private void CloseFriendProfile()
    {
        ShowFriendProfile = false;
        ViewingFriendName = "";
    }

    private async Task LoadFriendProfileAsync(string friendUsername)
    {
        try
        {
            var profile  = await _client.GetFriendProfileAsync(friendUsername) ?? new UserProfile { Username = friendUsername };
            var games    = await _client.GetFriendGamesAsync(friendUsername);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                FriendProfileVm.Load(profile, games, new List<Achievement>(), false));
        }
        catch { /* best-effort — placeholder already visible */ }
    }

    private void OpenDetailFromLocalRom(LocalRom rom)
    {
        // Show ROM info immediately so the UI is responsive
        DetailVm.LoadFromLocalRom(rom);
        ShowDetail = true;

        // Asynchronously enrich with cover art / description / screenshots / achievements
        // from the platform-specific Games.Database (PS3, Switch, Xbox 360, etc.)
        // Pass TitleID for precise matching of PS3/PS4/Switch folder-named ROMs.
        _ = EnrichLocalGameDetailAsync(rom.Title, rom.Platform, rom.TitleId);
    }

    private void OpenDetailFromLocalGame(LocalGame game)
    {
        // Show basic info immediately so the UI is responsive
        DetailVm.LoadFromLocalGame(game);
        ShowDetail = true;

        // Asynchronously enrich with cover/description/trailer from Games.Database
        _ = EnrichLocalGameDetailAsync(game.Title, "PC");
    }

    private void OpenDetailFromLocalRepack(LocalRepack repack)
    {
        // Show repack info immediately so the UI is responsive
        DetailVm.LoadFromLocalRepack(repack);
        ShowDetail = true;

        // Asynchronously enrich with real cover art / description / screenshots
        // from the Games.Database — same enrichment as installed local games.
        // StripRepackMarkers is applied inside EnrichLocalGameDetailAsync via
        // FindDatabaseGame, so "[FitGirl Repack]" suffixes are stripped automatically.
        _ = EnrichLocalGameDetailAsync(repack.Title, "PC");
    }

    /// <summary>
    /// Opens the detail overlay for a card from the unified "My Games" section.
    /// Routes to the correct Load* method based on the card's source type, then
    /// enriches with real cover art / description / trailer / achievements from
    /// the Games.Database — the same data the website shows.
    /// </summary>
    private void OpenDetailFromMyGameCard(LocalGameCardVm card)
    {
        if (card.SourceGame != null)
        {
            OpenDetailFromLocalGame(card.SourceGame);
        }
        else if (card.SourceRepack != null)
        {
            OpenDetailFromLocalRepack(card.SourceRepack);
        }
        else if (card.SourceRom != null)
        {
            OpenDetailFromLocalRom(card.SourceRom);
        }
        else if (card.SourceCloudGame != null)
        {
            // Cloud library game shown in "Continue Playing" — open via standard cloud detail flow
            OpenDetailFromGame(card.SourceCloudGame);
        }
    }

    /// <summary>
    /// Launches the game for the given card directly (no detail overlay shown).
    /// Used by the "Continue Playing" hero button on the dashboard to skip the
    /// detail screen and start the last-played game immediately.
    /// </summary>
    private void LaunchFromCard(LocalGameCardVm card)
    {
        // Load the detail VM in background (no ShowDetail = true) then trigger launch.
        if (card.SourceGame != null)
        {
            DetailVm.LoadFromLocalGame(card.SourceGame);
        }
        else if (card.SourceRom != null)
        {
            DetailVm.LoadFromLocalRom(card.SourceRom);
        }
        else if (card.SourceRepack != null)
        {
            // Repacks are not yet installed — fall back to opening the detail overlay so
            // the user can select an install location.
            OpenDetailFromLocalRepack(card.SourceRepack);
            return;
        }
        else if (card.SourceCloudGame != null)
        {
            // Find the matching local copy (if any) so the detail VM has IsInstalled = true.
            var cg = card.SourceCloudGame;
            var localGame = LibraryVm.LocalGames
                .FirstOrDefault(lg => lg.Title.Equals(cg.Title, StringComparison.OrdinalIgnoreCase));
            var repack = localGame == null
                ? LibraryVm.ReadyToInstall
                    .FirstOrDefault(r => r.Title.Equals(cg.Title, StringComparison.OrdinalIgnoreCase))
                : null;
            var localRom = localGame == null && repack == null
                ? FindMatchingRom(cg.Title, cg.Platform, cg.TitleId)
                : null;
            DetailVm.LoadFromGame(cg, localGame, repack, localRom);
        }
        else
        {
            return;
        }

        // Launch immediately without showing the overlay
        if (DetailVm.IsInstalled)
            DetailVm.LaunchGameCommand.Execute(null);
        else
        {
            // Game is not installed — open detail so user can install it
            ShowDetail = true;
        }
    }

    /// <summary>
    /// Finds the first local ROM that matches the given title/platform/titleId combination.
    /// Used to detect whether a cloud library game (added via web) is also present as a
    /// ROM file on a local drive, so the detail view can show a Play button.
    /// </summary>
    private LocalRom? FindMatchingRom(string title, string platform, string? titleId)
    {
        string normalizedPlatform = Models.PlatformHelper.NormalizePlatform(platform);
        return LibraryVm.LocalRoms.FirstOrDefault(r =>
            string.Equals(r.Platform, normalizedPlatform, StringComparison.OrdinalIgnoreCase) &&
            (// 1. TitleID match (most precise — PS3/PS4/Switch folder-based ROMs)
             (!string.IsNullOrEmpty(titleId) &&
              string.Equals(r.TitleId, titleId, StringComparison.OrdinalIgnoreCase)) ||
             // 2. Exact title match
             string.Equals(r.Title, title, StringComparison.OrdinalIgnoreCase) ||
             // 3. Fuzzy match stripping trademark/copyright symbols
             string.Equals(
                 Models.PlatformHelper.StripSpecialSymbols(r.Title),
                 Models.PlatformHelper.StripSpecialSymbols(title),
                 StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Looks up <paramref name="game"/>'s achievements URL in the Games.Database and,
    /// if found, stores it on the game object and triggers achievement loading in the
    /// currently-open detail panel (if it's still showing this game).
    /// Called when a non-PC cloud library game is opened and its AchievementsUrl is empty —
    /// this covers games that were added before achievementsUrl was stored in the library.
    /// </summary>
    private async Task EnrichGameAchievementsAsync(Game game)
    {
        try
        {
            var dbGames = await GameOsClient.FetchGamesDatabaseAsync(game.Platform);
            var dbGame  = FindDatabaseGame(dbGames, game.Title, game.TitleId);
            if (string.IsNullOrEmpty(dbGame?.AchievementsUrl)) return;

            // Persist for future opens within this session
            game.AchievementsUrl = dbGame.AchievementsUrl;

            // If the detail panel is still showing this game, trigger achievement loading
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (!DetailVm.HasAchievements &&
                    string.Equals(DetailVm.Title, game.Title, StringComparison.OrdinalIgnoreCase))
                    _ = DetailVm.FetchAndDisplayAchievementsAsync(dbGame.AchievementsUrl!);
            });
        }
        catch { /* best-effort */ }
    }


    /// "My Games" list from the Games.Database.  Groups cards by platform to
    /// minimise API calls; results are cached on disk (24 h TTL) so subsequent
    /// launches are instant.  Updates each <see cref="LocalGameCardVm.CoverUrl"/>
    /// on the UI thread so card images appear progressively as data loads.
    /// Works for both real scan mode and demo mode.
    /// </summary>
    private async Task EnrichMyGamesListAsync()
    {
        try
        {
            // Collect unique platforms from all current My Games cards
            var sources = LibraryVm.GetMyGameSources();
            if (sources.Count == 0) return;

            var byPlatform = sources
                .GroupBy(s => s.Platform, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(s => (s.Title, s.TitleId)).ToList(),
                    StringComparer.OrdinalIgnoreCase);

            foreach (var (platform, entries) in byPlatform)
            {
                try
                {
                    var dbGames = await GameOsClient.FetchGamesDatabaseAsync(platform);
                    if (dbGames.Count == 0) continue;

                    foreach (var (title, titleId) in entries)
                    {
                        var db = FindDatabaseGame(dbGames, title, titleId);
                        if (db == null) continue;

                        var card = LibraryVm.FindMyGameCard(title, platform);
                        if (card == null) continue;

                        // Resolve the real game title for TitleID-based ROM cards
                        // (e.g. a PS4 folder named "CUSA00572" → "God of War Ragnarök").
                        // This must be updated regardless of whether we have a cover URL.
                        string? realTitle = (!string.IsNullOrEmpty(titleId) &&
                                             db.Title != null &&
                                             !string.Equals(card.Title, db.Title, StringComparison.OrdinalIgnoreCase))
                                            ? db.Title : null;

                        string? coverUrl = string.IsNullOrEmpty(card.CoverUrl)
                                           ? db.CoverUrl : null;

                        // Only post an update if there is actually something new to set
                        if (coverUrl == null && realTitle == null) continue;

                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            if (!string.IsNullOrEmpty(coverUrl))
                                card.CoverUrl = coverUrl;

                            if (!string.IsNullOrEmpty(realTitle))
                                card.DisplayTitle = realTitle;
                        });
                    }
                }
                catch { /* best-effort per platform */ }
            }
        }
        catch { /* best-effort — cards already show gradient placeholder */ }
    }

    /// <summary>
    /// Looks up <paramref name="localTitle"/> in the specified platform's Games.Database and,
    /// if found, enriches the currently-open detail panel with cover, description, trailer,
    /// screenshots and achievements — the same data shown on the website.
    /// Title matching handles Windows-safe folder names such as
    /// "Call of Duty - Black Ops II" → "Call of Duty: Black Ops II".
    /// </summary>
    private async Task EnrichLocalGameDetailAsync(string localTitle, string platform,
                                                   string? titleId = null)
    {
        try
        {
            var dbGames = await GameOsClient.FetchGamesDatabaseAsync(platform);
            var dbGame  = FindDatabaseGame(dbGames, localTitle, titleId);
            if (dbGame != null)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => DetailVm.EnrichFromDatabaseGame(dbGame));
            }
        }
        catch { /* best-effort — basic info already displayed */ }
    }

    /// <summary>
    /// After login, enriches library games that are missing cover art / description /
    /// trailer with data from the public Games.Database — mirrors the web app's
    /// <c>openGameModalFromLibrary → fetchGamesDbPlatform(platform)</c> flow.
    /// Groups games by platform to minimise API calls; results are cached on disk
    /// so subsequent launches are instant.
    /// </summary>
    private async Task EnrichLibraryFromDatabaseAsync(List<Game> library)
    {
        // Enrich games that are missing any visual metadata or the achievements URL
        var toEnrich = library
            .Where(g => string.IsNullOrEmpty(g.CoverUrl)
                     || string.IsNullOrEmpty(g.Description)
                     || string.IsNullOrEmpty(g.AchievementsUrl))
            .ToList();

        if (toEnrich.Count == 0) return;

        // Group by platform so we fetch each platform database at most once
        var platforms = toEnrich
            .Select(g => g.Platform)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var platform in platforms)
        {
            try
            {
                var dbGames = await GameOsClient.FetchGamesDatabaseAsync(platform);
                if (dbGames.Count == 0) continue;

                var platformGames = toEnrich
                    .Where(g => string.Equals(g.Platform, platform, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                bool anyUpdated = false;
                foreach (var game in platformGames)
                {
                    var dbGame = FindDatabaseGame(dbGames, game.Title, game.TitleId);
                    if (dbGame == null) continue;

                    // Only fill in fields that are still empty
                    if (string.IsNullOrEmpty(game.CoverUrl) && !string.IsNullOrEmpty(dbGame.CoverUrl))
                    { game.CoverUrl = dbGame.CoverUrl; anyUpdated = true; }
                    if (string.IsNullOrEmpty(game.Description) && !string.IsNullOrEmpty(dbGame.Description))
                    { game.Description = dbGame.Description; anyUpdated = true; }
                    if (string.IsNullOrEmpty(game.TrailerUrl) && !string.IsNullOrEmpty(dbGame.TrailerUrl))
                    { game.TrailerUrl = dbGame.TrailerUrl; anyUpdated = true; }
                    if ((game.Screenshots == null || game.Screenshots.Count == 0)
                        && dbGame.Screenshots != null && dbGame.Screenshots.Count > 0)
                    { game.Screenshots = dbGame.Screenshots; anyUpdated = true; }
                    if (!string.IsNullOrEmpty(dbGame.TitleId) && string.IsNullOrEmpty(game.TitleId))
                    { game.TitleId = dbGame.TitleId; anyUpdated = true; }
                    if (string.IsNullOrEmpty(game.Genre) && !string.IsNullOrEmpty(dbGame.Genre))
                    { game.Genre = dbGame.Genre; anyUpdated = true; }
                    // Enrich achievements URL so non-PC games show achievements when opened
                    if (string.IsNullOrEmpty(game.AchievementsUrl) && !string.IsNullOrEmpty(dbGame.AchievementsUrl))
                    { game.AchievementsUrl = dbGame.AchievementsUrl; anyUpdated = true; }
                }

                // Refresh the library UI once per platform batch if anything changed
                if (anyUpdated)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        LibraryVm.Load(_library);
                        DashboardVm.Load(_profile, _library, _achievements);
                    });
                }
            }
            catch { /* best-effort — library is still usable without enrichment */ }
        }
    }

    /// <summary>
    /// Tries to match a local game folder title against the Games.Database.
    /// Attempts (in order):
    ///   1. TitleID lookup (most precise).
    ///   2. Exact case-insensitive match.
    ///   3. After removing trademark/copyright symbols (™, ®, ©).
    ///   4. After removing [Repack] / [repack] style markers.
    ///   5. After applying NormalizeGameTitle (Windows " - " → ": " and HTML entities).
    ///   6. After applying both stripping and normalisation.
    ///   7. Against each game's AlternateNames list.
    /// </summary>
    private static DatabaseGame? FindDatabaseGame(List<DatabaseGame> dbGames, string localTitle)
        => FindDatabaseGame(dbGames, localTitle, null);

    private static DatabaseGame? FindDatabaseGame(List<DatabaseGame> dbGames, string localTitle, string? titleId)
    {
        // 0. TitleID lookup (most precise — works for PS3/PS4/Switch folder-named games)
        if (!string.IsNullOrEmpty(titleId))
        {
            var byTitleId = dbGames.FirstOrDefault(g =>
                string.Equals(g.TitleId, titleId, StringComparison.OrdinalIgnoreCase));
            if (byTitleId != null) return byTitleId;
        }

        // 1. Exact case-insensitive match
        var exact = dbGames.FirstOrDefault(g =>
            string.Equals(g.Title, localTitle, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        // 2. Strip trademark/copyright/registered symbols (e.g. "Mario Kart™ 8 Deluxe" → "Mario Kart 8 Deluxe")
        string noSymbols = PlatformHelper.StripSpecialSymbols(localTitle);
        if (!string.Equals(noSymbols, localTitle, StringComparison.Ordinal))
        {
            var byNoSymbols = dbGames.FirstOrDefault(g =>
                string.Equals(PlatformHelper.StripSpecialSymbols(g.Title ?? ""), noSymbols, StringComparison.OrdinalIgnoreCase));
            if (byNoSymbols != null) return byNoSymbols;
        }

        // 3. Strip [Repack] / [repack] / "[FitGirl Repack]" style suffixes
        string stripped = StripRepackMarkers(localTitle);
        if (!string.Equals(stripped, localTitle, StringComparison.Ordinal))
        {
            var byStripped = dbGames.FirstOrDefault(g =>
                string.Equals(g.Title, stripped, StringComparison.OrdinalIgnoreCase));
            if (byStripped != null) return byStripped;
        }

        // 4. Normalise Windows-safe title separators and HTML entities
        string normalized = NormalizeGameTitle(localTitle);
        if (!string.Equals(normalized, localTitle, StringComparison.Ordinal))
        {
            var byNorm = dbGames.FirstOrDefault(g =>
                string.Equals(g.Title, normalized, StringComparison.OrdinalIgnoreCase));
            if (byNorm != null) return byNorm;
        }

        // 5. Try stripping + normalising together
        string strippedNorm = NormalizeGameTitle(stripped);
        if (!string.Equals(strippedNorm, localTitle, StringComparison.Ordinal))
        {
            var byStrippedNorm = dbGames.FirstOrDefault(g =>
                string.Equals(g.Title, strippedNorm, StringComparison.OrdinalIgnoreCase));
            if (byStrippedNorm != null) return byStrippedNorm;
        }

        // 6. Match against AlternateNames (e.g. "GoW" → "God of War", "TLOU2" → "The Last of Us Part II")
        var byAltName = dbGames.FirstOrDefault(g =>
            g.AlternateNames != null &&
            g.AlternateNames.Any(alt =>
                string.Equals(alt, localTitle,  StringComparison.OrdinalIgnoreCase) ||
                string.Equals(alt, noSymbols,   StringComparison.OrdinalIgnoreCase) ||
                string.Equals(alt, stripped,    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(alt, normalized,  StringComparison.OrdinalIgnoreCase)));
        if (byAltName != null) return byAltName;

        return null;
    }

    /// <summary>
    /// Removes common repack annotation patterns from a folder/file name so
    /// the clean game title can be matched against the Games.Database.
    /// Examples:
    ///   "Call of Duty [Repack]"         → "Call of Duty"
    ///   "The Witcher 3 [FitGirl Repack]"→ "The Witcher 3"
    ///   "[Repack] Cyberpunk 2077"        → "Cyberpunk 2077"
    /// </summary>
    internal static string StripRepackMarkers(string title)
    {
        if (string.IsNullOrEmpty(title)) return title;
        return _repackMarkerRegex.Replace(title, "").Trim();
    }

    // Matches "[Repack]", "[FitGirl Repack]", "[DODI Repack]", etc. (case-insensitive)
    private static readonly Regex _repackMarkerRegex =
        new(@"\[[\w\s]*[Rr]epack[\w\s]*\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Converts a Windows folder-safe game name to its canonical form.
    /// Windows folder names cannot contain ":" so installers often replace
    /// "Franchise: Subtitle" with "Franchise - Subtitle".
    /// This method reverses that substitution so the database lookup succeeds
    /// and the correct title is displayed in the UI.
    /// Only the first " - " separator is replaced (non-greedy) to preserve any
    /// additional dashes in the subtitle (e.g. "Game - Part 1 - Episode 2"
    /// becomes "Game: Part 1 - Episode 2").
    /// Also decodes HTML entities such as &amp;#39; → ' and &amp;amp; → &amp;
    /// </summary>
    internal static string NormalizeGameTitle(string title)
    {
        if (string.IsNullOrEmpty(title)) return title;
        // Replace only the first " - " with ": " to reconstruct subtitle separators
        string result = _titleNormalizeRegex.Replace(title, "$1: $2");
        // Decode HTML entities that sometimes appear in database titles
        result = WebUtility.HtmlDecode(result);
        return result;
    }

    // Compiled once for the process lifetime (called on every local game detail open)
    private static readonly Regex _titleNormalizeRegex =
        new(@"^(.+?) - (.+)$", RegexOptions.Compiled);

    [RelayCommand]
    private void SignOut()
    {
        // Stop all background timers before clearing credentials
        _messagePoller?.Dispose();
        _messagePoller = null;
        _lastKnownMessageAt.Clear();

        _presenceTimer?.Dispose();
        _presenceTimer = null;

        StopOfflineReconnectTimer();
        StopSyncCheckTimer();

        // Clear the saved token so the next launch shows the login form
        // (equivalent to the web calling localStorage.removeItem('gameOSUser'))
        if (_client.LoggedInUser != null)
            _sessionCache.ClearToken(_client.LoggedInUser);

        _client.Logout();
        _library      = new();
        _achievements = new();
        _profile      = new();

        // Refresh login screen with up-to-date saved accounts and offline profiles
        LoginVm.RefreshForAccountSwitch();

        LoginVm.Username = "";
        LoginVm.Password = "";
        LoginVm.ErrorMessage = "";
        LoginVm.ShowRegister = false;

        ShowMain  = false;
        ShowDetail = false;
        ShowLogin = true;
        IsOfflineMode = false;
    }

    /// <summary>
    /// Returns to the accounts / login section without clearing offline caches so
    /// that all previously-logged-in profiles remain available for offline selection.
    /// Unlike <see cref="SignOut"/>, this does not clear the saved token — the current
    /// session can be silently restored if the user selects the same account again.
    /// </summary>
    [RelayCommand]
    private void SwitchAccount()
    {
        // Stop all background timers
        _messagePoller?.Dispose();
        _messagePoller = null;
        _lastKnownMessageAt.Clear();

        _presenceTimer?.Dispose();
        _presenceTimer = null;

        StopOfflineReconnectTimer();
        StopSyncCheckTimer();

        // Keep the session token so the account can be silently restored; only clear
        // in-memory state so the launcher returns to the account selection screen.
        _client.Logout();
        _library      = new();
        _achievements = new();
        _profile      = new();

        // Refresh login screen showing all available accounts (session + offline profiles)
        LoginVm.RefreshForAccountSwitch();

        LoginVm.Username = "";
        LoginVm.Password = "";
        LoginVm.ErrorMessage = "";
        LoginVm.ShowRegister = false;

        ShowMain  = false;
        ShowDetail = false;
        ShowLogin = true;
        IsOfflineMode = false;
    }

    public void Dispose()
    {
        _messagePoller?.Dispose();
        _messagePoller = null;
        _presenceTimer?.Dispose();
        _presenceTimer = null;
        StopOfflineReconnectTimer();
        StopSyncCheckTimer();
        _scanner.Dispose();
        (_client as IDisposable)?.Dispose();
        StoreVm.Dispose();
        _playtimeSvc.Dispose();
    }

    // ── Presence heartbeat ─────────────────────────────────────────────────────

    /// <summary>
    /// Starts a background timer that refreshes the logged-in user's presence
    /// timestamp every 2 minutes, mirroring the web app's heartbeat:
    /// <code>setInterval(() =&gt; updatePresence(username), 2 * 60 * 1000)</code>
    /// A friend is considered Online when their lastSeen is less than 5 minutes old.
    /// The timer fires for the first time after 2 minutes (the initial call happens
    /// immediately in <see cref="OnLoginSuccess"/> before this method is invoked).
    /// </summary>
    private void StartPresenceHeartbeat()
    {
        _presenceTimer?.Dispose();
        _presenceTimer = new Timer(_ =>
        {
            Task.Run(async () =>
            {
                try   { await _client.UpdatePresenceAsync().ConfigureAwait(false); }
                catch { /* presence update failure is non-fatal */ }
            });
        }, null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));
    }

    // ── Offline reconnect timer ───────────────────────────────────────────────

    /// <summary>Interval used for both the offline reconnect timer and the online sync-check timer.</summary>
    private static readonly TimeSpan PeriodicCheckInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Starts a background timer that fires every 5 minutes while the app is in Offline
    /// Mode.  On each tick it performs a lightweight connectivity check; if the server is
    /// reachable again the session is restored, local caches are refreshed, pending changes
    /// are flushed, and the timer is stopped.
    /// </summary>
    private void StartOfflineReconnectTimer()
    {
        _offlineReconnectTimer?.Dispose();
        _offlineReconnectTimer = new Timer(_ =>
        {
            Task.Run(async () =>
            {
                try   { await TryReconnectAsync().ConfigureAwait(false); }
                catch { /* reconnect attempt failure is non-fatal */ }
            });
        }, null, PeriodicCheckInterval, PeriodicCheckInterval);

        System.Diagnostics.Debug.WriteLine(
            "[Reconnect] Offline reconnect timer started (every 5 minutes).");
    }

    private void StopOfflineReconnectTimer()
    {
        _offlineReconnectTimer?.Dispose();
        _offlineReconnectTimer = null;
    }

    /// <summary>
    /// Called by the offline reconnect timer.  Checks server connectivity; if online,
    /// restores the session, re-fetches user data, saves the updated local cache, flushes
    /// the pending-changes queue, and starts online-only background services.
    /// </summary>
    private async Task TryReconnectAsync()
    {
        if (!IsOfflineMode) return;

        var username = _profile?.Username;
        if (string.IsNullOrEmpty(username)) return;

        System.Diagnostics.Debug.WriteLine(
            $"[Reconnect] Checking connectivity for '{username}'...");

        try
        {
            // Lightweight connectivity check — does not transfer user data
            bool connected = await _client.CheckHealthAsync().ConfigureAwait(false);
            if (!connected)
            {
                System.Diagnostics.Debug.WriteLine("[Reconnect] Still offline — no server response.");
                return;
            }

            // Server is reachable — try to restore the session using the saved token
            var session = _sessionCache.GetSession(username);
            var token   = session?.Token ?? "";

            UserProfile profile;
            if (!string.IsNullOrEmpty(token))
            {
                profile = await _client.RestoreSessionAsync(token, username).ConfigureAwait(false);
            }
            else
            {
                // No saved token — server is reachable but we cannot silently re-auth.
                // Transition out of offline mode and let the user sign in again.
                System.Diagnostics.Debug.WriteLine(
                    "[Reconnect] Server reachable but no saved token — returning to login.");
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    StopOfflineReconnectTimer();
                    IsOfflineMode = false;
                });
                return;
            }

            var games        = await _client.GetGamesAsync().ConfigureAwait(false);
            var achievements = await _client.GetAchievementsAsync().ConfigureAwait(false);

            System.Diagnostics.Debug.WriteLine(
                $"[Reconnect] Online! Restored session for '{username}'. " +
                $"Fetched {games.Count} games, {achievements.Count} achievements.");

            // Enrich platforms
            foreach (var g in games)
                g.Platform = Models.PlatformHelper.NormalizePlatform(g.Platform);

            // Update in-memory state and save fresh cache on UI thread
            var capturedProfile = profile;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _profile      = capturedProfile;
                _library      = games;
                _achievements = achievements;
                IsOfflineMode = false;

                DashboardVm.Load(_profile, _library, _achievements);
                LibraryVm.Load(_library);
                StoreVm.Load(GameCatalog.Store, _library, _profile, _client, _client.IsAdmin);
                ProfileVm.Load(_profile, _library, _achievements, _client.IsAdmin);
            });

            // Persist fresh cache so the next offline session is up to date
            _offlineCache.Save(username, profile, games, achievements);

            // Flush offline mutations now that we are back online
            _ = FlushPendingChangesAsync(username);

            // Stop reconnect timer and start online-only services
            StopOfflineReconnectTimer();
            _ = _client.UpdatePresenceAsync();
            StartPresenceHeartbeat();
            StartSyncCheckTimer();
            StartMessagePolling();

            // Enrich library in the background
            _ = EnrichLibraryFromDatabaseAsync(games);

            System.Diagnostics.Debug.WriteLine(
                $"[Reconnect] Transitioned to online mode for '{username}'.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[Reconnect] Attempt failed: {ex.Message}");
        }
    }

    // ── Periodic online sync-check timer ──────────────────────────────────────

    /// <summary>
    /// Starts a background timer that fires every 5 minutes while the app is Online.
    /// On each tick it checks whether the remote user data or Games.Database files have
    /// changed; if so the local caches are updated and the UI is refreshed.
    /// </summary>
    private void StartSyncCheckTimer()
    {
        _syncCheckTimer?.Dispose();
        _syncCheckTimer = new Timer(_ =>
        {
            Task.Run(async () =>
            {
                try   { await TryRefreshUserDataAsync().ConfigureAwait(false); }
                catch { /* sync check failure is non-fatal */ }
            });
        }, null, PeriodicCheckInterval, PeriodicCheckInterval);

        System.Diagnostics.Debug.WriteLine(
            "[SyncCheck] Online sync-check timer started (every 5 minutes).");
    }

    private void StopSyncCheckTimer()
    {
        _syncCheckTimer?.Dispose();
        _syncCheckTimer = null;
    }

    /// <summary>
    /// Called by the online sync-check timer.  Re-fetches the logged-in user's games
    /// and achievements from the server; if they differ from the current in-memory data
    /// the local cache and UI are updated.  Also triggers a Games.Database staleness check.
    /// If the network is unavailable, the app transitions to Offline Mode.
    /// </summary>
    private async Task TryRefreshUserDataAsync()
    {
        if (IsOfflineMode) return;

        var username = _profile?.Username;
        if (string.IsNullOrEmpty(username)) return;

        System.Diagnostics.Debug.WriteLine(
            $"[SyncCheck] Checking for remote userdata updates for '{username}'...");

        try
        {
            var games        = await _client.GetGamesAsync().ConfigureAwait(false);
            var achievements = await _client.GetAchievementsAsync().ConfigureAwait(false);

            bool gamesChanged = !GamesListEqual(_library, games);
            bool achvChanged  = achievements.Count != _achievements.Count;

            if (gamesChanged || achvChanged)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[SyncCheck] Remote userdata changed for '{username}' " +
                    $"(games: {(gamesChanged ? "changed" : "same")}, " +
                    $"achievements: {(achvChanged ? "changed" : "same")}). Updating local cache.");

                foreach (var g in games)
                    g.Platform = Models.PlatformHelper.NormalizePlatform(g.Platform);

                _library      = games;
                _achievements = achievements;

                _offlineCache.Save(username, _profile ?? new Models.UserProfile(), games, achievements);

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    DashboardVm.Load(_profile ?? new Models.UserProfile(), _library, _achievements);
                    LibraryVm.Load(_library);
                });
            }
            else
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[SyncCheck] Remote userdata unchanged for '{username}' — skipping update.");
            }

            // Also check Games.Database for updated platform JSON files (respects AutoUpdate preference)
            if (Services.AppSettingsService.Load().AutoUpdate)
                _ = Services.GitHubDataService.CheckForUpdatesAsync();
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            // Network lost — transition to Offline Mode and start the reconnect timer
            System.Diagnostics.Debug.WriteLine(
                $"[SyncCheck] Network lost: {ex.Message}. Switching to Offline Mode.");

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                IsOfflineMode = true;
                StopSyncCheckTimer();
                _messagePoller?.Dispose();
                _messagePoller = null;
                _presenceTimer?.Dispose();
                _presenceTimer = null;
                StartOfflineReconnectTimer();
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SyncCheck] Sync check failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Compares two game lists by title+platform set membership.
    /// Returns <c>true</c> when both lists contain exactly the same games.
    /// </summary>
    private static bool GamesListEqual(List<Game> a, List<Game> b)
    {
        if (a.Count != b.Count) return false;
        var aSet = new HashSet<string>(
            a.Select(g => $"{g.Platform?.ToLowerInvariant()}|{g.Title?.ToLowerInvariant()}"),
            StringComparer.Ordinal);
        return b.All(g => aSet.Contains(
            $"{g.Platform?.ToLowerInvariant()}|{g.Title?.ToLowerInvariant()}"));
    }

    // ── Message polling & OS notifications ────────────────────────────────────

    /// <summary>Maximum number of friends to poll per cycle to avoid excessive API calls.</summary>
    private const int MaxFriendsToPoll = 20;

    /// <summary>
    /// Starts a background timer that polls for new direct messages every 60 seconds.
    /// Fires an OS notification whenever a new message arrives from a friend.
    /// </summary>
    private void StartMessagePolling()
    {
        _messagePoller?.Dispose();
        _lastKnownMessageAt.Clear();
        _messagePoller = new Timer(_ =>
        {
            // Fire-and-forget with structured exception handling
            Task.Run(async () =>
            {
                try   { await PollMessagesAsync().ConfigureAwait(false); }
                catch { /* polling failure is non-fatal */ }
            });
        }, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
    }

    /// <summary>
    /// Fetches recent messages from online friends and fires a notification for any
    /// messages that arrived since the last poll.  Suppresses notifications for the
    /// conversation that is currently open in the Friends view.
    /// </summary>
    private async Task PollMessagesAsync()
    {
        if (!_client.IsAuthenticated) return;

        try
        {
            // Collect online friend usernames to poll (limit to avoid excessive API calls)
            var friends = FriendsVm.OnlineFriends
                .Concat(FriendsVm.OfflineFriends)
                .Select(f => f.Username)
                .Take(MaxFriendsToPoll)
                .ToList();

            foreach (var friendUsername in friends)
            {
                try
                {
                    var messages = await _client.GetMessagesAsync(friendUsername);
                    if (messages.Count == 0) continue;

                    // The most recent message in the conversation
                    var latest = messages
                        .OrderByDescending(m => m.SentAt)
                        .FirstOrDefault();

                    if (latest == null) continue;

                    // Only surface messages sent by the friend (not the logged-in user)
                    if (!string.Equals(latest.From, friendUsername, StringComparison.OrdinalIgnoreCase))
                        continue;

                    _lastKnownMessageAt.TryGetValue(friendUsername, out var lastSeen);

                    bool isNew = string.IsNullOrEmpty(lastSeen) ||
                                 string.Compare(latest.SentAt, lastSeen, StringComparison.Ordinal) > 0;

                    if (isNew)
                    {
                        _lastKnownMessageAt[friendUsername] = latest.SentAt;

                        // Skip notification if this conversation is currently open
                        bool conversationOpen =
                            string.Equals(FriendsVm.ConversationFriend, friendUsername,
                                          StringComparison.OrdinalIgnoreCase) &&
                            FriendsVm.ShowConversation;

                        // Skip notification if this is the very first poll (seed phase)
                        bool seedPhase = string.IsNullOrEmpty(lastSeen);

                        if (!conversationOpen && !seedPhase)
                        {
                            string preview = latest.Text.Length > 80
                                ? latest.Text[..80] + "…"
                                : latest.Text;
                            NotificationService.ShowMessageNotification(friendUsername, preview);
                        }
                    }
                }
                catch { /* best-effort per friend */ }
            }
        }
        catch { /* best-effort — polling failure is non-fatal */ }
    }
}
