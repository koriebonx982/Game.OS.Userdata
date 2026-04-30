using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GameLauncher.Services;
using GameLauncher.ViewModels;
using GameLauncher.Views;

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
            // configured one in Settings.  The video is launched in the
            // system's default media player so no additional dependencies are
            // required.  Playback runs concurrently with the launcher.
            mainWindow.Opened += OnMainWindowOpened;
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void OnMainWindowOpened(object? sender, System.EventArgs e)
    {
        if (sender is Avalonia.Controls.Window w)
            w.Opened -= OnMainWindowOpened;

        var settings = AppSettingsService.Load();
        if (!settings.ShowIntroVideo) return;
        if (string.IsNullOrEmpty(settings.IntroVideoPath)) return;
        if (!System.IO.File.Exists(settings.IntroVideoPath)) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = settings.IntroVideoPath,
                UseShellExecute = true,
            });
        }
        catch { /* best-effort: if the video can't open, continue normally */ }
    }
}

