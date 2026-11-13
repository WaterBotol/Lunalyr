using System.Globalization;
using System.Text;

namespace Lunalyr.Tools.WallpaperCost;

/// <summary>The Markdown report and its JSON twin.</summary>
internal static class Reports
{
    public static Report Build(Options o, List<RunResult> runs)
    {
        var drawn = runs.FirstOrDefault(r => r.Child is { Adapter: not "-" })?.Child;
        return new Report
        {
            When = DateTimeOffset.Now,
            Machine = $"{Environment.ProcessorCount} logical processors, {Environment.OSVersion.VersionString}",
            Adapter = drawn is null ? "-" : $"{drawn.Adapter} ({(drawn.VideoSupport ? "video support" : "no video support")})",
            Width = o.Width,
            Height = o.Height,
            Scale = o.Scale,
            SecondsPerRun = o.Seconds,
            Composition = o.Composition,
            Clip = $"{Media.ClipWidth}×{Media.ClipHeight} H.264, {Media.ClipFps} fps, 8 Mbit/s, {Media.ClipSeconds} s, no audio track (invented, drawn in code)",
            Picture = $"{Media.PictureWidth}×{Media.PictureHeight} JPEG (invented)",
            Caveats =
            [
                "Offscreen: the layers draw into one bitmap on the GPU. There is no swap chain, no Present, no DWM composition and only one monitor. "
                    + "On the desktop, DWM also composes the surface each frame, and each extra monitor has its own surface.",
                "Paced at 60 fps by a high-resolution timer standing in for vsync, with one frame in flight. A fence stands in for the swap chain's frame-latency waitable.",
                "Frames are drawn only when the render engine would draw them. With nothing animating (a still picture with nothing playing), the frame clock stops and the run shows almost no frames.",
                "Playing: the app's controller follows the silent demo player from song time ≈" + (o.Start + 3).ToString("0", CultureInfo.InvariantCulture)
                    + " s of \"Neon Harbour\", with word-timed lyrics and its cover's colours. The readings are the basic ones, without the Japanese dictionary.",
                "Idle: the controller with no player. This is the app's \"nothing playing\" wallpaper: the default colours, no lyrics, the clock stopped, and the background the settings pick.",
                "Process CPU % is of all cores, from the child's TotalProcessorTime over the measured window. GPU % is Windows' GPU Engine counters for the child on the measured adapter, sampled each second.",
                "The tool runs under the JIT, while the app ships as Native AOT. The CPU figures are for JIT-compiled code, and private bytes and working set include the JIT and its metadata, so both read higher than the shipped app's. Start-up and warm-up are excluded.",
                "Alloc B/frame is the measured window's render-thread allocations divided by its frames. The note gives how many frames allocated anything, the median frame's allocation and the most any one frame allocated, so a steady per-frame allocation can be told from bursts (the lyric layers set the next line ahead of time).",
                "Baseline: the same child with the controller running and no device or layers. The \"over baseline\" columns are the wallpaper's own cost.",
                "Render-thread ms is BeginDraw to Flush on the drawing thread. GPU ms comes from D3D timestamps around each frame (decode runs on its own engine and isn't included).",
            ],
            Runs = runs,
        };
    }

    public static string Markdown(Report r)
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("# Wallpaper cost per mode (offscreen measurement)");
        sb.AppendLine();
        sb.AppendLine(c, $"{r.When:yyyy-MM-dd HH:mm}. {r.Adapter}; {r.Machine}.");
        sb.AppendLine(c, $"Surface {r.Width}×{r.Height} at scale {r.Scale}, {r.SecondsPerRun:0.#} s measured per run, composition {r.Composition}.");
        sb.AppendLine(c, $"Video modes use a {r.Clip}; the imported picture is a {r.Picture}.");
        sb.AppendLine();
        foreach (var caveat in r.Caveats) sb.AppendLine("- " + caveat);
        sb.AppendLine();
        sb.AppendLine("| Mode | State | Layer | fps | Render ms/frame (mean / p95) | GPU ms/frame (mean / p95) | CPU % | CPU % over baseline | GPU 3D % | Video decode % | Video proc. % | Private MB | Private MB over baseline | Working set MB | GPU dedicated / shared MB | Alloc B/frame (all / background / lyrics) | Notes |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var run in r.Runs)
        {
            if (run.Child is not { } x)
            {
                sb.AppendLine(c, $"| {run.Title} | {run.State} | | | | | | | | | | | | | | | {run.Skipped} |");
                continue;
            }
            var notes = new List<string>(x.Notes);
            if (x.Frames == 0 && x.Mode != Mode.Baseline.Id) notes.Add("frame clock stopped (nothing animates)");
            if (x.MissedSlots > 0) notes.Add($"{x.MissedSlots} missed 60 fps slot(s)");
            if (x.LayerErrors > 0) notes.Add($"{x.LayerErrors} layer error(s)");
            if (run.Warnings > 0) notes.Add($"{run.Warnings} log warning(s)");
            if (run.DecodedOn is not null) notes.Add($"video decoded in {run.DecodedOn}");
            if (x.AllocatingFrames > 0) notes.Add($"{x.AllocatingFrames} of {x.Frames} frames allocated (median {x.MedianBytesInAFrame} B, at most {x.MaxBytesInAFrame} B)");
            if (x.Gc0 + x.Gc1 + x.Gc2 > 0) notes.Add($"GCs {x.Gc0}/{x.Gc1}/{x.Gc2}");
            if (x.SongStart is { } a && x.SongEnd is { } b) notes.Add(string.Create(c, $"song {a:0.0}→{b:0.0} s"));
            var drawn = x.Mode != Mode.Baseline.Id;
            sb.AppendLine(string.Create(c,
                $"| {run.Title} | {run.State} | {(drawn ? x.Layer : "-")} | {(drawn ? x.Fps.ToString("0.0", c) : "-")} | {Pair(x.CpuMsMean, x.CpuMsP95, "0.00")} | {Pair(x.GpuMsMean, x.GpuMsP95, "0.00")} "
                + $"| {N(run.CpuPercent, "0.00")} | {Signed(run.CpuPercentOverBaseline, 2)} | {N(run.Gpu3DPercent, "0.0")} | {N(run.GpuVideoDecodePercent, "0.0")} | {N(run.GpuVideoProcessingPercent, "0.0")} "
                + $"| {N(run.PrivateMb, "0")} | {Signed(run.PrivateMbOverBaseline, 0)} | {N(run.WorkingSetMb, "0")} | {N(run.GpuDedicatedMb, "0")} / {N(run.GpuSharedMb, "0")} | {(x.RenderThreadBytesPerFrame is null ? "-" : $"{N(x.RenderThreadBytesPerFrame, "0")} / {N(x.BackgroundBytesPerFrame, "0")} / {N(x.LyricsBytesPerFrame, "0")}")} | {string.Join("; ", notes)} |"));
        }
        return sb.ToString();

        string N(double? v, string format) => v is { } d && double.IsFinite(d) ? d.ToString(format, c) : "-";
        // Rounded first, so a hair below zero reads "0.00", not "-0.00".
        string Signed(double? v, int decimals) => v is { } d && double.IsFinite(d)
            ? Math.Round(d, decimals) is var r && r == 0 ? 0.0.ToString(decimals == 0 ? "0" : "0." + new string('0', decimals), c)
                : r.ToString((decimals == 0 ? "+0" : "+0." + new string('0', decimals)) + ";-" + (decimals == 0 ? "0" : "0." + new string('0', decimals)), c)
            : "-";
        string Pair(double? a, double? b, string format) => a is null ? "-" : $"{N(a, format)} / {N(b, format)}";
    }
}
