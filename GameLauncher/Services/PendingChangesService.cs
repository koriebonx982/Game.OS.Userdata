using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameLauncher.Models;

namespace GameLauncher.Services
{
    /// <summary>
    /// Stores library mutations made while the app is offline (or the server is
    /// unreachable) and replays them when the user reconnects.
    ///
    /// Supported operations:
    ///   <see cref="PendingChangeKind.AddGame"/>    — add a game to the cloud library.
    ///   <see cref="PendingChangeKind.RemoveGame"/> — remove a game from the cloud library.
    ///
    /// Queue location (per-user):
    ///   Windows : %APPDATA%\GameOS\{username}\pending-changes.json
    ///   Linux   : ~/.config/GameOS/{username}/pending-changes.json
    ///   macOS   : ~/Library/Application Support/GameOS/{username}/pending-changes.json
    ///
    /// Conflict resolution: last-write-wins (ordered by <see cref="PendingChange.Timestamp"/>).
    /// </summary>
    public class PendingChangesService
    {
        private static readonly JsonSerializerOptions _json = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private static readonly string BaseDir =
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GameOS");

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Enqueues an <see cref="PendingChangeKind.AddGame"/> operation.
        /// Safe to call when offline — the change is persisted immediately.
        /// </summary>
        public void EnqueueAddGame(string username, Game game)
        {
            if (string.IsNullOrWhiteSpace(username) || game == null!) return;
            Enqueue(username, new PendingChange
            {
                Kind      = PendingChangeKind.AddGame,
                GameData  = game,
                Timestamp = DateTime.UtcNow.ToString("o"),
            });
            System.Diagnostics.Debug.WriteLine(
                $"[PendingChanges] Queued AddGame '{game.Title}' for '{username}'.");
        }

        /// <summary>
        /// Enqueues a <see cref="PendingChangeKind.RemoveGame"/> operation.
        /// Safe to call when offline — the change is persisted immediately.
        /// </summary>
        public void EnqueueRemoveGame(string username, string platform, string title)
        {
            if (string.IsNullOrWhiteSpace(username)) return;
            Enqueue(username, new PendingChange
            {
                Kind     = PendingChangeKind.RemoveGame,
                Platform = platform,
                Title    = title,
                Timestamp = DateTime.UtcNow.ToString("o"),
            });
            System.Diagnostics.Debug.WriteLine(
                $"[PendingChanges] Queued RemoveGame '{title}' ({platform}) for '{username}'.");
        }

        /// <summary>
        /// Enqueues an achievement unlock to sync when connectivity returns.
        /// </summary>
        public void EnqueueAchievementUnlock(
            string username,
            string platform,
            string gameTitle,
            string? titleId,
            string achievementId,
            string achievementName,
            string? description,
            string? iconUrl,
            string unlockedAt)
        {
            if (string.IsNullOrWhiteSpace(username) ||
                string.IsNullOrWhiteSpace(platform) ||
                string.IsNullOrWhiteSpace(gameTitle) ||
                string.IsNullOrWhiteSpace(achievementId) ||
                string.IsNullOrWhiteSpace(achievementName))
                return;

            Enqueue(username, new PendingChange
            {
                Kind = PendingChangeKind.SaveAchievement,
                Platform = platform,
                Title = gameTitle,
                TitleId = titleId,
                AchievementId = achievementId,
                AchievementName = achievementName,
                AchievementDescription = description,
                AchievementIconUrl = iconUrl,
                UnlockedAt = unlockedAt,
                Timestamp = DateTime.UtcNow.ToString("o"),
            });

            System.Diagnostics.Debug.WriteLine(
                $"[PendingChanges] Queued SaveAchievement '{achievementName}' in '{gameTitle}' for '{username}'.");
        }

        /// <summary>
        /// Returns all pending changes for <paramref name="username"/>, ordered oldest-first.
        /// Returns an empty list when there is no queue file.
        /// </summary>
        public List<PendingChange> GetAll(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return new List<PendingChange>();
            return Load(username).OrderBy(c => c.Timestamp).ToList();
        }

        /// <summary>
        /// Returns <c>true</c> when there is at least one pending change for
        /// <paramref name="username"/>.
        /// </summary>
        public bool HasPending(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return false;
            return Load(username).Count > 0;
        }

        /// <summary>Removes all pending changes for <paramref name="username"/>.</summary>
        public void Clear(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return;
            try
            {
                string path = QueueFileFor(username);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[PendingChanges] Could not clear queue for '{username}': {ex.Message}");
            }
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private static string QueueFileFor(string username)
        {
            string safe = StorageHelpers.SanitiseName(username);
            return Path.Combine(BaseDir, safe, "pending-changes.json");
        }

        private void Enqueue(string username, PendingChange change)
        {
            var list = Load(username);
            list.Add(change);
            Save(username, list);
        }

        private List<PendingChange> Load(string username)
        {
            try
            {
                string path = QueueFileFor(username);
                if (!File.Exists(path)) return new List<PendingChange>();
                return JsonSerializer.Deserialize<List<PendingChange>>(
                    File.ReadAllText(path), _json) ?? new();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return new List<PendingChange>();
            }
        }

        private void Save(string username, List<PendingChange> changes)
        {
            try
            {
                string path = QueueFileFor(username);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(changes, _json));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[PendingChanges] Could not write queue for '{username}': {ex.Message}");
            }
        }
    }

    /// <summary>The kind of mutation stored in the pending-changes queue.</summary>
    public enum PendingChangeKind
    {
        AddGame,
        RemoveGame,
        SaveAchievement,
    }

    /// <summary>One queued mutation, serialized to <c>pending-changes.json</c>.</summary>
    public class PendingChange
    {
        [JsonPropertyName("kind")]      public PendingChangeKind Kind      { get; set; }
        [JsonPropertyName("timestamp")] public string            Timestamp { get; set; } = "";
        /// <summary>Full game object for <see cref="PendingChangeKind.AddGame"/>.</summary>
        [JsonPropertyName("gameData")]  public Game?             GameData  { get; set; }
        /// <summary>Platform for <see cref="PendingChangeKind.RemoveGame"/>.</summary>
        [JsonPropertyName("platform")]  public string?           Platform  { get; set; }
        /// <summary>Title for <see cref="PendingChangeKind.RemoveGame"/>.</summary>
        [JsonPropertyName("title")]     public string?           Title     { get; set; }
        [JsonPropertyName("titleId")]   public string?           TitleId   { get; set; }
        [JsonPropertyName("achievementId")] public string? AchievementId { get; set; }
        [JsonPropertyName("achievementName")] public string? AchievementName { get; set; }
        [JsonPropertyName("achievementDescription")] public string? AchievementDescription { get; set; }
        [JsonPropertyName("achievementIconUrl")] public string? AchievementIconUrl { get; set; }
        [JsonPropertyName("unlockedAt")] public string? UnlockedAt { get; set; }
    }
}
