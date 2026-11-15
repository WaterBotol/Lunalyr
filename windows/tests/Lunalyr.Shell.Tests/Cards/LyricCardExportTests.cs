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

    private static Lunalyr.Core.Colour.CoverArt Painted(int width, int height, Func<double, double, (double R, double G, double B)> colour)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var (r, g, b) = colour(x, y);
                var o = (y * width + x) * 4;
                pixels[o] = (byte)Math.Round(b * 255);
                pixels[o + 1] = (byte)Math.Round(g * 255);
                pixels[o + 2] = (byte)Math.Round(r * 255);
                pixels[o + 3] = 255;
            }
        return new Lunalyr.Core.Colour.CoverArt(width, height, pixels);
    }

    private static Lunalyr.Shell.Cards.LyricCardJob WithCover(LyricCardContent content, Lunalyr.Core.Colour.CoverArt cover) => new()
    {
        Content = content,
        Look = Lunalyr.Render.Cards.CardLook.ForCard(Lunalyr.Render.Cards.CardLook.PaletteFor(cover), cover, LyricStyle.Default),
    };

    /// <summary>The bounds of pixels brighter than <paramref name="threshold"/> in every channel, in a rectangle of a BGRA frame.</summary>
    private static (int Width, int Height, int MaxX) WhiteBounds(byte[] bgra, int width, int top, int bottom, int left, int right, double threshold)
    {
        int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
        for (var y = top; y < bottom; y += 2)
            for (var x = left; x < right; x += 2)
            {
                var o = (y * width + x) * 4;
                if (bgra[o + 2] / 255.0 <= threshold || bgra[o + 1] / 255.0 <= threshold || bgra[o] / 255.0 <= threshold) continue;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
        return maxX < 0 ? (0, 0, -1) : (maxX - minX + 1, maxY - minY + 1, maxX);
    }

    private static List<byte[]> GifFrames(Lunalyr.Shell.Cards.LyricCardJob job)
    {
        var frames = new List<byte[]>();
        Assert.True(CardMedia.Exporter().RenderFrames(job, 0.5, LyricCardFrames.GifCadence, LyricCardFrames.GifFrameBudget(job.Content.Pages.Count), null,
            (pixels, _, _) =>
            {
                frames.Add((byte[])pixels.Clone());
                return true;
            }, cancellationToken: TestContext.Current.CancellationToken));
        return frames;
    }

    // Ported from LyricExtrasTests.swift: testLongLinkedGIFKeepsTheFullCoverAndLyricsAtBothEnds (the GIF's first and last
    // frames as drawn, before the GIF's palette would blur the colours measured; the lyrics' check adapted, see below)
    [Fact]
    public void LongLinkedGifKeepsTheFullCoverAndLyricsAtBothEnds()
    {
        // A dusk gradient at 50 degrees with a warm disc in the middle, 160 x 120.
        var cover = Painted(160, 120, (x, y) =>
        {
            double dx = x - 80, dy = y - 60;
            if (dx * dx + dy * dy < 37 * 37) return (1, 0.76, 0.51);
            var t = Math.Clamp((x * Math.Cos(50 * Math.PI / 180) + (120 - y) * Math.Sin(50 * Math.PI / 180)) / 195, 0, 1);
            return t < 0.5 ? (0.19 + 0.8 * t, 0.15 + 0.16 * t, 0.40 + 0.12 * t) : (0.59 + 0.68 * (t - 0.5), 0.23 + 0.62 * (t - 0.5), 0.46 - 0.2 * (t - 0.5));
        });
        var labels = "Tonight I never thought you would end up waiting with me for this long again".Split(' ');
        var words = labels.Select((label, i) => new LyricWord(label, 11 + i * 0.26, 11 + i * 0.26 + 0.24)).ToList();
        var line = new LyricLine { Start = 10, End = 15, Text = string.Join(' ', labels), Words = words, WordTimed = true };
        var content = new LyricCardContent
        {
            Pages = [new CardPage(labels, CardAnimation.Create(line, CompositionKind.ConstellationEditorial, 1))],
            Title = "Where the Night Begins", Artist = "June Arcade & Noa Vale", HasArtwork = true, ReduceMotion = true,
        };
        var frames = GifFrames(WithCover(content, cover));
        const int width = 540;
        foreach (var (frame, index) in new[] { (frames[0], 0), (frames[^1], frames.Count - 1) })
        {
            double Red(int x, int y) => frame[(y * width + x) * 4 + 2] / 255.0;
            Assert.True(Red(75, 593) > 0.85, "inside the cover");
            Assert.True(Red(75, 654) < 0.60, "below the cover");
            Assert.True(WhiteBounds(frame, width, 561, 598, 132, 456, 0.65).Width > 200, "the song title sits beside the cover at the bottom");
            Assert.True(WhiteBounds(frame, width, 598, 632, 132, 456, 0.6).Width > 160, "the artist credit sits below the title");
            Assert.True(WhiteBounds(frame, width, 628, 652, 445, 516, 0.55).MaxX > 475, "the signature sits at the bottom");
            // Adapted: the lyrics are only checked to be there (at 0.35, over 20 pixels). The Mac fits its laid-out graph to the
            // card (up to 2.6 times); Windows' constellation layouts lay theirs out to fill the stage they are given, which
            // on a portrait card is a narrow column, so the Mac's 310-pixel width isn't reached; and Windows' Reduce Motion
            // entrance is still fading the first word in on the first frame.
            var lyrics = WhiteBounds(index == 0 ? frames[Math.Min(3, frames.Count - 1)] : frame, width, 42, 523, 15, 525, 0.35).Width;
            Assert.True(lyrics > 20, $"lyric ink {lyrics} px wide on frame {index}");
        }
    }

    // Ported from LyricExtrasTests.swift: testRollingGIFFocusWordIsLargeEvenWithACover
    [Fact]
    public void RollingGifFocusWordIsLargeEvenWithACover()
    {
        var cover = Painted(120, 120, (_, _) => (0.12, 0.08, 0.30));
        var word = new LyricWord("Light", 10, 11.2);
        var line = new LyricLine { Start = 10, End = 11.2, Text = word.Text, Words = [word], WordTimed = true };
        var content = new LyricCardContent
        {
            Pages = [new CardPage([word.Text], CardAnimation.Create(line, CompositionKind.Spoken, 0))],
            Title = "A Song", Artist = "An Artist", HasArtwork = true, ReduceMotion = true,
        };
        var frames = GifFrames(WithCover(content, cover));
        var ink = WhiteBounds(frames[8], 540, 165, 540, 30, 510, 0.72);
        Assert.True(ink.Height >= 42, $"text ink {ink.Height} px high in the 540 px wide card");
        Assert.True(ink.Width >= 350, $"the focus word, {ink.Width} px wide, should fill most of the 540 px card");
    }

    // Ported from LyricRunReadingTests.swift: testACardsColoursComeFromTheCoverItShows
    [Fact]
    public void ACardsColoursComeFromTheCoverItShows()
    {
        // No cover yet: the neutral colours.
        Assert.Equal(Lunalyr.Core.Colour.AlbumWallpaperPalette.Fallback.ArtworkStops,
                     Lunalyr.Render.Cards.CardLook.AlbumOf(PaletteMapper.Fallback).ArtworkStops);
        // A red cover gives red stops.
        var stops = Lunalyr.Render.Cards.CardLook.PaletteFor(Painted(64, 64, (_, _) => (0.85, 0.1, 0.1))).Accents;
        Assert.NotEmpty(stops);
        foreach (var stop in stops)
        {
            Assert.True(stop.X > stop.Y * 2, $"{stop}");
            Assert.True(stop.X > stop.Z * 2, $"{stop}");
        }
    }

    // Ported from LyricRunReadingTests.swift: testEchoAndCardsSetTheLineAsItReads (the card's half)
    [Fact]
    public void CardsSetTheLineAsItReads()
    {
        string[] characters = ["君", "の", "声", "が", "沈", "む", "よ", "う", "に"];
        var spaced = new LyricLine
        {
            Start = 0, End = characters.Length * 0.3, Text = "君の声が 沈むように", WordTimed = true,
            Words = characters.Select((c, i) => new LyricWord(c, i * 0.3, i * 0.3 + 0.3)).ToList(),
        };
        Assert.Equal(["君の声が", "沈むように"], Lunalyr.Shell.Cards.LyricCardBuilder.Words(spaced, null));
        // Latin syllables come together as their words; punctuation stays.
        var tonight = new LyricLine
        {
            Start = 0, End = 2, Text = "So beautiful, tonight", WordTimed = true,
            Words = [new("So", 0, 0.3), new("beau", 0.3, 0.6), new("ti", 0.6, 0.9), new("ful", 0.9, 1.2), new(",", 1.2, 1.2), new("tonight", 1.2, 2)],
        };
        Assert.Equal(["So", "beautiful,", "tonight"], Lunalyr.Shell.Cards.LyricCardBuilder.Words(tonight, null));
        // A card counts an unspaced line by its length, not as one word.
        Assert.Equal(3, LyricCardLayout.Weight(["I", "love", "you"]));
        Assert.Equal(13.0 / 3, LyricCardLayout.Weight(["沈むように溶けてゆくように"]), 9);
    }

    // The Mac's cards draw a line and its reading, never a translation: Show translations under lyrics (on in the
    // wallpaper's style the card copies) doesn't reach them.
    [Fact]
    public void ACardNeverDrawsATranslation()
    {
        var content = Content(CardAspect.Portrait);
        var translated = content.Pages[0].Animation! with { Line = content.Pages[0].Animation!.Line with { Translation = "Pero ignoré tu llamada" } };
        var look = Lunalyr.Render.Cards.CardLook.ForCard(PaletteMapper.Fallback, null, LyricStyle.Default with { Translations = true });
        var scene = Lunalyr.Render.Cards.CardPageScene.For(translated, content, look);
        Assert.False(scene.Scene.Style.Translations);
    }

    // Windows' own: after its first frame a GIF holds only what changed, transparent elsewhere; read back, every frame is
    // the whole picture again (as a browser composes it).
    [Fact]
    public void AGifsLaterFramesHoldOnlyWhatChanged()
    {
        using var media = new CardMedia();
        const int size = 96;
        byte[] Frame(int block)
        {
            var pixels = new byte[size * size * 4];
            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                {
                    var o = (y * size + x) * 4;
                    var inBlock = block >= 0 && x >= block && x < block + 20 && y >= 30 && y < 50;
                    pixels[o] = (byte)(inBlock ? 240 : 40 + x);
                    pixels[o + 1] = (byte)(inBlock ? 240 : 30 + y);
                    pixels[o + 2] = (byte)(inBlock ? 240 : 90);
                    pixels[o + 3] = 255;
                }
            return pixels;
        }
        var frames = new[] { Frame(-1), Frame(10), Frame(10), Frame(50) };
        var path = media.File("delta.gif");
        File.WriteAllBytes(path, CardImages.EncodeGif([.. frames.Select(f => (f, 0.1))], size, size, TestContext.Current.CancellationToken)!);
        var info = CardImages.ReadGif(path)!;
        Assert.Equal((size, size), (info.Frames[0].Width, info.Frames[0].Height));
        Assert.True(info.Frames[1].Width <= 20 && info.Frames[1].Height <= 20, $"{info.Frames[1]}");
        Assert.Equal((1, 1), (info.Frames[2].Width, info.Frames[2].Height)); // nothing changed
        Assert.All(info.Frames, f => Assert.Equal(0.1, f.Delay, 2));
        using var read = AnimatedPicture.Open(path)!;
        for (var i = 0; i < frames.Length; i++)
        {
            var art = read.Frame(i);
            var worst = 0;
            for (var p = 0; p < art.Pixels.Length; p++) worst = Math.Max(worst, Math.Abs(art.Pixels[p] - frames[i][p]));
            Assert.True(worst < 40, $"frame {i} differs by {worst}");
        }
    }
}
