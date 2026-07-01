using System;
using System.IO;
using System.Net.Http;
using System.IO.Compression;
using System.Threading.Tasks;
using System.Windows;
using System.Text.Json;
using System.Linq;
using System.Diagnostics;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace PS5_OS
{
    public static class CloudSaveManager
    {
        // Gets the Ludusavi save folder for the currently logged-in account
        public static string GetCurrentAccountLudusaviFolder()
        {
            string? accountPath = null;
            if (Application.Current?.Properties["LoggedInAccountPath"] is string p && !string.IsNullOrWhiteSpace(p))
            {
                accountPath = p;
            }
            else if (Application.Current?.Properties["LoggedInAccount"] is string name && !string.IsNullOrWhiteSpace(name))
            {
                accountPath = Path.Combine(AppContext.BaseDirectory, "Data", "Accounts", name);
            }
            else
            {
                accountPath = Path.Combine(AppContext.BaseDirectory, "Data", "Accounts", "Guest");
            }
            var ludusaviFolder = Path.Combine(accountPath, "Ludusavi");
            Directory.CreateDirectory(ludusaviFolder);
            return ludusaviFolder;
        }

        // Example: Backup a game's save folder to the user's Ludusavi folder
        public static async Task BackupGameSaveAsync(string gameSaveSourceFolder, string gameName)
        {
            var targetRoot = GetCurrentAccountLudusaviFolder();
            var targetGameFolder = Path.Combine(targetRoot, SanitizeForFolder(gameName));
            await Task.Run(() =>
            {
                if (Directory.Exists(targetGameFolder))
                    Directory.Delete(targetGameFolder, true);
                CopyDirectory(gameSaveSourceFolder, targetGameFolder);
            });
        }

        // Example: Restore a game's save folder from the user's Ludusavi folder
        public static async Task RestoreGameSaveAsync(string gameSaveTargetFolder, string gameName)
        {
            var sourceRoot = GetCurrentAccountLudusaviFolder();
            var sourceGameFolder = Path.Combine(sourceRoot, SanitizeForFolder(gameName));
            await Task.Run(() =>
            {
                if (!Directory.Exists(sourceGameFolder))
                    throw new DirectoryNotFoundException($"No backup found for {gameName}.");
                if (Directory.Exists(gameSaveTargetFolder))
                    Directory.Delete(gameSaveTargetFolder, true);
                CopyDirectory(sourceGameFolder, gameSaveTargetFolder);
            });
        }

        // Helper: Recursively copy a directory
        private static void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var dest = Path.Combine(destDir, Path.GetFileName(file));
                File.Copy(file, dest, true);
            }
            foreach (var dir in Directory.GetDirectories(sourceDir))
            {
                var dest = Path.Combine(destDir, Path.GetFileName(dir));
                CopyDirectory(dir, dest);
            }
        }

        // Helper: Sanitize game name for folder usage
        public static string SanitizeForFolder(string name)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            return string.Concat(name.Select(c => invalidChars.Contains(c) ? '_' : c));
        }

        // Ensures Ludusavi is downloaded and extracted to Data/Ludusavi/ludusavi.exe
        public static async Task<string> EnsureLudusaviDownloadedAsync()
        {
            string dataDir = Path.Combine(AppContext.BaseDirectory, "Data");
            string ludusaviDir = Path.Combine(dataDir, "Ludusavi");
            string exePath = Path.Combine(ludusaviDir, "ludusavi.exe");
            if (File.Exists(exePath))
                return exePath;

            Directory.CreateDirectory(ludusaviDir);

            // 1. Get latest release info from GitHub API
            string apiUrl = "https://api.github.com/repos/mtkennerly/ludusavi/releases/latest";
            string? zipUrl = null;
            using (var http = new HttpClient())
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("PS5_OS/1.0");
                var json = await http.GetStringAsync(apiUrl);
                using var doc = JsonDocument.Parse(json);
                var assets = doc.RootElement.GetProperty("assets");
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString();
                    var url = asset.GetProperty("browser_download_url").GetString();
                    if (name != null && url != null &&
                        (name.EndsWith("win64.zip", StringComparison.OrdinalIgnoreCase) || name.EndsWith("windows.zip", StringComparison.OrdinalIgnoreCase)))
                    {
                        zipUrl = url;
                        break;
                    }
                }
            }
            if (zipUrl == null)
                throw new Exception("Could not find Ludusavi Windows zip in latest release.");

            // 2. Download and extract
            string tempZip = Path.Combine(Path.GetTempPath(), $"ludusavi_{Guid.NewGuid()}.zip");
            using (var http = new HttpClient())
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("PS5_OS/1.0");
                using (var response = await http.GetAsync(zipUrl))
                {
                    response.EnsureSuccessStatusCode();
                    using (var fs = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await response.Content.CopyToAsync(fs);
                    }
                }
            }

            ZipFile.ExtractToDirectory(tempZip, ludusaviDir, true);
            File.Delete(tempZip);

            // Find the exe in the extracted files (in case it's in a subfolder)
            var exe = Directory.GetFiles(ludusaviDir, "ludusavi.exe", SearchOption.AllDirectories);
            if (exe.Length > 0)
            {
                if (!exe[0].Equals(exePath, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(exe[0], exePath, true);
                }
                return exePath;
            }
            throw new FileNotFoundException("Ludusavi executable not found after extraction.");
        }

        // Run Ludusavi CLI to back up saves for a specific game name
        public static async Task RunLudusaviBackupForGameAsync(string gameName)
        {
            var exePath = await EnsureLudusaviDownloadedAsync();
            var accountLudusaviDir = GetCurrentAccountLudusaviFolder();
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = $"backup --force --path \"{accountLudusaviDir}\" \"{gameName}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.Start();
            string output = await process.StandardOutput.ReadToEndAsync();
            string error = await process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new Exception($"Ludusavi error: {error}\n{output}");
            }
        }

        // Run Ludusavi CLI to back up saves for a user-supplied game name (fuzzy matching)
        public static async Task RunLudusaviBackupForGameFuzzyAsync(string userGameName)
        {
            var manifestName = await FindClosestManifestGameNameAsync(userGameName);
            if (manifestName == null)
            {
                throw new Exception($"Ludusavi does not support this game: {userGameName}");
            }
            await RunLudusaviBackupForGameAsync(manifestName);
        }

        // Run Ludusavi CLI to restore saves for a user-supplied game name (fuzzy matching)
        public static async Task RunLudusaviRestoreForGameFuzzyAsync(string userGameName)
        {
            var manifestName = await FindClosestManifestGameNameAsync(userGameName);
            if (manifestName == null)
            {
                throw new Exception($"Ludusavi does not support this game: {userGameName}");
            }
            await RunLudusaviRestoreForGameAsync(manifestName);
        }

        // Run Ludusavi CLI to restore saves for a specific game name
        public static async Task RunLudusaviRestoreForGameAsync(string gameName)
        {
            var exePath = await EnsureLudusaviDownloadedAsync();
            var accountLudusaviDir = GetCurrentAccountLudusaviFolder();
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = $"restore --force --path \"{accountLudusaviDir}\" \"{gameName}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.Start();
            string output = await process.StandardOutput.ReadToEndAsync();
            string error = await process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                throw new Exception($"Ludusavi error: {error}\n{output}");
            }
        }

        // Checks if a game exists in the Ludusavi PC manifest (online)
        public static async Task<bool> IsGameInLudusaviManifestAsync(string gameName)
        {
            string manifestUrl = "https://raw.githubusercontent.com/mtkennerly/ludusavi-manifest/master/data/manifest.yaml";
            using var http = new HttpClient();
            var yamlContent = await http.GetStringAsync(manifestUrl);

            var yaml = new YamlStream();
            using var reader = new StringReader(yamlContent);
            yaml.Load(reader);

            var root = (YamlMappingNode)yaml.Documents[0].RootNode;
            return root.Children.Keys
                .OfType<YamlScalarNode>()
                .Any(k => k.Value == gameName);
        }

        // Normalize game name for loose matching (removes non-alphanumeric, lowercases)
        public static string NormalizeGameName(string name)
        {
            return Regex.Replace(name ?? "", "[^a-zA-Z0-9]", "").ToLowerInvariant();
        }

        // Find the closest manifest game name using loose matching
        public static async Task<string?> FindClosestManifestGameNameAsync(string gameName)
        {
            string manifestUrl = "https://raw.githubusercontent.com/mtkennerly/ludusavi-manifest/master/data/manifest.yaml";
            using var http = new HttpClient();
            var yamlContent = await http.GetStringAsync(manifestUrl);

            var yaml = new YamlStream();
            using var reader = new StringReader(yamlContent);
            yaml.Load(reader);

            var root = (YamlMappingNode)yaml.Documents[0].RootNode;
            var normalizedInput = NormalizeGameName(gameName);

            // 1. Try exact normalized match
            foreach (var key in root.Children.Keys.OfType<YamlScalarNode>())
            {
                if (NormalizeGameName(key.Value) == normalizedInput)
                    return key.Value; // Return the canonical manifest name
            }
            // 2. Try partial (contains) normalized match
            foreach (var key in root.Children.Keys.OfType<YamlScalarNode>())
            {
                var normalizedKey = NormalizeGameName(key.Value);
                if (normalizedKey.Contains(normalizedInput) || normalizedInput.Contains(normalizedKey))
                    return key.Value;
            }
            // 3. Try substring match (case-insensitive, non-normalized)
            foreach (var key in root.Children.Keys.OfType<YamlScalarNode>())
            {
                if (key.Value != null && key.Value.IndexOf(gameName, StringComparison.OrdinalIgnoreCase) >= 0)
                    return key.Value;
            }
            return null; // No match found
        }
    }
}
