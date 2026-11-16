using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Parsing;

namespace Lunalyr.Core.Tests.Compositions;

/// <summary>The credit cues of the Mac's CreditsLayerTests (the parsing cases are in CreditsTests).</summary>
public class CreditCueTests
{
    private static LyricLine Credit(string text, double start) =>
        new() { Start = start, End = start + 0.01, Text = text, IsInstrumental = true, Credit = true };

    // Ported from CreditsLayerTests.swift: testChineseCreditLabelsShowInEnglishOnAnEnglishSong
    [Fact]
    public void ChineseCreditLabelsShowInEnglishOnAnEnglishSong()
    {
        Assert.Equal("Lyrics by: Dylan Minnette", LyricCredits.EnglishCredit("作词 : Dylan Minnette"));
        Assert.Equal("Arranged by: John Congleton", LyricCredits.EnglishCredit("编曲：John Congleton"));
        Assert.Equal("Produced by: Ian Kirkpatrick", LyricCredits.EnglishCredit("制作人 : Ian Kirkpatrick"));
        Assert.Null(LyricCredits.EnglishCredit("未经许可，不得翻唱或使用"));
        Assert.Null(LyricCredits.EnglishCredit("TME享有本翻译作品的著作权"));
        // No common English name: dropped.
        Assert.Null(LyricCredits.EnglishCredit("词版权管理方：某某音乐"));
        Assert.Equal("Producer: Max Martin", LyricCredits.EnglishCredit("Producer: Max Martin"));
    }

    // Ported from CreditsLayerTests.swift: testCuesTranslateOnlyWhenTheSongIsNotSungInChinese
    [Fact]
    public void CuesTranslateOnlyWhenTheSongIsNotSungInChinese()
    {
        static LyricLine Sung(string text, double start) => new() { Start = start, End = start + 2, Text = text };
        LyricLine[] english = [Credit("作词 : Dylan Minnette", 0), Credit("未经许可，不得翻唱", 1), Sung("Tell me why you're bored", 10), Sung("Are you bored yet", 14)];
        Assert.Equal(["Lyrics by: Dylan Minnette"], CreditCues.Cues(english).Select(c => c.Text));
        LyricLine[] chinese = [Credit("作词 : 方文山", 0), Sung("刮风这天我试过握着你手", 10)];
        Assert.Equal(["作词 : 方文山"], CreditCues.Cues(chinese).Select(c => c.Text));
    }

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

    // Ported from CreditsLayerTests.swift: testNoTitleCueOnlyRealCredits
    /// <summary>
    /// The old opening "Title - Artist" cue is gone: the opening title (<see cref="OpeningTitle"/>) names the song. Real
    /// credits stay, and the source's own title and artist headings are still dropped.
    /// </summary>
    [Fact]
    public void NoTitleCueOnlyRealCredits()
    {
        var none = LrcParser.Parse("[00:12.00]Hello\n[00:16.00]World", 30);
        Assert.Empty(CreditCues.Cues(none, "Human Nature", "Yot Club"));
        Assert.Empty(CreditCues.Cues([], "Human Nature", "Yot Club"));
        var listed = LrcParser.Parse("[00:00.00]Yot Club - Human Nature\n[00:00.50]Produced by: A B\n[00:12.00]Hello", 30);
        var flagged = LyricCredits.Flagging(listed, "Human Nature", "Yot Club");
        Assert.Equal(["Produced by: A B"], CreditCues.Cues(flagged, "Human Nature", "Yot Club").Select(cue => cue.Text));
    }

    // Ported from CreditsLayerTests.swift: testTitleAndArtistHeadingsAreDroppedAndCreditsShowOnce
    [Fact]
    public void TitleAndArtistHeadingsAreDroppedAndCreditsShowOnce()
    {
        LyricLine[] lines =
        [
            Credit("Human Nature", 0), Credit("Human Nature - Yot Club", 0.5), Credit("Yot Club - Human Nature", 1),
            Credit("Yot Club", 1.5), Credit("Produced by: A B", 2), Credit("Produced by: A B", 3),
            Credit("作词 : 方文山", 4),
        ];
        var cues = CreditCues.Cues(lines, "Human Nature", "Yot Club");
        Assert.Equal(["Produced by: A B", "作词 : 方文山"], cues.Select(cue => cue.Text));
        Assert.Equal([0, 1], cues.Select(cue => cue.Id));
        var bare = CreditCues.Cues([Credit("Human Nature", 0), Credit("Yot Club", 1)], "Human Nature", "Yot Club");
        Assert.Empty(bare);
    }

    // Ported from CreditsLayerTests.swift: testCreditsWaitForTheOpeningTitle
    /// <summary>Credits wait for the opening title to leave rather than sharing the screen with it.</summary>
    [Fact]
    public void CreditsWaitForTheOpeningTitle()
    {
        var lines = LrcParser.Parse("[00:00.00]Produced by: A B\n[00:00.00]Mixed by: C D\n[00:20.00]Hello", 30);
        var cues = CreditCues.Cues(lines, notBefore: 9.2);
        Assert.Equal(9.2, cues[0].Start, 9);
        Assert.Equal(9.2 + CreditCues.Stagger, cues[1].Start, 9);
        var late = LrcParser.Parse("[00:30.00]Produced by: A B\n[00:40.00]Hello", 60);
        Assert.Equal(30, CreditCues.Cues(late, notBefore: 9.2)[0].Start, 9);
    }

    // Ported from CreditsLayerTests.swift: testBracketsInACreditStayInTheLine (the cue's words; the Mac's liftsBacking
    // switch is the credits layer setting its words without lifting backing vocals, as Windows does)
    /// <summary>A credit such as "Remix (Night Edit) by: A B" used to lose its bracket to the backing vocal layer.</summary>
    [Fact]
    public void BracketsInACreditStayInTheLine()
    {
        var cue = CreditCues.Cues([Credit("Remix (Night Edit) by: A B", 1)])[0];
        Assert.NotEmpty(BackingVocals.Split(cue.Words, []).Backing); // lyrics would lift this group
        Assert.Contains("(Night", string.Concat(cue.Words.Select(word => word.Text)), StringComparison.Ordinal);
    }

    [Fact]
    public void CuesTakeTurnsAtTheTopAndFootAndHoldTheirBands()
    {
        LyricLine[] lines = [Credit("Produced by: A B", 0), Credit("Mixed by: C D", 0), Credit("Lyrics by: E F", 0)];
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
        Assert.Equal((0.0, 1.0, 0.0), LayerExit.Pose(LayerExitKind.Credit, 0, 100));
    }

    // Ported from LayerExitTests.swift: testEachKindLeavesItsOwnWayAndHoldsStillUntilItGoes (without the title's exit,
    // which went with the opening "Title - Artist" cue)
    [Fact]
    public void EachKindLeavesItsOwnWayAndHoldsStillUntilItGoes()
    {
        foreach (var (kind, top) in new[] { (LayerExitKind.Credit, true), (LayerExitKind.Backing, false) })
            Assert.Equal((0.0, 1.0, 0.0), LayerExit.Pose(kind, 0, 100, top));
        var credit = LayerExit.Pose(LayerExitKind.Credit, 1, 100, towardTop: true);
        var low = LayerExit.Pose(LayerExitKind.Credit, 1, 100, towardTop: false);
        Assert.True(credit.Offset < 0);
        Assert.True(low.Offset > 0);
        Assert.True(credit.Blur > 0);
        var backing = LayerExit.Pose(LayerExitKind.Backing, 1, 100);
        Assert.True(backing.Scale < 1);
        Assert.Equal(0, backing.Blur);
        Assert.True(backing.Offset > 0);
    }
}
