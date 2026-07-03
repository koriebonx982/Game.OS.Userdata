using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
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
    private const string DefaultBrowserUrl = "https://www.google.com";
    private readonly GameOsClient            _client;
    private readonly GameScannerService      _scanner;
    private readonly SessionCacheService     _sessionCache;
    private readonly OfflineDataCacheService _offlineCache   = new();
    private readonly PendingChangesService   _pendingChanges = new();
    private readonly PlaytimeService         _playtimeSvc    = new();

    /// <summary>
    /// Per-system metadata cache — shared across all logged-in accounts on this machine.
    /// Initialised at startup (no username required) since covers and achievements are
    /// the same data regardless of which user is signed in.
    /// </summary>
    private readonly GameMetadataCacheService _metadataCache = new GameMetadataCacheService();

    /// <summary>Timestamp of the last successful periodic sync (used by the Settings label).</summary>
    private DateTime _lastSyncedAt = DateTime.MinValue;
    private readonly SemaphoreSlim _manualSyncSemaphore = new(1, 1);
    private readonly SemaphoreSlim _syncRefreshSemaphore = new(1, 1);
    private CancellationTokenSource? _manualSyncCts;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _exophasePollers =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ExophasePollInterval = TimeSpan.FromMinutes(1);
    private int _startupBegun;

    // ── Sync status (shown in the bottom-right overlay) ────────────────────
    /// <summary>True while any background metadata cache task is running.</summary>
    [ObservableProperty] private bool   _isCachingGames;
    /// <summary>Human-readable description of the game currently being cached, e.g. "Caching The Witcher 3…".</summary>
    [ObservableProperty] private string _cacheSyncLabel = "";

    /// <summary>
    /// Reference count of concurrent background cache tasks.
    /// The sync indicator is shown while this is &gt; 0 and hidden when it reaches 0.
    /// Incremented/decremented via <see cref="System.Threading.Interlocked"/> for thread safety.
    /// </summary>
    private int _cacheTaskCount = 0;
    private readonly object _cacheLabelGate = new();
    private long _lastCacheLabelUpdatedAt;
    private string _lastCacheLabelText = "";

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

    // ── Friend presence poller ─────────────────────────────────────────────
    /// <summary>Fires every 2 minutes to detect when friends come online or start a game.</summary>
    private Timer? _friendPresencePoller;
    /// <summary>Last known presence per friend: username → (isOnline, currentGame).</summary>
    private readonly Dictionary<string, (bool IsOnline, string? CurrentGame)> _lastKnownFriendPresence =
        new(StringComparer.OrdinalIgnoreCase);
    /// <summary>True once the first friend-presence poll has completed (avoids false "online" bursts at login).</summary>
    private bool _friendPresenceInitialized;

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

    // ── Sync-signal heartbeat poller ───────────────────────────────────────
    /// <summary>
    /// Fires every 30 seconds while the app is Online.  Reads the tiny
    /// sync-signal.json file (written by Device A when a play session ends) and,
    /// if the <c>lastActivityAt</c> timestamp has advanced, immediately calls
    /// <see cref="TryRefreshUserDataAsync"/> and <see cref="ApplyCloudPlaytimeAsync"/>
    /// so recently-played / playtime reflect the other device without waiting up
    /// to 5 minutes for the full sync-check timer to fire.
    /// </summary>
    private Timer? _heartbeatPoller;
    /// <summary>Last <c>lastActivityAt</c> value read from sync-signal.json.</summary>
    private string? _lastKnownSyncSignal;

    // ── Window minimize/restore actions (wired by MainWindow code-behind) ──
    /// <summary>
    /// Invoked on the UI thread when a game launches and the "Minimize on Game Launch"
    /// setting is enabled.  MainWindow subscribes to this to minimise the window.
    /// </summary>
    public Action? MinimizeWindowRequested { get; set; }

    /// <summary>
    /// Invoked on the UI thread when a tracked game exits and the "Minimize on Game Launch"
    /// setting is enabled.  MainWindow subscribes to this to restore the window.
    /// </summary>
    public Action? RestoreWindowRequested { get; set; }

    /// <summary>
    /// Invoked when the Quick Menu guide requests the launcher be brought to the foreground
    /// (e.g. pressing Home).  Unlike <see cref="RestoreWindowRequested"/>, this only
    /// unminimizes when necessary and never resizes an already-visible fullscreen window.
    /// </summary>
    public Action? BringToForegroundRequested { get; set; }

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
    public InboxViewModel     InboxVm          { get; }
    public SettingsViewModel  SettingsVm       { get; }
    public GameDetailViewModel DetailVm        { get; }
    public QuickMenuViewModel  QuickMenuVm     { get; }

    // ── Navigation state ───────────────────────────────────────────────────
    [ObservableProperty] private bool _showLogin         = true;
    [ObservableProperty] private bool _showMain          = false;
    [ObservableProperty] private bool _showDetail        = false;
    [ObservableProperty] private bool _showFriendProfile = false;
    [ObservableProperty] private string _activePage      = "dashboard";
    /// <summary>True when the left nav sidebar overlay is visible.</summary>
    [ObservableProperty] private bool _isNavExpanded     = false;
    /// <summary>True when the Quick Menu overlay (Shift+Ctrl) is visible.</summary>
    [ObservableProperty] private bool _showQuickMenu     = false;
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
    /// <summary>True when the Inbox page is active.</summary>
    public bool IsInbox       => ActivePage == "inbox";
    /// <summary>True when the Friends or Inbox page is active (both show FriendsView).</summary>
    public bool IsFriendsOrInbox => ActivePage == "friends" || ActivePage == "inbox";

    partial void OnActivePageChanged(string value)
    {
        OnPropertyChanged(nameof(IsHome));
        OnPropertyChanged(nameof(IsLibrary));
        OnPropertyChanged(nameof(IsStore));
        OnPropertyChanged(nameof(IsProfile));
        OnPropertyChanged(nameof(IsFriends));
        OnPropertyChanged(nameof(IsSettings));
        OnPropertyChanged(nameof(IsInbox));
        OnPropertyChanged(nameof(IsFriendsOrInbox));
    }

    private void TryLaunchAcceptedInvite(GameInvite invite)
    {
        if (invite == null || string.IsNullOrWhiteSpace(invite.GameName))
            return;

        string invitePlatform = string.IsNullOrWhiteSpace(invite.Platform) ? "PC" : invite.Platform;
        string targetPlatform = PlatformHelper.NormalizePlatform(invitePlatform);
        string targetTitleKey = PlatformHelper.NormalizeTitleForComparison(invite.GameName);

        var card = GetDashboardCards().FirstOrDefault(c =>
        {
            string cardPlatform = PlatformHelper.NormalizePlatform(c.Platform);
            string cardTitle = string.IsNullOrWhiteSpace(c.EffectiveTitle) ? c.Title : c.EffectiveTitle;
            return string.Equals(cardPlatform, targetPlatform, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(
                       PlatformHelper.NormalizeTitleForComparison(cardTitle),
                       targetTitleKey,
                       StringComparison.OrdinalIgnoreCase);
        });

        if (card == null)
        {
            // Game is not installed — inform the user and navigate to the store
            string connInfo = string.IsNullOrWhiteSpace(invite.ConnectionType)
                ? ""
                : $" via {invite.ConnectionType}";
            NotificationService.ShowDeveloperNotification(
                $"Please install {invite.GameName} first",
                $"Invited by {invite.From} to play {invite.GameName} ({invite.Platform}){connInfo}");

            // Pre-populate the store search so the user can find the game quickly
            StoreVm.SearchText = invite.GameName;
            Navigate("store");
            return;
        }

        // Game found — notify the user about the connection method then launch
        if (!string.IsNullOrWhiteSpace(invite.ConnectionType))
        {
            NotificationService.ShowDeveloperNotification(
                $"Joining {invite.From}'s game",
                $"{invite.GameName} · {invite.Platform} · {invite.ConnectionType}");
        }

        LaunchFromCard(card);
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
        InboxVm        = new InboxViewModel();
        SettingsVm     = new SettingsViewModel();
        DetailVm       = new GameDetailViewModel();
        QuickMenuVm    = new QuickMenuViewModel();

        // Give the detail VM access to the per-system metadata cache so it can
        // load achievement JSON from disk instead of re-downloading every time.
        DetailVm.CacheService = _metadataCache;

        DetailVm.OnClose = () => ShowDetail = false;

        // Wire Quick Menu callbacks
        QuickMenuVm.OnDismiss  = () => ShowQuickMenu = false;
        QuickMenuVm.OnExitGame = () =>
        {
            // Forward to DetailVm to kill the running game process
            if (DetailVm.IsGameRunning)
                DetailVm.ForceExitGame();
        };
        QuickMenuVm.OnViewFriendProfile = OpenFriendProfile;
        QuickMenuVm.OnLoadConversation = async friendUsername =>
            await _client.GetMessagesAsync(friendUsername);
        QuickMenuVm.OnSendMessage = async (friendUsername, text) =>
        {
            await _client.SendMessageAsync(friendUsername, text);
            return true;
        };
        QuickMenuVm.OnInviteFriend = async (friendUsername, gameName, platform, connectionType) =>
        {
            try
            {
                await _client.SendInviteAsync(friendUsername, gameName, platform, connectionType);
                return true;
            }
            catch
            {
                return false;
            }
        };
        QuickMenuVm.OnNavigatePage = page =>
        {
            if (string.IsNullOrWhiteSpace(page)) return;
            Navigate(page);
        };
        QuickMenuVm.OnLaunchRecentGame = card =>
        {
            if (card == null) return;
            LaunchFromCard(card);
        };
        QuickMenuVm.OnOpenBrowser = () =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = DefaultBrowserUrl,
                    UseShellExecute = true
                });
            }
            catch { }
        };
        QuickMenuVm.OnSignOut = () => SignOutCommand.Execute(null);
        QuickMenuVm.OnSwitchAccount = () => SwitchAccountCommand.Execute(null);
        QuickMenuVm.OnExitApplication = () => ExitAppCommand.Execute(null);
        QuickMenuVm.OnRequestLauncherForeground = () => BringToForegroundRequested?.Invoke();
        QuickMenuVm.OnMediaPrevious = SendMediaPrevious;
        QuickMenuVm.OnMediaPlayPause = SendMediaPlayPause;
        QuickMenuVm.OnMediaNext = SendMediaNext;

        // Wire Settings → SyncNow so the Resync button triggers TryRefreshUserDataAsync
        SettingsVm.SyncNowAction = RequestManualSyncAsync;

        // Wire playtime tracking: when a game is launched from the detail view,
        // pass the process to the PlaytimeService to record the session on a
        // background thread so the UI never blocks, then immediately refresh the
        // dashboard so "Continue Playing" shows the game.
        DetailVm.OnRequestPlaytimeTracking = (proc, title, platform) =>
        {
            var settings = Services.AppSettingsService.Load();

            // ── Game-start toast notification ───────────────────────────────
            Services.NotificationService.ShowGameSessionStartedNotification(title);
            NotifyLaunchIntegrations(settings);

            // ── Game-start broadcast ────────────────────────────────────────
            if (settings.BroadcastGameStart)
            {
                _ = Task.Run(async () =>
                {
                    try { await _client.UpdatePresenceAsync(title).ConfigureAwait(false); }
                    catch { }
                });
            }

            // ── Window minimize ─────────────────────────────────────────────
            if (settings.MinimizeOnGameLaunch)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => MinimizeWindowRequested?.Invoke());

            // ── Playtime tracking ───────────────────────────────────────────
            // Run TrackProcess on the thread pool — it sets up event handlers and
            // timers but never blocks, and keeping it off the UI thread ensures the
            // launcher stays responsive even if many games are tracked simultaneously.
            if (settings.TrackFolderProcesses && proc != null)
            {
                // Determine the game's installation folder for mod-client tracking.
                string? gameFolder = null;
                try
                {
                    if (!proc.HasExited)
                    {
                        string? exe = proc.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(exe))
                            gameFolder = System.IO.Path.GetDirectoryName(exe);
                    }
                }
                catch { /* MainModule may not be accessible */ }

                if (!string.IsNullOrEmpty(gameFolder))
                {
                    _ = Task.Run(() => _playtimeSvc.TrackProcessWithFolderWatch(
                        proc, title, platform, gameFolder, _library));
                }
                else
                {
                    _ = Task.Run(() => _playtimeSvc.TrackProcess(proc, title, platform, _library));
                }
            }
            else
            {
                if (proc != null)
                    _ = Task.Run(() => _playtimeSvc.TrackProcess(proc, title, platform, _library));
            }

            // Refresh dashboard immediately so the game appears in "Continue Playing"
            RefreshDashboardLocalGames();

            if (proc != null && !string.IsNullOrWhiteSpace(DetailVm.ExophaseUrl))
            {
                StartExophasePollingForSession(
                    proc,
                    title,
                    platform,
                    DetailVm.ExophaseUrl!,
                    DetailVm.CurrentTitleId);
            }
        };
        DetailVm.OnRequestPlaytimeTrackingFallback = (launchTarget, title, platform, baselinePids) =>
        {
            var settings = Services.AppSettingsService.Load();
            Services.NotificationService.ShowGameSessionStartedNotification(title);
            NotifyLaunchIntegrations(settings);

            if (settings.BroadcastGameStart)
            {
                _ = Task.Run(async () =>
                {
                    try { await _client.UpdatePresenceAsync(title).ConfigureAwait(false); }
                    catch { }
                });
            }

            if (settings.MinimizeOnGameLaunch)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => MinimizeWindowRequested?.Invoke());

            _playtimeSvc.TrackProcessFromLaunchSnapshot(
                title,
                platform,
                launchTarget,
                baselinePids,
                _library);
            RefreshDashboardLocalGames();

            if (!string.IsNullOrWhiteSpace(DetailVm.ExophaseUrl))
            {
                _ = Task.Run(() => RequestManualExophaseSyncAsync(
                    DetailVm.ExophaseUrl!,
                    platform,
                    title,
                    DetailVm.CurrentTitleId));
            }
        };
        DetailVm.OnRequestManualExophaseSyncAsync = RequestManualExophaseSyncAsync;

        // Wire Xenia (Xbox 360) achievement unlock: persist newly-unlocked achievements
        // to the cloud so they don't re-fire toast notifications on the next session.
        DetailVm.OnRequestAchievementUnlockAsync = async (platform, gameTitle, achievementId, achievementName, iconUrl) =>
        {
            string queuedUnlockedAt = DateTime.UtcNow.ToString("o");
            string? queuedTitleId = _library.FirstOrDefault(g =>
                string.Equals(g.Platform, platform, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(g.Title, gameTitle, StringComparison.OrdinalIgnoreCase))
                ?.TitleId;
            string queuedAchievementId = string.IsNullOrWhiteSpace(achievementId) ? achievementName : achievementId;

            try
            {
                bool MatchesAchievement(Models.Achievement a) =>
                    string.Equals(a.Platform, platform, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(a.GameTitle, gameTitle, StringComparison.OrdinalIgnoreCase) &&
                    (
                        (!string.IsNullOrEmpty(a.AchievementId) &&
                         !string.IsNullOrEmpty(achievementId) &&
                         string.Equals(a.AchievementId, achievementId, StringComparison.OrdinalIgnoreCase)) ||
                        string.Equals(a.Name, achievementName, StringComparison.OrdinalIgnoreCase)
                    );

                var existing = _achievements.FirstOrDefault(MatchesAchievement);
                if (existing != null && !string.IsNullOrEmpty(existing.UnlockedAt))
                    return;

                string unlockedAt = queuedUnlockedAt;

                if (existing != null)
                {
                    existing.AchievementId = queuedAchievementId;
                    existing.Name          = achievementName;
                    existing.IconUrl       = iconUrl;
                    existing.UnlockedAt    = unlockedAt;
                }
                else
                {
                    _achievements.Add(new Models.Achievement
                    {
                        Platform      = platform,
                        GameTitle     = gameTitle,
                        AchievementId = queuedAchievementId,
                        Name          = achievementName,
                        IconUrl       = iconUrl,
                        UnlockedAt    = unlockedAt,
                    });
                }

                var libEntry = _library.FirstOrDefault(g =>
                    string.Equals(g.Platform, platform, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(g.Title, gameTitle, StringComparison.OrdinalIgnoreCase));
                if (libEntry != null)
                {
                    libEntry.GameAchievements ??= new List<Achievement>();
                    var libAch = libEntry.GameAchievements.FirstOrDefault(a =>
                        (!string.IsNullOrEmpty(a.AchievementId) &&
                         string.Equals(a.AchievementId, queuedAchievementId, StringComparison.OrdinalIgnoreCase)) ||
                        string.Equals(a.Name, achievementName, StringComparison.OrdinalIgnoreCase));
                    if (libAch != null)
                    {
                        libAch.AchievementId = queuedAchievementId;
                        libAch.Name          = achievementName;
                        libAch.IconUrl       = iconUrl;
                        libAch.UnlockedAt    = unlockedAt;
                    }
                    else
                    {
                        libEntry.GameAchievements.Add(new Models.Achievement
                        {
                            Platform      = platform,
                            GameTitle     = gameTitle,
                            AchievementId = queuedAchievementId,
                            Name          = achievementName,
                            IconUrl       = iconUrl,
                            UnlockedAt    = unlockedAt,
                        });
                    }
                }

                if (!string.IsNullOrEmpty(_profile?.Username))
                    _offlineCache.Save(_profile.Username, _profile, _library, _achievements);

                string? titleId = queuedTitleId;

                bool online = await _client.CheckHealthAsync().ConfigureAwait(false);
                if (!online)
                {
                    if (!string.IsNullOrEmpty(_profile?.Username))
                    {
                        _pendingChanges.EnqueueAchievementUnlock(
                            _profile.Username,
                            platform,
                            gameTitle,
                            queuedTitleId,
                            queuedAchievementId,
                            achievementName,
                            null,
                            iconUrl,
                            queuedUnlockedAt);
                    }
                    return;
                }

                await _client.LogAchievementUnlockAsync(
                    platform, gameTitle, titleId,
                    achievementName, iconUrl)
                    .ConfigureAwait(false);

                await _client.SaveAchievementAsync(
                    platform,
                    gameTitle,
                    queuedTitleId,
                    queuedAchievementId,
                    achievementName,
                    null,
                    queuedUnlockedAt)
                    .ConfigureAwait(false);
                _ = _client.WriteSyncSignalAsync();
            }
            catch
            {
                if (!string.IsNullOrEmpty(_profile?.Username))
                {
                    _pendingChanges.EnqueueAchievementUnlock(
                        _profile.Username,
                        platform,
                        gameTitle,
                        queuedTitleId,
                        queuedAchievementId,
                        achievementName,
                        null,
                        iconUrl,
                        queuedUnlockedAt);
                }
                /* best-effort — toast already shown, cloud sync is non-fatal */
            }
        };

        // Wire achievement total notification: once the full achievement template is loaded
        // for any game (detail view opened, network or cache), update the library card's
        // denominator so "3 / 3" becomes "3 / 98".
        DetailVm.OnAchievementTotalLoaded = (platform, title, total) =>
        {
            if (total <= 0) return;

            // Update TotalAchievements on the matching cloud library game
            var libEntry = _library.FirstOrDefault(g =>
                string.Equals(g.Platform, platform, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(g.Title, title, StringComparison.OrdinalIgnoreCase));
            if (libEntry != null && libEntry.TotalAchievements != total)
            {
                libEntry.TotalAchievements = total;
                // Refresh the card's achievement label now that we have the correct denominator
                var card = LibraryVm.FindMyGameCard(title, platform);
                if (card?.SourceCloudGame != null)
                    card.AchievementLabel = card.SourceCloudGame.AchievementCountLabel;
            }
        };

        // Wire per-game cloud folder write for ROM-based platforms (Xbox 360, Switch, PS4 …).
        // Called after FetchAndDisplayAchievementsAsync loads the full achievement template so
        // the private repo gets Achievements/{platform}/{titleKey}/achievements.json just like
        // Steam-synced PC games do.  A local cache file guards against redundant cloud writes.
        DetailVm.OnFullAchievementListReadyAsync = async (platform, titleKey, gameTitle, achievements) =>
        {
            try
            {
                string localPath = GetLocalPerGameAchievementsPath(
                    _profile?.Username ?? "", platform, titleKey);
                bool localExists = File.Exists(localPath);
                int localUnlockedCount = localExists ? CountLocalUnlockedAchievements(localPath) : 0;
                int nowUnlocked = achievements.Count(a => !string.IsNullOrEmpty(a.UnlockedAt));

                // Skip the cloud write if nothing has changed since the last write.
                if (localExists && nowUnlocked <= localUnlockedCount) return;

                await _client.SaveFullGameAchievementsAsync(
                    platform, titleKey, gameTitle, achievements).ConfigureAwait(false);

                WriteLocalPerGameAchievements(localPath, achievements.ToList());
                DevLogService.Log(
                    $"[PerGameAch] Wrote {achievements.Count} achievements " +
                    $"({nowUnlocked} unlocked) for '{gameTitle}' ({platform}) to cloud.");
            }
            catch { /* best-effort */ }
        };


        // so the stored playtime and status reflect the completed session.
        PlaytimeService.SessionCompleted += (platform, title) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                RefreshDashboardLocalGames();

                // Notify the detail view-model so the Play button switches back from "Playing..."
                DetailVm.OnGameSessionEnded(platform, title);

                // Restore the window if it was minimized for the game
                var s = Services.AppSettingsService.Load();
                if (s.MinimizeOnGameLaunch)
                    RestoreWindowRequested?.Invoke();

                // Always clear the "now playing" presence so friends see "Dashboard"
                // (regardless of BroadcastGameStart — session-end clear is unconditional).
                _ = Task.Run(async () =>
                {
                    try { await _client.UpdatePresenceAsync(null).ConfigureAwait(false); }
                    catch { }
                });

                // Sync GamerScore + total game count to cloud so friends and Device B see current values
                _ = Task.Run(async () =>
                {
                    try
                    {
                        int totalPlaytime = _library.Sum(g => g.PlaytimeMinutes);
                        var gs = Models.GamerScore.Compute(totalPlaytime, _achievements.Count);
                        int totalGames = LibraryVm.TotalGames;
                        await _client.UpdateProfileAsync(gamerScore: gs.Total, totalGames: totalGames)
                                     .ConfigureAwait(false);
                    }
                    catch { }
                });

                // Show a session-ended toast notification with the session duration
                int sessionMinutes = PlaytimeService.GetTotalMinutes(platform, title);
                if (sessionMinutes > 0)
                    Services.NotificationService.ShowSessionEndedNotification(title, sessionMinutes);

                StopExophasePolling(platform, title);
            });
        };

        LoginVm.OnLoginSuccess = OnLoginSuccess;

        // Wire up OpenDetail from child VMs
        DashboardVm.OnOpenDetail        = OpenDetailFromGame;
        DashboardVm.OnOpenStoreDetail   = OpenDetailFromStoreGame;
        DashboardVm.OnOpenLocalDetail   = OpenDetailFromMyGameCard;
        DashboardVm.OnContinuePlaying   = LaunchFromCard;
        DashboardVm.OnNavigateToLibrary = () => Navigate("library");
        DashboardVm.OnNavigateToPage    = Navigate;
        DashboardVm.OnPlayFocusedCard   = LaunchFromCard;
        DashboardVm.OnOpenFocusedCardDetail = OpenDetailFromMyGameCard;
        DashboardVm.OnViewFriendProfile  = OpenFriendProfile;
        DashboardVm.OnMessageFriend      = friendUsername =>
        {
            Navigate("inbox");
            if (!string.IsNullOrWhiteSpace(friendUsername))
                InboxVm.OpenConversationCommand.Execute(friendUsername);
        };
        DashboardVm.OnResolveCurrentGameContext = () => (
            DetailVm.IsGameRunning ? DetailVm.Title : null,
            DetailVm.IsGameRunning ? DetailVm.Platform : null
        );
        DashboardVm.OnInviteFriend = async (friendUsername, gameName, platform, connectionType) =>
        {
            try
            {
                await _client.SendInviteAsync(friendUsername, gameName, platform, connectionType);
                return true;
            }
            catch
            {
                return false;
            }
        };
        LibraryVm.OnOpenDetail        = OpenDetailFromGame;
        LibraryVm.OnOpenLocalDetail   = OpenDetailFromLocalGame;
        LibraryVm.OnOpenRepackDetail  = OpenDetailFromLocalRepack;
        LibraryVm.OnOpenRomDetail     = OpenDetailFromLocalRom;
        LibraryVm.OnOpenMyGameDetail  = OpenDetailFromMyGameCard;
        StoreVm.OnOpenDetail          = OpenDetailFromStoreGame;
        FriendsVm.OnViewFriendProfile = OpenFriendProfile;
        FriendsVm.OnInviteFriend = async (friendUsername, gameName, platform, connectionType) =>
        {
            try
            {
                await _client.SendInviteAsync(friendUsername, gameName, platform, connectionType);
                return true;
            }
            catch
            {
                return false;
            }
        };
        FriendsVm.OnResolveCurrentGameContext = () => (
            DetailVm.IsGameRunning ? DetailVm.Title : null,
            DetailVm.IsGameRunning ? DetailVm.Platform : null
        );
        // Keep dashboard Friends section in sync whenever the friends list updates
        FriendsVm.OnlineFriends.CollectionChanged  += (_, _) => _PushFriendsToDashboard();
        FriendsVm.OfflineFriends.CollectionChanged += (_, _) => _PushFriendsToDashboard();
        InboxVm.OnViewFriendProfile   = OpenFriendProfile;
        InboxVm.OnInviteAccepted      = TryLaunchAcceptedInvite;

        // Start background scanner regardless of login state
        _scanner = new GameScannerService();
        // Wire the rebuild-complete callback so cover enrichment always runs after
        // _allMyGames is fully populated — eliminates the race condition where
        // EnrichMyGamesListAsync ran before ScheduleRebuild had finished.
        LibraryVm.OnMyGamesRebuilt = () =>
        {
            _ = EnrichMyGamesListAsync();
            // Populate achievement labels on My Games cards from the in-memory achievements list
            EnrichMyGamesAchievementLabels();
        };
        _scanner.GamesUpdated += games =>
        {
            DevLogService.Log($"[Scanner] GamesUpdated: {games.Count} local games found.");
            LibraryVm.UpdateLocalGames(games);
            RefreshDashboardLocalGames();
            // Trigger background metadata caching for the newly found PC games
            _ = BackgroundCacheLocalGamesAsync();
        };
        _scanner.RepacksUpdated += repacks =>
        {
            DevLogService.Log($"[Scanner] RepacksUpdated: {repacks.Count} repacks found.");
            LibraryVm.UpdateRepacks(repacks);
            RefreshDashboardLocalGames();
        };
        _scanner.RomsUpdated += roms =>
        {
            DevLogService.Log($"[Scanner] RomsUpdated: {roms.Count} ROMs found.");
            LibraryVm.UpdateRoms(roms);
            RefreshDashboardLocalGames();
            // Trigger background metadata caching for the newly found ROMs
            _ = BackgroundCacheLocalGamesAsync();
        };
    }

    public void BeginStartup()
    {
        if (Interlocked.Exchange(ref _startupBegun, 1) == 1)
            return;

        DevLogService.Log("[MainViewModel] BeginStartup — starting deferred startup tasks.");

        DevLogService.Log("[Scanner] Starting background game scanner…");
        _ = _scanner.StartAsync();

        var settings = Services.AppSettingsService.Load();
        if (settings.AutoUpdate)
            _ = Services.GitHubDataService.CheckForUpdatesAsync();

        _ = Services.SwitchTranslateService.SyncAsync();
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
        InboxVm.LoadDemo(_profile.Username);
        SettingsVm.LoadAccount(_profile, _library);

        // Push demo friends into dashboard
        _PushFriendsToDashboard();

        // Optional screenshot helper:
        //   GAMEOS_DEMO_QUICKMENU_THEME=<theme>  (e.g. Wii, Switch, SteamBPM)
        //   GAMEOS_DEMO_SHOW_QUICKMENU=1         (auto-open quick menu in demo mode)
        var demoQuickMenuTheme = Environment.GetEnvironmentVariable("GAMEOS_DEMO_QUICKMENU_THEME");
        if (!string.IsNullOrWhiteSpace(demoQuickMenuTheme))
            SettingsVm.QuickMenuTheme = demoQuickMenuTheme.Trim();

        // Pre-fetch cover art for the unified My Games cards
        _ = EnrichMyGamesListAsync();
        _ = EnrichDashboardCoversAsync();

        // Open the inline conversation for screenshot purposes
        FriendsVm.OpenConversationDemo();

        // Skip the login screen
        ShowLogin = false;
        ShowMain  = true;
        ActivePage = "dashboard";
        OpenFirstRunSetupIfNeeded();

        if (string.Equals(Environment.GetEnvironmentVariable("GAMEOS_DEMO_SHOW_QUICKMENU"), "1", StringComparison.Ordinal))
            ToggleQuickMenu();
    }

    private void OnLoginSuccess(UserProfile profile, List<Game> library,
                                List<Achievement> achievements, bool isOffline)
    {
        _profile      = profile;
        _library      = DeduplicateLibrary(library);
        _achievements = achievements;

        IsOfflineMode = isOffline;

        bool isAdmin = _client.IsAdmin;

        DevLogService.Log($"[MainViewModel] OnLoginSuccess: user='{profile.Username}'  games={library.Count}  achievements={achievements.Count}  offline={isOffline}  isAdmin={isAdmin}");

        // Create the per-user data folder hierarchy beneath the executable
        UserDataService.CreateUserFolders(profile.Username);

        // Switch the playtime service to the current user's private data folder
        // so playtime records are stored per-account, not per-device.
        PlaytimeService.SetCurrentUser(profile.Username);

        // Attach the logged-in username to the detail view-model so review submissions
        // are correctly attributed to the right user.
        DetailVm.SetCurrentUser(profile.Username);

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
            DevLogService.Log("[MainViewModel] Online mode — saving offline cache and starting background services.");
            _offlineCache.Save(profile.Username, profile, library, achievements);

            // Flush any pending offline changes now that we are back online
            _ = FlushPendingChangesAsync(profile.Username);

            // Update presence immediately on login so the user appears “Online” to friends,
            // then start a background heartbeat that refreshes it every 2 minutes — mirroring
            // the web app's updatePresence() call at startup + setInterval every 2 minutes.
            _ = _client.UpdatePresenceAsync();
            StartPresenceHeartbeat();

            // Start polling friend presence to fire notifications
            StartFriendPresencePolling();

            // Start periodic sync check (every 5 minutes while online): re-fetches remote
            // user data and invalidates stale Games.Database caches.
            StartSyncCheckTimer();

            // Start the 30-second heartbeat poller that watches sync-signal.json.
            // When another device finishes a play session it writes a new timestamp;
            // the poller detects the change and immediately refreshes playtime/recently-played
            // without waiting for the full 5-minute sync tick.
            StartHeartbeatPoller();

            // Load cloud playtime from the backend and apply it to the library.
            // Cloud data is the source of truth — it overrides the local cache so
            // playtime is the same regardless of which device the user logs in from.
            _ = ApplyCloudPlaytimeAsync(library);

            // Subscribe to session-saved events so completed play sessions are
            // immediately pushed to the cloud activity log.
            PlaytimeService.SessionSaved += OnSessionSaved;

            // Start background metadata caching for cloud library games and local games
            _ = BackgroundCacheLibraryAsync(library);
            _ = BackgroundCacheLocalGamesAsync();

            // Seed any bundled Switch achievement JSON files to the Games.Database if they
            // are not already there (best-effort; runs silently in the background).
            _ = Task.Run(() => TrySeedSwitchGameAchievementsAsync());

            // Sync per-game achievement data from the cloud for devices that don't have
            // a Steam API key or haven't yet populated the local per-game cache.
            // Runs in background so it does not delay the login screen transition.
            _ = BackgroundSyncPerGameAchievementsFromCloudAsync();
        }
        else
        {
            System.Diagnostics.Debug.WriteLine(
                $"[MainViewModel] Offline mode — loaded cached data for '{profile.Username}'.");
            DevLogService.Log($"[MainViewModel] Offline mode — loaded cached data for '{profile.Username}'.");

            // Start the reconnect timer so the app automatically transitions back online
            // as soon as a live server connection becomes available.
            StartOfflineReconnectTimer();
        }

        var localCards = GetDashboardCards();

        DevLogService.Log($"[MainViewModel] Local game cards found: {localCards.Count}. Loading child view models…");
        DashboardVm.Load(profile, library, achievements, localCards);
        LibraryVm.Load(library);
        StoreVm.Load(GameCatalog.Store, library, profile, _client, isAdmin);
        ProfileVm.Load(profile, library, achievements, isAdmin);
        FriendsVm.Load(_client, profile.Username);
        SettingsVm.LoadAccount(profile, library);

        // Wire the Steam library import action so the Settings button calls through
        // to the Steam Web API and re-populates the library with the results.
        SettingsVm.ImportSteamLibraryAction = async (apiKey, steamUserId) =>
        {
            try
            {
                var steamGames = await Services.SteamGameImportService
                    .FetchAndSaveAsync(apiKey, steamUserId, profile.Username)
                    .ConfigureAwait(false);

                // Merge Steam games into the cloud library: add any title not already present
                int added = 0;
                var existingTitles = new System.Collections.Generic.HashSet<string>(
                    _library.Select(g => g.Title), StringComparer.OrdinalIgnoreCase);

                // Build a title→Game lookup to avoid O(n²) when updating SteamAppId on existing entries
                var libraryByTitle = _library
                    .Where(g => g.SteamAppId == null)
                    .GroupBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(grp => grp.Key, grp => grp.First(),
                                  StringComparer.OrdinalIgnoreCase);

                // Also build an AppId→Game lookup so that games already in the library
                // under a different title variant are not re-added as duplicates.
                var libraryByAppId = _library
                    .Where(g => g.SteamAppId.HasValue && g.SteamAppId.Value > 0)
                    .GroupBy(g => g.SteamAppId!.Value)
                    .ToDictionary(grp => grp.Key, grp => grp.First());

                foreach (var sg in steamGames)
                {
                    // Skip if already present by AppId (handles same game under a different title)
                    if (sg.AppId > 0 && libraryByAppId.ContainsKey(sg.AppId)) continue;

                    if (!existingTitles.Contains(sg.Name))
                    {
                        var newGame = new Models.Game
                        {
                            Platform        = "PC",
                            Title           = sg.Name,
                            CoverUrl        = sg.CoverUrl,
                            AddedAt         = DateTime.UtcNow.ToString("O"),
                            PlaytimeMinutes = sg.PlaytimeMinutes,
                            TitleId         = sg.AppId.ToString(),
                            SteamAppId      = sg.AppId,
                        };
                        _library.Add(newGame);
                        existingTitles.Add(sg.Name);
                        added++;
                    }
                    else if (libraryByTitle.TryGetValue(sg.Name, out var existing))
                    {
                        // Stamp SteamAppId on existing entry if not yet set
                        existing.SteamAppId = sg.AppId;
                    }
                }

                // Merge Steam playtime into local sessions (take the maximum, avoid double-counting)
                var importSettings = Services.AppSettingsService.Load();
                if (importSettings.EnableSteamSync)
                {
                    foreach (var sg in steamGames.Where(g => g.PlaytimeMinutes > 0))
                        Services.PlaytimeService.MergeExternalMinutes("PC", sg.Name,
                            sg.PlaytimeMinutes, _library);
                }

                if (steamGames.Count > 0)
                    _ = SyncSteamOwnedGamesToCloudAsync(steamGames);

                // Sync Steam achievements for all games with community stats.
                // Fetches unlocked achievements and uploads them to the cloud so
                // the detail view shows which achievements the user has earned.
                // Prioritise played games; unplayed ones are checked in background.
                if (importSettings.EnableAchievementAutoSync &&
                    !string.IsNullOrWhiteSpace(steamUserId) &&
                    !string.IsNullOrWhiteSpace(apiKey))
                {
                    _ = SyncSteamAchievementsAsync(apiKey, steamUserId,
                            steamGames.OrderByDescending(g => g.PlaytimeMinutes).ToList());
                }

                // Contribute new Steam games to the public Games.Database so other
                // users who have the same game can benefit from the metadata.
                if (added > 0)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var ghSvc = new Services.GitHubDataService();
                            await ghSvc.ContributeSteamGamesToDatabaseAsync(steamGames)
                                       .ConfigureAwait(false);
                        }
                        catch { /* best-effort */ }
                    });
                }

                // Refresh the library view with the updated data
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    LibraryVm.Load(_library);
                });

                // Sync steamUserId + new total game count to the cloud profile so other
                // devices and friends see the updated values.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        int totalGames = _library.Count;
                        await _client.UpdateProfileAsync(steamUserId: steamUserId, totalGames: totalGames)
                                     .ConfigureAwait(false);
                        DevLogService.Log($"[Steam Import] Synced steamUserId and totalGames ({totalGames}) to cloud profile.");
                    }
                    catch { }
                });

                return $"✅  Imported {steamGames.Count} Steam games ({added} new added to library).";
            }
            catch (Exception ex)
            {
                return $"❌  Import failed: {ex.Message}";
            }
        };

        // Wire Steam ID linking to enforce uniqueness on the backend
        SettingsVm.LinkSteamIdAction = async steamId =>
        {
            try { return await _client.LinkSteamIdAsync(steamId).ConfigureAwait(false); }
            catch { return null; /* non-critical — link errors shown inline */ }
        };

        // Load cached Steam games from the previous session into the library immediately
        _ = Task.Run(() =>
        {
            var cached = Services.SteamGameImportService.LoadCached(profile.Username);
            if (cached.Count > 0)
            {
                var existingTitles = new System.Collections.Generic.HashSet<string>(
                    _library.Select(g => g.Title), StringComparer.OrdinalIgnoreCase);
                var libraryByTitle = _library
                    .Where(g => g.SteamAppId == null)
                    .GroupBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(grp => grp.Key, grp => grp.First(),
                                  StringComparer.OrdinalIgnoreCase);
                var libraryByAppId = _library
                    .Where(g => g.SteamAppId.HasValue && g.SteamAppId.Value > 0)
                    .GroupBy(g => g.SteamAppId!.Value)
                    .ToDictionary(grp => grp.Key, grp => grp.First());
                int added = 0;
                foreach (var sg in cached)
                {
                    if (sg.AppId > 0 && libraryByAppId.ContainsKey(sg.AppId)) continue;
                    if (!existingTitles.Contains(sg.Name))
                    {
                        _library.Add(new Models.Game
                        {
                            Platform        = "PC",
                            Title           = sg.Name,
                            CoverUrl        = sg.CoverUrl,
                            AddedAt         = DateTime.UtcNow.ToString("O"),
                            PlaytimeMinutes = sg.PlaytimeMinutes,
                            TitleId         = sg.AppId.ToString(),
                            SteamAppId      = sg.AppId,
                        });
                        existingTitles.Add(sg.Name);
                        added++;
                    }
                    else if (libraryByTitle.TryGetValue(sg.Name, out var existing))
                    {
                        existing.SteamAppId = sg.AppId;
                    }
                }
                if (added > 0)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        LibraryVm.Load(_library);
                        ProfileVm.Load(_profile, _library, _achievements, _client.IsAdmin);
                    });
                }
            }
        });

        // Auto-refresh Steam library in the background on login when the cache is stale
        // (older than 24 hours) and the user has a Steam API key and Steam ID configured.
        if (!isOffline)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var settings = Services.AppSettingsService.Load();
                    if (string.IsNullOrWhiteSpace(settings.SteamApiKey) ||
                        string.IsNullOrWhiteSpace(settings.SteamUserId))
                        return;

                    // Only auto-refresh if the cache is absent or older than 24 hours
                    string cachePath = Services.SteamGameImportService.GetCachePath(profile.Username);
                    bool cacheIsStale = !System.IO.File.Exists(cachePath) ||
                        (DateTime.UtcNow - System.IO.File.GetLastWriteTimeUtc(cachePath)).TotalHours >= 24;

                    List<Services.SteamOwnedGame> steamGames;
                    if (cacheIsStale)
                    {
                        steamGames = await Services.SteamGameImportService
                            .FetchAndSaveAsync(settings.SteamApiKey, settings.SteamUserId, profile.Username)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        // Cache is still fresh — use it directly for achievement scraping
                        steamGames = Services.SteamGameImportService.LoadCached(profile.Username);
                    }

                    if (cacheIsStale && steamGames.Count > 0)
                    {
                        int added = 0;
                        var existingTitles = new System.Collections.Generic.HashSet<string>(
                            _library.Select(g => g.Title), StringComparer.OrdinalIgnoreCase);
                        var libraryByTitle = _library
                            .Where(g => g.SteamAppId == null)
                            .GroupBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(grp => grp.Key, grp => grp.First(),
                                          StringComparer.OrdinalIgnoreCase);
                        var libraryByAppId = _library
                            .Where(g => g.SteamAppId.HasValue && g.SteamAppId.Value > 0)
                            .GroupBy(g => g.SteamAppId!.Value)
                            .ToDictionary(grp => grp.Key, grp => grp.First());
                        foreach (var sg in steamGames)
                        {
                            if (sg.AppId > 0 && libraryByAppId.ContainsKey(sg.AppId)) continue;
                            if (!existingTitles.Contains(sg.Name))
                            {
                                _library.Add(new Models.Game
                                {
                                    Platform        = "PC",
                                    Title           = sg.Name,
                                    CoverUrl        = sg.CoverUrl,
                                    AddedAt         = DateTime.UtcNow.ToString("O"),
                                    PlaytimeMinutes = sg.PlaytimeMinutes,
                                    TitleId         = sg.AppId.ToString(),
                                    SteamAppId      = sg.AppId,
                                });
                                existingTitles.Add(sg.Name);
                                added++;
                            }
                            else if (libraryByTitle.TryGetValue(sg.Name, out var existing))
                            {
                                existing.SteamAppId = sg.AppId;
                            }
                        }

                        // Merge Steam playtime (take max, avoid double-counting)
                        if (settings.EnableSteamSync)
                        {
                            foreach (var sg in steamGames.Where(g => g.PlaytimeMinutes > 0))
                                Services.PlaytimeService.MergeExternalMinutes("PC", sg.Name,
                                    sg.PlaytimeMinutes, _library);
                        }

                        if (added > 0)
                        {
                            DevLogService.Log($"[Steam Auto-Sync] Added {added} new Steam games to library.");

                            // Trigger workflow to add new Steam games to the public Games.Database.
                            // ContributeSteamGamesToDatabaseAsync filters to truly new games internally.
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    var ghSvc = new Services.GitHubDataService();
                                    await ghSvc.ContributeSteamGamesToDatabaseAsync(steamGames)
                                               .ConfigureAwait(false);
                                }
                                catch { /* best-effort */ }
                            });

                            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            {
                                LibraryVm.Load(_library);
                                ProfileVm.Load(_profile, _library, _achievements, _client.IsAdmin);
                            });
                        }
                    }

                    // ── Startup achievement scraping ────────────────────────────────────
                    // On every app start, scrape Steam achievements for all library games
                    // that have a SteamAppId so achievements are always up-to-date and
                    // sync across devices via the cloud.  Runs regardless of cache staleness.
                    if (steamGames.Count > 0)
                    {
                        _ = SyncSteamOwnedGamesToCloudAsync(steamGames);
                    }

                    if (settings.EnableAchievementAutoSync && steamGames.Count > 0)
                    {
                        // Prioritise games the user has played (playtime > 0) then others
                        var toSync = steamGames
                            .OrderByDescending(g => g.PlaytimeMinutes)
                            .ToList();
                        _ = SyncSteamAchievementsAsync(settings.SteamApiKey, settings.SteamUserId, toSync);
                    }
                }
                catch { /* best-effort — Steam sync failure must not block login */ }
            });
        }

        // ── Cross-device SteamUserId sync ─────────────────────────────────────
        // If the cloud profile carries a steamUserId and this device's local AppSettings
        // don't have one yet, populate local settings so Steam import works without
        // the user having to re-enter their Steam ID on every device.
        if (!isOffline && !string.IsNullOrWhiteSpace(profile.SteamUserId))
        {
            _ = Task.Run(() =>
            {
                try
                {
                    var settings = Services.AppSettingsService.Load();
                    if (string.IsNullOrWhiteSpace(settings.SteamUserId))
                    {
                        settings.SteamUserId = profile.SteamUserId!;
                        Services.AppSettingsService.Save(settings);
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            SettingsVm.SteamUserId = settings.SteamUserId);
                        DevLogService.Log($"[Steam Sync] Populated local SteamUserId from cloud profile.");
                    }
                }
                catch { }
            });
        }

        // ── Initial cloud profile sync (GamerScore + TotalGames) ─────────────────
        // Upload the freshly-computed GamerScore and current total game count so that
        // friends and Device B see up-to-date values immediately after login.
        if (!isOffline)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    int totalPlaytime = _library.Sum(g => g.PlaytimeMinutes);
                    var gs = Models.GamerScore.Compute(totalPlaytime, _achievements.Count);
                    // Use the cloud library count — LibraryVm.TotalGames will be accurate after
                    // the UI thread loads it, but since we're on the thread pool we use
                    // _library.Count (cloud games) as the reliable base count.  The full count
                    // (cloud + local + roms + repacks) is updated again at the next session end.
                    int totalGames = _library.Count;
                    await _client.UpdateProfileAsync(gamerScore: gs.Total, totalGames: totalGames)
                                 .ConfigureAwait(false);
                }
                catch { }
            });
        }

        if (!isOffline)
        {
            // Asynchronously enrich library games with cover/desc/trailer from Games.Database.
            _ = EnrichLibraryFromDatabaseAsync(library);

            // Pre-fetch cover art for the unified My Games cards (scanner may already
            // have found games before login, so enrich what's there right away).
            _ = EnrichMyGamesListAsync();

            // Enrich cover art for any dashboard "Continue Playing" cards that are
            // activity-only or cloud cards without covers yet.
            _ = EnrichDashboardCoversAsync();

            // Start background message polling (checks for new DMs every 60 seconds)
            StartMessagePolling();
        }

        ShowLogin = false;
        ShowMain  = true;
        ActivePage = "dashboard";
        OpenFirstRunSetupIfNeeded();
        DevLogService.Log("[MainViewModel] Login flow complete — showing dashboard.");

        // Launch any enabled startup apps (Settings › System › Startup)
        RunStartupApps();
    }

    private void OpenFirstRunSetupIfNeeded()
    {
        if (!SettingsVm.ShowFirstRunSetupBanner)
            return;

        Navigate("settings");
        SettingsVm.SelectedSection = "app";
        DevLogService.Log("[MainViewModel] First run detected — opening Settings for initial setup.");
    }

    /// <summary>
    /// Launches all enabled startup apps configured in Settings › System › Startup.
    /// Called once after the user successfully logs in.
    /// </summary>
    private static void RunStartupApps()
    {
        try
        {
            var settings = Services.AppSettingsService.Load();
            foreach (var app in settings.StartupApps)
            {
                if (!app.Enabled || string.IsNullOrWhiteSpace(app.Path)) continue;
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName        = app.Path,
                        UseShellExecute = true,
                        WorkingDirectory = System.IO.Path.GetDirectoryName(app.Path) ?? "",
                    };
                    if (!string.IsNullOrWhiteSpace(app.Arguments))
                        psi.Arguments = app.Arguments;
                    System.Diagnostics.Process.Start(psi);
                }
                catch { /* best-effort: skip apps that cannot be found or started */ }
            }
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Called when a game session is saved locally.  Pushes the session to the
    /// cloud activity log and immediately updates the game's playtime in games.json
    /// so other devices see the new data on their next sync tick without waiting for
    /// activity-log aggregation.
    /// </summary>
    private void OnSessionSaved(Models.PlaySession session)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (!DateTime.TryParse(session.StartedAt, null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var start))
                    start = DateTime.UtcNow;
                if (!DateTime.TryParse(session.EndedAt, null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var end))
                    end = DateTime.UtcNow;

                // Look up the TitleId and current accumulated playtime from the cloud library
                // so the activity entry is correctly associated with Switch / PS3 / Xbox 360 games.
                var libraryGame = _library.FirstOrDefault(g =>
                    string.Equals(g.Platform, session.Platform, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(g.Title,    session.Title,    StringComparison.OrdinalIgnoreCase));

                string? titleId = libraryGame?.TitleId;

                // 1. Log to activity.json — the authoritative session history.
                await _client.LogActivityAsync(
                    session.Platform, session.Title, titleId,
                    start, end, session.Minutes).ConfigureAwait(false);

                // 2. Update games.json with the new accumulated playtime so other devices
                //    see the change on their next periodic sync without needing to aggregate
                //    the full activity log first.
                // For cloud library games: use the in-memory total (already incremented by
                //   FinaliseSession → UpdateLibraryEntry).
                // For local-only ROMs not yet in the cloud library: read the true accumulated
                //   total from local sessions so we don't just send this session's duration.
                int newTotal = libraryGame != null
                    ? libraryGame.PlaytimeMinutes
                    : PlaytimeService.GetTotalMinutes(session.Platform, session.Title);

                await _client.UpdateGamePlaytimeAsync(
                    session.Platform, session.Title,
                    newTotal, end.ToString("o")).ConfigureAwait(false);

                // 3. Write a sync signal so Device B's 30-second heartbeat poller detects
                //    the new session immediately and refreshes without waiting for the
                //    5-minute full sync tick.
                await _client.WriteSyncSignalAsync().ConfigureAwait(false);

                System.Diagnostics.Debug.WriteLine(
                    $"[Playtime] Synced '{session.Title}' ({session.Platform}) " +
                    $"{session.Minutes} min to cloud (total {newTotal} min) + heartbeat.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Playtime] Failed to sync session to cloud: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Fetches the user's full activity log from the cloud, calculates per-game
    /// playtime totals, and applies them to the in-memory library.  The cloud is
    /// the source of truth so the same totals appear on any device the user logs
    /// in from.
    /// </summary>
    private async Task ApplyCloudPlaytimeAsync(List<Game> library)
    {
        DevLogService.Log("[Playtime] Fetching cloud activity log…");
        try
        {
            var activity = await _client.GetActivityAsync().ConfigureAwait(false);
            if (activity.Count == 0)
            {
                DevLogService.Log("[Playtime] Cloud activity log is empty — no playtime to apply.");
                // Still refresh the dashboard so any library changes (new games, etc.) are
                // reflected with correct (zero) playtime rather than stale data.
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    DashboardVm.Load(_profile, library, _achievements, GetDashboardCards());
                });
                return;
            }
            DevLogService.Log($"[Playtime] Cloud activity log has {activity.Count} entries.");

            // Populate the cloud playtime cache in PlaytimeService so that local ROM cards
            // (which may not be in the cloud library) also reflect cross-device sessions.
            PlaytimeService.SetCloudTotals(activity);

            // Aggregate cloud minutes per (platform, title)
            var totals = activity
                .GroupBy(a => $"{a.Platform.ToLowerInvariant()}||{a.GameTitle.ToLowerInvariant()}")
                .ToDictionary(
                    g => g.Key,
                    g => (TotalMinutes: g.Sum(a => a.MinutesPlayed),
                          LastPlayed:   g.Max(a => a.SessionEnd ?? a.LoggedAt)));

            foreach (var game in library)
            {
                var key = $"{game.Platform.ToLowerInvariant()}||{game.Title.ToLowerInvariant()}";
                if (totals.TryGetValue(key, out var agg) && agg.TotalMinutes > 0)
                {
                    game.PlaytimeMinutes = agg.TotalMinutes;
                    if (!string.IsNullOrEmpty(agg.LastPlayed))
                        game.LastPlayedAt = agg.LastPlayed;
                }
            }

            // Always refresh the dashboard so cross-device playtime is shown immediately,
            // including local ROM cards that now have cloud playtime via the cache above.
            System.Diagnostics.Debug.WriteLine(
                "[Playtime] Applied cloud playtime totals to library and cache.");
            DevLogService.Log("[Playtime] Applied cloud playtime totals to library and cache.");
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                DashboardVm.Load(_profile, library, _achievements, GetDashboardCards());
                LibraryVm.Load(library);
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[Playtime] Failed to load cloud activity: {ex.Message}");
            DevLogService.Log($"[Playtime] Failed to load cloud activity: {ex.GetType().Name}: {ex.Message}");
        }
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

                    case Services.PendingChangeKind.SaveAchievement
                        when change.Platform != null &&
                             change.Title != null &&
                             change.AchievementId != null &&
                             change.AchievementName != null:
                        await _client.LogAchievementUnlockAsync(
                            change.Platform,
                            change.Title,
                            change.TitleId,
                            change.AchievementName,
                            change.AchievementIconUrl);
                        await _client.SaveAchievementAsync(
                            change.Platform,
                            change.Title,
                            change.TitleId,
                            change.AchievementId,
                            change.AchievementName,
                            change.AchievementDescription,
                            change.UnlockedAt);
                        _ = _client.WriteSyncSignalAsync();
                        System.Diagnostics.Debug.WriteLine(
                            $"[PendingChanges] Synced SaveAchievement '{change.AchievementName}' in '{change.Title}'.");
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
        var localCards = GetDashboardCards();
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            DashboardVm.Load(_profile, _library, _achievements, localCards));
    }

    [RelayCommand]
    private void Navigate(string page)
    {
        DevLogService.Log($"[Navigation] Navigate → '{page}'");
        // Auto-collapse the nav sidebar after a page is selected (console-like)
        IsNavExpanded = false;
        ShowDetail = false;
        ActivePage = page;
        if (page == "library")
            LibraryVm.Load(_library);
        if (page == "friends")
            FriendsVm.Load(_client, _profile.Username);
        if (page == "inbox")
            InboxVm.Load(_client, _profile.Username);
        if (page == "profile")
            ProfileVm.Load(_profile, _library, _achievements, _client.IsAdmin);
    }

    private void OpenDetailFromGame(Game game)
    {
        // Only match local PC games when the library game is also a PC game.
        // Without this check, a cloud library entry like "God of War" (PS4) would
        // incorrectly be shown as "installed" if the user has a PC folder called "God of War".
        // Try SteamAppId first (most reliable when the local folder title differs slightly,
        // e.g. "Call of Duty Black Ops III" vs "Call of Duty: Black Ops III").
        LocalGame? localGame = null;
        if (string.Equals(game.Platform, "PC", StringComparison.OrdinalIgnoreCase))
        {
            localGame = (game.SteamAppId > 0
                ? LibraryVm.LocalGames.FirstOrDefault(lg => lg.SteamAppId == game.SteamAppId)
                : null)
                ?? LibraryVm.LocalGames
                    .FirstOrDefault(lg => TitlesLikelyMatch(lg.Title, game.Title));
        }
        LocalRepack? repack = null;
        if (localGame == null && string.Equals(game.Platform, "PC", StringComparison.OrdinalIgnoreCase))
            repack = LibraryVm.ReadyToInstall
                .FirstOrDefault(r => r.Title.Equals(game.Title, StringComparison.OrdinalIgnoreCase))
                ?? LibraryVm.ReadyToInstall
                    .FirstOrDefault(r => Models.PlatformHelper.StripSpecialSymbols(r.Title)
                        .Equals(Models.PlatformHelper.StripSpecialSymbols(game.Title),
                                StringComparison.OrdinalIgnoreCase));

        // For non-PC platforms, check whether a matching ROM is found on a local drive
        // so the detail view shows a Play button even when the game was added to the
        // cloud library manually via the web interface.
        LocalRom? localRom = null;
        if (localGame == null && repack == null &&
            !string.Equals(game.Platform, "PC", StringComparison.OrdinalIgnoreCase))
            localRom = FindMatchingRom(game.Title, game.Platform, game.TitleId);

        DetailVm.LoadFromGame(game, localGame, repack, localRom);
        ShowDetail = true;

        // Always enrich achievements for non-PC library games: if AchievementsUrl is
        // already set, the function checks !HasAchievements before re-fetching so it's
        // a no-op when achievements already loaded.  This also catches the case where
        // the stored URL is stale or the title has special symbols (™, ®) that differ
        // from the catalog entry.
        if (!string.Equals(game.Platform, "PC", StringComparison.OrdinalIgnoreCase))
            _ = EnrichGameAchievementsAsync(game);

        // For PC Steam games with no achievements in the database, fetch the schema
        // from the Steam API, upload it to the Games.Database, and sync the player's unlocks.
        if (string.Equals(game.Platform, "PC", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrEmpty(game.AchievementsUrl) &&
            game.SteamAppId.HasValue && game.SteamAppId.Value > 0)
            _ = TryContributeSteamGameAchievementsAsync((int)game.SteamAppId.Value, game.Title);
    }

    private void OpenDetailFromStoreGame(StoreGame game)
    {
        // Only match local PC games when the store game is also a PC game.
        // Non-PC store games should never show a PC local game as "installed".
        LocalGame? localGame = string.Equals(game.Platform, "PC", StringComparison.OrdinalIgnoreCase)
            ? LibraryVm.LocalGames
                .FirstOrDefault(lg => TitlesLikelyMatch(lg.Title, game.Title))
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

    private static bool TitlesLikelyMatch(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            return true;

        string leftStripped  = Models.PlatformHelper.StripSpecialSymbols(left);
        string rightStripped = Models.PlatformHelper.StripSpecialSymbols(right);
        if (string.Equals(leftStripped, rightStripped, StringComparison.OrdinalIgnoreCase))
            return true;

        string leftNormalized  = NormalizeGameTitle(leftStripped);
        string rightNormalized = NormalizeGameTitle(rightStripped);
        if (string.Equals(leftNormalized, rightNormalized, StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(
            Models.PlatformHelper.NormalizeTitleForComparison(left),
            Models.PlatformHelper.NormalizeTitleForComparison(right),
            StringComparison.Ordinal);
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
        // Mark as friend profile so the email is hidden and activity feed is shown
        FriendProfileVm.IsFriendProfile = true;
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

    /// <summary>
    /// Toggles the Quick Menu overlay (shown by Left Shift + Left Ctrl from the main window).
    /// Refreshes content before showing.
    /// </summary>
    public void ToggleQuickMenu()
    {
        if (ShowQuickMenu)
        {
            ShowQuickMenu = false;
            return;
        }

        // Collect friends (online first)
        var onlineFriends = FriendsVm.OnlineFriends
            .Select(f => new FriendPresenceVm
            {
                Username    = f.Username,
                CurrentGame = f.CurrentGame ?? "",
                Status      = f.Status,
            })
            .ToList();
        var allFriends = FriendsVm.OnlineFriends
            .Concat(FriendsVm.OfflineFriends)
            .Select(f => new FriendPresenceVm
            {
                Username    = f.Username,
                CurrentGame = f.CurrentGame ?? "",
                Status      = f.Status,
            })
            .ToList();

        // Achievement progress for current game
        int unlocked = 0, total = 0;
        if (DetailVm.HasAchievements)
        {
            unlocked = DetailVm.Achievements.Count(a => a.IsUnlocked);
            total    = DetailVm.Achievements.Count;
        }

        int unreadCount = InboxVm.PendingInvites.Count + InboxVm.Conversations.Count;
        string? lastMessage = InboxVm.Conversations
            .OrderByDescending(c => c.LastMessageAt)
            .Select(c => c.LastMessage)
            .FirstOrDefault();

        QuickMenuVm.Refresh(
            currentUsername:       _profile.Username,
            currentGameTitle:      DetailVm.IsGameRunning ? DetailVm.Title : null,
            currentGamePlatform:   DetailVm.IsGameRunning ? DetailVm.Platform : null,
            sessionStartedAt:      DetailVm.IsGameRunning
                ? PlaytimeService.GetActiveSessionStart(DetailVm.Platform, DetailVm.Title)
                  ?? PlaytimeService.GetAnyActiveSessionStart()
                : null,
            onlineFriends:         onlineFriends,
            allFriends:            allFriends,
            unreadCount:           unreadCount,
            lastMessage:           lastMessage,
            unlockedAchievements:  unlocked,
            totalAchievements:     total,
            achievements:          DetailVm.Achievements,
            recentGames:           DashboardVm.Ps5RecentGames.ToList(),
            activePageKey:         ActivePage,
            pendingDownloadCount:  LibraryVm.ReadyToInstall.Count,
            quickMenuTheme:        SettingsVm.QuickMenuTheme);

        ShowQuickMenu = true;
    }

    /// <summary>
    /// Converts the current FriendsVm friend lists into <see cref="FriendPresenceVm"/> objects
    /// and pushes them into the dashboard so the Friends section shows live data.
    /// Safe to call from any thread; dispatches to the UI thread if needed.
    /// </summary>
    private void _PushFriendsToDashboard()
    {
        var allFriends = FriendsVm.OnlineFriends
            .Concat(FriendsVm.OfflineFriends)
            .Select(f => new FriendPresenceVm
            {
                Username    = f.Username,
                CurrentGame = f.CurrentGame ?? "",
                Status      = f.Status,
            })
            .ToList();

        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            DashboardVm.UpdateFriends(allFriends);
        else
            Avalonia.Threading.Dispatcher.UIThread.Post(() => DashboardVm.UpdateFriends(allFriends));
    }

    private static void SendMediaPrevious()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { Services.NativeMethods.SendMediaKey(Services.NativeMethods.VK_MEDIA_PREV_TRACK); }
        catch { }
    }

    private static void SendMediaPlayPause()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { Services.NativeMethods.SendMediaKey(Services.NativeMethods.VK_MEDIA_PLAY_PAUSE); }
        catch { }
    }

    private static void SendMediaNext()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { Services.NativeMethods.SendMediaKey(Services.NativeMethods.VK_MEDIA_NEXT_TRACK); }
        catch { }
    }

    private void NotifyLaunchIntegrations(AppSettings settings)
    {
        if (!settings.NotifyExophaseStatus)
            return;

        if (string.IsNullOrWhiteSpace(DetailVm.ExophaseUrl))
        {
            NotificationService.ShowDeveloperNotification("Exophase", "Url Not Found");
            return;
        }

        NotificationService.ShowDeveloperNotification("Exophase", "Url Found");
        if (!string.IsNullOrWhiteSpace(settings.ExophaseProfileId))
            NotificationService.ShowDeveloperNotification("Exophase", "Scrapping Ach in bg");
    }

    private async Task LoadFriendProfileAsync(string friendUsername)
    {
        try
        {
            var profile  = await _client.GetFriendProfileAsync(friendUsername) ?? new UserProfile { Username = friendUsername };
            var games    = await _client.GetFriendGamesAsync(friendUsername);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                FriendProfileVm.Load(profile, games, new List<Achievement>(), false);
                FriendProfileVm.LoadFriendActivity(games);
            });
        }
        catch { /* best-effort — placeholder already visible */ }
    }

    private void OpenDetailFromLocalRom(LocalRom rom)
    {
        // Show ROM info immediately so the UI is responsive
        DetailVm.LoadFromLocalRom(rom);
        ShowDetail = true;

        // Pre-populate the user's already-unlocked achievements from the in-memory list so
        // that FetchAndDisplayAchievementsAsync (called during enrichment) can merge their
        // unlock state into the full achievement template fetched from the Games.Database.
        // Without this, Achievements is empty at enrichment time and all achievements appear
        // locked even though the user has earned some (stored in cloud and _achievements).
        var unlockedAchievements = _achievements
            .Where(a => string.Equals(a.Platform,   rom.Platform, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(a.GameTitle,  rom.Title,    StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (unlockedAchievements.Count > 0)
            DetailVm.PopulateAchievements(unlockedAchievements);

        // Asynchronously enrich with cover art / description / screenshots / achievements
        // from the platform-specific Games.Database (PS3, Switch, Xbox 360, etc.)
        // Pass TitleID for precise matching of PS3/PS4/Switch folder-named ROMs.
        _ = EnrichLocalGameDetailAsync(rom.Title, rom.Platform, rom.TitleId);
    }

    private void OpenDetailFromLocalGame(LocalGame game)
    {
        // Look up the matching cloud library entry to use its accumulated playtime
        // (which includes Steam API data) as a floor. This ensures the detail view
        // shows the correct playtime even when the local folder name differs from the
        // Steam API title (e.g. "Call of Duty Black Ops III" vs "Call of Duty: Black Ops III").
        int cloudPlaytime = 0;
        var cloudGame = game.SteamAppId > 0
            ? _library.FirstOrDefault(g => g.SteamAppId == game.SteamAppId)
              ?? _library.FirstOrDefault(g =>
                    string.Equals(g.Title, game.Title, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(g.Platform, "PC", StringComparison.OrdinalIgnoreCase))
            : _library.FirstOrDefault(g =>
                    string.Equals(g.Title, game.Title, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(g.Platform, "PC", StringComparison.OrdinalIgnoreCase));
        cloudPlaytime = cloudGame?.PlaytimeMinutes ?? 0;

        // Show basic info immediately so the UI is responsive
        DetailVm.LoadFromLocalGame(game, cloudPlaytime);
        ShowDetail = true;

        // Asynchronously enrich with cover/description/trailer from Games.Database
        _ = EnrichLocalGameDetailAsync(game.Title, "PC", steamAppId: game.SteamAppId);
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
    /// Returns a snapshot of the unified My Games card list used to populate the
    /// dashboard "Continue Playing" section and the Recent Achievements game list.
    /// Extracted into a helper to avoid duplicating the same LINQ chain at every call site.
    /// </summary>
    private List<LocalGameCardVm> GetDashboardCards() =>
        LibraryVm.GetMyGameSources()
            .Select(s => LibraryVm.FindMyGameCard(s.Title, s.Platform))
            .Where(c => c != null)
            .Cast<LocalGameCardVm>()
            .ToList();

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
            int cloudPlaytime = _library.FirstOrDefault(g =>
                (card.SourceGame.SteamAppId > 0 && g.SteamAppId == card.SourceGame.SteamAppId) ||
                (string.Equals(g.Title, card.SourceGame.Title, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(g.Platform, "PC", StringComparison.OrdinalIgnoreCase)))
                ?.PlaytimeMinutes ?? 0;
            DetailVm.LoadFromLocalGame(card.SourceGame, cloudPlaytime);
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
            // Try SteamAppId first so games whose local folder title differs slightly (e.g.
            // "Call of Duty Black Ops III" vs "Call of Duty: Black Ops III") are still found.
            var cg = card.SourceCloudGame;
            var localGame = (cg.SteamAppId > 0
                ? LibraryVm.LocalGames.FirstOrDefault(lg => lg.SteamAppId == cg.SteamAppId)
                : null)
                ?? LibraryVm.LocalGames
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
                 StringComparison.OrdinalIgnoreCase) ||
             // 4. Fuzzy canonical title key match
             string.Equals(
                 Models.PlatformHelper.NormalizeTitleForComparison(r.Title),
                 Models.PlatformHelper.NormalizeTitleForComparison(title),
                 StringComparison.Ordinal)));
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
            string strippedTitle = Models.PlatformHelper.StripSpecialSymbols(game.Title);

            // 1. Instant fallback: check the in-memory store catalog first (no network needed).
            //    This covers library games whose title has special symbols (e.g. "Mario Kart™ 8
            //    Deluxe") but whose catalog entry is keyed on the plain name.
            string? achievementsUrl = GameCatalog.Store
                .FirstOrDefault(s =>
                    string.Equals(s.Platform, game.Platform, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        Models.PlatformHelper.StripSpecialSymbols(s.Title),
                        strippedTitle,
                        StringComparison.OrdinalIgnoreCase))
                ?.AchievementsUrl;

            // 2. Network fallback: fetch the platform database if the catalog didn't have a URL.
            if (string.IsNullOrEmpty(achievementsUrl))
            {
                var dbGames = await GameOsClient.FetchGamesDatabaseAsync(game.Platform);
                var dbGame  = FindDatabaseGame(dbGames, game.Title, game.TitleId);
                achievementsUrl = dbGame?.AchievementsUrl;
            }

            if (string.IsNullOrEmpty(achievementsUrl)) return;

            // Persist for future opens within this session
            if (string.IsNullOrEmpty(game.AchievementsUrl))
                game.AchievementsUrl = achievementsUrl;

            // If the detail panel is still showing this game, fetch the full achievement
            // template so the complete list is shown (not just previously cached unlocks).
            // Pass unlocked achievements so their state is merged into the template.
            // Use StripSpecialSymbols so "Mario Kart™ 8 Deluxe" matches "Mario Kart 8 Deluxe".
            string url = achievementsUrl;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (string.Equals(
                        Models.PlatformHelper.StripSpecialSymbols(DetailVm.Title),
                        strippedTitle,
                        StringComparison.OrdinalIgnoreCase))
                {
                    var knownUnlocked = DetailVm.Achievements.Where(a => a.IsUnlocked).ToList();
                    _ = DetailVm.FetchAndDisplayAchievementsAsync(
                        url,
                        knownUnlocked.Count > 0 ? knownUnlocked : null);
                }
            });
        }
        catch { /* best-effort */ }
    }


    /// <summary>
    /// Updates the <see cref="LocalGameCardVm.AchievementLabel"/> on all My Games cards
    /// using the in-memory <c>_achievements</c> list loaded at login.
    /// Called on the UI thread (already inside <c>Dispatcher.UIThread.Post</c>) after each
    /// My Games rebuild so the achievement count appears without waiting for the cloud.
    /// </summary>
    private void EnrichMyGamesAchievementLabels()
    {
        if (_achievements.Count == 0) return;

        // Build a count lookup: (platform||title) → unlocked count
        var unlockCounts = _achievements
            .GroupBy(a => $"{a.Platform.ToLowerInvariant()}||{a.GameTitle.ToLowerInvariant()}")
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        if (unlockCounts.Count == 0) return;

        foreach (var source in LibraryVm.GetMyGameSources())
        {
            var card = LibraryVm.FindMyGameCard(source.Title, source.Platform);
            if (card == null) continue;
            int total = GetCachedAchievementTotal(source.Platform, source.TitleId, source.Title);

            // For cloud game cards whose source Game already has enriched achievements
            // (GameAchievements populated), use AchievementCountLabel for the X / Y format.
            if (card.SourceCloudGame?.GameAchievements?.Count > 0)
            {
                card.AchievementLabel = card.SourceCloudGame.AchievementCountLabel;
                continue;
            }

            string key = $"{source.Platform.ToLowerInvariant()}||{source.Title.ToLowerInvariant()}";
            if (unlockCounts.TryGetValue(key, out int count))
                card.AchievementLabel = total > 0 ? $"🏆 {count} / {total}" : (count > 0 ? $"🏆 {count}" : "");
            else if (total > 0)
                card.AchievementLabel = $"🏆 0 / {total}";
        }
    }

    private int GetCachedAchievementTotal(string platform, string? titleId, string title)
    {
        try
        {
            string? path = _metadataCache.GetCachedAchievementsPath(platform, titleId, title);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return 0;

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
                return root.GetArrayLength();
            if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (root.TryGetProperty("achievements", out var achievements) &&
                    achievements.ValueKind == System.Text.Json.JsonValueKind.Array)
                    return achievements.GetArrayLength();
                if (root.TryGetProperty("Items", out var items) &&
                    items.ValueKind == System.Text.Json.JsonValueKind.Array)
                    return items.GetArrayLength();
            }
        }
        catch { }
        return 0;
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
    /// Enriches "Continue Playing" dashboard cards that are missing cover art.
    /// Handles activity-only and cloud-library cards whose CoverUrl was not set
    /// when the dashboard was first built (e.g. before EnrichLibraryFromDatabaseAsync
    /// completed).  Fetches the platform's Games.Database and updates the card's
    /// CoverUrl on the UI thread so images appear progressively.
    /// </summary>
    private async Task EnrichDashboardCoversAsync()
    {
        try
        {
            // Snapshot of cards still missing covers (read on caller's thread)
            var toEnrich = DashboardVm.RecentLocalGames
                .Where(c => string.IsNullOrEmpty(c.CoverUrl))
                .Select(c => (c.EffectiveTitle, c.Platform,
                              TitleId: c.SourceRom?.TitleId ?? c.SourceCloudGame?.TitleId))
                .Where(t => !string.IsNullOrEmpty(t.Platform))
                .Distinct()
                .ToList();

            if (toEnrich.Count == 0) return;

            var byPlatform = toEnrich
                .GroupBy(t => t.Platform, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            foreach (var (platform, entries) in byPlatform)
            {
                try
                {
                    var dbGames = await GameOsClient.FetchGamesDatabaseAsync(platform);
                    if (dbGames.Count == 0) continue;

                    foreach (var (title, _, titleId) in entries)
                    {
                        var db = FindDatabaseGame(dbGames, title, titleId);
                        if (string.IsNullOrEmpty(db?.CoverUrl)) continue;

                        string cover = db.CoverUrl!;
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            foreach (var card in DashboardVm.RecentLocalGames
                                .Where(c => string.IsNullOrEmpty(c.CoverUrl) &&
                                            string.Equals(c.EffectiveTitle, title,
                                                StringComparison.OrdinalIgnoreCase) &&
                                            string.Equals(c.Platform, platform,
                                                StringComparison.OrdinalIgnoreCase)))
                            {
                                card.CoverUrl = cover;
                            }
                        });
                    }
                }
                catch { /* best-effort per platform */ }
            }
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// if found, enriches the currently-open detail panel with cover, description, trailer,
    /// screenshots and achievements — the same data shown on the website.
    /// Title matching handles Windows-safe folder names such as
    /// "Call of Duty - Black Ops II" → "Call of Duty: Black Ops II".
    /// When <paramref name="steamAppId"/> is provided and the game has no achievements in the
    /// database, the Steam API is queried to fetch and upload a fresh achievement schema.
    /// </summary>
    private async Task EnrichLocalGameDetailAsync(string localTitle, string platform,
                                                   string? titleId = null, int steamAppId = 0)
    {
        DatabaseGame? dbGame = null;
        var cached = _metadataCache.LoadCachedGameInfo(platform, titleId, localTitle);
        if (cached != null)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => DetailVm.EnrichFromDatabaseGame(cached));
            if (steamAppId <= 0 && cached.AppId.HasValue)
                steamAppId = (int)cached.AppId.Value;
        }

        try
        {
            var dbGames = await GameOsClient.FetchGamesDatabaseAsync(platform);
            dbGame = FindDatabaseGame(dbGames, localTitle, titleId);

            // Resolve effective Steam AppId: prefer the caller's value, fall back to DB entry
            if (steamAppId <= 0 && dbGame?.AppId.HasValue == true)
                steamAppId = (int)dbGame.AppId!.Value;

            if (dbGame != null)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(
                    () => DetailVm.EnrichFromDatabaseGame(dbGame));
            }
        }
        catch
        {
            if (cached != null)
            {
                DevLogService.Log($"[Detail] Using cached game.json for '{localTitle}' ({platform}) — network unavailable.");
            }
        }

        // For PC Steam games whose achievements are missing from the database, automatically
        // fetch the schema from Steam, upload it to the Games.Database, and sync the
        // player's unlocked achievements so the detail view shows them immediately.
        bool noDbAchievements = string.IsNullOrEmpty(dbGame?.AchievementsUrl);
        if (noDbAchievements && steamAppId > 0 &&
            string.Equals(platform, "PC", StringComparison.OrdinalIgnoreCase))
        {
            _ = TryContributeSteamGameAchievementsAsync(steamAppId, localTitle);
        }
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
                    if (string.IsNullOrEmpty(game.ExophaseUrl) && !string.IsNullOrEmpty(dbGame.ExophaseUrl))
                    { game.ExophaseUrl = dbGame.ExophaseUrl; anyUpdated = true; }
                }

                // Refresh the library UI once per platform batch if anything changed
                if (anyUpdated)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        LibraryVm.Load(_library);
                        // Pass localCards so the "Continue Playing" section rebuilds with
                        // newly-enriched cover URLs on cloud game cards.
                        DashboardVm.Load(_profile, _library, _achievements, GetDashboardCards());
                    });
                    // Also re-run cover enrichment for any dashboard cards still missing covers
                    _ = EnrichDashboardCoversAsync();

                    // Cache achievements.json for any games that just had their AchievementsUrl
                    // set (they may have been skipped by BackgroundCacheLibraryAsync which ran
                    // concurrently before the URL was known).
                    var toCache = platformGames
                        .Where(g => !string.IsNullOrEmpty(g.AchievementsUrl) &&
                                    _metadataCache.GetCachedAchievementsPath(g.Platform, g.TitleId, g.Title) == null)
                        .Select(g => (g.Platform, g.TitleId, g.Title, g.AchievementsUrl!))
                        .ToList();
                    if (toCache.Count > 0)
                        _ = Task.Run(async () =>
                        {
                            foreach (var (p, tid, t, aUrl) in toCache)
                            {
                                try { await _metadataCache.CacheLocalGameAsync(p, tid, t, null, aUrl).ConfigureAwait(false); }
                                catch { /* best-effort */ }
                            }
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
                string.Equals(alt, normalized,  StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    PlatformHelper.NormalizeTitleForComparison(alt),
                    PlatformHelper.NormalizeTitleForComparison(localTitle),
                    StringComparison.Ordinal)));
        if (byAltName != null) return byAltName;

        // 7. Canonical fuzzy-key title match (punctuation/metadata-insensitive)
        string localCanonical = PlatformHelper.NormalizeTitleForComparison(localTitle);
        if (!string.IsNullOrEmpty(localCanonical))
        {
            var byCanonical = dbGames.FirstOrDefault(g =>
                string.Equals(
                    PlatformHelper.NormalizeTitleForComparison(g.Title ?? ""),
                    localCanonical,
                    StringComparison.Ordinal));
            if (byCanonical != null) return byCanonical;
        }

        return null;
    }

    /// <summary>
    /// Removes duplicate library entries that share the same platform + title.
    /// This can happen when a game was added manually and then again via Steam import,
    /// or when the Games.Database contains multiple entries for the same title with
    /// different AppIds.  The entry that carries a SteamAppId (or the most complete
    /// data) is kept; the others are discarded.
    /// </summary>
    private static List<Game> DeduplicateLibrary(List<Game> library)
    {
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<Game>(library.Count);

        // Process games with a SteamAppId first so they are preferred over stub entries.
        // Within same-AppId ties, prefer entries that have richer metadata (cover URL).
        var ordered = library
            .OrderByDescending(g => g.SteamAppId.HasValue && g.SteamAppId.Value > 0 ? 1 : 0)
            .ThenByDescending(g => string.IsNullOrEmpty(g.CoverUrl) ? 0 : 1);

        foreach (var game in ordered)
        {
            // Primary dedup key: platform + exact title
            string titleLower    = game.Title?.ToLowerInvariant() ?? "";
            string platformLower = game.Platform?.ToLowerInvariant() ?? "";
            string key           = $"{platformLower}||{titleLower}";

            // Secondary key: platform + canonical fuzzy title (handles punctuation,
            // symbol, and metadata suffix variants).
            string canonicalTitle = Models.PlatformHelper.NormalizeTitleForComparison(game.Title ?? "");
            string canonicalKey   = $"{platformLower}||{canonicalTitle}";

            // Check by exact key first; fall through to normalised key so that the
            // same game showing up under marginally different title strings is caught.
            bool isDuplicate = !seen.Add(key);
            if (!isDuplicate && canonicalKey != key)
                isDuplicate = seen.Contains(canonicalKey);

            if (!isDuplicate)
            {
                // Register both keys so future duplicates are caught either way
                seen.Add(canonicalKey);
                result.Add(game);
            }
            else
            {
                // Merge useful fields from the duplicate into the already-kept entry
                var kept = result.FirstOrDefault(g =>
                    string.Equals(g.Platform, game.Platform, StringComparison.OrdinalIgnoreCase) &&
                    (string.Equals(g.Title, game.Title, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(
                         Models.PlatformHelper.NormalizeTitleForComparison(g.Title ?? ""),
                         Models.PlatformHelper.NormalizeTitleForComparison(game.Title ?? ""),
                         StringComparison.Ordinal)));
                if (kept != null)
                {
                    if (!kept.SteamAppId.HasValue && game.SteamAppId.HasValue)
                        kept.SteamAppId = game.SteamAppId;
                    if (string.IsNullOrEmpty(kept.CoverUrl) && !string.IsNullOrEmpty(game.CoverUrl))
                        kept.CoverUrl = game.CoverUrl;
                    if (string.IsNullOrEmpty(kept.AchievementsUrl) && !string.IsNullOrEmpty(game.AchievementsUrl))
                        kept.AchievementsUrl = game.AchievementsUrl;
                    // Carry forward the highest playtime so it isn't lost when a duplicate
                    // entry (e.g. same game under a different AppID) has more play data.
                    if (game.PlaytimeMinutes > kept.PlaytimeMinutes)
                        kept.PlaytimeMinutes = game.PlaytimeMinutes;
                }
            }
        }

        return result;
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

        StopFriendPresencePoller();
        StopOfflineReconnectTimer();
        StopSyncCheckTimer();
        StopHeartbeatPoller();

        // Unsubscribe from session-saved cloud sync and clear the per-user path
        PlaytimeService.SessionSaved -= OnSessionSaved;
        PlaytimeService.ClearCurrentUser();

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

        StopFriendPresencePoller();
        StopOfflineReconnectTimer();
        StopSyncCheckTimer();
        StopHeartbeatPoller();

        // Unsubscribe from session-saved cloud sync and clear the per-user path
        PlaytimeService.SessionSaved -= OnSessionSaved;
        PlaytimeService.ClearCurrentUser();

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

    /// <summary>
    /// Shuts down the application immediately.  Equivalent to the Power → Exit in
    /// Windows — stops all background services and exits the process.
    /// </summary>
    [RelayCommand]
    private void ExitApp()
    {
        Dispose();
        // Use the Avalonia lifetime for a clean shutdown
        if (Avalonia.Application.Current?.ApplicationLifetime is
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
        else
        {
            Environment.Exit(0);
        }
    }

    public void Dispose()
    {
        _manualSyncCts?.Cancel();
        _manualSyncCts?.Dispose();
        _manualSyncCts = null;
        foreach (var kvp in _exophasePollers.ToArray())
        {
            if (_exophasePollers.TryRemove(kvp.Key, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }
        }
        _messagePoller?.Dispose();
        _messagePoller = null;
        _presenceTimer?.Dispose();
        _presenceTimer = null;
        StopFriendPresencePoller();
        StopOfflineReconnectTimer();
        StopSyncCheckTimer();
        StopHeartbeatPoller();
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
            // Include the currently running game so friends always see an accurate status.
            // DetailVm.IsGameRunning ? DetailVm.Title : null means "In Game" or "Dashboard".
            string? currentGame = DetailVm.IsGameRunning ? DetailVm.Title : null;
            Task.Run(async () =>
            {
                try   { await _client.UpdatePresenceAsync(currentGame).ConfigureAwait(false); }
                catch { /* presence update failure is non-fatal */ }
            });
        }, null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));
    }

    // ── Friend presence polling ────────────────────────────────────────────────

    /// <summary>
    /// Starts a background timer that fires every 2 minutes to check whether any
    /// friends have come online or started playing a game.  When enabled settings are
    /// detected the appropriate toast notifications are fired.
    /// The first tick is intentionally delayed by 2 minutes so that the initial
    /// "all friends come online" burst at login does not fire a flood of toasts.
    /// </summary>
    private void StartFriendPresencePolling()
    {
        _friendPresencePoller?.Dispose();
        _friendPresenceInitialized = false;
        _lastKnownFriendPresence.Clear();

        _friendPresencePoller = new Timer(_ =>
        {
            Task.Run(async () =>
            {
                try   { await PollFriendPresenceAsync().ConfigureAwait(false); }
                catch { /* non-fatal */ }
            });
        }, null, TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2));
    }

    private void StopFriendPresencePoller()
    {
        _friendPresencePoller?.Dispose();
        _friendPresencePoller = null;
        _friendPresenceInitialized = false;
        _lastKnownFriendPresence.Clear();
    }

    /// <summary>
    /// Fetches presence data for all friends and fires notifications when a status
    /// change is detected (online/offline transition or new game started).
    /// </summary>
    private async Task PollFriendPresenceAsync()
    {
        var settings = AppSettingsService.Load();
        bool notifyOnline    = settings.NotifyFriendOnline;
        bool notifyGameStart = settings.NotifyFriendGameStart;
        if (!notifyOnline && !notifyGameStart) return;

        List<string> friends;
        try   { friends = await _client.GetFriendsAsync().ConfigureAwait(false); }
        catch { return; }

        if (friends.Count == 0) return;

        foreach (var friend in friends)
        {
            try
            {
                var presence = await _client.GetFriendPresenceAsync(friend).ConfigureAwait(false);
                if (presence == null) continue;

                // Determine online status: lastSeen within 5 minutes
                bool isOnline = false;
                if (!string.IsNullOrEmpty(presence.LastSeen) &&
                    DateTime.TryParse(presence.LastSeen,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var lastSeen))
                {
                    isOnline = (DateTime.UtcNow - lastSeen).TotalMinutes < 5;
                }

                string? currentGame = presence.CurrentGame;

                bool wasKnown = _lastKnownFriendPresence.TryGetValue(friend, out var prev);

                if (_friendPresenceInitialized && wasKnown)
                {
                    // Friend came online
                    if (notifyOnline && isOnline && !prev.IsOnline)
                        NotificationService.ShowFriendOnlineNotification(friend);

                    // Friend started a new game (or a different game)
                    if (notifyGameStart && !string.IsNullOrEmpty(currentGame) &&
                        !string.Equals(currentGame, prev.CurrentGame, StringComparison.OrdinalIgnoreCase))
                        NotificationService.ShowFriendGameStartNotification(friend, currentGame);
                }

                _lastKnownFriendPresence[friend] = (isOnline, currentGame);
            }
            catch { /* skip individual friend on error */ }
        }

        _friendPresenceInitialized = true;
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

            // Apply stored local playtime to the fresh games list so that sessions played
            // on this device before going offline are not lost when the library reloads.
            PlaytimeService.ApplyStoredPlaytime(games);

            // Re-apply Steam cached games so they are never lost on reconnect.
            ReapplySteamCachedGames(games);

            // Update in-memory state and save fresh cache on UI thread
            var capturedProfile = profile;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _profile      = capturedProfile;
                _library      = games;
                _achievements = achievements;
                IsOfflineMode = false;

                var localCards = GetDashboardCards();
                DashboardVm.Load(_profile, _library, _achievements, localCards);
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
            StartHeartbeatPoller();
            StartMessagePolling();

            // Apply cloud playtime so any sessions played on other devices while this one
            // was offline are immediately visible after reconnecting.
            _ = ApplyCloudPlaytimeAsync(games);

            // Re-sync per-game achievement data from cloud in case the device came back
            // online after another device added new achievements.
            _ = BackgroundSyncPerGameAchievementsFromCloudAsync();

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

    // ── Sync-signal heartbeat poller ──────────────────────────────────────────

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Starts a 30-second poller that reads the tiny sync-signal.json file.
    /// When <c>lastActivityAt</c> advances (written by Device A after a session ends)
    /// this device immediately re-fetches activity, playtime, and recently-played data
    /// instead of waiting up to 5 minutes for the full sync-check timer.
    /// </summary>
    private void StartHeartbeatPoller()
    {
        _heartbeatPoller?.Dispose();
        _heartbeatPoller = new Timer(_ =>
        {
            Task.Run(async () =>
            {
                try   { await OnHeartbeatTickAsync().ConfigureAwait(false); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[Heartbeat] Tick failed (non-fatal): {ex.Message}");
                }
            });
        }, null, HeartbeatInterval, HeartbeatInterval);

        System.Diagnostics.Debug.WriteLine(
            "[Heartbeat] Sync-signal poller started (every 30 seconds).");
    }

    private void StopHeartbeatPoller()
    {
        _heartbeatPoller?.Dispose();
        _heartbeatPoller = null;
    }

    /// <summary>
    /// Called every 30 seconds by the heartbeat poller.  Reads sync-signal.json and,
    /// when the <c>lastActivityAt</c> timestamp has advanced beyond the last known
    /// value, immediately triggers a full data refresh (activity log + playtime +
    /// recently-played) without waiting for the 5-minute sync-check timer.
    /// </summary>
    private async Task OnHeartbeatTickAsync()
    {
        if (IsOfflineMode) return;
        if (string.IsNullOrEmpty(_profile?.Username)) return;

        var latest = await _client.ReadSyncSignalAsync().ConfigureAwait(false);

        if (string.IsNullOrEmpty(latest)) return;

        // First tick: seed the known value without triggering a refresh
        if (_lastKnownSyncSignal == null)
        {
            _lastKnownSyncSignal = latest;
            System.Diagnostics.Debug.WriteLine(
                $"[Heartbeat] Seeded sync-signal: {latest}");
            return;
        }

        // ISO 8601 strings produced by DateTime.ToString("o") / DateTimeOffset.ToString("o")
        // sort lexicographically in the same order as chronologically, so a plain ordinal
        // string comparison correctly identifies which timestamp is newer.
        if (string.Compare(latest, _lastKnownSyncSignal, StringComparison.Ordinal) <= 0)
            return;

        System.Diagnostics.Debug.WriteLine(
            $"[Heartbeat] Sync-signal advanced ({_lastKnownSyncSignal} → {latest}). " +
            "Triggering immediate refresh.");

        _lastKnownSyncSignal = latest;

        // Perform the same refresh that TryRefreshUserDataAsync does — re-fetch games,
        // achievements, and apply cloud playtime so the dashboard is up-to-date instantly.
        await TryRefreshUserDataAsync().ConfigureAwait(false);
        await ApplyCloudPlaytimeAsync(_library).ConfigureAwait(false);
    }

    private async Task RequestManualSyncAsync()
    {
        var cts = new CancellationTokenSource();
        // Hard timeout: cancel the sync after 30 seconds so the UI is never
        // indefinitely stuck in the "Syncing…" state due to a slow server.
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        var previous = Interlocked.Exchange(ref _manualSyncCts, cts);
        if (previous != null)
        {
            try { previous.Cancel(); }
            catch (ObjectDisposedException) { }
            try { previous.Dispose(); }
            catch (ObjectDisposedException) { }
        }

        if (!await _manualSyncSemaphore.WaitAsync(0).ConfigureAwait(false))
        {
            cts.Dispose();
            return;
        }

        try
        {
            await Task.Run(async () =>
            {
                if (!cts.IsCancellationRequested)
                    await TryRefreshUserDataAsync().ConfigureAwait(false);
            }, cts.Token).ConfigureAwait(false);

            if (!cts.IsCancellationRequested)
            {
                _lastSyncedAt = DateTime.UtcNow;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    SettingsVm.LastSyncedLabel = FormatLastSyncedLabel(_lastSyncedAt));
            }
        }
        catch (OperationCanceledException)
        {
            // Timed out or cancelled — update the label so the user knows
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                SettingsVm.LastSyncedLabel = "Last sync: timed out — try again");
        }
        finally
        {
            _manualSyncSemaphore.Release();
            if (ReferenceEquals(_manualSyncCts, cts))
                _manualSyncCts = null;
            cts.Dispose();
        }
    }

    private void StartExophasePollingForSession(
        System.Diagnostics.Process proc,
        string title,
        string platform,
        string exophaseUrl,
        string? titleId)
    {
        if (!_client.IsAuthenticated || string.IsNullOrWhiteSpace(exophaseUrl))
            return;

        if (string.IsNullOrWhiteSpace(AppSettingsService.Load().ExophaseProfileId))
            return;

        string key = $"{platform.ToLowerInvariant()}||{title.ToLowerInvariant()}";
        StopExophasePolling(platform, title);

        var cts = new CancellationTokenSource();
        if (!_exophasePollers.TryAdd(key, cts))
        {
            cts.Dispose();
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        if (proc.HasExited) break;
                    }
                    catch (InvalidOperationException)
                    {
                        break;
                    }

                    int changes = await SyncExophaseNowAsync(
                        exophaseUrl,
                        platform,
                        title,
                        titleId,
                        "poll",
                        cts.Token).ConfigureAwait(false);

                    if (changes > 0)
                    {
                        await TryRefreshUserDataAsync().ConfigureAwait(false);
                        await _client.WriteSyncSignalAsync(cts.Token).ConfigureAwait(false);
                    }

                    await Task.Delay(ExophasePollInterval, cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch { /* best-effort */ }
            finally
            {
                if (_exophasePollers.TryRemove(key, out var removed))
                    removed.Dispose();
            }
        }, cts.Token);
    }

    private async Task RequestManualExophaseSyncAsync(
        string exophaseUrl,
        string platform,
        string title,
        string? titleId)
    {
        if (string.IsNullOrWhiteSpace(exophaseUrl)) return;
        int changes = await SyncExophaseNowAsync(
            exophaseUrl, platform, title, titleId, "manual", CancellationToken.None)
            .ConfigureAwait(false);

        if (changes > 0)
        {
            await TryRefreshUserDataAsync().ConfigureAwait(false);

            // Re-stamp the currently-open achievement list with freshly-synced
            // Exophase unlocks so the user sees updated state without reopening the page.
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (DetailVm is null) return;
                if (!string.Equals(DetailVm.Title, title, StringComparison.OrdinalIgnoreCase))
                    return;

                var merged = DetailVm.Achievements
                    .Select(a =>
                    {
                        if (!string.IsNullOrEmpty(a.UnlockedAt)) return a;
                        var cloud = _achievements.FirstOrDefault(ca =>
                            string.Equals(ca.GameTitle, title, StringComparison.OrdinalIgnoreCase) &&
                            (string.Equals(ca.AchievementId, a.AchievementId, StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(ca.Name, a.Name, StringComparison.OrdinalIgnoreCase)));
                        if (cloud != null && !string.IsNullOrEmpty(cloud.UnlockedAt))
                            a.UnlockedAt = cloud.UnlockedAt;
                        return a;
                    })
                    .ToList();

                if (merged.Count > 0)
                    DetailVm.PopulateAchievements(merged);
            });

            await _client.WriteSyncSignalAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<int> SyncExophaseNowAsync(
        string exophaseUrl,
        string platform,
        string title,
        string? titleId,
        string reason,
        CancellationToken ct)
    {
        try
        {
            var settings = AppSettingsService.Load();
            string profileId = NormaliseExophaseProfileId(settings.ExophaseProfileId ?? "");
            if (string.IsNullOrEmpty(profileId))
            {
                DevLogService.Log($"[Exophase] Skipped sync ({reason}) for {title} ({platform}) — profile ID not configured.");
                return 0;
            }
            string resolvedExophaseUrl = ResolveExophaseUrlForProfile(exophaseUrl, profileId);

            string resolvedTitleId = titleId
                ?? _library.FirstOrDefault(g =>
                    string.Equals(g.Platform, platform, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(g.Title, title, StringComparison.OrdinalIgnoreCase))
                    ?.TitleId
                ?? "";

            int changes = await _client.SyncExophaseAchievementsAsync(
                resolvedExophaseUrl,
                platform,
                title,
                string.IsNullOrWhiteSpace(resolvedTitleId) ? null : resolvedTitleId,
                profileId,
                ct).ConfigureAwait(false);

            if (changes > 0)
                DevLogService.Log($"[Exophase] Sync succeeded ({reason}) for {title} ({platform}) — {changes} achievement changes.");
            else
                DevLogService.Log($"[Exophase] Sync completed ({reason}) for {title} ({platform}) — no changes.");

            return changes;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DevLogService.Log($"[Exophase] Sync failed ({reason}) for {title} ({platform}): {ex.Message}");
            return 0;
        }
    }

    private static string NormaliseExophaseProfileId(string profileId)
    {
        var trimmed = (profileId ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed))
            return "";
        return trimmed.StartsWith("#", StringComparison.Ordinal) ? trimmed : $"#{trimmed}";
    }

    private static string ResolveExophaseUrlForProfile(string exophaseUrl, string profileId)
    {
        if (string.IsNullOrWhiteSpace(exophaseUrl))
            return exophaseUrl;

        var normalisedProfileId = NormaliseExophaseProfileId(profileId);
        if (string.IsNullOrEmpty(normalisedProfileId))
            return exophaseUrl;

        var profileDigits = normalisedProfileId.TrimStart('#');
        var resolved = exophaseUrl
            .Replace("#$UserID", normalisedProfileId, StringComparison.OrdinalIgnoreCase)
            .Replace("$UserID", profileDigits, StringComparison.OrdinalIgnoreCase)
            .Replace("{#UserID}", normalisedProfileId, StringComparison.OrdinalIgnoreCase)
            .Replace("{UserID}", profileDigits, StringComparison.OrdinalIgnoreCase)
            .Replace("#$ExoID", normalisedProfileId, StringComparison.OrdinalIgnoreCase)
            .Replace("$ExoID", profileDigits, StringComparison.OrdinalIgnoreCase)
            .Replace("{#ExoID}", normalisedProfileId, StringComparison.OrdinalIgnoreCase)
            .Replace("{ExoID}", profileDigits, StringComparison.OrdinalIgnoreCase);

        if (!Uri.TryCreate(resolved, UriKind.Absolute, out var parsedUrl))
            return resolved;

        if (!(string.Equals(parsedUrl.Host, "exophase.com", StringComparison.OrdinalIgnoreCase) ||
              parsedUrl.Host.EndsWith(".exophase.com", StringComparison.OrdinalIgnoreCase)))
            return resolved;

        if (!string.IsNullOrEmpty(parsedUrl.Fragment))
            return resolved;

        try
        {
            var builder = new UriBuilder(parsedUrl)
            {
                Fragment = profileDigits
            };
            return builder.Uri.ToString();
        }
        catch
        {
            return resolved;
        }
    }

    private void StopExophasePolling(string platform, string title)
    {
        string key = $"{platform.ToLowerInvariant()}||{title.ToLowerInvariant()}";
        if (_exophasePollers.TryRemove(key, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
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
        if (!await _syncRefreshSemaphore.WaitAsync(0).ConfigureAwait(false))
            return;

        try
        {
            System.Diagnostics.Debug.WriteLine(
                $"[SyncCheck] Checking for remote userdata updates for '{username}'...");

            try
            {
                var games        = await _client.GetGamesAsync().ConfigureAwait(false);
                var achievements = await _client.GetAchievementsAsync().ConfigureAwait(false);

                bool gamesChanged = !GamesListEqual(_library, games);
                // Only treat achievements as changed when the server returned a non-empty list.
                // An empty response (count == 0) almost always signals a transient API error;
                // treating it as "unchanged" prevents the dashboard from resetting to "0 Achievements".
                bool achvChanged  = achievements.Count > 0 && achievements.Count != _achievements.Count;

                // Also detect when another device has updated a game's playtime/lastPlayedAt
                // in games.json (written by UpdateGamePlaytimeAsync on session end).
                bool playtimeChanged = !gamesChanged && GamesPlaytimeChanged(_library, games);

                // Detect newly-unlocked achievements and log them to the activity feed
                if (achvChanged && achievements.Count > _achievements.Count)
                    _ = DetectAndLogNewAchievementsAsync(_achievements, achievements);

                if (gamesChanged || achvChanged || playtimeChanged)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[SyncCheck] Remote userdata changed for '{username}' " +
                        $"(games: {(gamesChanged ? "changed" : "same")}, " +
                        $"achievements: {(achvChanged ? "changed" : "same")}, " +
                        $"playtime: {(playtimeChanged ? "changed" : "same")}). Updating local cache.");

                    foreach (var g in games)
                        g.Platform = Models.PlatformHelper.NormalizePlatform(g.Platform);

                    // Re-apply local playtime to the freshly fetched games (the server only stores
                    // game metadata, not playtime — without this the dashboard shows zero playtime
                    // every time the periodic sync refreshes the library list).
                    PlaytimeService.ApplyStoredPlaytime(games);

                    // Re-apply Steam cached games as a fallback so titles are never lost while
                    // background cloud sync catches up on freshly imported Steam libraries.
                    ReapplySteamCachedGames(games);

                    _library = games;
                    // Guard: never replace the in-memory achievement list with an empty server
                    // response — a zero-count result is almost always a transient API error.
                    if (achievements.Count > 0)
                        _achievements = achievements;

                    _offlineCache.Save(username, _profile ?? new Models.UserProfile(), games,
                        achievements.Count > 0 ? achievements : _achievements);

                    // Only refresh the library immediately; the dashboard will be refreshed by
                    // ApplyCloudPlaytimeAsync (called below) once cloud playtime has been applied,
                    // so the dashboard always shows the correct playtime total rather than the
                    // server-only (local-playtime-only) value that exists at this point.
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        LibraryVm.Load(_library);
                    });
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[SyncCheck] Remote userdata unchanged for '{username}' — skipping update.");
                }

                // Always refresh cloud playtime so that sessions played on another device are
                // reflected on this one without requiring a full sign-out/sign-in cycle.
                // This runs on every sync-check tick (every 5 minutes) and is lightweight
                // since it only reads the activity log and updates in-memory game objects.
                _ = ApplyCloudPlaytimeAsync(_library);

                // Track last sync time for the Settings label
                _lastSyncedAt = DateTime.UtcNow;
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    SettingsVm.LastSyncedLabel = FormatLastSyncedLabel(_lastSyncedAt));

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
                    StopHeartbeatPoller();
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
        finally
        {
            _syncRefreshSemaphore.Release();
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

    /// <summary>
    /// Re-applies Steam-cached games (from SteamGames.json) into the given list.
    /// Called after every library replacement so Steam titles are never silently dropped
    /// while waiting for cloud <c>games.json</c> to reflect the latest Steam import.
    /// The periodic sync replaces
    /// <c>_library</c> with a fresh cloud fetch.  Uses the maximum of the already-stored
    /// playtime and the cached Steam playtime to avoid double-counting.
    /// </summary>
    private void ReapplySteamCachedGames(List<Models.Game> library)
    {
        try
        {
            var cached = Services.SteamGameImportService.LoadCached(_profile?.Username ?? "");
            if (cached.Count == 0) return;

            var existingTitles = new System.Collections.Generic.HashSet<string>(
                library.Select(g => g.Title), StringComparer.OrdinalIgnoreCase);
            var libraryByTitle = library
                .GroupBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(grp => grp.Key, grp => grp.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var sg in cached)
            {
                if (!existingTitles.Contains(sg.Name))
                {
                    library.Add(new Models.Game
                    {
                        Platform        = "PC",
                        Title           = sg.Name,
                        CoverUrl        = sg.CoverUrl,
                        AddedAt         = DateTime.UtcNow.ToString("O"),
                        PlaytimeMinutes = sg.PlaytimeMinutes,
                        TitleId         = sg.AppId.ToString(),
                        SteamAppId      = sg.AppId,
                    });
                    existingTitles.Add(sg.Name);
                }
                else if (libraryByTitle.TryGetValue(sg.Name, out var existing))
                {
                    // Stamp SteamAppId and ensure playtime is the maximum
                    if (existing.SteamAppId == null)
                        existing.SteamAppId = sg.AppId;
                    if (sg.PlaytimeMinutes > existing.PlaytimeMinutes)
                        existing.PlaytimeMinutes = sg.PlaytimeMinutes;
                }
            }
        }
        catch { /* best-effort — Steam cache re-apply failure must not block sync */ }
    }

    /// <summary>
    /// Returns <c>true</c> when any game in <paramref name="remote"/> has a newer
    /// <see cref="Game.LastPlayedAt"/> than the corresponding entry in
    /// <paramref name="current"/>.  Used to detect playtime updates written to
    /// games.json by another device so the dashboard refreshes without waiting for
    /// the full activity-log aggregation.
    /// </summary>
    private static bool GamesPlaytimeChanged(List<Game> current, List<Game> remote)
    {
        var lookup = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in current)
            lookup[$"{g.Platform?.ToLowerInvariant()}|{g.Title?.ToLowerInvariant()}"] = g.LastPlayedAt;

        foreach (var g in remote)
        {
            if (string.IsNullOrEmpty(g.LastPlayedAt)) continue;
            var key = $"{g.Platform?.ToLowerInvariant()}|{g.Title?.ToLowerInvariant()}";
            if (!lookup.TryGetValue(key, out var existing) ||
                string.Compare(g.LastPlayedAt, existing, StringComparison.Ordinal) > 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Compares the previous achievements list against the newly fetched one,
    /// logs any newly-unlocked entries to the cloud activity feed via
    /// <see cref="BackendApiService.LogAchievementUnlockAsync"/>, and caches
    /// achievement icons for new unlocks.
    /// </summary>
    private async Task DetectAndLogNewAchievementsAsync(
        List<Achievement> previous, List<Achievement> current)
    {
        try
        {
            // Build a set of already-known unlocked achievements
            var known = new HashSet<string>(
                previous.Where(a => !string.IsNullOrEmpty(a.UnlockedAt))
                         .Select(a => $"{a.Platform?.ToLower()}|{a.GameTitle?.ToLower()}|{a.Name?.ToLower()}"),
                StringComparer.Ordinal);

            // Build a lookup from (platform, gameTitle) → titleId using the current library
            var titleIdLookup = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in _library)
            {
                var lk = $"{g.Platform?.ToLower()}|{g.Title?.ToLower()}";
                if (!titleIdLookup.ContainsKey(lk))
                    titleIdLookup[lk] = g.TitleId;
            }

            foreach (var ach in current)
            {
                if (string.IsNullOrEmpty(ach.UnlockedAt)) continue;

                var key = $"{ach.Platform?.ToLower()}|{ach.GameTitle?.ToLower()}|{ach.Name?.ToLower()}";
                if (known.Contains(key)) continue;

                System.Diagnostics.Debug.WriteLine(
                    $"[AchievementSync] New unlock: {ach.Name} in {ach.GameTitle} ({ach.Platform})");

                titleIdLookup.TryGetValue(
                    $"{ach.Platform?.ToLower()}|{ach.GameTitle?.ToLower()}", out var titleId);

                await _client.LogAchievementUnlockAsync(
                    ach.Platform ?? "", ach.GameTitle ?? "", titleId,
                    ach.Name ?? "", ach.IconUrl).ConfigureAwait(false);

                // Cache the achievement icon locally
                if (!string.IsNullOrEmpty(titleId) && !string.IsNullOrEmpty(ach.IconUrl))
                {
                    _ = _metadataCache.CacheAchievementIconAsync(
                        ach.Platform ?? "", titleId, ach.AchievementId, ach.IconUrl);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[AchievementSync] DetectAndLogNewAchievementsAsync failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Ensures the user's full Steam owned-game list is mirrored to cloud
    /// <c>games.json</c> so all devices share the same Steam library baseline.
    /// Existing entries are skipped; only missing titles are added.
    /// </summary>
    private async Task SyncSteamOwnedGamesToCloudAsync(List<Services.SteamOwnedGame> steamGames)
    {
        if (steamGames.Count == 0) return;
        try
        {
            bool online = await _client.CheckHealthAsync().ConfigureAwait(false);
            if (!online) return;

            var cloudGames = await _client.GetGamesAsync().ConfigureAwait(false);
            var cloudPcTitles = new HashSet<string>(
                cloudGames
                    .Where(g => string.Equals(g.Platform, "PC", StringComparison.OrdinalIgnoreCase))
                    .Select(g => g.Title),
                StringComparer.OrdinalIgnoreCase);

            int added = 0;
            foreach (var sg in steamGames)
            {
                if (string.IsNullOrWhiteSpace(sg.Name)) continue;
                if (cloudPcTitles.Contains(sg.Name)) continue;

                try
                {
                    await _client.AddGameAsync(new Models.Game
                    {
                        Platform        = "PC",
                        Title           = sg.Name,
                        TitleId         = sg.AppId.ToString(),
                        CoverUrl        = sg.CoverUrl,
                        SteamAppId      = sg.AppId,
                        PlaytimeMinutes = Math.Max(0, sg.PlaytimeMinutes),
                    }).ConfigureAwait(false);
                    cloudPcTitles.Add(sg.Name);
                    added++;
                }
                catch { /* best-effort per title */ }
            }

            if (added > 0)
            {
                DevLogService.Log($"[Steam Sync] Added {added} Steam game(s) to cloud games.json.");
                _ = _client.WriteSyncSignalAsync();
            }
        }
        catch { /* best-effort — cloud sync must not block Steam import */ }
    }

    /// <summary>
    /// Fetches unlocked Steam achievements for the given list of games and syncs them
    /// to the Game OS cloud achievement store.  Only achievements not already present
    /// in <c>_achievements</c> are uploaded to avoid duplicate entries.
    /// </summary>
    private async Task SyncSteamAchievementsAsync(
        string apiKey, string steamUserId, List<Services.SteamOwnedGame> games)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(steamUserId)) return;

        // Build a set of already-known unlocked keys so we skip duplicates
        var known = new HashSet<string>(
            _achievements
                .Where(a => !string.IsNullOrEmpty(a.UnlockedAt))
                .Select(a => $"pc|{a.GameTitle?.ToLowerInvariant()}|{a.AchievementId?.ToLowerInvariant()}"),
            StringComparer.Ordinal);

        int processed = 0;
        var newlyUnlockedAchievements = new List<Models.Achievement>();

        foreach (var sg in games)
        {
            processed++;

            try
            {
                // Fetch ALL achievements (locked + unlocked) so the per-game cloud folder
                // contains the complete list, not just earned ones.
                // The Steam API returns human-readable names and descriptions when l=en.
                var allAch = await Services.SteamGameImportService
                    .FetchAllPlayerAchievementsAsync(apiKey, steamUserId, sg.AppId)
                    .ConfigureAwait(false);

                if (allAch.Count == 0) continue;

                // Find the titleId for this game in the library
                string? titleId = _library.FirstOrDefault(g =>
                    g.SteamAppId == sg.AppId ||
                    string.Equals(g.Title, sg.Name, StringComparison.OrdinalIgnoreCase))
                    ?.TitleId;

                // ── Per-game cloud folder (all achievements, locked + unlocked) ──────
                // Use the numeric Steam AppId as the folder key so the path matches the
                // expected format: Achievements/PC/{AppId}/achievements.json
                string titleKey = sg.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture);

                // Detect first run for this game: check local per-game cache.
                // If the cache file doesn't exist the per-game cloud folder has not yet
                // been initialised on this device and must be written.
                string localPerGamePath = GetLocalPerGameAchievementsPath(
                    _profile?.Username ?? "", "PC", titleKey);
                bool localExists = File.Exists(localPerGamePath);

                int steamUnlocked = allAch.Count(a => a.Achieved == 1);
                // Count how many unlocked achievements we already know about for this game.
                int localUnlockedCount = localExists ? CountLocalUnlockedAchievements(localPerGamePath) : 0;
                bool hasNewUnlocks = steamUnlocked > localUnlockedCount;

                if (!localExists || hasNewUnlocks)
                {
                    // Build Achievement models for every achievement (locked + unlocked).
                    var fullList = allAch
                        .Select(a => new Models.Achievement
                        {
                            Platform      = "PC",
                            GameTitle     = sg.Name,
                            AchievementId = a.ApiName,
                            Name          = !string.IsNullOrWhiteSpace(a.Name) ? a.Name : a.ApiName,
                            Description   = a.Description,
                            // Empty string for locked, ISO 8601 date for unlocked.
                            UnlockedAt    = a.Achieved == 1
                                ? (a.UnlockTime > 0
                                    ? DateTimeOffset.FromUnixTimeSeconds(a.UnlockTime).UtcDateTime.ToString("O")
                                    : DateTime.UtcNow.ToString("O"))
                                : "",
                        })
                        .ToList();

                    // Write full list to the per-game cloud folder.
                    // Fire-and-forget to avoid blocking the sync loop.
                    // Even when 0 achievements are unlocked, we write the complete locked
                    // template so other devices can display the full achievement list
                    // (with all entries shown as locked) without needing the Steam API.
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _client.SaveFullGameAchievementsAsync(
                                "PC", titleKey, sg.Name, fullList)
                                .ConfigureAwait(false);
                            DevLogService.Log(
                                $"[SteamAchievements] Wrote {fullList.Count} achievements " +
                                $"({fullList.Count(x => !string.IsNullOrEmpty(x.UnlockedAt))} unlocked) " +
                                $"for '{sg.Name}' to cloud.");
                        }
                        catch (Exception ex)
                        {
                            DevLogService.Log(
                                $"[SteamAchievements] Cloud write failed for '{sg.Name}': {ex.Message}");
                        }
                    });

                    // Cache locally so subsequent runs can detect first-run quickly
                    // and compare unlock counts without a cloud round-trip.
                    WriteLocalPerGameAchievements(localPerGamePath, fullList);
                }

                // ── Global achievements.json (unlocked only, existing logic) ─────────
                foreach (var ach in allAch)
                {
                    if (ach.Achieved != 1) continue;

                    string key = $"pc|{sg.Name.ToLowerInvariant()}|{ach.ApiName.ToLowerInvariant()}";
                    if (known.Contains(key)) continue;

                    string unlockedAt = ach.UnlockTime > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(ach.UnlockTime).UtcDateTime.ToString("O")
                        : DateTime.UtcNow.ToString("O");

                    var achievement = new Models.Achievement
                    {
                        Platform      = "PC",
                        GameTitle     = sg.Name,
                        AchievementId = ach.ApiName,
                        Name          = !string.IsNullOrWhiteSpace(ach.Name) ? ach.Name : ach.ApiName,
                        Description   = ach.Description,
                        UnlockedAt    = unlockedAt,
                    };
                    // Add to local in-memory list and track as new for cloud sync
                    _achievements.Add(achievement);
                    newlyUnlockedAchievements.Add(achievement);
                    known.Add(key);

                    // Log unlock event to the activity feed (non-fatal)
                    try
                    {
                        await _client.LogAchievementUnlockAsync(
                            "PC", sg.Name, titleId,
                            achievement.Name, null).ConfigureAwait(false);
                    }
                    catch { /* log failure must not stop the sync loop */ }
                }

                // Small delay between requests to avoid hitting Steam rate limits
                await Task.Delay(200).ConfigureAwait(false);
            }
            catch { /* best-effort per game */ }
        }

        // Persist all newly-unlocked achievements to the cloud user repo
        // (accounts/{username}/achievements.json) so they survive across sessions
        // and are visible on other devices / the web profile.
        if (newlyUnlockedAchievements.Count > 0)
        {
            try
            {
                await _client.SaveAchievementsAsync(newlyUnlockedAchievements).ConfigureAwait(false);
                DevLogService.Log($"[SteamAchievements] Saved {newlyUnlockedAchievements.Count} new unlocks to cloud.");
                _ = _client.WriteSyncSignalAsync();
            }
            catch { /* best-effort — sync must not block the UI */ }

            if (!string.IsNullOrEmpty(_profile?.Username))
                _offlineCache.Save(_profile.Username, _profile, _library, _achievements);
        }

        // Update in-memory game achievement lists so the library cards show the right count
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            foreach (var game in _library)
            {
                var gameAchs = _achievements
                    .Where(a => string.Equals(a.Platform, "PC", StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(a.GameTitle, game.Title, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (gameAchs.Count > 0)
                    game.GameAchievements = gameAchs;
            }
        });

        DevLogService.Log($"[SteamAchievements] Synced achievements for {processed} Steam games.");
    }

    // ── Per-game local achievement cache helpers ───────────────────────────────

    /// <summary>
    /// Returns the path to the local per-game achievement cache file for a specific
    /// user / platform / game combination.
    /// The cache mirrors the structure of the private cloud repo:
    /// <c>%AppData%\GameOS\{username}\Achievements\{platform}\{titleKey}\achievements.json</c>
    /// The file is used to:
    /// <list type="bullet">
    ///   <item>Detect first-run (file absent → cloud folder not yet initialised on this device).</item>
    ///   <item>Count locally-known unlocks to skip cloud writes when nothing has changed.</item>
    ///   <item>Provide an offline fallback for the full per-game achievement list.</item>
    /// </list>
    /// </summary>
    private static string GetLocalPerGameAchievementsPath(string username, string platform, string titleKey)
    {
        string safeUser     = StorageHelpers.SanitiseName(username);
        string safePlatform = StorageHelpers.SanitiseName(platform);
        string safeTitleKey = StorageHelpers.SanitiseName(titleKey);
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GameOS", safeUser, "Achievements", safePlatform, safeTitleKey, "achievements.json");
    }

    /// <summary>
    /// Counts the number of achievements in the local per-game cache that have a
    /// non-empty <c>UnlockedAt</c> timestamp (i.e. that the player has earned).
    /// Returns 0 if the file cannot be read.
    /// </summary>
    private static int CountLocalUnlockedAchievements(string path)
    {
        try
        {
            string json = File.ReadAllText(path);
            var opts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var list = System.Text.Json.JsonSerializer.Deserialize<List<Models.Achievement>>(json, opts);
            return list?.Count(a => !string.IsNullOrEmpty(a.UnlockedAt)) ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Writes the full per-game achievement list to the local cache file.
    /// Creates any missing parent directories.  Failures are silently swallowed
    /// so a cache write error never blocks the sync loop.
    /// </summary>
    private static void WriteLocalPerGameAchievements(string path, List<Models.Achievement> achievements)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            string json = System.Text.Json.JsonSerializer.Serialize(
                achievements,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// When a Steam PC game is opened and has no achievement data in the Games.Database,
    /// fetches the full achievement schema from the Steam API, uploads it to the public
    /// Games.Database so all users benefit, then syncs the player's unlocked achievements
    /// to their Game OS profile and displays them in the currently-open detail view.
    /// </summary>
    private async Task TryContributeSteamGameAchievementsAsync(int appId, string gameName)
    {
        if (appId <= 0) return;
        try
        {
            var settings = Services.AppSettingsService.Load();
            if (string.IsNullOrWhiteSpace(settings.SteamApiKey)) return;

            // 1. Fetch ALL achievements (template) from Steam — this includes both locked
            //    and unlocked achievements, giving us the canonical achievement list.
            var schema = await Services.SteamGameImportService
                .FetchSchemaForGameAsync(settings.SteamApiKey, appId)
                .ConfigureAwait(false);
            if (schema.Count == 0) return;

            // 2. Upload the schema to the public Games.Database so future users of this
            //    game will automatically get achievements without needing a Steam API call.
            string? achUrl = null;
            try
            {
                var ghSvc = new Services.GitHubDataService();
                achUrl = await ghSvc.UploadSteamAchievementsToDatabaseAsync(appId, gameName, schema)
                                    .ConfigureAwait(false);
            }
            catch { /* best-effort — proceed to display even if upload fails */ }

            // 3. Build the Achievement list from the schema so the detail view can show them
            var achievements = schema
                .Select(a => new Models.Achievement
                {
                    Platform      = "PC",
                    GameTitle     = gameName,
                    AchievementId = a.ApiName,
                    Name          = string.IsNullOrWhiteSpace(a.DisplayName) ? a.ApiName : a.DisplayName,
                    Description   = a.Description,
                    IconUrl       = string.IsNullOrEmpty(a.Icon) ? null : a.Icon,
                })
                .ToList();

            // 3a. Cache the schema locally so the detail view works offline and loads
            //     instantly on subsequent opens (mirrors FetchAndDisplayAchievementsAsync).
            if (!string.IsNullOrEmpty(achUrl))
            {
                try
                {
                    string? writePath = _metadataCache.GetAchievementsCachePath("PC", null, gameName);
                    if (!string.IsNullOrEmpty(writePath) && !File.Exists(writePath))
                    {
                        var dbAchs = schema.Select(a => new
                        {
                            achievementId = a.ApiName,
                            name          = string.IsNullOrWhiteSpace(a.DisplayName) ? a.ApiName : a.DisplayName,
                            description   = a.Description,
                            iconUrl       = string.IsNullOrEmpty(a.Icon) ? (string?)null : a.Icon,
                        });
                        var json = System.Text.Json.JsonSerializer.Serialize(dbAchs,
                            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                        var dir = Path.GetDirectoryName(writePath);
                        if (!string.IsNullOrEmpty(dir))
                            Directory.CreateDirectory(dir);
                        await File.WriteAllTextAsync(writePath, json).ConfigureAwait(false);
                        DevLogService.Log($"[SteamAch] Cached achievements for '{gameName}' at {writePath}");
                    }
                }
                catch { /* best-effort — cache write must not block the detail view */ }
            }

            // Update the library entry and detail view on the UI thread
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // Stamp the achievements URL onto the in-memory library entry
                if (!string.IsNullOrEmpty(achUrl))
                {
                    var libEntry = _library.FirstOrDefault(g =>
                        g.SteamAppId == appId ||
                        string.Equals(g.Title, gameName, StringComparison.OrdinalIgnoreCase));
                    if (libEntry != null && string.IsNullOrEmpty(libEntry.AchievementsUrl))
                        libEntry.AchievementsUrl = achUrl;
                }

                // Display in the currently-open detail panel if it's still showing this game
                if (string.Equals(DetailVm.Title, gameName, StringComparison.OrdinalIgnoreCase) &&
                    !DetailVm.HasAchievements)
                {
                    DetailVm.PopulateAchievements(achievements);
                }
            });

            // 4. Sync the player's own unlocked achievements from Steam to their profile
            if (!string.IsNullOrWhiteSpace(settings.SteamUserId))
            {
                var steamGame = new Services.SteamOwnedGame { AppId = appId, Name = gameName };
                await SyncSteamAchievementsAsync(
                    settings.SteamApiKey, settings.SteamUserId,
                    new List<Services.SteamOwnedGame> { steamGame })
                    .ConfigureAwait(false);

                // Re-stamp unlocked status on the displayed achievements
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (!string.Equals(DetailVm.Title, gameName, StringComparison.OrdinalIgnoreCase))
                        return;

                    var unlockedKeys = new HashSet<string>(
                        _achievements
                            .Where(a => string.Equals(a.GameTitle, gameName, StringComparison.OrdinalIgnoreCase) &&
                                        !string.IsNullOrEmpty(a.UnlockedAt))
                            .Select(a => a.AchievementId ?? a.Name),
                        StringComparer.OrdinalIgnoreCase);

                    if (unlockedKeys.Count == 0) return;

                    // Rebuild achievement list with unlock dates merged in
                    var merged = DetailVm.Achievements
                        .Select(a =>
                        {
                            var cloud = _achievements.FirstOrDefault(ca =>
                                string.Equals(ca.GameTitle, gameName, StringComparison.OrdinalIgnoreCase) &&
                                (string.Equals(ca.AchievementId, a.AchievementId, StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(ca.Name, a.Name, StringComparison.OrdinalIgnoreCase)));
                            if (cloud != null && !string.IsNullOrEmpty(cloud.UnlockedAt))
                                a.UnlockedAt = cloud.UnlockedAt;
                            return a;
                        })
                        .ToList();

                    DetailVm.PopulateAchievements(merged);
                });
            }
        }
        catch (Exception ex) { DevLogService.Log($"[SteamAch] TryContributeSteamGameAchievementsAsync failed for appId={appId}: {ex.Message}"); }
    }

    /// <summary>
    /// Best-effort task: seeds Switch game achievement JSON files to the public
    /// Games.Database for any game that has a local definition in
    /// <c>Switch Ach/Games/</c> but whose Games.Database entry does not yet have an
    /// <c>Achievement.json</c> file.
    ///
    /// <para>Called after every successful online login.  On the first run on a machine
    /// with a valid GitHub token it uploads the bundled JSON; on subsequent runs it
    /// detects the file already exists and exits immediately.</para>
    /// </summary>
    private async Task TrySeedSwitchGameAchievementsAsync()
    {
        // Map: (titleId, localJsonFileName)
        // Add new entries here whenever a new Switch game achievement file is created
        // in the Switch Ach/Games/ folder.
        var seedList = new[]
        {
            ("010028600EBDA000", "Super Mario 3D World & Bowser's Fury.json"),
        };

        string switchAchDir = System.IO.Path.Combine(AppContext.BaseDirectory, "Switch Ach", "Games");

        try
        {
            using var svc = new Services.GitHubDataService();

            foreach (var (titleId, filename) in seedList)
            {
                try
                {
                    string localPath = System.IO.Path.Combine(switchAchDir, filename);
                    if (!System.IO.File.Exists(localPath)) continue;

                    // Games.Database path: Data/Nintendo - Switch/Games/{titleId}/Achievement.json
                    string dbFilePath = $"Data/Nintendo - Switch/Games/{titleId}/Achievement.json";

                    // Only upload when the file is absent — do not overwrite intentional edits.
                    var (existing, _) = await svc.ReadGamesDatabaseFileAsync<object>(dbFilePath)
                        .ConfigureAwait(false);
                    if (existing != null) continue;

                    string localJson = await System.IO.File.ReadAllTextAsync(localPath)
                        .ConfigureAwait(false);

                    // Deserialize via JsonDocument so the object graph roundtrips correctly
                    // without losing fields (avoids the Dictionary<string,object> flattening).
                    using var doc = System.Text.Json.JsonDocument.Parse(localJson);
                    var contentElement = doc.RootElement.Clone();

                    await svc.WriteGamesDatabaseFileAsync(
                        dbFilePath,
                        contentElement,
                        $"Seed Switch achievements for {titleId}").ConfigureAwait(false);

                    DevLogService.Log($"[SwitchSeed] Uploaded Achievement.json for {titleId} to Games.Database.");
                }
                catch (Exception ex)
                {
                    DevLogService.Log($"[SwitchSeed] Skipped {titleId}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            DevLogService.Log($"[SwitchSeed] TrySeedSwitchGameAchievementsAsync failed: {ex.Message}");
        }
    }
    /// <para>
    /// This is the cross-device sync path for achievements: when Device A (with a Steam API key)
    /// has written the per-game achievement folder to the private cloud repo, Device B (without
    /// a Steam API key) can restore the complete achievement list — including locked entries —
    /// from the cloud without any Steam credentials.
    /// </para>
    /// Failures are silently swallowed; the task is entirely best-effort.
    /// </summary>
    private async Task BackgroundSyncPerGameAchievementsFromCloudAsync()
    {
        if (_profile == null) return;

        // Process at most this many games per session to balance the initial sync speed
        // against GitHub API rate limits (5,000 authenticated requests/hour).
        const int MaxBackgroundSyncGames = 50;

        // Focus on Steam games that don't yet have a local per-game cache.
        // Process most-played games first so the detail views users are likely to
        // open next are populated before the later games in the queue.
        var steamGames = _library
            .Where(g => g.SteamAppId.HasValue && g.SteamAppId.Value > 0)
            .OrderByDescending(g => g.PlaytimeMinutes)
            .Take(MaxBackgroundSyncGames)
            .ToList();

        if (steamGames.Count == 0) return;

        DevLogService.Log(
            $"[AchievementSync] Background cloud sync: checking {steamGames.Count} Steam games…");

        int loaded = 0;

        foreach (var game in steamGames)
        {
            try
            {
                string titleKey = game.SteamAppId!.Value.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                string localPath = GetLocalPerGameAchievementsPath(
                    _profile.Username, "PC", titleKey);

                // Skip games that already have a local per-game cache.
                // SyncSteamAchievementsAsync keeps those up-to-date via the Steam API.
                if (File.Exists(localPath)) continue;

                // Try to download the per-game achievement file from the cloud.
                var cloudAchs = await _client.GetGameAchievementsAsync("PC", titleKey)
                    .ConfigureAwait(false);

                if (cloudAchs == null || cloudAchs.Count == 0) continue;

                // Write to local cache so the file exists on the next session and this
                // code skips the cloud round-trip.
                WriteLocalPerGameAchievements(localPath, cloudAchs);
                loaded++;

                // Update the in-memory game record so the detail view shows the full list
                // (locked + unlocked) without needing another network fetch.
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    game.GameAchievements = cloudAchs;
                });

                DevLogService.Log(
                    $"[AchievementSync] Loaded {cloudAchs.Count} achievements " +
                    $"({cloudAchs.Count(a => !string.IsNullOrEmpty(a.UnlockedAt))} unlocked) " +
                    $"from cloud for '{game.Title}'.");

                // Brief pause between cloud reads to avoid rate-limiting the GitHub API.
                await Task.Delay(150).ConfigureAwait(false);
            }
            catch { /* best-effort per game */ }
        }

        if (loaded > 0)
        {
            DevLogService.Log(
                $"[AchievementSync] Synced per-game achievements from cloud for {loaded} game(s).");
            // Refresh the library view so card denominators reflect the full counts.
            Avalonia.Threading.Dispatcher.UIThread.Post(() => LibraryVm.Load(_library));
        }
    }

    private void UpdateCacheSyncLabel(string label, bool force = false)
    {
        bool shouldPost;
        lock (_cacheLabelGate)
        {
            long now = Environment.TickCount64;
            long elapsed = now - _lastCacheLabelUpdatedAt;

            shouldPost = force ||
                         !string.Equals(_lastCacheLabelText, label, StringComparison.Ordinal) ||
                         elapsed >= 500;
            if (!shouldPost) return;

            _lastCacheLabelText = label;
            _lastCacheLabelUpdatedAt = now;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() => CacheSyncLabel = label);
    }

    private void ResetCacheSyncLabelTracking()
    {
        lock (_cacheLabelGate)
        {
            _lastCacheLabelText = "";
            _lastCacheLabelUpdatedAt = 0;
        }
    }

    /// <summary>
    /// Background task: iterates all cloud library games and caches metadata + images
    /// not yet populated on the in-memory object (e.g. because enrichment is still
    /// running in parallel), the task fetches the missing data directly from the
    /// Games.Database so all library games — including Switch ROMs — are fully cached.
    /// Also saves a <c>game.json</c> file with rich game metadata.
    /// Reports progress via IsCachingGames / CacheSyncLabel.
    /// </summary>
    private async Task BackgroundCacheLibraryAsync(List<Game> library)
    {
        if (library.Count == 0) return;
        System.Threading.Interlocked.Increment(ref _cacheTaskCount);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => IsCachingGames = true);
        try
        {
            // Group by platform so we fetch each Games.Database at most once
            var byPlatform = library
                .GroupBy(g => g.Platform, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(grp => grp.Key, grp => grp.ToList(), StringComparer.OrdinalIgnoreCase);

            foreach (var (platform, games) in byPlatform)
            {
                // Fetch the platform database once — used to fill in missing CoverUrl /
                // AchievementsUrl and to produce the game.json metadata file.
                List<DatabaseGame>? dbGames = null;
                bool anyNeedDb = games.Any(g =>
                    string.IsNullOrEmpty(g.CoverUrl) ||
                    string.IsNullOrEmpty(g.AchievementsUrl) ||
                    !_metadataCache.IsGameInfoCached(g.Platform, g.TitleId, g.Title));
                if (anyNeedDb)
                {
                    try { dbGames = await GameOsClient.FetchGamesDatabaseAsync(platform).ConfigureAwait(false); }
                    catch { /* best-effort — fall through to cache whatever we already have */ }
                }

                foreach (var game in games)
                {
                    try
                    {
                        // Resolve effective CoverUrl and AchievementsUrl, falling back to
                        // the Games.Database when the in-memory game object is incomplete.
                        string? coverUrl       = game.CoverUrl;
                        string? achievementsUrl = game.AchievementsUrl;
                        DatabaseGame? dbGame   = null;

                        if (dbGames != null &&
                            (string.IsNullOrEmpty(coverUrl) ||
                             string.IsNullOrEmpty(achievementsUrl) ||
                             !_metadataCache.IsGameInfoCached(game.Platform, game.TitleId, game.Title)))
                        {
                            dbGame = FindDatabaseGame(dbGames, game.Title, game.TitleId);
                            if (dbGame != null)
                            {
                                if (string.IsNullOrEmpty(coverUrl))
                                    coverUrl = dbGame.CoverUrl;
                                if (string.IsNullOrEmpty(achievementsUrl))
                                    achievementsUrl = dbGame.AchievementsUrl;
                            }
                        }

                        UpdateCacheSyncLabel($"Caching {game.Title}…");

                        // Create a minimal game record with the best-available CoverUrl and
                        // AchievementsUrl for caching — avoids racing with
                        // EnrichLibraryFromDatabaseAsync which may be updating the same game
                        // objects concurrently on another thread.
                        bool fullyCached =
                            _metadataCache.GetCachedCoverPath(game.Platform, game.TitleId, game.Title) != null &&
                            _metadataCache.GetCachedAchievementsPath(game.Platform, game.TitleId, game.Title) != null &&
                            _metadataCache.IsGameInfoCached(game.Platform, game.TitleId, game.Title);
                        if (fullyCached)
                            continue;

                        var gameForCache = new Models.Game
                        {
                            Platform        = game.Platform,
                            Title           = game.Title,
                            TitleId         = game.TitleId,
                            CoverUrl        = coverUrl,
                            AchievementsUrl = achievementsUrl,
                        };
                        await _metadataCache.CacheGameAsync(gameForCache).ConfigureAwait(false);

                        // Save game.json with full metadata so the launcher can display
                        // rich info (name, id, cover urls, description, etc.) offline.
                        if (dbGame != null && !_metadataCache.IsGameInfoCached(game.Platform, game.TitleId, game.Title))
                        {
                            await _metadataCache.CacheGameInfoJsonAsync(
                                game.Platform, game.TitleId ?? dbGame.TitleId, game.Title,
                                dbGame).ConfigureAwait(false);
                        }
                    }
                    catch { /* best-effort */ }
                }
            }

            // Prune stale cache entries while retaining anything still present in
            // either cloud library or local scan results.
            try
            {
                var currentEntries = new List<(string Platform, string Key)>();
                foreach (var game in library)
                {
                    var key = ResolveCacheKey(game.TitleId, game.Title);
                    if (!string.IsNullOrEmpty(key))
                        currentEntries.Add((game.Platform, key));
                }

                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    foreach (var localGame in LibraryVm.LocalGames)
                    {
                        var key = ResolveCacheKey(null, localGame.Title);
                        if (!string.IsNullOrEmpty(key))
                            currentEntries.Add(("PC", key));
                    }

                    foreach (var localRom in LibraryVm.LocalRoms)
                    {
                        var key = ResolveCacheKey(localRom.TitleId, localRom.Title);
                        if (!string.IsNullOrEmpty(key))
                            currentEntries.Add((localRom.Platform, key));
                    }
                });

                _metadataCache.PruneMissingGames(currentEntries);
            }
            catch { /* best-effort */ }
        }
        finally
        {
            if (System.Threading.Interlocked.Decrement(ref _cacheTaskCount) == 0)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    IsCachingGames = false;
                    CacheSyncLabel = "";
                });
                ResetCacheSyncLabelTracking();
            }
        }
    }

    /// <summary>
    /// Background task: iterates all locally-detected games (PC games and ROMs found by the
    /// scanner) and downloads metadata + achievements.json for each one from the Games.Database
    /// — mirroring what the website shows.  Safe to call multiple times; already-cached assets
    /// are skipped.
    /// </summary>
    private async Task BackgroundCacheLocalGamesAsync()
    {
        try
        {
            // Snapshot local games on the UI thread to avoid collection-modified exceptions
            List<(string Title, string Platform, string? TitleId)> sources = new();
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var g in LibraryVm.LocalGames)
                    sources.Add((g.Title, "PC", null));
                foreach (var r in LibraryVm.LocalRoms)
                    sources.Add((r.Title, r.Platform, r.TitleId));
            });

            if (sources.Count == 0) return;

            DevLogService.Log($"[Cache] BackgroundCacheLocalGamesAsync: {sources.Count} local games to process.");

            System.Threading.Interlocked.Increment(ref _cacheTaskCount);
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                IsCachingGames = true;
            });
            UpdateCacheSyncLabel("Scanning local games…", force: true);

            // Group by platform to fetch each database at most once
            var byPlatform = sources
                .GroupBy(s => s.Platform, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var (platform, entries) in byPlatform)
                {
                    try
                    {
                        var pendingEntries = entries
                            .Where(entry =>
                                _metadataCache.GetCachedCoverPath(platform, entry.TitleId, entry.Title) == null ||
                                _metadataCache.GetCachedAchievementsPath(platform, entry.TitleId, entry.Title) == null ||
                                !_metadataCache.IsGameInfoCached(platform, entry.TitleId, entry.Title))
                            .ToList();
                        if (pendingEntries.Count == 0)
                            continue;

                        var dbGames = await GameOsClient.FetchGamesDatabaseAsync(platform).ConfigureAwait(false);
                        if (dbGames.Count == 0) continue;

                        foreach (var (title, _, titleId) in pendingEntries)
                        {
                            try
                            {
                                var dbGame = FindDatabaseGame(dbGames, title, titleId);
                                if (dbGame == null) continue;

                                // Resolve the effective cache key: scanner titleId → database titleId → title
                                string? cacheKey = titleId ?? dbGame.TitleId;

                                UpdateCacheSyncLabel($"Caching {dbGame.Title ?? title}…");

                                await _metadataCache.CacheLocalGameAsync(
                                    platform,
                                    cacheKey,
                                    title,
                                    dbGame.CoverUrl,
                                    dbGame.AchievementsUrl).ConfigureAwait(false);

                                // Save game.json with full metadata (name, id, cover urls, etc.)
                                await _metadataCache.CacheGameInfoJsonAsync(
                                    platform,
                                    cacheKey,
                                    title,
                                    dbGame).ConfigureAwait(false);
                            }
                            catch { /* best-effort per game */ }
                        }
                    }
                    catch { /* best-effort per platform */ }
                }
            }
            finally
            {
                if (System.Threading.Interlocked.Decrement(ref _cacheTaskCount) == 0)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        IsCachingGames = false;
                        CacheSyncLabel = "";
                    });
                    ResetCacheSyncLabelTracking();
                }
            }
        }
        catch (Exception ex)
        {
            DevLogService.Log($"[Cache] BackgroundCacheLocalGamesAsync failed: {ex.Message}");
        }
    }

    private static string ResolveCacheKey(string? titleId, string? title)
    {
        if (!string.IsNullOrWhiteSpace(titleId))
            return titleId;
        return title ?? "";
    }
    /// <summary>Formats the last-synced label for the Settings Sync section.</summary>
    private static string FormatLastSyncedLabel(DateTime lastSyncedAt)
    {
        if (lastSyncedAt == DateTime.MinValue) return "Last synced: never";
        var elapsed = DateTime.UtcNow - lastSyncedAt;
        if (elapsed.TotalSeconds < 60)  return "Last synced: just now";
        if (elapsed.TotalMinutes < 2)   return "Last synced: 1 minute ago";
        if (elapsed.TotalMinutes < 60)  return $"Last synced: {(int)elapsed.TotalMinutes} minutes ago";
        if (elapsed.TotalHours < 2)     return "Last synced: 1 hour ago";
        return $"Last synced: {(int)elapsed.TotalHours} hours ago";
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
                        if (!conversationOpen)
                        {
                            conversationOpen =
                                string.Equals(InboxVm.ConversationFriend, friendUsername,
                                              StringComparison.OrdinalIgnoreCase) &&
                                InboxVm.ShowConversation;
                        }

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
