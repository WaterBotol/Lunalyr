using System.Numerics;
using Lunalyr.Core.Logging;
using Lunalyr.Platform.Readings;
using Lunalyr.Render.Scene;
using Lunalyr.Tools.CompositionPreview;

namespace Lunalyr.Render.Compositions.Tests;

/// <summary>The wallpaper extras drawn offscreen: word-by-word readings, credits and highlight tiles.</summary>
[Collection(GpuCollection.Name)]
public sealed class ExtrasRenderTests : IDisposable
{
    private const int Width = 960, Height = 540;
    private const float Scale = 1;
    private readonly Offscreen _stage = new(Width, Height);
    private readonly Sink _sink = new();
    private readonly Logger _logger;
    private static readonly Vector4 Background = Fixtures.Night.Base;

    public ExtrasRenderTests() => _logger = new Logger(LogLevel.Warn, _sink);

    public void Dispose() => _stage.Dispose();

    private sealed class Sink : ILogSink
    {
        public List<string> Entries { get; } = [];
        public void Write(in LogEntry entry) => Entries.Add(entry.ToString() ?? "");
    }

    private IRenderLayer Layer(string id)
    {
        var breaker = IcuWordBreaker.TryCreate();
        IRenderLayerFactory factory = id switch
        {
            "spoken" => new SpokenFactory(breaker, _logger),
            "cascade" => new CascadeFactory(breaker, _logger),
            "cutout" => new CutoutFactory(breaker, _logger),
            "scattered" => new ScatteredFactory(breaker, _logger),
            "constellation" => new ConstellationFactory(breaker, _logger),
            "constellationEditorial" => new ConstellationEditorialFactory(breaker, _logger),
            "echo" => new EchoFactory(breaker, _logger),
            _ => new EditorialFactory(breaker, _logger),
        };
        Assert.Equal(id, factory.Id);
        var layer = factory.Create();
        layer.CreateResources(_stage.Dc, _stage.DWrite, new Vector2(Width, Height), Scale);
        return layer;
    }

    private byte[] Frame(IRenderLayer layer, SceneSnapshot scene, double time)
    {
        _stage.Render(layer, scene, time, 100, Scale, Background);
        return _stage.Pixels();
    }

    private static int Count(byte[] pixels, Func<int, int, int, bool> match, int fromY = 0, int toY = Height)
    {
        var count = 0;
        for (var y = fromY; y < toY; y++)
        for (var x = 0; x < Width; x++)
        {
            var i = (y * Width + x) * 4;
            // BGRA.
            if (match(pixels[i + 2], pixels[i + 1], pixels[i])) count++;
        }
        return count;
    }

    private static int Lit(byte[] pixels, int fromY = 0, int toY = Height) => Count(pixels, (r, g, b) => r + g + b > 3 * 110, fromY, toY);

    private static int Differing(byte[] a, byte[] b)
    {
        var count = 0;
        for (var i = 0; i < a.Length; i += 4)
            if (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]) > 60) count++;
        return count;
    }

    [Theory]
    [InlineData("editorial")]
    [InlineData("spoken")]
    [InlineData("cascade")]
    [InlineData("cutout")]
    [InlineData("scattered")]
    [InlineData("constellation")]
    [InlineData("constellationEditorial")]
    [InlineData("echo")]
    public void WordReadingsAreDrawnUnderTheirWords(string composition)
    {
        using var layer = Layer(composition);
        var with = ExtrasFixtures.JapaneseWords();
        var without = with with { Style = with.Style with { Readings = ReadingPlacement.Off } };
        var withPixels = Frame(layer, with, 12.9);
        var withoutPixels = Frame(layer, without, 12.9);
        // Drawn under each word (Spoken's readings take the ink's colours, so changed pixels are counted, not light ones).
        Assert.True(Differing(withPixels, withoutPixels) > 300, $"{Differing(withPixels, withoutPixels)}");
        Assert.Empty(_sink.Entries);
    }

    [Theory]
    [InlineData("editorial")]
    [InlineData("spoken")]
    [InlineData("cascade")]
    [InlineData("cutout")]
    [InlineData("scattered")]
    [InlineData("constellation")]
    [InlineData("constellationEditorial")]
    [InlineData("echo")]
    public void InsteadSetsTheReadingsInTheWordsPlace(string composition)
    {
        using var layer = Layer(composition);
        var below = ExtrasFixtures.JapaneseWords();
        var instead = ExtrasFixtures.JapaneseWords(LyricStyle.Default with { Readings = ReadingPlacement.Instead });
        var off = below with { Style = below.Style with { Readings = ReadingPlacement.Off } };
        var insteadPixels = Frame(layer, instead, 12.9);
        Assert.True(Lit(insteadPixels) > 200);
        Assert.True(Differing(insteadPixels, Frame(layer, off, 12.9)) > 300, "the words are replaced");
        Assert.True(Differing(insteadPixels, Frame(layer, below, 12.9)) > 300, "not drawn below the words");
        Assert.Empty(_sink.Entries);
    }

    [Theory]
    [InlineData("editorial")]
    [InlineData("spoken")]
    [InlineData("cascade")]
    [InlineData("cutout")]
    [InlineData("scattered")]
    [InlineData("constellation")]
    [InlineData("constellationEditorial")]
    [InlineData("echo")]
    public void CreditsOpenTheSongAndThenFollowEachOther(string composition)
    {
        using var layer = Layer(composition);
        var on = ExtrasFixtures.Credits();
        var off = on with { Style = on.Style with { Credits = false } };
        // Before the first line, only the opening "Title - Artist" is up, in the top band.
        var opening = Frame(layer, on, 2.0);
        Assert.True(Lit(opening, 0, Height / 3) > 150, $"{Lit(opening, 0, Height / 3)}");
        // (Credits off: only the intro's interlude dots, in the middle.)
        Assert.Equal(0, Lit(Frame(layer, off, 2.0), 0, Height / 3));
        // A production credit arrives in the foot band while the song plays.
        var credit = Frame(layer, on, 10.5);
        var plain = Frame(layer, off, 10.5);
        Assert.True(Lit(credit, Height * 2 / 3) > Lit(plain, Height * 2 / 3) + 150);
        Assert.Empty(_sink.Entries);
    }

    [Theory]
    [InlineData("editorial")]
    [InlineData("spoken")]
    [InlineData("cascade")]
    [InlineData("scattered")]
    [InlineData("constellation")]
    [InlineData("constellationEditorial")]
    [InlineData("echo")]
    public void SungWordsGetAHighlightTile(string composition)
    {
        using var layer = Layer(composition);
        var highlighted = ExtrasFixtures.Highlight();
        var plain = highlighted with { Style = highlighted.Style with { Reveal = "fade" } };
        // The custom colour is a vivid green: tiles are green paper.
        static bool Green(int r, int g, int b) => g > 120 && g > r + 50 && g > b + 30;
        var tiles = Count(Frame(layer, highlighted, 12.9), Green);
        var none = Count(Frame(layer, plain, 12.9), Green);
        Assert.True(tiles > none + 300, $"{tiles} vs {none}");
        Assert.Empty(_sink.Entries);
    }

    // The Mac presses a rolling word's tile behind the whole unit (SpokenTypographyView: the CutoutTile behind a
    // RollingUnit, padded by labelReach): with readings shown the tile reaches down over them too.
    [Theory]
    [InlineData("spoken")]
    [InlineData("cascade")]
    public void ARollingTileCoversTheWordAndItsReading(string composition)
    {
        static bool Green(int r, int g, int b) => g > 120 && g > r + 50 && g > b + 30;
        var style = LyricStyle.Default with { Reveal = "cutoutHighlight", HighlightMode = "custom", HighlightCustom = "#2BD96B" };
        var below = ExtrasFixtures.JapaneseWords(style);
        var off = below with { Style = below.Style with { Readings = ReadingPlacement.Off } };
        using var layer = Layer(composition);
        var withReadings = Count(Frame(layer, below, 12.9), Green);
        var withoutReadings = Count(Frame(layer, off, 12.9), Green);
        Assert.True(withoutReadings > 300, $"{withoutReadings}");
        Assert.True(withReadings > withoutReadings * 1.25, $"{withReadings} vs {withoutReadings}");
        Assert.Empty(_sink.Entries);
    }

    [Fact]
    public void TheHighlightColourFollowsTheSetting()
    {
        var palette = Fixtures.Night;
        var complement = LyricInks.Settled(palette, LyricStyle.Default);
        var custom = LyricInks.Settled(palette, LyricStyle.Default with { HighlightMode = "custom", HighlightCustom = "#2BD96B" });
        Assert.NotEqual(complement.TileColour, custom.TileColour);
        Assert.True(custom.TileColour.G > custom.TileColour.R);
        Assert.Equal(Core.Colour.LyricHighlightMode.Album, LyricInks.HighlightMode("album"));
        Assert.Equal(Core.Colour.LyricHighlightMode.Complement, LyricInks.HighlightMode("neon"));
    }

    [Theory]
    [InlineData("editorial")]
    [InlineData("spoken")]
    [InlineData("cascade")]
    [InlineData("cutout")]
    [InlineData("scattered")]
    [InlineData("constellation")]
    [InlineData("constellationEditorial")]
    [InlineData("echo")]
    public void ExtrasFramesAreAPureFunctionOfSongTime(string composition)
    {
        foreach (var scene in new[] { ExtrasFixtures.Credits(), ExtrasFixtures.Highlight(), ExtrasFixtures.JapaneseWords() })
        {
            using var played = Layer(composition);
            for (var t = 10.0; t < 12.9; t += 1.0 / 30) Frame(played, scene, t);
            var afterPlaying = Frame(played, scene, 12.9);
            using var fresh = Layer(composition);
            Assert.Equal(afterPlaying, Frame(fresh, scene, 12.9));
        }
    }
}
