using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Models;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

/// <summary>
/// View-model for the game detail overlay, supporting cloud library games,
/// store games, and locally detected games (with multi-drive switching).
/// </summary>
public partial class GameDetailViewModel : ViewModelBase
{
    // ── Display properties ────────────────────────────────────────────────────
    [ObservableProperty] private string  _title        = "";
    [ObservableProperty] private string  _platform     = "";
    [ObservableProperty] private string  _genre        = "";
    [ObservableProperty] private string  _description  = "";
    [ObservableProperty] private string  _ratingStars  = "";
    [ObservableProperty] private string? _price;
    [ObservableProperty] private string? _releaseYear;
    [ObservableProperty] private string? _coverUrl;
    [ObservableProperty] private string? _coverGradient;

    // ── Per-game playtime ─────────────────────────────────────────────────────
    /// <summary>Human-readable playtime for this specific game, e.g. "3h 20m".</summary>
    [ObservableProperty] private string _playtimeLabel = "";
    [ObservableProperty] private bool   _hasPlaytime;

    // ── Regions / Language (for ROM games) ───────────────────────────────────
    [ObservableProperty] private string  _regionsLabel  = "";
    [ObservableProperty] private bool    _hasRegions;

    // ── Store page link ───────────────────────────────────────────────────────
    [ObservableProperty] private string? _storePageUrl;
    [ObservableProperty] private bool    _hasStoreUrl;
    [ObservableProperty] private string  _storeButtonLabel = "🛒  View in Store";

    // ── Trailer ───────────────────────────────────────────────────────────────
    /// <summary>YouTube trailer URL from the real Games.Database (e.g. https://youtu.be/…).</summary>
    [ObservableProperty] private string? _trailerUrl;
    [ObservableProperty] private bool    _hasTrailer;
    [ObservableProperty] private string  _trailerLabel = "▶  Watch Trailer";

    /// <summary>
    /// True when the in-app trailer player overlay is visible.
    /// Bound to the VideoView overlay in GameDetailView.axaml.
    /// </summary>
    [ObservableProperty] private bool    _isTrailerPlayerOpen;

    /// <summary>
    /// The extracted YouTube video ID (e.g. "dQw4w9WgXcQ").
    /// Empty string when <see cref="TrailerUrl"/> is not a recognised YouTube URL.
    /// </summary>
    public string YoutubeVideoId => ExtractYoutubeVideoId(TrailerUrl);

    /// <summary>
    /// The YouTube embed URL used by the in-app WebView player
    /// (e.g. <c>https://www.youtube.com/embed/dQw4w9WgXcQ?autoplay=1</c>).
    /// Empty string when <see cref="TrailerUrl"/> is not a recognised YouTube URL.
    /// </summary>
    public string TrailerEmbedUrl =>
        string.IsNullOrEmpty(YoutubeVideoId)
            ? ""
            : $"https://www.youtube.com/embed/{YoutubeVideoId}?autoplay=1&rel=0";

    partial void OnTrailerUrlChanged(string? value)
    {
        OnPropertyChanged(nameof(YoutubeVideoId));
        OnPropertyChanged(nameof(TrailerEmbedUrl));
    }

    /// <summary>
    /// Extracts the YouTube video ID from <c>youtube.com/watch?v=</c>, <c>youtu.be/</c>,
    /// or <c>youtube.com/shorts/</c> URLs.  Returns an empty string for other URLs.
    /// </summary>
    private static string ExtractYoutubeVideoId(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return "";
        var host = uri.Host.ToLowerInvariant();

        if (host == "youtu.be" || host == "www.youtu.be")
            return uri.AbsolutePath.TrimStart('/').Split('?')[0];

        if (host == "youtube.com" || host == "www.youtube.com")
        {
            // /watch?v=ID — parse query string manually (avoids System.Web dependency)
            var query = uri.Query.TrimStart('?');
            foreach (var part in query.Split('&'))
            {
                var eq = part.IndexOf('=');
                if (eq < 0) continue;
                var paramName  = Uri.UnescapeDataString(part[..eq]);
                var paramValue = Uri.UnescapeDataString(part[(eq + 1)..]);
                if (paramName == "v" && !string.IsNullOrEmpty(paramValue))
                    return paramValue;
            }

            // /shorts/ID  or  /embed/ID
            var segs = uri.AbsolutePath.TrimStart('/').Split('/');
            if (segs.Length >= 2 && (segs[0] == "shorts" || segs[0] == "embed"))
                return segs[1].Split('?')[0];
        }

        return "";
    }

    // ── Screenshots ───────────────────────────────────────────────────────────
    public ObservableCollection<string> Screenshots { get; } = new();
    [ObservableProperty] private bool _hasScreenshots;

    // ── Achievements ──────────────────────────────────────────────────────────
    public ObservableCollection<Achievement> Achievements { get; } = new();
    /// <summary>Subset of Achievements currently visible (respects ShowAllAchievements flag).</summary>
    public ObservableCollection<Achievement> VisibleAchievements { get; } = new();
    [ObservableProperty] private bool   _hasAchievements;
    [ObservableProperty] private string _achievementsLabel = "";
    /// <summary>When false, only the first <see cref="AchievementsPreviewCount"/> achievements are shown.</summary>
    [ObservableProperty] private bool   _showAllAchievements = false;
    [ObservableProperty] private bool   _hasMoreAchievements = false;
    private const int AchievementsPreviewCount = 6;

    partial void OnShowAllAchievementsChanged(bool value) => RefreshVisibleAchievements();

    [RelayCommand]
    private void ToggleShowAllAchievements()
        => ShowAllAchievements = !ShowAllAchievements;

    // ── Local game / drive info ───────────────────────────────────────────────
    [ObservableProperty] private bool   _isLocalGame;
    [ObservableProperty] private bool   _hasMultipleDrives;
    [ObservableProperty] private int    _selectedDriveIndex;
    [ObservableProperty] private string _activeDriveLabel = "";
    [ObservableProperty] private string _activeDrivePath  = "";
    [ObservableProperty] private string _activeExeType    = "";

    /// <summary>
    /// Database description stored when <see cref="EnrichFromDatabaseGame"/> is called.
    /// Prevents <see cref="RefreshActiveDrive"/> from overwriting a real description
    /// with the "Installed at: …" placeholder.
    /// </summary>
    private string? _databaseDescription;

    /// <summary>
    /// TitleID from the Games Database, stored when <see cref="EnrichFromDatabaseGame"/>
    /// is called.  Used as a fallback by <see cref="LoadSwitchMods"/> and
    /// <see cref="OpenSwitchModsFolder"/> when the scanned ROM does not carry a TitleID.
    /// </summary>
    private string? _databaseTitleId;

    // ── Install / launch state ────────────────────────────────────────────────
    /// <summary>True when the game is found installed on a local drive.</summary>
    [ObservableProperty] private bool _isInstalled;
    /// <summary>True when a repack archive is available to install (but game is not yet installed).</summary>
    [ObservableProperty] private bool _isRepack;
    /// <summary>File path of the repack archive/folder/setup, used by the Install command.</summary>
    [ObservableProperty] private string _repackPath = "";
    /// <summary>Display label for the repack archive size.</summary>
    [ObservableProperty] private string _repackSizeLabel = "";
    /// <summary>True when the repack is a folder with a setup installer (Setup.exe).</summary>
    [ObservableProperty] private bool _isSetupRepack;
    /// <summary>True when the repack is an archive and we should show a drive-selection picker.</summary>
    [ObservableProperty] private bool _showDrivePicker;
    /// <summary>Extraction progress percentage (0–100) shown in the progress bar during archive extraction.</summary>
    [ObservableProperty] private double _extractionProgress;
    /// <summary>True while an archive is being extracted — drives the progress bar visibility.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallButtonLabel))]
    private bool _isExtracting;
    /// <summary>Label for the Install button: "Install" normally, "Installing…" during extraction.</summary>
    public string InstallButtonLabel => IsExtracting ? "Installing…" : "Install";
    /// <summary>Status message shown in the main info panel during/after archive installation (not the settings panel).</summary>
    [ObservableProperty] private string _installStatusMessage = "";
    /// <summary>True when <see cref="InstallStatusMessage"/> represents an error (drives red foreground in the UI).</summary>
    [ObservableProperty] private bool _installStatusIsError;
    /// <summary>Foreground colour for <see cref="InstallStatusMessage"/>: red on error, green on success.</summary>
    public string InstallStatusForeground => InstallStatusIsError ? "#f85149" : "#3fb950";

    partial void OnInstallStatusIsErrorChanged(bool value) => OnPropertyChanged(nameof(InstallStatusForeground));

    // ── Running-game state (Play → Playing... → Resume) ───────────────────────
    /// <summary>True while the launched game process is still running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayButtonLabel))]
    [NotifyPropertyChangedFor(nameof(PlayButtonIsResume))]
    private bool _isGameRunning;

    /// <summary>
    /// Label shown on the main play button text block (the arrow icon is separate in XAML).
    /// "Play" normally, "Playing..." while the process is active.
    /// The "Resume" state is indicated by <see cref="PlayButtonIsResume"/> instead.
    /// </summary>
    public string PlayButtonLabel =>
        IsGameRunning ? "Playing..." : "Play";

    /// <summary>
    /// True once the game has been launched from this detail view and the process
    /// has exited — the button switches to a blue "Resume" style so the user can
    /// bring the game window back to the foreground.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayButtonLabel))]
    private bool _playButtonIsResume;

    /// <summary>
    /// The running game process stored so we can bring it to the foreground when
    /// the user clicks the Resume button.  Null when not tracked.
    /// </summary>
    private System.Diagnostics.Process? _runningProcess;

    /// <summary>
    /// Called by MainViewModel after a tracked game session ends so the button
    /// can switch from "Playing..." to "Resume" (or back to "Play" if the detail
    /// view was closed and re-opened).
    /// </summary>
    public void OnGameSessionEnded(string platform, string title)
    {
        if (!string.Equals(platform, Platform, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(title, Title, StringComparison.OrdinalIgnoreCase))
            return;

        IsGameRunning    = false;
        PlayButtonIsResume = false; // game exited — no window to resume
        _runningProcess  = null;
    }

    /// <summary>
    /// Kills the currently running game process immediately (called from Quick Menu "Exit Game").
    /// Has no effect when no process is being tracked.
    /// </summary>
    public void ForceExitGame()
    {
        try
        {
            if (_runningProcess != null && !_runningProcess.HasExited)
                _runningProcess.Kill(entireProcessTree: true);
        }
        catch { /* best-effort */ }
    }

    // ── Repack-available badge (shown alongside an installed game) ────────────
    /// <summary>True when the game is installed AND a matching repack archive is also available.</summary>
    [ObservableProperty] private bool _hasMatchingRepack;
    /// <summary>Human-readable label for the matching repack (e.g. "🗜 Repack available · 12.4 GB").</summary>
    [ObservableProperty] private string _matchingRepackLabel = "";

    /// <summary>Available drives for archive-repack installation.</summary>
    public ObservableCollection<InstallDriveOption> InstallDrives { get; } = new();

    // ── Static compiled regex ─────────────────────────────────────────────────
    /// <summary>Matches 7-Zip progress output like "  42% - filename".</summary>
    private static readonly Regex _sevenZipProgressRegex =
        new(@"(\d+)%", RegexOptions.Compiled);

    /// <summary>Switch TitleID pattern: exactly 16 hexadecimal characters (e.g. "0100152000022000").</summary>
    private static readonly Regex _switchTitleIdValidationRegex =
        new(@"^[0-9A-Fa-f]{16}$", RegexOptions.Compiled);

    public ObservableCollection<string> DriveLabels { get; } = new();

    private List<LocalGameDriveEntry> _driveInstances = new();

    /// <summary>Steam AppId for locally-installed Steam games (0 for non-Steam games).</summary>
    private int _steamAppId;

    /// <summary>
    /// True when the game is a Steam-API-registered title that is not currently
    /// installed locally.  Shows an "Install via Steam" button in the UI.
    /// </summary>
    [ObservableProperty] private bool   _isSteamInstallable;
    /// <summary>steam://install/{AppId} URL for the Install via Steam button.</summary>
    [ObservableProperty] private string _steamInstallUrl = "";
    /// <summary>
    /// True when a Steam AppId is known for this game, making a "▶ Launch via Steam"
    /// entry available alongside other exe options in the settings panel.
    /// </summary>
    [ObservableProperty] private bool   _hasSteamLaunchOption;

    /// <summary>Cached reference to the current LocalRom (if any), used to expose AdditionalPaths in settings.</summary>
    private LocalRom? _currentLocalRom;

    // ── Nintendo Switch / Ryujinx mod management ──────────────────────────────
    /// <summary>True when the current game is a Nintendo Switch title.</summary>
    [ObservableProperty] private bool _isSwitch;
    /// <summary>True when at least one Ryujinx mod is found for this game's TitleID.</summary>
    [ObservableProperty] private bool _hasSwitchMods;
    /// <summary>Controls visibility of the mods management panel (toggled by the Mods button).</summary>
    [ObservableProperty] private bool _showModsPanel;
    /// <summary>Status message shown at the bottom of the mods section (save confirmation / error).</summary>
    [ObservableProperty] private string _switchModsStatus = "";
    /// <summary>True when mods.json was found but contained no entries (vs. the file not existing at all).</summary>
    [ObservableProperty] private bool _modsJsonExistsButEmpty;
    /// <summary>True when no mods are loaded AND the mods.json file was not found (shows "no file" empty state).</summary>
    public bool ShowModsNotFoundMessage => !HasSwitchMods && !ModsJsonExistsButEmpty;

    partial void OnHasSwitchModsChanged(bool value)    => OnPropertyChanged(nameof(ShowModsNotFoundMessage));
    partial void OnModsJsonExistsButEmptyChanged(bool value) => OnPropertyChanged(nameof(ShowModsNotFoundMessage));

    /// <summary>Full path to the mods.json currently loaded (null when mods are not available).</summary>
    private string? _ryujinxModsJsonPath;
    /// <summary>All Ryujinx mods for the current game, populated by <see cref="LoadSwitchMods"/>.</summary>
    public ObservableCollection<RyujinxModVm> SwitchMods { get; } = new();

    /// <summary>Full path to the mods.json for the current Switch game, shown in the mods panel.</summary>
    [ObservableProperty] private string _switchModsJsonPath = "";

    // ── Per-game reviews ──────────────────────────────────────────────────────
    public System.Collections.ObjectModel.ObservableCollection<Models.GameReview> Reviews { get; } = new();
    [ObservableProperty] private bool   _hasReviews;
    [ObservableProperty] private bool   _showReviewPanel;
    [ObservableProperty] private int    _newReviewRating = 5; // 1–5 stars
    [ObservableProperty] private string _newReviewNote   = "";
    [ObservableProperty] private string _reviewStatus    = "";

    [RelayCommand]
    private void ToggleReviewPanel() => ShowReviewPanel = !ShowReviewPanel;

    [RelayCommand]
    private void SubmitReview()
    {
        if (string.IsNullOrEmpty(_currentUsername)) return;
        if (NewReviewRating < 1 || NewReviewRating > 5) return;

        var review = new Models.GameReview
        {
            Username  = _currentUsername,
            Rating    = NewReviewRating,
            Note      = NewReviewNote.Trim(),
            CreatedAt = System.DateTime.UtcNow.ToString("o"),
        };

        Services.GameReviewService.AddOrUpdateReview(Platform, Title, review);

        // Upload to Games.Database in the background (fire-and-forget)
        string? titleId = _currentLocalRom?.TitleId ?? _databaseTitleId;
        string  title   = Title;
        string  platform = Platform;
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                using var svc = new Services.GitHubDataService();
                await svc.UploadReviewToDatabaseAsync(platform, titleId, title, review);
            }
            catch (Exception ex)
            {
                Services.DevLogService.Log($"[Review] Upload failed: {ex.Message}");
            }
        });

        // Refresh the displayed list
        LoadReviews();

        NewReviewNote  = "";
        ReviewStatus   = "✓ Review saved!";
    }

    /// <summary>
    /// The current user's username, set by <see cref="MainViewModel"/> after login
    /// so the review panel knows which user is submitting.
    /// </summary>
    private string _currentUsername = "";

    /// <summary>Wired by MainViewModel after login to attach the logged-in username.</summary>
    public void SetCurrentUser(string username) => _currentUsername = username;

    private void LoadReviews()
    {
        Reviews.Clear();
        var all = Services.GameReviewService.LoadReviews(Platform, Title);
        foreach (var r in all)
            Reviews.Add(r);
        HasReviews = Reviews.Count > 0;
    }

    // ── Per-game compatibility reports ────────────────────────────────────────
    public System.Collections.ObjectModel.ObservableCollection<Models.GameCompatibility> CompatibilityReports { get; } = new();
    [ObservableProperty] private bool   _hasCompatibilityReports;
    [ObservableProperty] private bool   _showCompatibilityPanel;

    [RelayCommand]
    private void ToggleCompatibilityPanel() => ShowCompatibilityPanel = !ShowCompatibilityPanel;

    // ── Navigation back-action ────────────────────────────────────────────────
    public System.Action? OnClose { get; set; }

    [RelayCommand]
    private void Close() => OnClose?.Invoke();

    // ── Settings panel ────────────────────────────────────────────────────────
    /// <summary>True when the settings overlay is visible.</summary>
    [ObservableProperty] private bool _showSettings;

    /// <summary>Custom .exe or .bat path saved for this game (overrides auto-detected).</summary>
    [ObservableProperty] private string _settingsExePath = "";

    /// <summary>Command-line arguments for the selected executable.</summary>
    [ObservableProperty] private string _settingsExeArgs = "";

    /// <summary>ROM file path used when launching via an emulator (for non-PC platforms).</summary>
    [ObservableProperty] private string _settingsRomPath = "";

    /// <summary>True when this game is a ROM (non-PC) and shows the Rom Select field.</summary>
    [ObservableProperty] private bool _isRom;

    // ── Exe / ROM file pickers (populated when the settings panel opens) ─────
    /// <summary>Detected .exe/.bat files in the game folder — shown as a ComboBox dropdown.</summary>
    public ObservableCollection<string> AvailableExePaths { get; } = new();
    /// <summary>Known ROM files for this game (main path + additional paths) — shown as a ComboBox.</summary>
    public ObservableCollection<string> AvailableRomPaths { get; } = new();

    // ── Emulator picker (for ROM games with multiple emulators configured) ───
    /// <summary>Named emulators configured for this game's platform.</summary>
    public ObservableCollection<string> AvailableEmulators { get; } = new();
    /// <summary>True when more than one emulator is configured for this platform.</summary>
    [ObservableProperty] private bool _hasMultipleEmulators;
    /// <summary>Name of the emulator the user has selected for this game.</summary>
    [ObservableProperty] private string _selectedEmulatorName = "";

    /// <summary>Path typed by the user when adding a new pre-launch entry.</summary>
    [ObservableProperty] private string _newPreLaunchPath        = "";
    [ObservableProperty] private string _newPreLaunchArgs        = "";
    [ObservableProperty] private string _newPreLaunchLabel       = "";
    [ObservableProperty] private bool   _newPreLaunchWaitForReady = false;

    /// <summary>Path typed by the user when adding a new during-launch entry.</summary>
    [ObservableProperty] private string _newDuringLaunchPath  = "";
    [ObservableProperty] private string _newDuringLaunchArgs  = "";
    [ObservableProperty] private string _newDuringLaunchLabel = "";

    /// <summary>Path typed by the user when adding a new post-launch entry.</summary>
    [ObservableProperty] private string _newPostLaunchPath  = "";
    [ObservableProperty] private string _newPostLaunchArgs  = "";
    [ObservableProperty] private string _newPostLaunchLabel = "";

    /// <summary>Status message shown at the bottom of the settings panel.</summary>
    [ObservableProperty] private string _settingsStatus = "";

    public ObservableCollection<LaunchEntry> PreLaunchEntries    { get; } = new();
    public ObservableCollection<LaunchEntry> DuringLaunchEntries { get; } = new();
    public ObservableCollection<LaunchEntry> PostLaunchEntries   { get; } = new();

    /// <summary>Opens the settings panel and loads any saved settings for the current game.</summary>
    private void OpenSettings()
    {
        var saved = GameSettingsService.Load(Title);

        // Apply saved exe path (prefer saved > auto-detected)
        SettingsExePath = saved.ExePath ?? "";
        SettingsExeArgs = saved.ExeArgs ?? "";
        SettingsRomPath = saved.RomPath ?? "";

        // If no saved exe path but we have a detected one, pre-fill it
        if (string.IsNullOrEmpty(SettingsExePath) && _driveInstances.Count > 0)
        {
            int idx = System.Math.Clamp(SelectedDriveIndex, 0, _driveInstances.Count - 1);
            SettingsExePath = _driveInstances[idx].ExecutablePath ?? "";
        }

        // ── Populate exe dropdown with detected executables in the game folder ──
        AvailableExePaths.Clear();
        const int MaxExeFileSearchResults = 20;
        if (!string.IsNullOrEmpty(SettingsExePath))
            AvailableExePaths.Add(SettingsExePath);
        if (_driveInstances.Count > 0)
        {
            var folderPath = _driveInstances[0].FolderPath;
            if (!string.IsNullOrEmpty(folderPath) && System.IO.Directory.Exists(folderPath))
            {
                foreach (var exe in System.IO.Directory.EnumerateFiles(folderPath, "*.exe")
                             .Concat(System.IO.Directory.EnumerateFiles(folderPath, "*.bat"))
                             .Take(MaxExeFileSearchResults))
                {
                    if (!AvailableExePaths.Contains(exe))
                        AvailableExePaths.Add(exe);
                }
            }
        }

        // ── Populate ROM dropdown with known ROM paths ──────────────────────
        AvailableRomPaths.Clear();
        if (!string.IsNullOrEmpty(SettingsRomPath))
            AvailableRomPaths.Add(SettingsRomPath);
        if (_driveInstances.Count > 0 && !string.IsNullOrEmpty(_driveInstances[0].ExecutablePath))
        {
            string mainRomPath = _driveInstances[0].ExecutablePath;
            if (!AvailableRomPaths.Contains(mainRomPath))
                AvailableRomPaths.Add(mainRomPath);
        }
        // Add multi-disk / multi-region paths from the ROM scanner
        if (_currentLocalRom?.AdditionalPaths != null)
        {
            foreach (var p in _currentLocalRom.AdditionalPaths)
                if (!string.IsNullOrEmpty(p) && !AvailableRomPaths.Contains(p))
                    AvailableRomPaths.Add(p);
        }

        // ── Populate emulator dropdown ──────────────────────────────────────
        AvailableEmulators.Clear();
        if (IsRom)
        {
            var emulators = Services.EmulatorSettingsService.LoadAll(Platform);
            foreach (var e in emulators.Where(e => !string.IsNullOrEmpty(e.EmulatorPath)))
            {
                string label = string.IsNullOrWhiteSpace(e.EmulatorName)
                    ? System.IO.Path.GetFileNameWithoutExtension(e.EmulatorPath)
                    : e.EmulatorName;
                if (!AvailableEmulators.Contains(label))
                    AvailableEmulators.Add(label);
            }
            HasMultipleEmulators = AvailableEmulators.Count > 1;
            SelectedEmulatorName = saved.PreferredEmulatorName ?? (AvailableEmulators.Count > 0 ? AvailableEmulators[0] : "");
        }
        else
        {
            HasMultipleEmulators = false;
            SelectedEmulatorName = "";
        }

        PreLaunchEntries.Clear();
        foreach (var e in saved.PreLaunch)
            PreLaunchEntries.Add(e);

        DuringLaunchEntries.Clear();
        foreach (var e in saved.DuringLaunch)
            DuringLaunchEntries.Add(e);

        PostLaunchEntries.Clear();
        foreach (var e in saved.PostLaunch)
            PostLaunchEntries.Add(e);

        NewPreLaunchPath         = "";
        NewPreLaunchArgs         = "";
        NewPreLaunchLabel        = "";
        NewPreLaunchWaitForReady = false;
        NewDuringLaunchPath  = "";
        NewDuringLaunchArgs  = "";
        NewDuringLaunchLabel = "";
        NewPostLaunchPath   = "";
        NewPostLaunchArgs   = "";
        NewPostLaunchLabel  = "";
        SettingsStatus      = "";
        ShowSettings        = true;
    }

    [RelayCommand]
    private void SaveSettings()
    {
        var settings = new GameSettings
        {
            GameTitle              = Title,
            ExePath                = string.IsNullOrWhiteSpace(SettingsExePath) ? null : SettingsExePath.Trim(),
            ExeArgs                = string.IsNullOrWhiteSpace(SettingsExeArgs)  ? null : SettingsExeArgs.Trim(),
            RomPath                = string.IsNullOrWhiteSpace(SettingsRomPath)  ? null : SettingsRomPath.Trim(),
            PreferredEmulatorName  = string.IsNullOrWhiteSpace(SelectedEmulatorName) ? null : SelectedEmulatorName.Trim(),
            PreLaunch              = PreLaunchEntries.ToList(),
            DuringLaunch           = DuringLaunchEntries.ToList(),
            PostLaunch             = PostLaunchEntries.ToList(),
        };
        GameSettingsService.Save(settings);
        SettingsStatus = "✓  Settings saved.";
    }

    [RelayCommand]
    private void CloseSettings()
    {
        ShowSettings   = false;
        SettingsStatus = "";
    }

    [RelayCommand]
    private void AddPreLaunch()
    {
        if (string.IsNullOrWhiteSpace(NewPreLaunchPath)) return;
        PreLaunchEntries.Add(new LaunchEntry
        {
            Label        = string.IsNullOrWhiteSpace(NewPreLaunchLabel)
                               ? System.IO.Path.GetFileName(NewPreLaunchPath.Trim())
                               : NewPreLaunchLabel.Trim(),
            Path         = NewPreLaunchPath.Trim(),
            Arguments    = string.IsNullOrWhiteSpace(NewPreLaunchArgs) ? null : NewPreLaunchArgs.Trim(),
            WaitForReady = NewPreLaunchWaitForReady,
        });
        NewPreLaunchPath         = "";
        NewPreLaunchArgs         = "";
        NewPreLaunchLabel        = "";
        NewPreLaunchWaitForReady = false;
    }

    [RelayCommand]
    private void RemovePreLaunch(LaunchEntry? entry)
    {
        if (entry != null) PreLaunchEntries.Remove(entry);
    }

    [RelayCommand]
    private void AddDuringLaunch()
    {
        if (string.IsNullOrWhiteSpace(NewDuringLaunchPath)) return;
        DuringLaunchEntries.Add(new LaunchEntry
        {
            Label     = string.IsNullOrWhiteSpace(NewDuringLaunchLabel)
                            ? System.IO.Path.GetFileName(NewDuringLaunchPath.Trim())
                            : NewDuringLaunchLabel.Trim(),
            Path      = NewDuringLaunchPath.Trim(),
            Arguments = string.IsNullOrWhiteSpace(NewDuringLaunchArgs) ? null : NewDuringLaunchArgs.Trim(),
        });
        NewDuringLaunchPath  = "";
        NewDuringLaunchArgs  = "";
        NewDuringLaunchLabel = "";
    }

    [RelayCommand]
    private void RemoveDuringLaunch(LaunchEntry? entry)
    {
        if (entry != null) DuringLaunchEntries.Remove(entry);
    }

    [RelayCommand]
    private void AddPostLaunch()
    {
        if (string.IsNullOrWhiteSpace(NewPostLaunchPath)) return;
        PostLaunchEntries.Add(new LaunchEntry
        {
            Label     = string.IsNullOrWhiteSpace(NewPostLaunchLabel)
                            ? System.IO.Path.GetFileName(NewPostLaunchPath.Trim())
                            : NewPostLaunchLabel.Trim(),
            Path      = NewPostLaunchPath.Trim(),
            Arguments = string.IsNullOrWhiteSpace(NewPostLaunchArgs) ? null : NewPostLaunchArgs.Trim(),
        });
        NewPostLaunchPath  = "";
        NewPostLaunchArgs  = "";
        NewPostLaunchLabel = "";
    }

    [RelayCommand]
    private void RemovePostLaunch(LaunchEntry? entry)
    {
        if (entry != null) PostLaunchEntries.Remove(entry);
    }

    /// <summary>Opens the game folder in the system file manager.</summary>
    [RelayCommand]
    private void OpenGameFolder()
    {
        if (string.IsNullOrEmpty(ActiveDrivePath)) return;
        OpenWithSystem(ActiveDrivePath);
    }

    /// <summary>Deletes the game folder from disk after confirmation via SettingsStatus.</summary>
    [ObservableProperty] private bool _confirmDelete;

    [RelayCommand]
    private void RequestDeleteGame()
    {
        ConfirmDelete  = true;
        SettingsStatus = "⚠  Click 'Confirm Delete' to permanently remove the game folder.";
    }

    [RelayCommand]
    private void ConfirmDeleteGame()
    {
        if (string.IsNullOrEmpty(ActiveDrivePath)) return;

        // Safety guard: only allow deletion of directories whose name contains "Games"
        // or whose parent directory contains "Games" — prevents accidental deletion of
        // root drives, user home folders, or other system directories.
        var normalized = System.IO.Path.GetFullPath(ActiveDrivePath);
        bool looksLikeGameDir =
            normalized.Contains(System.IO.Path.DirectorySeparatorChar + "Games" + System.IO.Path.DirectorySeparatorChar,
                                 StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(System.IO.Path.DirectorySeparatorChar + "Roms" + System.IO.Path.DirectorySeparatorChar,
                                 StringComparison.OrdinalIgnoreCase);

        if (!looksLikeGameDir)
        {
            SettingsStatus = "⛔  Safety check failed: path does not appear to be inside a Games folder.";
            ConfirmDelete  = false;
            return;
        }

        try
        {
            if (Directory.Exists(normalized))
            {
                Directory.Delete(normalized, recursive: true);
                SettingsStatus  = "✓  Game folder deleted.";
                IsInstalled     = false;
                ActiveDrivePath = "";
            }
        }
        catch (Exception ex)
        {
            SettingsStatus = $"Delete failed: {ex.Message}";
        }
        finally
        {
            ConfirmDelete = false;
        }
    }

    [RelayCommand]
    private void CancelDelete()
    {
        ConfirmDelete  = false;
        SettingsStatus = "";
    }

    // ── Copy / Move ROM ───────────────────────────────────────────────────────
    /// <summary>True when the copy/move destination picker is visible.</summary>
    [ObservableProperty] private bool   _showCopyMovePicker;
    /// <summary>"Copy" or "Move" — set when picker is opened.</summary>
    [ObservableProperty] private string _copyMoveMode = "Copy";

    /// <summary>Available drives for ROM copy/move destination.</summary>
    public ObservableCollection<InstallDriveOption> CopyMoveDrives { get; } = new();

    [RelayCommand]
    private void CopyRom()
    {
        if (!IsRom) return;
        CopyMoveMode = "Copy";
        PopulateCopyMoveDrives();
        ShowCopyMovePicker = CopyMoveDrives.Count > 0;
        if (!ShowCopyMovePicker)
            SettingsStatus = "No drives with a Roms folder found.";
    }

    [RelayCommand]
    private void MoveRom()
    {
        if (!IsRom) return;
        CopyMoveMode = "Move";
        PopulateCopyMoveDrives();
        ShowCopyMovePicker = CopyMoveDrives.Count > 0;
        if (!ShowCopyMovePicker)
            SettingsStatus = "No drives with a Roms folder found.";
    }

    [RelayCommand]
    private void SelectCopyMoveDrive(InstallDriveOption? option)
    {
        if (option == null) return;
        ShowCopyMovePicker = false;

        if (_driveInstances.Count == 0) return;
        var entry0 = _driveInstances[0];
        string romSource = entry0.ExecutablePath ?? "";

        bool srcIsDir  = Directory.Exists(romSource);
        bool srcIsFile = System.IO.File.Exists(romSource);

        if (string.IsNullOrEmpty(romSource) || (!srcIsFile && !srcIsDir))
        {
            SettingsStatus = "ROM not found.";
            return;
        }

        if (srcIsDir)
        {
            // Folder-based ROM (e.g. PS3/PS4 TitleID directory): copy/move the whole folder.
            // Destination: Roms/{platformFolder}/Games/{FolderName}/
            string destFolder   = Services.RomPathHelper.ComputeFolderRomDestPath(romSource, option.DriveRoot, Platform);
            string destGamesDir = System.IO.Path.GetDirectoryName(destFolder) ?? System.IO.Path.Combine(option.DriveRoot, "Roms", Platform, "Games");
            try { Directory.CreateDirectory(destGamesDir); } catch { }
            _ = ExecuteCopyMoveFolderAsync(romSource, destFolder, CopyMoveMode == "Move");
        }
        else
        {
            // File-based ROM: preserve any subfolder between Games/ and the file so the
            // scanner can reconstruct the same title from the folder name.
            // e.g.  …/Roms/Sony - PlayStation 2/Games/Grand Theft Auto/gta_sa.iso
            //       → {dest}/Roms/Sony - PlayStation 2/Games/Grand Theft Auto/gta_sa.iso
            string destFile = Services.RomPathHelper.ComputeFileRomDestPath(
                romSource, entry0.FolderPath, option.DriveRoot, Platform);
            try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destFile) ?? ""); } catch { }
            _ = ExecuteCopyMoveAsync(romSource, destFile, CopyMoveMode == "Move");
        }
    }

    [RelayCommand]
    private void CancelCopyMove() => ShowCopyMovePicker = false;

    private async System.Threading.Tasks.Task ExecuteCopyMoveAsync(string source, string dest, bool move)
    {
        SettingsStatus = move ? "Moving ROM…" : "Copying ROM…";
        try
        {
            await System.Threading.Tasks.Task.Run(() =>
            {
                if (move)
                    System.IO.File.Move(source, dest, overwrite: true);
                else
                    System.IO.File.Copy(source, dest, overwrite: true);
            });

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                SettingsStatus = move ? $"✓  ROM moved to {dest}" : $"✓  ROM copied to {dest}";
                if (move)
                {
                    // Update the displayed path to the new location
                    ActiveDrivePath = System.IO.Path.GetDirectoryName(dest) ?? ActiveDrivePath;
                    if (_driveInstances.Count > 0)
                    {
                        var entry = _driveInstances[0];
                        _driveInstances[0] = new LocalGameDriveEntry
                        {
                            DriveRoot      = System.IO.Path.GetPathRoot(dest) ?? entry.DriveRoot,
                            FolderPath     = System.IO.Path.GetDirectoryName(dest) ?? entry.FolderPath,
                            ExecutablePath = dest,
                            ExecutableType = entry.ExecutableType,
                        };
                    }
                }
            });
        }
        catch (Exception ex)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                SettingsStatus = $"Failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Recursively copies or moves a ROM folder (e.g. a PS3/PS4 TitleID directory)
    /// to <paramref name="destFolder"/>, then updates the displayed drive path.
    /// </summary>
    private async System.Threading.Tasks.Task ExecuteCopyMoveFolderAsync(
        string sourceFolder, string destFolder, bool move)
    {
        SettingsStatus = move ? "Moving ROM…" : "Copying ROM…";
        try
        {
            await System.Threading.Tasks.Task.Run(() =>
            {
                if (move)
                {
                    // Directory.Move only works on the same volume; use copy+delete for cross-drive.
                    string? srcRoot  = System.IO.Path.GetPathRoot(sourceFolder);
                    string? destRoot = System.IO.Path.GetPathRoot(destFolder);
                    if (string.Equals(srcRoot, destRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        if (Directory.Exists(destFolder))
                            Directory.Delete(destFolder, recursive: true);
                        Directory.Move(sourceFolder, destFolder);
                    }
                    else
                    {
                        CopyDirectoryRecursive(sourceFolder, destFolder);
                        Directory.Delete(sourceFolder, recursive: true);
                    }
                }
                else
                {
                    CopyDirectoryRecursive(sourceFolder, destFolder);
                }
            });

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                SettingsStatus = move
                    ? $"✓  ROM moved to {destFolder}"
                    : $"✓  ROM copied to {destFolder}";
                if (move && _driveInstances.Count > 0)
                {
                    var entry = _driveInstances[0];
                    _driveInstances[0] = new LocalGameDriveEntry
                    {
                        DriveRoot      = System.IO.Path.GetPathRoot(destFolder) ?? entry.DriveRoot,
                        FolderPath     = System.IO.Path.GetDirectoryName(destFolder) ?? entry.FolderPath,
                        ExecutablePath = destFolder,
                        ExecutableType = entry.ExecutableType,
                    };
                    ActiveDrivePath = _driveInstances[0].FolderPath;
                }
            });
        }
        catch (Exception ex)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                SettingsStatus = $"Failed: {ex.Message}");
        }
    }

    private static void CopyDirectoryRecursive(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
            System.IO.File.Copy(file, System.IO.Path.Combine(dest, System.IO.Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectoryRecursive(dir, System.IO.Path.Combine(dest, System.IO.Path.GetFileName(dir)));
    }

    private void PopulateCopyMoveDrives()
    {
        CopyMoveDrives.Clear();
        try
        {
            // Skip the drive the ROM is currently on
            string? currentRoot = _driveInstances.Count > 0
                ? System.IO.Path.GetPathRoot(_driveInstances[0].ExecutablePath ?? "") : null;

            // Use the actual platform folder name from the source ROM path so the displayed
            // destination matches the layout the scanner expects (e.g. "Sony - PlayStation 2").
            string romPath        = _driveInstances.Count > 0 ? (_driveInstances[0].ExecutablePath ?? "") : "";
            string platformFolder = Services.RomPathHelper.GetRomPlatformFolderName(romPath, Platform);

            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
            {
                try
                {
                    string romsPath  = System.IO.Path.Combine(drive.RootDirectory.FullName, "Roms", platformFolder, "Games");
                    string gamesPath = romsPath; // reuse InstallDriveOption.GamesFolderPath for display
                    bool   exists    = Directory.Exists(romsPath);
                    long   free      = drive.AvailableFreeSpace;
                    string freeLabel = free >= 1_073_741_824
                        ? $"{free / 1_073_741_824.0:F1} GB free"
                        : $"{free / 1_048_576.0:F0} MB free";

                    CopyMoveDrives.Add(new InstallDriveOption
                    {
                        DriveRoot       = drive.RootDirectory.FullName,
                        GamesFolderPath = gamesPath,
                        FreeSpaceLabel  = freeLabel,
                        GamesExists     = exists,
                    });
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// Opens the trailer in-app (via the LibVLC VideoView overlay) for recognised
    /// YouTube URLs.  Falls back to the system browser for unrecognised URLs.
    /// </summary>
    [RelayCommand]
    private void OpenTrailer()
    {
        if (string.IsNullOrEmpty(TrailerUrl)) return;

        // If we can extract a YouTube video ID, open the in-app player
        if (!string.IsNullOrEmpty(YoutubeVideoId))
        {
            IsTrailerPlayerOpen = true;
            return;
        }

        // Fallback: unrecognised URL → open in system browser
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = TrailerUrl,
                UseShellExecute = true
            });
        }
        catch { /* best-effort */ }
    }

    /// <summary>Dismisses the in-app trailer player overlay.</summary>
    [RelayCommand]
    private void CloseTrailerPlayer() => IsTrailerPlayerOpen = false;

    /// <summary>Opens the Steam install page for this game via the Steam protocol URI.</summary>
    [RelayCommand]
    private void InstallViaSteam()
    {
        if (string.IsNullOrEmpty(SteamInstallUrl)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = SteamInstallUrl,
                UseShellExecute = true,
            });
        }
        catch { /* best-effort */ }
    }

    /// <summary>Opens the game's store page URL in the system's default browser.</summary>
    [RelayCommand]
    private void OpenStorePage()
    {
        if (string.IsNullOrEmpty(StorePageUrl)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = StorePageUrl,
                UseShellExecute = true
            });
        }
        catch { /* best-effort */ }
    }

    /// <summary>Launches the installed game executable.</summary>
    [RelayCommand]
    private void LaunchGame()
    {
        if (!IsInstalled) return;

        // If the game is already running (tracked), just bring its window to front
        if (IsGameRunning)
        {
            ResumeGameWindow();
            return;
        }

        // Load saved settings to get the preferred exe path / arguments
        var saved = GameSettingsService.Load(Title);

        // Run pre-launch entries first (fire-and-forget, best-effort)
        foreach (var pre in saved.PreLaunch)
            TryStartProcess(pre.Path, pre.Arguments, pre.WaitForReady);

        // ── ROM launch: use configured emulator if available ──────────────────
        if (IsRom)
        {
            // Determine the ROM file path: saved override → selected drive entry → auto-detected
            string romPath = "";
            if (!string.IsNullOrEmpty(saved.RomPath) && System.IO.File.Exists(saved.RomPath))
                romPath = saved.RomPath;
            else if (_driveInstances.Count > 0)
            {
                int driveIdx = System.Math.Clamp(SelectedDriveIndex, 0, _driveInstances.Count - 1);
                romPath = _driveInstances[driveIdx].ExecutablePath ?? "";
            }

            if (!string.IsNullOrEmpty(romPath))
            {
                // Use the game's preferred emulator when set; otherwise the first enabled one
                var emuSettings = string.IsNullOrWhiteSpace(saved.PreferredEmulatorName)
                    ? EmulatorSettingsService.Load(Platform)
                    : EmulatorSettingsService.LoadByName(Platform, saved.PreferredEmulatorName);

                if (!string.IsNullOrEmpty(emuSettings.EmulatorPath)
                    && System.IO.File.Exists(emuSettings.EmulatorPath)
                    && emuSettings.Enabled)
                {
                    // Replace {rom} placeholder with the ROM path, safely quoting any embedded quotes
                    string safeRomPath = romPath.Replace("\"", "\\\"");
                    string args = emuSettings.Arguments.Replace("{rom}", $"\"{safeRomPath}\"");
                    System.Diagnostics.Process? romProc = null;

                    // ── Switch log reader: clean up stale Ryujinx logs before launch ──
                    bool readSwitchLog = IsSwitch
                        && AppSettingsService.Load().ReadSwitchLog
                        && emuSettings.EmulatorPath.ToLowerInvariant().Contains("ryujinx");

                    if (readSwitchLog)
                    {
                        SwitchLogReaderService.DeleteOldLogs(emuSettings.EmulatorPath);
                        SwitchLogReaderService.AppendToLauncherLog(
                            $"Launching '{Title}' via Ryujinx — old logs cleared.");
                    }

                    try
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName         = emuSettings.EmulatorPath,
                            Arguments        = args,
                            UseShellExecute  = false,
                            WorkingDirectory = System.IO.Path.GetDirectoryName(emuSettings.EmulatorPath) ?? "",
                        };
                        romProc = System.Diagnostics.Process.Start(psi);

                        if (romProc != null)
                        {
                            _runningProcess    = romProc;
                            IsGameRunning      = true;
                            PlayButtonIsResume = false;
                            // Track playtime for ROM games through the emulator process
                            OnRequestPlaytimeTracking?.Invoke(romProc, Title, Platform);
                        }
                    }
                    catch { /* best-effort */ }

                    if (saved.PostLaunch.Count > 0)
                        _ = WatchAndRunPostLaunchAsync(romProc, saved.PostLaunch);

                    // ── Switch log reader: read Ryujinx log after session ends ──────
                    if (readSwitchLog)
                        _ = WatchAndReadSwitchLogAsync(romProc, emuSettings.EmulatorPath, Title);

                    // ── Xenia log reader: read achievement unlocks after session ends ──
                    bool readXeniaLog = string.Equals(Platform, "Xbox 360", StringComparison.OrdinalIgnoreCase)
                        && emuSettings.EmulatorPath.Contains("xenia", StringComparison.OrdinalIgnoreCase);
                    if (readXeniaLog)
                    {
                        XeniaLogReaderService.DeleteOldLogs(emuSettings.EmulatorPath);
                        _ = WatchAndReadXeniaLogAsync(romProc, emuSettings.EmulatorPath, Title);
                    }

                    return;
                }

                // No emulator configured — open ROM file with system handler as fallback
                OpenWithSystem(romPath);
            }
            return;
        }

        // ── Regular (PC) game launch ──────────────────────────────────────────
        // Determine the executable to launch:
        // Priority: saved settings ExePath → steam://launch (for Steam games) → detected drive entry → open folder
        string? exePath = null;
        string? exeArgs = string.IsNullOrWhiteSpace(saved.ExeArgs) ? null : saved.ExeArgs;

        if (!string.IsNullOrEmpty(saved.ExePath) && System.IO.File.Exists(saved.ExePath))
        {
            exePath = saved.ExePath;
        }
        else if (_steamAppId > 0)
        {
            // Launch through the Steam client so overlays and cloud saves work correctly.
            // steam:// is handled by the OS shell; we cannot track the resulting game process,
            // so process-based playtime tracking and post-launch watchers are not applicable.
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = $"steam://launch/{_steamAppId}",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                DevLogService.Log($"[LaunchGame] Steam launch failed for AppId {_steamAppId}: {ex.Message}");
            }
            return;
        }
        else if (_driveInstances.Count > 0)
        {
            int idx   = System.Math.Clamp(SelectedDriveIndex, 0, _driveInstances.Count - 1);
            var entry = _driveInstances[idx];
            if (!string.IsNullOrEmpty(entry.ExecutablePath))
                exePath = entry.ExecutablePath;
        }

        if (!string.IsNullOrEmpty(exePath))
        {
            // Save the resolved exe path so "Continue Playing" reuses it next session
            if (string.IsNullOrEmpty(saved.ExePath) || saved.ExePath != exePath)
            {
                saved.ExePath    = exePath;
                saved.GameTitle  = Title;
                GameSettingsService.Save(saved);
            }

            System.Diagnostics.Process? gameProc = null;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName         = exePath,
                    UseShellExecute  = true,
                    WorkingDirectory = System.IO.Path.GetDirectoryName(exePath) ?? "",
                };
                if (!string.IsNullOrEmpty(exeArgs))
                    psi.Arguments = exeArgs;
                gameProc = System.Diagnostics.Process.Start(psi);

                if (gameProc != null)
                {
                    _runningProcess    = gameProc;
                    IsGameRunning      = true;
                    PlayButtonIsResume = false;
                    // Track playtime for PC games
                    OnRequestPlaytimeTracking?.Invoke(gameProc, Title, Platform);
                }
            }
            catch { /* best-effort */ }

            // Register post-launch watcher (fire-and-forget)
            if (saved.PostLaunch.Count > 0)
                _ = WatchAndRunPostLaunchAsync(gameProc, saved.PostLaunch);
        }
        else if (!string.IsNullOrEmpty(ActiveDrivePath))
        {
            // Fallback: open the game folder
            OpenWithSystem(ActiveDrivePath);
        }
    }

    /// <summary>
    /// Callback wired by MainViewModel so the detail view-model can request
    /// playtime tracking for a launched game process without directly referencing
    /// the service (keeps the VM testable).
    /// </summary>
    public Action<System.Diagnostics.Process, string, string>? OnRequestPlaytimeTracking { get; set; }

    /// <summary>
    /// Brings the currently-running game window to the foreground.
    /// Tries the main window handle first; falls back to opening the game folder.
    /// This is a best-effort operation — it may not work on all platforms.
    /// </summary>
    private void ResumeGameWindow()
    {
        var proc = _runningProcess;
        if (proc == null) return;

        try
        {
            if (proc.HasExited)
            {
                IsGameRunning      = false;
                PlayButtonIsResume = false;
                _runningProcess    = null;
                return;
            }

            if (OperatingSystem.IsWindows())
            {
                var hwnd = proc.MainWindowHandle;
                if (hwnd != nint.Zero)
                {
                    Services.NativeMethods.ShowWindow(hwnd, Services.NativeMethods.SW_RESTORE);
                    Services.NativeMethods.SetForegroundWindow(hwnd);
                    return;
                }
            }

            // Non-Windows or no main window handle: open the game folder as fallback
            if (!string.IsNullOrEmpty(ActiveDrivePath))
                OpenWithSystem(ActiveDrivePath);
        }
        catch { /* best-effort */ }
    }

    /// <summary>Raised when the user clicks Browse… next to the Pre-Launch path input.</summary>
    public System.Action<System.Action<string>>? BrowseLaunchPathRequested { get; set; }

    [RelayCommand]
    private void BrowsePreLaunch()
        => BrowseLaunchPathRequested?.Invoke(path => NewPreLaunchPath = path);

    [RelayCommand]
    private void BrowseDuringLaunch()
        => BrowseLaunchPathRequested?.Invoke(path => NewDuringLaunchPath = path);

    [RelayCommand]
    private void BrowsePostLaunch()
        => BrowseLaunchPathRequested?.Invoke(path => NewPostLaunchPath = path);

    /// <summary>
    /// The per-system metadata cache, wired by MainViewModel at startup.
    /// Used by <see cref="FetchAndDisplayAchievementsAsync"/> to load achievements.json
    /// from disk instead of re-downloading from GitHub every time a game detail is opened.
    /// </summary>
    public Services.GameMetadataCacheService? CacheService { get; set; }

    private static void TryStartProcess(string path, string? args, bool waitForReady = false)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (waitForReady)
            {
                // WaitForInputIdle waits until the process finishes starting and its message
                // loop is idle — meaning the main window is ready for input.
                // NOTE: WaitForInputIdle only works for GUI applications with a Win32 message
                // loop (e.g. Steam, Epic Games Launcher). It has no effect on console-only
                // apps, services, or non-Windows platforms. For those, the game launches
                // immediately after the process starts.
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName         = path,
                    UseShellExecute  = false,
                    WorkingDirectory = System.IO.Path.GetDirectoryName(path) ?? "",
                };
                if (!string.IsNullOrEmpty(args))
                    psi.Arguments = args;
                var proc = System.Diagnostics.Process.Start(psi);
                if (proc != null)
                {
                    try { proc.WaitForInputIdle(30_000); } catch { /* best-effort */ }
                }
            }
            else
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName         = path,
                    UseShellExecute  = true,
                    WorkingDirectory = System.IO.Path.GetDirectoryName(path) ?? "",
                };
                if (!string.IsNullOrEmpty(args))
                    psi.Arguments = args;
                System.Diagnostics.Process.Start(psi);
            }
        }
        catch { /* best-effort */ }
    }

    /// <summary>Maximum time to wait for a game/emulator process to exit.</summary>
    private const int MaxEmulatorWaitHours = 24;
    /// <summary>Milliseconds to wait after the emulator exits for its log file to be fully flushed.</summary>
    private const int LogFlushDelayMs = 500;

    private static async System.Threading.Tasks.Task WatchAndRunPostLaunchAsync(
        System.Diagnostics.Process? gameProc, List<LaunchEntry> postEntries)
    {
        if (gameProc != null)
        {
            try
            {
                // Wait up to MaxEmulatorWaitHours for the game process to exit
                using var cts = new System.Threading.CancellationTokenSource(
                    System.TimeSpan.FromHours(MaxEmulatorWaitHours));
                await gameProc.WaitForExitAsync(cts.Token);
            }
            catch { /* process may have already exited or be inaccessible */ }
            finally
            {
                gameProc.Dispose();
            }
        }

        foreach (var post in postEntries)
            TryStartProcess(post.Path, post.Arguments);
    }

    /// <summary>
    /// Waits for <paramref name="gameProc"/> to exit then reads the latest Ryujinx
    /// log file (all lines, IPs redacted) and writes a summary to
    /// <c>Switch log Reader.log</c> next to the Game.OS launcher executable.
    /// Before writing, checks whether a log snippet for this TitleID has already
    /// been recorded; if yes, the session is skipped to avoid duplicates.
    /// </summary>
    private async System.Threading.Tasks.Task WatchAndReadSwitchLogAsync(
        System.Diagnostics.Process? gameProc, string ryujinxExePath, string gameTitle)
    {
        if (gameProc != null)
        {
            try
            {
                // Wait up to MaxEmulatorWaitHours for the emulator process to exit
                using var cts = new System.Threading.CancellationTokenSource(
                    System.TimeSpan.FromHours(MaxEmulatorWaitHours));
                await gameProc.WaitForExitAsync(cts.Token);
            }
            catch { /* process may have already exited or be inaccessible */ }
            finally
            {
                gameProc.Dispose();
            }
        }

        // Give Ryujinx a moment to finish flushing its log to disk
        await System.Threading.Tasks.Task.Delay(LogFlushDelayMs);

        string? latestLog = SwitchLogReaderService.FindLatestLog(ryujinxExePath);
        if (string.IsNullOrEmpty(latestLog))
        {
            SwitchLogReaderService.AppendToLauncherLog(
                $"'{gameTitle}' session ended — no Ryujinx log file found.");
            return;
        }

        // Resolve the best TitleID for deduplication: ROM entry → database match → null
        string? titleId = _currentLocalRom?.TitleId ?? _databaseTitleId;
        if (string.IsNullOrEmpty(titleId) && !string.IsNullOrEmpty(gameTitle))
            titleId = Services.GitHubDataService.TryGetTitleIdFromLocalCache("Switch", gameTitle);

        // Deduplicate: if a log snippet for this TitleID has already been recorded,
        // skip writing this session to avoid flooding the log with duplicate data.
        if (!string.IsNullOrEmpty(titleId) && SwitchLogReaderService.HasLogSnippet(titleId))
        {
            SwitchLogReaderService.AppendToLauncherLog(
                $"'{gameTitle}' ({titleId}): log snippet already recorded — skipping.");
            return;
        }

        // Read the full log with IP addresses redacted
        var fullLines = SwitchLogReaderService.ReadFullLog(latestLog);
        SwitchLogReaderService.WriteSessionToLauncherLogFull(gameTitle, latestLog, fullLines);

        // Mark this TitleID as "snippet recorded" so future sessions are skipped
        if (!string.IsNullOrEmpty(titleId))
            SwitchLogReaderService.MarkLogSnippetRecorded(titleId);

        // ── Switch log → Games.Database upload ────────────────────────────────
        // If we have a valid TitleID that is NOT yet in the Games.Database, add it.
        if (!string.IsNullOrEmpty(titleId) && !string.IsNullOrEmpty(gameTitle))
        {
            bool alreadyInDb = Services.GitHubDataService.IsTitleIdInLocalCache("Switch", titleId);
            if (!alreadyInDb)
            {
                DevLogService.Log(
                    $"[SwitchLog] '{gameTitle}' ({titleId}) not in DB — queuing submission.");
                _ = System.Threading.Tasks.Task.Run(() => SubmitSwitchGameToDatabaseAsync(titleId, gameTitle));
            }
        }
    }

    /// <summary>
    /// Submits a minimal Switch game entry (TitleId, Title, platform=Switch) to the
    /// Games.Database via the GitHub API, then invalidates the local disk cache so the
    /// next store load picks up the new entry.
    /// </summary>
    private static async System.Threading.Tasks.Task SubmitSwitchGameToDatabaseAsync(
        string titleId, string title)
    {
        try
        {
            DevLogService.Log($"[SwitchDb] Submitting '{title}' ({titleId}) to Games.Database…");
            using var svc = new Services.GitHubDataService();
            // Read the current Switch database file
            const string path = "Switch.Games.json";
            List<Models.DatabaseGame>? games;
            string? sha;
            try
            {
                (games, sha) = await svc.ReadGamesDatabaseFileAsync<List<Models.DatabaseGame>>(path);
            }
            catch
            {
                games = null;
                sha   = null;
            }

            games ??= new List<Models.DatabaseGame>();

            // Double-check the entry is still not there (another client may have uploaded)
            if (games.Any(g =>
                string.Equals(g.TitleId, titleId, StringComparison.OrdinalIgnoreCase)))
            {
                DevLogService.Log($"[SwitchDb] '{title}' ({titleId}) already exists — skipping.");
                return;
            }

            games.Add(new Models.DatabaseGame
            {
                TitleId = titleId,
                Title   = title,
            });

            await svc.WriteGamesDatabaseFileAsync(path, games,
                $"Add Switch game: {title} ({titleId})", sha);

            // Invalidate the local cache so the next fetch picks up the new entry
            Services.GitHubDataService.InvalidatePlatformCache("Switch");
            DevLogService.Log($"[SwitchDb] Successfully submitted '{title}' ({titleId}).");
        }
        catch (Exception ex)
        {
            DevLogService.Log($"[SwitchDb] Failed to submit '{title}': {ex.Message}");
        }
    }

    /// <summary>
    /// Waits for the Xenia emulator process to exit, then reads its log file for
    /// achievement-unlock events.  New unlocks (not already in the local cache) are
    /// toasted to the user and synced to the achievements cache.
    /// </summary>
    private async System.Threading.Tasks.Task WatchAndReadXeniaLogAsync(
        System.Diagnostics.Process? gameProc, string xeniaExePath, string gameTitle)
    {
        if (gameProc != null)
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(
                    System.TimeSpan.FromHours(MaxEmulatorWaitHours));
                await gameProc.WaitForExitAsync(cts.Token);
            }
            catch { /* process may have already exited or be inaccessible */ }
            finally
            {
                gameProc.Dispose();
            }
        }

        // Give Xenia a moment to flush its log to disk
        await System.Threading.Tasks.Task.Delay(LogFlushDelayMs);

        // Load already-unlocked IDs from the achievements cache to avoid duplicates
        // Use the local achievements.json file path from the cache service
        string? cachePath = CacheService?.GetCachedAchievementsPath("Xbox 360", null, gameTitle);
        var alreadyUnlocked = new System.Collections.Generic.HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(cachePath) && System.IO.File.Exists(cachePath))
        {
            try
            {
                var cachedJson = System.IO.File.ReadAllText(cachePath);
                var cached = System.Text.Json.JsonSerializer.Deserialize<List<Achievement>>(cachedJson);
                if (cached != null)
                {
                    foreach (var a in cached.Where(a => a.IsUnlocked && !string.IsNullOrEmpty(a.AchievementId)))
                        alreadyUnlocked.Add(a.AchievementId);
                }
            }
            catch { /* best-effort */ }
        }

        var newUnlocks = XeniaLogReaderService.GetNewUnlocks(xeniaExePath, alreadyUnlocked);
        if (newUnlocks.Count == 0) return;

        // Fire toast for each newly unlocked achievement and mark it in the cache
        foreach (var (id, name) in newUnlocks)
        {
            Services.NotificationService.ShowAchievementUnlockedNotification(name, gameTitle);

            // Update the in-memory achievement list so the detail view reflects the unlock
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var existing = Achievements.FirstOrDefault(a =>
                    string.Equals(a.AchievementId, id, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.UnlockedAt = DateTime.UtcNow.ToString("o");
                    RefreshVisibleAchievements();
                }
            });
        }
    }


    /// <summary>
    /// Installs the repack.
    /// - If the repack is a folder containing Setup.exe: runs Setup.exe directly.
    /// - If the repack is an archive (.zip/.rar/.7z): populates the drive-selection
    ///   picker so the user can choose where to extract the game.
    /// - Otherwise: opens the archive with the system extractor.
    /// </summary>
    [RelayCommand]
    private void InstallRepack()
    {
        if (!IsRepack || string.IsNullOrEmpty(RepackPath)) return;

        // Folder repack with Setup.exe — run installer directly
        if (IsSetupRepack)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = RepackPath, // already the Setup.exe path
                    UseShellExecute = true
                });
            }
            catch { /* best-effort */ }
            return;
        }

        // Archive repack — show drive picker so user can choose install location
        string ext = Path.GetExtension(RepackPath).ToLowerInvariant();
        bool isArchive = ext is ".zip" or ".rar" or ".7z";
        if (isArchive)
        {
            PopulateInstallDrives();
            ShowDrivePicker = InstallDrives.Count > 0;
            if (!ShowDrivePicker)
            {
                // No Games folder found on any drive — fall back to opening the archive
                OpenWithSystem(RepackPath);
            }
            return;
        }

        // Fallback: open with system handler
        OpenWithSystem(RepackPath);
    }

    /// <summary>
    /// Called when the user selects a drive from the install-drive picker.
    /// Extracts .zip archives automatically; for .rar and .7z attempts 7-Zip CLI;
    /// falls back to opening with the system extractor if 7-Zip is not found.
    /// </summary>
    [RelayCommand]
    private void SelectInstallDrive(InstallDriveOption? option)
    {
        if (option == null) return;
        ShowDrivePicker = false;

        // Ensure the Games folder exists on the target drive
        string destFolder = Path.Combine(option.GamesFolderPath, Title);
        try { Directory.CreateDirectory(destFolder); } catch { }

        // Write a metadata file with the real display title so the scanner can show
        // the correct game name even when the folder name is an abbreviation (e.g.
        // "LHPCR" installed as "LEGO® Harry Potter™ Collection").
        WriteGameOsTitle(destFolder, Title);

        string ext = Path.GetExtension(RepackPath).ToLowerInvariant();
        if (ext == ".zip")
        {
            // Use built-in .NET extraction for ZIP archives
            _ = ExtractZipAsync(RepackPath, destFolder);
        }
        else if (ext is ".rar" or ".7z")
        {
            // Try 7-Zip CLI; fall back to opening with the system handler
            if (!TryExtractWith7Zip(RepackPath, destFolder))
                OpenWithSystem(RepackPath);
        }
        else
        {
            OpenWithSystem(RepackPath);
        }
    }

    /// <summary>
    /// Writes a <c>.gameos-title</c> file inside <paramref name="gameFolder"/> containing
    /// <paramref name="title"/>, so that the scanner can resolve the real display name
    /// even when the folder name is an opaque abbreviation (e.g. "LHPCR").
    /// </summary>
    private static void WriteGameOsTitle(string gameFolder, string title)
    {
        try
        {
            string path = Path.Combine(gameFolder, ".gameos-title");
            File.WriteAllText(path, title.Trim(), System.Text.Encoding.UTF8);
        }
        catch { /* best-effort */ }
    }

    /// <summary>Dismisses the drive-picker without installing.</summary>
    [RelayCommand]
    private void CancelInstall() => ShowDrivePicker = false;

    /// <summary>
    /// Extracts a ZIP archive to <paramref name="destFolder"/> in a background thread,
    /// reporting per-entry progress via <see cref="ExtractionProgress"/>.
    /// </summary>
    private async System.Threading.Tasks.Task ExtractZipAsync(string archivePath, string destFolder)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            SettingsStatus          = "Extracting ZIP…";
            InstallStatusMessage    = "⏳  Extracting archive…";
            InstallStatusIsError    = false;
            ExtractionProgress      = 0;
            IsExtracting            = true;
        });
        try
        {
            await System.Threading.Tasks.Task.Run(() =>
            {
                using var archive = System.IO.Compression.ZipFile.OpenRead(archivePath);
                int total = archive.Entries.Count;
                int done  = 0;
                foreach (var entry in archive.Entries)
                {
                    string destPath = System.IO.Path.Combine(destFolder, entry.FullName);
                    // Directory entries end with a separator
                    if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                    {
                        System.IO.Directory.CreateDirectory(destPath);
                    }
                    else
                    {
                        string? dir = System.IO.Path.GetDirectoryName(destPath);
                        if (!string.IsNullOrEmpty(dir))
                            System.IO.Directory.CreateDirectory(dir);
                        entry.ExtractToFile(destPath, overwrite: true);
                    }
                    done++;
                    double pct = total > 0 ? (double)done / total * 100.0 : 0;
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => ExtractionProgress = pct);
                }
            });

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                SettingsStatus          = $"✓  Extracted to {destFolder}";
                InstallStatusMessage    = $"✓  Installed to {destFolder}";
                InstallStatusIsError    = false;
                ExtractionProgress      = 100;
                IsExtracting            = false;
            });
        }
        catch (Exception ex)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                SettingsStatus          = $"Extraction failed: {ex.Message}";
                InstallStatusMessage    = $"⛔  Extraction failed: {ex.Message}";
                InstallStatusIsError    = true;
                IsExtracting            = false;
            });
        }
    }

    /// <summary>
    /// Tries to extract <paramref name="archivePath"/> to <paramref name="destFolder"/>
    /// using the 7-Zip command-line tool.  Reports progress via <see cref="ExtractionProgress"/>.
    /// Returns false if 7-Zip is not found so the caller can fall back to the system handler.
    /// </summary>
    private bool TryExtractWith7Zip(string archivePath, string destFolder)
    {
        // Locate 7-Zip: common Windows install paths, then fall back to system PATH
        string? sevenZip = null;

        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows))
        {
            string[] windowsCandidates =
            [
                @"C:\Program Files\7-Zip\7z.exe",
                @"C:\Program Files (x86)\7-Zip\7z.exe",
            ];
            sevenZip = windowsCandidates.FirstOrDefault(System.IO.File.Exists);
        }

        if (sevenZip == null)
        {
            // Verify "7z" actually exists on PATH before using it as a fallback,
            // so we can return false (and let the caller open with the system handler)
            // instead of showing a confusing "system cannot find the file" error.
            bool isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows);
            string exeName = isWindows ? "7z.exe" : "7z";
            bool foundOnPath = (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(System.IO.Path.PathSeparator)
                .Where(dir => !string.IsNullOrWhiteSpace(dir))
                .Any(dir => System.IO.File.Exists(System.IO.Path.Combine(dir.Trim(), exeName)));

            if (!foundOnPath)
                return false;

            sevenZip = "7z";
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            SettingsStatus          = "Extracting archive with 7-Zip…";
            InstallStatusMessage    = "⏳  Extracting archive with 7-Zip…";
            InstallStatusIsError    = false;
            ExtractionProgress      = 0;
            IsExtracting            = true;
        });

        // Safely escape paths to avoid issues with special characters
        string safeArchive = archivePath.Replace("\"", "\\\"");
        string safeDest    = destFolder.Replace("\"", "\\\"");

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName               = sevenZip,
                    // -bsp1 makes 7z write progress percentages to stdout
                    Arguments              = $"x \"{safeArchive}\" -o\"{safeDest}\" -y -bsp1",
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc != null)
                {
                    // Parse 7z progress output asynchronously
                    var outputTask = System.Threading.Tasks.Task.Run(async () =>
                    {
                        while (!proc.StandardOutput.EndOfStream)
                        {
                            var line = await proc.StandardOutput.ReadLineAsync();
                            if (line == null) break;
                            // 7z progress format: "  XX% - filename"
                            var match = _sevenZipProgressRegex.Match(line);
                            if (match.Success && int.TryParse(match.Groups[1].Value, out int pct))
                                Avalonia.Threading.Dispatcher.UIThread.Post(() => ExtractionProgress = pct);
                        }
                    });
                    await proc.WaitForExitAsync();
                    await outputTask;
                }

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    SettingsStatus          = $"✓  Extracted to {destFolder}";
                    InstallStatusMessage    = $"✓  Installed to {destFolder}";
                    InstallStatusIsError    = false;
                    ExtractionProgress      = 100;
                    IsExtracting            = false;
                });
            }
            catch (Exception ex)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    SettingsStatus          = $"Extraction failed: {ex.Message}";
                    InstallStatusMessage    = $"⛔  Extraction failed: {ex.Message}";
                    InstallStatusIsError    = true;
                    IsExtracting            = false;
                });
            }
        });

        return true;
    }

    private static void OpenWithSystem(string path)
    {
        try
        {
            System.Diagnostics.ProcessStartInfo psi;

            // On Windows, passing a directory path directly as FileName can silently
            // fail.  Invoke explorer.exe explicitly so folders always open correctly.
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                    System.Runtime.InteropServices.OSPlatform.Windows)
                && Directory.Exists(path))
            {
                psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = "explorer.exe",
                    Arguments       = $"\"{path}\"",
                    UseShellExecute = true,
                };
            }
            else
            {
                psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = path,
                    UseShellExecute = true,
                };
            }

            System.Diagnostics.Process.Start(psi);
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Discovers all drives that already have a Games folder, plus drives that
    /// are ready (have free space) for one to be created, for the drive picker.
    /// </summary>
    private void PopulateInstallDrives()
    {
        InstallDrives.Clear();
        try
        {
            var drives = DriveInfo.GetDrives().Where(d => d.IsReady);

            foreach (var drive in drives)
            {
                try
                {
                    string gamesPath = Path.Combine(drive.RootDirectory.FullName, "Games");
                    bool   exists    = Directory.Exists(gamesPath);
                    long   free      = drive.AvailableFreeSpace;
                    string freeLabel = free >= 1_073_741_824
                        ? $"{free / 1_073_741_824.0:F1} GB free"
                        : $"{free / 1_048_576.0:F0} MB free";

                    InstallDrives.Add(new InstallDriveOption
                    {
                        DriveRoot      = drive.RootDirectory.FullName,
                        GamesFolderPath= gamesPath,
                        FreeSpaceLabel = freeLabel,
                        GamesExists    = exists,
                    });
                }
                catch { /* skip inaccessible drive */ }
            }
        }
        catch { }
    }

    /// <summary>Opens the game settings panel (exe select, arguments, pre/post launch, folder ops).</summary>
    [RelayCommand]
    private void ShowMoreOptions()
    {
        OpenSettings();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Populate from a cloud library Game
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Load a cloud library game into the detail view.
    /// </summary>
    /// <param name="game">The cloud library entry.</param>
    /// <param name="localGame">If not null, the game is installed on this drive — shows Play + ··· buttons.</param>
    /// <param name="repack">If not null (and localGame is null), a repack is available — shows Install button.</param>
    public void LoadFromGame(Game game, LocalGame? localGame = null, LocalRepack? repack = null,
                             LocalRom? localRom = null)
    {
        ShowSettings    = false;
        ShowModsPanel   = false;
        ShowDrivePicker = false;
        _currentLocalRom = localRom;
        Title         = game.Title;
        Platform      = game.Platform;
        Genre         = game.Genre    ?? "";
        Description   = game.Description ?? "";
        RatingStars   = game.RatingStars;
        Price         = game.Price;
        CoverUrl      = game.CoverUrl;
        CoverGradient = game.CoverGradient;
        IsRom         = localRom != null;
        PopulateRegions(localRom?.Regions.Count > 0 ? localRom.Regions : null);
        PopulateStoreUrl(null, game.Platform, null);

        PopulateTrailer(game.TrailerUrl);
        PopulateScreenshots(game.Screenshots);
        PopulateAchievements(game.GameAchievements);
        IsLocalGame = false;
        HasMultipleDrives = false;
        DriveLabels.Clear();

        // Load achievements from the database URL when not already populated
        if (!HasAchievements && !string.IsNullOrEmpty(game.AchievementsUrl))
            _ = FetchAndDisplayAchievementsAsync(game.AchievementsUrl);

        PopulatePlaytime(game.Platform, game.Title);
        ApplyInstallState(localGame, repack, localRom);
        // LoadSwitchMods is intentionally called for all platforms: it sets IsSwitch = false
        // for non-Switch games (clearing stale state from a previously viewed Switch game)
        // and returns immediately without file system operations.
        LoadSwitchMods();
        _steamAppId = 0;

        // Extract Steam AppId from the TitleId (format: "steam:{AppId}")
        if (game.TitleId?.StartsWith("steam:", StringComparison.OrdinalIgnoreCase) == true &&
            int.TryParse(game.TitleId.AsSpan(6), out int steamId) && steamId > 0)
        {
            _steamAppId = steamId;
        }

        // "Install via Steam" shown for Steam-API games not yet installed locally
        IsSteamInstallable   = _steamAppId > 0 && !IsInstalled && !IsRepack;
        SteamInstallUrl      = _steamAppId > 0 ? $"steam://install/{_steamAppId}" : "";
        HasSteamLaunchOption = _steamAppId > 0;
        LoadReviews();
    }
    /// <param name="localGame">If not null, the game is installed — shows Play + ··· buttons.</param>
    /// <param name="repack">If not null (and localGame is null), a repack is available — shows Install button.</param>
    public void LoadFromStoreGame(StoreGame game, LocalGame? localGame = null, LocalRepack? repack = null,
                                  LocalRom? localRom = null)
    {
        ShowSettings    = false;
        ShowModsPanel   = false;
        ShowDrivePicker = false;
        _currentLocalRom = localRom;
        Title         = game.Title;
        Platform      = game.Platform;
        Genre         = game.Genre;
        Description   = game.Description;
        RatingStars   = game.RatingStars;
        Price         = game.Price;
        ReleaseYear   = game.ReleaseYear;
        CoverUrl      = game.CoverUrl;
        CoverGradient = game.CoverGradient;
        IsRom         = localRom != null;
        PopulateRegions(localRom?.Regions.Count > 0 ? localRom.Regions : null);
        PopulateStoreUrl(game.StorePageUrl, game.Platform, null);

        PopulateTrailer(game.TrailerUrl);
        PopulateScreenshots(game.Screenshots);
        PopulateAchievements(null);
        IsLocalGame       = false;
        HasMultipleDrives = false;
        DriveLabels.Clear();

        // Load achievements from the database URL when available
        if (!string.IsNullOrEmpty(game.AchievementsUrl))
            _ = FetchAndDisplayAchievementsAsync(game.AchievementsUrl);

        PopulatePlaytime(game.Platform, game.Title);
        ApplyInstallState(localGame, repack, localRom);
        // Intentionally unconditional — resets IsSwitch=false for non-Switch store games
        // so stale Switch state from a previously viewed game is always cleared.
        LoadSwitchMods();
        _steamAppId          = 0;
        IsSteamInstallable   = false;
        SteamInstallUrl      = "";
        HasSteamLaunchOption = false;
        LoadReviews();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Populate from a locally detected LocalGame
    // ─────────────────────────────────────────────────────────────────────────

    // ─────────────────────────────────────────────────────────────────────────
    // Populate from a locally detected LocalGame
    // ─────────────────────────────────────────────────────────────────────────

    public void LoadFromLocalGame(LocalGame game)
    {
        ShowSettings    = false;
        ShowModsPanel   = false;
        ShowDrivePicker = false;
        // Reset Switch/Ryujinx state so the Mods button is not incorrectly visible
        // when navigating from a Switch game to a PC game.
        IsSwitch             = false;
        SwitchMods.Clear();
        HasSwitchMods        = false;
        ModsJsonExistsButEmpty = false;
        SwitchModsStatus     = "";
        SwitchModsJsonPath   = "";
        _ryujinxModsJsonPath = null;
        _currentLocalRom = null;
        Title             = game.Title;
        Platform          = "PC";
        Genre             = "";
        CoverGradient     = "#0d2137,#163d5e";
        RatingStars       = "—";
        Price             = null;
        CoverUrl          = null;
        IsRom             = false;
        _databaseDescription = null;
        _databaseTitleId     = null;
        PopulateRegions(null);
        PopulateStoreUrl(null, "PC", null);

        PopulateTrailer(null);
        Screenshots.Clear();
        HasScreenshots = false;
        PopulateAchievements(null);
        IsLocalGame     = true;
        IsInstalled     = true;
        IsRepack        = false;
        IsSetupRepack   = false;
        ShowDrivePicker = false;
        RepackPath     = "";
        RepackSizeLabel = "";

        _driveInstances = game.DriveInstances.Count > 0
            ? game.DriveInstances
            : new List<LocalGameDriveEntry>
            {
                new LocalGameDriveEntry
                {
                    DriveRoot      = game.DriveRoot,
                    FolderPath     = game.FolderPath,
                    ExecutablePath = game.ExecutablePath,
                    ExecutableType = game.ExecutableType,
                }
            };

        DriveLabels.Clear();
        foreach (var d in _driveInstances)
            DriveLabels.Add(d.DriveRoot);

        HasMultipleDrives  = _driveInstances.Count > 1;
        SelectedDriveIndex = 0;
        RefreshActiveDrive();
        PopulatePlaytime("PC", game.Title);
        _steamAppId = game.SteamAppId;

        // For locally-installed Steam games, show the Steam store page link
        if (game.SteamAppId > 0)
            PopulateStoreUrl($"https://store.steampowered.com/app/{game.SteamAppId}", "PC", null);

        // A locally-installed Steam game can also be launched via Steam (overlay, cloud saves)
        IsSteamInstallable   = false; // already installed
        SteamInstallUrl      = game.SteamAppId > 0 ? $"steam://install/{game.SteamAppId}" : "";
        HasSteamLaunchOption = game.SteamAppId > 0;
        LoadReviews();
    }

    /// <summary>
    /// Sets up the detail overlay for a repack archive found on disk.
    /// Shows basic title/size info immediately; the caller should follow up with
    /// <see cref="MainViewModel.EnrichLocalGameDetailAsync"/> to pull real cover
    /// art, description, screenshots and achievements from the Games.Database.
    /// </summary>
    public void LoadFromLocalRepack(LocalRepack repack)
    {
        ShowSettings    = false;
        ShowDrivePicker = false;
        Title             = repack.Title;
        Platform          = "PC";
        Genre             = "";
        CoverGradient     = "#2d1b00,#5c3800";
        RatingStars       = "—";
        Price             = null;
        CoverUrl          = null;
        IsRom             = false;
        _databaseDescription = null;
        _databaseTitleId     = null;
        PopulateRegions(null);
        PopulateStoreUrl(null, "PC", null);

        Description = $"Repack archive ready to install  ·  {repack.SizeLabel}";

        PopulateTrailer(null);
        Screenshots.Clear();
        HasScreenshots = false;
        PopulateAchievements(null);

        IsLocalGame          = true;
        IsInstalled          = false;
        IsRepack             = true;
        IsSetupRepack        = repack.FileType == "setup";
        ShowDrivePicker      = false;
        RepackPath           = repack.FilePath;
        RepackSizeLabel      = repack.SizeLabel;
        HasMatchingRepack    = false;
        MatchingRepackLabel  = "";
        InstallStatusMessage = "";
        InstallStatusIsError = false;
        _driveInstances      = new List<LocalGameDriveEntry>();
        DriveLabels.Clear();
        HasMultipleDrives    = false;
        SelectedDriveIndex   = 0;
        ActiveDriveLabel     = "";
        ActiveDrivePath      = "";
        ActiveExeType        = "";
        IsSwitch             = false;
        SwitchMods.Clear();
        HasSwitchMods        = false;
        ShowModsPanel        = false;
        SwitchModsStatus     = "";
        _steamAppId          = 0;
        IsSteamInstallable   = false;
        SteamInstallUrl      = "";
        HasSteamLaunchOption = false;
        LoadReviews();
    }

    public void LoadFromLocalRom(LocalRom rom)
    {
        ShowSettings    = false;
        ShowModsPanel   = false;
        ShowDrivePicker = false;
        Title             = rom.Title;
        Platform          = rom.Platform;
        Genre             = "";
        CoverGradient     = "#0d1f3c,#1a3264";
        RatingStars       = "—";
        Price             = null;
        CoverUrl          = null;
        IsRom             = true;
        _databaseDescription = null;
        _databaseTitleId     = null;

        // Populate region/language metadata from the ROM file
        PopulateRegions(rom.Regions.Count > 0 ? rom.Regions : null);
        PopulateStoreUrl(null, rom.Platform, rom.TitleId);

        Description = $"ROM file  ·  {rom.SizeLabel}";

        PopulateTrailer(null);
        Screenshots.Clear();
        HasScreenshots = false;
        PopulateAchievements(null);

        IsLocalGame          = true;
        IsInstalled          = true;   // ROM is "installed" (the file exists on disk)
        IsRepack             = false;
        IsSetupRepack        = false;
        ShowDrivePicker      = false;
        RepackPath           = "";
        RepackSizeLabel      = "";
        HasMatchingRepack    = false;
        MatchingRepackLabel  = "";
        InstallStatusMessage = "";

        // Store the ROM's directory as the "folder path" so the Open Folder button works
        _currentLocalRom = rom;
        ApplyRomDriveInstances(rom);
        PopulatePlaytime(rom.Platform, rom.Title);
        LoadSwitchMods();
        _steamAppId          = 0;
        IsSteamInstallable   = false;
        SteamInstallUrl      = "";
        HasSteamLaunchOption = false;
        LoadReviews();
    }


    /// <summary>
    /// Applies installation / repack state shared by <see cref="LoadFromGame"/>
    /// and <see cref="LoadFromStoreGame"/>.
    /// </summary>
    private void ApplyInstallState(LocalGame? localGame, LocalRepack? repack, LocalRom? localRom = null)
    {
        if (localGame != null)
        {
            // Game is installed on a local drive — show Play + ··· buttons
            IsInstalled     = true;
            IsRepack        = false;
            RepackPath      = "";
            RepackSizeLabel = "";
            // Show a repack-available badge when the game is installed AND a repack exists.
            HasMatchingRepack  = repack != null;
            MatchingRepackLabel = repack != null
                ? $"🗜  Repack available  ·  {repack.SizeLabel}"
                : "";

            _driveInstances = localGame.DriveInstances.Count > 0
                ? localGame.DriveInstances
                : new List<LocalGameDriveEntry>
                {
                    new LocalGameDriveEntry
                    {
                        DriveRoot      = localGame.DriveRoot,
                        FolderPath     = localGame.FolderPath,
                        ExecutablePath = localGame.ExecutablePath,
                        ExecutableType = localGame.ExecutableType,
                    }
                };

            DriveLabels.Clear();
            foreach (var d in _driveInstances)
                DriveLabels.Add(d.DriveRoot);

            HasMultipleDrives  = _driveInstances.Count > 1;
            SelectedDriveIndex = 0;
            RefreshActiveDrive();
        }
        else if (localRom != null)
        {
            // ROM file is on a local drive — show Play button using the ROM file
            IsInstalled         = true;
            IsRom               = true;
            IsRepack            = false;
            IsSetupRepack       = false;
            ShowDrivePicker     = false;
            RepackPath          = "";
            RepackSizeLabel     = "";
            HasMatchingRepack   = false;
            MatchingRepackLabel = "";

            // Build one drive entry per distinct drive root so the multi-drive switcher
            // appears when the same ROM is present on several drives.
            ApplyRomDriveInstances(localRom);
        }
        else if (repack != null)
        {
            // Repack archive available — show Install button
            IsInstalled         = false;
            IsRepack            = true;
            IsSetupRepack       = repack.FileType == "setup";
            ShowDrivePicker     = false;
            RepackPath          = repack.FilePath;
            RepackSizeLabel     = repack.SizeLabel;
            HasMatchingRepack   = false;
            MatchingRepackLabel = "";
            _driveInstances     = new List<LocalGameDriveEntry>();
            ActiveDriveLabel    = "";
            ActiveDrivePath     = "";
            ActiveExeType       = "";
        }
        else
        {
            // Neither installed nor a repack — no action buttons
            IsInstalled         = false;
            IsRepack            = false;
            IsSetupRepack       = false;
            ShowDrivePicker     = false;
            RepackPath          = "";
            RepackSizeLabel     = "";
            HasMatchingRepack   = false;
            MatchingRepackLabel = "";
            _driveInstances     = new List<LocalGameDriveEntry>();
            ActiveDriveLabel    = "";
            ActiveDrivePath     = "";
            ActiveExeType       = "";
        }
    }

    // ─────────────────────────────────────────────────────────────────────────

    partial void OnSelectedDriveIndexChanged(int value) => RefreshActiveDrive();

    // ─────────────────────────────────────────────────────────────────────────
    // Enrich a local game detail with data looked up from Games.Database
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Called asynchronously after <see cref="LoadFromLocalGame"/> to fill in
    /// cover image, description, trailer, screenshots and achievements URL from
    /// the public Games.Database — the same data the website shows.
    /// Must be called on the UI thread.
    /// </summary>
    public void EnrichFromDatabaseGame(DatabaseGame dbGame)
    {
        // Use the canonical database title (e.g. "Call of Duty: Black Ops II"
        // instead of the Windows-safe folder name "Call of Duty - Black Ops II")
        if (!string.IsNullOrEmpty(dbGame.Title))
            Title = dbGame.Title;

        if (!string.IsNullOrEmpty(dbGame.CoverUrl))
            CoverUrl = dbGame.CoverUrl;

        if (!string.IsNullOrEmpty(dbGame.Description))
        {
            _databaseDescription = dbGame.Description;
            Description          = dbGame.Description;
        }

        // Populate genre if not already set (Xbox 360 and enriched databases include this)
        if (!string.IsNullOrEmpty(dbGame.Genre) && string.IsNullOrEmpty(Genre))
            Genre = dbGame.Genre;

        // Populate release year if not already set
        if (!string.IsNullOrEmpty(dbGame.ReleaseYear) && string.IsNullOrEmpty(ReleaseYear))
            ReleaseYear = dbGame.ReleaseYear;

        // Cache the TitleID from the database so LoadSwitchMods can use it even
        // when the scanned ROM does not carry a TitleID of its own.
        if (!string.IsNullOrEmpty(dbGame.TitleId))
        {
            _databaseTitleId = dbGame.TitleId;

            // If LoadSwitchMods ran earlier (synchronously inside LoadFromLocalRom) but had
            // no TitleID at the time, re-run it now that we have one from the database.
            // This handles the common case where async enrichment completes after the initial
            // detail view is shown.
            // _ryujinxModsJsonPath is set by a successful LoadSwitchMods run; null means it
            // either hasn't run yet for a Switch game or returned early due to no TitleID.
            // The additional check on _currentLocalRom?.TitleId confirms the early-return
            // was specifically because the ROM lacked a TitleID (not a non-Switch game).
            if (IsSwitch && _ryujinxModsJsonPath == null
                         && string.IsNullOrEmpty(_currentLocalRom?.TitleId))
                LoadSwitchMods();
        }

        // Populate store URL from database (overrides any previously derived one)
        if (!string.IsNullOrEmpty(dbGame.StorePageUrl) || dbGame.AppId.HasValue || !string.IsNullOrEmpty(dbGame.TitleId))
            PopulateStoreUrl(dbGame.StorePageUrl, Platform, dbGame.TitleId ?? (dbGame.AppId.HasValue ? dbGame.AppId.Value.ToString() : null));

        PopulateTrailer(dbGame.TrailerUrl);
        PopulateScreenshots(dbGame.Screenshots);

        // Load achievements from the AchievementsUrl if we don't already have them
        if (!HasAchievements && !string.IsNullOrEmpty(dbGame.AchievementsUrl))
            _ = FetchAndDisplayAchievementsAsync(dbGame.AchievementsUrl);
    }

    /// <summary>
    /// Fetches the achievements JSON from the given URL (or local disk cache if
    /// available) and populates the Achievements collection.
    /// Mirrors <c>_loadAchievementsInModal</c> in script.js.
    /// </summary>
    /// <remarks>
    /// Marked <c>internal</c> so <see cref="MainViewModel.EnrichGameAchievementsAsync"/>
    /// can trigger achievement loading for non-PC cloud library games whose
    /// <c>AchievementsUrl</c> was not stored when the game was added to the library.
    /// </remarks>
    internal async System.Threading.Tasks.Task FetchAndDisplayAchievementsAsync(string url)
    {
        try
        {
            string json;

            // Prefer the locally-cached achievements.json so the detail view works
            // offline and loads instantly without a network round-trip.
            // Resolve the best cache key: ROM titleId → database titleId → title
            string? titleId = _currentLocalRom?.TitleId ?? _databaseTitleId;
            string? cachedPath = CacheService?.GetCachedAchievementsPath(Platform, titleId, Title);

            if (!string.IsNullOrEmpty(cachedPath) && System.IO.File.Exists(cachedPath))
            {
                DevLogService.Log($"[AchievementsCache] Loading from disk: {cachedPath}");
                json = await System.IO.File.ReadAllTextAsync(cachedPath).ConfigureAwait(false);
            }
            else
            {
                using var http = new System.Net.Http.HttpClient();
                http.DefaultRequestHeaders.UserAgent.ParseAdd("GameOS-Launcher/2.0");
                json = await http.GetStringAsync(url);
            }

            if (string.IsNullOrWhiteSpace(json)) return;

            var opts = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Achievements JSON can be a root array or { "achievements": [...] }
            System.Text.Json.JsonElement arr;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
                arr = root;
            else if (root.TryGetProperty("achievements", out var sub) && sub.ValueKind == System.Text.Json.JsonValueKind.Array)
                arr = sub;
            else
                return;

            var list = new List<Achievement>();
            foreach (var item in arr.EnumerateArray())
            {
                string name = TryGetStringProp(item, "name", "Name");
                string desc = TryGetStringProp(item, "description", "Description");
                string icon = TryGetStringProp(item, "iconUrl", "IconUrl");

                if (string.IsNullOrEmpty(name)) continue;
                list.Add(new Achievement
                {
                    Name        = name,
                    Description = desc,
                    IconUrl     = string.IsNullOrEmpty(icon) ? null : icon,
                });
            }

            if (list.Count > 0)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    PopulateAchievements(list));
            }
        }
        catch { /* best-effort */ }
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds <see cref="_driveInstances"/> and <see cref="DriveLabels"/> from all paths
    /// associated with a ROM (primary <see cref="LocalRom.FilePath"/> + any
    /// <see cref="LocalRom.AdditionalPaths"/>).  One entry is created per distinct drive
    /// root so the "Available on Multiple Drives" switcher appears whenever the same ROM
    /// is present on more than one drive.  Multiple paths on the same drive (multi-disk)
    /// are intentionally collapsed into a single entry and handled separately by the ROM
    /// settings ComboBox.
    /// </summary>
    private void ApplyRomDriveInstances(LocalRom rom)
    {
        var allPaths = new List<string> { rom.FilePath };
        if (rom.AdditionalPaths != null)
            allPaths.AddRange(rom.AdditionalPaths);

        var seenDrives  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _driveInstances = new List<LocalGameDriveEntry>();
        foreach (var path in allPaths)
        {
            string driveRoot = System.IO.Path.GetPathRoot(path) ?? "";
            if (!seenDrives.Add(driveRoot)) continue;
            _driveInstances.Add(new LocalGameDriveEntry
            {
                DriveRoot      = driveRoot,
                FolderPath     = System.IO.Path.GetDirectoryName(path) ?? "",
                ExecutablePath = path,
                ExecutableType = rom.FileType,
            });
        }

        DriveLabels.Clear();
        foreach (var d in _driveInstances)
            DriveLabels.Add(d.DriveRoot);

        HasMultipleDrives  = _driveInstances.Count > 1;
        SelectedDriveIndex = 0;
        ActiveDriveLabel   = _driveInstances[0].DriveRoot;
        ActiveDrivePath    = _driveInstances[0].FolderPath;
        ActiveExeType      = rom.FileType.ToUpperInvariant();
    }

    private void RefreshActiveDrive()
    {
        if (_driveInstances.Count == 0) return;
        int idx = System.Math.Clamp(SelectedDriveIndex, 0, _driveInstances.Count - 1);
        var entry = _driveInstances[idx];
        ActiveDriveLabel = entry.DriveRoot;
        ActiveDrivePath  = entry.FolderPath;
        ActiveExeType    = entry.ExecutableType.ToUpperInvariant();
        // Use the real database description if available; fall back to install path
        if (!string.IsNullOrEmpty(_databaseDescription))
            Description = _databaseDescription;
        else
            Description = $"Installed at: {entry.FolderPath}";
    }

    [RelayCommand]
    private void SelectDrive(string drive)
    {
        int idx = DriveLabels.IndexOf(drive);
        if (idx >= 0) SelectedDriveIndex = idx;
    }

    private void PopulateTrailer(string? url)
    {
        TrailerUrl   = url;
        HasTrailer   = !string.IsNullOrEmpty(url);
        TrailerLabel = HasTrailer
            ? (string.IsNullOrEmpty(YoutubeVideoId) ? "▶  Watch Trailer on YouTube" : "▶  Watch Trailer")
            : "▶  Watch Trailer";
    }

    private void PopulateScreenshots(List<string>? shots)
    {
        Screenshots.Clear();
        if (shots != null)
            foreach (var s in shots) Screenshots.Add(s);
        HasScreenshots = Screenshots.Count > 0;
    }

    /// <summary>Loads per-game playtime from the PlaytimeService and updates the display label.</summary>
    private void PopulatePlaytime(string platform, string title)
    {
        int minutes = PlaytimeService.GetTotalMinutes(platform, title);
        if (minutes <= 0)
        {
            PlaytimeLabel = "";
            HasPlaytime   = false;
            return;
        }

        if (minutes < 60)
        {
            PlaytimeLabel = $"{minutes}m played";
        }
        else
        {
            int days  = minutes / 1440;
            int hours = (minutes % 1440) / 60;
            int mins  = minutes % 60;
            if (days > 0)
                PlaytimeLabel = mins > 0
                    ? $"{days}d {hours}h {mins}m played"
                    : $"{days}d {hours}h played";
            else
                PlaytimeLabel = mins > 0
                    ? $"{hours}h {mins}m played"
                    : $"{hours}h played";
        }
        HasPlaytime = true;
    }

    private void PopulateAchievements(List<Achievement>? achievements)
    {
        Achievements.Clear();
        if (achievements != null)
            foreach (var a in achievements) Achievements.Add(a);
        HasAchievements   = Achievements.Count > 0;
        ShowAllAchievements = false;
        HasMoreAchievements = Achievements.Count > AchievementsPreviewCount;
        AchievementsLabel = HasAchievements
            ? $"🏆  Achievements  ({Achievements.Count})"
            : "🏆  Achievements";
        RefreshVisibleAchievements();
    }

    private void RefreshVisibleAchievements()
    {
        VisibleAchievements.Clear();
        var source = ShowAllAchievements
            ? Achievements
            : Achievements.Take(AchievementsPreviewCount);
        foreach (var a in source)
            VisibleAchievements.Add(a);
        HasMoreAchievements = Achievements.Count > AchievementsPreviewCount;
    }

    private void PopulateRegions(List<string>? regions)
    {
        if (regions != null && regions.Count > 0)
        {
            RegionsLabel = string.Join(" · ", regions);
            HasRegions   = true;
        }
        else
        {
            RegionsLabel = "";
            HasRegions   = false;
        }
    }

    /// <summary>
    /// Builds the store page URL based on the platform, app ID, or title ID.
    /// Platform → URL format:
    ///   PC (Steam): https://store.steampowered.com/app/{AppId}/
    ///   PS3/PS4:    https://store.playstation.com/en-gb/product/{TitleId}
    ///   Switch:     https://www.nintendo.com/search/#q={title}
    ///   Xbox 360:   https://marketplace.xbox.com/en-US/Product/{TitleId}
    /// </summary>
    private void PopulateStoreUrl(string? explicitUrl, string platform, string? idHint)
    {
        string? url = explicitUrl;

        if (string.IsNullOrEmpty(url))
        {
            bool isPlayStation = platform is "PS3" or "PS4" or "PS5";
            bool isXbox        = platform is "Xbox 360" or "Xbox One";

            if (!string.IsNullOrEmpty(idHint))
            {
                if (string.Equals(platform, "PC", StringComparison.OrdinalIgnoreCase))
                {
                    // idHint is AppId (Steam)
                    if (long.TryParse(idHint, out long appId) && appId > 0)
                        url = $"https://store.steampowered.com/app/{appId}/";
                }
                else if (isPlayStation)
                {
                    url = $"https://store.playstation.com/en-gb/product/{idHint}";
                }
                else if (isXbox)
                {
                    url = $"https://www.xbox.com/en-GB/search?q={Uri.EscapeDataString(Title)}";
                }
            }

            if (string.IsNullOrEmpty(url))
            {
                // Fallback: search by title on the platform's storefront
                if (!string.IsNullOrEmpty(Title))
                {
                    if (string.Equals(platform, "Switch", StringComparison.OrdinalIgnoreCase))
                        url = $"https://www.nintendo.com/search/#q={Uri.EscapeDataString(Title)}";
                    else if (isXbox)
                        url = $"https://www.xbox.com/en-GB/search?q={Uri.EscapeDataString(Title)}";
                }
            }
        }

        StorePageUrl   = url;
        HasStoreUrl    = !string.IsNullOrEmpty(url);
        StoreButtonLabel = platform switch
        {
            "PC"                => "🎮  View on Steam",
            "PS3" or "PS4" or "PS5" => "🛒  PlayStation Store",
            "Switch"            => "🛒  Nintendo eShop",
            "Xbox 360" or "Xbox One" => "🛒  Xbox Store",
            _                   => "🛒  View in Store",
        };
    }

    /// <summary>
    /// Returns the string value of the first matching property from an element,
    /// trying each key in order (case-sensitive).  Returns "" when none match.
    /// </summary>
    private static string TryGetStringProp(System.Text.Json.JsonElement el, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (el.TryGetProperty(key, out var val))
                return val.GetString() ?? "";
        }
        return "";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Nintendo Switch Ryujinx mod management
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Opens the mods management panel so the user can enable/disable mods.</summary>
    [RelayCommand]
    private void OpenModsPanel()
    {
        // Reload mods each time the panel is opened so that newly added mods are shown
        // without requiring the user to navigate away and back.
        LoadSwitchMods();
        ShowModsPanel = true;
    }

    /// <summary>Closes the mods management panel.</summary>
    [RelayCommand]
    private void CloseModsPanel()
    {
        ShowModsPanel    = false;
        SwitchModsStatus = "";
    }

    /// <summary>
    /// Loads Ryujinx mods for the current Switch game from its <c>mods.json</c>.
    /// Silently clears the mods collection for non-Switch games or when no TitleID
    /// is available.
    /// </summary>
    private void LoadSwitchMods()
    {
        SwitchMods.Clear();
        HasSwitchMods           = false;
        ModsJsonExistsButEmpty  = false;
        ShowModsPanel           = false;
        SwitchModsStatus        = "";
        SwitchModsJsonPath      = "";
        _ryujinxModsJsonPath    = null;

        IsSwitch = string.Equals(Platform, "Switch", StringComparison.OrdinalIgnoreCase);
        if (!IsSwitch) return;

        // Resolve TitleID using a three-tier fallback:
        //   1. TitleID embedded in the scanned ROM file itself (most precise)
        //   2. TitleID cached from EnrichFromDatabaseGame (set when async enrichment ran first)
        //   3. Synchronous lookup from the local GamesDbCache/Switch.json — always prefers
        //      the locally-downloaded file so mods work even without a network round-trip.
        string? titleId = _currentLocalRom?.TitleId;

        if (string.IsNullOrEmpty(titleId))
            titleId = _databaseTitleId;

        if (string.IsNullOrEmpty(titleId) && !string.IsNullOrEmpty(Title))
            titleId = Services.GitHubDataService.TryGetTitleIdFromLocalCache("Switch", Title);

        if (string.IsNullOrEmpty(titleId)) return;

        // Guard: Switch TitleIDs are always exactly 16 hexadecimal characters.
        // Any other value (e.g. a RAWG database "id" such as "1222700") must not
        // be used as a folder name or mods.json path.
        if (!_switchTitleIdValidationRegex.IsMatch(titleId))
            return;

        // Locate the Ryujinx executable from the configured emulator for Switch
        var emuSettings = Services.EmulatorSettingsService.Load("Switch");
        if (string.IsNullOrEmpty(emuSettings.EmulatorPath)) return;

        // Only apply logic when the configured emulator is actually Ryujinx
        string exeName = System.IO.Path.GetFileNameWithoutExtension(emuSettings.EmulatorPath)
                         .ToLowerInvariant();
        if (!exeName.Contains("ryujinx")) return;

        // Try to find an existing mods.json; fall back to the default creation path so
        // the panel has a valid target even before the file is first created.
        string? modsJsonPath = Services.RyujinxModService.FindModsJson(emuSettings.EmulatorPath, titleId)
                               ?? Services.RyujinxModService.GetDefaultModsJsonPath(emuSettings.EmulatorPath, titleId);

        _ryujinxModsJsonPath = modsJsonPath;
        SwitchModsJsonPath   = modsJsonPath ?? "";

        if (!System.IO.File.Exists(modsJsonPath))
        {
            // File not yet created — show the "no mods.json" empty state.
            // _ryujinxModsJsonPath is already set so OpenSwitchModsFolder / SaveMods
            // know where to write when the user clicks "Open Mods Folder".
            return;
        }

        var mods = Services.RyujinxModService.LoadMods(modsJsonPath);
        foreach (var mod in mods)
            SwitchMods.Add(new RyujinxModVm { Name = mod.Name, Path = mod.Path, Enabled = mod.Enabled });

        HasSwitchMods          = SwitchMods.Count > 0;
        // ModsJsonExistsButEmpty = file was found but contained no mod entries.
        ModsJsonExistsButEmpty = !HasSwitchMods;
    }

    /// <summary>Toggles the enabled state of a Ryujinx mod and persists the change to <c>mods.json</c>.</summary>
    [RelayCommand]
    private void ToggleSwitchMod(RyujinxModVm? mod)
    {
        if (mod == null || string.IsNullOrEmpty(_ryujinxModsJsonPath)) return;

        mod.Enabled = !mod.Enabled;

        // Persist all mods back to mods.json
        var modList = SwitchMods.Select(m => new GameLauncher.Models.RyujinxMod
        {
            Name    = m.Name,
            Path    = m.Path,
            Enabled = m.Enabled,
        }).ToList();

        try
        {
            Services.RyujinxModService.SaveMods(_ryujinxModsJsonPath, modList);
            SwitchModsStatus = "✓  Mod settings saved.";
        }
        catch (Exception ex)
        {
            SwitchModsStatus = $"Failed to save: {ex.Message}";
        }
    }

    /// <summary>
    /// Opens the Ryujinx mods folder for the current Switch game in the system file manager.
    /// Uses the same emulator exe path stored in Settings → Switch to derive the folder,
    /// creates it together with an empty <c>mods.json</c> on first use, then opens the
    /// directory exactly the way the "Open ROM Folder" button opens <see cref="ActiveDrivePath"/>.
    /// Path: <c>{ryujinxDir}\portable\games\{titleId}\</c> (portable mode) or
    /// <c>%APPDATA%\Ryujinx\games\{titleId}\</c> (standard mode).
    /// </summary>
    [RelayCommand]
    private void OpenSwitchModsFolder()
    {
        // ── 1. Resolve the mods.json path ────────────────────────────────────
        // Prefer the path already computed by LoadSwitchMods; fall back to
        // deriving it fresh from the emulator settings + titleId.
        string? modsJsonPath = _ryujinxModsJsonPath;

        if (string.IsNullOrEmpty(modsJsonPath))
        {
            string? titleId = _currentLocalRom?.TitleId;

            if (string.IsNullOrEmpty(titleId))
                titleId = _databaseTitleId;

            if (string.IsNullOrEmpty(titleId) && !string.IsNullOrEmpty(Title))
                titleId = Services.GitHubDataService.TryGetTitleIdFromLocalCache("Switch", Title);

            if (string.IsNullOrEmpty(titleId))
            {
                SwitchModsStatus = "⚠  No TitleID found for this game.";
                return;
            }

            var emuSettings = Services.EmulatorSettingsService.Load("Switch");
            if (string.IsNullOrEmpty(emuSettings.EmulatorPath))
            {
                SwitchModsStatus = "⚠  Ryujinx path not configured — set it in Settings.";
                return;
            }

            modsJsonPath = Services.RyujinxModService.GetDefaultModsJsonPath(emuSettings.EmulatorPath, titleId);
        }

        string modsDir = System.IO.Path.GetDirectoryName(modsJsonPath) ?? "";
        if (string.IsNullOrEmpty(modsDir))
        {
            SwitchModsStatus = "⚠  Could not determine the mods folder path.";
            return;
        }

        // ── 2. Create folder + empty mods.json on first use ──────────────────
        try
        {
            Directory.CreateDirectory(modsDir);

            if (!File.Exists(modsJsonPath))
                Services.RyujinxModService.SaveMods(modsJsonPath,
                    new System.Collections.Generic.List<GameLauncher.Models.RyujinxMod>());
        }
        catch (Exception ex)
        {
            SwitchModsStatus = $"⚠  Could not create mods folder: {ex.Message}";
            return;
        }

        // ── 3. Reload panel state ─────────────────────────────────────────────
        LoadSwitchMods();
        ShowModsPanel = true;

        // ── 4. Open the folder — same as OpenGameFolder opens ActiveDrivePath ─
        OpenWithSystem(modsDir);
    }
}
