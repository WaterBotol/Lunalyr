using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Lyrics.Timing;

namespace Lunalyr.Shell.Lyrics;

/// <summary>
/// The steps between the lookup's choice and the screen, which <see cref="Core.Lyrics.Pipeline.LyricService"/> leaves
/// to the display layer. Ported from the Mac's <c>LyricsViewModel.presentation(of:repairingGaps:track:)</c> (Lunalyr's
/// own lines; the two lines of its outer switch that trace back to the Verso import are written here in this port's
/// own structure).
/// </summary>
public static class LyricPresentation
{
    /// <summary>
    /// Synced lyrics: credits flagged, QQ Music's line-only lyrics split at long pauses when
    /// <paramref name="repairingGaps"/> is on ("Treat long pauses as instrumental"), estimated word timing worked out
    /// afresh for the whole song, each line's word timing re-validated, and line-timed copies of word-timed lines
    /// dropped; the precision label is recomputed. Plain lyrics: leading credit lines dropped (unless that leaves
    /// nothing). Anything empty gives null.
    /// </summary>
    public static LyricResult? Prepare(LyricResult? result, LyricQuery query, bool repairingGaps, WordDistributor? distribute = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (result is LyricResult.Synced synced && synced.Lines.Count > 0)
        {
            var qq = synced.Provenance.Source.Contains("qq", StringComparison.OrdinalIgnoreCase);
            IReadOnlyList<LyricLine> flagged = LyricCredits.Flagging(synced.Lines, query.Title, query.Artist);
            if (repairingGaps && qq) flagged = QQGapRepair.Repair(flagged, distribute);
            var validated = LyricGapTiming.EstimatingWords(flagged, repairingGaps, distribute)
                .Select(line => line with { WordTimed = LyricTimingRules.ValidWordTiming(line) })
                .ToList();
            var lines = LyricLineIndex.RemovingMixedTimingCopies(validated, LyricQuery.Normalize, LyricQuery.Similarity, distribute);
            return new LyricResult.Synced(lines, synced.Provenance with { Precision = LyricTimingRules.Precision(lines) });
        }

        if (result is LyricResult.Plain plain && plain.Lines.Count > 0)
        {
            var dropped = LyricCredits.DroppingLeadingCredits(plain.Lines, query.Title, query.Artist);
            return new LyricResult.Plain(dropped.Count == 0 ? plain.Lines : dropped, plain.Provenance);
        }

        return null;
    }

    /// <summary>
    /// The readings to draw, one string per line index (the scene's shape): the whole-line reading where one is drawn
    /// whole, otherwise the words' readings joined. Empty when readings are off or the lyrics aren't synced.
    /// </summary>
    public static IReadOnlyDictionary<int, string> Readings(LyricResult? result, LyricRomanizer romanizer, RomanizationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(romanizer);
        var readings = new Dictionary<int, string>();
        if (result is not LyricResult.Synced synced || options.Display == RomanizationDisplay.Off) return readings;
        foreach (var (index, words) in romanizer.Readings(synced.Lines, options, cancellationToken))
        {
            if (index < 0 || index >= synced.Lines.Count) continue;
            var text = LyricRomanizer.LineReading(words, synced.Lines[index])
                ?? string.Join(" ", words.Select(word => word.Text.Trim()).Where(word => word.Length > 0));
            if (text.Length > 0) readings[index] = text;
        }

        return readings;
    }
}
