using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using GameLauncher.Models;

namespace GameLauncher.Services
{
    /// <summary>
    /// Game.OS backend REST API client.
    ///
    /// Authenticates users and reads/writes their data via the Node.js backend
    /// server — exactly the same way the web frontend calls the backend when
    /// <c>getBackendBase()</c> returns a URL.  The server holds the GitHub PAT;
    /// the launcher never needs to store or bundle one.
    ///
    /// Login flow (mirrors the web frontend's backend-mode path):
    ///   1. POST {baseUrl}/api/auth/token  { username, password }
    ///      → { success, token, username }
    ///   2. Subsequent calls use:  Authorization: Bearer {token}
    ///
    /// Configuration (via environment variable):
    ///   GAMEOS_BACKEND_URL  – base URL of the deployed backend server
    ///                         e.g. https://gameos.up.railway.app
    ///                         or   http://localhost:3000 for local dev
    /// </summary>
    public sealed class BackendApiService : IDisposable
    {
        // ── Configuration ─────────────────────────────────────────────────────

        /// <summary>
        /// Base URL of the backend server, resolved from the environment variable
        /// <c>GAMEOS_BACKEND_URL</c>.  Returns <c>null</c> when the variable is not set.
        /// </summary>
        public static readonly string? BackendUrl = ResolveBackendUrl();

        /// <summary>Returns <c>true</c> when a backend URL has been configured.</summary>
        public static bool IsConfigured => !string.IsNullOrEmpty(BackendUrl);

        private static string? ResolveBackendUrl()
        {
            // 1. Environment variable (developer / CI override)
            var url = Environment.GetEnvironmentVariable("GAMEOS_BACKEND_URL")?.Trim().TrimEnd('/');
            if (!string.IsNullOrEmpty(url)) return url;

            // 2. Bundled URL file injected at build/publish time
            //    (mirrors gameos-token.dat for the GitHub PAT)
            //    Also checks the current working directory so that running from Visual Studio
            //    (where the working directory is the project folder) picks up the file.
            var candidateDirs = new[]
            {
                AppContext.BaseDirectory,
                System.Environment.CurrentDirectory,
            };

            foreach (var dir in candidateDirs)
            {
                try
                {
                    var urlFile = System.IO.Path.Combine(dir, "gameos-backend.url");
                    if (System.IO.File.Exists(urlFile))
                    {
                        var fileUrl = System.IO.File.ReadAllText(urlFile).Trim().TrimEnd('/');
                        if (!string.IsNullOrEmpty(fileUrl))
                            return fileUrl;
                    }
                }
                catch (System.IO.IOException ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[BackendApiService] Failed to read gameos-backend.url in {dir}: {ex.Message}");
                }
                catch (UnauthorizedAccessException ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[BackendApiService] Access denied reading gameos-backend.url in {dir}: {ex.Message}");
                }
            }

            return null;
        }

        // ── HTTP client ───────────────────────────────────────────────────────

        private readonly HttpClient _http;
        private string? _bearerToken;

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        public BackendApiService()
        {
            _http = new HttpClient
            {
                BaseAddress = new Uri(BackendUrl ?? "http://localhost:3000"),
                Timeout     = TimeSpan.FromSeconds(15),
            };
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("GameOS-Launcher/2.0");
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        }

        // ── Authentication ────────────────────────────────────────────────────

        /// <summary>
        /// Verify credentials against the backend server and obtain a bearer token.
        /// Mirrors the web frontend's call to  POST {backend}/api/auth/token.
        /// Returns the user's profile on success (username + email from the token response).
        /// </summary>
        public async Task<(UserProfile Profile, string Token)> LoginAsync(
            string usernameOrEmail, string password, CancellationToken ct = default)
        {
            var body = new { username = usernameOrEmail, password };
            using var resp = await _http.PostAsJsonAsync("/api/auth/token", body, ct);

            if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                throw new GameOsException(429, "Too many login attempts — please wait a minute and try again.");

            var data = await resp.Content
                .ReadFromJsonAsync<AuthTokenResponse>(_jsonOpts, ct)
                ?? throw new GameOsException(500, "Empty response from backend.");

            if (!resp.IsSuccessStatusCode || !data.Success)
                throw new GameOsException(
                    (int)resp.StatusCode,
                    data.Message ?? "Invalid username/email or password.");

            _bearerToken = data.Token
                ?? throw new GameOsException(500, "Backend did not return a token.");

            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _bearerToken);

            var profile = new UserProfile
            {
                Username  = data.Username ?? usernameOrEmail,
                Email     = data.Email    ?? "",
                CreatedAt = "",
            };
            return (profile, _bearerToken);
        }

        /// <summary>
        /// Restore a session using a previously-issued bearer token.
        /// Calls GET /api/me to verify the token is still valid.
        /// </summary>
        public async Task<UserProfile> RestoreSessionAsync(string bearerToken, CancellationToken ct = default)
        {
            _bearerToken = bearerToken;
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _bearerToken);

            return await GetProfileAsync(ct);
        }

        // ── Profile ───────────────────────────────────────────────────────────

        /// <summary>Fetch the authenticated user's profile via GET /api/me.</summary>
        public async Task<UserProfile> GetProfileAsync(CancellationToken ct = default)
        {
            EnsureAuthenticated();
            using var resp = await _http.GetAsync("/api/me", ct);

            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                throw new GameOsException(401, "Session expired. Please log in again.");

            resp.EnsureSuccessStatusCode();

            var data = await resp.Content
                .ReadFromJsonAsync<MeResponse>(_jsonOpts, ct)
                ?? throw new GameOsException(500, "Empty response from backend.");

            return data.Profile ?? throw new GameOsException(500, "Backend returned no profile.");
        }

        // ── Games ─────────────────────────────────────────────────────────────

        /// <summary>Fetch the authenticated user's game library via GET /api/me/games.</summary>
        public async Task<List<Game>> GetGamesAsync(CancellationToken ct = default)
        {
            EnsureAuthenticated();
            using var resp = await _http.GetAsync("/api/me/games", ct);
            resp.EnsureSuccessStatusCode();

            var data = await resp.Content
                .ReadFromJsonAsync<GamesResponse>(_jsonOpts, ct);
            return data?.Games ?? new List<Game>();
        }

        /// <summary>Add a game to the authenticated user's library via POST /api/me/games.</summary>
        public async Task AddGameAsync(Game game, CancellationToken ct = default)
        {
            EnsureAuthenticated();
            var body = new { platform = game.Platform, title = game.Title, titleId = game.TitleId };
            using var resp = await _http.PostAsJsonAsync("/api/me/games", body, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadFromJsonAsync<ErrorResponse>(_jsonOpts, ct);
                throw new GameOsException((int)resp.StatusCode, err?.Message ?? "Failed to add game.");
            }
        }

        /// <summary>
        /// Removes a game from the authenticated user's library via DELETE /api/me/games.</summary>
        public async Task RemoveGameAsync(string platform, string title, CancellationToken ct = default)
        {
            EnsureAuthenticated();
            var request = new HttpRequestMessage(HttpMethod.Delete, "/api/me/games")
            {
                Content = JsonContent.Create(new { platform, title }),
            };
            using var resp = await _http.SendAsync(request, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadFromJsonAsync<ErrorResponse>(_jsonOpts, ct);
                throw new GameOsException((int)resp.StatusCode, err?.Message ?? "Failed to remove game.");
            }
        }

        /// <summary>
        /// Updates a game's accumulated playtime and lastPlayedAt in games.json via
        /// PATCH /api/me/games/playtime.  Non-fatal — failures are swallowed.
        /// Called immediately when a session ends so other devices see the new total
        /// on their next sync tick.
        /// </summary>
        public async Task UpdateGamePlaytimeAsync(
            string platform, string title, int totalMinutes, string lastPlayedAt,
            CancellationToken ct = default)
        {
            try
            {
                EnsureAuthenticated();
                var body = new { platform, title, playtimeMinutes = totalMinutes, lastPlayedAt };
                using var req = new HttpRequestMessage(
                    new HttpMethod("PATCH"), "/api/me/games/playtime")
                {
                    Content = JsonContent.Create(body),
                };
                using var resp = await _http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode)
                    System.Diagnostics.Debug.WriteLine(
                        $"[BackendApiService] UpdateGamePlaytime HTTP {(int)resp.StatusCode}: {title} ({platform})");
            }
            catch { /* best-effort */ }
        }

        // ── Achievements ──────────────────────────────────────────────────────

        /// <summary>Fetch the authenticated user's achievements via GET /api/me/achievements.</summary>
        public async Task<List<Achievement>> GetAchievementsAsync(CancellationToken ct = default)
        {
            EnsureAuthenticated();
            using var resp = await _http.GetAsync("/api/me/achievements", ct);
            resp.EnsureSuccessStatusCode();

            var data = await resp.Content
                .ReadFromJsonAsync<AchievementsResponse>(_jsonOpts, ct);
            return data?.Achievements ?? new List<Achievement>();
        }

        // ── Friends ───────────────────────────────────────────────────────────

        /// <summary>Fetch the authenticated user's friends list via GET /api/me/friends.</summary>
        public async Task<List<string>> GetFriendsAsync(CancellationToken ct = default)
        {
            EnsureAuthenticated();
            using var resp = await _http.GetAsync("/api/me/friends", ct);
            resp.EnsureSuccessStatusCode();

            var data = await resp.Content
                .ReadFromJsonAsync<FriendsResponse>(_jsonOpts, ct);
            return data?.Friends ?? new List<string>();
        }

        public async Task<List<FriendRequest>> GetFriendRequestsAsync(
            string username, CancellationToken ct = default)
        {
            using var resp = await _http.GetAsync(
                $"/api/get-friend-requests?username={Uri.EscapeDataString(username)}", ct);
            resp.EnsureSuccessStatusCode();

            var data = await resp.Content
                .ReadFromJsonAsync<FriendRequestsResponse>(_jsonOpts, ct);
            return data?.FriendRequests ?? new List<FriendRequest>();
        }

        public async Task SendFriendRequestAsync(
            string fromUsername, string toUsername, CancellationToken ct = default)
        {
            EnsureAuthenticated();
            var body = new { username = fromUsername, friendUsername = toUsername };
            using var resp = await _http.PostAsJsonAsync("/api/send-friend-request", body, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadFromJsonAsync<ErrorResponse>(_jsonOpts, ct);
                throw new GameOsException((int)resp.StatusCode, err?.Message ?? "Failed to send friend request.");
            }
        }

        public async Task AcceptFriendRequestAsync(
            string username, string fromUsername, CancellationToken ct = default)
        {
            EnsureAuthenticated();
            var body = new { username, fromUsername };
            using var resp = await _http.PostAsJsonAsync("/api/accept-friend-request", body, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadFromJsonAsync<ErrorResponse>(_jsonOpts, ct);
                throw new GameOsException((int)resp.StatusCode, err?.Message ?? "Failed to accept friend request.");
            }
        }

        public async Task DeclineFriendRequestAsync(
            string username, string fromUsername, CancellationToken ct = default)
        {
            EnsureAuthenticated();
            var body = new { username, fromUsername };
            using var resp = await _http.PostAsJsonAsync("/api/decline-friend-request", body, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadFromJsonAsync<ErrorResponse>(_jsonOpts, ct);
                throw new GameOsException((int)resp.StatusCode, err?.Message ?? "Failed to decline friend request.");
            }
        }

        // ── Presence ──────────────────────────────────────────────────────────

        /// <summary>
        /// Updates the logged-in user's presence timestamp and optionally sets the
        /// currently playing game.  Non-fatal — failures are swallowed.
        /// </summary>
        public async Task UpdatePresenceAsync(string username, string? currentGame = null,
                                              CancellationToken ct = default)
        {
            try
            {
                object body = string.IsNullOrEmpty(currentGame)
                    ? (object)new { username }
                    : new { username, currentGame };
                using var resp = await _http.PostAsJsonAsync("/api/update-presence", body, ct);
                // Presence updates are non-critical — failures should not interrupt user operations
            }
            catch { }
        }

        /// <summary>
        /// Fetches full presence data (lastSeen + currentGame) for the given user.
        /// Returns <c>null</c> when offline or the user is not found.
        /// </summary>
        public async Task<PresenceData?> GetFullPresenceAsync(string username,
                                                              CancellationToken ct = default)
        {
            try
            {
                using var resp = await _http.GetAsync(
                    $"/api/get-presence?username={Uri.EscapeDataString(username)}", ct);
                if (!resp.IsSuccessStatusCode) return null;

                var data = await resp.Content
                    .ReadFromJsonAsync<FullPresenceResponse>(_jsonOpts, ct);
                if (data == null) return null;
                return new PresenceData
                {
                    Username    = username,
                    LastSeen    = data.LastSeen,
                    CurrentGame = data.CurrentGame,
                };
            }
            catch { return null; }
        }

        public async Task<string?> GetPresenceAsync(string username, CancellationToken ct = default)
        {
            try
            {
                using var resp = await _http.GetAsync(
                    $"/api/get-presence?username={Uri.EscapeDataString(username)}", ct);
                if (!resp.IsSuccessStatusCode) return null;

                var data = await resp.Content
                    .ReadFromJsonAsync<FullPresenceResponse>(_jsonOpts, ct);
                return data?.LastSeen;
            }
            catch { return null; }
        }

        // ── Messages ──────────────────────────────────────────────────────────

        public async Task SendMessageAsync(
            string fromUsername, string toUsername, string text, CancellationToken ct = default)
        {
            EnsureAuthenticated();
            var body = new { from = fromUsername, to = toUsername, text };
            using var resp = await _http.PostAsJsonAsync("/api/send-message", body, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadFromJsonAsync<ErrorResponse>(_jsonOpts, ct);
                throw new GameOsException((int)resp.StatusCode, err?.Message ?? "Failed to send message.");
            }
        }

        public async Task<List<Message>> GetMessagesAsync(
            string username, string withUsername, CancellationToken ct = default)
        {
            using var resp = await _http.GetAsync(
                $"/api/get-messages?username={Uri.EscapeDataString(username)}&with={Uri.EscapeDataString(withUsername)}", ct);
            resp.EnsureSuccessStatusCode();

            var data = await resp.Content
                .ReadFromJsonAsync<MessagesResponse>(_jsonOpts, ct);
            return data?.Messages ?? new List<Message>();
        }

        // ── Registration ──────────────────────────────────────────────────────

        /// <summary>
        /// Register a new account via POST /api/create-account.
        /// Returns the new profile and the bearer token issued for it.
        /// </summary>
        public async Task<(UserProfile Profile, string Token)> RegisterAsync(
            string username, string email, string password, CancellationToken ct = default)
        {
            var body = new { username, email, password };
            using var resp = await _http.PostAsJsonAsync("/api/create-account", body, ct);

            var data = await resp.Content
                .ReadFromJsonAsync<CreateAccountResponse>(_jsonOpts, ct)
                ?? throw new GameOsException(500, "Empty response from backend.");

            if (!resp.IsSuccessStatusCode || !data.Success)
                throw new GameOsException(
                    (int)resp.StatusCode,
                    data.Message ?? "Failed to create account.");

            _bearerToken = data.Token
                ?? throw new GameOsException(500, "Backend did not return a token after registration.");

            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _bearerToken);

            var profile = new UserProfile
            {
                Username  = data.Username ?? username,
                Email     = email,
                CreatedAt = DateTimeOffset.UtcNow.ToString("o"),
            };
            return (profile, _bearerToken);
        }

        // ── Activity / Playtime ───────────────────────────────────────────────────

        /// <summary>
        /// Logs a completed play session to the user's cloud activity log
        /// via POST /api/me/activity.  Non-fatal — failures are swallowed.
        /// </summary>
        public async Task LogActivityAsync(
            string platform, string gameTitle, string? titleId,
            DateTime startedAt, DateTime endedAt, int minutesPlayed,
            CancellationToken ct = default)
        {
            try
            {
                EnsureAuthenticated();
                var body = new
                {
                    platform,
                    gameTitle,
                    titleId,
                    sessionStart  = startedAt.ToString("o"),
                    sessionEnd    = endedAt.ToString("o"),
                    minutesPlayed,
                };
                using var resp = await _http.PostAsJsonAsync("/api/me/activity", body, ct);
                if (!resp.IsSuccessStatusCode)
                    System.Diagnostics.Debug.WriteLine(
                        $"[BackendApiService] LogActivity HTTP {(int)resp.StatusCode}: {gameTitle} ({platform})");
                // Non-critical — do not throw on failure
            }
            catch { /* best-effort */ }
        }

        /// <summary>
        /// Fetches the user's full cloud activity log via GET /api/me/activity.
        /// Returns an empty list when offline or on error.
        /// </summary>
        public async Task<List<GameLauncher.Models.ActivityEntry>> GetActivityAsync(
            CancellationToken ct = default)
        {
            try
            {
                EnsureAuthenticated();
                using var resp = await _http.GetAsync("/api/me/activity", ct);
                if (!resp.IsSuccessStatusCode) return new();
                var data = await resp.Content
                    .ReadFromJsonAsync<ActivityResponse>(_jsonOpts, ct);
                return data?.Activity ?? new();
            }
            catch { return new(); }
        }

        /// <summary>
        /// Logs an achievement-unlock event to the user's cloud activity log
        /// via POST /api/me/activity with type "achievement_unlocked".  Non-fatal — failures are swallowed.
        /// </summary>
        public async Task LogAchievementUnlockAsync(
            string platform, string gameTitle, string? titleId,
            string achievementName, string? achievementIcon = null,
            CancellationToken ct = default)
        {
            try
            {
                EnsureAuthenticated();
                var body = new
                {
                    type            = "achievement_unlocked",
                    platform,
                    gameTitle,
                    titleId,
                    achievementName,
                    achievementIcon,
                    sessionStart    = DateTime.UtcNow.ToString("o"),
                    minutesPlayed   = 0,
                };
                using var resp = await _http.PostAsJsonAsync("/api/me/activity", body, ct);
                if (!resp.IsSuccessStatusCode)
                    System.Diagnostics.Debug.WriteLine(
                        $"[BackendApiService] LogAchievementUnlock HTTP {(int)resp.StatusCode}: {achievementName} in {gameTitle}");
            }
            catch { /* best-effort */ }
        }

        // ── Sync signal (cross-device heartbeat) ──────────────────────────────

        /// <summary>
        /// Writes a sync signal to the cloud (POST /api/me/sync-signal).
        /// Called immediately after a play session is pushed so other open instances
        /// detect the change within their 30-second heartbeat poll and refresh.
        /// Non-fatal — failures are swallowed.
        /// </summary>
        public async Task WriteSyncSignalAsync(CancellationToken ct = default)
        {
            try
            {
                EnsureAuthenticated();
                using var resp = await _http.PostAsJsonAsync("/api/me/sync-signal", new { }, ct);
                if (!resp.IsSuccessStatusCode)
                    System.Diagnostics.Debug.WriteLine(
                        $"[BackendApiService] WriteSyncSignal HTTP {(int)resp.StatusCode}");
            }
            catch { /* best-effort */ }
        }

        /// <summary>
        /// Reads the sync signal from the cloud (GET /api/me/sync-signal).
        /// Returns the <c>lastActivityAt</c> ISO timestamp, or <c>null</c> when not set or on error.
        /// </summary>
        public async Task<string?> ReadSyncSignalAsync(CancellationToken ct = default)
        {
            try
            {
                EnsureAuthenticated();
                using var resp = await _http.GetAsync("/api/me/sync-signal", ct);
                if (!resp.IsSuccessStatusCode) return null;
                var data = await resp.Content
                    .ReadFromJsonAsync<SyncSignalResponse>(_jsonOpts, ct);
                return data?.Signal?.LastActivityAt;
            }
            catch { return null; }
        }

        // ── Health check ──────────────────────────────────────────────────────

        /// <summary>Returns <c>true</c> when the backend server is reachable.</summary>
        public async Task<bool> CheckHealthAsync(CancellationToken ct = default)
        {
            try
            {
                using var resp = await _http.GetAsync("/health", ct);
                return resp.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void EnsureAuthenticated()
        {
            if (string.IsNullOrEmpty(_bearerToken))
                throw new GameOsException(401, "Not authenticated.");
        }

        /// <summary>
        /// Clears the stored bearer token and HTTP Authorization header so the next
        /// login uses fresh credentials.  Called by <see cref="GameOsClient.Logout"/>
        /// to prevent the stale token from leaking into a subsequent account's requests.
        /// </summary>
        public void ClearAuthentication()
        {
            _bearerToken = null;
            _http.DefaultRequestHeaders.Authorization = null;
        }

        public void Dispose() => _http.Dispose();

        // ── Response DTOs ─────────────────────────────────────────────────────

        private sealed class AuthTokenResponse
        {
            [JsonPropertyName("success")]  public bool    Success  { get; set; }
            [JsonPropertyName("token")]    public string? Token    { get; set; }
            [JsonPropertyName("username")] public string? Username { get; set; }
            [JsonPropertyName("email")]    public string? Email    { get; set; }
            [JsonPropertyName("message")]  public string? Message  { get; set; }
        }

        private sealed class MeResponse
        {
            [JsonPropertyName("success")] public bool         Success { get; set; }
            [JsonPropertyName("profile")] public UserProfile? Profile { get; set; }
        }

        private sealed class GamesResponse
        {
            [JsonPropertyName("success")] public bool       Success { get; set; }
            [JsonPropertyName("games")]   public List<Game>? Games  { get; set; }
        }

        private sealed class AchievementsResponse
        {
            [JsonPropertyName("success")]      public bool              Success      { get; set; }
            [JsonPropertyName("achievements")] public List<Achievement>? Achievements { get; set; }
        }

        private sealed class FriendsResponse
        {
            [JsonPropertyName("success")] public bool          Success { get; set; }
            [JsonPropertyName("friends")] public List<string>? Friends { get; set; }
        }

        private sealed class FriendRequestsResponse
        {
            [JsonPropertyName("success")]        public bool                Success        { get; set; }
            [JsonPropertyName("friendRequests")] public List<FriendRequest>? FriendRequests { get; set; }
        }

        private sealed class PresenceResponse
        {
            [JsonPropertyName("lastSeen")] public string? LastSeen { get; set; }
        }

        private sealed class FullPresenceResponse
        {
            [JsonPropertyName("lastSeen")]    public string? LastSeen    { get; set; }
            [JsonPropertyName("currentGame")] public string? CurrentGame { get; set; }
        }

        private sealed class MessagesResponse
        {
            [JsonPropertyName("success")]  public bool          Success  { get; set; }
            [JsonPropertyName("messages")] public List<Message>? Messages { get; set; }
        }

        private sealed class CreateAccountResponse
        {
            [JsonPropertyName("success")]  public bool    Success  { get; set; }
            [JsonPropertyName("token")]    public string? Token    { get; set; }
            [JsonPropertyName("username")] public string? Username { get; set; }
            [JsonPropertyName("message")]  public string? Message  { get; set; }
        }

        private sealed class ErrorResponse
        {
            [JsonPropertyName("message")] public string? Message { get; set; }
        }

        private sealed class ActivityResponse
        {
            [JsonPropertyName("success")]  public bool Success { get; set; }
            [JsonPropertyName("activity")] public List<GameLauncher.Models.ActivityEntry>? Activity { get; set; }
        }

        private sealed class SyncSignalResponse
        {
            [JsonPropertyName("success")] public bool                             Success { get; set; }
            [JsonPropertyName("signal")]  public GameLauncher.Models.SyncSignal?  Signal  { get; set; }
        }
    }
}
