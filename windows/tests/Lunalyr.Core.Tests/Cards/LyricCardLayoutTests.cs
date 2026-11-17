using Lunalyr.Core.Cards;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;

namespace Lunalyr.Core.Tests.Cards;

/// <summary>The card's shapes, its lyric stage, the still's lines, the progress bar's shares and the line picking.</summary>
public class LyricCardLayoutTests
{
    private static LyricCardContent Content(CardAspect aspect)
    {
        const string text = "But I ignored your call";
        var words = text.Split(' ').Select((w, i) => new LyricWord(w, 10 + i * 0.3, 10.28 + i * 0.3)).ToList();
        var line = new LyricLine { Start = 10, End = 10.3 * words.Count + 0.4, Text = text, Words = words, WordTimed = true };
        return new LyricCardContent
        {
            Pages = [new CardPage(words.Select(w => w.Text).ToList(), CardAnimation.Create(line, CompositionKind.Editorial, 0))],
            Title = "A Song", Artist = "An Artist", HasArtwork = true, Aspect = aspect, ReduceMotion = true,
        };
    }

    // Ported from LyricCardFormatsTests.swift: testTheLyricStageStaysInsideEveryShape
    [Fact]
    public void TheLyricStageStaysInsideEveryShape()
    {
        foreach (var aspect in CardAspects.All)
        {
            var stage = Content(aspect).LyricStage;
            Assert.True(stage.Width > 300, aspect.ToString());
            Assert.True(stage.Height > 300, aspect.ToString());
            var size = aspect.Size();
            Assert.True(new CardRect(0, 0, size.Width, size.Height).Contains(stage), aspect.ToString());
        }
    }

    // Ported from LyricCardFormatsTests.swift: testEveryShapeIsDrawnAtItsOwnSize (the sizes; the PNG's pixels are
    // checked in the Shell tests)
    [Fact]
    public void EveryShapeHasTheMacsSize()
    {
        Assert.Equal(new CardSize(1080, 1350), CardAspect.Portrait.Size());
        Assert.Equal(new CardSize(1080, 1080), CardAspect.Square.Size());
        Assert.Equal(new CardSize(1080, 1920), CardAspect.Story.Size());
        Assert.Equal(new CardSize(1920, 1080), CardAspect.Landscape.Size());
        Assert.Equal(["4:5", "1:1", "9:16", "16:9"], CardAspects.All.Select(a => a.Title()));
        Assert.Equal(["mp4", "gif", "png"], CardFormats.All.Select(f => f.Raw()));
        Assert.All(CardAspects.All, a => Assert.Equal(a, CardAspects.Parse(a.Raw())));
    }

    // Ported from LyricCardFormatsTests.swift: testAStillHoldsAtMostThreeLinesOneUnderAnother
    [Fact]
    public void AStillHoldsAtMostThreeLinesOneUnderAnother()
    {
        var many = Content(CardAspect.Portrait);
        var animation = many.Pages[0].Animation;
        many = many with { Pages = Enumerable.Range(0, 6).Select(i => new CardPage(["Line", $"{i}"], animation)).ToList() };
        var still = many.StillPage;
        Assert.Equal(2, still.Words.Count(w => w == LyricCardLayout.LineBreak));
        Assert.Equal(3, still.Words.Count(w => w == "Line"));
        Assert.DoesNotContain("3", still.Words);
        var one = many with { Pages = [many.Pages[0]] };
        Assert.DoesNotContain(LyricCardLayout.LineBreak, one.StillPage.Words);
    }

    // Ported from LyricExtrasTests.swift: testCardControlsPersistAndCountOnlyVocalRowsAroundTheCurrentLine (the rows; the
    // settings' half is in the Shell tests)
    [Fact]
    public void CardRowsCountOnlyVocalRowsAroundTheCurrentLine()
    {
        int[] vocalRows = [1, 3, 5, 8, 9];
        Assert.Equal([3, 5, 8, 9], LyricCardLayout.SelectedRows(vocalRows, 5, 1, 2));
        Assert.Equal([5, 8], LyricCardLayout.SelectedRows(vocalRows, 6, 0, 1));
        Assert.Equal([1, 3], LyricCardLayout.SelectedRows(vocalRows, null, 5, 1));
        Assert.Equal([9], LyricCardLayout.SelectedRows(vocalRows, 9, 0, 5));
        Assert.Empty(LyricCardLayout.SelectedRows([], null, 1, 1));
    }

    // Ported from LyricCardExportProgressTests.swift: testFormatsShareTheBarByHowLongTheyTake
    [Fact]
    public void FormatsShareTheBarByHowLongTheyTake()
    {
        var shares = LyricCardExportPlan.ProgressShares([CardFormat.Mp4, CardFormat.Gif, CardFormat.Png], 20, 4, hasSound: true);
        var mp4 = shares[CardFormat.Mp4];
        var gif = shares[CardFormat.Gif];
        var png = shares[CardFormat.Png];
        Assert.Equal(0, mp4.From);
        Assert.Equal(mp4.To, gif.From);
        Assert.Equal(gif.To, png.From);
        Assert.Equal(1, png.To);
        // The GIF is drawn at full size at 25 fps, the video at two-thirds size at 30 (the Mac's 7767ef6).
        Assert.True(gif.To - gif.From > mp4.To - mp4.From);
        var videoAndStill = LyricCardExportPlan.ProgressShares([CardFormat.Mp4, CardFormat.Png], 20, 4, hasSound: true);
        Assert.True(videoAndStill[CardFormat.Mp4].To > 0.9); // 20 s of video is most of the work
        Assert.True(png.To - png.From < 0.05); // one still is a sliver
        Assert.Equal((0.0, 1.0), LyricCardExportPlan.ProgressShares([CardFormat.Png], 3, 1, hasSound: false)[CardFormat.Png]);
        Assert.Empty(LyricCardExportPlan.ProgressShares([], 3, 1, hasSound: false));
    }

    // Ported from LyricCardPopoutTests.swift: testTheRecordingCountdownChangesOnceASecond
    [Fact]
    public void TheRecordingCountdownChangesOnceASecond()
    {
        RecordingUpdate? last = null;
        var shown = new List<int>();
        // Swift's stride(from: 0, through: 3, by: 0.15): 0, 0.15 … and 3 itself.
        for (var i = 0; i <= 20; i++)
        {
            var tick = Math.Min(3.0, i * 0.15);
            if (RecordingUpdate.Next(tick, 3, last) is not { } next) continue;
            last = next;
            if (next is RecordingUpdate.Recording recording) shown.Add(recording.SecondsLeft);
        }
        Assert.Equal([3, 2, 1, 0], shown);
        Assert.Equal(new RecordingUpdate.Recording(3, 3, 0), RecordingUpdate.Next(5, 3, null));
    }

    // Windows' own: the type size follows how much a line holds, as the Mac's does.
    [Fact]
    public void TheCardsTypeSizeShrinksWithLongerLines()
    {
        LyricLine Line(string text) => new()
        {
            Start = 0, End = 3, Text = text, WordTimed = true,
            Words = text.Split(' ').Select((w, i) => new LyricWord(w, i * 0.3, i * 0.3 + 0.25)).ToList(),
        };
        Assert.Equal(224, CardAnimation.Create(Line("Hold on"), CompositionKind.Editorial, 0).CardTypeSize, 6);
        var ten = CardAnimation.Create(Line("one two three four five six seven eight nine ten"), CompositionKind.Editorial, 0);
        Assert.Equal(224 * Math.Sqrt(0.5), ten.CardTypeSize, 6);
        var long_ = CardAnimation.Create(Line(string.Join(' ', Enumerable.Repeat("word", 30))), CompositionKind.Editorial, 0);
        Assert.Equal(126, long_.CardTypeSize);
        // Japanese written without spaces counts about three characters to a word.
        Assert.Equal(13 / 3.0, LyricCardLayout.Weight(["沈むように溶けてゆくように"]), 6);
    }

    // Windows' own: the files an export makes never overwrite a card already there.
    [Fact]
    public void AnAvailableFileNeverOverwritesACard()
    {
        var folder = Directory.CreateTempSubdirectory("card-files-").FullName;
        try
        {
            Assert.Equal(Path.Combine(folder, "Lunalyr Lyric Card.mp4"), LyricCardExportPlan.AvailableFile("Lunalyr Lyric Card", "mp4", folder));
            File.WriteAllBytes(Path.Combine(folder, "Lunalyr Lyric Card.mp4"), [1]);
            File.WriteAllBytes(Path.Combine(folder, "Lunalyr Lyric Card 2.mp4"), [1]);
            Assert.Equal(Path.Combine(folder, "Lunalyr Lyric Card 3.mp4"), LyricCardExportPlan.AvailableFile("Lunalyr Lyric Card", "mp4", folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
