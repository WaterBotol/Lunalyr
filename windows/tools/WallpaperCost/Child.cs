using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Compositions;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.NowPlaying;
using Lunalyr.Core.Playback;
using Lunalyr.Platform.NowPlaying;
using Lunalyr.Render.Backgrounds;
using Lunalyr.Render.Pacing;
using Lunalyr.Render.Scene;
using Lunalyr.Shell;
using Lunalyr.Shell.Demo;
using Lunalyr.Shell.Rendering;
using Lunalyr.Shell.Settings;
using Lunalyr.Shell.Visualisers;
using Vortice.Mathematics;

namespace Lunalyr.Tools.WallpaperCost;

/// <summary>
/// One measured run, in its own process: the app's controller following the silent demo player (playing) or no player
/// (idle), its scene, clock and background choice drawn by the app's layers into one offscreen surface the way the render
/// engine draws a wallpaper surface, paced at 60 fps. Talks to the parent on stdout ("READY", then "RESULT {json}") and
/// waits for a line on stdin before it tears down, so the parent can read its memory while everything is still alive.
/// </summary>
internal static partial class Child
{
    private const double Period = 1.0 / 60, IdlePollSeconds = 0.25;

    public static async Task<int> RunAsync(Mode mode, bool playing, Options o)
    {
        var logger = new Logger(LogLevel.Info, new StderrSink());
        var baseline = ReferenceEquals(mode, Mode.Baseline) || mode.Id == Mode.Baseline.Id;
        var settings = mode.Configure(new AppSettings { LyricComposition = o.Composition });
        var factories = new Dictionary<string, IRenderLayerFactory>(StringComparer.Ordinal);
        foreach (var f in WallpaperLayers.Factories(ReadingServices.Basic.WordBreaker, logger)) factories[f.Id] = f;

        using IMediaSessionSource source = playing ? new DemoMediaSessionSource(new ShiftedTime(o.Start)) : new NoPlayer();
        var renderer = new CaptureRenderer(factories.Keys);
        await using var controller = new AppController(source, new DemoLyricLookup(), renderer, settings, new SongOffsets(),
            ReadingServices.Basic, new PlaybackGuard(), logger);

        // The visualiser's sound, as the app runs it under --demo: the demo's synthetic signal through the capture path.
        // Nothing is captured or played.
        SystemAudioService? audio = null;
        if (settings.BackgroundMode == "visualizer")
        {
            VisualiserServices.UseSettings(() => settings);
            audio = new SystemAudioService(() => settings, () =>
            {
                var status = controller.Status;
                return (status.Playback == PlaybackStatus.Playing, status.Player);
            }, demo: true, logger);
            audio.RenderStatusChanged([new Lunalyr.Render.SurfaceStatus(0, "offscreen", o.Width, o.Height, o.Scale, 60,
                Lunalyr.Platform.Desktop.AttachMode.Progman24H2, "offscreen", true, false, SurfaceState.Drawing, PauseReason.None,
                FrameRateCap.Fps60, 1, 0, false)], null);
            audio.Start();
        }
        controller.Start();

        var clock = Stopwatch.StartNew();
        double Now() => clock.Elapsed.TotalSeconds;
        // The playing scene is ready once the demo song's lyrics and cover are in (the lookup and palette are quick).
        if (playing)
        {
            while (Now() < 15 && !(renderer.Scene?.Lyrics is LyricResult.Synced && renderer.Scene.Cover is not null))
                await Task.Delay(20).ConfigureAwait(false);
        }
        var notes = new List<string>();
        if (playing && renderer.Scene?.Lyrics is not LyricResult.Synced) notes.Add("the demo's lyrics never arrived");

        try
        {
            if (baseline) return await BaselineAsync(controller, o, playing, notes).ConfigureAwait(false);
            using var gpu = Gpu.Create(o.Adapter, o.Width, o.Height, o.Scale);
            return Draw(mode, playing, o, gpu, controller, renderer, factories, settings, logger, notes, clock);
        }
        finally
        {
            audio?.Dispose();
        }
    }

    private static async Task<int> BaselineAsync(AppController controller, Options o, bool playing, List<string> notes)
    {
        await Task.Delay(TimeSpan.FromSeconds(Warmup(o, playing))).ConfigureAwait(false);
        var start = Snapshot.Take();
        Console.WriteLine("READY -");
        await Task.Delay(TimeSpan.FromSeconds(o.Seconds)).ConfigureAwait(false);
        var end = Snapshot.Take();
        var song = controller.ReadClock();
        var result = new ChildResult
        {
            Mode = Mode.Baseline.Id, State = playing ? "playing" : "idle", Layer = "-", Composition = "-", Adapter = "-",
            Seconds = end.Wall - start.Wall, Notes = notes,
            AllocatedBytesPerSecond = (end.Allocated - start.Allocated) / Math.Max(1e-9, end.Wall - start.Wall),
            Gc0 = end.Gc0 - start.Gc0, Gc1 = end.Gc1 - start.Gc1, Gc2 = end.Gc2 - start.Gc2,
            SongEnd = double.IsFinite(song.Seconds) ? song.Seconds : null,
        };
        Finish(result);
        return 0;
    }

    private static double Warmup(Options o, bool playing) => o.Warmup ?? (playing ? 3 : 6);

    private static int Draw(Mode mode, bool playing, Options o, Gpu gpu, AppController controller, CaptureRenderer renderer,
        Dictionary<string, IRenderLayerFactory> factories, AppSettings settings, Logger logger, List<string> notes, Stopwatch clock)
    {
        double Now() => clock.Elapsed.TotalSeconds;
        var (clip, picture) = (Path.Combine(o.Media, Media.ClipName), Path.Combine(o.Media, Media.PictureName));
        // The file-backed layers on the tool's own test media (never through the app's wallpaper store).
        var follows = mode.Source == LayerSource.Controller;
        string? fixedLayer = null;
        switch (mode.Source)
        {
            case LayerSource.Picture:
                factories["custom"] = new CustomLayerFactory(picture, WallpaperPlacement.Fill, log: logger);
                fixedLayer = "custom";
                break;
            case LayerSource.Video:
                factories["custom"] = new CustomLayerFactory(clip, WallpaperPlacement.Fill, log: logger);
                fixedLayer = "custom";
                break;
            case LayerSource.Motion when playing:
                factories[MotionCoverLayerFactory.LayerId] = new MotionCoverLayerFactory(clip, null, WallpaperPlacement.Fill, log: logger);
                fixedLayer = MotionCoverLayerFactory.LayerId;
                break;
            case LayerSource.MusicVideo when playing:
                var feed = new Feed(() =>
                {
                    var (seconds, moving) = controller.ReadClock();
                    return new MusicVideoInputs(clip, seconds, moving, 0, settings.MusicVideoDim, settings.MusicVideoBlur);
                });
                factories[MusicVideoLayerFactory.LayerId] = new MusicVideoLayerFactory(feed, log: logger);
                fixedLayer = MusicVideoLayerFactory.LayerId;
                break;
            default:
                follows = true;
                break;
        }

        string Wanted()
        {
            var id = fixedLayer ?? EngineWallpaperRenderer.BackgroundLayerFor(renderer.Background ?? controller.BackgroundLayer);
            return factories.ContainsKey(id) ? id : AppController.FallbackBackground;
        }

        var composition = factories.ContainsKey(o.Composition) ? o.Composition : "editorial";
        var backgroundId = Wanted();
        if (backgroundId != (fixedLayer ?? EngineWallpaperRenderer.BackgroundLayerFor(renderer.Background ?? controller.BackgroundLayer)))
            notes.Add($"layer {renderer.Background} isn't registered; drew {backgroundId}");
        var background = factories[backgroundId].Create();
        var lyric = factories[composition].Create();
        gpu.Prepare(background);
        gpu.Prepare(lyric);
        var layers = new List<string> { backgroundId };

        var ahead = new AheadWork();
        var joins = new OpeningTitleMemory();
        var pacer = new Pacer();
        long backgroundBytes = 0, lyricBytes = 0;
        var inWindow = false;
        var errors = 0;
        SceneSnapshot? lastScene = null;
        var lastWall = double.NaN;

        void Frame(double now, double song, bool moving, SceneSnapshot scene)
        {
            var dc = gpu.Dc;
            ahead.NewPass();
            var frame = new FrameContext
            {
                Dc = dc, DWrite = gpu.DWrite, D2DFactory = gpu.Factory, Size = gpu.Size, Scale = gpu.Scale, WallSeconds = now,
                SongSeconds = song, IsPlaying = moving, Scene = scene,
                DeltaSeconds = double.IsNaN(lastWall) ? 0 : Math.Clamp(now - lastWall, 0, 0.25),
                AheadWork = ahead, SongJoined = joins.Joined(scene.TrackKey, song), IsMainDisplay = true,
            };
            gpu.BeginFrame();
            dc.BeginDraw();
            dc.Transform = Matrix3x2.Identity;
            dc.Clear(background is ISeeThroughLayer ? new Color4(0, 0, 0, 0) : new Color4(0, 0, 0, 1));
            var a0 = GC.GetAllocatedBytesForCurrentThread();
            Render(background, frame);
            var a1 = GC.GetAllocatedBytesForCurrentThread();
            Render(lyric, frame);
            if (inWindow)
            {
                backgroundBytes += a1 - a0;
                lyricBytes += GC.GetAllocatedBytesForCurrentThread() - a1;
            }
            var result = dc.EndDraw();
            if (result.Failure) errors++;
            gpu.EndFrame();
            lastWall = now;
        }

        void Render(Lunalyr.Render.Scene.IRenderLayer layer, in FrameContext frame)
        {
            frame.Dc.Transform = Matrix3x2.Identity;
            try
            {
                layer.Render(in frame);
            }
            catch (Exception ex)
            {
                if (errors++ == 0) Console.Error.WriteLine($"LOG\tERROR\ttool\tA layer failed to render: {ex.Message}");
            }
        }

        bool Idle() => FrameClockPolicy.IsIdle(FrameClockPolicy.QuietAfter(background.IsAnimating, lyric.IsAnimating, null), Now());

        // What the measured window records (Step fills it once measuring).
        var capacity = (int)(o.Seconds * 60 * 1.2) + 256;
        var cpu = new double[capacity];
        var gpuMs = new List<double>(capacity);
        var alloc = new long[capacity];

        var frames = 0;
        var late = 0;
        var idlePolls = 0;
        var previousFrameAt = double.NaN;
        var measuredPrevious = false;

        // ---- Warm-up: fades in, videos decoding, the idle notice gone; then one snapshot ----
        var warmEnd = Now() + Warmup(o, playing);
        var firstFrameBy = warmEnd + 6;
        while (true)
        {
            var now = Now();
            var ready = background is not IFirstFrameLayer first || first.HasFirstFrame;
            if (now >= warmEnd && (ready || now >= firstFrameBy))
            {
                if (!ready) notes.Add("the background had no first frame after warm-up");
                break;
            }
            Step(measuring: false, until: warmEnd);
        }
        SnapshotResult? snapshot = null;
        if (o.Snapshots is not null)
        {
            // A frame drawn now (even if idle) so the picture is what the wallpaper shows.
            var (song, moving) = controller.ReadClock();
            gpu.FinishPrevious();
            Frame(Now(), song, moving, renderer.Scene ?? new SceneSnapshot());
            gpu.FinishPrevious();
            var pixels = gpu.ReadPixels();
            Directory.CreateDirectory(o.Snapshots);
            var path = Path.Combine(o.Snapshots, $"{mode.Id}-{(playing ? "playing" : "idle")}.png");
            Gpu.SavePng(path, pixels, gpu.Width, gpu.Height);
            snapshot = new SnapshotResult { Path = path, MeanLuminance = Math.Round(MeanLuminance(pixels), 4), Opaque = Opaque(pixels) };
        }

        // ---- Measured window ----
        var songStart = controller.ReadClock().Seconds;
        // The snapshot's readback and PNG took a while: the 60 fps grid starts again here, so they don't count as missed slots.
        pacer.Reset(Now());
        var missedAtStart = pacer.Missed;
        var start = Snapshot.Take();
        Console.WriteLine($"READY {gpu.CounterLuid}");
        inWindow = true;
        var end = Now() + o.Seconds;
        while (Now() < end) Step(measuring: true, until: end);
        var final = gpu.FinishPrevious();
        if (measuredPrevious && double.IsFinite(final)) gpuMs.Add(final);
        var stop = Snapshot.Take();
        var songEnd = controller.ReadClock().Seconds;

        // ---- Result ----
        var measuredCpu = cpu.AsSpan(0, Math.Min(frames, capacity)).ToArray();
        Array.Sort(measuredCpu);
        var sortedGpu = gpuMs.Order().ToArray();
        var seconds = stop.Wall - start.Wall;
        var result = new ChildResult
        {
            Mode = mode.Id, State = playing ? "playing" : "idle", Layer = string.Join(" â†’ ", layers), Composition = composition,
            Adapter = gpu.AdapterName, AdapterLuid = gpu.CounterLuid, VideoSupport = gpu.VideoSupport, FrameLatencyFence = gpu.HasFence,
            Width = gpu.Width, Height = gpu.Height, Scale = gpu.Scale, Seconds = seconds, Frames = frames,
            Fps = frames / Math.Max(1e-9, seconds), IdlePolls = idlePolls, MissedSlots = pacer.Missed - missedAtStart, LateFrames = late,
            CpuMsMean = Mean(measuredCpu), CpuMsP50 = Pct(measuredCpu, 0.5), CpuMsP95 = Pct(measuredCpu, 0.95), CpuMsMax = measuredCpu.Length > 0 ? measuredCpu[^1] : null,
            GpuMsMean = Mean(sortedGpu), GpuMsP95 = Pct(sortedGpu, 0.95), GpuSamples = sortedGpu.Length,
            RenderThreadBytesPerFrame = frames == 0 ? null : alloc.AsSpan(0, Math.Min(frames, capacity)).ToArray().Average(),
            BackgroundBytesPerFrame = frames == 0 ? null : backgroundBytes / (double)frames,
            LyricsBytesPerFrame = frames == 0 ? null : lyricBytes / (double)frames,
            AllocatedBytesPerSecond = (stop.Allocated - start.Allocated) / Math.Max(1e-9, seconds),
            Gc0 = stop.Gc0 - start.Gc0, Gc1 = stop.Gc1 - start.Gc1, Gc2 = stop.Gc2 - start.Gc2,
            LayerErrors = errors, Snapshot = snapshot, Notes = notes,
            SongStart = double.IsFinite(songStart) ? songStart : null, SongEnd = double.IsFinite(songEnd) ? songEnd : null,
        };
        Finish(result);
        background.Dispose();
        lyric.Dispose();
        return 0;

        // One turn of the engine's loop: an idle surface waits (the engine polls every 0.25 s), a drawing one waits for
        // its 60 fps slot and the previous frame's GPU work, then draws.
        void Step(bool measuring, double until)
        {
            // The controller moved the background on (Match the music for the next song): swap, as the engine would
            // (without its crossfade).
            if (follows && fixedLayer is null && Wanted() is var wanted && wanted != backgroundId)
            {
                gpu.FinishPrevious();
                background.Dispose();
                backgroundId = wanted;
                background = factories[wanted].Create();
                gpu.Prepare(background);
                layers.Add(wanted);
                lastScene = null;
            }
            var scene = renderer.Scene ?? new SceneSnapshot();
            var changed = !ReferenceEquals(scene, lastScene);
            if (!changed && Idle())
            {
                if (measuring) idlePolls++;
                pacer.Sleep(Math.Min(IdlePollSeconds, Math.Max(0, until - Now())));
                pacer.Reset(Now());
                return;
            }
            pacer.WaitForSlot(Now);
            var previous = gpu.FinishPrevious();
            if (measuring && measuredPrevious && double.IsFinite(previous)) gpuMs.Add(previous);
            var now = Now();
            var (song, moving) = controller.ReadClock();
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var t0 = Stopwatch.GetTimestamp();
            Frame(now, song, moving, scene);
            var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
            lastScene = scene;
            measuredPrevious = measuring;
            if (!measuring) return;
            if (frames < capacity)
            {
                cpu[frames] = ms;
                alloc[frames] = bytes;
            }
            if (double.IsFinite(previousFrameAt) && now - previousFrameAt > 1.5 * Period) late++;
            previousFrameAt = now;
            frames++;
        }
    }

    private static void Finish(ChildResult result)
    {
        Console.WriteLine("RESULT " + System.Text.Json.JsonSerializer.Serialize(result, CostJson.Default.ChildResult));
        Console.Out.Flush();
        // The parent reads this process's memory now, then says to go.
        Console.In.ReadLine();
    }

    private static double? Mean(double[] sorted) => sorted.Length == 0 ? null : sorted.Average();

    private static double? Pct(double[] sorted, double q) => sorted.Length == 0 ? null : sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))];

    private static double MeanLuminance(byte[] bgra)
    {
        double sum = 0;
        for (var i = 0; i < bgra.Length; i += 4) sum += 0.0722 * bgra[i] + 0.7152 * bgra[i + 1] + 0.2126 * bgra[i + 2];
        return sum / (bgra.Length / 4) / 255;
    }

    private static bool Opaque(byte[] bgra)
    {
        for (var i = 3; i < bgra.Length; i += 4 * 97)
            if (bgra[i] != 255) return false;
        return true;
    }

    /// <summary>Process-wide counters at one moment.</summary>
    private readonly record struct Snapshot(double Wall, long Allocated, int Gc0, int Gc1, int Gc2)
    {
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        public static Snapshot Take() => new(Clock.Elapsed.TotalSeconds, GC.GetTotalAllocatedBytes(false), GC.CollectionCount(0),
            GC.CollectionCount(1), GC.CollectionCount(2));
    }

    /// <summary>
    /// 60 fps slots on a high-resolution waitable timer (as the engine waits for gated frames), standing in for vsync:
    /// a frame that misses its slot waits for the next one, as Present with a sync interval of 1 would.
    /// </summary>
    private sealed partial class Pacer
    {
        private readonly nint _timer = CreateWaitableTimerExW(0, 0, 0x2 /* HIGH_RESOLUTION */, 0x1F0003 /* TIMER_ALL_ACCESS */);
        private double _next = double.NaN;

        public long Missed { get; private set; }

        public void Reset(double now) => _next = now;

        public void WaitForSlot(Func<double> now)
        {
            var t = now();
            if (double.IsNaN(_next)) _next = t;
            if (t < _next)
            {
                Sleep(_next - t);
            }
            else if (t - _next >= Period)
            {
                // A whole slot or more went by (a slow frame, or the process was held up): the grid starts again from
                // now, as the latency waitable is already signalled and the frame is drawn at once.
                Missed += (long)Math.Floor((t - _next) / Period);
                _next = t;
            }
            _next += Period;
        }

        public unsafe void Sleep(double seconds)
        {
            if (seconds <= 0) return;
            long due = -(long)(seconds * 10_000_000);
            if (_timer != 0 && SetWaitableTimer(_timer, &due, 0, 0, 0, false)) Gpu.WaitForSingleObject(_timer, 2000);
            else Thread.Sleep(TimeSpan.FromSeconds(seconds));
        }

        [LibraryImport("kernel32.dll")]
        private static partial nint CreateWaitableTimerExW(nint attributes, nint name, uint flags, uint access);

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static unsafe partial bool SetWaitableTimer(nint timer, long* due, int period, nint completion, nint arg,
            [MarshalAs(UnmanagedType.Bool)] bool resume);
    }
}
