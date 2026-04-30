using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using GameLauncher.Services;

namespace GameLauncher.Views;

/// <summary>
/// Modal gallery dialog that lists available intro videos from the Game.OS GitHub
/// repository, lets the user download one, and returns the local file path to the caller.
///
/// Usage:
/// <code>
///   var picker = new IntroVideoPickerWindow();
///   var path   = await picker.ShowDialog&lt;string?&gt;(ownerWindow);
///   if (path != null) vm.IntroVideoPath = path;
/// </code>
/// </summary>
public partial class IntroVideoPickerWindow : Window
{
    private CancellationTokenSource _cts = new();

    public IntroVideoPickerWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        RetryButton.Click += (_, _) => _ = LoadGalleryAsync();
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    private async void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        await LoadGalleryAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts.Cancel();
        _cts.Dispose();
        base.OnClosed(e);
    }

    // ── Gallery loading ────────────────────────────────────────────────────────

    private async Task LoadGalleryAsync()
    {
        LoadingPanel.IsVisible = true;
        ErrorPanel.IsVisible   = false;
        EmptyPanel.IsVisible   = false;
        ListPanel.IsVisible    = false;

        try
        {
            var items = await IntroVideoGalleryService.FetchGalleryAsync(_cts.Token);

            LoadingPanel.IsVisible = false;

            if (items.Count == 0)
            {
                EmptyPanel.IsVisible = true;
                return;
            }

            var vms = new ObservableCollection<IntroVideoItemVm>();
            foreach (var item in items)
                vms.Add(new IntroVideoItemVm(item));

            VideoList.ItemsSource = vms;
            ListPanel.IsVisible   = true;
        }
        catch (OperationCanceledException)
        {
            // Window was closed; nothing to do.
        }
        catch (Exception ex)
        {
            LoadingPanel.IsVisible = false;
            ErrorLabel.Text        = $"⚠  Could not load gallery:\n{ex.Message}";
            ErrorPanel.IsVisible   = true;
            DevLogService.Log($"[IntroVideoPickerWindow] Gallery fetch failed: {ex.Message}");
        }
    }

    // ── Item download ──────────────────────────────────────────────────────────

    private async void OnItemButtonClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not IntroVideoItemVm vm) return;
        if (vm.IsDownloading) return;

        vm.IsDownloading    = true;
        vm.DownloadProgress = 0;
        vm.StatusLabel      = "Downloading…";

        try
        {
            var progress = new Progress<double>(p =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    vm.DownloadProgress = p;
                    vm.StatusLabel      = $"{p * 100:F0}%";
                });
            });

            var localPath = await IntroVideoGalleryService.DownloadVideoAsync(
                vm.Item, progress, _cts.Token);

            vm.StatusLabel      = "Done!";
            vm.DownloadProgress = 1;

            DevLogService.Log($"[IntroVideoPickerWindow] Selected: '{localPath}'");
            Close(localPath);
        }
        catch (OperationCanceledException)
        {
            // Cancelled — leave the window open.
        }
        catch (Exception ex)
        {
            vm.StatusLabel      = $"⚠  {ex.Message}";
            vm.IsDownloading    = false;
            vm.DownloadProgress = 0;
            DevLogService.Log($"[IntroVideoPickerWindow] Download failed: {ex.Message}");
        }
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        Close(null);
    }
}

// ── Per-item view-model ────────────────────────────────────────────────────────

/// <summary>Observable view-model for a single row in the intro-video gallery list.</summary>
public partial class IntroVideoItemVm : ObservableObject
{
    public IntroVideoGalleryItem Item { get; }

    public string Name      => Item.Name;
    public string SizeLabel => Item.SizeLabel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsButtonEnabled))]
    [NotifyPropertyChangedFor(nameof(ButtonLabel))]
    private bool _isDownloading = false;

    [ObservableProperty] private double _downloadProgress = 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _statusLabel = "";

    public bool IsButtonEnabled    => !IsDownloading;
    public bool HasStatus          => !string.IsNullOrEmpty(StatusLabel);
    // Checked once at construction — the file either exists in the cache or it doesn't.
    public bool IsAlreadyDownloaded { get; }

    public string ButtonLabel =>
        IsDownloading ? "Downloading…" : IsAlreadyDownloaded ? "Use This" : "Download & Use";

    public IntroVideoItemVm(IntroVideoGalleryItem item)
    {
        Item = item;
        IsAlreadyDownloaded =
            File.Exists(Path.Combine(IntroVideoGalleryService.LocalCacheDir, item.Name));
    }
}
