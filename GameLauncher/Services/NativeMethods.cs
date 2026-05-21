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
    internal const ushort VK_MEDIA_NEXT_TRACK = 0xB0;
    internal const ushort VK_MEDIA_PREV_TRACK = 0xB1;
    internal const ushort VK_MEDIA_PLAY_PAUSE = 0xB3;

    // SetWindowPos flags
    internal const uint SWP_NOMOVE     = 0x0002;
    internal const uint SWP_NOSIZE     = 0x0001;
    internal const uint SWP_SHOWWINDOW = 0x0040;

    /// <summary>Sentinel HWND that places a window above all non-topmost windows.</summary>
    internal static readonly nint HWND_TOPMOST = new nint(-1);

    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    internal static bool SendMediaKey(ushort virtualKey)
    {
        var inputs = new INPUT[2];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].u.ki = new KEYBDINPUT
        {
            wVk = virtualKey,
            wScan = 0,
            dwFlags = 0,
            time = 0,
            dwExtraInfo = nint.Zero
        };
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].u.ki = new KEYBDINPUT
        {
            wVk = virtualKey,
            wScan = 0,
            dwFlags = KEYEVENTF_KEYUP,
            time = 0,
            dwExtraInfo = nint.Zero
        };

        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent == 0)
        {
            int error = Marshal.GetLastWin32Error();
            System.Diagnostics.Debug.WriteLine($"[NativeMethods] SendInput failed (VK={virtualKey}, Win32Error={error}).");
            return false;
        }

        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }
}
