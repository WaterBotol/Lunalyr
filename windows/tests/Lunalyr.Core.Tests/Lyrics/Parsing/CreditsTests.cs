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
        // The Mac's 1cdde21 changed this case: a first line that is only the title, never sung again, is the file's
        // header, credits or not. A title the song sings again stays a lyric.
        var bare = LyricCredits.Flagging([Line("Human Nature", 0), Line("Hello", 5)], "Human Nature", "Yot Club");
        Assert.Equal([true, false], bare.Select(line => line.Credit == true));
        var sung = LyricCredits.Flagging([Line("Human Nature", 0), Line("Hello", 5), Line("Human nature, oh", 9)], "Human Nature", "Yot Club");
        Assert.All(sung, line => Assert.NotEqual(true, line.Credit));
    }

    // ---- The Mac's next/integration 1cdde21, from the Mac session's description (its tests aren't in the checkout) ----

    [Fact]
    public void AFirstLineOfOnlyTheArtistIsAHeader()
    {
        var shown = LyricCredits.Flagging([Line("Yot Club", 0), Line("Always felt like you", 5), Line("Yot Club", 9)], "Human Nature", "Yot Club");
        Assert.Equal([true, false, false], shown.Select(line => line.Credit == true));
        // Only the first line: later, the artist's name is sung.
        var later = LyricCredits.Flagging([Line("Always felt like you", 0), Line("Yot Club", 5)], "Human Nature", "Yot Club");
        Assert.All(later, line => Assert.NotEqual(true, line.Credit));
        // Plain text drops it the same way.
        Assert.Equal(["Always felt"], LyricCredits.DroppingLeadingCredits(["Yot Club", "Always felt"], "Human Nature", "Yot Club"));
        Assert.Equal(["Always felt", "Human Nature"],
            LyricCredits.DroppingLeadingCredits(["Always felt", "Human Nature"], "Human Nature", "Yot Club"));
    }

    [Fact]
    public void RoleLinesCountInClosingCreditBlocks()
    {
        var lines = new[]
        {
            Line("故事的小黄花", 0), Line("从出生那年就飘着", 5), Line("吉他：蔡科俊Again", 200), Line("贝斯：韦伟", 202), Line("鼓：Gary Novak", 204),
        };
        var shown = LyricCredits.Flagging(lines, "晴天", "周杰倫");
        Assert.Equal([false, false, true, true, true], shown.Select(line => line.Credit == true));
        // The same labels mid-song, with lyrics after them, are sung.
        var middle = LyricCredits.Flagging([Line("故事的小黄花", 0), Line("吉他：蔡科俊Again", 5), Line("从出生那年就飘着", 9)], "晴天", "周杰倫");
        Assert.All(middle, line => Assert.NotEqual(true, line.Credit));
        // Sung words after an everyday label are no names, at the end too.
        var drums = LyricCredits.Flagging([Line("Hold on", 0), Line("Let go", 4), Line("鼓：咚咚", 8), Line("Music: it's all I need", 12)]);
        Assert.All(drums, line => Assert.NotEqual(true, line.Credit));
    }

    [Fact]
    public void NamesMayMixChineseAndLatinScript()
    {
        Assert.True(LyricCredits.IsCredit("吉他：蔡科俊Again", inCredits: true));
        Assert.True(LyricCredits.IsCredit("和声：Jay周杰伦 / 林迈可", inCredits: true));
        Assert.False(LyricCredits.IsCredit("吉他：我们一起again", inCredits: true));
    }

    [Fact]
    public void JapaneseAndKoreanRolesAreCredits()
    {
        foreach (var text in new[] { "作詞：田中太郎", "ギター：田中太郎", "ボーカル：Ado", "작사: 김이나", "편곡 : Someone Else", "기타：이상순" })
        {
            Assert.True(LyricCredits.IsCredit(text, inCredits: true), text);
        }
        // An everyday label in the middle of a song is sung.
        Assert.False(LyricCredits.IsCredit("ギター：田中太郎", inCredits: false));
        var shown = LyricCredits.Flagging([Line("ギター：田中太郎", 0), Line("夜の街を歩く", 5), Line("ベース：山田花子", 200)], "夜", "誰か");
        Assert.Equal([true, false, true], shown.Select(line => line.Credit == true));
    }

    [Fact]
    public void TraditionalAndSimplifiedArtistNamesAreTheSameHeader()
    {
        var shown = LyricCredits.Flagging([Line("周杰伦", 0), Line("故事的小黄花", 5)], "晴天", "周杰倫");
        Assert.Equal([true, false], shown.Select(line => line.Credit == true));
    }

    /// <summary>Agreement compares sung lines only: plain text's credits and header don't count for or against it.</summary>
    [Fact]
    public void PlainTextsCreditsAndHeaderAreNotSung()
    {
        Assert.Equal(["Always felt", "Like you were"],
            LyricCredits.SungLines(["Yot Club", "Lyrics by: Ryan Kaiser", "Always felt", "Like you were", "吉他：蔡科俊Again"], "Human Nature", "Yot Club"));
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
