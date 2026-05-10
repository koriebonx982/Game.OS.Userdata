using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using AvaloniaWebView;
using GameLauncher.Models;
using GameLauncher.Services;
using GameLauncher.ViewModels;
using GameLauncher.Views;
using System;
using System.IO;

namespace GameLauncher;

public partial class App : Application
{
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

            var mainWindow = new MainWindow { DataContext = mainVm };
            desktop.MainWindow = mainWindow;

            // Resolve the intro video path:
            //   1. Default fixed location  — Data/Intro/Intro.mp4 (matches PS5 OS reference)
            //   2. User override           — IntroVideoPath from settings (if set and exists)
            var settings   = AppSettingsService.Load();
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
}
