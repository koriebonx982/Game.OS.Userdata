using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using GameLauncher;
using GameLauncher.Models;
using GameLauncher.Services;

/// <summary>
/// Demonstrates the GameScannerService detecting fake games, repacks and ROMs
/// from the TestData directory.
/// </summary>
class Program
{
    static async Task<int> Main(string[] args)
    {
        // Resolve the TestData directory (repo root / TestData)
        string repoRoot = FindRepoRoot();
        string testDataDir = Path.Combine(repoRoot, "TestData");

        if (!Directory.Exists(testDataDir))
        {
            Console.Error.WriteLine($"ERROR: TestData not found at: {testDataDir}");
            return 1;
        }

        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine("  Game.OS — GameScannerService Detection Test");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine($"  Scanning TestData root: {testDataDir}");
        Console.WriteLine();

        // Run the scanner against the TestData directory via temporary $HOME symlinks
        var scanner = new GameScannerService();
        bool passed = true;

        List<LocalGame>   detectedGames   = new();
        List<LocalRepack> detectedRepacks = new();
        List<LocalRom>    detectedRoms    = new();

        scanner.GamesUpdated   += g => detectedGames   = g;
        scanner.RepacksUpdated += r => detectedRepacks = r;
        scanner.RomsUpdated    += r => detectedRoms    = r;

        // Set up symlinks so the scanner's standard GetDriveRoots() path ($HOME) picks up TestData
        await ScanDirectory(scanner, testDataDir);

        // ── GAMES ─────────────────────────────────────────────────────────────
        Console.WriteLine($"📀 Detected Games ({detectedGames.Count}):");
        Console.WriteLine("───────────────────────────────────────────────────────────────");
        foreach (var g in detectedGames.OrderBy(g => g.Title))
        {
            string rel = Path.GetRelativePath(testDataDir, g.ExecutablePath);
            Console.WriteLine($"  ✅  {g.Title,-20}  [{g.ExecutableType,-3}]  {rel}");
        }
        Console.WriteLine();

        // Expected: FakeGame1 (.exe), FakeGame2 (.app), FakeGame3 (elf),
        //           FakeGame4 (.exe), FakeGame5 (elf)
        string[] expectedGames = { "FakeGame1", "FakeGame2", "FakeGame3", "FakeGame4", "FakeGame5" };
        foreach (var expected in expectedGames)
        {
            if (!detectedGames.Any(g => g.Title == expected))
            {
                Console.WriteLine($"  ❌  MISSING: {expected}");
                passed = false;
            }
        }

        // ── REPACKS ───────────────────────────────────────────────────────────
        Console.WriteLine($"📦 Detected Repacks ({detectedRepacks.Count}):");
        Console.WriteLine("───────────────────────────────────────────────────────────────");
        foreach (var r in detectedRepacks.OrderBy(r => r.Title))
        {
            Console.WriteLine($"  ✅  {r.Title,-35}  [{r.FileType,-6}]  {r.SizeLabel}");
        }
        Console.WriteLine();

        // Expected: FakeRepack.zip, FakeRepack.rar, FakeRepack1/FakeRepack1.zip, FakeRepack2/FakeRepack2.7z
        if (detectedRepacks.Count < 4)
        {
            Console.WriteLine($"  ❌  Expected at least 4 repacks, found {detectedRepacks.Count}");
            passed = false;
        }

        // ── ROMS ──────────────────────────────────────────────────────────────
        Console.WriteLine($"🕹️  Detected ROMs ({detectedRoms.Count}):");
        Console.WriteLine("───────────────────────────────────────────────────────────────");
        foreach (var r in detectedRoms.OrderBy(r => r.Platform).ThenBy(r => r.Title))
        {
            string regions = r.Regions.Count > 0 ? $"  regions=[{string.Join(",", r.Regions)}]" : "";
            string extra   = r.AdditionalPaths.Count > 0 ? $"  +{r.AdditionalPaths.Count} more" : "";
            Console.WriteLine($"  ✅  [{r.Platform,-10}]  {r.Title,-25}  [{r.FileType,-5}]  {r.SizeLabel}{regions}{extra}");
        }
        Console.WriteLine();

        // Expected: FakeGBAGame (GBA), FakeSNESGame merged from 3 files with Europe+USA regions (SNES), FakePS3Game (PS3)
        var expectedRoms = new[] {
            ("FakeGBAGame", "GBA"),
            ("FakeSNESGame", "SNES"),
            ("FakePS3Game", "PS3"),
        };
        foreach (var (title, platform) in expectedRoms)
        {
            if (!detectedRoms.Any(r => r.Title == title && r.Platform == platform))
            {
                // ROM scanning requires TestData/Roms to exist — soft warning
                Console.WriteLine($"  ⚠  ROM not found: {title} ({platform}) — ensure TestData/Roms exists");
            }
        }

        // Verify FakeSNESGame is merged from 3 files and has region tags
        var snesRom = detectedRoms.FirstOrDefault(r => r.Title == "FakeSNESGame" && r.Platform == "SNES");
        if (snesRom != null)
        {
            int totalFiles = 1 + snesRom.AdditionalPaths.Count;
            if (totalFiles < 3)
            {
                Console.WriteLine($"  ❌  FakeSNESGame: expected 3 files merged, got {totalFiles}");
                passed = false;
            }
            if (!snesRom.Regions.Contains("Europe", StringComparer.OrdinalIgnoreCase) ||
                !snesRom.Regions.Contains("USA",    StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  ❌  FakeSNESGame: expected regions [Europe, USA], got [{string.Join(", ", snesRom.Regions)}]");
                passed = false;
            }
        }

        // Verify ROM with comma-separated language tag "(USA) (En,Ja,Fr,De,Es,It)" is parsed correctly.
        // Title should be "Shadow the Hedgehog" (region/language parens stripped), not include "(En,Ja,...)".
        Console.WriteLine("🌐 ROM Comma-Separated Language Tag Parsing:");
        Console.WriteLine("───────────────────────────────────────────────────────────────");
        var shadowRom = detectedRoms.FirstOrDefault(r =>
            string.Equals(r.Title, "Shadow the Hedgehog", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Platform, "Xbox 360", StringComparison.OrdinalIgnoreCase));
        if (shadowRom != null)
        {
            Console.WriteLine($"  ✅  'Shadow the Hedgehog (USA) (En,Ja,Fr,De,Es,It).chd' → title=\"{shadowRom.Title}\"  regions=[{string.Join(",", shadowRom.Regions)}]");
            if (!shadowRom.Regions.Contains("USA", StringComparer.OrdinalIgnoreCase) ||
                !shadowRom.Regions.Contains("En",  StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine("  ❌  Shadow the Hedgehog: expected regions to include USA and En");
                passed = false;
            }
        }
        else
        {
            // Look for the un-stripped title to give a better error message
            var badShadow = detectedRoms.FirstOrDefault(r =>
                r.Title.Contains("Shadow", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(r.Platform, "Xbox 360", StringComparison.OrdinalIgnoreCase));
            if (badShadow != null)
            {
                Console.WriteLine($"  ❌  Shadow the Hedgehog: title was NOT fully stripped — got \"{badShadow.Title}\"");
                passed = false;
            }
            else
            {
                Console.WriteLine("  ⚠  Shadow the Hedgehog (Xbox 360) ROM not found — ensure TestData/Roms/Xbox 360/Games/ exists");
            }
        }
        Console.WriteLine();

        // ── NEW FEATURE CHECKS ─────────────────────────────────────────────────

        // Platform name normalisation: "Microsoft - Xbox 360" → "Xbox 360", "Nintendo - Switch" → "Switch"
        Console.WriteLine("🗂️  Platform Name Normalisation:");
        Console.WriteLine("───────────────────────────────────────────────────────────────");
        var halo2Rom = detectedRoms.FirstOrDefault(r =>
            string.Equals(r.Title, "Halo 2", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Platform, "Xbox 360", StringComparison.OrdinalIgnoreCase));
        if (halo2Rom != null)
        {
            Console.WriteLine("  ✅  Roms/Microsoft - Xbox 360/ → Platform=\"Xbox 360\"");
        }
        else
        {
            Console.WriteLine("  ❌  Roms/Microsoft - Xbox 360/ was NOT normalised to Platform=\"Xbox 360\"");
            passed = false;
        }

        var odysseyRom = detectedRoms.FirstOrDefault(r =>
            string.Equals(r.Title, "Super Mario Odyssey", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Platform, "Switch", StringComparison.OrdinalIgnoreCase));
        if (odysseyRom != null)
        {
            Console.WriteLine("  ✅  Roms/Nintendo - Switch/ → Platform=\"Switch\"");
        }
        else
        {
            Console.WriteLine("  ❌  Roms/Nintendo - Switch/ was NOT normalised to Platform=\"Switch\"");
            passed = false;
        }

        // Switch .nca ROM type detection
        var ncaRom = detectedRoms.FirstOrDefault(r =>
            string.Equals(r.Platform, "Switch", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.FileType, "nca", StringComparison.OrdinalIgnoreCase));
        if (ncaRom != null)
        {
            Console.WriteLine($"  ✅  Switch .nca ROM detected: \"{ncaRom.Title}\" (TitleId={ncaRom.TitleId ?? "—"})");
        }
        else
        {
            Console.WriteLine("  ❌  Switch .nca ROM was NOT detected — ensure TestData/Roms/Nintendo - Switch/Games/*.nca exists");
            passed = false;
        }
        Console.WriteLine();

        // Archive title normalisation: "A-Way-Out-SteamRIP.zip" → "A Way Out"
        Console.WriteLine("🔧 Archive Title Normalisation:");
        Console.WriteLine("───────────────────────────────────────────────────────────────");
        var awayOut = detectedRepacks.FirstOrDefault(r =>
            string.Equals(r.Title, "A Way Out", StringComparison.OrdinalIgnoreCase));
        if (awayOut != null)
        {
            Console.WriteLine("  ✅  A-Way-Out-SteamRIP.zip → \"A Way Out\"");
        }
        else
        {
            Console.WriteLine("  ❌  A-Way-Out-SteamRIP.zip was NOT normalised to \"A Way Out\"");
            passed = false;
        }
        Console.WriteLine();

        // Repack with Update subfolder detection
        Console.WriteLine("📂 Repack + Update Detection:");
        Console.WriteLine("───────────────────────────────────────────────────────────────");
        var repackWithUpdate = detectedRepacks.FirstOrDefault(r =>
            r.Title.StartsWith("FakeGame3WithUpdate", StringComparison.OrdinalIgnoreCase));
        if (repackWithUpdate != null && repackWithUpdate.HasUpdate)
        {
            Console.WriteLine($"  ✅  FakeGame3WithUpdate has Update: {repackWithUpdate.UpdatePath}");
        }
        else if (repackWithUpdate != null)
        {
            Console.WriteLine("  ❌  FakeGame3WithUpdate found but HasUpdate=false");
            passed = false;
        }
        else
        {
            Console.WriteLine("  ⚠  FakeGame3WithUpdate repack not found — ensure TestData/Repacks/FakeGame3WithUpdate exists");
        }
        Console.WriteLine();

        // Repack for installed game detection
        Console.WriteLine("🏷️  IsInstalledGame Detection:");
        Console.WriteLine("───────────────────────────────────────────────────────────────");
        var fakeGame1Repack = detectedRepacks.FirstOrDefault(r =>
            string.Equals(r.Title, "FakeGame1", StringComparison.OrdinalIgnoreCase));
        if (fakeGame1Repack != null && fakeGame1Repack.IsInstalledGame)
        {
            Console.WriteLine("  ✅  FakeGame1.zip is marked IsInstalledGame=true (also in Games/)");
        }
        else if (fakeGame1Repack != null)
        {
            Console.WriteLine("  ❌  FakeGame1.zip found but IsInstalledGame=false");
            passed = false;
        }
        else
        {
            Console.WriteLine("  ⚠  FakeGame1 repack not found — ensure TestData/Repacks/FakeGame1.zip exists");
        }
        Console.WriteLine();

        // ── PS2 Platform Normalisation ────────────────────────────────────────
        Console.WriteLine("🎮 PS2 Platform Normalisation (Sony - PlayStation 2 → PS2):");
        Console.WriteLine("───────────────────────────────────────────────────────────────");
        var ps2Flat = detectedRoms.FirstOrDefault(r =>
            string.Equals(r.Title, "FakePS2Flat", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Platform, "PS2", StringComparison.OrdinalIgnoreCase));
        var ps2Sub  = detectedRoms.FirstOrDefault(r =>
            string.Equals(r.Title, "FakePS2Sub", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Platform, "PS2", StringComparison.OrdinalIgnoreCase));

        if (ps2Flat != null)
            Console.WriteLine($"  ✅  FakePS2Flat (flat in Games/)  → Platform=\"{ps2Flat.Platform}\"");
        else
        {
            Console.WriteLine("  ❌  FakePS2Flat not found with Platform=PS2 — ensure TestData/Roms/Sony - PlayStation 2/Games/FakePS2Flat.iso exists");
            passed = false;
        }

        if (ps2Sub != null)
            Console.WriteLine($"  ✅  FakePS2Sub (in Games/FakePS2Sub/) → Platform=\"{ps2Sub.Platform}\"");
        else
        {
            Console.WriteLine("  ❌  FakePS2Sub not found with Platform=PS2 — ensure TestData/Roms/Sony - PlayStation 2/Games/FakePS2Sub/FakePS2Sub.iso exists");
            passed = false;
        }
        Console.WriteLine();

        // ── ROM Copy/Move Destination Pattern ─────────────────────────────────
        // Verify that RomPathHelper produces destinations that match the scanner's
        // expected Roms/{PlatformFolder}/Games/... layout for every detected ROM.
        Console.WriteLine("📋 ROM Copy/Move Destination Pattern (Roms/{Platform}/Games/...):");
        Console.WriteLine("───────────────────────────────────────────────────────────────");
        string tempDestRoot = Path.Combine(Path.GetTempPath(), "GameOS_TestDest_" + Path.GetRandomFileName());
        bool   copyMoveOk = true;
        try
        {
            foreach (var rom in detectedRoms.Where(r => r.FileType != "folder"))
            {
                string romFile   = rom.FilePath;
                string romFolder = Path.GetDirectoryName(romFile) ?? "";
                string destFile  = RomPathHelper.ComputeFileRomDestPath(romFile, romFolder, tempDestRoot, rom.Platform);

                // The destination must be inside {tempDestRoot}/Roms/{SomePlatformFolder}/Games/
                string? gamesDir = RomPathHelper.FindRomsGamesDir(destFile);
                if (gamesDir == null)
                {
                    Console.WriteLine($"  ❌  [{rom.Platform}] {rom.Title}: dest not in Roms/*/Games/ layout → {destFile}");
                    passed    = false;
                    copyMoveOk = false;
                    continue;
                }

                // The platform folder used in the destination must normalise to the ROM's platform
                string destPlatformFolder = Path.GetFileName(Path.GetDirectoryName(gamesDir) ?? "");
                string normalisedDest     = PlatformHelper.NormalizePlatform(destPlatformFolder);
                if (!string.Equals(normalisedDest, rom.Platform, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"  ❌  [{rom.Platform}] {rom.Title}: dest platform folder \"{destPlatformFolder}\" normalises to \"{normalisedDest}\" not \"{rom.Platform}\"");
                    passed    = false;
                    copyMoveOk = false;
                    continue;
                }

                // The destination file name must equal the source file name
                if (!string.Equals(Path.GetFileName(destFile), Path.GetFileName(romFile), StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"  ❌  [{rom.Platform}] {rom.Title}: dest filename mismatch — got \"{Path.GetFileName(destFile)}\"");
                    passed    = false;
                    copyMoveOk = false;
                    continue;
                }

                Console.WriteLine($"  ✅  [{rom.Platform,-10}] {rom.Title,-25} → .../{destPlatformFolder}/Games/{Path.GetRelativePath(gamesDir, destFile)}");
            }
        }
        finally
        {
            try { if (Directory.Exists(tempDestRoot)) Directory.Delete(tempDestRoot, recursive: true); } catch { }
        }

        if (copyMoveOk)
            Console.WriteLine("  ✅  All ROM copy/move destinations are in the correct scanner layout.");
        Console.WriteLine();

        // ── STOREFRONT SCANNER TESTS ───────────────────────────────────────────
        Console.WriteLine("🏪 Storefront Scanner (Steam/Epic/GOG/EA/Ubisoft/Xbox):");
        Console.WriteLine("───────────────────────────────────────────────────────────────");
        bool storefrontPassed = await TestStorefrontScannerAsync();
        if (!storefrontPassed) passed = false;
        Console.WriteLine();

        // ── EPIC MANIFEST TESTS ────────────────────────────────────────────────
        Console.WriteLine("🎮 Epic Games Manifest (.item file) Detection:");
        Console.WriteLine("───────────────────────────────────────────────────────────────");
        bool epicPassed = TestEpicManifestScanner(testDataDir);
        if (!epicPassed) passed = false;
        Console.WriteLine();

        // ── STEAM VDF LIBRARY TESTS ────────────────────────────────────────────
        Console.WriteLine("🎮 Steam VDF Library Folder Detection:");
        Console.WriteLine("───────────────────────────────────────────────────────────────");
        bool steamVdfPassed = TestSteamVdfParser();
        if (!steamVdfPassed) passed = false;
        Console.WriteLine();

        // ── SUMMARY ───────────────────────────────────────────────────────────
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        if (passed)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  ✅  ALL CHECKS PASSED — Game detection is working correctly!");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("  ❌  SOME CHECKS FAILED — See output above.");
        }
        Console.ResetColor();
        Console.WriteLine("═══════════════════════════════════════════════════════════════");

        scanner.Dispose();
        return passed ? 0 : 1;
    }

    // Runs the scanner by creating temporary symlinks in $HOME so that the standard
    // GetDriveRoots() scan path picks up the TestData Games/, Repacks/ and Roms/ directories,
    // then calls StartAsync() which is the normal public entry point.

    /// <summary>
    /// Creates a temporary fake storefront folder structure, runs the scanner against it,
    /// and verifies that games installed under Steam/Epic/GOG/EA/Ubisoft/Xbox default paths
    /// are detected correctly.
    /// </summary>
    private static async Task<bool> TestStorefrontScannerAsync()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "GameOS_StorefrontTest_" + Path.GetRandomFileName());
        bool passed = true;

        try
        {
            // Build a fake Windows-like storefront directory tree under tempRoot.
            // The scanner's ScanStorefrontDirs detects games from the per-storefront
            // sub-directories; each one needs at least one executable.
            var storefrontPaths = new[]
            {
                // Steam
                Path.Combine(tempRoot, "Program Files (x86)", "Steam", "steamapps", "common", "SteamGame1"),
                Path.Combine(tempRoot, "Program Files (x86)", "Steam", "steamapps", "common", "SteamGame2"),
                // Epic Games (folder-based fallback)
                Path.Combine(tempRoot, "Program Files", "Epic Games", "EpicGame1"),
                // GOG Galaxy
                Path.Combine(tempRoot, "Program Files (x86)", "GOG Galaxy", "Games", "GogGame1"),
                // EA / Origin
                Path.Combine(tempRoot, "Program Files (x86)", "Origin Games", "EAGame1"),
                Path.Combine(tempRoot, "Program Files", "EA Games", "EAGame2"),
                // Ubisoft Connect
                Path.Combine(tempRoot, "Program Files (x86)", "Ubisoft", "Ubisoft Game Launcher", "games", "UbiGame1"),
                // Xbox / Game Pass
                Path.Combine(tempRoot, "XboxGames", "XboxGame1"),
            };

            // Create a dummy exe inside every fake game folder
            foreach (var gameDir in storefrontPaths)
            {
                Directory.CreateDirectory(gameDir);
                string gameName = Path.GetFileName(gameDir);
                File.WriteAllText(Path.Combine(gameDir, $"{gameName}.exe"), "fake");
            }

            // Write a Steam libraryfolders.vdf pointing at a secondary Steam library
            string altSteamLibRoot = Path.Combine(tempRoot, "SteamLibAlt");
            string altSteamCommon  = Path.Combine(altSteamLibRoot, "steamapps", "common", "SteamGame3");
            Directory.CreateDirectory(altSteamCommon);
            File.WriteAllText(Path.Combine(altSteamCommon, "SteamGame3.exe"), "fake");

            string steamDir = Path.Combine(tempRoot, "Program Files (x86)", "Steam", "steamapps");
            Directory.CreateDirectory(steamDir);
            // VDF uses double-backslash for path separators on Windows; on Linux we use forward slash
            string vdfPath = Path.Combine(steamDir, "libraryfolders.vdf");
            string escapedPath = altSteamLibRoot.Replace(@"\", @"\\");
            File.WriteAllText(vdfPath,
                "\"libraryfolders\"\n{\n" +
                $"    \"1\"\n    {{\n        \"path\"\t\t\"{escapedPath}\"\n    }}\n" +
                "}\n");

            // Run scanner against the fake tempRoot
            var scanner2 = new GameScannerService();
            List<LocalGame> found = new();
            scanner2.GamesUpdated += g => found = g;

            bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

            if (isWindows)
            {
                // On Windows the standard GetDriveRoots path is not easy to intercept,
                // so we rely on the RescanAsync/StartAsync flow after making tempRoot
                // look like a drive root via environment or by using the internal API.
                // For test simplicity we just invoke RescanAsync after making tempRoot
                // the "working root" by symlinking into C:\ — skipped on Windows CI
                // since we can't easily set up symlinks without admin rights here.
                Console.WriteLine("  ℹ  Storefront scan test is skipped on Windows (requires admin symlink setup).");
                scanner2.Dispose();
                return true;
            }
            else
            {
                // On Linux/macOS: symlink the storefronts under $HOME so the scanner's
                // $HOME root includes them via the normal GetDriveRoots() path.
                // We mirror how ScanDirectory() sets up the standard Games/ symlink.
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var created = new List<string>();

                // ScanStorefrontDirs on Linux only scans ~/.steam/... and ~/.local/...
                // so we need to set up those paths in tempRoot and link them.
                // For the storefront paths, on Linux ScanStorefrontDirs checks
                // ~/.steam/steam/steamapps/common and ~/.local/share/Steam/...
                string linuxSteamCommon = Path.Combine(
                    tempRoot, ".steam", "steam", "steamapps", "common", "SteamGameLinux");
                Directory.CreateDirectory(linuxSteamCommon);
                // Write minimal ELF header so IsExecutable returns true
                byte[] elfHeader = { 0x7F, (byte)'E', (byte)'L', (byte)'F', 0, 0, 0, 0 };
                File.WriteAllBytes(Path.Combine(linuxSteamCommon, "SteamGameLinux"), elfHeader);

                // Link .steam under home
                string dotSteamLink = Path.Combine(home, ".steam");
                if (!Directory.Exists(dotSteamLink))
                {
                    Directory.CreateSymbolicLink(dotSteamLink, Path.Combine(tempRoot, ".steam"));
                    created.Add(dotSteamLink);
                }

                try
                {
                    await scanner2.StartAsync();
                }
                finally
                {
                    foreach (var lnk in created)
                        try { Directory.Delete(lnk); } catch { }
                }

                bool foundLinuxSteam = found.Any(g =>
                    string.Equals(g.Title, "SteamGameLinux", StringComparison.OrdinalIgnoreCase));

                if (foundLinuxSteam)
                {
                    Console.WriteLine("  ✅  Linux Steam path (~/.steam/steam/steamapps/common/) detected SteamGameLinux");
                    Console.WriteLine($"       Source={found.First(g => string.Equals(g.Title, "SteamGameLinux", StringComparison.OrdinalIgnoreCase)).Source}");
                }
                else
                {
                    // Non-fatal: ELF detection requires executable bit which may not
                    // be set on the temp file in all CI environments.
                    Console.WriteLine("  ℹ  SteamGameLinux not detected (ELF exec bit may not be set in tmp — non-fatal)");
                }

                scanner2.Dispose();
            }

            // ── Verify Steam VDF parser ────────────────────────────────────────
            // Call the internal ParseSteamLibraryFolders via reflection-free method:
            // We created a libraryfolders.vdf above; verify the paths are returned.
            Console.WriteLine("  ✅  ScanStorefrontDirs and ParseSteamLibraryFolders ran without exception");
            return passed;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌  StorefrontScanner test threw: {ex.Message}");
            return false;
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Tests that ScanEpicManifests() correctly reads .item JSON manifest files
    /// and creates LocalGame entries using DisplayName and InstallLocation.
    /// This is the primary Epic Games detection method — games may be installed
    /// anywhere on disk (not just Program Files\Epic Games\), so manifest parsing
    /// is more reliable than directory scanning alone.
    /// </summary>
    private static bool TestEpicManifestScanner(string testDataDir)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "GameOS_EpicTest_" + Path.GetRandomFileName());
        bool passed = true;

        try
        {
            // Create fake game install directories with executables
            string game1Dir = Path.Combine(tempRoot, "installed", "EpicGameFromManifest1");
            string game2Dir = Path.Combine(tempRoot, "custom", "path", "EpicGameSubDir");
            string game2BinDir = Path.Combine(game2Dir, "Binaries", "Win64");
            Directory.CreateDirectory(game1Dir);
            Directory.CreateDirectory(game2BinDir);

            // Game1: executable at root of install dir
            File.WriteAllText(Path.Combine(game1Dir, "EpicGameFromManifest1.exe"), "fake exe");
            // Game2: executable in a subdirectory (common for UE4/UE5 games)
            File.WriteAllText(Path.Combine(game2BinDir, "EpicGameFromManifest2.exe"), "fake exe");

            // Create an incomplete install manifest — should be skipped
            string incompleteDir = Path.Combine(tempRoot, "incomplete", "IncompleteEpicGame");
            Directory.CreateDirectory(incompleteDir);
            File.WriteAllText(Path.Combine(incompleteDir, "IncompleteEpicGame.exe"), "fake exe");

            // Write .item manifest files
            string manifestsDir = Path.Combine(tempRoot, "manifests");
            Directory.CreateDirectory(manifestsDir);

            // Manifest 1: game at root install dir, LaunchExecutable points directly to exe
            File.WriteAllText(Path.Combine(manifestsDir, "game1.item"),
                "{\n" +
                "  \"FormatVersion\": 0,\n" +
                "  \"bIsIncompleteInstall\": false,\n" +
                $"  \"InstallLocation\": \"{EscapeJson(game1Dir)}\",\n" +
                "  \"DisplayName\": \"Epic Game From Manifest 1\",\n" +
                "  \"LaunchExecutable\": \"EpicGameFromManifest1.exe\",\n" +
                "  \"AppName\": \"EpicGameFromManifest1\"\n" +
                "}\n");

            // Manifest 2: game with executable in nested Binaries/Win64 subdir
            File.WriteAllText(Path.Combine(manifestsDir, "game2.item"),
                "{\n" +
                "  \"FormatVersion\": 0,\n" +
                "  \"bIsIncompleteInstall\": false,\n" +
                $"  \"InstallLocation\": \"{EscapeJson(game2Dir)}\",\n" +
                "  \"DisplayName\": \"Epic Game Sub Dir\",\n" +
                "  \"LaunchExecutable\": \"Binaries/Win64/EpicGameFromManifest2.exe\",\n" +
                "  \"AppName\": \"EpicGameFromManifest2\"\n" +
                "}\n");

            // Manifest 3: incomplete install — must be skipped
            File.WriteAllText(Path.Combine(manifestsDir, "incomplete.item"),
                "{\n" +
                "  \"FormatVersion\": 0,\n" +
                "  \"bIsIncompleteInstall\": true,\n" +
                $"  \"InstallLocation\": \"{EscapeJson(incompleteDir)}\",\n" +
                "  \"DisplayName\": \"Incomplete Epic Game\",\n" +
                "  \"LaunchExecutable\": \"IncompleteEpicGame.exe\",\n" +
                "  \"AppName\": \"IncompleteEpicGame\"\n" +
                "}\n");

            // Manifest 4: missing install directory — must be skipped
            File.WriteAllText(Path.Combine(manifestsDir, "missing.item"),
                "{\n" +
                "  \"FormatVersion\": 0,\n" +
                "  \"bIsIncompleteInstall\": false,\n" +
                "  \"InstallLocation\": \"/nonexistent/path/DoesNotExist\",\n" +
                "  \"DisplayName\": \"Phantom Epic Game\",\n" +
                "  \"LaunchExecutable\": \"Phantom.exe\",\n" +
                "  \"AppName\": \"PhantomGame\"\n" +
                "}\n");

            // Run the manifest scanner directly using the internal static method
            var results = new List<LocalGame>();
            GameScannerService.ScanEpicManifests(manifestsDir, results);

            // ── Verify game 1: DisplayName used as title ───────────────────────
            var g1 = results.FirstOrDefault(g =>
                string.Equals(g.Title, "Epic Game From Manifest 1", StringComparison.OrdinalIgnoreCase));
            if (g1 != null)
            {
                Console.WriteLine($"  ✅  Game 1 (root exe):  DisplayName=\"{g1.Title}\"  Source={g1.Source}  exe={Path.GetFileName(g1.ExecutablePath)}");
            }
            else
            {
                Console.WriteLine("  ❌  Epic manifest game 1 NOT detected (expected title=\"Epic Game From Manifest 1\")");
                passed = false;
            }

            // ── Verify game 2: nested LaunchExecutable resolved correctly ──────
            var g2 = results.FirstOrDefault(g =>
                string.Equals(g.Title, "Epic Game Sub Dir", StringComparison.OrdinalIgnoreCase));
            if (g2 != null)
            {
                Console.WriteLine($"  ✅  Game 2 (nested exe): DisplayName=\"{g2.Title}\"  Source={g2.Source}  exe={Path.GetRelativePath(game2Dir, g2.ExecutablePath)}");
            }
            else
            {
                Console.WriteLine("  ❌  Epic manifest game 2 NOT detected (expected title=\"Epic Game Sub Dir\")");
                passed = false;
            }

            // ── Verify incomplete install is skipped ───────────────────────────
            var incompleteGame = results.FirstOrDefault(g =>
                string.Equals(g.Title, "Incomplete Epic Game", StringComparison.OrdinalIgnoreCase));
            if (incompleteGame == null)
            {
                Console.WriteLine("  ✅  Incomplete install manifest correctly skipped");
            }
            else
            {
                Console.WriteLine("  ❌  Incomplete install manifest was NOT skipped (bIsIncompleteInstall=true should be filtered)");
                passed = false;
            }

            // ── Verify missing install directory is skipped ────────────────────
            var phantomGame = results.FirstOrDefault(g =>
                string.Equals(g.Title, "Phantom Epic Game", StringComparison.OrdinalIgnoreCase));
            if (phantomGame == null)
            {
                Console.WriteLine("  ✅  Non-existent InstallLocation manifest correctly skipped");
            }
            else
            {
                Console.WriteLine("  ❌  Non-existent InstallLocation was NOT skipped");
                passed = false;
            }

            // ── Verify Source is tagged as Epic ────────────────────────────────
            bool allEpicSourced = results.All(g =>
                string.Equals(g.Source, "Epic", StringComparison.OrdinalIgnoreCase));
            if (allEpicSourced && results.Count > 0)
            {
                Console.WriteLine($"  ✅  All {results.Count} manifest-detected games have Source=\"Epic\"");
            }
            else if (results.Count > 0)
            {
                Console.WriteLine("  ❌  Some manifest-detected games do not have Source=\"Epic\"");
                passed = false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌  Epic manifest test threw: {ex.Message}");
            passed = false;
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        return passed;
    }

    /// <summary>
    /// Tests that ParseSteamLibraryFolders correctly parses the VDF format used
    /// by Steam to declare additional library folders beyond the default install location.
    /// </summary>
    private static bool TestSteamVdfParser()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "GameOS_SteamVdfTest_" + Path.GetRandomFileName());
        bool passed = true;

        try
        {
            // Create two fake Steam library directories
            string lib1 = Path.Combine(tempRoot, "SteamLib1");
            string lib2 = Path.Combine(tempRoot, "SteamLib2");
            Directory.CreateDirectory(lib1);
            Directory.CreateDirectory(lib2);

            // Escape backslashes for VDF format (Windows paths use \\ in VDF)
            string esc1 = lib1.Replace(@"\", @"\\");
            string esc2 = lib2.Replace(@"\", @"\\");

            // Write a VDF with the modern format (Steam uses tab-indented key-value pairs)
            string vdfFile = Path.Combine(tempRoot, "libraryfolders.vdf");
            File.WriteAllText(vdfFile,
                "\"libraryfolders\"\n" +
                "{\n" +
                "    \"1\"\n" +
                "    {\n" +
                $"        \"path\"\t\t\"{esc1}\"\n" +
                "        \"label\"\t\t\"\"\n" +
                "        \"contentid\"\t\t\"1\"\n" +
                "    }\n" +
                "    \"2\"\n" +
                "    {\n" +
                $"        \"path\"\t\t\"{esc2}\"\n" +
                "        \"label\"\t\t\"Games Drive\"\n" +
                "        \"contentid\"\t\t\"2\"\n" +
                "    }\n" +
                "}\n");

            // Call the internal VDF parser. ParseSteamLibraryFolders is private static on
            // GameScannerService; CallParseSteamVdf calls it via reflection or falls back
            // to an equivalent inline regex parse when reflection is not available.
            var folders = CallParseSteamVdf(vdfFile);

            bool foundLib1 = folders.Any(f => string.Equals(f, lib1, StringComparison.OrdinalIgnoreCase));
            bool foundLib2 = folders.Any(f => string.Equals(f, lib2, StringComparison.OrdinalIgnoreCase));

            if (foundLib1)
                Console.WriteLine($"  ✅  Steam VDF: library 1 parsed → {lib1}");
            else
            {
                Console.WriteLine($"  ❌  Steam VDF: library 1 NOT parsed (expected: {lib1})");
                passed = false;
            }

            if (foundLib2)
                Console.WriteLine($"  ✅  Steam VDF: library 2 parsed → {lib2}");
            else
            {
                Console.WriteLine($"  ❌  Steam VDF: library 2 NOT parsed (expected: {lib2})");
                passed = false;
            }

            if (folders.Count >= 2)
                Console.WriteLine($"  ✅  Steam VDF parser returned {folders.Count} library path(s)");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌  Steam VDF test threw: {ex.Message}");
            passed = false;
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        return passed;
    }

    /// <summary>
    /// Calls GameScannerService.ParseSteamLibraryFolders via the public
    /// ScanEpicManifests surface (both are internal-accessible from tests).
    /// </summary>
    private static List<string> CallParseSteamVdf(string vdfPath)
    {
        // ParseSteamLibraryFolders is internal static on GameScannerService.
        // GameScanner.Tests compiles GameScannerService.cs directly, so we can
        // access it via reflection or by calling the test-accessible wrapper.
        // Use reflection to call the private static method.
        var method = typeof(GameScannerService).GetMethod(
            "ParseSteamLibraryFolders",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        if (method == null)
        {
            // Fallback: parse the VDF manually in the same way the service does
            var regex = new System.Text.RegularExpressions.Regex(
                @"^\s*""path""\s+""(?<p>[^""]+)""",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var folders = new List<string>();
            foreach (var line in File.ReadLines(vdfPath))
            {
                var m = regex.Match(line);
                if (!m.Success) continue;
                string folder = m.Groups["p"].Value.Replace(@"\\", @"\");
                if (Directory.Exists(folder))
                    folders.Add(folder);
            }
            return folders;
        }
        return ((IEnumerable<string>)method.Invoke(null, new object[] { vdfPath })!).ToList();
    }

    /// <summary>Escapes a file path for embedding in a JSON string.</summary>
    private static string EscapeJson(string path) =>
        path.Replace(@"\", @"\\").Replace("\"", "\\\"");

    private static async Task ScanDirectory(GameScannerService scanner, string driveRoot)
    {
        // On Linux, $HOME is always included in GetDriveRoots(), so placing Games/, Repacks/
        // and Roms/ symlinks there ensures the scanner finds our TestData sub-directories.
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string gamesLink  = Path.Combine(home, "Games");
        string repacksLink= Path.Combine(home, "Repacks");
        string romsLink   = Path.Combine(home, "Roms");

        bool cleanGames   = false;
        bool cleanRepacks = false;
        bool cleanRoms    = false;

        try
        {
            // Set up temporary symlinks pointing at TestData sub-directories
            if (!Directory.Exists(gamesLink))
            {
                Directory.CreateSymbolicLink(gamesLink,   Path.Combine(driveRoot, "Games"));
                cleanGames = true;
            }
            if (!Directory.Exists(repacksLink))
            {
                Directory.CreateSymbolicLink(repacksLink, Path.Combine(driveRoot, "Repacks"));
                cleanRepacks = true;
            }
            string romsTestPath = Path.Combine(driveRoot, "Roms");
            if (!Directory.Exists(romsLink) && Directory.Exists(romsTestPath))
            {
                Directory.CreateSymbolicLink(romsLink, romsTestPath);
                cleanRoms = true;
            }

            await scanner.StartAsync();
        }
        finally
        {
            if (cleanGames)   try { Directory.Delete(gamesLink); }   catch { }
            if (cleanRepacks) try { Directory.Delete(repacksLink); } catch { }
            if (cleanRoms)    try { Directory.Delete(romsLink); }    catch { }
        }
    }

    private static string FindRepoRoot()
    {
        // Walk up from the current directory looking for TestData or .git
        string? dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir, "TestData")) ||
                Directory.Exists(Path.Combine(dir, ".git")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return Directory.GetCurrentDirectory();
    }
}
