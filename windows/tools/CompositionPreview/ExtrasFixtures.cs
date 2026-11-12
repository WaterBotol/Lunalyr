// Invented songs for the wallpaper extras: word-by-word readings (below and instead), credits, and highlight tiles.
// Built as Fixtures builds its songs, through the real parsers.

using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Render.Scene;
using Lunalyr.Shell.Demo;
using static Lunalyr.Shell.Demo.LyricBuilder;

namespace Lunalyr.Tools.CompositionPreview;

public static class ExtrasFixtures
{
    private static LyricResult.Synced Parse(LyricFormat format, string payload, double duration)
    {
        var synced = (LyricResult.Synced)LyricBuilder.Parse(format, payload, duration);
        var prepared = LyricPauseTiming.Prepare(synced.Lines, synced.Provenance.Source, treatLongPausesAsInstrumental: true);
        return new LyricResult.Synced(prepared.Lines, synced.Provenance);
    }

    private static SceneSnapshot Scene(string title, LyricResult lyrics, LyricStyle? style) => new()
    {
        Title = title,
        Artist = "Fixture Ensemble",
        Lyrics = lyrics,
        Palette = Fixtures.Night,
        Style = style ?? LyricStyle.Default,
    };

    /// <summary>
    /// Two Japanese lines spaced by phrase (as many lyric files write them), timed by word, with word-by-word readings
    /// as the romanizer gives them: each phrase is read under itself. With <see cref="ReadingPlacement.Instead"/> the
    /// readings are the words shown.
    /// </summary>
    public static SceneSnapshot JapaneseWords(LyricStyle? style = null)
    {
        style ??= LyricStyle.Default;
        static Sung W(string text, double start, double end, bool space = false) => new(text, start, end, space);
        Sung[] first = [W("夕焼け", 10.0, 10.6), W("の", 10.6, 10.8, true), W("帰り道", 10.9, 11.5), W("を", 11.5, 11.7, true), W("歩い", 11.8, 12.4), W("た", 12.4, 13.0)];
        Sung[] second = [W("窓辺", 13.4, 13.9), W("に", 13.9, 14.1, true), W("座っ", 14.2, 14.7), W("て", 14.7, 14.9, true), W("海", 15.0, 15.4), W("を", 15.4, 15.6, true), W("見て", 15.7, 16.2), W("いた", 16.2, 17.0)];
        string[][] read = [["yuuyake", "no", "kaerimichi", "o", "arui", "ta"], ["madobe", "ni", "suwat", "te", "umi", "o", "mite", "ita"]];
        var lyrics = Parse(LyricFormat.Ttml, Ttml("ja", first, second), 40);
        var words = new Dictionary<int, IReadOnlyList<LyricWord>>();
        for (var row = 0; row < lyrics.Lines.Count && row < read.Length; row++)
        {
            var line = lyrics.Lines[row];
            if (line.Words.Count != read[row].Length) continue;
            words[row] = [.. line.Words.Zip(read[row], (word, text) => new LyricWord(text, word.Start, word.End))];
        }
        var lines = words.ToDictionary(pair => pair.Key, pair => string.Join(" ", pair.Value.Select(word => word.Text)));
        return Scene("Evening Road", lyrics, style) with { WordReadings = words, Readings = lines };
    }

    /// <summary>The English fixture with two production credits, credits on: one after the other, top band then foot.</summary>
    public static SceneSnapshot Credits(LyricStyle? style = null)
    {
        var english = Fixtures.English(style);
        var synced = (LyricResult.Synced)english.Lyrics!;
        LyricLine[] credits =
        [
            new() { Start = 0, End = 0.01, Text = "Produced by: Mira Okafor", IsInstrumental = true, Credit = true },
            new() { Start = 0, End = 0.01, Text = "Written by: Jun Ishida & Lea Brandt", IsInstrumental = true, Credit = true },
        ];
        var lyrics = new LyricResult.Synced([.. credits, .. synced.Lines], synced.Provenance);
        return english with { Lyrics = lyrics, Style = (style ?? english.Style) with { Credits = true } };
    }

    /// <summary>The English fixture with "Cutout · Highlight": a tile pressed behind each sung word.</summary>
    public static SceneSnapshot Highlight(LyricStyle? style = null) =>
        Fixtures.English((style ?? LyricStyle.Default) with { Reveal = "cutoutHighlight", HighlightMode = "custom", HighlightCustom = "#2BD96B" });
}
