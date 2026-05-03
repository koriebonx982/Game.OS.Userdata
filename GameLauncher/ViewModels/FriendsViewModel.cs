using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Models;

namespace GameLauncher.ViewModels;

/// <summary>
/// View-model for the Friends screen.  Loads the friend list and incoming
/// requests from the Game.OS backend API, or shows demo friend data when
/// running in demo mode (no backend connected).
/// Also manages the inline direct-message conversation panel.
/// </summary>
public partial class FriendsViewModel : ViewModelBase
{
    [ObservableProperty] private int    _onlineCount;
    [ObservableProperty] private int    _totalCount;
    [ObservableProperty] private bool   _isLoading  = false;
    [ObservableProperty] private string _errorMessage = "";

    // ── Messaging panel ───────────────────────────────────────────────────────
    [ObservableProperty] private bool   _showConversation;
    [ObservableProperty] private string _conversationFriend = "";
    [ObservableProperty] private string _newMessageText     = "";
    [ObservableProperty] private bool   _isSendingMessage;
    [ObservableProperty] private string _messageError       = "";

    public ObservableCollection<Message> ConversationMessages { get; } = new();

    public bool HasNoFriends      => TotalCount == 0 && !IsLoading;
    public bool HasRecentActivity => RecentActivity.Count > 0;

    partial void OnTotalCountChanged(int value)  => OnPropertyChanged(nameof(HasNoFriends));
    partial void OnIsLoadingChanged(bool value)  => OnPropertyChanged(nameof(HasNoFriends));

    public ObservableCollection<FriendEntry>          OnlineFriends   { get; } = new();
    public ObservableCollection<FriendEntry>          OfflineFriends  { get; } = new();
    public ObservableCollection<FriendRequestDisplay> PendingRequests { get; } = new();
    /// <summary>Recent activity feed — what friends have been playing recently.</summary>
    public ObservableCollection<FriendActivityItem>   RecentActivity  { get; } = new();

    private GameOsClient? _client;
    private string        _username = "";

    /// <summary>Invoked when the user clicks "View Profile" on a friend row.</summary>
    public System.Action<string>? OnViewFriendProfile { get; set; }

    public void Load(GameOsClient client, string username)
    {
        _client   = client;
        _username = username;
        _ = LoadAsync();
    }

    /// <summary>
    /// Populates the friends screen with demo data — used when running in
    /// demo / offline mode so the Friends page shows realistic content.
    /// </summary>
    public void LoadDemo()
    {
        IsLoading     = false;
        ErrorMessage  = "";

        OnlineFriends.Clear();
        OfflineFriends.Clear();
        PendingRequests.Clear();
        RecentActivity.Clear();

        // Demo online friends — include what they're currently playing
        OnlineFriends.Add(new FriendEntry
        {
            Username = "NintendoFan42", Status = "Online", LastSeen = "Now",
            CurrentGame = "Mario Kart 8 Deluxe", RecentGameTitle = "Mario Kart 8 Deluxe",
            RecentGamePlatform = "Switch"
        });
        OnlineFriends.Add(new FriendEntry
        {
            Username = "SwitchPlayer99", Status = "Away", LastSeen = "12 min ago",
            CurrentGame = "Pokémon Scarlet", RecentGameTitle = "Pokémon Scarlet",
            RecentGamePlatform = "Switch"
        });
        OnlineFriends.Add(new FriendEntry
        {
            Username = "GamingWithLex", Status = "Online", LastSeen = "Now",
            CurrentGame = "Elden Ring", RecentGameTitle = "Elden Ring",
            RecentGamePlatform = "PC"
        });

        // Demo offline friends — show their last played game
        OfflineFriends.Add(new FriendEntry
        {
            Username = "ProGamer2025", Status = "Offline", LastSeen = "3h ago",
            RecentGameTitle = "Call of Duty: Warzone", RecentGamePlatform = "PC"
        });
        OfflineFriends.Add(new FriendEntry
        {
            Username = "RetroKing", Status = "Offline", LastSeen = "1 Mar",
            RecentGameTitle = "Halo 3", RecentGamePlatform = "Xbox 360"
        });
        OfflineFriends.Add(new FriendEntry
        {
            Username = "SpeedRunner7", Status = "Offline", LastSeen = "28 Feb",
            RecentGameTitle = "Celeste", RecentGamePlatform = "PC"
        });

        // Demo pending request
        PendingRequests.Add(new FriendRequestDisplay
        {
            FromUsername = "MKDeluxeChamp",
            SentAgo      = "2 hours ago"
        });

        // Demo recent activity feed
        RecentActivity.Add(new FriendActivityItem
        {
            Username = "NintendoFan42", GameTitle = "Mario Kart 8 Deluxe", Platform = "Switch",
            TimeAgo = "Just now", ActivityText = "is playing", Icon = "🎮", SortKey = 0
        });
        RecentActivity.Add(new FriendActivityItem
        {
            Username = "GamingWithLex", GameTitle = "Elden Ring", Platform = "PC",
            TimeAgo = "Just now", ActivityText = "is playing", Icon = "🎮", SortKey = 0
        });
        RecentActivity.Add(new FriendActivityItem
        {
            Username = "SwitchPlayer99", GameTitle = "Pokémon Scarlet", Platform = "Switch",
            TimeAgo = "12 min ago", ActivityText = "played for 45m", Icon = "🎮", SortKey = 1
        });
        RecentActivity.Add(new FriendActivityItem
        {
            Username = "ProGamer2025", GameTitle = "Call of Duty: Warzone", Platform = "PC",
            TimeAgo = "3 hours ago", ActivityText = "played for 2h 10m", Icon = "🎮", SortKey = 2
        });
        RecentActivity.Add(new FriendActivityItem
        {
            Username = "RetroKing", GameTitle = "Halo 3", Platform = "Xbox 360",
            TimeAgo = "1 Mar", ActivityText = "played for 1h 30m", Icon = "🎮", SortKey = 3
        });

        OnPropertyChanged(nameof(HasRecentActivity));
        OnlineCount = OnlineFriends.Count;
        TotalCount  = OnlineFriends.Count + OfflineFriends.Count;
    }

    /// <summary>
    /// Pre-opens a demo conversation with realistic messages for screenshot mode.
    /// Called after <see cref="LoadDemo"/> so the Friends page shows an active inbox.
    /// </summary>
    public void OpenConversationDemo()
    {
        ConversationFriend = "NintendoFan42";
        ConversationMessages.Clear();
        MessageError = "";
        ShowConversation = true;

        // Simulate a realistic message thread
        var msgs = new[]
        {
            new Message { From = "NintendoFan42", Text = "Dude, have you tried the new DLC for Elden Ring yet?!",            SentAt = "2026-03-10T18:15:00Z" },
            new Message { From = "Koriebonx98",   Text = "Yeah, Shadow of the Erdtree is insane 😤 already 12 hours in",     SentAt = "2026-03-10T18:17:00Z" },
            new Message { From = "NintendoFan42", Text = "Haha same, the final boss is brutal. What build are you running?",  SentAt = "2026-03-10T18:20:00Z" },
            new Message { From = "Koriebonx98",   Text = "Rivers of Blood bleed build, works every time 🗡️",                  SentAt = "2026-03-10T18:22:00Z" },
            new Message { From = "NintendoFan42", Text = "Lol classic. Hey wanna squad up on Mario Kart tonight at 8?",       SentAt = "2026-03-10T18:30:00Z" },
            new Message { From = "Koriebonx98",   Text = "100% I'm in, see you then! 🏎️💨",                                   SentAt = "2026-03-10T18:31:00Z" },
        };
        foreach (var m in msgs)
            ConversationMessages.Add(m);
    }

    // ── Messaging commands ────────────────────────────────────────────────────

    /// <summary>Navigates to the friend's profile page.</summary>
    [RelayCommand]
    private void ViewFriendProfile(string friendUsername)
    {
        if (!string.IsNullOrEmpty(friendUsername))
            OnViewFriendProfile?.Invoke(friendUsername);
    }

    /// <summary>Opens the conversation panel for the specified friend.</summary>
    [RelayCommand]
    private async Task OpenConversation(string friendUsername)
    {
        if (string.IsNullOrEmpty(friendUsername)) return;

        ConversationFriend = friendUsername;
        ConversationMessages.Clear();
        MessageError = "";
        ShowConversation = true;

        if (_client == null) return;

        try
        {
            var messages = await _client.GetMessagesAsync(friendUsername);
            ConversationMessages.Clear();
            foreach (var m in messages)
                ConversationMessages.Add(m);
        }
        catch (Exception ex)
        {
            MessageError = $"Could not load messages: {ex.Message}";
        }
    }

    /// <summary>Sends the current message to the active conversation partner.</summary>
    [RelayCommand]
    private async Task SendMessage()
    {
        if (string.IsNullOrWhiteSpace(NewMessageText) || string.IsNullOrEmpty(ConversationFriend))
            return;
        if (_client == null) return;

        IsSendingMessage = true;
        MessageError     = "";
        string text      = NewMessageText.Trim();
        NewMessageText   = "";

        try
        {
            await _client.SendMessageAsync(ConversationFriend, text);

            // Append the sent message to the local conversation immediately
            ConversationMessages.Add(new Message
            {
                From   = _username,
                Text   = text,
                SentAt = DateTimeOffset.UtcNow.ToString("o"),
            });
        }
        catch (Exception ex)
        {
            MessageError   = $"Send failed: {ex.Message}";
            NewMessageText = text; // restore so the user can retry
        }
        finally
        {
            IsSendingMessage = false;
        }
    }

    /// <summary>Closes the conversation panel.</summary>
    [RelayCommand]
    private void CloseConversation()
    {
        ShowConversation = false;
        ConversationFriend = "";
        ConversationMessages.Clear();
        MessageError = "";
    }

    // ── Friend list loading ───────────────────────────────────────────────────

    private async Task LoadAsync()
    {
        if (_client == null) return;

        IsLoading    = true;
        ErrorMessage = "";

        OnlineFriends.Clear();
        OfflineFriends.Clear();
        PendingRequests.Clear();
        RecentActivity.Clear();

        try
        {
            // Load friend usernames and incoming requests in parallel
            var friendsTask  = _client.GetFriendsAsync();
            var requestsTask = _client.GetFriendRequestsAsync(_username);
            await Task.WhenAll(friendsTask, requestsTask);

            var friendUsernames = await friendsTask;
            var requests        = await requestsTask;

            // Build pending requests for display
            foreach (var req in requests)
            {
                PendingRequests.Add(new FriendRequestDisplay
                {
                    FromUsername = req.From,
                    SentAgo      = FormatTimeAgo(req.SentAt)
                });
            }

            // Fetch full presence (lastSeen + currentGame) AND last-played game for each friend
            // in parallel (best-effort).
            // currentGame from presence is the authoritative "now playing" signal:
            //   - non-null → friend is actively in that game
            //   - null     → friend is at the Dashboard (even if online)
            var presenceTasks = new List<Task<(string username, Models.PresenceData? presence)>>();
            var gamesTasks    = new List<Task<(string username, string? gameTitle, string? gamePlatform)>>();

            foreach (var friendName in friendUsernames)
            {
                string name = friendName; // capture
                presenceTasks.Add(Task.Run(async () =>
                {
                    try { return (name, await _client.GetFriendPresenceAsync(name)); }
                    catch { return (name, (Models.PresenceData?)null); }
                }));

                gamesTasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var games = await _client.GetFriendGamesAsync(name);
                        var last  = games
                            .Where(g => !string.IsNullOrEmpty(g.LastPlayedAt))
                            .OrderByDescending(g => g.LastPlayedAt)
                            .FirstOrDefault();
                        return (name, last?.Title, last?.Platform);
                    }
                    catch { return (name, (string?)null, (string?)null); }
                }));
            }

            var presenceResults = await Task.WhenAll(presenceTasks);
            var gamesResults    = await Task.WhenAll(gamesTasks);

            // Build a games lookup (last-played) keyed by username — used for offline cards
            var gamesMap = new Dictionary<string, (string? Title, string? Platform)>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var (name, title, platform) in gamesResults)
                gamesMap[name] = (title, platform);

            var activityItems = new List<FriendActivityItem>();

            foreach (var (name, presence) in presenceResults)
            {
                string? lastSeen    = presence?.LastSeen;
                string? currentGame = presence?.CurrentGame;   // null = at Dashboard
                gamesMap.TryGetValue(name, out var gameInfo);
                // Use currentGame from presence for "now playing"; use last-played from games.json
                // only for the offline "recently played" card display.
                var entry = BuildFriendEntry(name, lastSeen, currentGame, gameInfo.Title, gameInfo.Platform);
                if (entry.IsOnline || entry.IsAway)
                    OnlineFriends.Add(entry);
                else
                    OfflineFriends.Add(entry);

                // Recent activity: prefer currentGame (from presence) if active, else last-played
                bool    isActive         = entry.IsOnline || entry.IsAway;
                string? activityTitle    = isActive ? currentGame : gameInfo.Title;
                string? activityPlatform = isActive ? null        : gameInfo.Platform;
                if (!string.IsNullOrEmpty(activityTitle))
                {
                    int sortKey = entry.IsOnline ? 0 : (entry.IsAway ? 1 : 2);
                    activityItems.Add(new FriendActivityItem
                    {
                        Username     = name,
                        GameTitle    = activityTitle,
                        Platform     = activityPlatform ?? "",
                        TimeAgo      = isActive ? (entry.IsOnline ? "Just now" : entry.LastSeen)
                                                : entry.LastSeen,
                        ActivityText = isActive ? "is playing" : "last played",
                        Icon         = "🎮",
                        SortKey      = sortKey,
                    });
                }
            }

            // Sort activity: online first (SortKey=0), away second (1), offline last (2)
            foreach (var item in activityItems.OrderBy(i => i.SortKey))
            {
                RecentActivity.Add(item);
            }

            OnPropertyChanged(nameof(HasRecentActivity));
            OnlineCount = OnlineFriends.Count;
            TotalCount  = friendUsernames.Count;
        }
        catch (Exception ex)
        {
            // If the API is unreachable (demo mode, no backend), fall back to demo data
            if (!_client.IsAuthenticated)
            {
                LoadDemo();
            }
            else
            {
                ErrorMessage = $"Could not load friends: {ex.Message}";
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <param name="currentGame">
    /// The game the friend is CURRENTLY playing, sourced from their live presence record.
    /// <c>null</c> means they are at the Dashboard (not in a game), even if online.
    /// </param>
    /// <param name="recentGameTitle">Last-played game from their games.json — shown on offline cards.</param>
    private static FriendEntry BuildFriendEntry(string username, string? lastSeenIso,
                                                 string? currentGame        = null,
                                                 string? recentGameTitle    = null,
                                                 string? recentGamePlatform = null)
    {
        string status   = "Offline";
        string lastSeen = "Unknown";

        if (!string.IsNullOrEmpty(lastSeenIso) &&
            DateTimeOffset.TryParse(lastSeenIso, out var ts))
        {
            var ago = DateTimeOffset.UtcNow - ts;
            if (ago.TotalMinutes < 5)
            {
                status   = "Online";
                lastSeen = "Now";
            }
            else if (ago.TotalMinutes < 30)
            {
                status   = "Away";
                lastSeen = $"{(int)ago.TotalMinutes} min ago";
            }
            else if (ago.TotalHours < 24)
            {
                lastSeen = $"{(int)ago.TotalHours}h ago";
            }
            else
            {
                lastSeen = ts.LocalDateTime.ToString("dd MMM");
            }
        }

        return new FriendEntry
        {
            Username           = username,
            Status             = status,
            LastSeen           = lastSeen,
            RecentGameTitle    = recentGameTitle,
            RecentGamePlatform = recentGamePlatform,
            // CurrentGame is ONLY set from the live presence record (not from last-played).
            // null = "Dashboard" (at menu, not in a game).
            CurrentGame        = (status == "Online" || status == "Away") ? currentGame : null,
        };
    }

    private static string FormatTimeAgo(string? isoTimestamp)
    {
        if (string.IsNullOrEmpty(isoTimestamp) ||
            !DateTimeOffset.TryParse(isoTimestamp, out var ts))
            return "";

        var ago = DateTimeOffset.UtcNow - ts;
        if (ago.TotalMinutes < 60) return $"{(int)ago.TotalMinutes} min ago";
        if (ago.TotalHours   < 24) return $"{(int)ago.TotalHours} hours ago";
        return $"{(int)ago.TotalDays} days ago";
    }
}
