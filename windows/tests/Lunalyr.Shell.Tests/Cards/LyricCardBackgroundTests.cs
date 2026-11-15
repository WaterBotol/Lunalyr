using Lunalyr.Core.Cards;
using Lunalyr.Core.Colour;
using Lunalyr.Platform.Media;
using Lunalyr.Shell.Cards;
using Lunalyr.Tests.Shared;

namespace Lunalyr.Shell.Tests.Cards;

/// <summary>Pictures, GIFs and videos behind a card, and the MP4's length and frames over them (decoded, never played).</summary>
public class LyricCardBackgroundTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static byte[] Solid(int size, byte r, byte g, byte b)
    {
        var pixels = new byte[size * size * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = 255;
        }
        return pixels;
    }

    private static (int R, int G, int B) At(CoverArt art, int x, int y)
    {
        var o = (y * art.Width + x) * 4;
        return (art.Pixels[o + 2], art.Pixels[o + 1], art.Pixels[o]);
    }

    private static async Task<CardBackgroundSource> Load(string path)
    {
        var loaded = await CardBackgroundSource.LoadAsync(path, Token);
        Assert.NotNull(loaded);
        return loaded!;
    }

    private static LyricCardJob WithBackground(LyricCardContent content, CardBackgroundSource background) =>
        CardMedia.Job(content).WithBackground(background, null);

    // Ported from LyricCardBackgroundTests.swift: testAPictureReplacesTheAlbumLook
    [Fact]
    public async Task APictureReplacesTheAlbumLook()
    {
        using var media = new CardMedia();
        var path = media.File("red.png");
        File.WriteAllBytes(path, CardImages.EncodePng(Solid(64, 255, 0, 0), 64, 64));
        using var background = await Load(path);
        Assert.Equal(CardBackgroundKind.Image, background.Info.Kind);
        Assert.False(background.Info.IsAnimated);
        var (width, _, pixels) = CardMedia.Exporter().Still(WithBackground(CardProbe.SpacedContent(), background), 1);
        var o = (12 * width + width / 2) * 4;
        Assert.True(pixels[o + 2] > 80);
        Assert.True(pixels[o + 1] < 30);
        Assert.True(pixels[o] < 30);
    }

    // Ported from LyricCardBackgroundTests.swift: testAGIFIsReadFrameByFrameAndLoops
    [Fact]
    public async Task AGifIsReadFrameByFrameAndLoops()
    {
        using var media = new CardMedia();
        var path = media.File("three.gif");
        File.WriteAllBytes(path, CardImages.EncodeGif([(Solid(64, 255, 0, 0), 0.1), (Solid(64, 0, 255, 0), 0.1), (Solid(64, 0, 0, 255), 0.1)], 64, 64, Token)!);
        using var background = await Load(path);
        Assert.Equal(CardBackgroundKind.AnimatedImage, background.Info.Kind);
        Assert.Equal(0.3, background.Info.Duration, 3);
        Assert.True(At(background.FrameAt(0.05), 32, 12).R > 200);
        Assert.True(At(background.FrameAt(0.15), 32, 12).G > 200);
        Assert.True(At(background.FrameAt(0.35), 32, 12).R > 200);
    }

    // Windows' own: the GIF writer stores only the rectangle that changed. A repeated frame (a held line), a single
    // changed pixel and a change in the far corner all encode, keep their delays, and read back over the frame before.
    [Fact]
    public async Task UnchangedAndTinyChangesEncodeAndReadBack()
    {
        using var media = new CardMedia();
        var red = Solid(64, 255, 0, 0);
        var dot = (byte[])red.Clone();
        dot[(10 * 64 + 20) * 4 + 1] = 255; // (20, 10) turns yellow
        var corner = (byte[])dot.Clone();
        corner[(63 * 64 + 63) * 4] = 255; // (63, 63) turns magenta
        var path = media.File("held.gif");
        File.WriteAllBytes(path, CardImages.EncodeGif([(red, 0.1), (red, 0.1), (dot, 0.1), (corner, 0.1), (corner, 0.1)], 64, 64, Token)!);
        using var background = await Load(path);
        Assert.Equal(CardBackgroundKind.AnimatedImage, background.Info.Kind);
        Assert.Equal(0.5, background.Info.Duration, 3);
        Assert.True(At(background.FrameAt(0.15), 32, 12).R > 200);
        var shown = background.FrameAt(0.25);
        Assert.True(At(shown, 20, 10) is { R: > 200, G: > 200 });
        Assert.True(At(shown, 32, 12) is { R: > 200, G: < 60 });
        var last = background.FrameAt(0.45);
        Assert.True(At(last, 63, 63) is { R: > 200, B: > 200 });
        Assert.True(At(last, 20, 10) is { R: > 200, G: > 200 });
    }

    // Ported from LyricCardBackgroundTests.swift: testAVideoBackgroundPlaysUnderTheLyrics
    [Fact]
    public async Task AVideoBackgroundPlaysUnderTheLyrics()
    {
        using var media = new CardMedia();
        var exporter = CardMedia.Exporter();
        var source = media.File("source.mp4");
        Assert.True(exporter.Video(CardMedia.Job(CardProbe.SpacedContent()), source, cancellationToken: Token));
        using var background = await Load(source);
        Assert.Equal(CardBackgroundKind.Video, background.Info.Kind);
        Assert.True(background.Info.Duration > 1);
        Assert.True(background.FrameAt(background.Info.Duration + 0.4).Width > 100);
        var output = media.File("over.mp4");
        Assert.True(exporter.Video(WithBackground(CardProbe.SpacedContent(), background), output, cancellationToken: Token));
        Assert.True(MediaProbe.Read(output)!.Duration > 1.4);
    }

    // Ported from LyricCardBackgroundTests.swift: testAFileThatIsNotMediaIsRefused
    [Fact]
    public async Task AFileThatIsNotMediaIsRefused()
    {
        using var media = new CardMedia();
        var path = media.File("notes.txt");
        File.WriteAllText(path, "hello");
        Assert.Null(await CardBackgroundSource.LoadAsync(path, Token));
    }

    // Ported from LyricCardBackgroundTests.swift: testAVideoBackgroundsSoundIsCarriedIntoTheMP4AndLooped
    [Fact]
    public async Task AVideoBackgroundsSoundIsCarriedIntoTheMp4AndLooped()
    {
        using var media = new CardMedia();
        var exporter = CardMedia.Exporter();
        var silent = media.File("silent.mp4");
        Assert.True(exporter.Video(CardMedia.Job(CardProbe.SpacedContent()), silent, cancellationToken: Token));
        Assert.True(CardMedia.AddSound(silent, media.Tone("tone.wav", 440, 0.3, 0.6)));
        using var background = await Load(silent);
        Assert.True(background.Info.HasAudio);
        var job = WithBackground(CardProbe.SpacedContent(), background);
        var files = await exporter.ExportAsync(job, new HashSet<CardFormat> { CardFormat.Mp4 }, media.File("out"), cancellationToken: Token);
        var info = MediaProbe.Read(files[0].Path)!;
        Assert.True(info.IsAac);
        Assert.InRange(info.AudioSeconds, info.VideoSeconds - 0.3, info.VideoSeconds + 0.3);

        var quiet = await exporter.ExportAsync(job with { Content = job.Content with { KeepBackgroundSound = false } },
            new HashSet<CardFormat> { CardFormat.Mp4 }, media.File("quiet"), cancellationToken: Token);
        Assert.False(MediaProbe.Read(quiet[0].Path)!.HasAudio);
    }

    // Ported from LyricCardLengthTests.swift: testALoopingVideoBlendsItsEndIntoItsStart
    [Fact]
    public async Task ALoopingVideoBlendsItsEndIntoItsStart()
    {
        using var media = new CardMedia();
        // Red for the first 0.6 s, blue after.
        var path = media.Video("clip.mp4", 1.2, t => t < 0.6 ? ((byte)255, (byte)0, (byte)0) : ((byte)0, (byte)0, (byte)255));
        using var clip = await Load(path);
        var loop = new LoopTiming(clip.Info.Duration, 0.3);
        (int R, int B) Colour(double time)
        {
            var c = At(clip.FrameAt(time, loop), 32, 32);
            return (c.R, c.B);
        }
        var start = Colour(0.3);
        Assert.True(start.R > 200 && start.B < 60, $"{start}");
        var end = Colour(0.85);
        Assert.True(end.R < 60 && end.B > 200, $"{end}");
        var middle = Colour(0.9 + 0.15); // halfway through the first seam
        Assert.InRange(middle.R, 90, 170);
        Assert.InRange(middle.B, 90, 170);
        var past = Colour(0.9 + 0.35); // the second pass, back to red
        Assert.True(past.R > 200 && past.B < 60, $"{past}");
    }

    private static double Difference(byte[] a, byte[] b)
    {
        double total = 0;
        for (var i = 0; i < a.Length; i++) total += Math.Abs(a[i] - b[i]);
        return total / a.Length;
    }

    // Ported from LyricCardLengthTests.swift: testFramesHoldTheLastLineThenFadeItToTheVideosEnd
    [Fact]
    public async Task FramesHoldTheLastLineThenFadeItToTheVideosEnd()
    {
        using var media = new CardMedia();
        using var background = await Load(media.Video("bg.mp4", 5));
        var job = WithBackground(CardProbe.Content(), background);
        var plan = LyricCardTiming.Plan(job.Content);
        var frames = new List<(double Time, byte[] Pixels)>();
        var time = 0.0;
        Assert.True(CardMedia.Exporter().RenderFrames(job, 0.25, 0.1, null, plan, (pixels, delay, _) =>
        {
            frames.Add((time, (byte[])pixels.Clone()));
            time += delay;
            return true;
        }, cancellationToken: Token));
        Assert.InRange(time, 4.97, 5.03);
        var last = frames[^1].Pixels;
        byte[] Near(double t) => frames.MinBy(f => Math.Abs(f.Time - t)).Pixels;
        var held = Difference(Near(3.5), last);
        var halfway = Difference(Near(4.75), last);
        var gone = Difference(Near(4.95), last);
        Assert.True(held > 0.5, "the held line is on screen");
        Assert.True(halfway < held * 0.8, "fading");
        Assert.True(gone < halfway);
        // The held frames show the line as it ended: the same picture from 2.5 s to 4.4 s.
        Assert.True(Difference(Near(2.5), Near(4.4)) < held * 0.3);
    }

    // Ported from LyricCardLengthTests.swift: testTheMP4LastsAsLongAsTheBackgroundAndItsSoundLoopsAndFadesOut
    [Fact]
    public async Task TheMp4LastsAsLongAsTheBackgroundAndItsSoundLoopsAndFadesOut()
    {
        using var media = new CardMedia();
        var video = media.Video("bg.mp4", 5);
        Assert.True(CardMedia.AddSound(video, media.Tone("bgtone.wav", 440, 0.3, 1)));
        using var background = await Load(video);
        Assert.True(background.Info.HasAudio);
        var files = await CardMedia.Exporter().ExportAsync(WithBackground(CardProbe.Content(), background),
            new HashSet<CardFormat> { CardFormat.Mp4 }, media.File("long"), cancellationToken: Token);
        var info = MediaProbe.Read(files[0].Path)!;
        Assert.InRange(info.Duration, 4.9, 5.1);
        Assert.InRange(info.AudioSeconds, 4.85, 5.15);
        var pcm = CardMedia.DecodeMono(files[0].Path);
        var end = Math.Min(pcm.Length, (int)(info.Duration * 48000));
        var middle = CardProbe.RmsDb(pcm, 96000, 144000);
        var lateFade = CardProbe.RmsDb(pcm, end - 7200, end - 4800);
        var tail = CardProbe.RmsDb(pcm, end - 1440, end);
        Assert.True(middle > -30);
        Assert.True(lateFade < middle - 3);
        Assert.True(tail < middle - 20);
    }

    // Ported from LyricCardLengthTests.swift: testTheExportSaysWhenLinesDontFitOrTheSongIsMissing
    [Fact]
    public async Task TheExportSaysWhenLinesDontFitOrTheSongIsMissing()
    {
        using var media = new CardMedia();
        using var background = await Load(media.Video("bg.mp4", 2.5));
        var content = CardProbe.Content((10, 11.6), (12, 13.6), (14, 15.6));
        var range = LyricCardSound.SongRange(content)!.Value;
        var tone = media.File("twopart.wav");
        WavFile.Write(tone, CardProbe.TwoPartTone(), 1, 48_000);
        // A recording that starts late, so the first line isn't in it.
        var job = WithBackground(content, background);
        job = job with { Content = job.Content with { SongAudio = new SongAudioClip(tone, range.From + 1, range.From + 4) } };
        var notes = new List<string>();
        var files = await CardMedia.Exporter().ExportAsync(job, new HashSet<CardFormat> { CardFormat.Mp4 }, media.File("notes"),
            notes: notes.Add, cancellationToken: Token);
        Assert.Contains("2 lines won’t fit; pick fewer lines or Match lyrics", notes);
        Assert.Contains(notes, n => n.StartsWith("Parts of these lines aren’t in the song’s recording", StringComparison.Ordinal));
        // The song is still there, silent where it wasn't recorded.
        var info = MediaProbe.Read(files[0].Path)!;
        Assert.True(info.IsAac);
        Assert.InRange(info.Duration, 2.4, 2.6);
    }
}
