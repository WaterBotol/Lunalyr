// VisualiserPreview <out-dir> [--style <raw>] [--size WxH] [--adapter <name>] [--costs] [--limiter]
//
// Renders every visualiser scene from recorded synthetic sound to PNGs (a 2 × 2 sheet per scene: silence, a beat, a
// sweep, loud noise), checks the photosensitivity limiter against a jump to loud noise with Reduce Motion on, and with
// --costs measures each scene's GPU time at 2560 × 1440 on the named adapter. Sound is generated in memory and fed to
// the analyser; nothing is captured, played or saved as audio.

using System.Globalization;
using System.Numerics;
using System.Text;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Visualisers;
using Lunalyr.Render.Diagnostics;
using Lunalyr.Render.Scene;
using Lunalyr.Render.Visualisers;

var outDir = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal) ? args[0] : "visualiser-previews";
string? Option(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
var only = Option("--style");
var adapter = Option("--adapter");
var size = (Option("--size") ?? "480x270").Split('x');
int width = int.Parse(size[0], CultureInfo.InvariantCulture), height = int.Parse(size[1], CultureInfo.InvariantCulture);
Directory.CreateDirectory(outDir);
var styles = VisualizerStyles.All.Where(s => only is null || s.RawValue() == only).ToArray();

if (args.Contains("--loopback-check"))
{
    // A short live check of System audio: loopback of the default output for a few seconds, printing levels only.
    // Nothing is saved; nothing is played.
    var audio = new VisualizerAudio();
    var feed = new Lunalyr.Core.Visualisers.Capture.CaptureFeed(audio);
    using var meter = new Lunalyr.Platform.Audio.OutputActivityMeter();
    var events = new PrintEvents();
    var opened = System.Diagnostics.Stopwatch.StartNew();
    using (var session = Lunalyr.Platform.Audio.WasapiLoopback.Open(feed, events))
    {
        Console.WriteLine($"Started in {opened.ElapsedMilliseconds} ms; output latency {(session.OutputLatency is { } l ? $"{l * 1000:0.0} ms" : "not reported")}.");
        for (var second = 1; second <= 6; second++)
        {
            Thread.Sleep(1000);
            var now = AudioAnalyzer.Now();
            var report = feed.Read(now);
            var snapshot = audio.Latest();
            Console.WriteLine(report is { } r
                ? $"{second}s: packets {r.Packets}, rms {r.Levels.Rms:0.000}, bass {snapshot.Bass:0.00}, mid {snapshot.Mid:0.00}, treble {snapshot.Treble:0.00}, audible {(r.AudibleAt is { } a ? $"{now - a:0.00}s ago" : "never")}, format {r.FormatLabel}, output active {meter.IsActive()}"
                : $"{second}s: no packets (nothing playing?), output active {meter.IsActive()}");
        }
    }
    var stopped = System.Diagnostics.Stopwatch.StartNew();
    Console.WriteLine($"Stopped cleanly ({stopped.ElapsedMilliseconds} ms after the last read).");
    return;
}

var signals = new (string Name, AudioSnapshot Sound, double Time)[]
{
    ("silence", Synth.Silence(), 12),
    ("beat", Synth.Beat(), 14),
    ("sweep", Synth.Sweep(), 16),
    ("noise", Synth.Noise(), 18),
};

if (args.Contains("--costs"))
{
    using var gpu = new VisualiserStill(adapter);
    var report = new StringBuilder();
    report.AppendLine($"GPU time per frame on {gpu.AdapterName}, 2560 × 1440 surface (median of 120 frames, the beat signal):");
    report.AppendLine();
    report.AppendLine("| Scene | Drawn at | ms at the scene's pixel budget | ms at full 2560 × 1440 |");
    report.AppendLine("|---|---|---|---|");
    foreach (var style in styles)
    {
        var (w, h) = RenderScaleLadder.DrawableSize(2560, 1440, style, preview: false);
        var budget = gpu.MeasureMilliseconds(style, signals[1].Sound, w, h, 120);
        var full = gpu.MeasureMilliseconds(style, signals[1].Sound, 2560, 1440, 120);
        report.AppendLine(CultureInfo.InvariantCulture, $"| {style.Title()} | {w} × {h} | {budget:0.00} | {full:0.00} |");
    }
    Console.Write(report);
    File.WriteAllText(Path.Combine(outDir, $"costs-{Slug(gpu.AdapterName)}.md"), report.ToString());
    return;
}

using (var still = new VisualiserStill(adapter))
{
    var cover = new AlbumWallpaperPalette([.. Enumerable.Range(0, 9).Select(i => new Double3(0.55 + 0.04 * i, 0.7, 0.85))]);
    foreach (var style in styles)
    {
        var sheet = new byte[width * 2 * height * 2 * 4];
        for (var k = 0; k < signals.Length; k++)
        {
            var pixels = still.Render(style, signals[k].Sound, width, height, signals[k].Time);
            Blit(pixels, sheet, k % 2 * width, k / 2 * height);
            Console.WriteLine($"{style.Title(),-18} {signals[k].Name,-8} mean luminance {Mean(pixels):0.000}");
        }
        File.WriteAllBytes(Path.Combine(outDir, $"{style.RawValue()}.png"), VisualiserStill.Png(sheet, width * 2, height * 2));
        var album = still.Render(style, signals[1].Sound, width, height, 14, palette: cover);
        File.WriteAllBytes(Path.Combine(outDir, $"{style.RawValue()}-album.png"), VisualiserStill.Png(album, width, height));
    }
}

if (args.Contains("--limiter")) Limiter(styles);
return;

void Blit(byte[] tile, byte[] sheet, int left, int top)
{
    for (var y = 0; y < height; y++)
        Array.Copy(tile, y * width * 4, sheet, ((top + y) * width * 2 + left) * 4, width * 4);
}

static double Mean(byte[] bgra)
{
    double sum = 0;
    for (var i = 0; i < bgra.Length; i += 4) sum += ExposureLimiter.Luminance(bgra[i + 2] / 255f, bgra[i + 1] / 255f, bgra[i] / 255f);
    return sum / (bgra.Length / 4);
}

static string Slug(string name) => new([.. name.Where(char.IsLetterOrDigit).Take(24)]);

// Silence for a second, then loud noise, drawn through the wallpaper layer at 60 fps: once with Reduce Motion on (the
// limiter caps every rise at 0.25 relative luminance a second) and once without.
void Limiter(VisualizerStyle[] scenes)
{
    var csv = new StringBuilder("scene,calm,frame,seconds,mean\n");
    var summary = new StringBuilder("| Scene | Unlimited rise in the first 100 ms | Limited rise in the first 100 ms | Largest limited rise over any 1 s |\n|---|---|---|---|\n");
    foreach (var style in scenes)
    {
        var rises = new double[2];
        var worst = 0.0;
        foreach (var calm in new[] { false, true })
        {
            var sound = new SwitchedSound(Synth.Silence(), signals[3].Sound, switchAt: 1);
            var host = new VisualiserHost(() => VisualiserSettings.Default with { Style = style }, sound);
            using var surface = new OffscreenSurface(320, 180);
            using var layer = new VisualiserLayerFactory(host).Create();
            surface.Prepare(layer);
            var scene = new SceneSnapshot { ReduceMotion = calm };
            var means = new List<double>();
            for (var frame = 0; frame < 240; frame++)
            {
                var t = frame / 60.0;
                sound.Time = 1_000 + t;
                surface.Draw(layer, scene, t);
                var mean = Mean(surface.ReadPixels());
                means.Add(mean);
                csv.AppendLine(CultureInfo.InvariantCulture, $"{style.RawValue()},{calm},{frame},{t:0.000},{mean:0.00000}");
            }
            rises[calm ? 1 : 0] = means[66] - means[59];
            if (calm)
                for (var start = 60; start + 60 < means.Count; start++)
                    worst = Math.Max(worst, Enumerable.Range(start, 60).Sum(i => Math.Max(0, means[i + 1] - means[i])));
        }
        summary.AppendLine(CultureInfo.InvariantCulture, $"| {style.Title()} | {rises[0]:0.0000} | {rises[1]:0.0000} | {worst:0.0000} |");
    }
    File.WriteAllText(Path.Combine(outDir, "limiter.csv"), csv.ToString());
    File.WriteAllText(Path.Combine(outDir, "limiter.md"), summary.ToString());
    Console.Write(summary);
}

/// <summary>One recorded reading until a moment, another after it; the clock is set by the caller.</summary>
sealed class SwitchedSound(AudioSnapshot before, AudioSnapshot after, double switchAt) : IVisualiserSound
{
    public double Time { get; set; }

    public AudioSnapshot Latest()
    {
        var sound = Time - 1_000 < switchAt ? before : after;
        return sound;
    }

    public double Now => Time;
}

/// <summary>Deterministic synthetic sound, analysed as capture would be (no audio is ever played or saved).</summary>
static class Synth
{
    private const double Rate = 48_000;

    private static AudioSnapshot Run(double seconds, Func<long, double, float> sample)
    {
        double clock = 0;
        var audio = new VisualizerAudio(now: () => clock);
        var buffer = new float[480];
        long n = 0;
        for (var block = 0; block < (int)(seconds * 100); block++)
        {
            for (var i = 0; i < buffer.Length; i++, n++) buffer[i] = sample(n, n / Rate);
            clock = n / Rate;
            audio.Append(buffer, Rate, 0, clock);
        }
        return audio.Latest(clock);
    }

    public static AudioSnapshot Silence() => Run(2, (_, _) => 0);

    /// <summary>120 bpm: a kick on every beat, hats between, a bass line and a soft pad; read just after a kick.</summary>
    public static AudioSnapshot Beat()
    {
        var random = new Random(7);
        return Run(4.03, (_, t) =>
        {
            var beat = t % 0.5;
            var kick = Math.Sin(2 * Math.PI * (50 + 80 * Math.Exp(-beat * 30)) * beat) * Math.Exp(-beat * 9) * 0.8;
            var off = (t + 0.25) % 0.5;
            var hat = (random.NextDouble() * 2 - 1) * Math.Exp(-off * 60) * 0.25;
            var bass = Math.Sin(2 * Math.PI * 82.4 * t) * 0.18;
            var pad = (Math.Sin(2 * Math.PI * 220 * t) + Math.Sin(2 * Math.PI * 277.2 * t) + Math.Sin(2 * Math.PI * 329.6 * t)) * 0.05;
            return (float)(kick + hat + bass + pad);
        });
    }

    /// <summary>A tone gliding from 40 Hz to 12 kHz over three seconds, read part-way (about 1.3 kHz).</summary>
    public static AudioSnapshot Sweep()
    {
        double phase = 0;
        return Run(1.8, (_, t) =>
        {
            var frequency = 40 * Math.Pow(300, t / 3);
            phase += 2 * Math.PI * frequency / Rate;
            return (float)(Math.Sin(phase) * 0.4);
        });
    }

    /// <summary>Loud white noise.</summary>
    public static AudioSnapshot Noise()
    {
        var random = new Random(11);
        return Run(2, (_, _) => (float)((random.NextDouble() * 2 - 1) * 0.9));
    }
}

/// <summary>Prints what the loopback session reports.</summary>
sealed class PrintEvents : Lunalyr.Core.Visualisers.Capture.ICaptureSessionEvents
{
    public void RouteChanged(string reason) => Console.WriteLine("Route changed: " + reason);

    public void Failed(string message) => Console.WriteLine("Failed: " + message);
}
