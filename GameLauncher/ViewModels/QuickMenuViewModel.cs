using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Models;

namespace GameLauncher.ViewModels;

/// <summary>
/// View-model for the Quick Menu overlay (triggered by Left Shift + Left Ctrl).
/// Displays current session info, friends list, inbox preview, achievements, and
/// an "Exit Game" button that kills the tracked game process.
/// </summary>
public partial class QuickMenuViewModel : ViewModelBase
{
    // ── Current game info ──────────────────────────────────────────────────
    [ObservableProperty] private string _currentGameTitle    = "";
    [ObservableProperty] private string _currentSessionLabel = "Not playing";
    [ObservableProperty] private bool   _isPlayingGame;

    // ── Friends list ──────────────────────────────────────────────────────
    public ObservableCollection<FriendPresenceVm> OnlineFriends { get; } = new();
    public ObservableCollection<FriendPresenceVm> Friends       { get; } = new();
    [ObservableProperty] private bool _hasOnlineFriends;
    [ObservableProperty] private bool _hasFriends;

    // ── Inbox preview ─────────────────────────────────────────────────────
    [ObservableProperty] private int    _unreadMessageCount;
    [ObservableProperty] private string _lastMessagePreview = "";
    [ObservableProperty] private bool   _hasUnreadMessages;

    // ── Achievements for current game ─────────────────────────────────────
    [ObservableProperty] private int    _achievementsUnlocked;
    [ObservableProperty] private int    _achievementsTotal;
    [ObservableProperty] private string _achievementsLabel = "";
    [ObservableProperty] private bool   _hasAchievementsProgress;
    public ObservableCollection<QuickMenuAchievementVm> Achievements { get; } = new();
    [ObservableProperty] private bool _hasAchievements;

    // ── Quick menu page state ──────────────────────────────────────────────
    [ObservableProperty] private string _activePage = "home";
    public bool IsHomePage         => ActivePage == "home";
    public bool IsAchievementsPage => ActivePage == "achievements";
    public bool IsFriendsPage      => ActivePage == "friends";

    partial void OnActivePageChanged(string value)
    {
        OnPropertyChanged(nameof(IsHomePage));
        OnPropertyChanged(nameof(IsAchievementsPage));
        OnPropertyChanged(nameof(IsFriendsPage));
    }

    // ── Friend chat mini-window ───────────────────────────────────────────
    [ObservableProperty] private bool _showChatWindow;
    [ObservableProperty] private string _chatFriendUsername = "";
    [ObservableProperty] private string _chatError = "";
    [ObservableProperty] private string _newChatMessage = "";
    [ObservableProperty] private bool _isChatBusy;
    [ObservableProperty] private string _currentUsername = "";
    public ObservableCollection<Message> ChatMessages { get; } = new();

    // ── Callbacks ──────────────────────────────────────────────────────────
    /// <summary>Invoked when the user clicks "Exit Game" in the Quick Menu.</summary>
    public System.Action? OnExitGame { get; set; }

    /// <summary>Invoked when the Quick Menu should be dismissed.</summary>
    public System.Action? OnDismiss { get; set; }

    /// <summary>Invoked when the user selects "View" from a friend profile menu.</summary>
    public System.Action<string>? OnViewFriendProfile { get; set; }

    /// <summary>Loads DM history for the selected friend.</summary>
    public System.Func<string, Task<System.Collections.Generic.IReadOnlyList<Message>>>? OnLoadConversation { get; set; }

    /// <summary>Sends a DM to the selected friend; return false if send was rejected.</summary>
    public System.Func<string, string, Task<bool>>? OnSendMessage { get; set; }

    [RelayCommand]
    private void ExitGame()
    {
        OnExitGame?.Invoke();
        OnDismiss?.Invoke();
    }

    [RelayCommand]
    private void Dismiss() => OnDismiss?.Invoke();

    [RelayCommand]
    private void OpenAchievements() => ActivePage = "achievements";

    [RelayCommand]
    private void OpenFriends() => ActivePage = "friends";

    [RelayCommand]
    private void BackToHome() => ActivePage = "home";

    [RelayCommand]
    private void InviteFriend(string friendUsername)
    {
        if (string.IsNullOrWhiteSpace(friendUsername)) return;
        // Intentionally no-op for now: each game/emulator has different invite paths.
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
        ChatError          = "";
        NewChatMessage     = "";
        ShowChatWindow     = true;
        ChatMessages.Clear();

        if (OnLoadConversation == null) return;

        IsChatBusy = true;
        try
        {
            var messages = await OnLoadConversation(friendUsername);
            foreach (var message in messages.OrderBy(m => m.SentAt))
                ChatMessages.Add(message);
        }
        catch (System.Exception ex)
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
                From   = "You",
                Text   = text,
                SentAt = System.DateTimeOffset.UtcNow.ToString("o"),
            });
        }
        catch (System.Exception ex)
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

    /// <summary>
    /// Refreshes the Quick Menu with the current session data.
    /// </summary>
    /// <param name="currentGameTitle">Title of the currently running game, or null if not playing.</param>
    /// <param name="sessionStartedAt">When the current session began (UTC), used to compute elapsed time.</param>
    /// <param name="friends">Current friend list with presence info.</param>
    /// <param name="unreadCount">Number of unread direct messages.</param>
    /// <param name="lastMessage">Preview text of the most recent unread message.</param>
    /// <param name="unlockedAchievements">Count of unlocked achievements for the current game.</param>
    /// <param name="totalAchievements">Total achievements available for the current game.</param>
    public void Refresh(
        string? currentUsername,
        string? currentGameTitle,
        System.DateTime? sessionStartedAt,
        System.Collections.Generic.IReadOnlyList<FriendPresenceVm> onlineFriends,
        System.Collections.Generic.IReadOnlyList<FriendPresenceVm> allFriends,
        int unreadCount,
        string? lastMessage,
        int unlockedAchievements,
        int totalAchievements,
        System.Collections.Generic.IReadOnlyList<Achievement> achievements)
    {
        CurrentUsername    = currentUsername ?? "";
        IsPlayingGame      = !string.IsNullOrEmpty(currentGameTitle);
        CurrentGameTitle   = currentGameTitle ?? "";
        ActivePage         = "home";
        CloseChatWindow();

        if (IsPlayingGame && sessionStartedAt.HasValue)
        {
            var elapsed = System.DateTime.UtcNow - sessionStartedAt.Value;
            int mins    = (int)elapsed.TotalMinutes;
            CurrentSessionLabel = mins < 60
                ? $"Playing for {mins}m"
                : $"Playing for {mins / 60}h {mins % 60}m";
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
        foreach (var f in allFriends
                     .OrderByDescending(f => f.IsOnline)
                     .ThenBy(f => f.Username, System.StringComparer.OrdinalIgnoreCase))
            Friends.Add(f);
        HasFriends = Friends.Count > 0;

        UnreadMessageCount = unreadCount;
        LastMessagePreview = lastMessage ?? "";
        HasUnreadMessages  = unreadCount > 0;

        AchievementsUnlocked     = unlockedAchievements;
        AchievementsTotal        = totalAchievements;
        HasAchievementsProgress  = totalAchievements > 0;
        AchievementsLabel        = totalAchievements > 0
            ? $"{unlockedAchievements} / {totalAchievements} unlocked"
            : "";

        Achievements.Clear();
        foreach (var achievement in achievements)
        {
            Achievements.Add(new QuickMenuAchievementVm
            {
                Name        = achievement.Name,
                Description = achievement.Description,
                IsUnlocked  = achievement.IsUnlocked,
            });
        }
        HasAchievements = Achievements.Count > 0;
    }
}

/// <summary>A friend entry shown in the Quick Menu's friends list.</summary>
public class FriendPresenceVm
{
    public string Username    { get; set; } = "";
    public string CurrentGame { get; set; } = "";
    public string Status      { get; set; } = "Online";
    public bool   IsOnline    => !string.Equals(Status, "Offline", System.StringComparison.OrdinalIgnoreCase);
    public bool   IsPlaying   => IsOnline && !string.IsNullOrEmpty(CurrentGame);
    public string StatusLabel => IsPlaying ? $"Playing {CurrentGame}" : Status;
}

public class QuickMenuAchievementVm
{
    public string Name        { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsUnlocked    { get; set; }
    public string StatusLabel => IsUnlocked ? "Unlocked" : "Locked";
    public string StatusIcon  => IsUnlocked ? "✅" : "🔒";
}
