using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace GameLauncher.Services;

/// <summary>
/// Helper service for the "Read Switch game log" feature.
///
/// <para>When the user enables <c>ReadSwitchLog</c> in Settings and launches a
/// Nintendo Switch ROM via Ryujinx, this service:</para>
/// <list type="number">
///   <item>Locates and deletes any stale Ryujinx log files so the next session
///         starts with a clean log.</item>
///   <item>After the emulator exits, reads the freshly-written log and extracts
///         every line that contains <c>"Room: "</c> (LDN / multiplayer room info).</item>
///   <item>Appends a timestamped summary to <c>Switch log Reader.log</c> next to
///         the Game.OS launcher executable so the user can inspect it easily.</item>
/// </list>
/// </summary>
public static class SwitchLogReaderService
{
    // ── Log directory discovery ────────────────────────────────────────────────

    /// <summary>
    /// Returns the Ryujinx log directory for the given emulator path.
    /// Checks portable mode first (<c>{ryujinxDir}\portable\Logs\</c>), then falls
    /// back to the standard AppData location (<c>%APPDATA%\Ryujinx\Logs\</c>).
    /// Returns <see langword="null"/> when neither directory exists.
    /// </summary>
    public static string? FindLogDirectory(string ryujinxExePath)
    {
        if (string.IsNullOrEmpty(ryujinxExePath)) return null;

        string ryujinxDir = Path.GetDirectoryName(ryujinxExePath) ?? "";

        // Portable mode
        string portableLogs = Path.Combine(ryujinxDir, "portable", "Logs");
        if (Directory.Exists(portableLogs)) return portableLogs;

        // Standard AppData mode
        string appDataLogs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Ryujinx", "Logs");
        if (Directory.Exists(appDataLogs)) return appDataLogs;

        // Create the AppData log dir path even if it doesn't exist yet so the
        // caller can pass it to FindLatestLog after the emulator has run.
        return appDataLogs;
    }

    // ── Log file management ────────────────────────────────────────────────────

    /// <summary>
    /// Deletes all <c>*.log</c> files found in the Ryujinx log directory.
    /// Silently skips files that are locked or otherwise undeletable.
    /// </summary>
    public static void DeleteOldLogs(string ryujinxExePath)
    {
        string? logDir = FindLogDirectory(ryujinxExePath);
        if (logDir == null || !Directory.Exists(logDir)) return;

        foreach (string file in Directory.EnumerateFiles(logDir, "*.log"))
        {
            try { File.Delete(file); }
            catch { /* file in use or access denied — skip */ }
        }
    }

    /// <summary>
    /// Returns the path of the most-recently-written <c>*.log</c> file in the
    /// Ryujinx log directory, or <see langword="null"/> when none is found.
    /// </summary>
    public static string? FindLatestLog(string ryujinxExePath)
    {
        string? logDir = FindLogDirectory(ryujinxExePath);
        if (logDir == null || !Directory.Exists(logDir)) return null;

        return Directory
            .EnumerateFiles(logDir, "*.log")
            .Select(f => new FileInfo(f))
            .OrderByDescending(fi => fi.LastWriteTimeUtc)
            .FirstOrDefault()?.FullName;
    }

    // ── Log content parsing ────────────────────────────────────────────────────

    /// <summary>
    /// Reads <paramref name="logFilePath"/> and returns every line that contains
    /// the text <c>"Room: "</c> (case-insensitive).
    /// Returns an empty list when the file cannot be read.
    /// </summary>
    public static List<string> ReadRoomLines(string logFilePath)
    {
        var results = new List<string>();
        if (string.IsNullOrEmpty(logFilePath) || !File.Exists(logFilePath))
            return results;

        try
        {
            // Open with read-share so we can access files still open by Ryujinx
            using var fs = new FileStream(logFilePath, FileMode.Open,
                                          FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Contains("Room: ", StringComparison.OrdinalIgnoreCase))
                    results.Add(line);
            }
        }
        catch { /* best-effort */ }

        return results;
    }

    // ── Launcher-side log file ─────────────────────────────────────────────────

    /// <summary>
    /// Returns the full path to <c>Switch log Reader.log</c> placed next to the
    /// running Game.OS launcher executable.
    /// </summary>
    public static string GetLauncherLogPath()
    {
        string? exeDir = AppContext.BaseDirectory;
        return Path.Combine(string.IsNullOrEmpty(exeDir) ? "." : exeDir,
                            "Switch log Reader.log");
    }

    /// <summary>
    /// Appends a timestamped <paramref name="message"/> (followed by a blank line)
    /// to the launcher-side log file returned by <see cref="GetLauncherLogPath"/>.
    /// </summary>
    public static void AppendToLauncherLog(string message)
    {
        try
        {
            string logPath = GetLauncherLogPath();
            string entry   = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
            File.AppendAllText(logPath, entry, Encoding.UTF8);
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Convenience wrapper that writes a full session block to the launcher log:
    /// <list type="bullet">
    ///   <item>A "session start" header line.</item>
    ///   <item>One entry per line in <paramref name="roomLines"/>.</item>
    ///   <item>A blank separator.</item>
    /// </list>
    /// </summary>
    public static void WriteSessionToLauncherLog(string gameTitle, string ryujinxLogPath, List<string> roomLines)
    {
        AppendToLauncherLog($"--- Session: {gameTitle} | log: {ryujinxLogPath} ---");

        if (roomLines.Count == 0)
        {
            AppendToLauncherLog("  (no 'Room: ' lines found in log)");
        }
        else
        {
            foreach (string line in roomLines)
                AppendToLauncherLog($"  {line.Trim()}");
        }

        AppendToLauncherLog("--- End of session ---");
        AppendToLauncherLog(""); // blank separator
    }
}
