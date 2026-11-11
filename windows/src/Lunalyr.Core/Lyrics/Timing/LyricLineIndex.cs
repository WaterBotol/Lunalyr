using System.Text.RegularExpressions;

namespace Lunalyr.Core.Lyrics.Timing;

/// <summary>Turns a lyric line's text into a comparison key (the ranking code's normaliser; injected).</summary>
public delegate string LyricNormalizer(string text);

/// <summary>How alike two lyric lines' texts are, 0…1 (the ranking code's similarity; injected).</summary>
public delegate double LyricSimilarity(string a, string b);

/// <summary>
/// Finds the line being sung at a time. Prepared once for a lyric result (the starts are copied into an array);
/// <see cref="LineAt"/> is a binary search and allocation-free, so it can run every frame.
/// </summary>
/// <remarks>
/// The search is re-implemented from a description of the Mac's behaviour (its lines trace back to the squashed
/// import commit): the active line is the last one whose start is at or before the time; none before the first start.
/// When the starts are not all finite and in order (a malformed import), the lines are scanned from the top and the
/// scan stops at the first start after the time, as the original forward scan did.
/// The rest of this type (held lines, mixed-timing copies) is Lunalyr's own and ported as is.
/// </remarks>
public sealed class LyricLineIndex
{
    private readonly double[] _starts;
    private readonly bool _sorted;

    public LyricLineIndex(IReadOnlyList<LyricLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        _starts = new double[lines.Count];
        _sorted = true;
        for (var i = 0; i < _starts.Length; i++)
        {
            var start = lines[i].Start;
            _starts[i] = start;
            if (!double.IsFinite(start) || (i > 0 && start < _starts[i - 1])) _sorted = false;
        }
    }

    public int Count => _starts.Length;

    /// <summary>The index of the line active at <paramref name="time"/>, or null before the first line.</summary>
    public int? LineAt(double time)
    {
        var starts = _starts.AsSpan();
        if (!_sorted)
        {
            var found = -1;
            for (var i = 0; i < starts.Length && starts[i] <= time; i++) found = i;
            return found < 0 ? null : found;
        }
        // First start after the time; the line before it is the active one.
        int low = 0, high = starts.Length;
        while (low < high)
        {
            var middle = low + ((high - low) >> 1);
            if (starts[middle] <= time) low = middle + 1;
            else high = middle;
        }
        return low == 0 ? null : low - 1;
    }

    // ---- Held lines (duets, ad-libs) -----------------------------------------------------------------------------

    /// <summary>How long a note must ring on into the next line for the line to stay.</summary>
    public const double HeldOverlap = 0.5;

    /// <summary>How long a line's words must go on into the next line when one of them only starts as the next line does.</summary>
    public const double AnswerOverlap = 0.2;

    /// <summary>
    /// A word starting this close before the next line, or after it, is sung over it: an ordinary hand-over would
    /// take the line away as it begins.
    /// </summary>
    public const double AnswerLead = 0.1;

    /// <summary>
    /// The line before <paramref name="index"/> while it is still being sung at <paramref name="time"/>: duet parts and
    /// ad-libs can overlap the line that has become active. Only supplied word timing says a line is still sounding,
    /// so line-only lyrics, whose lines end where the next begins, never overlap.
    /// </summary>
    public static int? HeldLine(int index, IReadOnlyList<LyricLine> lines, double time)
    {
        var previous = index - 1;
        if (HeldEnd(previous, lines) is not { } end || time < lines[previous].Start || time >= end) return null;
        return previous;
    }

    /// <summary>
    /// When line <paramref name="row"/>'s last word ends, if it is still being sung after the next line starts; null
    /// when it is not. Only a real overlap counts: a word that comes once the next line has started (a duet part
    /// answering, an ad-lib), sung for at least <see cref="AnswerOverlap"/>, or a note held at least
    /// <see cref="HeldOverlap"/> into the next line. A last syllable ringing on for a few tenths of a second, or
    /// timings that differ only by rounding, are an ordinary line change.
    /// </summary>
    public static double? HeldEnd(int row, IReadOnlyList<LyricLine> lines)
    {
        if (row < 0 || row + 1 >= lines.Count) return null;
        var line = lines[row];
        var next = lines[row + 1].Start;
        if (!line.WordTimed || line.IsInstrumental || line.Words.Count == 0) return null;
        var words = line.Words;
        var end = double.NegativeInfinity;
        for (var i = 0; i < words.Count; i++) end = Math.Max(end, words[i].End);
        var overlap = end - next;
        if (overlap >= HeldOverlap) return end;
        if (overlap < AnswerOverlap) return null;
        for (var i = 0; i < words.Count; i++)
        {
            if (words[i].Start >= next - AnswerLead && TextElements.HasLettersOrDigits(words[i].Text)) return end;
        }
        return null;
    }

    // ---- Mixed timing copies -------------------------------------------------------------------------------------

    private static readonly Regex BracketedAside = new(@"^\s*[（(][^）)]*[）)]\s+|\s+[（(][^）)]*[）)]\s*$",
                                                       RegexOptions.CultureInvariant);

    /// <summary>
    /// Some mixed files carry a line-timed copy just ahead of (or just after) a word-timed cue. Resolves that
    /// occurrence before either version can reach the stage: the copy is dropped, its reading and translation are kept
    /// on the word-timed line, and a line-timed line before a dropped copy is extended over the copy's slot.
    /// </summary>
    /// <param name="lines">The lines, in order.</param>
    /// <param name="normalize">The ranking code's lyric normaliser.</param>
    /// <param name="similarity">The ranking code's similarity, 0…1.</param>
    /// <param name="distribute">Spreads estimated words over a window (defaults to <see cref="EstimatedWords.Distribute"/>).</param>
    public static IReadOnlyList<LyricLine> RemovingMixedTimingCopies(IReadOnlyList<LyricLine> lines,
                                                                     LyricNormalizer normalize, LyricSimilarity similarity,
                                                                     WordDistributor? distribute = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(normalize);
        ArgumentNullException.ThrowIfNull(similarity);
        distribute ??= EstimatedWords.Distribute;
        var anyWordTimed = false;
        var anyLineTimed = false;
        foreach (var line in lines)
        {
            anyWordTimed |= line.WordTimed;
            anyLineTimed |= !line.WordTimed && !line.IsInstrumental;
        }
        if (!anyWordTimed || !anyLineTimed) return lines;

        var result = lines.ToArray();
        var copies = new HashSet<int>();
        for (var index = 0; index < lines.Count; index++)
        {
            var timed = lines[index];
            if (!timed.WordTimed) continue;
            var key = TimingKey(timed.Text, normalize);
            var main = TimingKey(BracketedAside.Replace(timed.Text, ""), normalize);
            var vocalEnd = VocalEnd(timed);
            int? duplicate = null;
            ReadOnlySpan<int> neighbours = [index - 1, index + 1];
            foreach (var candidate in neighbours)
            {
                if (candidate < 0 || candidate >= lines.Count || copies.Contains(candidate)) continue;
                var fallback = lines[candidate];
                if (fallback.WordTimed || fallback.IsInstrumental) continue;
                if (!IsCopy(fallback, candidate > index, timed, key, main, vocalEnd, normalize, similarity)) continue;
                if (duplicate is not { } kept
                    || Math.Abs(fallback.Start - timed.Start) < Math.Abs(lines[kept].Start - timed.Start))
                    duplicate = candidate;
            }
            if (duplicate is not { } copy) continue;
            copies.Add(copy);
            result[index] = result[index] with
            {
                Romanization = result[index].Romanization ?? lines[copy].Romanization,
                Translation = result[index].Translation ?? lines[copy].Translation,
            };
        }
        if (copies.Count == 0) return lines;

        var keep = new List<int>(lines.Count - copies.Count);
        for (var i = 0; i < lines.Count; i++)
            if (!copies.Contains(i)) keep.Add(i);
        for (var k = 0; k + 1 < keep.Count; k++)
        {
            int index = keep[k], next = keep[k + 1];
            if (next <= index + 1 || lines[index].EndFromNextLine != true) continue;
            double start = lines[index].Start, end = lines[next].Start;
            if (!(end > start) || !(Math.Abs(lines[index].End - lines[index + 1].Start) < 0.05)) continue;
            var extended = result[index] with { End = Math.Max(lines[index].End, end) };
            if (!extended.WordTimed && !extended.IsInstrumental)
                extended = extended with { Words = distribute(lines[index].Text, start, end) };
            result[index] = extended;
        }
        var survivors = new LyricLine[keep.Count];
        for (var i = 0; i < survivors.Length; i++) survivors[i] = result[keep[i]];
        return survivors;
    }

    private static bool IsCopy(LyricLine fallback, bool after, LyricLine timed, string key, string main, double vocalEnd,
                               LyricNormalizer normalize, LyricSimilarity similarity)
    {
        var other = TimingKey(fallback.Text, normalize);
        if ((other == key || (main.Length > 0 && other == main)) && IsTimingCopy(fallback, timed, vocalEnd)) return true;
        // A near copy: the line-timed version sits just ahead of the word-timed one (sources are synced a little
        // apart) and ends by the time it starts.
        if (fallback.EndFromNextLine != true) return false;
        if (after)
        {
            // A copy after it: the line-timed version starts while the word-timed line is still being sung (or as it
            // ends), often as one half of a line the word timing keeps whole.
            return SameLyric(fallback.Text, timed.Text, normalize, similarity, minimumShare: 0.25)
                && fallback.Start >= timed.Start - 0.3 && fallback.Start <= vocalEnd + 0.15;
        }
        return SameLyric(fallback.Text, timed.Text, normalize, similarity)
            && Math.Abs(fallback.Start - timed.Start) <= 2.0 && fallback.End <= timed.Start + 0.6;
    }

    /// <summary>
    /// The same words, give or take punctuation, a dropped "yeah" or a split phrase: "Dear Slim, I wrote you, but" /
    /// "Dear Slim, I wrote you but", "Give me yo, give me yo," / "give me yo".
    /// </summary>
    public static bool SameLyric(string a, string b, LyricNormalizer normalize, LyricSimilarity similarity,
                                 double minimumShare = 0.4)
    {
        string x = TimingKey(a, normalize), y = TimingKey(b, normalize);
        if (x == y) return true;
        var (shorter, longer) = TextElements.Count(x) <= TextElements.Count(y) ? (x, y) : (y, x);
        int shortCount = TextElements.Count(shorter), longCount = TextElements.Count(longer);
        if (shortCount >= 8 && longer.Contains(shorter, StringComparison.Ordinal)
            && (double)shortCount / longCount >= minimumShare) return true;
        return similarity(a, b) >= 0.8;
    }

    private static string TimingKey(string text, LyricNormalizer normalize)
    {
        var normalized = normalize(text);
        var key = string.Concat(normalized.Where(c => !char.IsWhiteSpace(c)));
        return key.Length == 0 ? text : key;
    }

    private static double VocalEnd(LyricLine line)
    {
        if (line.Words.Count == 0) return line.End;
        var end = double.NegativeInfinity;
        foreach (var word in line.Words) end = Math.Max(end, word.End);
        return end;
    }

    private static bool IsTimingCopy(LyricLine fallback, LyricLine timed, double vocalEnd)
    {
        var multipleSyllables = fallback.Words.Count > 1 || TextElements.KeepsSyllables(fallback.Text);
        return MixedTimingCopy(fallback.Start, fallback.End, fallback.EndFromNextLine == true, multipleSyllables,
                               timed.Start, vocalEnd);
    }

    /// <summary>
    /// Whether a line-timed line (start, end) is a copy of a word-timed cue starting at <paramref name="wordStart"/>
    /// whose singing ends at <paramref name="vocalEnd"/>: the two start within 0.85 s and overlap by at least 0.12 s,
    /// or the copy's inferred end lands right on the cue's start (a line-only copy may end exactly where the
    /// word-timed cue starts). An explicit short line or a single-syllable pickup can be a real repeat.
    /// </summary>
    public static bool MixedTimingCopy(double lineStart, double lineEnd, bool inferredEnd, bool multipleSyllables,
                                       double wordStart, double vocalEnd)
    {
        if (!double.IsFinite(lineStart) || !double.IsFinite(lineEnd) || !double.IsFinite(wordStart)
            || !double.IsFinite(vocalEnd) || Math.Abs(lineStart - wordStart) > 0.85 || lineEnd <= lineStart)
            return false;
        var overlap = Math.Min(lineEnd, vocalEnd) - Math.Max(lineStart, wordStart);
        if (overlap >= 0.12) return true;
        return inferredEnd && multipleSyllables && lineStart < wordStart && Math.Abs(lineEnd - wordStart) < 0.06;
    }
}
