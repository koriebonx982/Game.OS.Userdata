using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using GameLauncher.Models;

namespace GameLauncher.Services;

/// <summary>
/// Reads and writes Ryujinx mod configuration files (<c>mods.json</c>) for a
/// given Nintendo Switch TitleID.
///
/// <para>Ryujinx stores per-game mod lists at:</para>
/// <list type="bullet">
///   <item>Portable mode — <c>{ryujinxDir}\portable\games\{titleId}\mods.json</c></item>
///   <item>Standard mode — <c>%APPDATA%\Ryujinx\games\{titleId}\mods.json</c></item>
/// </list>
/// </summary>
public static class RyujinxModService
{
    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented        = true,
        PropertyNamingPolicy = null,  // preserve exact property names from the model
    };

    /// <summary>
    /// Searches for the <c>mods.json</c> file for the given <paramref name="titleId"/>
    /// relative to the Ryujinx executable at <paramref name="ryujinxExePath"/>.
    /// Portable mode is checked first; falls back to the standard AppData location.
    /// Returns <see langword="null"/> when neither file exists.
    /// </summary>
    public static string? FindModsJson(string ryujinxExePath, string titleId)
    {
        if (string.IsNullOrEmpty(ryujinxExePath) || string.IsNullOrEmpty(titleId))
            return null;

        string ryujinxDir = Path.GetDirectoryName(ryujinxExePath) ?? "";
        string tid        = titleId.ToLowerInvariant();

        // ── Portable mode ─────────────────────────────────────────────────────
        string portablePath = Path.Combine(ryujinxDir, "portable", "games", tid, "mods.json");
        if (File.Exists(portablePath)) return portablePath;

        // ── Standard AppData path ─────────────────────────────────────────────
        string appData       = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string standardPath  = Path.Combine(appData, "Ryujinx", "games", tid, "mods.json");
        if (File.Exists(standardPath)) return standardPath;

        return null;
    }

    /// <summary>
    /// Returns the path where a new <c>mods.json</c> would be created for the given
    /// TitleID.  Prefers portable mode when the portable directory exists; falls back
    /// to the standard AppData location.
    /// </summary>
    public static string GetDefaultModsJsonPath(string ryujinxExePath, string titleId)
    {
        string ryujinxDir  = Path.GetDirectoryName(ryujinxExePath) ?? "";
        string tid         = titleId.ToLowerInvariant();
        string portableDir = Path.Combine(ryujinxDir, "portable");

        if (Directory.Exists(portableDir))
            return Path.Combine(portableDir, "games", tid, "mods.json");

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "Ryujinx", "games", tid, "mods.json");
    }

    /// <summary>Deserialises <paramref name="modsJsonPath"/> and returns the mod list.</summary>
    public static List<RyujinxMod> LoadMods(string modsJsonPath)
    {
        try
        {
            string json = File.ReadAllText(modsJsonPath);
            var cfg = JsonSerializer.Deserialize<RyujinxModConfig>(json,
                          new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return cfg?.Mods ?? new List<RyujinxMod>();
        }
        catch { return new List<RyujinxMod>(); }
    }

    /// <summary>
    /// Serialises <paramref name="mods"/> back to <paramref name="modsJsonPath"/>,
    /// creating the parent directory if needed.
    /// </summary>
    public static void SaveMods(string modsJsonPath, List<RyujinxMod> mods)
    {
        string? dir = Path.GetDirectoryName(modsJsonPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var cfg  = new RyujinxModConfig { Mods = mods };
        string json = JsonSerializer.Serialize(cfg, _jsonOpts);
        File.WriteAllText(modsJsonPath, json);
    }
}
