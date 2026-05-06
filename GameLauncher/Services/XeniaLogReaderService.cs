using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace GameLauncher.Services;

/// <summary>
/// Reads Xenia emulator log files and extracts achievement-unlock events.
///
/// <para>Xenia outputs lines like:</para>
/// <code>i&gt; HANDLE Achievement unlocked: My Achievement Name</code>
/// <para>or (some builds include a numeric ID):</para>
/// <code>i&gt; HANDLE Achievement unlocked: 1 My Achievement Name</code>
///
/// <para>This service:</para>
/// <list type="number">
///   <item>Locates and optionally clears stale Xenia log files before a session.</item>
///   <item>Supports real-time log tailing via <see cref="ReadUnlocksFromNewContent"/>
///         so toasts can fire while the game is still running.</item>
///   <item>Cross-references against the per-game achievements cache so that
///         achievements already recorded are skipped (Xenia replays all unlocks
///         on every emulator restart).</item>
///   <item>Returns only newly unlocked achievements that are not yet cached.</item>
/// </list>
/// </summary>
public static class XeniaLogReaderService
{
    // ── Pattern matching ────────────────────────────────────────────────────

    /// <summary>
    /// Matches Xenia achievement-unlock log lines in both forms:
    ///   "Achievement unlocked: 1 My Achievement"  (numeric ID present)
    ///   "Achievement unlocked: My Achievement"    (no numeric ID — most Xenia builds)
    /// Groups: 1 = optional numeric ID, 2 = achievement name.
    /// </summary>
    private static readonly Regex _unlockPattern =
        new(@"achievement\s+unlocked[:\s]+(?:(\d+)\s+)?(.+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── Log directory discovery ─────────────────────────────────────────────

    /// <summary>
    /// Returns the Xenia log directory for the given emulator path.
    /// Checks portable mode first (<c>{xeniaDir}\Logs\</c>), then falls back
    /// to the standard AppData location (<c>%APPDATA%\Xenia\Logs\</c>).
    /// Returns <see langword="null"/> when the emulator path is empty.
    /// </summary>
    public static string? FindLogDirectory(string xeniaExePath)
    {
        if (string.IsNullOrEmpty(xeniaExePath)) return null;

        string xeniaDir = Path.GetDirectoryName(xeniaExePath) ?? "";

        // Portable mode: logs next to the exe
        string portableLogs = Path.Combine(xeniaDir, "Logs");
        if (Directory.Exists(portableLogs)) return portableLogs;

        // Standard AppData mode
        string appDataLogs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Xenia", "Logs");
        if (Directory.Exists(appDataLogs)) return appDataLogs;

        // Return the AppData path even if it doesn't exist yet — it may be
        // created by the emulator during the session.
        return appDataLogs;
    }

    /// <summary>
    /// Deletes all <c>*.log</c> files found in the Xenia log directory AND
    /// directly in the Xenia executable's directory so the next session starts
    /// with a clean log.  Silently skips files that are locked or otherwise
    /// undeletable (e.g. the current session log held open by Xenia).
    /// </summary>
    public static void DeleteOldLogs(string xeniaExePath)
    {
        if (string.IsNullOrEmpty(xeniaExePath)) return;

        // Also delete logs written directly next to the exe (xenia.log, xenia_canary.log, …)
        string xeniaDir = Path.GetDirectoryName(xeniaExePath) ?? string.Empty;
        if (!string.IsNullOrEmpty(xeniaDir) && Directory.Exists(xeniaDir))
        {
            foreach (string file in Directory.EnumerateFiles(xeniaDir, "*.log"))
            {
                try { File.Delete(file); }
                catch { /* file in use or access denied — skip */ }
            }
        }

        string? logDir = FindLogDirectory(xeniaExePath);
        if (logDir == null || !Directory.Exists(logDir)) return;

        foreach (string file in Directory.EnumerateFiles(logDir, "*.log"))
        {
            try { File.Delete(file); }
            catch { /* file in use or access denied — skip */ }
        }
    }

    /// <summary>
    /// Returns the path of the most-recently-written <c>*.log</c> file across
    /// all known Xenia log locations:
    /// <list type="number">
    ///   <item>The Xenia executable's own directory (<c>xenia.log</c> / <c>xenia_canary.log</c>
    ///         written by most portable Xenia builds).</item>
    ///   <item><c>{xeniaDir}\Logs\</c> (older portable layout).</item>
    ///   <item><c>%APPDATA%\Xenia\Logs\</c> (installed mode).</item>
    /// </list>
    /// Returns <see langword="null"/> when no log file is found.
    /// </summary>
    public static string? FindLatestLog(string xeniaExePath)
    {
        if (string.IsNullOrEmpty(xeniaExePath)) return null;

        string xeniaDir = Path.GetDirectoryName(xeniaExePath) ?? string.Empty;
        var candidates = new List<string>();

        // 1. Log files written directly next to the exe (most common Xenia behaviour)
        if (!string.IsNullOrEmpty(xeniaDir) && Directory.Exists(xeniaDir))
            candidates.AddRange(Directory.EnumerateFiles(xeniaDir, "*.log"));

        // 2. Portable Logs\ sub-directory
        string portableLogs = Path.Combine(xeniaDir, "Logs");
        if (Directory.Exists(portableLogs))
            candidates.AddRange(Directory.EnumerateFiles(portableLogs, "*.log"));

        // 3. AppData Xenia\Logs\
        string appDataLogs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Xenia", "Logs");
        if (Directory.Exists(appDataLogs))
            candidates.AddRange(Directory.EnumerateFiles(appDataLogs, "*.log"));

        return candidates
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    // ── Achievement extraction ──────────────────────────────────────────────

    /// <summary>
    /// Parses a single matched line into an (id, name) tuple.
    /// When no numeric ID is present the normalized name is used as the ID
    /// so deduplication still works across sessions.
    /// </summary>
    private static (string Id, string Name) ParseMatch(Match m)
    {
        string name = m.Groups[2].Value.Trim();
        string id   = m.Groups[1].Success && !string.IsNullOrEmpty(m.Groups[1].Value)
                          ? m.Groups[1].Value.Trim()
                          : name.ToLowerInvariant();
        return (id, name);
    }

    /// <summary>
    /// Reads the most recent Xenia log file and returns a list of all achievement-unlock
    /// entries found in it.  Each entry is a tuple of (id, name).
    /// Returns an empty list when the log file does not exist or contains no unlocks.
    /// </summary>
    public static IReadOnlyList<(string Id, string Name)> ReadUnlocks(string xeniaExePath)
    {
        string? logPath = FindLatestLog(xeniaExePath);
        if (logPath == null || !File.Exists(logPath)) return [];

        var unlocks = new List<(string, string)>();
        try
        {
            foreach (string line in File.ReadLines(logPath))
            {
                var m = _unlockPattern.Match(line);
                if (!m.Success) continue;
                var (id, name) = ParseMatch(m);
                if (!string.IsNullOrEmpty(name))
                    unlocks.Add((id, name));
            }
        }
        catch { /* best-effort — log may still be open by the emulator */ }

        // Deduplicate by id (Xenia may log the same unlock multiple times)
        return unlocks
            .GroupBy(u => u.Item1, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>
    /// Reads new content appended to <paramref name="logPath"/> since the last call,
    /// returning any achievement-unlock entries found in that new content.
    /// <paramref name="fileOffset"/> is updated to the new end-of-file position on each call
    /// so subsequent calls only process lines written after the previous call.
    /// This is the preferred method for real-time log tailing while the game is running.
    /// Returns an empty list when the file does not exist or no new unlocks were found.
    /// </summary>
    public static IReadOnlyList<(string Id, string Name)> ReadUnlocksFromNewContent(
        string logPath, ref long fileOffset)
    {
        if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath)) return [];

        var unlocks = new List<(string, string)>();
        try
        {
            using var fs = new FileStream(logPath, FileMode.Open,
                                          FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fileOffset > fs.Length)
                fileOffset = 0; // log was truncated / replaced — restart from beginning

            fs.Seek(fileOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8,
                                                detectEncodingFromByteOrderMarks: false,
                                                bufferSize: 4096,
                                                leaveOpen: true);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var m = _unlockPattern.Match(line);
                if (m.Success)
                {
                    var (id, name) = ParseMatch(m);
                    if (!string.IsNullOrEmpty(name))
                        unlocks.Add((id, name));
                }
            }

            fileOffset = fs.Position;
        }
        catch { /* best-effort — log may be in use */ }

        return unlocks
            .GroupBy(u => u.Item1, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>
    /// Returns only the achievement-unlock entries that are NOT already present
    /// in the supplied cached achievement set.
    ///
    /// <para>Xenia re-replays all achievement unlocks on every emulator restart,
    /// so this method is necessary to avoid double-counting.</para>
    /// </summary>
    /// <param name="xeniaExePath">Path to the Xenia executable.</param>
    /// <param name="alreadyUnlockedIds">
    /// Set of achievement IDs already recorded in the local cache (achievements.json).
    /// May be <see langword="null"/> to treat all entries as new.
    /// </param>
    public static IReadOnlyList<(string Id, string Name)> GetNewUnlocks(
        string xeniaExePath,
        IReadOnlySet<string>? alreadyUnlockedIds)
    {
        var all = ReadUnlocks(xeniaExePath);
        if (alreadyUnlockedIds == null || alreadyUnlockedIds.Count == 0) return all;

        return all
            .Where(u => !alreadyUnlockedIds.Contains(u.Id))
            .ToList();
    }
}
