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

    private static SceneSnapshot Kept(SceneSnapshot scene, double strength = 0.55) =>
        scene with { Style = scene.Style with { OpeningTitleKeep = true, OpeningTitleStrength = strength } };

    // The Mac's d849c49: the card stays for the whole song, low on the left, quieter once the ordinary card would have
    // left; credits wait only as long as for the ordinary card.
    [Fact]
    public void AKeptCardStaysAllSongAndRestsAtTitleStrength()
    {
        using var layer = Layer("editorial");
        var scene = OpeningFixtures.HumanNature();
        var off = Frame(layer, Off(scene), 60);
        var rested = Frame(layer, Kept(scene), 60);
        Assert.True(Differing(rested, off, 0, Width / 2, Height / 2, Height) > 1000, $"{Differing(rested, off, 0, Width / 2, Height / 2, Height)}");
        Assert.Equal(0, Differing(Frame(layer, scene, 60), off)); // not kept: long gone
        // At 100% it stays as strong as it arrived; at the default it rests quieter.
        var solid = Frame(layer, Kept(scene, 1), 60);
        Assert.True(Differing(solid, rested, 0, Width / 2, Height / 2, Height) > 300);
        Assert.Equal(0, Differing(Frame(layer, Kept(scene, 1), 5), Frame(layer, Kept(scene), 5))); // before it rests, the same
        var credits = OpeningFixtures.HumanNature(credits: true);
        Assert.True(Lit(Frame(layer, Kept(credits), 11), 0, Height / 4) > 150);
        Assert.Empty(_sink.Entries);
    }

    // The Mac's d849c49: in the floating window the kept title is a row at the top, and the lyrics move down clear of it.
    [Fact]
    public void TheFloatingWindowsKeptTitleIsARowAboveTheLyrics()
    {
        using var layer = Layer("editorial");
        var floating = OpeningFixtures.HumanNature() with { Style = OpeningFixtures.HumanNature().Style with { FloatingTextSize = 40 } };
        var kept = Kept(floating);
        var untitled = kept with { Title = "" };
        // The row: in the band at the top (40 × (0.18 + 0.62 + 0.16) ≈ 38 px), nowhere in the lower half.
        var row = Frame(layer, kept, 60);
        var none = Frame(layer, untitled, 60);
        Assert.True(Differing(row, none, 0, Width, 0, 40) > 200, $"{Differing(row, none, 0, Width, 0, 40)}");
        Assert.Equal(0, Differing(row, none, 0, Width, Height / 2, Height));
        // The lyrics sit lower than without the band.
        var line = Frame(layer, floating, 12.5);
        var lowered = Frame(layer, untitled, 12.5);
        Assert.True(Centroid(lowered) > Centroid(line) + 10, $"{Centroid(line)} → {Centroid(lowered)}");
        Assert.Empty(_sink.Entries);
    }

    /// <summary>The title card's own window (the Mac's b11a1ab3): the scene as that window draws it, its box set by an inset.</summary>
    private static SceneSnapshot CardWindow(SceneSnapshot scene, double inset = 0) =>
        Kept(scene with { Style = scene.Style with { FloatingTextSize = 40, FloatingTitleOnly = true, FloatingInset = inset } }, 1);

    // Ported from OpeningTitleTests.swift (b11a1ab3): the card's window draws the card alone, never the lyrics.
    [Fact]
    public void TheTitleCardsWindowDrawsOnlyTheCard()
    {
        using var layer = Layer("editorial");
        var card = CardWindow(OpeningFixtures.HumanNature());
        var drawn = Frame(layer, card, 12.5);
        Assert.True(Lit(drawn, 0, Height) > 400, $"{Lit(drawn, 0, Height)}");
        // No title: nothing at all, though a line is being sung.
        Assert.Equal(0, Lit(Frame(layer, card with { Title = "" }, 12.5), 0, Height));
        Assert.Empty(_sink.Entries);
    }

    // Ported from OpeningTitleTests.swift (b11a1ab3): under 14 pt the card becomes one row, cross-fading for 0.25 s.
    [Fact]
    public void TheCardCrossFadesToARowWhenItsBoxGetsSmall()
    {
        using var layer = Layer("editorial");
        var scene = OpeningFixtures.HumanNature();
        IRenderLayer[] layers = [layer];
        // A 30 px tall box in the middle: too small for a stacked title.
        var small = CardWindow(scene, inset: (Height - 30) / 2.0);
        const double song = 60;
        _stage.Render(layers, CardWindow(scene), song, 200, Scale);
        _stage.Render(layers, small, song, 200.05, Scale);
        var fading = _stage.Pixels();
        _stage.Render(layers, small, song, 201, Scale);
        var row = _stage.Pixels();
        // Settled: only the row, in its box.
        Assert.True(Lit(row, Height / 2 - 15, Height / 2 + 15) > 100, $"{Lit(row, Height / 2 - 15, Height / 2 + 15)}");
        Assert.Equal(0, Lit(row, 0, Height / 2 - 40));
        // A twentieth of a second in, the card (at the small box's size) is still fading out over it.
        Assert.True(Differing(fading, row) > 50, $"{Differing(fading, row)}");
        Assert.Empty(_sink.Entries);
    }

    /// <summary>The mean row of the lit pixels.</summary>
    private static double Centroid(byte[] pixels)
    {
        double sum = 0, count = 0;
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var i = (y * Width + x) * 4;
            if (pixels[i] + pixels[i + 1] + pixels[i + 2] <= 3 * 110) continue;
            sum += y;
            count++;
        }
        return count == 0 ? double.NaN : sum / count;
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
            // Line setup is spread over frames: let it finish, so what follows only draws.
            _stage.Settle(layers, scene, from, 100 + from, Scale);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var t = from; t < from + 1; t += 1.0 / 60) _stage.Render(layers, scene, t, 100 + t, Scale);
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
        }
        return allocated;
    }
}
