using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Lunalyr.Tools.WallpaperCost;

/// <summary>
/// Starts one child run (no window: a console process with redirected streams) and samples it from outside: CPU time
/// across the measured window, the GPU engine counters once a second, private bytes and working set at its end.
/// </summary>
internal static partial class Runner
{
    public static RunResult Run(Mode mode, string state, Options o)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("--child");
        info.ArgumentList.Add(mode.Id);
        info.ArgumentList.Add(state);
        foreach (var a in o.ChildArguments()) info.ArgumentList.Add(a);

        var log = new List<string>();
        var result = new RunResult { Mode = mode.Id, Title = mode.Title, State = state, Ran = true };
        using var child = Process.Start(info) ?? throw new InvalidOperationException("Couldn't start a child run.");
        child.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (log) log.Add(e.Data.StartsWith("LOG\t", StringComparison.Ordinal) ? e.Data[4..] : e.Data);
        };
        child.BeginErrorReadLine();
        var lines = new System.Collections.Concurrent.BlockingCollection<string>();
        var reader = new Thread(() =>
        {
            try
            {
                while (child.StandardOutput.ReadLine() is { } line) lines.Add(line);
            }
            catch (IOException) { }
            lines.CompleteAdding();
        }) { IsBackground = true };
        reader.Start();

        string? Next(TimeSpan wait)
        {
            try
            {
                return lines.TryTake(out var line, wait) ? line : null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        try
        {
            // Start-up, the device, warm-up: then READY, and the measured window starts.
            string? ready = null;
            var startBy = Stopwatch.StartNew();
            while (ready is null && startBy.Elapsed < TimeSpan.FromSeconds(120) && !lines.IsCompleted)
            {
                var line = Next(TimeSpan.FromSeconds(1));
                if (line?.StartsWith("READY", StringComparison.Ordinal) == true) ready = line;
                else if (line is not null) lock (log) log.Add(line);
            }
            if (ready is null) return Failed(result, child, log, "it never got ready");
            var cpu0 = child.TotalProcessorTime;
            var wall = Stopwatch.StartNew();
            var luid = ready.Length > 6 ? ready[6..].Trim() : "-";
            using var counters = luid.StartsWith("luid_", StringComparison.Ordinal) ? new GpuCounters(child.Id, luid) : null;

            string? done = null;
            var endBy = TimeSpan.FromSeconds(o.Seconds + 60);
            while (done is null && wall.Elapsed < endBy && !lines.IsCompleted)
            {
                var line = Next(TimeSpan.FromSeconds(1));
                if (line?.StartsWith("RESULT ", StringComparison.Ordinal) == true) done = line[7..];
                else if (line is not null) lock (log) log.Add(line);
                if (done is null) counters?.Sample();
            }
            if (done is null) return Failed(result, child, log, "it never reported");
            var cpu1 = child.TotalProcessorTime;
            var seconds = wall.Elapsed.TotalSeconds;
            counters?.Sample();
            child.Refresh();
            var privateMb = child.PrivateMemorySize64 / (1024.0 * 1024);
            var workingSetMb = child.WorkingSet64 / (1024.0 * 1024);
            var measured = System.Text.Json.JsonSerializer.Deserialize(done, CostJson.Default.ChildResult);
            child.StandardInput.WriteLine("go");
            child.StandardInput.Flush();
            if (!child.WaitForExit(30_000)) child.Kill(entireProcessTree: true);
            child.WaitForExit();
            List<string> kept;
            lock (log) kept = [.. log];
            return result with
            {
                Child = measured,
                CpuPercent = (cpu1 - cpu0).TotalSeconds / seconds / Environment.ProcessorCount * 100,
                Gpu3DPercent = counters?.ThreeD,
                GpuVideoDecodePercent = counters?.Decode,
                GpuVideoProcessingPercent = counters?.Processing,
                GpuOtherPercent = counters?.Other,
                GpuCounterSamples = counters?.Samples ?? 0,
                GpuDedicatedMb = counters?.DedicatedMb,
                GpuSharedMb = counters?.SharedMb,
                PrivateMb = privateMb,
                WorkingSetMb = workingSetMb,
                DecodedOn = kept.Select(l => DecodedIn().Match(l)).FirstOrDefault(m => m.Success)?.Groups[1].Value,
                Warnings = kept.Count(l => l.StartsWith("WARN", StringComparison.Ordinal) || l.StartsWith("ERROR", StringComparison.Ordinal)),
                Log = kept,
            };
        }
        catch (Exception ex)
        {
            return Failed(result, child, log, ex.Message);
        }
    }

    private static RunResult Failed(RunResult result, Process child, List<string> log, string why)
    {
        try
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            child.WaitForExit(10_000);
        }
        catch (InvalidOperationException) { }
        List<string> kept;
        lock (log) kept = [.. log];
        Console.Error.WriteLine($"{result.Mode} {result.State}: {why}");
        foreach (var line in kept.TakeLast(15)) Console.Error.WriteLine("  " + line);
        return result with { Log = kept, Skipped = "failed: " + why, Warnings = kept.Count };
    }

    /// <summary>One line on the console as each run ends.</summary>
    public static string Line(RunResult r)
    {
        if (r.Child is not { } c) return $"{r.Mode,-16} {r.State,-8} {r.Skipped}";
        var ci = CultureInfo.InvariantCulture;
        var frames = string.Create(ci, $"{r.Mode,-16} {r.State,-8} {c.Layer,-20} {c.Fps,5:0.0} fps  cpu {c.CpuMsMean ?? 0,6:0.00} ms (p95 {c.CpuMsP95 ?? 0,6:0.00})  gpu {c.GpuMsMean ?? 0,6:0.00} ms");
        var process = string.Create(ci, $"  {r.CpuPercent,5:0.00}% CPU  3D {r.Gpu3DPercent ?? 0,5:0.0}%  decode {r.GpuVideoDecodePercent ?? 0,5:0.0}%  {r.PrivateMb,6:0} MB private  {c.RenderThreadBytesPerFrame ?? 0,7:0} B/frame");
        var extra = (r.Warnings > 0 ? $"  {r.Warnings} warning(s)" : "")
            + (c.Snapshot is { } s ? string.Create(ci, $"  snapshot luminance {s.MeanLuminance:0.000}") : "");
        return frames + process + extra;
    }

    [GeneratedRegex(@"decoded in (\w+)")]
    private static partial Regex DecodedIn();
}
