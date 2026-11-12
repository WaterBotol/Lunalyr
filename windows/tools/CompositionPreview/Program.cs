// CompositionPreview: renders the lyric compositions offscreen to PNGs, and measures their per-frame cost.
//
//   CompositionPreview [-out DIR] [-width 1920] [-height 1080] [-scale 1.25] [-only editorial|spoken|cascade|cutout]
//                      [-reveal automatic|fade|lift|...]
//                      [-shot fixture@seconds ...] [-bench] [-warp]
//
// Fixtures: english, japanese, arabic, emoji, linetimed, plain, nolyrics; extras: japanesewords, japaneseinstead,
// credits, highlight.
//   (and the stage layouts: -only hinge|broadside|portal|mosaic|torque|fable)

using System.Diagnostics;
using System.Globalization;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Platform.Readings;
using Lunalyr.Render.Compositions;
using Lunalyr.Render.Compositions.Stage;
using Lunalyr.Render.Scene;
using Lunalyr.Tools.CompositionPreview;

var output = "previews";
int width = 1920, height = 1080;
var scale = 1.25f;
string? only = null;
string? reveal = null;
var bench = false;
var warp = false;
var shots = new List<(string Fixture, double Time)>();
for (var i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    switch (args[i].TrimStart('-'))
    {
        case "out": output = Next(); break;
        case "width": width = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "height": height = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "scale": scale = float.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "only": only = Next(); break;
        case "reveal": reveal = Next(); break;
        case "bench": bench = true; break;
        case "dump":
            foreach (var line in ((Lunalyr.Core.Lyrics.LyricResult.Synced)Fixtures.English().Lyrics!).Lines)
                Console.WriteLine($"{line.Start:0.00}-{line.End:0.00} wt={line.WordTimed} [{line.Text}] " + string.Join(" | ", line.Words.Select(w => $"{w.Text}@{w.Start:0.00}")));
            return;
        case "warp": warp = true; break;
        case "probe":
            using (var probeStage = new Offscreen(64, 64)) Probe.Run(probeStage);
            return;
        case "shot":
            var parts = Next().Split('@');
            shots.Add((parts[0], double.Parse(parts[1], CultureInfo.InvariantCulture)));
            break;
    }
}

IWordBreaker breaker = IcuWordBreaker.TryCreate() ?? (IWordBreaker)SimpleWordBreaker.Instance;
IRenderLayerFactory[] factories = [new EditorialFactory(breaker), new SpokenFactory(breaker), new CascadeFactory(breaker), new CutoutFactory(breaker)];
factories = [.. factories, .. StageFactories.All(breaker)];
var fixtures = new Dictionary<string, Func<SceneSnapshot>>
{
    ["english"] = () => Fixtures.English(),
    ["japanese"] = () => Fixtures.Japanese(),
    ["arabic"] = () => Fixtures.Arabic(),
    ["emoji"] = () => Fixtures.Emoji(),
    ["linetimed"] = () => Fixtures.LineTimed(),
    ["plain"] = () => Fixtures.Plain(),
    ["nolyrics"] = () => Fixtures.NoLyrics(),
    // The wallpaper extras: word-by-word readings below and instead, credits, highlight tiles.
    ["japanesewords"] = () => ExtrasFixtures.JapaneseWords(),
    ["japaneseinstead"] = () => ExtrasFixtures.JapaneseWords(LyricStyle.Default with { Readings = ReadingPlacement.Instead }),
    ["credits"] = () => ExtrasFixtures.Credits(),
    ["highlight"] = () => ExtrasFixtures.Highlight(),
};
if (shots.Count == 0)
{
    shots.AddRange([
        ("english", 9.8), ("english", 10.25), ("english", 11.3), ("english", 12.9), ("english", 14.6), ("english", 15.9),
        ("english", 19.5), ("english", 24.5), ("english", 29.5), ("english", 36.5),
        ("japanese", 10.75), ("japanese", 12.8), ("japanese", 15.3),
        ("arabic", 10.9), ("arabic", 12.6),
        ("emoji", 11.2), ("emoji", 13.0),
        ("linetimed", 9.8), ("linetimed", 10.15), ("linetimed", 12.0), ("linetimed", 13.8), ("linetimed", 14.4),
        ("linetimed", 32.0),
        ("plain", 5), ("nolyrics", 0),
        ("japanesewords", 12.8), ("japanesewords", 16.5), ("japaneseinstead", 12.8), ("credits", 1.6), ("credits", 10.5),
        ("highlight", 12.9), ("highlight", 15.9),
    ]);
}

using var stage = new Offscreen(width, height, warp);
Console.WriteLine($"Device: {stage.AdapterName}{(stage.IsWarp ? " (WARP)" : "")}, {width}x{height} at {scale}x");
var background = Fixtures.Night.Base;

foreach (var factory in factories)
{
    if (only is not null && factory.Id != only) continue;
    if (bench)
    {
        Bench(stage, factory, new SceneSnapshot(), 9.5, 12, scale);
        Bench(stage, factory, Fixtures.NoLyrics(), 9.5, 12, scale);
        Bench(stage, factory, Fixtures.English(), 9.5, 24, scale);
        Bench(stage, factory, Fixtures.Japanese(), 9.5, 17, scale);
        continue;
    }
    using var layer = factory.Create();
    layer.CreateResources(stage.Dc, stage.DWrite, new System.Numerics.Vector2(width, height), scale);
    var scenes = new Dictionary<string, SceneSnapshot>();
    foreach (var (name, time) in shots)
    {
        if (!fixtures.TryGetValue(name, out var make)) { Console.WriteLine($"No fixture {name}"); continue; }
        if (!scenes.TryGetValue(name, out var scene))
        {
            scene = make();
            if (reveal is not null) scene = scene with { Style = scene.Style with { Reveal = reveal } };
            scenes[name] = scene;
        }
        // A few frames leading up to the shot, as the app would draw them.
        for (var t = time - 0.2; t < time; t += 1.0 / 60) stage.Render(layer, scene, t, 10 + t, scale, background);
        stage.Render(layer, scene, time, 10 + time, scale, background);
        var path = Path.Combine(output, $"{factory.Id}-{name}-{time.ToString("00.00", CultureInfo.InvariantCulture)}{(reveal is null ? "" : "-" + reveal)}.png");
        stage.SavePng(path);
        Console.WriteLine(path);
    }
}

static void Bench(Offscreen stage, IRenderLayerFactory factory, SceneSnapshot scene, double from, double to, float scale)
{
    using var layer = factory.Create();
    layer.CreateResources(stage.Dc, stage.DWrite, new System.Numerics.Vector2(stage.Width, stage.Height), scale);
    var background = Fixtures.Night.Base;
    // Cold: a fresh layer played through once, so lines are set (text laid out and baked) as they arrive.
    {
        using var cold = factory.Create();
        cold.CreateResources(stage.Dc, stage.DWrite, new System.Numerics.Vector2(stage.Width, stage.Height), scale);
        var coldWatch = new Stopwatch();
        double coldWorst = 0, coldTotal = 0;
        int coldFrames = 0, building = 0;
        for (var t = from; t < to; t += 1.0 / 60)
        {
            coldWatch.Restart();
            stage.Render(cold, scene, t, t, scale, background);
            coldWatch.Stop();
            var ms = coldWatch.Elapsed.TotalMilliseconds;
            coldWorst = Math.Max(coldWorst, ms);
            coldTotal += ms;
            coldFrames++;
            if (ms > 2) { building++; Console.Write($"[{t:0.00}s {ms:0.0} ms] "); }
        }
        Console.WriteLine($"{factory.Id} {scene.Title} (cold): {coldTotal / coldFrames:0.000} ms/frame avg, worst {coldWorst:0.00} ms, {building} frames over 2 ms");
    }
    // Warm up: every line's plan is built once.
    for (var t = from; t < to; t += 1.0 / 15) stage.Render(layer, scene, t, t, scale, background);
    var frames = 0;
    double renderMs = 0, worst = 0;
    long allocated = 0;
    var steady = 0;
    var allocating = 0;
    long steadyAllocated = 0;
    var watch = new Stopwatch();
    for (var pass = 0; pass < 2; pass++)
    {
        for (var t = from; t < to; t += 1.0 / 60)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            watch.Restart();
            stage.Render(layer, scene, t, t, scale, background);
            watch.Stop();
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            var ms = watch.Elapsed.TotalMilliseconds;
            frames++;
            renderMs += ms;
            worst = Math.Max(worst, ms);
            allocated += bytes;
            if (pass == 1) { steady++; steadyAllocated += bytes; if (bytes > 0) allocating++; }
        }
    }
    Console.WriteLine($"{factory.Id} {scene.Title}: {frames} frames, {renderMs / frames:0.000} ms/frame avg (render + EndDraw), " +
                      $"worst {worst:0.00} ms, {allocated / (double)frames:0} B/frame allocated; second pass {steadyAllocated / (double)Math.Max(1, steady):0} B/frame, {allocating} of {steady} frames allocated anything");
}
