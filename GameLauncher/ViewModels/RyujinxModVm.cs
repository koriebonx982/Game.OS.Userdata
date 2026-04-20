using CommunityToolkit.Mvvm.ComponentModel;

namespace GameLauncher.ViewModels;

/// <summary>
/// Observable view-model wrapping a single Ryujinx mod entry.
/// Exposes derived display properties that update automatically
/// when <see cref="Enabled"/> is toggled.
/// </summary>
public partial class RyujinxModVm : ViewModelBase
{
    /// <summary>Human-readable mod name from <c>mods.json</c>.</summary>
    public string Name { get; set; } = "";

    /// <summary>Full path to the mod content folder from <c>mods.json</c>.</summary>
    public string Path { get; set; } = "";

    /// <summary>Whether the mod is currently enabled.</summary>
    [ObservableProperty]
    private bool _enabled;

    // ── Derived display properties (update when Enabled changes) ─────────────

    public string StatusLabel      => Enabled ? "✓ Enabled"  : "✗ Disabled";
    public string StatusBackground => Enabled ? "#1a4a1a"    : "#2a1a1a";
    public string StatusForeground => Enabled ? "#3fb950"    : "#f85149";
    public string ToggleLabel      => Enabled ? "Disable"    : "Enable";
    public string ToggleBackground => Enabled ? "#3d1a1a"    : "#1a3a1a";
    public string ToggleForeground => Enabled ? "#f85149"    : "#3fb950";

    partial void OnEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusBackground));
        OnPropertyChanged(nameof(StatusForeground));
        OnPropertyChanged(nameof(ToggleLabel));
        OnPropertyChanged(nameof(ToggleBackground));
        OnPropertyChanged(nameof(ToggleForeground));
    }
}
