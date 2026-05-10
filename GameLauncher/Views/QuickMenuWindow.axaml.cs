using Avalonia;
using Avalonia.Controls;

namespace GameLauncher.Views;

/// <summary>
/// A lightweight, always-on-top, borderless window that hosts the Quick Menu overlay.
/// Shown over the active game/app via the global Left Ctrl + Left Shift hotkey without
/// restoring or focusing the main launcher window — mirroring the Steam/NVIDIA overlay
/// pattern for borderless-windowed applications.
/// </summary>
public partial class QuickMenuWindow : Window
{
    public QuickMenuWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Positions the window to the right edge of the primary screen and makes it
    /// visible.  The window height is automatically sized to fit its content.
    /// </summary>
    public void ShowOverGame()
    {
        // Use the XAML-declared width as the authoritative width.
        // Avalonia may not yet have measured the window at this point, so reading
        // the Width property directly is more reliable than DesiredSize.
        const double fallbackWidth = 340;
        double windowWidth = double.IsNaN(Width) || Width <= 0 ? fallbackWidth : Width;

        // Anchor to the right edge of the primary screen
        var screen = Screens.Primary;
        if (screen != null)
        {
            var wa    = screen.WorkingArea;
            double scale   = screen.Scaling;
            double waRight = wa.X + wa.Width;
            double waTop   = wa.Y;
            // Position window so its right edge aligns with the screen's right edge
            Position = new PixelPoint(
                (int)(waRight - windowWidth * scale),
                (int)waTop);
        }

        Show();
        Activate();
    }
}
