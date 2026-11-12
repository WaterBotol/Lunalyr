using Lunalyr.Core.Backgrounds;
using Lunalyr.Render.Backgrounds;
using Lunalyr.Render.Backgrounds.Gpu;
using Lunalyr.Render.Diagnostics;
using Lunalyr.Render.Scene;

namespace Lunalyr.Render.Tests.Backgrounds;

/// <summary>The line patterns over the colour field, drawn offscreen on WARP and read back.</summary>
public sealed class LinePatternLayerTests : IDisposable
{
    private const int Width = 320, Height = 180;
    private static readonly BackgroundOptions Plain = new() { Grain = false, ScrimStrength = 0, Speed = 0 };
    private readonly OffscreenSurface _surface = new(Width, Height);

    public void Dispose() => _surface.Dispose();

    public static TheoryData<string> Patterns => [.. LinePatternCatalog.All.Select(p => p.Id)];

    private static SceneSnapshot Scene => new() { Palette = GpuEffectLayerTests.TwoColour, Title = "Song", Artist = "Artist" };

    // No title: the stage is empty, so no text backing (whose strength follows the pattern) darkens the comparison.
    private static SceneSnapshot Quiet => new() { Palette = GpuEffectLayerTests.TwoColour };

    // Pattern visibility at 0: the factory's opacity, or the scene's once the wallpaper-types adjustments are merged.
    private static SceneSnapshot Hidden =>
#if LUNALYR_WALLPAPER_TYPES
        Quiet with { Adjustments = new WallpaperAdjustments { PatternOpacity = 0 } };
#else
        Quiet;
#endif

    private byte[] Frame(IRenderLayer layer, double wall, SceneSnapshot? scene = null)
    {
        _surface.Draw(layer, scene ?? Quiet, wall);
        return _surface.ReadPixels();
    }

    private static double Sum(byte[] pixels)
    {
        double sum = 0;
        for (var i = 0; i < pixels.Length; i += 4) sum += pixels[i] + pixels[i + 1] + pixels[i + 2];
        return sum;
    }

    private static double Difference(byte[] a, byte[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return sum / a.Length;
    }

    [Fact]
    public void EveryPatternHasALayerFactoryWithItsMacId()
    {
        var factories = LinePatternLayerFactory.All(Plain);
        Assert.Equal(18, factories.Count);
        Assert.Equal("pattern.ripples", factories[0].Id);
        Assert.Equal("pattern.crosshatch", factories[17].Id);
        Assert.Equal(17, LinePatternCatalog.Find("crosshatch")!.RawValue);
    }

    [Theory]
    [MemberData(nameof(Patterns))]
    public void EachPatternAddsLightThatMoves(string id)
    {
        // The colour field stands still (Speed 0 stops it) but the pattern clock does too, so compare with no pattern
        // and between two moments at normal speed.
        using var none = new LinePatternLayerFactory(id, Plain, opacity: 0).Create();
        using var layer = new LinePatternLayerFactory(id, Plain).Create();
        _surface.Prepare(none);
        _surface.Prepare(layer);
        var plain = Frame(none, 20, Hidden);
        var drawn = Frame(layer, 20);
        Assert.True(Sum(drawn) > Sum(plain) * 1.001, $"{id} added no light: {Sum(drawn)} vs {Sum(plain)}");

        using var moving = new LinePatternLayerFactory(id, Plain with { Speed = 1 }).Create();
        _surface.Prepare(moving);
        var first = Frame(moving, 20);
        var later = Frame(moving, 50);
        Assert.True(Difference(first, later) > 0.05, $"{id} did not move: {Difference(first, later):0.###}");
    }

    [Theory]
    [MemberData(nameof(Patterns))]
    public void APatternFrameAllocatesNothing(string id)
    {
        using var layer = new LinePatternLayerFactory(id, Plain with { Speed = 1 }).Create();
        _surface.Prepare(layer);
        var scene = Scene;
        for (var i = 0; i < 5; i++) _surface.Draw(layer, scene, 10 + i / 60.0);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 5; i < 65; i++) _surface.Draw(layer, scene, 10 + i / 60.0);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"{id}: {allocated} bytes over 60 frames");
    }

    [Fact]
    public void ANewSongRearrangesASeededPattern()
    {
        using var layer = new LinePatternLayerFactory("constellation", Plain).Create();
        _surface.Prepare(layer);
        _surface.Draw(layer, Scene, 20);
        var one = _surface.ReadPixels();
        _surface.Draw(layer, Scene with { Title = "Another song" }, 20);
        var other = _surface.ReadPixels();
        Assert.True(Difference(one, other) > 0.02, $"{Difference(one, other)}");
    }
}
