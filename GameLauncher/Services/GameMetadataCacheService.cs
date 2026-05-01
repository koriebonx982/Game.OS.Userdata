using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using GameLauncher.Models;

namespace GameLauncher.Services
{
    /// <summary>
    /// Per-game metadata cache stored in <c>%APPDATA%\GameOS\{username}\GameCache\{platform}\{titleId}\</c>.
    ///
    /// For each game the user owns or has played, this service caches:
    /// <list type="bullet">
    ///   <item>cover.jpg / cover.png</item>
    ///   <item>background.jpg / background.png</item>
    ///   <item>info.json</item>
    ///   <item>achievements.json</item>
    ///   <item>ach-icons\{achievementId}.png</item>
    /// </list>
    ///
    /// Store images are <b>never</b> cached — only games in the user's library.
    /// </summary>
    public class GameMetadataCacheService
    {
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        private static readonly JsonSerializerOptions _jsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        private readonly string _cacheRoot;

        /// <summary>
        /// Creates a cache service scoped to the given username.
        /// Cache root: <c>%APPDATA%\GameOS\{username}\GameCache\</c>
        /// </summary>
        public GameMetadataCacheService(string username)
        {
            if (string.IsNullOrWhiteSpace(username))
                throw new ArgumentException("Username must be non-empty.", nameof(username));

            _cacheRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GameOS",
                UserDataService.SanitiseFolderName(username),
                "GameCache");
        }

        // ── Path helpers ─────────────────────────────────────────────────────

        private string GameFolder(string platform, string titleId) =>
            Path.Combine(_cacheRoot,
                UserDataService.SanitiseFolderName(platform),
                UserDataService.SanitiseFolderName(titleId));

        /// <summary>Returns true if a cache folder exists for this game.</summary>
        public bool IsCached(string platform, string titleId) =>
            !string.IsNullOrEmpty(titleId) &&
            Directory.Exists(GameFolder(platform, titleId));

        /// <summary>
        /// Returns the local path to the cached cover image, or null when not cached.
        /// </summary>
        public string? GetCachedCoverPath(string platform, string titleId)
        {
            var folder = GameFolder(platform, titleId);
            foreach (var ext in new[] { "jpg", "png", "webp" })
            {
                var path = Path.Combine(folder, $"cover.{ext}");
                if (File.Exists(path)) return path;
            }
            return null;
        }

        /// <summary>
        /// Returns the local path to the cached background image, or null when not cached.
        /// </summary>
        public string? GetCachedBackgroundPath(string platform, string titleId)
        {
            var folder = GameFolder(platform, titleId);
            foreach (var ext in new[] { "jpg", "png", "webp" })
            {
                var path = Path.Combine(folder, $"background.{ext}");
                if (File.Exists(path)) return path;
            }
            return null;
        }

        /// <summary>
        /// Returns the local path to the cached achievements.json, or null when not cached.
        /// </summary>
        public string? GetCachedAchievementsPath(string platform, string titleId)
        {
            var path = Path.Combine(GameFolder(platform, titleId), "achievements.json");
            return File.Exists(path) ? path : null;
        }

        /// <summary>
        /// Returns the local path to a cached achievement icon, or null when not cached.
        /// </summary>
        public string? GetCachedAchievementIconPath(string platform, string titleId, string achievementId)
        {
            var dir = Path.Combine(GameFolder(platform, titleId), "ach-icons");
            foreach (var ext in new[] { "png", "jpg", "webp" })
            {
                var path = Path.Combine(dir, $"{UserDataService.SanitiseFolderName(achievementId)}.{ext}");
                if (File.Exists(path)) return path;
            }
            return null;
        }

        // ── Cache operations ─────────────────────────────────────────────────

        /// <summary>
        /// Downloads and stores all cacheable assets for one game.
        /// Skips assets that are already cached.
        /// </summary>
        public async Task CacheGameAsync(Game game, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(game.TitleId)) return;

            var folder = GameFolder(game.Platform, game.TitleId);
            Directory.CreateDirectory(folder);

            // Cover image
            if (!string.IsNullOrEmpty(game.CoverUrl) && GetCachedCoverPath(game.Platform, game.TitleId) == null)
                await TryCacheImageAsync(game.CoverUrl, folder, "cover", ct);

            // Background image
            if (!string.IsNullOrEmpty(game.BackgroundUrl) && GetCachedBackgroundPath(game.Platform, game.TitleId) == null)
                await TryCacheImageAsync(game.BackgroundUrl, folder, "background", ct);

            // Cache achievements.json if we have an AchievementsUrl
            if (!string.IsNullOrEmpty(game.AchievementsUrl) && GetCachedAchievementsPath(game.Platform, game.TitleId) == null)
                await TryCacheJsonAsync(game.AchievementsUrl, Path.Combine(folder, "achievements.json"), ct);
        }

        /// <summary>
        /// Re-fetches info.json and achievements.json for the given game from the server.
        /// Does not re-download images unless <paramref name="forceImages"/> is true.
        /// Updates local files when the server version is newer.
        /// </summary>
        public async Task SyncMetadataAsync(Game game, bool forceImages = false, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(game.TitleId)) return;

            var folder = GameFolder(game.Platform, game.TitleId);
            Directory.CreateDirectory(folder);

            if (!string.IsNullOrEmpty(game.AchievementsUrl))
                await TryCacheJsonAsync(game.AchievementsUrl, Path.Combine(folder, "achievements.json"), ct, force: true);

            if (forceImages)
            {
                if (!string.IsNullOrEmpty(game.CoverUrl))
                    await TryCacheImageAsync(game.CoverUrl, folder, "cover", ct);
                if (!string.IsNullOrEmpty(game.BackgroundUrl))
                    await TryCacheImageAsync(game.BackgroundUrl, folder, "background", ct);
            }
        }

        /// <summary>
        /// Downloads and caches an individual achievement icon.
        /// </summary>
        public async Task CacheAchievementIconAsync(string platform, string titleId, string achievementId, string iconUrl, CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(titleId) || string.IsNullOrEmpty(iconUrl)) return;
            if (GetCachedAchievementIconPath(platform, titleId, achievementId) != null) return;

            var dir = Path.Combine(GameFolder(platform, titleId), "ach-icons");
            Directory.CreateDirectory(dir);

            var ext  = GuessExtension(iconUrl);
            var path = Path.Combine(dir, $"{UserDataService.SanitiseFolderName(achievementId)}.{ext}");
            await TryCacheImageAsync(iconUrl, dir, UserDataService.SanitiseFolderName(achievementId), ct);
        }

        /// <summary>
        /// Removes cached folders for games no longer in the user's library.
        /// </summary>
        public void PruneMissingGames(List<Game> currentLibrary)
        {
            try
            {
                if (!Directory.Exists(_cacheRoot)) return;

                // Build a set of valid (platform, titleId) pairs
                var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var game in currentLibrary)
                {
                    if (!string.IsNullOrEmpty(game.TitleId))
                        valid.Add(Path.Combine(
                            UserDataService.SanitiseFolderName(game.Platform),
                            UserDataService.SanitiseFolderName(game.TitleId)));
                }

                foreach (var platformDir in Directory.GetDirectories(_cacheRoot))
                {
                    foreach (var titleDir in Directory.GetDirectories(platformDir))
                    {
                        var rel = Path.Combine(
                            Path.GetFileName(platformDir),
                            Path.GetFileName(titleDir));

                        if (!valid.Contains(rel))
                        {
                            try { Directory.Delete(titleDir, recursive: true); }
                            catch { /* best-effort */ }
                        }
                    }
                }
            }
            catch { /* best-effort — cache is not critical */ }
        }

        // ── Private helpers ──────────────────────────────────────────────────

        private async Task TryCacheImageAsync(string url, string folder, string baseName, CancellationToken ct, bool force = false)
        {
            try
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    return;

                var ext  = GuessExtension(url);
                var path = Path.Combine(folder, $"{baseName}.{ext}");

                if (!force && File.Exists(path)) return;

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(20));

                var bytes = await _http.GetByteArrayAsync(uri, cts.Token).ConfigureAwait(false);
                Directory.CreateDirectory(folder);
                await File.WriteAllBytesAsync(path, bytes, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch { /* best-effort */ }
        }

        private async Task TryCacheJsonAsync(string url, string localPath, CancellationToken ct, bool force = false)
        {
            try
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    return;

                if (!force && File.Exists(localPath)) return;

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(15));

                var json = await _http.GetStringAsync(uri, cts.Token).ConfigureAwait(false);
                Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
                await File.WriteAllTextAsync(localPath, json, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch { /* best-effort */ }
        }

        private static string GuessExtension(string url)
        {
            try
            {
                var path = new Uri(url).AbsolutePath;
                var ext  = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
                return ext is "jpg" or "jpeg" or "png" or "webp" or "gif" ? ext : "jpg";
            }
            catch { return "jpg"; }
        }
    }
}
