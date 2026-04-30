using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
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

        try
        {
            Core.Initialize();

            _libVlc      = new LibVLC(enableDebugLogs: false);
            _mediaPlayer = new MediaPlayer(_libVlc);

            // Attach the media player to the VideoView so it renders inside the window.
            IntroVideoView.MediaPlayer = _mediaPlayer;

            _mediaPlayer.EndReached       += OnEndReached;
            _mediaPlayer.EncounteredError += OnEncounteredError;

            var settings = Services.AppSettingsService.Load();
            var path     = settings.IntroVideoPath;

            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                // Keep a reference to the Media so it isn't disposed before VLC
                // finishes reading it (disposing immediately after Play() can cut
                // off playback on some builds).
                _media = new Media(_libVlc, new Uri(path));
                _mediaPlayer.Play(_media);
            }
            else
            {
                // No valid video path — skip straight to main window.
                FinishIntro();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[IntroWindow] Failed to start playback: {ex.Message}");
            FinishIntro();
        }
    }

    private void OnEndReached(object? sender, EventArgs e)
    {
        // VLC fires this on a background thread; marshal to the UI thread.
        Dispatcher.UIThread.Post(FinishIntro);
    }

    private void OnEncounteredError(object? sender, EventArgs e)
    {
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
