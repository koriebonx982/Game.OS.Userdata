using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GameLauncher.Services;
using GameLauncher.ViewModels;
using GameLauncher.Views;
using System;
using System.IO;

namespace GameLauncher;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

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

            var settings = AppSettingsService.Load();
            DevLogService.Log($"[App] ShowIntroVideo={settings.ShowIntroVideo}  IntroVideoPath='{settings.IntroVideoPath}'");

            if (settings.ShowIntroVideo &&
                !string.IsNullOrEmpty(settings.IntroVideoPath) &&
                File.Exists(settings.IntroVideoPath))
            {
                DevLogService.Log("[App] Intro video file found — showing IntroWindow.");

                // Hide the main window until the intro finishes; IntroWindow
                // will show it fullscreen once playback ends.
                mainWindow.IsVisible = false;

                var intro = new IntroWindow();
                intro.Show();
            }
            else
            {
                if (!settings.ShowIntroVideo)
                    DevLogService.Log("[App] Intro video disabled in settings — skipping.");
                else if (string.IsNullOrEmpty(settings.IntroVideoPath))
                    DevLogService.Log("[App] IntroVideoPath is empty — skipping intro video.");
                else
                    DevLogService.Log($"[App] Intro video file NOT found at '{settings.IntroVideoPath}' — skipping.");

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
}
