using System.Numerics;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Platform.Readings;
using Lunalyr.Render.Scene;
using Lunalyr.Tools.CompositionPreview;

namespace Lunalyr.Render.Compositions.Tests;

/// <summary>
/// Scattered, Constellation, Constellation + Editorial and Echo drawn offscreen, and every word entrance in every
/// whole-line composition: they draw, never throw, are pure in song time and allocate nothing per frame once set.
/// </summary>
[Collection(GpuCollection.Name)]
public sealed class ScatteredFamilyRenderTests : IDisposable
{
    private const int Width = 960, Height = 540;
    private const float Scale = 1;
    private readonly Offscreen _stage = new(Width, Height);
    private readonly Sink _sink = new();
    private readonly Logger _logger;
    private static readonly Vector4 Background = Fixtures.Night.Base;
    private static readonly IWordBreaker Breaker = IcuWordBreaker.TryCreate() ?? (IWordBreaker)SimpleWordBreaker.Instance;

    public ScatteredFamilyRenderTests() => _logger = new Logger(LogLevel.Warn, _sink);

    public void Dispose() => _stage.Dispose();

    private sealed class Sink : ILogSink
    {
        public List<string> Entries { get; } = [];
        public void Write(in LogEntry entry) => Entries.Add(entry.ToString() ?? "");
    }

    public static readonly string[] Ids = ["scattered", "constellation", "constellationEditorial", "echo"];

    public static readonly string[] Reveals =
        ["cutoutFold", "cutoutSlide", "cutoutHighlight", "satinRibbon", "splitInk", "mix", "mix:satinRibbon,cutoutSlide,cutoutHighlight"];

    private IRenderLayerFactory Factory(string id) => id switch
    {
        "scattered" => new ScatteredFactory(Breaker, _logger),
        "constellation" => new ConstellationFactory(Breaker, _logger),
        "constellationEditorial" => new ConstellationEditorialFactory(Breaker, _logger),
        "echo" => new EchoFactory(Breaker, _logger),
        "spoken" => new SpokenFactory(Breaker, _logger),
        _ => new EditorialFactory(Breaker, _logger),
    };

    private IRenderLayer Layer(string id, Offscreen? stage = null, float scale = Scale)
    {
        stage ??= _stage;
        var factory = Factory(id);
        Assert.Equal(id, factory.Id);
        var layer = factory.Create();
        layer.CreateResources(stage.Dc, stage.DWrite, new Vector2(stage.Width, stage.Height), scale);
        return layer;
    }

    private byte[] Frame(IRenderLayer layer, SceneSnapshot scene, double time, double wall = 100)
    {
        _stage.Render(layer, scene, time, wall, Scale, Background);
        return _stage.Pixels();
    }

    private static int Lit(byte[] pixels, int width = Width)
    {
        var count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
            if (pixels[i] + pixels[i + 1] + pixels[i + 2] > 3 * 110) count++;
        return count;
    }

    private static LyricStyle Style(string reveal) => LyricStyle.Default with { Reveal = reveal };

    private static SceneSnapshot Fixture(string name, LyricStyle? style = null) => name switch
    {
        "english" => Fixtures.English(style),
        "japanese" => Fixtures.Japanese(style),
        "arabic" => Fixtures.Arabic(style),
        "emoji" => Fixtures.Emoji(style),
        "linetimed" => Fixtures.LineTimed(style),
        "plain" => Fixtures.Plain(style),
        _ => Fixtures.NoLyrics(style),
    };

    public static TheoryData<string, string, double> Shots()
    {
        var data = new TheoryData<string, string, double>();
        foreach (var id in Ids)
        {
            data.Add(id, "english", 11.3);
            data.Add(id, "japanese", 12.8);
            data.Add(id, "arabic", 12.6);
            data.Add(id, "emoji", 13.0);
            data.Add(id, "linetimed", 12.0);
            data.Add(id, "plain", 5);
            data.Add(id, "nolyrics", 0);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Shots))]
    public void EveryFixtureDraws(string composition, string fixture, double time)
    {
        using var layer = Layer(composition);
        var pixels = Frame(layer, Fixture(fixture), time, wall: 100);
        if (fixture == "nolyrics") pixels = Frame(layer, Fixture(fixture), time, wall: 102);
        Assert.True(Lit(pixels) > (fixture == "nolyrics" ? 40 : 200), $"{composition} drew nothing for {fixture} at {time}");
        Assert.Empty(_sink.Entries);
    }

    public static TheoryData<string, string> RevealsEverywhere()
    {
        var data = new TheoryData<string, string>();
        foreach (var reveal in Reveals)
        foreach (var id in new[] { "editorial", "spoken", "scattered", "constellation", "echo" })
            data.Add(id, reveal);
        return data;
    }

    /// <summary>Every entrance in every composition: it draws each fixture through its arrivals, and never throws.</summary>
    [Theory]
    [MemberData(nameof(RevealsEverywhere))]
    public void EveryEntranceDrawsEveryFixture(string composition, string reveal)
    {
        using var layer = Layer(composition);
        foreach (var (fixture, from, to) in new[] { ("english", 9.8, 13.4), ("japanese", 9.8, 12.2), ("arabic", 9.8, 11.2), ("linetimed", 9.8, 10.6) })
        {
            var scene = Fixture(fixture, Style(reveal));
            for (var t = from; t < to; t += 0.09) Frame(layer, scene, t);
            Assert.True(Lit(Frame(layer, scene, to)) > 200, $"{composition} with {reveal} drew nothing for {fixture}");
        }
        Assert.Empty(_sink.Entries);
    }

    [Theory]
    [InlineData("scattered")]
    [InlineData("constellation")]
    [InlineData("constellationEditorial")]
    [InlineData("echo")]
    public void NothingIsDrawnWithoutLyricsAndBadInputNeverThrows(string composition)
    {
        using var layer = Layer(composition);
        Assert.Equal(0, Lit(Frame(layer, new SceneSnapshot(), 10)));
        Frame(layer, Fixtures.English(), double.NaN);
        LyricLine[] broken =
        [
            new() { Start = 1, End = 3, Text = "out of order", WordTimed = true,
                    Words = [new("order", 2.5, 2.9), new("out", 1, 1.4), new("of", 1.4, 0.2)] },
            new() { Start = 3, End = 4, Text = "" },
            new() { Start = 4, End = 6, Text = "", IsInstrumental = true },
            new() { Start = 1e12, End = double.NaN, Text = "far away", WordTimed = true, Words = [new("far", 1e12, double.NaN)] },
        ];
        foreach (var reveal in Reveals.Append("automatic"))
        {
            var scene = new SceneSnapshot
            {
                Lyrics = new LyricResult.Synced(broken, new LyricProvenance("Word-by-word", "Test")), Style = Style(reveal),
            };
            foreach (var time in new[] { -5, 0, 1.2, 2.7, 3.5, 5, 1e12, double.PositiveInfinity }) Frame(layer, scene, time);
        }
        Assert.Empty(_sink.Entries);
    }

    public static TheoryData<string, string> PureFrames()
    {
        var data = new TheoryData<string, string>();
        foreach (var id in Ids) data.Add(id, "automatic");
        foreach (var reveal in Reveals) data.Add("scattered", reveal);
        return data;
    }

    [Theory]
    [MemberData(nameof(PureFrames))]
    public void AFrameIsAPureFunctionOfSongTime(string composition, string reveal)
    {
        var scene = Fixtures.English(Style(reveal));
        using var played = Layer(composition);
        for (var t = 9.0; t < 12.15; t += 1.0 / 30) Frame(played, scene, t);
        var afterPlaying = Frame(played, scene, 12.15);
        using var fresh = Layer(composition);
        var jumpedTo = Frame(fresh, scene, 12.15);
        // Entrances drawn through effects (tinted words on tiles, swipes over blurred words) may round a pixel one
        // level differently after a different effect history; anything more is a real difference.
        var tolerance = reveal == "automatic" ? 0 : 1;
        var differing = Enumerable.Range(0, afterPlaying.Length).Where(i => Math.Abs(afterPlaying[i] - jumpedTo[i]) > tolerance).ToList();
        Assert.True(differing.Count == 0, $"{differing.Count} bytes differ, first at x {differing.FirstOrDefault() / 4 % Width}, y {differing.FirstOrDefault() / 4 / Width}, max {differing.Select(i => Math.Abs(afterPlaying[i] - jumpedTo[i])).DefaultIfEmpty(0).Max()}");
    }

    public static TheoryData<string, string> Allocations()
    {
        var data = new TheoryData<string, string>();
        foreach (var id in Ids) data.Add(id, "automatic");
        foreach (var reveal in Reveals)
        {
            data.Add("editorial", reveal);
            data.Add("spoken", reveal);
        }
        data.Add("constellation", "cutoutHighlight");
        data.Add("echo", "satinRibbon");
        return data;
    }

    /// <summary>
    /// Once a line is set nothing is allocated per frame: words arriving through every entrance (swipes and their
    /// layers, tiles and their tinted words, cutouts' clips), links and Echo's repeats included.
    /// </summary>
    [Theory]
    [MemberData(nameof(Allocations))]
    public void FramesDoNotAllocateOnceALineIsSet(string composition, string reveal)
    {
        long Allocated(string style)
        {
            using var layer = Layer(composition);
            var scene = Fixtures.English(Style(style));
            // Every frame drawn once, so lines are set, effects made and the JIT done.
            for (var t = 10.0; t < 12.6; t += 1.0 / 60) _stage.Render(layer, scene, t, t, Scale, Background);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var t = 10.0; t < 12.6; t += 1.0 / 60) _stage.Render(layer, scene, t, t, Scale, Background);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
        // Spoken's rolling window is rebuilt (and allocates) whenever a word joins or leaves it, whatever the entrance:
        // an entrance adds nothing to that.
        Assert.Equal(composition == "spoken" ? Allocated("fade") : 0, Allocated(reveal));
    }

    // Ported from RenderingPerformanceTests.swift: testSwipeStylesDrawDistinctInk (a word half way through its entrance)
    [Fact]
    public void SwipeStylesDrawDistinctInk()
    {
        using var layer = Layer("scattered");
        var ribbon = Frame(layer, Fixtures.English(Style("satinRibbon")), 12.17);
        var split = Frame(layer, Fixtures.English(Style("splitInk")), 12.17);
        var fade = Frame(layer, Fixtures.English(Style("fade")), 12.17);
        Assert.NotEqual(ribbon, split);
        Assert.NotEqual(ribbon, fade);
        // Once every word has arrived the swipes are gone: the same pixels as Soft focus but for the blur.
        var settledRibbon = Frame(layer, Fixtures.English(Style("satinRibbon")), 12.9);
        var settledSplit = Frame(layer, Fixtures.English(Style("splitInk")), 12.9);
        Assert.Equal(settledRibbon, settledSplit);
    }

    [Fact]
    public void ACutoutIsUnmaskedFromItsLeadingEdge()
    {
        using var layer = Layer("scattered");
        // "rain" (12.1) a few frames in: the cutout shows less of the word than Pop (which grows the same way).
        var cutout = Frame(layer, Fixtures.English(Style("cutoutFold")), 12.13);
        var settled = Frame(layer, Fixtures.English(Style("cutoutFold")), 12.9);
        Assert.NotEqual(cutout, settled);
        Assert.Empty(_sink.Entries);
    }

    /// <summary>Pixels within a little of <paramref name="colour"/> (sRGB, 0…1).</summary>
    private static int Near(byte[] pixels, (double R, double G, double B) colour)
    {
        var count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (Math.Abs(pixels[i + 2] / 255.0 - colour.R) < 0.1 && Math.Abs(pixels[i + 1] / 255.0 - colour.G) < 0.1
                && Math.Abs(pixels[i] / 255.0 - colour.B) < 0.1) count++;
        }
        return count;
    }

    // Ported from LyricHighlightTests.swift: testEveryLayoutHighlightsTheSungWord (the whole-line layouts; Spoken's
    // tiles belong to the rolling stage and are not built yet)
    [Theory]
    [InlineData("editorial")]
    [InlineData("scattered")]
    [InlineData("constellation")]
    [InlineData("constellationEditorial")]
    [InlineData("echo")]
    public void EveryLayoutHighlightsTheSungWord(string composition)
    {
        var tile = LyricInks.Settled(Fixtures.Night).TileColour;
        var colour = (tile.R, tile.G, tile.B);
        using var layer = Layer(composition);
        var plain = Near(Frame(layer, Fixtures.English(), 11.3), colour);
        var highlighted = Near(Frame(layer, Fixtures.English(Style("cutoutHighlight")), 11.3), colour);
        Assert.True(highlighted > plain + 60, $"{composition}: no tile behind the sung words ({highlighted} vs {plain})");
        // In a mix, words that draw "Cutout · Highlight" get it.
        Assert.True(Near(Frame(layer, Fixtures.English(Style("mix:cutoutHighlight")), 11.3), colour) > plain + 60);
    }

    /// <summary>A layer that shows its plans, to compare what the layout set.</summary>
    private sealed class Probe(CompositionKind kind) : LyricCompositionLayer(kind, ScatteredFamilyRenderTests.Breaker, null)
    {
        public LinePlan? PlanFor(int row) => Plan(row);
    }

    // Ported from LyricHighlightTests.swift: testTheTileNeverMovesOrResizesWords (the constellations' slots and links;
    // the rolling stages' part is the rolling stage's)
    [Theory]
    [InlineData(CompositionKind.Constellation)]
    [InlineData(CompositionKind.ConstellationEditorial)]
    public void TheTileNeverMovesOrResizesWords(CompositionKind kind)
    {
        (PointD[] Centres, Core.Motion.Segment[] Links) Set(string reveal)
        {
            using var probe = new Probe(kind);
            probe.CreateResources(_stage.Dc, _stage.DWrite, new Vector2(Width, Height), Scale);
            _stage.Render(probe, Fixtures.English(Style(reveal)), 11.56, 100, Scale, Background);
            var plan = probe.PlanFor(0)!;
            return ([.. plan.Runs.Select(run => run.Centre)], plan.Links);
        }
        var plain = Set("automatic");
        var tiled = Set("cutoutHighlight");
        Assert.Equal(6, plain.Centres.Length);
        Assert.NotEmpty(plain.Links);
        Assert.Equal(plain.Centres, tiled.Centres);
        Assert.Equal(plain.Links, tiled.Links);
    }

    [Fact]
    public void ConstellationLinksJoinConsecutiveWordsAndScatterThem()
    {
        using var probe = new Probe(CompositionKind.Constellation);
        probe.CreateResources(_stage.Dc, _stage.DWrite, new Vector2(Width, Height), Scale);
        _stage.Render(probe, Fixtures.English(), 11.56, 100, Scale, Background);
        var plan = probe.PlanFor(0)!;
        Assert.All(plan.Links, link => Assert.Equal(link.From + 1, link.To));
        var heights = plan.Typography.Runs.Select(run => run.Metrics.WaveOffset).ToArray();
        Assert.True(heights.Max() - heights.Min() > 5, string.Join(", ", heights));
        using var scattered = new Probe(CompositionKind.Scattered);
        scattered.CreateResources(_stage.Dc, _stage.DWrite, new Vector2(Width, Height), Scale);
        _stage.Render(scattered, Fixtures.English(), 11.56, 100, Scale, Background);
        Assert.Empty(scattered.PlanFor(0)!.Links);
        Assert.Null(scattered.PlanFor(0)!.Echo);
    }

    [Fact]
    public void EchoRepeatsThePhraseBehindTheLine()
    {
        using var echo = Layer("echo");
        using var scattered = Layer("scattered");
        var scene = Fixtures.English();
        var withRepeats = Frame(echo, scene, 12.9);
        var without = Frame(scattered, scene, 12.9);
        // The repeats are faint: count anything a little brighter than the backdrop.
        static int Faint(byte[] pixels)
        {
            var count = 0;
            for (var i = 0; i < pixels.Length; i += 4)
                if (pixels[i] + pixels[i + 1] + pixels[i + 2] > 3 * 30) count++;
            return count;
        }
        Assert.True(Faint(withRepeats) > Faint(without) + 2000, $"{Faint(withRepeats)} vs {Faint(without)}");
        // A line-timed line is a plain phrase in Echo too.
        Assert.Equal(Frame(scattered, Fixtures.LineTimed(), 12.0), Frame(echo, Fixtures.LineTimed(), 12.0));
    }

    // Ported from LyricHighlightTests.swift: testARunOfJapaneseIsHighlightedWordByWord
    [Fact]
    public void ARunOfJapaneseIsHighlightedWordByWord()
    {
        (string, double, double)[] tokens =
        [
            ("君", 30.0, 30.3), ("と", 30.3, 30.5), ("歩いた", 30.5, 31.0), ("あの", 31.0, 31.4), ("道", 31.4, 31.7),
            ("を", 31.7, 31.9), ("今も", 31.9, 32.4), ("覚えてる", 32.4, 33.6),
        ];
        var words = tokens.Select(token => new LyricWord(token.Item1, token.Item2, token.Item3)).ToList();
        var line = new LyricLine { Start = 30, End = 33.6, Text = "君と歩いたあの道を今も覚えてる", Words = words, WordTimed = true };
        var glue = LyricRuns.Glue(line, words);
        var layout = EditorialSetting.Layout(words, glue, 44, 11, 0, true, false);
        Assert.Single(layout.Runs);
        var segments = HighlightSegment.Of(layout, words, glue, Breaker, 34)[0];
        Assert.True(segments.Length >= 5, string.Join(" | ", segments));
        Assert.Equal(0, segments[0].From);
        Assert.Equal(1, segments[^1].To, 9);
        for (var k = 1; k < segments.Length; k++)
        {
            Assert.Equal(segments[k - 1].To, segments[k].From, 9);
            Assert.True(segments[k - 1].Onset < segments[k].Onset);
        }
        Assert.All(segments, segment => Assert.Equal(34, segment.Release));
        Assert.Equal(segments.Length, segments.Count(segment => LyricHighlightTiming.Pose(33.9, segment.Onset, segment.Release, false).IsVisible));
    }

    private static int[] InkedColumns(byte[] pixels, int width, int height, int fromY, int toY)
    {
        var columns = new List<int>();
        for (var x = 0; x < width; x++)
        for (var y = fromY; y < toY; y += 1)
        {
            var i = (y * width + x) * 4;
            if (pixels[i] + pixels[i + 1] + pixels[i + 2] > 3 * 128) { columns.Add(x); break; }
        }
        return [.. columns];
    }

    /// <summary>The empty stretches between inked columns, left to right, wider than <paramref name="least"/>.</summary>
    private static List<int> Gaps(int[] columns, int least = 2)
    {
        var gaps = new List<int>();
        for (var k = 1; k < columns.Length; k++)
            if (columns[k] - columns[k - 1] - 1 > least) gaps.Add(columns[k] - columns[k - 1] - 1);
        return gaps;
    }

    // Ported from LyricReviewFixesTests.swift: testWordGapsStayNearASpace (two settled words on a level row, in
    // Scattered: the gap between their ink is about a third of an em)
    [Fact]
    public void WordGapsStayNearASpace()
    {
        // Line 4 sets level and upright, as the Mac test's style does; filler lines come first.
        LyricLine Filler(int i) => new() { Start = i, End = i + 0.5, Text = "la", WordTimed = true, Words = [new("la", i, i + 0.5)] };
        LyricLine[] lines =
        [
            Filler(0), Filler(1), Filler(2), Filler(3),
            new() { Start = 5, End = 8, Text = "Hold light", WordTimed = true, Words = [new("Hold", 5, 5.4), new("light", 5.4, 5.8)] },
            Filler(8),
        ];
        var scene = new SceneSnapshot
        {
            Lyrics = new LyricResult.Synced(lines, new LyricProvenance("Word-by-word", "Test")), Palette = Fixtures.Night,
            Style = LyricStyle.Default with { Reveal = "fade" },
        };
        using var layer = Layer("scattered");
        var pixels = Frame(layer, scene, 7.2);
        var columns = InkedColumns(pixels, Width, Height, 0, Height);
        var widest = Gaps(columns).DefaultIfEmpty(0).Max();
        var metrics = ViewportMetrics.For(Width, Height, Scale, LyricStyle.Default.SizeFraction);
        var em = EditorialSetting.Layout(lines[4].Words, [false, false], metrics.BaseSize, 11, 4, true, false).Runs[0].Metrics.Size;
        Assert.InRange(widest / em, 0.15, 0.55);
    }

    /// <summary>
    /// The gap before a held, emphasised word stays open while it is sung: its swell is held to its own clearance and
    /// its hold swells it glyph by glyph rather than widening it ("neon RAIN", not "neonRAIN").
    /// </summary>
    [Fact]
    public void TheGapBeforeAHeldWordStaysOpenWhileItIsSung()
    {
        using var layer = Layer("editorial");
        var scene = Fixtures.English(Style("fade"));
        int GapBeforeLast(double time)
        {
            var pixels = Frame(layer, scene, time);
            var gaps = Gaps(InkedColumns(pixels, Width, Height, Height / 2 - 60, Height / 2 + 60));
            return gaps.Count > 0 ? gaps[^1] : 0;
        }
        var sung = GapBeforeLast(12.9);
        var metrics = ViewportMetrics.For(Width, Height, Scale, LyricStyle.Default.SizeFraction);
        // At least half of "neon"'s own clearance (0.12 em of a word near 0.64 of the base size) is left.
        Assert.True(sung > 0.12 * 0.64 * metrics.BaseSize * 0.5, $"gap of {sung} px at base {metrics.BaseSize:0.0}");
    }

    // Ported from RenderingPerformanceTests.swift: testRenderedConstellationWithWideWordsStaysOnPortraitDesktop (at the
    // default spread: the snapshot has no Spread setting)
    [Theory]
    [InlineData("constellation")]
    [InlineData("constellationEditorial")]
    public void AConstellationOfWideWordsStaysOnAPortraitDesktop(string composition)
    {
        using var portrait = new Offscreen(420, 680);
        LyricLine Filler(int i) => new() { Start = i, End = i + 0.5, Text = "la", WordTimed = true, Words = [new("la", i, i + 0.5)] };
        var words = Enumerable.Range(0, 10).Select(i => new LyricWord(new string('W', 9 + i * 2), 10 + i * 0.2, 10.8 + i * 0.2)).ToList();
        LyricLine[] lines = [Filler(0), Filler(1), new() { Start = 10, End = 13, Text = "W", Words = words, WordTimed = true }];
        var scene = new SceneSnapshot
        {
            Lyrics = new LyricResult.Synced(lines, new LyricProvenance("Word-by-word", "Test")), Palette = Fixtures.Night,
            Style = LyricStyle.Default with { SizeFraction = 0.075f * 1.5f },
        };
        using var layer = Layer(composition, portrait);
        portrait.Render(layer, scene, 12.4, 100, Scale, Background);
        var pixels = portrait.Pixels();
        double minX = 1, maxX = 0, minY = 1, maxY = 0;
        for (var y = 0; y < portrait.Height; y += 2)
        for (var x = 0; x < portrait.Width; x += 2)
        {
            var i = (y * portrait.Width + x) * 4;
            if (Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2])) <= 0.45 * 255) continue;
            minX = Math.Min(minX, x / (double)portrait.Width); maxX = Math.Max(maxX, x / (double)portrait.Width);
            minY = Math.Min(minY, y / (double)portrait.Height); maxY = Math.Max(maxY, y / (double)portrait.Height);
        }
        Assert.True(maxX > minX, "nothing drawn");
        Assert.True(minX > 0.06 && maxX < 0.94, $"{composition}: x {minX:0.00}…{maxX:0.00}");
        Assert.True(minY > 0.09 && maxY < 0.91, $"{composition}: y {minY:0.00}…{maxY:0.00}");
        Assert.Empty(_sink.Entries);
    }
}
