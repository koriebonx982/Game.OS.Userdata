using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Models;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

public partial class DashboardViewModel : ViewModelBase
{
    [ObservableProperty] private UserProfile _profile = new();
    [ObservableProperty] private string _greeting = "";
    [ObservableProperty] private int _gamesCount;
    [ObservableProperty] private int _achievementsCount;
    [ObservableProperty] private int _platformsCount;
    [ObservableProperty] private string _totalPlaytimeLabel = "";

    // Hero featured / last-played game
    [ObservableProperty] private StoreGame? _featuredGame;
    [ObservableProperty] private string     _featuredGradient  = "#1a1a2e,#16213e";
    [ObservableProperty] private string     _featuredBadgeText = "⭐  FEATURED";
    [ObservableProperty] private bool       _isFeaturedLastPlayed;

    // References used to route the hero button click to the correct detail view
    private Game?            _heroCloudGame;
    private LocalGameCardVm? _heroLocalCard;

    // ── Constants ────────────────────────────────────────────────────────────
    /// <summary>Maximum number of games shown in "Continue Playing" and hero last-played lookup.</summary>
    private const int MaxRecentGames = 8;

    // Recently added / played (cloud + local combined)
    public ObservableCollection<Game> RecentGames { get; } = new();
    /// <summary>True when there are recently detected local ROMs or installed games to show.</summary>
    [ObservableProperty] private bool _hasRecentLocalGames;
    public ObservableCollection<LocalGameCardVm> RecentLocalGames { get; } = new();

    // Recent achievements
    public ObservableCollection<Achievement> RecentAchievements { get; } = new();

    /// <summary>Invoked when the user clicks a game card to open the detail overlay.</summary>
    public Action<Game>?            OnOpenDetail        { get; set; }
    public Action<StoreGame>?       OnOpenStoreDetail   { get; set; }
    public Action<LocalGameCardVm>? OnOpenLocalDetail   { get; set; }
    /// <summary>
    /// Invoked when the user clicks "Continue Playing" on the hero banner.
    /// The card is the most-recently played local game; the handler should launch
    /// the game directly without opening the detail overlay.
    /// </summary>
    public Action<LocalGameCardVm>? OnContinuePlaying   { get; set; }

    public void Load(UserProfile profile, List<Game> library, List<Achievement> achievements,
                     IReadOnlyList<LocalGameCardVm>? localCards = null)
    {
        Profile           = profile;
        GamesCount        = library.Count;
        AchievementsCount = achievements.Count;
        PlatformsCount    = library.Select(g => g.Platform).Distinct().Count();

        // Total playtime across all games — show days/hours/minutes breakdown.
        // Include cloud library games AND local-only games (not in the cloud library) so that
        // PC games tracked only by the scanner are counted towards the total playtime.
        int totalMinutes = library.Sum(g => g.PlaytimeMinutes);

        if (localCards != null)
        {
            // Build a set of lowercase "platform||title" keys already counted from the cloud library
            // to avoid double-counting games that appear in both the library and local cards.
            var cloudKeys = new HashSet<string>(
                library.Select(g => $"{g.Platform.ToLowerInvariant()}||{g.Title.ToLowerInvariant()}"));

            foreach (var c in localCards)
            {
                // Use EffectiveTitle (resolved via DB enrichment) so that TitleID-named ROMs
                // (e.g. "CUSA00572" → "God of War") are correctly matched against cloud titles.
                string key = $"{c.Platform.ToLowerInvariant()}||{c.EffectiveTitle.ToLowerInvariant()}";
                if (cloudKeys.Contains(key)) continue; // already counted via the library entry
                totalMinutes += PlaytimeService.GetTotalMinutes(c.Platform, c.EffectiveTitle);
            }
        }

        if (totalMinutes <= 0)
        {
            TotalPlaytimeLabel = "0m";
        }
        else
        {
            int days  = totalMinutes / 1440;
            int hours = (totalMinutes % 1440) / 60;
            int mins  = totalMinutes % 60;
            if (days > 0)
                TotalPlaytimeLabel = mins > 0 ? $"{days}d {hours}h {mins}m" : $"{days}d {hours}h";
            else if (hours > 0)
                TotalPlaytimeLabel = mins > 0 ? $"{hours}h {mins}m" : $"{hours}h";
            else
                TotalPlaytimeLabel = $"{mins}m";
        }

        string hour = System.DateTime.Now.Hour switch
        {
            < 12 => "Good morning",
            < 17 => "Good afternoon",
            _    => "Good evening"
        };
        Greeting = $"{hour}, {profile.Username}!";

        // Recently Played — only games that have actually been played (have LastPlayedAt set)
        // Parse ISO 8601 strings to DateTime for correct chronological comparison.
        static DateTime ParseDate(string? s) =>
            DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
                ? dt : DateTime.MinValue;

        RecentGames.Clear();
        var recentlyPlayed = library
            .Where(g => !string.IsNullOrEmpty(g.LastPlayedAt))
            .OrderByDescending(g => ParseDate(g.LastPlayedAt))
            .Take(MaxRecentGames)
            .ToList();
        foreach (var g in recentlyPlayed)
            RecentGames.Add(g);

        RecentAchievements.Clear();
        foreach (var a in achievements.OrderByDescending(a => ParseDate(a.UnlockedAt)).Take(4))
            RecentAchievements.Add(a);

        // ── Continue Playing ──────────────────────────────────────────────────
        // Shows locally detected games/ROMs with tracked playtime AND cloud library
        // games with recorded playtime (e.g. PC games not detected by the scanner).
        RecentLocalGames.Clear();
        var addedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Local cards (ROMs, installed games, repacks) with playtime
        if (localCards != null)
        {
            foreach (var c in localCards
                .Where(c => PlaytimeService.GetTotalMinutes(c.Platform, c.EffectiveTitle) > 0
                         || PlaytimeService.IsBeingTracked(c.Platform, c.EffectiveTitle))
                .OrderByDescending(c => PlaytimeService.IsBeingTracked(c.Platform, c.EffectiveTitle)
                                        ? DateTime.MaxValue
                                        : PlaytimeService.GetLastPlayedAt(c.Platform, c.EffectiveTitle))
                .Take(MaxRecentGames))
            {
                if (PlaytimeService.IsBeingTracked(c.Platform, c.EffectiveTitle))
                    c.PlaytimeLabel = "▶ Playing now";
                else
                {
                    int mins = PlaytimeService.GetTotalMinutes(c.Platform, c.EffectiveTitle);
                    c.PlaytimeLabel = FormatMinutes(mins);
                }
                RecentLocalGames.Add(c);
                addedKeys.Add($"{c.Platform}||{c.EffectiveTitle}");
            }
        }

        // 2. Cloud library games with playtime that aren't already shown as local cards
        //    This ensures PC cloud-library games (not detected by the scanner) appear here too.
        foreach (var g in library
            .Where(g => g.PlaytimeMinutes > 0 && !string.IsNullOrEmpty(g.LastPlayedAt))
            .Where(g => !addedKeys.Contains($"{g.Platform}||{g.Title}"))
            .OrderByDescending(g => ParseDate(g.LastPlayedAt))
            .Take(Math.Max(0, MaxRecentGames - RecentLocalGames.Count)))
        {
            var card = new LocalGameCardVm
            {
                Title           = g.Title,
                Platform        = g.Platform,
                CoverUrl        = g.CoverUrl,
                CoverGradient   = g.CoverGradient ?? "#0d2137,#163d5e",
                SourceCloudGame = g,
            };
            card.PlaytimeLabel = FormatMinutes(g.PlaytimeMinutes);
            RecentLocalGames.Add(card);
        }

        HasRecentLocalGames = RecentLocalGames.Count > 0;

        // ── Hero / Featured ───────────────────────────────────────────────────
        // Show the most recently played game (local or cloud) as the hero.
        // Fall back to the highest-rated store game when nothing has been played yet.

        _heroCloudGame  = null;
        _heroLocalCard  = null;

        // Most recently played cloud library game
        var lastCloud = library
            .Where(g => !string.IsNullOrEmpty(g.LastPlayedAt))
            .OrderByDescending(g => ParseDate(g.LastPlayedAt))
            .FirstOrDefault();

        // Most recently played local card
        LocalGameCardVm? lastLocal = null;
        DateTime         lastLocalTime = DateTime.MinValue;
        if (localCards != null)
        {
            foreach (var c in localCards.Where(c =>
                PlaytimeService.GetTotalMinutes(c.Platform, c.EffectiveTitle) > 0))
            {
                var t = PlaytimeService.GetLastPlayedAt(c.Platform, c.EffectiveTitle);
                if (t > lastLocalTime) { lastLocalTime = t; lastLocal = c; }
            }
        }

        DateTime lastCloudTime = lastCloud != null ? ParseDate(lastCloud.LastPlayedAt) : DateTime.MinValue;

        if (lastLocal != null && lastLocalTime >= lastCloudTime && lastLocalTime > DateTime.MinValue)
        {
            // A local game was played more recently
            _heroLocalCard = lastLocal;
            FeaturedGame = new StoreGame
            {
                Title         = lastLocal.EffectiveTitle,
                Platform      = lastLocal.Platform,
                Description   = "",
                CoverUrl      = lastLocal.CoverUrl,
                CoverGradient = lastLocal.CoverGradient,
            };
            FeaturedGradient      = lastLocal.CoverGradient;
            FeaturedBadgeText     = "▶  LAST PLAYED";
            IsFeaturedLastPlayed  = true;
        }
        else if (lastCloud != null && lastCloudTime > DateTime.MinValue)
        {
            // A cloud library game was played more recently
            _heroCloudGame = lastCloud;
            FeaturedGame = new StoreGame
            {
                Title         = lastCloud.Title,
                Platform      = lastCloud.Platform,
                Genre         = lastCloud.Genre ?? "",
                Description   = lastCloud.Description ?? "",
                CoverUrl      = lastCloud.CoverUrl,
                CoverGradient = lastCloud.CoverGradient ?? "#1a1a2e,#16213e",
                Price         = lastCloud.Price ?? "",
                AchievementsUrl = lastCloud.AchievementsUrl,
                TrailerUrl    = lastCloud.TrailerUrl,
            };
            FeaturedGradient      = lastCloud.CoverGradient ?? "#1a1a2e,#16213e";
            FeaturedBadgeText     = "▶  LAST PLAYED";
            IsFeaturedLastPlayed  = true;
        }
        else
        {
            // Nothing played yet — show highest-rated store game as "FEATURED"
            _heroCloudGame       = null;
            _heroLocalCard       = null;
            FeaturedGame         = GameCatalog.Store.OrderByDescending(s => s.Rating).FirstOrDefault();
            FeaturedGradient     = FeaturedGame?.CoverGradient ?? "#1a1a2e,#16213e";
            FeaturedBadgeText    = "⭐  FEATURED";
            IsFeaturedLastPlayed = false;
        }
    }

    // Keep the original 3-arg overload for backwards compatibility with existing callers.
    public void Load(UserProfile profile, List<Game> library, List<Achievement> achievements)
        => Load(profile, library, achievements, null);

    [RelayCommand]
    private void OpenGameDetail(Game? game)
    {
        if (game != null) OnOpenDetail?.Invoke(game);
    }

    [RelayCommand]
    private void OpenLocalGameDetail(LocalGameCardVm? card)
    {
        if (card != null) OnOpenLocalDetail?.Invoke(card);
    }

    [RelayCommand]
    private void OpenFeaturedDetail()
    {
        if (_heroLocalCard != null)
            OnOpenLocalDetail?.Invoke(_heroLocalCard);
        else if (_heroCloudGame != null)
            OnOpenDetail?.Invoke(_heroCloudGame);
        else if (FeaturedGame != null)
            OnOpenStoreDetail?.Invoke(FeaturedGame);
    }

    /// <summary>
    /// Called when the user clicks the "Continue Playing" button on the hero banner.
    /// Invokes <see cref="OnContinuePlaying"/> with the most-recently played local card
    /// so the caller can launch the game immediately without opening the detail view.
    /// Falls back to <see cref="OpenFeaturedDetail"/> for cloud / store featured games.
    /// </summary>
    [RelayCommand]
    private void ContinuePlaying()
    {
        if (_heroLocalCard != null && OnContinuePlaying != null)
            OnContinuePlaying(_heroLocalCard);
        else
            OpenFeaturedDetail();
    }

    private static string FormatMinutes(int minutes)
    {
        if (minutes <= 0) return "";
        int days  = minutes / 1440;
        int hours = (minutes % 1440) / 60;
        int mins  = minutes % 60;
        if (days > 0)
            return mins > 0 ? $"{days}d {hours}h {mins}m" : $"{days}d {hours}h";
        if (hours > 0)
            return mins > 0 ? $"{hours}h {mins}m" : $"{hours}h";
        return $"{mins}m";
    }
}
