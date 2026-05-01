using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using GameLauncher.Models;

namespace GameLauncher.Services
{
    /// <summary>
    /// Per-system, per-game metadata cache stored in <c>Data\GameCache\{platform}\{titleId}\</c>
    /// next to the Game.OS executable.  The cache is shared across all user accounts on the
    /// same machine so covers and achievement definitions are only downloaded once regardless
    /// of which user is logged in.
    ///
    /// For each game the service caches:
    /// <list type="bullet">
    ///   <item>cover.jpg / cover.png</item>
    ///   <item>background.jpg / background.png</item>
    ///   <item>achievements.json</item>
    ///   <item>ach-icons\{achievementId}.png</item>
    /// </list>
    ///
    /// Store games are <b>never</b> cached — only games present in the local library.
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
        /// Creates the per-system cache service.
        /// Cache root: <c>{exe_dir}\Data\GameCache\</c>
        /// </summary>
        public GameMetadataCacheService()
        {
            _cacheRoot = Path.Combine(UserDataService.DataRoot, "GameCache");
        }

        // ── Path helpers ─────────────────────────────────────────────────────

        /// <summary>
        /// Returns the cache folder path for a game.
        /// <paramref name="key"/> is the TitleId when available; otherwise a sanitised
        /// version of the game title is used so PC games (which have no TitleId) can
        /// still be cached.
        /// </summary>
        private string GameFolder(string platform, string key) =>
            Path.Combine(_cacheRoot,
                UserDataService.SanitiseFolderName(platform),
                UserDataService.SanitiseFolderName(key));

        /// <summary>
        /// Resolves the best cache key for a game: TitleId when available, otherwise
        /// the game title (sanitised for use as a folder name).
        /// </summary>
        private static string ResolveKey(string? titleId, string? title) =>
            !string.IsNullOrEmpty(titleId) ? titleId :
            !string.IsNullOrEmpty(title)   ? UserDataService.SanitiseFolderName(title) : "";

        /// <summary>Returns true if a cache folder exists for this game.</summary>
        public bool IsCached(string platform, string titleId) =>
            !string.IsNullOrEmpty(titleId) &&
            Directory.Exists(GameFolder(platform, titleId));

        /// <summary>
        /// Returns the local path to the cached cover image, or null when not cached.
        /// </summary>
        public string? GetCachedCoverPath(string platform, string? titleId, string? title = null)
        {
            var key = ResolveKey(titleId, title);
            if (string.IsNullOrEmpty(key)) return null;
            var folder = GameFolder(platform, key);
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
        public string? GetCachedBackgroundPath(string platform, string? titleId, string? title = null)
        {
            var key = ResolveKey(titleId, title);
            if (string.IsNullOrEmpty(key)) return null;
            var folder = GameFolder(platform, key);
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
        public string? GetCachedAchievementsPath(string platform, string? titleId, string? title = null)
        {
            var key = ResolveKey(titleId, title);
            if (string.IsNullOrEmpty(key)) return null;
            var path = Path.Combine(GameFolder(platform, key), "achievements.json");
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
        /// Downloads and stores all cacheable assets for one cloud-library game.
        /// Uses TitleId as the cache key when available; otherwise falls back to the
        /// game title so PC games without a TitleId are also cached.
        /// Skips assets that are already cached.
        /// </summary>
        public async Task CacheGameAsync(Game game, CancellationToken ct = default)
        {
            var key = ResolveKey(game.TitleId, game.Title);
            if (string.IsNullOrEmpty(key)) return;

            var folder = GameFolder(game.Platform, key);
            Directory.CreateDirectory(folder);

            // Cover image
            if (!string.IsNullOrEmpty(game.CoverUrl) && GetCachedCoverPath(game.Platform, game.TitleId, game.Title) == null)
                await TryCacheImageAsync(game.CoverUrl, folder, "cover", ct);

            // Cache achievements.json if we have an AchievementsUrl
            if (!string.IsNullOrEmpty(game.AchievementsUrl) && GetCachedAchievementsPath(game.Platform, game.TitleId, game.Title) == null)
                await TryCacheJsonAsync(game.AchievementsUrl, Path.Combine(folder, "achievements.json"), ct);
        }

        /// <summary>
        /// Caches cover art and achievements.json for a locally detected game (ROM or PC game)
        /// using explicit metadata fetched from the Games.Database.
        /// Skips assets that are already cached.
        /// </summary>
        public async Task CacheLocalGameAsync(string platform, string? titleId, string title,
                                              string? coverUrl, string? achievementsUrl,
                                              CancellationToken ct = default)
        {
            var key = ResolveKey(titleId, title);
            if (string.IsNullOrEmpty(key)) return;

            var folder = GameFolder(platform, key);
            Directory.CreateDirectory(folder);

            if (!string.IsNullOrEmpty(coverUrl) && GetCachedCoverPath(platform, titleId, title) == null)
                await TryCacheImageAsync(coverUrl, folder, "cover", ct);

            if (!string.IsNullOrEmpty(achievementsUrl) && GetCachedAchievementsPath(platform, titleId, title) == null)
                await TryCacheJsonAsync(achievementsUrl, Path.Combine(folder, "achievements.json"), ct);
        }

        /// <summary>
        /// Re-fetches achievements.json for the given game from the server.
        /// Does not re-download images unless <paramref name="forceImages"/> is true.
        /// Updates local files when the server version is newer.
        /// </summary>
        public async Task SyncMetadataAsync(Game game, bool forceImages = false, CancellationToken ct = default)
        {
            var key = ResolveKey(game.TitleId, game.Title);
            if (string.IsNullOrEmpty(key)) return;

            var folder = GameFolder(game.Platform, key);
            Directory.CreateDirectory(folder);

            if (!string.IsNullOrEmpty(game.AchievementsUrl))
                await TryCacheJsonAsync(game.AchievementsUrl, Path.Combine(folder, "achievements.json"), ct, force: true);

            if (forceImages && !string.IsNullOrEmpty(game.CoverUrl))
                await TryCacheImageAsync(game.CoverUrl, folder, "cover", ct);
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

            await TryCacheImageAsync(iconUrl, dir, UserDataService.SanitiseFolderName(achievementId), ct);
        }

        /// <summary>
        /// Removes cached folders for games no longer in the user's library or local game list.
        /// </summary>
        public void PruneMissingGames(IEnumerable<(string Platform, string Key)> currentEntries)
        {
            try
            {
                if (!Directory.Exists(_cacheRoot)) return;

                var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (platform, key) in currentEntries)
                {
                    if (!string.IsNullOrEmpty(key))
                        valid.Add(Path.Combine(
                            UserDataService.SanitiseFolderName(platform),
                            UserDataService.SanitiseFolderName(key)));
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

        /// <summary>
        /// Overload that accepts a cloud library game list for pruning.
        /// </summary>
        public void PruneMissingGames(List<Game> currentLibrary)
        {
            var entries = currentLibrary
                .Select(g => (g.Platform, Key: ResolveKey(g.TitleId, g.Title)));
            PruneMissingGames(entries);
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
                var dir  = Path.GetDirectoryName(localPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
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
