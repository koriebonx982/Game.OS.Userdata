using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameLauncher.Models;

namespace GameLauncher.Services
{
    /// <summary>
    /// Persists and loads the user's profile, game library and achievements to a local
    /// JSON cache so the app can function fully offline after the first successful login.
    ///
    /// Cache location (per-user, nested inside the shared GameOS config directory):
    ///   Windows : %APPDATA%\GameOS\{username}\userdata.json
    ///   Linux   : ~/.config/GameOS/{username}/userdata.json
    ///   macOS   : ~/Library/Application Support/GameOS/{username}/userdata.json
    ///
    /// The file stores a <see cref="CachedUserData"/> envelope that includes a
    /// <c>cachedAt</c> timestamp so callers can decide whether the cache is stale.
    /// </summary>
    public class OfflineDataCacheService
    {
        // ── Constants ─────────────────────────────────────────────────────────

        /// <summary>
        /// Cache files older than this are considered stale.  The caller still
        /// uses the stale cache for the offline UI but will attempt a background
        /// sync when online.
        /// 24 hours matches the typical daily session pattern: a user who logged in
        /// yesterday will get cached data on reconnect today and sync immediately.
        /// </summary>
        private static readonly TimeSpan StalenessThreshold = TimeSpan.FromHours(24);

        private static readonly JsonSerializerOptions _json = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        // ── Storage paths ─────────────────────────────────────────────────────

        private static readonly string BaseDir =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GameOS");

        private static string CacheFileFor(string username)
        {
            string safe = StorageHelpers.SanitiseName(username);
            return Path.Combine(BaseDir, safe, "userdata.json");
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Persists <paramref name="profile"/>, <paramref name="games"/> and
        /// <paramref name="achievements"/> to the local cache for
        /// <paramref name="username"/>.  Called after every successful online login
        /// or sync so the cache always reflects the latest server state.
        /// </summary>
        public void Save(
            string username,
            UserProfile profile,
            List<Game> games,
            List<Achievement> achievements)
        {
            if (string.IsNullOrWhiteSpace(username)) return;

            try
            {
                string path = CacheFileFor(username);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                var envelope = new CachedUserData
                {
                    Username     = username,
                    CachedAt     = DateTime.UtcNow.ToString("o"),
                    Profile      = profile,
                    Games        = games,
                    Achievements = achievements,
                };

                File.WriteAllText(path, JsonSerializer.Serialize(envelope, _json));
                System.Diagnostics.Debug.WriteLine(
                    $"[OfflineCache] Saved {games.Count} games and {achievements.Count} " +
                    $"achievements for '{username}'.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[OfflineCache] Could not write cache for '{username}': {ex.Message}");
            }
        }

        /// <summary>
        /// Loads the cached data for <paramref name="username"/>.
        /// Returns <c>null</c> when no cache file exists.
        /// </summary>
        public CachedUserData? Load(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return null;

            try
            {
                string path = CacheFileFor(username);
                if (!File.Exists(path)) return null;

                var data = JsonSerializer.Deserialize<CachedUserData>(
                    File.ReadAllText(path), _json);

                if (data != null)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[OfflineCache] Loaded cache for '{username}' " +
                        $"(cached at {data.CachedAt}, " +
                        $"{data.Games?.Count ?? 0} games, " +
                        $"{data.Achievements?.Count ?? 0} achievements).");
                }

                return data;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[OfflineCache] Could not read cache for '{username}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Returns <c>true</c> when a cache file exists for <paramref name="username"/>
        /// (whether or not it is still fresh).
        /// </summary>
        public bool HasCache(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return false;
            return File.Exists(CacheFileFor(username));
        }

        /// <summary>
        /// Returns <c>true</c> when the cache for <paramref name="username"/> was
        /// written within the last <see cref="StalenessThreshold"/> period.
        /// </summary>
        public bool IsFresh(string username)
        {
            var data = Load(username);
            if (data == null) return false;

            if (!DateTime.TryParse(data.CachedAt, null,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var cachedAt))
                return false;

            return DateTime.UtcNow - cachedAt < StalenessThreshold;
        }

        /// <summary>Deletes the local cache file for <paramref name="username"/>.</summary>
        public void Clear(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return;

            try
            {
                string path = CacheFileFor(username);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[OfflineCache] Could not delete cache for '{username}': {ex.Message}");
            }
        }

        /// <summary>
        /// Enumerates all usernames that have a valid local cache file.
        /// Discovers profiles by looking for <c>userdata.json</c> under each
        /// per-user subfolder of <see cref="BaseDir"/>.
        /// Returns an empty list when the base directory does not exist.
        /// </summary>
        public List<string> EnumerateAllCachedUsers()
        {
            var result = new List<string>();
            if (!Directory.Exists(BaseDir)) return result;

            try
            {
                foreach (var dir in Directory.GetDirectories(BaseDir))
                {
                    var cacheFile = Path.Combine(dir, "userdata.json");
                    if (!File.Exists(cacheFile)) continue;

                    try
                    {
                        var data = JsonSerializer.Deserialize<CachedUserData>(
                            File.ReadAllText(cacheFile), _json);
                        if (!string.IsNullOrWhiteSpace(data?.Username))
                            result.Add(data.Username);
                    }
                    catch { /* skip corrupt cache files */ }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[OfflineCache] Could not enumerate cached users: {ex.Message}");
            }

            return result;
        }

        // ── Private helpers ───────────────────────────────────────────────────
    }

    /// <summary>
    /// Envelope written to <c>userdata.json</c> by <see cref="OfflineDataCacheService"/>.
    /// </summary>
    public class CachedUserData
    {
        [JsonPropertyName("username")]     public string           Username     { get; set; } = "";
        [JsonPropertyName("cachedAt")]     public string           CachedAt     { get; set; } = "";
        [JsonPropertyName("profile")]      public UserProfile?     Profile      { get; set; }
        [JsonPropertyName("games")]        public List<Game>?      Games        { get; set; }
        [JsonPropertyName("achievements")] public List<Achievement>? Achievements { get; set; }
    }
}
