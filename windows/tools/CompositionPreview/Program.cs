// CompositionPreview: renders the lyric compositions offscreen to PNGs, and measures their per-frame cost.
//
//   CompositionPreview [-out DIR] [-width 1920] [-height 1080] [-scale 1.25] [-only editorial|spoken|scattered|...]
//                      [-reveal automatic|fade|lift|pop|orbit|glide|unfold|cutoutFold|cutoutSlide|cutoutHighlight|satinRibbon|splitInk|mix]
//                      [-shot fixture@seconds ...] [-reduce] [-bench] [-warp] [-floating [-screen 2560|3200]]
//                      [-style name=value ...] [-tag text]
//   CompositionPreview -setup [-runs 5] [-surfaces 1|2] [-only id] [-width 2560 -height 1440 -scale 1.25]
//   CompositionPreview -hashes FILE [-only id] [-width 1280 -height 720 -scale 1]
//
// -setup and -hashes play the --demo songs through every composition (see SetupBench.cs): the line-setup benchmark,
// and a hash of the frame every tenth of a second to compare two builds pixel for pixel.
//
// Compositions: editorial, spoken, cascade, cutout, scattered, constellation, constellationEditorial, echo, and the
// stage layouts hinge, broadside, portal, mosaic, torque, fable. With -reveal, every fixture uses that word entrance
// and the file names carry it (<composition>-<reveal>-<fixture>-<seconds>.png). With -reduce, every scene asks for
// Reduce Motion (SceneSnapshot.ReduceMotion, as with Windows' "Animation effects" off) and the names carry "-reduced"
// after the composition and reveal; the tool never reads the system's own setting.
//
// With -style, every scene takes the Lyric style extras given (backing=off, backingStyle=echo, editorial=0.5,
// fonts=random, colour=sungSweep|musicReactive|customGradient|albumDrift, strength=1.6, speed=2, custom=#RRGGBB,#RRGGBB,
// reveal=off, preview=off, lead=1.0); -tag adds a word to the file names so the variants sit side by side.
//
// Fixtures: english, japanese, arabic, emoji, linetimed, plain, nolyrics; extras: japanesewords, japaneseinstead,
// japanesesyllables, credits, highlight.

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
var reduce = false;
var warp = false;
var opening = false;
var floating = false;
string? screenFilter = null;
var setup = false;
var runs = 5;
var surfaces = 1;
string? hashes = null;
var shots = new List<(string Fixture, double Time)>();
var extras = new List<(string Name, string Value)>();
string? tag = null;
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
        case "setup": setup = true; break;
        case "runs": runs = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "surfaces": surfaces = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "hashes": hashes = Next(); break;
        case "reduce": reduce = true; break;
        case "style":
            var pair = Next().Split('=', 2);
            extras.Add((pair[0], pair.Length > 1 ? pair[1] : ""));
            break;
        case "tag": tag = Next(); break;
        case "dump":
            foreach (var line in ((Lunalyr.Core.Lyrics.LyricResult.Synced)Fixtures.English().Lyrics!).Lines)
                Console.WriteLine($"{line.Start:0.00}-{line.End:0.00} wt={line.WordTimed} [{line.Text}] " + string.Join(" | ", line.Words.Select(w => $"{w.Text}@{w.Start:0.00}")));
            return;
        case "warp": warp = true; break;
        case "opening": opening = true; break;
        case "floating": floating = true; break;
        case "screen": screenFilter = Next(); break;
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
IRenderLayerFactory[] factories =
[
    new EditorialFactory(breaker), new SpokenFactory(breaker), new CascadeFactory(breaker), new CutoutFactory(breaker),
    new ScatteredFactory(breaker), new ConstellationFactory(breaker), new ConstellationEditorialFactory(breaker),
    new EchoFactory(breaker),
];
factories = [.. factories, .. StageFactories.All(breaker)];
var style = reveal is null ? null : LyricStyle.Default with { Reveal = reveal };
var fixtures = new Dictionary<string, Func<SceneSnapshot>>
{
    ["english"] = () => Fixtures.English(style),
    ["japanese"] = () => Fixtures.Japanese(style),
    ["arabic"] = () => Fixtures.Arabic(style),
    ["emoji"] = () => Fixtures.Emoji(style),
    ["linetimed"] = () => Fixtures.LineTimed(style),
    ["plain"] = () => Fixtures.Plain(style),
    ["nolyrics"] = () => Fixtures.NoLyrics(),
    // The wallpaper extras: word-by-word readings below and instead, credits, highlight tiles.
    ["japanesewords"] = () => ExtrasFixtures.JapaneseWords(),
    ["japaneseinstead"] = () => ExtrasFixtures.JapaneseWords(LyricStyle.Default with { Readings = ReadingPlacement.Instead }),
    // Readings that fill in syllable by syllable with their words (the Mac's StageReadingRevealTests lines).
    ["japanesesyllables"] = () => ExtrasFixtures.JapaneseSyllables(),
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
        ("japanesewords", 12.8), ("japanesewords", 16.5), ("japaneseinstead", 12.8), ("credits", 1.6), ("credits", 5.5), ("credits", 10.5),
        ("highlight", 12.9), ("highlight", 15.9),
    ]);
}

if (floating)
{
    // The floating lyrics window over a stand-in desktop on this PC's two displays (FloatingShots.cs). Files:
    // <screen>-<shot>.png. -only picks the layout (Editorial by default); -screen 2560 or 3200 picks one display.
    var lyricFactory = factories.FirstOrDefault(factory => factory.Id == (only ?? "editorial")) ?? factories[0];
    var floatingFixtures = new Dictionary<string, Func<SceneSnapshot>>(fixtures) { ["opening"] = () => OpeningFixtures.HumanNature() };
    // -style reaches the floating window's scenes too (-tag names the folder the shots go to).
    if (extras.Count > 0)
        foreach (var (name, make) in floatingFixtures.ToList())
            floatingFixtures[name] = () => make() is var scene ? scene with { Style = StyleExtras.Apply(scene.Style, extras) } : scene;
    if (tag is not null) output = Path.Combine(output, tag);
    FloatingShots.Run(output, lyricFactory, floatingFixtures, warp, screenFilter);
    return;
}

using var stage = new Offscreen(width, height, warp);
Console.WriteLine($"Device: {stage.AdapterName}{(stage.IsWarp ? " (WARP)" : "")}, {width}x{height} at {scale}x");
var background = Fixtures.Night.Base;

if (setup)
{
    SetupBench.Run(stage, factories, only, scale, runs, surfaces);
    return;
}
if (hashes is not null)
{
    SetupBench.Hashes(stage, factories, only, scale, hashes);
    return;
}

if (opening)
{
    // The song's opening title over the album gradient, through each card's moments (-only picks the layout; Editorial
    // by default). Files: <song>-<moment>.png.
    var lyricFactory = factories.FirstOrDefault(factory => factory.Id == (only ?? "editorial")) ?? factories[0];
    foreach (var song in OpeningFixtures.Songs())
    {
        using var gradient = new Lunalyr.Render.Backgrounds.GradientLayerFactory().Create();
        using var lyrics = lyricFactory.Create();
        IRenderLayer[] layers = [gradient, lyrics];
        foreach (var layer in layers) layer.CreateResources(stage.Dc, stage.DWrite, new System.Numerics.Vector2(width, height), scale);
        foreach (var (label, time) in song.Moments)
        {
            // A few seconds leading up to the moment (from where the song was joined), as the app would draw them.
            var from = Math.Max(song.Joined, time - 3);
            for (var t = from; t < time; t += 1.0 / 30) stage.Render(layers, song.Scene, t, 10 + t, scale, song.Joined);
            stage.Render(layers, song.Scene, time, 10 + time, scale, song.Joined);
            var path = Path.Combine(output, $"{song.Name}-{label}.png");
            stage.SavePng(path);
            Console.WriteLine(path);
        }
    }
    return;
}

foreach (var factory in factories)
{
    if (only is not null && factory.Id != only) continue;
    if (bench)
    {
        Bench(stage, factory, new SceneSnapshot(), 9.5, 12, scale);
        Bench(stage, factory, Fixtures.NoLyrics(), 9.5, 12, scale);
        Bench(stage, factory, Fixtures.English(style), 9.5, 24, scale);
        Bench(stage, factory, Fixtures.Japanese(style), 9.5, 17, scale);
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
            if (reduce) scene = scene with { ReduceMotion = true };
            if (extras.Count > 0) scene = scene with { Style = StyleExtras.Apply(scene.Style, extras) };
            scenes[name] = scene;
        }
        // A few frames leading up to the shot, as the app would draw them.
        for (var t = time - 0.2; t < time; t += 1.0 / 60) stage.Render(layer, scene, t, 10 + t, scale, background);
        stage.Render(layer, scene, time, 10 + time, scale, background);
        var path = Path.Combine(output, $"{factory.Id}{(reveal is null ? "" : "-" + reveal)}{(reduce ? "-reduced" : "")}{(tag is null ? "" : "-" + tag)}-{name}-{time.ToString("00.00", CultureInfo.InvariantCulture)}.png");
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
