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
    /// <summary>Default card gradient used for cloud/activity-only game cards without a cover image.</summary>
    private const string DefaultCloudCardGradient = "#0d2137,#163d5e";

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

        DevLogService.Log($"[Dashboard] Load: user='{profile.Username}'  games={library.Count}  achievements={achievements.Count}  localCards={localCards?.Count ?? 0}  platforms={PlatformsCount}");

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
                // Use the larger of local and cloud playtime so cross-device sessions are counted.
                int localMins = PlaytimeService.GetTotalMinutes(c.Platform, c.EffectiveTitle);
                int cloudMins = PlaytimeService.GetCloudMinutes(c.Platform, c.EffectiveTitle);
                totalMinutes += Math.Max(localMins, cloudMins);
            }
        }

        // Also count activity-only games (played on another device, not in the library or local cards)
        // so the total playtime stat includes cross-device sessions for all tracked games.
        {
            var countedKeys = BuildGameKeySet(library, localCards);
            foreach (var (platform, title, minutes, _) in PlaytimeService.GetAllCloudTotals()
                .Where(t => t.Minutes > 0))
            {
                if (!countedKeys.Contains($"{platform.ToLowerInvariant()}||{title.ToLowerInvariant()}"))
                    totalMinutes += minutes;
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
                         || PlaytimeService.IsBeingTracked(c.Platform, c.EffectiveTitle)
                         || PlaytimeService.GetCloudMinutes(c.Platform, c.EffectiveTitle) > 0)
                .OrderByDescending(c =>
                {
                    if (PlaytimeService.IsBeingTracked(c.Platform, c.EffectiveTitle))
                        return DateTime.MaxValue;
                    var localAt = PlaytimeService.GetLastPlayedAt(c.Platform, c.EffectiveTitle);
                    var cloudAt = PlaytimeService.GetCloudLastPlayedAt(c.Platform, c.EffectiveTitle);
                    return localAt >= cloudAt ? localAt : cloudAt;
                })
                .Take(MaxRecentGames))
            {
                if (PlaytimeService.IsBeingTracked(c.Platform, c.EffectiveTitle))
                    c.PlaytimeLabel = "▶ Playing now";
                else
                {
                    int localMins = PlaytimeService.GetTotalMinutes(c.Platform, c.EffectiveTitle);
                    int cloudMins = PlaytimeService.GetCloudMinutes(c.Platform, c.EffectiveTitle);
                    c.PlaytimeLabel = FormatMinutes(Math.Max(localMins, cloudMins));
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
                CoverGradient   = g.CoverGradient ?? DefaultCloudCardGradient,
                SourceCloudGame = g,
            };
            card.PlaytimeLabel = FormatMinutes(g.PlaytimeMinutes);
            RecentLocalGames.Add(card);
            addedKeys.Add($"{g.Platform}||{g.Title}");
        }

        // 3. Activity-log games that are NOT in the cloud library at all.
        //    These are games played on another device that were only scanned locally there
        //    (never added to games.json). The cloud activity log still recorded the sessions,
        //    so we can show them here so "Continue Playing" matches the website timeline.
        if (RecentLocalGames.Count < MaxRecentGames)
        {
            var libraryKeys = BuildGameKeySet(library, null);
            foreach (var (platform, title, minutes, lastPlayed) in PlaytimeService.GetAllCloudTotals()
                .Where(t => t.Minutes > 0 && !string.IsNullOrEmpty(t.LastPlayed))
                .Where(t => !libraryKeys.Contains(
                    $"{t.Platform.ToLowerInvariant()}||{t.Title.ToLowerInvariant()}"))
                .Where(t => !addedKeys.Contains($"{t.Platform}||{t.Title}"))
                .OrderByDescending(t => ParseDate(t.LastPlayed))
                .Take(MaxRecentGames - RecentLocalGames.Count))
            {
                var syntheticGame = new Game
                {
                    Title           = title,
                    Platform        = platform,
                    PlaytimeMinutes = minutes,
                    LastPlayedAt    = lastPlayed,
                };
                var card = new LocalGameCardVm
                {
                    Title         = title,
                    Platform      = platform,
                    CoverGradient = DefaultCloudCardGradient,
                    SourceCloudGame = syntheticGame,
                };
                card.PlaytimeLabel = FormatMinutes(minutes);
                RecentLocalGames.Add(card);
                addedKeys.Add($"{platform}||{title}");
            }
        }

        HasRecentLocalGames = RecentLocalGames.Count > 0;

        // ── Hero / Featured ───────────────────────────────────────────────────
        // Show the most recently played game (local, cloud library, or activity-only)
        // as the hero.  Fall back to the highest-rated store game when nothing has
        // been played yet.

        _heroCloudGame  = null;
        _heroLocalCard  = null;

        // Most recently played cloud library game
        var lastCloud = library
            .Where(g => !string.IsNullOrEmpty(g.LastPlayedAt))
            .OrderByDescending(g => ParseDate(g.LastPlayedAt))
            .FirstOrDefault();

        // Most recently played local card (locally installed ROMs / PC games)
        LocalGameCardVm? lastLocal = null;
        DateTime         lastLocalTime = DateTime.MinValue;
        if (localCards != null)
        {
            foreach (var c in localCards.Where(c =>
                PlaytimeService.GetTotalMinutes(c.Platform, c.EffectiveTitle) > 0
                || PlaytimeService.GetCloudMinutes(c.Platform, c.EffectiveTitle) > 0))
            {
                var localAt = PlaytimeService.GetLastPlayedAt(c.Platform, c.EffectiveTitle);
                var cloudAt = PlaytimeService.GetCloudLastPlayedAt(c.Platform, c.EffectiveTitle);
                var t = localAt >= cloudAt ? localAt : cloudAt;
                if (t > lastLocalTime) { lastLocalTime = t; lastLocal = c; }
            }
        }

        // Most recently played activity-only game — played on another device, not present
        // in this device's cloud library (games.json) or local scan results.
        // These are always in _cloudTotals after ApplyCloudPlaytimeAsync runs.
        Game?    lastActivityGame = null;
        DateTime lastActivityTime = DateTime.MinValue;
        {
            var libAndLocalKeys = BuildGameKeySet(library, localCards);
            foreach (var (platform, title, minutes, lastPlayed) in PlaytimeService.GetAllCloudTotals()
                .Where(t => t.Minutes > 0 && !string.IsNullOrEmpty(t.LastPlayed)))
            {
                if (libAndLocalKeys.Contains(
                        $"{platform.ToLowerInvariant()}||{title.ToLowerInvariant()}")) continue;
                var t = ParseDate(lastPlayed);
                if (t > lastActivityTime)
                {
                    lastActivityTime = t;
                    lastActivityGame = new Game
                    {
                        Title           = title,
                        Platform        = platform,
                        PlaytimeMinutes = minutes,
                        LastPlayedAt    = lastPlayed,
                    };
                }
            }
        }

        DateTime lastCloudTime = lastCloud != null ? ParseDate(lastCloud.LastPlayedAt) : DateTime.MinValue;

        if (lastLocal != null && lastLocalTime >= lastCloudTime
                              && lastLocalTime >= lastActivityTime
                              && lastLocalTime > DateTime.MinValue)
        {
            // A local game was played more recently than any cloud or activity-only game
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
            DevLogService.Log($"[Dashboard] FeaturedGame = local '{lastLocal.EffectiveTitle}' ({lastLocal.Platform}) — last played {lastLocalTime:yyyy-MM-dd HH:mm:ss}");
        }
        else if (lastActivityGame != null && lastActivityTime > lastCloudTime
                                          && lastActivityTime > DateTime.MinValue)
        {
            // An activity-only game (played on another device, not in this device's library
            // or local scan) is the most recently played game.
            // Route through _heroCloudGame so clicking "Continue Playing" opens the detail
            // overlay (via OpenDetailFromGame) with proper enrichment — the same path used
            // for cloud library games that are not locally installed.
            _heroCloudGame = lastActivityGame;
            FeaturedGame = new StoreGame
            {
                Title         = lastActivityGame.Title,
                Platform      = lastActivityGame.Platform,
                Description   = "",
                CoverGradient = DefaultCloudCardGradient,
            };
            FeaturedGradient      = DefaultCloudCardGradient;
            FeaturedBadgeText     = "▶  LAST PLAYED";
            IsFeaturedLastPlayed  = true;
            DevLogService.Log($"[Dashboard] FeaturedGame = activity-only '{lastActivityGame.Title}' ({lastActivityGame.Platform}) — last played {lastActivityTime:yyyy-MM-dd HH:mm:ss}");
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
            DevLogService.Log($"[Dashboard] FeaturedGame = cloud '{lastCloud.Title}' ({lastCloud.Platform}) — last played {lastCloudTime:yyyy-MM-dd HH:mm:ss}");
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
            DevLogService.Log($"[Dashboard] FeaturedGame = store fallback '{FeaturedGame?.Title ?? "(none)"}' — no games played yet.");
        }
    }

    // Keep the original 3-arg overload for backwards compatibility with existing callers.
    public void Load(UserProfile profile, List<Game> library, List<Achievement> achievements)
        => Load(profile, library, achievements, null);

    /// <summary>
    /// Builds a case-insensitive set of "platform||title" keys for all games in
    /// <paramref name="library"/> and all local card titles in <paramref name="localCards"/>.
    /// Used in multiple places to avoid counting or showing the same game twice.
    /// </summary>
    private static HashSet<string> BuildGameKeySet(
        List<Game> library, IReadOnlyList<LocalGameCardVm>? localCards)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in library)
            keys.Add($"{g.Platform.ToLowerInvariant()}||{g.Title.ToLowerInvariant()}");
        if (localCards != null)
            foreach (var c in localCards)
                keys.Add($"{c.Platform.ToLowerInvariant()}||{c.EffectiveTitle.ToLowerInvariant()}");
        return keys;
    }

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
