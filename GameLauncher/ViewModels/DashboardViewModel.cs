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
    /// <summary>Maximum number of local cards shown in the XB360 "Games Library" strip.</summary>
    private const int MaxLibraryGames = 12;
    /// <summary>Default card gradient used for cloud/activity-only game cards without a cover image.</summary>
    private const string DefaultCloudCardGradient = "#0d2137,#163d5e";

    // Recently added / played (cloud + local combined)
    public ObservableCollection<Game> RecentGames { get; } = new();
    /// <summary>True when there are recently detected local ROMs or installed games to show.</summary>
    [ObservableProperty] private bool _hasRecentLocalGames;
    public ObservableCollection<LocalGameCardVm> RecentLocalGames { get; } = new();
    /// <summary>True when there are local Game.OS games available for the XB360 "Games Library" strip.</summary>
    [ObservableProperty] private bool _hasLocalLibraryGames;
    public ObservableCollection<LocalGameCardVm> LocalLibraryGames { get; } = new();

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
        // Built from the cloud activity log as the single source of truth so that
        // the list matches the website's recently-played order on every device.
        // Device A and Device B see the same game order regardless of which games
        // are locally installed on each machine — just like Xbox / PlayStation.
        RecentLocalGames.Clear();
        var addedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Build quick-lookup maps so we can resolve each activity entry to a
        // locally-installed card or a cloud-library game without an O(n²) scan.
        var localCardMap = new Dictionary<string, LocalGameCardVm>(StringComparer.OrdinalIgnoreCase);
        if (localCards != null)
            foreach (var c in localCards)
                localCardMap[$"{c.Platform.ToLowerInvariant()}||{c.EffectiveTitle.ToLowerInvariant()}"] = c;

        var cloudGameMap = new Dictionary<string, Game>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in library)
            cloudGameMap[$"{g.Platform.ToLowerInvariant()}||{g.Title.ToLowerInvariant()}"] = g;

        // Step 1 — currently-running games are always pinned at the top regardless of
        // their position in the activity log.
        if (localCards != null)
        {
            foreach (var c in localCards.Where(
                c => PlaytimeService.IsBeingTracked(c.Platform, c.EffectiveTitle)))
            {
                c.PlaytimeLabel = "▶ Playing now";
                RecentLocalGames.Add(c);
                addedKeys.Add($"{c.Platform.ToLowerInvariant()}||{c.EffectiveTitle.ToLowerInvariant()}");
            }
        }

        // Step 2 — merge activity-log entries (all devices) with any cloud-library games
        // that have playtime but pre-date the activity log, then sort globally by the
        // cloud last-played timestamp so the order is identical to the website.
        var cloudEntries = PlaytimeService.GetAllCloudTotals()
            .Where(t => t.Minutes > 0 && !string.IsNullOrEmpty(t.LastPlayed))
            .ToList();

        // Build an O(1) lookup set so the legacy-entry filter below is O(n) not O(n²).
        var cloudEntryKeys = new HashSet<string>(
            cloudEntries.Select(c => $"{c.Platform.ToLowerInvariant()}||{c.Title.ToLowerInvariant()}"),
            StringComparer.OrdinalIgnoreCase);

        // Include cloud-library games whose playtime was recorded before the activity log
        // existed (legacy data). They use LastPlayedAt from games.json as their timestamp.
        var legacyLibraryEntries = library
            .Where(g => g.PlaytimeMinutes > 0 && !string.IsNullOrEmpty(g.LastPlayedAt))
            .Where(g => !cloudEntryKeys.Contains(
                $"{g.Platform.ToLowerInvariant()}||{g.Title.ToLowerInvariant()}"))
            .Select(g => (Platform: g.Platform, Title: g.Title,
                          Minutes: g.PlaytimeMinutes, LastPlayed: g.LastPlayedAt!));

        // Single globally-sorted stream: activity log + legacy library entries
        var allEntries = cloudEntries
            .Concat(legacyLibraryEntries)
            .OrderByDescending(t => ParseDate(t.LastPlayed));

        foreach (var (platform, title, minutes, lastPlayed) in allEntries)
        {
            if (RecentLocalGames.Count >= MaxRecentGames) break;

            var key = $"{platform.ToLowerInvariant()}||{title.ToLowerInvariant()}";
            if (addedKeys.Contains(key)) continue;
            addedKeys.Add(key);

            // Prefer a locally-installed card (can be launched directly).
            if (localCardMap.TryGetValue(key, out var localCard))
            {
                int localMins = PlaytimeService.GetTotalMinutes(platform, title);
                localCard.PlaytimeLabel = FormatMinutes(Math.Max(localMins, minutes));
                RecentLocalGames.Add(localCard);
                continue;
            }

            // Fall back to a cloud-library entry (has cover art and metadata).
            if (cloudGameMap.TryGetValue(key, out var cloudGame))
            {
                var card = new LocalGameCardVm
                {
                    Title           = cloudGame.Title,
                    Platform        = cloudGame.Platform,
                    CoverUrl        = cloudGame.CoverUrl,
                    CoverGradient   = cloudGame.CoverGradient ?? DefaultCloudCardGradient,
                    SourceCloudGame = cloudGame,
                };
                card.PlaytimeLabel = FormatMinutes(Math.Max(cloudGame.PlaytimeMinutes, minutes));
                RecentLocalGames.Add(card);
                continue;
            }

            // Activity-only game: played on another device, not installed here.
            // Show a placeholder card so the list mirrors the website timeline.
            var syntheticGame = new Game
            {
                Title           = title,
                Platform        = platform,
                PlaytimeMinutes = minutes,
                LastPlayedAt    = lastPlayed,
            };
            var activityCard = new LocalGameCardVm
            {
                Title           = title,
                Platform        = platform,
                CoverGradient   = DefaultCloudCardGradient,
                SourceCloudGame = syntheticGame,
            };
            activityCard.PlaytimeLabel = FormatMinutes(minutes);
            RecentLocalGames.Add(activityCard);
        }

        // Step 3 — local-only games with playtime that have not yet been synced to the
        // cloud (e.g. offline sessions). Append them after the cloud-sorted entries.
        if (localCards != null && RecentLocalGames.Count < MaxRecentGames)
        {
            foreach (var c in localCards
                .Where(c => PlaytimeService.GetTotalMinutes(c.Platform, c.EffectiveTitle) > 0
                         && !addedKeys.Contains(
                             $"{c.Platform.ToLowerInvariant()}||{c.EffectiveTitle.ToLowerInvariant()}"))
                .OrderByDescending(c => PlaytimeService.GetLastPlayedAt(c.Platform, c.EffectiveTitle))
                .Take(MaxRecentGames - RecentLocalGames.Count))
            {
                c.PlaytimeLabel = FormatMinutes(
                    PlaytimeService.GetTotalMinutes(c.Platform, c.EffectiveTitle));
                RecentLocalGames.Add(c);
                addedKeys.Add($"{c.Platform.ToLowerInvariant()}||{c.EffectiveTitle.ToLowerInvariant()}");
            }
        }

        HasRecentLocalGames = RecentLocalGames.Count > 0;

        LocalLibraryGames.Clear();
        if (localCards != null)
        {
            foreach (var c in localCards
                .OrderByDescending(c => PlaytimeService.GetLastPlayedAt(c.Platform, c.EffectiveTitle))
                .ThenBy(c => c.EffectiveTitle)
                .Take(MaxLibraryGames))
            {
                LocalLibraryGames.Add(c);
            }
        }
        HasLocalLibraryGames = LocalLibraryGames.Count > 0;

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
