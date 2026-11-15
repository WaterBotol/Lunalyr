// Ported from the Mac app's LyricLayerView.swift (renderLyricLine, lineTimedLyric, readingPairs): Lunalyr's own code.
// How a line's readings, as LyricRomanizer works them out (one reading per word, or the words to show instead), reach
// a stage that sets the line in runs.

using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;

namespace Lunalyr.Core.Compositions;

/// <summary>
/// A line as a stage sets it, given its readings: the words drawn (the line's own, or with "Replace lyrics" its
/// readings), how they run together, the reading of each run, and a reading drawn once under the whole line.
/// </summary>
/// <param name="Words">The words to set: the line's own, or the readings that replace them.</param>
/// <param name="Glue">Per word of <paramref name="Words"/>: continues the previous word's run.</param>
/// <param name="Annotations">"Below lyrics": each drawn token's share of its run's reading
/// (<see cref="LyricReadingPairs.Shares"/>); a run's shares written together are its reading.</param>
/// <param name="LineReading">A reading that cannot be split by word, drawn once under the whole line.</param>
/// <param name="Replaced">The words are the line's readings.</param>
public sealed record LineReadingSet(
    IReadOnlyList<LyricWord> Words,
    IReadOnlyList<bool> Glue,
    IReadOnlyDictionary<LyricWord, string> Annotations,
    string? LineReading,
    bool Replaced)
{
    /// <summary>The reading of each drawn run of <paramref name="words"/> (glued by <paramref name="glue"/>), by run
    /// index as <see cref="EditorialSetting.Layout"/> counts runs; null when no run has one.</summary>
    public string?[]? RunReadings(IReadOnlyList<LyricWord> words, IReadOnlyList<bool> glue) =>
        LineReadings.RunReadings(Annotations, words, glue);

    /// <summary>The reading of each drawn run share by share (<see cref="LineReadings.RunShares"/>); null when no run
    /// has one.</summary>
    public ReadingShares?[]? RunShares(IReadOnlyList<LyricWord> words, IReadOnlyList<bool> glue) =>
        LineReadings.RunShares(Annotations, words, glue);
}

public static class LineReadings
{
    private static readonly IReadOnlyDictionary<LyricWord, string> None = new Dictionary<LyricWord, string>();

    /// <summary>
    /// How <paramref name="line"/> is set with <paramref name="readings"/> (LyricRomanizer's entry for it, or null)
    /// shown as <paramref name="display"/>. "Replace lyrics" sets the readings in the words' place, grouped as the
    /// line's words were (<see cref="LyricRuns.Glue(LyricLine, IReadOnlyList{LyricWord})"/>). "Below lyrics" keeps the
    /// words and gives each drawn token its share of its run's reading (<see cref="LyricReadingPairs.Shares"/>), so a
    /// run's reading can fill in as the run is sung; a reading that cannot be split by word goes once under the whole
    /// line (<see cref="LyricReadingPairs.Line"/>).
    /// </summary>
    /// <param name="lineGlue">The glue of the line's own words, when the caller has it already.</param>
    public static LineReadingSet For(LyricLine line, IReadOnlyList<LyricWord>? readings, RomanizationDisplay display,
                                     IReadOnlyList<bool>? lineGlue = null)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (display == RomanizationDisplay.Replace && readings is { Count: > 0 })
            return new LineReadingSet(readings, LyricRuns.Glue(line, readings), None, null, true);
        var glue = lineGlue ?? LyricRuns.Glue(line, line.Words);
        if (display != RomanizationDisplay.Below || readings is not { Count: > 0 })
            return new LineReadingSet(line.Words, glue, None, null, false);
        return new LineReadingSet(line.Words, glue, LyricReadingPairs.Shares(readings, line.Words, glue),
                                  LyricReadingPairs.Line(readings, line, display), false);
    }

    /// <summary>
    /// The reading of each drawn run of <paramref name="words"/>, by run index (runs of the drawn tokens, as
    /// <see cref="LyricRuns.Visible"/> and <see cref="LyricRuns.Ranges"/> count them): its tokens' shares written
    /// together. Null when no run has one.
    /// </summary>
    public static string?[]? RunReadings(IReadOnlyDictionary<LyricWord, string> annotations, IReadOnlyList<LyricWord> words,
                                         IReadOnlyList<bool> glue)
    {
        if (RunShares(annotations, words, glue) is not { } shares) return null;
        var result = new string?[shares.Length];
        for (var r = 0; r < shares.Length; r++) result[r] = shares[r]?.Text;
        return result;
    }

    /// <summary>
    /// The reading of each drawn run of <paramref name="words"/>, share by share (<see cref="ReadingShares.Of(IEnumerable{LyricWord}, IReadOnlyDictionary{LyricWord, string})"/>),
    /// by run index as <see cref="RunReadings"/> counts them. Null when no run has one.
    /// </summary>
    public static ReadingShares?[]? RunShares(IReadOnlyDictionary<LyricWord, string> annotations, IReadOnlyList<LyricWord> words,
                                              IReadOnlyList<bool> glue)
    {
        ArgumentNullException.ThrowIfNull(annotations);
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(glue);
        if (annotations.Count == 0) return null;
        var (visible, visibleGlue) = LyricRuns.Visible(words, glue);
        var ranges = LyricRuns.Ranges(visibleGlue, visible.Count);
        var result = new ReadingShares?[ranges.Count];
        var any = false;
        for (var r = 0; r < ranges.Count; r++)
        {
            var (start, end) = ranges[r];
            if (ReadingShares.Of(visible.GetRange(start, end - start), annotations) is not { } shares
                || string.IsNullOrWhiteSpace(shares.Text)) continue;
            result[r] = shares;
            any = true;
        }
        return any ? result : null;
    }
}
