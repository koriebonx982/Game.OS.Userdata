using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Models;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

/// <summary>
/// View-model for the Settings page.
/// Allows the user to configure per-platform emulators and basic app settings.
/// Each platform can have multiple emulators; the user can add, remove, and reorder them.
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    // ── Platform emulator groups ───────────────────────────────────────────
    public ObservableCollection<EmulatorPlatformGroupVm> EmulatorGroups { get; } = new();

    // ── Application-wide settings ──────────────────────────────────────────
    /// <summary>Check for Games.Database updates on startup.</summary>
    [ObservableProperty] private bool _autoUpdate = true;
    /// <summary>Play the Game.OS intro animation when the launcher starts.</summary>
    [ObservableProperty] private bool _showIntroVideo = true;
    /// <summary>Path to a custom intro video file (empty = use built-in animation).</summary>
    [ObservableProperty] private string _introVideoPath = "";
    /// <summary>Read the Ryujinx log after each Switch game session.</summary>
    [ObservableProperty] private bool _readSwitchLog = false;
    /// <summary>Write all debug output and exceptions to Dev.log next to the exe.</summary>
    [ObservableProperty] private bool _devLogs = false;

    // ── Game launch settings ───────────────────────────────────────────────
    /// <summary>Minimise Game.OS when a game launches; restore when it exits.</summary>
    [ObservableProperty] private bool _minimizeOnGameLaunch = false;
    /// <summary>Also watch child processes in the game folder (for mod clients like Plutonium).</summary>
    [ObservableProperty] private bool _trackFolderProcesses = true;

    // ── Notification settings ──────────────────────────────────────────────
    /// <summary>Show a toast when a friend comes online.</summary>
    [ObservableProperty] private bool _notifyFriendOnline = false;
    /// <summary>Show a toast when a friend starts playing a game.</summary>
    [ObservableProperty] private bool _notifyFriendGameStart = false;
    /// <summary>Broadcast to friends when the user starts playing a game.</summary>
    [ObservableProperty] private bool _broadcastGameStart = false;
    /// <summary>Broadcast to friends when the user comes online.</summary>
    [ObservableProperty] private bool _broadcastUserOnline = false;

    // ── Developer / Feature flags ──────────────────────────────────────────
    /// <summary>Sync Steam playtime into Game.OS after each Steam import.</summary>
    [ObservableProperty] private bool _enableSteamSync = true;
    /// <summary>Automatically record and sync achievement unlocks from emulator logs.</summary>
    [ObservableProperty] private bool _enableAchievementAutoSync = true;
    /// <summary>Show debug toast notifications for Ryujinx log detection/read status.</summary>
    [ObservableProperty] private bool _notifyRyujinxLogStatus = false;

    // ── Scanner diagnostic logging toggles ─────────────────────────────────
    /// <summary>Log Games-folder scan summary per drive.</summary>
    [ObservableProperty] private bool _logGamesScanner = false;
    /// <summary>Log every individual game found (advanced mode).</summary>
    [ObservableProperty] private bool _logGamesScannerAdvanced = false;
    /// <summary>Log ROMs-folder scan summary per drive / platform.</summary>
    [ObservableProperty] private bool _logRomsScanner = false;
    /// <summary>Log every individual ROM found (advanced mode).</summary>
    [ObservableProperty] private bool _logRomsScannerAdvanced = false;
    /// <summary>Log Repacks-folder scan summary per drive.</summary>
    [ObservableProperty] private bool _logRepacksScanner = false;
    /// <summary>Log every individual repack found (advanced mode).</summary>
    [ObservableProperty] private bool _logRepacksScannerAdvanced = false;
    /// <summary>Log local Steam (ACF + folder) scan activity.</summary>
    [ObservableProperty] private bool _logLocalSteamScanner = false;
    /// <summary>Log Steam Web API import activity.</summary>
    [ObservableProperty] private bool _logSteamApiScanner = false;

    // ── Third-party integration settings (stored locally, never synced) ────
    /// <summary>Steam Web API key — local only.</summary>
    [ObservableProperty] private string _steamApiKey = "";
    /// <summary>Steam 64-bit User ID (SteamID64) for fetching the owned games library.</summary>
    [ObservableProperty] private string _steamUserId = "";
    /// <summary>True while a Steam library import is in progress.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImportSteamButtonLabel))]
    private bool _isSteamImporting;
    /// <summary>Label for the Import Steam Library button.</summary>
    public string ImportSteamButtonLabel =>
        IsSteamImporting ? "⏳  Importing…" : "⬇  Import Steam Library";
    /// <summary>Status message shown in the Steam Integration card (success / error).</summary>
    [ObservableProperty] private string _steamImportStatus = "";
    /// <summary>Exophase username for achievement scraping.</summary>
    [ObservableProperty] private string _exophaseUsername = "";
    /// <summary>Exophase password — local only.</summary>
    [ObservableProperty] private string _exophasePassword = "";
    /// <summary>Exophase profile anchor/id (e.g. #2896888) used for per-user achievement pages.</summary>
    [ObservableProperty] private string _exophaseProfileId = "";
    /// <summary>Enable a system-wide quick-menu hotkey on Windows.</summary>
    [ObservableProperty] private bool _enableGlobalQuickMenuHotkey = false;
    /// <summary>Use a top-most quick-menu fallback for problematic fullscreen games.</summary>
    [ObservableProperty] private bool _compatibilityOverlayMode = false;
    /// <summary>Prefer cached local metadata first for installed games and offline sessions.</summary>
    [ObservableProperty] private bool _preferOfflineCachedMetadata = true;

    /// <summary>
    /// Wired by MainViewModel: invoked when "Import Steam Library" is clicked.
    /// Receives (apiKey, steamUserId) and returns a status string.
    /// Stored in SettingsViewModel so the button command can trigger it.
    /// </summary>
    public Func<string, string, Task<string>>? ImportSteamLibraryAction { get; set; }

    /// <summary>
    /// Wired by MainViewModel: invoked after Save() when a non-empty Steam User ID
    /// is present, to link/verify it on the backend.
    /// Returns <c>null</c> on success or an error message string.
    /// </summary>
    public Func<string, Task<string?>>? LinkSteamIdAction { get; set; }

    [RelayCommand]
    private async Task ImportSteamLibrary()
    {
        if (IsSteamImporting) return;
        if (string.IsNullOrWhiteSpace(SteamApiKey) || string.IsNullOrWhiteSpace(SteamUserId))
        {
            SteamImportStatus = "⚠  Enter a Steam API Key and Steam User ID before importing.";
            return;
        }

        // Save current values before importing so they are persisted
        Save();

        IsSteamImporting  = true;
        SteamImportStatus = "Importing Steam library…";
        try
        {
            if (ImportSteamLibraryAction != null)
                SteamImportStatus = await ImportSteamLibraryAction(SteamApiKey, SteamUserId);
            else
                SteamImportStatus = "⚠  Import not available.";
        }
        catch (Exception ex)
        {
            SteamImportStatus = $"❌  Import failed: {ex.Message}";
        }
        finally
        {
            IsSteamImporting = false;
        }
    }

    /// <summary>Opens the Steam API key page in the system browser.</summary>
    [RelayCommand]
    private void OpenSteamApiKeyPage()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = "https://steamcommunity.com/dev/apikey",
                UseShellExecute = true,
            });
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Fires one of each toast notification type with dummy data so the developer
    /// can verify that toasts appear correctly without running a real game session.
    /// Notifications are staggered by 400 ms so Windows does not throttle them.
    /// </summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task TestToastNotifications()
    {
        Services.NotificationService.ShowAchievementUnlockedNotification("Test Achievement", "Test Game");
        await System.Threading.Tasks.Task.Delay(400);
        Services.NotificationService.ShowFriendOnlineNotification("TestFriend");
        await System.Threading.Tasks.Task.Delay(400);
        Services.NotificationService.ShowGameSessionStartedNotification("Test Game");
        await System.Threading.Tasks.Task.Delay(400);
        Services.NotificationService.ShowSessionEndedNotification("Test Game", 42);
        await System.Threading.Tasks.Task.Delay(400);
        Services.NotificationService.ShowMessageNotification("TestFriend", "Hello from Game.OS!");
    }

    // ── Active settings section (Steam-style left-nav) ────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAppSection))]
    [NotifyPropertyChangedFor(nameof(IsEmulatorSection))]
    [NotifyPropertyChangedFor(nameof(IsPlaytimeSection))]
    [NotifyPropertyChangedFor(nameof(IsSyncSection))]
    [NotifyPropertyChangedFor(nameof(IsAccountSection))]
    [NotifyPropertyChangedFor(nameof(IsCustomiseSection))]
    [NotifyPropertyChangedFor(nameof(IsSystemSection))]
    [NotifyPropertyChangedFor(nameof(IsNotificationsSection))]
    [NotifyPropertyChangedFor(nameof(IsDeveloperSection))]
    private string _selectedSection = "app";

    public bool IsAppSection           => SelectedSection == "app";
    public bool IsEmulatorSection      => SelectedSection == "emulator";
    public bool IsPlaytimeSection      => SelectedSection == "playtime";
    public bool IsSyncSection          => SelectedSection == "sync";
    public bool IsAccountSection       => SelectedSection == "account";
    public bool IsCustomiseSection     => SelectedSection == "customise";
    public bool IsSystemSection        => SelectedSection == "system";
    public bool IsNotificationsSection => SelectedSection == "notifications";
    public bool IsDeveloperSection     => SelectedSection == "developer";

    [RelayCommand]
    private void SelectSection(string section) => SelectedSection = section;

    // ── Account section ────────────────────────────────────────────────────
    [ObservableProperty] private string _accountUsername   = "";
    [ObservableProperty] private string _accountEmail      = "";
    [ObservableProperty] private string _accountMemberSince = "";
    [ObservableProperty] private int    _accountGamesCount;

    /// <summary>First character of AccountUsername, upper-cased, for the avatar circle.</summary>
    public string AccountAvatarInitial =>
        AccountUsername.Length > 0 ? AccountUsername[0].ToString().ToUpper() : "?";

    partial void OnAccountUsernameChanged(string value) =>
        OnPropertyChanged(nameof(AccountAvatarInitial));

    /// <summary>
    /// Populates the Account section with data from the logged-in user's profile.
    /// Called by MainViewModel after a successful login.
    /// </summary>
    public void LoadAccount(UserProfile profile, List<Game> library)
    {
        AccountUsername   = profile.Username;
        AccountEmail      = !string.IsNullOrEmpty(profile.Email) ? profile.Email : "—";
        AccountGamesCount = library.Count;
        if (DateTime.TryParse(profile.CreatedAt, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            AccountMemberSince = dt.ToString("MMMM d, yyyy");
        else
            AccountMemberSince = "—";
    }

    // ── Sync section ───────────────────────────────────────────────────────
    /// <summary>True while a manual sync is in progress.</summary>
    [ObservableProperty] private bool   _isSyncing;
    /// <summary>Human-readable label for the last successful sync time, e.g. "Last synced: 3 minutes ago".</summary>
    [ObservableProperty] private string _lastSyncedLabel = "Last synced: never";

    /// <summary>
    /// Wired by MainViewModel.  Executes an immediate full sync and updates
    /// <see cref="IsSyncing"/> / <see cref="LastSyncedLabel"/>.
    /// </summary>
    public Func<Task>? SyncNowAction { get; set; }

    [RelayCommand]
    private async Task SyncNow()
    {
        if (IsSyncing) return;
        IsSyncing = true;
        try
        {
            if (SyncNowAction != null)
                await SyncNowAction();
        }
        finally
        {
            IsSyncing = false;
        }
    }

    // ── Status message ─────────────────────────────────────────────────────
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool   _isSaveSuccess;

    // ── System section — startup apps ──────────────────────────────────────
    /// <summary>Startup app entries displayed in the System section.</summary>
    public ObservableCollection<StartupAppRowVm> StartupApps { get; } = new();

    /// <summary>Path typed by the user when adding a new custom startup app.</summary>
    [ObservableProperty] private string _newStartupAppPath      = "";
    [ObservableProperty] private string _newStartupAppArgs      = "";
    [ObservableProperty] private string _newStartupAppLabel     = "";

    public SettingsViewModel()
    {
        Load();
    }

    public void Load()
    {
        EmulatorGroups.Clear();
        foreach (var platform in EmulatorSettingsService.SupportedPlatforms)
        {
            var allSettings = EmulatorSettingsService.LoadAll(platform);
            var group       = new EmulatorPlatformGroupVm(platform, this);
            foreach (var s in allSettings)
                group.Emulators.Add(new EmulatorRowVm(platform, s));
            EmulatorGroups.Add(group);
        }

        var appSettings = AppSettingsService.Load();
        AutoUpdate             = appSettings.AutoUpdate;
        ShowIntroVideo         = appSettings.ShowIntroVideo;
        IntroVideoPath         = appSettings.IntroVideoPath;
        ReadSwitchLog          = appSettings.ReadSwitchLog;
        DevLogs                = appSettings.DevLogs;
        MinimizeOnGameLaunch   = appSettings.MinimizeOnGameLaunch;
        TrackFolderProcesses   = appSettings.TrackFolderProcesses;
        NotifyFriendOnline     = appSettings.NotifyFriendOnline;
        NotifyFriendGameStart  = appSettings.NotifyFriendGameStart;
        BroadcastGameStart     = appSettings.BroadcastGameStart;
        BroadcastUserOnline    = appSettings.BroadcastUserOnline;
        SteamApiKey            = appSettings.SteamApiKey;
        SteamUserId            = appSettings.SteamUserId;
        ExophaseProfileId      = appSettings.ExophaseProfileId;
        ExophaseUsername       = appSettings.ExophaseUsername;
        ExophasePassword       = appSettings.ExophasePassword;
        EnableSteamSync        = appSettings.EnableSteamSync;
        EnableAchievementAutoSync = appSettings.EnableAchievementAutoSync;
        NotifyRyujinxLogStatus    = appSettings.NotifyRyujinxLogStatus;
        EnableGlobalQuickMenuHotkey = appSettings.EnableGlobalQuickMenuHotkey;
        CompatibilityOverlayMode   = appSettings.CompatibilityOverlayMode;
        PreferOfflineCachedMetadata = appSettings.PreferOfflineCachedMetadata;
        LogGamesScanner           = appSettings.LogGamesScanner;
        LogGamesScannerAdvanced   = appSettings.LogGamesScannerAdvanced;
        LogRomsScanner            = appSettings.LogRomsScanner;
        LogRomsScannerAdvanced    = appSettings.LogRomsScannerAdvanced;
        LogRepacksScanner         = appSettings.LogRepacksScanner;
        LogRepacksScannerAdvanced = appSettings.LogRepacksScannerAdvanced;
        LogLocalSteamScanner      = appSettings.LogLocalSteamScanner;
        LogSteamApiScanner        = appSettings.LogSteamApiScanner;

        // ── Startup apps: merge saved entries with the built-in presets ──
        LoadStartupApps(appSettings.StartupApps);

        StatusMessage = "";
    }

    /// <summary>
    /// Merges the built-in presets (Steam, Epic, Radmin VPN) with any user-saved entries.
    /// Built-in presets are always present at the top; user custom entries follow.
    /// </summary>
    private void LoadStartupApps(List<Models.StartupAppEntry> saved)
    {
        StartupApps.Clear();

        // Built-in presets — default paths on Windows; user can override
        var presets = new[]
        {
            new Models.StartupAppEntry
            {
                Label    = "Steam",
                Path     = @"C:\Program Files (x86)\Steam\steam.exe",
                IsPreset = true,
                Enabled  = false,
            },
            new Models.StartupAppEntry
            {
                Label    = "Epic Games Launcher",
                Path     = @"C:\Program Files (x86)\Epic Games\Launcher\Portal\Binaries\Win32\EpicGamesLauncher.exe",
                IsPreset = true,
                Enabled  = false,
            },
            new Models.StartupAppEntry
            {
                Label     = "Radmin VPN",
                Path      = @"C:\Program Files (x86)\Radmin VPN\RadminVPN.exe",
                IsPreset  = true,
                Enabled   = false,
            },
        };

        foreach (var preset in presets)
        {
            // Check if the user has a saved version of this preset (matched by Label)
            var savedPreset = saved.FirstOrDefault(s => s.IsPreset &&
                string.Equals(s.Label, preset.Label, StringComparison.OrdinalIgnoreCase));
            if (savedPreset != null)
            {
                // Use the saved preset's values (user may have changed path / enabled state)
                StartupApps.Add(new StartupAppRowVm(savedPreset, this));
            }
            else
            {
                StartupApps.Add(new StartupAppRowVm(preset, this));
            }
        }

        // Custom (non-preset) user entries follow the presets
        foreach (var entry in saved.Where(s => !s.IsPreset))
            StartupApps.Add(new StartupAppRowVm(entry, this));
    }

    [RelayCommand]
    private void AddStartupApp()
    {
        if (string.IsNullOrWhiteSpace(NewStartupAppPath)) return;
        var entry = new Models.StartupAppEntry
        {
            Label     = string.IsNullOrWhiteSpace(NewStartupAppLabel)
                            ? AppLabelFromPath(NewStartupAppPath.Trim())
                            : NewStartupAppLabel.Trim(),
            Path      = NewStartupAppPath.Trim(),
            Arguments = string.IsNullOrWhiteSpace(NewStartupAppArgs) ? null : NewStartupAppArgs.Trim(),
            IsPreset  = false,
            Enabled   = true,
        };
        StartupApps.Add(new StartupAppRowVm(entry, this));
        NewStartupAppPath  = "";
        NewStartupAppArgs  = "";
        NewStartupAppLabel = "";
    }

    // ── safe helper to extract a display name from a file path ───────────────
    private static string AppLabelFromPath(string path)
    {
        try
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            return string.IsNullOrWhiteSpace(name) ? path : name;
        }
        catch
        {
            return path;
        }
    }

    [RelayCommand]
    private void RemoveStartupApp(StartupAppRowVm? row)
    {
        // Cannot remove built-in presets — only custom entries
        if (row != null && !row.IsPreset)
            StartupApps.Remove(row);
    }

    [RelayCommand]
    private void MoveStartupAppUp(StartupAppRowVm? row)
    {
        if (row == null) return;
        int idx = StartupApps.IndexOf(row);
        if (idx > 0) StartupApps.Move(idx, idx - 1);
    }

    [RelayCommand]
    private void MoveStartupAppDown(StartupAppRowVm? row)
    {
        if (row == null) return;
        int idx = StartupApps.IndexOf(row);
        if (idx >= 0 && idx < StartupApps.Count - 1) StartupApps.Move(idx, idx + 1);
    }

    [RelayCommand]
    private void BrowseStartupApp(StartupAppRowVm? row)
    {
        if (row == null) return;
        BrowseStartupAppRequested?.Invoke(row);
    }

    [RelayCommand]
    private void BrowseNewStartupApp()
    {
        BrowseNewStartupAppRequested?.Invoke();
    }

    /// <summary>Raised when the user clicks Browse… on a startup-app row.</summary>
    public System.Action<StartupAppRowVm>? BrowseStartupAppRequested     { get; set; }
    /// <summary>Raised when the user clicks Browse… for the new startup-app path.</summary>
    public System.Action?                  BrowseNewStartupAppRequested  { get; set; }

    [RelayCommand]
    private void Save()
    {
        foreach (var group in EmulatorGroups)
        {
            var list = group.Emulators.Select(row => new EmulatorSettings
            {
                Platform     = row.Platform,
                EmulatorPath = row.EmulatorPath,
                Arguments    = string.IsNullOrWhiteSpace(row.Arguments) ? "{rom}" : row.Arguments,
                EmulatorName = row.EmulatorName,
                Enabled      = row.Enabled,
            }).ToList();
            EmulatorSettingsService.SaveAll(group.Platform, list);
        }

        AppSettingsService.Save(new Models.AppSettings
        {
            AutoUpdate            = AutoUpdate,
            ShowIntroVideo        = ShowIntroVideo,
            IntroVideoPath        = IntroVideoPath,
            ReadSwitchLog         = ReadSwitchLog,
            DevLogs               = DevLogs,
            MinimizeOnGameLaunch  = MinimizeOnGameLaunch,
            TrackFolderProcesses  = TrackFolderProcesses,
            NotifyFriendOnline    = NotifyFriendOnline,
            NotifyFriendGameStart = NotifyFriendGameStart,
            BroadcastGameStart    = BroadcastGameStart,
            BroadcastUserOnline   = BroadcastUserOnline,
            SteamApiKey           = SteamApiKey,
            SteamUserId           = SteamUserId,
            ExophaseProfileId     = NormaliseExophaseProfileId(ExophaseProfileId),
            ExophaseUsername      = ExophaseUsername,
            ExophasePassword      = ExophasePassword,
            EnableSteamSync       = EnableSteamSync,
            EnableAchievementAutoSync = EnableAchievementAutoSync,
            NotifyRyujinxLogStatus    = NotifyRyujinxLogStatus,
            EnableGlobalQuickMenuHotkey = EnableGlobalQuickMenuHotkey,
            CompatibilityOverlayMode   = CompatibilityOverlayMode,
            PreferOfflineCachedMetadata = PreferOfflineCachedMetadata,
            LogGamesScanner           = LogGamesScanner,
            LogGamesScannerAdvanced   = LogGamesScannerAdvanced,
            LogRomsScanner            = LogRomsScanner,
            LogRomsScannerAdvanced    = LogRomsScannerAdvanced,
            LogRepacksScanner         = LogRepacksScanner,
            LogRepacksScannerAdvanced = LogRepacksScannerAdvanced,
            LogLocalSteamScanner      = LogLocalSteamScanner,
            LogSteamApiScanner        = LogSteamApiScanner,
            StartupApps           = StartupApps.Select(r => new Models.StartupAppEntry
            {
                Label     = r.Label,
                Path      = r.Path,
                Arguments = string.IsNullOrWhiteSpace(r.Arguments) ? null : r.Arguments,
                IsPreset  = r.IsPreset,
                Enabled   = r.Enabled,
            }).ToList(),
        });

        StatusMessage = "✅ Settings saved!";
        IsSaveSuccess = true;
        ExophaseProfileId = NormaliseExophaseProfileId(ExophaseProfileId);
        SettingsApplied?.Invoke();

        // Link Steam ID to the current account in the background (prevents duplicate SteamID).
        if (LinkSteamIdAction != null && !string.IsNullOrWhiteSpace(SteamUserId))
        {
            string steamId = SteamUserId.Trim();
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    string? err = await LinkSteamIdAction(steamId);
                    if (!string.IsNullOrEmpty(err))
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            StatusMessage = $"⚠ Steam ID: {err}";
                            IsSaveSuccess = false;
                        });
                    }
                }
                catch (Exception ex)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        StatusMessage = $"⚠ Steam ID link error: {ex.Message}";
                        IsSaveSuccess = false;
                    });
                }
            });
        }
    }

    [RelayCommand]
    private void BrowseEmulator(EmulatorRowVm? row)
    {
        if (row == null) return;
        BrowseRequested?.Invoke(row);
    }

    [RelayCommand]
    private void BrowseIntroVideo()
    {
        BrowseIntroVideoRequested?.Invoke();
    }

    [RelayCommand]
    private void PickIntroVideoFromGallery()
    {
        PickIntroVideoFromGalleryRequested?.Invoke();
    }

    /// <summary>Raised when the user clicks Browse… on an emulator row.</summary>
    public System.Action<EmulatorRowVm>? BrowseRequested { get; set; }

    /// <summary>Raised when the user clicks Browse… next to the intro video path.</summary>
    public System.Action? BrowseIntroVideoRequested { get; set; }

    /// <summary>Raised when the user clicks the Gallery button to pick an intro video from GitHub.</summary>
    public System.Action? PickIntroVideoFromGalleryRequested { get; set; }

    /// <summary>Raised after settings are persisted so the shell can refresh runtime-only behaviour.</summary>
    public System.Action? SettingsApplied { get; set; }

    private static string NormaliseExophaseProfileId(string value)
    {
        var trimmed = (value ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed)) return "";
        return trimmed.StartsWith("#", StringComparison.Ordinal) ? trimmed : $"#{trimmed}";
    }
}

/// <summary>
/// View-model for a platform group in the emulator settings grid.
/// Contains 1-N <see cref="EmulatorRowVm"/> entries that can be added or removed.
/// </summary>
public partial class EmulatorPlatformGroupVm : ViewModelBase
{
    private readonly SettingsViewModel _parent;

    public string Platform { get; }
    public ObservableCollection<EmulatorRowVm> Emulators { get; } = new();

    public EmulatorPlatformGroupVm(string platform, SettingsViewModel parent)
    {
        Platform = platform;
        _parent  = parent;
    }

    [RelayCommand]
    private void AddEmulator()
    {
        Emulators.Add(new EmulatorRowVm(Platform, new EmulatorSettings
        {
            Platform  = Platform,
            Arguments = "{rom}",
            Enabled   = true,
        }));
    }

    [RelayCommand]
    private void RemoveEmulator(EmulatorRowVm? row)
    {
        if (row != null && Emulators.Count > 1)
            Emulators.Remove(row);
        // Note: removing the last emulator entry is intentionally blocked to ensure
        // the platform always has at least one (possibly empty) configuration row.
    }
}

/// <summary>Editable row in the emulator settings grid.</summary>
public partial class EmulatorRowVm : ViewModelBase
{
    public string Platform { get; }

    [ObservableProperty] private string _emulatorPath = "";
    [ObservableProperty] private string _arguments    = "{rom}";
    [ObservableProperty] private string _emulatorName = "";
    [ObservableProperty] private bool   _enabled      = true;

    public EmulatorRowVm(string platform, EmulatorSettings settings)
    {
        Platform     = platform;
        EmulatorPath = settings.EmulatorPath;
        Arguments    = settings.Arguments;
        EmulatorName = settings.EmulatorName;
        Enabled      = settings.Enabled;
    }
}

/// <summary>Editable row in the System ▸ Startup Apps list.</summary>
public partial class StartupAppRowVm : ViewModelBase
{
    private readonly SettingsViewModel _parent;

    /// <summary>True for the built-in Steam / Epic / Radmin presets; these cannot be removed.</summary>
    public bool IsPreset { get; }

    [ObservableProperty] private string  _label     = "";
    [ObservableProperty] private string  _path      = "";
    [ObservableProperty] private string  _arguments = "";
    [ObservableProperty] private bool    _enabled;

    public StartupAppRowVm(Models.StartupAppEntry entry, SettingsViewModel parent)
    {
        _parent   = parent;
        IsPreset  = entry.IsPreset;
        Label     = entry.Label;
        Path      = entry.Path;
        Arguments = entry.Arguments ?? "";
        Enabled   = entry.Enabled;
    }
}
