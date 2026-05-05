using System;
using System.Collections.Generic;
using System.Linq;
using GameLauncher.Models;

namespace GameLauncher.Services;

/// <summary>
/// Detects Nintendo Switch achievement unlocks by interpreting game-event data
/// extracted from the Ryujinx emulator log.
///
/// <para>The Nintendo Switch has no native achievement system; this service
/// implements game-specific detection rules for each supported title using
/// the <c>ProcessPlayReport</c> events that Ryujinx writes to its log during
/// gameplay.</para>
///
/// <para>Currently supported games:</para>
/// <list type="bullet">
///   <item><b>Mario Kart 8 Deluxe</b> — cup-win achievements detected by tracking
///         which courses in each cup were completed in 1st place.  Coin-total
///         achievements are also tracked via cumulative coin counts.</item>
/// </list>
/// </summary>
public static class SwitchAchievementDetectorService
{
    // ── Mario Kart 8 Deluxe ────────────────────────────────────────────────────
    //
    // Achievement names come from the Switch-Achievements JSON.
    // Cup course codes come from the game's Translate.txt reference file.
    // "Win 1st in X Cup" = all 4 courses in the cup completed with Rank == 1.

    private static readonly Dictionary<string, string[]> Mk8dCups =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Original cups
            ["Magic Mushroom's"]           = ["Gu_FirstCircuit",  "Gu_WaterPark",    "Gu_Cake",       "Gu_DossunIseki"],
            ["Flower Power"]               = ["Gu_MarioCircuit",  "Gu_City",         "Gu_HorrorHouse","Gu_Expert"],
            ["Star Light"]                 = ["Gu_Airport",       "Gu_Ocean",        "Gu_Techno",     "Gu_SnowMountain"],
            ["Special Delivery"]           = ["Gu_Cloud",         "Gu_Desert",       "Gu_BowserCastle","Gu_RainbowRoad"],
            // Retro Egg Cup
            ["Egg Hunt"]                   = ["Dgc_YoshiCircuit", "Du_ExciteBike",   "Du_DragonRoad", "Du_MuteCity"],
            // Retro Cross Cup
            ["Cross Country"]              = ["Dgc_BabyPark",     "Dagb_CheeseLand", "Du_Woods",      "Du_Animal_Summer"],
            // Retro Shell Cup
            ["Shell Shock"]                = ["Gwii_MooMooMeadows","Gagb_MarioCircuit","Gds_PukupukuBeach","G64_KinopioHighway"],
            // Retro Banana Cup
            ["Going Bananas"]              = ["Ggc_DryDryDesert", "Gsfc_DonutsPlain3","G64_PeachCircuit","G3ds_DKJungle"],
            // Retro Leaf Cup
            ["Leaf It To Me"]              = ["Gds_WarioStadium", "Ggc_SherbetLand", "G3ds_MusicPark","G64_YoshiValley"],
            // Retro Lightning Cup
            ["Lightning Fast"]             = ["Gds_TickTockClock","G3ds_PackunSlider","Gwii_GrumbleVolcano","G64_RainbowRoad"],
            // Booster Course Pass cups (BCP wave 1)
            ["Golden Dash Cup"]            = ["Cnsw_11","Cnsw_12","Cnsw_13","Cnsw_14"],
            ["Lucky Cat Cup"]              = ["Cnsw_15","Cnsw_16","Cnsw_17","Cnsw_18"],
            ["Turnip Cup"]                 = ["Cnsw_21","Cnsw_22","Cnsw_23","Cnsw_24"],
            // BCP wave 2
            ["Paper, Sissors, Rock"]       = ["Cnsw_31","Cnsw_33","Cnsw_34","Cnsw_62"],
            ["Flying High"]                = ["Cnsw_25","Cnsw_26","Cnsw_27","Cnsw_28"],
            ["First Mii On The Moon"]      = ["Cnsw_35","Cnsw_32","Cnsw_37","Cnsw_38"],
            ["A Bit of a Fruity Taste"]    = ["Cnsw_41","Cnsw_47","Cnsw_42","Cnsw_44"],
            ["What Goes Around, Comes Around"] = ["Cnsw_55","Cnsw_43","Cnsw_36","Cnsw_45"],
            ["Light As a Feather"]         = ["Cnsw_65","Cnsw_46","Cnsw_63","Cnsw_58"],
            ["Tangfastic"]                 = ["Cnsw_48","Cnsw_53","Cnsw_52","Cnsw_61"],
            ["Pretty Nuts"]                = ["Cnsw_54","Cnsw_55","Cnsw_62","Cnsw_61"],
            ["Sonic, The Turtle"]          = ["Cnsw_45","Cnsw_46","Cnsw_47","Cnsw_48"],
            ["Propeller Cup"]              = ["Cnsw_25","Cnsw_26","Cnsw_27","Cnsw_28"],
        };

    // Coin-threshold achievements: key = achievement name, value = required total coins.
    private static readonly Dictionary<string, int> Mk8dCoinThresholds =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["500"]  = 500,
            ["1000"] = 1000,
        };

    // ── Session state ──────────────────────────────────────────────────────────

    /// <summary>
    /// Mutable per-game-session state used by <see cref="DetectNewUnlocks"/>.
    /// Create one instance per game session and keep it alive across poll intervals.
    /// </summary>
    public sealed class SessionState
    {
        /// <summary>Courses completed in 1st place this session, keyed by course code.</summary>
        public HashSet<string> CoursesWonFirstPlace { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Running total of coins accumulated across all races this session.</summary>
        public int TotalCoins { get; set; }

        /// <summary>Achievement names already toasted in this session (prevents duplicate toasts).</summary>
        public HashSet<string> AlreadyToasted { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Processes a batch of newly parsed race results and returns the names of any
    /// achievements that were just unlocked (not previously cached or toasted).
    /// </summary>
    /// <param name="gameTitle">Title of the game currently running.</param>
    /// <param name="newResults">Race results extracted from the latest log content.</param>
    /// <param name="session">Mutable session state; update this object in place.</param>
    /// <param name="alreadyUnlockedNames">
    /// Achievement names that are already recorded as unlocked in the local cache.
    /// Achievements in this set are never returned as new unlocks.
    /// </param>
    /// <param name="achievementsList">
    /// The full achievement list for this game (may be <see langword="null"/>).
    /// When provided the returned names are validated against it; when
    /// <see langword="null"/> any detected achievement is returned.
    /// </param>
    public static IReadOnlyList<string> DetectNewUnlocks(
        string                   gameTitle,
        IReadOnlyList<SwitchLogReaderService.SwitchRaceResult> newResults,
        SessionState             session,
        IReadOnlySet<string>?    alreadyUnlockedNames,
        IReadOnlyList<Achievement>? achievementsList)
    {
        if (newResults.Count == 0) return [];

        // Determine which game-specific ruleset to apply
        if (IsMarioKart8Deluxe(gameTitle))
            return DetectMk8dUnlocks(newResults, session, alreadyUnlockedNames, achievementsList);

        // No ruleset for this game — nothing to detect
        return [];
    }

    // ── Mario Kart 8 Deluxe detection ─────────────────────────────────────────

    private static bool IsMarioKart8Deluxe(string gameTitle) =>
        gameTitle.Contains("mario kart 8", StringComparison.OrdinalIgnoreCase) ||
        gameTitle.Contains("mariokart8", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> DetectMk8dUnlocks(
        IReadOnlyList<SwitchLogReaderService.SwitchRaceResult> results,
        SessionState             session,
        IReadOnlySet<string>?    alreadyCached,
        IReadOnlyList<Achievement>? achievementsList)
    {
        var newUnlocks = new List<string>();

        // Build a quick look-up of valid achievement names from the loaded list
        var validNames = achievementsList != null
            ? new HashSet<string>(
                achievementsList.Select(a => a.Name),
                StringComparer.OrdinalIgnoreCase)
            : null;

        // ── Step 1: accumulate session state ────────────────────────────────
        foreach (var r in results)
        {
            // Track 1st-place course wins
            if (r.Rank == 1 &&
                string.Equals(r.FinishReason, "Finish", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(r.Course))
            {
                session.CoursesWonFirstPlace.Add(r.Course);
            }

            // Accumulate coins
            session.TotalCoins += r.CoinNum;
        }

        // ── Step 2: check cup-win achievements ──────────────────────────────
        foreach (var (achName, courses) in Mk8dCups)
        {
            // Skip if already cached, already toasted this session, or not in
            // the loaded achievement list for this game.
            if (alreadyCached != null && alreadyCached.Contains(achName)) continue;
            if (session.AlreadyToasted.Contains(achName)) continue;
            if (validNames != null && !validNames.Contains(achName)) continue;

            // All 4 courses in the cup must have been won in 1st place
            bool cupComplete = courses.All(c => session.CoursesWonFirstPlace.Contains(c));
            if (!cupComplete) continue;

            newUnlocks.Add(achName);
            session.AlreadyToasted.Add(achName);
        }

        // ── Step 3: check coin-threshold achievements ────────────────────────
        foreach (var (achName, threshold) in Mk8dCoinThresholds)
        {
            if (alreadyCached != null && alreadyCached.Contains(achName)) continue;
            if (session.AlreadyToasted.Contains(achName)) continue;
            if (validNames != null && !validNames.Contains(achName)) continue;
            if (session.TotalCoins < threshold) continue;

            newUnlocks.Add(achName);
            session.AlreadyToasted.Add(achName);
        }

        return newUnlocks;
    }
}
