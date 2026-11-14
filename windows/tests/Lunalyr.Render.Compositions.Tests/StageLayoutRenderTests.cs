using System.Numerics;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Motion;
using Lunalyr.Platform.Readings;
using Lunalyr.Render.Compositions.Stage;
using Lunalyr.Render.Scene;
using Lunalyr.Tools.CompositionPreview;

namespace Lunalyr.Render.Compositions.Tests;

/// <summary>
/// The Build 88 stage layouts' parts that need the system (the word breaker, the icon font, drawing): Portal's words
/// and icons, and Mosaic's bar (the Mac's PortalLayoutTests and MosaicLayoutTests, their drawing and Japanese parts).
/// </summary>
[Collection(GpuCollection.Name)]
public sealed class StageLayoutRenderTests : IDisposable
{
    private const float Scale = 1;
    private readonly Offscreen _stage = new(1280, 720, warp: true);
    private readonly Sink _sink = new();
    private readonly Logger _logger;
    private static readonly Vector4 Background = Fixtures.Night.Base;

    public StageLayoutRenderTests() => _logger = new Logger(LogLevel.Warn, _sink);

    public void Dispose() => _stage.Dispose();

    private sealed class Sink : ILogSink
    {
        public List<string> Entries { get; } = [];
        public void Write(in LogEntry entry) => Entries.Add(entry.ToString() ?? "");
    }

    /// <summary>君の名前を呼んで夜空に消えた, sung a character at a time (StageReadingTests.japanese).</summary>
    private static List<LyricWord> Japanese =>
        [.. new[] { "君", "の", "名", "前", "を", "呼", "ん", "で", "夜", "空", "に", "消", "え", "た" }
            .Select((text, i) => new LyricWord(text, 1 + i * 0.22, 1 + i * 0.22 + 0.22 * 0.9))];

    private static bool[] JapaneseGlue => [false, true, false, true, true, false, true, true, false, true, true, false, true, true];

    // Ported from PortalLayoutTests.swift: testJapaneseGluedLineIsPartedIntoWords
    [Fact]
    public void JapaneseGluedLineIsPartedIntoWords()
    {
        var breaker = IcuWordBreaker.TryCreate();
        Assert.NotNull(breaker);
        var words = Japanese;
        // Glued as the app glues an unspaced line: one run of tokens.
        var appGlue = words.Select((_, i) => i > 0).ToArray();
        var runs = PortalGeometry.Runs(words, appGlue, breaker!);
        Assert.True(runs.Count > 2);
        Assert.True(runs.Count < words.Count);
        Assert.Equal(string.Concat(words.Select(word => word.Text)), string.Concat(runs.Select(run => run.Text)));
        Assert.True(runs.Any(run => run.Text.Contains("名前", StringComparison.Ordinal)), $"{string.Join(" ", runs.Select(r => r.Text))}: 名前 is one word");
        // The test line's own glue (already parted) is never joined further: every pill lies inside one of its words. (The
        // Mac keeps it exactly; Windows' word breaker parts 呼んで as 呼 んで, as Windows' rolling layouts do too.)
        var given = PortalGeometry.Runs(words, JapaneseGlue, breaker!);
        var parted = StageRun.Make(words, JapaneseGlue);
        Assert.Equal(string.Concat(parted.Select(run => run.Text)), string.Concat(given.Select(run => run.Text)));
        foreach (var pill in given)
            Assert.Contains(parted, run => run.Start <= pill.Start && pill.End <= run.End && run.Text.Contains(pill.Text, StringComparison.Ordinal));
    }

    // Ported from LyricWordIconsTests.swift: testEverySymbolResolves (on Windows, every glyph chosen is in the icon font
    // itself; the dollar sign is set from the text face by fallback, by choice)
    [Fact]
    public void EveryIconGlyphIsInTheIconFont()
    {
        using var kit = new TextKit(_stage.Dc, _stage.DWrite);
        foreach (var (symbol, glyph) in SegoeIcons.Glyphs)
        {
            if (glyph is null || glyph == "$") continue;
            Assert.True(kit.SymbolFontHas(glyph), $"{symbol}: U+{(int)glyph[0]:X4} is not in {SegoeIcons.Family}");
        }
    }

    private IRenderLayer Layer(string id)
    {
        var layer = StageFactories.All(IcuWordBreaker.TryCreate(), _logger).Single(f => f.Id == id).Create();
        layer.CreateResources(_stage.Dc, _stage.DWrite, new Vector2(_stage.Width, _stage.Height), Scale);
        return layer;
    }

    private static SceneSnapshot Scene(IReadOnlyList<LyricWord> words, IReadOnlyList<bool> glue)
    {
        // The words as the parser hands them: a space after each word that is not glued to the next.
        var spaced = words.Select((word, i) => word with { Text = word.Text + (i + 1 < words.Count && !glue[i + 1] ? " " : "") }).ToList();
        var line = new LyricLine
        {
            Start = spaced[0].Start, End = spaced[^1].End + 2.3, Text = string.Concat(spaced.Select(w => w.Text)).Trim(), WordTimed = true,
            Words = spaced,
        };
        var next = new LyricLine { Start = line.End, End = line.End + 1, Text = "next", WordTimed = true, Words = [new("next", line.End, line.End + 1)] };
        return new SceneSnapshot
        {
            Title = "Stage", Artist = "Test", Palette = Fixtures.Night,
            Lyrics = new LyricResult.Synced([line, next], new LyricProvenance("Word-by-word", "Test")),
        };
    }

    private byte[] Frame(IRenderLayer layer, SceneSnapshot scene, double time)
    {
        _stage.Render(layer, scene, time, 100, Scale, Background);
        return _stage.Pixels();
    }

    // Ported from PortalLayoutTests.swift: testPortalDrawsIconsShapesAndGluedWords
    [Fact]
    public void PortalDrawsIconsShapesAndGluedWords()
    {
        List<LyricWord> icons = [.. "call me home my love the city lights are burning bright".Split(' ').Select((w, i) => new LyricWord(w, 1 + i * 0.45, 1 + i * 0.45 + 0.4))];
        (string Text, double Start, bool Glued)[] syllables =
        [
            ("so", 0.80, false), ("beau", 1.00, false), ("ti", 1.20, true), ("ful", 1.40, true), ("run", 1.90, false),
            ("ning", 2.10, true), ("un", 2.60, false), ("for", 2.75, true), ("get", 2.90, true), ("ta", 3.05, true),
            ("ble", 3.20, true), ("night", 3.60, false),
        ];
        List<LyricWord> fast = [.. "we run it back and turn it up til the lights go".Split(' ').Select((w, i) => new LyricWord(w, 1 + i * 0.18, 1 + i * 0.18 + 0.16))];
        (IReadOnlyList<LyricWord> Words, IReadOnlyList<bool> Glue)[] lines =
        [
            (icons, new bool[icons.Count]),
            ([.. syllables.Select(s => new LyricWord(s.Text, s.Start, s.Start + 0.14))], [.. syllables.Select(s => s.Glued)]),
            (fast, new bool[fast.Count]),
            (Japanese, [.. Japanese.Select((_, i) => i > 0)]),
        ];
        using var layer = Layer("portal");
        foreach (var (words, glue) in lines)
        {
            var scene = Scene(words, glue);
            var lit = 0;
            for (var time = 0.9; time <= 4.5; time += 0.37) lit += Frame(layer, scene, time).Where((_, i) => i % 4 == 0).Count(b => b > 110);
            Assert.True(lit > 1000, "Portal drew nothing");
        }
        Assert.Empty(_sink.Entries);
    }

    // Ported from MosaicLayoutTests.swift: testBarNeverShowsWithoutItsWord. Drawn: whenever any of the bar shows, its word
    // shows on it in full proportion, through the whole entrance.
    [Fact]
    public void MosaicBarNeverShowsWithoutItsWord()
    {
        // "oh" is the longest word held, so huge; "no" takes the bar.
        List<LyricWord> words = [new("oh", 1, 1.5), new("no", 1.6, 2.0)];
        Assert.Equal([MosaicKind.Huge, MosaicKind.Bar], MosaicGeometry.Beats(StageRun.Make(words, [false, false])).Select(beat => beat.Kind));
        using var layer = Layer("mosaic");
        var scene = Scene(words, [false, false]);
        var frames = new List<byte[]>();
        for (var step = 0; step <= 30; step++) frames.Add(Frame(layer, scene, 1.6 - 0.2 + step / 60.0));
        // The bar's colour: the commonest strongly coloured pixel once it is fully in.
        var bar = Commonest(frames[^1]);
        (int Bar, int Word) Count(byte[] pixels)
        {
            int w = _stage.Width, h = _stage.Height;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, count = 0;
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                if (!Near(pixels, i, bar, 18)) continue;
                count++;
                minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
            }
            if (count == 0) return (0, 0);
            var word = 0;
            for (var y = minY; y <= maxY; y++)
            for (var x = minX; x <= maxX; x++)
            {
                var i = (y * w + x) * 4;
                if (!Near(pixels, i, bar, 60) && !Near(pixels, i, Background, 30)) word++;
            }
            return (count, word);
        }
        var full = Count(frames[^1]);
        var ratio = (double)full.Word / Math.Max(1, full.Bar);
        Assert.True(ratio > 0.05, "the finished bar should carry its word");
        for (var index = 0; index < frames.Count; index++)
        {
            var (barPixels, wordPixels) = Count(frames[index]);
            if (barPixels > 600)
                Assert.True((double)wordPixels / barPixels > ratio * 0.6, $"frame {index}: the bar shows with too little of its word");
        }
    }

    private static bool Near(byte[] pixels, int i, Vector4 colour, int tolerance) =>
        Math.Abs(pixels[i + 2] - colour.X * 255) + Math.Abs(pixels[i + 1] - colour.Y * 255) + Math.Abs(pixels[i] - colour.Z * 255) <= tolerance;

    /// <summary>The commonest saturated colour in a frame (BGRA), as 0…1 RGB.</summary>
    private static Vector4 Commonest(byte[] pixels)
    {
        var counts = new Dictionary<int, int>();
        for (var i = 0; i < pixels.Length; i += 4)
        {
            int b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
            if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) < 60) continue;
            var key = (r << 16) | (g << 8) | b;
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        var best = counts.MaxBy(pair => pair.Value).Key;
        return new Vector4(((best >> 16) & 255) / 255f, ((best >> 8) & 255) / 255f, (best & 255) / 255f, 1);
    }
}
