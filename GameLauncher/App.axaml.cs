using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaloniaWebView;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.ViewModels;
using GameLauncher.Views;
using System;
using System.IO;
using System.Threading.Tasks;

namespace GameLauncher;

public partial class App : Application
{
    private const string DefaultDesignTheme = "Default";
    private const string Xb360DesignTheme   = "XB360";
    private const string Ps5DesignTheme     = "PS5";
    private const string WiiDesignTheme     = "Wii";
    private const string SwitchDesignTheme  = "Switch";
    private const string SteamBpmDesignTheme = "SteamBPM";
    private const string Xb360ThemeSource   = "avares://GameLauncher/Styles/Xb360Theme.axaml";
    private const string Ps5ThemeSource     = "avares://GameLauncher/Styles/Ps5Theme.axaml";
    private const string WiiThemeSource     = "avares://GameLauncher/Styles/WiiTheme.axaml";
    private const string SwitchThemeSource  = "avares://GameLauncher/Styles/SwitchTheme.axaml";
    private const string SteamBpmThemeSource = "avares://GameLauncher/Styles/SteamBpmTheme.axaml";
    private StyleInclude? _designThemeStyle;

    // Default intro video location — mirrors the PS5 OS reference:
    // the user places Intro.mp4 in {AppDir}/Data/Intro/Intro.mp4.
    private static readonly string DefaultIntroPath =
        Path.Combine(AppContext.BaseDirectory, "Data", "Intro", "Intro.mp4");

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void RegisterServices()
    {
        base.RegisterServices();
        AvaloniaWebViewBuilder.Initialize(default);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        DevLogService.Log("[App] OnFrameworkInitializationCompleted — building main window.");

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainVm = new MainViewModel();
            if (DemoMode.IsEnabled)
            {
                DevLogService.Log("[App] Demo mode active — loading demo data.");
                mainVm.LoadDemo();
            }

            DevLogService.Log("[App] Constructing MainWindow…");
            MainWindow mainWindow;
            try
            {
                mainWindow = new MainWindow { DataContext = mainVm };
            }
            catch (Exception ex)
            {
                DevLogService.Log($"[App] FATAL — MainWindow constructor threw: {ex}");
                throw;
            }
            DevLogService.Log("[App] MainWindow constructed OK.");
            desktop.MainWindow = mainWindow;

            // Resolve the intro video path:
            //   1. Default fixed location  — Data/Intro/Intro.mp4 (matches PS5 OS reference)
            //   2. User override           — IntroVideoPath from settings (if set and exists)
            var settings   = AppSettingsService.Load();
            ApplyDesignTheme(settings.DesignTheme);
            string? introPath = ResolveIntroPath(settings);

            DevLogService.Log($"[App] ShowIntroVideo={settings.ShowIntroVideo}  " +
                              $"DefaultIntroPath='{DefaultIntroPath}'  " +
                              $"IntroVideoPath='{settings.IntroVideoPath}'  " +
                              $"Resolved='{introPath ?? "(none)"}'");

            if (settings.ShowIntroVideo && introPath != null)
            {
                DevLogService.Log($"[App] Intro video found at '{introPath}' — showing IntroWindow.");

                // Hide the main window until the intro finishes; IntroWindow calls
                // ShowMainWindow() to reveal it fullscreen once playback ends.
                mainWindow.IsVisible = false;

                var intro = new IntroWindow(introPath);
                intro.Show();
            }
            else
            {
                if (!settings.ShowIntroVideo)
                    DevLogService.Log("[App] Intro video disabled in settings — skipping.");
                else
                    DevLogService.Log("[App] No intro video found — skipping. " +
                                      $"Place an intro video at '{DefaultIntroPath}' to enable it.");

                mainWindow.Show();
                DevLogService.Log("[App] Main window shown.");
                mainVm.BeginStartup();

                // Screenshot mode: after a short delay, render the window to a PNG and exit.
                if (DemoMode.ScreenshotPath is not null)
                {
                    var outPath = DemoMode.ScreenshotPath;
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(4000);
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            try
                            {
                                var size = new Avalonia.Size(mainWindow.Width, mainWindow.Height);
                                var pixelSize = new PixelSize((int)size.Width, (int)size.Height);
                                using var rtb = new RenderTargetBitmap(pixelSize, new Vector(96, 96));
                                rtb.Render(mainWindow);
                                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                                rtb.Save(outPath);
                                DevLogService.Log($"[Screenshot] Saved to '{outPath}'");
                            }
                            catch (Exception ex)
                            {
                                DevLogService.Log($"[Screenshot] Failed: {ex.Message}");
                            }
                            desktop.Shutdown();
                        });
                    });
                }
            }
        }
        else
        {
            DevLogService.Log("[App] Non-desktop lifetime — no window created.");
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Shows the main window fullscreen and brings it to the foreground.
    /// Called by <see cref="IntroWindow"/> after playback ends or fails —
    /// mirroring <c>app.ShowMainWindow()</c> in the PS5 OS reference.
    /// </summary>
    public void ShowMainWindow()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
            desktop.MainWindow is { } main)
        {
            main.WindowState = WindowState.FullScreen;
            main.Show();
            main.Activate();
            if (main.DataContext is MainViewModel vm)
                vm.BeginStartup();
            DevLogService.Log("[App] ShowMainWindow — main window shown fullscreen.");
        }
    }

    /// <summary>
    /// Resolves which intro video file to use.
    /// Priority: user override path (if set and exists) → default fixed path.
    /// Returns <see langword="null"/> when no valid video file can be found.
    /// </summary>
    private static string? ResolveIntroPath(AppSettings settings)
    {
        // User override takes priority (backwards compat with existing custom paths)
        if (!string.IsNullOrEmpty(settings.IntroVideoPath) &&
            File.Exists(settings.IntroVideoPath))
            return settings.IntroVideoPath;

        // Default fixed location — matches the PS5 OS reference pattern
        if (File.Exists(DefaultIntroPath))
            return DefaultIntroPath;

        return null;
    }

    public void ApplyDesignTheme(string designTheme)
    {
        var trimmed = (designTheme ?? "").Trim();
        string normalised;
        if (string.Equals(trimmed, Xb360DesignTheme, StringComparison.OrdinalIgnoreCase))
            normalised = Xb360DesignTheme;
        else if (string.Equals(trimmed, Ps5DesignTheme, StringComparison.OrdinalIgnoreCase))
            normalised = Ps5DesignTheme;
        else if (string.Equals(trimmed, WiiDesignTheme, StringComparison.OrdinalIgnoreCase))
            normalised = WiiDesignTheme;
        else if (string.Equals(trimmed, SwitchDesignTheme, StringComparison.OrdinalIgnoreCase))
            normalised = SwitchDesignTheme;
        else if (string.Equals(trimmed, SteamBpmDesignTheme, StringComparison.OrdinalIgnoreCase))
            normalised = SteamBpmDesignTheme;
        else
            normalised = DefaultDesignTheme;

        if (_designThemeStyle != null)
        {
            Styles.Remove(_designThemeStyle);
            _designThemeStyle = null;
        }

        string? themeSource = normalised switch
        {
            Xb360DesignTheme => Xb360ThemeSource,
            Ps5DesignTheme   => Ps5ThemeSource,
            WiiDesignTheme   => WiiThemeSource,
            SwitchDesignTheme => SwitchThemeSource,
            SteamBpmDesignTheme => SteamBpmThemeSource,
            _                => null,
        };

        if (themeSource != null)
        {
            _designThemeStyle = new StyleInclude(new Uri("avares://GameLauncher/App.axaml"))
            {
                Source = new Uri(themeSource),
            };
            Styles.Add(_designThemeStyle);
        }
    }
}
