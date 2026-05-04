using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using GameLauncher.Models;
using GameLauncher.Services;

namespace GameLauncher.Services;

/// <summary>
/// Opt-in developer logging service.
/// When enabled (via the Dev Logs setting), all <see cref="Debug"/>/<see cref="Trace"/>
/// output, plus unhandled exceptions, are appended to <c>Dev.log</c> next to the exe.
/// </summary>
public static class DevLogService
{
    private static TextWriterTraceListener? _listener;
    private static StreamWriter?            _writer;

    /// <summary><see langword="true"/> while the dev log is active.</summary>
    public static bool IsEnabled => _listener != null;

    /// <summary>
    /// Starts logging to <c>Dev.log</c> in the application base directory.
    /// Safe to call multiple times — only the first call has effect.
    /// </summary>
    public static void Enable()
    {
        if (_listener != null) return;

        try
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "Dev.log");
            _writer = new StreamWriter(logPath, append: true, Encoding.UTF8)
            {
                AutoFlush = true,
            };

            _listener = new TextWriterTraceListener(_writer);
            Trace.Listeners.Add(_listener);
            Trace.AutoFlush = true;

            // Global exception catchers so crashes always land in the log.
            AppDomain.CurrentDomain.UnhandledException    += OnUnhandledException;
            TaskScheduler.UnobservedTaskException         += OnUnobservedTaskException;

            // Session header — makes it easy to spot a new run inside the file.
            var bar = new string('=', 60);
            _writer.WriteLine(bar);
            _writer.WriteLine($"[DevLog] Session started  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            _writer.WriteLine($"[DevLog] Exe location     {AppContext.BaseDirectory}");
            _writer.WriteLine($"[DevLog] Log file         {logPath}");
            _writer.WriteLine(bar);
        }
        catch (Exception ex)
        {
            // If we cannot open the log file (locked, read-only path …) just skip it.
            Debug.WriteLine($"[DevLog] Failed to open Dev.log: {ex.Message}");
            _writer?.Dispose();
            _writer    = null;
            _listener  = null;
        }
    }

    /// <summary>
    /// Stops logging and flushes/closes the log file.
    /// </summary>
    public static void Disable()
    {
        if (_listener == null) return;

        AppDomain.CurrentDomain.UnhandledException    -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException         -= OnUnobservedTaskException;

        Log("[DevLog] Session ended");

        Trace.Listeners.Remove(_listener);
        _listener.Flush();
        _listener.Dispose();
        _listener = null;

        _writer?.Flush();
        _writer?.Dispose();
        _writer = null;
    }

    /// <summary>
    /// Writes a timestamped line directly to the log (independent of <see cref="Debug"/>).
    /// Does nothing when the service is disabled.
    /// </summary>
    public static void Log(string message)
    {
        if (_writer == null) return;
        try
        {
            _writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");
        }
        catch (Exception ex)
        {
            // Fall back to Debug output so mid-session write failures are still visible.
            Debug.WriteLine($"[DevLog] Write failed: {ex.Message} — original: {message}");
        }
    }

    // ── Per-scanner conditional helpers ────────────────────────────────────
    // The scanner service reads these flags directly from the saved AppSettings so
    // that the log level can be changed without restarting the app.

    private static Models.AppSettings? _cachedSettings;
    private static DateTime            _settingsCacheTime = DateTime.MinValue;

    /// <summary>Returns the current <see cref="Models.AppSettings"/>, refreshed at most once per second.</summary>
    private static Models.AppSettings Settings()
    {
        if (_cachedSettings == null || (DateTime.Now - _settingsCacheTime).TotalSeconds >= 1)
        {
            _cachedSettings    = Services.AppSettingsService.Load();
            _settingsCacheTime = DateTime.Now;
        }
        return _cachedSettings;
    }

    /// <summary>Write <paramref name="message"/> only when Games Scanner logging is enabled.</summary>
    public static void LogGames(string message)
    {
        if (_writer != null && Settings().LogGamesScanner) Log(message);
    }

    /// <summary>Write <paramref name="message"/> only when Games Scanner Advanced logging is enabled.</summary>
    public static void LogGamesAdvanced(string message)
    {
        if (_writer != null && Settings().LogGamesScanner && Settings().LogGamesScannerAdvanced) Log(message);
    }

    /// <summary>Write <paramref name="message"/> only when ROMs Scanner logging is enabled.</summary>
    public static void LogRoms(string message)
    {
        if (_writer != null && Settings().LogRomsScanner) Log(message);
    }

    /// <summary>Write <paramref name="message"/> only when ROMs Scanner Advanced logging is enabled.</summary>
    public static void LogRomsAdvanced(string message)
    {
        if (_writer != null && Settings().LogRomsScanner && Settings().LogRomsScannerAdvanced) Log(message);
    }

    /// <summary>Write <paramref name="message"/> only when Repacks Scanner logging is enabled.</summary>
    public static void LogRepacks(string message)
    {
        if (_writer != null && Settings().LogRepacksScanner) Log(message);
    }

    /// <summary>Write <paramref name="message"/> only when Repacks Scanner Advanced logging is enabled.</summary>
    public static void LogRepacksAdvanced(string message)
    {
        if (_writer != null && Settings().LogRepacksScanner && Settings().LogRepacksScannerAdvanced) Log(message);
    }

    /// <summary>Write <paramref name="message"/> only when Local Steam Scanner logging is enabled.</summary>
    public static void LogLocalSteam(string message)
    {
        if (_writer != null && Settings().LogLocalSteamScanner) Log(message);
    }

    /// <summary>Write <paramref name="message"/> only when Steam API Scanner logging is enabled.</summary>
    public static void LogSteamApi(string message)
    {
        if (_writer != null && Settings().LogSteamApiScanner) Log(message);
    }

    // ── Global exception handlers ───────────────────────────────────────────

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Log($"[FATAL] Unhandled exception (isTerminating={e.IsTerminating}):");
        Log(e.ExceptionObject?.ToString() ?? "(null)");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log("[ERROR] Unobserved task exception:");
        Log(e.Exception.ToString());
        e.SetObserved();
    }
}
