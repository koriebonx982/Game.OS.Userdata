using System;
using System.Collections.Generic;
using System.IO;

namespace GameLauncher.Services;

/// <summary>
/// Parses <c>Switch Ach/Translate.txt</c> — the universal translation table for Nintendo
/// Switch log data — and exposes the resulting mappings to the rest of the application.
///
/// <para>
/// The pipeline for Switch achievement detection is:
/// <list type="number">
///   <item>Read raw values from the Ryujinx log (e.g. <c>"Course": "Gu_FirstCircuit"</c>,
///         <c>"Cup": "Kinoko"</c>, <c>"Driver": "Mario"</c>).</item>
///   <item>Translate each raw value to its clean display name using this service
///         (e.g. <c>Gu_FirstCircuit</c> → <c>Mario Kart Stadium</c>,
///         <c>Kinoko</c> → <c>Mushroom Cup</c>).</item>
///   <item>Match the clean names against the <c>Description</c> fields of the loaded
///         achievement JSON to determine which achievements have been earned.</item>
/// </list>
/// </para>
///
/// <para>
/// The Translate.txt format is:
/// <code>
/// # Cup Header:      — sets the current cup context for subsequent course lines
/// RawCode = Clean Name
/// </code>
/// Lines beginning with <c>##</c> are section separators.
/// Lines beginning with a single <c>#</c> that end with the word "Cup" define a cup
/// header and group the course codes that follow under that cup name.
/// All other <c>Key = Value</c> lines contribute to the raw→clean translation map.
/// </para>
/// </summary>
public static class SwitchTranslateService
{
    // ── Parsed result ──────────────────────────────────────────────────────────

    /// <summary>
    /// Immutable snapshot of all translations parsed from Translate.txt.
    /// </summary>
    public sealed class SwitchTranslations
    {
        private readonly IReadOnlyDictionary<string, string>               _rawToClean;
        private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _cupToCourses;

        internal SwitchTranslations(
            Dictionary<string, string>       rawToClean,
            Dictionary<string, List<string>> cupToCourses)
        {
            _rawToClean = rawToClean;

            var cupMap = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in cupToCourses)
                cupMap[k] = v.AsReadOnly();
            _cupToCourses = cupMap;
        }

        /// <summary>
        /// Translates a raw log code to its clean display name.
        /// Returns the original <paramref name="raw"/> value unchanged when no
        /// mapping exists.
        /// </summary>
        public string Translate(string raw) =>
            _rawToClean.TryGetValue(raw, out string? clean) ? clean : raw;

        /// <summary>
        /// Returns the raw course codes that belong to the cup identified by
        /// <paramref name="cleanCupName"/>, or an empty list when the cup is
        /// not listed in Translate.txt.
        /// </summary>
        public IReadOnlyList<string> GetCupCourses(string cleanCupName) =>
            _cupToCourses.TryGetValue(cleanCupName, out var courses)
                ? courses
                : Array.Empty<string>();

        /// <summary>All clean cup names found in Translate.txt (e.g. "Mushroom Cup").</summary>
        public IEnumerable<string> CupNames => _cupToCourses.Keys;
    }

    // ── Public entry point ─────────────────────────────────────────────────────

    /// <summary>
    /// Loads and parses <c>Switch Ach/Translate.txt</c> from the application's
    /// base directory.  Returns an empty <see cref="SwitchTranslations"/> when the
    /// file does not exist (graceful degradation).
    /// </summary>
    public static SwitchTranslations Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Switch Ach", "Translate.txt");
        return LoadFromFile(path);
    }

    /// <summary>
    /// Loads and parses the Translate.txt file at the given path.
    /// Exposed as <see langword="internal"/> for unit-testing with a custom path.
    /// </summary>
    internal static SwitchTranslations LoadFromFile(string path)
    {
        var rawToClean   = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var cupToCourses = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(path))
            return new SwitchTranslations(rawToClean, cupToCourses);

        string? currentCup = null;

        foreach (string line in File.ReadLines(path))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            // Section separator (## ... ##) — reset cup context
            if (trimmed.StartsWith("##"))
            {
                currentCup = null;
                continue;
            }

            // Comment line
            if (trimmed.StartsWith('#'))
            {
                currentCup = TryParseCupHeader(trimmed, out string cupName) ? cupName : null;
                continue;
            }

            // Key = Value line
            int eq = trimmed.IndexOf('=');
            if (eq < 1) continue;

            string raw   = trimmed[..eq].Trim();
            string clean = trimmed[(eq + 1)..].Trim();
            if (string.IsNullOrEmpty(raw) || string.IsNullOrEmpty(clean)) continue;

            rawToClean[raw] = clean;

            // If we are inside a cup block, record this as one of its courses
            if (currentCup != null)
            {
                if (!cupToCourses.TryGetValue(currentCup, out var list))
                    cupToCourses[currentCup] = list = new List<string>();
                list.Add(raw);
            }
        }

        return new SwitchTranslations(rawToClean, cupToCourses);
    }

    // ── Cup header detection ───────────────────────────────────────────────────

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="commentLine"/> is a cup
    /// section header (e.g. <c># Mushroom Cup:</c>) and sets
    /// <paramref name="cupName"/> to the clean cup name (e.g. <c>Mushroom Cup</c>).
    ///
    /// <para>A line is treated as a cup header when — after stripping leading
    /// <c>#</c> characters, surrounding whitespace, and a trailing <c>:</c> — the
    /// result ends with the word "Cup" (case insensitive) and contains no
    /// parentheses (to exclude meta-comment lines such as
    /// <c># Cup codes (gp_result "Cup" field):</c>).</para>
    /// </summary>
    private static bool TryParseCupHeader(string commentLine, out string cupName)
    {
        cupName = "";
        string text = commentLine.TrimStart('#').Trim();
        if (text.EndsWith(':')) text = text[..^1].Trim();

        // Must end with "Cup" (case-insensitive) and contain no parentheses
        if (!text.EndsWith("Cup", StringComparison.OrdinalIgnoreCase)) return false;
        if (text.Contains('(')) return false;

        cupName = text;
        return true;
    }
}
