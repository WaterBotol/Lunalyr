// Line construction and the timing labels shared by every parser.
//
// Provenance: the Mac's `makeLine` and part of `precision` (LyricsFetcher.swift) still trace back to the Verso import,
// so they are re-implemented here from this description, not translated:
//   * A line's text is trimmed of spaces; empty text or text made only of note glyphs and dashes ("♪", "-") is an
//     instrumental break with no text and no words.
//   * Without word timing, a line's window is shared out across its words (EstimatedWords).
//   * Precision: "Word-by-word" when every sung line has valid source word timing, "Mixed timing" when some do, else
//     "Line-synced" (LyricTimingRules.Precision).

using Lunalyr.Core.Lyrics.Timing;

namespace Lunalyr.Core.Lyrics.Parsing;

public static class LyricLines
{
    public const string WordByWord = "Word-by-word";
    public const string MixedTiming = "Mixed timing";
    public const string LineSynced = "Line-synced";
    public const string NotSynced = "Not synced";

    /// <summary>Empty text, or only note glyphs and dashes: an instrumental break, not a lyric.</summary>
    public static bool IsInstrumentalMark(string text)
    {
        foreach (var c in text)
        {
            if (!char.IsWhiteSpace(c) && "♪♫•·-–—".IndexOf(c) < 0) return false;
        }
        return true;
    }

    /// <summary>
    /// A line timed only by its window: estimated words (<paramref name="distribute"/>, the built-in word splitting
    /// when null), or an instrumental break.
    /// </summary>
    public static LyricLine MakeLine(string text, double start, double end, WordDistributor? distribute = null)
    {
        var cleaned = TextRules.TrimSpaces(text);
        var instrumental = IsInstrumentalMark(cleaned);
        return new LyricLine
        {
            Start = start,
            End = end,
            Text = instrumental ? "" : cleaned,
            Words = instrumental ? [] : (distribute ?? EstimatedWords.Distribute)(cleaned, start, end),
            IsInstrumental = instrumental,
        };
    }

}
