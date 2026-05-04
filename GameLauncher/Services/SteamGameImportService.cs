using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace GameLauncher.Services;

/// <summary>
/// Fetches the user's Steam library via the Steam Web API and stores the results
/// in a per-user JSON file so different Game.OS accounts can have different Steam
/// libraries on the same machine.
///
/// Storage path:  <c>Data/{username}/SteamGames.json</c>
/// </summary>
public static class SteamGameImportService
{
    // ── HTTP client (shared) ────────────────────────────────────────────────

    private static readonly HttpClient _http = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    // ── Public API ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the path of the per-user Steam games cache file.
    /// </summary>
    public static string GetCachePath(string username)
    {
        string safeUser = SanitiseName(username);
        return Path.Combine(UserDataService.DataRoot, safeUser, "SteamGames.json");
    }

    /// <summary>
    /// Loads cached Steam games for <paramref name="username"/> from disk.
    /// Returns an empty list if the cache file doesn't exist or cannot be parsed.
    /// </summary>
    public static List<SteamOwnedGame> LoadCached(string username)
    {
        string path = GetCachePath(username);
        if (!File.Exists(path)) return new List<SteamOwnedGame>();
        try
        {
            string json = File.ReadAllText(path);
            var list = JsonSerializer.Deserialize<List<SteamOwnedGame>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return list ?? new List<SteamOwnedGame>();
        }
        catch { return new List<SteamOwnedGame>(); }
    }

    /// <summary>
    /// Calls the Steam Web API to fetch the user's owned games (excluding demos and
    /// free-to-play games that have not been played), persists the result to the
    /// per-user cache file, and returns the list.
    ///
    /// Throws <see cref="InvalidOperationException"/> when the API key or Steam
    /// User ID is missing.  Throws <see cref="HttpRequestException"/> on network errors.
    /// </summary>
    public static async Task<List<SteamOwnedGame>> FetchAndSaveAsync(
        string apiKey, string steamUserId, string username)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Steam API key is not set.");
        if (string.IsNullOrWhiteSpace(steamUserId))
            throw new InvalidOperationException("Steam User ID is not set.");
        if (string.IsNullOrWhiteSpace(username))
            throw new InvalidOperationException("Username is required for the cache path.");

        // GetOwnedGames v1 — include_appinfo gives us name/img_icon_url/img_logo_url
        // include_played_free_games=1 to include free games that the user has played
        string url =
            $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v0001/" +
            $"?key={Uri.EscapeDataString(apiKey)}" +
            $"&steamid={Uri.EscapeDataString(steamUserId)}" +
            $"&include_appinfo=1" +
            $"&include_played_free_games=1" +
            $"&format=json";

        string body = await _http.GetStringAsync(url).ConfigureAwait(false);

        var root = JsonSerializer.Deserialize<SteamGetOwnedGamesResponse>(body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var games = root?.Response?.Games ?? new List<SteamOwnedGame>();

        // Filter out demos: Steam marks demos with a "(Demo)" or "Demo" suffix in the title.
        // Only check the suffix (not Contains) to avoid filtering legitimate titles that
        // contain the word "Demo" elsewhere in their name.
        games.RemoveAll(g =>
            g.Name.EndsWith(" Demo", StringComparison.OrdinalIgnoreCase) ||
            g.Name.EndsWith("(Demo)", StringComparison.OrdinalIgnoreCase));

        // Deduplicate by normalised title: Steam occasionally returns the same game
        // under more than one AppID (e.g. "Alien: Colonial Marines" with different
        // regional AppIDs).  Keep the entry with the highest playtime so the user's
        // most-played copy is used as the canonical entry.
        var dedupedByTitle = new Dictionary<string, SteamOwnedGame>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in games)
        {
            string normTitle = g.Name.Trim();
            if (!dedupedByTitle.TryGetValue(normTitle, out var existing) ||
                g.PlaytimeMinutes > existing.PlaytimeMinutes)
            {
                dedupedByTitle[normTitle] = g;
            }
        }
        if (dedupedByTitle.Count < games.Count)
        {
            games = dedupedByTitle.Values.ToList();
        }

        // Persist to disk
        string cachePath = GetCachePath(username);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            string json = JsonSerializer.Serialize(games,
                new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(cachePath, json).ConfigureAwait(false);
        }
        catch { /* best-effort — in-memory list still returned */ }

        return games;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches the player's achievement status for a single Steam game from the
    /// <c>ISteamUserStats/GetPlayerAchievements</c> endpoint and returns only the
    /// achievements the player has already unlocked.
    /// Returns an empty list when the game has no community stats, when the profile
    /// is private, or on any network/parsing error.
    /// </summary>
    public static async Task<List<SteamPlayerAchievement>> FetchPlayerAchievementsAsync(
        string apiKey, string steamUserId, int appId)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(steamUserId))
            return new List<SteamPlayerAchievement>();

        try
        {
            string url =
                $"https://api.steampowered.com/ISteamUserStats/GetPlayerAchievements/v0001/" +
                $"?appid={appId}" +
                $"&key={Uri.EscapeDataString(apiKey)}" +
                $"&steamid={Uri.EscapeDataString(steamUserId)}" +
                $"&l=en";

            string body = await _http.GetStringAsync(url).ConfigureAwait(false);
            var root = JsonSerializer.Deserialize<SteamGetPlayerAchievementsResponse>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var playerStats = root?.PlayerStats;
            if (playerStats?.Success != true || playerStats.Achievements == null)
                return new List<SteamPlayerAchievement>();

            // Return only unlocked achievements
            var unlocked = new List<SteamPlayerAchievement>();
            foreach (var a in playerStats.Achievements)
            {
                if (a.Achieved == 1)
                    unlocked.Add(a);
            }
            return unlocked;
        }
        catch
        {
            return new List<SteamPlayerAchievement>();
        }
    }

    /// <summary>
    /// Fetches the full achievement schema (ALL achievements, both locked and unlocked)
    /// for a Steam game via <c>ISteamUserStats/GetSchemaForGame</c>.
    /// Returns an empty list when the game has no stats, when the API key is invalid,
    /// or on any network/parsing error.
    /// </summary>
    public static async Task<List<SteamSchemaAchievement>> FetchSchemaForGameAsync(
        string apiKey, int appId)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || appId <= 0)
            return new List<SteamSchemaAchievement>();

        try
        {
            string url =
                $"https://api.steampowered.com/ISteamUserStats/GetSchemaForGame/v0002/" +
                $"?appid={appId}" +
                $"&key={Uri.EscapeDataString(apiKey)}" +
                $"&l=en";

            string body = await _http.GetStringAsync(url).ConfigureAwait(false);
            var root = JsonSerializer.Deserialize<SteamGetSchemaForGameResponse>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return root?.Game?.AvailableGameStats?.Achievements
                ?? new List<SteamSchemaAchievement>();
        }
        catch
        {
            return new List<SteamSchemaAchievement>();
        }
    }

    private static string SanitiseName(string name)
        => string.Concat(name.Split(Path.GetInvalidFileNameChars()));
}

// ── Steam API response models ─────────────────────────────────────────────────

/// <summary>Root response wrapper from IPlayerService/GetOwnedGames.</summary>
public class SteamGetOwnedGamesResponse
{
    [JsonPropertyName("response")] public SteamGetOwnedGamesInner? Response { get; set; }
}

public class SteamGetOwnedGamesInner
{
    [JsonPropertyName("game_count")] public int GameCount { get; set; }
    [JsonPropertyName("games")]      public List<SteamOwnedGame> Games { get; set; } = new();
}

/// <summary>A single Steam game entry from the GetOwnedGames response.</summary>
public class SteamOwnedGame
{
    [JsonPropertyName("appid")]                 public int    AppId                  { get; set; }
    [JsonPropertyName("name")]                  public string Name                   { get; set; } = "";
    [JsonPropertyName("playtime_forever")]      public int    PlaytimeMinutes        { get; set; }
    [JsonPropertyName("img_icon_url")]          public string ImgIconUrl             { get; set; } = "";
    [JsonPropertyName("img_logo_url")]          public string ImgLogoUrl             { get; set; } = "";
    [JsonPropertyName("has_community_visible_stats")] public bool HasCommunityStats  { get; set; }
    [JsonPropertyName("playtime_windows_forever")] public int  PlaytimeWindows       { get; set; }

    /// <summary>Steam store header image URL (600×900 portrait, used as cover art).</summary>
    [JsonIgnore]
    public string CoverUrl =>
        $"https://cdn.cloudflare.steamstatic.com/steam/apps/{AppId}/library_600x900.jpg";

    /// <summary>Steam store page URL for the "View on Steam" button.</summary>
    [JsonIgnore]
    public string StoreUrl => $"https://store.steampowered.com/app/{AppId}";
}

// ── Steam GetPlayerAchievements response models ───────────────────────────────

/// <summary>Root wrapper from ISteamUserStats/GetPlayerAchievements.</summary>
public class SteamGetPlayerAchievementsResponse
{
    [JsonPropertyName("playerstats")] public SteamPlayerStats? PlayerStats { get; set; }
}

public class SteamPlayerStats
{
    [JsonPropertyName("steamID")]      public string? SteamId      { get; set; }
    [JsonPropertyName("gameName")]     public string? GameName     { get; set; }
    [JsonPropertyName("achievements")] public List<SteamPlayerAchievement>? Achievements { get; set; }
    [JsonPropertyName("success")]      public bool    Success      { get; set; }
}

/// <summary>A single achievement entry from the GetPlayerAchievements response.</summary>
public class SteamPlayerAchievement
{
    [JsonPropertyName("apiname")]       public string ApiName      { get; set; } = "";
    [JsonPropertyName("achieved")]      public int    Achieved     { get; set; } // 1 = unlocked, 0 = locked
    [JsonPropertyName("unlocktime")]    public long   UnlockTime   { get; set; } // Unix timestamp
    [JsonPropertyName("name")]          public string Name         { get; set; } = "";
    [JsonPropertyName("description")]   public string Description  { get; set; } = "";

    /// <summary>UTC DateTime of when the achievement was unlocked (MinValue if locked).</summary>
    [JsonIgnore]
    public DateTime UnlockedAt =>
        Achieved == 1 && UnlockTime > 0
            ? DateTimeOffset.FromUnixTimeSeconds(UnlockTime).UtcDateTime
            : DateTime.MinValue;
}

// ── Steam GetSchemaForGame response models ────────────────────────────────────

/// <summary>Root wrapper from ISteamUserStats/GetSchemaForGame.</summary>
public class SteamGetSchemaForGameResponse
{
    [JsonPropertyName("game")] public SteamGameSchema? Game { get; set; }
}

public class SteamGameSchema
{
    [JsonPropertyName("availableGameStats")] public SteamAvailableGameStats? AvailableGameStats { get; set; }
}

public class SteamAvailableGameStats
{
    [JsonPropertyName("achievements")] public List<SteamSchemaAchievement>? Achievements { get; set; }
}

/// <summary>
/// A single achievement entry from the GetSchemaForGame response.
/// Represents the full achievement template (both locked and unlocked).
/// </summary>
public class SteamSchemaAchievement
{
    /// <summary>Internal Steam API name (e.g. "ACH_FIRST_KILL").</summary>
    [JsonPropertyName("name")]        public string ApiName     { get; set; } = "";
    /// <summary>Localised display name shown in the Steam overlay.</summary>
    [JsonPropertyName("displayName")] public string DisplayName { get; set; } = "";
    /// <summary>Localised description of the achievement.</summary>
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    /// <summary>URL to the colour (unlocked) achievement icon.</summary>
    [JsonPropertyName("icon")]        public string Icon        { get; set; } = "";
    /// <summary>URL to the greyscale (locked) achievement icon.</summary>
    [JsonPropertyName("icongray")]    public string IconGray    { get; set; } = "";
}
