// Syncing a YouTube video from its own captions: the caption track (the uploader's lyric captions, or YouTube's speech
// recognition, which times each word) is matched with the song's timed lyric lines, and where they agree, how far into
// the video the song starts is measured rather than guessed. Pure values only, so it can be tested on its own.
// Ported from the Mac's MusicVideoCaptionSync.swift (deb366e, 30f2ddf, 3c6c931, 1384242; Lunalyr's own code).
//
// The text comes from inside YouTube's own player: its /api/timedtext replies are read as the player loads them (on
// Windows, WebView2's WebResourceResponseReceived sees the embed's frame), since YouTube refuses that endpoint to
// requests from anywhere else.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using Lunalyr.Core.Lyrics.Ranking;

namespace Lunalyr.Core.MusicVideo;

/// <summary>A caption in the video's own time. <paramref name="Words"/>: when each word starts, for tracks that say (YouTube's automatic captions do).</summary>
public sealed record MusicVideoCaptionCue(double Start, double End, string Text, IReadOnlyList<MusicVideoCaptionCue.Word>? Words = null)
{
    public readonly record struct Word(double Start, string Text);

    public bool Equals(MusicVideoCaptionCue? other) =>
        other is not null && Start == other.Start && End == other.End && Text == other.Text
        && (Words is null ? other.Words is null : other.Words is not null && Words.SequenceEqual(other.Words));

    public override int GetHashCode() => HashCode.Combine(Start, End, Text, Words?.Count);
}

public static partial class MusicVideoCaptionSync
{
    // ---- Parsing ----

    /// <summary>Cues from a timed-text reply in either of YouTube's formats (json3, srv3 or the older XML). Empty when it is neither.</summary>
    public static IReadOnlyList<MusicVideoCaptionCue> Cues(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            if (b is 0x20 or 0x0A or 0x0D or 0x09 or 0xEF or 0xBB or 0xBF) continue;
            if (b == (byte)'{') return CuesJson3(data.ToArray());
            if (b == (byte)'<') return CuesXml(data.ToArray());
            return [];
        }
        return [];
    }

    public static IReadOnlyList<MusicVideoCaptionCue> Cues(string text) => Cues(Encoding.UTF8.GetBytes(text ?? ""));

    /// <summary>
    /// <c>{"events":[{"tStartMs":1200,"dDurationMs":2000,"segs":[{"utf8":"what's"},{"utf8":" wrong","tOffsetMs":1000}]}]}</c>.
    /// Events that only carry a newline (<c>aAppend</c>) or a window style are skipped.
    /// </summary>
    public static IReadOnlyList<MusicVideoCaptionCue> CuesJson3(byte[] data)
    {
        var cues = new List<MusicVideoCaptionCue>();
        try
        {
            using var document = JsonDocument.Parse(data);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array)
            {
                return [];
            }
            foreach (var item in events.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || Number(item, "tStartMs") is not { } startMs || !double.IsFinite(startMs)
                    || !item.TryGetProperty("segs", out var segments) || segments.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                var start = startMs / 1000;
                var duration = (Number(item, "dDurationMs") ?? 0) / 1000;
                var words = new List<MusicVideoCaptionCue.Word>();
                var timed = false;
                var text = new StringBuilder();
                foreach (var segment in segments.EnumerateArray())
                {
                    if (segment.ValueKind != JsonValueKind.Object || !segment.TryGetProperty("utf8", out var utf8)
                        || utf8.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }
                    var piece = utf8.GetString()!;
                    text.Append(piece);
                    var offset = Number(segment, "tOffsetMs");
                    if (offset is not null) timed = true;
                    var trimmed = piece.Trim();
                    if (trimmed.Length == 0) continue;
                    words.Add(new MusicVideoCaptionCue.Word(start + (offset ?? 0) / 1000, trimmed));
                }
                var clean = text.ToString().Replace('\n', ' ').Trim();
                if (clean.Length == 0) continue;
                cues.Add(new MusicVideoCaptionCue(start, start + Math.Max(0, duration), clean, timed && words.Count > 1 ? words : null));
            }
        }
        catch (JsonException)
        {
            return [];
        }
        return cues;
    }

    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : null;

    /// <summary>srv3 (<c>&lt;p t="1200" d="2000"&gt;&lt;s t="0"&gt;what's&lt;/s&gt;…</c>, milliseconds) or the older format (<c>&lt;text start="1.2" dur="2"&gt;</c>, seconds).</summary>
    public static IReadOnlyList<MusicVideoCaptionCue> CuesXml(byte[] data)
    {
        var cues = new List<MusicVideoCaptionCue>();
        double? cueStart = null, cueEnd = null, wordStart = null;
        var text = new StringBuilder();
        var wordText = new StringBuilder();
        var words = new List<MusicVideoCaptionCue.Word>();
        var timedWords = false;
        static double? Attribute(XmlReader reader, string name) =>
            double.TryParse(reader.GetAttribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
        void Begin(double start, double duration)
        {
            cueStart = start;
            cueEnd = start + Math.Max(0, duration);
            text.Clear();
            words.Clear();
            timedWords = false;
        }
        void End(string name)
        {
            switch (name)
            {
                case "s":
                    if (wordStart is { } at)
                    {
                        var trimmed = wordText.ToString().Trim();
                        if (trimmed.Length > 0) words.Add(new MusicVideoCaptionCue.Word(at, trimmed));
                    }
                    wordStart = null;
                    break;
                case "p" or "text":
                    if (cueStart is not { } start || cueEnd is not { } end) return;
                    var clean = text.ToString().Replace('\n', ' ').Trim();
                    if (clean.Length > 0) cues.Add(new MusicVideoCaptionCue(start, end, clean, timedWords && words.Count > 1 ? [.. words] : null));
                    cueStart = null;
                    break;
            }
        }
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(data), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        var name = reader.Name;
                        var empty = reader.IsEmptyElement;
                        switch (name)
                        {
                            case "p" when Attribute(reader, "t") is { } t:
                                Begin(t / 1000, (Attribute(reader, "d") ?? 0) / 1000);
                                break;
                            case "text" when Attribute(reader, "start") is { } s:
                                Begin(s, Attribute(reader, "dur") ?? 0);
                                break;
                            case "s" when cueStart is { } start:
                                var offset = Attribute(reader, "t");
                                if (offset is not null) timedWords = true;
                                wordStart = start + (offset ?? 0) / 1000;
                                wordText.Clear();
                                break;
                            case "br":
                                text.Append(' ');
                                break;
                        }
                        if (empty) End(name);
                        break;
                    case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                        if (cueStart is null) break;
                        text.Append(reader.Value);
                        if (wordStart is not null) wordText.Append(reader.Value);
                        break;
                    case XmlNodeType.EndElement:
                        End(reader.Name);
                        break;
                }
            }
        }
        catch (XmlException)
        {
            return [];
        }
        return cues;
    }

    // ---- Words ----

    /// <summary>Lower case, without accents, punctuation, bracketed asides ("[Music]", "(Chorus)", backing vocals) or note symbols; spaces collapsed.</summary>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var s = LyricTextFolding.FoldWidth(text);
        s = Asides().Replace(s, " ");
        s = s.Replace('’', '\'').Replace("'", "", StringComparison.Ordinal);
        var kept = new StringBuilder(s.Length);
        foreach (var rune in s.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                    or UnicodeCategory.EnclosingMark or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber)
            {
                kept.Append(rune.ToString());
            }
            else
            {
                kept.Append(' ');
            }
        }
        return string.Join(' ', kept.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex Asides();

    /// <summary>Words, or single characters for scripts written without spaces (Japanese, Chinese), so they can be matched either way.</summary>
    public static IReadOnlyList<string> Tokens(string text)
    {
        var normalized = Normalize(text);
        if (IsUnspaced(normalized)) return Characters(normalized.Replace(" ", "", StringComparison.Ordinal));
        return normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool IsUnspaced(string text) => text.EnumerateRunes().Any(r => r.Value is >= 0x3040 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF);

    private static List<string> Characters(string text)
    {
        var characters = new List<string>();
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext()) characters.Add(elements.GetTextElement());
        return characters;
    }

    /// <summary>What speech recognition writes for music, applause or singing it can't read: not lyrics.</summary>
    public static IReadOnlySet<string> NoiseCues { get; } =
        new HashSet<string>(["foreign", "music", "applause", "laughter", "instrumental", "singing"], StringComparer.Ordinal);

    /// <summary>The same word, allowing for how lyrics and recognition spell it: "askin" and "asking", one letter off in a longer word.</summary>
    public static bool SameWord(string a, string b)
    {
        if (a == b) return true;
        if (a + "g" == b || b + "g" == a) return true;
        var x = Characters(a);
        var y = Characters(b);
        if (Math.Min(x.Count, y.Count) < 4 || Math.Abs(x.Count - y.Count) > 1) return false;
        return EditDistance(x, y, 1) <= 1;
    }

    private static int EditDistance(List<string> a, List<string> b, int limit)
    {
        if (a.Count == 0 || b.Count == 0) return Math.Max(a.Count, b.Count);
        var previous = Enumerable.Range(0, b.Count + 1).ToArray();
        for (var i = 1; i <= a.Count; i++)
        {
            var current = new int[b.Count + 1];
            current[0] = i;
            for (var j = 1; j <= b.Count; j++)
            {
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }
            if (current.Min() > limit) return limit + 1;
            previous = current;
        }
        return previous[b.Count];
    }

    /// <summary>One caption word in the video's time. <paramref name="Exact"/>: its start was given (a timed word, or the first word of a cue); otherwise spread over its cue.</summary>
    public readonly record struct CaptionWord(string Token, double Time, bool Exact);

    /// <summary>A word's typical length when sung, seconds.</summary>
    public const double TypicalWord = 0.3;

    /// <summary>Recognition's gap for a word it could not time.</summary>
    public const double PlaceholderGap = 1.0;

    /// <summary>
    /// The caption track as one stream of words, noise left out. Speech recognition times most words, but a word it
    /// could not place comes exactly 1.000 s after the one before: a placeholder, not counted as exact. Untimed cues (an
    /// uploader's) time only their first word; the rest are spread over the cue.
    /// </summary>
    public static List<CaptionWord> CaptionWords(IEnumerable<MusicVideoCaptionCue> cues)
    {
        var stream = new List<CaptionWord>();
        foreach (var cue in cues.Where(c => double.IsFinite(c.Start)).OrderBy(c => c.Start))
        {
            var all = Tokens(cue.Text);
            if (all.Count == 0 || (all.Count == 1 && NoiseCues.Contains(all[0]))) continue;
            if (cue.Words is { Count: > 0 } words)
            {
                double? previous = null;
                foreach (var word in words.Where(w => double.IsFinite(w.Start)))
                {
                    var placeholder = previous is { } before && Math.Abs(word.Start - before - PlaceholderGap) < 0.015;
                    var index = 0;
                    foreach (var token in Tokens(word.Text))
                    {
                        stream.Add(new CaptionWord(token, word.Start, index == 0 && !placeholder));
                        index++;
                    }
                    previous = word.Start;
                }
            }
            else
            {
                var span = cue.End > cue.Start ? cue.End - cue.Start : all.Count * TypicalWord;
                var step = span / all.Count;
                for (var index = 0; index < all.Count; index++) stream.Add(new CaptionWord(all[index], cue.Start + (index * step), index == 0));
            }
        }
        return stream;
    }

    // ---- Matching ----

    /// <summary>One lyric line found in the captions. <paramref name="Score"/>: share of the line's words found, 0…1.</summary>
    public readonly record struct Match(int Line, double SongTime, double VideoTime, double Score)
    {
        public double Offset => VideoTime - SongTime;
    }

    /// <summary>A stretch of the song with its own offset (<see cref="Result.Segments"/>).</summary>
    public readonly record struct Segment(double SongStart, double SongEnd, double Offset, int Lines);

    /// <summary>Whether the captions' offset is trusted, and if not, why.</summary>
    public enum VerdictKind
    {
        Trusted,

        /// <summary>No lyric line was found in the captions.</summary>
        NoMatches,

        /// <summary>Fewer than <see cref="MinimumMatches"/> lines agree.</summary>
        TooFew,

        /// <summary>Enough lines agree, but under <see cref="MinimumAgreement"/> of those matched.</summary>
        ThinAgreement,

        /// <summary>Another offset (a chorus one repeat off) has <see cref="Verdict.Rival"/> lines against the winner's <see cref="Verdict.Support"/>.</summary>
        Ambiguous,
    }

    public readonly record struct Verdict(VerdictKind Kind, int Rival = 0, int Support = 0)
    {
        public static Verdict Trusted => new(VerdictKind.Trusted);

        /// <summary>For the log: a few words, no lyric text.</summary>
        public string Summary => Kind switch
        {
            VerdictKind.Trusted => "trusted",
            VerdictKind.NoMatches => "refused: no lyric line found in the captions",
            VerdictKind.TooFew => $"refused: too few agreeing lines (need {MinimumMatches})",
            VerdictKind.ThinAgreement => $"refused: thin agreement (under {(int)(MinimumAgreement * 100)}% of matched lines)",
            _ => $"refused: ambiguous, another offset has {Rival} lines against {Support} (a chorus shift)",
        };
    }

    /// <summary>
    /// <see cref="Offset"/> (video time − song time) when the captions agree on one well enough to trust (at least
    /// <see cref="MinimumMatches"/> lines within <see cref="Tolerance"/> of the median, and <see cref="MinimumAgreement"/> of
    /// all matched lines, with no rival offset nearly as well supported); null otherwise. <see cref="Matches"/>: each
    /// matched line's match, the one nearest the consensus. <see cref="Segments"/>: two stretches with offsets a second or
    /// more apart, one after the other (a video with an extra scene mid-song); empty otherwise.
    /// </summary>
    public sealed record Result(double? Offset, IReadOnlyList<Match> Matches, int Agreeing, double Spread, IReadOnlyList<Segment> Segments,
        Verdict Verdict)
    {
        public static Result None { get; } = new(null, [], 0, 0, [], new Verdict(VerdictKind.NoMatches));
    }

    public const int MinimumMatches = 5;
    public const double MinimumAgreement = 0.6;
    public const double Tolerance = 0.5;

    /// <summary>A lyric line needs this many words to be matched (characters, for scripts without spaces: <see cref="MinimumCharacters"/>).</summary>
    public const int MinimumWords = 3;

    public const int MinimumCharacters = 4;

    /// <summary>Share of a line's words that must be found, in order.</summary>
    public const double MinimumScore = 0.6;

    public const double RangeMin = -300, RangeMax = 300;

    /// <summary>The latest word of a line its start may be worked back from.</summary>
    public const int MaximumAnchorWord = 2;

    /// <summary>A rival offset with this share of the winner's lines makes it ambiguous.</summary>
    public const double RivalShare = 0.75;

    public const int SegmentLines = 3;

    /// <summary>Where each lyric line could be in the captions, as offsets.</summary>
    public static List<List<Match>> Candidates(IEnumerable<MusicVideoCaptionCue> cues, IReadOnlyList<MusicVideoLyricLine> lines,
        double rangeMin = RangeMin, double rangeMax = RangeMax)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var stream = CaptionWords(cues);
        var sorted = lines.Select((line, index) => (Line: line, Index: index)).Where(e => double.IsFinite(e.Line.Start))
            .OrderBy(e => e.Line.Start).ToList();
        var result = new List<List<Match>>();
        for (var position = 0; position < sorted.Count; position++)
        {
            var (line, lineIndex) = sorted[position];
            var words = Tokens(line.Text);
            var unspaced = IsUnspaced(string.Concat(words));
            var needed = unspaced ? MinimumCharacters : MinimumWords;
            if (words.Count < needed) continue;
            var next = position + 1 < sorted.Count ? sorted[position + 1].Line.Start : line.Start + 4;
            var pace = Math.Min(0.4, Math.Max(0.12, (next - line.Start) / words.Count));
            var found = new List<Match>();
            for (var p = 0; p < stream.Count; p++)
            {
                // A start: one of the line's first two words.
                var first = -1;
                for (var k = 0; k < Math.Min(2, words.Count); k++)
                {
                    if (SameWord(words[k], stream[p].Token))
                    {
                        first = k;
                        break;
                    }
                }
                if (first < 0) continue;
                var pairs = new List<(int Word, int At)> { (first, p) };
                var j = p + 1;
                for (var k = first + 1; k < words.Count; k++)
                {
                    var hit = -1;
                    for (var q = j; q < Math.Min(stream.Count, j + 3); q++)
                    {
                        if (SameWord(words[k], stream[q].Token))
                        {
                            hit = q;
                            break;
                        }
                    }
                    if (hit < 0) continue;
                    pairs.Add((k, hit));
                    j = hit + 1;
                }
                var score = (double)pairs.Count / words.Count;
                if (pairs.Count < needed || score < MinimumScore) continue;
                // Not spread wider than the line could be sung.
                if (stream[pairs[^1].At].Time - stream[p].Time > (words.Count * 0.8) + 2) continue;
                // The line's start in the video: its first word if exactly timed, else back from the first exact word
                // (one of its first three) at the line's pace.
                var anchorAt = pairs.FindIndex(pair => stream[pair.At].Exact);
                if (anchorAt < 0 || pairs[anchorAt].Word > MaximumAnchorWord) continue;
                var anchor = pairs[anchorAt];
                var videoTime = stream[anchor.At].Time - (anchor.Word * pace);
                var match = new Match(lineIndex, line.Start, videoTime, score);
                if (match.Offset < rangeMin || match.Offset > rangeMax) continue;
                if (found.Count > 0 && Math.Abs(found[^1].VideoTime - videoTime) < 1)
                {
                    if (score > found[^1].Score) found[^1] = match;
                    continue;
                }
                found.Add(match);
            }
            if (found.Count > 0) result.Add(found);
        }
        return result;
    }

    /// <summary>The offset the captions agree on, if they do (<see cref="Result"/>).</summary>
    public static Result Measure(IEnumerable<MusicVideoCaptionCue> cues, IReadOnlyList<MusicVideoLyricLine> lines,
        double rangeMin = RangeMin, double rangeMax = RangeMax) => Consensus(Candidates(cues, lines, rangeMin, rangeMax));

    /// <summary>Each inner list: the places one lyric line matched.</summary>
    public static Result Consensus(IReadOnlyList<IReadOnlyList<Match>> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (Vote(lines) is not { } center) return Result.None;
        var chosen = lines.Select(line => Nearest(line, center)).ToList();
        var median = Median(chosen.Select(m => m.Offset));
        var agreeing = chosen.Select(m => m.Offset).Where(offset => Math.Abs(offset - median) <= Tolerance).ToList();
        var refined = Median(agreeing);
        var spread = Median(agreeing.Select(offset => Math.Abs(offset - refined)));
        // Another offset with nearly as many lines (a chorus heard once less, one chorus off) leaves it ambiguous.
        var support = Support(lines, center);
        var rival = lines.SelectMany(l => l).Select(m => m.Offset).Where(offset => Math.Abs(offset - center) > 1)
            .Select(offset => Support(lines, offset)).DefaultIfEmpty(0).Max();
        Verdict verdict;
        if (agreeing.Count < MinimumMatches) verdict = new Verdict(VerdictKind.TooFew);
        else if (agreeing.Count < MinimumAgreement * chosen.Count) verdict = new Verdict(VerdictKind.ThinAgreement);
        else if (rival >= RivalShare * support) verdict = new Verdict(VerdictKind.Ambiguous, rival, support);
        else verdict = Verdict.Trusted;
        return new Result(verdict.Kind == VerdictKind.Trusted ? Math.Round(refined * 20, MidpointRounding.AwayFromZero) / 20 : null,
            [.. chosen.OrderBy(m => m.SongTime)], agreeing.Count, spread, Segments(lines, center), verdict);
    }

    /// <summary>How many lines have a match within <see cref="Tolerance"/> of <paramref name="center"/>.</summary>
    private static int Support(IReadOnlyList<IReadOnlyList<Match>> lines, double center) =>
        lines.Count(line => line.Any(m => Math.Abs(m.Offset - center) <= Tolerance));

    /// <summary>The offset with the most lines having a match near it (ties: the better-matched).</summary>
    private static double? Vote(IReadOnlyList<IReadOnlyList<Match>> lines, double within = Tolerance)
    {
        (double Center, int Count, double Score)? best = null;
        foreach (var center in lines.SelectMany(l => l).Select(m => m.Offset))
        {
            int count = 0;
            double score = 0;
            foreach (var line in lines)
            {
                var near = line.Where(m => Math.Abs(m.Offset - center) <= within).ToList();
                if (near.Count == 0) continue;
                count++;
                score += near.Max(m => m.Score);
            }
            if (best is not { } b || count > b.Count || (count == b.Count && score > b.Score)) best = (center, count, score);
        }
        return best?.Center;
    }

    private static Match Nearest(IReadOnlyList<Match> matches, double center)
    {
        var best = matches[0];
        foreach (var m in matches) if (Math.Abs(m.Offset - center) < Math.Abs(best.Offset - center)) best = m;
        return best;
    }

    /// <summary>
    /// Two offsets a second or more apart, each held by at least <see cref="SegmentLines"/> lines, the one's lines all
    /// before the other's (one stray allowed): the video's edit differs from the song partway.
    /// </summary>
    public static IReadOnlyList<Segment> Segments(IReadOnlyList<IReadOnlyList<Match>> lines, double first)
    {
        var others = lines.Where(line => !line.Any(m => Math.Abs(m.Offset - first) <= 1))
            .Select(line => (IReadOnlyList<Match>)line.Where(m => Math.Abs(m.Offset - first) > 1).ToList()).ToList();
        if (Vote(others) is not { } second) return [];
        var assigned = new List<(double Time, int Which, double Offset)>();
        foreach (var line in lines)
        {
            var a = Nearest(line, first);
            var b = Nearest(line, second);
            if (Math.Abs(a.Offset - first) <= Tolerance) assigned.Add((a.SongTime, 0, a.Offset));
            else if (Math.Abs(b.Offset - second) <= Tolerance) assigned.Add((b.SongTime, 1, b.Offset));
        }
        assigned = [.. assigned.OrderBy(x => x.Time)];
        if (assigned.Count(x => x.Which == 0) < SegmentLines || assigned.Count(x => x.Which == 1) < SegmentLines) return [];
        // The best split: fewest lines on the wrong side.
        (int Index, int Strays, bool FirstEarly)? best = null;
        for (var split = 1; split < assigned.Count; split++)
        {
            foreach (var firstEarly in new[] { true, false })
            {
                var early = firstEarly ? 0 : 1;
                var strays = assigned.Take(split).Count(x => x.Which != early) + assigned.Skip(split).Count(x => x.Which == early);
                if (best is not { } b || strays < b.Strays) best = (split, strays, firstEarly);
            }
        }
        if (best is not { } chosen || chosen.Strays > 1) return [];
        var earlyPart = assigned.Take(chosen.Index).ToList();
        var latePart = assigned.Skip(chosen.Index).ToList();
        var earlyWhich = chosen.FirstEarly ? 0 : 1;
        Segment Of(List<(double Time, int Which, double Offset)> part, int which)
        {
            var own = part.Where(x => x.Which == which).ToList();
            return new Segment(part[0].Time, part[^1].Time, Math.Round(Median(own.Select(x => x.Offset)) * 20, MidpointRounding.AwayFromZero) / 20, own.Count);
        }
        return [Of(earlyPart, earlyWhich), Of(latePart, 1 - earlyWhich)];
    }

    public static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0;
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
    }

    // ---- Capture ----

    /// <summary>Replies larger than this are not read (a song's track is ~10-80 kB).</summary>
    public const int MaximumReply = 2_000_000;

    /// <summary>
    /// Run in every frame at document start: inside YouTube's player frame only, hides the player's caption text so the
    /// captions load unseen. Windows reads the replies themselves from WebView2 (the Mac's capture script also passes
    /// them on, which WebView2 does not need).
    /// </summary>
    public const string HideScript = """
        (function () {
          var host = location.hostname;
          if (host !== 'www.youtube-nocookie.com' && host !== 'www.youtube.com') return;
          if (window.__lunalyrCaptions) return;
          window.__lunalyrCaptions = true;
          function hide() {
            var style = document.createElement('style');
            style.textContent = '.ytp-caption-window-container,.caption-window,.ytp-caption-segment{display:none!important;opacity:0!important}';
            (document.head || document.documentElement).appendChild(style);
          }
          if (document.documentElement) hide(); else document.addEventListener('DOMContentLoaded', hide);
        })();
        """;

    /// <summary>
    /// What a captured reply is, if it is to be believed: YouTube's timed-text endpoint on its own host (https), for
    /// <paramref name="videoId"/>, not a machine translation (<c>tlang</c>). <paramref name="Automatic"/>: speech recognition.
    /// </summary>
    public readonly record struct Capture(bool Automatic, string? Language);

    public static Capture? CaptureOf(string url, string videoId)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.Host.ToLowerInvariant() is not ("www.youtube-nocookie.com" or "www.youtube.com") || uri.AbsolutePath != "/api/timedtext")
        {
            return null;
        }
        string? Value(string name)
        {
            foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = part.Split('=', 2);
                if (Uri.UnescapeDataString(pair[0]) == name) return pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : "";
            }
            return null;
        }
        if (Value("v") != videoId || Value("tlang") is not null) return null;
        return new Capture(Value("kind") == "asr", Value("lang"));
    }
}
