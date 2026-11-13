using Lunalyr.Core.Cards;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Platform.Media;
using Lunalyr.Platform.Wallpaper;
using Lunalyr.Render.Scene;
using Lunalyr.Tests.Shared;

namespace Lunalyr.Shell.Tests.Cards;

/// <summary>The card's PNG, MP4 and GIF: their sizes, frames and files (drawn on WARP, decoded back, never played).</summary>
public class LyricCardExportTests
{
    private static LyricCardContent Content(CardAspect aspect, bool longLine = false)
    {
        var text = longLine ? "I have been waiting all of my life for a night like this one under the city lights" : "But I ignored your call";
        var words = text.Split(' ').Select((w, i) => new LyricWord(w, 10 + i * 0.3, 10.28 + i * 0.3)).ToList();
        var line = new LyricLine { Start = 10, End = 10.3 * words.Count + 0.4, Text = text, Words = words, WordTimed = true };
        return new LyricCardContent
        {
            Pages = [new CardPage(words.Select(w => w.Text).ToList(), CardAnimation.Create(line, CompositionKind.Editorial, 0))],
            Title = "A Song", Artist = "An Artist", HasArtwork = true, Aspect = aspect, ReduceMotion = true,
        };
    }

    private static async Task<Lunalyr.Core.Colour.CoverArt> DecodePng(byte[] png, CardMedia media)
    {
        var path = media.File($"{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, png, TestContext.Current.CancellationToken);
        var art = await WallpaperPicture.DecodeAsync(path, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(art);
        return art!;
    }

    // Ported from LyricCardFormatsTests.swift: testEveryShapeIsDrawnAtItsOwnSize
    [Fact]
    public async Task EveryShapeIsDrawnAtItsOwnSize()
    {
        using var media = new CardMedia();
        var exporter = CardMedia.Exporter();
        foreach (var aspect in CardAspects.All)
        {
            var png = exporter.StillPng(CardMedia.Job(Content(aspect)), scale: 1);
            var art = await DecodePng(png, media);
            Assert.Equal(aspect.Size().Width, art.Width);
            Assert.Equal(aspect.Size().Height, art.Height);
        }
    }

    // Ported from LyricCardFormatsTests.swift: testTheStillIsTwiceTheCardsSizeByDefault
    [Fact]
    public async Task TheStillIsTwiceTheCardsSizeByDefault()
    {
        using var media = new CardMedia();
        var art = await DecodePng(CardMedia.Exporter().StillPng(CardMedia.Job(Content(CardAspect.Portrait))), media);
        Assert.Equal(2160, art.Width);
        Assert.Equal(2700, art.Height);
    }

    // Ported from LyricCardFormatsTests.swift: testTheVideoIsH264AtTheCardsSize
    [Fact]
    public void TheVideoIsH264AtTheCardsSize()
    {
        using var media = new CardMedia();
        var path = media.File("card.mp4");
        Assert.True(CardMedia.Exporter().Video(CardMedia.Job(Content(CardAspect.Square)), path, cancellationToken: TestContext.Current.CancellationToken));
        var info = MediaProbe.Read(path)!;
        Assert.True(info.IsH264);
        Assert.Equal((720, 720), (info.Width, info.Height)); // MP4s are two thirds of the card size
        Assert.True(info.Duration > 1.5);
        Assert.True(info.FrameRate > 20);
        Assert.False(info.HasAudio);
    }

    // Ported from LyricCardFormatsTests.swift: testExportMakesEachChosenFormatAndPublishesThemTogether (the files; the
    // clipboard is the window's and isn't touched by tests)
    [Fact]
    public async Task ExportMakesEachChosenFormat()
    {
        using var media = new CardMedia();
        var files = await CardMedia.Exporter().ExportAsync(CardMedia.Job(Content(CardAspect.Portrait)),
            new HashSet<CardFormat> { CardFormat.Png, CardFormat.Gif, CardFormat.Mp4 }, media.File("cards"),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal([CardFormat.Mp4, CardFormat.Gif, CardFormat.Png], files.Select(f => f.Format));
        foreach (var (_, path) in files) Assert.True(new FileInfo(path).Length > 1000);
    }

    // Ported from LyricCardFormatsTests.swift: testSavingTwiceIntoOneFolderKeepsBothCards
    [Fact]
    public async Task SavingTwiceIntoOneFolderKeepsBothCards()
    {
        using var media = new CardMedia();
        var directory = media.File("cards");
        var exporter = CardMedia.Exporter();
        var job = CardMedia.Job(Content(CardAspect.Square));
        var formats = new HashSet<CardFormat> { CardFormat.Png, CardFormat.Mp4 };
        var first = await exporter.ExportAsync(job, formats, directory, cancellationToken: TestContext.Current.CancellationToken);
        var second = await exporter.ExportAsync(job, formats, directory, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(["Lunalyr Lyric Card.mp4", "Lunalyr Lyric Card.png"], first.Select(f => Path.GetFileName(f.Path)));
        Assert.Equal(["Lunalyr Lyric Card 2.mp4", "Lunalyr Lyric Card 2.png"], second.Select(f => Path.GetFileName(f.Path)));
        foreach (var (_, path) in first.Concat(second)) Assert.True(new FileInfo(path).Length > 1000);
    }

    // Ported from LyricExtrasTests.swift: testTheLyricGIFIsExportedAtFullCardResolution
    [Fact]
    public void TheLyricGifIsExportedAtFullCardResolution()
    {
        using var media = new CardMedia();
        var words = Enumerable.Range(0, 8).Select(i => new LyricWord($"word{i}", 10 + i * 0.5, 10.45 + i * 0.5)).ToList();
        var line = new LyricLine { Start = 10, End = 14, Text = string.Join(' ', words.Select(w => w.Text)), Words = words, WordTimed = true };
        var content = new LyricCardContent
        {
            Pages = [new CardPage(words.Select(w => w.Text).ToList(), CardAnimation.Create(line, CompositionKind.Cutout, 0))],
            Title = "A Song", Artist = "An Artist", ReduceMotion = true,
        };
        var gif = CardMedia.Exporter().Gif(CardMedia.Job(content, cover: false), cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(gif);
        var path = media.File("card.gif");
        File.WriteAllBytes(path, gif!);
        var info = CardImages.ReadGif(path)!;
        Assert.Equal((1080, 1350), (info.Frames[0].Width, info.Frames[0].Height));
    }

    // Ported from LyricExtrasTests.swift: testAnExcerptIncludesTimedAndUntimedPagesInOneGIF
    [Fact]
    public void AnExcerptIncludesTimedAndUntimedPagesInOneGif()
    {
        using var media = new CardMedia();
        var word = new LyricWord("Middle", 10, 10.7);
        var line = new LyricLine { Start = 10, End = 10.8, Text = word.Text, Words = [word], WordTimed = true };
        var content = new LyricCardContent
        {
            Pages = [new CardPage(["Before"]), new CardPage([word.Text], CardAnimation.Create(line, CompositionKind.Cutout, 0)), new CardPage(["After"])],
            Title = "A Song", Artist = "An Artist", ReduceMotion = true,
        };
        var gif = CardMedia.Exporter().Gif(CardMedia.Job(content, cover: false), scale: 0.5, cancellationToken: TestContext.Current.CancellationToken);
        var path = media.File("excerpt.gif");
        File.WriteAllBytes(path, gif!);
        var info = CardImages.ReadGif(path)!;
        Assert.Equal(12, info.Frames.Count);
        Assert.Equal(1.8, info.Frames[0].Delay, 2);
        Assert.Equal(0.08, info.Frames[5].Delay, 2);
        Assert.Equal(1.8, info.Frames[^1].Delay, 2);
    }

    // Ported from LyricExtrasTests.swift: testUntimedLyricCardRendersTheLineAsASingleGIFFrame
    [Fact]
    public void UntimedLyricCardRendersTheLineAsASingleGifFrame()
    {
        using var media = new CardMedia();
        var content = new LyricCardContent { Pages = [new CardPage(["Hold", "on", "to", "the", "light"])], Title = "A Song", Artist = "An Artist" };
        var job = CardMedia.Job(content, cover: false);
        var exporter = CardMedia.Exporter();
        var (width, height, pixels) = exporter.Still(job);
        Assert.Equal((2160, 2700), (width, height));
        // The line is drawn: bright text in the middle band of the card.
        var bright = 0;
        for (var y = height / 3; y < height * 2 / 3; y += 6)
            for (var x = 0; x < width; x += 6)
                if (CardMedia.Brightness(pixels, width, x, y) > 0.85) bright++;
        Assert.True(bright > 200, $"{bright} bright samples");
        var gif = exporter.Gif(job, scale: 0.5, cancellationToken: TestContext.Current.CancellationToken);
        var path = media.File("still.gif");
        File.WriteAllBytes(path, gif!);
        Assert.Single(CardImages.ReadGif(path)!.Frames);
    }

    // Ported from LyricExtrasTests.swift: testTimedLyricCardGIFLoopsAndDrawsTheSelectedStyle (the loop, the delays and the
    // size; the Mac's pixel comparison between styles and its pasteboard copy are not ported)
    [Fact]
    public void TimedLyricCardGifLoops()
    {
        using var media = new CardMedia();
        LyricWord[] words = [new("Hold", 10, 10.35), new("the light", 10.4, 10.95)];
        var line = new LyricLine { Start = 10, End = 11.2, Text = "Hold the light", Words = words, WordTimed = true };
        var content = new LyricCardContent
        {
            Pages = [new CardPage(["Hold", "the light"], CardAnimation.Create(line, CompositionKind.Spoken, 0))],
            Title = "A Song", Artist = "An Artist", ReduceMotion = true,
        };
        var gif = CardMedia.Exporter().Gif(CardMedia.Job(content, cover: false), scale: 0.5, cancellationToken: TestContext.Current.CancellationToken);
        var path = media.File("spoken.gif");
        File.WriteAllBytes(path, gif!);
        var info = CardImages.ReadGif(path)!;
        Assert.Equal(0, info.LoopCount);
        Assert.True(info.Frames.Count > 12);
        Assert.Equal(0.08, info.Frames[0].Delay, 2);
        Assert.True(info.Frames.Count * info.Frames[0].Delay >= line.End - line.Start);
        Assert.Equal((540, 675), (info.Frames[0].Width, info.Frames[0].Height));
    }

    // Ported from LyricExtrasTests.swift: testRightToLeftCardsSetTheLineFromTheRight
    [Fact]
    public void RightToLeftCardsSetTheLineFromTheRight()
    {
        double Centre(string[] words)
        {
            var content = new LyricCardContent { Pages = [new CardPage(words)] };
            var (width, height, pixels) = CardMedia.Exporter().Still(CardMedia.Job(content, cover: false), 0.5);
            double sum = 0, count = 0;
            for (var y = height / 3; y < height * 2 / 3; y += 2)
                for (var x = 0; x < width; x += 2)
                    if (CardMedia.Brightness(pixels, width, x, y) > 0.85)
                    {
                        sum += x;
                        count++;
                    }
            return sum / Math.Max(1, count) / width;
        }
        Assert.True(Centre(["אני", "אוהב"]) > 0.5);
        Assert.True(Centre(["I", "love"]) < 0.5);
    }
}
