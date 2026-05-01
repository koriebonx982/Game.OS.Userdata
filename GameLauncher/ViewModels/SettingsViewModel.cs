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

    // ── Active settings section (Steam-style left-nav) ────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAppSection))]
    [NotifyPropertyChangedFor(nameof(IsEmulatorSection))]
    [NotifyPropertyChangedFor(nameof(IsPlaytimeSection))]
    [NotifyPropertyChangedFor(nameof(IsSyncSection))]
    private string _selectedSection = "app";

    public bool IsAppSection      => SelectedSection == "app";
    public bool IsEmulatorSection => SelectedSection == "emulator";
    public bool IsPlaytimeSection => SelectedSection == "playtime";
    public bool IsSyncSection     => SelectedSection == "sync";

    [RelayCommand]
    private void SelectSection(string section) => SelectedSection = section;

    // ── Sync section ───────────────────────────────────────────────────────
    /// <summary>True while a manual sync is in progress.</summary>
    [ObservableProperty] private bool   _isSyncing;
    /// <summary>Human-readable label for the last successful sync time, e.g. "Last synced: 3 minutes ago".</summary>
    [ObservableProperty] private string _lastSyncedLabel = "Last synced: never";

    /// <summary>
    /// Wired by MainViewModel.  Executes an immediate full sync and updates
    /// <see cref="IsSyncing"/> / <see cref="LastSyncedLabel"/>.
    /// </summary>
    public System.Func<System.Threading.Tasks.Task>? SyncNowAction { get; set; }

    [RelayCommand]
    private async System.Threading.Tasks.Task SyncNow()
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
        AutoUpdate     = appSettings.AutoUpdate;
        ShowIntroVideo = appSettings.ShowIntroVideo;
        IntroVideoPath = appSettings.IntroVideoPath;
        ReadSwitchLog  = appSettings.ReadSwitchLog;
        DevLogs        = appSettings.DevLogs;

        StatusMessage = "";
    }

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
            AutoUpdate     = AutoUpdate,
            ShowIntroVideo = ShowIntroVideo,
            IntroVideoPath = IntroVideoPath,
            ReadSwitchLog  = ReadSwitchLog,
            DevLogs        = DevLogs,
        });

        StatusMessage = "✅ Settings saved!";
        IsSaveSuccess = true;
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
