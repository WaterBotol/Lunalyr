using System.Globalization;
using System.Text.RegularExpressions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Timing;

namespace Lunalyr.Core.Tests.Lyrics.Timing;

/// <summary>
/// Builds lines the way the Mac's parsers would for the simple inputs these tests use, so the timing tests keep the
/// Mac tests' inputs without depending on the parser workstream.
/// </summary>
internal static partial class TimingTestLines
{
    /// <summary>
    /// What <c>LyricsFetcher.parseLRC</c> gives for plain "[mm:ss.xx]text" rows: each line ends where the next starts
    /// (the last at the track's end), <c>EndFromNextLine</c> is true, empty rows are instrumental, and the words are
    /// estimated over the window.
    /// </summary>
    public static List<LyricLine> Lrc(string lrc, double trackDuration)
    {
        var rows = new List<(double Start, string Text)>();
        foreach (var raw in lrc.Split('\n'))
        {
            var match = Row().Match(raw.Trim());
            if (!match.Success) continue;
            var start = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 60
                + double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            rows.Add((start, match.Groups[3].Value.Trim()));
        }
        var lines = new List<LyricLine>();
        for (var i = 0; i < rows.Count; i++)
        {
            var end = i + 1 < rows.Count ? rows[i + 1].Start : trackDuration;
            lines.Add(Make(rows[i].Text, rows[i].Start, end) with { EndFromNextLine = true });
        }
        return lines;
    }

    /// <summary>What <c>LyricsFetcher.makeLine</c> gives: estimated words, the end the caller supplied.</summary>
    public static LyricLine Make(string text, double start, double end)
    {
        var instrumental = text.Trim().All(c => "♪♫•·-–—".Contains(c) || char.IsWhiteSpace(c));
        return new LyricLine
        {
            Start = start,
            End = end,
            Text = instrumental ? "" : text.Trim(),
            Words = instrumental ? [] : EstimatedWords.Distribute(text.Trim(), start, end),
            IsInstrumental = instrumental,
        };
    }

    public static LyricLine WordTimed(double start, double end, string text, params LyricWord[] words) =>
        new() { Start = start, End = end, Text = text, Words = words, WordTimed = true };

    [GeneratedRegex(@"^\[(\d+):(\d+(?:\.\d+)?)\](.*)$")]
    private static partial Regex Row();
}
