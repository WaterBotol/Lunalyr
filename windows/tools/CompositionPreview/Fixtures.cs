// Invented lyrics (not from any real song) for previews and rendering tests, built through the real parsers so the
// lines carry what the app gives the layers: word timing from TTML, estimated words and line ends from LRC.

using System.Globalization;
using System.Numerics;
using System.Text;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Render.Scene;

namespace Lunalyr.Tools.CompositionPreview;

public static class Fixtures
{
    /// <summary>One sung word: its text and times, and whether a space follows it in the line.</summary>
    public readonly record struct Sung(string Text, double Start, double End, bool Space = true, bool Backing = false);

    public static readonly Palette Night = new(
        new Vector4(0.075f, 0.063f, 0.125f, 1),
        [new(0.62f, 0.45f, 0.95f, 1), new(0.95f, 0.55f, 0.35f, 1)],
        new Vector4(1, 1, 1, 1),
        new Vector4(1, 0.93f, 0.82f, 1));

    /// <summary>Words spread evenly from <paramref name="start"/>, each <paramref name="length"/> long.</summary>
    public static Sung[] Even(double start, double step, double length, params string[] words) =>
        [.. words.Select((word, index) => new Sung(word, start + index * step, start + index * step + length))];

    /// <summary>A word-timed TTML document (Apple Music's shape) from lines of sung words.</summary>
    public static string Ttml(string language, params Sung[][] lines)
    {
        static string S(double seconds) => seconds.ToString("0.000", CultureInfo.InvariantCulture);
        var text = new StringBuilder();
        text.Append($"""<tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata" xmlns:itunes="http://music.apple.com/lyric-ttml-internal" itunes:timing="Word" xml:lang="{language}"><body><div>""");
        foreach (var line in lines)
        {
            var end = line.Max(word => word.End);
            text.Append($"""<p begin="{S(line[0].Start)}" end="{S(end)}">""");
            var inBacking = false;
            for (var i = 0; i < line.Length; i++)
            {
                var word = line[i];
                if (word.Backing && !inBacking) { text.Append("""<span ttm:role="x-bg">"""); inBacking = true; }
                text.Append($"""<span begin="{S(word.Start)}" end="{S(word.End)}">{System.Security.SecurityElement.Escape(word.Text)}</span>""");
                var closes = inBacking && (i + 1 == line.Length || !line[i + 1].Backing);
                if (closes) { text.Append("</span>"); inBacking = false; }
                if (word.Space && i + 1 < line.Length) text.Append(' ');
            }
            text.Append("</p>");
        }
        text.Append("</div></body></tt>");
        return text.ToString();
    }

    private static LyricResult Parse(LyricFormat format, string payload, double duration)
    {
        var raw = new RawLyrics(LyricSourceId.AppleMusicCache, format, payload, ReportedDuration: duration);
        var result = LyricParser.Parse(raw) ?? throw new InvalidOperationException("The fixture did not parse.");
        if (result is LyricResult.Synced synced)
        {
            var prepared = LyricPauseTiming.Prepare(synced.Lines, synced.Provenance.Source, treatLongPausesAsInstrumental: true);
            return new LyricResult.Synced(prepared.Lines, synced.Provenance);
        }
        return result;
    }

    private static SceneSnapshot Scene(string title, LyricResult? lyrics, IReadOnlyDictionary<int, string>? readings = null,
                                       string? status = null, LyricStyle? style = null) => new()
    {
        Title = title,
        Artist = "Fixture Ensemble",
        Lyrics = lyrics,
        Readings = readings ?? new Dictionary<int, string>(),
        Palette = Night,
        Style = style ?? LyricStyle.Default,
        Status = status,
    };

    /// <summary>
    /// An English word-timed song: an ordinary line, one with backing vocals, a duet part held under the next line, a
    /// long held note, an instrumental break, and a line long enough to wrap.
    /// </summary>
    public static SceneSnapshot English(LyricStyle? style = null)
    {
        Sung[] first = [.. Even(10.0, 0.42, 0.38, "We", "keep", "dreaming", "under"), new("neon", 11.7, 12.05), new("rain", 12.1, 13.2)];
        Sung[] second =
        [
            new("Hold", 13.6, 14.0), new("me", 14.0, 14.3),
            new("(oh", 14.3, 14.7), new("oh)", 14.7, 15.1),
            new("through", 15.1, 15.5), new("the", 15.5, 15.7), new("night", 15.7, 16.6),
        ];
        Sung[] duet = [.. Even(17.0, 0.8, 0.8, "I", "will", "wait", "for"), new("you", 20.2, 21.4)];
        Sung[] answer = [new("Yeah", 19.0, 19.6), new("yeah", 19.6, 20.1)];
        Sung[] held = [new("Stay", 22.0, 22.5), new("with", 22.5, 22.8), new("me", 22.8, 26.4)];
        Sung[] wrapping = Even(33.0, 0.36, 0.34, "Every", "little", "window", "on", "the", "boulevard", "is", "singing",
                               "back", "the", "colours", "of", "the", "morning", "light");
        var payload = Ttml("en", first, second, duet, answer, held, wrapping);
        return Scene("Neon Harbour", Parse(LyricFormat.Ttml, payload, 60), style: style);
    }

    /// <summary>Two unspaced Japanese lines timed by word, with readings.</summary>
    public static SceneSnapshot Japanese(LyricStyle? style = null)
    {
        Sung[] first =
        [
            new("夕焼け", 10.0, 10.6, false), new("の", 10.6, 10.8, false), new("帰り道", 10.9, 11.5, false),
            new("を", 11.5, 11.7, false), new("歩い", 11.8, 12.4, false), new("た", 12.4, 13.0, false),
        ];
        Sung[] second =
        [
            new("窓辺", 13.4, 13.9, false), new("に", 13.9, 14.1, false), new("座っ", 14.2, 14.7, false),
            new("て", 14.7, 14.9, false), new("海", 15.0, 15.4, false), new("を", 15.4, 15.6, false),
            new("見て", 15.7, 16.2, false), new("いた", 16.2, 17.0, false),
        ];
        var readings = new Dictionary<int, string>
        {
            [0] = "yuuyake no kaerimichi o aruita",
            [1] = "madobe ni suwatte umi o miteita",
        };
        return Scene("Evening Road", Parse(LyricFormat.Ttml, Ttml("ja", first, second), 40), readings, style: style);
    }

    /// <summary>An Arabic line, right to left, timed by word.</summary>
    public static SceneSnapshot Arabic(LyricStyle? style = null)
    {
        Sung[] line = Even(10.0, 0.55, 0.5, "نمشي", "معا", "تحت", "ضوء", "القمر");
        Sung[] second = Even(13.6, 0.6, 0.55, "ونغني", "حتى", "الصباح");
        return Scene("Moonlight Walk", Parse(LyricFormat.Ttml, Ttml("ar", line, second), 40), style: style);
    }

    /// <summary>A line with colour emoji between its words.</summary>
    public static SceneSnapshot Emoji(LyricStyle? style = null)
    {
        Sung[] line = Even(10.0, 0.5, 0.45, "Starlight", "🌙✨", "tonight", "🎶", "forever", "💜");
        return Scene("Star Shapes", Parse(LyricFormat.Ttml, Ttml("en", line), 30), style: style);
    }

    /// <summary>A song timed only by line (LRC), with a break before its last line.</summary>
    public static SceneSnapshot LineTimed(LyricStyle? style = null)
    {
        const string lrc = """
            [00:10.00]Paper boats are sailing down the avenue
            [00:14.00]Every window glowing like a lantern
            [00:18.00]We were young and the city was ours
            [00:22.00]Whisper back the colours of the evening
            [00:26.50]
            [00:40.00]And the river carries us home
            """;
        return Scene("Paper Boats", Parse(LyricFormat.Lrc, lrc, 60), style: style);
    }

    /// <summary>Untimed lyrics.</summary>
    public static SceneSnapshot Plain(LyricStyle? style = null)
    {
        const string text = """
            Paper boats are sailing down the avenue
            Every window glowing like a lantern

            We were young and the city was ours
            Whisper back the colours of the evening
            And the river carries us home
            """;
        return Scene("Paper Boats", Parse(LyricFormat.Plain, text, 0), style: style);
    }

    /// <summary>No lyrics: only the calm status text.</summary>
    public static SceneSnapshot NoLyrics(LyricStyle? style = null) => Scene("Quiet Song", null, status: "No lyrics found", style: style);
}
