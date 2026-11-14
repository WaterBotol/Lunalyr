// Lines for checking the six stage layouts (Hinge, Broadside, Portal, Mosaic, Torque, Fable): the Mac's review lines
// from its Build 88 stage tests (fast word runs, a quick three-word burst, glued syllables, the long phrased line, words
// with pictures), built through the real TTML parser as the app gets them.

using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Render.Scene;
using Lunalyr.Shell.Demo;
using static Lunalyr.Shell.Demo.LyricBuilder;

namespace Lunalyr.Tools.CompositionPreview;

public static class StageFixtures
{
    private static SceneSnapshot Scene(string title, string payload, double duration, LyricStyle? style)
    {
        var result = LyricBuilder.Parse(LyricFormat.Ttml, payload, duration);
        if (result is LyricResult.Synced synced)
        {
            var prepared = LyricPauseTiming.Prepare(synced.Lines, synced.Provenance.Source, treatLongPausesAsInstrumental: true);
            result = new LyricResult.Synced(prepared.Lines, synced.Provenance);
        }
        return new SceneSnapshot
        {
            Title = title, Artist = "Fixture Ensemble", Lyrics = result, Readings = new Dictionary<int, string>(),
            Palette = Fixtures.Night, Style = style ?? LyricStyle.Default,
        };
    }

    /// <summary>
    /// A fast song: eleven words (three of them glued syllables) inside 2.1 s from 10 s; twelve words 0.18 s apart from
    /// 13 s; then a line with three words inside 0.1 s and two glued words from 16 s.
    /// </summary>
    public static SceneSnapshot Fast(LyricStyle? style = null)
    {
        string[] texts = ["we", "were", "run", "ning", "out", "of", "time", "so", "we", "nev", "er", "said", "good", "bye"];
        int[] glued = [3, 10, 13];
        Sung[] first = [.. texts.Select((text, i) => new Sung(text, 10 + i * 0.155, 10 + i * 0.155 + 0.14, !glued.Contains(i + 1)))];
        Sung[] second = Even(13.0, 0.18, 0.16, "we", "run", "it", "back", "and", "turn", "it", "up", "til", "the", "lights", "go");
        Sung[] third =
        [
            new("and", 16.0, 16.3), new("then", 16.4, 16.7), new("oh", 16.8, 16.84), new("my", 16.85, 16.89), new("god", 16.9, 17.4),
            new("you", 17.5, 17.8), new("came", 17.9, 18.2), new("back", 18.3, 18.7), new("beau", 18.8, 18.95, false),
            new("ti", 19.0, 19.15, false), new("ful", 19.2, 19.6), new("to", 19.8, 19.95, false), new("night", 20.0, 21.0),
        ];
        return Scene("Run It Back", Ttml("en", first, second, third), 40, style);
    }

    /// <summary>
    /// Long lines: the review videos' line (a quick run, a held word, a pause) from 10 s; a 23-word line a word every
    /// 0.3 s from 18 s; and a line of words with pictures from 26 s.
    /// </summary>
    public static SceneSnapshot Long(LyricStyle? style = null)
    {
        Sung[] mixed =
        [
            new("and", 10.00, 10.12), new("we", 10.15, 10.27), new("keep", 10.30, 10.50), new("running", 10.60, 11.85),
            new("through", 11.90, 12.20), new("the", 12.25, 12.45), new("city", 12.50, 12.95), new("lights", 13.00, 13.50),
            new("until", 14.10, 14.45), new("the", 14.50, 14.65), new("morning", 14.70, 15.30), new("comes", 15.35, 15.80),
        ];
        Sung[] longest = Even(18.0, 0.3, 0.27,
            "And if the night keeps calling out my name I will follow every echo down the empty streets until the morning finds us".Split(' '));
        Sung[] icons = Even(26.0, 0.45, 0.4, "call me home my love the city lights are burning bright".Split(' '));
        return Scene("Morning Comes", Ttml("en", mixed, longest, icons), 40, style);
    }
}
