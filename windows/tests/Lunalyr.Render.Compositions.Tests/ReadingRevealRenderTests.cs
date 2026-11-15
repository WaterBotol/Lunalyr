using System.Numerics;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Platform.Readings;
using Lunalyr.Render.Compositions.Stage;
using Lunalyr.Render.Scene;
using Lunalyr.Tools.CompositionPreview;
using Vortice.Direct2D1;
using Vortice.DirectWrite;

namespace Lunalyr.Render.Compositions.Tests;

/// <summary>
/// "Below lyrics" readings filling in syllable by syllable with their words, drawn offscreen in every layout: the
/// reading is drawn, and a frame part-way through a word allocates nothing once the line is set.
/// </summary>
[Collection(GpuCollection.Name)]
public sealed class ReadingRevealRenderTests : IDisposable
{
    private const int Width = 960, Height = 540;
    private const float Scale = 1;
    private readonly Offscreen _stage = new(Width, Height);
    private static readonly Vector4 Background = Fixtures.Night.Base;

    public void Dispose() => _stage.Dispose();

    public static TheoryData<string> Layouts() =>
    [
        "editorial", "spoken", "cascade", "cutout", "scattered", "constellation", "constellationEditorial", "echo",
        .. StageRenderTests.Ids,
    ];

    private IRenderLayer Layer(string id)
    {
        var breaker = IcuWordBreaker.TryCreate();
        IRenderLayerFactory[] factories =
        [
            new EditorialFactory(breaker, Logger.Null), new SpokenFactory(breaker, Logger.Null), new CascadeFactory(breaker, Logger.Null),
            new CutoutFactory(breaker, Logger.Null), new ScatteredFactory(breaker, Logger.Null),
            new ConstellationFactory(breaker, Logger.Null), new ConstellationEditorialFactory(breaker, Logger.Null),
            new EchoFactory(breaker, Logger.Null), .. StageFactories.All(breaker, Logger.Null),
        ];
        var layer = factories.Single(factory => factory.Id == id).Create();
        layer.CreateResources(_stage.Dc, _stage.DWrite, new Vector2(Width, Height), Scale);
        return layer;
    }

    private byte[] Frame(IRenderLayer layer, SceneSnapshot scene, double time)
    {
        _stage.Render(layer, scene, time, 100, Scale, Background);
        return _stage.Pixels();
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void ReadingsAreDrawnPartWayThroughAWord(string id)
    {
        // 思い出す part-way: "omoi" shows under 思い, "da" and "su" not yet.
        using var with = Layer(id);
        using var without = Layer(id);
        var scene = ExtrasFixtures.JapaneseSyllables();
        var off = scene with { Style = scene.Style with { Readings = ReadingPlacement.Off } };
        for (var t = 18.7; t < 20.7; t += 1.0 / 15) { Frame(with, scene, t); Frame(without, off, t); }
        var a = Frame(with, scene, 20.7);
        var b = Frame(without, off, 20.7);
        var differing = 0;
        for (var i = 0; i < a.Length; i += 4)
            if (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]) > 24) differing++;
        Assert.True(differing > 60, $"{id}: no reading was drawn ({differing} pixels differ)");
    }

    /// <summary>A layer that only runs <paramref name="draw"/> on the frame's device context.</summary>
    private sealed class Drawing(Action<ID2D1DeviceContext> draw) : IRenderLayer
    {
        public void CreateResources(ID2D1DeviceContext dc, IDWriteFactory dwrite, Vector2 size, float scale) { }
        public void Resize(Vector2 size, float scale) { }
        public void Render(in FrameContext frame) => draw(frame.Dc);
        public void ReleaseResources() { }
        public bool IsAnimating => false;
        public void Dispose() { }
    }

    /// <summary>The leftmost and rightmost columns lit by text.</summary>
    private static (int Left, int Right) LitSpan(byte[] pixels)
    {
        int left = Width, right = -1;
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var i = (y * Width + x) * 4;
            if (pixels[i] + pixels[i + 1] + pixels[i + 2] <= 3 * 110) continue;
            left = Math.Min(left, x);
            right = Math.Max(right, x);
        }
        return (left, right);
    }

    [Fact]
    public void EachShareIsDrawnInItsOwnPlace()
    {
        // "omoidasu" by its shares, with "omo" and "i" sung and "da" and "su" not yet: plain and tinted (a reading on a
        // highlight tile) alike, only the sung shares are drawn, where they sit in the whole reading.
        using var kit = new TextKit(_stage.Dc, _stage.DWrite);
        using var layout = kit.Layout("omoidasu", Face.Quiet, 60, "en-us", false);
        var shares = new ReadingShares("omoidasu", [new("omo", 1), new("i", 2), new("da", 3), new("su", 4)]);
        var slices = ReadingSlices.Measure(layout, "omoidasu", shares);
        Assert.NotNull(slices);
        var b = slices.Boundaries;
        Assert.Equal(0, b[0]);
        Assert.Equal(1, b[^1]);
        Assert.True(b[1] > 0.25 && b[1] < b[2] && b[2] < b[3] && b[3] < 1, string.Join(", ", b));
        using var ink = kit.Bake(layout, Vector4.One, 1, ShadowSpec.None);
        var world = Matrix3x2.CreateTranslation(Width / 2f, Height / 2f);
        var edge = Width / 2f - ink.Box.X / 2 + (float)b[2] * ink.Box.X;
        var i = Width / 2f - ink.Box.X / 2 + (float)b[1] * ink.Box.X;
        double[] sung = [1, 1, 0, 0];
        var whole = LitSpan(Render(dc => ink.Draw(dc, world, 1, 1, 0, GlowSpec.None, 0)));
        var plain = LitSpan(Render(dc => ink.Draw(dc, world, 1, 1, 0, GlowSpec.None, 0, b, sung)));
        var tinted = LitSpan(Render(dc => ink.DrawTinted(dc, world, Vector4.One, InkClip.None, b, sung)));
        foreach (var (name, span) in new[] { ("plain", plain), ("tinted", tinted) })
        {
            Assert.True(Math.Abs(span.Left - whole.Left) <= 1, $"{name}: starts at {span.Left}, the whole reading at {whole.Left}");
            Assert.True(span.Right <= edge + 1, $"{name}: reaches {span.Right}, past where \"da\" begins ({edge})");
            // "i" is drawn after "omo", where it sits in the whole reading.
            Assert.True(span.Right > i + (edge - i) * 0.4, $"{name}: ends at {span.Right}; \"i\" runs from {i} to {edge}");
        }

        byte[] Render(Action<ID2D1DeviceContext> draw)
        {
            using var layer = new Drawing(draw);
            _stage.Render(layer, new SceneSnapshot(), 0, 0, Scale, Background);
            return _stage.Pixels();
        }
    }

    [Theory]
    [InlineData("spoken")]
    [InlineData("cascade")]
    public void ReadingsOnHighlightTilesFillInWithoutAllocating(string id)
    {
        // "Cutout · Highlight" tints the reading where its tile has pressed in, share by share.
        using var layer = Layer(id);
        var scene = ExtrasFixtures.JapaneseSyllables();
        scene = scene with { Style = scene.Style with { Reveal = "cutoutHighlight" } };
        for (var t = 19.0; t < 21.0; t += 1.0 / 30) _stage.Render(layer, scene, t, t, Scale, Background);
        // Line setup is spread over frames: let it finish, so what follows only draws.
        _stage.Settle(layer, scene, 19.0, Scale, Background);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var t = 19.0; t < 21.0; t += 1.0 / 60) _stage.Render(layer, scene, t, t, Scale, Background);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void FramesFillingInAReadingDoNotAllocate(string id)
    {
        using var layer = Layer(id);
        var scene = ExtrasFixtures.JapaneseSyllables();
        // 思, い, 出 and す sung one after another; the rolling stages' window holds the same words throughout.
        for (var t = 19.0; t < 21.0; t += 1.0 / 30) _stage.Render(layer, scene, t, t, Scale, Background);
        // Line setup is spread over frames: let it finish, so what follows only draws.
        _stage.Settle(layer, scene, 19.0, Scale, Background);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var t = 19.0; t < 21.0; t += 1.0 / 60) _stage.Render(layer, scene, t, t, Scale, Background);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
