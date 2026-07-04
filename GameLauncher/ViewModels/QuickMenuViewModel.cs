using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Models;

namespace GameLauncher.ViewModels;

/// <summary>
/// PS5-style quick menu view model. Provides module pages and actions:
/// Home / Switcher / Recent / Notifications / Downloads / Friends / Inbox / Achievements / Media / Browser / Power.
/// </summary>
public partial class QuickMenuViewModel : ViewModelBase
{
    private readonly DispatcherTimer _xb360ClockTimer;
    private const int MaxRecentGames = 5;
    private const string InvitePayloadSeparator = "|";
    private static readonly string[] HubOrder =
    {
        "home", "switcher", "recent", "notifications", "downloads",
        "friends", "inbox", "achievements", "media", "browser", "power"
    };
    // XB360 two-axis navigation arrays
    // Up/Down navigates the center list; Left/Right navigates between blade tabs.
    private static readonly string[] Xb360CenterItemKeys =
        { "home", "friends", "inbox", "notifications", "recent", "downloads", "exit" };

    private static readonly string[] Xb360BladeOrder =
        { "games", "profile", "main", "media", "settings" };

    // ── Current game/session ────────────────────────────────────────────────
    [ObservableProperty] private string _currentGameTitle = "";
    [ObservableProperty] private string _currentSessionLabel = "Not playing";
    [ObservableProperty] private bool _isPlayingGame;
    [ObservableProperty] private string _currentUsername = "";
    [ObservableProperty] private string _xb360CurrentTimeLabel = "";

    // ── Header text (center panel text from PS5 ref) ───────────────────────
    [ObservableProperty] private string _menuTitle = "Home";
    [ObservableProperty] private string _menuSubtitle = "Return to the home screen.";

    // ── Visual style selection ───────────────────────────────────────────────
    [ObservableProperty] private string _quickMenuTheme = "PS5";
    public bool IsPs5Theme => string.Equals(QuickMenuTheme, "PS5", StringComparison.OrdinalIgnoreCase);
    public bool IsXb360Theme => string.Equals(QuickMenuTheme, "XB360", StringComparison.OrdinalIgnoreCase);
    public bool IsGameOsTheme => string.Equals(QuickMenuTheme, "GameOS", StringComparison.OrdinalIgnoreCase);
    public bool IsWiiTheme => string.Equals(QuickMenuTheme, "Wii", StringComparison.OrdinalIgnoreCase);
    public bool IsSwitchTheme => string.Equals(QuickMenuTheme, "Switch", StringComparison.OrdinalIgnoreCase);
    public bool IsSteamBpmTheme => string.Equals(QuickMenuTheme, "SteamBPM", StringComparison.OrdinalIgnoreCase);
    public bool UsesHubLayout => !IsGameOsTheme && !IsWiiTheme && !IsXb360Theme;
    public bool UsesXb360GuideLayout => IsXb360Theme;

    // ── Hub page state ──────────────────────────────────────────────────────
    [ObservableProperty] private string _activePage = "home";
    [ObservableProperty] private int _selectedHubIndex;

    public bool IsHomePage          => ActivePage == "home";
    public bool IsSwitcherPage      => ActivePage == "switcher";
    public bool IsRecentGamesPage   => ActivePage == "recent";
    public bool IsNotificationsPage => ActivePage == "notifications";
    public bool IsDownloadsPage     => ActivePage == "downloads";
    public bool IsFriendsPage       => ActivePage == "friends";
    public bool IsInboxPage         => ActivePage == "inbox";
    public bool IsMediaPage         => ActivePage == "media";
    public bool IsSettingsPage      => ActivePage == "settings";
    public bool IsBrowserPage       => ActivePage == "browser";
    public bool IsPowerPage         => ActivePage == "power";
    public bool IsAchievementsPage  => ActivePage == "achievements";

    // ── XB360 two-axis navigation state ────────────────────────────────────
    // Xb360CenterIndex  = Up/Down cursor within the center item list (0–6).
    // Xb360BladeId      = Left/Right blade tab: "games"|"profile"|"main"|"media"|"settings".
    [ObservableProperty] private int _xb360CenterIndex;
    [ObservableProperty] private string _xb360BladeId = "main";

    // Cursor booleans — used by XAML to highlight the Up/Down position.
    public bool Xb360CursorHome          => IsXb360Theme && Xb360BladeId == "main" && Xb360CenterIndex == 0;
    public bool Xb360CursorFriends       => IsXb360Theme && Xb360BladeId == "main" && Xb360CenterIndex == 1;
    public bool Xb360CursorInbox         => IsXb360Theme && Xb360BladeId == "main" && Xb360CenterIndex == 2;
    public bool Xb360CursorNotifications => IsXb360Theme && Xb360BladeId == "main" && Xb360CenterIndex == 3;
    public bool Xb360CursorRecent        => IsXb360Theme && Xb360BladeId == "main" && Xb360CenterIndex == 4;
    public bool Xb360CursorDownloads     => IsXb360Theme && Xb360BladeId == "main" && Xb360CenterIndex == 5;
    public bool Xb360CursorExit          => IsXb360Theme && Xb360BladeId == "main" && Xb360CenterIndex == 6;

    // Blade-active booleans — used by XAML to highlight the Left/Right blade tab.
    public bool Xb360GamesBladeActive    => IsXb360Theme && Xb360BladeId == "games";
    public bool Xb360ProfileBladeActive  => IsXb360Theme && Xb360BladeId == "profile";
    public bool Xb360MainBladeActive     => IsXb360Theme && Xb360BladeId == "main";
    public bool Xb360MediaBladeActive    => IsXb360Theme && Xb360BladeId == "media";
    public bool Xb360SettingsBladeActive => IsXb360Theme && Xb360BladeId == "settings";

    // ── Friends / Inbox ─────────────────────────────────────────────────────
    public ObservableCollection<FriendPresenceVm> OnlineFriends { get; } = new();
    public ObservableCollection<FriendPresenceVm> Friends { get; } = new();
    public System.Collections.Generic.IReadOnlyList<FriendPresenceVm> QuickFriends
        => Friends.Take(6).ToList();
    [ObservableProperty] private bool _hasOnlineFriends;
    [ObservableProperty] private bool _hasFriends;
    [ObservableProperty] private int _unreadMessageCount;
    [ObservableProperty] private string _lastMessagePreview = "";
    [ObservableProperty] private bool _hasUnreadMessages;
    [ObservableProperty] private string _currentGamePlatform = "";
    [ObservableProperty] private string _inviteStatusMessage = "";

    // ── Recent games / switcher / downloads ────────────────────────────────
    public ObservableCollection<LocalGameCardVm> RecentGames { get; } = new();
    public ObservableCollection<QuickMenuSwitcherItemVm> SwitcherItems { get; } = new();
    [ObservableProperty] private int _pendingDownloadCount;
    public bool HasRecentGames => RecentGames.Count > 0;
    public bool HasSwitcherItems => SwitcherItems.Count > 0;
    public bool HasPendingDownloads => PendingDownloadCount > 0;
    public int Xb360RecentGamesCount => RecentGames.Count;
    public int Xb360OnlineFriendsCount => OnlineFriends.Count;
    public int Xb360FriendsCount => Friends.Count;
    public int Xb360MessagesCount => UnreadMessageCount;
    public int Xb360DownloadsCount => PendingDownloadCount;
    public string Xb360UserInitial
    {
        get
        {
            var trimmed = (CurrentUsername ?? "").Trim();
            return trimmed.Length == 0 ? "?" : trimmed[0].ToString().ToUpperInvariant();
        }
    }

    // ── Achievements ────────────────────────────────────────────────────────
    [ObservableProperty] private int _achievementsUnlocked;
    [ObservableProperty] private int _achievementsTotal;
    [ObservableProperty] private string _achievementsLabel = "";
    [ObservableProperty] private bool _hasAchievementsProgress;
    public ObservableCollection<QuickMenuAchievementVm> Achievements { get; } = new();
    [ObservableProperty] private bool _hasAchievements;

    // ── Media ───────────────────────────────────────────────────────────────
    [ObservableProperty] private string _mediaStatusLabel = "Media controls are ready.";

    // ── Friend chat mini-window ────────────────────────────────────────────
    [ObservableProperty] private bool _showChatWindow;
    [ObservableProperty] private string _chatFriendUsername = "";
    [ObservableProperty] private string _chatError = "";
    [ObservableProperty] private string _newChatMessage = "";
    [ObservableProperty] private bool _isChatBusy;
    public ObservableCollection<Message> ChatMessages { get; } = new();

    // ── Callbacks wired by MainViewModel ────────────────────────────────────
    public Action? OnExitGame { get; set; }
    public Action? OnDismiss { get; set; }
    public Action<string>? OnViewFriendProfile { get; set; }
    public Func<string, Task<System.Collections.Generic.IReadOnlyList<Message>>>? OnLoadConversation { get; set; }
    public Func<string, string, Task<bool>>? OnSendMessage { get; set; }
    public Func<string, string, string, string, Task<bool>>? OnInviteFriend { get; set; }
    public Action<string>? OnNavigatePage { get; set; }
    public Action<LocalGameCardVm>? OnLaunchRecentGame { get; set; }
    public Action? OnOpenBrowser { get; set; }
    public Action? OnSignOut { get; set; }
    public Action? OnSwitchAccount { get; set; }
    public Action? OnExitApplication { get; set; }
    public Action? OnRequestLauncherForeground { get; set; }
    public Action? OnMediaPrevious { get; set; }
    public Action? OnMediaPlayPause { get; set; }
    public Action? OnMediaNext { get; set; }

    public QuickMenuViewModel()
    {
        _xb360ClockTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _xb360ClockTimer.Tick += (_, _) =>
        {
            if (IsXb360Theme)
                Xb360CurrentTimeLabel = DateTime.Now.ToString("h:mm tt");
        };
        _xb360ClockTimer.Start();

        RecentGames.CollectionChanged += OnCollectionsChanged;
        SwitcherItems.CollectionChanged += OnCollectionsChanged;
        OnlineFriends.CollectionChanged += OnCollectionsChanged;
        Friends.CollectionChanged += OnCollectionsChanged;
    }

    private void OnCollectionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasRecentGames));
        OnPropertyChanged(nameof(HasSwitcherItems));
        OnPropertyChanged(nameof(Xb360RecentGamesCount));
        OnPropertyChanged(nameof(Xb360OnlineFriendsCount));
        OnPropertyChanged(nameof(Xb360FriendsCount));
        OnPropertyChanged(nameof(QuickFriends));
    }

    partial void OnPendingDownloadCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasPendingDownloads));
        OnPropertyChanged(nameof(Xb360DownloadsCount));
    }

    partial void OnCurrentUsernameChanged(string value) => OnPropertyChanged(nameof(Xb360UserInitial));
    partial void OnUnreadMessageCountChanged(int value) => OnPropertyChanged(nameof(Xb360MessagesCount));
    partial void OnQuickMenuThemeChanged(string value)
    {
        OnPropertyChanged(nameof(IsPs5Theme));
        OnPropertyChanged(nameof(IsXb360Theme));
        OnPropertyChanged(nameof(IsGameOsTheme));
        OnPropertyChanged(nameof(IsWiiTheme));
        OnPropertyChanged(nameof(IsSwitchTheme));
        OnPropertyChanged(nameof(IsSteamBpmTheme));
        OnPropertyChanged(nameof(UsesHubLayout));
        OnPropertyChanged(nameof(UsesXb360GuideLayout));
        NotifyXb360BladeProps();
        NotifyXb360CursorProps();
    }

    partial void OnXb360CenterIndexChanged(int value) => NotifyXb360CursorProps();
    partial void OnXb360BladeIdChanged(string value)
    {
        NotifyXb360BladeProps();
        NotifyXb360CursorProps();
    }

    private void NotifyXb360CursorProps()
    {
        OnPropertyChanged(nameof(Xb360CursorHome));
        OnPropertyChanged(nameof(Xb360CursorFriends));
        OnPropertyChanged(nameof(Xb360CursorInbox));
        OnPropertyChanged(nameof(Xb360CursorNotifications));
        OnPropertyChanged(nameof(Xb360CursorRecent));
        OnPropertyChanged(nameof(Xb360CursorDownloads));
        OnPropertyChanged(nameof(Xb360CursorExit));
    }

    private void NotifyXb360BladeProps()
    {
        OnPropertyChanged(nameof(Xb360GamesBladeActive));
        OnPropertyChanged(nameof(Xb360ProfileBladeActive));
        OnPropertyChanged(nameof(Xb360MainBladeActive));
        OnPropertyChanged(nameof(Xb360MediaBladeActive));
        OnPropertyChanged(nameof(Xb360SettingsBladeActive));
    }

    partial void OnActivePageChanged(string value)
    {
        OnPropertyChanged(nameof(IsHomePage));
        OnPropertyChanged(nameof(IsSwitcherPage));
        OnPropertyChanged(nameof(IsRecentGamesPage));
        OnPropertyChanged(nameof(IsNotificationsPage));
        OnPropertyChanged(nameof(IsDownloadsPage));
        OnPropertyChanged(nameof(IsFriendsPage));
        OnPropertyChanged(nameof(IsInboxPage));
        OnPropertyChanged(nameof(IsMediaPage));
        OnPropertyChanged(nameof(IsSettingsPage));
        OnPropertyChanged(nameof(IsBrowserPage));
        OnPropertyChanged(nameof(IsPowerPage));
        OnPropertyChanged(nameof(IsAchievementsPage));
        UpdateMenuHeader(value);

        // XB360 uses Xb360CenterIndex/BladeId for navigation — do not sync ActivePage ↔ SelectedHubIndex.
        if (IsXb360Theme) return;

        var navigationOrder = GetCurrentNavigationOrder();
        int idx = Array.IndexOf(navigationOrder, value);
        if (idx >= 0 && idx != SelectedHubIndex)
            SelectedHubIndex = idx;
    }

    partial void OnSelectedHubIndexChanged(int value)
    {
        // XB360 uses Xb360CenterIndex/BladeId — SelectedHubIndex is not relevant there.
        if (IsXb360Theme) return;

        var navigationOrder = GetCurrentNavigationOrder();
        if (navigationOrder.Length == 0) return;

        int idx = Math.Clamp(value, 0, navigationOrder.Length - 1);
        if (idx != value)
        {
            SelectedHubIndex = idx;
            return;
        }

        var page = navigationOrder[idx];
        if (!string.Equals(ActivePage, page, StringComparison.Ordinal))
            ActivePage = page;
    }

    public bool HandleBackNavigation()
    {
        if (ShowChatWindow)
        {
            CloseChatWindow();
            return true;
        }

        // XB360: if a side blade is active, return to the main center list.
        if (IsXb360Theme && Xb360BladeId != "main")
        {
            Xb360BladeId = "main";
            return true;
        }

        if (!IsHomePage)
        {
            ActivePage = "home";
            return true;
        }

        return false;
    }

    public void MoveHubSelection(int delta)
    {
        // XB360 uses MoveXb360Blade / MoveXb360CenterItem instead.
        if (IsXb360Theme) return;

        var navigationOrder = GetCurrentNavigationOrder();
        if (navigationOrder.Length == 0) return;
        // Wrap in both directions so negative deltas cycle from first -> last.
        int next = ((SelectedHubIndex + delta) % navigationOrder.Length + navigationOrder.Length) % navigationOrder.Length;
        SelectedHubIndex = next;
    }

    /// <summary>Moves the Up/Down cursor within the XB360 center item list.</summary>
    public void MoveXb360CenterItem(int delta)
    {
        if (!IsXb360Theme || Xb360BladeId != "main") return;
        Xb360CenterIndex = Math.Clamp(Xb360CenterIndex + delta, 0, Xb360CenterItemKeys.Length - 1);
    }

    /// <summary>Moves the Left/Right selection between XB360 blade tabs.</summary>
    public void MoveXb360Blade(int delta)
    {
        if (!IsXb360Theme) return;
        int idx = Array.IndexOf(Xb360BladeOrder, Xb360BladeId);
        if (idx < 0) idx = 2; // default: "main"
        Xb360BladeId = Xb360BladeOrder[Math.Clamp(idx + delta, 0, Xb360BladeOrder.Length - 1)];
    }

    public void ActivateSelectedHub()
    {
        if (IsXb360Theme)
        {
            ActivateXb360Current();
            return;
        }

        var navigationOrder = GetCurrentNavigationOrder();
        if (SelectedHubIndex < 0 || SelectedHubIndex >= navigationOrder.Length) return;
        var page = navigationOrder[SelectedHubIndex];
        ActivePage = page;
    }

    /// <summary>Activates the current XB360 selection (Enter / A-button).</summary>
    private void ActivateXb360Current()
    {
        switch (Xb360BladeId)
        {
            case "media":
                OpenMediaPageCommand.Execute(null);
                return;
            case "settings":
                OpenSettingsPageCommand.Execute(null);
                return;
            case "profile":
                OpenProfilePageCommand.Execute(null);
                return;
            case "games":
                OpenLibraryPageCommand.Execute(null);
                return;
        }

        // Center list: primary action for cursor item.
        if (Xb360CenterIndex < 0 || Xb360CenterIndex >= Xb360CenterItemKeys.Length) return;
        switch (Xb360CenterItemKeys[Xb360CenterIndex])
        {
            case "home":
                GoToGameOsHomeCommand.Execute(null);
                break;
            case "friends":
                OpenFriendsPageCommand.Execute(null);
                break;
            case "inbox":
                OpenInboxPageCommand.Execute(null);
                break;
            case "notifications":
                OpenNotificationsPageCommand.Execute(null);
                break;
            case "recent":
                OnNavigatePage?.Invoke("library");
                OnRequestLauncherForeground?.Invoke();
                OnDismiss?.Invoke();
                break;
            case "downloads":
                OpenDownloadsPageCommand.Execute(null);
                break;
            case "exit":
                PowerSignOutCommand.Execute(null);
                break;
        }
    }

    private void UpdateMenuHeader(string page)
    {
        switch (page)
        {
            case "home":
                MenuTitle = "Home";
                MenuSubtitle = "Return to the home screen.";
                break;
            case "switcher":
                MenuTitle = "Switcher";
                MenuSubtitle = "Switch between launcher pages quickly.";
                break;
            case "recent":
                MenuTitle = "Recent Games";
                MenuSubtitle = "View and launch your recent games.";
                break;
            case "notifications":
                MenuTitle = "Notifications";
                MenuSubtitle = "Review invites and unread messages.";
                break;
            case "downloads":
                MenuTitle = "Downloads";
                MenuSubtitle = "Manage queued installs and updates.";
                break;
            case "friends":
                MenuTitle = "Friends";
                MenuSubtitle = "View online status and open profiles.";
                break;
            case "inbox":
                MenuTitle = "Inbox";
                MenuSubtitle = "Open your messages and party invites.";
                break;
            case "media":
                MenuTitle = "Media";
                MenuSubtitle = "Control global media playback.";
                break;
            case "settings":
                MenuTitle = "Settings";
                MenuSubtitle = "Open launcher settings.";
                break;
            case "browser":
                MenuTitle = "Browser";
                MenuSubtitle = "Open your default web browser.";
                break;
            case "power":
                MenuTitle = "Power";
                MenuSubtitle = "Sign out, switch account, or exit Game.OS.";
                break;
            case "achievements":
                MenuTitle = "Achievements";
                MenuSubtitle = "View current game achievement progress.";
                break;
            default:
                MenuTitle = "Quick Menu";
                MenuSubtitle = "Choose an option.";
                break;
        }
    }

    [RelayCommand]
    private void ExitGame()
    {
        OnExitGame?.Invoke();
        OnDismiss?.Invoke();
    }

    [RelayCommand]
    private void Dismiss() => OnDismiss?.Invoke();

    [RelayCommand] private void SelectHome() => ActivePage = "home";
    [RelayCommand] private void SelectSwitcher() => ActivePage = "switcher";
    [RelayCommand] private void SelectRecentGames() => ActivePage = "recent";
    [RelayCommand] private void SelectNotifications() => ActivePage = "notifications";
    [RelayCommand] private void SelectDownloads() => ActivePage = "downloads";
    [RelayCommand] private void SelectFriends() => ActivePage = "friends";
    [RelayCommand] private void SelectInbox() => ActivePage = "inbox";
    [RelayCommand] private void SelectBrowser() => ActivePage = "browser";
    [RelayCommand] private void SelectMedia() => ActivePage = "media";
    [RelayCommand] private void SelectPower() => ActivePage = "power";
    [RelayCommand] private void OpenAchievements() => ActivePage = "achievements";
    [RelayCommand] private void BackToHome() => ActivePage = "home";
    [RelayCommand] private void OpenSettingsPage()
    {
        OnNavigatePage?.Invoke("settings");
        OnRequestLauncherForeground?.Invoke();
        OnDismiss?.Invoke();
    }
    [RelayCommand] private void GoToGameOsHome()
    {
        OnNavigatePage?.Invoke("dashboard");
        OnRequestLauncherForeground?.Invoke();
        OnDismiss?.Invoke();
    }

    [RelayCommand]
    private void OpenPageFromSwitcher(QuickMenuSwitcherItemVm? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.PageKey)) return;
        OnNavigatePage?.Invoke(item.PageKey);
        OnRequestLauncherForeground?.Invoke();
        OnDismiss?.Invoke();
    }

    [RelayCommand]
    private void LaunchRecentGame(LocalGameCardVm? card)
    {
        if (card == null) return;
        OnLaunchRecentGame?.Invoke(card);
        OnDismiss?.Invoke();
    }

    [RelayCommand]
    private void OpenNotificationsPage()
    {
        OnNavigatePage?.Invoke("inbox");
        OnRequestLauncherForeground?.Invoke();
        OnDismiss?.Invoke();
    }

    [RelayCommand]
    private void OpenDownloadsPage()
    {
        OnNavigatePage?.Invoke("library");
        OnRequestLauncherForeground?.Invoke();
        OnDismiss?.Invoke();
    }

    [RelayCommand]
    private void OpenInboxPage()
    {
        OnNavigatePage?.Invoke("inbox");
        OnRequestLauncherForeground?.Invoke();
        OnDismiss?.Invoke();
    }

    [RelayCommand]
    private void OpenFriendsPage()
    {
        OnNavigatePage?.Invoke("friends");
        OnRequestLauncherForeground?.Invoke();
        OnDismiss?.Invoke();
    }

    [RelayCommand]
    private void OpenProfilePage()
    {
        OnNavigatePage?.Invoke("profile");
        OnRequestLauncherForeground?.Invoke();
        OnDismiss?.Invoke();
    }

    [RelayCommand]
    private void OpenLibraryPage()
    {
        OnNavigatePage?.Invoke("library");
        OnRequestLauncherForeground?.Invoke();
        OnDismiss?.Invoke();
    }

    [RelayCommand]
    private void OpenStorePage()
    {
        OnNavigatePage?.Invoke("store");
        OnRequestLauncherForeground?.Invoke();
        OnDismiss?.Invoke();
    }

    [RelayCommand]
    private void OpenMediaPage()
    {
        OnNavigatePage?.Invoke("media");
        OnRequestLauncherForeground?.Invoke();
        OnDismiss?.Invoke();
    }

    [RelayCommand]
    private void SwitchToXb360Blade(string? bladeId)
    {
        if (IsXb360Theme && bladeId != null)
            Xb360BladeId = bladeId;
    }

    [RelayCommand]
    private void OpenBrowser()
    {
        OnOpenBrowser?.Invoke();
        OnDismiss?.Invoke();
    }

    [RelayCommand]
    private void PowerSignOut()
    {
        OnSignOut?.Invoke();
        OnDismiss?.Invoke();
    }

    [RelayCommand]
    private void PowerSwitchAccount()
    {
        OnSwitchAccount?.Invoke();
        OnDismiss?.Invoke();
    }

    [RelayCommand]
    private void PowerExitApplication()
    {
        OnExitApplication?.Invoke();
    }

    [RelayCommand]
    private void MediaPrevious()
    {
        OnMediaPrevious?.Invoke();
        MediaStatusLabel = "Previous track";
    }

    [RelayCommand]
    private void MediaPlayPause()
    {
        OnMediaPlayPause?.Invoke();
        MediaStatusLabel = "Play / Pause";
    }

    [RelayCommand]
    private void MediaNext()
    {
        OnMediaNext?.Invoke();
        MediaStatusLabel = "Next track";
    }

    [RelayCommand]
    private async Task InviteFriend(string friendUsername)
    {
        if (string.IsNullOrWhiteSpace(friendUsername)) return;
        await InviteFriendWithConnection(friendUsername, "Crossplay");
    }

    [RelayCommand]
    private async Task InviteFriendViaConnection(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return;

        string[] parts = payload.Split(InvitePayloadSeparator, 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return;

        await InviteFriendWithConnection(parts[0], parts[1]);
    }

    private async Task InviteFriendWithConnection(string friendUsername, string connectionType)
    {
        if (string.IsNullOrWhiteSpace(friendUsername) ||
            string.IsNullOrWhiteSpace(connectionType))
            return;

        string gameName = (CurrentGameTitle ?? "").Trim();
        string platform = string.IsNullOrWhiteSpace(CurrentGamePlatform) ? "PC" : CurrentGamePlatform.Trim();
        if (string.IsNullOrWhiteSpace(gameName))
        {
            InviteStatusMessage = "Start a game first, then send an invite from Quick Menu.";
            return;
        }

        if (OnInviteFriend == null)
        {
            InviteStatusMessage = "Invite service is unavailable right now.";
            return;
        }

        InviteStatusMessage = "";
        try
        {
            bool sent = await OnInviteFriend(friendUsername, gameName, platform, connectionType);
            InviteStatusMessage = sent
                ? $"Invite sent: {friendUsername} → {gameName} ({platform}, {connectionType})"
                : "Invite was not sent.";
        }
        catch (Exception ex)
        {
            InviteStatusMessage = $"Invite failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ViewFriend(string friendUsername)
    {
        if (string.IsNullOrWhiteSpace(friendUsername)) return;
        OnViewFriendProfile?.Invoke(friendUsername);
        OnDismiss?.Invoke();
    }

    [RelayCommand]
    private async Task MessageFriend(string friendUsername)
    {
        if (string.IsNullOrWhiteSpace(friendUsername)) return;

        ChatFriendUsername = friendUsername;
        ChatError = "";
        NewChatMessage = "";
        ShowChatWindow = true;
        ChatMessages.Clear();

        if (OnLoadConversation == null) return;

        IsChatBusy = true;
        try
        {
            var messages = await OnLoadConversation(friendUsername);
            foreach (var message in messages.OrderBy(m => m.SentAt))
                ChatMessages.Add(message);
        }
        catch (Exception ex)
        {
            ChatError = $"Could not load messages: {ex.Message}";
        }
        finally
        {
            IsChatBusy = false;
        }
    }

    [RelayCommand]
    private async Task SendChatMessage()
    {
        if (string.IsNullOrWhiteSpace(NewChatMessage) ||
            string.IsNullOrWhiteSpace(ChatFriendUsername) ||
            OnSendMessage == null) return;

        string text = NewChatMessage.Trim();
        NewChatMessage = "";
        ChatError = "";
        IsChatBusy = true;
        try
        {
            bool sent = await OnSendMessage(ChatFriendUsername, text);
            if (!sent)
            {
                ChatError = "Message was not sent.";
                NewChatMessage = text;
                return;
            }

            ChatMessages.Add(new Message
            {
                From = "You",
                Text = text,
                SentAt = DateTimeOffset.UtcNow.ToString("o"),
            });
        }
        catch (Exception ex)
        {
            ChatError = $"Send failed: {ex.Message}";
            NewChatMessage = text;
        }
        finally
        {
            IsChatBusy = false;
        }
    }

    [RelayCommand]
    private void CloseChatWindow()
    {
        ShowChatWindow = false;
        ChatFriendUsername = "";
        ChatError = "";
        NewChatMessage = "";
        ChatMessages.Clear();
    }

    public void Refresh(
        string? currentUsername,
        string? currentGameTitle,
        string? currentGamePlatform,
        DateTime? sessionStartedAt,
        System.Collections.Generic.IReadOnlyList<FriendPresenceVm> onlineFriends,
        System.Collections.Generic.IReadOnlyList<FriendPresenceVm> allFriends,
        int unreadCount,
        string? lastMessage,
        int unlockedAchievements,
        int totalAchievements,
        System.Collections.Generic.IReadOnlyList<Achievement> achievements,
        System.Collections.Generic.IReadOnlyList<LocalGameCardVm>? recentGames,
        string activePageKey,
        int pendingDownloadCount,
        string quickMenuTheme)
    {
        QuickMenuTheme = NormaliseQuickMenuTheme(quickMenuTheme);
        CurrentUsername = currentUsername ?? "";
        CurrentGamePlatform = currentGamePlatform ?? "";
        Xb360CurrentTimeLabel = DateTime.Now.ToString("h:mm tt");
        IsPlayingGame = !string.IsNullOrEmpty(currentGameTitle);
        CurrentGameTitle = currentGameTitle ?? "";
        ActivePage = "home";
        SelectedHubIndex = 0;
        Xb360CenterIndex = 0;
        Xb360BladeId = "main";
        CloseChatWindow();

        if (IsPlayingGame && sessionStartedAt.HasValue)
        {
            var elapsed = DateTime.UtcNow - sessionStartedAt.Value;
            int mins = (int)elapsed.TotalMinutes;
            CurrentSessionLabel = mins < 60 ? $"Playing for {mins}m" : $"Playing for {mins / 60}h {mins % 60}m";
        }
        else
        {
            CurrentSessionLabel = "Not playing";
        }

        OnlineFriends.Clear();
        foreach (var f in onlineFriends)
            OnlineFriends.Add(f);
        HasOnlineFriends = OnlineFriends.Count > 0;

        Friends.Clear();
        foreach (var f in allFriends.OrderByDescending(f => f.IsOnline).ThenBy(f => f.Username, StringComparer.OrdinalIgnoreCase))
            Friends.Add(f);
        HasFriends = Friends.Count > 0;

        UnreadMessageCount = unreadCount;
        LastMessagePreview = lastMessage ?? "";
        HasUnreadMessages = unreadCount > 0;

        PendingDownloadCount = Math.Max(0, pendingDownloadCount);

        RecentGames.Clear();
        if (recentGames != null)
        {
            foreach (var card in recentGames.Take(MaxRecentGames))
                RecentGames.Add(card);
        }

        SwitcherItems.Clear();
        AddSwitcher("dashboard", "Home", "Return to dashboard", activePageKey);
        AddSwitcher("library", "Library", "Browse installed games", activePageKey);
        AddSwitcher("store", "Store", "Browse game store", activePageKey);
        AddSwitcher("friends", "Friends", "View friend presence", activePageKey);
        AddSwitcher("inbox", "Inbox", "Messages and invites", activePageKey);
        AddSwitcher("profile", "Profile", "View profile and stats", activePageKey);
        AddSwitcher("settings", "Settings", "System and app settings", activePageKey);

        AchievementsUnlocked = unlockedAchievements;
        AchievementsTotal = totalAchievements;
        HasAchievementsProgress = totalAchievements > 0;
        AchievementsLabel = totalAchievements > 0
            ? $"{unlockedAchievements} / {totalAchievements} unlocked"
            : "";

        Achievements.Clear();
        foreach (var achievement in achievements)
        {
            Achievements.Add(new QuickMenuAchievementVm
            {
                Name = achievement.Name,
                Description = achievement.Description,
                IsUnlocked = achievement.IsUnlocked,
            });
        }
        HasAchievements = Achievements.Count > 0;
        MediaStatusLabel = "Media controls are ready.";
    }

    private string[] GetCurrentNavigationOrder() => HubOrder;

    private static string NormaliseQuickMenuTheme(string value)
    {
        var v = (value ?? "").Trim();
        if (string.Equals(v, "XB360", StringComparison.OrdinalIgnoreCase)) return "XB360";
        if (string.Equals(v, "GameOS", StringComparison.OrdinalIgnoreCase)) return "GameOS";
        if (string.Equals(v, "Wii", StringComparison.OrdinalIgnoreCase)) return "Wii";
        if (string.Equals(v, "Switch", StringComparison.OrdinalIgnoreCase)) return "Switch";
        if (string.Equals(v, "SteamBPM", StringComparison.OrdinalIgnoreCase)) return "SteamBPM";
        return "PS5";
    }

    private void AddSwitcher(string pageKey, string title, string subtitle, string activePageKey)
    {
        SwitcherItems.Add(new QuickMenuSwitcherItemVm
        {
            PageKey = pageKey,
            Title = title,
            Subtitle = subtitle,
            IsActive = string.Equals(pageKey, activePageKey, StringComparison.OrdinalIgnoreCase),
        });
    }
}

public class FriendPresenceVm
{
    public string Username { get; set; } = "";
    public string CurrentGame { get; set; } = "";
    public string Status { get; set; } = "Online";
    public bool IsOnline => !string.Equals(Status, "Offline", StringComparison.OrdinalIgnoreCase);
    public bool IsPlaying => IsOnline && !string.IsNullOrEmpty(CurrentGame);
    public string StatusLabel => IsPlaying ? $"Playing {CurrentGame}" : Status;
}

public class QuickMenuAchievementVm
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsUnlocked { get; set; }
    public string StatusLabel => IsUnlocked ? "Unlocked" : "Locked";
    public string StatusIcon => IsUnlocked ? "✅" : "🔒";
}

public class QuickMenuSwitcherItemVm
{
    public string PageKey { get; set; } = "";
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public bool IsActive { get; set; }
}
