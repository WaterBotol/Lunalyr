using System.Numerics;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Platform.Readings;
using Lunalyr.Render.Scene;
using Lunalyr.Tools.CompositionPreview;

namespace Lunalyr.Render.Compositions.Tests;

/// <summary>
/// The Scattered family's settings (Spread, Links, Repeats), the custom mix and an entrance per layout, from the scene's
/// style to the pixels (milestone 5's settings window sets them).
/// </summary>
[Collection(GpuCollection.Name)]
public sealed class FamilySettingsRenderTests : IDisposable
{
    private const int Width = 960, Height = 540;
    private const float Scale = 1;
    private readonly Offscreen _stage = new(Width, Height);
    private static readonly Vector4 Background = Fixtures.Night.Base;
    private static readonly IWordBreaker Breaker = IcuWordBreaker.TryCreate() ?? (IWordBreaker)SimpleWordBreaker.Instance;

    public void Dispose() => _stage.Dispose();

    /// <summary>A layer that shows its plans.</summary>
    private sealed class Probe(CompositionKind kind) : LyricCompositionLayer(kind, FamilySettingsRenderTests.Breaker, null)
    {
        public LinePlan? PlanFor(int row) => Plan(row);
    }

    private IRenderLayer Layer(IRenderLayerFactory factory)
    {
        var layer = factory.Create();
        layer.CreateResources(_stage.Dc, _stage.DWrite, new Vector2(Width, Height), Scale);
        return layer;
    }

    private byte[] Frame(IRenderLayer layer, SceneSnapshot scene, double time)
    {
        _stage.Render(layer, scene, time, 100, Scale, Background);
        return _stage.Pixels();
    }

    private static int Faint(byte[] pixels)
    {
        var count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
            if (pixels[i] + pixels[i + 1] + pixels[i + 2] > 3 * 30) count++;
        return count;
    }

    [Fact]
    public void RepeatsAtZeroDrawNoEcho()
    {
        using var echo = Layer(new EchoFactory(Breaker, Logger.Null));
        var full = Faint(Frame(echo, Fixtures.English(), 12.9));
        var none = Faint(Frame(echo, Fixtures.English(LyricStyle.Default with { EchoStrength = 0 }), 12.9));
        var strong = Faint(Frame(echo, Fixtures.English(LyricStyle.Default with { EchoStrength = 1.8 }), 12.9));
        Assert.True(full > none + 2000, $"{full} vs {none}");
        Assert.True(strong >= full, $"{strong} vs {full}");
    }

    [Fact]
    public void LinksAtZeroDrawNoHairlines()
    {
        using var constellation = Layer(new ConstellationFactory(Breaker, Logger.Null));
        var linked = Frame(constellation, Fixtures.English(), 11.56);
        var unlinked = Frame(constellation, Fixtures.English(LyricStyle.Default with { ConstellationLinkOpacity = 0 }), 11.56);
        Assert.NotEqual(linked, unlinked);
        Assert.True(Faint(linked) > Faint(unlinked), $"{Faint(linked)} vs {Faint(unlinked)}");
    }

    [Fact]
    public void SpreadMovesTheConstellationsWords()
    {
        using var narrow = new Probe(CompositionKind.Constellation);
        narrow.CreateResources(_stage.Dc, _stage.DWrite, new Vector2(Width, Height), Scale);
        _stage.Render(narrow, Fixtures.English(LyricStyle.Default with { ConstellationSpread = 0.4 }), 11.56, 100, Scale, Background);
        using var wide = new Probe(CompositionKind.Constellation);
        wide.CreateResources(_stage.Dc, _stage.DWrite, new Vector2(Width, Height), Scale);
        _stage.Render(wide, Fixtures.English(LyricStyle.Default with { ConstellationSpread = 1.8 }), 11.56, 100, Scale, Background);

        static double Range(Probe probe)
        {
            var heights = probe.PlanFor(0)!.Typography.Runs.Select(run => run.Metrics.WaveOffset).ToArray();
            return heights.Max() - heights.Min();
        }
        Assert.True(Range(wide) > Range(narrow) * 1.5, $"{Range(wide)} vs {Range(narrow)}");
    }

    [Fact]
    public void ALayoutTakesItsOwnEntranceAndTheCustomMix()
    {
        using var probe = new Probe(CompositionKind.Scattered);
        probe.CreateResources(_stage.Dc, _stage.DWrite, new Vector2(Width, Height), Scale);
        var style = LyricStyle.Default with
        {
            Reveal = "fade",
            RevealByLayout = new ValueMap([KeyValuePair.Create("scattered", "mix"), KeyValuePair.Create("echo", "pop")]),
            MixEntrances = new ValueList(["glide"]),
        };
        _stage.Render(probe, Fixtures.English(style), 11.56, 100, Scale, Background);
        Assert.All(probe.PlanFor(0)!.Chosen, chosen => Assert.Equal(WordArrival.Glide, chosen));

        using var editorial = new Probe(CompositionKind.Constellation);
        editorial.CreateResources(_stage.Dc, _stage.DWrite, new Vector2(Width, Height), Scale);
        _stage.Render(editorial, Fixtures.English(style), 11.56, 100, Scale, Background);
        Assert.All(editorial.PlanFor(0)!.Chosen, chosen => Assert.Equal(WordArrival.Fade, chosen)); // no entry: the one for all
    }
}
