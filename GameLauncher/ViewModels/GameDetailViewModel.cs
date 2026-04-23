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
    [ObservableProperty] private bool _isExtracting;
    /// <summary>Status message shown in the main info panel during/after archive installation (not the settings panel).</summary>
    [ObservableProperty] private string _installStatusMessage = "";
    /// <summary>True when <see cref="InstallStatusMessage"/> represents an error (drives red foreground in the UI).</summary>
    [ObservableProperty] private bool _installStatusIsError;
    /// <summary>Foreground colour for <see cref="InstallStatusMessage"/>: red on error, green on success.</summary>
    public string InstallStatusForeground => InstallStatusIsError ? "#f85149" : "#3fb950";

    partial void OnInstallStatusIsErrorChanged(bool value) => OnPropertyChanged(nameof(InstallStatusForeground));

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

    public ObservableCollection<string> DriveLabels { get; } = new();

    private List<LocalGameDriveEntry> _driveInstances = new();

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
    [ObservableProperty] private string _newPreLaunchPath  = "";
    [ObservableProperty] private string _newPreLaunchArgs  = "";
    [ObservableProperty] private string _newPreLaunchLabel = "";

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

        NewPreLaunchPath    = "";
        NewPreLaunchArgs    = "";
        NewPreLaunchLabel   = "";
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
            Label     = string.IsNullOrWhiteSpace(NewPreLaunchLabel)
                            ? System.IO.Path.GetFileName(NewPreLaunchPath.Trim())
                            : NewPreLaunchLabel.Trim(),
            Path      = NewPreLaunchPath.Trim(),
            Arguments = string.IsNullOrWhiteSpace(NewPreLaunchArgs) ? null : NewPreLaunchArgs.Trim(),
        });
        NewPreLaunchPath  = "";
        NewPreLaunchArgs  = "";
        NewPreLaunchLabel = "";
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

    /// <summary>Opens the trailer URL in the system's default browser.</summary>
    [RelayCommand]
    private void OpenTrailer()
    {
        if (string.IsNullOrEmpty(TrailerUrl)) return;
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

        // Load saved settings to get the preferred exe path / arguments
        var saved = GameSettingsService.Load(Title);

        // Run pre-launch entries first (fire-and-forget, best-effort)
        foreach (var pre in saved.PreLaunch)
            TryStartProcess(pre.Path, pre.Arguments);

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

                        // Track playtime for ROM games through the emulator process
                        if (romProc != null && OnRequestPlaytimeTracking != null)
                            OnRequestPlaytimeTracking(romProc, Title, Platform);
                    }
                    catch { /* best-effort */ }

                    if (saved.PostLaunch.Count > 0)
                        _ = WatchAndRunPostLaunchAsync(romProc, saved.PostLaunch);
                    return;
                }

                // No emulator configured — open ROM file with system handler as fallback
                OpenWithSystem(romPath);
            }
            return;
        }

        // ── Regular (PC) game launch ──────────────────────────────────────────
        // Determine the executable to launch:
        // Priority: saved settings ExePath → detected drive entry → open folder
        string? exePath = null;
        string? exeArgs = string.IsNullOrWhiteSpace(saved.ExeArgs) ? null : saved.ExeArgs;

        if (!string.IsNullOrEmpty(saved.ExePath) && System.IO.File.Exists(saved.ExePath))
        {
            exePath = saved.ExePath;
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

                // Track playtime for PC games
                if (gameProc != null && OnRequestPlaytimeTracking != null)
                    OnRequestPlaytimeTracking(gameProc, Title, Platform);
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

    private static void TryStartProcess(string path, string? args)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
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
        catch { /* best-effort */ }
    }

    private static async System.Threading.Tasks.Task WatchAndRunPostLaunchAsync(
        System.Diagnostics.Process? gameProc, List<LaunchEntry> postEntries)
    {
        if (gameProc != null)
        {
            try
            {
                // Wait up to 24 hours for the game process to exit
                using var cts = new System.Threading.CancellationTokenSource(
                    System.TimeSpan.FromHours(24));
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
        LoadSwitchMods();
    }
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Load a store game into the detail view.
    /// </summary>
    /// <param name="game">The store entry.</param>
    /// <param name="localGame">If not null, the game is installed — shows Play + ··· buttons.</param>
    /// <param name="repack">If not null (and localGame is null), a repack is available — shows Install button.</param>
    public void LoadFromStoreGame(StoreGame game, LocalGame? localGame = null, LocalRepack? repack = null,
                                  LocalRom? localRom = null)
    {
        ShowSettings    = false;
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
        ShowDrivePicker = false;
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
    }
    // ─────────────────────────────────────────────────────────────────────────

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
    }

    public void LoadFromLocalRom(LocalRom rom)
    {
        ShowSettings    = false;
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
    /// Fetches the achievements JSON from the given URL and populates the
    /// Achievements collection.  Mirrors <c>_loadAchievementsInModal</c> in script.js.
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
            using var http = new System.Net.Http.HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("GameOS-Launcher/2.0");
            var json = await http.GetStringAsync(url);
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
        TrailerLabel = HasTrailer ? "▶  Watch Trailer on YouTube" : "▶  Watch Trailer";
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
        _ryujinxModsJsonPath    = null;

        IsSwitch = string.Equals(Platform, "Switch", StringComparison.OrdinalIgnoreCase);
        if (!IsSwitch) return;

        // TitleID comes from the current ROM (if any)
        string? titleId = _currentLocalRom?.TitleId;
        if (string.IsNullOrEmpty(titleId)) return;

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
    /// Creates the folder and an empty <c>mods.json</c> if they do not yet exist, then
    /// refreshes the mods panel so the updated state is immediately reflected in the UI.
    /// Path: <c>{ryujinxDir}\portable\games\{titleId}\mods.json</c> (portable mode) or
    /// <c>%APPDATA%\Ryujinx\games\{titleId}\mods.json</c> (standard mode).
    /// </summary>
    [RelayCommand]
    private void OpenSwitchModsFolder()
    {
        string? titleId = _currentLocalRom?.TitleId;
        if (string.IsNullOrEmpty(titleId)) return;

        var emuSettings = Services.EmulatorSettingsService.Load("Switch");
        if (string.IsNullOrEmpty(emuSettings.EmulatorPath)) return;

        string modsJsonPath = Services.RyujinxModService.GetDefaultModsJsonPath(emuSettings.EmulatorPath, titleId);
        string modsDir = System.IO.Path.GetDirectoryName(modsJsonPath) ?? "";
        if (string.IsNullOrEmpty(modsDir)) return;

        try
        {
            Directory.CreateDirectory(modsDir);

            // Create an empty mods.json if it does not exist yet so Ryujinx can
            // recognise the folder and the mods panel can read/update the file.
            if (!File.Exists(modsJsonPath))
                Services.RyujinxModService.SaveMods(modsJsonPath,
                    new System.Collections.Generic.List<GameLauncher.Models.RyujinxMod>());
        }
        catch { /* best-effort */ }

        // Reload the panel to reflect the newly created (or existing) mods.json.
        LoadSwitchMods();
        ShowModsPanel = true;

        OpenWithSystem(modsDir);
    }
}
