using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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
    /// <c>"Room: "</c> (case-insensitive) together with any immediately following
    /// property lines that belong to that room block (identified by having deeper
    /// indentation in the Ryujinx log message field).
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

            // Read all lines first so we can look ahead for property sub-lines
            var allLines = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) != null)
                allLines.Add(line);

            for (int i = 0; i < allLines.Count; i++)
            {
                if (!allLines[i].Contains("Room: ", StringComparison.OrdinalIgnoreCase))
                    continue;

                results.Add(allLines[i]);

                // Determine the indentation depth of the "Room:" message so we
                // can capture deeper-indented property lines that follow it.
                string roomMsg   = ExtractLogMessage(allLines[i]);
                int    roomDepth = CountLeadingWhitespace(roomMsg);

                // Capture subsequent lines that are indented deeper than the
                // "Room: " line — these are its property fields.
                for (int j = i + 1; j < allLines.Count; j++)
                {
                    string nextLine = allLines[j];
                    if (string.IsNullOrWhiteSpace(nextLine)) break;

                    string nextMsg = ExtractLogMessage(nextLine);
                    if (string.IsNullOrWhiteSpace(nextMsg)) break;

                    int nextDepth = CountLeadingWhitespace(nextMsg);
                    if (nextDepth <= roomDepth) break; // back to same/higher level

                    results.Add(nextLine);
                    i = j; // advance outer loop so we don't re-process these lines
                }
            }
        }
        catch { /* best-effort */ }

        return results;
    }

    /// <summary>
    /// Reads the entire contents of <paramref name="logFilePath"/>, redacts any
    /// IPv4 and IPv6 addresses (replaced with <c>[IP REDACTED]</c>), and returns
    /// all lines.  Returns an empty list when the file cannot be read.
    /// </summary>
    public static List<string> ReadFullLog(string logFilePath)
    {
        var results = new List<string>();
        if (string.IsNullOrEmpty(logFilePath) || !File.Exists(logFilePath))
            return results;

        try
        {
            using var fs = new FileStream(logFilePath, FileMode.Open,
                                          FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            string? line;
            while ((line = reader.ReadLine()) != null)
                results.Add(RedactIpAddresses(line));
        }
        catch { /* best-effort */ }

        return results;
    }

    // Compiled regex patterns for IP redaction
    private static readonly System.Text.RegularExpressions.Regex _ipv4Regex =
        new(@"\b(?:\d{1,3}\.){3}\d{1,3}\b",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex _ipv6Regex =
        new(@"\b(?:[0-9a-fA-F]{1,4}:){2,7}[0-9a-fA-F]{0,4}\b",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Replaces IPv4 and IPv6 addresses in <paramref name="line"/> with
    /// <c>[IP REDACTED]</c>.
    /// </summary>
    public static string RedactIpAddresses(string line)
    {
        if (string.IsNullOrEmpty(line)) return line;
        line = _ipv4Regex.Replace(line, "[IP REDACTED]");
        line = _ipv6Regex.Replace(line, "[IP REDACTED]");
        return line;
    }

    /// <summary>
    /// Extracts the message portion from a Ryujinx log line.
    /// Ryujinx format: <c>|HH:MM:SS.mmm|L|Module|Message</c>.
    /// Returns the full line if the format is not recognised.
    /// </summary>
    private static string ExtractLogMessage(string logLine)
    {
        int pipes = 0;
        for (int k = 0; k < logLine.Length; k++)
        {
            if (logLine[k] == '|')
            {
                pipes++;
                if (pipes == 4)
                    return logLine.Substring(k + 1);
            }
        }
        return logLine;
    }

    /// <summary>
    /// Returns the number of leading space or tab characters in <paramref name="s"/>.
    /// </summary>
    private static int CountLeadingWhitespace(string s)
    {
        int count = 0;
        foreach (char c in s)
        {
            if (c == ' ' || c == '\t') count++;
            else break;
        }
        return count;
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

    /// <summary>
    /// Returns <see langword="true"/> when a log snippet for <paramref name="titleId"/>
    /// has already been recorded in the local marker directory.
    /// This prevents uploading duplicate data for the same game across sessions.
    /// </summary>
    public static bool HasLogSnippet(string titleId)
    {
        if (string.IsNullOrWhiteSpace(titleId)) return false;
        string markerPath = GetLogSnippetMarkerPath(titleId);
        return File.Exists(markerPath);
    }

    /// <summary>
    /// Marks <paramref name="titleId"/> as "log snippet recorded" by creating a
    /// small marker file in the local log-snippet directory.
    /// </summary>
    public static void MarkLogSnippetRecorded(string titleId)
    {
        if (string.IsNullOrWhiteSpace(titleId)) return;
        try
        {
            string markerPath = GetLogSnippetMarkerPath(titleId);
            Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
            File.WriteAllText(markerPath, DateTime.UtcNow.ToString("O"), Encoding.UTF8);
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Returns the path of the per-TitleID log-snippet marker file.
    /// Format: <c>Data/SwitchLogSnippets/{titleId}.marker</c> next to the exe.
    /// </summary>
    private static string GetLogSnippetMarkerPath(string titleId)
    {
        string? exeDir = AppContext.BaseDirectory;
        string safeId  = string.Concat(titleId.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(string.IsNullOrEmpty(exeDir) ? "." : exeDir,
                            "Data", "SwitchLogSnippets", $"{safeId}.marker");
    }


    /// <summary>
    /// Writes a complete (full-log) session block to the launcher log, with IP addresses
    /// already redacted.  Writes all <paramref name="fullLines"/> rather than only the
    /// "Room:" section, preserving all Ryujinx output for diagnostic purposes.
    /// </summary>
    public static void WriteSessionToLauncherLogFull(
        string gameTitle, string ryujinxLogPath, List<string> fullLines)
    {
        AppendToLauncherLog($"--- Full log session: {gameTitle} | log: {ryujinxLogPath} ---");

        if (fullLines.Count == 0)
        {
            AppendToLauncherLog("  (log file was empty)");
        }
        else
        {
            foreach (string line in fullLines)
                AppendToLauncherLog($"  {line}");
        }

        AppendToLauncherLog("--- End of session ---");
        AppendToLauncherLog(""); // blank separator
    }

    // ── Race-result parsing (for Switch achievement detection) ─────────────────

    /// <summary>
    /// Holds the key fields extracted from a Ryujinx <c>ServicePrepo ProcessPlayReport</c>
    /// block whose <c>Room</c> value is <c>match</c>.  Used for Switch achievement detection.
    /// </summary>
    public sealed class SwitchRaceResult
    {
        /// <summary>Internal course code, e.g. <c>Gu_FirstCircuit</c>.</summary>
        public string Course        { get; init; } = "";
        /// <summary><c>Finish</c> when the race completed normally.</summary>
        public string FinishReason  { get; init; } = "";
        /// <summary>Finishing position (1 = first place).</summary>
        public int    Rank          { get; init; }
        /// <summary>Game mode rule string, e.g. <c>GP</c>, <c>VS</c>, <c>Battle</c>.</summary>
        public string Rule          { get; init; } = "";
        /// <summary>Engine class, e.g. <c>150cc</c>, <c>200cc</c>.</summary>
        public string Engine        { get; init; } = "";
        /// <summary>Number of coins collected in this race.</summary>
        public int    CoinNum       { get; init; }
        /// <summary>Internal driver code, e.g. <c>Mario</c>.</summary>
        public string Driver        { get; init; } = "";
    }

    /// <summary>
    /// Holds the key fields extracted from a Ryujinx <c>ServicePrepo ProcessPlayReport</c>
    /// block whose <c>Room</c> value is <c>gp_result</c>.  Fired once per Grand Prix after
    /// all four races, carrying the player's overall cup rank.
    /// </summary>
    public sealed class SwitchGpResult
    {
        /// <summary>Internal cup code, e.g. <c>Kinoko</c> (Mushroom Cup).</summary>
        public string Cup  { get; init; } = "";
        /// <summary>Overall Grand Prix finishing position (1 = won the cup).</summary>
        public int    Rank { get; init; }
    }

    // Ryujinx timestamp prefix: "HH:MM:SS.mmm |L| ..."
    private static readonly Regex _tsPrefix =
        new(@"^\d{2}:\d{2}:\d{2}\.\d{3}\s*\|", RegexOptions.Compiled);

    /// <summary>
    /// Reads new content appended to <paramref name="logPath"/> since the last call.
    /// Returns every <c>Room: match</c> play-report block as a <see cref="SwitchRaceResult"/>
    /// and every <c>Room: gp_result</c> block as a <see cref="SwitchGpResult"/> via
    /// <paramref name="gpResults"/>.
    /// <paramref name="fileOffset"/> is updated to the current end-of-file position so
    /// subsequent calls only process newly appended lines.
    /// </summary>
    public static List<SwitchRaceResult> ReadRaceResultsFromNewContent(
        string logPath, ref long fileOffset, out List<SwitchGpResult> gpResults)
    {
        var results = new List<SwitchRaceResult>();
        gpResults   = new List<SwitchGpResult>();
        if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath)) return results;

        try
        {
            using var fs = new FileStream(logPath, FileMode.Open,
                                          FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fileOffset > fs.Length)
                fileOffset = 0; // log truncated — restart

            fs.Seek(fileOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8,
                                                detectEncodingFromByteOrderMarks: false,
                                                bufferSize: 4096,
                                                leaveOpen: true);
            // Collect all new lines into a list for look-ahead parsing
            var lines = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) != null)
                lines.Add(line);

            fileOffset = fs.Position;

            // Walk lines looking for ProcessPlayReport blocks
            for (int i = 0; i < lines.Count; i++)
            {
                if (!lines[i].Contains("ServicePrepo ProcessPlayReport", StringComparison.Ordinal))
                    continue;

                // Collect the continuation lines of this block (lines without a new timestamp)
                var block = new List<string> { lines[i] };
                for (int j = i + 1; j < lines.Count; j++)
                {
                    if (_tsPrefix.IsMatch(lines[j])) break; // next log entry
                    block.Add(lines[j]);
                    i = j;
                }

                bool isMatch    = block.Any(l => l.Contains("Room: match",     StringComparison.OrdinalIgnoreCase));
                bool isGpResult = block.Any(l => l.Contains("Room: gp_result", StringComparison.OrdinalIgnoreCase));

                if (!isMatch && !isGpResult) continue;

                // Extract the JSON report block { ... }
                // NOTE: In Ryujinx logs "Report:" appears in the middle of the
                // "ServicePrepo ProcessPlayReport" trigger line (not on its own
                // continuation line), so we search the entire line rather than
                // relying on a StartsWith check.
                var jsonLines = new List<string>();
                bool inJson = false;
                foreach (string bl in block)
                {
                    if (!inJson)
                    {
                        // "Report:" appears in the line either as the standalone prefix
                        // " Report: {" or embedded in "ProcessPlayReport:" on the trigger
                        // line; use IndexOf so both same-line and separate-line formats work.
                        int idx = bl.IndexOf("Report:", StringComparison.Ordinal);
                        if (idx >= 0)
                        {
                            jsonLines.Add(bl.Substring(idx + "Report:".Length).Trim());
                            inJson = true;
                        }
                        // Haven't entered JSON yet — skip this line.
                        continue;
                    }
                    // inJson == true: accumulate the JSON body.
                    jsonLines.Add(bl);
                }

                if (jsonLines.Count == 0) continue;

                string jsonText = string.Join("\n", jsonLines);
                // Find the outermost { ... } pair
                int start = jsonText.IndexOf('{');
                if (start < 0) continue;
                int depth = 0;
                int end = -1;
                for (int k = start; k < jsonText.Length; k++)
                {
                    if (jsonText[k] == '{') depth++;
                    else if (jsonText[k] == '}') { depth--; if (depth == 0) { end = k; break; } }
                }
                if (end < 0) continue;

                string json = jsonText.Substring(start, end - start + 1);
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    static string Str(JsonElement r, string key)
                    {
                        if (r.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String)
                            return p.GetString() ?? "";
                        return "";
                    }
                    static int Int(JsonElement r, string key)
                    {
                        if (r.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.Number)
                            return p.GetInt32();
                        return 0;
                    }

                    if (isMatch)
                    {
                        results.Add(new SwitchRaceResult
                        {
                            Course       = Str(root, "Course"),
                            FinishReason = Str(root, "FinishReason"),
                            Rank         = Int(root, "Rank"),
                            Rule         = Str(root, "Rule"),
                            Engine       = Str(root, "Engine"),
                            CoinNum      = Int(root, "CoinNum"),
                            Driver       = Str(root, "Driver"),
                        });
                    }
                    else // isGpResult
                    {
                        gpResults.Add(new SwitchGpResult
                        {
                            Cup  = Str(root, "Cup"),
                            Rank = Int(root, "Rank"),
                        });
                    }
                }
                catch { /* malformed JSON — skip this block */ }
            }
        }
        catch { /* best-effort */ }

        return results;
    }
}
