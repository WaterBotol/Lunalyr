// NetEase's word-timed YRC format.
//
// Provenance: the Mac's `parseYRC` (NetEaseLyricsProvider.swift) still has Verso-attributed lines (the row loop, the
// word duration cap and the line construction), so the row reading is re-implemented from this description:
//   * A row reads "[lineStartMs,lineDurationMs]" then "(wordStartMs,wordDurationMs,flag)text" pieces; a piece's text
//     runs up to the next tuple, so brackets inside the lyric ("Oh (yeah) baby") stay in place. Rows without the
//     header (NetEase's JSON credit rows) are skipped.
//   * A word holds at least 0.06 s and at most 5 s: YRC sometimes parks 30 s on a trailing "!" across a break.
//   * A row with no sung letters is an instrumental break; a zero-length row that has sung words ends with them.
// Lunalyr's own rules (credits, punctuation attachment, readings by time) are ported as written.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Lunalyr.Core.Lyrics.Parsing;

public static partial class YrcParser
{
    /// <summary>Longest a single sung word may hold; longer is bad data, not singing.</summary>
    private const double MaxWordDuration = 5.0;
    private const double MinWordDuration = 0.06;

    [GeneratedRegex(@"^\[([0-9]+),([0-9]+)\]", RegexOptions.CultureInvariant)]
    private static partial Regex RowHeader();

    [GeneratedRegex(@"\(([0-9]+),([0-9]+),-?[0-9]+\)", RegexOptions.CultureInvariant)]
    private static partial Regex WordTiming();

    public static IReadOnlyList<LyricLine> Parse(string yrc)
    {
        if (Encoding.UTF8.GetByteCount(yrc) > 2_000_000) return [];
        var lines = new List<LyricLine>();
        var inCredits = true;
        foreach (var row in LrcParser.SplitNewlines(yrc))
        {
            if (row.Length == 0) continue;
            var header = RowHeader().Match(row);
            if (!header.Success || !TextRules.TryParseDouble(header.Groups[1].ValueSpan, out var rowStartMs)
                || !TextRules.TryParseDouble(header.Groups[2].ValueSpan, out var rowLengthMs)) continue;

            var timings = WordTiming().Matches(row, header.Index + header.Length);
            var pieces = new List<TimedToken>(timings.Count);
            var text = new StringBuilder();
            var spaced = true;
            var sungUntil = 0.0;
            for (var index = 0; index < timings.Count; index++)
            {
                var timing = timings[index];
                var from = timing.Index + timing.Length;
                var to = index + 1 < timings.Count ? timings[index + 1].Index : row.Length;
                var piece = row[from..to];
                var wordStartMs = double.Parse(timing.Groups[1].ValueSpan, CultureInfo.InvariantCulture);
                var wordLengthMs = double.Parse(timing.Groups[2].ValueSpan, CultureInfo.InvariantCulture);
                text.Append(piece);
                sungUntil = Math.Max(sungUntil, (wordStartMs + wordLengthMs) / 1000);
                var glued = !spaced && !(piece.Length == 0 || char.IsWhiteSpace(piece[0]));
                if (piece.Length > 0) spaced = char.IsWhiteSpace(piece[^1]);
                var label = TextRules.TrimSpaces(piece);
                if (label.Length == 0) continue;
                var start = wordStartMs / 1000;
                var held = Math.Clamp(wordLengthMs / 1000, MinWordDuration, MaxWordDuration);
                pieces.Add(new TimedToken(label, start, start + held, glued));
            }

            var cleaned = TextRules.TrimSpaces(text.ToString());
            if (cleaned.Length == 0) continue;
            if (LyricCredits.IsCredit(cleaned, inCredits))
            {
                inCredits = true;
                var at = rowStartMs / 1000;
                lines.Add(new LyricLine { Start = at, End = at + 0.01, Text = cleaned, IsInstrumental = true, Credit = true });
                continue;
            }
            // Zero-length rows ("[0,0](0,0,0) 作词 : …") are headers, not lyrics; one that does carry sung words ends
            // with them.
            var lineStart = rowStartMs / 1000;
            var lineEnd = rowLengthMs > 0 ? (rowStartMs + rowLengthMs) / 1000 : sungUntil;
            if (!(lineEnd > lineStart)) continue;
            // Glued Latin syllables ("beau" "ti" "ful ") join into one word; Han characters stay separate tokens.
            var words = LyricScript.HasLetters(cleaned) ? LyricScript.Words(AttachingPunctuation(pieces, lineEnd)) : [];
            if (words.Count > 0) inCredits = false;
            lines.Add(new LyricLine
            {
                Start = lineStart, End = lineEnd, Text = words.Count == 0 ? "" : cleaned, Words = words,
                IsInstrumental = words.Count == 0, WordTimed = words.Count > 0,
            });
        }
        return LyricCredits.MarkingTitleHeader(lines);
    }

    /// <summary>
    /// Bare punctuation is its own piece in YRC. A mark touching a word belongs to it: before the word when it opens
    /// one ("(" "yeah" ")", "\"" "Hello" "\""), otherwise after the word it follows ("day" "!"). A mark with spaces on
    /// both sides ("Je t'aime !", "Oh - yeah") stays a token of its own, so every word reads exactly as it does in the
    /// line. A mark's own timing means little, so a word keeps its own.
    /// </summary>
    private static List<TimedToken> AttachingPunctuation(List<TimedToken> pieces, double lineEnd)
    {
        static bool Opens(string text)
        {
            foreach (var rune in text.EnumerateRunes())
            {
                if (Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.OpenPunctuation or UnicodeCategory.InitialQuotePunctuation)) return false;
            }
            return true;
        }
        var tokens = new List<TimedToken>();
        var index = 0;
        while (index < pieces.Count)
        {
            // A run of pieces with no whitespace between them: one source word.
            var next = index + 1;
            while (next < pieces.Count && pieces[next].Glued) next++;
            var run = pieces.GetRange(index, next - index);
            index = next;
            if (!run.Any(piece => LyricScript.HasLetters(piece.Text)))
            {
                var start = run[0].Start;
                var end = lineEnd > start ? Math.Min(run[^1].End, lineEnd) : run[^1].End;
                tokens.Add(new TimedToken(string.Concat(run.Select(piece => piece.Text)), start, end, false));
                continue;
            }
            var opening = "";
            var hasWord = false;
            for (var offset = 0; offset < run.Count; offset++)
            {
                var piece = run[offset];
                if (LyricScript.HasLetters(piece.Text))
                {
                    tokens.Add(new TimedToken(opening + piece.Text, piece.Start, piece.End, hasWord));
                    opening = "";
                    hasWord = true;
                }
                else if (hasWord && !(Opens(piece.Text) && run.Skip(offset + 1).Any(later => LyricScript.HasLetters(later.Text))))
                {
                    tokens[^1] = tokens[^1] with { Text = tokens[^1].Text + opening + piece.Text };
                    opening = "";
                }
                else
                {
                    opening += piece.Text;
                }
            }
            if (opening.Length > 0) tokens[^1] = tokens[^1] with { Text = tokens[^1].Text + opening };
        }
        return tokens;
    }

    /// <summary>
    /// Attaches NetEase's romanised lines (<c>romalrc</c>, timed like the LRC) to the lines they read. A YRC line can
    /// start a little after its LRC stamp, so a reading goes to the nearest line within 0.6 s, and only when that line
    /// has no reading nearer to it.
    /// </summary>
    public static IReadOnlyList<LyricLine> AttachingRomanization(string? romalrc, IReadOnlyList<LyricLine> lines, double duration)
    {
        if (string.IsNullOrEmpty(romalrc)) return lines;
        var readings = LrcParser.Parse(romalrc, duration).Where(line => !line.IsInstrumental).ToList();
        var order = Enumerable.Range(0, lines.Count).Where(i => !lines[i].IsInstrumental).OrderBy(i => lines[i].Start).ToList();
        if (readings.Count == 0 || order.Count == 0) return lines;
        var lineStarts = order.Select(i => lines[i].Start).ToArray();
        var readingStarts = readings.Select(line => line.Start).ToArray();
        var result = lines.ToList();
        for (var index = 0; index < readings.Count; index++)
        {
            var reading = readings[index];
            if (Nearest(reading.Start, lineStarts) is not { } nearestLine || !(Math.Abs(lineStarts[nearestLine] - reading.Start) <= 0.6)
                || Nearest(lineStarts[nearestLine], readingStarts) != index) continue;
            var target = order[nearestLine];
            if (result[target].Romanization is not null
                || reading.Text.ToLowerInvariant() == result[target].Text.ToLowerInvariant()) continue;
            result[target] = result[target] with { Romanization = reading.Text };
        }
        return result;
    }

    /// <summary>Index of the value closest to <paramref name="value"/> in ascending <paramref name="sorted"/>.</summary>
    private static int? Nearest(double value, double[] sorted)
    {
        if (sorted.Length == 0) return null;
        int low = 0, high = sorted.Length;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (sorted[middle] < value) low = middle + 1;
            else high = middle;
        }
        if (low == sorted.Length) return low - 1;
        return low > 0 && value - sorted[low - 1] <= sorted[low] - value ? low - 1 : low;
    }
}
