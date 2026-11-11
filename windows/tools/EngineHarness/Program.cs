// Manual check of the render engine. Usage:
//   EngineHarness --seconds N [--windowed] [--fps display|60|30|15] [--snapshot out.png [--snapshot-at S]]
//                 [--stats out.json] [--pause-covered] [--low-power] [--no-playback] [--log]
//                 [--rebuild-at S] [--device-loss-at S] [--crossfade-at S] [--pause-at S] [--resume-at S]
// Shows a solid colour and the debug text layer on every monitor behind the desktop icons (or in one window with
// --windowed) for N seconds, then prints frame statistics. --seconds is required so nothing is left behind the icons.
// It plays nothing, reads no media session and changes no setting: the song clock is a made-up timer.
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Lunalyr.Core.Logging;
using Lunalyr.Render;
using Lunalyr.Render.Layers;
using Lunalyr.Render.Pacing;
using Lunalyr.Render.Scene;

var launch = Stopwatch.StartNew();
Console.OutputEncoding = Encoding.UTF8;

string? Arg(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
double? Seconds(string name) => double.TryParse(Arg(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
bool Flag(string name) => args.Contains(name);

if (Seconds("--seconds") is not { } runSeconds || runSeconds <= 0)
{
    Console.Error.WriteLine("Usage: EngineHarness --seconds N [--windowed] [--fps display|60|30|15] [--snapshot out.png] [--stats out.json] ...");
    return 2;
}
var fps = (Arg("--fps") ?? "display") switch
{
    "15" => FrameRateCap.Fps15,
    "30" => FrameRateCap.Fps30,
    "60" => FrameRateCap.Fps60,
    _ => FrameRateCap.Display,
};
string? snapshotPath = Arg("--snapshot");
double snapshotAt = Seconds("--snapshot-at") ?? 2;
string? statsPath = Arg("--stats");
var notes = new List<string>();

var logger = new Logger(Flag("--log") ? LogLevel.Debug : LogLevel.Info, new ConsoleSink(notes, Flag("--log")));
var engine = new RenderEngine(new RenderEngineOptions
{
    Windowed = Flag("--windowed"),
    BackgroundId = "solid",
    LyricId = "debug",
    StatsLogInterval = TimeSpan.FromSeconds(10),
    Performance = PerformanceOptions.Default with
    {
        FrameRate = fps,
        // Off by default here: with the harness's own terminal covering the desktop, the engine would pause and the
        // numbers would describe a paused wallpaper.
        PauseWhenCovered = Flag("--pause-covered"),
        LowPowerVisuals = Flag("--low-power"),
    },
}, logger);
engine.RegisterLayer(new SolidColorLayerFactory("solid-b", new Vector4(0.10f, 0.35f, 0.30f, 1)));
engine.SetScene(new SceneSnapshot { Title = "Lunalyr engine harness", Artist = "solid + debug layers" });
if (!Flag("--no-playback"))
{
    var song = Stopwatch.StartNew();
    engine.SongClock = () => (song.Elapsed.TotalSeconds, true);
}
var quit = new ManualResetEventSlim();
engine.CloseRequested += (_, _) => quit.Set();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.Set(); };

engine.Start();
double startupMs = launch.Elapsed.TotalMilliseconds;
Console.WriteLine($"Started in {startupMs:0} ms; attach: {engine.AttachMode}");
foreach (var s in engine.Surfaces)
    Console.WriteLine($"  surface {s.Index}: {s.Monitor} {s.Width}x{s.Height} @{s.Scale * 100:0}% {s.RefreshHz} Hz on {s.Adapter}"
        + $"{(s.AdapterDrivesMonitor ? "" : " (fallback)")}{(s.Hdr ? " HDR" : "")}, {s.FrameRate} -> sync {s.SyncInterval}");

// Timed actions, each once.
var actions = new List<(double At, string Name, Action Run)>();
void At(double? at, string name, Action run) { if (at is { } t) actions.Add((t, name, run)); }
At(1, "reset stats", engine.ResetStats); // steady state: drop start-up
At(snapshotPath is null ? null : snapshotAt, "snapshot", () =>
{
    try
    {
        var png = engine.CaptureAsync(0).GetAwaiter().GetResult();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(snapshotPath!))!);
        File.WriteAllBytes(snapshotPath!, png);
        Console.WriteLine($"Snapshot of surface 0: {snapshotPath} ({png.Length / 1024} KB)");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Snapshot failed: {ex.Message}");
        notes.Add("snapshot failed: " + ex.Message);
    }
});
At(Seconds("--rebuild-at"), "rebuild", engine.RequestRebuild);
At(Seconds("--device-loss-at"), "device loss", engine.SimulateDeviceLoss);
At(Seconds("--crossfade-at"), "crossfade", () => engine.SetLayers("solid-b", "debug"));
At(Seconds("--pause-at"), "pause wallpaper", () => engine.SetPerformance(engine.Performance with { PauseWallpaper = true }));
At(Seconds("--resume-at"), "resume wallpaper", () => engine.SetPerformance(engine.Performance with { PauseWallpaper = false }));
actions.Sort((a, b) => a.At.CompareTo(b.At));

var run = Stopwatch.StartNew();
int next = 0;
while (run.Elapsed.TotalSeconds < runSeconds && !quit.IsSet)
{
    while (next < actions.Count && run.Elapsed.TotalSeconds >= actions[next].At)
    {
        Console.WriteLine($"[{run.Elapsed.TotalSeconds:0.0}s] {actions[next].Name}");
        actions[next++].Run();
    }
    quit.Wait(20);
}

var stats = engine.GetStats();
string tree = engine.AttachTree;
var process = Process.GetCurrentProcess();
process.Refresh();
double cpuSeconds = process.TotalProcessorTime.TotalSeconds;
engine.Stop();

Console.WriteLine($"{stats.Fps} fps over {stats.Seconds} s ({stats.Frames} frames); frame cpu avg {stats.FrameCpuMs.Avg} / p95 {stats.FrameCpuMs.P95} ms; "
    + $"interval p50 {stats.FrameIntervalMs.P50} / p99 {stats.FrameIntervalMs.P99} ms; slow {stats.SlowFrames}; rebuilds {stats.Rebuilds}; "
    + $"device recoveries {stats.DeviceRecoveries}; working set {process.WorkingSet64 / 1048576.0:0.0} MB");
foreach (var s in stats.Surfaces) Console.WriteLine($"  surface {s.Index}: {s.State} {s.Reason}{(s.Idle ? " (idle)" : "")}");

if (statsPath is not null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(statsPath))!);
    using var file = File.Create(statsPath);
    using var w = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    w.WriteStartObject();
    w.WriteString("stack", "lunalyr-render-engine-aot");
    w.WriteString("adapter", string.Join(", ", stats.Surfaces.Select(s => s.Adapter).Distinct()));
    w.WriteStartArray("monitors");
    foreach (var s in stats.Surfaces)
    {
        w.WriteStartObject();
        w.WriteNumber("w", s.Width); w.WriteNumber("h", s.Height); w.WriteNumber("dpi", Math.Round(s.Scale * 96));
        w.WriteNumber("hz", s.RefreshHz); w.WriteString("mode", s.Mode.ToString()); w.WriteString("state", s.State.ToString());
        w.WriteString("reason", s.Reason.ToString()); w.WriteNumber("syncInterval", s.SyncInterval); w.WriteBoolean("hdr", s.Hdr);
        w.WriteBoolean("adapterDrivesMonitor", s.AdapterDrivesMonitor);
        w.WriteEndObject();
    }
    w.WriteEndArray();
    w.WriteNumber("seconds", stats.Seconds);
    w.WriteNumber("frames", stats.Frames);
    w.WriteNumber("fps", stats.Fps);
    WritePercentiles(w, "cpuMsPerFrame", stats.FrameCpuMs);
    WritePercentiles(w, "frameIntervalMs", stats.FrameIntervalMs);
    w.WriteNumber("slowFrames", stats.SlowFrames);
    w.WriteNumber("startupMs", Math.Round(startupMs, 1));
    w.WriteNumber("workingSetMB", Math.Round(process.WorkingSet64 / 1048576.0, 1));
    w.WriteNumber("privateMB", Math.Round(process.PrivateMemorySize64 / 1048576.0, 1));
    w.WriteNumber("processCpuPercent", Math.Round(cpuSeconds / launch.Elapsed.TotalSeconds / Environment.ProcessorCount * 100, 2));
    w.WriteNumber("gcGen0", GC.CollectionCount(0));
    w.WriteNumber("gcGen2", GC.CollectionCount(2));
    w.WriteNumber("allocatedMB", Math.Round(GC.GetTotalAllocatedBytes() / 1048576.0, 1));
    w.WriteNumber("rebuilds", stats.Rebuilds);
    w.WriteNumber("deviceRecoveries", stats.DeviceRecoveries);
    w.WriteStartArray("notes");
    lock (notes) foreach (var n in notes) w.WriteStringValue(n);
    if (!string.IsNullOrEmpty(tree)) w.WriteStringValue("tree after attach: " + tree);
    w.WriteEndArray();
    w.WriteEndObject();
}
return 0;

static void WritePercentiles(Utf8JsonWriter w, string name, Percentiles p)
{
    w.WriteStartObject(name);
    w.WriteNumber("avg", p.Avg); w.WriteNumber("p50", p.P50); w.WriteNumber("p95", p.P95); w.WriteNumber("p99", p.P99);
    w.WriteEndObject();
}

/// <summary>Prints engine log lines and keeps warnings and errors as notes for the stats file.</summary>
internal sealed class ConsoleSink(List<string> notes, bool verbose) : ILogSink
{
    public void Write(in LogEntry entry)
    {
        if (entry.Level >= LogLevel.Warn) lock (notes) notes.Add($"{entry.Level}: {entry.Message}");
        if (verbose || entry.Level >= LogLevel.Info) Console.WriteLine($"  [{entry.Timestamp:HH:mm:ss.fff} {entry.Level}] {entry.Message}{(entry.Exception is null ? "" : " " + entry.Exception)}");
    }
}
