using System.Numerics;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics;
using Lunalyr.Platform.Readings;
using Lunalyr.Render.Scene;
using Lunalyr.Tools.CompositionPreview;

namespace Lunalyr.Render.Compositions.Tests;

/// <summary>Cascade and Cutout drawn offscreen: every fixture draws, bad input never throws, frames are pure and settled frames allocate nothing.</summary>
[Collection(GpuCollection.Name)]
public sealed class ArrivalRenderTests : IDisposable
{
    private const int Width = 960, Height = 540;
    private const float Scale = 1;
    private readonly Offscreen _stage = new(Width, Height);
    private readonly Sink _sink = new();
    private readonly Logger _logger;
    private static readonly Vector4 Background = Fixtures.Night.Base;

    public ArrivalRenderTests() => _logger = new Logger(LogLevel.Warn, _sink);

    public void Dispose() => _stage.Dispose();

    private sealed class Sink : ILogSink
    {
        public List<string> Entries { get; } = [];
        public void Write(in LogEntry entry) => Entries.Add(entry.ToString() ?? "");
    }

    private IRenderLayer Layer(string id, Offscreen? stage = null, float scale = Scale)
    {
        stage ??= _stage;
        var breaker = IcuWordBreaker.TryCreate();
        IRenderLayerFactory factory = id switch
        {
            "cascade" => new CascadeFactory(breaker, _logger),
            "cutout" => new CutoutFactory(breaker, _logger),
            _ => new SpokenFactory(breaker, _logger),
        };
        Assert.Equal(id, factory.Id);
        var layer = factory.Create();
        layer.CreateResources(stage.Dc, stage.DWrite, new Vector2(stage.Width, stage.Height), scale);
        return layer;
    }

    private byte[] Frame(IRenderLayer layer, SceneSnapshot scene, double time, double wall = 100)
    {
        _stage.Render(layer, scene, time, wall, Scale, Background);
        return _stage.Pixels();
    }

    private static int Lit(byte[] pixels)
    {
        var count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
            if (pixels[i] + pixels[i + 1] + pixels[i + 2] > 3 * 110) count++;
        return count;
    }

    private static double CentreX(byte[] pixels, int width)
    {
        double sum = 0;
        var count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
            if (pixels[i] + pixels[i + 1] + pixels[i + 2] > 3 * 110) { sum += i / 4 % width; count++; }
        return count == 0 ? double.NaN : sum / count;
    }

    private static SceneSnapshot Fixture(string name, string? reveal = null)
    {
        var scene = name switch
        {
            "english" => Fixtures.English(),
            "japanese" => Fixtures.Japanese(),
            "arabic" => Fixtures.Arabic(),
            "emoji" => Fixtures.Emoji(),
            "linetimed" => Fixtures.LineTimed(),
            "plain" => Fixtures.Plain(),
            _ => Fixtures.NoLyrics(),
        };
        return reveal is null ? scene : scene with { Style = scene.Style with { Reveal = reveal } };
    }

    public static TheoryData<string, string, double, string> Shots()
    {
        var data = new TheoryData<string, string, double, string>();
        foreach (var id in new[] { "cascade", "cutout" })
        foreach (var reveal in new[] { "fade", "automatic" })
        {
            data.Add(id, "english", 11.3, reveal);
            data.Add(id, "japanese", 12.8, reveal);
            data.Add(id, "arabic", 12.6, reveal);
            data.Add(id, "emoji", 13.0, reveal);
            data.Add(id, "linetimed", 12.0, reveal);
            data.Add(id, "plain", 5, reveal);
            data.Add(id, "nolyrics", 0, reveal);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Shots))]
    public void EveryFixtureDraws(string composition, string fixture, double time, string reveal)
    {
        using var layer = Layer(composition);
        var scene = Fixture(fixture, reveal);
        var pixels = Frame(layer, scene, time, wall: 100);
        if (fixture == "nolyrics") pixels = Frame(layer, scene, time, wall: 102);
        Assert.True(Lit(pixels) > (fixture == "nolyrics" ? 40 : 200), $"{composition} drew nothing for {fixture} at {time}");
        Assert.Empty(_sink.Entries);
    }

    [Theory]
    [InlineData("cascade")]
    [InlineData("cutout")]
    public void NothingIsDrawnWithoutLyricsOrStatusAndBadInputNeverThrows(string composition)
    {
        using var layer = Layer(composition);
        Assert.Equal(0, Lit(Frame(layer, new SceneSnapshot(), 10)));
        Frame(layer, Fixtures.English(), double.NaN);
        LyricLine[] broken =
        [
            new() { Start = 1, End = 3, Text = "out of order", WordTimed = true,
                    Words = [new("order", 2.5, 2.9), new("out", 1, 1.4), new("of", 1.4, 0.2)] },
            new() { Start = 3, End = 4, Text = "" },
            new() { Start = 4, End = 6, Text = "", IsInstrumental = true },
            new() { Start = 1e12, End = double.NaN, Text = "far away", WordTimed = true, Words = [new("far", 1e12, double.NaN)] },
        ];
        var scene = new SceneSnapshot { Lyrics = new LyricResult.Synced(broken, new LyricProvenance("Word-by-word", "Test")) };
        foreach (var time in new[] { -5, 0, 1.2, 2.7, 3.5, 5, 1e12, double.PositiveInfinity }) Frame(layer, scene, time);
        Assert.Empty(_sink.Entries);
    }

    [Theory]
    [InlineData("cascade", "fade")]
    [InlineData("cutout", "fade")]
    [InlineData("cascade", "automatic")]
    [InlineData("cutout", "automatic")]
    public void AFrameIsAPureFunctionOfSongTime(string composition, string reveal)
    {
        var scene = Fixture("english", reveal);
        using var played = Layer(composition);
        for (var t = 9.0; t < 11.3; t += 1.0 / 30) Frame(played, scene, t);
        var afterPlaying = Frame(played, scene, 11.3);
        using var fresh = Layer(composition);
        Assert.Equal(afterPlaying, Frame(fresh, scene, 11.3));
    }

    [Fact]
    public void TheColumnRunsLeftOfSpokensCentre()
    {
        using var spoken = Layer("spoken");
        using var cascade = Layer("cascade");
        using var cutout = Layer("cutout");
        var scene = Fixtures.English();
        var middle = CentreX(Frame(spoken, scene, 11.3), Width);
        // 13% of the 768-pixel stage to the left.
        Assert.InRange(CentreX(Frame(cascade, scene, 11.3), Width), middle - 100 - 40, middle - 100 + 40);
        Assert.InRange(CentreX(Frame(cutout, scene, 11.3), Width), middle - 100 - 40, middle - 100 + 40);
    }

    /// <summary>The average colour of the brightest pixels (R, G, B in 0…1).</summary>
    private static (double R, double G, double B) Tint(byte[] pixels)
    {
        double r = 0, g = 0, b = 0;
        var count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] + pixels[i + 1] + pixels[i + 2] <= 3 * 110) continue;
            b += pixels[i]; g += pixels[i + 1]; r += pixels[i + 2];
            count++;
        }
        return count == 0 ? (0, 0, 0) : (r / count / 255, g / count / 255, b / count / 255);
    }

    private static Palette Cover(byte r, byte g, byte b) =>
        PaletteMapper.FromAlbum(new Core.Colour.AlbumWallpaperPalette(Core.Colour.AlbumColourGrid.Extract(Core.Colour.ArtworkPixels.Solid(64, 64, r, g, b))));

    /// <summary>Pixels within a few levels of <paramref name="colour"/>.</summary>
    private static int Near(byte[] pixels, Core.Colour.SrgbColour colour)
    {
        int r = (int)Math.Round(colour.R * 255), g = (int)Math.Round(colour.G * 255), b = (int)Math.Round(colour.B * 255);
        var count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
            if (Math.Abs(pixels[i + 2] - r) <= 6 && Math.Abs(pixels[i + 1] - g) <= 6 && Math.Abs(pixels[i] - b) <= 6) count++;
        return count;
    }

    // The Mac's defaults: Album drift ink, and the highlight complementing it. Cascade's held word ("dreaming") is set
    // in the highlight's held colour and Cutout's label in its tile colour, both worked out from the cover.
    [Theory]
    [InlineData("cascade")]
    [InlineData("cutout")]
    public void TheHeldWordsAndLabelsTakeTheCoversHighlight(string composition)
    {
        using var layer = Layer(composition);
        var scene = Fixtures.English();
        var colours = new List<Core.Colour.SrgbColour>();
        foreach (var cover in new[] { Cover(200, 30, 40), Cover(30, 60, 210) })
        {
            var ink = LyricInks.Settled(cover);
            var colour = composition == "cascade" ? ink.Accent!.Value : ink.TileColour;
            colours.Add(colour);
            var pixels = Frame(layer, scene with { Palette = cover }, 11.3);
            Assert.True(Near(pixels, colour) > (composition == "cascade" ? 150 : 3000), $"{composition}: {Near(pixels, colour)} pixels of {colour}");
        }
        Assert.True(Math.Abs(colours[0].R - colours[1].R) + Math.Abs(colours[0].G - colours[1].G) + Math.Abs(colours[0].B - colours[1].B) > 0.2,
                    $"the two covers gave {colours[0]} and {colours[1]}");
        Assert.Empty(_sink.Entries);
    }

    [Fact]
    public void ALabelPressesInBehindItsWord()
    {
        // "dreaming" (a hero, so on a label) starts at 10.84: a sliver before, the whole label once it has landed.
        using var layer = Layer("cutout");
        var scene = Fixtures.English();
        static int Tile(byte[] pixels)
        {
            // The label's colour: strongly warm, unlike the words' pastels or the night background.
            var count = 0;
            for (var i = 0; i < pixels.Length; i += 4)
                if (pixels[i + 2] > 200 && pixels[i + 1] < 160 && pixels[i] < 120) count++;
            return count;
        }
        var before = Tile(Frame(layer, scene, 10.6));
        var landed = Tile(Frame(layer, scene, 11.3));
        Assert.True(landed > 4000, $"{landed}");
        Assert.True(before < landed / 4, $"{before} before, {landed} landed");
        // Cascade has no labels.
        using var cascade = Layer("cascade");
        Assert.True(Tile(Frame(cascade, scene, 11.3)) < landed / 10);
    }

    [Theory]
    [InlineData("spoken")]
    [InlineData("cascade")]
    [InlineData("cutout")]
    public void SettledFramesAllocateNothingWhileTheWindowHolds(string composition)
    {
        using var layer = Layer(composition);
        var scene = Fixtures.English();
        // From 14.6 to 15.7 no word joins the rolling window ("night" joins at 14.5, "I" at 15.8).
        for (var t = 13.5; t < 15.7; t += 1.0 / 30) Frame(layer, scene, t);
        // Line setup is spread over frames: let it finish, so what follows only draws.
        _stage.Settle(layer, scene, 14.6, Scale, Background);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var t = 14.6; t < 15.7; t += 1.0 / 60) _stage.Render(layer, scene, t, t, Scale, Background);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Theory]
    [InlineData("cascade")]
    [InlineData("cutout")]
    public void ArrivalLayersAllocateNoMoreThanSpoken(string composition)
    {
        long Allocated(IRenderLayer layer, SceneSnapshot scene)
        {
            for (var t = 9.5; t < 20; t += 1.0 / 60) _stage.Render(layer, scene, t, t, Scale, Background);
            // Line setup is spread over frames: let it finish, so what follows only draws.
            _stage.Settle(layer, scene, 9.5, Scale, Background);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var t = 9.5; t < 20; t += 1.0 / 60) _stage.Render(layer, scene, t, t, Scale, Background);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
        var scene = Fixtures.English();
        using var spoken = Layer("spoken");
        using var layer = Layer(composition);
        var reference = Allocated(spoken, scene);
        var own = Allocated(layer, scene);
        // Only the frames where a word joins or leaves the window allocate, as in Spoken.
        Assert.True(own <= reference * 1.1 + 1024, $"{composition} {own} B against Spoken's {reference} B");
    }

    // Ported from RenderingPerformanceTests.swift: testRenderedWideCutoutAndReadingFitDesktopAndScaledPreview (the
    // desktop; Windows has no scaled settings preview of the stage)
    [Fact]
    public void ARenderedWideCutoutAndReadingFitTheDesktop()
    {
        using var display = new Offscreen(680, 420);
        using var layer = Layer("cutout", display);
        var word = new LyricWord(new string('W', 42), 0, 2);
        LyricLine[] lines = [new() { Start = 0, End = 2, Text = word.Text, WordTimed = true, Words = [word] }];
        var scene = new SceneSnapshot
        {
            Lyrics = new LyricResult.Synced(lines, new LyricProvenance("Word-by-word", "Test")),
            Readings = new Dictionary<int, string> { [0] = string.Concat(Enumerable.Repeat("romaji ", 12)).Trim() },
            Palette = Fixtures.Night,
            Style = LyricStyle.Default with { SizeFraction = 0.075f * 1.5f },
        };
        for (var t = 0.4; t < 0.6; t += 1.0 / 60) display.Render(layer, scene, t, t, 1, Background);
        display.Render(layer, scene, 0.6, 0.6, 1, Background);
        var pixels = display.Pixels();
        double minX = 1, maxX = 0, minY = 1, maxY = 0;
        for (var y = 0; y < 420; y += 2)
        for (var x = 0; x < 680; x += 2)
        {
            var i = (y * 680 + x) * 4;
            if (Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2])) <= 0.45 * 255) continue;
            minX = Math.Min(minX, x / 680.0); maxX = Math.Max(maxX, x / 680.0);
            minY = Math.Min(minY, y / 420.0); maxY = Math.Max(maxY, y / 420.0);
        }
        Assert.True(maxX > minX, "nothing was drawn");
        Assert.True(minX > 0.09, $"{minX}");
        Assert.True(maxX < 0.91, $"{maxX}");
        Assert.True(minY > 0.12, $"{minY}");
        Assert.True(maxY < 0.88, $"{maxY}");
        Assert.Empty(_sink.Entries);
    }
}
