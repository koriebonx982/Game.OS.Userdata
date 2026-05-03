using Avalonia.Controls;
using Avalonia.Platform.Storage;
using GameLauncher.ViewModels;

namespace GameLauncher.Views;

public partial class GameDetailView : UserControl
{
    public GameDetailView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is GameDetailViewModel vm)
            vm.BrowseLaunchPathRequested = OnBrowseLaunchPathRequested;
    }

    private async void OnBrowseLaunchPathRequested(System.Action<string> onPicked)
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

        if (files.Count > 0)
            onPicked(files[0].Path.LocalPath);
    }
}
