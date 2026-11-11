using System.Numerics;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics;
using Lunalyr.Platform.Readings;
using Lunalyr.Render.Scene;
using Lunalyr.Tools.CompositionPreview;

namespace Lunalyr.Render.Compositions.Tests;

/// <summary>The compositions drawn offscreen: every fixture draws, bad input never throws, and a frame is a pure function of song time.</summary>
[Collection(GpuCollection.Name)]
public sealed class CompositionRenderTests : IDisposable
{
    private const int Width = 960, Height = 540;
    private const float Scale = 1;
    private readonly Offscreen _stage = new(Width, Height);
    private readonly Sink _sink = new();
    private readonly Logger _logger;
    private static readonly Vector4 Background = Fixtures.Night.Base;

    public CompositionRenderTests() => _logger = new Logger(LogLevel.Warn, _sink);

    public void Dispose() => _stage.Dispose();

    private sealed class Sink : ILogSink
    {
        public List<string> Entries { get; } = [];
        public void Write(in LogEntry entry) => Entries.Add(entry.ToString() ?? "");
    }

    private IRenderLayer Layer(string id)
    {
        var breaker = IcuWordBreaker.TryCreate();
        IRenderLayerFactory factory = id == "spoken" ? new SpokenFactory(breaker, _logger) : new EditorialFactory(breaker, _logger);
        Assert.Equal(id, factory.Id);
        var layer = factory.Create();
        layer.CreateResources(_stage.Dc, _stage.DWrite, new Vector2(Width, Height), Scale);
        return layer;
    }

    private byte[] Frame(IRenderLayer layer, SceneSnapshot scene, double time, double wall = 100)
    {
        _stage.Render(layer, scene, time, wall, Scale, Background);
        return _stage.Pixels();
    }

    /// <summary>Pixels clearly brighter than the background (lit by text).</summary>
    private static int Lit(byte[] pixels, int fromX = 0, int toX = Width, int fromY = 0, int toY = Height)
    {
        var count = 0;
        for (var y = fromY; y < toY; y++)
        for (var x = fromX; x < toX; x++)
        {
            var i = (y * Width + x) * 4;
            if (pixels[i] + pixels[i + 1] + pixels[i + 2] > 3 * 110) count++;
        }
        return count;
    }

    private static double CentreX(byte[] pixels)
    {
        double sum = 0;
        var count = 0;
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var i = (y * Width + x) * 4;
            if (pixels[i] + pixels[i + 1] + pixels[i + 2] > 3 * 110) { sum += x; count++; }
        }
        return count == 0 ? double.NaN : sum / count;
    }

    public static TheoryData<string, string, double> Shots() => new()
    {
        { "editorial", "english", 11.3 }, { "editorial", "japanese", 12.8 }, { "editorial", "arabic", 12.6 },
        { "editorial", "emoji", 13.0 }, { "editorial", "linetimed", 12.0 }, { "editorial", "plain", 5 },
        { "editorial", "nolyrics", 0 },
        { "spoken", "english", 11.3 }, { "spoken", "japanese", 12.8 }, { "spoken", "arabic", 12.6 },
        { "spoken", "emoji", 13.0 }, { "spoken", "linetimed", 12.0 }, { "spoken", "plain", 5 }, { "spoken", "nolyrics", 0 },
    };

    private static SceneSnapshot Fixture(string name) => name switch
    {
        "english" => Fixtures.English(),
        "japanese" => Fixtures.Japanese(),
        "arabic" => Fixtures.Arabic(),
        "emoji" => Fixtures.Emoji(),
        "linetimed" => Fixtures.LineTimed(),
        "plain" => Fixtures.Plain(),
        _ => Fixtures.NoLyrics(),
    };

    [Theory]
    [MemberData(nameof(Shots))]
    public void EveryFixtureDraws(string composition, string fixture, double time)
    {
        using var layer = Layer(composition);
        var pixels = Frame(layer, Fixture(fixture), time, wall: 100);
        // The status text eases in: look again once it has.
        if (fixture == "nolyrics") pixels = Frame(layer, Fixture(fixture), time, wall: 102);
        Assert.True(Lit(pixels) > (fixture == "nolyrics" ? 40 : 200), $"{composition} drew nothing for {fixture} at {time}");
        Assert.Empty(_sink.Entries);
    }

    [Theory]
    [InlineData("editorial")]
    [InlineData("spoken")]
    public void NothingIsDrawnWithoutLyricsOrStatusAndBadInputNeverThrows(string composition)
    {
        using var layer = Layer(composition);
        Assert.Equal(0, Lit(Frame(layer, new SceneSnapshot(), 10)));
        // Nothing playing.
        Frame(layer, Fixtures.English(), double.NaN);
        // Broken lyrics: words out of order, an empty line, an instrumental line, times beyond reason.
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
    [InlineData("editorial")]
    [InlineData("spoken")]
    public void AFrameIsAPureFunctionOfSongTime(string composition)
    {
        var scene = Fixtures.English();
        using var played = Layer(composition);
        for (var t = 9.0; t < 11.3; t += 1.0 / 30) Frame(played, scene, t);
        var afterPlaying = Frame(played, scene, 11.3);
        using var fresh = Layer(composition);
        var jumpedTo = Frame(fresh, scene, 11.3);
        Assert.Equal(afterPlaying, jumpedTo);
    }

    [Fact]
    public void ARightToLeftLineStartsOnTheRight()
    {
        using var layer = Layer("editorial");
        // Only the first word has been sung.
        var first = CentreX(Frame(layer, Fixtures.Arabic(), 10.45));
        var whole = CentreX(Frame(layer, Fixtures.Arabic(), 12.9));
        Assert.True(first > whole + 50, $"first word at {first}, whole line at {whole}");
    }

    [Fact]
    public void ReadingsAreDrawnUnderTheLine()
    {
        using var layer = Layer("editorial");
        var with = Fixtures.Japanese();
        var without = with with { Style = with.Style with { Readings = ReadingPlacement.Off } };
        var lower = Height / 2 + 20;
        Assert.True(Lit(Frame(layer, with, 12.9), fromY: lower) > Lit(Frame(layer, without, 12.9), fromY: lower) + 200);
    }

    [Fact]
    public void TheNextLinesFirstWordPreviewsWhereItWillBe()
    {
        using var layer = Layer("editorial");
        var scene = Fixtures.LineTimed();
        // "Every" previews from 13.55 over the dimmed "Paper boats..." line; it is gone without the preview.
        var dimmed = Lit(Frame(layer, scene, 13.3));
        var previewing = Frame(layer, scene, 13.95);
        Assert.True(Lit(previewing) < dimmed, "the line being sung yields to the preview");
    }

    [Fact]
    public void SettledFramesDoNotAllocate()
    {
        using var layer = Layer("editorial");
        var scene = Fixtures.English();
        for (var t = 10.0; t < 12.0; t += 1.0 / 30) Frame(layer, scene, t);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var t = 10.0; t < 12.0; t += 1.0 / 60) _stage.Render(layer, scene, t, t, Scale, Background);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>The average colour of the pixels lit by text (R, G, B in 0…1).</summary>
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

    // The Mac's default lyric colour mode, Album drift: timed words take pastel tints of the cover's colours.
    [Theory]
    [InlineData("editorial")]
    [InlineData("spoken")]
    public void TheLyricColoursFollowTheAlbum(string composition)
    {
        using var layer = Layer(composition);
        var scene = Fixtures.English();
        var red = Tint(Frame(layer, scene with { Palette = Cover(200, 30, 40) }, 11.3));
        var blue = Tint(Frame(layer, scene with { Palette = Cover(30, 60, 210) }, 11.3));
        Assert.True(red.R > red.B + 0.05, $"red cover: {red}");
        Assert.True(blue.B > blue.R + 0.05, $"blue cover: {blue}");
        Assert.Empty(_sink.Entries);
    }

    [Fact]
    public void ALargerSizeOrANewScaleSetsTheLineAgain()
    {
        using var layer = Layer("spoken");
        var small = Lit(Frame(layer, Fixtures.English(), 11.3));
        layer.Resize(new Vector2(Width, Height), 1.5f);
        var scene = Fixtures.English();
        var large = Lit(Frame(layer, scene with { Style = scene.Style with { SizeFraction = 0.11f } }, 11.3));
        Assert.True(large > small, $"{large} vs {small}");
    }
}
