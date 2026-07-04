using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GameLauncher.Services;
using GameLauncher.ViewModels;
using LibVLCSharp.Shared;
using System;
using System.IO;

namespace GameLauncher.Views;

public partial class GameDetailView : UserControl
{
    // ── VLC local file player state ───────────────────────────────────────────
    private LibVLC?      _libVlc;
    private MediaPlayer? _mediaPlayer;
    private Media?       _media;

    public GameDetailView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded           += OnUnloaded;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is GameDetailViewModel vm)
        {
            vm.BrowseLaunchPathRequested  = OnBrowseLaunchPathRequested;
            vm.PlayLocalVideoRequested    = OnPlayLocalVideoRequested;
        }
    }

    private void OnUnloaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        DisposeVlc();
    }

    // ── Executable browse ────────────────────────────────────────────────────

    private async void OnBrowseLaunchPathRequested(Action<string> onPicked)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title         = "Select executable or script",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Executable / Script")
                    {
                        Patterns = new[] { "*.exe", "*.bat", "*.cmd", "*.sh", "*.ps1", "*.AppImage" },
                    },
                    FilePickerFileTypes.All,
                },
            });

        if (files.Count > 0)
            onPicked(files[0].Path.LocalPath);
    }

    // ── VLC local file player ────────────────────────────────────────────────

    /// <summary>
    /// Called by the ViewModel's <see cref="GameDetailViewModel.PlayLocalVideoCommand"/>.
    /// Shows a file picker, then plays the selected video file inside the VLC overlay.
    /// </summary>
    private async void OnPlayLocalVideoRequested()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title         = "Select a video file to play",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Video files")
                    {
                        Patterns = new[]
                        {
                            "*.mp4", "*.mkv", "*.avi", "*.mov", "*.wmv",
                            "*.flv", "*.webm", "*.m4v", "*.ts", "*.m2ts",
                        },
                    },
                    FilePickerFileTypes.All,
                },
            });

        if (files.Count == 0) return;
        string path = files[0].Path.LocalPath;
        if (!File.Exists(path)) return;

        if (DataContext is not GameDetailViewModel vm) return;

        // Stop any running playback before starting a new one.
        DisposeVlc();

        try
        {
            string? vlcDir = IntroWindow.FindBundledLibVlcDirectoryPublic();
            if (!string.IsNullOrEmpty(vlcDir))
                Core.Initialize(vlcDir);
            else
                Core.Initialize();

            _libVlc      = new LibVLC(enableDebugLogs: false);
            _mediaPlayer = new MediaPlayer(_libVlc);
            _mediaPlayer.EndReached       += OnVlcEndReached;
            _mediaPlayer.EncounteredError += OnVlcError;
            _media = new Media(_libVlc, new Uri(path));

            vm.VlcMediaPath    = Path.GetFileName(path);
            vm.IsVlcPlayerOpen = true;

            // Attach to the VideoView AFTER the overlay is shown (next layout pass).
            Dispatcher.UIThread.Post(() =>
            {
                if (VlcOverlayVideoView != null)
                    VlcOverlayVideoView.MediaPlayer = _mediaPlayer;
                _mediaPlayer?.Play(_media);
            }, DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            DevLogService.Log($"[GameDetailView] VLC init failed: {ex.GetType().Name}: {ex.Message}");
            DisposeVlc();
        }
    }

    private void OnVlcEndReached(object? sender, EventArgs e)
    {
        System.Threading.Tasks.Task.Run(() =>
            Dispatcher.UIThread.Post(CloseVlcOverlay));
    }

    private void OnVlcError(object? sender, EventArgs e)
    {
        System.Threading.Tasks.Task.Run(() =>
            Dispatcher.UIThread.Post(CloseVlcOverlay));
    }

    private void CloseVlcOverlay()
    {
        DisposeVlc();
        if (DataContext is GameDetailViewModel vm)
            vm.CloseVlcPlayerCommand.Execute(null);
    }

    private void DisposeVlc()
    {
        if (_mediaPlayer != null)
        {
            _mediaPlayer.EndReached       -= OnVlcEndReached;
            _mediaPlayer.EncounteredError -= OnVlcError;
            try { _mediaPlayer.Stop(); } catch { }
            _mediaPlayer.Dispose();
            _mediaPlayer = null;
        }
        _media?.Dispose();
        _media = null;
        _libVlc?.Dispose();
        _libVlc = null;
    }
}
