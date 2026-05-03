using System;
using System.Diagnostics;
using System.IO;
using System.Security;

namespace GameLauncher.Services
{
    /// <summary>
    /// Shows OS-native desktop notifications.
    /// On Windows 10 / 11 the notification appears as a toast in the Action Center.
    /// On other operating systems the call is a no-op (no additional packages needed).
    /// </summary>
    public static class NotificationService
    {
        /// <summary>
        /// Displays a toast notification for an incoming direct message.
        /// </summary>
        /// <param name="senderUsername">Shown as the notification title.</param>
        /// <param name="messagePreview">Short preview of the message body (≤ 80 chars recommended).</param>
        public static void ShowMessageNotification(string senderUsername, string messagePreview)
        {
            if (!OperatingSystem.IsWindows()) return;

            try
            {
                ShowWindowsToast(senderUsername, messagePreview);
            }
            catch { /* best-effort — notifications are non-critical */ }
        }

        /// <summary>
        /// Shows a "Friend is now online" toast notification.
        /// </summary>
        /// <param name="friendUsername">Username of the friend who just came online.</param>
        public static void ShowFriendOnlineNotification(string friendUsername)
        {
            if (!OperatingSystem.IsWindows()) return;
            try
            {
                ShowWindowsToast(
                    "Friend Online 🟢",
                    $"{friendUsername} is now online on Game.OS");
            }
            catch { }
        }

        /// <summary>
        /// Shows a "Friend started playing a game" toast notification.
        /// </summary>
        /// <param name="friendUsername">Username of the friend who started playing.</param>
        /// <param name="gameTitle">Title of the game they are now playing.</param>
        public static void ShowFriendGameStartNotification(string friendUsername, string gameTitle)
        {
            if (!OperatingSystem.IsWindows()) return;
            try
            {
                ShowWindowsToast(
                    $"{friendUsername} is now playing 🎮",
                    gameTitle);
            }
            catch { }
        }

        // ── Windows Toast via PowerShell ──────────────────────────────────────

        /// <summary>
        /// Displays a Windows toast notification by invoking a hidden PowerShell
        /// command that uses the built-in Windows.UI.Notifications WinRT API.
        /// Writing the XML payload to a temp file avoids PowerShell argument-length
        /// limits and prevents any injection through user-supplied text.
        /// </summary>
        private static void ShowWindowsToast(string title, string body)
        {
            // Build the toast XML, escaping any XML-special characters.
            string safeTitle = SecurityElement.Escape(title)  ?? "";
            string safeBody  = SecurityElement.Escape(body)   ?? "";

            string toastXml =
                $"<toast>" +
                $"<visual><binding template=\"ToastGeneric\">" +
                $"<text>{safeTitle}</text>" +
                $"<text>{safeBody}</text>" +
                $"</binding></visual>" +
                $"</toast>";

            // Write to a temp file so the PowerShell command stays short and safe.
            // GetTempFileName always returns a path in the system temp dir which
            // on Windows never contains single quotes, but we escape just in case.
            string xmlPath = Path.GetTempFileName();
            try
            {
                File.WriteAllText(xmlPath, toastXml);

                // Escape single quotes in the path for safe embedding in the PS command
                string safePath = xmlPath.Replace("'", "''");
                string appId    = "Game.OS"; // Displayed as the notification source name

                // PowerShell script: load WinRT types, read XML from file, show toast
                string ps =
                    "[void][Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime]; " +
                    "[void][Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom, ContentType = WindowsRuntime]; " +
                    $"$xml = New-Object Windows.Data.Xml.Dom.XmlDocument; " +
                    $"$xml.LoadXml([System.IO.File]::ReadAllText('{safePath}')); " +
                    $"$toast = [Windows.UI.Notifications.ToastNotification]::new($xml); " +
                    $"[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('{appId}').Show($toast); " +
                    $"Remove-Item -LiteralPath '{safePath}' -ErrorAction SilentlyContinue";

                var psi = new ProcessStartInfo("powershell.exe",
                    $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{ps}\"")
                {
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = false,
                    RedirectStandardError  = false,
                };

                // Start and immediately dispose: we do not need to wait for the
                // PowerShell process since it handles its own temp-file cleanup.
                using var proc = Process.Start(psi);
                // proc may be null if the shell is unavailable; that is a no-op.
            }
            catch
            {
                // If anything goes wrong, clean up the temp file ourselves
                try { File.Delete(xmlPath); } catch { }
            }
        }
    }
}
