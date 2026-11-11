using System.Globalization;
using System.Text;
using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Ranking;

namespace Lunalyr.Core.Lyrics.Timing;

/// <summary>The longest run of lines stamped faster than anyone could sing them (see <see cref="LyricGapTiming.BunchedRun"/>).</summary>
public readonly record struct BunchedRun(int Count, double Start, double End);

/// <summary>
/// When singing is heard, and how long line-only lyrics are really sung: the vocal windows for the instrumental-break
/// cue, estimated word timing, and the "Treat long pauses as instrumental" inference.
/// </summary>
/// <remarks>
/// Lunalyr's own logic, ported as is, except the vocal-window filter in <see cref="Windows"/>, which is re-implemented
/// from its behaviour (those lines trace back to the squashed import commit): a line yields a window only when it is
/// sung and has words; word-timed lines span their words, others their line; windows that are not finite, start
/// before 0 or are empty are dropped; the result is ordered by start.
/// </remarks>
public static class LyricGapTiming
{
    /// <summary>The quickest singing the estimate assumes, seconds a syllable.</summary>
    public const double MinimumSecondsPerSyllable = 0.4;

    public const double CrushedSecondsPerSyllable = 0.06;
    public const double BunchedSecondsPerSyllable = 0.08;
    public const int BunchedRunLength = 5;

    /// <summary>
    /// Vocal windows for the instrumental-break cue. With <paramref name="inferringPauses"/>, a line-only lyric whose
    /// end is merely where the next LRC line starts (<see cref="LyricLine.EndFromNextLine"/>) counts as sung only for as
    /// long as its text could plausibly take, so a long break no longer looks like one long sung line. Supplied word
    /// ends and supplied line ends (TTML, YRC) are never shortened. It is off unless the caller asks: the lyrics model
    /// asks for every source's line-only lyrics except QQ's, which <see cref="QQGapRepair"/> splits instead, and one
    /// setting turns both off.
    /// </summary>
    public static VocalWindow[] Windows(IReadOnlyList<LyricLine> lines, bool inferringPauses = false)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var pace = inferringPauses ? SecondsPerSyllable(lines) : MinimumSecondsPerSyllable;
        var windows = new List<VocalWindow>(lines.Count);
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (line.IsInstrumental || line.Words.Count == 0) continue;
            double start = line.Start, end = line.End;
            if (line.WordTimed)
            {
                (start, end) = (double.PositiveInfinity, double.NegativeInfinity);
                foreach (var word in line.Words)
                {
                    start = Math.Min(start, word.Start);
                    end = Math.Max(end, word.End);
                }
            }
            // The spread words were only guessed, so their last end says nothing; the text's own estimate may shorten the line.
            else if (inferringPauses && line.EndFromNextLine == true && EndsAtNextLine(lines, index)
                     && EstimatedVocalEnd(line, pace) is { } sung)
            {
                end = Math.Min(end, sung);
            }
            if (double.IsFinite(start) && double.IsFinite(end) && start >= 0 && end > start)
                windows.Add(new VocalWindow(start, end));
        }
        // Stable, so windows with equal starts keep the lines' order.
        return [.. windows.OrderBy(w => w.Start)];
    }

    private static bool EndsAtNextLine(IReadOnlyList<LyricLine> lines, int index) =>
        index + 1 == lines.Count || Math.Abs(lines[index + 1].Start - lines[index].End) < 0.05;

    /// <summary>
    /// Estimated word timing for the lyrics without any (<see cref="LyricLine.WordTimed"/> false): each line's words
    /// fill its window, except where that window is far longer than singing the line would take at the song's own
    /// pace (<see cref="EstimatedVocalEnd"/>): the last line, which runs to the end of the track, or a line before a
    /// long break. Those words are spread over the estimate instead, so they do not light one by one across an outro.
    /// An ordinary line is never shortened: a cap for every line raced slow lines and held notes to their end long
    /// before the singing. Without <paramref name="inferringPauses"/> (the setting is off, for songs with long held
    /// notes) no line is shortened. Line starts and ends, supplied word timing and supplied line ends are never
    /// changed. Every such line is spread again here, so words saved by an older estimate are corrected.
    /// </summary>
    public static LyricLine[] EstimatingWords(IReadOnlyList<LyricLine> lines, bool inferringPauses,
                                              WordDistributor? distribute = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        distribute ??= EstimatedWords.Distribute;
        var pace = inferringPauses ? SecondsPerSyllable(lines) : MinimumSecondsPerSyllable;
        var result = new LyricLine[lines.Count];
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            result[index] = line;
            if (line.WordTimed || line.IsInstrumental || line.Words.Count == 0
                || !double.IsFinite(line.Start) || !double.IsFinite(line.End) || !(line.End > line.Start)) continue;
            var end = line.End;
            if (inferringPauses && line.EndFromNextLine == true && EndsAtNextLine(lines, index)
                && EstimatedVocalEnd(line, pace) is { } sung)
                end = sung;
            var words = distribute(line.Text, line.Start, end);
            if (words.Count > 0) result[index] = line with { Words = words };
        }
        return result;
    }

    /// <summary>
    /// The longest run of consecutive sung lines that start faster than anyone could sing them (each line's window
    /// shorter than <see cref="BunchedSecondsPerSyllable"/> a syllable, and under 0.6 s), or null when no run reaches
    /// <see cref="BunchedRunLength"/> lines. A listener-made LRC sometimes stamps a whole chorus within a few seconds;
    /// shown as it is, the lyrics race through those lines and land at the end of them.
    /// </summary>
    /// <param name="lines">The lines, in order.</param>
    /// <param name="isCredit">Whether a line is a production credit (<see cref="LyricCredits.IsCredit"/> when null):
    /// credits are often stamped a moment apart before the song starts, so they are left out.</param>
    public static BunchedRun? FindBunchedRun(IReadOnlyList<LyricLine> lines, Func<string, bool>? isCredit = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        isCredit ??= IsCredit;
        var sung = lines.Where(l => !l.IsInstrumental && !string.IsNullOrWhiteSpace(l.Text) && double.IsFinite(l.Start)
                                    && !isCredit(l.Text)).ToList();
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
                    best = new BunchedRun(count, sung[first].Start, sung[index].Start);
                runStart = null;
            }
        }
        return best;
    }

    /// <summary>
    /// Word-timed lines whose words are crammed into far less time than singing them takes (under
    /// <see cref="CrushedSecondsPerSyllable"/> a syllable, four syllables or more): a listener-made upload sometimes
    /// squeezes a line, often one with backing vocals, into a split second, and the highlight shoots to its end. The
    /// start of each, in order.
    /// </summary>
    public static List<double> CrushedLines(IReadOnlyList<LyricLine> lines, Func<string, bool>? isCredit = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        isCredit ??= IsCredit;
        var starts = new List<double>();
        foreach (var line in lines)
        {
            if (!line.WordTimed || line.IsInstrumental || line.Words.Count < 3) continue;
            double first = double.PositiveInfinity, last = double.NegativeInfinity;
            foreach (var word in line.Words)
            {
                first = Math.Min(first, word.Start);
                last = Math.Max(last, word.End);
            }
            if (!double.IsFinite(first) || !double.IsFinite(last) || isCredit(line.Text)) continue;
            var syllables = EstimatedSyllables(line.Text);
            if (syllables >= 4 && last - first < syllables * CrushedSecondsPerSyllable) starts.Add(line.Start);
        }
        return starts;
    }

    /// <summary>
    /// Where a line-only lyric has most likely finished being sung, or null when its window is not clearly longer than
    /// singing it would take. The estimate is deliberately generous (the song's own pace, at least 0.4 s a syllable,
    /// plus 3 s for a held final note, 5 to 20 s in all), and the window must be more than twice that, and over 12 s,
    /// before a break is inferred. 12 s is also the shortest window <see cref="QQGapRepair"/> splits.
    /// </summary>
    public static double? EstimatedVocalEnd(LyricLine line, double secondsPerSyllable = MinimumSecondsPerSyllable)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.IsInstrumental || !double.IsFinite(line.Start) || !double.IsFinite(line.End)) return null;
        var perSyllable = double.IsFinite(secondsPerSyllable)
            ? Math.Min(1, Math.Max(MinimumSecondsPerSyllable, secondsPerSyllable))
            : MinimumSecondsPerSyllable;
        var sung = Math.Min(20, Math.Max(5, EstimatedSyllables(line.Text) * perSyllable + 3));
        return line.End - line.Start > Math.Max(12, sung * 2) ? line.Start + sung : null;
    }

    /// <summary>
    /// The song's own pace: the median time per syllable over its compact LRC windows (1 to 8 s, three syllables or
    /// more, last line excluded), between 0.4 and 1 s. A window also holds the breath before the next line, so this
    /// errs towards a longer estimate, as slow songs need.
    /// </summary>
    public static double SecondsPerSyllable(IReadOnlyList<LyricLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var samples = new List<double>();
        for (var index = 0; index + 1 < lines.Count; index++)
        {
            var line = lines[index];
            var window = line.End - line.Start;
            if (line.EndFromNextLine != true || line.IsInstrumental || line.WordTimed || !(window >= 1) || !(window <= 8))
                continue;
            var syllables = EstimatedSyllables(line.Text);
            if (syllables >= 3) samples.Add(window / syllables);
        }
        if (samples.Count == 0) return MinimumSecondsPerSyllable;
        samples.Sort();
        return Math.Min(1, Math.Max(MinimumSecondsPerSyllable, samples[samples.Count / 2]));
    }

    /// <summary>
    /// A rough syllable count: one per Han, kana or Hangul character, one per vowel group in Latin words and one per two
    /// letters in other scripts.
    /// </summary>
    public static int EstimatedSyllables(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var folded = LyricTextFolding.Fold(text);
        var count = 0;
        int vowels = 0, others = 0;
        bool inVowel = false, inWord = false, hasAscii = false;
        var elements = StringInfo.GetTextElementEnumerator(folded);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            var first = Rune.GetRuneAt(element, 0);
            if (!Rune.IsLetter(first) && !Rune.IsNumber(first))
            {
                if (inWord) count += WordSyllables(vowels, others, hasAscii);
                (vowels, others, inVowel, inWord, hasAscii) = (0, 0, false, false, false);
                continue;
            }
            inWord = true;
            var ascii = IsAscii(element);
            hasAscii |= ascii;
            if (LyricScript.KeepsSyllables(element))
            {
                count++;
                inVowel = false;
            }
            else if (ascii)
            {
                var vowel = element.Length == 1 && "aeiouy".Contains(element[0], StringComparison.Ordinal);
                if (vowel && !inVowel) vowels++;
                inVowel = vowel;
            }
            else
            {
                others++;
                inVowel = false;
            }
        }
        if (inWord) count += WordSyllables(vowels, others, hasAscii);
        return count;
    }

    private static bool IsCredit(string text) => LyricCredits.IsCredit(text);

    private static int WordSyllables(int vowels, int others, bool hasAscii) =>
        Math.Max(vowels, hasAscii ? 1 : 0) + (others + 1) / 2;

    private static bool IsAscii(string element)
    {
        foreach (var c in element)
            if (c > 0x7F) return false;
        return true;
    }
}
