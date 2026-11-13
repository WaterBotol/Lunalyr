// WallpaperCost: what each wallpaper mode costs, playing and idle.
//
//   WallpaperCost [-seconds 15] [-size 2560x1440] [-scale 1.25] [-adapter <name part>] [-only mode,mode]
//                 [-states playing,idle] [-out file.md] [-snapshots <dir>] [-composition editorial] [-media <dir>]
//                 [-start 5] [-warmup <s>] [-no-baseline]
//
// For each mode (Mode.All, or -only; shader.<id> and pattern.<id> pin any effect or pattern) and state, a child process
// of this tool runs the app's controller (following the silent demo player, or no player), draws its scene with the
// app's own layers into one offscreen surface on the GPU that drives the main display, paced at 60 fps, and reports
// its frame times; this process samples the child's CPU time, memory and GPU engine counters from outside. A baseline
// run (the controller, no device) comes first so the rows read as the wallpaper's own cost. -out writes the Markdown
// report and a .json beside it; the table is printed either way. -snapshots writes one PNG per run after warm-up.
//
// Nothing is played or captured (the visualiser hears the demo's synthetic signal, videos have no audio track and only
// their video stream is read), nothing is drawn on screen, nothing touches the network or the app's data folder.

using System.Diagnostics;
using System.Globalization;
using Lunalyr.Tools.WallpaperCost;

if (args.Length >= 3 && args[0] == "--child")
{
    var mode = args[1] == Mode.Baseline.Id ? Mode.Baseline : Mode.Find(args[1]);
    var childOptions = Options.Parse(args, 3, out var childError);
    if (mode is null || childError is not null)
    {
        Console.Error.WriteLine(childError ?? $"Unknown mode {args[1]}.");
        return 2;
    }
    return await Child.RunAsync(mode, args[2] == "playing", childOptions).ConfigureAwait(false);
}

var options = Options.Parse(args, 0, out var error);
if (error is not null)
{
    Console.Error.WriteLine(error);
    Console.Error.WriteLine("usage: WallpaperCost [-seconds 15] [-size 2560x1440] [-scale 1.25] [-adapter <name>] [-only mode,mode] "
        + "[-states playing,idle] [-out file.md] [-snapshots <dir>] [-composition editorial] [-media <dir>] [-start 5] [-warmup s] [-no-baseline]");
    Console.Error.WriteLine("modes: " + string.Join(", ", Mode.All.Select(m => m.Id)) + ", shader.<id>, pattern.<id>");
    return 2;
}
var modes = new List<Mode>();
foreach (var id in options.Only ?? [.. Mode.All.Select(m => m.Id)])
{
    if (Mode.Find(id) is { } found) modes.Add(found);
    else
    {
        Console.Error.WriteLine($"Unknown mode {id} (modes: {string.Join(", ", Mode.All.Select(m => m.Id))}, shader.<id>, pattern.<id>).");
        return 2;
    }
}
if (modes.Any(m => m.Source != LayerSource.Controller)) Media.Ensure(options.Media);

var states = options.States;
var plan = new List<(Mode Mode, string State)>();
if (options.Baseline) foreach (var state in states) plan.Add((Mode.Baseline, state));
foreach (var mode in modes) foreach (var state in states) plan.Add((mode, state));
var perRun = options.Seconds + 6;
Console.WriteLine($"{plan.Count} runs of {options.Seconds:0.#} s (about {plan.Count * perRun / 60:0.#} min in all).");

var runs = new List<RunResult>();
var total = Stopwatch.StartNew();
foreach (var (mode, state) in plan)
{
    if (state == "idle" && mode.IdleAs is { } same)
    {
        runs.Add(new RunResult { Mode = mode.Id, Title = mode.Title, State = state, Skipped = $"same as {same} idle: {mode.IdleWhy}" });
        Console.WriteLine($"{mode.Id,-16} {state,-8} skipped (same as {same} idle)");
        continue;
    }
    var run = Runner.Run(mode, state, options);
    runs.Add(run);
    Console.WriteLine(Runner.Line(run));
}
// Over the baseline of the same state.
for (var i = 0; i < runs.Count; i++)
{
    var r = runs[i];
    var baseline = runs.FirstOrDefault(b => b.Mode == Mode.Baseline.Id && b.State == r.State && b.Ran);
    if (!r.Ran || baseline is null || r.Mode == Mode.Baseline.Id) continue;
    runs[i] = r with
    {
        CpuPercentOverBaseline = r.CpuPercent - baseline.CpuPercent,
        PrivateMbOverBaseline = r.PrivateMb - baseline.PrivateMb,
    };
}
Console.WriteLine($"Done in {total.Elapsed.TotalMinutes:0.0} min.");

var report = Reports.Build(options, runs);
var markdown = Reports.Markdown(report);
Console.WriteLine();
Console.WriteLine(markdown);
if (options.Out is not null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(options.Out)!);
    File.WriteAllText(options.Out, markdown);
    var json = Path.ChangeExtension(options.Out, ".json");
    File.WriteAllText(json, System.Text.Json.JsonSerializer.Serialize(report, CostJson.Default.Report));
    Console.WriteLine($"Wrote {options.Out} and {json}.");
}
return runs.Any(r => r.Ran && r.Child is null) ? 1 : 0;
