using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameLauncher.Models;
using GameLauncher.Services;

namespace GameLauncher.ViewModels;

public partial class LoginViewModel : ViewModelBase
{
    private readonly GameOsClient        _client;
    private readonly SessionCacheService _cache;
    private readonly OfflineDataCacheService _offlineCache;

    [ObservableProperty] private string _username  = "";
    [ObservableProperty] private string _password  = "";
    [ObservableProperty] private string _email     = "";
    [ObservableProperty] private string _confirmPassword = "";
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private bool   _isLoading    = false;
    [ObservableProperty] private bool   _showRegister = false;
    /// <summary>When true the session token is saved to disk so the next launch
    /// auto-logs the user in — mirrors the "Remember me" checkbox on the website.</summary>
    [ObservableProperty] private bool   _rememberMe   = true;

    /// <summary>Local saved accounts shown in the quick-login panel.
    /// Populated from <see cref="SessionCacheService"/> so the list always
    /// reflects real previously-logged-in accounts.</summary>
    public ObservableCollection<SavedSession> SavedAccounts { get; } = new();

    /// <summary>
    /// Invoked on successful login/restore.  The bool parameter is <c>true</c>
    /// when the session was restored from the local cache (offline mode).
    /// </summary>
    public System.Action<UserProfile, List<Game>, List<Achievement>, bool>? OnLoginSuccess { get; set; }

    public LoginViewModel(GameOsClient client, SessionCacheService cache,
                          OfflineDataCacheService offlineCache)
    {
        _client       = client;
        _cache        = cache;
        _offlineCache = offlineCache;
        RefreshSavedAccounts();
    }

    // ── Auto-login on startup ─────────────────────────────────────────────

    /// <summary>
    /// Called once at application startup.  Tries to restore the session online
    /// first; if the server is unreachable and local cached data exists the session
    /// is restored in offline mode.  Shows a clear error if there is no cache and
    /// no internet — the user must log in online at least once.
    /// </summary>
    public async System.Threading.Tasks.Task TryAutoLoginAsync()
    {
        var saved = _cache.GetRememberedSession();
        if (saved == null) return;

        IsLoading    = true;
        ErrorMessage = "";
        try
        {
            // ── Online path ───────────────────────────────────────────────
            var profile      = await _client.RestoreSessionAsync(saved.Token, saved.Username);
            var games        = await _client.GetGamesAsync();
            var achievements = await _client.GetAchievementsAsync();
            EnrichGames(games);
            OnLoginSuccess?.Invoke(profile, games, achievements, false);
        }
        catch (Exception ex) when (ex is GameOsException or System.Net.Http.HttpRequestException)
        {
            System.Diagnostics.Debug.WriteLine($"[AutoLogin] Online restore failed: {ex.Message}");

            // ── Offline fallback ──────────────────────────────────────────
            // If the failure is a definitive auth rejection (401/403) from the
            // server, clear the stale token.  For connectivity errors, try the
            // local cache instead of showing the login form.
            bool isAuthFailure = ex is GameOsException ge && (ge.StatusCode == 401 || ge.StatusCode == 403);

            if (isAuthFailure)
            {
                _cache.ClearToken(saved.Username);
                _client.Logout();
                ErrorMessage = "";
                return;
            }

            // Network/server unavailable — attempt offline login from cache
            var cached = _offlineCache.Load(saved.Username);
            if (cached?.Profile != null && cached.Games != null && cached.Achievements != null)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[AutoLogin] Falling back to offline cache for '{saved.Username}'.");
                EnrichGames(cached.Games);
                OnLoginSuccess?.Invoke(
                    cached.Profile, cached.Games, cached.Achievements, true);
            }
            else
            {
                // No cache — clear the token and show the login form
                _cache.ClearToken(saved.Username);
                _client.Logout();
                ErrorMessage = "";
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────

    [RelayCommand]
    private async System.Threading.Tasks.Task SignInAsync()
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Please enter your username and password.";
            return;
        }

        IsLoading = true;
        ErrorMessage = "";
        try
        {
            var profile      = await _client.LoginAsync(Username, Password);
            var games        = await _client.GetGamesAsync();
            var achievements = await _client.GetAchievementsAsync();
            EnrichGames(games);

            // Persist the session so the user stays logged in across launches
            // (same as the website writing to localStorage when "Remember me" is checked).
            _cache.SaveSession(new CachedSession
            {
                Username    = profile.Username,
                Email       = profile.Email,
                Token       = _client.Token ?? "",
                AvatarColor = "#1e90ff",
                SavedAt     = System.DateTime.UtcNow.ToString("o"),
                RememberMe  = RememberMe,
            });
            RefreshSavedAccounts();

            OnLoginSuccess?.Invoke(profile, games, achievements, false);
        }
        catch (GameOsException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (System.Net.Http.HttpRequestException)
        {
            // Server unreachable — try offline login if a cache exists
            var cached = _offlineCache.Load(Username.Trim());
            if (cached?.Profile != null && cached.Games != null && cached.Achievements != null)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[SignIn] Server unreachable — loading offline cache for '{Username}'.");
                EnrichGames(cached.Games);
                _cache.SaveSession(new CachedSession
                {
                    Username    = cached.Profile.Username,
                    Email       = cached.Profile.Email,
                    Token       = "",          // no valid token in offline mode
                    AvatarColor = "#1e90ff",
                    SavedAt     = System.DateTime.UtcNow.ToString("o"),
                    RememberMe  = RememberMe,
                });
                RefreshSavedAccounts();
                OnLoginSuccess?.Invoke(cached.Profile, cached.Games, cached.Achievements, true);
            }
            else
            {
                ErrorMessage =
                    "Cannot reach the Game.OS server and no local cache was found. " +
                    "Please connect to the internet to log in for the first time.";
            }
        }
        catch (System.Exception ex)
        {
            ErrorMessage = $"Connection error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task RegisterAsync()
    {
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Email)
            || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Please fill in all fields.";
            return;
        }
        if (Password != ConfirmPassword)
        {
            ErrorMessage = "Passwords do not match.";
            return;
        }
        if (Password.Length < 8)
        {
            ErrorMessage = "Password must be at least 8 characters.";
            return;
        }

        IsLoading = true;
        ErrorMessage = "";
        try
        {
            var profile = await _client.RegisterAsync(Username, Email, Password);

            _cache.SaveSession(new CachedSession
            {
                Username    = profile.Username,
                Email       = profile.Email,
                Token       = _client.Token ?? "",
                AvatarColor = "#1e90ff",
                SavedAt     = System.DateTime.UtcNow.ToString("o"),
                RememberMe  = RememberMe,
            });
            RefreshSavedAccounts();

            OnLoginSuccess?.Invoke(profile, new List<Game>(), new List<Achievement>(), false);
        }
        catch (GameOsException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (System.Exception ex)
        {
            ErrorMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void ToggleForm()
    {
        ShowRegister = !ShowRegister;
        ErrorMessage = "";
    }

    /// <summary>
    /// Quick-login — if the saved session has a token, restore it silently.
    /// Falls back to offline cached data when the server is unreachable.
    /// Otherwise pre-fills the username field so the user just needs to type
    /// the password.
    /// </summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task QuickLogin(SavedSession? session)
    {
        if (session == null) return;

        // Try token-based silent restore first
        var cached = _cache.GetSession(session.Username);
        if (cached != null && !string.IsNullOrEmpty(cached.Token))
        {
            IsLoading    = true;
            ErrorMessage = "";
            try
            {
                var profile      = await _client.RestoreSessionAsync(cached.Token, cached.Username);
                var games        = await _client.GetGamesAsync();
                var achievements = await _client.GetAchievementsAsync();
                EnrichGames(games);
                OnLoginSuccess?.Invoke(profile, games, achievements, false);
                return;
            }
            catch (Exception ex) when (ex is GameOsException ge &&
                                        (ge.StatusCode == 401 || ge.StatusCode == 403))
            {
                // Definitive auth failure — clear token and fall through to password form
                _cache.ClearToken(session.Username);
                _client.Logout();
                System.Diagnostics.Debug.WriteLine($"[QuickLogin] Auth rejected: {ex.Message}");
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or GameOsException)
            {
                // Network/server unavailable — try offline cache
                System.Diagnostics.Debug.WriteLine($"[QuickLogin] Offline fallback: {ex.Message}");
                var offlineData = _offlineCache.Load(session.Username);
                if (offlineData?.Profile != null && offlineData.Games != null
                    && offlineData.Achievements != null)
                {
                    EnrichGames(offlineData.Games);
                    OnLoginSuccess?.Invoke(
                        offlineData.Profile, offlineData.Games, offlineData.Achievements, true);
                    return;
                }
                else
                {
                    ErrorMessage = "Cannot reach the server. Connect to the internet to sign in.";
                }
            }
            finally
            {
                IsLoading = false;
            }
        }

        // Token not available or expired — pre-fill username so the user
        // only needs to enter the password (same as web behaviour).
        Username     = session.Username;
        Password     = "";
        ErrorMessage = "";
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private void RefreshSavedAccounts()
    {
        SavedAccounts.Clear();
        foreach (var s in _cache.GetSavedAccounts())
            SavedAccounts.Add(s);
    }

    /// <summary>
    /// Enrich API-returned games with UI metadata (cover URL, genre, etc.)
    /// that the backend does not store — same as the website falling back to
    /// static game metadata when the API response lacks those fields.
    /// </summary>
    private static void EnrichGames(List<Game> games)
    {
        foreach (var g in games)
        {
            // Normalize platform names that may be abbreviated in stored data
            g.Platform = PlatformHelper.NormalizePlatform(g.Platform);

            var meta = GameCatalog.Metadata.FirstOrDefault(d =>
                d.Title.Equals(g.Title, System.StringComparison.OrdinalIgnoreCase));
            if (meta != null)
            {
                g.Genre       ??= meta.Genre;
                g.Description ??= meta.Description;
                g.Rating      ??= meta.Rating;
                g.CoverColor  ??= meta.CoverColor;
                g.CoverUrl    ??= meta.CoverUrl;
            }
        }
    }
}

