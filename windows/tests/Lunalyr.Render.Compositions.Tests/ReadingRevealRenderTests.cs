using System.Numerics;
using Lunalyr.Core.Logging;
using Lunalyr.Platform.Readings;
using Lunalyr.Render.Compositions.Stage;
using Lunalyr.Render.Scene;
using Lunalyr.Tools.CompositionPreview;

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

    [Theory]
    [MemberData(nameof(Layouts))]
    public void FramesFillingInAReadingDoNotAllocate(string id)
    {
        using var layer = Layer(id);
        var scene = ExtrasFixtures.JapaneseSyllables();
        // 思, い, 出 and す sung one after another; the rolling stages' window holds the same words throughout.
        for (var t = 19.0; t < 21.0; t += 1.0 / 30) _stage.Render(layer, scene, t, t, Scale, Background);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var t = 19.0; t < 21.0; t += 1.0 / 60) _stage.Render(layer, scene, t, t, Scale, Background);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
