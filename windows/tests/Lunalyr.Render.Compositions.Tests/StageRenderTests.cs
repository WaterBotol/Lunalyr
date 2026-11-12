using System.Numerics;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics;
using Lunalyr.Platform.Readings;
using Lunalyr.Render.Compositions.Stage;
using Lunalyr.Render.Scene;
using Lunalyr.Tools.CompositionPreview;

namespace Lunalyr.Render.Compositions.Tests;

/// <summary>
/// The stage layouts (Hinge, Broadside, Portal, Mosaic, Torque, Fable) drawn offscreen: the drawing parts of the Mac's
/// StageLyricSnapshotTests, StageMotionTests, FableRenderTests and StageReadingTests, on the same lines.
/// </summary>
[Collection(GpuCollection.Name)]
public sealed class StageRenderTests : IDisposable
{
    private const int Width = 960, Height = 540;
    private const float Scale = 1;
    private readonly Offscreen _stage = new(Width, Height);
    private readonly Sink _sink = new();
    private readonly Logger _logger;
    private static readonly Vector4 Background = Fixtures.Night.Base;

    public static readonly string[] Ids = ["hinge", "broadside", "portal", "mosaic", "torque", "fable"];

    public StageRenderTests() => _logger = new Logger(LogLevel.Warn, _sink);

    public void Dispose() => _stage.Dispose();

    private sealed class Sink : ILogSink
    {
        public List<string> Entries { get; } = [];
        public void Write(in LogEntry entry) => Entries.Add(entry.ToString() ?? "");
    }

    public static TheoryData<string> Layouts()
    {
        var data = new TheoryData<string>();
        foreach (var id in Ids) data.Add(id);
        return data;
    }

    private IRenderLayer Layer(string id, Offscreen? on = null)
    {
        on ??= _stage;
        var factory = StageFactories.All(IcuWordBreaker.TryCreate(), _logger).Single(f => f.Id == id);
        var layer = factory.Create();
        layer.CreateResources(on.Dc, on.DWrite, new Vector2(on.Width, on.Height), Scale);
        return layer;
    }

    private byte[] Frame(IRenderLayer layer, SceneSnapshot scene, double time, Offscreen? on = null)
    {
        on ??= _stage;
        on.Render(layer, scene, time, 100, Scale, Background);
        return on.Pixels();
    }

    private static bool IsLit(byte[] pixels, int i) => pixels[i] + pixels[i + 1] + pixels[i + 2] > 3 * 110;

    /// <summary>Anything drawn at all (dim words, readings, shadows): the pixel differs from the empty background.</summary>
    private static bool Drawn(byte[] pixels, int i, byte[] blank) =>
        Math.Abs(pixels[i] - blank[i]) + Math.Abs(pixels[i + 1] - blank[i + 1]) + Math.Abs(pixels[i + 2] - blank[i + 2]) > 6;

    private static int Lit(byte[] pixels) => Enumerable.Range(0, pixels.Length / 4).Count(i => IsLit(pixels, i * 4));

    private static int Difference(byte[] a, byte[] b)
    {
        var count = 0;
        for (var i = 0; i < a.Length; i += 4)
            if (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]) > 3 * 12) count++;
        return count;
    }

    /// <summary>Lit pixels within <paramref name="inset"/> of the edge of the surface.</summary>
    private static int EdgeLit(byte[] pixels, int width, int height, int inset, byte[] blank)
    {
        var count = 0;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            if (y >= inset && y < height - inset && x >= inset && x < width - inset) continue;
            if (Drawn(pixels, (y * width + x) * 4, blank)) count++;
        }
        return count;
    }

    /// <summary>One word-timed line of <paramref name="texts"/>, a word every <paramref name="step"/> seconds from 1 s.</summary>
    private static LyricLine Line(string[] texts, double step = 0.5, double start = 1, bool[]? spaced = null)
    {
        var words = texts.Select((text, i) =>
            new LyricWord(spaced is null || spaced[i] ? text + (i < texts.Length - 1 ? " " : "") : text, start + i * step, start + i * step + step * 0.9)).ToList();
        return new LyricLine
        {
            // Ends as the next line (Following) starts, so the stage's own hold (1.2 s past the last word) decides when it leaves.
            Start = start, End = words[^1].End + 2.3, Text = string.Concat(words.Select(w => w.Text)).Trim(), WordTimed = true, Words = words,
        };
    }

    private static SceneSnapshot Scene(LyricLine line, string? reading = null, ReadingPlacement placement = ReadingPlacement.Below,
                                       float size = 0.075f) => new()
    {
        Title = "Stage", Artist = "Test",
        Lyrics = new LyricResult.Synced([line, Following(line)], new LyricProvenance("Word-by-word", "Test")),
        Readings = reading is null ? new Dictionary<int, string>() : new Dictionary<int, string> { [0] = reading },
        Palette = Fixtures.Night,
        Style = LyricStyle.Default with { Readings = placement, SizeFraction = size },
    };

    private static SceneSnapshot Fixture(string name) => name switch
    {
        "english" => Fixtures.English(),
        "japanese" => Fixtures.Japanese(),
        "arabic" => Fixtures.Arabic(),
        "emoji" => Fixtures.Emoji(),
        "linetimed" => Fixtures.LineTimed(),
        "plain" => Fixtures.Plain(),
        _ => Fixtures.NoLyrics(),
    };

    // Ported from StageLyricSnapshotTests.swift: testEveryStageLayoutDraws (every fixture, as CompositionRenderTests)
    [Theory]
    [MemberData(nameof(Layouts))]
    public void EveryFixtureDraws(string id)
    {
        using var layer = Layer(id);
        foreach (var (fixture, time) in new[] { ("english", 11.3), ("japanese", 12.8), ("arabic", 12.6), ("emoji", 13.0), ("linetimed", 12.0), ("plain", 5.0) })
            Assert.True(Lit(Frame(layer, Fixture(fixture), time)) > 200, $"{id} drew nothing for {fixture} at {time}");
        Assert.Empty(_sink.Entries);
    }

    // Ported from StageMotionTests.swift: testEveryLayoutDrawsThroughHandOversAndReduceMotion (Reduce Motion is not in
    // the scene snapshot), with the stage's exit as the line's own
    [Theory]
    [MemberData(nameof(Layouts))]
    public void EveryLayoutDrawsThroughHandOversAndTheExit(string id)
    {
        using var layer = Layer(id);
        var line = Line("Rain over the city can fly away tonight".Split(' '), 0.6);
        var scene = Scene(line);
        foreach (var t in new[] { 2.75, 2.8, 2.85, 2.9, 3.0, 3.15, 5.5, 6.2 })
            Assert.True(Lit(Frame(layer, scene, t)) > 100, $"{id} drew nothing at {t}");
        // Gone once the line has been held and has left.
        Assert.Equal(0, Lit(Frame(layer, scene, LyricPhraseTimingEnd(line) + 0.9)));
        Assert.Empty(_sink.Entries);
    }

    private static double LyricPhraseTimingEnd(LyricLine line) => Core.Motion.LyricPhraseTiming.StageHeldUntil(line);

    /// <summary>A line 2.3 s after <paramref name="line"/>, so no instrumental break (2.4 s or more) opens before it.</summary>
    private static LyricLine Following(LyricLine line) => new()
    {
        Start = line.End, End = line.End + 1, Text = "next", WordTimed = true, Words = [new("next", line.End, line.End + 1)],
    };

    // Ported from StageLyricSnapshotTests.swift: testWordsOutOfOrderNeverCrashTheStageLayouts
    [Theory]
    [MemberData(nameof(Layouts))]
    public void WordsOutOfOrderNeverCrashTheStageLayouts(string id)
    {
        using var layer = Layer(id);
        var line = new LyricLine
        {
            Start = 10, End = 13, Text = "hello world again", WordTimed = true,
            Words = [new("hello ", 12, 13), new("world ", 10, 11), new("again", 11, 12)],
        };
        foreach (var time in new[] { 10.5, 11.5, 12.5 }) Frame(layer, Scene(line), time);
        Assert.Empty(_sink.Entries);
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void NothingIsDrawnWithoutLyricsAndBadInputNeverThrows(string id)
    {
        using var layer = Layer(id);
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
        var scene = new SceneSnapshot { Lyrics = new LyricResult.Synced(broken, new LyricProvenance("Word-by-word", "Test")) };
        foreach (var time in new[] { -5, 0, 1.2, 2.7, 3.5, 5, 1e12, double.PositiveInfinity }) Frame(layer, scene, time);
        Assert.Empty(_sink.Entries);
    }

    // Ported from FableLayoutTests.swift: testSameLineSameMomentDrawsTheSame (and a frame is a pure function of song time)
    [Theory]
    [MemberData(nameof(Layouts))]
    public void AFrameIsAPureFunctionOfSongTime(string id)
    {
        var scene = Fixtures.English();
        using var played = Layer(id);
        for (var t = 9.0; t < 11.3; t += 1.0 / 30) Frame(played, scene, t);
        var afterPlaying = Frame(played, scene, 11.3);
        using var fresh = Layer(id);
        Assert.Equal(afterPlaying, Frame(fresh, scene, 11.3));
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void SettledFramesDoNotAllocate(string id)
    {
        using var layer = Layer(id);
        var scene = Fixtures.English();
        // Onsets, a hand-over to the next line, backing vocals and that line's exit.
        for (var t = 10.0; t < 17.0; t += 1.0 / 30) Frame(layer, scene, t);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var t = 10.0; t < 17.0; t += 1.0 / 60) _stage.Render(layer, scene, t, t, Scale, Background);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // Ported from FableLayoutTests.swift: testFableStaysOnStageForEveryKindOfLine
    [Fact]
    public void FableStaysOnStageForEveryKindOfLine()
    {
        var cases = new List<(string Name, LyricLine Line, string? Reading)>
        {
            ("normal", Line("Rain over the city can fly".Split(' ')), null),
            ("long", Line("And if the night keeps calling out my name I will follow every echo down the empty streets until the morning finds us".Split(' '), 0.3), null),
            ("short", Line(["Oh"]), null),
            ("longword", Line(["Supercalifragilisticexpialidocious", "yeah"]), null),
            ("japanese", Line(["今", "日", "も", "雨", "が", "降", "る", "け", "ど"], 0.35, 1, [false, false, true, false, true, false, true, false, false]), "kyoumo amega furu kedo"),
            ("korean", Line(["사랑해", "너를", "영원히", "기억할게"]), "saranghae neoreul yeongwonhi gieokhalge"),
            ("arabic", Line("أنا أحبك يا قمر الليل الجميل".Split(' ')), null),
            ("hebrew", Line("אני אוהב אותך עד הסוף".Split(' ')), null),
            ("emoji", Line("dancing 💃🏽 all night 🌙✨ with you 👨‍👩‍👧".Split(' ')), null),
            ("credits", Line("Written by: Jane Doe, John Smith & The Orchestra".Split(' ')), null),
            ("instrumental", Line(["♪"]), null),
        };
        // The user's two displays, at the size a stage is set on them.
        foreach (var (w, h) in new[] { (1512, 982), (2560, 1440) })
        {
            using var on = new Offscreen(w, h);
            foreach (var (name, line, reading) in cases)
            {
                using var layer = Layer("fable", on);
                var lastStart = line.Words.Max(word => word.Start);
                foreach (var time in new[] { (1 + lastStart) / 2 + 0.05, lastStart + 1.0 })
                {
                    var pixels = Frame(layer, Scene(line, reading), time, on);
                    Assert.True(Lit(pixels) > 20, $"{name} on {w} drew nothing at {time}");
                    // The stage is 80% × 72% of the screen: nothing at all (shadows included) reaches the band round it.
                    Assert.Equal(0, StageBandLit(pixels, w, h, Frame(layer, new SceneSnapshot(), 0, on)));
                }
            }
        }
        Assert.Empty(_sink.Entries);
    }

    /// <summary>Lit pixels outside the stage (80% × 72% of the surface, centred), less 3 pixels.</summary>
    private static int StageBandLit(byte[] pixels, int width, int height, byte[] blank)
    {
        int left = (int)(width * 0.1) - 3, right = (int)(width * 0.9) + 3, top = (int)(height * 0.14) - 3, bottom = (int)(height * 0.86) + 3;
        var count = 0;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            if (x >= left && x < right && y >= top && y < bottom) continue;
            if (Drawn(pixels, (y * width + x) * 4, blank)) count++;
        }
        return count;
    }

    // Ported from FableLayoutTests.swift: testSettledLineHoldsStillAndLeaves
    [Fact]
    public void FableSettledLineHoldsStillAndLeaves()
    {
        using var layer = Layer("fable");
        foreach (var (line, reading) in new[]
        {
            (Line("Rain over the city can fly".Split(' ')), (string?)null),
            (Line(["今", "日", "も", "雨", "が", "降", "る", "け", "ど"], 0.35, 1, [false, false, true, false, true, false, true, false, false]), "kyoumo amega furu kedo"),
        })
        {
            var scene = Scene(line, reading);
            var lastStart = line.Words.Max(word => word.Start);
            // The stage keeps a sung line 1.2 s past its last word; the Mac's test held it forever.
            Assert.Equal(Frame(layer, scene, lastStart + 0.8), Frame(layer, scene, lastStart + 1.3));
            Assert.Equal(0, Lit(Frame(layer, scene, LyricPhraseTimingEnd(line) + 0.9)));
        }
    }

    // Ported from FableLayoutTests.swift: testHonoursEditorialTypographyAndChosenEntrances (the chosen entrance; the
    // editorial typography and Cutout · Highlight settings are not in the scene snapshot)
    [Fact]
    public void FableHonoursAChosenEntrance()
    {
        var line = Line("Rain over the city can fly".Split(' '), 0.5);
        using var layer = Layer("fable");
        var plain = Frame(layer, Scene(line), 3.08);
        using var lifted = Layer("fable");
        var lift = Scene(line) with { Style = Scene(line).Style with { Reveal = "lift" } };
        Assert.True(Difference(plain, Frame(lifted, lift, 3.08)) > 50);
    }

    private static LyricLine Japanese => Line(["君", "の", "名", "前", "を", "呼", "ん", "で", "夜", "空", "に", "消", "え", "た"], 0.22, 1,
        [false, true, false, false, true, false, false, true, false, false, true, false, false, true]);

    // Ported from StageReadingTests.swift: testEveryLayoutDrawsReadings (one reading per run, as the snapshot pairs it,
    // and a reading that cannot be split, drawn under the stage)
    [Theory]
    [MemberData(nameof(Layouts))]
    public void EveryLayoutDrawsReadings(string id)
    {
        var line = Japanese;
        var time = line.Words.Max(w => w.Start) + 0.9;
        foreach (var reading in new[] { "kimino namaewo yonde yozorani kieta", "kimi no namae wo yonde yozora ni kieta" })
        {
            using var with = Layer(id);
            using var without = Layer(id);
            var a = Frame(with, Scene(line, reading), time);
            var b = Frame(without, Scene(line, reading, ReadingPlacement.Off), time);
            Assert.True(Difference(a, b) > 150, $"{id}: no reading was drawn for \"{reading}\"");
        }
    }

    // Ported from StageReadingTests.swift: testNoReadingsDrawNothing (the drawing part)
    [Theory]
    [MemberData(nameof(Layouts))]
    public void NoReadingsDrawNothing(string id)
    {
        var line = Japanese;
        var time = line.Words.Max(w => w.Start) + 0.9;
        using var off = Layer(id);
        using var stranger = Layer(id);
        var none = Frame(off, Scene(line, "kimino namaewo yonde yozorani kieta", ReadingPlacement.Off), time);
        var other = Scene(line) with { Readings = new Dictionary<int, string> { [5] = "ame" } };
        Assert.Equal(none, Frame(stranger, other, time));
    }

    // Ported from StageReadingTests.swift: testReadingsStayOnScreen
    [Theory]
    [InlineData("portal")]
    [InlineData("hinge")]
    [InlineData("torque")]
    public void ReadingsStayOnScreen(string id)
    {
        foreach (var (w, h) in new[] { (1512, 982), (2560, 1440) })
        {
            using var on = new Offscreen(w, h);
            foreach (var (line, reading) in new[]
            {
                (Japanese, "kimino namaewo yonde yozorani kieta"),
                (Line(["사랑해", "너를", "영원히", "기억할게"]), "saranghae neoreul yeongwonhi gieokhalge"),
            })
            {
                var lastStart = line.Words.Max(word => word.Start);
                foreach (var time in new[] { (1 + lastStart) / 2 + 0.6, lastStart + 0.9 })
                {
                    using var layer = Layer(id, on);
                    // The size slider at its top (the Mac's 1.5).
                    var pixels = Frame(layer, Scene(line, reading, size: 0.1125f), time, on);
                    Assert.True(EdgeLit(pixels, w, h, 3, Frame(layer, new SceneSnapshot(), 0, on)) == 0, $"{id} at {time} s on {w} reaches the screen edge");
                }
            }
        }
    }

    // Ported from StageReadingTests.swift: testReplaceSetsTheRomanisedWords (the drawing part)
    [Theory]
    [MemberData(nameof(Layouts))]
    public void InsteadSetsTheRomanisedWords(string id)
    {
        var line = Line(["君", "の", "名", "前"], 0.3, 1, [false, false, false, false]);
        using var romaji = Layer(id);
        using var kanji = Layer(id);
        var a = Frame(romaji, Scene(line, "kimi no na mae", ReadingPlacement.Instead), 2.5);
        var b = Frame(kanji, Scene(line, "kimi no na mae", ReadingPlacement.Off), 2.5);
        Assert.True(Difference(a, b) > 150, $"{id} did not draw the romanised words");
    }

    // The Mac's default lyric colour mode, Album drift: timed words take pastel tints of the cover's colours.
    [Theory]
    [MemberData(nameof(Layouts))]
    public void TheLyricColoursFollowTheAlbum(string id)
    {
        static Palette Cover(byte r, byte g, byte b) =>
            PaletteMapper.FromAlbum(new Core.Colour.AlbumWallpaperPalette(Core.Colour.AlbumColourGrid.Extract(Core.Colour.ArtworkPixels.Solid(64, 64, r, g, b))));
        using var layer = Layer(id);
        var scene = Fixtures.English();
        var red = Frame(layer, scene with { Palette = Cover(200, 30, 40) }, 11.3);
        var blue = Frame(layer, scene with { Palette = Cover(30, 60, 210) }, 11.3);
        Assert.True(Difference(red, blue) > 200, $"{id}: the album's colours changed nothing");
        Assert.Empty(_sink.Entries);
    }
}
