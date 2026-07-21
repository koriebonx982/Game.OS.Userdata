using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GameLauncher.Models;

namespace GameLauncher.Services
{
    /// <summary>
    /// Handles syncing achievement unlocks detected from Steam emulators
    /// to the Game.OS account database via the GameOsClient.
    /// 
    /// This service bridges the gap between local Steam emulator achievement
    /// detection and cloud-synced achievement persistence.
    /// </summary>
    public class SteamEmuAchievementSyncService
    {
        private readonly GameOsClient _client;
        private readonly string _platform;
        private readonly string _gameTitle;
        private readonly long? _steamAppId;
        private readonly HashSet<string> _syncedUnlocks = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Creates a new sync service for a specific game.
        /// </summary>
        /// <param name="client">The Game.OS client for persisting achievements.</param>
        /// <param name="platform">Platform name (e.g., "PC").</param>
        /// <param name="gameTitle">Human-readable game title.</param>
        /// <param name="steamAppId">Optional Steam AppID for metadata matching.</param>
        public SteamEmuAchievementSyncService(
            GameOsClient client,
            string platform,
            string gameTitle,
            long? steamAppId = null)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _platform = platform ?? throw new ArgumentNullException(nameof(platform));
            _gameTitle = gameTitle ?? throw new ArgumentNullException(nameof(gameTitle));
            _steamAppId = steamAppId;
        }

        /// <summary>
        /// Syncs a newly-detected achievement unlock to the Game.OS account.
        /// Prevents duplicate syncs for the same achievement during a session.
        /// </summary>
        /// <param name="achievementId">Raw emulator achievement ID (e.g., "ACH_WIN_GAME").</param>
        /// <param name="displayName">Human-readable achievement name.</param>
        /// <param name="description">Optional achievement description.</param>
        /// <param name="iconUrl">Optional achievement icon URL.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task SyncAchievementAsync(
            string achievementId,
            string displayName,
            string? description = null,
            string? iconUrl = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(achievementId))
                return;

            // Deduplicate: skip if already synced in this session
            if (!_syncedUnlocks.Add(achievementId))
                return;

            try
            {
                var achievement = new Achievement
                {
                    Platform = _platform,
                    GameTitle = _gameTitle,
                    AchievementId = achievementId,
                    Name = displayName ?? achievementId,
                    Description = description ?? "",
                    IconUrl = iconUrl ?? "",
                    IsUnlocked = true,
                    UnlockedAt = DateTime.UtcNow.ToString("O"),
                };

                await _client.SaveAchievementAsync(
                    achievement.Platform,
                    achievement.GameTitle,
                    titleId: null, // Per-game mirrors are handled by the client
                    achievement.AchievementId,
                    achievement.Name,
                    achievement.Description,
                    achievement.UnlockedAt,
                    ct)
                    .ConfigureAwait(false);

                DevLogService.Log($"[SteamEmuSync] ✓ Synced: {displayName} ({achievementId}) to Game.OS account");
            }
            catch (Exception ex)
            {
                DevLogService.Log($"[SteamEmuSync] ✗ Failed to sync {achievementId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Syncs multiple newly-detected achievement unlocks in batch.
        /// Useful when resuming a game session and discovering multiple new unlocks at once.
        /// </summary>
        /// <param name="achievements">
        /// Collection of (achievementId, displayName, description, iconUrl) tuples.
        /// </param>
        /// <param name="ct">Cancellation token.</param>
        public async Task SyncAchievementsBatchAsync(
            IEnumerable<(string Id, string Name, string? Description, string? IconUrl)> achievements,
            CancellationToken ct = default)
        {
            if (achievements == null) return;

            var list = achievements.ToList();
            if (list.Count == 0) return;

            foreach (var (id, name, desc, icon) in list)
            {
                if (ct.IsCancellationRequested)
                    break;

                await SyncAchievementAsync(id, name, desc, icon, ct).ConfigureAwait(false);
                // Small delay between syncs to avoid overwhelming the backend
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Gets the set of achievements already synced in this session
        /// (prevents re-syncing the same unlock multiple times).
        /// </summary>
        public IReadOnlySet<string> SyncedUnlocks => _syncedUnlocks;

        /// <summary>
        /// Clears the in-memory sync cache. Call this when starting a new game session.
        /// </summary>
        public void ResetSession()
        {
            _syncedUnlocks.Clear();
        }
    }
}
