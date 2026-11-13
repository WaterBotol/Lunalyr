// The line-setup benchmark and frame hashes (CompositionPreview -setup / -hashes): every composition plays the --demo
// songs as the app shows them (opening title, cover, credits, the Japanese song's readings worked out by the Windows
// readings), English, then Japanese, then English again, from each song's start at 60 frames a second, offscreen.
//
// -setup records per frame the render-thread time of the whole pass (each surface's Render and EndDraw) and the part
// spent setting lines up (laying text out and baking it: the layers' SetupMeter), and the pass each new song starts on.
// With -surfaces 2, two layers share one AheadWork per pass, as two monitors do. The first play-through of each
// composition warms the JIT and the device and is not counted; the rest (-runs, 5 by default) are summarised by their
// median, and the worst frame by the worst of all runs.
//
// -hashes FILE writes a SHA-256 of the frame every tenth of a second of the same play-through at 30 frames a second, to
// compare two builds pixel for pixel (same adapter, same size). With the environment variable LUNALYR_KEEP_FRAMES=DIR it
// also keeps one frame a second (raw BGRA); with LUNALYR_AGAINST_FRAMES=DIR it prints how far each of those is from the
// one kept there (how many channel values differ, by how many steps at most). Direct2D is not bit-stable from run to
// run: two runs of one build already differ by a step in a few pixels of some frames, so compare more than one run.
//
// LUNALYR_SETUP_TRACE=1 makes -setup print every pass with more than 4 ms of setup (the warm-up's too, marked).

using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Platform.Readings;
using Lunalyr.Render.Compositions;
using Lunalyr.Render.Pacing;
using Lunalyr.Render.Scene;
using Lunalyr.Shell.Demo;

namespace Lunalyr.Tools.CompositionPreview;

internal static class SetupBench
{
    private sealed record Song(string Name, SceneSnapshot Scene, double Duration);

    /// <summary>Which play-through is running (for the trace).</summary>
    private static string _run = "";

    private sealed record RunStats(double SetupMax, double SetupP50, int SetupFrames, double FrameP50, double FrameP99,
                                   double FrameMax, int Over4, int Over16, double[] NewSongMs, double[] NewSongSetupMs);

    /// <summary>The --demo songs as the app presents them, in the order the benchmark plays them.</summary>
    private static List<Song> Playlist()
    {
        var services = WindowsReadingServices.Create(Path.Combine(Path.GetTempPath(), "lunalyr-previews", "dictionaries"));
        var romanizer = new LyricRomanizer(services);
        var songs = new List<Song>();
        foreach (var demo in DemoSongs.Songs)
        {
            var presented = Lunalyr.Shell.Lyrics.LyricPresentation.Prepare(demo.Lyrics, new LyricQuery(demo.Title, demo.Artist, demo.Album, demo.Duration), true);
            var cover = OpeningFixtures.Cover(demo.Cover);
            var (readings, words) = Lunalyr.Shell.Lyrics.LyricPresentation.SceneReadings(presented, romanizer, RomanizationOptions.Default);
            var titleReading = LyricRomanizer.ContainsNonLatin(demo.Title)
                ? new OpeningTitleMemory().Reading(demo.Title, RomanizationOptions.Default, romanizer) : null;
            var scene = new SceneSnapshot
            {
                Title = demo.Title, Artist = demo.Artist, Album = demo.Album, TrackKey = demo.Title, Duration = demo.Duration,
                Lyrics = presented, Readings = readings, WordReadings = words, Palette = cover.Palette, Cover = cover.Art,
                TitleReading = titleReading,
                Style = LyricStyle.Default with { OpeningTitle = true, Credits = true },
            };
            songs.Add(new Song(demo.Title, scene, demo.Duration));
        }
        return [songs[0], songs[1], songs[0]];
    }

    public static void Run(Offscreen stage, IReadOnlyList<IRenderLayerFactory> factories, string? only, float scale, int runs, int surfaces)
    {
        var playlist = Playlist();
        Console.WriteLine($"Line setup, {stage.Width}x{stage.Height} at {scale}x, {surfaces} surface(s), {runs} runs after a warm-up; "
                          + "milliseconds on the render thread (median of runs; worst = worst of all runs)");
        Console.WriteLine("| Composition | Setup per frame, worst | Setup per frame, median | Frames with setup | Frame p50 | Frame p99 | Frame worst | Frames > 4 ms | Frames > 16.7 ms | New song (frame / setup) |");
        Console.WriteLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var factory in factories)
        {
            if (only is not null && factory.Id != only) continue;
            _run = "warm-up";
            Play(stage, factory, playlist, scale, surfaces, null);
            var results = new List<RunStats>();
            for (var r = 0; r < runs; r++)
            {
                _run = $"run {r + 1}";
                results.Add(Play(stage, factory, playlist, scale, surfaces, null));
            }
            double Median(Func<RunStats, double> pick)
            {
                var values = results.Select(pick).Order().ToArray();
                return values[values.Length / 2];
            }
            var newSongs = string.Join(", ", Enumerable.Range(0, results[0].NewSongMs.Length).Select(i =>
                string.Create(CultureInfo.InvariantCulture, $"{Median(s => s.NewSongMs[i]):0.0} / {Median(s => s.NewSongSetupMs[i]):0.0}")));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"| {factory.Id} | {Median(s => s.SetupMax):0.0} (worst {results.Max(s => s.SetupMax):0.0}) | {Median(s => s.SetupP50):0.00} | {Median(s => s.SetupFrames):0} | "
                + $"{Median(s => s.FrameP50):0.00} | {Median(s => s.FrameP99):0.00} | {Median(s => s.FrameMax):0.0} (worst {results.Max(s => s.FrameMax):0.0}) | "
                + $"{Median(s => s.Over4):0} | {Median(s => s.Over16):0} | {newSongs} |"));
        }
    }

    /// <summary>Writes a hash of the frame every tenth of a second of each composition's play-through.</summary>
    public static void Hashes(Offscreen stage, IReadOnlyList<IRenderLayerFactory> factories, string? only, float scale, string path)
    {
        var playlist = Playlist();
        using var file = new StreamWriter(path);
        foreach (var factory in factories)
        {
            if (only is not null && factory.Id != only) continue;
            Play(stage, factory, playlist, scale, 1, (song, frame, t) =>
            {
                if (frame % 3 != 0) return;
                var pixels = stage.Pixels();
                file.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{factory.Id} {song} {t:0.000} {Convert.ToHexString(SHA256.HashData(pixels))}"));
                // Frames kept (one a second) to compare two runs pixel by pixel; see the header.
                if (frame % 30 != 0) return;
                var name = string.Create(CultureInfo.InvariantCulture, $"{factory.Id}-{song}-{t:00.000}.bgra");
                if (Environment.GetEnvironmentVariable("LUNALYR_KEEP_FRAMES") is { } keep) File.WriteAllBytes(Path.Combine(keep, name), pixels);
                if (Environment.GetEnvironmentVariable("LUNALYR_AGAINST_FRAMES") is { } against && File.Exists(Path.Combine(against, name)))
                {
                    var other = File.ReadAllBytes(Path.Combine(against, name));
                    int count = 0, worst = 0;
                    for (var i = 0; i < pixels.Length; i++)
                    {
                        var d = Math.Abs(pixels[i] - other[i]);
                        if (d == 0) continue;
                        count++;
                        worst = Math.Max(worst, d);
                    }
                    if (count > 0) Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{name}: {count} channel values differ, up to {worst}/255"));
                }
            }, fps: 30);
            Console.WriteLine($"{factory.Id} hashed");
        }
    }

    private static RunStats Play(Offscreen stage, IRenderLayerFactory factory, List<Song> playlist, float scale, int surfaces,
                                 Action<int, int, double>? afterFrame, double fps = 60)
    {
        var layers = new IRenderLayer[surfaces];
        for (var i = 0; i < surfaces; i++)
        {
            layers[i] = factory.Create();
            layers[i].CreateResources(stage.Dc, stage.DWrite, new Vector2(stage.Width, stage.Height), scale);
        }
        var ahead = new AheadWork();
        var background = Fixtures.Night.Base;
        var setups = new List<double>();
        var frames = new List<double>();
        var newSong = new List<double>();
        var newSongSetup = new List<double>();
        var wall = 100.0;
        try
        {
            for (var s = 0; s < playlist.Count; s++)
            {
                var song = playlist[s];
                var count = (int)(song.Duration * fps);
                // A new song's cost: its worst pass in its first 1.5 s (the scene taken in, the first line and the
                // opening title, which arrives at 0.5 s, set).
                double songWorst = 0, songWorstSetup = 0;
                for (var f = 0; f <= count; f++)
                {
                    var t = f / fps;
                    ahead.NewPass();
                    double pass = 0, setup = 0;
                    foreach (var layer in layers)
                    {
                        var start = Stopwatch.GetTimestamp();
                        stage.Render(layer, song.Scene, t, wall + t, scale, background, ahead: ahead);
                        pass += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                        setup += ((LyricCompositionLayer)layer).Meter.TakeMilliseconds();
                    }
                    // Paced as the desktop paces it: the GPU has finished each pass before the next begins.
                    stage.WaitForGpu();
                    frames.Add(pass);
                    if (setup > 0) setups.Add(setup);
                    if (setup > 4 && Environment.GetEnvironmentVariable("LUNALYR_SETUP_TRACE") is not null)
                        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {factory.Id} {_run}, song {s} at {t:0.000} s: setup {setup:0.0} ms, pass {pass:0.0} ms"));
                    if (t < 1.5 && pass > songWorst) { songWorst = pass; songWorstSetup = setup; }
                    afterFrame?.Invoke(s, f, t);
                }
                if (s > 0) { newSong.Add(songWorst); newSongSetup.Add(songWorstSetup); }
                wall += song.Duration + 1;
            }
        }
        finally
        {
            foreach (var layer in layers) layer.Dispose();
        }
        static double Percentile(List<double> values, double p)
        {
            if (values.Count == 0) return 0;
            var sorted = values.Order().ToArray();
            return sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p * sorted.Length) - 1)];
        }
        return new RunStats(setups.Count == 0 ? 0 : setups.Max(), Percentile(setups, 0.5), setups.Count, Percentile(frames, 0.5),
                            Percentile(frames, 0.99), frames.Max(), frames.Count(ms => ms > 4), frames.Count(ms => ms > 1000 / 60.0),
                            [.. newSong], [.. newSongSetup]);
    }
}
