using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Tests.Compositions;

/// <summary>
/// The lines the Mac's stage layout tests share (StagePhrasesTests' even and mixed lines, StageReadingTests' Japanese
/// line, MosaicFastVideoTests' fast and burst lines, FableRenderTests' long line), with the same words and times.
/// </summary>
internal static class StageLines
{
    /// <summary>Evenly sung words, <paramref name="step"/> apart, each sung for 88% of the step (StagePhrasesTests.even).</summary>
    public static List<LyricWord> Even(string text, double step = 0.62, double from = 1) =>
        [.. text.Split(' ').Select((w, i) => new LyricWord(w, from + i * step, from + i * step + step * 0.88))];

    /// <summary>The review videos' long line: a quick three-word run, a held word (1.25 s), a 0.6 s pause before "until".</summary>
    public static List<LyricWord> Mixed =>
    [
        new("and", 1.00, 1.12), new("we", 1.15, 1.27), new("keep", 1.30, 1.50), new("running", 1.60, 2.85),
        new("through", 2.90, 3.20), new("the", 3.25, 3.45), new("city", 3.50, 3.95), new("lights", 4.00, 4.50),
        new("until", 5.10, 5.45), new("the", 5.50, 5.65), new("morning", 5.70, 6.30), new("comes", 6.35, 6.80),
    ];

    /// <summary>Words <paramref name="step"/> apart from 1 s, each sung for 90% of the step (StageReadingTests.words).</summary>
    public static List<LyricWord> Words(string[] texts, double step, double from = 1) =>
        [.. texts.Select((text, i) => new LyricWord(text, from + i * step, from + i * step + step * 0.9))];

    /// <summary>君の名前を呼んで夜空に消えた, sung a character at a time (StageReadingTests.japanese).</summary>
    public static List<LyricWord> Japanese => Words(["君", "の", "名", "前", "を", "呼", "ん", "で", "夜", "空", "に", "消", "え", "た"], 0.22);

    public static bool[] JapaneseGlue => [false, true, false, true, true, false, true, true, false, true, true, false, true, true];

    /// <summary>"we were running out of time so we never said goodbye": 14 tokens, 11 words, 0.155 s apart.</summary>
    public static (List<LyricWord> Words, bool[] Glue) Fast
    {
        get
        {
            string[] texts = ["we", "were", "run", "ning", "out", "of", "time", "so", "we", "nev", "er", "said", "good", "bye"];
            var words = texts.Select((text, i) => new LyricWord(text, 1 + i * 0.155, 1 + i * 0.155 + 0.14)).ToList();
            int[] glued = [3, 10, 13];
            return (words, [.. texts.Select((_, i) => glued.Contains(i))]);
        }
    }

    /// <summary>"and then oh my god you came back beautiful tonight": "oh my god" inside 0.1 s, beau·ti·ful and to·night glued.</summary>
    public static (List<LyricWord> Words, bool[] Glue) Burst
    {
        get
        {
            List<LyricWord> words =
            [
                new("and", 1.0, 1.3), new("then", 1.4, 1.7), new("oh", 1.8, 1.84), new("my", 1.85, 1.89), new("god", 1.9, 2.4),
                new("you", 2.5, 2.8), new("came", 2.9, 3.2), new("back", 3.3, 3.7), new("beau", 3.8, 3.95), new("ti", 4.0, 4.15),
                new("ful", 4.2, 4.6), new("to", 4.8, 4.95), new("night", 5.0, 6.0),
            ];
            int[] glued = [9, 10, 12];
            return (words, [.. words.Select((_, i) => glued.Contains(i))]);
        }
    }

    /// <summary>FableRenderTests' long line, a word every 0.3 s.</summary>
    public static List<LyricWord> Long =>
        Words("And if the night keeps calling out my name I will follow every echo down the empty streets until the morning finds us".Split(' '), 0.3);

    public static List<StageRun> Runs(IReadOnlyList<LyricWord> words, IReadOnlyList<bool>? glue = null) =>
        StageRun.Make(words, glue ?? new bool[words.Count]);
}
