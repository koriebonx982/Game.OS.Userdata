using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Models;

namespace GameLauncher.ViewModels;

public partial class StoreViewModel : ViewModelBase, IDisposable
{
    private List<StoreGame> _allStore     = new();
    private List<StoreGame> _allStoreBase = new(); // curated catalog, restored on "All"
    private List<Game>      _library  = new();
    private UserProfile     _profile  = new();
    private GameOsClient    _client   = new();

    [ObservableProperty] private string  _searchText = "";
    [ObservableProperty] private string  _filterGenre = "All";
    [ObservableProperty] private string? _statusMessage;

    // ── Platform selector ─────────────────────────────────────────────────
    [ObservableProperty] private string _selectedPlatform = "All";
    [ObservableProperty] private bool   _isLoading        = false;
    [ObservableProperty] private string _loadingMessage   = "";
    [ObservableProperty] private string _browseCountLabel = "";

    /// <summary>Platforms available in the Games.Database (plus "All" for the curated catalog).</summary>
    public ObservableCollection<string> Platforms { get; } = new()
        { "All", "PC", "PS3", "PS4", "Switch", "Xbox 360" };

    // ── Tab navigation: Games Store vs App Store ──────────────────────────
    [ObservableProperty] private bool _isGamesTab    = true;
    [ObservableProperty] private bool _isAppStoreTab = false;

    [ObservableProperty] private string _appStoreSearchText = "";
    [ObservableProperty] private string _appStoreGenreFilter = "All";
    public ObservableCollection<AppStoreEntry> AllAppStoreEntries    { get; } = new();
    public ObservableCollection<AppStoreEntry> FilteredAppStoreEntries { get; } = new();
    public ObservableCollection<string>        AppStoreGenres        { get; } = new();

    partial void OnAppStoreSearchTextChanged(string value)  => ApplyAppStoreFilter();
    partial void OnAppStoreGenreFilterChanged(string value) => ApplyAppStoreFilter();

    // ── Admin catalog management ──────────────────────────────────────────
    [ObservableProperty] private bool   _isAdmin      = false;
    [ObservableProperty] private bool   _showAdminForm = false;
    [ObservableProperty] private string _adminTitle       = "";
    [ObservableProperty] private string _adminPlatform    = "PC";
    [ObservableProperty] private string _adminGenre       = "";
    [ObservableProperty] private string _adminPrice       = "";
    [ObservableProperty] private string _adminDescription = "";
    [ObservableProperty] private string _adminRating      = "8.0";
    [ObservableProperty] private string _adminCoverUrl    = "";

    /// <summary>
    /// Total games across all platforms in the Koriebonx98/Games.Database repository.
    /// PC: ~153,713  |  Xbox 360: 5,132  |  PS3: 4,000  |  Switch: 2,245  |  PS4: 5
    /// Updated by counting each {Platform}.Games.json in the public repository.
    /// </summary>
    private const int RealDatabaseTotal = 165_095;

    /// <summary>
    /// Maximum number of game cards to render at once in the UI.
    /// Prevents the WrapPanel from rendering 150,000+ items for large platforms.
    /// The search box can be used to narrow the results below this threshold,
    /// or the "Load More" button appends the next page of results.
    /// </summary>
    private const int MaxDisplayedGames = 2000;

    [ObservableProperty] private int    _totalCatalogCount = 0;
    [ObservableProperty] private string _catalogCountLabel = "";
    [ObservableProperty] private bool   _hasMoreGames      = false;

    /// <summary>Current upper limit on how many games are shown. Increases with each "Load More" click.</summary>
    private int _displayLimit = MaxDisplayedGames;
    public int DisplayLimit
    {
        get => _displayLimit;
        private set { _displayLimit = value; OnPropertyChanged(); }
    }

    /// <summary>True once the real catalog count has been loaded; drives the subtitle visibility.</summary>
    public bool HasCatalogCount => TotalCatalogCount > 0;

    partial void OnTotalCatalogCountChanged(int value) => OnPropertyChanged(nameof(HasCatalogCount));

    public ObservableCollection<StoreGame> Featured      { get; } = new();
    public ObservableCollection<StoreGame> FilteredStore { get; } = new();
    public ObservableCollection<string>    Genres        { get; } = new();

    /// <summary>True when Featured contains at least one game.</summary>
    public bool HasFeatured => Featured.Count > 0;

    /// <summary>Invoked when the user clicks a store game card.</summary>
    public Action<StoreGame>? OnOpenDetail { get; set; }

    public void Load(List<StoreGame> store, List<Game> library,
                     UserProfile profile, GameOsClient client, bool isAdmin,
                     int totalCatalogCount = RealDatabaseTotal)
    {
        _allStoreBase = new List<StoreGame>(store); // keep the curated catalog
        _allStore     = new List<StoreGame>(store); // work on a copy so admin changes are session-scoped
        _library  = library;
        _profile  = profile;
        _client   = client;
        IsAdmin   = isAdmin;

        TotalCatalogCount = totalCatalogCount;
        CatalogCountLabel = $"{totalCatalogCount:N0}+ games in the database";

        // Reset to curated "All" view (if already "All" the setter is a no-op;
        // if coming back from a different platform, LoadPlatformAsync("All") runs
        // synchronously to restore the curated catalog before RebuildCollections below).
        SelectedPlatform = "All";

        RebuildCollections();

        // Eagerly load App Store in the background
        _ = LoadAppStoreAsync();
    }

    [RelayCommand]
    private void SwitchToGamesTab()
    {
        IsGamesTab    = true;
        IsAppStoreTab = false;
    }

    [RelayCommand]
    private void SwitchToAppStoreTab()
    {
        IsGamesTab    = false;
        IsAppStoreTab = true;
        if (AllAppStoreEntries.Count == 0)
            _ = LoadAppStoreAsync();
    }

    private async Task LoadAppStoreAsync()
    {
        try
        {
            var entries = await GameOsClient.FetchAppStoreAsync();
            AllAppStoreEntries.Clear();
            foreach (var e in entries)
                AllAppStoreEntries.Add(e);

            // Build genre list
            AppStoreGenres.Clear();
            AppStoreGenres.Add("All");
            foreach (var genre in entries.Select(e => e.Genre).Distinct().OrderBy(g => g))
                AppStoreGenres.Add(genre);

            AppStoreGenreFilter = "All";
            ApplyAppStoreFilter();
        }
        catch { /* best-effort */ }
    }

    private void ApplyAppStoreFilter()
    {
        FilteredAppStoreEntries.Clear();
        var results = AllAppStoreEntries.AsEnumerable();

        if (AppStoreGenreFilter != "All")
            results = results.Where(e => e.Genre == AppStoreGenreFilter);

        if (!string.IsNullOrWhiteSpace(AppStoreSearchText))
            results = results.Where(e =>
                e.Name.Contains(AppStoreSearchText, StringComparison.OrdinalIgnoreCase) ||
                e.Genre.Contains(AppStoreSearchText, StringComparison.OrdinalIgnoreCase) ||
                e.Platform.Contains(AppStoreSearchText, StringComparison.OrdinalIgnoreCase));

        foreach (var e in results.OrderBy(e => e.Genre).ThenBy(e => e.Name))
            FilteredAppStoreEntries.Add(e);
    }

    [RelayCommand]
    private void OpenAppUrl(AppStoreEntry? app)
    {
        if (app == null || string.IsNullOrEmpty(app.Url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = app.Url,
                UseShellExecute = true,
            });
        }
        catch { /* best-effort */ }
    }

    /// <summary>Clears the disk cache for the current platform and re-fetches from GitHub.</summary>
    [RelayCommand]
    private void RefreshPlatform()
    {
        if (SelectedPlatform == "All") return;
        // Force a fresh fetch by removing the platform from the in-memory cache
        Services.GitHubDataService.InvalidatePlatformCache(SelectedPlatform);
        // Re-trigger the load
        var platform = SelectedPlatform;
        SelectedPlatform = "All"; // reset first to ensure the setter fires
        SelectedPlatform = platform;
    }

    private void RebuildCollections()
    {
        Featured.Clear();
        foreach (var g in _allStore.Where(s => s.IsFeatured))
            Featured.Add(g);
        OnPropertyChanged(nameof(HasFeatured));

        Genres.Clear();
        Genres.Add("All");
        foreach (var genre in _allStore.Select(s => s.Genre).Distinct().OrderBy(g => g))
            Genres.Add(genre);

        FilterGenre = "All"; // resets genre filter; callback fires ApplyFilter() if changed
        ApplyFilter();       // always re-apply in case FilterGenre was already "All"
    }

    partial void OnSearchTextChanged(string value)   { DisplayLimit = MaxDisplayedGames; ApplyFilter(); }
    partial void OnFilterGenreChanged(string value)  { DisplayLimit = MaxDisplayedGames; ApplyFilter(); }

    // Cancellation source for in-flight platform loads
    private CancellationTokenSource _loadCts = new();

    partial void OnSelectedPlatformChanged(string value)
    {
        // Reset pagination and cancel any previous in-flight load
        DisplayLimit = MaxDisplayedGames;
        _loadCts.Cancel();
        _loadCts.Dispose();
        _loadCts = new CancellationTokenSource();
        _ = LoadPlatformAsync(value, _loadCts.Token);
    }

    private async Task LoadPlatformAsync(
        string platform, CancellationToken ct)
    {
        if (platform == "All")
        {
            // Restore curated catalog instantly — no network needed
            _allStore = new List<StoreGame>(_allStoreBase);
            RebuildCollections();
            return;
        }

        IsLoading      = true;
        LoadingMessage = $"Loading {platform} games…";
        StatusMessage  = null;
        FilteredStore.Clear();
        Featured.Clear();
        OnPropertyChanged(nameof(HasFeatured));

        try
        {
            var dbGames = await GameOsClient.FetchGamesDatabaseAsync(platform, ct);
            if (ct.IsCancellationRequested) return;

            _allStore = dbGames
                .Select(g => new StoreGame
                {
                    Title           = WebUtility.HtmlDecode(g.Title ?? ""),
                    Platform        = platform,
                    Genre           = "Unknown",
                    Price           = "N/A",
                    Rating          = 0,
                    CoverUrl        = g.CoverUrl,
                    Description     = g.Description ?? "",
                    TrailerUrl      = g.TrailerUrl,
                    AchievementsUrl = g.AchievementsUrl,
                    Screenshots     = g.Screenshots,
                    StorePageUrl    = g.StorePageUrl,
                })
                .ToList();

            RebuildCollections();
        }
        catch (OperationCanceledException)
        {
            // Platform changed before load completed — silently discard
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            if (ct.IsCancellationRequested) return;

            // Network unavailable — try to serve from the stale disk cache so the Store
            // remains functional offline (mirrors the offline-first goal from PR #218).
            System.Diagnostics.Debug.WriteLine(
                $"[StoreVM] Network unavailable loading {platform}: {ex.Message}. " +
                "Trying local disk cache (may be stale).");

            var stale = Services.GitHubDataService.TryLoadStaleDiskCacheForStore(platform);
            if (stale != null && stale.Count > 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[StoreVM] Loaded {stale.Count} games for {platform} from local cache (offline).");
                _allStore = stale
                    .Select(g => new StoreGame
                    {
                        Title           = System.Net.WebUtility.HtmlDecode(g.Title ?? ""),
                        Platform        = platform,
                        Genre           = "Unknown",
                        Price           = "N/A",
                        Rating          = 0,
                        CoverUrl        = g.CoverUrl,
                        Description     = g.Description ?? "",
                        TrailerUrl      = g.TrailerUrl,
                        AchievementsUrl = g.AchievementsUrl,
                        Screenshots     = g.Screenshots,
                        StorePageUrl    = g.StorePageUrl,
                    })
                    .ToList();
                StatusMessage = $"Offline — showing cached {platform} data.";
            }
            else
            {
                StatusMessage = $"Offline — no cached data available for {platform}.";
                _allStore = new List<StoreGame>();
            }
            RebuildCollections();
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                StatusMessage = $"Failed to load {platform} games: {ex.Message}";
                // Restore an empty (but consistent) view so the UI doesn't stay blank
                _allStore = new List<StoreGame>();
                RebuildCollections();
            }
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsLoading      = false;
                LoadingMessage = "";
            }
        }
    }

    private void ApplyFilter()
    {
        FilteredStore.Clear();
        var results = _allStore.AsEnumerable();

        if (FilterGenre != "All")
            results = results.Where(s => s.Genre == FilterGenre);

        if (!string.IsNullOrWhiteSpace(SearchText))
            results = results.Where(s =>
                s.Title.Contains(SearchText, System.StringComparison.OrdinalIgnoreCase) ||
                s.Genre.Contains(SearchText, System.StringComparison.OrdinalIgnoreCase));

        // Materialize once to get the total count, then sort and take the display page.
        var allMatches = results.ToList();
        int total = allMatches.Count;
        int shown = Math.Min(total, DisplayLimit);

        foreach (var g in allMatches
            .OrderByDescending(s => s.Rating)
            .ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
            .Take(shown))
            FilteredStore.Add(g);

        HasMoreGames = shown < total;

        BrowseCountLabel = total == 0
            ? ""
            : shown < total
                ? $"Showing {shown:N0} of {total:N0} games — use search to filter or load more below"
                : $"{total:N0} game{(total == 1 ? "" : "s")}";
    }

    /// <summary>Appends the next page of games to the current filtered view.</summary>
    [RelayCommand]
    private void LoadMore()
    {
        DisplayLimit += MaxDisplayedGames;
        ApplyFilter();
    }

    public bool IsOwned(string title) =>
        _library.Any(g => g.Title.Equals(title, System.StringComparison.OrdinalIgnoreCase));

    [RelayCommand]
    private void OpenGameDetail(StoreGame? game)
    {
        if (game != null) OnOpenDetail?.Invoke(game);
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task AddGameAsync(StoreGame? game)
    {
        if (game == null) return;
        if (IsOwned(game.Title))
        {
            StatusMessage = $"'{game.Title}' is already in your library.";
            return;
        }

        StatusMessage = $"Adding '{game.Title}'…";
        try
        {
            // Build the full Game so all metadata (cover URL, genre, description,
            // rating, screenshots) is persisted to GitHub — the same data that
            // accounts/{user}/games.json stores on the website backend.
            var newGame = new Game
            {
                Platform        = game.Platform,
                Title           = game.Title,
                Genre           = game.Genre,
                Rating          = game.Rating,
                Description     = game.Description,
                CoverUrl        = game.CoverUrl,
                Screenshots     = game.Screenshots,
                Price           = game.Price,
                TrailerUrl      = game.TrailerUrl,
                AchievementsUrl = game.AchievementsUrl,
                AddedAt         = System.DateTimeOffset.UtcNow.ToString("o"),
            };
            // CoverColor / CoverGradient are [JsonIgnore] — UI-only, not persisted
            newGame.CoverColor    = game.CoverColor;
            newGame.CoverGradient = game.CoverGradient;

            await _client.AddGameAsync(newGame);
            _library.Add(newGame);
            StatusMessage = $"✓  '{game.Title}' added to your library!";
        }
        catch (System.Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }

        ApplyFilter();
    }

    // ── Admin: toggle add-game form ───────────────────────────────────────
    [RelayCommand]
    private void AdminToggleForm()
    {
        ShowAdminForm = !ShowAdminForm;
        if (ShowAdminForm)
        {
            AdminTitle = ""; AdminPlatform = "PC"; AdminGenre = "";
            AdminPrice = ""; AdminDescription = ""; AdminRating = "8.0";
            AdminCoverUrl = "";
        }
    }

    // ── Admin: add a new game to the catalog (session-only) ───────────────
    [RelayCommand]
    private void AdminAddCatalogGame()
    {
        if (string.IsNullOrWhiteSpace(AdminTitle) || string.IsNullOrWhiteSpace(AdminPlatform))
        {
            StatusMessage = "Title and Platform are required.";
            return;
        }
        if (!double.TryParse(AdminRating, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double rating))
            rating = 8.0;

        var newGame = new StoreGame
        {
            Title       = AdminTitle.Trim(),
            Platform    = AdminPlatform.Trim(),
            Genre       = string.IsNullOrWhiteSpace(AdminGenre) ? "Other" : AdminGenre.Trim(),
            Price       = string.IsNullOrWhiteSpace(AdminPrice) ? "Free" : AdminPrice.Trim(),
            Description = AdminDescription.Trim(),
            Rating      = Math.Clamp(rating, 0, 10),
            CoverUrl    = string.IsNullOrWhiteSpace(AdminCoverUrl) ? null : AdminCoverUrl.Trim(),
            IsFeatured  = false,
            ReleaseYear = System.DateTime.Now.Year.ToString()
        };

        _allStore.Add(newGame);
        RebuildCollections();
        ShowAdminForm = false;
        StatusMessage = $"✓  '{newGame.Title}' added to the catalog (this session only).";
    }

    // ── Admin: remove a game from the catalog (session-only) ─────────────
    [RelayCommand]
    private void AdminDeleteCatalogGame(StoreGame? game)
    {
        if (game == null) return;
        _allStore.RemoveAll(s => s.Title == game.Title && s.Platform == game.Platform);
        RebuildCollections();
        StatusMessage = $"✓  '{game.Title}' removed from the catalog (this session only).";
    }

    public void Dispose()
    {
        _loadCts.Cancel();
        _loadCts.Dispose();
    }
}
