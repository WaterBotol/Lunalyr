using System.Numerics;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Platform.Readings;
using Lunalyr.Render.Compositions.Stage;
using Lunalyr.Render.Pacing;
using Lunalyr.Render.Scene;
using Lunalyr.Tools.CompositionPreview;

namespace Lunalyr.Render.Compositions.Tests;

/// <summary>
/// Line setup spread over frames (laid out ahead, baked a slice at a time, or when first drawn) changes when text is
/// baked, never what is drawn: every composition draws the same pixels for the same song time as with each line set
/// whole on the frame that first needs it (the way lines were set before).
/// </summary>
[Collection(GpuCollection.Name)]
public sealed class SpreadSetupRenderTests : IDisposable
{
    private const int Width = 960, Height = 540;
    private const float Scale = 1;
    // Both on WARP, Direct2D's software rasteriser: the comparison is of Lunalyr's own logic (spread setup draws what
    // whole setup draws), and a hardware GPU shared with other processes now and then rasterises a pixel differently
    // between two devices (seen: 1 pixel 126/255 off, 6 pixels on mosaic, about 1 run in 20-50 under load). WARP
    // draws the same input the same way every time.
    private readonly Offscreen _stage = new(Width, Height, warp: true);
    // A device of its own for the layer compared: two layers baking on one device move a few of each other's pixels by a
    // step (Direct2D keeps state across bakes on a device), whichever way their lines are set. On two devices, two
    // layers setting lines the old way draw exactly the same frames here.
    private readonly Offscreen _other = new(Width, Height, warp: true);
    private readonly Sink _sink = new();
    private readonly Logger _logger;
    private static readonly Vector4 Background = Fixtures.Night.Base;

    public SpreadSetupRenderTests() => _logger = new Logger(LogLevel.Warn, _sink);

    public void Dispose()
    {
        _stage.Dispose();
        _other.Dispose();
    }

    private sealed class Sink : ILogSink
    {
        public List<string> Entries { get; } = [];
        public void Write(in LogEntry entry) => Entries.Add(entry.ToString() ?? "");
    }

    public static readonly string[] Ids =
    [
        "editorial", "spoken", "cascade", "cutout", "scattered", "constellation", "constellationEditorial", "echo",
        "hinge", "broadside", "portal", "mosaic", "torque", "fable",
    ];

    public static TheoryData<string> Compositions()
    {
        var data = new TheoryData<string>();
        foreach (var id in Ids) data.Add(id);
        return data;
    }

    private LyricCompositionLayer Layer(string id, bool spreads, Offscreen? on = null)
    {
        IWordBreaker breaker = IcuWordBreaker.TryCreate() ?? (IWordBreaker)SimpleWordBreaker.Instance;
        IRenderLayerFactory[] factories =
        [
            new EditorialFactory(breaker, _logger), new SpokenFactory(breaker, _logger), new CascadeFactory(breaker, _logger),
            new CutoutFactory(breaker, _logger), new ScatteredFactory(breaker, _logger), new ConstellationFactory(breaker, _logger),
            new ConstellationEditorialFactory(breaker, _logger), new EchoFactory(breaker, _logger), .. StageFactories.All(breaker, _logger),
        ];
        var layer = (LyricCompositionLayer)factories.Single(factory => factory.Id == id).Create();
        layer.SpreadsSetup = spreads;
        on ??= _stage;
        layer.CreateResources(on.Dc, on.DWrite, new Vector2(Width, Height), Scale);
        return layer;
    }

    private byte[] Frame(IRenderLayer layer, SceneSnapshot scene, double time, AheadWork? ahead = null, Offscreen? on = null)
    {
        on ??= _stage;
        on.Render(layer, scene, time, 100 + time, Scale, Background, ahead: ahead);
        return on.Pixels();
    }

    /// <summary>
    /// The songs played: word-timed English (backing vocals, a duet held under the next line, a held note), Japanese
    /// with readings, a line-timed song, and a song from its start with its opening title, cover and credits.
    /// </summary>
    private static IEnumerable<(string Name, SceneSnapshot Scene, double From, double To)> Songs()
    {
        yield return ("english", Fixtures.English(), 9.5, 26);
        yield return ("japanese", Fixtures.Japanese(), 9.5, 17);
        yield return ("linetimed", Fixtures.LineTimed(), 9.5, 20);
        yield return ("opening", OpeningFixtures.HumanNature(credits: true), 0, 13);
    }

    [Theory]
    [MemberData(nameof(Compositions))]
    public void SpreadSetupDrawsTheSameFrames(string id)
    {
        foreach (var (name, scene, from, to) in Songs())
        {
            using var whole = Layer(id, spreads: false);
            using var spread = Layer(id, spreads: true, _other);
            // An allowance all but spent: one slice of baking a frame, so lines are drawn while their text is still
            // being baked (and anything drawn before its turn is baked there and then).
            var ahead = new AheadWork();
            int waited = 0, frame = 0, compared = 0, inexact = 0;
            for (var t = from; t < to; t += 1.0 / 30, frame++)
            {
                ahead.NewPass();
                ahead.Spend(AheadWork.AllowanceTicks - 1);
                if (frame % 3 == 0)
                {
                    var expected = Frame(whole, scene, t);
                    var actual = Frame(spread, scene, t, ahead, _other);
                    compared++;
                    if (!expected.AsSpan().SequenceEqual(actual))
                    {
                        // Direct2D itself is not bit-stable at the last step: two runs of the unchanged renderer differ
                        // by one step in a few pixels of some frames. Nothing more than that is allowed, and rarely.
                        var (pixels, worst) = Compare(expected, actual);
                        Assert.True(worst <= 1 && pixels <= 8, $"{id} differs on {name} at {t:0.000} s: {pixels} pixels, up to {worst}/255");
                        inexact++;
                    }
                }
                else
                {
                    _stage.Render(whole, scene, t, 100 + t, Scale, Background);
                    _other.Render(spread, scene, t, 100 + t, Scale, Background, ahead: ahead);
                }
                if (spread.SetupPending) waited++;
            }
            Assert.True(waited > 0, $"{id} never spread its setup over frames on {name}");
            Assert.True(inexact * 100 <= compared, $"{id}: {inexact} of {compared} frames on {name} were a step off");
        }
        Assert.Empty(_sink.Entries);
    }

    /// <summary>How many pixels of two frames differ, and by how many steps at most.</summary>
    private static (int Pixels, int Worst) Compare(byte[] a, byte[] b)
    {
        int count = 0, worst = 0;
        for (var i = 0; i < a.Length; i += 4)
        {
            var d = Math.Max(Math.Abs(a[i] - b[i]), Math.Max(Math.Abs(a[i + 1] - b[i + 1]), Math.Abs(a[i + 2] - b[i + 2])));
            if (d == 0) continue;
            count++;
            worst = Math.Max(worst, d);
        }
        return (count, worst);
    }

    [Theory]
    [InlineData("editorial")]
    [InlineData("hinge")]
    [InlineData("spoken")]
    public void AJumpDrawsWhatPlayingThroughDraws(string id)
    {
        // A line needed at once is baked at once: a fresh layer jumping into the middle of a line draws the frame that
        // playing through to it (with its setup spread) draws.
        var scene = Fixtures.English();
        using var played = Layer(id, spreads: true);
        var ahead = new AheadWork();
        for (var t = 9.0; t < 15.3; t += 1.0 / 30)
        {
            ahead.NewPass();
            ahead.Spend(AheadWork.AllowanceTicks - 1);
            _stage.Render(played, scene, t, 100 + t, Scale, Background, ahead: ahead);
        }
        var afterPlaying = Frame(played, scene, 15.3);
        using var fresh = Layer(id, spreads: true);
        Assert.Equal(afterPlaying, Frame(fresh, scene, 15.3));
    }

    [Fact]
    public void SpreadFramesDoNotAllocateOnceSet()
    {
        using var layer = Layer("hinge", spreads: true);
        var scene = Fixtures.English();
        var ahead = new AheadWork();
        for (var t = 10.0; t < 17.0; t += 1.0 / 30)
        {
            ahead.NewPass();
            _stage.Render(layer, scene, t, t, Scale, Background, ahead: ahead);
        }
        _stage.Settle(layer, scene, 10.0, Scale, Background);
        Assert.False(layer.SetupPending);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var t = 10.0; t < 17.0; t += 1.0 / 60)
        {
            ahead.NewPass();
            _stage.Render(layer, scene, t, t, Scale, Background, ahead: ahead);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>
    /// Offscreen (no <see cref="AheadWork"/>: previews, lyric cards, tests) work ahead of time is always allowed: a line
    /// laid out ahead is baked whole on the next frame, however busy the machine is. A frame's own allowance of real time
    /// used to decide it, so what was left pending (and the frames and allocations that followed) changed with the load.
    /// </summary>
    [Theory]
    [InlineData("editorial")]
    [InlineData("scattered")]
    [InlineData("portal")]
    [InlineData("mosaic")]
    public void OffscreenSetupLaidOutAheadIsBakedOnTheNextFrame(string id)
    {
        using var layer = Layer(id, spreads: true);
        var scene = Fixtures.English();
        int laidOut = 0;
        for (var t = 9.5; t < 26; t += 1.0 / 30)
        {
            var pending = layer.SetupPending;
            _stage.Render(layer, scene, t, 100 + t, Scale, Background);
            if (pending) Assert.False(layer.SetupPending, $"{id}: setup still pending at {t:0.000} s");
            else if (layer.SetupPending) laidOut++;
        }
        Assert.True(laidOut > 0, $"{id} never laid a line out ahead");
        Assert.Empty(_sink.Entries);
    }

    [Fact]
    public void ABakeWaitsItsTurnAndALetGoBakeIsSkipped()
    {
        using var kit = new TextKit(_stage.Dc, _stage.DWrite) { Defers = true };
        using var layout = kit.Layout("Waiting", Face.Regular, 48, "en-us", false);
        var shadow = new ShadowSpec(0.5f, 3, 2);
        using var first = kit.Bake(layout, Vector4.One, 1, shadow);
        var dropped = kit.Bake(layout, Vector4.One, 1, shadow);
        using var middle = kit.Bake(layout, Vector4.One, 1, shadow);
        using var last = kit.Bake(layout, Vector4.One, 1, shadow);
        // Measured at once; baked later, a slice at a time: the text, then its shadow.
        Assert.True(first.Box.X > 50);
        Assert.False(first.IsBaked);
        Assert.True(kit.HasPending);
        kit.RunPending(0);
        Assert.False(first.IsBaked);
        kit.RunPending(0);
        Assert.True(first.IsBaked);
        Assert.False(middle.IsBaked);
        dropped.Dispose();
        // Drawn before its turn: baked there and then, with what was asked for before it (bakes keep their order).
        Assert.True(middle.Bake());
        Assert.True(middle.IsBaked);
        Assert.False(last.IsBaked);
        Assert.False(dropped.IsBaked);
        Assert.False(dropped.Bake());
        kit.RunPending(long.MaxValue);
        Assert.False(kit.HasPending);
        Assert.True(last.IsBaked);

        // The same bitmaps as a bake made at once.
        using var now = new TextKit(_stage.Dc, _stage.DWrite);
        using var made = now.Bake(layout, Vector4.One, 1, shadow);
        var world = Matrix3x2.CreateTranslation(Width / 2f, Height / 2f);
        Assert.Equal(Draw(made), Draw(first));

        byte[] Draw(Ink ink)
        {
            var layer = new DrawOnly(ink, world);
            _stage.Render(layer, new SceneSnapshot(), 0, 0, Scale, Background);
            return _stage.Pixels();
        }
    }

    /// <summary>A layer that only draws one ink.</summary>
    private sealed class DrawOnly(Ink ink, Matrix3x2 world) : IRenderLayer
    {
        public bool IsAnimating => false;
        public void CreateResources(Vortice.Direct2D1.ID2D1DeviceContext dc, Vortice.DirectWrite.IDWriteFactory dwrite, Vector2 size, float scale) { }
        public void Resize(Vector2 size, float scale) { }
        public void Render(in FrameContext frame) => ink.Draw(frame.Dc, world, 1, 1, 0, GlowSpec.None, 1);
        public void ReleaseResources() { }
        public void Dispose() { }
    }
}
