using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Models;

namespace GameLauncher.ViewModels;

/// <summary>
/// PS5-style quick menu view model. Provides module pages and actions:
/// Home / Switcher / Recent / Notifications / Downloads / Friends / Inbox / Media / Browser / Power.
/// </summary>
public partial class QuickMenuViewModel : ViewModelBase
{
    private const int MaxRecentGames = 5;
    private static readonly string[] HubOrder =
    {
        "home", "switcher", "recent", "notifications", "downloads",
        "friends", "inbox", "media", "browser", "power"
    };

    // ── Current game/session ────────────────────────────────────────────────
    [ObservableProperty] private string _currentGameTitle = "";
    [ObservableProperty] private string _currentSessionLabel = "Not playing";
    [ObservableProperty] private bool _isPlayingGame;
    [ObservableProperty] private string _currentUsername = "";

    // ── Header text (center panel text from PS5 ref) ───────────────────────
    [ObservableProperty] private string _menuTitle = "Home";
    [ObservableProperty] private string _menuSubtitle = "Return to the home screen.";

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
    public bool IsPowerPage         => ActivePage == "power";
    public bool IsAchievementsPage  => ActivePage == "achievements";

    // ── Friends / Inbox ─────────────────────────────────────────────────────
    public ObservableCollection<FriendPresenceVm> OnlineFriends { get; } = new();
    public ObservableCollection<FriendPresenceVm> Friends { get; } = new();
    [ObservableProperty] private bool _hasOnlineFriends;
    [ObservableProperty] private bool _hasFriends;
    [ObservableProperty] private int _unreadMessageCount;
    [ObservableProperty] private string _lastMessagePreview = "";
    [ObservableProperty] private bool _hasUnreadMessages;

    // ── Recent games / switcher / downloads ────────────────────────────────
    public ObservableCollection<LocalGameCardVm> RecentGames { get; } = new();
    public ObservableCollection<QuickMenuSwitcherItemVm> SwitcherItems { get; } = new();
    [ObservableProperty] private int _pendingDownloadCount;
    public bool HasRecentGames => RecentGames.Count > 0;
    public bool HasSwitcherItems => SwitcherItems.Count > 0;
    public bool HasPendingDownloads => PendingDownloadCount > 0;

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
    public Action<string>? OnNavigatePage { get; set; }
    public Action<LocalGameCardVm>? OnLaunchRecentGame { get; set; }
    public Action? OnOpenBrowser { get; set; }
    public Action? OnSignOut { get; set; }
    public Action? OnSwitchAccount { get; set; }
    public Action? OnExitApplication { get; set; }
    public Action? OnMediaPrevious { get; set; }
    public Action? OnMediaPlayPause { get; set; }
    public Action? OnMediaNext { get; set; }

    public QuickMenuViewModel()
    {
        RecentGames.CollectionChanged += OnCollectionsChanged;
        SwitcherItems.CollectionChanged += OnCollectionsChanged;
    }

    private void OnCollectionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasRecentGames));
        OnPropertyChanged(nameof(HasSwitcherItems));
    }

    partial void OnPendingDownloadCountChanged(int value) => OnPropertyChanged(nameof(HasPendingDownloads));

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
        OnPropertyChanged(nameof(IsPowerPage));
        OnPropertyChanged(nameof(IsAchievementsPage));
        UpdateMenuHeader(value);

        int idx = Array.IndexOf(HubOrder, value);
        if (idx >= 0 && idx != SelectedHubIndex)
            SelectedHubIndex = idx;
    }

    partial void OnSelectedHubIndexChanged(int value)
    {
        int idx = Math.Clamp(value, 0, HubOrder.Length - 1);
        if (idx != value)
        {
            SelectedHubIndex = idx;
            return;
        }

        var page = HubOrder[idx];
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

        if (!IsHomePage)
        {
            ActivePage = "home";
            return true;
        }

        return false;
    }

    public void MoveHubSelection(int delta)
    {
        if (HubOrder.Length == 0) return;
        // Wrap in both directions so negative deltas cycle from first -> last.
        int next = ((SelectedHubIndex + delta) % HubOrder.Length + HubOrder.Length) % HubOrder.Length;
        SelectedHubIndex = next;
    }

    public void ActivateSelectedHub()
    {
        if (SelectedHubIndex < 0 || SelectedHubIndex >= HubOrder.Length) return;
        ActivePage = HubOrder[SelectedHubIndex];
        if (IsBrowserSelected)
            OpenBrowser();
    }

    private bool IsBrowserSelected => HubOrder[Math.Clamp(SelectedHubIndex, 0, HubOrder.Length - 1)] == "browser";

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
    [RelayCommand] private void SelectMedia() => ActivePage = "media";
    [RelayCommand] private void SelectPower() => ActivePage = "power";
    [RelayCommand] private void OpenAchievements() => ActivePage = "achievements";
    [RelayCommand] private void BackToHome() => ActivePage = "home";

    [RelayCommand]
    private void OpenPageFromSwitcher(QuickMenuSwitcherItemVm? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.PageKey)) return;
        OnNavigatePage?.Invoke(item.PageKey);
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
        OnDismiss?.Invoke();
    }

    [RelayCommand]
    private void OpenDownloadsPage()
    {
        OnNavigatePage?.Invoke("library");
        OnDismiss?.Invoke();
    }

    [RelayCommand]
    private void OpenInboxPage()
    {
        OnNavigatePage?.Invoke("inbox");
        OnDismiss?.Invoke();
    }

    [RelayCommand]
    private void OpenFriendsPage()
    {
        OnNavigatePage?.Invoke("friends");
        OnDismiss?.Invoke();
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
    private void InviteFriend(string friendUsername)
    {
        if (string.IsNullOrWhiteSpace(friendUsername)) return;
        // Intentionally no-op for now: invite flow depends on per-game integrations.
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
        int pendingDownloadCount)
    {
        CurrentUsername = currentUsername ?? "";
        IsPlayingGame = !string.IsNullOrEmpty(currentGameTitle);
        CurrentGameTitle = currentGameTitle ?? "";
        ActivePage = "home";
        SelectedHubIndex = 0;
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
