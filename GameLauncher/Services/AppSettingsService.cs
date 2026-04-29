using System;
using System.IO;
using System.Text.Json;
using GameLauncher.Models;

namespace GameLauncher.Services
{
    /// <summary>
    /// Loads and saves application-wide settings from/to the user's AppData folder.
    /// Settings are stored at <c>%APPDATA%\GameOS\appsettings.json</c>.
    /// </summary>
    public static class AppSettingsService
    {
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GameOS", "appsettings.json");

        private static readonly JsonSerializerOptions _jsonOpts =
            new JsonSerializerOptions { WriteIndented = true };

        /// <summary>
        /// Loads the saved application settings.
        /// Returns a default <see cref="AppSettings"/> instance when no file exists.
        /// </summary>
        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(SettingsPath))
                {
                    var json = File.ReadAllText(SettingsPath);
                    return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                }
            }
            catch { /* best-effort */ }
            return new AppSettings();
        }

        /// <summary>Persists the given settings to disk.</summary>
        public static void Save(AppSettings settings)
        {
            try
            {
                string? dir = Path.GetDirectoryName(SettingsPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, _jsonOpts));
            }
            catch { /* best-effort */ }
        }
    }
}
