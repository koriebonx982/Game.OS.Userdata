using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GameLauncher.Services;
using GameLauncher.ViewModels;
using GameLauncher.Views;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace GameLauncher;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainVm = new MainViewModel();
            if (DemoMode.IsEnabled)
                mainVm.LoadDemo();

            var mainWindow = new MainWindow { DataContext = mainVm };
            desktop.MainWindow = mainWindow;

            // Play intro video when the main window opens, if the user has
            // configured one in Settings.  The video is played fullscreen via
            // VLC or mpv so the launcher is hidden during playback.  After the
            // player exits the launcher is shown fullscreen at the login page.
            mainWindow.Opened += OnMainWindowOpened;
        }
        base.OnFrameworkInitializationCompleted();
    }

    private async void OnMainWindowOpened(object? sender, EventArgs e)
    {
        if (sender is not Window window) return;
        window.Opened -= OnMainWindowOpened;

        var settings = AppSettingsService.Load();
        if (!settings.ShowIntroVideo) return;
        if (string.IsNullOrEmpty(settings.IntroVideoPath)) return;
        if (!File.Exists(settings.IntroVideoPath)) return;

        // Find a suitable fullscreen-capable video player
        var (playerPath, playerArgs) = FindVideoPlayer();

        if (playerPath == null)
        {
            // No known player found — fall back to the system default and
            // skip the fullscreen/wait behaviour (existing behaviour).
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName        = settings.IntroVideoPath,
                    UseShellExecute = true,
                });
            }
            catch { /* best-effort */ }
            return;
        }

        // Minimise the launcher while the video plays so the user only sees
        // the fullscreen video and never sees the desktop between the two.
        window.WindowState = WindowState.Minimized;

        try
        {
            var psi = new ProcessStartInfo(playerPath)
            {
                UseShellExecute = false,
                CreateNoWindow  = false,
            };
            foreach (var arg in playerArgs)
                psi.ArgumentList.Add(arg);
            psi.ArgumentList.Add(settings.IntroVideoPath);

            using var proc = Process.Start(psi);
            if (proc != null)
                await proc.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            // If the player couldn't start, log and continue normally
            Debug.WriteLine($"[IntroVideo] Player failed to start: {ex.Message}");
        }

        // After the video ends, show the launcher fullscreen at the accounts page
        window.WindowState = WindowState.FullScreen;
        window.Activate();
    }

    /// <summary>
    /// Locates the best available fullscreen video player on the current OS.
    /// Returns <c>(null, …)</c> when no supported player is found so the caller
    /// can fall back to the default OS handler.
    /// </summary>
    private static (string? path, string[] args) FindVideoPlayer()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Prefer Windows Media Player — same method used by console OS UIs
            // (PS4/PS5 system software plays intro videos through the system media player)
            var wmpPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"Windows Media Player\wmplayer.exe");
            if (!File.Exists(wmpPath))
                wmpPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    @"Windows Media Player\wmplayer.exe");
            if (File.Exists(wmpPath))
                return (wmpPath, new[] { "/fullscreen" });

            // Fall back to VLC from common install locations
            foreach (var vlc in new[]
            {
                @"C:\Program Files\VideoLAN\VLC\vlc.exe",
                @"C:\Program Files (x86)\VideoLAN\VLC\vlc.exe",
            })
            {
                if (File.Exists(vlc))
                    return (vlc, new[] { "--fullscreen", "--play-and-exit" });
            }

            // Try mpv from PATH
            var mpvPath = FindInPath("mpv.exe");
            if (mpvPath != null)
                return (mpvPath, new[] { "--fs", "--no-border", "--force-window" });

            return (null, Array.Empty<string>());
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            const string macVlc = "/Applications/VLC.app/Contents/MacOS/VLC";
            if (File.Exists(macVlc))
                return (macVlc, new[] { "--fullscreen", "--play-and-exit" });

            var mpvPath = FindInPath("mpv");
            if (mpvPath != null)
                return (mpvPath, new[] { "--fs", "--no-border" });

            return (null, Array.Empty<string>());
        }

        // Linux / other Unix
        {
            var vlcPath = FindInPath("vlc");
            if (vlcPath != null)
                return (vlcPath, new[] { "--fullscreen", "--play-and-exit" });

            var mpvPath = FindInPath("mpv");
            if (mpvPath != null)
                return (mpvPath, new[] { "--fs", "--no-border" });

            return (null, Array.Empty<string>());
        }
    }

    /// <summary>
    /// Searches the system PATH for <paramref name="executable"/> and returns
    /// its full path, or <c>null</c> when it is not found.
    /// </summary>
    private static string? FindInPath(string executable)
    {
        try
        {
            var whichCmd = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "where" : "which";
            using var p = Process.Start(new ProcessStartInfo(whichCmd, executable)
            {
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            });
            if (p == null) return null;
            var line = p.StandardOutput.ReadLine()?.Trim();
            p.WaitForExit();
            if (!string.IsNullOrEmpty(line) && File.Exists(line))
                return line;
        }
        catch { /* not found or lookup not supported */ }
        return null;
    }
}
