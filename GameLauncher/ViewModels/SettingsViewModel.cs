using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
            AutoUpdate = AutoUpdate,
            ShowIntroVideo = ShowIntroVideo,
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

    /// <summary>Raised when the user clicks Browse… on an emulator row.</summary>
    public System.Action<EmulatorRowVm>? BrowseRequested { get; set; }
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
