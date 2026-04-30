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
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainVm = new MainViewModel();
            if (DemoMode.IsEnabled)
                mainVm.LoadDemo();

            var mainWindow = new MainWindow { DataContext = mainVm };
            desktop.MainWindow = mainWindow;

            var settings = AppSettingsService.Load();
            if (settings.ShowIntroVideo &&
                !string.IsNullOrEmpty(settings.IntroVideoPath) &&
                File.Exists(settings.IntroVideoPath))
            {
                // Hide the main window until the intro finishes; IntroWindow
                // will show it fullscreen once playback ends.
                mainWindow.IsVisible = false;

                var intro = new IntroWindow();
                intro.Show();
            }
            else
            {
                mainWindow.Show();
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
