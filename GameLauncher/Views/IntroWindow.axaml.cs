using Avalonia;
using Avalonia.Threading;
using Avalonia.Controls;
using GameLauncher.Services;
using LibVLCSharp.Shared;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace GameLauncher.Views;

/// <summary>
/// Full-screen intro video window that plays before the login screen appears.
/// Mirrors the PS5 OS reference: the video plays from a fixed path with no user
/// interaction, and on end or failure <see cref="App.ShowMainWindow"/> is called
/// exactly as the PS5 reference calls <c>app.ShowMainWindow()</c>.
/// </summary>
public partial class IntroWindow : Window
{
    // Guard so Core.Initialize is only ever called once per process.
    private static volatile bool _vlcCoreInitialized;

    private LibVLC?       _libVlc;
    private MediaPlayer?  _mediaPlayer;
    private Media?        _media;
    private bool          _finished;
    private readonly string _videoPath;

    // Safety timeout: if VLC hasn't started playback within 8 s, proceed to main window.
    private CancellationTokenSource? _timeoutCts;

    /// <summary>
    /// Parameterless constructor required so Avalonia's XAML compiler/runtime can
    /// resolve the view type; runtime playback still uses <see cref="IntroWindow(string)"/>.
    /// </summary>
    public IntroWindow() : this(string.Empty) { }

    /// <param name="videoPath">Absolute path to the intro video file.</param>
    public IntroWindow(string videoPath)
    {
        _videoPath = videoPath;
        InitializeComponent();
        Opened  += OnOpened;
        Closing += OnClosing;
        Closed  += OnClosed;
    }

    // ── Playback startup ──────────────────────────────────────────────────────

    private void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        DevLogService.Log($"[IntroWindow] Opened. Playing '{_videoPath}'.");

        if (!File.Exists(_videoPath))
        {
            DevLogService.Log("[IntroWindow] Video file missing — finishing immediately.");
            FinishAndShowMain();
            return;
        }

        // 8-second safety timeout — prevents a blank screen if VLC fails silently.
        _timeoutCts = new CancellationTokenSource();
        var tok = _timeoutCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(8000, tok).ConfigureAwait(false);
                DevLogService.Log("[IntroWindow] Startup timeout reached — forcing finish.");
                Dispatcher.UIThread.Post(FinishAndShowMain);
            }
            catch (OperationCanceledException) { /* playback started in time */ }
        }, tok);

        try
        {
            if (!_vlcCoreInitialized)
            {
                string? vlcDir = FindBundledLibVlcDirectory();
                if (!string.IsNullOrEmpty(vlcDir))
                {
                    Core.Initialize(vlcDir);
                    DevLogService.Log($"[IntroWindow] Core.Initialize('{vlcDir}') succeeded.");
                }
                else
                {
                    Core.Initialize();
                    DevLogService.Log($"[IntroWindow] Core.Initialize() succeeded (default search on {GetCurrentPlatformLabel()}).");
                }
                _vlcCoreInitialized = true;
            }

            _libVlc      = new LibVLC(enableDebugLogs: false);
            _mediaPlayer = new MediaPlayer(_libVlc);
            _mediaPlayer.EndReached       += OnEndReached;
            _mediaPlayer.EncounteredError += OnEncounteredError;
            _media = new Media(_libVlc, new Uri(_videoPath));

            // DispatcherPriority.Background fires after layout + first render pass so
            // the VideoView has a valid native surface handle for VLC to draw into.
            Dispatcher.UIThread.Post(() =>
            {
                IntroVideoView.MediaPlayer = _mediaPlayer;
                bool started = _mediaPlayer.Play(_media);
                DevLogService.Log($"[IntroWindow] MediaPlayer.Play() = {started}.");
                if (!started)
                {
                    DevLogService.Log("[IntroWindow] Play() returned false — finishing.");
                    FinishAndShowMain();
                }
                else
                {
                    // Cancel timeout — playback is live.
                    try { _timeoutCts?.Cancel(); }
                    catch (ObjectDisposedException) { }
                }
            }, DispatcherPriority.Background);
        }
        catch (Exception ex)
        {
            DevLogService.Log($"[IntroWindow] VLC init FAILED: {ex.GetType().Name}: {ex.Message}");
            FinishAndShowMain();
        }
    }

    // ── VLC event handlers ────────────────────────────────────────────────────

    private void OnEndReached(object? sender, EventArgs e)
    {
        DevLogService.Log("[IntroWindow] MediaEnded.");
        // VLC fires on its own thread — break the VLC-thread chain before posting
        // to the UI thread to avoid the Stop() deadlock in LibVLCSharp 3.x.
        Task.Run(() => Dispatcher.UIThread.Post(FinishAndShowMain));
    }

    private void OnEncounteredError(object? sender, EventArgs e)
    {
        DevLogService.Log("[IntroWindow] MediaFailed — proceeding to main window.");
        Task.Run(() => Dispatcher.UIThread.Post(FinishAndShowMain));
    }

    // ── Window close guard ───────────────────────────────────────────────────

    // Matches PS5 reference: user cannot close the intro window while it is playing.
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_finished)
            e.Cancel = true;
    }

    private void OnClosed(object? sender, EventArgs e) => DisposeVlc();

    // ── Finish transition ────────────────────────────────────────────────────

    /// <summary>
    /// Stops playback, shows the main window fullscreen, and closes the intro —
    /// exactly matching <c>FinishAndShowMain()</c> in the PS5 OS reference.
    /// </summary>
    private void FinishAndShowMain()
    {
        if (_finished) return;
        _finished = true;

        // Cancel the safety timeout if still running.
        try { _timeoutCts?.Cancel(); }
        catch (ObjectDisposedException) { }
        _timeoutCts?.Dispose();
        _timeoutCts = null;

        try { _mediaPlayer?.Stop(); }
        catch (ObjectDisposedException) { }

        DisposeVlc();

        // Delegate to App.ShowMainWindow() — mirrors the PS5 reference pattern.
        if (Avalonia.Application.Current is App app)
            app.ShowMainWindow();

        // _finished = true so the Closing guard no longer blocks Close().
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

    private static string? FindBundledLibVlcDirectory()
    {
        string baseDir = AppContext.BaseDirectory;
        var candidates = new List<string>();

        if (OperatingSystem.IsWindows())
            candidates.Add(baseDir);

        string? preferredRuntimeId = GetPreferredLibVlcRuntimeId();
        if (!string.IsNullOrEmpty(preferredRuntimeId))
        {
            candidates.Add(Path.Combine(baseDir, "libvlc", preferredRuntimeId));
            candidates.Add(Path.Combine(baseDir, "runtimes", preferredRuntimeId, "native"));
        }

        foreach (var runtimeId in GetLibVlcRuntimeIds())
        {
            candidates.Add(Path.Combine(baseDir, "libvlc", runtimeId));
            candidates.Add(Path.Combine(baseDir, "runtimes", runtimeId, "native"));
        }

        candidates.AddRange(GetSystemLibVlcDirectories());

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate) && DirectoryContainsLibVlc(candidate))
                return candidate;
        }

        return null;
    }

    private static string? GetPreferredLibVlcRuntimeId()
    {
        if (OperatingSystem.IsWindows())
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64   => "win-x64",
                Architecture.X86   => "win-x86",
                Architecture.Arm64 => "win-arm64",
                _                  => null
            };
        }

        if (OperatingSystem.IsLinux())
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64   => "linux-x64",
                Architecture.Arm64 => "linux-arm64",
                _                  => null
            };
        }

        if (OperatingSystem.IsMacOS())
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64   => "osx-x64",
                Architecture.Arm64 => "osx-arm64",
                _                  => null
            };
        }

        return null;
    }

    private static IEnumerable<string> GetLibVlcRuntimeIds()
    {
        if (OperatingSystem.IsWindows())
            return new[] { "win-x64", "win-x86", "win-arm64" };
        if (OperatingSystem.IsLinux())
            return new[] { "linux-x64", "linux-arm64" };
        if (OperatingSystem.IsMacOS())
            return new[] { "osx-x64", "osx-arm64" };
        return Array.Empty<string>();
    }

    private static IEnumerable<string> GetSystemLibVlcDirectories()
    {
        if (OperatingSystem.IsLinux())
        {
            return
            [
                "/usr/lib",
                "/usr/lib64",
                "/usr/lib/vlc",
                "/usr/lib/x86_64-linux-gnu",
                "/usr/lib/aarch64-linux-gnu",
                "/snap/vlc/current/usr/lib",
            ];
        }

        if (OperatingSystem.IsMacOS())
        {
            string userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return
            [
                "/Applications/VLC.app/Contents/MacOS/lib",
                Path.Combine(userHome, "Applications", "VLC.app", "Contents", "MacOS", "lib"),
            ];
        }

        return Array.Empty<string>();
    }

    private static bool DirectoryContainsLibVlc(string directory)
    {
        foreach (var fileName in GetExpectedLibVlcFileNames())
            if (File.Exists(Path.Combine(directory, fileName)))
                return true;
        return false;
    }

    private static IEnumerable<string> GetExpectedLibVlcFileNames()
    {
        if (OperatingSystem.IsWindows())
            return new[] { "libvlc.dll" };
        if (OperatingSystem.IsLinux())
            return new[] { "libvlc.so", "libvlc.so.5" };
        if (OperatingSystem.IsMacOS())
            return new[] { "libvlc.dylib" };
        return Array.Empty<string>();
    }

    private static string GetCurrentPlatformLabel()
    {
        if (OperatingSystem.IsWindows()) return "Windows";
        if (OperatingSystem.IsLinux()) return "Linux";
        if (OperatingSystem.IsMacOS()) return "macOS";
        return Environment.OSVersion.Platform.ToString();
    }
}
