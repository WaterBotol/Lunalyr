using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Parsing;

namespace Lunalyr.Core.Tests.Lyrics.Parsing;

/// <summary>The parsing cases of the Mac's CreditsLayerTests (the credit cues and layer belong to the display workstream).</summary>
public class CreditsTests
{
    private static LyricLine Line(string text, double start) =>
        new() { Start = start, End = start + 2, Text = text };

    // Ported from CreditsLayerTests.swift: testProducedByIsACredit
    [Fact]
    public void ProducedByIsACredit()
    {
        foreach (var text in new[] { "Produced by: Max Martin", "produced by：Max Martin", "Prod. by: Metro Boomin",
                     "Produced By: Metro Boomin & Southside", "Produced by Max Martin", "Written by Tove Lo & Ali Payami" })
        {
            Assert.True(LyricCredits.IsCredit(text), text);
        }
    }

    // Ported from CreditsLayerTests.swift: testColonlessCreditNeedsTheStartOfTheSong
    [Fact]
    public void ColonlessCreditNeedsTheStartOfTheSong()
    {
        Assert.False(LyricCredits.IsCredit("Produced by Max Martin", inCredits: false));
        Assert.False(LyricCredits.IsCredit("Written by the stars tonight"));
        Assert.False(LyricCredits.IsCredit("Made by hand and by heart"));
    }

    // Ported from CreditsLayerTests.swift: testLRCKeepsCreditsApartFromLyrics
    [Fact]
    public void LrcKeepsCreditsApartFromLyrics()
    {
        var lines = LrcParser.Parse("[00:00.00]Produced by: Max Martin\n[00:00.00]Written by: Someone Else\n[00:12.00]Hold on\n[00:16.00]Let go", 30);
        var credits = lines.Where(line => line.Credit == true).ToList();
        Assert.Equal(["Produced by: Max Martin", "Written by: Someone Else"], credits.Select(line => line.Text));
        Assert.All(credits, line => Assert.True(line.IsInstrumental && line.Words.Count == 0));
        Assert.Equal(["Hold on", "Let go"], lines.Where(line => !line.IsInstrumental).Select(line => line.Text));
    }

    // Ported from CreditsLayerTests.swift: testTrackHeadingVariantsAreCredits
    [Fact]
    public void TrackHeadingVariantsAreCredits()
    {
        foreach (var heading in new[] { "Human Nature - Yot Club", "Yot Club - Human Nature", "Human Nature (Yot Club)", "Human Nature by Yot Club",
                     "Yot Club: Human Nature", "HUMAN NATURE / YOT CLUB", "Human Nature – Yot Club & Friends" })
        {
            var shown = LyricCredits.Flagging([Line(heading, 0), Line("Always felt like you", 5)], "Human Nature", "Yot Club");
            Assert.True(shown[0].Credit, heading);
            Assert.Null(shown[1].Credit);
        }
    }

    // Ported from CreditsLayerTests.swift: testALoneTitleOnlyCountsAheadOfCredits
    [Fact]
    public void ALoneTitleOnlyCountsAheadOfCredits()
    {
        var heading = LyricCredits.Flagging([Line("Human Nature", 0), Line("Produced by: A B", 1), Line("Hello", 5)], "Human Nature", "Yot Club");
        Assert.Equal([true, true, false], heading.Select(line => line.Credit == true));
        var chorus = LyricCredits.Flagging([Line("Hello", 0), Line("Human Nature", 5), Line("Hello again", 9)], "Human Nature", "Yot Club");
        Assert.All(chorus, line => Assert.NotEqual(true, line.Credit));
        var bare = LyricCredits.Flagging([Line("Human Nature", 0), Line("Hello", 5)], "Human Nature", "Yot Club");
        Assert.All(bare, line => Assert.NotEqual(true, line.Credit));
    }

    // Ported from CreditsLayerTests.swift: testAnySourcesCreditsAreFlaggedWhenShown (the flagging and plain-text parts;
    // LyricsViewModel.presentation belongs to the display workstream)
    [Fact]
    public void AnySourcesCreditsAreFlagged()
    {
        static LyricLine Timed(string text, double start) => new()
        {
            Start = start, End = start + 2, Text = text, Words = [new LyricWord(text, start, start + 2)], WordTimed = true,
        };
        var shown = LyricCredits.Flagging([Timed("Human Nature - Yot Club", 0), Timed("Lyrics by：Ryan Kaiser/Harrison Lipton", 2),
            Timed("Produced by：Ryan Kaiser/Harrison Lipton", 4), Timed("Always felt like you were gonna stick around", 8)]);
        Assert.Equal(3, shown.Count(line => line.Credit == true));
        Assert.Equal(["Always felt like you were gonna stick around"], shown.Where(line => !line.IsInstrumental).Select(line => line.Text));
        Assert.Equal(["Always felt"], LyricCredits.DroppingLeadingCredits(["Human Nature - Yot Club", "Lyrics by: A B", "", "Always felt"]));
    }
}
