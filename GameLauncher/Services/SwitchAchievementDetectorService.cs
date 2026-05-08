using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using GameLauncher.Models;

namespace GameLauncher.Services;

/// <summary>
/// Detects Nintendo Switch achievement unlocks by combining Ryujinx log data with the
/// universal translation table in <c>Switch Ach/Translate.txt</c>.
///
/// <para>Pipeline:</para>
/// <list type="number">
///   <item>Raw values from the log (e.g. <c>"Cup": "Kinoko"</c>) are
///         <b>translated</b> to clean display names using
///         <see cref="SwitchTranslateService"/> (e.g. <c>Kinoko</c> →
///         <c>Mushroom Cup</c>).</item>
///   <item>The clean names are matched against each achievement's
///         <c>Description</c> field to determine which achievements have been
///         earned — no achievement names or raw codes are hardcoded here.</item>
/// </list>
///
/// <para>Currently supported games:</para>
/// <list type="bullet">
///   <item><b>Mario Kart 8 Deluxe</b>
///     <list type="bullet">
///       <item>Cup wins — primary: <c>gp_result</c> block (cup rank 1);
///             fallback: all 4 courses in the cup individually won in 1st
///             place (for logs that pre-date the gp_result event).</item>
///       <item>Coin totals — description pattern <c>"N Coins Total"</c>.</item>
///     </list>
///   </item>
/// </list>
/// </summary>
public static class SwitchAchievementDetectorService
{
    // ── Session state ──────────────────────────────────────────────────────────

    /// <summary>
    /// Mutable per-game-session state used by <see cref="DetectNewUnlocks"/>.
    /// Create one instance per game session and keep it alive across poll intervals.
    /// </summary>
    public sealed class SessionState
    {
        /// <summary>Raw course codes completed in 1st place this session.</summary>
        public HashSet<string> CoursesWonFirstPlace { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Running total of coins accumulated across all races this session.</summary>
        public int TotalCoins { get; set; }

        /// <summary>Achievement names already toasted this session (prevents duplicates).</summary>
        public HashSet<string> AlreadyToasted { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Processes newly parsed race and Grand Prix results and returns the names of any
    /// achievements just unlocked (not previously cached or toasted this session).
    /// </summary>
    /// <param name="gameTitle">Title of the game currently running.</param>
    /// <param name="newResults">Race results from the latest log content.</param>
    /// <param name="newGpResults">Grand Prix cup results from the latest log content.</param>
    /// <param name="session">Mutable session state; updated in place.</param>
    /// <param name="alreadyUnlockedNames">Achievement names already in the local cache.</param>
    /// <param name="achievementsList">Full achievement list for this game (may be null).</param>
    /// <param name="translations">
    /// Parsed Translate.txt data.  Pass <see langword="null"/> only as a last resort;
    /// detection will be disabled without it.
    /// </param>
    public static IReadOnlyList<string> DetectNewUnlocks(
        string                                                    gameTitle,
        IReadOnlyList<SwitchLogReaderService.SwitchRaceResult>    newResults,
        IReadOnlyList<SwitchLogReaderService.SwitchGpResult>      newGpResults,
        SessionState                                              session,
        IReadOnlySet<string>?                                     alreadyUnlockedNames,
        IReadOnlyList<Achievement>?                               achievementsList,
        SwitchTranslateService.SwitchTranslations?                translations)
    {
        if (newResults.Count == 0 && newGpResults.Count == 0) return [];
        if (translations == null) return [];

        if (IsMarioKart8Deluxe(gameTitle))
            return DetectMk8dUnlocks(newResults, newGpResults, session,
                                     alreadyUnlockedNames, achievementsList, translations);

        return [];
    }

    // ── Mario Kart 8 Deluxe ────────────────────────────────────────────────────

    private static bool IsMarioKart8Deluxe(string gameTitle)
    {
        // Strip trademark/copyright symbols that may appear in stored game titles
        // (e.g. "Mario Kart™ 8 Deluxe" → "Mario Kart 8 Deluxe") before comparison.
        string normalized = gameTitle
            .Replace("™", "")
            .Replace("®", "")
            .Replace("©", "");
        return normalized.Contains("mario kart 8", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("mariokart8",   StringComparison.OrdinalIgnoreCase);
    }

    // Matches achievement descriptions of the form "N Coins Total" (e.g. "100 Coins Total").
    private static readonly Regex _coinTotalRegex =
        new(@"^(\d+)\s+coins\s+total\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static IReadOnlyList<string> DetectMk8dUnlocks(
        IReadOnlyList<SwitchLogReaderService.SwitchRaceResult> results,
        IReadOnlyList<SwitchLogReaderService.SwitchGpResult>   gpResults,
        SessionState                                           session,
        IReadOnlySet<string>?                                  alreadyCached,
        IReadOnlyList<Achievement>?                            achievementsList,
        SwitchTranslateService.SwitchTranslations              translations)
    {
        var newUnlocks = new List<string>();

        // Candidates: achievements not yet cached and not yet toasted this session
        IReadOnlyList<Achievement> candidates = achievementsList != null
            ? achievementsList
                .Where(a => (alreadyCached == null || !alreadyCached.Contains(a.Name))
                         && !session.AlreadyToasted.Contains(a.Name))
                .ToList()
            : [];

        // ── Step 1: accumulate session state from individual race results ────────
        foreach (var r in results)
        {
            if (r.Rank == 1 &&
                string.Equals(r.FinishReason, "Finish", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(r.Course))
            {
                session.CoursesWonFirstPlace.Add(r.Course);

                // Detect race-specific achievements such as:
                // "get 1st as Mario on \"Mario Kart Stadium\""
                string cleanCourse = translations.Translate(r.Course);
                string cleanDriver = string.IsNullOrEmpty(r.Driver) ? "" : translations.Translate(r.Driver);
                foreach (var ach in candidates)
                {
                    if (session.AlreadyToasted.Contains(ach.Name)) continue;
                    if (!IsFirstAsDriverOnCourseDescription(ach.Description, cleanDriver, cleanCourse)) continue;

                    newUnlocks.Add(ach.Name);
                    session.AlreadyToasted.Add(ach.Name);
                }
            }

            session.TotalCoins += r.CoinNum;
        }

        // ── Step 2: cup wins via gp_result (primary) ─────────────────────────────
        // The game fires one gp_result block per completed Grand Prix with the
        // overall cup rank.  Rank 1 = won the cup.
        foreach (var gp in gpResults)
        {
            if (gp.Rank != 1 || string.IsNullOrEmpty(gp.Cup)) continue;

            // Translate raw cup code → clean name (e.g. "Kinoko" → "Mushroom Cup")
            string cleanCup = translations.Translate(gp.Cup);

            foreach (var ach in candidates)
            {
                if (session.AlreadyToasted.Contains(ach.Name)) continue;
                if (!IsWinCupDescription(ach.Description, cleanCup)) continue;

                newUnlocks.Add(ach.Name);
                session.AlreadyToasted.Add(ach.Name);
            }
        }

        // ── Step 3: cup wins via per-race tracking (fallback) ─────────────────────
        // For logs that do not include a gp_result event, award the cup achievement
        // when all courses in a cup have been individually won in 1st place.
        foreach (string cleanCupName in translations.CupNames)
        {
            var courses = translations.GetCupCourses(cleanCupName);
            if (courses.Count == 0) continue;
            if (!courses.All(c => session.CoursesWonFirstPlace.Contains(c))) continue;

            foreach (var ach in candidates)
            {
                if (session.AlreadyToasted.Contains(ach.Name)) continue;
                if (!IsWinCupDescription(ach.Description, cleanCupName)) continue;

                newUnlocks.Add(ach.Name);
                session.AlreadyToasted.Add(ach.Name);
            }
        }

        // ── Step 4: coin-total achievements ──────────────────────────────────────
        // Description pattern: "N Coins Total" (e.g. "100 Coins Total").
        foreach (var ach in candidates)
        {
            if (session.AlreadyToasted.Contains(ach.Name)) continue;

            var m = _coinTotalRegex.Match(ach.Description);
            if (!m.Success) continue;

            if (!int.TryParse(m.Groups[1].Value, out int threshold)) continue;
            if (session.TotalCoins < threshold) continue;

            newUnlocks.Add(ach.Name);
            session.AlreadyToasted.Add(ach.Name);
        }

        return newUnlocks;
    }

    // ── Description helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="description"/> describes
    /// winning 1st place in a cup whose clean name is <paramref name="cleanCupName"/>.
    /// Matching is case-insensitive.  The cup name must appear at the end of the
    /// description (after optional whitespace) to avoid false positives.
    /// Examples that match for cleanCupName="Mushroom Cup":
    ///   "Win 1st In Mushroom Cup", "Win 1st in Mushroom Cup".
    /// </summary>
    private static bool IsWinCupDescription(string description, string cleanCupName) =>
        description.Contains("win 1st", StringComparison.OrdinalIgnoreCase) &&
        description.TrimEnd().EndsWith(cleanCupName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="description"/> matches:
    /// "get 1st as {Driver} on {Course}" (case-insensitive),
    /// with optional double quotes around the course name.
    /// </summary>
    private static bool IsFirstAsDriverOnCourseDescription(
        string description,
        string cleanDriverName,
        string cleanCourseName)
    {
        const string Prefix = "get 1st as ";
        if (string.IsNullOrWhiteSpace(description) ||
            string.IsNullOrWhiteSpace(cleanDriverName) ||
            string.IsNullOrWhiteSpace(cleanCourseName))
            return false;

        string text = description.Trim();
        if (!text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        int onIndex = text.IndexOf(" on ", Prefix.Length, StringComparison.OrdinalIgnoreCase);
        if (onIndex < 0)
            return false;

        string driverPart = text[Prefix.Length..onIndex].Trim().Trim('"');
        string coursePart = text[(onIndex + " on ".Length)..].Trim().Trim('"');

        return driverPart.Equals(cleanDriverName, StringComparison.OrdinalIgnoreCase) &&
               coursePart.Equals(cleanCourseName, StringComparison.OrdinalIgnoreCase);
    }
}
