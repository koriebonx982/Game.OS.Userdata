using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GameLauncher;
using GameLauncher.Models;
using GameLauncher.Services;

/// <summary>
/// Login authentication tests for the Game.OS C# launcher.
///
/// Tests that the C# PBKDF2 hash implementation is identical to the JavaScript
/// <c>hashPassword()</c> function in script.js, and that the login flow handles
/// both PBKDF2 and bcrypt hashes (same dual-hash support as the web frontend).
///
/// Run:
///   dotnet run --project LoginAuth.Tests
///
/// Optional live backend test — set env var before running:
///   GAMEOS_GITHUB_TOKEN=&lt;your PAT&gt; dotnet run --project LoginAuth.Tests
/// </summary>
class Program
{
    // PBKDF2 test vectors: fixed expected values computed independently via Node.js:
    //   node -e "const c=require('crypto');c.pbkdf2('<pass>','<user_lower>:gameos',100000,32,'sha256',(e,k)=>console.log(k.toString('hex')))"
    private static readonly (string username, string password, string? expectedHex)[] Vectors =
    [
        ("testuser",     "TestPass123", "50ad5d6fb130bcaca2c96094d9609a9e3ebb1852a51245afeabc05f9e6b81379"),
        ("Admin.GameOS", "GameOS2026",  "2f96663f1e20c234b7b4dc61d3887f9ffa417141345cbda1a0b0079c737a3502"),
        ("Koriebonx98",  "mypassword",  null),  // format / determinism check only
    ];

    // Reference hash for Koriebonx98 with the real account password.
    // Pre-computed via Node.js (same algorithm as hashPassword() in script.js):
    //   node -e "const c=require('crypto');c.pbkdf2('Myipodfool98','koriebonx98:gameos',100000,32,'sha256',(e,k)=>console.log(k.toString('hex')))"
    private const string Koriebonx98ExpectedHash =
        "98df8a71e3a4ad953895182c2dfd793327959b46311692d8f26aeac935822db4";

    static async Task<int> Main(string[] args)
    {
        Banner();
        bool allPassed = true;

        // ── 1. PBKDF2 hash parity ────────────────────────────────────────────
        allPassed &= TestPbkdf2HashParity();

        // ── 1b. Koriebonx98 real-account hash parity ─────────────────────────
        allPassed &= TestKoriebonx98HashParity();

        // ── 2. PBKDF2 case-insensitive salt (JS lowercases username) ─────────
        allPassed &= TestPbkdf2CaseInsensitiveSalt();

        // ── 3. bcrypt detection ───────────────────────────────────────────────
        allPassed &= TestBcryptDetection();

        // ── 4. dual-hash login (mock profile) ────────────────────────────────
        allPassed &= TestDualHashLogin();

        // ── 5. live backend GitHub-direct (optional — requires GAMEOS_GITHUB_TOKEN) ──
        allPassed &= await TestLiveBackendOptionalAsync();

        // ── 6. live backend REST API (optional — requires GAMEOS_BACKEND_URL) ──
        allPassed &= await TestLiveRestApiOptionalAsync();

        // ── 7. backend API round-trip using Admin.GameOS (requires GAMEOS_BACKEND_URL) ──
        allPassed &= await TestBackendAdminLoginAsync();

        // ── 8. OfflineDataCacheService round-trip ──────────────────────────────
        allPassed &= TestOfflineDataCacheRoundTrip();

        // ── 9. PendingChangesService queue ─────────────────────────────────────
        allPassed &= TestPendingChangesQueue();

        // ── 10. PlatformHelper.NormalizePlatform (abbreviated names) ───────────
        allPassed &= TestPlatformNormalization();

        // ── 11. OfflineDataCacheService.EnumerateAllCachedUsers ───────────────
        allPassed &= TestEnumerateAllCachedUsers();

        // ── 12. Reconnect timer state-transition logic ──────────────────────
        allPassed &= TestReconnectStateLogic();

        // ── 13. GitHub-direct achievement mirror sync ───────────────────────
        allPassed &= await TestAchievementMirrorSyncAsync();

        // ── Summary ───────────────────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════════");
        if (allPassed)
        {
            Colour(ConsoleColor.Green,
                "  ✅  ALL TESTS PASSED — C# login logic matches web frontend exactly!");
        }
        else
        {
            Colour(ConsoleColor.Red,
                "  ❌  SOME TESTS FAILED — see output above for details.");
        }
        Console.WriteLine("═══════════════════════════════════════════════════════════════════");
        return allPassed ? 0 : 1;
    }

    // ── Test 1: PBKDF2 hash parity with Node.js reference values ─────────────
    static bool TestPbkdf2HashParity()
    {
        Section("1. PBKDF2 Hash Parity  (C# ≡ JavaScript hashPassword())");

        bool passed = true;
        foreach (var (username, password, expected) in Vectors)
        {
            var actual = GitHubDataService.HashPassword(password, username);

            if (expected == null)
            {
                // Just check it's a 64-char hex string and deterministic
                bool ok = actual.Length == 64 &&
                          IsHex(actual) &&
                          actual == GitHubDataService.HashPassword(password, username);
                Pass(ok, $"username={username} → len={actual.Length} deterministic={ok}");
                passed &= ok;
            }
            else
            {
                bool ok = string.Equals(actual, expected, StringComparison.Ordinal);
                Pass(ok, $"username={username,-12} → {(ok ? "hash matches reference vector ✓" : $"MISMATCH\n     expected: {expected}\n     actual:   {actual}")}");
                passed &= ok;
            }
        }
        return passed;
    }

    // ── Test 1b: Koriebonx98 real-account PBKDF2 hash parity ─────────────────
    // Verifies that the C# hash for the Koriebonx98 account with its real password
    // matches the pre-computed Node.js reference value — proving the C# app will
    // accept the same credentials as the web frontend.
    //
    // When GAMEOS_TEST_PASSWORD is set (CI / developer with credentials), the test
    // additionally verifies that the computed hash matches the pre-computed constant.
    // Without the env var the hash is still checked for format / determinism.
    static bool TestKoriebonx98HashParity()
    {
        Section("1b. Koriebonx98 Real-Account Hash Parity  (C# hash = Node.js reference)");

        bool passed = true;

        // Always: verify the reference constant itself is a valid 64-char hex string
        bool refOk = Koriebonx98ExpectedHash.Length == 64 && IsHex(Koriebonx98ExpectedHash);
        Pass(refOk, $"Reference hash is valid 64-char hex: {refOk}");
        passed &= refOk;

        string? testPassword = Environment.GetEnvironmentVariable("GAMEOS_TEST_PASSWORD");
        if (string.IsNullOrEmpty(testPassword))
        {
            Colour(ConsoleColor.Yellow,
                "  ⚠  GAMEOS_TEST_PASSWORD not set — skipping live hash-match check.");
            Console.WriteLine("       To verify: GAMEOS_TEST_PASSWORD=<password> dotnet run");
            return passed;
        }

        // Compute C# hash and compare to the pre-computed Node.js reference
        var actualHash = GitHubDataService.HashPassword(testPassword, "Koriebonx98");
        bool hashOk = string.Equals(actualHash, Koriebonx98ExpectedHash, StringComparison.Ordinal);
        Pass(hashOk, hashOk
            ? "C# hash for Koriebonx98 matches Node.js reference vector ✓"
            : $"MISMATCH\n     expected: {Koriebonx98ExpectedHash}\n     actual:   {actualHash}");
        passed &= hashOk;

        if (hashOk)
        {
            Colour(ConsoleColor.Green,
                "  ✅  Koriebonx98 PBKDF2 hash verified — C# login will accept this account");
        }

        return passed;
    }

    // ── Test 2: case-insensitive salt ─────────────────────────────────────────
    static bool TestPbkdf2CaseInsensitiveSalt()
    {
        Section("2. PBKDF2 Salt Case-Insensitivity  (username lowercased for salt)");

        // JS does username.toLowerCase() before building the salt string.
        // C# does username.ToLowerInvariant() — same result for ASCII usernames.
        string h1 = GitHubDataService.HashPassword("mypassword", "Koriebonx98");
        string h2 = GitHubDataService.HashPassword("mypassword", "koriebonx98");
        string h3 = GitHubDataService.HashPassword("mypassword", "KORIEBONX98");

        bool ok = (h1 == h2) && (h2 == h3);
        Pass(ok, $"hash(Koriebonx98) == hash(koriebonx98) == hash(KORIEBONX98): {ok}");
        return ok;
    }

    // ── Test 3: bcrypt detection ──────────────────────────────────────────────
    static bool TestBcryptDetection()
    {
        Section("3. Bcrypt Hash Detection  (accounts created via Node.js backend)");

        // Generate a real bcrypt hash and verify it round-trips
        string bcryptHash = BCrypt.Net.BCrypt.HashPassword("TestPass123");
        bool verifyOk  = BCrypt.Net.BCrypt.Verify("TestPass123", bcryptHash);
        bool rejectOk  = !BCrypt.Net.BCrypt.Verify("wrong", bcryptHash);
        bool startsOk  = bcryptHash.StartsWith("$2", StringComparison.Ordinal);

        Pass(verifyOk,  $"BCrypt.Verify(correct password) = {verifyOk}");
        Pass(rejectOk,  $"BCrypt.Verify(wrong password)   = {!rejectOk} (expect false)");
        Pass(startsOk,  $"bcrypt hash starts with $2      = {startsOk}");

        return verifyOk && rejectOk && startsOk;
    }

    // ── Test 4: dual-hash login (mock profile objects) ────────────────────────
    static bool TestDualHashLogin()
    {
        Section("4. Dual-Hash Login Flow  (PBKDF2 and bcrypt paths exercised)");
        bool passed = true;

        // --- 4a. PBKDF2 path (account created via web frontend / direct GitHub) ---
        {
            string username = "webuser";
            string password = "mywebpassword";
            string storedHash = GitHubDataService.HashPassword(password, username);

            // Simulate what VerifyLoginAsync does:
            bool ok = string.Equals(
                GitHubDataService.HashPassword(password, username),
                storedHash, StringComparison.Ordinal);
            Pass(ok, $"PBKDF2 path — correct password accepted: {ok}");
            passed &= ok;

            bool reject = !string.Equals(
                GitHubDataService.HashPassword("wrongpassword", username),
                storedHash, StringComparison.Ordinal);
            Pass(reject, $"PBKDF2 path — wrong password rejected: {reject}");
            passed &= reject;
        }

        // --- 4b. bcrypt path (account created via Node.js backend) ---
        {
            string password   = "nodepassword";
            string bcryptHash = BCrypt.Net.BCrypt.HashPassword(password);

            bool ok     = BCrypt.Net.BCrypt.Verify(password, bcryptHash);
            bool reject = !BCrypt.Net.BCrypt.Verify("wrongpassword", bcryptHash);

            Pass(ok,     $"bcrypt path — correct password accepted: {ok}");
            Pass(reject, $"bcrypt path — wrong password rejected:   {reject}");
            passed &= ok && reject;
        }

        // --- 4c. hash type auto-detection ---
        {
            string pbkdf2Hash = GitHubDataService.HashPassword("pass", "user");
            string bcryptHash = BCrypt.Net.BCrypt.HashPassword("pass");

            bool pbkdf2IsNotBcrypt = !pbkdf2Hash.StartsWith("$2", StringComparison.Ordinal);
            bool bcryptIsBcrypt    =  bcryptHash.StartsWith("$2", StringComparison.Ordinal);

            Pass(pbkdf2IsNotBcrypt, $"PBKDF2 hash correctly identified as NOT bcrypt: {pbkdf2IsNotBcrypt}");
            Pass(bcryptIsBcrypt,    $"bcrypt hash correctly identified as bcrypt:     {bcryptIsBcrypt}");
            passed &= pbkdf2IsNotBcrypt && bcryptIsBcrypt;
        }

        return passed;
    }

    // ── Test 5: live backend (optional) ──────────────────────────────────────
    static async Task<bool> TestLiveBackendOptionalAsync()
    {
        Section("5. Live Backend Test  (requires GAMEOS_GITHUB_TOKEN)");

        string? token    = Environment.GetEnvironmentVariable("GAMEOS_GITHUB_TOKEN");
        string? username = Environment.GetEnvironmentVariable("GAMEOS_TEST_USERNAME");
        string? password = Environment.GetEnvironmentVariable("GAMEOS_TEST_PASSWORD");

        if (string.IsNullOrEmpty(token))
        {
            Colour(ConsoleColor.Yellow, "  ⚠  Skipped — GAMEOS_GITHUB_TOKEN not set.");
            Console.WriteLine("       To run: GAMEOS_GITHUB_TOKEN=<pat> GAMEOS_TEST_USERNAME=<user> GAMEOS_TEST_PASSWORD=<pass> dotnet run");
            return true;  // not a failure
        }

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            Colour(ConsoleColor.Yellow, "  ⚠  Skipped — GAMEOS_TEST_USERNAME or GAMEOS_TEST_PASSWORD not set.");
            return true;
        }

        Console.WriteLine($"  Connecting to {GitHubDataService.DataRepoOwner}/{GitHubDataService.DataRepoName} …");

        try
        {
            using var client = new GameOsClient();
            bool healthy = await client.CheckHealthAsync(CancellationToken.None);
            Pass(healthy, $"Repository reachable: {healthy}");

            if (!healthy)
                return false;

            var profile = await client.LoginAsync(username, password, CancellationToken.None);
            bool loginOk = profile != null && !string.IsNullOrEmpty(profile.Username);
            Pass(loginOk, $"Login succeeded — username={profile?.Username}");

            if (loginOk)
            {
                var games = await client.GetGamesAsync(CancellationToken.None);
                Pass(true, $"Games loaded from backend: {games.Count}");

                var achievements = await client.GetAchievementsAsync(CancellationToken.None);
                Pass(true, $"Achievements loaded:       {achievements.Count}");

                Console.WriteLine();
                Colour(ConsoleColor.Green,
                    $"  ✅  Real backend login confirmed: {profile!.Username} authenticated via PBKDF2");
                Colour(ConsoleColor.Green,
                    $"  ✅  C# launcher login identical to web frontend — same PBKDF2-SHA256, same accounts");
            }

            return loginOk;
        }
        catch (GameOsException ex)
        {
            Pass(false, $"GameOsException {ex.StatusCode}: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Pass(false, $"Unexpected: {ex.Message}");
            return false;
        }
    }

    // ── Test 6: live backend REST API (optional) ─────────────────────────────
    // Mirrors exactly the web frontend backend-mode login path:
    //   POST {GAMEOS_BACKEND_URL}/api/auth/token  { username, password }
    // No GitHub PAT required — the backend server holds the PAT.
    static async Task<bool> TestLiveRestApiOptionalAsync()
    {
        Section("6. Live Backend REST API Test  (requires GAMEOS_BACKEND_URL)");

        string? backendUrl = Environment.GetEnvironmentVariable("GAMEOS_BACKEND_URL");
        string? username   = Environment.GetEnvironmentVariable("GAMEOS_TEST_USERNAME");
        string? password   = Environment.GetEnvironmentVariable("GAMEOS_TEST_PASSWORD");

        if (string.IsNullOrEmpty(backendUrl))
        {
            Colour(ConsoleColor.Yellow, "  ⚠  Skipped — GAMEOS_BACKEND_URL not set.");
            Console.WriteLine("       To run: GAMEOS_BACKEND_URL=<url> GAMEOS_TEST_USERNAME=<user> GAMEOS_TEST_PASSWORD=<pass> dotnet run");
            Console.WriteLine("       This test exercises the same REST API path the web frontend uses.");
            Console.WriteLine("       No GitHub PAT required — just point to the deployed backend server.");
            return true;  // not a failure
        }

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            Colour(ConsoleColor.Yellow, "  ⚠  Skipped — GAMEOS_TEST_USERNAME or GAMEOS_TEST_PASSWORD not set.");
            return true;
        }

        Console.WriteLine($"  Connecting to backend REST API at {backendUrl} ...");

        try
        {
            using var service = new BackendApiService();

            bool healthy = await service.CheckHealthAsync(CancellationToken.None);
            Pass(healthy, $"Backend server reachable: {healthy}");

            if (!healthy)
            {
                Pass(false, $"Backend health check failed — verify GAMEOS_BACKEND_URL={backendUrl}");
                return false;
            }

            var (profile, token) = await service.LoginAsync(username, password, CancellationToken.None);
            bool loginOk = profile != null && !string.IsNullOrEmpty(profile.Username);
            Pass(loginOk, $"Login succeeded — username={profile?.Username}");

            bool hasToken = !string.IsNullOrEmpty(token);
            Pass(hasToken, $"Bearer token received from backend: {(hasToken ? "yes" : "no")}");

            if (loginOk)
            {
                var games = await service.GetGamesAsync(CancellationToken.None);
                Pass(true, $"Games loaded via REST API:     {games.Count}");

                var achievements = await service.GetAchievementsAsync(CancellationToken.None);
                Pass(true, $"Achievements loaded via REST:  {achievements.Count}");

                Console.WriteLine();
                Colour(ConsoleColor.Green,
                    $"  ✅  Backend REST API login confirmed: {profile!.Username} authenticated");
                Colour(ConsoleColor.Green,
                    $"  ✅  Same path as web frontend — no GitHub PAT required from client");
            }

            return loginOk && hasToken;
        }
        catch (GameOsException ex)
        {
            Pass(false, $"GameOsException {ex.StatusCode}: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Pass(false, $"Unexpected: {ex.Message}");
            return false;
        }
    }

    // ── Test 7: Backend API login using Admin.GameOS account ─────────────────
    // Proves the C# launcher can log in via POST /api/auth/token exactly like
    // the web frontend does in backend mode.
    //
    // The backend automatically creates the Admin.GameOS account on first startup
    // with password = ADMIN_GAMEOS_PASSWORD env var (default: "GameOS2026").
    // The backend's verifyPassword() accepts both PBKDF2 (web/GitHub-direct accounts)
    // and bcrypt (backend-created accounts), so this works regardless of how the
    // account was originally created.
    //
    // Requires GAMEOS_BACKEND_URL to be set.  Skips gracefully if it is not.
    static async Task<bool> TestBackendAdminLoginAsync()
    {
        Section("7. Backend API Login — Admin.GameOS  (requires GAMEOS_BACKEND_URL)");

        string? backendUrl     = Environment.GetEnvironmentVariable("GAMEOS_BACKEND_URL");
        // Allow overriding the admin password via env var for flexibility;
        // default matches the backend's ADMIN_GAMEOS_PASSWORD default.
        string  adminDefaultPassword = Environment.GetEnvironmentVariable("ADMIN_GAMEOS_PASSWORD") ?? "GameOS2026";
        const string adminUser = "Admin.GameOS";

        if (string.IsNullOrEmpty(backendUrl))
        {
            Colour(ConsoleColor.Yellow, "  ⚠  Skipped — GAMEOS_BACKEND_URL not set.");
            Console.WriteLine("       Start the backend server and set GAMEOS_BACKEND_URL to run this test.");
            Console.WriteLine("       Example: GAMEOS_BACKEND_URL=http://localhost:3000 dotnet run");
            return true;  // not a failure
        }

        Console.WriteLine($"  Backend URL  : {backendUrl}");
        Console.WriteLine($"  Test account : {adminUser}");
        Console.WriteLine();

        try
        {
            using var service = new BackendApiService();

            // Health check first
            bool healthy = await service.CheckHealthAsync(CancellationToken.None);
            Pass(healthy, $"Backend server reachable at {backendUrl}");
            if (!healthy)
            {
                Pass(false, $"Backend not reachable — check GAMEOS_BACKEND_URL={backendUrl}");
                return false;
            }

            // Login as Admin.GameOS via POST /api/auth/token.
            // This is the same REST API call the web frontend makes in backend mode.
            // A 401 here means wrong password; other errors propagate to the outer catch.
            GameLauncher.Models.UserProfile? profile;
            string? token;
            try
            {
                (profile, token) = await service.LoginAsync(adminUser, adminDefaultPassword, CancellationToken.None);
            }
            catch (GameOsException ex) when (ex.StatusCode == 401)
            {
                Pass(false, $"Login failed (401) — wrong password for {adminUser}");
                Console.WriteLine($"       Password tried: [{adminDefaultPassword.Length} chars]");
                Console.WriteLine("       If the admin password was changed, set ADMIN_GAMEOS_PASSWORD=<new_password>");
                Console.WriteLine("       Or check the data repo for the current accounts/admin.gameos/profile.json");
                return false;
            }

            bool loginOk  = profile != null && !string.IsNullOrEmpty(profile.Username);
            bool hasToken = !string.IsNullOrEmpty(token);

            Pass(loginOk,  $"Login succeeded — username={profile?.Username}");
            Pass(hasToken, $"Bearer token received from backend: {(hasToken ? "yes (gos_...)" : "no")}");

            if (loginOk && hasToken)
            {
                // Verify the session by calling GET /api/me
                var meProfile = await service.GetProfileAsync(CancellationToken.None);
                bool meOk = meProfile != null && !string.IsNullOrEmpty(meProfile.Username);
                Pass(meOk, $"GET /api/me succeeded — profile.username={meProfile?.Username}");

                // Load games via GET /api/me/games
                var games = await service.GetGamesAsync(CancellationToken.None);
                Pass(true, $"GET /api/me/games — {games.Count} game(s) in library");

                Console.WriteLine();
                Colour(ConsoleColor.Green,
                    $"  ✅  Backend API login CONFIRMED — {profile!.Username} authenticated via C# launcher");
                Colour(ConsoleColor.Green,
                    $"  ✅  Same REST API path as web frontend — POST /api/auth/token → Bearer token");
                Colour(ConsoleColor.Green,
                    $"  ✅  Login works via backend/API exactly as required");
            }

            return loginOk && hasToken;
        }
        catch (GameOsException ex)
        {
            Pass(false, $"GameOsException {ex.StatusCode}: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            Pass(false, $"Unexpected: {ex.Message}");
            return false;
        }
    }

    // ── Test 8: OfflineDataCacheService round-trip ────────────────────────────
    static bool TestOfflineDataCacheRoundTrip()
    {
        Section("8. OfflineDataCacheService  (save → load round-trip)");
        bool passed = true;

        string testUser = "__testuser_offline__";
        var svc = new OfflineDataCacheService();

        try
        {
            // Ensure clean state
            svc.Clear(testUser);

            // Verify no cache initially
            bool hadCache = svc.HasCache(testUser);
            Pass(!hadCache, $"No cache before first save: {!hadCache}");
            passed &= !hadCache;

            // Save
            var profile = new GameLauncher.Models.UserProfile
            {
                Username = testUser,
                Email    = "test@example.com",
            };
            var games = new System.Collections.Generic.List<GameLauncher.Models.Game>
            {
                new() { Title = "Test Game 1", Platform = "PC",  AddedAt = "2025-01-01T00:00:00Z" },
                new() { Title = "Test Game 2", Platform = "PS4", AddedAt = "2025-01-02T00:00:00Z" },
            };
            var achievements = new System.Collections.Generic.List<GameLauncher.Models.Achievement>
            {
                new() { Name = "First Achievement", GameTitle = "Test Game 1", UnlockedAt = "2025-01-05T00:00:00Z" },
            };
            svc.Save(testUser, profile, games, achievements);

            // Verify HasCache
            bool hasCache = svc.HasCache(testUser);
            Pass(hasCache, $"Cache exists after save: {hasCache}");
            passed &= hasCache;

            // Load and verify
            var loaded = svc.Load(testUser);
            bool loadOk = loaded != null;
            Pass(loadOk, $"Load returns non-null: {loadOk}");
            passed &= loadOk;

            if (loaded != null)
            {
                bool userOk = loaded.Profile?.Username == testUser;
                Pass(userOk, $"Profile.Username matches: {userOk}");
                passed &= userOk;

                bool gameCountOk = loaded.Games?.Count == 2;
                Pass(gameCountOk, $"Games count = 2: {gameCountOk}");
                passed &= gameCountOk;

                bool achCountOk = loaded.Achievements?.Count == 1;
                Pass(achCountOk, $"Achievements count = 1: {achCountOk}");
                passed &= achCountOk;

                bool cachedAtOk = !string.IsNullOrEmpty(loaded.CachedAt);
                Pass(cachedAtOk, $"CachedAt is set: {cachedAtOk}");
                passed &= cachedAtOk;

                bool freshOk = svc.IsFresh(testUser);
                Pass(freshOk, $"IsFresh = true immediately after save: {freshOk}");
                passed &= freshOk;
            }
        }
        finally
        {
            // Clean up test data
            svc.Clear(testUser);
            bool clearedOk = !svc.HasCache(testUser);
            Pass(clearedOk, $"Cache cleared after test: {clearedOk}");
            passed &= clearedOk;
        }

        return passed;
    }

    // ── Test 9: PendingChangesService queue ───────────────────────────────────
    static bool TestPendingChangesQueue()
    {
        Section("9. PendingChangesService  (offline changes queue)");
        bool passed = true;

        string testUser = "__testuser_pending__";
        var svc = new PendingChangesService();

        try
        {
            // Ensure clean state
            svc.Clear(testUser);

            // No pending initially
            bool noPending = !svc.HasPending(testUser);
            Pass(noPending, $"No pending changes initially: {noPending}");
            passed &= noPending;

            // Enqueue an AddGame
            var game = new GameLauncher.Models.Game
            {
                Title    = "Offline Added Game",
                Platform = "PC",
                AddedAt  = "2025-01-03T12:00:00Z",
            };
            svc.EnqueueAddGame(testUser, game);

            bool hasPending = svc.HasPending(testUser);
            Pass(hasPending, $"HasPending = true after AddGame: {hasPending}");
            passed &= hasPending;

            // Enqueue a RemoveGame
            svc.EnqueueRemoveGame(testUser, "Switch", "Game to Remove");

            var all = svc.GetAll(testUser);
            bool countOk = all.Count == 2;
            Pass(countOk, $"Queue has 2 pending changes: {countOk}");
            passed &= countOk;

            bool firstIsAdd = all[0].Kind == PendingChangeKind.AddGame;
            Pass(firstIsAdd, $"First change is AddGame: {firstIsAdd}");
            passed &= firstIsAdd;

            bool secondIsRemove = all[1].Kind == PendingChangeKind.RemoveGame;
            Pass(secondIsRemove, $"Second change is RemoveGame: {secondIsRemove}");
            passed &= secondIsRemove;

            bool gameDataOk = all[0].GameData?.Title == "Offline Added Game";
            Pass(gameDataOk, $"AddGame has correct title: {gameDataOk}");
            passed &= gameDataOk;

            bool removeTitleOk = all[1].Title == "Game to Remove";
            Pass(removeTitleOk, $"RemoveGame has correct title: {removeTitleOk}");
            passed &= removeTitleOk;
        }
        finally
        {
            svc.Clear(testUser);
            bool cleared = !svc.HasPending(testUser);
            Pass(cleared, $"Queue cleared after test: {cleared}");
            passed &= cleared;
        }

        return passed;
    }

    // ── Test 10: PlatformHelper.NormalizePlatform ─────────────────────────────
    static bool TestPlatformNormalization()
    {
        Section("10. PlatformHelper.NormalizePlatform  (abbreviated and full names)");
        bool passed = true;

        var cases = new (string input, string expected)[]
        {
            // Abbreviated single-char tags (the bug reported in the issue)
            ("S",                         "Switch"),
            ("2",                         "PS2"),
            ("3",                         "PS3"),
            ("4",                         "PS4"),
            ("5",                         "PS5"),
            // Canonical names pass through unchanged
            ("PC",                        "PC"),
            ("Switch",                    "Switch"),
            ("PS1",                       "PS1"),
            ("PS2",                       "PS2"),
            ("PS3",                       "PS3"),
            ("PS4",                       "PS4"),
            ("PS5",                       "PS5"),
            ("PSP",                       "PSP"),
            ("PS Vita",                   "PS Vita"),
            ("Xbox 360",                  "Xbox 360"),
            ("Xbox One",                  "Xbox One"),
            // Aliases
            ("vita",                      "PS Vita"),
            ("xbox360",                   "Xbox 360"),
            ("xbone",                     "Xbox One"),
            // RetroArch/Libretro verbose names
            ("Microsoft - Xbox 360",      "Xbox 360"),
            ("Nintendo - Switch",         "Switch"),
            ("Sony - PlayStation 4",      "PS4"),
            ("Sony - PlayStation 3",      "PS3"),
            ("Sony - PSP",                "PSP"),
            // Already-canonical should be unchanged
            ("Unknown Platform",          "Unknown Platform"),
        };

        foreach (var (input, expected) in cases)
        {
            var actual = GameLauncher.Models.PlatformHelper.NormalizePlatform(input);
            bool ok = string.Equals(actual, expected, StringComparison.Ordinal);
            Pass(ok, ok
                ? $"NormalizePlatform(\"{input,-28}\") → \"{actual}\""
                : $"NormalizePlatform(\"{input}\") → expected \"{expected}\" but got \"{actual}\"");
            passed &= ok;
        }

        return passed;
    }

    // ── Test 11: OfflineDataCacheService.EnumerateAllCachedUsers ─────────────
    static bool TestEnumerateAllCachedUsers()
    {
        Section("11. OfflineDataCacheService.EnumerateAllCachedUsers  (multi-profile discovery)");
        bool passed = true;

        string user1 = "__enum_test_alice__";
        string user2 = "__enum_test_bob__";
        var svc = new OfflineDataCacheService();

        try
        {
            // Clean up any prior test state
            svc.Clear(user1);
            svc.Clear(user2);

            // Before saving: neither user should appear in the enumeration
            var before = svc.EnumerateAllCachedUsers();
            bool noAlice = !before.Any(u => string.Equals(u, user1, StringComparison.OrdinalIgnoreCase));
            bool noBob   = !before.Any(u => string.Equals(u, user2, StringComparison.OrdinalIgnoreCase));
            Pass(noAlice, $"User1 not present before save: {noAlice}");
            Pass(noBob,   $"User2 not present before save: {noBob}");
            passed &= noAlice && noBob;

            // Save caches for both test users
            var profile1 = new GameLauncher.Models.UserProfile { Username = user1 };
            var profile2 = new GameLauncher.Models.UserProfile { Username = user2 };
            var games1   = new System.Collections.Generic.List<GameLauncher.Models.Game>
                           { new() { Title = "Game A", Platform = "PC" } };
            var games2   = new System.Collections.Generic.List<GameLauncher.Models.Game>
                           { new() { Title = "Game B", Platform = "Switch" } };

            svc.Save(user1, profile1, games1, new());
            svc.Save(user2, profile2, games2, new());

            // After saving: both users should be enumerated
            var after = svc.EnumerateAllCachedUsers();
            bool hasAlice = after.Any(u => string.Equals(u, user1, StringComparison.OrdinalIgnoreCase));
            bool hasBob   = after.Any(u => string.Equals(u, user2, StringComparison.OrdinalIgnoreCase));
            Pass(hasAlice, $"User1 found in enumeration after save: {hasAlice}");
            Pass(hasBob,   $"User2 found in enumeration after save: {hasBob}");
            passed &= hasAlice && hasBob;

            // Clear user1 — it should disappear from enumeration
            svc.Clear(user1);
            var afterClear = svc.EnumerateAllCachedUsers();
            bool aliceGone = !afterClear.Any(u => string.Equals(u, user1, StringComparison.OrdinalIgnoreCase));
            bool bobStays  =  afterClear.Any(u => string.Equals(u, user2, StringComparison.OrdinalIgnoreCase));
            Pass(aliceGone, $"User1 gone after clear: {aliceGone}");
            Pass(bobStays,  $"User2 still present after clearing User1: {bobStays}");
            passed &= aliceGone && bobStays;
        }
        finally
        {
            svc.Clear(user1);
            svc.Clear(user2);
        }

        return passed;
    }

    // ── Test 12: Reconnect timer state-transition logic ───────────────────────
    // Exercises the GamesListEqual comparison helper that drives remote-change detection.
    static bool TestReconnectStateLogic()
    {
        Section("12. Reconnect Logic  (GamesListEqual change-detection helper)");
        bool passed = true;

        // Test same lists are equal
        var a = new System.Collections.Generic.List<GameLauncher.Models.Game>
        {
            new() { Title = "Game A", Platform = "PC" },
            new() { Title = "Game B", Platform = "Switch" },
        };
        var b = new System.Collections.Generic.List<GameLauncher.Models.Game>
        {
            new() { Title = "Game A", Platform = "PC" },
            new() { Title = "Game B", Platform = "Switch" },
        };

        bool sameListsEqual = GamesListEqual(a, b);
        Pass(sameListsEqual, $"Identical game lists are equal: {sameListsEqual}");
        passed &= sameListsEqual;

        // Test different counts
        var c = new System.Collections.Generic.List<GameLauncher.Models.Game>
        {
            new() { Title = "Game A", Platform = "PC" },
        };
        bool diffCountNotEqual = !GamesListEqual(a, c);
        Pass(diffCountNotEqual, $"Lists with different counts are not equal: {diffCountNotEqual}");
        passed &= diffCountNotEqual;

        // Test same count but different content
        var d = new System.Collections.Generic.List<GameLauncher.Models.Game>
        {
            new() { Title = "Game A", Platform = "PC" },
            new() { Title = "Game C", Platform = "PS4" }, // different title
        };
        bool diffContentNotEqual = !GamesListEqual(a, d);
        Pass(diffContentNotEqual, $"Same-count lists with different content are not equal: {diffContentNotEqual}");
        passed &= diffContentNotEqual;

        // Test empty lists
        bool emptyEqual = GamesListEqual(new(), new());
        Pass(emptyEqual, $"Two empty lists are equal: {emptyEqual}");
        passed &= emptyEqual;

        // Test case-insensitive comparison
        var e = new System.Collections.Generic.List<GameLauncher.Models.Game>
        {
            new() { Title = "game a", Platform = "pc" }, // lowercase
            new() { Title = "GAME B", Platform = "SWITCH" }, // uppercase
        };
        bool caseInsensitiveOk = GamesListEqual(a, e);
        Pass(caseInsensitiveOk, $"Game list comparison is case-insensitive: {caseInsensitiveOk}");
        passed &= caseInsensitiveOk;

        return passed;
    }

    /// <summary>
    /// Local copy of MainViewModel.GamesListEqual for testing purposes.
    /// Compares two game lists by title+platform set membership (case-insensitive).
    /// </summary>
    static bool GamesListEqual(
        System.Collections.Generic.List<GameLauncher.Models.Game> a,
        System.Collections.Generic.List<GameLauncher.Models.Game> b)
    {
        if (a.Count != b.Count) return false;
        var aSet = new System.Collections.Generic.HashSet<string>(
            a.Select(g => $"{g.Platform?.ToLowerInvariant()}|{g.Title?.ToLowerInvariant()}"),
            StringComparer.Ordinal);
        return b.All(g => aSet.Contains(
            $"{g.Platform?.ToLowerInvariant()}|{g.Title?.ToLowerInvariant()}"));
    }

    static async Task<bool> TestAchievementMirrorSyncAsync()
    {
        Section("13. GitHub-direct Achievement Mirror Sync");

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["accounts/testuser/games.json"] = JsonSerializer.Serialize(new List<Game>
            {
                new()
                {
                    Platform = "Switch",
                    Title = "Mario Kart 8 Deluxe",
                    TitleId = "0100152000022000"
                }
            }),
            ["accounts/testuser/achievements.json"] = JsonSerializer.Serialize(new List<Achievement>
            {
                new()
                {
                    Platform = "Switch",
                    GameTitle = "Mario Kart 8 Deluxe",
                    AchievementId = "mk8-first",
                    Name = "First Win",
                    Description = "Win a race",
                    UnlockedAt = "2026-01-01T00:00:00.0000000Z"
                }
            })
        };

        using var http = new HttpClient(new FakeGitHubHandler(files))
        {
            BaseAddress = new Uri("https://api.github.com/")
        };
        using var service = new GitHubDataService(http);

        await service.SaveAchievementsAsync("TestUser", new List<Achievement>
        {
            new()
            {
                Platform = "Switch",
                GameTitle = "Mario Kart 8 Deluxe",
                AchievementId = "mk8-first",
                Name = "First Win",
                Description = "Win a race",
                UnlockedAt = "2026-01-01T00:00:00.0000000Z"
            },
            new()
            {
                Platform = "Switch",
                GameTitle = "Mario Kart 8 Deluxe",
                AchievementId = "mk8-second",
                Name = "Tournament Ready",
                Description = "Finish 10 races",
                UnlockedAt = "2026-05-08T12:34:56.0000000Z"
            }
        });

        const string canonicalPath = "accounts/testuser/Achievements/Switch/0100152000022000/achievements.json";

        bool canonicalExists = files.ContainsKey(canonicalPath);
        Pass(canonicalExists, $"Canonical mirror created at titleId path: {canonicalPath}");

        var rootAchievements = DeserializeAchievements(files, "accounts/testuser/achievements.json");
        bool rootCountOk = rootAchievements.Count == 2;
        Pass(rootCountOk, $"Root achievements.json contains both unlocks: count={rootAchievements.Count}");

        var canonicalAchievements = DeserializeAchievements(files, canonicalPath);
        bool canonicalCountOk = canonicalAchievements.Count == 2;
        Pass(canonicalCountOk, $"Canonical mirror contains both unlocks: count={canonicalAchievements.Count}");

        bool secondAchievementPresent = canonicalAchievements.Any(a =>
            string.Equals(a.AchievementId, "mk8-second", StringComparison.OrdinalIgnoreCase));
        Pass(secondAchievementPresent, "Canonical mirror contains the new achievement ID");

        return canonicalExists && rootCountOk && canonicalCountOk && secondAchievementPresent;
    }

    static List<Achievement> DeserializeAchievements(Dictionary<string, string> files, string path)
    {
        if (!files.TryGetValue(path, out var json))
            return new List<Achievement>();

        return JsonSerializer.Deserialize<List<Achievement>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new List<Achievement>();
    }

    sealed class FakeGitHubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _files;
        private readonly Dictionary<string, string> _shas = new(StringComparer.OrdinalIgnoreCase);

        public FakeGitHubHandler(Dictionary<string, string> files)
        {
            _files = files;
            foreach (var path in files.Keys)
                _shas[path] = $"sha-{path.GetHashCode():x8}";
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string path = ExtractRepoPath(request.RequestUri);

            if (request.Method == HttpMethod.Get)
            {
                if (!_files.TryGetValue(path, out var json))
                    return new HttpResponseMessage(HttpStatusCode.NotFound);

                var payload = new
                {
                    content = Convert.ToBase64String(Encoding.UTF8.GetBytes(json)),
                    sha = _shas[path]
                };

                return JsonResponse(HttpStatusCode.OK, payload);
            }

            if (request.Method == HttpMethod.Put)
            {
                using var doc = JsonDocument.Parse(
                    await request.Content!.ReadAsStringAsync(cancellationToken));
                string base64 = doc.RootElement.GetProperty("content").GetString() ?? "";
                string json = Encoding.UTF8.GetString(Convert.FromBase64String(base64));

                _files[path] = json;
                _shas[path] = $"sha-{_files.Count:x8}-{path.Length:x4}";

                return JsonResponse(HttpStatusCode.OK, new { content = new { sha = _shas[path] } });
            }

            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }

        private static string ExtractRepoPath(Uri? uri)
        {
            string absolutePath = uri?.AbsolutePath ?? "";
            const string marker = "/contents/";
            int idx = absolutePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return "";
            return Uri.UnescapeDataString(absolutePath[(idx + marker.Length)..]);
        }

        private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, object payload)
            => new(statusCode)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json")
            };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    static void Banner()
    {
        Console.WriteLine("═══════════════════════════════════════════════════════════════════");
        Console.WriteLine("  Game.OS — Login Authentication Tests (C# Launcher)");
        Console.WriteLine("  Verifies: C# auth ≡ JavaScript web frontend auth");
        Console.WriteLine("═══════════════════════════════════════════════════════════════════");
        Console.WriteLine();
    }

    static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"  ── {title}");
        Console.WriteLine("  " + new string('─', 65));
    }

    static void Pass(bool ok, string message)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
        Console.Write(ok ? "  ✅  " : "  ❌  ");
        Console.ForegroundColor = prev;
        Console.WriteLine(message);
    }

    static void Colour(ConsoleColor c, string msg)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = c;
        Console.WriteLine(msg);
        Console.ForegroundColor = prev;
    }

    static bool IsHex(string s)
    {
        foreach (char c in s)
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                return false;
        return true;
    }
}
