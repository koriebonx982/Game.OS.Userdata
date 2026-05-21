using System;
using Avalonia;
using Avalonia.Controls;
using GameLauncher.Services;

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
    /// Positions the window over the full primary screen and makes it visible.
    /// </summary>
    public void ShowOverGame()
    {
        // Cover the full working area so the PS5-style bottom strip and overlays
        // can render the same way in both inline and global overlay modes.
        var screen = Screens.Primary;
        if (screen != null)
        {
            var wa = screen.WorkingArea;
            double scale = screen.Scaling;
            Position = new PixelPoint(wa.X, wa.Y);
            Width = wa.Width / scale;
            Height = wa.Height / scale;
        }

        Show();

        // On Windows, reinforce the topmost Z-order at the Win32 level.
        // Avalonia's Topmost=true sets WS_EX_TOPMOST but Activate() from a background
        // process is restricted by Windows; SetWindowPos bypasses this and forces the
        // window above the currently-focused game window (borderless-windowed mode).
        if (OperatingSystem.IsWindows())
        {
            var handle = TryGetPlatformHandle();
            if (handle is not null)
            {
                NativeMethods.SetWindowPos(
                    handle.Handle,
                    NativeMethods.HWND_TOPMOST,
                    0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_SHOWWINDOW);
                NativeMethods.SetForegroundWindow(handle.Handle);
            }
        }
        else
        {
            Activate();
        }
    }
}
