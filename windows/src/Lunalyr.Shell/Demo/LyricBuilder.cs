using System.Globalization;
using System.Text;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Parsing;

namespace Lunalyr.Shell.Demo;

/// <summary>
/// Builds invented word-timed lyrics through the real parsers, so they carry what a lookup gives the app: word timing
/// from TTML (Apple Music's shape), estimated words and line ends from LRC. Used by <c>--demo</c>, the composition
/// previews and the rendering tests.
/// </summary>
public static class LyricBuilder
{
    /// <summary>One sung word: its text and times, whether a space follows it, and whether it is a backing vocal.</summary>
    public readonly record struct Sung(string Text, double Start, double End, bool Space = true, bool Backing = false);

    /// <summary>Words spread evenly from <paramref name="start"/>, each <paramref name="length"/> long.</summary>
    public static Sung[] Even(double start, double step, double length, params string[] words) =>
        [.. words.Select((word, index) => new Sung(word, start + index * step, start + index * step + length))];

    /// <summary>A word-timed TTML document (Apple Music's shape) from lines of sung words.</summary>
    public static string Ttml(string language, params Sung[][] lines)
    {
        static string S(double seconds) => seconds.ToString("0.000", CultureInfo.InvariantCulture);
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"""<tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata" xmlns:itunes="http://music.apple.com/lyric-ttml-internal" itunes:timing="Word" xml:lang="{language}"><body><div>""");
        foreach (var line in lines)
        {
            var end = line.Max(word => word.End);
            text.Append(CultureInfo.InvariantCulture, $"""<p begin="{S(line[0].Start)}" end="{S(end)}">""");
            var inBacking = false;
            for (var i = 0; i < line.Length; i++)
            {
                var word = line[i];
                if (word.Backing && !inBacking) { text.Append("""<span ttm:role="x-bg">"""); inBacking = true; }
                text.Append(CultureInfo.InvariantCulture, $"""<span begin="{S(word.Start)}" end="{S(word.End)}">{System.Security.SecurityElement.Escape(word.Text)}</span>""");
                var closes = inBacking && (i + 1 == line.Length || !line[i + 1].Backing);
                if (closes) { text.Append("</span>"); inBacking = false; }
                if (word.Space && i + 1 < line.Length) text.Append(' ');
            }
            text.Append("</p>");
        }
        text.Append("</div></body></tt>");
        return text.ToString();
    }

    /// <summary>
    /// Parses <paramref name="payload"/> as lyrics from <paramref name="source"/> would be parsed (no further
    /// preparation: the app's presentation steps do the rest, as for a real lookup).
    /// </summary>
    public static LyricResult Parse(LyricFormat format, string payload, double duration,
                                    LyricSourceId source = LyricSourceId.AppleMusicCache)
    {
        var raw = new RawLyrics(source, format, payload, ReportedDuration: duration);
        return LyricParser.Parse(raw) ?? throw new InvalidOperationException("The invented lyrics did not parse.");
    }
}
