using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using GameLauncher.Models;

namespace GameLauncher.Services
{
    /// <summary>
    /// Tracks how long the user plays each game.
    /// Records a play session when a game process is launched and detects its exit,
    /// then persists the accumulated minutes to a local JSON file.
    /// Also updates <see cref="Game.PlaytimeMinutes"/> and <see cref="Game.LastPlayedAt"/>
    /// on the in-memory library list.
    /// </summary>
    public sealed class PlaytimeService : IDisposable
    {
        // ── Per-user storage paths ─────────────────────────────────────────────

        /// <summary>
        /// The username of the currently logged-in user.  Must be set via
        /// <see cref="SetCurrentUser"/> at login and cleared via
        /// <see cref="ClearCurrentUser"/> at logout so sessions are stored
        /// under the correct user's folder.
        /// </summary>
        private static string? _currentUser;
        private static readonly object _userLock = new();

        /// <summary>
        /// Data directory for the currently logged-in user's playtime records.
        /// Falls back to a shared directory when no user is set (legacy / migration path).
        /// </summary>
        private static string DataDir
        {
            get
            {
                string? user;
                lock (_userLock) user = _currentUser;
                return user != null
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                   "GameOS", "Users", user, "Playtime")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                   "GameOS", "Playtime");
            }
        }

        private static string SessionsFile => Path.Combine(DataDir, "sessions.json");

        // ── User lifecycle ─────────────────────────────────────────────────────

        /// <summary>
        /// Sets the current user so playtime data is stored and loaded from
        /// that user's private folder.  Call this at login before
        /// <see cref="ApplyStoredPlaytime"/>.
        /// </summary>
        public static void SetCurrentUser(string username)
        {
            lock (_userLock) _currentUser = username;
        }

        /// <summary>
        /// Clears the current user (call at logout / account switch).
        /// Subsequent playtime reads/writes will use the legacy shared path until
        /// a new user is set.
        /// </summary>
        public static void ClearCurrentUser()
        {
            lock (_userLock) _currentUser = null;
        }

        private static readonly JsonSerializerOptions _jsonOpts =
            new JsonSerializerOptions { WriteIndented = true };

        // ── Constants ─────────────────────────────────────────────────────────

        /// <summary>How often (in minutes) a running-game checkpoint is written to disk.</summary>
        private const int CheckpointIntervalMinutes = 2;

        // ── In-memory active session tracking (static so callers can query from any context) ──

        /// <summary>
        /// Games that are currently running (platform||title → session start time).
        /// Updated immediately when <see cref="TrackProcess"/> is called so the
        /// dashboard can show the game in "Continue Playing" without waiting for exit.
        /// </summary>
        private static readonly Dictionary<string, DateTime> _activeSessions =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly object _activeSessionsLock = new();

        /// <summary>
        /// Fired on the thread-pool when a tracked game process exits and the session
        /// has been saved to disk.  Parameters are (platform, title).
        /// </summary>
        public static event Action<string, string>? SessionCompleted;

        /// <summary>
        /// Fired on the <b>thread-pool</b> (not the UI thread) after a session is
        /// finalised and written to disk.
        /// Passes the complete <see cref="PlaySession"/> record so callers can push
        /// it to the cloud without needing to re-read the local file.
        /// Subscribers that update the UI must marshal back to the UI thread.
        /// </summary>
        public static event Action<PlaySession>? SessionSaved;

        // Active watch record: process + metadata
        private sealed class WatchEntry
        {
            public Process  Proc      { get; init; } = null!;
            public string   Title     { get; init; } = "";
            public string   Platform  { get; init; } = "";
            public DateTime StartedAt { get; init; }
            public Timer?   Checkpoint { get; set; }
        }

        private readonly List<WatchEntry> _watching = new();

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Returns <c>true</c> if a game process with the given platform and title is
        /// currently being tracked (i.e. the game is running right now).
        /// </summary>
        public static bool IsBeingTracked(string platform, string title)
        {
            string key = MakeKey(platform, title);
            lock (_activeSessionsLock)
                return _activeSessions.ContainsKey(key);
        }

        /// <summary>
        /// Returns the number of minutes the game has been running in the current session.
        /// Returns 0 when the game is not currently being tracked.
        /// </summary>
        public static int GetActiveMinutes(string platform, string title)
        {
            string key = MakeKey(platform, title);
            lock (_activeSessionsLock)
            {
                if (_activeSessions.TryGetValue(key, out var startedAt))
                    return Math.Max(1, (int)(DateTime.UtcNow - startedAt).TotalMinutes);
            }
            return 0;
        }

        /// <summary>
        /// Called after a game process has been started.
        /// The service monitors the process and records the session on exit.
        /// Immediately marks the game as "active" so callers can detect it without
        /// waiting for the process to exit.
        /// </summary>
        public void TrackProcess(Process proc, string title, string platform,
                                 List<Game>? libraryToUpdate = null)
        {
            if (proc == null) return;

            var startedAt = DateTime.UtcNow;
            string key = MakeKey(platform, title);

            lock (_activeSessionsLock)
                _activeSessions[key] = startedAt;

            // Immediately stamp LastPlayedAt on the cloud-library game (if present)
            // so the dashboard "Recently Played" section updates right away.
            if (libraryToUpdate != null)
                StampLastPlayedAt(libraryToUpdate, platform, title);

            // If the process has already exited (e.g. launcher-style games that spawn
            // a child and exit quickly), record the session synchronously and return.
            if (proc.HasExited)
            {
                FinaliseSession(key, platform, title, startedAt, libraryToUpdate);
                return;
            }

            var entry = new WatchEntry
            {
                Proc      = proc,
                Title     = title,
                Platform  = platform,
                StartedAt = startedAt,
            };
            _watching.Add(entry);

            // Periodic checkpoint: save an interim session every 2 minutes so playtime
            // is not lost if the launcher crashes before the game exits cleanly.
            entry.Checkpoint = new Timer(_ =>
            {
                lock (_activeSessionsLock)
                {
                    if (!_activeSessions.ContainsKey(key)) return; // session already ended
                }
                var checkpoint = new PlaySession
                {
                    Platform  = platform,
                    Title     = title,
                    StartedAt = startedAt.ToString("o"),
                    EndedAt   = DateTime.UtcNow.ToString("o"),
                    Minutes   = Math.Max(1, (int)(DateTime.UtcNow - startedAt).TotalMinutes),
                    IsCheckpoint = true,
                };
                SaveCheckpoint(checkpoint);
            }, null, TimeSpan.FromMinutes(CheckpointIntervalMinutes), TimeSpan.FromMinutes(CheckpointIntervalMinutes));

            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) =>
            {
                entry.Checkpoint?.Change(Timeout.Infinite, Timeout.Infinite);
                entry.Checkpoint?.Dispose();
                entry.Checkpoint = null;
                _watching.Remove(entry);
                FinaliseSession(key, platform, title, startedAt, libraryToUpdate);
            };

            // Guard against a race: if the process exited between the HasExited check
            // and the Exited handler registration, the event may never fire.
            // Check and remove atomically under the lock to avoid double-finalisation.
            bool raceDetected = false;
            if (proc.HasExited)
            {
                lock (_activeSessionsLock)
                {
                    raceDetected = _activeSessions.ContainsKey(key);
                }
            }
            if (raceDetected)
            {
                entry.Checkpoint?.Change(Timeout.Infinite, Timeout.Infinite);
                entry.Checkpoint?.Dispose();
                entry.Checkpoint = null;
                _watching.Remove(entry);
                FinaliseSession(key, platform, title, startedAt, libraryToUpdate);
            }
        }

        /// <summary>
        /// Loads the accumulated playtime totals from disk and applies them to
        /// the given library list (updating <see cref="Game.PlaytimeMinutes"/> and
        /// <see cref="Game.LastPlayedAt"/> for each matching game).
        /// Call this once at login so the dashboard shows accurate totals.
        /// </summary>
        public static void ApplyStoredPlaytime(List<Game> library)
        {
            try
            {
                var sessions = LoadSessions().Where(s => !s.IsCheckpoint).ToList();
                if (sessions.Count == 0) return;

                // Group sessions by (platform, title) and sum minutes / find latest session
                var grouped = sessions
                    .GroupBy(s => $"{s.Platform.ToLowerInvariant()}||{s.Title.ToLowerInvariant()}")
                    .ToDictionary(
                        g => g.Key,
                        g => (TotalMinutes: g.Sum(s => s.Minutes),
                              LastPlayed:   g.Max(s => s.EndedAt)));

                foreach (var game in library)
                {
                    var key = $"{game.Platform.ToLowerInvariant()}||{game.Title.ToLowerInvariant()}";
                    if (grouped.TryGetValue(key, out var agg))
                    {
                        game.PlaytimeMinutes = agg.TotalMinutes;
                        if (!string.IsNullOrEmpty(agg.LastPlayed))
                            game.LastPlayedAt = agg.LastPlayed;
                    }
                }
            }
            catch { /* best-effort */ }
        }

        /// <summary>Returns total minutes played for a given game (persisted sessions only).</summary>
        public static int GetTotalMinutes(string platform, string title)
        {
            try
            {
                return LoadSessions()
                    .Where(s => !s.IsCheckpoint &&
                                string.Equals(s.Platform, platform, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(s.Title,    title,    StringComparison.OrdinalIgnoreCase))
                    .Sum(s => s.Minutes);
            }
            catch { return 0; }
        }

        /// <summary>
        /// Returns the UTC <see cref="DateTime"/> when the given game was last played
        /// according to persisted sessions, or <see cref="DateTime.MinValue"/> if never.
        /// </summary>
        public static DateTime GetLastPlayedAt(string platform, string title)
        {
            try
            {
                var latest = LoadSessions()
                    .Where(s => !s.IsCheckpoint &&
                                string.Equals(s.Platform, platform, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(s.Title,    title,    StringComparison.OrdinalIgnoreCase) &&
                                !string.IsNullOrEmpty(s.EndedAt))
                    .Select(s => DateTime.TryParse(s.EndedAt, null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt : DateTime.MinValue)
                    .DefaultIfEmpty(DateTime.MinValue)
                    .Max();
                return latest;
            }
            catch { return DateTime.MinValue; }
        }

        public void Dispose()
        {
            foreach (var e in _watching)
            {
                e.Checkpoint?.Dispose();
            }
            _watching.Clear();
        }

        // ── Private helpers ────────────────────────────────────────────────────

        private static string MakeKey(string platform, string title)
            => $"{platform.ToLowerInvariant()}||{title.ToLowerInvariant()}";

        private static void FinaliseSession(string key, string platform, string title,
                                             DateTime startedAt, List<Game>? libraryToUpdate)
        {
            lock (_activeSessionsLock)
                _activeSessions.Remove(key);

            var minutes = Math.Max(1, (int)(DateTime.UtcNow - startedAt).TotalMinutes);

            var session = new PlaySession
            {
                Platform  = platform,
                Title     = title,
                StartedAt = startedAt.ToString("o"),
                EndedAt   = DateTime.UtcNow.ToString("o"),
                Minutes   = minutes,
            };

            // Remove any checkpoint entries for this game before saving the final session
            RemoveCheckpoints(platform, title);
            AppendSession(session);

            if (libraryToUpdate != null)
                UpdateLibraryEntry(libraryToUpdate, platform, title, minutes);

            SessionCompleted?.Invoke(platform, title);
            SessionSaved?.Invoke(session);
        }

        private static void StampLastPlayedAt(List<Game> library, string platform, string title)
        {
            var game = library.FirstOrDefault(g =>
                string.Equals(g.Platform, platform, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(g.Title,    title,    StringComparison.OrdinalIgnoreCase));
            if (game != null)
                game.LastPlayedAt = DateTime.UtcNow.ToString("o");
        }

        private static void AppendSession(PlaySession session)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                var sessions = LoadSessions();
                sessions.Add(session);
                File.WriteAllText(SessionsFile,
                    JsonSerializer.Serialize(sessions, _jsonOpts));
            }
            catch { /* best-effort */ }
        }

        private static void SaveCheckpoint(PlaySession checkpoint)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                var sessions = LoadSessions();
                // Replace any existing checkpoint for the same game with the latest one
                sessions.RemoveAll(s => s.IsCheckpoint &&
                    string.Equals(s.Platform, checkpoint.Platform, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(s.Title,    checkpoint.Title,    StringComparison.OrdinalIgnoreCase));
                sessions.Add(checkpoint);
                File.WriteAllText(SessionsFile,
                    JsonSerializer.Serialize(sessions, _jsonOpts));
            }
            catch { /* best-effort */ }
        }

        private static void RemoveCheckpoints(string platform, string title)
        {
            try
            {
                var sessions = LoadSessions();
                bool changed = sessions.RemoveAll(s => s.IsCheckpoint &&
                    string.Equals(s.Platform, platform, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(s.Title,    title,    StringComparison.OrdinalIgnoreCase)) > 0;
                if (changed)
                    File.WriteAllText(SessionsFile,
                        JsonSerializer.Serialize(sessions, _jsonOpts));
            }
            catch { /* best-effort */ }
        }

        private static List<PlaySession> LoadSessions()
        {
            try
            {
                if (!File.Exists(SessionsFile)) return new List<PlaySession>();
                var json = File.ReadAllText(SessionsFile);
                return JsonSerializer.Deserialize<List<PlaySession>>(json)
                       ?? new List<PlaySession>();
            }
            catch { return new List<PlaySession>(); }
        }

        private static void UpdateLibraryEntry(List<Game> library,
                                               string platform, string title, int newMinutes)
        {
            var game = library.FirstOrDefault(g =>
                string.Equals(g.Platform, platform, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(g.Title,    title,    StringComparison.OrdinalIgnoreCase));

            if (game == null) return;

            game.PlaytimeMinutes += newMinutes;
            game.LastPlayedAt    = DateTime.UtcNow.ToString("o");
        }
    }
}
