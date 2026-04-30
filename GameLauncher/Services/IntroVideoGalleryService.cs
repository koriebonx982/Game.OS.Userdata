using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace GameLauncher.Services
{
    /// <summary>
    /// Represents a single video available in the Game.OS intro-video gallery on GitHub.
    /// </summary>
    public class IntroVideoGalleryItem
    {
        public string Name        { get; set; } = "";
        public string DownloadUrl { get; set; } = "";
        public long   SizeBytes   { get; set; }

        /// <summary>Human-readable file size, e.g. "12.3 MB".</summary>
        public string SizeLabel =>
            SizeBytes >= 1_048_576
                ? $"{SizeBytes / 1_048_576.0:F1} MB"
                : SizeBytes >= 1024
                    ? $"{SizeBytes / 1024.0:F1} KB"
                    : $"{SizeBytes} B";
    }

    /// <summary>
    /// Fetches the list of available intro videos from the public Game-OS GitHub repository
    /// and downloads the user's chosen video into the local AppData cache.
    /// </summary>
    public static class IntroVideoGalleryService
    {
        private const string GalleryApiUrl =
            "https://api.github.com/repos/Koriebonx98/Game-OS/contents/Intro%20Videos";

        /// <summary>Local folder where downloaded intro videos are cached.</summary>
        public static readonly string LocalCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GameOS", "IntroVideos");

        private static readonly string[] VideoExtensions =
            { ".mp4", ".webm", ".avi", ".mkv", ".mov" };

        /// <summary>
        /// Fetches the list of intro videos from the Game.OS GitHub repository.
        /// Returns an empty list if the request fails.
        /// </summary>
        public static async Task<List<IntroVideoGalleryItem>> FetchGalleryAsync(
            CancellationToken ct = default)
        {
            var items = await _http.GetFromJsonAsync<GitHubContentItem[]>(GalleryApiUrl, ct)
                        ?? Array.Empty<GitHubContentItem>();

            return items
                .Where(i => i.Type == "file" && IsVideoFile(i.Name)
                            && !string.IsNullOrEmpty(i.DownloadUrl))
                .Select(i => new IntroVideoGalleryItem
                {
                    Name        = i.Name,
                    DownloadUrl = i.DownloadUrl,
                    SizeBytes   = i.Size,
                })
                .ToList();
        }

        /// <summary>
        /// Downloads <paramref name="item"/> into <see cref="LocalCacheDir"/> and
        /// returns the full local file path.
        /// Reports progress as a value in [0, 1].
        /// </summary>
        public static async Task<string> DownloadVideoAsync(
            IntroVideoGalleryItem item,
            IProgress<double>?    progress = null,
            CancellationToken     ct       = default)
        {
            Directory.CreateDirectory(LocalCacheDir);
            var localPath = Path.Combine(LocalCacheDir, item.Name);

            using var response = await _http.GetAsync(
                item.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? item.SizeBytes;
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            await using var file   = File.Create(localPath);

            var  buffer     = new byte[81_920];
            long downloaded = 0;
            int  read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                downloaded += read;
                if (total > 0)
                    progress?.Report((double)downloaded / total);
            }

            DevLogService.Log($"[IntroVideoGalleryService] Downloaded '{item.Name}' → '{localPath}'");
            return localPath;
        }

        /// <summary>Returns true when a filename has a recognised video extension.</summary>
        public static bool IsVideoFile(string name) =>
            VideoExtensions.Contains(
                Path.GetExtension(name ?? "").ToLowerInvariant());

        // Shared HttpClient — reused across all gallery requests to avoid socket exhaustion.
        private static readonly HttpClient _http = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var http = new HttpClient();
            // GitHub API requires a User-Agent header.
            http.DefaultRequestHeaders.UserAgent.ParseAdd("GameOS-Launcher/1.0");
            return http;
        }
    }

    // ── Internal GitHub API response model ────────────────────────────────────

    internal sealed class GitHubContentItem
    {
        [JsonPropertyName("name")]         public string Name        { get; set; } = "";
        [JsonPropertyName("type")]         public string Type        { get; set; } = "";
        [JsonPropertyName("download_url")] public string DownloadUrl { get; set; } = "";
        [JsonPropertyName("size")]         public long   Size        { get; set; }
    }
}
