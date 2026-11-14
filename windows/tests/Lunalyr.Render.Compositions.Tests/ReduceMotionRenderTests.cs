using System.Numerics;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics;
using Lunalyr.Platform.Readings;
using Lunalyr.Render.Compositions.Stage;
using Lunalyr.Render.Scene;
using Lunalyr.Tools.CompositionPreview;

namespace Lunalyr.Render.Compositions.Tests;

/// <summary>
/// Reduce Motion (<see cref="SceneSnapshot.ReduceMotion"/>, Windows' "Animation effects" off) in every composition,
/// drawn offscreen: what the Mac's lyricReduceMotion does to its views.
/// </summary>
[Collection(GpuCollection.Name)]
public sealed class ReduceMotionRenderTests : IDisposable
{
    private const int Width = 960, Height = 540;
    private const float Scale = 1;
    // WARP, Direct2D's software rasteriser: these compare frames pixel for pixel, and a hardware GPU shared with other
    // processes now and then rasterises a pixel differently (see SpreadSetupRenderTests); WARP draws the same input the
    // same way every time.
    private readonly Offscreen _stage = new(Width, Height, warp: true);
    private readonly Sink _sink = new();
    private readonly Logger _logger;
    private static readonly Vector4 Background = Fixtures.Night.Base;

    public ReduceMotionRenderTests() => _logger = new Logger(LogLevel.Warn, _sink);

    public void Dispose() => _stage.Dispose();

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

    public static TheoryData<string> All()
    {
        var data = new TheoryData<string>();
        foreach (var id in Ids) data.Add(id);
        return data;
    }

    private IRenderLayer Layer(string id)
    {
        var breaker = IcuWordBreaker.TryCreate();
        IRenderLayerFactory[] factories =
        [
            new EditorialFactory(breaker, _logger), new SpokenFactory(breaker, _logger), new CascadeFactory(breaker, _logger),
            new CutoutFactory(breaker, _logger), new ScatteredFactory(breaker, _logger), new ConstellationFactory(breaker, _logger),
            new ConstellationEditorialFactory(breaker, _logger), new EchoFactory(breaker, _logger), .. StageFactories.All(breaker, _logger),
        ];
        var layer = factories.Single(f => f.Id == id).Create();
        layer.CreateResources(_stage.Dc, _stage.DWrite, new Vector2(Width, Height), Scale);
        return layer;
    }

    private byte[] Frame(IRenderLayer layer, SceneSnapshot scene, double time)
    {
        _stage.Render(layer, scene, time, 100, Scale, Background);
        return _stage.Pixels();
    }

    private static int Difference(byte[] a, byte[] b)
    {
        var count = 0;
        for (var i = 0; i < a.Length; i += 4)
            if (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]) > 3 * 12) count++;
        return count;
    }

    /// <summary>Pixels that differ clearly from the empty background.</summary>
    private static int Drawn(byte[] pixels)
    {
        var count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
            if (Math.Abs(pixels[i + 2] - Background.X * 255) + Math.Abs(pixels[i + 1] - Background.Y * 255) + Math.Abs(pixels[i] - Background.Z * 255) > 36) count++;
        return count;
    }

    private static int Lit(byte[] pixels)
    {
        var count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
            if (pixels[i] + pixels[i + 1] + pixels[i + 2] > 3 * 110) count++;
        return count;
    }

    /// <summary>A word-timed line, a word every 0.4 s from 1 s, held for 2 s after its last word (too short for an
    /// instrumental break), then another line.</summary>
    private static SceneSnapshot Held(bool reduceMotion)
    {
        string[] texts = ["Rain", "over", "the", "city", "lights"];
        var words = texts.Select((text, i) => new LyricWord(text + (i < texts.Length - 1 ? " " : ""), 1 + i * 0.4, 1.35 + i * 0.4)).ToList();
        var line = new LyricLine { Start = 1, End = words[^1].End + 2, Text = string.Concat(words.Select(w => w.Text)), WordTimed = true, Words = words };
        var next = new LyricLine { Start = line.End, End = line.End + 2, Text = "next", WordTimed = true, Words = [new("next", line.End, line.End + 1)] };
        return new SceneSnapshot
        {
            Title = "Calm", Artist = "Test", Palette = Fixtures.Night, ReduceMotion = reduceMotion,
            Lyrics = new LyricResult.Synced([line, next], new LyricProvenance("Word-by-word", "Test")),
        };
    }

    // Every composition draws every fixture with Reduce Motion, and a frame is still a pure function of song time.
    [Theory]
    [MemberData(nameof(All))]
    public void EveryCompositionDrawsUnderReduceMotionAndStaysPureInTime(string id)
    {
        foreach (var (scene, time) in new[]
        {
            (Fixtures.English() with { ReduceMotion = true }, 11.3), (Fixtures.Japanese() with { ReduceMotion = true }, 12.8),
            (Fixtures.LineTimed() with { ReduceMotion = true }, 10.15), (ExtrasFixtures.Credits() with { ReduceMotion = true }, 5.5),
            (ExtrasFixtures.Highlight() with { ReduceMotion = true }, 12.9),
        })
        {
            using var played = Layer(id);
            for (var t = time - 0.5; t < time; t += 1.0 / 30) Frame(played, scene, t);
            var afterPlaying = Frame(played, scene, time);
            // Hinge's Reduce Motion page turn is a cross-fade, both pages dimmed part way through it: drawn at all.
            Assert.True((id == "hinge" ? Drawn(afterPlaying) : Lit(afterPlaying)) > 100, $"{id} drew nothing at {time}");
            using var fresh = Layer(id);
            Assert.Equal(afterPlaying, Frame(fresh, scene, time));
        }
        Assert.Empty(_sink.Entries);
    }

    // Turning Reduce Motion on while a song shows sets the line again (its colours drift ten times slower, so a frame
    // differs whether or not the layout itself moves), and the layer draws exactly what one that started with it draws:
    // nothing baked for full motion is left over.
    [Theory]
    [MemberData(nameof(All))]
    public void TurningReduceMotionOnTakesEffectAtOnce(string id)
    {
        var full = Fixtures.English();
        var calm = full with { ReduceMotion = true };
        using var switched = Layer(id);
        Frame(switched, full, 12.9);
        using var fresh = Layer(id);
        Assert.Equal(Frame(fresh, calm, 12.9), Frame(switched, calm, 12.9));
        Assert.True(Difference(Frame(switched, full, 12.9), Frame(fresh, calm, 12.9)) > 0, $"{id}: Reduce Motion changed nothing");
    }

    // The Mac's lyricReduceMotion stops the idle float and Echo's wandering repeats: a line that has been sung holds
    // still (only its colours could change, and they are baked per line).
    [Theory]
    [InlineData("editorial")]
    [InlineData("scattered")]
    [InlineData("constellation")]
    [InlineData("constellationEditorial")]
    [InlineData("echo")]
    public void ASungLineHoldsStillUnderReduceMotion(string id)
    {
        using var calm = Layer(id);
        var still = Held(reduceMotion: true);
        var a = Frame(calm, still, 3.6);
        Assert.True(Lit(a) > 100);
        var later = Difference(a, Frame(calm, still, 4.05));
        Assert.True(later == 0, $"{id} moved {later} pixels while held");
        // With motion the words float (and Echo's repeats wander) meanwhile.
        using var moving = Layer(id);
        var full = Held(reduceMotion: false);
        Assert.True(Difference(Frame(moving, full, 3.6), Frame(moving, full, 4.05)) > 0, $"{id} held still with full motion");
        Assert.Empty(_sink.Entries);
    }
}
