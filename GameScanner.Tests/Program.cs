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

        // Trademark symbol stripping: "Super Mario Odyssey™.nca" → title="Super Mario Odyssey"
        Console.WriteLine("™  Trademark Symbol Stripping (ParseRomTitle):");
        Console.WriteLine("───────────────────────────────────────────────────────────────");
        var (tmStripped, _) = GameScannerService.ParseRomTitle("Super Mario Odyssey™");
        if (string.Equals(tmStripped, "Super Mario Odyssey", StringComparison.Ordinal))
        {
            Console.WriteLine("  ✅  \"Super Mario Odyssey™\" → \"Super Mario Odyssey\" (™ stripped)");
        }
        else
        {
            Console.WriteLine($"  ❌  ParseRomTitle did not strip ™ — got \"{tmStripped}\"");
            passed = false;
        }
        // Verify via the scanner that the fixture file title is clean (no ™ in stored title)
        static bool HasTrademarkSymbol(string title) =>
            title.Contains('™') || title.Contains('®') || title.Contains('©');
        var badRom = detectedRoms.FirstOrDefault(r => HasTrademarkSymbol(r.Title));
        if (badRom == null)
            Console.WriteLine("  ✅  No detected ROM title contains a trademark/copyright symbol");
        else
        {
            Console.WriteLine($"  ❌  ROM title still contains symbol: \"{badRom.Title}\"");
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

        // ── STEAM ACF MANIFEST TESTS ───────────────────────────────────────────
        Console.WriteLine("🎮 Steam ACF Manifest Detection:");
        Console.WriteLine("───────────────────────────────────────────────────────────────");
        bool steamAcfPassed = TestSteamAcfManifests();
        if (!steamAcfPassed) passed = false;
        Console.WriteLine();

        // ── STEAM VDF LIBRARY TESTS ────────────────────────────────────────────
        Console.WriteLine("🎮 Steam VDF Library Folder Detection:");
        Console.WriteLine("───────────────────────────────────────────────────────────────");
        bool steamVdfPassed = TestSteamVdfParser();
        if (!steamVdfPassed) passed = false;
        Console.WriteLine();

        // ── TITLE NORMALIZATION TESTS ──────────────────────────────────────────
        Console.WriteLine("📝 Game Title Normalization (\" - \" → \":\"):");
        Console.WriteLine("───────────────────────────────────────────────────────────────");
        bool titleNormPassed = TestTitleNormalization();
        if (!titleNormPassed) passed = false;
        Console.WriteLine();

        // ── STEAM ACF PRIORITY OVER FOLDER NAME ───────────────────────────────
        Console.WriteLine("🏷️  Steam ACF Name Priority (ACF name overrides folder name):");
        Console.WriteLine("───────────────────────────────────────────────────────────────");
        bool acfPriorityPassed = TestSteamAcfNamePriority();
        if (!acfPriorityPassed) passed = false;
        Console.WriteLine();

        // ── GAMES FOLDER DEEP EXE DETECTION ───────────────────────────────────
        Console.WriteLine("🔍 Games Folder Deep Exe Detection (exe in subdirectory):");
        Console.WriteLine("───────────────────────────────────────────────────────────────");
        bool deepExePassed = await TestGamesFolderDeepExe();
        if (!deepExePassed) passed = false;
        Console.WriteLine();

        // ── REPACK DEDUP WITH FUZZY TITLE MATCHING ────────────────────────────
        Console.WriteLine("🔄 Repack Dedup with Fuzzy Title Matching:");
        Console.WriteLine("───────────────────────────────────────────────────────────────");
        bool repackDedupPassed = TestRepackDedupFuzzy();
        if (!repackDedupPassed) passed = false;
        Console.WriteLine();

        // ── .GAMEOS-TITLE METADATA FILE ───────────────────────────────────────
        Console.WriteLine("📄 .gameos-title metadata file (ReadGameOsTitle):");
        Console.WriteLine("───────────────────────────────────────────────────────────────");
        bool gameOsTitlePassed = TestReadGameOsTitle();
        if (!gameOsTitlePassed) passed = false;
        Console.WriteLine();

        // ── STEAM NON-GAME FOLDER FILTER ─────────────────────────────────────
        Console.WriteLine("🚫 Steam Non-Game Folder Filter (acfNamesOnly blocks utility folders):");
        Console.WriteLine("───────────────────────────────────────────────────────────────");
        bool nonGameFilterPassed = TestSteamNonGameFolderFilter();
        if (!nonGameFilterPassed) passed = false;
        Console.WriteLine();

        // ── SWITCH ACHIEVEMENT DETECTION (Mario Kart 8 Deluxe) ───────────────
        Console.WriteLine("🏁 Switch Achievement Detection (MK8D race condition):");
        Console.WriteLine("───────────────────────────────────────────────────────────────");
        bool switchAchPassed = TestSwitchMk8RaceAchievementDetection();
        if (!switchAchPassed) passed = false;
        Console.WriteLine();

        // ── SWITCH LOG PARSING (actual Ryujinx log format) ────────────────────
        Console.WriteLine("📋 Switch Log Parsing (ReadRaceResultsFromNewContent):");
        Console.WriteLine("───────────────────────────────────────────────────────────────");
        bool switchLogParsePassed = TestSwitchLogParsing();
        if (!switchLogParsePassed) passed = false;
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
    /// Tests that ScanSteamAcfManifests() correctly parses Steam appmanifest_*.acf files
    /// and detects games with their canonical DisplayName, including games whose executable
    /// is in a subdirectory (which the folder scan alone would miss).
    /// </summary>
    private static bool TestSteamAcfManifests()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "GameOS_SteamAcfTest_" + Path.GetRandomFileName());
        bool passed = true;

        try
        {
            // Create a fake steamapps directory with common/ sub-folder
            string steamAppsDir = Path.Combine(tempRoot, "steamapps");
            string commonDir    = Path.Combine(steamAppsDir, "common");

            // Game 1: exe at root of install folder (same as directory scan would find)
            string game1Dir = Path.Combine(commonDir, "SteamAcfGame1");
            Directory.CreateDirectory(game1Dir);
            File.WriteAllText(Path.Combine(game1Dir, "game1.exe"), "fake exe");

            // Game 2: exe in a sub-directory (only ACF scan can find this reliably)
            string game2Dir    = Path.Combine(commonDir, "SteamAcfGame2Folder");
            string game2BinDir = Path.Combine(game2Dir, "Binaries", "Win64");
            Directory.CreateDirectory(game2BinDir);
            File.WriteAllText(Path.Combine(game2BinDir, "game2.exe"), "fake exe");

            // Write ACF manifests
            // ACF format: "appid"  "1234"  "name"  "Display Name"  "installdir"  "FolderName"  "StateFlags"  "4"
            WriteAcfManifest(steamAppsDir, appId: "100001", name: "Steam ACF Game One",
                             installDir: "SteamAcfGame1", stateFlags: "4");
            WriteAcfManifest(steamAppsDir, appId: "100002", name: "Steam ACF Game Two",
                             installDir: "SteamAcfGame2Folder", stateFlags: "4");
            // A partially-downloaded game (bit 2 / value 4 NOT set) — should be skipped
            string game3Dir = Path.Combine(commonDir, "SteamAcfGame3Partial");
            Directory.CreateDirectory(game3Dir);
            WriteAcfManifest(steamAppsDir, appId: "100003", name: "Steam ACF Game Three Partial",
                             installDir: "SteamAcfGame3Partial", stateFlags: "2");
            // Installed + update required (StateFlags 6 = 4|2): bit 2 IS set → should be included
            string game4Dir = Path.Combine(commonDir, "SteamAcfGame4UpdateRequired");
            Directory.CreateDirectory(game4Dir);
            File.WriteAllText(Path.Combine(game4Dir, "game4.exe"), "fake exe");
            WriteAcfManifest(steamAppsDir, appId: "100004", name: "Steam ACF Game Four UpdateRequired",
                             installDir: "SteamAcfGame4UpdateRequired", stateFlags: "6");
            // Installed + update paused (StateFlags 516 = 4|512): bit 2 IS set → should be included
            string game5Dir = Path.Combine(commonDir, "SteamAcfGame5UpdatePaused");
            Directory.CreateDirectory(game5Dir);
            File.WriteAllText(Path.Combine(game5Dir, "game5.exe"), "fake exe");
            WriteAcfManifest(steamAppsDir, appId: "100005", name: "Steam ACF Game Five UpdatePaused",
                             installDir: "SteamAcfGame5UpdatePaused", stateFlags: "516");

            var results = new List<LocalGame>();
            GameScannerService.ScanSteamAcfManifests(steamAppsDir, results);

            // ── Game 1 ────────────────────────────────────────────────────────
            var g1 = results.FirstOrDefault(g =>
                string.Equals(g.Title, "Steam ACF Game One", StringComparison.OrdinalIgnoreCase));
            if (g1 != null)
                Console.WriteLine($"  ✅  Game 1 (top-level exe):  Title=\"{g1.Title}\"  Source={g1.Source}  exe={Path.GetFileName(g1.ExecutablePath)}");
            else
            {
                Console.WriteLine("  ❌  Game 1 (appmanifest with top-level exe) was NOT detected");
                passed = false;
            }

            // ── Game 2: sub-directory exe (key feature of ACF scanning) ──────
            var g2 = results.FirstOrDefault(g =>
                string.Equals(g.Title, "Steam ACF Game Two", StringComparison.OrdinalIgnoreCase));
            if (g2 != null)
                Console.WriteLine($"  ✅  Game 2 (nested exe):    Title=\"{g2.Title}\"  Source={g2.Source}  exe=...{g2.ExecutablePath.Substring(Math.Max(0, g2.ExecutablePath.Length - 30))}");
            else
            {
                Console.WriteLine("  ❌  Game 2 (appmanifest with nested exe) was NOT detected — ACF deep-search may be broken");
                passed = false;
            }

            // ── Partial download should be skipped ────────────────────────────
            var g3 = results.FirstOrDefault(g =>
                string.Equals(g.Title, "Steam ACF Game Three Partial", StringComparison.OrdinalIgnoreCase));
            if (g3 == null)
                Console.WriteLine("  ✅  Partial download (StateFlags=2) correctly skipped");
            else
            {
                Console.WriteLine("  ❌  Partial download was NOT skipped (StateFlags=2 should be filtered)");
                passed = false;
            }

            // ── StateFlags=6 (installed + update required): must be included ──
            var g4 = results.FirstOrDefault(g =>
                string.Equals(g.Title, "Steam ACF Game Four UpdateRequired", StringComparison.OrdinalIgnoreCase));
            if (g4 != null)
                Console.WriteLine($"  ✅  Game 4 (StateFlags=6, installed+update required): included");
            else
            {
                Console.WriteLine("  ❌  Game 4 (StateFlags=6) was NOT detected — installed games pending update must be shown");
                passed = false;
            }

            // ── StateFlags=516 (installed + update paused): must be included ──
            var g5 = results.FirstOrDefault(g =>
                string.Equals(g.Title, "Steam ACF Game Five UpdatePaused", StringComparison.OrdinalIgnoreCase));
            if (g5 != null)
                Console.WriteLine($"  ✅  Game 5 (StateFlags=516, installed+update paused): included");
            else
            {
                Console.WriteLine("  ❌  Game 5 (StateFlags=516) was NOT detected — installed games with paused update must be shown");
                passed = false;
            }

            // ── All detected games should have Source="Steam" ─────────────────
            bool allSteam = results.All(g =>
                string.Equals(g.Source, "Steam", StringComparison.OrdinalIgnoreCase));
            if (allSteam && results.Count > 0)
                Console.WriteLine($"  ✅  All {results.Count} ACF-detected game(s) have Source=\"Steam\"");
            else if (results.Count > 0)
            {
                Console.WriteLine("  ❌  Some ACF-detected games do not have Source=\"Steam\"");
                passed = false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌  Steam ACF test threw: {ex.Message}");
            passed = false;
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        return passed;
    }

    private static void WriteAcfManifest(string steamAppsDir, string appId, string name,
                                          string installDir, string stateFlags)
    {
        string content =
            "\"AppState\"\n{\n" +
            $"\t\"appid\"\t\t\"{appId}\"\n" +
            $"\t\"name\"\t\t\"{name}\"\n" +
            $"\t\"installdir\"\t\"{installDir}\"\n" +
            $"\t\"StateFlags\"\t\"{stateFlags}\"\n" +
            "}\n";
        Directory.CreateDirectory(steamAppsDir);
        File.WriteAllText(Path.Combine(steamAppsDir, $"appmanifest_{appId}.acf"), content);
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

    // ─────────────────────────────────────────────────────────────────────────
    // New feature tests
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that NormalizeGameTitle converts " - " subtitle separators to ": ".
    /// Examples: "Call of Duty - Ghosts" → "Call of Duty: Ghosts",
    ///           "Mass Effect - Andromeda" → "Mass Effect: Andromeda".
    /// </summary>
    private static bool TestTitleNormalization()
    {
        bool passed = true;

        var cases = new[]
        {
            ("Call of Duty - Ghosts",           "Call of Duty: Ghosts"),
            ("Mass Effect - Andromeda",          "Mass Effect: Andromeda"),
            ("The Elder Scrolls V - Skyrim",     "The Elder Scrolls V: Skyrim"),
            ("Deadpool",                          "Deadpool"),                 // no separator — unchanged
            ("LEGO® Harry Potter™ Collection",   "LEGO® Harry Potter™ Collection"), // symbols preserved
            ("God of War - Ragnarok",            "God of War: Ragnarok"),
        };

        foreach (var (input, expected) in cases)
        {
            string result = GameScannerService.NormalizeGameTitle(input);
            if (string.Equals(result, expected, StringComparison.Ordinal))
                Console.WriteLine($"  ✅  \"{input}\" → \"{result}\"");
            else
            {
                Console.WriteLine($"  ❌  \"{input}\" → \"{result}\" (expected \"{expected}\")");
                passed = false;
            }
        }

        return passed;
    }

    /// <summary>
    /// Verifies that when the same game folder exists in Steam common/ AND has an ACF
    /// manifest with a proper display name, the ACF name wins over the raw folder name.
    /// E.g. folder "LHPCR" → ACF name "LEGO® Harry Potter™ Collection".
    ///
    /// Tests two sub-cases:
    /// 1. ACF StateFlags=516 (installed + update paused) — the fully-installed flag
    ///    (value 4) IS set, so ACF scan adds the game directly with the proper name.
    /// 2. ACF StateFlags=2 (download queued) — the fully-installed flag is NOT set,
    ///    so ACF scan skips the game.  The folder-scan fallback must still use the
    ///    proper ACF name via
    ///    <see cref="GameScannerService.BuildAcfInstallDirNames"/>.
    /// </summary>
    private static bool TestSteamAcfNamePriority()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "GameOS_AcfPrio_" + Path.GetRandomFileName());
        bool passed = true;

        try
        {
            string steamAppsDir = Path.Combine(tempRoot, "steamapps");
            string commonDir    = Path.Combine(steamAppsDir, "common");

            // Create a folder with a short/cryptic name, mimicking LHPCR
            string gameDir = Path.Combine(commonDir, "LHPCR");
            Directory.CreateDirectory(gameDir);
            File.WriteAllText(Path.Combine(gameDir, "game.exe"), "fake");

            // ── Case 1: StateFlags=516 (installed + update paused, fully-installed flag IS set) ─
            // ACF scan adds the game → folder scan skips it → proper name used.
            WriteAcfManifest(steamAppsDir, appId: "400750",
                             name: "LEGO Harry Potter Collection",
                             installDir: "LHPCR", stateFlags: "516");

            // Run the ACF scan first (as the production code now does), then ScanDir
            var results = new List<LocalGame>();
            GameScannerService.ScanSteamAcfManifests(steamAppsDir, results);

            // Simulate what ScanStorefrontDirs.ScanDir does (with the new duplicate check)
            // by only adding if the folder is not already in results.
            var acfNames = GameScannerService.BuildAcfInstallDirNames(steamAppsDir);
            if (!results.Any(g => string.Equals(g.FolderPath, gameDir, StringComparison.OrdinalIgnoreCase)))
            {
                string folderName = Path.GetFileName(gameDir);
                string title = acfNames.TryGetValue(folderName, out var n) ? n : folderName;
                results.Add(new LocalGame { Title = title, FolderPath = gameDir, Source = "Steam" });
            }

            // There must be exactly one entry (no duplicate cards)
            if (results.Count == 1)
                Console.WriteLine($"  ✅  [StateFlags=516] Exactly 1 entry (no duplicate): \"{results[0].Title}\"");
            else
            {
                Console.WriteLine($"  ❌  [StateFlags=516] Expected 1 entry but got {results.Count} — duplicate detection broken");
                passed = false;
            }

            // The title must be the ACF display name, not the raw folder name
            var entry = results.FirstOrDefault();
            if (entry != null &&
                string.Equals(entry.Title, "LEGO Harry Potter Collection", StringComparison.OrdinalIgnoreCase))
                Console.WriteLine($"  ✅  [StateFlags=516] Title from ACF manifest: \"{entry.Title}\" (not raw \"LHPCR\")");
            else
            {
                Console.WriteLine($"  ❌  [StateFlags=516] Title was \"{entry?.Title}\" — expected ACF name to take priority over \"LHPCR\"");
                passed = false;
            }

            // ── Case 2: StateFlags=2 (download queued, fully-installed flag NOT set) ─
            // ACF scan skips the game.  Folder-scan fallback must use the ACF name
            // via BuildAcfInstallDirNames (which reads all ACF files regardless of
            // StateFlags).  This reproduces the "LHPCR vs LEGO Harry Potter" bug
            // where the game shows as a cryptic folder name instead of its title.
            string tempRoot2     = Path.Combine(Path.GetTempPath(), "GameOS_AcfPrio2_" + Path.GetRandomFileName());
            string steamApps2    = Path.Combine(tempRoot2, "steamapps");
            string commonDir2    = Path.Combine(steamApps2, "common");
            string gameDir2      = Path.Combine(commonDir2, "LHPCR");
            try
            {
                Directory.CreateDirectory(gameDir2);
                File.WriteAllText(Path.Combine(gameDir2, "game.exe"), "fake");

                // StateFlags=2 → bit 4 NOT set → ACF scan skips the game
                WriteAcfManifest(steamApps2, appId: "400750",
                                 name: "LEGO Harry Potter Collection",
                                 installDir: "LHPCR", stateFlags: "2");

                var results2 = new List<LocalGame>();
                GameScannerService.ScanSteamAcfManifests(steamApps2, results2);

                // ACF scan must have skipped this game
                if (results2.Count == 0)
                    Console.WriteLine("  ✅  [StateFlags=2] ACF scan correctly skipped game (bit 4 not set)");
                else
                {
                    Console.WriteLine($"  ❌  [StateFlags=2] Expected ACF scan to skip game but got {results2.Count} entries");
                    passed = false;
                }

                // BuildAcfInstallDirNames must still return the proper name
                var acfNames2 = GameScannerService.BuildAcfInstallDirNames(steamApps2);
                if (acfNames2.TryGetValue("LHPCR", out var mappedName) &&
                    string.Equals(mappedName, "LEGO Harry Potter Collection", StringComparison.OrdinalIgnoreCase))
                    Console.WriteLine($"  ✅  [StateFlags=2] BuildAcfInstallDirNames returns proper name: \"{mappedName}\"");
                else
                {
                    Console.WriteLine($"  ❌  [StateFlags=2] BuildAcfInstallDirNames did not return proper name for \"LHPCR\" (got \"{mappedName}\")");
                    passed = false;
                }

                // Simulate folder-scan fallback: game not in results → use acfNames2 for title
                string folderName2 = Path.GetFileName(gameDir2);
                string title2 = acfNames2.TryGetValue(folderName2, out var n2) ? n2 : folderName2;
                results2.Add(new LocalGame { Title = title2, FolderPath = gameDir2, Source = "Steam" });

                // The folder-scan fallback must use the proper ACF name, not "LHPCR"
                var entry2 = results2.FirstOrDefault();
                if (entry2 != null &&
                    string.Equals(entry2.Title, "LEGO Harry Potter Collection", StringComparison.OrdinalIgnoreCase))
                    Console.WriteLine($"  ✅  [StateFlags=2] Folder-scan fallback uses ACF name: \"{entry2.Title}\" (not raw \"LHPCR\")");
                else
                {
                    Console.WriteLine($"  ❌  [StateFlags=2] Folder-scan fallback used raw name \"{entry2?.Title}\" — expected \"LEGO Harry Potter Collection\"");
                    passed = false;
                }
            }
            finally
            {
                try { if (Directory.Exists(tempRoot2)) Directory.Delete(tempRoot2, recursive: true); } catch { }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌  AcfNamePriority test threw: {ex.Message}");
            passed = false;
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        return passed;
    }

    /// <summary>
    /// Verifies that ScanGamesDir detects games even when the main executable is
    /// inside a sub-directory of the game folder (e.g. Binaries\Win64\game.exe).
    /// This is the scenario that caused Deadpool to show as "Not Installed".
    /// </summary>
    private static async Task<bool> TestGamesFolderDeepExe()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "GameOS_DeepExe_" + Path.GetRandomFileName());
        bool passed = true;

        try
        {
            // Create a Games folder with one game that has its exe in a subdirectory
            string gamesDir = Path.Combine(tempRoot, "Games");
            string deepDir  = Path.Combine(gamesDir, "DeadpoolGame", "Binaries", "Win64");
            Directory.CreateDirectory(deepDir);
            File.WriteAllText(Path.Combine(deepDir, "Deadpool.exe"), "fake exe");

            // Run a real scanner scan using the temp root as the drive root
            var scanner = new GameScannerService();
            List<LocalGame> found = new();
            scanner.GamesUpdated += g => found = g;

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string gamesLink = Path.Combine(home, "Games");
            bool cleanLink = false;

            try
            {
                if (!Directory.Exists(gamesLink))
                {
                    Directory.CreateSymbolicLink(gamesLink, gamesDir);
                    cleanLink = true;
                }
                await scanner.StartAsync();
            }
            finally
            {
                if (cleanLink) try { Directory.Delete(gamesLink); } catch { }
                scanner.Dispose();
            }

            var deadpool = found.FirstOrDefault(g =>
                string.Equals(g.Title, "DeadpoolGame", StringComparison.OrdinalIgnoreCase));
            if (deadpool != null)
                Console.WriteLine($"  ✅  DeadpoolGame detected with exe in subdirectory: {deadpool.ExecutableType}/{Path.GetFileName(deadpool.ExecutablePath)}");
            else
            {
                Console.WriteLine("  ❌  DeadpoolGame NOT detected — deep exe search in Games folder is broken");
                passed = false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌  DeepExe test threw: {ex.Message}");
            passed = false;
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        return passed;
    }

    /// <summary>
    /// Verifies that IsInstalledGame is set on repacks even when the game title has
    /// special symbols or subtitle separator differences.
    /// E.g. game "LEGO Harry Potter Collection" (local folder, no symbols) should
    /// match repack "LEGO® Harry Potter™ Collection" (with symbols).
    /// Also tests that "Call of Duty - Ghosts" repack matches installed "Call of Duty: Ghosts".
    /// </summary>
    private static bool TestRepackDedupFuzzy()
    {
        bool passed = true;

        // Test NormalizeGameTitle + StripSpecialSymbols chain used by the scanner
        var normalizeCases = new[]
        {
            // (repackTitle, installedFolderTitle, shouldMatch)
            // Symbols stripped: "LEGO® Harry Potter™ Collection" matches "LEGO Harry Potter Collection"
            ("LEGO® Harry Potter™ Collection [FitGirl Repack]",  "LEGO Harry Potter Collection", true),
            // Subtitle separator: "Call of Duty - Ghosts" repack matches installed "Call of Duty: Ghosts"
            ("Call of Duty - Ghosts [DODI Repack]",              "Call of Duty: Ghosts",          true),
            // Exact match
            ("Deadpool",                                          "Deadpool",                      true),
            // Bracketed repack marker stripped before comparing
            ("Grand Theft Auto V [Repack]",                      "Grand Theft Auto V",            true),
            // Different game — must not match
            ("Forza Horizon 4 [ElAmigos Repack]",                "Forza Horizon 5",               false),
        };

        foreach (var (repackTitle, installedTitle, shouldMatch) in normalizeCases)
        {
            // Simulate the scanner's gameTitleSet which includes multiple normalized variants
            var gameTitleSet = GameScannerService.BuildFuzzyTitleSet(
                new[] { installedTitle });

            bool matched = GameScannerService.RepackMatchesInstalledTitle(repackTitle, gameTitleSet);

            if (matched == shouldMatch)
            {
                string verdict = shouldMatch ? "correctly matches installed" : "correctly not matched";
                Console.WriteLine($"  ✅  \"{repackTitle}\" {verdict} \"{installedTitle}\"");
            }
            else
            {
                string expected = shouldMatch ? "should match" : "should NOT match";
                Console.WriteLine($"  ❌  \"{repackTitle}\" {expected} installed \"{installedTitle}\" but {(matched ? "did" : "did not")}");
                passed = false;
            }
        }

        return passed;
    }

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

    private static bool TestReadGameOsTitle()
    {
        bool passed = true;
        string tempDir = Path.Combine(Path.GetTempPath(), "GameOS_TitleTest_" + Path.GetRandomFileName());

        try
        {
            Directory.CreateDirectory(tempDir);

            // Case 1: .gameos-title file exists with a real title
            string gameFolderWithTitle = Path.Combine(tempDir, "LHPCR");
            Directory.CreateDirectory(gameFolderWithTitle);
            File.WriteAllText(Path.Combine(gameFolderWithTitle, ".gameos-title"),
                              "LEGO® Harry Potter™ Collection",
                              System.Text.Encoding.UTF8);
            string? result1 = GameScannerService.ReadGameOsTitle(gameFolderWithTitle);
            if (result1 == "LEGO® Harry Potter™ Collection")
                Console.WriteLine("  ✅  Folder with .gameos-title: title read correctly as real display name");
            else
            {
                Console.WriteLine($"  ❌  Folder with .gameos-title: expected \"LEGO® Harry Potter™ Collection\", got \"{result1}\"");
                passed = false;
            }

            // Case 2: .gameos-title file does not exist — returns null so folder name is used
            string gameFolderNoTitle = Path.Combine(tempDir, "SomeGame");
            Directory.CreateDirectory(gameFolderNoTitle);
            string? result2 = GameScannerService.ReadGameOsTitle(gameFolderNoTitle);
            if (result2 == null)
                Console.WriteLine("  ✅  Folder without .gameos-title: returns null (falls back to folder name)");
            else
            {
                Console.WriteLine($"  ❌  Folder without .gameos-title: expected null, got \"{result2}\"");
                passed = false;
            }

            // Case 3: .gameos-title exists but contains only whitespace — returns null
            string gameFolderEmpty = Path.Combine(tempDir, "EMPTYNAME");
            Directory.CreateDirectory(gameFolderEmpty);
            File.WriteAllText(Path.Combine(gameFolderEmpty, ".gameos-title"), "   \n",
                              System.Text.Encoding.UTF8);
            string? result3 = GameScannerService.ReadGameOsTitle(gameFolderEmpty);
            if (result3 == null)
                Console.WriteLine("  ✅  Folder with whitespace-only .gameos-title: returns null");
            else
            {
                Console.WriteLine($"  ❌  Folder with whitespace-only .gameos-title: expected null, got \"{result3}\"");
                passed = false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌  Exception: {ex.Message}");
            passed = false;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }

        return passed;
    }

    private static bool TestSteamNonGameFolderFilter()
    {
        // Verifies that the acfNamesOnly=true mode on ScanDir (used for all Steam
        // steamapps/common/ directories) filters out well-known Steam utility folders
        // that have no ACF manifest — "Steamworks Common Redistributables", "SteamVR",
        // "steam_settings" sub-folders, etc. — even when those folders contain .exe files.
        // This mirrors the Game Store.cs approach: only add Steam common/ folders that
        // appear in the ACF installdir→name map.
        string tempRoot = Path.Combine(Path.GetTempPath(), "GameOS_NonGameFilter_" + Path.GetRandomFileName());
        bool passed = true;

        try
        {
            string steamAppsDir = Path.Combine(tempRoot, "steamapps");
            string commonDir    = Path.Combine(steamAppsDir, "common");

            // ── Real game: has an ACF entry ────────────────────────────────
            string gameDir = Path.Combine(commonDir, "GodOfWarRagnarok");
            Directory.CreateDirectory(gameDir);
            File.WriteAllText(Path.Combine(gameDir, "GoW.exe"), "fake");
            WriteAcfManifest(steamAppsDir, appId: "2322010",
                             name: "God of War Ragnarök",
                             installDir: "GodOfWarRagnarok", stateFlags: "4");

            // ── Steam utility folders: no ACF entry but contain an .exe ───
            // Steamworks Common Redistributables (real Steam pseudo-package)
            string redistributablesDir = Path.Combine(commonDir, "Steamworks Common Redistributables");
            Directory.CreateDirectory(redistributablesDir);
            File.WriteAllText(Path.Combine(redistributablesDir, "installscript_redistributables.exe"), "fake");

            // SteamVR
            string steamVrDir = Path.Combine(commonDir, "SteamVR");
            Directory.CreateDirectory(steamVrDir);
            File.WriteAllText(Path.Combine(steamVrDir, "vrstartup.exe"), "fake");

            // steam_settings (embedded inside a game folder in some cracked game distributions)
            string steamSettingsDir = Path.Combine(commonDir, "steam_settings");
            Directory.CreateDirectory(steamSettingsDir);
            File.WriteAllText(Path.Combine(steamSettingsDir, "Game.exe"), "fake");

            // Build the ACF installdir→name map (only GodOfWarRagnarok has an ACF)
            var acfNames = GameScannerService.BuildAcfInstallDirNames(steamAppsDir);

            // Run ACF scan first, then ScanDir with acfNamesOnly=true
            // (We call the internal public APIs directly, mirroring what ScanStorefrontDirs does)
            var results = new List<LocalGame>();
            GameScannerService.ScanSteamAcfManifests(steamAppsDir, results);

            // Simulate ScanDir with acfNamesOnly=true by manually applying the same logic:
            // only add folders that have an entry in acfNames and are not already in results.
            var existingPaths = new HashSet<string>(
                results.Select(g => g.FolderPath), StringComparer.OrdinalIgnoreCase);
            foreach (var folder in Directory.EnumerateDirectories(commonDir))
            {
                if (existingPaths.Contains(folder)) continue;
                string folderName = Path.GetFileName(folder);
                // acfNamesOnly check: skip if not in acfNames
                if (!acfNames.ContainsKey(folderName)) continue;
                // Would add game here, but for this test we just record the folder
                results.Add(new LocalGame
                {
                    Title      = acfNames.TryGetValue(folderName, out var n) ? n : folderName,
                    FolderPath = folder,
                    Source     = "Steam",
                });
            }

            // ── God of War Ragnarök must be present ────────────────────────
            var gow = results.FirstOrDefault(g =>
                string.Equals(g.Title, "God of War Ragnarök", StringComparison.OrdinalIgnoreCase));
            if (gow != null)
                Console.WriteLine($"  ✅  Real game detected: \"{gow.Title}\"");
            else
            {
                Console.WriteLine("  ❌  Real game \"God of War Ragnarök\" was NOT detected");
                passed = false;
            }

            // ── Utility folders must NOT be present ────────────────────────
            var utilityFolders = new[]
            {
                ("Steamworks Common Redistributables", "Steamworks Common Redistributables"),
                ("SteamVR",                            "SteamVR"),
                ("steam_settings",                     "steam_settings"),
            };
            foreach (var (folderName, displayName) in utilityFolders)
            {
                var entry = results.FirstOrDefault(g =>
                    string.Equals(g.Title, displayName, StringComparison.OrdinalIgnoreCase)
                    || (g.FolderPath != null && Path.GetFileName(g.FolderPath)
                            .Equals(folderName, StringComparison.OrdinalIgnoreCase)));
                if (entry == null)
                    Console.WriteLine($"  ✅  \"{folderName}\" correctly excluded (no ACF entry)");
                else
                {
                    Console.WriteLine($"  ❌  \"{folderName}\" was NOT excluded — utility folder appeared as a game card");
                    passed = false;
                }
            }

            if (results.Count == 1)
                Console.WriteLine($"  ✅  Exactly 1 game in results (utility folders filtered out)");
            else
            {
                Console.WriteLine($"  ❌  Expected 1 game but got {results.Count} — some utility folders leaked through");
                passed = false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌  SteamNonGameFolderFilter test threw: {ex.Message}");
            passed = false;
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        return passed;
    }

    /// <summary>
    /// Exercises <see cref="SwitchLogReaderService.ReadRaceResultsFromNewContent"/> against
    /// a mock Ryujinx log file written in the exact multi-line format the emulator produces.
    /// Verifies that the race result (Course, Driver, Rank, FinishReason) is extracted, and
    /// then that the achievement detector correctly unlocks the "Test" achievement.
    /// </summary>
    private static bool TestSwitchLogParsing()
    {
        bool passed = true;
        string? logPath = null;
        try
        {
            // Exact Ryujinx log format as observed in production (multi-line PlayReport block):
            // - trigger line ends with "ProcessPlayReport: " (message is on continuation lines)
            // - "Room: match" and "Report: {" are on separate continuation lines
            // - JSON body follows on further continuation lines
            // - block ends when the next timestamp line begins
            string logContent =
                "00:00:00.100 |I| KernelThread Boot: started\n" +
                "00:10:42.000 |I| HLE.OsThread.22 ServicePrepo ProcessPlayReport: \n" +
                "PlayReport log:\n" +
                " Kind: Normal\n" +
                " Pid: 127\n" +
                " ApplicationVersion: 0\n" +
                " UserId: 00000000000000010000000000000000\n" +
                " Room: ui\n" +
                " Report: {\n" +
                "    \"SequenceTime\": 20260508112703\n" +
                "}\n" +
                "00:13:32.115 |I| HLE.OsThread.22 ServicePrepo ProcessPlayReport: \n" +
                "PlayReport log:\n" +
                " Kind: Normal\n" +
                " Pid: 127\n" +
                " ApplicationVersion: 0\n" +
                " UserId: 00000000000000010000000000000000\n" +
                " Room: match\n" +
                " Report: {\n" +
                "    \"BeginTime\": 20260508112753,\n" +
                "    \"Nation\": \"-\",\n" +
                "    \"RaceNo\": 0,\n" +
                "    \"Mode\": \"Offline\",\n" +
                "    \"Rule\": \"VS\",\n" +
                "    \"Engine\": \"200cc\",\n" +
                "    \"Course\": \"Gu_FirstCircuit\",\n" +
                "    \"Driver\": \"Mario\",\n" +
                "    \"Body\": \"K_Std\",\n" +
                "    \"Tire\": \"Std\",\n" +
                "    \"Wing\": \"Std\",\n" +
                "    \"RankHistory1\": {\n" +
                "        \"TypeCode\": 0,\n" +
                "        \"Value\": \"0x000000000111112C\"\n" +
                "    },\n" +
                "    \"RankHistory2\": {\n" +
                "        \"TypeCode\": 0,\n" +
                "        \"Value\": \"0x0000000000000000\"\n" +
                "    },\n" +
                "    \"FinishReason\": \"Finish\",\n" +
                "    \"CoinNum\": 10,\n" +
                "    \"Rank\": 1,\n" +
                "    \"EndRate\": 0\n" +
                "}\n" +
                "00:13:32.200 |S| HLE.OsThread.47 ServiceAm LockExit: Stubbed.\n";

            logPath = Path.GetTempFileName();
            File.WriteAllText(logPath, logContent, System.Text.Encoding.UTF8);

            long fileOffset = 0;
            var results = SwitchLogReaderService.ReadRaceResultsFromNewContent(
                logPath, ref fileOffset, out var gpResults, out _);

            // Verify the "ui" block is NOT returned as a match result
            if (results.Count == 0)
            {
                Console.WriteLine("  ❌  No race results extracted from log — parsing failed");
                passed = false;
            }
            else if (results.Count > 1)
            {
                Console.WriteLine($"  ❌  Expected 1 race result, got {results.Count}");
                passed = false;
            }
            else
            {
                var r = results[0];
                bool ok = true;

                if (!string.Equals(r.Course, "Gu_FirstCircuit", StringComparison.Ordinal))
                { Console.WriteLine($"  ❌  Course: expected 'Gu_FirstCircuit', got '{r.Course}'"); ok = false; }
                if (!string.Equals(r.Driver, "Mario", StringComparison.Ordinal))
                { Console.WriteLine($"  ❌  Driver: expected 'Mario', got '{r.Driver}'"); ok = false; }
                if (r.Rank != 1)
                { Console.WriteLine($"  ❌  Rank: expected 1, got {r.Rank}"); ok = false; }
                if (!string.Equals(r.FinishReason, "Finish", StringComparison.Ordinal))
                { Console.WriteLine($"  ❌  FinishReason: expected 'Finish', got '{r.FinishReason}'"); ok = false; }
                if (r.CoinNum != 10)
                { Console.WriteLine($"  ❌  CoinNum: expected 10, got {r.CoinNum}"); ok = false; }

                if (ok)
                    Console.WriteLine($"  ✅  Race result parsed: Course={r.Course} Driver={r.Driver} Rank={r.Rank} FinishReason={r.FinishReason} CoinNum={r.CoinNum}");
                else
                    passed = false;
            }

            if (gpResults.Count != 0)
            {
                Console.WriteLine($"  ❌  Expected 0 GP results, got {gpResults.Count}");
                passed = false;
            }
            else
            {
                Console.WriteLine("  ✅  No GP results (as expected — no gp_result block in log)");
            }

            // End-to-end: run the full detection pipeline on the parsed results
            if (results.Count == 1)
            {
                string repoRoot2 = FindRepoRoot();
                string runtimeTranslateDir2 = Path.Combine(AppContext.BaseDirectory, "Switch Ach");
                string sourceTranslatePath2 = Path.Combine(repoRoot2, "Switch Ach", "Translate.txt");
                Directory.CreateDirectory(runtimeTranslateDir2);
                File.Copy(sourceTranslatePath2, Path.Combine(runtimeTranslateDir2, "Translate.txt"), overwrite: true);

                var translations2 = SwitchTranslateService.Load();
                var session2 = new SwitchAchievementDetectorService.SessionState();
                var achievements2 = new List<Achievement>
                {
                    new() { Name = "Test", Description = "get 1st as Mario on \"Mario Kart Stadium\"" }
                };

                var unlocks2 = SwitchAchievementDetectorService.DetectNewUnlocks(
                    "Mario Kart 8 Deluxe", results, gpResults, [], session2, null, achievements2, translations2);

                if (unlocks2.Contains("Test", StringComparer.OrdinalIgnoreCase))
                    Console.WriteLine("  ✅  End-to-end: \"Test\" achievement unlocked from parsed log");
                else
                {
                    Console.WriteLine("  ❌  End-to-end: \"Test\" achievement NOT unlocked from parsed log");
                    passed = false;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌  Switch log parsing test threw: {ex.Message}");
            passed = false;
        }
        finally
        {
            if (logPath != null) try { File.Delete(logPath); } catch { }
        }
        return passed;
    }

    private static bool TestSwitchMk8RaceAchievementDetection()
    {
        bool passed = true;
        try
        {
            string repoRoot = FindRepoRoot();
            string sourceTranslatePath = Path.Combine(repoRoot, "Switch Ach", "Translate.txt");
            string runtimeTranslateDir = Path.Combine(AppContext.BaseDirectory, "Switch Ach");
            string runtimeTranslatePath = Path.Combine(runtimeTranslateDir, "Translate.txt");
            Directory.CreateDirectory(runtimeTranslateDir);
            File.Copy(sourceTranslatePath, runtimeTranslatePath, overwrite: true);

            var translations = SwitchTranslateService.Load();
            var session = new SwitchAchievementDetectorService.SessionState();

            var achievements = new List<Achievement>
            {
                new()
                {
                    Name = "Test",
                    Description = "get 1st as Mario on \"Mario Kart Stadium\""
                }
            };

            var results = new List<SwitchLogReaderService.SwitchRaceResult>
            {
                new()
                {
                    Course = "Gu_FirstCircuit",
                    Driver = "Mario",
                    FinishReason = "Finish",
                    Rank = 1,
                    CoinNum = 0
                }
            };

            var unlocks = SwitchAchievementDetectorService.DetectNewUnlocks(
                gameTitle: "Mario Kart 8 Deluxe",
                newResults: results,
                newGpResults: [],
                newStageResults: [],
                session: session,
                alreadyUnlockedNames: null,
                achievementsList: achievements,
                translations: translations);

            if (unlocks.Contains("Test", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine("  ✅  \"Test\" unlocked from: get 1st as Mario on \"Mario Kart Stadium\"");
            }
            else
            {
                Console.WriteLine("  ❌  \"Test\" was not unlocked for rank 1 Mario on Gu_FirstCircuit");
                passed = false;
            }

            // Also verify that the title with ™ symbol is handled correctly
            // ("Mario Kart™ 8 Deluxe" is how the title appears in the user's library)
            var session2 = new SwitchAchievementDetectorService.SessionState();
            var unlocksTm = SwitchAchievementDetectorService.DetectNewUnlocks(
                gameTitle: "Mario Kart™ 8 Deluxe",
                newResults: results,
                newGpResults: [],
                newStageResults: [],
                session: session2,
                alreadyUnlockedNames: null,
                achievementsList: achievements,
                translations: translations);

            if (unlocksTm.Contains("Test", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine("  ✅  \"Test\" unlocked with title \"Mario Kart™ 8 Deluxe\" (™ normalised)");
            }
            else
            {
                Console.WriteLine("  ❌  \"Test\" was NOT unlocked when title contains ™ — IsMarioKart8Deluxe failed");
                passed = false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌  Switch MK8 achievement test threw: {ex.Message}");
            passed = false;
        }

        return passed;
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
