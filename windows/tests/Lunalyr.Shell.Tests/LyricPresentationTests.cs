using Lunalyr.Core.Lyrics;
using Lunalyr.Shell.Lyrics;

namespace Lunalyr.Shell.Tests;

/// <summary>The steps between the lookup's choice and the screen.</summary>
public sealed class LyricPresentationTests
{
    // Phrase-token rule (12 Nov 2026, as on the Mac): a line marked word-timed whose tokens are whole lines or phrases
    // (as saved before the rule, or from another source) gets estimated words before display, like any line-timed line.
    [Fact]
    public void PhraseTimedLinesGetEstimatedWords()
    {
        var phrase = new LyricLine
        {
            Start = 10, End = 14, Text = "Stay here with me tonight", WordTimed = true,
            Words = [new LyricWord("Stay here with me tonight", 10, 14)],
        };
        var real = new LyricLine
        {
            Start = 15, End = 17, Text = "Hold on", WordTimed = true,
            Words = [new LyricWord("Hold", 15, 16), new LyricWord("on", 16, 17)],
        };
        var shown = Assert.IsType<LyricResult.Synced>(LyricPresentation.Prepare(
            new LyricResult.Synced([phrase, real], new LyricProvenance("Word-by-word", "LRCLIB")),
            new LyricQuery("Song", "Artist", null, 200), repairingGaps: false));
        Assert.False(shown.Lines[0].WordTimed);
        Assert.Equal(["Stay", "here", "with", "me", "tonight"], shown.Lines[0].Words.Select(word => word.Text.Trim()));
        Assert.Equal(10, shown.Lines[0].Words[0].Start);
        Assert.True(shown.Lines[0].Words[^1].End <= 14);
        // Real word timing is left as it is.
        Assert.True(shown.Lines[1].WordTimed);
        Assert.Equal([15.0, 16.0], shown.Lines[1].Words.Select(word => word.Start));
    }
}
