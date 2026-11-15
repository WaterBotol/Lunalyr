using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Parsing;

namespace Lunalyr.Core.Tests.Lyrics.Parsing;

public class BackingVocalsTests
{
    private static List<LyricWord> Words(params (string Text, double Start)[] pieces) =>
        pieces.Select(piece => new LyricWord(piece.Text, piece.Start, piece.Start + 0.3)).ToList();

    // Ported from BackingVocalsTests.swift: testBracketedWordsAreLiftedOutOfTheLead
    [Fact]
    public void BracketedWordsAreLiftedOutOfTheLead()
    {
        var split = BackingVocals.Split(Words(("Hold", 1), ("me", 1.3), ("(oh", 1.6), ("oh)", 1.9), ("tonight", 2.2)), [false, false, false, false, false]);
        Assert.Equal(["Hold", "me", "tonight"], split.Main.Select(word => word.Text));
        Assert.Equal(["oh oh"], split.Backing.Select(group => group.Text));
        Assert.Equal(1.6, split.Backing[0].Start, 0.001);
        Assert.Equal(2.2, split.Backing[0].End, 0.001);
        Assert.Equal(split.Main.Count, split.MainGlue.Count);
    }

    // Ported from BackingVocalsTests.swift: testASingleBracketedWordAndSeveralGroups
    [Fact]
    public void ASingleBracketedWordAndSeveralGroups()
    {
        var split = BackingVocals.Split(Words(("(ooh)", 0), ("Stay", 0.4), ("(yeah)", 0.8), ("here", 1.2), ("(ha", 1.6), ("ha)", 1.9)),
            Enumerable.Repeat(false, 6).ToList());
        Assert.Equal(["Stay", "here"], split.Main.Select(word => word.Text));
        Assert.Equal(["ooh", "yeah", "ha ha"], split.Backing.Select(group => group.Text));
    }

    // Ported from BackingVocalsTests.swift: testALineWithNoBracketsIsUntouched
    [Fact]
    public void ALineWithNoBracketsIsUntouched()
    {
        var line = Words(("Plain", 0), ("words", 0.4));
        var split = BackingVocals.Split(line, [false, true]);
        Assert.Equal(line, split.Main);
        Assert.Equal([false, true], split.MainGlue);
        Assert.Empty(split.Backing);
    }

    // Ported from BackingVocalsTests.swift: testAWordAfterALiftedGroupIsNotGluedToIt
    [Fact]
    public void AWordAfterALiftedGroupIsNotGluedToIt()
    {
        var split = BackingVocals.Split(Words(("Go", 0), ("(oh)", 0.4), ("now", 0.8)), [false, false, true]);
        Assert.Equal(["Go", "now"], split.Main.Select(word => word.Text));
        Assert.Equal([false, false], split.MainGlue);
    }

    // Ported from BackingVocalsTests.swift: testAnUnclosedBracketTakesTheRestOfTheLine
    [Fact]
    public void AnUnclosedBracketTakesTheRestOfTheLine()
    {
        var split = BackingVocals.Split(Words(("Stay", 0), ("(oh", 0.4), ("oh", 0.8)), [false, false, false]);
        Assert.Equal(["Stay"], split.Main.Select(word => word.Text));
        Assert.Equal(["oh oh"], split.Backing.Select(group => group.Text));
    }

    // Ported from BackingVocalsTests.swift: testGroupsKeepTheirWordsWithoutBrackets
    [Fact]
    public void GroupsKeepTheirWordsWithoutBrackets()
    {
        var split = BackingVocals.Split(Words(("Go", 0), ("(oh", 0.4), ("oh)", 0.7), ("now", 1)), [false, false, false, false]);
        Assert.Equal(["oh", "oh"], split.Backing[0].Words.Select(word => word.Text));
        Assert.Equal(0.7, split.Backing[0].Words[1].Start, 0.001);
        Assert.Equal(2, split.Backing[0].Glue.Count);
    }

    // Ported from BackingVocalsTests.swift: testTheLayerEasesInHoldsAndLingersAfterAQuickSung
    [Fact]
    public void TheLayerEasesInHoldsAndLingersAfterAQuickSung()
    {
        var group = new BackingGroup(0, "oh", 10, 10.2, [new LyricWord("oh", 10, 10.2)], [false]);
        Assert.Equal(0, Lunalyr.Core.Compositions.BackingVocalLayout.Level(group, 9.5));
        Assert.True(Lunalyr.Core.Compositions.BackingVocalLayout.Level(group, 10.2) > 0);
        Assert.True(Lunalyr.Core.Compositions.BackingVocalLayout.Level(group, 9.95) < 1);
        Assert.Equal(1, Lunalyr.Core.Compositions.BackingVocalLayout.Level(group, 11.0)); // held past the 0.2 s it was sung
        Assert.True(Lunalyr.Core.Compositions.BackingVocalLayout.Level(group, 11.5) > 0);
        Assert.Equal(0, Lunalyr.Core.Compositions.BackingVocalLayout.Level(group, 13));
    }
}
