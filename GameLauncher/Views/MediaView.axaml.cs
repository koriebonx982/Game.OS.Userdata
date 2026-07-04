using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GameLauncher.Services;
using GameLauncher.ViewModels;
using LibVLCSharp.Shared;
using System;
using System.IO;

namespace GameLauncher.Views;

public partial class MediaView : UserControl
{
    // ── VLC local file player state ───────────────────────────────────────────
    private LibVLC?      _libVlc;
    private MediaPlayer? _mediaPlayer;
    private Media?       _media;

    public MediaView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded           += OnUnloaded;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MediaViewModel vm)
        {
            vm.PlayLocalVideoRequested = OnPlayLocalVideoRequested;
            vm.PlayMediaItemRequested = OnPlayMediaItemRequested;
        }
    }

    private void OnUnloaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        DisposeVlc();
    }

    // ── VLC local file player ────────────────────────────────────────────────

    private async void OnPlayLocalVideoRequested()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title         = "Select a media file to play",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Video / Audio files")
                    {
                        Patterns = new[]
                        {
                            "*.mp4", "*.mkv", "*.avi", "*.mov", "*.wmv",
                            "*.flv", "*.webm", "*.m4v", "*.ts", "*.m2ts",
                            "*.mp3", "*.flac", "*.aac", "*.ogg", "*.wav", "*.m4a",
                        },
                    },
                    FilePickerFileTypes.All,
                },
            });

        if (files.Count == 0) return;
        string path = files[0].Path.LocalPath;
        if (!File.Exists(path)) return;
        StartPlayback(path);
    }

    private void OnPlayMediaItemRequested(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        StartPlayback(path);
    }

    private void StartPlayback(string path)
    {
        if (DataContext is not MediaViewModel vm) return;

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
            DevLogService.Log($"[MediaView] VLC init failed: {ex.GetType().Name}: {ex.Message}");
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
        if (DataContext is MediaViewModel vm)
            vm.CloseVlcPlayerCommand.Execute(null);
    }

    private void DisposeVlc()
    {
        if (_mediaPlayer != null)
        {
            _mediaPlayer.EndReached       -= OnVlcEndReached;
            _mediaPlayer.EncounteredError -= OnVlcError;
            try { _mediaPlayer.Stop(); } catch (Exception ex) { DevLogService.Log($"[MediaView] VLC stop: {ex.Message}"); }
            _mediaPlayer.Dispose();
            _mediaPlayer = null;
        }
        _media?.Dispose();
        _media = null;
        _libVlc?.Dispose();
        _libVlc = null;
    }
}
