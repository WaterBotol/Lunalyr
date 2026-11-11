// Ported from the Mac app's LocalLyricBridge.swift: `supplement`, `attachingReadings`, `attachingTranslations` and
// `Companion` are Lunalyr's own code (commits aa22c91, fc34684) and are ported as written. The few lines of those
// functions blamed to the Verso import are the ordinary `lines.map { var result = line … return result }` wrappers.

using Lunalyr.Core.Lyrics.Timing;

namespace Lunalyr.Core.Lyrics.Parsing;

/// <summary>
/// The files a local lyric export keeps beside its LRC: a word-timed sidecar (<c>.ttml</c> or <c>.yrc</c>) that may
/// cover only some lines, a reading file (<c>.roma.lrc</c>) and a translation file (<c>.tr.lrc</c>), each timed like
/// the lyrics.
/// </summary>
public static class LocalSidecars
{
    /// <summary>
    /// Sidecars can contain just the lines for which the provider has word timing. The LRC-only lines are kept when
    /// both files share the same timeline: exact text first, then near copies up to two seconds apart (files from two
    /// services are rarely synced to the same instant, and an unpaired line-timed copy would show the whole line before
    /// the word-timed one).
    /// </summary>
    public static IReadOnlyList<LyricLine> Supplement(IReadOnlyList<LyricLine> native, IReadOnlyList<LyricLine> lrc)
    {
        ArgumentNullException.ThrowIfNull(native);
        ArgumentNullException.ThrowIfNull(lrc);
        var sidecar = new Companion(native);
        var keys = sidecar.Rows.Select(LyricKey).ToList();
        var paired = new Dictionary<int, int>();
        var used = new HashSet<int>();
        foreach (var (tolerance, fuzzy) in new[] { (0.18, false), (1.0, false), (2.0, true) })
        {
            for (var index = 0; index < lrc.Count; index++)
            {
                var line = lrc[index];
                if (line.IsInstrumental || paired.ContainsKey(index))
                {
                    continue;
                }
                var key = LyricKey(line);
                if (sidecar.MatchingIndex(line.Start, tolerance, row => !used.Contains(row) && !sidecar.Rows[row].IsInstrumental
                        && (keys[row] == key || (fuzzy && LyricLineIndex.SameLyric(line.Text, sidecar.Rows[row].Text, LyricQuery.Normalize, LyricQuery.Similarity))))
                    is { } match)
                {
                    paired[index] = match;
                    used.Add(match);
                }
            }
        }
        if (paired.Count == 0)
        {
            return native;
        }
        var result = sidecar.Rows.ToList();
        for (var index = 0; index < lrc.Count; index++)
        {
            var line = lrc[index];
            var nearby = sidecar.Index(line.Start);
            if (paired.TryGetValue(index, out var row))
            {
                result[row] = result[row] with
                {
                    Romanization = result[row].Romanization ?? line.Romanization,
                    Translation = result[row].Translation ?? line.Translation,
                };
            }
            else if (nearby is { } near && !used.Contains(near))
            {
                // A sidecar may include backing vocals in a line that has the same onset as a shorter LRC version.
                if (result[near].IsInstrumental && !line.IsInstrumental)
                {
                    result[near] = line;
                }
                used.Add(near);
            }
            else if (!line.IsInstrumental || nearby is null)
            {
                result.Add(line);
            }
        }
        // Stable, like Swift's sort on these already ordered rows.
        result = [.. result.OrderBy(line => line.Start)];
        for (var index = 0; index + 1 < result.Count; index++)
        {
            var line = result[index];
            if (line.EndFromNextLine != true)
            {
                continue;
            }
            var nextStart = result[index + 1].Start;
            if (!(nextStart > line.Start))
            {
                continue;
            }
            if (line.WordTimed)
            {
                result[index] = line with { End = Math.Max(nextStart, line.Words.Count > 0 ? line.Words.Max(word => word.End) : nextStart) };
            }
            else
            {
                result[index] = line.IsInstrumental
                    ? line with { End = nextStart }
                    : line with { End = nextStart, Words = LyricLines.DistributeWords(line.Text, line.Start, nextStart) };
            }
        }
        return result;

        static string LyricKey(LyricLine line)
        {
            var key = LyricQuery.Normalize(line.Text);
            return key.Length == 0 ? line.Text : key;
        }
    }

    /// <summary>
    /// A reading file (<c>.roma.lrc</c>), timed like the lyrics. A line takes the row stamped nearest it, within
    /// 0.18 s, unless that row is an instrumental break.
    /// </summary>
    public static IReadOnlyList<LyricLine> AttachingReadings(string text, IReadOnlyList<LyricLine> lines, double duration)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var readings = new Companion(LrcParser.Parse(text, duration));
        return [.. lines.Select(line => readings.Row(line.Start) is { IsInstrumental: false } reading ? line with { Romanization = reading.Text } : line)];
    }

    /// <summary>
    /// A translation file (<c>.tr.lrc</c>), timed like the lyrics. A line takes the row stamped nearest it, within
    /// 0.18 s, unless it already has a translation (a bilingual file's own), the row is blank, or the row only repeats
    /// the line.
    /// </summary>
    public static IReadOnlyList<LyricLine> AttachingTranslations(string text, IReadOnlyList<LyricLine> lines, double duration)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var translations = new Companion(LrcParser.Parse(text, duration));
        if (translations.Rows.Count == 0)
        {
            return lines;
        }
        return [.. lines.Select(line =>
        {
            if (line.Translation is not null || line.IsInstrumental || translations.Row(line.Start) is not { IsInstrumental: false } row)
            {
                return line;
            }
            var translation = row.Text.Trim();
            return translation.Length > 0 && translation != line.Text.Trim() ? line with { Translation = translation } : line;
        })];
    }

    /// <summary>
    /// The rows of a companion file, sorted once by time, so each lyric line finds its row by binary search: a file of
    /// many thousands of rows costs a moment rather than minutes on every lookup and folder change.
    /// </summary>
    private sealed class Companion
    {
        // In file order where two rows share a time, as a scan would find them.
        public Companion(IReadOnlyList<LyricLine> rows) => Rows = [.. rows.Select((line, index) => (line, index))
            .OrderBy(pair => pair.line.Start).ThenBy(pair => pair.index).Select(pair => pair.line)];

        public IReadOnlyList<LyricLine> Rows { get; }

        /// <summary>The row stamped nearest <paramref name="time"/>, within 0.18 s; the earlier row on a tie, and the first of rows that share a time.</summary>
        public LyricLine? Row(double time) => Index(time) is { } index ? Rows[index] : null;

        public int? Index(double time)
        {
            var after = FirstAtOrAfter(time);
            int? nearest = after > 0 ? FirstAtOrAfter(Rows[after - 1].Start) : null;
            if (after < Rows.Count && (nearest is not { } before || Math.Abs(Rows[after].Start - time) < Math.Abs(Rows[before].Start - time)))
            {
                nearest = after;
            }
            return nearest is { } found && Math.Abs(Rows[found].Start - time) <= 0.18 ? found : null;
        }

        public int? MatchingIndex(double time, double tolerance, Func<int, bool> matches)
        {
            var index = FirstAtOrAfter(time - tolerance);
            int? nearest = null;
            var distance = tolerance;
            while (index < Rows.Count && Rows[index].Start <= time + tolerance)
            {
                var offset = Math.Abs(Rows[index].Start - time);
                if (offset <= distance && (nearest is null || offset < distance) && matches(index))
                {
                    nearest = index;
                    distance = offset;
                    if (distance == 0)
                    {
                        break;
                    }
                }
                index++;
            }
            return nearest;
        }

        /// <summary>The index of the first row stamped at or after <paramref name="time"/>.</summary>
        private int FirstAtOrAfter(double time)
        {
            int low = 0, high = Rows.Count;
            while (low < high)
            {
                var middle = (low + high) / 2;
                if (Rows[middle].Start < time)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            return low;
        }
    }
}
