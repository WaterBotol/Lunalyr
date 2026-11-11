// LRC, including enhanced (word-timed) LRC, [offset:], several timestamps on one row and bilingual rows that share a
// timestamp.
//
// Provenance: the Mac's `parseLRC` (LyricsFetcher.swift) mixes Lunalyr's rules with lines still traced to the Verso
// import (the two stamp patterns, the time conversion, the offset read and the row and marker loops). This file
// re-implements the format reading from the LRC format and this description, in its own structure, and ports
// Lunalyr's rules (credits, expansion budget, syllable gluing, shared timestamps, word validity) as written:
//   * A row is a line of the file, trimmed of spaces. It is timed by one or more "[m:ss]" stamps at its start
//     (1-3 digits of minutes, seconds 00-59, then optionally "." or ":" and 1-3 digits of fraction), which may be
//     separated by whitespace ("[00:12.00] [00:45.00]Chorus"). Its time is minutes*60 + seconds + fraction/10^digits.
//   * Enhanced rows carry "<m:ss.xx>" stamps of the same form before each word or syllable.
//   * The first "[offset:±ms]" tag (any case, its own line) shows lyrics sooner when positive: every time has
//     offset/1000 taken off, then clamps at 0.
//   * Rows without a leading stamp (metadata such as "[ar:…]", plain text) are skipped.

using System.Text;
using System.Text.RegularExpressions;
using Lunalyr.Core.Lyrics.Ranking;

namespace Lunalyr.Core.Lyrics.Parsing;

public static partial class LrcParser
{
    /// <summary>The longest LRC text read, in UTF-8 bytes.</summary>
    public const int InputLimit = 1_000_000;

    /// <summary>
    /// A row is read once for each of its timestamps. The copies, counted by their text's length plus one each, may
    /// come to at most this, so a crafted file cannot exhaust memory; the rows after that point are not read.
    /// </summary>
    public const int ExpansionLimit = 2 * InputLimit;

    private sealed class Row
    {
        /// <summary>The row's time after the offset, before clamping at zero.</summary>
        public double Stamp;
        public double Start;
        public string Text = "";
        public List<TimedToken> Tokens = [];
        public string? Romanization;
        public string? Translation;
        public bool Credit;
    }

    private readonly record struct Stamp(int Index, int Length, double Seconds);

    private readonly record struct Syllable(string Text, double Time, double? Next, bool Glued);

    [GeneratedRegex(@"^\s*\[offset:\s*([+-]?[0-9]+(?:\.[0-9]+)?)\s*\]", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex OffsetTag();

    /// <summary>
    /// The lines of an LRC file. <paramref name="trackDuration"/> ends the last line (0 or non-finite when unknown:
    /// it then runs four seconds). <paramref name="reader"/> tells readings from translations on shared timestamps.
    /// </summary>
    public static IReadOnlyList<LyricLine> Parse(string text, double trackDuration, LatinReader? reader = null)
    {
        if (Encoding.UTF8.GetByteCount(text) > InputLimit) return [];
        // LRC convention: a positive [offset:] (milliseconds) shows lyrics sooner, matching the app's own song offset.
        var offset = 0.0;
        var tag = OffsetTag().Match(text);
        if (tag.Success && TextRules.TryParseDouble(tag.Groups[1].ValueSpan, out var milliseconds))
        {
            offset = double.IsFinite(milliseconds) ? milliseconds / 1000 : 0;
        }

        var rows = new List<Row>();
        var inCredits = true;
        var budget = ExpansionLimit;
        foreach (var raw in SplitNewlines(text))
        {
            var row = TextRules.TrimSpaces(raw);
            var leading = LeadingStamps(row);
            if (leading.Count == 0) continue;
            var last = leading[^1];
            var body = TextRules.TrimSpaces(row[(last.Index + last.Length)..]);
            var markers = FindStamps(body, '<', '>');
            var plain = RemoveStamps(body, markers);
            // Credits are not lyrics; an everyday label ("鼓：…") counts only before the first lyric or straight
            // after another credit.
            if (LyricCredits.IsCredit(plain, inCredits))
            {
                inCredits = true;
                foreach (var stamp in leading)
                {
                    var at = stamp.Seconds - offset;
                    rows.Add(new Row { Stamp = at, Start = Math.Max(0, at), Text = TextRules.TrimSpaces(plain), Credit = true });
                }
                continue;
            }
            if (!LyricLines.IsInstrumentalMark(plain)) inCredits = false;
            var cost = (long)leading.Count * (body.Length + 1);
            if (cost > budget) break;
            budget -= (int)cost;

            // The word stamps are read once; each copy of the row moves them by its own timestamp.
            var prefix = markers.Count > 0 ? TextRules.TrimSpaces(body[..markers[0].Index]) : "";
            var syllables = ReadSyllables(body, markers);
            var firstTime = leading[0].Seconds;
            foreach (var stamp in leading)
            {
                var at = stamp.Seconds - offset;
                var shift = stamp.Seconds - firstTime - offset;
                var begin = Math.Max(0, at);
                var tokens = new List<TimedToken>(syllables.Count);
                var valid = prefix.Length == 0;
                foreach (var syllable in syllables)
                {
                    var start = Math.Max(0, syllable.Time + shift);
                    var end = syllable.Next is { } next ? Math.Max(start, next + shift) : double.MaxValue;
                    if (!(start >= begin) || (tokens.Count > 0 && !(start >= tokens[^1].Start))) valid = false;
                    tokens.Add(new TimedToken(syllable.Text, start, end, syllable.Glued));
                }
                rows.Add(new Row { Stamp = at, Start = begin, Text = plain, Tokens = valid ? tokens : [] });
            }
        }

        var merged = MergingSharedTimestamps(rows, new LyricScript.ReadingCache(reader));
        var lastEnd = double.IsFinite(trackDuration) ? trackDuration : 0;
        var lines = new List<LyricLine>(merged.Count);
        for (var index = 0; index < merged.Count; index++)
        {
            var item = merged[index];
            var end = index + 1 < merged.Count ? merged[index + 1].Start : Math.Max(lastEnd, item.Start + 4);
            if (item.Credit)
            {
                // Credits sit apart from the lyric: kept for the credits layer, never drawn as a lead line, and
                // never in the way of one.
                lines.Add(new LyricLine
                {
                    Start = item.Start, End = item.Start + 0.01, EndFromNextLine = true, Text = item.Text, IsInstrumental = true,
                    Credit = true,
                });
                continue;
            }
            // A row an [offset:] pushed before the start is overtaken at 0.
            if (!(end > item.Start)) continue;
            var line = LyricLines.MakeLine(item.Text, item.Start, end) with { EndFromNextLine = true };
            // Words are built once the end is known: the last syllable stamp runs to it, so a final "tiful day" can
            // still be divided.
            var words = LyricScript.Words(item.Tokens.Select(token => token with { End = Math.Min(token.End, end) }).ToList());
            if (!line.IsInstrumental && words.Count > 0 && words.All(word => word.Start < end))
            {
                var timed = line with { Words = words, WordTimed = true };
                if (LyricTimingRules.ValidWordTiming(timed)) line = timed;
            }
            if (!line.IsInstrumental) line = line with { Romanization = item.Romanization, Translation = item.Translation };
            lines.Add(line);
        }
        return LyricCredits.MarkingTitleHeader(lines);
    }

    /// <summary>
    /// Bilingual LRC repeats a timestamp for a reading or a translation, e.g. "[00:10.00]君の名は" then
    /// "[00:10.00]kimi no na wa". Those rows become one line: the first is the lyric, a Latin reading of a non-Latin
    /// lyric is its romanisation and any other version is its translation. An empty or "♪" row gives way to a lyric
    /// at the same time, and a word-timed copy of the lyric lends it its word timing.
    /// </summary>
    private static List<Row> MergingSharedTimestamps(List<Row> rows, LyricScript.ReadingCache readings)
    {
        // Stable: rows at one instant keep their order in the file. Rows that meet at 0 only because an [offset:]
        // moved them earlier stay apart.
        var ordered = rows.Select((row, index) => (row, index)).OrderBy(pair => pair.row.Stamp).ThenBy(pair => pair.index)
            .Select(pair => pair.row);
        var merged = new List<Row>();
        foreach (var row in ordered)
        {
            var line = merged.Count > 0 ? merged[^1] : null;
            if (line is null || !(Math.Abs(line.Stamp - row.Stamp) < 0.0005) || line.Credit || row.Credit)
            {
                merged.Add(row);
                continue;
            }
            var text = TextRules.TrimSpaces(row.Text);
            var lyric = TextRules.TrimSpaces(line.Text);
            if (LyricLines.IsInstrumentalMark(text)) continue;
            if (LyricLines.IsInstrumentalMark(lyric))
            {
                line.Text = row.Text;
                line.Tokens = row.Tokens;
            }
            else if (text == lyric)
            {
                if (line.Tokens.Count == 0 && row.Tokens.Count > 0) line.Tokens = row.Tokens;
            }
            else if (line.Romanization is null && LyricScript.IsReading(text, lyric, readings))
            {
                line.Romanization = text;
            }
            else if (line.Translation is null)
            {
                line.Translation = text;
            }
        }
        return merged;
    }

    /// <summary>The syllables between word stamps, with the next stamp's time and whether a space came before.</summary>
    private static List<Syllable> ReadSyllables(string body, List<Stamp> markers)
    {
        var syllables = new List<Syllable>(markers.Count);
        var spaced = true;
        for (var index = 0; index < markers.Count; index++)
        {
            var from = markers[index].Index + markers[index].Length;
            var to = index + 1 < markers.Count ? markers[index + 1].Index : body.Length;
            var label = body[from..to];
            var trimmed = label.Trim();
            // Syllable stamps with no space between them belong to one word.
            var glued = !spaced && !(label.Length == 0 || char.IsWhiteSpace(label[0]));
            if (label.Length > 0) spaced = char.IsWhiteSpace(label[^1]);
            if (trimmed.Length == 0) continue;
            syllables.Add(new Syllable(trimmed, markers[index].Seconds, index + 1 < markers.Count ? markers[index + 1].Seconds : null, glued));
        }
        return syllables;
    }

    /// <summary>The "[m:ss]" stamps that open a row, whitespace allowed between them.</summary>
    private static List<Stamp> LeadingStamps(string row)
    {
        var all = FindStamps(row, '[', ']');
        var leading = new List<Stamp>();
        var end = 0;
        foreach (var stamp in all)
        {
            if (stamp.Index < end || !TextRules.AllWhitespace(row.AsSpan(end, stamp.Index - end))) break;
            leading.Add(stamp);
            end = stamp.Index + stamp.Length;
        }
        return leading;
    }

    /// <summary>Every stamp in the text, left to right, without overlaps.</summary>
    private static List<Stamp> FindStamps(string text, char open, char close)
    {
        var stamps = new List<Stamp>();
        var index = 0;
        while (index < text.Length)
        {
            if (text[index] == open && TryStamp(text, index, close, out var length, out var seconds))
            {
                stamps.Add(new Stamp(index, length, seconds));
                index += length;
            }
            else index++;
        }
        return stamps;
    }

    /// <summary>Reads "[mmm:ss]", "[mm:ss.f]", "[mm:ss:fff]" (with <paramref name="close"/> as the closing mark) at <paramref name="at"/>.</summary>
    private static bool TryStamp(string text, int at, char close, out int length, out double seconds)
    {
        length = 0;
        seconds = 0;
        var i = at + 1;
        var minutesStart = i;
        while (i < text.Length && i - minutesStart < 3 && char.IsAsciiDigit(text[i])) i++;
        var minuteDigits = i - minutesStart;
        if (minuteDigits == 0 || i >= text.Length || text[i] != ':') return false;
        var minutes = Digits(text, minutesStart, minuteDigits);
        i++;
        if (i + 1 >= text.Length || text[i] is < '0' or > '5' || !char.IsAsciiDigit(text[i + 1])) return false;
        var secs = Digits(text, i, 2);
        i += 2;
        var fraction = 0.0;
        var fractionDigits = 0;
        if (i < text.Length && text[i] is '.' or ':')
        {
            var fractionStart = i + 1;
            var j = fractionStart;
            while (j < text.Length && j - fractionStart < 3 && char.IsAsciiDigit(text[j])) j++;
            // A fraction counts only when the stamp closes straight after it.
            if (j > fractionStart && j < text.Length && text[j] == close)
            {
                fractionDigits = j - fractionStart;
                fraction = Digits(text, fractionStart, fractionDigits);
                i = j;
            }
        }
        if (i >= text.Length || text[i] != close) return false;
        length = i + 1 - at;
        seconds = minutes * 60 + secs + fraction / Math.Pow(10, fractionDigits);
        return true;
    }

    private static double Digits(string text, int start, int count)
    {
        var value = 0;
        for (var i = 0; i < count; i++) value = value * 10 + (text[start + i] - '0');
        return value;
    }

    private static string RemoveStamps(string text, List<Stamp> stamps)
    {
        if (stamps.Count == 0) return text;
        var builder = new StringBuilder(text.Length);
        var cursor = 0;
        foreach (var stamp in stamps)
        {
            builder.Append(text, cursor, stamp.Index - cursor);
            cursor = stamp.Index + stamp.Length;
        }
        builder.Append(text, cursor, text.Length - cursor);
        return builder.ToString();
    }

    /// <summary>The text cut at every line-break character, as Foundation's <c>components(separatedBy: .newlines)</c>.</summary>
    internal static IEnumerable<string> SplitNewlines(string text)
    {
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is '\n' or '\r' or '\u000B' or '\u000C' or '\u0085' or (char)0x2028 or (char)0x2029)
            {
                yield return text[start..index];
                start = index + 1;
            }
        }
        yield return text[start..];
    }
}
