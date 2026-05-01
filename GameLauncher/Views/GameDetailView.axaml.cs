using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using GameLauncher.Services;
using GameLauncher.ViewModels;
using LibVLCSharp.Shared;

namespace GameLauncher.Views;

public partial class GameDetailView : UserControl
{
    // ── LibVLC trailer player ─────────────────────────────────────────────────
    // VLC is only initialised once per process; re-use the same flag as the
    // intro window so we never call Core.Initialize() twice.
    private static volatile bool _vlcCoreInitialized;

    private LibVLC?       _libVlc;
    private MediaPlayer?  _mediaPlayer;
    private Media?        _media;

    public GameDetailView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private GameDetailViewModel? ViewModel => DataContext as GameDetailViewModel;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (ViewModel != null)
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GameDetailViewModel.IsTrailerPlayerOpen))
        {
            if (ViewModel?.IsTrailerPlayerOpen == true)
                Dispatcher.UIThread.Post(StartTrailerPlayback, DispatcherPriority.Background);
            else
                StopTrailerPlayback();
        }
    }

    private void StartTrailerPlayback()
    {
        var vm = ViewModel;
        if (vm == null || string.IsNullOrEmpty(vm.YoutubeVideoId)) return;

        try
        {
            // Initialise VLC core once per process
            if (!_vlcCoreInitialized)
            {
                var appDir = AppContext.BaseDirectory;
                if (!string.IsNullOrEmpty(appDir) && Directory.Exists(appDir)
                    && File.Exists(System.IO.Path.Combine(appDir, "libvlc.dll")))
                    Core.Initialize(appDir);
                else
                    Core.Initialize();

                _vlcCoreInitialized = true;
            }

            StopTrailerPlayback(); // clean up any previous instance

            _libVlc = new LibVLC(enableDebugLogs: false);
            _mediaPlayer = new MediaPlayer(_libVlc);

            _mediaPlayer.EndReached       += OnTrailerEndReached;
            _mediaPlayer.EncounteredError += OnTrailerError;

            // Build the YouTube URL; VLC's youtube.lua script will resolve the stream.
            var youtubeUrl = $"https://www.youtube.com/watch?v={vm.YoutubeVideoId}";
            _media = new Media(_libVlc, new Uri(youtubeUrl));

            if (TrailerVideoView != null)
                TrailerVideoView.MediaPlayer = _mediaPlayer;

            _mediaPlayer.Play(_media);
        }
        catch (Exception ex)
        {
            DevLogService.Log($"[TrailerPlayer] VLC init failed: {ex.Message}. Falling back to browser.");
            StopTrailerPlayback();
            FallbackToBrowser();
        }
    }

    private void StopTrailerPlayback()
    {
        if (_mediaPlayer != null)
        {
            _mediaPlayer.EndReached       -= OnTrailerEndReached;
            _mediaPlayer.EncounteredError -= OnTrailerError;
            try { _mediaPlayer.Stop(); } catch { /* best-effort */ }
            _mediaPlayer.Dispose();
            _mediaPlayer = null;
        }
        _media?.Dispose();  _media   = null;
        _libVlc?.Dispose(); _libVlc  = null;
    }

    private void OnTrailerEndReached(object? sender, EventArgs e)
    {
        // VLC fires this on an internal thread; marshal to UI
        Task.Run(() => Dispatcher.UIThread.Post(() =>
        {
            StopTrailerPlayback();
            if (ViewModel != null) ViewModel.IsTrailerPlayerOpen = false;
        }));
    }

    private void OnTrailerError(object? sender, EventArgs e)
    {
        DevLogService.Log("[TrailerPlayer] VLC encountered an error — falling back to browser.");
        Task.Run(() => Dispatcher.UIThread.Post(() =>
        {
            StopTrailerPlayback();
            if (ViewModel != null) ViewModel.IsTrailerPlayerOpen = false;
            FallbackToBrowser();
        }));
    }

    private void FallbackToBrowser()
    {
        var url = ViewModel?.TrailerUrl;
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = url,
                UseShellExecute = true
            });
        }
        catch { /* best-effort */ }
    }
}
