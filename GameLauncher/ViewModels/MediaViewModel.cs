using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace GameLauncher.ViewModels;

public partial class MediaViewModel : ObservableObject
{
    public sealed class MediaLibraryItem
    {
        public string Title { get; init; } = "";
        public string FullPath { get; init; } = "";
        public string SourceFolder { get; init; } = "";
        public string MediaType { get; init; } = "";
        public string Extension { get; init; } = "";
        public long FileSizeBytes { get; init; }
    }

    // ── VLC local file player ─────────────────────────────────────────────────
    /// <summary>True when the in-app VLC file-player overlay is visible.</summary>
    [ObservableProperty] private bool   _isVlcPlayerOpen;
    /// <summary>File name of the media currently loaded in the VLC player.</summary>
    [ObservableProperty] private string _vlcMediaPath = "";

    /// <summary>
    /// Delegate set by the View code-behind. When invoked, the View shows a file
    /// picker and—once the user selects a file—starts VLC playback in the overlay.
    /// </summary>
    public Action? PlayLocalVideoRequested { get; set; }
    public Action<string>? PlayMediaItemRequested { get; set; }

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".webm", ".flv", ".mpeg", ".mpg", ".ts"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".aac", ".ogg", ".m4a", ".wma", ".alac", ".opus"
    };

    private readonly ObservableCollection<MediaLibraryItem> _movies = new();
    private readonly ObservableCollection<MediaLibraryItem> _tvShows = new();
    private readonly ObservableCollection<MediaLibraryItem> _music = new();
    private DateTime _lastScanUtc = DateTime.MinValue;
    private static readonly TimeSpan ScanCacheDuration = TimeSpan.FromMinutes(2);

    public ReadOnlyObservableCollection<MediaLibraryItem> Movies { get; }
    public ReadOnlyObservableCollection<MediaLibraryItem> TvShows { get; }
    public ReadOnlyObservableCollection<MediaLibraryItem> Music { get; }

    public IEnumerable<MediaLibraryItem> ActiveItems => SelectedMediaSection switch
    {
        "movies" => Movies,
        "tvshows" => TvShows,
        _ => Music
    };

    public string ActiveSectionTitle => SelectedMediaSection switch
    {
        "movies" => "Movies",
        "tvshows" => "TV Shows",
        _ => "Music"
    };

    public string ActiveSectionSummary => SelectedMediaSection switch
    {
        "movies" => $"{Movies.Count} item(s) found",
        "tvshows" => $"{TvShows.Count} item(s) found",
        _ => $"{Music.Count} item(s) found"
    };

    public bool IsMoviesSelected => SelectedMediaSection == "movies";
    public bool IsTvShowsSelected => SelectedMediaSection == "tvshows";
    public bool IsMusicSelected => SelectedMediaSection == "music";
    public bool HasSelectedItem => SelectedItem != null;
    public string SelectedItemTitle => SelectedItem?.Title ?? "Select media";
    public string SelectedItemPath => SelectedItem?.FullPath ?? "Pick a media card to view details.";
    public string SelectedItemTypeLabel =>
        SelectedItem is null
            ? ""
            : $"{SelectedItem.MediaType.ToUpperInvariant()} · {SelectedItem.Extension.ToUpperInvariant().TrimStart('.')}";
    public string SelectedItemSizeLabel => SelectedItem is null ? "" : FormatFileSize(SelectedItem.FileSizeBytes);
    public string SelectedItemActionLabel => SelectedItem?.MediaType == "music" ? "▶ Play Track" : "▶ Play";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedItem))]
    [NotifyPropertyChangedFor(nameof(SelectedItemTitle))]
    [NotifyPropertyChangedFor(nameof(SelectedItemPath))]
    [NotifyPropertyChangedFor(nameof(SelectedItemTypeLabel))]
    [NotifyPropertyChangedFor(nameof(SelectedItemSizeLabel))]
    [NotifyPropertyChangedFor(nameof(SelectedItemActionLabel))]
    private MediaLibraryItem? _selectedItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMoviesSelected))]
    [NotifyPropertyChangedFor(nameof(IsTvShowsSelected))]
    [NotifyPropertyChangedFor(nameof(IsMusicSelected))]
    [NotifyPropertyChangedFor(nameof(ActiveItems))]
    [NotifyPropertyChangedFor(nameof(ActiveSectionTitle))]
    [NotifyPropertyChangedFor(nameof(ActiveSectionSummary))]
    private string _selectedMediaSection = "movies";

    partial void OnSelectedMediaSectionChanged(string value)
    {
        EnsureSelectedItemForActiveSection();
    }

    public MediaViewModel()
    {
        Movies = new ReadOnlyObservableCollection<MediaLibraryItem>(_movies);
        TvShows = new ReadOnlyObservableCollection<MediaLibraryItem>(_tvShows);
        Music = new ReadOnlyObservableCollection<MediaLibraryItem>(_music);
        RefreshMediaLibrary();
    }

    public void OpenSection(string section)
    {
        SelectedMediaSection = NormalizeSection(section);
        EnsureMediaLibraryFresh();
        EnsureSelectedItemForActiveSection();
    }

    public void EnsureMediaLibraryFresh()
    {
        if (DateTime.UtcNow - _lastScanUtc > ScanCacheDuration)
            RefreshMediaLibrary();
        else
        {
            OnPropertyChanged(nameof(ActiveItems));
            OnPropertyChanged(nameof(ActiveSectionSummary));
        }
        EnsureSelectedItemForActiveSection();
    }

    [RelayCommand]
    private void SelectMovies() => SelectedMediaSection = "movies";

    [RelayCommand]
    private void SelectTvShows() => SelectedMediaSection = "tvshows";

    [RelayCommand]
    private void SelectMusic() => SelectedMediaSection = "music";

    [RelayCommand]
    private void SelectMediaItem(MediaLibraryItem? item)
    {
        if (item == null) return;
        SelectedItem = item;
    }

    [RelayCommand]
    private void PlaySelectedMedia()
    {
        if (SelectedItem == null) return;
        PlayMediaItemRequested?.Invoke(SelectedItem.FullPath);
    }

    [RelayCommand]
    private void RefreshMediaLibrary()
    {
        _movies.Clear();
        _tvShows.Clear();
        _music.Clear();

        var knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = GameScannerService.GetDriveRoots()
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var driveRoot in roots)
        {
            TryCollectMediaCategory(driveRoot, _movies, VideoExtensions, knownPaths, "Movies");
            TryCollectMediaCategory(driveRoot, _music, AudioExtensions, knownPaths, "Music");
            TryCollectMediaCategory(driveRoot, _tvShows, VideoExtensions, knownPaths, "TV Shows");
        }

        _lastScanUtc = DateTime.UtcNow;
        OnPropertyChanged(nameof(ActiveItems));
        OnPropertyChanged(nameof(ActiveSectionSummary));
        EnsureSelectedItemForActiveSection();
    }

    private static string NormalizeSection(string? section) =>
        section?.Trim().ToLowerInvariant() switch
        {
            "movies" => "movies",
            "tvshows" or "tv shows" or "tv" => "tvshows",
            _ => "music"
        };

    /// <summary>
    /// Scans each drive's Media folder for subdirectories matching any provided
    /// category name and adds files with allowed extensions into <paramref name="destination"/>.
    /// </summary>
    private static void TryCollectMediaCategory(
        string driveRoot,
        ICollection<MediaLibraryItem> destination,
        HashSet<string> allowedExtensions,
        HashSet<string> knownPaths,
        params string[] categoryFolderNames)
    {
        string mediaRoot = Path.Combine(driveRoot, "Media");
        if (!Directory.Exists(mediaRoot)) return;

        IEnumerable<string> categoryDirs;
        try
        {
            categoryDirs = Directory
                .EnumerateDirectories(mediaRoot)
                .Where(path => categoryFolderNames.Contains(
                    Path.GetFileName(path),
                    StringComparer.OrdinalIgnoreCase))
                .ToList();
        }
        catch (UnauthorizedAccessException) { return; }
        catch (IOException) { return; }

        foreach (var mediaFolder in categoryDirs)
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(mediaFolder, "*", SearchOption.AllDirectories); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var file in files)
            {
                string extension = Path.GetExtension(file);
                if (!allowedExtensions.Contains(extension)) continue;
                if (!knownPaths.Add(file)) continue;

                destination.Add(new MediaLibraryItem
                {
                    Title = Path.GetFileName(file),
                    FullPath = file,
                    SourceFolder = mediaFolder,
                    MediaType = string.Equals(Path.GetFileName(mediaFolder), "Music", StringComparison.OrdinalIgnoreCase)
                        ? "music"
                        : "video",
                    Extension = extension,
                    FileSizeBytes = SafeGetFileSize(file)
                });
            }
        }
    }

    private void EnsureSelectedItemForActiveSection()
    {
        var active = ActiveItems.ToList();
        if (active.Count == 0)
        {
            SelectedItem = null;
            return;
        }

        if (SelectedItem == null || !active.Any(item =>
                string.Equals(item.FullPath, SelectedItem.FullPath, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedItem = active[0];
        }
    }

    private static long SafeGetFileSize(string filePath)
    {
        try
        {
            return new FileInfo(filePath).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0 ? $"{size:0} {units[unit]}" : $"{size:0.##} {units[unit]}";
    }

    // ── VLC commands ─────────────────────────────────────────────────────────

    [RelayCommand]
    private void PlayLocalVideo()
    {
        DevLogService.Log("[MediaViewModel] PlayLocalVideo requested");
        PlayLocalVideoRequested?.Invoke();
    }

    [RelayCommand]
    private void CloseVlcPlayer()
    {
        IsVlcPlayerOpen = false;
        VlcMediaPath    = "";
    }
}
