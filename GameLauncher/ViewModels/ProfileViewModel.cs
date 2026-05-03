using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using GameLauncher.Models;

namespace GameLauncher.ViewModels;

public partial class ProfileViewModel : ViewModelBase
{
    [ObservableProperty] private string _username        = "";
    [ObservableProperty] private string _email           = "";
    [ObservableProperty] private string _memberSince     = "";
    [ObservableProperty] private int    _gamesCount;
    [ObservableProperty] private int    _achievementsCount;
    [ObservableProperty] private string _avatarInitial   = "?";
    [ObservableProperty] private string _modeBadge       = "LIVE";
    [ObservableProperty] private bool   _isAdmin         = false;

    /// <summary>Total games owned across all sources (cloud library + Steam + local).</summary>
    [ObservableProperty] private int    _totalOwned;

    // ── GamerScore ────────────────────────────────────────────────────────────
    [ObservableProperty] private int    _gamerScoreTotal;
    /// <summary>Human-readable GamerScore label, e.g. "1 250 GS".</summary>
    [ObservableProperty] private string _gamerScoreLabel = "0 GS";

    /// <summary>
    /// True when this view-model is showing a friend's profile (not the current user's own profile).
    /// Hides the email address and shows the activity feed instead.
    /// </summary>
    [ObservableProperty] private bool _isFriendProfile = false;

    public bool HasFriendActivity => FriendActivity.Count > 0;

    public ObservableCollection<Achievement>       AllAchievements { get; } = new();
    /// <summary>Recent game activity for the friend's profile (games recently played).</summary>
    public ObservableCollection<FriendActivityItem> FriendActivity { get; } = new();

    public void Load(UserProfile profile, List<Game> library,
                     List<Achievement> achievements, bool isAdmin)
    {
        Username          = profile.Username;
        Email             = profile.Email;
        // Use cloud-synced total if available (includes local + roms + repacks from other devices);
        // fall back to the current library count when the cloud field is absent.
        GamesCount        = profile.TotalGames ?? library.Count;
        TotalOwned        = profile.TotalGames ?? library.Count;
        AchievementsCount = achievements.Count;
        AvatarInitial     = profile.Username.Length > 0
            ? profile.Username[0].ToString().ToUpper() : "?";
        IsAdmin   = isAdmin;
        ModeBadge = isAdmin ? "ADMIN" : "LIVE";

        if (System.DateTimeOffset.TryParse(profile.CreatedAt, out var dt))
            MemberSince = dt.ToString("dd MMMM yyyy");
        else
            MemberSince = profile.CreatedAt;

        // Use the cloud-synced GamerScore when available (most up-to-date, includes all devices).
        // Fall back to computing it locally if the cloud field is absent.
        if (profile.GamerScore.HasValue && profile.GamerScore.Value > 0)
        {
            GamerScoreTotal = profile.GamerScore.Value;
            GamerScoreLabel = GamerScore.FormatLabel(profile.GamerScore.Value);
        }
        else
        {
            int totalPlaytimeMinutes = library.Sum(g => g.PlaytimeMinutes);
            var gs = GamerScore.Compute(totalPlaytimeMinutes, achievements.Count);
            GamerScoreTotal = gs.Total;
            GamerScoreLabel = gs.Label;
        }

        AllAchievements.Clear();
        foreach (var a in achievements.OrderByDescending(a => a.UnlockedAt))
            AllAchievements.Add(a);
    }

    /// <summary>
    /// Loads a placeholder state while the real profile data is being fetched.
    /// Called immediately when opening a friend's profile so something shows right away.
    /// </summary>
    public void LoadPlaceholder(string username)
    {
        Username          = username;
        Email             = "";
        GamesCount        = 0;
        AchievementsCount = 0;
        AvatarInitial     = username.Length > 0 ? username[0].ToString().ToUpper() : "?";
        IsAdmin           = false;
        ModeBadge         = "LIVE";
        MemberSince       = "Loading…";
        AllAchievements.Clear();
        FriendActivity.Clear();
        OnPropertyChanged(nameof(HasFriendActivity));
    }

    /// <summary>
    /// Populates the activity feed with recently played games from the friend's library.
    /// Called after the library is fetched from the backend.
    /// </summary>
    public void LoadFriendActivity(List<Game> library)
    {
        FriendActivity.Clear();

        // Build activity items from games with a LastPlayedAt timestamp, sorted newest first
        var items = library
            .Where(g => !string.IsNullOrEmpty(g.LastPlayedAt))
            .OrderByDescending(g => g.LastPlayedAt)
            .Take(10)
            .Select(g => new FriendActivityItem
            {
                Username     = Username,
                GameTitle    = g.Title,
                Platform     = g.Platform,
                TimeAgo      = FormatTimeAgo(g.LastPlayedAt),
                ActivityText = "played",
                Icon         = "🎮",
                SortKey      = 0,
            });

        foreach (var item in items)
            FriendActivity.Add(item);

        OnPropertyChanged(nameof(HasFriendActivity));
    }

    private static string FormatTimeAgo(string? isoTimestamp)
    {
        if (string.IsNullOrEmpty(isoTimestamp) ||
            !System.DateTimeOffset.TryParse(isoTimestamp, out var ts))
            return "";

        var ago = System.DateTimeOffset.UtcNow - ts;
        if (ago.TotalSeconds < 60) return "just now";
        if (ago.TotalMinutes < 60) return $"{(int)ago.TotalMinutes} min ago";
        if (ago.TotalHours   < 24) return $"{(int)ago.TotalHours} hours ago";
        if (ago.TotalDays    < 30) return $"{(int)ago.TotalDays} days ago";
        return ts.LocalDateTime.ToString("dd MMM yyyy");
    }
}
