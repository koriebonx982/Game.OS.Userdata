using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using GameLauncher.Models;

namespace GameLauncher.Services
{
    /// <summary>
    /// Direct GitHub API client for Game.OS user data.
    ///
    /// Mirrors the JavaScript helpers in script.js:
    ///   githubRead()           → <see cref="ReadFileAsync{T}"/>
    ///   githubWrite()          → <see cref="WriteFileAsync"/>
    ///   hashPassword()         → <see cref="HashPassword"/>
    ///   verifyAccountGitHub()  → <see cref="VerifyLoginAsync"/>
    ///   createAccountGitHub()  → <see cref="CreateAccountAsync"/>
    ///
    /// Token resolution (same priority as the web frontend):
    ///   1. GAMEOS_GITHUB_TOKEN environment variable (developer / CI override)
    ///   2. gameos-token.dat alongside the executable, XOR-encoded with key
    ///      "GameOS_KEY" — injected at build time by build-csharp-launcher.yml
    ///      using the same encoding as deploy.yml uses for GITHUB_TOKEN_ENCODED
    ///      in script.js.  The empty placeholder is committed to the repo.
    ///
    /// Other configuration (via environment variables):
    ///   GAMEOS_DATA_REPO_OWNER  – GitHub account that owns the private data repo
    ///                             (default: Koriebonx98)
    ///   GAMEOS_DATA_REPO_NAME   – repository name for user data
    ///                             (default: Game.OS.Private.Data)
    /// </summary>
    public sealed class GitHubDataService : IDisposable
    {
        // ── Configuration ─────────────────────────────────────────────────────
        public static readonly string DataRepoOwner =
            Environment.GetEnvironmentVariable("GAMEOS_DATA_REPO_OWNER") ?? "Koriebonx98";

        public static readonly string DataRepoName =
            Environment.GetEnvironmentVariable("GAMEOS_DATA_REPO_NAME") ?? "Game.OS.Private.Data";

        /// <summary>
        /// Resolved GitHub PAT.  Uses the same priority order as the web frontend:
        /// env var override → bundled gameos-token.dat → null (no auth, public repos only).
        /// </summary>
        public static readonly string? GitHubToken = ResolveGitHubToken();

        /// <summary>
        /// Resolve the GitHub PAT.
        /// Priority:
        ///   1. GAMEOS_GITHUB_TOKEN environment variable (developer / CI override)
        ///   2. gameos-token.dat in the application directory, XOR-decoded with key
        ///      "GameOS_KEY" — the same encoding used by GITHUB_TOKEN_ENCODED in
        ///      script.js and injected by the deploy / build workflows.
        /// </summary>
        private static string? ResolveGitHubToken()
        {
            // 1. Environment variable (developer or CI override)
            var envToken = Environment.GetEnvironmentVariable("GAMEOS_GITHUB_TOKEN");
            if (!string.IsNullOrEmpty(envToken)) return envToken;

            // 2. Bundled token file injected at build/publish time
            //    (mirrors GITHUB_TOKEN_ENCODED = '...' in script.js)
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
                    var tokenFile = System.IO.Path.Combine(dir, "gameos-token.dat");
                    if (System.IO.File.Exists(tokenFile))
                    {
                        var encoded = System.IO.File.ReadAllText(tokenFile).Trim();
                        if (!string.IsNullOrEmpty(encoded))
                        {
                            var decoded = DecodeXorToken(encoded);
                            if (decoded != null)
                                return decoded;
                        }
                    }
                }
                catch (Exception ex) when (
                    ex is System.IO.IOException or
                    UnauthorizedAccessException or
                    System.IO.DirectoryNotFoundException)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[GitHubDataService] Could not read gameos-token.dat in {dir}: {ex.Message}");
                }
            }

            return null;
        }

        /// <summary>
        /// XOR-decode a token encoded with key "GameOS_KEY".
        /// Exactly reverses the encoding used by deploy.yml and build-csharp-launcher.yml,
        /// and mirrors the JavaScript decoder in script.js:
        ///   bytes.map((h, i) => String.fromCharCode(parseInt(h, 16) ^ key.charCodeAt(i % key.length)))
        /// Returns null if the hex string is malformed (odd length or invalid characters).
        /// </summary>
        private static string? DecodeXorToken(string xorHex)
        {
            const string key = "GameOS_KEY";
            if (xorHex.Length % 2 != 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[GitHubDataService] gameos-token.dat contains an odd-length hex string — " +
                    "the file may be corrupted or only partially written.");
                return null;
            }
            try
            {
                var sb = new StringBuilder(xorHex.Length / 2);
                for (int i = 0; i < xorHex.Length; i += 2)
                {
                    var b = Convert.ToByte(xorHex.Substring(i, 2), 16);
                    sb.Append((char)(b ^ key[(i / 2) % key.Length]));
                }
                return sb.ToString();
            }
            catch (FormatException ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[GitHubDataService] gameos-token.dat contains invalid hex data: {ex.Message}");
                return null;
            }
        }

        private readonly HttpClient _http;

        private static readonly JsonSerializerOptions _jsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented               = true,
        };

        public GitHubDataService()
        {
            _http = new HttpClient();
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("GameOS-Launcher/2.0");
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

            if (!string.IsNullOrEmpty(GitHubToken))
                _http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", GitHubToken);
        }

        // ── Password hashing ──────────────────────────────────────────────────
        /// <summary>
        /// Hash a password using PBKDF2-SHA256 with 100,000 iterations.
        /// Exactly matches <c>hashPassword(password, username)</c> in script.js.
        /// Salt is <c>{username_lower}:gameos</c>, output is 32-byte hex string.
        /// </summary>
        public static string HashPassword(string password, string username)
        {
            var salt      = Encoding.UTF8.GetBytes($"{username.ToLowerInvariant()}:gameos");
            using var kdf = new Rfc2898DeriveBytes(
                password, salt, 100_000, HashAlgorithmName.SHA256);
            return Convert.ToHexString(kdf.GetBytes(32)).ToLowerInvariant();
        }

        // ── GitHub file I/O ───────────────────────────────────────────────────
        /// <summary>
        /// Read and decode a JSON file from the data repository.
        /// Returns (<c>default</c>, <c>null</c>) when the file does not exist (404).
        /// Mirrors <c>githubRead(path)</c> in script.js.
        /// </summary>
        public async Task<(T? Content, string? Sha)> ReadFileAsync<T>(
            string path, CancellationToken ct = default)
        {
            var url  = $"https://api.github.com/repos/{DataRepoOwner}/{DataRepoName}/contents/{path}";
            var resp = await _http.GetAsync(url, ct);

            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return (default, null);

            if (!resp.IsSuccessStatusCode)
                throw new GameOsException(
                    (int)resp.StatusCode,
                    $"GitHub API error {(int)resp.StatusCode} reading {path}");

            var file = await resp.Content
                .ReadFromJsonAsync<GitHubFileResponse>(cancellationToken: ct)
                ?? throw new GameOsException(500, "Invalid GitHub API response");

            var bytes   = Convert.FromBase64String(file.Content.Replace("\n", ""));
            var json    = Encoding.UTF8.GetString(bytes);
            var content = JsonSerializer.Deserialize<T>(json, _jsonOpts);
            return (content, file.Sha);
        }

        /// <summary>
        /// Create or overwrite a JSON file in the data repository.
        /// Pass <paramref name="sha"/> when overwriting an existing file.
        /// Mirrors <c>githubWrite(path, content, message, sha)</c> in script.js.
        /// </summary>
        public async Task WriteFileAsync(
            string path, object content, string message,
            string? sha = null, CancellationToken ct = default)
        {
            var json   = JsonSerializer.Serialize(content, _jsonOpts);
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

            var body = new GitHubWriteBody
            {
                Message   = message,
                Content   = base64,
                Sha       = sha,
                Committer = new GitHubCommitter
                {
                    Name  = "Game.OS Bot",
                    Email = "game-os-bot@users.noreply.github.com"
                }
            };

            var url  = $"https://api.github.com/repos/{DataRepoOwner}/{DataRepoName}/contents/{path}";
            var resp = await _http.PutAsJsonAsync(url, body, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                throw new GameOsException(
                    (int)resp.StatusCode,
                    $"GitHub write error {(int)resp.StatusCode}: {err}");
            }
        }

        // ── Authentication ────────────────────────────────────────────────────
        /// <summary>
        /// Verify username/email and password against the GitHub data repository.
        /// Mirrors <c>verifyAccountGitHub(identifier, password)</c> in script.js.
        /// Returns the <see cref="UserProfile"/> on success, or <c>null</c> on failure.
        /// </summary>
        public async Task<UserProfile?> VerifyLoginAsync(
            string identifier, string password, CancellationToken ct = default)
        {
            string accountKey;
            if (identifier.Contains('@'))
            {
                // Email login: look up username via email index
                var (index, _) = await ReadFileAsync<Dictionary<string, string>>(
                    "accounts/email-index.json", ct);
                if (index == null) return null;
                if (!index.TryGetValue(identifier.ToLowerInvariant(), out accountKey!))
                    return null;
            }
            else
            {
                accountKey = identifier.ToLowerInvariant();
            }

            var (profile, _) = await ReadFileAsync<UserProfile>(
                $"accounts/{accountKey}/profile.json", ct);
            if (profile == null) return null;

            if (!VerifyPassword(password, profile.PasswordHash, profile.Username))
                return null;

            return profile;
        }

        /// <summary>
        /// Verify a password against a stored hash that may be either a bcrypt hash
        /// (produced by the Node.js backend — starts with "$2") or a PBKDF2 hex hash
        /// (produced by the JavaScript frontend in GitHub mode).
        /// Mirrors <c>verifyPassword(password, storedHash, username)</c> in backend/index.js.
        /// </summary>
        private static bool VerifyPassword(string password, string storedHash, string username)
        {
            if (IsBcryptHash(storedHash))
            {
                try { return BCrypt.Net.BCrypt.Verify(password, storedHash); }
                catch { return false; }
            }

            var pbkdf2Hash = HashPassword(password, username);
            return string.Equals(pbkdf2Hash, storedHash, StringComparison.Ordinal);
        }

        /// <summary>Returns true when the stored hash was produced by bcrypt (starts with "$2").</summary>
        private static bool IsBcryptHash(string hash) =>
            !string.IsNullOrEmpty(hash) && hash.StartsWith("$2", StringComparison.Ordinal);

        /// <summary>
        /// Register a new account in the GitHub data repository.
        /// Mirrors <c>createAccountGitHub(username, email, password)</c> in script.js.
        /// Throws <see cref="GameOsException"/> if the username or email is already taken.
        /// </summary>
        public async Task<UserProfile> CreateAccountAsync(
            string username, string email, string password, CancellationToken ct = default)
        {
            var usernameLower = username.ToLowerInvariant();
            var emailLower    = email.ToLowerInvariant();

            // Duplicate username check
            var (existing, _) = await ReadFileAsync<UserProfile>(
                $"accounts/{usernameLower}/profile.json", ct);
            if (existing != null)
                throw new GameOsException(409, "Username already exists.");

            // Duplicate email check
            var (indexContent, indexSha) = await ReadFileAsync<Dictionary<string, string>>(
                "accounts/email-index.json", ct);
            var emailMap = indexContent ?? new Dictionary<string, string>();
            if (emailMap.ContainsKey(emailLower))
                throw new GameOsException(409, "Email already registered.");

            // Create profile
            var profile = new UserProfile
            {
                Username     = username,
                Email        = email,
                PasswordHash = HashPassword(password, username),
                CreatedAt    = DateTimeOffset.UtcNow.ToString("o"),
            };
            await WriteFileAsync(
                $"accounts/{usernameLower}/profile.json",
                profile,
                $"Create account: {username}",
                null, ct);

            // Update email index (up to 3 attempts to handle concurrent write conflicts,
            // matching the retry loop in script.js createAccountGitHub)
            emailMap[emailLower] = usernameLower;
            string? currentIndexSha = indexSha;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    await WriteFileAsync(
                        "accounts/email-index.json",
                        emailMap,
                        $"Add email index for: {username}",
                        currentIndexSha, ct);
                    break;
                }
                catch (GameOsException ex) when (attempt < 2)
                {
                    // GitHub returns 409 Conflict when the SHA is stale — re-read for
                    // the updated SHA then retry with a brief back-off, mirroring the
                    // retry loop in script.js createAccountGitHub.
                    System.Diagnostics.Debug.WriteLine(
                        $"[GitHubDataService] Email index write conflict (attempt {attempt + 1}): {ex.Message}");
                    var (refreshed, refreshedSha) = await ReadFileAsync<Dictionary<string, string>>(
                        "accounts/email-index.json", ct);
                    emailMap             = refreshed ?? new Dictionary<string, string>();
                    emailMap[emailLower] = usernameLower;
                    currentIndexSha      = refreshedSha;
                    await Task.Delay(200 * (attempt + 1), ct);
                }
            }

            return profile;
        }

        // ── User data reads ───────────────────────────────────────────────────
        public async Task<UserProfile?> GetProfileAsync(
            string username, CancellationToken ct = default)
        {
            var (profile, _) = await ReadFileAsync<UserProfile>(
                $"accounts/{username.ToLowerInvariant()}/profile.json", ct);
            return profile;
        }

        public async Task<List<Game>> GetGamesAsync(
            string username, CancellationToken ct = default)
        {
            var (games, _) = await ReadFileAsync<List<Game>>(
                $"accounts/{username.ToLowerInvariant()}/games.json", ct);
            return games ?? new List<Game>();
        }

        public async Task<List<Achievement>> GetAchievementsAsync(
            string username, CancellationToken ct = default)
        {
            var (achievements, _) = await ReadFileAsync<List<Achievement>>(
                $"accounts/{username.ToLowerInvariant()}/achievements.json", ct);
            return achievements ?? new List<Achievement>();
        }

        public async Task<List<string>> GetFriendsAsync(
            string username, CancellationToken ct = default)
        {
            var (friends, _) = await ReadFileAsync<List<string>>(
                $"accounts/{username.ToLowerInvariant()}/friends.json", ct);
            return friends ?? new List<string>();
        }

        public async Task<List<FriendRequest>> GetFriendRequestsAsync(
            string username, CancellationToken ct = default)
        {
            var (requests, _) = await ReadFileAsync<List<FriendRequest>>(
                $"accounts/{username.ToLowerInvariant()}/friend_requests.json", ct);
            return requests ?? new List<FriendRequest>();
        }

        public async Task<string?> GetPresenceAsync(
            string username, CancellationToken ct = default)
        {
            try
            {
                var (data, _) = await ReadFileAsync<PresenceData>(
                    $"accounts/{username.ToLowerInvariant()}/presence.json", ct);
                return data?.LastSeen;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[GitHubDataService] GetPresence failed for '{username}': {ex.Message}");
                return null;
            }
        }

        // ── User data writes ──────────────────────────────────────────────────
        /// <summary>
        /// Add a game to the user's library, persisting the full metadata
        /// (cover URL, genre, description, rating, screenshots) so it round-trips
        /// through GitHub exactly the way the website stores game data.
        /// </summary>
        public async Task AddGameAsync(
            string username, Game game,
            CancellationToken ct = default)
        {
            var key = $"accounts/{username.ToLowerInvariant()}/games.json";
            var (games, sha) = await ReadFileAsync<List<Game>>(key, ct);
            var library = games ?? new List<Game>();

            // Don't add duplicates (same title + platform)
            if (library.Any(g =>
                    string.Equals(g.Title,    game.Title,    StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(g.Platform, game.Platform, StringComparison.OrdinalIgnoreCase)))
                return;

            // Stamp AddedAt if the caller left it blank
            if (string.IsNullOrEmpty(game.AddedAt))
                game.AddedAt = DateTimeOffset.UtcNow.ToString("o");

            library.Add(game);
            await WriteFileAsync(key, library, $"Add game '{game.Title}' for {username}", sha, ct);
        }

        public async Task RemoveGameAsync(
            string username, string platform, string title,
            CancellationToken ct = default)
        {
            var key = $"accounts/{username.ToLowerInvariant()}/games.json";
            var (games, sha) = await ReadFileAsync<List<Game>>(key, ct);
            if (games == null) return;

            games.RemoveAll(g =>
                string.Equals(g.Platform, platform, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(g.Title,    title,    StringComparison.OrdinalIgnoreCase));

            await WriteFileAsync(key, games, $"Remove game '{title}' for {username}", sha, ct);
        }

        /// <summary>
        /// Updates a game's accumulated playtime and lastPlayedAt in games.json so other
        /// devices see the new total on their next sync tick.  Retries up to 3 times on
        /// SHA-conflict.  Non-fatal — failures are swallowed.
        /// </summary>
        public async Task UpdateGamePlaytimeAsync(
            string username, string platform, string title,
            int totalMinutes, string lastPlayedAt,
            CancellationToken ct = default)
        {
            var key = $"accounts/{username.ToLowerInvariant()}/games.json";

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var (games, sha) = await ReadFileAsync<List<Game>>(key, ct);
                    if (games == null) return;

                    var game = games.FirstOrDefault(g =>
                        string.Equals(g.Platform, platform, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(g.Title,    title,    StringComparison.OrdinalIgnoreCase));

                    if (game == null) return; // local-only ROM not in cloud library — skip

                    // Only advance playtime forward to avoid stale writes overwriting newer data
                    if (totalMinutes > game.PlaytimeMinutes)
                        game.PlaytimeMinutes = totalMinutes;
                    // Only advance lastPlayedAt forward (ISO 8601 strings sort lexicographically)
                    if (string.Compare(lastPlayedAt, game.LastPlayedAt, StringComparison.Ordinal) > 0)
                        game.LastPlayedAt = lastPlayedAt;

                    await WriteFileAsync(key, games,
                        $"Playtime: {title} ({platform}) – {game.PlaytimeMinutes}min",
                        sha, ct);
                    return;
                }
                catch (GameOsException ex) when (ex.StatusCode == 409 && attempt < 2)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[GitHubDataService] UpdateGamePlaytime SHA conflict (attempt {attempt + 1}): {ex.Message}");
                    await Task.Delay(200 * (attempt + 1), ct);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[GitHubDataService] UpdateGamePlaytime failed: {ex.Message}");
                    return;
                }
            }
        }

        // ── Activity / Playtime ───────────────────────────────────────────────
        /// <summary>
        /// Appends a completed play session to the user's cloud activity log
        /// at <c>accounts/{username}/activity.json</c>.  Trims the log to the
        /// last 500 entries (same cap as the Node.js backend).
        /// Retries up to 3 times on a GitHub SHA-conflict (409) to handle
        /// concurrent writes from multiple devices.
        /// Non-fatal — failures are swallowed.
        /// </summary>
        public async Task LogActivityAsync(
            string username, string platform, string gameTitle, string? titleId,
            DateTime startedAt, DateTime endedAt, int minutesPlayed,
            CancellationToken ct = default)
        {
            const int maxEntries = 500;
            var key = $"accounts/{username.ToLowerInvariant()}/activity.json";

            var entry = new Models.ActivityEntry
            {
                Platform      = platform,
                GameTitle     = gameTitle,
                TitleId       = titleId,
                SessionStart  = startedAt.ToString("o"),
                SessionEnd    = endedAt.ToString("o"),
                MinutesPlayed = minutesPlayed,
                LoggedAt      = DateTimeOffset.UtcNow.ToString("o"),
            };

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var (existing, sha) = await ReadFileAsync<List<Models.ActivityEntry>>(key, ct);
                    var log = existing ?? new List<Models.ActivityEntry>();
                    log.Add(entry);
                    if (log.Count > maxEntries)
                        log.RemoveRange(0, log.Count - maxEntries);

                    await WriteFileAsync(key, log,
                        $"Activity: {gameTitle} ({platform}) – {minutesPlayed}min",
                        sha, ct);
                    return;
                }
                catch (GameOsException ex) when (ex.StatusCode == 409 && attempt < 2)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[GitHubDataService] LogActivity SHA conflict (attempt {attempt + 1}): {ex.Message}");
                    await Task.Delay(200 * (attempt + 1), ct);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[GitHubDataService] LogActivity failed: {ex.Message}");
                    return;
                }
            }
        }

        /// <summary>
        /// Reads the user's full cloud activity log from
        /// <c>accounts/{username}/activity.json</c>.
        /// Returns an empty list when the file does not exist or on error.
        /// </summary>
        public async Task<List<Models.ActivityEntry>> GetActivityAsync(
            string username, CancellationToken ct = default)
        {
            try
            {
                var (entries, _) = await ReadFileAsync<List<Models.ActivityEntry>>(
                    $"accounts/{username.ToLowerInvariant()}/activity.json", ct);
                return entries ?? new List<Models.ActivityEntry>();
            }
            catch { return new List<Models.ActivityEntry>(); }
        }

        public async Task UpdatePresenceAsync(
            string username, CancellationToken ct = default)
        {
            try
            {
                var key = $"accounts/{username.ToLowerInvariant()}/presence.json";
                var (_, sha) = await ReadFileAsync<PresenceData>(key, ct);
                var data = new PresenceData
                {
                    Username = username,
                    LastSeen = DateTimeOffset.UtcNow.ToString("o"),
                };
                await WriteFileAsync(key, data, $"Update presence: {username}", sha, ct);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[GitHubDataService] UpdatePresence failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Writes a sync signal to <c>accounts/{username}/sync-signal.json</c> so other
        /// open instances detect the change within their 30-second heartbeat poll.
        /// Non-fatal — failures are swallowed.
        /// </summary>
        public async Task WriteSyncSignalAsync(string username, CancellationToken ct = default)
        {
            try
            {
                var key = $"accounts/{username.ToLowerInvariant()}/sync-signal.json";
                var (_, sha) = await ReadFileAsync<Models.SyncSignal>(key, ct);
                var signal = new Models.SyncSignal
                {
                    LastActivityAt = DateTimeOffset.UtcNow.ToString("o"),
                };
                await WriteFileAsync(key, signal, $"Sync signal: {username}", sha, ct);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[GitHubDataService] WriteSyncSignal failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Reads the sync signal from <c>accounts/{username}/sync-signal.json</c>.
        /// Returns the <c>lastActivityAt</c> ISO timestamp, or <c>null</c> when not set or on error.
        /// </summary>
        public async Task<string?> ReadSyncSignalAsync(string username, CancellationToken ct = default)
        {
            try
            {
                var (signal, _) = await ReadFileAsync<Models.SyncSignal>(
                    $"accounts/{username.ToLowerInvariant()}/sync-signal.json", ct);
                return signal?.LastActivityAt;
            }
            catch { return null; }
        }

        public async Task SendMessageAsync(
            string from, string to, string text, CancellationToken ct = default)
        {
            // Thread file key is always <lower_alphabetical_first>_<lower_alphabetical_second>
            var (a, b) = BuildThreadKey(from, to);
            var key = $"accounts/messages/{a}_{b}.json";

            var (existing, sha) = await ReadFileAsync<List<Message>>(key, ct);
            var thread = existing ?? new List<Message>();
            thread.Add(new Message
            {
                From   = from,
                Text   = text,
                SentAt = DateTimeOffset.UtcNow.ToString("o"),
            });
            await WriteFileAsync(key, thread, $"Message from {from} to {to}", sha, ct);
        }

        public async Task<List<Message>> GetMessagesAsync(
            string username, string withUsername, CancellationToken ct = default)
        {
            var (a, b) = BuildThreadKey(username, withUsername);
            var (messages, _) = await ReadFileAsync<List<Message>>(
                $"accounts/messages/{a}_{b}.json", ct);
            return messages ?? new List<Message>();
        }

        public async Task SendFriendRequestAsync(
            string from, string to, CancellationToken ct = default)
        {
            var fromLower = from.ToLowerInvariant();
            var toLower   = to.ToLowerInvariant();

            // Add to recipient's incoming requests
            var reqKey = $"accounts/{toLower}/friend_requests.json";
            var (requests, reqSha) = await ReadFileAsync<List<FriendRequest>>(reqKey, ct);
            var reqList = requests ?? new List<FriendRequest>();
            reqList.Add(new FriendRequest { From = from, SentAt = DateTimeOffset.UtcNow.ToString("o") });
            await WriteFileAsync(reqKey, reqList, $"Friend request from {from} to {to}", reqSha, ct);

            // Add to sender's sent requests.
            // In sent_requests.json the FriendRequest.From field holds the RECIPIENT
            // (the person the request was sent to), so that AcceptFriendRequestAsync
            // can locate and remove this entry by matching on the accepter's username.
            var sentKey = $"accounts/{fromLower}/sent_requests.json";
            var (sent, sentSha) = await ReadFileAsync<List<FriendRequest>>(sentKey, ct);
            var sentList = sent ?? new List<FriendRequest>();
            sentList.Add(new FriendRequest { From = to, SentAt = DateTimeOffset.UtcNow.ToString("o") });
            await WriteFileAsync(sentKey, sentList, $"Sent friend request from {from} to {to}", sentSha, ct);
        }

        public async Task AcceptFriendRequestAsync(
            string username, string fromUsername, CancellationToken ct = default)
        {
            var userLower = username.ToLowerInvariant();
            var fromLower = fromUsername.ToLowerInvariant();

            // Remove from incoming requests
            var reqKey = $"accounts/{userLower}/friend_requests.json";
            var (requests, reqSha) = await ReadFileAsync<List<FriendRequest>>(reqKey, ct);
            if (requests != null)
            {
                requests.RemoveAll(r =>
                    string.Equals(r.From, fromUsername, StringComparison.OrdinalIgnoreCase));
                await WriteFileAsync(reqKey, requests,
                    $"Accept friend request from {fromUsername}", reqSha, ct);
            }

            // Remove from sender's sent requests
            var sentKey = $"accounts/{fromLower}/sent_requests.json";
            var (sent, sentSha) = await ReadFileAsync<List<FriendRequest>>(sentKey, ct);
            if (sent != null)
            {
                sent.RemoveAll(r =>
                    string.Equals(r.From, username, StringComparison.OrdinalIgnoreCase));
                await WriteFileAsync(sentKey, sent,
                    $"Friend request accepted by {username}", sentSha, ct);
            }

            // Add to both users' friends lists
            await AddFriendAsync(userLower, fromUsername, ct);
            await AddFriendAsync(fromLower, username, ct);
        }

        public async Task DeclineFriendRequestAsync(
            string username, string fromUsername, CancellationToken ct = default)
        {
            var userLower = username.ToLowerInvariant();
            var fromLower = fromUsername.ToLowerInvariant();

            var reqKey = $"accounts/{userLower}/friend_requests.json";
            var (requests, reqSha) = await ReadFileAsync<List<FriendRequest>>(reqKey, ct);
            if (requests != null)
            {
                requests.RemoveAll(r =>
                    string.Equals(r.From, fromUsername, StringComparison.OrdinalIgnoreCase));
                await WriteFileAsync(reqKey, requests,
                    $"Decline friend request from {fromUsername}", reqSha, ct);
            }

            var sentKey = $"accounts/{fromLower}/sent_requests.json";
            var (sent, sentSha) = await ReadFileAsync<List<FriendRequest>>(sentKey, ct);
            if (sent != null)
            {
                sent.RemoveAll(r =>
                    string.Equals(r.From, username, StringComparison.OrdinalIgnoreCase));
                await WriteFileAsync(sentKey, sent,
                    $"Friend request declined by {username}", sentSha, ct);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private async Task AddFriendAsync(
            string userLower, string friendUsername, CancellationToken ct)
        {
            var key = $"accounts/{userLower}/friends.json";
            var (friends, sha) = await ReadFileAsync<List<string>>(key, ct);
            var list = friends ?? new List<string>();
            if (!list.Exists(f => string.Equals(f, friendUsername,
                                                 StringComparison.OrdinalIgnoreCase)))
                list.Add(friendUsername);
            await WriteFileAsync(key, list, $"Add friend {friendUsername} for {userLower}", sha, ct);
        }

        private static (string a, string b) BuildThreadKey(string user1, string user2)
        {
            var lo1 = user1.ToLowerInvariant();
            var lo2 = user2.ToLowerInvariant();
            return string.Compare(lo1, lo2, StringComparison.Ordinal) <= 0
                ? (lo1, lo2)
                : (lo2, lo1);
        }

        public void Dispose() => _http.Dispose();

        // ── Public App Store (no auth required) ──────────────────────────────

        /// <summary>
        /// Raw URL for the App Store data file in the public Koriebonx98/AppStore- repository.
        /// </summary>
        private static readonly string AppStoreDataUrl =
            "https://raw.githubusercontent.com/Koriebonx98/AppStore-/main/data.json";

        /// <summary>Cached in-memory copy of the App Store entry list.</summary>
        private static List<AppStoreEntry>? _appStoreCache;

        /// <summary>
        /// Fetches all app entries from the public Koriebonx98/AppStore- repository.
        /// Results are cached in memory for the session lifetime.
        /// </summary>
        public static async Task<List<AppStoreEntry>> FetchAppStoreAsync(
            CancellationToken ct = default)
        {
            if (_appStoreCache != null)
                return _appStoreCache;

            try
            {
                var resp = await _rawHttp.GetAsync(AppStoreDataUrl, ct);
                if (!resp.IsSuccessStatusCode)
                    return new List<AppStoreEntry>();

                var json = await resp.Content.ReadAsStringAsync(ct);
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var entries = JsonSerializer.Deserialize<List<AppStoreEntry>>(json, opts)
                              ?? new List<AppStoreEntry>();

                _appStoreCache = entries;
                return entries;
            }
            catch
            {
                return new List<AppStoreEntry>();
            }
        }

        // ── Public Games Database (no auth required) ──────────────────────────

        /// <summary>
        /// Base URL for raw content in the public Koriebonx98/Games.Database repository.
        /// Mirrors <c>GAMES_DB_RAW_BASE</c> in script.js.
        /// </summary>
        public static readonly string GamesDbRawBase =
            "https://raw.githubusercontent.com/Koriebonx98/Games.Database/main";

        /// <summary>
        /// The known platforms in the Games.Database repository.
        /// Mirrors <c>GAMES_DB_PLATFORMS</c> in script.js.
        /// </summary>
        public static readonly string[] GamesDbPlatforms =
            { "PC", "PS3", "PS4", "Switch", "Xbox 360" };

        // Shared HttpClient for public raw.githubusercontent.com fetches (no auth needed).
        // Static lifetime matches the application: a static HttpClient is intentionally not
        // disposed — it is safe to share for the process lifetime and disposing would cause
        // ObjectDisposedException on subsequent requests.
        private static readonly HttpClient _rawHttp = CreateRawHttpClient();

        private static HttpClient CreateRawHttpClient()
        {
            var c = new HttpClient();
            c.DefaultRequestHeaders.UserAgent.ParseAdd("GameOS-Launcher/2.0");
            return c;
        }

        // ── Games Database cache ──────────────────────────────────────────────

        /// <summary>
        /// Maps verbose RetroArch/Libretro-style platform folder names to the canonical
        /// Games.Database platform identifiers used in URL paths.
        /// <para>
        /// Examples: "Microsoft - Xbox 360" → "Xbox 360", "Nintendo - Switch" → "Switch",
        /// "Sony - PlayStation 3" → "PS3".
        /// </para>
        /// Canonical names (e.g. "Xbox 360", "Switch") pass through unchanged.
        /// </summary>
        internal static string NormalizePlatform(string platform)
            => Models.PlatformHelper.NormalizePlatform(platform);

        /// <summary>
        /// Persistent disk-cache directory for platform game lists.
        /// Mirrors the browser's cache so the store loads instantly after the first fetch.
        /// </summary>
        private static readonly string DbCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GameOS", "GamesDbCache");

        /// <summary>How long a disk-cached platform file remains valid before a re-fetch.</summary>
        private static readonly TimeSpan DbCacheTtl = TimeSpan.FromHours(24);

        /// <summary>In-memory session cache: platform → game list (instant after first load).</summary>
        private static readonly Dictionary<string, List<DatabaseGame>> _dbMemoryCache =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Switch TitleID pattern: exactly 16 hexadecimal characters.</summary>
        private static readonly System.Text.RegularExpressions.Regex _switchTitleIdPattern =
            new(@"^[0-9A-Fa-f]{16}$", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static List<DatabaseGame>? TryLoadDiskCache(string platform)
        {
            try
            {
                string file = Path.Combine(DbCacheDir, $"{platform}.json");
                if (!File.Exists(file)) return null;
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow - DbCacheTtl) return null;
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var games = JsonSerializer.Deserialize<List<DatabaseGame>>(File.ReadAllText(file), opts);
                return games?.Count > 0 ? games : null;
            }
            catch { return null; }
        }

        private static void SaveDiskCache(string platform, List<DatabaseGame> games)
        {
            try
            {
                Directory.CreateDirectory(DbCacheDir);
                string file = Path.Combine(DbCacheDir, $"{platform}.json");
                File.WriteAllText(file, JsonSerializer.Serialize(games,
                    new JsonSerializerOptions { WriteIndented = false }));
            }
            catch { }
        }

        /// <summary>
        /// Loads the disk-cached game list for <paramref name="platform"/> regardless of
        /// its age (TTL).  Used as an offline fallback in the Store view so the user can
        /// still browse previously-loaded platform catalogues without a network connection.
        /// Returns <c>null</c> when no cache file exists.
        /// </summary>
        public static List<DatabaseGame>? TryLoadStaleDiskCacheForStore(string platform)
        {
            try
            {
                platform = NormalizePlatform(platform);
                string file = Path.Combine(DbCacheDir, $"{platform}.json");
                if (!File.Exists(file)) return null;

                var opts  = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var games = JsonSerializer.Deserialize<List<DatabaseGame>>(File.ReadAllText(file), opts);
                System.Diagnostics.Debug.WriteLine(
                    $"[GitHubDataService] Stale disk cache for {platform}: " +
                    $"{games?.Count ?? 0} games (last written {File.GetLastWriteTimeUtc(file):u}).");
                return games?.Count > 0 ? games : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Removes the cached game list for <paramref name="platform"/> from both the
        /// in-memory and disk caches so the next call to <see cref="FetchGamesDatabaseAsync"/>
        /// re-fetches fresh data from GitHub.
        /// </summary>
        public static void InvalidatePlatformCache(string platform)
        {
            lock (_dbMemoryCache)
                _dbMemoryCache.Remove(platform);

            try
            {
                string file = Path.Combine(DbCacheDir, $"{platform}.json");
                if (File.Exists(file)) File.Delete(file);
            }
            catch { }
        }

        /// <summary>
        /// Reads a JSON file from the public Koriebonx98/Games.Database repository.
        /// Returns the deserialized content and the file SHA (needed for future writes).
        /// Returns <c>(null, null)</c> if the file does not exist or cannot be read.
        /// </summary>
        public async Task<(T? Content, string? Sha)> ReadGamesDatabaseFileAsync<T>(
            string filePath, CancellationToken ct = default)
        {
            try
            {
                var url  = $"https://api.github.com/repos/Koriebonx98/Games.Database/contents/{Uri.EscapeDataString(filePath)}";
                var resp = await _http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode) return (default, null);

                using var stream = await resp.Content.ReadAsStreamAsync(ct);
                var file = await JsonSerializer.DeserializeAsync<GitHubFileResponse>(stream, _jsonOpts, ct);
                if (file == null) return (default, null);

                var bytes   = Convert.FromBase64String(file.Content.Replace("\n", ""));
                var json    = Encoding.UTF8.GetString(bytes);
                var content = JsonSerializer.Deserialize<T>(json, _jsonOpts);
                return (content, file.Sha);
            }
            catch { return (default, null); }
        }

        /// <summary>
        /// Creates or overwrites a JSON file in the public Koriebonx98/Games.Database repository.
        /// Pass <paramref name="sha"/> when overwriting an existing file.
        /// </summary>
        public async Task WriteGamesDatabaseFileAsync(
            string filePath, object content, string message,
            string? sha = null, CancellationToken ct = default)
        {
            var json   = JsonSerializer.Serialize(content, _jsonOpts);
            var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

            var body = new GitHubWriteBody
            {
                Message   = message,
                Content   = base64,
                Sha       = sha,
                Committer = new GitHubCommitter
                {
                    Name  = "Game.OS Bot",
                    Email = "game-os-bot@users.noreply.github.com"
                }
            };

            var url  = $"https://api.github.com/repos/Koriebonx98/Games.Database/contents/{Uri.EscapeDataString(filePath)}";
            var resp = await _http.PutAsJsonAsync(url, body, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                throw new GameOsException(
                    (int)resp.StatusCode,
                    $"GitHub Games.Database write error {(int)resp.StatusCode}: {err}");
            }
        }

        /// <summary>
        /// Checks the GitHub API to see whether the cached platform JSON files are
        /// up-to-date.  For each platform whose cached SHA differs from the current
        /// HEAD SHA on GitHub, the disk and in-memory caches are invalidated so the
        /// next <see cref="FetchGamesDatabaseAsync"/> call re-downloads fresh data.
        ///
        /// This mirrors the web app checking <c>?t=Date.now()</c> on every load;
        /// here we use a lightweight Contents API call (returns only metadata, not
        /// the full file body) to minimise bandwidth.
        /// </summary>
        public static async Task CheckForUpdatesAsync(
            string[]? platforms = null, CancellationToken ct = default)
        {
            platforms ??= GamesDbPlatforms;
            foreach (var platform in platforms)
            {
                try
                {
                    // GitHub Contents API returns the blob SHA for the file — tiny response
                    var metaUrl = $"https://api.github.com/repos/Koriebonx98/Games.Database/contents/{Uri.EscapeDataString(platform)}.Games.json";
                    using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, metaUrl);
                    req.Headers.UserAgent.ParseAdd("GameOS-Launcher/2.0");
                    req.Headers.Accept.ParseAdd("application/vnd.github.v3+json");

                    using var resp = await _rawHttp.SendAsync(req, ct);
                    if (!resp.IsSuccessStatusCode) continue;

                    var json = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);

                    if (!doc.RootElement.TryGetProperty("sha", out var shaEl)) continue;
                    var remoteSha = shaEl.GetString();
                    if (string.IsNullOrEmpty(remoteSha)) continue;

                    // Compare with stored SHA
                    var cachedSha = TryLoadCachedSha(platform);
                    if (!string.Equals(cachedSha, remoteSha, StringComparison.OrdinalIgnoreCase))
                    {
                        // Cache is stale — invalidate so the next fetch pulls fresh data
                        System.Diagnostics.Debug.WriteLine(
                            $"[GitHubDataService] {platform} cache is stale (remote SHA changed). Invalidating.");
                        InvalidatePlatformCache(platform);
                        SaveCachedSha(platform, remoteSha);
                    }
                }
                catch { /* best-effort — do not block startup on a network error */ }
            }
        }

        private static string? TryLoadCachedSha(string platform)
        {
            try
            {
                var path = Path.Combine(DbCacheDir, $"{platform}.sha");
                return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            }
            catch { return null; }
        }

        private static void SaveCachedSha(string platform, string sha)
        {
            try
            {
                Directory.CreateDirectory(DbCacheDir);
                File.WriteAllText(Path.Combine(DbCacheDir, $"{platform}.sha"), sha);
            }
            catch { }
        }

        /// <summary>
        /// Fetch all games for a given platform from the public Games.Database repository.
        /// Mirrors <c>fetchGamesDbPlatform(platform)</c> in script.js.
        /// Returns an empty list when the platform file does not exist (404).
        /// </summary>
        public static async Task<List<DatabaseGame>> FetchGamesDatabaseAsync(
            string platform, CancellationToken ct = default)
        {
            // Normalise verbose RetroArch-style names ("Microsoft - Xbox 360" → "Xbox 360")
            // so they map to the correct Games.Database URL and cache key.
            platform = NormalizePlatform(platform);

            // 1. In-memory cache — instant for platform switches within the same session
            lock (_dbMemoryCache)
            {
                if (_dbMemoryCache.TryGetValue(platform, out var mem))
                    return mem;
            }

            // 2. Disk cache — fast on subsequent app launches (avoids re-download)
            var disk = TryLoadDiskCache(platform);
            if (disk != null)
            {
                lock (_dbMemoryCache) _dbMemoryCache[platform] = disk;
                return disk;
            }

            // 3. Network fetch
            var url  = $"{GamesDbRawBase}/{Uri.EscapeDataString(platform)}.Games.json";
            var resp = await _rawHttp.GetAsync(url, ct);

            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new List<DatabaseGame>();

            if (!resp.IsSuccessStatusCode)
                throw new GameOsException(
                    (int)resp.StatusCode,
                    $"Failed to load {platform} games: HTTP {(int)resp.StatusCode}");

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc    = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            // Detect the JSON format used by the Games.Database:
            //   { "Games": [...] }  — primary format
            //   { "games": [...] }  — alternate casing
            //   [...]               — root array
            // Mirrors the three-case detection in fetchGamesDbPlatform() (script.js).
            JsonElement gamesArray;
            if (root.ValueKind == JsonValueKind.Array)
            {
                gamesArray = root;
            }
            else if (root.ValueKind == JsonValueKind.Object &&
                     (root.TryGetProperty("Games", out gamesArray) ||
                      root.TryGetProperty("games", out gamesArray)))
            {
                // gamesArray is set by the first successful TryGetProperty call
            }
            else
            {
                return new List<DatabaseGame>();
            }

            var result = new List<DatabaseGame>();
            foreach (var item in gamesArray.EnumerateArray())
            {
                // Title — try multiple casings / field names to handle different platform JSON formats.
                // Switch and PS3 use "game_name"; Xbox 360 uses "title" (lowercase); PC/PS4 use "Title".
                // Mirrors: game.Title || game.game_name || game.title in script.js
                var title =
                    item.TryGetProperty("Title",     out var t)  && t.ValueKind  == JsonValueKind.String ? t.GetString()  :
                    item.TryGetProperty("game_name", out var t2) && t2.ValueKind == JsonValueKind.String ? t2.GetString() :
                    item.TryGetProperty("title",     out var t3) && t3.ValueKind == JsonValueKind.String ? t3.GetString() :
                    item.TryGetProperty("name",      out var t4) && t4.ValueKind == JsonValueKind.String ? t4.GetString() :
                    null;
                if (string.IsNullOrWhiteSpace(title))
                    continue; // skip non-game / empty entries

                // Cover URL — check CoverUrl, then image, then cover_url (mirrors getGameCoverUrl in script.js)
                string? coverUrl =
                    item.TryGetProperty("CoverUrl", out var cu)  && cu.ValueKind == JsonValueKind.String ? cu.GetString() :
                    item.TryGetProperty("image",    out var img) && img.ValueKind == JsonValueKind.String ? img.GetString() :
                    item.TryGetProperty("cover_url",out var cov) && cov.ValueKind == JsonValueKind.String ? cov.GetString() :
                    null;
                if (string.IsNullOrWhiteSpace(coverUrl)) coverUrl = null;

                // Description — check both casings (mirrors game.Description || game.description in script.js)
                string? description =
                    item.TryGetProperty("Description", out var d1) && d1.ValueKind == JsonValueKind.String ? d1.GetString() :
                    item.TryGetProperty("description", out var d2) && d2.ValueKind == JsonValueKind.String ? d2.GetString() :
                    null;
                // Treat empty strings as null so callers don't overwrite real descriptions with blanks
                if (string.IsNullOrWhiteSpace(description)) description = null;

                // Trailer — first element of the trailers array
                string? trailerUrl = null;
                if (item.TryGetProperty("trailers", out var tr) && tr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var elem in tr.EnumerateArray())
                    {
                        if (elem.ValueKind == JsonValueKind.String)
                        { trailerUrl = elem.GetString(); break; }
                    }
                }

                // Achievements URL — check multiple field name variants used by different platform JSONs
                // (Switch/PS3 may use "achievements_url" while PC/PS4 use "achievementsUrl")
                string? achievementsUrl =
                    item.TryGetProperty("achievementsUrl",  out var au)  && au.ValueKind  == JsonValueKind.String ? au.GetString()  :
                    item.TryGetProperty("achievements_url", out var au2) && au2.ValueKind == JsonValueKind.String ? au2.GetString() :
                    item.TryGetProperty("AchievementsUrl",  out var au3) && au3.ValueKind == JsonValueKind.String ? au3.GetString() :
                    null;
                if (string.IsNullOrWhiteSpace(achievementsUrl)) achievementsUrl = null;

                // Store page URL — explicit field or construct from appid
                string? storePageUrl =
                    item.TryGetProperty("storeUrl",   out var su1) && su1.ValueKind == JsonValueKind.String ? su1.GetString() :
                    item.TryGetProperty("store_url",  out var su2) && su2.ValueKind == JsonValueKind.String ? su2.GetString() :
                    item.TryGetProperty("StoreUrl",   out var su3) && su3.ValueKind == JsonValueKind.String ? su3.GetString() :
                    null;

                // AppId (Steam) — construct store URL if not already set
                long? appId =
                    item.TryGetProperty("appid", out var aid) && aid.ValueKind == JsonValueKind.Number
                    ? aid.GetInt64() : null;
                if (string.IsNullOrEmpty(storePageUrl) && appId.HasValue && appId.Value > 0)
                    storePageUrl = $"https://store.steampowered.com/app/{appId.Value}/";

                // For PC Steam games that have an appId but no explicit cover URL, derive
                // the portrait cover from the Steam CDN (library_600x900 format).
                if (string.IsNullOrEmpty(coverUrl) && appId.HasValue && appId.Value > 0)
                    coverUrl = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId.Value}/library_600x900.jpg";

                // Screenshots / background images
                List<string>? screenshots = null;
                if (item.TryGetProperty("background_images", out var bi) && bi.ValueKind == JsonValueKind.Array)
                {
                    screenshots = new List<string>();
                    foreach (var elem in bi.EnumerateArray())
                    {
                        if (elem.ValueKind == JsonValueKind.String)
                        {
                            var s = elem.GetString();
                            if (!string.IsNullOrEmpty(s)) screenshots.Add(s);
                        }
                    }
                    if (screenshots.Count == 0) screenshots = null;
                }

                // TitleID — mirrors: game.TitleID || game.title_id || game.titleid || game.id in script.js
                // Switch/PS3 use "title_id"; Xbox 360 uses "titleid" (no separator); PS4/PC use "TitleID".
                string? titleId =
                    item.TryGetProperty("TitleID",   out var tid)  && tid.ValueKind  == JsonValueKind.String ? tid.GetString()  :
                    item.TryGetProperty("TitleId",   out var tid2) && tid2.ValueKind == JsonValueKind.String ? tid2.GetString() :
                    item.TryGetProperty("title_id",  out var tid3) && tid3.ValueKind == JsonValueKind.String ? tid3.GetString() :
                    item.TryGetProperty("titleid",   out var tid4) && tid4.ValueKind == JsonValueKind.String ? tid4.GetString() :
                    item.TryGetProperty("id",        out var tid5) && tid5.ValueKind == JsonValueKind.String ? tid5.GetString() :
                    appId.HasValue ? appId.Value.ToString() : null;

                // For Switch games, the TitleID must be exactly 16 hexadecimal characters
                // (e.g. "0100152000022000").  Reject any other value such as a RAWG internal
                // "id" (e.g. "1222700") to prevent mods.json from being looked up under a
                // wrong directory name.
                if (!string.IsNullOrEmpty(titleId) &&
                    string.Equals(platform, "Switch", StringComparison.OrdinalIgnoreCase) &&
                    !_switchTitleIdPattern.IsMatch(titleId))
                {
                    titleId = null;
                }

                // Genre — Xbox 360 and some enriched databases include this field
                string? genre =
                    item.TryGetProperty("Genre",  out var gen1) && gen1.ValueKind == JsonValueKind.String ? gen1.GetString() :
                    item.TryGetProperty("genre",  out var gen2) && gen2.ValueKind == JsonValueKind.String ? gen2.GetString() :
                    null;
                if (string.IsNullOrWhiteSpace(genre)) genre = null;

                // Release year — extract from ReleaseDate ("2020-06-19" → "2020") or releaseDate field
                string? releaseYear = null;
                if (item.TryGetProperty("ReleaseDate", out var rd1) && rd1.ValueKind == JsonValueKind.String)
                {
                    var s = rd1.GetString();
                    if (!string.IsNullOrEmpty(s) && s.Length >= 4) releaseYear = s[..4];
                }
                else if (item.TryGetProperty("releaseDate", out var rd2) && rd2.ValueKind == JsonValueKind.String)
                {
                    var s = rd2.GetString();
                    if (!string.IsNullOrEmpty(s) && s.Length >= 4) releaseYear = s[..4];
                }
                else if (item.TryGetProperty("ReleaseYear", out var ry1))
                {
                    releaseYear = ry1.ValueKind == JsonValueKind.String  ? ry1.GetString() :
                                  ry1.ValueKind == JsonValueKind.Number  ? ry1.GetInt32().ToString() : null;
                }
                if (string.IsNullOrWhiteSpace(releaseYear)) releaseYear = null;

                // AlternateNames — used for fuzzy title matching
                // Mirrors: game.AlternateNames || game.alternate_names in script.js
                List<string>? alternateNames = null;
                var altProp = item.TryGetProperty("AlternateNames", out var an1) ? an1 :
                              item.TryGetProperty("alternate_names", out var an2) ? an2 : default;
                if (altProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var elem in altProp.EnumerateArray())
                    {
                        if (elem.ValueKind == JsonValueKind.String)
                        {
                            var s = elem.GetString();
                            if (!string.IsNullOrEmpty(s))
                            {
                                alternateNames ??= new List<string>();
                                alternateNames.Add(s);
                            }
                        }
                    }
                }

                result.Add(new DatabaseGame
                {
                    Title           = title,
                    TitleId         = titleId,
                    CoverUrl        = coverUrl,
                    AppId           = appId,
                    Description     = description,
                    TrailerUrl      = trailerUrl,
                    AchievementsUrl = achievementsUrl,
                    Screenshots     = screenshots,
                    StorePageUrl    = storePageUrl,
                    Genre           = genre,
                    ReleaseYear     = releaseYear,
                    AlternateNames  = alternateNames,
                });
            }

            // Store in both caches so future calls (same session or next launch) are instant
            lock (_dbMemoryCache) _dbMemoryCache[platform] = result;
            // Write to disk on a background thread; SaveDiskCache swallows all exceptions so
            // this fire-and-forget is intentional and safe — a failed write just means the
            // next launch re-fetches from the network.
            _ = Task.Run(() => SaveDiskCache(platform, result), CancellationToken.None);

            return result;
        }

        /// <summary>
        /// Synchronously looks up the TitleID for <paramref name="title"/> on
        /// <paramref name="platform"/> using only locally cached data.
        ///
        /// <para>Lookup order:</para>
        /// <list type="number">
        ///   <item>In-memory session cache (populated by any prior <see cref="FetchGamesDatabaseAsync"/> call).</item>
        ///   <item>Disk cache at <c>%AppData%\GameOS\GamesDbCache\{platform}.json</c> —
        ///         read regardless of TTL so the most recently downloaded file is always used.</item>
        /// </list>
        ///
        /// Returns <c>null</c> when no matching entry is found or no cache exists.
        /// </summary>
        public static string? TryGetTitleIdFromLocalCache(string platform, string title)
        {
            if (string.IsNullOrWhiteSpace(title)) return null;
            platform = NormalizePlatform(platform);

            // 1. In-memory cache — instant when the database has already been loaded this session
            List<DatabaseGame>? games;
            lock (_dbMemoryCache)
                _dbMemoryCache.TryGetValue(platform, out games);

            // 2. Disk cache — read stale-or-fresh; local files are kept up-to-date by
            //    CheckForUpdatesAsync so we always prefer them over a network fetch.
            if (games == null || games.Count == 0)
            {
                try
                {
                    string file = Path.Combine(DbCacheDir, $"{platform}.json");
                    if (File.Exists(file))
                    {
                        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                        games = JsonSerializer.Deserialize<List<DatabaseGame>>(
                                    File.ReadAllText(file), opts);
                    }
                }
                catch { /* best-effort — a corrupt or unreadable cache file is not fatal */ }
            }

            if (games == null || games.Count == 0) return null;

            // Title matching: exact → normalised (Windows-safe separators) → symbol-stripped
            // Inline the same normalisation used by MainViewModel.NormalizeGameTitle:
            // replace the first " - " with ": " to reconstruct subtitle separators.
            string normalised = System.Text.RegularExpressions.Regex.Replace(
                title, @"^(.+?) - (.+)$", "$1: $2");
            string stripped   = Models.PlatformHelper.StripSpecialSymbols(title);

            var match =
                games.FirstOrDefault(g => string.Equals(g.Title, title,       StringComparison.OrdinalIgnoreCase))
             ?? games.FirstOrDefault(g => string.Equals(g.Title, normalised,  StringComparison.OrdinalIgnoreCase))
             ?? games.FirstOrDefault(g => string.Equals(
                    Models.PlatformHelper.StripSpecialSymbols(g.Title ?? ""),
                    stripped, StringComparison.OrdinalIgnoreCase));

            return string.IsNullOrEmpty(match?.TitleId) ? null : match.TitleId;
        }

        /// <summary>
        /// Checks whether <paramref name="titleId"/> already exists in the local
        /// Games.Database cache for <paramref name="platform"/>.
        /// Returns <c>false</c> when no cache data is available (to be safe, treats
        /// "unknown" as already present to avoid accidental duplicates).
        /// </summary>
        public static bool IsTitleIdInLocalCache(string platform, string titleId)
        {
            if (string.IsNullOrWhiteSpace(titleId)) return false;
            platform = NormalizePlatform(platform);

            List<DatabaseGame>? games;
            lock (_dbMemoryCache)
                _dbMemoryCache.TryGetValue(platform, out games);

            if (games == null || games.Count == 0)
            {
                try
                {
                    string file = Path.Combine(DbCacheDir, $"{platform}.json");
                    if (File.Exists(file))
                    {
                        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                        games = JsonSerializer.Deserialize<List<DatabaseGame>>(File.ReadAllText(file), opts);
                    }
                }
                catch { }
            }

            if (games == null || games.Count == 0) return false;
            return games.Any(g =>
                string.Equals(g.TitleId, titleId, StringComparison.OrdinalIgnoreCase));
        }

        // ── Steam game contribution to public Games.Database ─────────────────

        /// <summary>
        /// Triggers the <c>add-user-steam-games</c> workflow in this repository via
        /// the GitHub Actions <c>workflow_dispatch</c> API so new Steam games are added
        /// to the Games.Database in a serialised, conflict-free way by the CI runner.
        /// Falls back gracefully (logs the failure) if the token is absent or the
        /// workflow is not available.
        /// </summary>
        public async Task TriggerAddGamesWorkflowAsync(
            IReadOnlyList<SteamOwnedGame> newGames, CancellationToken ct = default)
        {
            if (newGames.Count == 0 || GitHubToken == null) return;

            // Build a compact array of {title, appid} objects for the workflow input
            var gamesPayload = newGames.Select(g => new { title = g.Name, appid = g.AppId }).ToList();
            string gamesJson = System.Text.Json.JsonSerializer.Serialize(gamesPayload,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = false });

            // Truncate to GitHub's 65 536-byte workflow input limit (each entry ~40 bytes)
            // by capping at 1 000 games; typical API response is well under this limit.
            if (gamesJson.Length > 60_000)
            {
                gamesPayload = gamesPayload.Take(1_000).ToList();
                gamesJson = System.Text.Json.JsonSerializer.Serialize(gamesPayload,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
            }

            // POST /repos/{owner}/{repo}/actions/workflows/{workflow_file}/dispatches
            string url = $"https://api.github.com/repos/{DataRepoOwner}/Game.OS.Userdata" +
                         "/actions/workflows/add-user-steam-games.yml/dispatches";

            var payload = new
            {
                @ref   = "main",
                inputs = new { games_json = gamesJson },
            };

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = JsonContent.Create(payload),
                };
                req.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", GitHubToken);
                req.Headers.Accept.ParseAdd("application/vnd.github+json");
                req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

                using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                    DevLogService.Log(
                        $"[GamesDB] Triggered add-user-steam-games workflow for {newGames.Count} game(s).");
                else
                {
                    string err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    DevLogService.Log(
                        $"[GamesDB] Workflow dispatch returned {(int)resp.StatusCode}: {err}");
                }
            }
            catch (Exception ex)
            {
                DevLogService.Log($"[GamesDB] TriggerAddGamesWorkflowAsync failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Contributes new Steam PC games to the public Games.Database <c>PC.Games.json</c>
        /// when they are not already present (checked by AppId and title).
        /// First attempts to trigger the <c>add-user-steam-games</c> GitHub Actions workflow
        /// (serialised, conflict-free); falls back to a direct file update when the token
        /// does not have Actions: Write permission (e.g. a read-only token).
        /// Best-effort: failures are logged but never thrown to the caller.
        /// </summary>
        public async Task ContributeSteamGamesToDatabaseAsync(
            IReadOnlyList<SteamOwnedGame> newGames, CancellationToken ct = default)
        {
            if (newGames.Count == 0) return;

            // Filter to games that are absent from the in-memory/disk cache
            var toContribute = new List<SteamOwnedGame>();
            lock (_dbMemoryCache)
            {
                if (_dbMemoryCache.TryGetValue("PC", out var cached))
                {
                    var existingIds = new HashSet<long>(
                        cached.Where(g => g.AppId.HasValue).Select(g => g.AppId!.Value));
                    var existingTitles = new HashSet<string>(
                        cached.Select(g => g.Title ?? ""), StringComparer.OrdinalIgnoreCase);
                    toContribute = newGames
                        .Where(g => g.AppId > 0 &&
                                    !existingIds.Contains(g.AppId) &&
                                    !existingTitles.Contains(g.Name))
                        .ToList();
                }
                else
                {
                    // Cache not populated yet — proceed with the full list; duplicates will
                    // be caught by the in-file check below.
                    toContribute = newGames.Where(g => g.AppId > 0).ToList();
                }
            }

            if (toContribute.Count == 0) return;

            // ── Preferred path: trigger the CI workflow (serialised, conflict-free) ──
            // The workflow accepts a JSON list of {title, appid} objects and does the
            // read-modify-write inside a GitHub Actions runner, avoiding race conditions
            // between concurrent launcher instances.
            bool workflowTriggered = false;
            if (GitHubToken != null)
            {
                try
                {
                    await TriggerAddGamesWorkflowAsync(toContribute, ct).ConfigureAwait(false);
                    workflowTriggered = true;
                    DevLogService.Log(
                        $"[GamesDB] Dispatched workflow for {toContribute.Count} new game(s) " +
                        "— skipping direct write.");
                }
                catch { /* fall through to direct write below */ }
            }

            if (workflowTriggered) return;

            // ── Fallback: direct file update (read-modify-write against GitHub Contents API) ──
            const string filePath = "PC.Games.json";
            string? rawJson = null;
            string? sha = null;

            try
            {
                var url = $"https://api.github.com/repos/Koriebonx98/Games.Database/contents/{Uri.EscapeDataString(filePath)}";
                var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return;

                var file = await resp.Content
                    .ReadFromJsonAsync<GitHubFileResponse>(cancellationToken: ct)
                    .ConfigureAwait(false);
                if (file == null) return;

                var bytes = Convert.FromBase64String(file.Content.Replace("\n", ""));
                rawJson = System.Text.Encoding.UTF8.GetString(bytes);
                sha     = file.Sha;
            }
            catch (Exception ex)
            {
                DevLogService.Log($"[GamesDB] Could not read PC.Games.json: {ex.Message}");
                return;
            }

            System.Text.Json.Nodes.JsonNode? root;
            try { root = System.Text.Json.Nodes.JsonNode.Parse(rawJson ?? "null"); }
            catch (System.Text.Json.JsonException ex)
            {
                DevLogService.Log($"[GamesDB] Could not parse PC.Games.json: {ex.Message}");
                return;
            }

            System.Text.Json.Nodes.JsonArray? gamesArray;
            bool isRootArray = root is System.Text.Json.Nodes.JsonArray;
            if (isRootArray)
            {
                gamesArray = (System.Text.Json.Nodes.JsonArray)root!;
            }
            else if (root is System.Text.Json.Nodes.JsonObject obj)
            {
                gamesArray = (obj["Games"] ?? obj["games"]) as System.Text.Json.Nodes.JsonArray;
                if (gamesArray == null) return;
            }
            else return;

            // Build in-file duplicate sets
            var existingFileIds    = new HashSet<long>();
            var existingFileTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in gamesArray)
            {
                if (item is not System.Text.Json.Nodes.JsonObject entry) continue;
                if (entry["appid"] is System.Text.Json.Nodes.JsonValue av &&
                    av.TryGetValue<long>(out var id)) existingFileIds.Add(id);
                var t = entry["Title"]?.GetValue<string?>()
                     ?? entry["title"]?.GetValue<string?>();
                if (!string.IsNullOrEmpty(t)) existingFileTitles.Add(t);
            }

            int added = 0;
            foreach (var sg in toContribute)
            {
                if (existingFileIds.Contains(sg.AppId) ||
                    existingFileTitles.Contains(sg.Name)) continue;

                gamesArray.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["Title"] = sg.Name,
                    ["appid"] = sg.AppId,
                });
                existingFileIds.Add(sg.AppId);
                existingFileTitles.Add(sg.Name);
                added++;
            }

            if (added == 0) return;

            try
            {
                // Serialize the root node (contains the updated games array in-place)
                object toWrite = isRootArray ? (object)gamesArray : root!;
                await WriteGamesDatabaseFileAsync(
                    filePath, toWrite,
                    $"Add {added} Steam PC game(s) contributed via user import",
                    sha, ct).ConfigureAwait(false);

                // Invalidate cache so the next FetchGamesDatabaseAsync re-downloads fresh data
                InvalidatePlatformCache("PC");
                DevLogService.Log($"[GamesDB] Contributed {added} new Steam PC game(s) to PC.Games.json.");
            }
            catch (Exception ex)
            {
                DevLogService.Log($"[GamesDB] Failed to write PC.Games.json contributions: {ex.Message}");
            }
        }

        /// <summary>
        /// Uploads the full achievement schema for a Steam game to
        /// <c>Data/PC/Games/{appId}/achievements.json</c> in the Games.Database repository.
        /// Returns the raw URL to the uploaded file on success, or <c>null</c> on failure.
        /// If an achievements file already exists it is not overwritten.
        /// </summary>
        public async Task<string?> UploadSteamAchievementsToDatabaseAsync(
            int appId, string gameTitle, IReadOnlyList<SteamSchemaAchievement> achievements,
            CancellationToken ct = default)
        {
            if (achievements.Count == 0 || appId <= 0) return null;

            // Format understood by FetchAndDisplayAchievementsAsync:
            // [{ "name": "...", "description": "...", "iconUrl": "..." }]
            var dbAchievements = achievements.Select(a => new
            {
                name        = string.IsNullOrWhiteSpace(a.DisplayName) ? a.ApiName : a.DisplayName,
                description = a.Description,
                iconUrl     = string.IsNullOrEmpty(a.Icon) ? null : a.Icon,
            }).ToList();

            string filePath = $"Data/PC/Games/{appId}/achievements.json";
            string achUrl   = $"{GamesDbRawBase}/Data/PC/Games/{appId}/achievements.json";

            try
            {
                // Check whether the file already exists (get SHA for update, or null for create)
                var (existing, sha) = await ReadGamesDatabaseFileAsync<object>(filePath, ct)
                    .ConfigureAwait(false);

                // Do not overwrite an already-present achievements file
                if (existing != null) return achUrl;

                await WriteGamesDatabaseFileAsync(
                    filePath, dbAchievements,
                    $"Add Steam achievements for {gameTitle} (AppId {appId})",
                    sha, ct).ConfigureAwait(false);

                DevLogService.Log(
                    $"[GamesDB] Uploaded {achievements.Count} achievements for '{gameTitle}' (AppId {appId}).");
                return achUrl;
            }
            catch (Exception ex)
            {
                DevLogService.Log(
                    $"[GamesDB] Failed to upload achievements for '{gameTitle}': {ex.Message}");
                return null;
            }
        }

        // ── Review upload to Games.Database ──────────────────────────────────

        /// <summary>
        /// Maps a canonical platform name (e.g. "Switch", "PS3") to the corresponding
        /// folder name used in the Games.Database Data/ directory.
        /// </summary>
        internal static string? PlatformToDbFolder(string platform)
            => NormalizePlatform(platform) switch
            {
                "Switch"   => "Nintendo - Switch",
                "PS3"      => "Sony - PlayStation 3",
                "PS4"      => "Sony - PlayStation 4",
                "PS5"      => "Sony - PlayStation 5",
                "PS1"      => "Sony - PlayStation",
                "PS2"      => "Sony - PlayStation 2",
                "PSP"      => "Sony - PSP",
                "PS Vita"  => "Sony - PS Vita",
                "Xbox 360" => "Microsoft - Xbox 360",
                "Xbox One" => "Microsoft - Xbox One",
                "PC"       => "PC",
                _          => null,
            };

        /// <summary>
        /// Sanitizes a string for use as a folder name in Games.Database
        /// by replacing file-system-unsafe characters with underscores.
        /// </summary>
        private static string SanitizeDbFolderName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        /// <summary>
        /// Uploads a single user review for a game to the public Games.Database repository,
        /// storing it at <c>Data/{platformFolder}/Games/{titleId}/reviews.json</c>.
        ///
        /// The list is read first so existing reviews (including other users') are preserved.
        /// Any previous review by the same username is replaced (one review per user per game).
        /// </summary>
        /// <param name="platform">Canonical platform name (e.g. "Switch", "PC", "Xbox 360").</param>
        /// <param name="titleId">
        /// The game's title ID (e.g. a 16-hex Switch TitleID or BLUS serial).
        /// When <c>null</c> or empty, <paramref name="gameTitle"/> is used as the folder name.
        /// </param>
        /// <param name="gameTitle">Display title, used as the folder name when no title ID is available.</param>
        /// <param name="review">The review to add or replace.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task UploadReviewToDatabaseAsync(
            string platform, string? titleId, string gameTitle,
            Models.GameReview review, CancellationToken ct = default)
        {
            string? folder = PlatformToDbFolder(platform);
            if (folder == null)
            {
                DevLogService.Log($"[ReviewDb] Unknown platform '{platform}' — skipping upload.");
                return;
            }

            string idSegment = !string.IsNullOrWhiteSpace(titleId)
                ? titleId.Trim()
                : SanitizeDbFolderName(gameTitle);

            if (string.IsNullOrWhiteSpace(idSegment))
            {
                DevLogService.Log("[ReviewDb] Cannot determine game folder — skipping upload.");
                return;
            }

            string filePath = $"Data/{folder}/Games/{idSegment}/reviews.json";
            DevLogService.Log($"[ReviewDb] Uploading review by '{review.Username}' to {filePath}…");

            try
            {
                List<Models.GameReview>? reviews;
                string? sha;
                (reviews, sha) = await ReadGamesDatabaseFileAsync<List<Models.GameReview>>(filePath, ct);
                reviews ??= new List<Models.GameReview>();

                reviews.RemoveAll(r =>
                    string.Equals(r.Username, review.Username, StringComparison.OrdinalIgnoreCase));
                reviews.Add(review);

                await WriteGamesDatabaseFileAsync(
                    filePath, reviews,
                    $"Review: {review.Username} rated '{gameTitle}' {review.Rating}/5",
                    sha, ct);

                DevLogService.Log($"[ReviewDb] Successfully uploaded review for '{gameTitle}'.");
            }
            catch (Exception ex)
            {
                DevLogService.Log($"[ReviewDb] Failed to upload review for '{gameTitle}': {ex.Message}");
            }
        }

        // ── GitHub API response models ─────────────────────────────────────────
        private sealed class GitHubFileResponse
        {
            [JsonPropertyName("content")] public string Content { get; set; } = "";
            [JsonPropertyName("sha")]     public string Sha     { get; set; } = "";
        }

        private sealed class GitHubWriteBody
        {
            [JsonPropertyName("message")]   public string          Message   { get; set; } = "";
            [JsonPropertyName("content")]   public string          Content   { get; set; } = "";
            [JsonPropertyName("sha")]       public string?         Sha       { get; set; }
            [JsonPropertyName("committer")] public GitHubCommitter Committer { get; set; } = new();
        }

        private sealed class GitHubCommitter
        {
            [JsonPropertyName("name")]  public string Name  { get; set; } = "";
            [JsonPropertyName("email")] public string Email { get; set; } = "";
        }
    }
}
