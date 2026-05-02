using Avalonia.Controls;
using Avalonia.Platform.Storage;
using GameLauncher.ViewModels;

namespace GameLauncher.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.BrowseRequested                    = OnBrowseRequested;
            vm.BrowseIntroVideoRequested          = OnBrowseIntroVideoRequested;
            vm.PickIntroVideoFromGalleryRequested = OnPickIntroVideoFromGalleryRequested;
            vm.BrowseStartupAppRequested          = OnBrowseStartupAppRequested;
            vm.BrowseNewStartupAppRequested       = OnBrowseNewStartupAppRequested;
        }
    }

    private async void OnBrowseRequested(EmulatorRowVm row)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = $"Select emulator for {row.Platform}",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Executable")
                    {
                        Patterns = new[] { "*.exe", "*.bat", "*.sh", "*.AppImage" },
                    },
                    FilePickerFileTypes.All,
                },
            });

        if (files.Count > 0)
            row.EmulatorPath = files[0].Path.LocalPath;
    }

    private async void OnBrowseIntroVideoRequested()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Select intro video file",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Video files")
                    {
                        Patterns = new[] { "*.mp4", "*.webm", "*.avi", "*.mkv", "*.mov" },
                    },
                    FilePickerFileTypes.All,
                },
            });

        if (files.Count > 0 && DataContext is SettingsViewModel vm)
            vm.IntroVideoPath = files[0].Path.LocalPath;
    }

    private async void OnPickIntroVideoFromGalleryRequested()
    {
        var ownerWindow = TopLevel.GetTopLevel(this) as Window;
        if (ownerWindow == null) return;

        var picker = new IntroVideoPickerWindow();
        var result = await picker.ShowDialog<string?>(ownerWindow);

        if (!string.IsNullOrEmpty(result) && DataContext is SettingsViewModel vm)
            vm.IntroVideoPath = result;
    }

    private async void OnBrowseStartupAppRequested(StartupAppRowVm row)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title         = $"Select executable for {row.Label}",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Executable / Script")
                    {
                        Patterns = new[] { "*.exe", "*.bat", "*.cmd", "*.sh", "*.ps1", "*.AppImage" },
                    },
                    FilePickerFileTypes.All,
                },
            });

        if (files.Count > 0)
            row.Path = files[0].Path.LocalPath;
    }

    private async void OnBrowseNewStartupAppRequested()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title         = "Select executable or script",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Executable / Script")
                    {
                        Patterns = new[] { "*.exe", "*.bat", "*.cmd", "*.sh", "*.ps1", "*.AppImage" },
                    },
                    FilePickerFileTypes.All,
                },
            });

        if (files.Count > 0 && DataContext is SettingsViewModel vm)
            vm.NewStartupAppPath = files[0].Path.LocalPath;
    }
}
