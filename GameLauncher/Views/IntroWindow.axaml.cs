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
using System.Threading;
using System.Threading.Tasks;

namespace GameLauncher.Views;

public partial class IntroWindow : Window
{
    // Guard so Core.Initialize is only ever called once per process.
    // volatile ensures the write is visible to all threads without a lock.
    private static volatile bool _vlcCoreInitialized;

    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private Media? _media;
    private bool _finished;

    // Safety timer: if VLC doesn't start playback within 8 seconds, skip intro.
    private CancellationTokenSource? _timeoutCts;

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

        // Start a safety timeout so a failed VLC init never leaves the user stuck
        // on a blank intro window. 8 seconds is well above typical VLC startup time.
        _timeoutCts = new CancellationTokenSource();
        var timeoutToken = _timeoutCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(8000, timeoutToken).ConfigureAwait(false);
                DevLogService.Log("[IntroWindow] VLC startup timeout reached — forcing FinishIntro.");
                Dispatcher.UIThread.Post(FinishIntro);
            }
            catch (OperationCanceledException) { /* playback started in time */ }
        }, timeoutToken);

        try
        {
            if (!_vlcCoreInitialized)
            {
                // Resolve where VLC's native DLLs live.
                // For framework-dependent builds the DLLs sit alongside the exe in
                // AppContext.BaseDirectory; we pass that path explicitly.
                // For self-contained single-file builds the .NET runtime embeds the
                // DLLs and extracts them at startup to a per-version temp directory
                // (%TEMP%\.net\<App>\<Hash>\).  In that case libvlc.dll is NOT in the
                // exe directory, so we call Core.Initialize() without a path and let
                // LibVLCSharp's NativeLibrary resolver (which covers the extraction
                // directory) locate the DLLs automatically.
                var appDir = AppContext.BaseDirectory;
                DevLogService.Log($"[IntroWindow] App directory for VLC search: '{appDir}'");

                if (!string.IsNullOrEmpty(appDir) && Directory.Exists(appDir)
                    && File.Exists(Path.Combine(appDir, "libvlc.dll")))
                {
                    // VLC DLLs are physically present alongside the exe
                    // (framework-dependent build or manually-placed DLLs).
                    DevLogService.Log($"[IntroWindow] libvlc.dll found in appDir — calling Core.Initialize('{appDir}')…");
                    Core.Initialize(appDir);
                    DevLogService.Log("[IntroWindow] Core.Initialize(appDir) succeeded.");
                }
                else
                {
                    // VLC DLLs are not in the exe directory.  This is expected for
                    // self-contained single-file builds: the .NET runtime extracts
                    // native libraries to a temp directory (%TEMP%\.net\<App>\<Hash>\)
                    // and registers a NativeLibrary resolver that covers those paths.
                    // Calling Core.Initialize() without a path lets LibVLCSharp use
                    // that resolver (and the OS DLL search path) to locate libvlc.dll.
                    // It also handles the case where VLC is installed system-wide.
                    DevLogService.Log("[IntroWindow] libvlc.dll not found in appDir — calling Core.Initialize() with default search (single-file bundle or system VLC).");
                    Core.Initialize();
                    DevLogService.Log("[IntroWindow] Core.Initialize() succeeded.");
                }

                _vlcCoreInitialized = true;
            }
            else
            {
                DevLogService.Log("[IntroWindow] Core already initialized — skipping Core.Initialize().");
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

            // Attach the media player after the first render pass so the
            // VideoView has a valid native window handle.
            // DispatcherPriority.Background fires after layout AND rendering,
            // guaranteeing the native drawable surface is ready for VLC.
            Dispatcher.UIThread.Post(() =>
            {
                DevLogService.Log("[IntroWindow] Attaching MediaPlayer to VideoView and starting playback.");
                IntroVideoView.MediaPlayer = _mediaPlayer;
                bool started = _mediaPlayer.Play(_media);
                DevLogService.Log($"[IntroWindow] MediaPlayer.Play() returned {started}.");
                if (!started)
                {
                    DevLogService.Log("[IntroWindow] Play() returned false — finishing intro immediately.");
                    FinishIntro();
                }
                else
                {
                    // Cancel the timeout — playback started successfully.
                    try { _timeoutCts?.Cancel(); }
                    catch (ObjectDisposedException) { }
                }
            }, DispatcherPriority.Background);
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
        // VLC fires EndReached on its own internal thread.  Calling Stop() (inside
        // FinishIntro) from within that same thread context — even via Post — can
        // deadlock in LibVLCSharp 3.x because Stop() joins the VLC event thread.
        // Wrapping in Task.Run breaks the VLC-thread chain before marshalling to UI.
        Task.Run(() => Dispatcher.UIThread.Post(FinishIntro));
    }

    private void OnEncounteredError(object? sender, EventArgs e)
    {
        DevLogService.Log("[IntroWindow] VLC EncounteredError — finishing intro.");
        Task.Run(() => Dispatcher.UIThread.Post(FinishIntro));
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

        // Cancel the safety timeout if it hasn't fired yet.
        try { _timeoutCts?.Cancel(); }
        catch (ObjectDisposedException) { }
        _timeoutCts?.Dispose();
        _timeoutCts = null;

        DevLogService.Log("[IntroWindow] FinishIntro — stopping player and transitioning to main window.");

        try { _mediaPlayer?.Stop(); }
        catch (ObjectDisposedException) { /* already cleaned up */ }

        DisposeVlc();

        // Show the main window (which starts at the login screen), then close the intro.
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
