using System.Runtime.InteropServices;

namespace GameLauncher.Services;

/// <summary>
/// Minimal Win32 P/Invoke declarations used to bring a game window to the foreground
/// when the user clicks "Resume" on the game detail overlay.
/// All calls are guarded by <see cref="OperatingSystem.IsWindows"/> at the call site.
/// </summary>
internal static class NativeMethods
{
    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = false)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(nint hWnd, int nCmdShow);
}
