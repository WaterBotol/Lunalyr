using System.Numerics;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Logging;
using Lunalyr.Platform.Readings;
using Lunalyr.Render.Scene;
using Lunalyr.Tools.CompositionPreview;

namespace Lunalyr.Render.Compositions.Tests;

/// <summary>
/// The title card's layout, measured in pixels (the Mac's OpeningTitleCardLayoutPreviewTests.testTheCoverIsOneSizeForEveryTitle
/// and OpeningTitleFaceTests, 297b2f2 and 79088c3): a flat magenta cover and white type on black, the cover's rows and the
/// bands of type beside it found in a settled card.
/// </summary>
[Collection(GpuCollection.Name)]
public sealed class TitleCardLayoutRenderTests : IDisposable
{
    private const int Width = 1512, Height = 982;
    private readonly Offscreen _stage = new(Width, Height, warp: true);
    private readonly Logger _logger = new(LogLevel.Warn, new Sink());

    public void Dispose() => _stage.Dispose();

    private sealed class Sink : ILogSink
    {
        public void Write(in LogEntry entry) { }
    }

    /// <summary>A cover of one flat colour (magenta), so its drawn size can be measured.</summary>
    private static readonly CoverArt Flat = new(64, 64, [.. Enumerable.Range(0, 64 * 64).SelectMany(_ => new byte[] { 255, 0, 255, 255 })]);

    private sealed record Measured(int CoverTop, int CoverBottom, List<(int Top, int Bottom)> Bands)
    {
        public int Side => CoverBottom - CoverTop + 1;
        public int TitleHeight => Bands.Count == 0 ? 0 : Bands[0].Bottom - Bands[0].Top + 1;
    }

    private Measured Measure(string title, string? reading = null, string font = "", bool compact = false,
                             string artist = "WALLOWS", string album = "TELL ME THAT ITS OVER")
    {
        var scene = new SceneSnapshot
        {
            Title = title, Artist = artist, Album = album, TrackKey = title, TitleReading = reading, Cover = Flat, ReduceMotion = true,
            Palette = PaletteMapper.Fallback,
            Style = LyricStyle.Default with
            {
                OpeningTitle = true, OpeningTitleCover = true, Credits = false, Interludes = false,
                FontFamily = font.Length == 0 ? LyricStyle.Default.FontFamily : font,
            },
        };
        using var layer = new EditorialFactory(IcuWordBreaker.TryCreate(), _logger).Create();
        layer.CreateResources(_stage.Dc, _stage.DWrite, new Vector2(Width, Height), 1);
        // Settled: the full card at 5 s with nothing to sing along to; a compact one joined part-way.
        var (time, joined) = compact ? (98.0, 95.0) : (5.0, 0.0);
        _stage.Render([layer], scene, time, 100 + time, 1, joined);
        var pixels = _stage.Pixels();
        int coverTop = int.MaxValue, coverBottom = -1, coverRight = 0;
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width / 2; x += 2)
        {
            var i = (y * Width + x) * 4;
            if (pixels[i + 2] > 180 && pixels[i + 1] < 76 && pixels[i] > 180)
            {
                coverTop = Math.Min(coverTop, y);
                coverBottom = Math.Max(coverBottom, y);
                coverRight = Math.Max(coverRight, x);
            }
        }
        var bands = new List<(int Top, int Bottom)>();
        if (coverBottom < 0) return new Measured(0, -1, bands);
        for (var y = Math.Max(0, coverTop - 200); y < Math.Min(Height, coverBottom + 200); y++)
        {
            var lit = false;
            for (var x = coverRight + 4; x < Width && !lit; x++)
            {
                var i = (y * Width + x) * 4;
                // White type: the album is set at 70% (about 179 on black).
                lit = pixels[i] > 150 && pixels[i + 1] > 150 && pixels[i + 2] > 150;
            }
            if (!lit) continue;
            if (bands.Count > 0 && y <= bands[^1].Bottom + 2) bands[^1] = (bands[^1].Top, y);
            else bands.Add((y, y));
        }
        return new Measured(coverTop, coverBottom, bands);
    }

    // Ported from OpeningTitleCardLayoutPreviewTests.swift: testTheCoverIsOneSizeForEveryTitle
    [Fact]
    public void TheCoverIsOneSizeForEveryTitle()
    {
        foreach (var compact in new[] { false, true })
        {
            var one = Measure("Human Nature", compact: compact).Side;
            var two = Measure("Everything You Said Before the Summer Ended (Midnight Version)", compact: compact).Side;
            var reading = Measure("君の知らない物語のその先へ行こう", "Kimi no Shiranai Monogatari no Sono Saki e Ikou", compact: compact).Side;
            Assert.True(one > 20, $"compact {compact}: the cover was drawn ({one})");
            Assert.InRange(two, one - 2, one + 2);
            Assert.InRange(reading, one - 2, one + 2);
        }
    }

    // Ported from OpeningTitleFaceTests.swift: testTheCardKeepsItsShapeInAnyFace, in faces that come with Windows: the
    // default, a serif, a condensed one and a script.
    [Fact]
    public void TheCardKeepsItsShapeInAnyFace()
    {
        var heights = new Dictionary<string, double>();
        foreach (var face in new[] { "", "Georgia", "Impact", "Gabriola" })
        {
            var name = face.Length == 0 ? "default" : face;
            var card = Measure("MARVELOUS", font: face);
            double side = card.Side;
            Assert.True(side > 60, $"{name}: the cover was drawn");
            Assert.True(card.Bands.Count == 3, $"{name}: title, artist and album: {string.Join(", ", card.Bands)}");
            heights[name] = card.TitleHeight / side;
            // A one-line title's block is shorter than the cover: centred on it rather than top-aligned with a hole.
            double above = card.Bands[0].Top - card.CoverTop, below = card.CoverBottom - card.Bands[^1].Bottom;
            Assert.True(above >= -0.03 * side, $"{name}: the title stays within the cover's top ({above})");
            Assert.True(below >= -0.03 * side, $"{name}: the details stay within the cover's foot ({below})");
            Assert.True(Math.Abs(above - below) <= 0.06 * side, $"{name}: the block is centred on the cover ({above} above, {below} below)");
            // No hole: the title and the artist a set step apart (a script's swashes reach into it).
            var gap = (card.Bands[1].Top - card.Bands[0].Bottom) / side;
            Assert.True(Math.Abs(gap - Core.Compositions.OpeningTitle.BlockGap) <= 0.07, $"{name}: the step under the title ({gap})");
        }
        // The title's capitals stand at much the same height in every face.
        var mean = heights.Values.Average();
        foreach (var (name, height) in heights)
            Assert.True(Math.Abs(height - mean) <= mean * 0.15, $"{name}: title cap height {height} vs {mean}");
    }

    // Ported from OpeningTitleFaceTests.swift: testATwoLineTitleSpansTheCover
    [Fact]
    public void ATwoLineTitleSpansTheCover()
    {
        var card = Measure("EVERYTHING YOU SAID BEFORE THE SUMMER");
        double side = card.Side;
        Assert.True(card.Bands.Count >= 3, string.Join(", ", card.Bands));
        Assert.True(Math.Abs(card.Bands[0].Top - card.CoverTop) <= 0.04 * side, $"capitals on the cover's top: {card.Bands[0].Top} vs {card.CoverTop}");
        Assert.True(Math.Abs(card.CoverBottom - card.Bands[^1].Bottom) <= 0.04 * side, $"last baseline on its foot: {card.Bands[^1].Bottom} vs {card.CoverBottom}");
    }
}
