using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Timing;

namespace Lunalyr.Core.Lyrics.Ranking;

/// <summary>
/// How precisely lyrics are timed (the tiers ranking compares) and whether that timing is sound. Ported from the
/// Mac's <c>LyricsFetcher</c> timing helpers and <c>LyricGapTiming.bunchedRun</c>/<c>crushedLines</c> (Lunalyr's
/// own code).
/// </summary>
public static class LyricTimingRules
{
    /// <summary>
    /// Whether a line's own word timing can be trusted: the words spell the line (letters and numbers), and each is
    /// timed inside it in order.
    /// </summary>
    public static bool ValidWordTiming(LyricLine line)
    {
        if (!line.WordTimed || line.Words.Count == 0 || !double.IsFinite(line.Start) || !double.IsFinite(line.End)
            || line.End <= line.Start)
        {
            return false;
        }
        var covered = LyricTextFolding.LettersAndNumbers(string.Concat(line.Words.Select(w => w.Text)));
        if (!string.Equals(covered, LyricTextFolding.LettersAndNumbers(line.Text), StringComparison.Ordinal))
        {
            return false;
        }
        for (var i = 0; i < line.Words.Count; i++)
        {
            var word = line.Words[i];
            if (!double.IsFinite(word.Start) || !double.IsFinite(word.End) || word.Start < line.Start - 0.05
                || word.End <= word.Start || word.End > line.End + 0.05 || (i > 0 && word.Start < line.Words[i - 1].Start))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Every sung line has valid word timing.</summary>
    public static bool HasWordTiming(LyricResult result)
    {
        if (result is not LyricResult.Synced synced)
        {
            return false;
        }
        var any = false;
        foreach (var line in synced.Lines)
        {
            if (line.IsInstrumental)
            {
                continue;
            }
            any = true;
            if (!ValidWordTiming(line))
            {
                return false;
            }
        }
        return any;
    }

    /// <summary>3 for word timing throughout, 2 for mixed timing, 1 for line timing, 0 for plain text or no sung line.</summary>
    public static int TimingTier(LyricResult result)
    {
        if (HasWordTiming(result))
        {
            return 3;
        }
        if (result is LyricResult.Synced synced)
        {
            var vocal = synced.Lines.Where(line => !line.IsInstrumental).ToList();
            if (vocal.Any(ValidWordTiming))
            {
                return 2;
            }
            return vocal.Count == 0 ? 0 : 1;
        }
        return 0;
    }

    /// <summary>The timing matches are ranked by: 2 for word-by-word or mixed timing (ranked together), 1 for line timing, 0 for plain text.</summary>
    public static int RankedTiming(LyricResult result) => Math.Min(TimingTier(result), 2);

    /// <summary>"Word-by-word", "Mixed timing" or "Line-synced".</summary>
    public static string Precision(IReadOnlyList<LyricLine> lines)
    {
        var vocal = lines.Where(line => !line.IsInstrumental).ToList();
        if (vocal.Count > 0 && vocal.All(ValidWordTiming))
        {
            return LyricLines.WordByWord;
        }
        return vocal.Any(ValidWordTiming) ? LyricLines.MixedTiming : LyricLines.LineSynced;
    }

    /// <summary>
    /// Structural equality, so re-running a lookup that finds the same lyrics does not restart what is on screen.
    /// Every field of every line counts, translations included.
    /// </summary>
    public static bool SameResult(LyricResult? a, LyricResult? b) => Equals(a, b);

    /// <summary>Whether timed lyrics race through lines (<see cref="TimingFault(LyricResult)"/>).</summary>
    public static bool HasTimingFault(LyricResult result) => TimingFault(result) is not null;

    /// <summary>
    /// What is wrong with timed lyrics' timing, for the source list, or null when it is sound: a run of lines stamped
    /// faster than they could be sung, or lines whose words are crammed into a split second.
    /// </summary>
    public static string? TimingFault(LyricResult result) => result is LyricResult.Synced synced ? TimingFault(synced.Lines) : null;

    public static string? TimingFault(IReadOnlyList<LyricLine> lines)
    {
        if (LyricGapTiming.FindBunchedRun(lines) is { } run)
        {
            return $"{run.Count} lines bunched at {LyricQuery.Clock(run.Start)}";
        }
        var crushed = LyricGapTiming.CrushedLines(lines);
        if (crushed.Count == 0)
        {
            return null;
        }
        return crushed.Count == 1
            ? $"a line crammed at {LyricQuery.Clock(crushed[0])}"
            : $"{crushed.Count} lines crammed from {LyricQuery.Clock(crushed[0])}";
    }
}
