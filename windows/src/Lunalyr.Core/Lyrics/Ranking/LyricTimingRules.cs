using System.Text;

namespace Lunalyr.Core.Lyrics.Ranking;

/// <summary>A run of consecutive sung lines started faster than anyone could sing them.</summary>
/// <param name="Count">Lines in the run, including the one the quick lines lead into.</param>
/// <param name="Start">Start of the run's first line.</param>
/// <param name="End">Start of the run's last line.</param>
public readonly record struct BunchedRun(int Count, double Start, double End);

/// <summary>
/// How precisely lyrics are timed (the tiers ranking compares) and whether that timing is sound. Ported from the
/// Mac's <c>LyricsFetcher</c> timing helpers and <c>LyricGapTiming.bunchedRun</c>/<c>crushedLines</c> (Lunalyr's
/// own code).
/// </summary>
public static class LyricTimingRules
{
    /// <summary>A word-timed line squeezed under this many seconds a syllable is crammed.</summary>
    public const double CrushedSecondsPerSyllable = 0.06;

    /// <summary>Lines started under this many seconds a syllable (and under 0.6 s) are too quick to sing.</summary>
    public const double BunchedSecondsPerSyllable = 0.08;

    /// <summary>The fewest quick lines in a row that make a fault; four quick ad-libs are a stutter.</summary>
    public const int BunchedRunLength = 5;

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
            return "Word-by-word";
        }
        return vocal.Any(ValidWordTiming) ? "Mixed timing" : "Line-synced";
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
        if (FindBunchedRun(lines) is { } run)
        {
            return $"{run.Count} lines bunched at {LyricQuery.Clock(run.Start)}";
        }
        var crushed = CrushedLines(lines);
        if (crushed.Count == 0)
        {
            return null;
        }
        return crushed.Count == 1
            ? $"a line crammed at {LyricQuery.Clock(crushed[0])}"
            : $"{crushed.Count} lines crammed from {LyricQuery.Clock(crushed[0])}";
    }

    /// <summary>
    /// The longest run of consecutive sung lines that start faster than anyone could sing them (each line's window
    /// shorter than <see cref="BunchedSecondsPerSyllable"/> a syllable, and under 0.6 s), or null when no run reaches
    /// <see cref="BunchedRunLength"/> lines. A listener-made LRC sometimes stamps a whole chorus within a few seconds.
    /// </summary>
    public static BunchedRun? FindBunchedRun(IReadOnlyList<LyricLine> lines)
    {
        // Credit lines ("Lyrics by: …") are often stamped a moment apart before the song starts.
        var sung = lines.Where(line => !line.IsInstrumental && !string.IsNullOrWhiteSpace(line.Text)
            && double.IsFinite(line.Start) && !RankingCredits.IsCredit(line.Text)).ToList();
        BunchedRun? best = null;
        int? runStart = null;
        for (var index = 0; index < sung.Count; index++)
        {
            var tooQuick = false;
            if (index + 1 < sung.Count)
            {
                var window = sung[index + 1].Start - sung[index].Start;
                var limit = Math.Min(0.6, Math.Max(1, EstimatedSyllables(sung[index].Text)) * BunchedSecondsPerSyllable);
                tooQuick = window >= 0 && window < limit;
            }
            if (tooQuick)
            {
                runStart ??= index;
            }
            else if (runStart is { } first)
            {
                // The run's last line is the one the quick lines lead into.
                var count = index - first + 1;
                if (count >= BunchedRunLength && count > (best?.Count ?? 0))
                {
                    best = new BunchedRun(count, sung[first].Start, sung[index].Start);
                }
                runStart = null;
            }
        }
        return best;
    }

    /// <summary>
    /// Word-timed lines whose words are crammed into far less time than singing them takes (under
    /// <see cref="CrushedSecondsPerSyllable"/> a syllable, four syllables or more). The start of each, in order.
    /// </summary>
    public static IReadOnlyList<double> CrushedLines(IReadOnlyList<LyricLine> lines)
    {
        var starts = new List<double>();
        foreach (var line in lines)
        {
            if (!line.WordTimed || line.IsInstrumental || line.Words.Count < 3)
            {
                continue;
            }
            var first = line.Words.Min(w => w.Start);
            var last = line.Words.Max(w => w.End);
            if (!double.IsFinite(first) || !double.IsFinite(last) || RankingCredits.IsCredit(line.Text))
            {
                continue;
            }
            var syllables = EstimatedSyllables(line.Text);
            if (syllables >= 4 && last - first < syllables * CrushedSecondsPerSyllable)
            {
                starts.Add(line.Start);
            }
        }
        return starts;
    }

    /// <summary>
    /// A rough syllable count: one per Han, kana or Hangul character, one per vowel group in Latin words and one per
    /// two letters in other scripts. Ported from the Mac's <c>LyricGapTiming.estimatedSyllables</c>; a
    /// de-duplication candidate with the gap-timing workstream.
    /// </summary>
    public static int EstimatedSyllables(string text)
    {
        var folded = LyricTextFolding.Fold(text);
        var count = 0;
        var word = new List<string>();
        foreach (var cluster in LyricTextFolding.Graphemes(folded))
        {
            if (Rune.DecodeFromUtf16(cluster, out var first, out _) == System.Buffers.OperationStatus.Done
                && LyricTextFolding.IsLetterOrNumber(first))
            {
                word.Add(cluster);
            }
            else
            {
                count += CountWord(word);
                word.Clear();
            }
        }
        count += CountWord(word);
        return count;

        static int CountWord(List<string> characters)
        {
            if (characters.Count == 0)
            {
                return 0;
            }
            int vowels = 0, others = 0;
            bool inVowel = false, hasAscii = false;
            foreach (var character in characters)
            {
                var ascii = character.All(c => c < 0x80);
                hasAscii |= ascii;
                if (RankingScript.KeepsSyllables(character))
                {
                    // Counted directly below.
                    inVowel = false;
                }
                else if (ascii)
                {
                    var vowel = character.Length == 1 && "aeiouy".Contains(character[0], StringComparison.Ordinal);
                    if (vowel && !inVowel)
                    {
                        vowels++;
                    }
                    inVowel = vowel;
                }
                else
                {
                    others++;
                    inVowel = false;
                }
            }
            var syllabic = characters.Count(RankingScript.KeepsSyllables);
            return syllabic + Math.Max(vowels, hasAscii ? 1 : 0) + (others + 1) / 2;
        }
    }
}
