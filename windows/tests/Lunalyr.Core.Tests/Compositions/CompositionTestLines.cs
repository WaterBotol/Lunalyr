using Lunalyr.Core.Lyrics;

namespace Lunalyr.Core.Tests.Compositions;

/// <summary>The Mac composition tests' line helpers.</summary>
internal static class CompositionTestLines
{
    /// <summary>Words <paramref name="step"/> apart from <paramref name="start"/>, each <paramref name="length"/> long.</summary>
    public static LyricWord[] Words(string[] texts, double start, double step = 0.5, double length = 0.4) =>
        [.. texts.Select((text, index) => new LyricWord(text, start + index * step, start + index * step + length))];

    /// <summary>A line of <paramref name="words"/>, ending at its last word's end unless told otherwise.</summary>
    public static LyricLine Line(IReadOnlyList<LyricWord> words, double? end = null, bool wordTimed = true) => new()
    {
        Start = words.Count > 0 ? words[0].Start : 0,
        End = end ?? (words.Count > 0 ? words[^1].End : 0),
        Text = string.Join(" ", words.Select(word => word.Text)),
        Words = words,
        WordTimed = wordTimed,
    };

    /// <summary>LyricLayerFlowTests' <c>line(_:start:step:length:wordTimed:)</c>.</summary>
    public static LyricLine Line(string[] texts, double start, double step = 0.5, double length = 0.4, bool wordTimed = true) =>
        Line(Words(texts, start, step, length), wordTimed: wordTimed);
}
