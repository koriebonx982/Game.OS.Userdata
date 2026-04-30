using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using GameLauncher.Services;
using LibVLCSharp.Shared;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace GameLauncher.Views;

public partial class IntroWindow : Window
{
    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private Media? _media;
    private bool _finished;

    public IntroWindow()
    {
        InitializeComponent();
        Opened  += OnOpened;
        Closing += OnClosing;
        Closed  += OnClosed;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;

        var settings = AppSettingsService.Load();
        var path     = settings.IntroVideoPath;

        DevLogService.Log($"[IntroWindow] Opened. ShowIntroVideo={settings.ShowIntroVideo}  IntroVideoPath='{path}'");

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            DevLogService.Log(string.IsNullOrEmpty(path)
                ? "[IntroWindow] IntroVideoPath is empty — finishing intro immediately."
                : $"[IntroWindow] Video file not found at '{path}' — finishing intro immediately.");
            FinishIntro();
            return;
        }

        DevLogService.Log($"[IntroWindow] Video file confirmed at '{path}'. Initialising VLC…");

        try
        {
            // Provide the app directory so VLC can locate its native DLLs even
            // when the working directory differs from the executable's location.
            var appDir = AppContext.BaseDirectory;
            DevLogService.Log($"[IntroWindow] App directory for VLC search: '{appDir}'");

            if (!string.IsNullOrEmpty(appDir) && Directory.Exists(appDir))
            {
                try
                {
                    DevLogService.Log($"[IntroWindow] Calling Core.Initialize('{appDir}')…");
                    Core.Initialize(appDir);
                    DevLogService.Log("[IntroWindow] Core.Initialize(appDir) succeeded.");
                }
                catch (Exception initEx)
                {
                    DevLogService.Log($"[IntroWindow] Core.Initialize(appDir) failed: {initEx.Message} — falling back to Core.Initialize().");
                    Core.Initialize();
                    DevLogService.Log("[IntroWindow] Core.Initialize() fallback succeeded.");
                }
            }
            else
            {
                DevLogService.Log("[IntroWindow] appDir is empty or missing — calling Core.Initialize().");
                Core.Initialize();
                DevLogService.Log("[IntroWindow] Core.Initialize() succeeded.");
            }

            DevLogService.Log("[IntroWindow] Creating LibVLC instance…");
            _libVlc      = new LibVLC(enableDebugLogs: false);
            DevLogService.Log("[IntroWindow] LibVLC created. Creating MediaPlayer…");
            _mediaPlayer = new MediaPlayer(_libVlc);

            _mediaPlayer.EndReached       += OnEndReached;
            _mediaPlayer.EncounteredError += OnEncounteredError;

            // Keep a reference to the Media so it isn't disposed before VLC
            // finishes reading it (disposing immediately after Play() can cut
            // off playback on some builds).
            DevLogService.Log($"[IntroWindow] Opening media: {path}");
            _media = new Media(_libVlc, new Uri(path));

            // Attach the media player after the first layout pass so the
            // VideoView has a valid native window handle.
            Dispatcher.UIThread.Post(() =>
            {
                DevLogService.Log("[IntroWindow] Attaching MediaPlayer to VideoView and starting playback.");
                IntroVideoView.MediaPlayer = _mediaPlayer;
                _mediaPlayer.Play(_media);
            }, Avalonia.Threading.DispatcherPriority.Loaded);
        }
        catch (Exception ex)
        {
            DevLogService.Log($"[IntroWindow] VLC initialisation FAILED: {ex.GetType().Name}: {ex.Message}");
            Debug.WriteLine($"[IntroWindow] Failed to start playback: {ex.Message}");
            FinishIntro();
        }
    }

    private void OnEndReached(object? sender, EventArgs e)
    {
        DevLogService.Log("[IntroWindow] Playback ended (EndReached).");
        // VLC fires this on a background thread; marshal to the UI thread.
        Dispatcher.UIThread.Post(FinishIntro);
    }

    private void OnEncounteredError(object? sender, EventArgs e)
    {
        DevLogService.Log("[IntroWindow] VLC EncounteredError — finishing intro.");
        Dispatcher.UIThread.Post(FinishIntro);
    }

    // Prevent the user from closing the intro while it is playing.
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_finished)
            e.Cancel = true;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        DisposeVlc();
    }

    private void FinishIntro()
    {
        if (_finished) return;
        _finished = true;

        DevLogService.Log("[IntroWindow] FinishIntro — stopping player and transitioning to main window.");

        try { _mediaPlayer?.Stop(); }
        catch (ObjectDisposedException) { /* already cleaned up */ }

        DisposeVlc();

        // Show the main window fullscreen, then close the intro overlay.
        if (Application.Current?.ApplicationLifetime is
            IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow is { } main)
        {
            main.WindowState = WindowState.FullScreen;
            main.Show();
            main.Activate();
            DevLogService.Log("[IntroWindow] Main window shown fullscreen.");
        }

        Close();
    }

    private void DisposeVlc()
    {
        if (_mediaPlayer != null)
        {
            _mediaPlayer.EndReached       -= OnEndReached;
            _mediaPlayer.EncounteredError -= OnEncounteredError;
            _mediaPlayer.Dispose();
            _mediaPlayer = null;
        }

        _media?.Dispose();
        _media = null;

        _libVlc?.Dispose();
        _libVlc = null;
    }
}
