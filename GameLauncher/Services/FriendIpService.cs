using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GameLauncher.Models;

namespace GameLauncher.Services
{
    /// <summary>
    /// Stores and loads locally-saved network addresses (Radmin IP, LAN IP) for each friend.
    /// Data is written to:
    ///   %APPDATA%/GameOS/FriendIps/{SafeUsername}.json
    /// These records are never sent to the backend.
    /// </summary>
    public static class FriendIpService
    {
        private static readonly string IpsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GameOS", "FriendIps");

        private static readonly JsonSerializerOptions _jsonOpts =
            new JsonSerializerOptions { WriteIndented = true };

        /// <summary>Returns all locally-saved IPs for the given friend username.</summary>
        public static List<FriendIpEntry> Load(string username)
        {
            try
            {
                var path = GetPath(username);
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    return JsonSerializer.Deserialize<List<FriendIpEntry>>(json) ?? new();
                }
            }
            catch { /* best-effort */ }
            return new();
        }

        /// <summary>Persists the full IP list for the given friend username.</summary>
        public static void Save(string username, IEnumerable<FriendIpEntry> entries)
        {
            try
            {
                Directory.CreateDirectory(IpsDir);
                var path = GetPath(username);
                File.WriteAllText(path, JsonSerializer.Serialize(new List<FriendIpEntry>(entries), _jsonOpts));
            }
            catch { /* best-effort */ }
        }

        private static string GetPath(string username)
        {
            var safe = string.Concat(username.Select(c =>
                Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            if (string.IsNullOrWhiteSpace(safe)) safe = "unknown";
            return Path.Combine(IpsDir, $"{safe}.friendips.json");
        }
    }
}
