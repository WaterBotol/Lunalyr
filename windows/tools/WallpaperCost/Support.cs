using System.Text.Json.Serialization;
using Lunalyr.Core.Logging;
using Lunalyr.Platform.Desktop;
using Lunalyr.Platform.NowPlaying;
using Lunalyr.Render.Backgrounds;
using Lunalyr.Render.Scene;
using Lunalyr.Shell.Rendering;

namespace Lunalyr.Tools.WallpaperCost;

/// <summary>The command line, shared by the parent and its child runs.</summary>
internal sealed record Options
{
    public double Seconds { get; init; } = 15;
    public int Width { get; init; } = 2560;
    public int Height { get; init; } = 1440;
    public float Scale { get; init; } = 1.25f;
    public string? Adapter { get; init; }
    public string[]? Only { get; init; }
    public string[] States { get; init; } = ["playing", "idle"];
    public string? Out { get; init; }
    public string Composition { get; init; } = "editorial";
    public string? Snapshots { get; init; }
    public string Media { get; init; } = WallpaperCost.Media.DefaultFolder();
    /// <summary>The demo song's time when the controller starts following it (the measured stretch starts about 3 s later).</summary>
    public double Start { get; init; } = 8;
    /// <summary>Seconds drawn before measuring; default 3 playing, 6 idle (the idle notice fades out in 5).</summary>
    public double? Warmup { get; init; }
    public bool Baseline { get; init; } = true;

    /// <summary>The options a child run needs, as arguments.</summary>
    public IEnumerable<string> ChildArguments()
    {
        string F(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        yield return "-seconds"; yield return F(Seconds);
        yield return "-size"; yield return $"{Width}x{Height}";
        yield return "-scale"; yield return F(Scale);
        if (Adapter is not null) { yield return "-adapter"; yield return Adapter; }
        yield return "-composition"; yield return Composition;
        if (Snapshots is not null) { yield return "-snapshots"; yield return Snapshots; }
        yield return "-media"; yield return Media;
        yield return "-start"; yield return F(Start);
        if (Warmup is { } w) { yield return "-warmup"; yield return F(w); }
    }

    public static Options Parse(IReadOnlyList<string> args, int from, out string? error)
    {
        var o = new Options();
        error = null;
        var c = System.Globalization.CultureInfo.InvariantCulture;
        for (var i = from; i < args.Count; i++)
        {
            var name = args[i].ToLowerInvariant();
            if (name == "-no-baseline")
            {
                o = o with { Baseline = false };
                continue;
            }
            if (i + 1 >= args.Count)
            {
                error = $"{args[i]} needs a value.";
                return o;
            }
            var value = args[++i];
            switch (name)
            {
                case "-seconds": o = o with { Seconds = double.Parse(value, c) }; break;
                case "-size":
                    var parts = value.Split('x');
                    o = o with { Width = int.Parse(parts[0], c), Height = int.Parse(parts[1], c) };
                    break;
                case "-scale": o = o with { Scale = float.Parse(value, c) }; break;
                case "-adapter": o = o with { Adapter = value }; break;
                case "-only": o = o with { Only = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) }; break;
                case "-states": o = o with { States = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) }; break;
                case "-out": o = o with { Out = Path.GetFullPath(value) }; break;
                case "-composition": o = o with { Composition = value }; break;
                case "-snapshots": o = o with { Snapshots = Path.GetFullPath(value) }; break;
                case "-media": o = o with { Media = Path.GetFullPath(value) }; break;
                case "-start": o = o with { Start = double.Parse(value, c) }; break;
                case "-warmup": o = o with { Warmup = double.Parse(value, c) }; break;
                default:
                    error = $"Unknown option {args[i - 1]}.";
                    return o;
            }
        }
        if (o.States.Any(s => s is not ("playing" or "idle"))) error = "-states takes playing and/or idle.";
        return o;
    }
}

/// <summary>What the desktop renderer is told, kept for the tool's draw loop to read (the scene, the layers, the clock).</summary>
internal sealed class CaptureRenderer(IEnumerable<string> layerIds) : IWallpaperRenderer
{
    private readonly string[] _ids = [.. layerIds];
    private volatile SceneSnapshot? _scene;
    private volatile string? _background;
    private volatile Func<(double Seconds, bool Playing)>? _clock;

    public SceneSnapshot? Scene => _scene;

    public string? Background => _background;

    public Func<(double Seconds, bool Playing)>? Clock => _clock;

    public IReadOnlyCollection<string> LayerIds => _ids;

    public void Start() { }

    public void Stop() { }

    public void SetScene(SceneSnapshot scene) => _scene = scene;

    public void SetLayers(string background, string composition) => _background = background;

    public void SetClock(Func<(double Seconds, bool Playing)> clock) => _clock = clock;

    public void SetFrameRate(int framesPerSecond) { }

    public void ReplaceLayer(IRenderLayerFactory factory) { }

    public void SetFloating(FloatingLyricsOptions options) { }

    public void Dispose() { }
}

/// <summary>
/// The system clock, except that its first reading (the demo player's start) is <paramref name="seconds"/> earlier, so the
/// demo starts part-way into its first song and runs in real time from there.
/// </summary>
internal sealed class ShiftedTime(double seconds) : TimeProvider
{
    private int _timers, _readings;

    public override DateTimeOffset GetUtcNow() =>
        Interlocked.Increment(ref _readings) == 1 ? base.GetUtcNow() - TimeSpan.FromSeconds(seconds) : base.GetUtcNow();

    // The demo's first timer is its first song's end: due that much sooner, so the song still moves on at its end.
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        base.CreateTimer(callback, state, Interlocked.Increment(ref _timers) == 1 && dueTime > TimeSpan.FromSeconds(seconds)
            ? dueTime - TimeSpan.FromSeconds(seconds) : dueTime, period);
}

/// <summary>No player at all: the app's wallpaper with nothing playing.</summary>
internal sealed class NoPlayer : IMediaSessionSource
{
    public IReadOnlyList<IMediaSession> Sessions => [];

    public IMediaSession? CurrentSession => null;

    public event EventHandler? SessionsChanged { add { } remove { } }

    public event EventHandler? CurrentSessionChanged { add { } remove { } }

    public void Dispose() { }
}

/// <summary>Log entries to stderr, one a line, for the parent to keep with the run.</summary>
internal sealed class StderrSink : ILogSink
{
    public void Write(in LogEntry entry)
    {
        var text = entry.Message.Replace('\n', ' ');
        Console.Error.WriteLine($"LOG\t{entry.Level.ToString().ToUpperInvariant()}\t{entry.Category}\t{text}"
            + (entry.Exception is null ? "" : " | " + entry.Exception.Split('\n')[0]));
    }
}

/// <summary>The music video layer's inputs: the test clip at the song's time.</summary>
internal sealed class Feed(Func<MusicVideoInputs> read) : IMusicVideoFeed
{
    public MusicVideoInputs Read() => read();

    public void Report(string path, double? duration, double videoTime, bool playing) { }

    public void Failed(string path, string reason) => Console.Error.WriteLine($"LOG\tERROR\ttool\tMusic video failed: {reason}");
}

/// <summary>A snapshot written after warm-up.</summary>
internal sealed record SnapshotResult
{
    public string Path { get; init; } = "";
    public double MeanLuminance { get; init; }
    public bool Opaque { get; init; }
}

/// <summary>What a child run measured itself (the parent adds CPU, memory and GPU engine use).</summary>
internal sealed record ChildResult
{
    public string Mode { get; init; } = "";
    public string State { get; init; } = "";
    public string Layer { get; init; } = "";
    public string Composition { get; init; } = "";
    public string Adapter { get; init; } = "";
    public string? AdapterLuid { get; init; }
    public bool VideoSupport { get; init; }
    public bool FrameLatencyFence { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public float Scale { get; init; }
    public double Seconds { get; init; }
    public int Frames { get; init; }
    public double Fps { get; init; }
    public int IdlePolls { get; init; }
    public long MissedSlots { get; init; }
    public int LateFrames { get; init; }
    public double? CpuMsMean { get; init; }
    public double? CpuMsP50 { get; init; }
    public double? CpuMsP95 { get; init; }
    public double? CpuMsMax { get; init; }
    public double? GpuMsMean { get; init; }
    public double? GpuMsP95 { get; init; }
    public int GpuSamples { get; init; }
    public double? RenderThreadBytesPerFrame { get; init; }
    public double? BackgroundBytesPerFrame { get; init; }
    public double? LyricsBytesPerFrame { get; init; }
    /// <summary>How many measured frames allocated anything on the render thread, and the most one did.</summary>
    public int AllocatingFrames { get; init; }
    public long MaxBytesInAFrame { get; init; }
    public long MedianBytesInAFrame { get; init; }
    public double AllocatedBytesPerSecond { get; init; }
    public int Gc0 { get; init; }
    public int Gc1 { get; init; }
    public int Gc2 { get; init; }
    public int LayerErrors { get; init; }
    public SnapshotResult? Snapshot { get; init; }
    public List<string> Notes { get; init; } = [];
    public double? SongStart { get; init; }
    public double? SongEnd { get; init; }
}

/// <summary>One row of the report: the child's own numbers and what the parent sampled from outside it.</summary>
internal sealed record RunResult
{
    public string Mode { get; init; } = "";
    public string Title { get; init; } = "";
    public string State { get; init; } = "";
    public bool Ran { get; init; }
    public string? Skipped { get; init; }
    public ChildResult? Child { get; init; }
    public double? CpuPercent { get; init; }
    public double? Gpu3DPercent { get; init; }
    public double? GpuVideoDecodePercent { get; init; }
    public double? GpuVideoProcessingPercent { get; init; }
    public double? GpuOtherPercent { get; init; }
    public int GpuCounterSamples { get; init; }
    public double? GpuDedicatedMb { get; init; }
    public double? GpuSharedMb { get; init; }
    public double? PrivateMb { get; init; }
    public double? WorkingSetMb { get; init; }
    public double? CpuPercentOverBaseline { get; init; }
    public double? PrivateMbOverBaseline { get; init; }
    public string? DecodedOn { get; init; }
    public int Warnings { get; init; }
    public List<string> Log { get; init; } = [];
}

/// <summary>The whole report as JSON.</summary>
internal sealed record Report
{
    public string Tool { get; init; } = "WallpaperCost";
    public DateTimeOffset When { get; init; }
    public string Machine { get; init; } = "";
    public string Adapter { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }
    public float Scale { get; init; }
    public double SecondsPerRun { get; init; }
    public string Composition { get; init; } = "";
    public string Clip { get; init; } = "";
    public string Picture { get; init; } = "";
    public List<string> Caveats { get; init; } = [];
    public List<RunResult> Runs { get; init; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals)]
[JsonSerializable(typeof(ChildResult))]
[JsonSerializable(typeof(Report))]
internal sealed partial class CostJson : JsonSerializerContext;
