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
        var lineLetters = LyricTextFolding.LettersAndNumbers(line.Text);
        if (!string.Equals(covered, lineLetters, StringComparison.Ordinal))
        {
            return false;
        }
        // Phrase-token rule (12 Nov 2026, now on the Mac too): tokens that each span a whole line or phrase are line
        // timing dressed as word timing. LRCLIB's Lyricsfile does this for line-synced uploads (one token per line,
        // or per space-separated phrase in Japanese), which otherwise ranked as word or mixed timing and stopped
        // sources with real word timing. A genuine one-word line still counts. The Lyricsfile parser and the
        // presentation apply the same check (IsPhraseTimed), so such lines get estimated words instead.
        if (IsPhraseTimed(line.Text, lineLetters, line.Words.Count))
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

    /// <summary>
    /// True when a line's tokens are phrases rather than words. In scripts written without spaces (Chinese,
    /// Japanese, Thai…): one token over more than two letters, or more than four letters per token on average
    /// (real word or syllable timing there runs one to three characters a token). In spaced scripts: one token over
    /// several words, or fewer than half as many tokens as words.
    /// </summary>
    public static bool IsPhraseTimed(string text, int tokens)
    {
        ArgumentNullException.ThrowIfNull(text);
        return IsPhraseTimed(text, LyricTextFolding.LettersAndNumbers(text), tokens);
    }

    /// <summary>A line marked word-timed whose tokens are phrases (<see cref="IsPhraseTimed(string, int)"/>).</summary>
    public static bool IsPhraseTimed(LyricLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return line.WordTimed && !line.IsInstrumental && IsPhraseTimed(line.Text, line.Words.Count);
    }

    private static bool IsPhraseTimed(string text, string letters, int tokens)
    {
        if (tokens <= 0)
        {
            return false;
        }
        if (LyricScript.KeepsSyllables(text))
        {
            var count = System.Globalization.StringInfo.ParseCombiningCharacters(letters).Length;
            return (tokens == 1 && count > 2) || (double)count / tokens > 4;
        }
        var words = 0;
        foreach (var part in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (LyricTextFolding.LettersAndNumbers(part).Length > 0)
            {
                words++;
            }
        }
        return (tokens == 1 && words > 1) || tokens * 2 <= words && words > 2;
    }

    /// <summary>
    /// The Mac's <c>hasWordTimingEvidence</c> (phrase-token rule of 12 Nov 2026, on both apps; spec §5.1): lyrics
    /// count as word or mixed timing only if at least one sung line, and at least a quarter of them, has two or more
    /// validly timed words. Lyrics where every timed line is a single token (LRCLIB Lyricsfile rows for line-synced
    /// uploads, plus a few genuinely one-word lines) carry no word timing at all and rank as line timing.
    /// </summary>
    static bool HasRealWordEvidence(IEnumerable<LyricLine> lines)
    {
        // At least a quarter of the sung lines (and at least one) must carry two or more real word tokens: a couple
        // of space-split decorative lines in otherwise line-timed lyrics (晴天's bopomofo solfège) aren't word timing.
        int sung = 0, real = 0;
        foreach (var line in lines)
        {
            if (line.IsInstrumental)
            {
                continue;
            }
            sung++;
            if (line.Words.Count >= 2 && ValidWordTiming(line))
            {
                real++;
            }
        }
        return real > 0 && real * 4 >= sung;
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
        return any && HasRealWordEvidence(synced.Lines);
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
            if (vocal.Any(ValidWordTiming) && HasRealWordEvidence(vocal))
            {
                return 2;
            }
            return vocal.Count == 0 ? 0 : 1;
        }
        return 0;
    }

    /// <summary>
    /// The timing matches are ranked by: 2 for word-by-word or mixed timing (ranked together), 1 for line timing, 0 for
    /// plain text. Word stamps a tool spread over each line (<see cref="LyricSpreadTiming"/>) rank as the line timing they
    /// are (the Mac's 4c0d3ce); <c>CouldBeat</c> weighs the same tier.
    /// </summary>
    public static int RankedTiming(LyricResult result)
    {
        var tier = Math.Min(TimingTier(result), 2);
        return tier == 2 && LyricSpreadTiming.HasEvenlySpreadWordTiming(result) ? 1 : tier;
    }

    /// <summary>"Word-by-word", "Mixed timing" or "Line-synced".</summary>
    public static string Precision(IReadOnlyList<LyricLine> lines)
    {
        var vocal = lines.Where(line => !line.IsInstrumental).ToList();
        if (vocal.Count > 0 && vocal.All(ValidWordTiming) && HasRealWordEvidence(vocal))
        {
            return LyricLines.WordByWord;
        }
        return vocal.Any(ValidWordTiming) && HasRealWordEvidence(vocal) ? LyricLines.MixedTiming : LyricLines.LineSynced;
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
