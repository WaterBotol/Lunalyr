using System.Numerics;
using Lunalyr.Core.Logging;
using Lunalyr.Platform.Readings;
using Lunalyr.Render.Compositions.Stage;
using Lunalyr.Render.Scene;
using Lunalyr.Tools.CompositionPreview;

namespace Lunalyr.Render.Compositions.Tests;

/// <summary>The song's opening title drawn offscreen: where it sits, its cover, what it waits for and what waits for it.</summary>
[Collection(GpuCollection.Name)]
public sealed class OpeningTitleRenderTests : IDisposable
{
    private const int Width = 960, Height = 540;
    private const float Scale = 1;
    private readonly Offscreen _stage = new(Width, Height);
    private readonly Sink _sink = new();
    private readonly Logger _logger;

    public OpeningTitleRenderTests() => _logger = new Logger(LogLevel.Warn, _sink);

    public void Dispose() => _stage.Dispose();

    private sealed class Sink : ILogSink
    {
        public List<string> Entries { get; } = [];
        public void Write(in LogEntry entry) => Entries.Add(entry.ToString() ?? "");
    }

    private IRenderLayer Layer(string id)
    {
        var breaker = IcuWordBreaker.TryCreate();
        IRenderLayerFactory[] factories =
        [
            new EditorialFactory(breaker, _logger), new SpokenFactory(breaker, _logger), new ScatteredFactory(breaker, _logger),
            .. StageFactories.All(breaker, _logger),
        ];
        var layer = factories.Single(factory => factory.Id == id).Create();
        layer.CreateResources(_stage.Dc, _stage.DWrite, new Vector2(Width, Height), Scale);
        return layer;
    }

    private byte[] Frame(IRenderLayer layer, SceneSnapshot scene, double time, double joined = 0)
    {
        _stage.Render([layer], scene, time, 100 + time, Scale, joined);
        return _stage.Pixels();
    }

    private static SceneSnapshot Off(SceneSnapshot scene) => scene with { Style = scene.Style with { OpeningTitle = false } };

    /// <summary>Pixels that differ (by more than GPU rounding) between two frames, within a box of the frame.</summary>
    private static int Differing(byte[] a, byte[] b, int fromX = 0, int toX = Width, int fromY = 0, int toY = Height)
    {
        var count = 0;
        for (var y = fromY; y < toY; y++)
        for (var x = fromX; x < toX; x++)
        {
            var i = (y * Width + x) * 4;
            if (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]) > 24) count++;
        }
        return count;
    }

    private static int Lit(byte[] pixels, int fromY, int toY)
    {
        var count = 0;
        for (var y = fromY; y < toY; y++)
        for (var x = 0; x < Width; x++)
        {
            var i = (y * Width + x) * 4;
            if (pixels[i] + pixels[i + 1] + pixels[i + 2] > 3 * 110) count++;
        }
        return count;
    }

    [Theory]
    [InlineData("editorial")]
    [InlineData("spoken")]
    [InlineData("scattered")]
    [InlineData("hinge")]
    [InlineData("portal")]
    public void TheCardIsLowOnTheLeftAndGoneBeforeTheFirstLine(string composition)
    {
        using var layer = Layer(composition);
        var scene = OpeningFixtures.HumanNature();
        // Holding (first line at 11 s: the full card holds from 2.1 s and leaves by 9.25 s).
        var on = Frame(layer, scene, 5);
        var off = Frame(layer, Off(scene), 5);
        Assert.True(Differing(on, off, 0, Width / 2, Height / 2, Height) > 2000, $"{Differing(on, off, 0, Width / 2, Height / 2, Height)}");
        // Nothing of it on the right or at the top (its corner shade reaches no further than a fifth of the way down).
        Assert.Equal(0, Differing(on, off, Width * 3 / 4, Width, 0, Height));
        Assert.Equal(0, Differing(on, off, 0, Width, 0, Height / 5));
        // Gone once the first line can show.
        Assert.Equal(0, Differing(Frame(layer, scene, 11.6), Frame(layer, Off(scene), 11.6)));
        Assert.Empty(_sink.Entries);
    }

    [Fact]
    public void TheCoverSitsBesideTheTitleAndTheWordsMoveForIt()
    {
        using var layer = Layer("editorial");
        var with = Frame(layer, OpeningFixtures.HumanNature(), 5);
        var coverOff = Frame(layer, OpeningFixtures.HumanNature(coverOn: false), 5);
        // The cover's square, at the card's left edge (padding: 5.8% of the width).
        Assert.True(Differing(with, coverOff, 56, 56 + 40, Height / 2, Height) > 1500, $"{Differing(with, coverOff, 56, 96, Height / 2, Height)}");
        // No artwork draws as the setting off does.
        Assert.Equal(0, Differing(coverOff, Frame(layer, OpeningFixtures.HumanNature(cover: false) with { Palette = OpeningFixtures.HumanNature().Palette }, 5)));
    }

    [Fact]
    public void CreditsWaitForTheCardToLeave()
    {
        using var layer = Layer("editorial");
        var scene = OpeningFixtures.HumanNature(credits: true);
        // With the card off the credit is spelled out in the top band from 0.5 s; with it on, not before 9.25 s.
        Assert.True(Lit(Frame(layer, Off(scene), 4), 0, Height / 4) > 150);
        Assert.Equal(0, Lit(Frame(layer, scene, 4), 0, Height / 4));
        Assert.True(Lit(Frame(layer, scene, 11), 0, Height / 4) > 150);
    }

    [Fact]
    public void ASongJoinedPartWayGetsOnlyABriefCardFromWhereItWasJoined()
    {
        using var layer = Layer("editorial");
        var scene = OpeningFixtures.HumanNature();
        // Joined at 95 s: nothing at the song's start time, a brief compact card from 95.3 s.
        Assert.Equal(0, Differing(Frame(layer, scene, 5, joined: 95), Frame(layer, Off(scene), 5, joined: 95)));
        var brief = Frame(layer, scene, 97, joined: 95);
        Assert.True(Differing(brief, Frame(layer, Off(scene), 97, joined: 95), 0, Width / 2, Height / 2, Height) > 800);
        // Compact: smaller than the full card.
        var full = Differing(Frame(layer, scene, 5), Frame(layer, Off(scene), 5), 0, Width / 2, Height / 2, Height);
        Assert.True(Differing(brief, Frame(layer, Off(scene), 97, joined: 95), 0, Width / 2, Height / 2, Height) < full);
    }

    [Fact]
    public void UnderReduceMotionTheCardOnlyFades()
    {
        using var layer = Layer("editorial");
        var scene = OpeningFixtures.HumanNature() with { ReduceMotion = true };
        // Part way in, every part of the card is equally there: no wipe, so the end of the title shows as its start does.
        var entering = Frame(layer, scene, 0.8);
        var off = Frame(layer, Off(scene), 0.8);
        Assert.True(Differing(entering, off, 0, Width / 2, Height / 2, Height) > 500);
        // Settled, it is the card as drawn with motion (below the interlude dots, which hold still under Reduce Motion).
        Assert.Equal(0, Differing(Frame(layer, scene, 5), Frame(layer, OpeningFixtures.HumanNature(), 5), 0, Width / 2, Height * 3 / 5, Height));
    }

    [Fact]
    public void TheCardIsAPureFunctionOfSongTimeAndAFrameAllocatesNothing()
    {
        var scene = OpeningFixtures.HumanNature();
        using var played = Layer("editorial");
        for (var t = 0.4; t < 3; t += 1.0 / 30) Frame(played, scene, t);
        var afterPlaying = Frame(played, scene, 3);
        using var fresh = Layer("editorial");
        Assert.Equal(0, Differing(afterPlaying, Frame(fresh, scene, 3)));

        // Through the entrance and the exit again, every piece already set: the card adds no allocation to a frame.
        Assert.Equal(Allocated(Off(scene)), Allocated(scene));
    }

    /// <summary>Bytes a layer allocates drawing the card's entrance and exit a second time (the first sets everything).</summary>
    private long Allocated(SceneSnapshot scene)
    {
        using var layer = Layer("editorial");
        IRenderLayer[] layers = [layer];
        long allocated = 0;
        foreach (var from in new[] { 0.5, 8.4 })
        {
            for (var t = from; t < from + 1; t += 1.0 / 60) _stage.Render(layers, scene, t, 100 + t, Scale);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var t = from; t < from + 1; t += 1.0 / 60) _stage.Render(layers, scene, t, 100 + t, Scale);
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
        }
        return allocated;
    }
}
