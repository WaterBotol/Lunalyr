using System.Numerics;
using Lunalyr.Core.Logging;
using Lunalyr.Platform.Readings;
using Lunalyr.Render.Compositions.Stage;
using Lunalyr.Render.Scene;
using Lunalyr.Shell.Demo;
using Lunalyr.Tools.CompositionPreview;

namespace Lunalyr.Render.Compositions.Tests;

/// <summary>
/// A held song clock (a scrub held still on the desktop player's seek bar, or a pause) in every composition: the lyric
/// on screen stays exactly as it was however long the wall clock runs. Drawn on the demo song "Neon Harbour", in its
/// long wrapping line ("Every little window on the boulevard…", 32.5–37.9 s) and in an ordinary one.
/// </summary>
[Collection(GpuCollection.Name)]
public sealed class HeldLyricRenderTests : IDisposable
{
    private const int Width = 960, Height = 540;
    private const float Scale = 1;
    private const double Fps = 30, HeldSeconds = 10;
    private readonly Offscreen _stage = new(Width, Height);
    private readonly Sink _sink = new();
    private readonly Logger _logger;
    private static readonly Vector4 Background = Fixtures.Night.Base;

    public HeldLyricRenderTests() => _logger = new Logger(LogLevel.Warn, _sink);

    public void Dispose() => _stage.Dispose();

    private sealed class Sink : ILogSink
    {
        public List<string> Entries { get; } = [];
        public void Write(in LogEntry entry) => Entries.Add(entry.ToString() ?? "");
    }

    private static readonly string[] Ids =
    [
        "editorial", "spoken", "cascade", "cutout", "scattered", "constellation", "constellationEditorial", "echo",
        "hinge", "broadside", "portal", "mosaic", "torque", "fable",
    ];

    /// <summary>Song times in "Neon Harbour": two in the wrapping line (the M5 report's 0:33.75, and 0:36 from
    /// <c>--player-debug scrub=0.8</c>) and one in the ordinary first line.</summary>
    private static readonly (string Line, double Time)[] Moments = [("wrapping", 33.75), ("wrapping", 36.0), ("ordinary", 11.3)];

    public static TheoryData<string, string, double> Holds()
    {
        var data = new TheoryData<string, string, double>();
        foreach (var id in Ids)
        foreach (var (line, time) in Moments)
            data.Add(id, line, time);
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

    private static SceneSnapshot Demo()
    {
        var song = DemoSongs.Songs[0];
        Assert.Equal("Neon Harbour", song.Title);
        return new SceneSnapshot
        {
            Title = song.Title, Artist = song.Artist, Album = song.Album, Lyrics = song.Lyrics, Duration = song.Duration,
            Palette = Fixtures.Night,
        };
    }

    private static int Lit(byte[] pixels)
    {
        var count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
            if (pixels[i] + pixels[i + 1] + pixels[i + 2] > 3 * 110) count++;
        return count;
    }

    private static int Difference(byte[] a, byte[] b)
    {
        var count = 0;
        for (var i = 0; i < a.Length; i += 4)
            if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2]) count++;
        return count;
    }

    // Played into the moment, then held there (not playing) for ten seconds of wall time: every second's frame is the
    // first held frame, lit, and the frame a fresh layer draws on jumping straight to that time.
    [Theory]
    [MemberData(nameof(Holds))]
    public void AHeldClockKeepsTheLyricOnScreen(string id, string line, double time)
    {
        var scene = Demo();
        using var layer = Layer(id);
        var wall = 100.0;
        for (var t = time - 2; t < time; t += 1 / 60.0, wall += 1 / 60.0) _stage.Render(layer, scene, t, wall, Scale, Background);

        _stage.Render(layer, scene, time, wall, Scale, Background, playing: false);
        var held = _stage.Pixels();
        Assert.True(Lit(held) > 100, $"{id} drew nothing in the {line} line at {time}");
        for (var k = 1; k <= HeldSeconds * Fps; k++)
        {
            wall += 1 / Fps;
            _stage.Render(layer, scene, time, wall, Scale, Background, playing: false);
            if (k % (int)Fps != 0) continue;
            var changed = Difference(held, _stage.Pixels());
            Assert.True(changed == 0, $"{id}, {line} line held at {time}: {changed} pixels changed after {k / Fps:0} s");
        }

        using var fresh = Layer(id);
        _stage.Render(fresh, scene, time, 500, Scale, Background, playing: false);
        Assert.Equal(held, _stage.Pixels());
        Assert.Empty(_sink.Entries);
    }
}
