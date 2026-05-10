using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace GameLauncher.Models
{
    public class UserProfile
    {
        [JsonPropertyName("username")]            public string  Username     { get; set; } = "";
        [JsonPropertyName("email")]               public string  Email        { get; set; } = "";
        [JsonPropertyName("password_hash")]       public string  PasswordHash { get; set; } = "";
        [JsonPropertyName("created_at")]          public string  CreatedAt    { get; set; } = "";
        [JsonPropertyName("api_token_issued_at")] public string? TokenIssuedAt{ get; set; }
        /// <summary>Cloud-synced GamerScore total, updated after each play session.</summary>
        [JsonPropertyName("gamerScore")]          public int?    GamerScore   { get; set; }
        /// <summary>Steam 64-bit User ID, synced to the cloud so Device B can read it.</summary>
        [JsonPropertyName("steamUserId")]         public string? SteamUserId  { get; set; }
        /// <summary>Total owned game count (cloud + local + repacks + roms), synced to cloud.</summary>
        [JsonPropertyName("totalGames")]          public int?    TotalGames   { get; set; }
    }

    public class Game
    {
        [JsonPropertyName("platform")]           public string        Platform           { get; set; } = "";
        [JsonPropertyName("title")]              public string        Title              { get; set; } = "";
        [JsonPropertyName("titleId")]            public string?       TitleId            { get; set; }
        [JsonPropertyName("coverUrl")]           public string?       CoverUrl           { get; set; }
        [JsonPropertyName("screenshots")]        public List<string>? Screenshots        { get; set; }
        [JsonPropertyName("addedAt")]            public string        AddedAt            { get; set; } = "";
        [JsonPropertyName("lastPlayedAt")]       public string?       LastPlayedAt       { get; set; }
        [JsonPropertyName("playtimeMinutes")]    public int           PlaytimeMinutes    { get; set; }
        [JsonPropertyName("genre")]              public string?       Genre              { get; set; }
        [JsonPropertyName("description")]        public string?       Description        { get; set; }
        [JsonPropertyName("rating")]             public double?       Rating             { get; set; }
        [JsonPropertyName("price")]              public string?       Price              { get; set; }
        [JsonPropertyName("mods")]               public List<ModLink>? Mods              { get; set; }
        [JsonPropertyName("sysSpecMin")]         public SystemSpec?   SysSpecMin         { get; set; }
        [JsonPropertyName("sysSpecRecommended")] public SystemSpec?   SysSpecRecommended { get; set; }
        [JsonPropertyName("achievementsUrl")]    public string?       AchievementsUrl    { get; set; }
        [JsonPropertyName("trailerUrl")]         public string?       TrailerUrl         { get; set; }
        [JsonPropertyName("exophaseUrl")]        public string?       ExophaseUrl        { get; set; }
        /// <summary>Steam App ID for games sourced from the Steam API (0 or null = not a Steam game).</summary>
        [JsonPropertyName("steamAppId")]         public long?         SteamAppId         { get; set; }
        // UI-only (not persisted) – enriched from demo data
        [JsonIgnore] public string?  CoverColor    { get; set; }
        [JsonIgnore] public string?  CoverGradient { get; set; }
        // UI-only – per-game achievements loaded from AchievementsUrl / passed at login
        [JsonIgnore] public List<Achievement>? GameAchievements { get; set; }
        /// <summary>
        /// Total number of achievements defined for this game (from the database/cache).
        /// Populated once the full achievement template is loaded.  0 means unknown.
        /// </summary>
        [JsonIgnore] public int TotalAchievements { get; set; }
        [JsonIgnore] public string   RatingStars   =>
            Rating.HasValue ? new string('★', (int)System.Math.Round(Rating.Value / 2.0))
                              + new string('☆', 5 - (int)System.Math.Round(Rating.Value / 2.0)) : "—";
        /// <summary>Human-readable playtime string, e.g. "3h 20m" or "45m".</summary>
        [JsonIgnore] public string PlaytimeLabel =>
            PlaytimeMinutes >= 60
                ? $"{PlaytimeMinutes / 60}h {PlaytimeMinutes % 60}m"
                : PlaytimeMinutes > 0 ? $"{PlaytimeMinutes}m" : "";
        /// <summary>
        /// Achievement count label shown on library cards (e.g. "🏆 12 / 50").
        /// Uses <see cref="TotalAchievements"/> as the denominator when it is known
        /// (non-zero); otherwise falls back to <see cref="GameAchievements"/>.Count
        /// so the label is still shown while the full template is being loaded.
        /// Returns empty string when no achievements are loaded.
        /// </summary>
        [JsonIgnore] public string AchievementCountLabel
        {
            get
            {
                if (GameAchievements == null || GameAchievements.Count == 0) return "";
                int unlocked = 0;
                foreach (var a in GameAchievements)
                    if (a.IsUnlocked) unlocked++;
                int total = TotalAchievements > 0 ? TotalAchievements : GameAchievements.Count;
                return $"🏆 {unlocked} / {total}";
            }
        }
    }

    public class ModLink
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("url")]  public string Url  { get; set; } = "";
    }

    // ── Ryujinx mod management (Nintendo Switch) ──────────────────────────────

    /// <summary>
    /// A single mod entry from a Ryujinx <c>mods.json</c> file.
    /// Located at <c>{RyujinxPortableDir}\games\{titleId}\mods.json</c>.
    /// </summary>
    public class RyujinxMod
    {
        [JsonPropertyName("name")]    public string Name    { get; set; } = "";
        [JsonPropertyName("path")]    public string Path    { get; set; } = "";
        [JsonPropertyName("enabled")] public bool   Enabled { get; set; }
    }

    /// <summary>Root wrapper for <c>mods.json</c>: <c>{ "mods": [ … ] }</c>.</summary>
    public class RyujinxModConfig
    {
        [JsonPropertyName("mods")] public List<RyujinxMod> Mods { get; set; } = new();
    }

    public class SystemSpec
    {
        [JsonPropertyName("cpu")]    public string? Cpu    { get; set; }
        [JsonPropertyName("gpu")]    public string? Gpu    { get; set; }
        [JsonPropertyName("ram")]    public string? Ram    { get; set; }
        [JsonPropertyName("os")]     public string? Os     { get; set; }
        [JsonPropertyName("storage")]public string? Storage{ get; set; }
    }

    public class Achievement
    {
        [JsonPropertyName("platform")]      public string  Platform      { get; set; } = "";
        [JsonPropertyName("gameTitle")]     public string  GameTitle     { get; set; } = "";
        [JsonPropertyName("achievementId")] public string  AchievementId { get; set; } = "";
        [JsonPropertyName("name")]          public string  Name          { get; set; } = "";
        [JsonPropertyName("description")]   public string  Description   { get; set; } = "";
        [JsonPropertyName("unlockedAt")]    public string  UnlockedAt    { get; set; } = "";
        /// <summary>Achievement icon image URL from the real Games.Database.</summary>
        [JsonIgnore] public string? IconUrl      { get; set; }
        /// <summary>True when the achievement has been unlocked (UnlockedAt is non-empty).</summary>
        [JsonIgnore] public bool    IsUnlocked   => !string.IsNullOrEmpty(UnlockedAt);
        /// <summary>Opacity used in the UI: 1.0 for unlocked, 0.35 for locked (darkened).</summary>
        [JsonIgnore] public double  LockedOpacity => IsUnlocked ? 1.0 : 0.35;
    }

    public class FriendRequest
    {
        [JsonPropertyName("from")]   public string From   { get; set; } = "";
        [JsonPropertyName("sentAt")] public string SentAt { get; set; } = "";
    }

    public class Message
    {
        [JsonPropertyName("from")]   public string From   { get; set; } = "";
        [JsonPropertyName("text")]   public string Text   { get; set; } = "";
        [JsonPropertyName("sentAt")] public string SentAt { get; set; } = "";
    }

    public class GameInvite
    {
        [JsonPropertyName("inviteId")]    public string  InviteId    { get; set; } = "";
        [JsonPropertyName("from")]        public string  From        { get; set; } = "";
        [JsonPropertyName("gameName")]    public string  GameName    { get; set; } = "";
        [JsonPropertyName("sentAt")]      public string  SentAt      { get; set; } = "";
        [JsonPropertyName("status")]      public string  Status      { get; set; } = "";
        [JsonPropertyName("respondedAt")] public string? RespondedAt { get; set; }
    }

    public class ActivityEntry
    {
        /// <summary>Event type: "playtime" (default) or "achievement_unlocked".</summary>
        [JsonPropertyName("type")]            public string? Type            { get; set; }
        [JsonPropertyName("platform")]        public string  Platform        { get; set; } = "";
        [JsonPropertyName("gameTitle")]       public string  GameTitle       { get; set; } = "";
        [JsonPropertyName("titleId")]         public string? TitleId         { get; set; }
        [JsonPropertyName("sessionStart")]    public string  SessionStart    { get; set; } = "";
        [JsonPropertyName("sessionEnd")]      public string? SessionEnd      { get; set; }
        [JsonPropertyName("minutesPlayed")]   public int     MinutesPlayed   { get; set; }
        [JsonPropertyName("loggedAt")]        public string  LoggedAt        { get; set; } = "";
        /// <summary>For achievement_unlocked events: the achievement name.</summary>
        [JsonPropertyName("achievementName")] public string? AchievementName { get; set; }
        /// <summary>For achievement_unlocked events: optional icon URL or local path.</summary>
        [JsonPropertyName("achievementIcon")] public string? AchievementIcon { get; set; }
    }

    public class PresenceData
    {
        [JsonPropertyName("username")]    public string? Username    { get; set; }
        [JsonPropertyName("lastSeen")]    public string? LastSeen    { get; set; }
        /// <summary>Title of the game the user is currently playing, or null when idle.</summary>
        [JsonPropertyName("currentGame")] public string? CurrentGame { get; set; }
    }

    /// <summary>
    /// Lightweight cross-device sync signal written to the cloud whenever a play
    /// session ends.  Other open instances poll this tiny file every 30 seconds;
    /// when <see cref="LastActivityAt"/> advances they immediately re-fetch playtime
    /// and recently-played data without waiting for the 5-minute full sync tick.
    /// </summary>
    public class SyncSignal
    {
        /// <summary>ISO 8601 UTC timestamp of the most-recently completed play session.</summary>
        [JsonPropertyName("lastActivityAt")] public string? LastActivityAt { get; set; }
    }

    /// <summary>A friend displayed in the Friends screen, populated from presence API.</summary>
    public class FriendEntry
    {
        public string Username      { get; set; } = "";
        /// <summary>First character of Username, upper-cased, for the avatar circle.</summary>
        public string AvatarInitial =>
            Username.Length > 0 ? Username[0].ToString().ToUpper() : "?";
        /// <summary>Online / Away / Offline, derived from lastSeen timestamp.</summary>
        public string Status        { get; set; } = "Offline";
        public string LastSeen      { get; set; } = "Unknown";
        public bool   IsOnline      => Status == "Online";
        public bool   IsAway        => Status == "Away";
        /// <summary>Game the friend is currently playing (shown on their card when Online/Away).</summary>
        public string? CurrentGame      { get; set; }
        /// <summary>Title of the most recently played game (shown on offline friend cards).</summary>
        public string? RecentGameTitle  { get; set; }
        /// <summary>Platform of the most recently played game (e.g. "PC", "Switch").</summary>
        public string? RecentGamePlatform { get; set; }
        /// <summary>Friend's GamerScore total (fetched from their cloud profile).</summary>
        public int     GamerScore    { get; set; }
        /// <summary>Human-readable GamerScore label, e.g. "1,250 GS". Empty when GamerScore is 0.</summary>
        public string  GamerScoreLabel =>
            GamerScore > 0 ? $"{GamerScore:N0} GS" : "";
    }

    /// <summary>An item in the Friends Recent Activity feed (what friends have been playing).</summary>
    public class FriendActivityItem
    {
        public string Username     { get; set; } = "";
        /// <summary>First character of Username for the avatar circle.</summary>
        public string AvatarInitial =>
            Username.Length > 0 ? Username[0].ToString().ToUpper() : "?";
        /// <summary>Game title associated with this activity event.</summary>
        public string GameTitle    { get; set; } = "";
        /// <summary>Platform tag, e.g. "PC", "Switch", "Xbox 360".</summary>
        public string Platform     { get; set; } = "";
        /// <summary>Human-readable time ago, e.g. "2 hours ago".</summary>
        public string TimeAgo      { get; set; } = "";
        /// <summary>Short description shown in the activity row, e.g. "played for 45m".</summary>
        public string ActivityText { get; set; } = "";
        /// <summary>"🎮" for play sessions, "🏆" for achievement unlocks.</summary>
        public string Icon         { get; set; } = "🎮";
        /// <summary>
        /// Numeric key used to sort activity items chronologically.
        /// Lower values appear first.  Set to 0 for "just now" / currently playing,
        /// increasing for older activity.
        /// </summary>
        public int SortKey         { get; set; }
    }

    /// <summary>An incoming friend request shown in the Friends screen.</summary>
    public class FriendRequestDisplay
    {
        public string FromUsername  { get; set; } = "";
        public string AvatarInitial =>
            FromUsername.Length > 0 ? FromUsername[0].ToString().ToUpper() : "?";
        public string SentAgo       { get; set; } = "";
    }

    /// <summary>Session data saved locally so players can stay logged in.</summary>
    public class SavedSession
    {
        public string Username    { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string AvatarColor { get; set; } = "#1e90ff";
        public string SavedAt     { get; set; } = "";
    }

    /// <summary>A locally installed game detected by scanning drives.</summary>
    public class LocalGame
    {
        public string Title          { get; set; } = "";
        public string ExecutablePath { get; set; } = "";
        public string DriveRoot      { get; set; } = "";
        public string FolderPath     { get; set; } = "";
        public string ExecutableType { get; set; } = ""; // "exe", "app", "elf"
        /// <summary>Which storefront or scanner discovered this game (e.g. "Steam", "Epic", "GOG", "Xbox", "Local").</summary>
        public string Source         { get; set; } = "Local";
        /// <summary>Steam AppId when Source == "Steam", 0 otherwise.  Used to launch via steam://launch/{AppId}.</summary>
        public int SteamAppId        { get; set; }
        /// <summary>All drive locations where this game was found (populated when same title exists on multiple drives).</summary>
        public List<LocalGameDriveEntry> DriveInstances { get; set; } = new();
        [JsonIgnore] public bool HasMultipleDrives => DriveInstances.Count > 1;
        [JsonIgnore] public string DriveCountLabel => $"{DriveInstances.Count} drives";
    }

    /// <summary>A repack archive found in a Repacks directory, ready to install.</summary>
    public class LocalRepack
    {
        public string Title    { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string FileType { get; set; } = ""; // "zip", "rar", "folder"
        public long   SizeBytes{ get; set; }
        /// <summary>True when the repack folder contains an "Update" sub-directory (patch/update to apply after installation).</summary>
        public bool   HasUpdate  { get; set; }
        /// <summary>Path to the Update sub-directory inside the repack folder, if present.</summary>
        public string? UpdatePath { get; set; }
        /// <summary>True when a matching game is also found in the Games folder (i.e. this title is already installed).</summary>
        public bool   IsInstalledGame { get; set; }

        private string? _sizeLabel;
        public string SizeLabel =>
            _sizeLabel ??=
                SizeBytes >= 1_073_741_824 ? $"{SizeBytes / 1_073_741_824.0:F1} GB" :
                SizeBytes >= 1_048_576     ? $"{SizeBytes / 1_048_576.0:F0} MB"     :
                SizeBytes >= 1_024         ? $"{SizeBytes / 1_024.0:F0} KB"         :
                $"{SizeBytes} B";
    }

    /// <summary>A store entry shown in the Games Store screen.</summary>
    public class StoreGame
    {
        public string        Title           { get; set; } = "";
        public string        Platform        { get; set; } = "";
        public string        Genre           { get; set; } = "";
        public string        Price           { get; set; } = "";
        public double        Rating          { get; set; }
        public string        Description     { get; set; } = "";
        public bool          IsFeatured      { get; set; }
        public string        ReleaseYear     { get; set; } = "";
        public string        CoverColor      { get; set; } = "#1e1b4b";
        public string        CoverGradient   { get; set; } = "#1e1b4b,#312e81";
        public string?       CoverUrl        { get; set; }
        public List<string>? Screenshots     { get; set; }
        /// <summary>YouTube trailer URL from the real Games.Database.</summary>
        public string?       TrailerUrl      { get; set; }
        /// <summary>Link to the achievements JSON file in the Games.Database.</summary>
        public string?       AchievementsUrl { get; set; }
        /// <summary>Direct store page URL (e.g. Steam store page, Nintendo eShop).</summary>
        public string?       StorePageUrl    { get; set; }
        public string        RatingStars     =>
            new string('★', (int)System.Math.Round(Rating / 2.0))
            + new string('☆', 5 - (int)System.Math.Round(Rating / 2.0));
    }

    /// <summary>One drive location where a LocalGame was found.</summary>
    public class LocalGameDriveEntry
    {
        public string DriveRoot      { get; set; } = "";
        public string FolderPath     { get; set; } = "";
        public string ExecutablePath { get; set; } = "";
        public string ExecutableType { get; set; } = "";
    }

    /// <summary>A locally found ROM file for a non-PC platform.</summary>
    public class LocalRom
    {
        public string Title        { get; set; } = "";
        public string Platform     { get; set; } = "";
        public string FilePath     { get; set; } = "";
        public string FileType     { get; set; } = ""; // e.g. "iso", "gb", "snes"
        public long   SizeBytes    { get; set; }
        /// <summary>Platform-specific title ID (e.g. PS3: BLUS30305, Switch: 0100ADC022586000) extracted from the folder/file name when it matches a known TitleID pattern.</summary>
        public string? TitleId     { get; set; }
        /// <summary>Region/language tags extracted from the ROM filename, e.g. ["Europe", "USA"].</summary>
        public List<string> Regions         { get; set; } = new();
        /// <summary>Additional file paths when multiple ROM files share the same base title (e.g. multi-disk or multi-region).</summary>
        public List<string> AdditionalPaths { get; set; } = new();

        private string? _sizeLabel;
        public string SizeLabel =>
            _sizeLabel ??=
                SizeBytes >= 1_073_741_824 ? $"{SizeBytes / 1_073_741_824.0:F1} GB" :
                SizeBytes >= 1_048_576     ? $"{SizeBytes / 1_048_576.0:F0} MB"     :
                SizeBytes >= 1_024         ? $"{SizeBytes / 1_024.0:F0} KB"         :
                $"{SizeBytes} B";
    }

    /// <summary>
    /// A drive that has (or can have) a Games folder, shown in the
    /// install-drive picker when extracting a zip/rar repack.
    /// </summary>
    public class InstallDriveOption
    {
        /// <summary>Drive root path (e.g. "C:\", "/dev/sdb1").</summary>
        public string DriveRoot     { get; set; } = "";
        /// <summary>Full path to the Games folder on this drive.</summary>
        public string GamesFolderPath { get; set; } = "";
        /// <summary>Human-readable free-space label (e.g. "120 GB free").</summary>
        public string FreeSpaceLabel{ get; set; } = "";
        /// <summary>Whether the Games folder already exists on this drive.</summary>
        public bool   GamesExists   { get; set; }
    }

    // ── Application-wide settings ─────────────────────────────────────────────

    /// <summary>
    /// Application-wide preferences persisted by <see cref="GameLauncher.Services.AppSettingsService"/>.
    /// </summary>
    public class AppSettings
    {
        /// <summary>
        /// When <see langword="true"/> (the default), the launcher checks whether the
        /// Games.Database platform JSON files are stale on every startup and downloads
        /// fresh data as needed.  Set to <see langword="false"/> to skip the network
        /// check and always use the locally cached database.
        /// </summary>
        [JsonPropertyName("autoUpdate")]    public bool AutoUpdate    { get; set; } = true;

        /// <summary>
        /// When <see langword="true"/> (the default), the launcher plays the Game.OS
        /// intro animation on startup.  Set to <see langword="false"/> to skip it.
        /// </summary>
        [JsonPropertyName("showIntroVideo")] public bool ShowIntroVideo { get; set; } = true;

        /// <summary>
        /// Optional path to a custom intro video file.  When non-empty and
        /// <see cref="ShowIntroVideo"/> is <see langword="true"/>, this file is used
        /// instead of the built-in intro animation.
        /// </summary>
        [JsonPropertyName("introVideoPath")] public string IntroVideoPath { get; set; } = "";

        /// <summary>
        /// When <see langword="true"/>, Game.OS reads the Ryujinx log file after each
        /// Switch game session and displays any relevant output in the launcher.
        /// </summary>
        [JsonPropertyName("readSwitchLog")] public bool ReadSwitchLog { get; set; } = false;

        /// <summary>
        /// When <see langword="true"/>, all debug output, trace messages, and unhandled
        /// exceptions are written to <c>Dev.log</c> next to the launcher exe.
        /// Useful for diagnosing playback, network, and startup issues.
        /// </summary>
        [JsonPropertyName("devLogs")] public bool DevLogs { get; set; } = false;

        /// <summary>
        /// Apps to launch automatically after the user logs in (Settings › System › Startup).
        /// Includes built-in presets (Steam, Epic, Radmin VPN) and user-defined entries.
        /// </summary>
        [JsonPropertyName("startupApps")] public List<StartupAppEntry> StartupApps { get; set; } = new();

        // ── Notifications ────────────────────────────────────────────────────

        /// <summary>Show a toast notification when a friend comes online.</summary>
        [JsonPropertyName("notifyFriendOnline")] public bool NotifyFriendOnline { get; set; } = false;

        /// <summary>Show a toast notification when a friend starts playing a game.</summary>
        [JsonPropertyName("notifyFriendGameStart")] public bool NotifyFriendGameStart { get; set; } = false;

        /// <summary>Broadcast a "now playing" presence update to friends when a game is launched.</summary>
        [JsonPropertyName("broadcastGameStart")] public bool BroadcastGameStart { get; set; } = false;

        /// <summary>Broadcast an online presence update to friends when the user first logs in.</summary>
        [JsonPropertyName("broadcastUserOnline")] public bool BroadcastUserOnline { get; set; } = false;

        // ── Game Launch ──────────────────────────────────────────────────────

        /// <summary>
        /// When <see langword="true"/>, Game.OS minimizes to the background when a game
        /// launches and restores to full-screen when the game exits.
        /// </summary>
        [JsonPropertyName("minimizeOnGameLaunch")] public bool MinimizeOnGameLaunch { get; set; } = false;

        /// <summary>
        /// When <see langword="true"/>, Game.OS also watches for child processes spawned
        /// inside the game's folder (e.g. Plutonium, iw4x, Project BO4 mod clients) and
        /// continues tracking playtime until all related processes have exited.
        /// </summary>
        [JsonPropertyName("trackFolderProcesses")] public bool TrackFolderProcesses { get; set; } = true;

        // ── Third-party integrations (stored locally, never synced to cloud) ─

        /// <summary>
        /// Steam Web API key — stored locally only, never uploaded to the cloud.
        /// Used to fetch the user's Steam game library via the Steam Web API.
        /// Obtain yours at https://steamcommunity.com/dev/apikey
        /// </summary>
        [JsonPropertyName("steamApiKey")] public string SteamApiKey { get; set; } = "";

        /// <summary>
        /// Steam 64-bit User ID (SteamID64).  Used with the Steam Web API to fetch
        /// the user's owned games library.  Shown on the user's Steam profile page.
        /// Stored locally only, never uploaded to the cloud.
        /// </summary>
        [JsonPropertyName("steamUserId")] public string SteamUserId { get; set; } = "";

        /// <summary>
        /// Exophase profile identifier including the leading '#', e.g. "#2896888".
        /// Stored locally only and used to resolve per-user Exophase achievement pages.
        /// </summary>
        [JsonPropertyName("exophaseProfileId")] public string ExophaseProfileId { get; set; } = "";

        /// <summary>
        /// Exophase username for achievement scraping.
        /// Stored locally only, never synced.
        /// </summary>
        [JsonPropertyName("exophaseUsername")] public string ExophaseUsername { get; set; } = "";

        /// <summary>
        /// Exophase password — stored locally only, never uploaded to the cloud.
        /// Used alongside <see cref="ExophaseUsername"/> to authenticate scrape requests.
        /// </summary>
        [JsonPropertyName("exophasePassword")] public string ExophasePassword { get; set; } = "";

        // ── Developer / Feature flags ────────────────────────────────────────

        /// <summary>
        /// When <see langword="true"/>, automatically syncs Steam playtime into Game.OS
        /// after each Steam library import (takes the maximum of local and Steam values).
        /// </summary>
        [JsonPropertyName("enableSteamSync")] public bool EnableSteamSync { get; set; } = true;

        /// <summary>
        /// When <see langword="true"/>, automatically records and syncs achievement
        /// unlocks detected from Xenia / Ryujinx log files.
        /// </summary>
        [JsonPropertyName("enableAchievementAutoSync")] public bool EnableAchievementAutoSync { get; set; } = true;

        /// <summary>
        /// When <see langword="true"/>, shows debug toast notifications for Ryujinx
        /// log detection and read status while Switch achievement polling is running.
        /// </summary>
        [JsonPropertyName("notifyRyujinxLogStatus")] public bool NotifyRyujinxLogStatus { get; set; } = false;

        /// <summary>
        /// Enables a Windows global Left Ctrl + Left Shift poller so the quick menu
        /// can be opened while another game window is focused.
        /// </summary>
        [JsonPropertyName("enableGlobalQuickMenuHotkey")] public bool EnableGlobalQuickMenuHotkey { get; set; } = false;

        /// <summary>
        /// When enabled, the launcher temporarily raises its quick-menu window as a
        /// top-most full-screen overlay and then minimizes back after dismissal.
        /// </summary>
        [JsonPropertyName("compatibilityOverlayMode")] public bool CompatibilityOverlayMode { get; set; } = false;

        /// <summary>
        /// When enabled, locally cached metadata is preferred for installed games so
        /// detail pages stay responsive and available offline.
        /// </summary>
        [JsonPropertyName("preferOfflineCachedMetadata")] public bool PreferOfflineCachedMetadata { get; set; } = true;

        // ── Placeholder future feature ───────────────────────────────────────

        /// <summary>
        /// Placeholder for the upcoming Local Transfer feature (peer-to-peer game
        /// file sharing between devices on the same network).  Not yet implemented.
        /// </summary>
        [JsonPropertyName("localTransferEnabled")] public bool LocalTransferEnabled { get; set; } = false;

        // ── Scanner diagnostic logging ────────────────────────────────────────
        // Each flag gates a specific scanner's verbose output to Dev.log.
        // The "Advanced" companions additionally print every individual item found.

        /// <summary>Log Games-folder scan results (per-drive summary) to Dev.log.</summary>
        [JsonPropertyName("logGamesScanner")] public bool LogGamesScanner { get; set; } = false;

        /// <summary>Log every individual game found per drive when LogGamesScanner is on.</summary>
        [JsonPropertyName("logGamesScannerAdvanced")] public bool LogGamesScannerAdvanced { get; set; } = false;

        /// <summary>Log ROMs-folder scan results (per-drive, per-platform summary) to Dev.log.</summary>
        [JsonPropertyName("logRomsScanner")] public bool LogRomsScanner { get; set; } = false;

        /// <summary>Log every individual ROM found per drive when LogRomsScanner is on.</summary>
        [JsonPropertyName("logRomsScannerAdvanced")] public bool LogRomsScannerAdvanced { get; set; } = false;

        /// <summary>Log Repacks-folder scan results (per-drive summary) to Dev.log.</summary>
        [JsonPropertyName("logRepacksScanner")] public bool LogRepacksScanner { get; set; } = false;

        /// <summary>Log every individual repack found per drive when LogRepacksScanner is on.</summary>
        [JsonPropertyName("logRepacksScannerAdvanced")] public bool LogRepacksScannerAdvanced { get; set; } = false;

        /// <summary>Log local Steam (ACF + folder) scan results to Dev.log.</summary>
        [JsonPropertyName("logLocalSteamScanner")] public bool LogLocalSteamScanner { get; set; } = false;

        /// <summary>Log Steam Web API import results to Dev.log.</summary>
        [JsonPropertyName("logSteamApiScanner")] public bool LogSteamApiScanner { get; set; } = false;
    }

    // ── Game launch settings (saved locally per game title) ───────────────────

    /// <summary>
    /// Per-game launch settings persisted locally by <see cref="GameLauncher.Services.GameSettingsService"/>.
    /// Stores the preferred executable, launch arguments, and pre/post-launch entries.
    /// </summary>
    public class GameSettings
    {
        [JsonPropertyName("gameTitle")]   public string  GameTitle   { get; set; } = "";
        /// <summary>Full path to the preferred .exe or .bat used to launch this game.</summary>
        [JsonPropertyName("exePath")]     public string? ExePath     { get; set; }
        /// <summary>Command-line arguments passed to the executable on launch.</summary>
        [JsonPropertyName("exeArgs")]     public string? ExeArgs     { get; set; }
        /// <summary>Full path to the ROM file used when launching via an emulator.</summary>
        [JsonPropertyName("romPath")]     public string? RomPath     { get; set; }
        /// <summary>
        /// Name (EmulatorName label) of the preferred emulator to use for this game.
        /// When set, this overrides the default first-enabled emulator for the platform.
        /// </summary>
        [JsonPropertyName("preferredEmulatorName")] public string? PreferredEmulatorName { get; set; }
        /// <summary>Apps/scripts to run <b>before</b> the game launches.</summary>
        [JsonPropertyName("preLaunch")]   public List<LaunchEntry> PreLaunch    { get; set; } = new();
        /// <summary>Apps/scripts to run <b>during</b> the game (e.g. overlay or server).</summary>
        [JsonPropertyName("duringLaunch")]public List<LaunchEntry> DuringLaunch { get; set; } = new();
        /// <summary>Apps/scripts to run <b>after</b> the game exits.</summary>
        [JsonPropertyName("postLaunch")]  public List<LaunchEntry> PostLaunch   { get; set; } = new();
    }

    /// <summary>
    /// One pre- or post-launch entry: an executable/script path with optional arguments
    /// and a human-readable label.
    /// </summary>
    public class LaunchEntry
    {
        [JsonPropertyName("label")]        public string  Label        { get; set; } = "";
        [JsonPropertyName("path")]         public string  Path         { get; set; } = "";
        [JsonPropertyName("arguments")]    public string? Arguments    { get; set; }
        /// <summary>
        /// When <see langword="true"/>, the launcher waits for this process to finish
        /// initialising (main window visible / input idle) before launching the game.
        /// Useful for Steam, Epic Games Launcher, etc. that need to be fully running first.
        /// </summary>
        [JsonPropertyName("waitForReady")] public bool    WaitForReady { get; set; } = false;
        /// <summary>
        /// When <see langword="true"/>, this entry represents a Steam launch action
        /// (steam://launch/{AppId}) rather than a local executable path.
        /// It is rendered with a Steam icon and no path-browse button.
        /// </summary>
        [JsonPropertyName("isSteamLaunch")] public bool   IsSteamLaunch { get; set; } = false;
    }

    /// <summary>
    /// One startup-app entry in Settings &gt; System.
    /// These apps are launched automatically after the user logs in.
    /// </summary>
    public class StartupAppEntry
    {
        [JsonPropertyName("label")]        public string  Label        { get; set; } = "";
        [JsonPropertyName("path")]         public string  Path         { get; set; } = "";
        [JsonPropertyName("arguments")]    public string? Arguments    { get; set; }
        /// <summary>When true this entry is a built-in preset (Steam, Epic, Radmin).</summary>
        [JsonPropertyName("isPreset")]     public bool    IsPreset     { get; set; } = false;
        /// <summary>When true this preset (or custom app) is enabled and will be launched.</summary>
        [JsonPropertyName("enabled")]      public bool    Enabled      { get; set; } = false;
    }

    // ── App Store entry (from Koriebonx98/AppStore- repository) ──────────────

    /// <summary>An application entry from the public Koriebonx98/AppStore- repository.</summary>
    public class AppStoreEntry
    {
        [JsonPropertyName("Name")]               public string  Name              { get; set; } = "";
        [JsonPropertyName("GameName")]           public string  GameName          { get; set; } = "";
        [JsonPropertyName("Url")]                public string  Url               { get; set; } = "";
        [JsonPropertyName("Image")]              public string? Image             { get; set; }
        [JsonPropertyName("Genre")]              public string  Genre             { get; set; } = "";
        [JsonPropertyName("Type")]               public string  Type              { get; set; } = "";
        [JsonPropertyName("Platform")]           public string  Platform          { get; set; } = "";
        [JsonPropertyName("Desc")]               public string? Description       { get; set; }
        [JsonPropertyName("Emulator Platforms")] public string? EmulatorPlatforms { get; set; }

        /// <summary>
        /// Converts the GitHub HTML image URL (blob/main/…) to a raw.githubusercontent.com URL
        /// so Avalonia's Image control can download it directly.
        /// Already-raw URLs and non-GitHub URLs are returned unchanged.
        /// </summary>
        public string? RawImageUrl
        {
            get
            {
                if (string.IsNullOrEmpty(Image)) return null;

                // Already a raw URL — return as-is
                if (Image.StartsWith("https://raw.githubusercontent.com/", StringComparison.Ordinal))
                    return Image;

                // Convert html blob URL:
                // https://github.com/Owner/Repo/blob/main/path → https://raw.githubusercontent.com/Owner/Repo/main/path
                if (Image.StartsWith("https://github.com/", StringComparison.Ordinal) &&
                    Image.Contains("/blob/", StringComparison.Ordinal))
                {
                    return Image
                        .Replace("https://github.com/", "https://raw.githubusercontent.com/", StringComparison.Ordinal)
                        .Replace("/blob/", "/", StringComparison.Ordinal);
                }

                return Image;
            }
        }
    }

    /// <summary>A game entry from the public Koriebonx98/Games.Database repository.</summary>
    public class DatabaseGame
    {
        [JsonPropertyName("Title")]    public string? Title          { get; set; }
        [JsonPropertyName("TitleID")]  public string? TitleId        { get; set; }
        [JsonPropertyName("CoverUrl")] public string? CoverUrl       { get; set; }
        [JsonPropertyName("appid")]    public long?   AppId          { get; set; }
        /// <summary>Game description (extracted from Description or description field).</summary>
        public string?       Description     { get; set; }
        /// <summary>First trailer URL (from trailers array).</summary>
        public string?       TrailerUrl      { get; set; }
        /// <summary>Link to the achievements JSON file in Games.Database.</summary>
        public string?       AchievementsUrl { get; set; }
        /// <summary>Optional Exophase achievements/trophies page URL for the game.</summary>
        public string?       ExophaseUrl    { get; set; }
        /// <summary>Background/screenshot image URLs (from background_images array).</summary>
        public List<string>? Screenshots     { get; set; }
        /// <summary>Direct store page URL (e.g. Steam store page, Nintendo eShop, PlayStation Store).</summary>
        public string?       StorePageUrl    { get; set; }
        /// <summary>Game genre (e.g. "Action", "RPG"). Present in Xbox 360 and enriched databases.</summary>
        public string?       Genre           { get; set; }
        /// <summary>Release year string (e.g. "2020"), extracted from ReleaseDate or releaseDate field.</summary>
        public string?       ReleaseYear     { get; set; }
        /// <summary>Alternate / known titles for fuzzy matching (e.g. ["GoW", "God of War 2018"]).</summary>
        public List<string>? AlternateNames  { get; set; }
    }

    /// <summary>
    /// Shared helpers for platform name handling, used by both
    /// <c>GameScannerService</c> and <c>GitHubDataService</c>.
    /// </summary>
    public static class PlatformHelper
    {
        /// <summary>
        /// Maps verbose RetroArch/Libretro-style platform folder names to the canonical
        /// Games.Database platform identifiers used in URL paths and the C# model.
        /// <para>
        /// Examples: "Microsoft - Xbox 360" → "Xbox 360", "Nintendo - Switch" → "Switch",
        /// "Sony - PlayStation 3" → "PS3".
        /// </para>
        /// Canonical names (e.g. "Xbox 360", "Switch") pass through unchanged.
        /// </summary>
        /// <summary>
        /// Removes trademark (™), registered trademark (®), and copyright (©) Unicode symbols
        /// from a game title for fuzzy matching purposes.
        /// For example: "Mario Kart™ 8 Deluxe" → "Mario Kart 8 Deluxe".
        /// </summary>
        public static string StripSpecialSymbols(string title)
        {
            if (string.IsNullOrEmpty(title)) return title;
            return _specialSymbolRegex.Replace(title, "").Trim();
        }

        // Matches ™ (U+2122), ® (U+00AE), © (U+00A9)
        private static readonly System.Text.RegularExpressions.Regex _specialSymbolRegex =
            new(@"[™®©]", System.Text.RegularExpressions.RegexOptions.Compiled);

        public static string NormalizePlatform(string platform)
        {
            if (string.IsNullOrEmpty(platform)) return platform;
            // Case-insensitive comparison so user folder names like
            // "Sony - Playstation 4" (lowercase 's') map correctly to "PS4".
            // Also handles abbreviated/truncated values (e.g. "S" → "Switch",
            // "2" → "PS2") that can appear when platform tags are cut off.
            return platform.Trim().ToLowerInvariant() switch
            {
                // ── Verbose RetroArch/Libretro-style names ─────────────────
                "microsoft - xbox 360"    => "Xbox 360",
                "microsoft - xbox one"    => "Xbox One",
                "nintendo - switch"       => "Switch",
                "sony - playstation"      => "PS1",
                "sony - playstation 2"    => "PS2",
                "sony - playstation 3"    => "PS3",
                "sony - playstation 4"    => "PS4",
                "sony - playstation 5"    => "PS5",
                "sony - psp"              => "PSP",
                "sony - ps vita"          => "PS Vita",
                "sony - playstation vita" => "PS Vita",
                // ── Common shortened aliases ───────────────────────────────
                "pc"                      => "PC",
                "ps1"                     => "PS1",
                "ps2"                     => "PS2",
                "ps3"                     => "PS3",
                "ps4"                     => "PS4",
                "ps5"                     => "PS5",
                "psp"                     => "PSP",
                "ps vita"                 => "PS Vita",
                "vita"                    => "PS Vita",
                "switch"                  => "Switch",
                "xbox 360"                => "Xbox 360",
                "xbox360"                 => "Xbox 360",
                "xbox one"                => "Xbox One",
                "xbone"                   => "Xbox One",
                // ── Single-character / truncated tags ─────────────────────
                // "S" → Switch, "2" → PS2, "3" → PS3, "4" → PS4, "5" → PS5
                "s"                       => "Switch",
                "2"                       => "PS2",
                "3"                       => "PS3",
                "4"                       => "PS4",
                "5"                       => "PS5",
                _                         => platform,
            };
        }
    }

    // ── Emulator Settings ────────────────────────────────────────────────────

    /// <summary>
    /// Per-platform emulator configuration persisted locally.
    /// Used by the Settings page so users can configure which emulator to
    /// use for each non-PC platform (PS1, PS2, PS3, PS4, Switch, etc.).
    /// </summary>
    public class EmulatorSettings
    {
        [JsonPropertyName("platform")]       public string  Platform      { get; set; } = "";
        /// <summary>Full path to the emulator executable.</summary>
        [JsonPropertyName("emulatorPath")]   public string  EmulatorPath  { get; set; } = "";
        /// <summary>Command-line arguments template; use {rom} as placeholder for the ROM path.</summary>
        [JsonPropertyName("arguments")]      public string  Arguments     { get; set; } = "{rom}";
        /// <summary>Optional name label for display (e.g. "PCSX2 2.x", "Ryujinx").</summary>
        [JsonPropertyName("emulatorName")]   public string  EmulatorName  { get; set; } = "";
        /// <summary>Whether this emulator is enabled and should be used to launch ROMs.</summary>
        [JsonPropertyName("enabled")]        public bool    Enabled       { get; set; } = true;
    }

    // ── Playtime session ─────────────────────────────────────────────────────

    /// <summary>
    /// A single recorded play session stored locally.
    /// Accumulated into <see cref="Game.PlaytimeMinutes"/> and used to set
    /// <see cref="Game.LastPlayedAt"/>.
    /// </summary>
    public class PlaySession
    {
        [JsonPropertyName("platform")]       public string Platform      { get; set; } = "";
        [JsonPropertyName("title")]          public string Title         { get; set; } = "";
        [JsonPropertyName("startedAt")]      public string StartedAt     { get; set; } = "";
        [JsonPropertyName("endedAt")]        public string EndedAt       { get; set; } = "";
        [JsonPropertyName("minutes")]        public int    Minutes       { get; set; }
        /// <summary>
        /// When <c>true</c>, this is a periodic checkpoint entry that may be superseded
        /// by the final session record once the game closes cleanly.  Checkpoints are
        /// excluded from playtime totals and not shown in history to avoid double-counting.
        /// </summary>
        [JsonPropertyName("isCheckpoint")]   public bool   IsCheckpoint  { get; set; }
    }

    // ── Gamer Score ───────────────────────────────────────────────────────────

    /// <summary>
    /// Computed Gamer Score for a user — a unique system that mixes playtime,
    /// achievement unlocks, and challenges into a single prestige value.
    ///
    /// Formula (subject to tuning):
    ///   PlaytimePoints  = totalMinutesPlayed / 10   (1 point per 10 minutes played)
    ///   AchievementPoints = unlockedAchievements * 15
    ///   Total = PlaytimePoints + AchievementPoints
    ///
    /// Challenges are not yet implemented; their weight is reserved for a future update.
    /// </summary>
    public class GamerScore
    {
        /// <summary>Total Gamer Score points.</summary>
        public int Total            { get; set; }
        /// <summary>Points earned from playtime (1 per 10 minutes played).</summary>
        public int PlaytimePoints   { get; set; }
        /// <summary>Points earned from unlocked achievements (15 per achievement).</summary>
        public int AchievementPoints{ get; set; }
        /// <summary>Human-readable label, e.g. "1 250 GS".</summary>
        public string Label         => $"{Total:N0} GS";

        /// <summary>
        /// Computes the Gamer Score from <paramref name="totalPlaytimeMinutes"/> and
        /// <paramref name="unlockedAchievementCount"/>.
        /// </summary>
        public static GamerScore Compute(int totalPlaytimeMinutes, int unlockedAchievementCount)
        {
            int pt  = totalPlaytimeMinutes / 10;
            int ach = unlockedAchievementCount * 15;
            return new GamerScore
            {
                PlaytimePoints    = pt,
                AchievementPoints = ach,
                Total             = pt + ach,
            };
        }

        /// <summary>Formats an integer GamerScore total into a human-readable label, e.g. "1,250 GS".</summary>
        public static string FormatLabel(int total) => $"{total:N0} GS";
    }

    // ── Wallet ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The user's in-app Wallet, containing a Coins balance that can be spent on
    /// cosmetic items (intro videos, banners, profile pictures, etc.).
    ///
    /// Coins are earned automatically as Gamer Score increases — every 100 GS awards
    /// 10 Coins.  The actual purchase flow for cosmetics is not yet implemented;
    /// this model exists to make the system ready for that future feature.
    /// </summary>
    public class Wallet
    {
        /// <summary>Current coin balance.</summary>
        [JsonPropertyName("coins")] public int Coins { get; set; }

        /// <summary>Human-readable label, e.g. "💰 250 Coins".</summary>
        public string Label => $"💰 {Coins:N0} Coins";

        /// <summary>
        /// Derives the expected coin balance from <paramref name="gamerScore"/>.
        /// Awards 10 Coins per 100 GS.  Actual balance may differ if coins have
        /// been spent; the returned value is the maximum earnable coins.
        /// </summary>
        public static int CoinsFromScore(int gamerScore) => (gamerScore / 100) * 10;
    }

    // ── Per-game review ────────────────────────────────────────────────────────

    /// <summary>
    /// A per-game user review, stored locally in
    /// <c>Data/GameCache/{platform}/{title}/reviews.json</c> and optionally uploaded
    /// to the Games.Database repository via <see cref="GameLauncher.Services.GitHubDataService"/>.
    /// </summary>
    public class GameReview
    {
        [JsonPropertyName("username")]  public string  Username  { get; set; } = "";
        /// <summary>Star rating from 1 (terrible) to 5 (excellent).</summary>
        [JsonPropertyName("rating")]    public int     Rating    { get; set; }
        /// <summary>Free-text review note (optional, may be empty).</summary>
        [JsonPropertyName("note")]      public string  Note      { get; set; } = "";
        [JsonPropertyName("createdAt")] public string  CreatedAt { get; set; } =
            DateTime.UtcNow.ToString("o");

        /// <summary>Returns the <see cref="Rating"/> rendered as Unicode stars, e.g. "★★★☆☆".</summary>
        [JsonIgnore] public string StarLabel =>
            new string('★', Rating) + new string('☆', 5 - Rating);
    }

    // ── Game compatibility report ──────────────────────────────────────────────

    /// <summary>
    /// A per-game compatibility report submitted by a user.  Records the hardware
    /// configuration and observed performance so others can estimate what to expect.
    /// Stored locally in <c>Data/GameCache/{platform}/{title}/compatibility.json</c>
    /// and uploaded to the Games.Database via
    /// <see cref="GameLauncher.Services.GitHubDataService"/>.
    /// </summary>
    public class GameCompatibility
    {
        [JsonPropertyName("cpu")]              public string  Cpu              { get; set; } = "";
        [JsonPropertyName("gpu")]              public string  Gpu              { get; set; } = "";
        [JsonPropertyName("ramGb")]            public int     RamGb            { get; set; }
        /// <summary>Storage type: "HDD", "SSD", "NVMe", or "".</summary>
        [JsonPropertyName("storageType")]      public string  StorageType      { get; set; } = "";
        [JsonPropertyName("resolutionWidth")]  public int     ResolutionWidth  { get; set; }
        [JsonPropertyName("resolutionHeight")] public int     ResolutionHeight { get; set; }
        /// <summary>Observed frames-per-second (0 = not measured).</summary>
        [JsonPropertyName("fps")]              public int     Fps              { get; set; }
        [JsonPropertyName("platform")]         public string  Platform         { get; set; } = "";
        /// <summary>Emulator name for non-PC platforms (e.g. "Ryujinx", "Xenia").</summary>
        [JsonPropertyName("emulator")]         public string? Emulator         { get; set; }
        [JsonPropertyName("userId")]           public string  UserId           { get; set; } = "";
        [JsonPropertyName("createdAt")]        public string  CreatedAt        { get; set; } =
            DateTime.UtcNow.ToString("o");

        /// <summary>Human-readable summary, e.g. "1920×1080 · 60 fps · SSD".</summary>
        [JsonIgnore] public string Summary =>
            $"{ResolutionWidth}×{ResolutionHeight}" +
            (Fps > 0 ? $" · {Fps} fps" : "") +
            (!string.IsNullOrEmpty(StorageType) ? $" · {StorageType}" : "") +
            (!string.IsNullOrEmpty(Emulator) ? $" · {Emulator}" : "");
    }
}
