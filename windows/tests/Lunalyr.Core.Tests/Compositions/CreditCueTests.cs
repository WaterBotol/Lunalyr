using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Parsing;

namespace Lunalyr.Core.Tests.Compositions;

/// <summary>The credit cues of the Mac's CreditsLayerTests (the parsing cases are in CreditsTests).</summary>
public class CreditCueTests
{
    private static LyricLine Credit(string text, double start) =>
        new() { Start = start, End = start + 0.01, Text = text, IsInstrumental = true, Credit = true };

    // Ported from CreditsLayerTests.swift: testCuesArriveOneAfterAnother
    [Fact]
    public void CuesArriveOneAfterAnother()
    {
        var lines = LrcParser.Parse("[00:00.00]Produced by: A B\n[00:00.00]Mixed by: C D\n[00:20.00]Hello", 30);
        var cues = CreditCues.Cues(lines);
        Assert.Equal(2, cues.Count);
        Assert.Equal(cues[0].Start + CreditCues.Stagger, cues[1].Start, 3);
        Assert.Equal(0, CreditCues.Level(cues[0], cues[0].Start - 1));
        Assert.Equal(1, CreditCues.Level(cues[0], cues[0].Start + 1.5));
        Assert.Equal(0, CreditCues.Level(cues[0], cues[0].End + 5));
    }

    // Ported from CreditsLayerTests.swift: testEverySongOpensWithItsTitleAndArtist
    [Fact]
    public void EverySongOpensWithItsTitleAndArtist()
    {
        var none = LrcParser.Parse("[00:12.00]Hello\n[00:16.00]World", 30);
        Assert.Equal(["Human Nature - Yot Club"], CreditCues.Cues(none, "Human Nature", "Yot Club").Select(cue => cue.Text));
        // Not added twice when the lyrics carry it.
        var listed = LrcParser.Parse("[00:00.00]Yot Club - Human Nature\n[00:00.50]Produced by: A B\n[00:12.00]Hello", 30);
        var flagged = LyricCredits.Flagging(listed, "Human Nature", "Yot Club");
        Assert.Equal(2, CreditCues.Cues(flagged, "Human Nature", "Yot Club").Count);
    }

    // Ported from CreditsLayerTests.swift: testTitleAndArtistShowOnlyOnce
    [Fact]
    public void TitleAndArtistShowOnlyOnce()
    {
        LyricLine[] lines =
        [
            Credit("Human Nature", 0), Credit("Human Nature - Yot Club", 0.5), Credit("Yot Club - Human Nature", 1),
            Credit("Yot Club", 1.5), Credit("Produced by: A B", 2), Credit("Produced by: A B", 3),
        ];
        Assert.Equal(["Human Nature - Yot Club", "Produced by: A B"],
                     CreditCues.Cues(lines, "Human Nature", "Yot Club").Select(cue => cue.Text));
        var bare = CreditCues.Cues([Credit("Human Nature", 0), Credit("Yot Club", 1)], "Human Nature", "Yot Club");
        Assert.Equal(["Human Nature - Yot Club"], bare.Select(cue => cue.Text));
    }

    // Ported from CreditsLayerTests.swift: testOpeningCueNamesTheArtistForHighlighting
    [Fact]
    public void OpeningCueNamesTheArtistForHighlighting()
    {
        var cue = CreditCues.Cues([], "Human Nature", "Yot Club")[0];
        Assert.Equal("Human Nature", cue.Title);
        Assert.Equal("Yot Club", cue.Artist);
    }

    // Ported from CreditsLayerTests.swift: testArtistWordsAreMarkedForTheHighlightTile
    [Fact]
    public void ArtistWordsAreMarkedForTheHighlightTile()
    {
        var cue = CreditCues.Cues([], "Human Nature", "Yot Club")[0];
        Assert.Equal(["Human", "Nature", "-", "Yot", "Club"], cue.Words.Select(word => word.Text));
        Assert.Equal(cue.Words[3].Start, cue.HighlightFrom);
        Assert.All(cue.Words.Take(3), word => Assert.True(word.Start < cue.HighlightFrom));
        Assert.All(cue.Words.Zip(cue.Words.Skip(1)), pair => Assert.True(pair.First.Start <= pair.Second.Start));
        Assert.Null(CreditCues.Cues([Credit("Produced by: A B", 1)], "Human Nature", "Yot Club")[1].HighlightFrom);
    }

    // Ported from CreditsLayerTests.swift: testBracketsInATitleOrCreditStayInTheLine (the cue's words; the Mac's
    // liftsBacking switch is the credits layer setting its words without lifting backing vocals, as Windows does)
    [Fact]
    public void BracketsInATitleOrCreditStayInTheLine()
    {
        var cue = CreditCues.Cues([], "Song (Remix)", "Band")[0];
        Assert.NotEmpty(BackingVocals.Split(cue.Words, []).Backing); // lyrics would lift this group
        Assert.Contains("Remix", string.Concat(cue.Words.Select(word => word.Text)), StringComparison.Ordinal);
    }

    [Fact]
    public void CuesTakeTurnsAtTheTopAndFootAndHoldTheirBands()
    {
        LyricLine[] lines = [Credit("Produced by: A B", 0), Credit("Mixed by: C D", 0)];
        var cues = CreditCues.Cues(lines, "Song", "Band");
        Assert.Equal([true, false, true], cues.Select(cue => cue.Top));
        var bands = CreditCues.Bands(cues);
        Assert.Equal(cues[1].End + CreditCues.FadeOut, bands[1].End);
        Assert.False(bands[1].Top);
        // A backing group up while the top band is busy moves to the foot.
        var group = new BackingGroup(0, "oh", 1, 2, [new LyricWord("oh", 1, 2)], [false]);
        Assert.False(BackingVocalLayout.OnTop(group, preferred: true, bands));
    }

    [Fact]
    public void ACuesHoldGrowsWithItsLength()
    {
        var cues = CreditCues.Cues([Credit("Produced by: A B", 0), Credit("Lyrics by: Someone With A Much Longer Name Indeed", 0)]);
        Assert.True(cues[1].End - cues[1].Start > cues[0].End - cues[0].Start);
        Assert.InRange(cues[0].End - cues[0].Start, 5, CreditCues.MinimumHold + 5);
        Assert.Equal(0, CreditCues.ExitProgress(cues[0], cues[0].End));
        Assert.Equal(1, CreditCues.ExitProgress(cues[0], cues[0].End + CreditCues.FadeOut));
    }

    [Fact]
    public void LayerExitsLeaveTheirOwnWay()
    {
        Assert.Equal((-45.0, 1.0, 9.0), LayerExit.Pose(LayerExitKind.Credit, 1, 100, towardTop: true));
        Assert.Equal((45.0, 1.0, 9.0), LayerExit.Pose(LayerExitKind.Credit, 1, 100));
        Assert.Equal((0.0, 1.08, 20.0), LayerExit.Pose(LayerExitKind.Title, 1, 100));
        Assert.Equal((0.0, 1.0, 0.0), LayerExit.Pose(LayerExitKind.Title, 0, 100));
    }
}
