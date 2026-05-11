using System.Runtime.InteropServices;

namespace GameLauncher.Services;

/// <summary>
/// Minimal Win32 P/Invoke declarations used to bring a game window to the foreground
/// when the user clicks "Resume" on the game detail overlay.
/// All calls are guarded by <see cref="OperatingSystem.IsWindows"/> at the call site.
/// </summary>
internal static class NativeMethods
{
    /// <summary>Restores a minimised window to its normal or maximised state.</summary>
    internal const int SW_RESTORE = 9;
    internal const int VK_LCONTROL = 0xA2;
    internal const int VK_LSHIFT = 0xA0;

    // SetWindowPos flags
    internal const uint SWP_NOMOVE     = 0x0002;
    internal const uint SWP_NOSIZE     = 0x0001;
    internal const uint SWP_SHOWWINDOW = 0x0040;

    /// <summary>Sentinel HWND that places a window above all non-topmost windows.</summary>
    internal static readonly nint HWND_TOPMOST = new nint(-1);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = false)]
    internal static extern short GetAsyncKeyState(int vKey);

    /// <summary>
    /// Changes the size, position, and Z-order of a window.
    /// Used to force an overlay window into the topmost Z-band at the Win32 level.
    /// </summary>
    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);
}
