using System.Numerics;
using Lunalyr.Core.Visualisers;
using Lunalyr.Render.Visualisers;
using L = Lunalyr.Core.Visualisers.StarTrailsLayout;

namespace Lunalyr.Render.Tests.Visualisers;

/// <summary>Star Trails' shader drawn from simulation states (WARP), with the Mac's inputs.</summary>
public sealed class StarTrailsRenderTests : IDisposable
{
    private readonly VisualiserStill _still = new();

    public void Dispose() => _still.Dispose();

    private static Vector4[] State(Action<StarTrailsSound, int, Action<StarTrailsSound>> configure, double? level = null)
    {
        var simulation = new StarTrailsSimulation { FreezeSky = 5 };
        var sound = new StarTrailsSound();
        if (level is { } l) sound.Level = l;
        for (var i = 0; i < 45; i++)
        {
            configure(sound, i, s => sound = s);
            simulation.Advance(1.0 / 30, 1.0 / 30, sound);
        }
        return simulation.Pack(16.0 / 9);
    }

    // Ported from StarTrailsTests.swift: testSoundTypesLightDifferentStars
    [Fact]
    public void SoundTypesLightDifferentStars()
    {
        const int width = 1280, height = 720;
        var silence = State((_, _, _) => { });
        // The craft, trails and asteroids of silence, so only the stars (and clouds) differ.
        Vector4[] Isolated(Vector4[] s)
        {
            for (var i = L.Craft; i < L.Onset; i++) s[i] = silence[i];
            for (var i = L.Asteroid; i < L.Float4Count; i++) s[i] = silence[i];
            return s;
        }
        Vector4[] Hit(Func<StarTrailsSound, StarTrailsSound> fire, Func<StarTrailsSound, StarTrailsSound> decay, int[] frames) =>
            Isolated(State((sound, i, set) => set(frames.Contains(i) ? fire(sound) : decay(sound))));
        (string Name, Vector4[] State)[] cases =
        [
            ("kick", Hit(s => s with { KickOnsets = s.KickOnsets + 1, Kick = 0.9 }, s => s with { Kick = s.Kick * 0.8 }, [38])),
            ("snare", Hit(s => s with { SnareOnsets = s.SnareOnsets + 1, Snare = 0.9 }, s => s with { Snare = s.Snare * 0.8 }, [38])),
            ("hat", Hit(s => s with { HatOnsets = s.HatOnsets + 1, Hat = 0.9 }, s => s with { Hat = s.Hat * 0.8 }, [36, 39, 42])),
            ("bass", Isolated(State((s, _, set) => set(s with { Bass = 0.85 })))),
            ("vocal", Isolated(State((s, _, set) => set(s with { Vocal = 0.85 })))),
            ("pad", Isolated(State((s, _, set) => set(s with { Pad = 0.85 })))),
        ];
        var quiet = _still.RenderStarTrails(silence, width, height);
        var lit = new Dictionary<string, HashSet<int>>();
        var hues = new Dictionary<string, (double Hue, double Saturation)>();
        foreach (var (name, state) in cases)
        {
            var image = _still.RenderStarTrails(state, width, height);
            double ar = 0, ag = 0, ab = 0;
            var pixels = new HashSet<int>();
            for (var p = 0; p < image.Length; p += 4)
            {
                double dr = image[p + 2] - quiet[p + 2], dg = image[p + 1] - quiet[p + 1], db = image[p] - quiet[p];
                ar += Math.Max(0, dr); ag += Math.Max(0, dg); ab += Math.Max(0, db);
                if (Math.Max(dr, Math.Max(dg, db)) > 60) pixels.Add(p / 4);
            }
            lit[name] = pixels;
            double high = Math.Max(ar, Math.Max(ag, ab)), low = Math.Min(ar, Math.Min(ag, ab)), hue = 0;
            if (high > low)
            {
                if (high == ar) hue = 60 * ((ag - ab) / (high - low));
                else if (high == ag) hue = 60 * ((ab - ar) / (high - low) + 2);
                else hue = 60 * ((ar - ag) / (high - low) + 4);
            }
            hues[name] = ((hue + 360) % 360, high > 0 ? (high - low) / high : 0);
        }
        var report = string.Join("; ", cases.Select(c => $"{c.Name}: {lit[c.Name].Count} px, hue {(int)hues[c.Name].Hue}, sat {hues[c.Name].Saturation:0.00}"));
        foreach (var name in new[] { "kick", "snare", "hat", "bass", "vocal", "pad" })
            Assert.True(lit[name].Count > 20, $"{name} lights stars: {report}");
        // Pad light includes the pink the pad adds to the clouds, so it sits a little towards magenta.
        var ranges = new Dictionary<string, (double Low, double High)>
        {
            ["kick"] = (15, 55), ["snare"] = (170, 210), ["bass"] = (210, 250), ["vocal"] = (310, 350), ["pad"] = (255, 300),
        };
        foreach (var (name, range) in ranges)
            Assert.True(hues[name].Hue >= range.Low && hues[name].Hue <= range.High, $"{name} colour: {report}");
        Assert.True(hues["hat"].Saturation < 0.3, $"hats glitter silver: {report}");
        var names = cases.Select(c => c.Name).ToArray();
        for (var i = 0; i < names.Length; i++)
            for (var j = i + 1; j < names.Length; j++)
            {
                var a = lit[names[i]];
                var b = lit[names[j]];
                var jaccard = a.Intersect(b).Count() / (double)Math.Max(1, a.Union(b).Count());
                Assert.True(jaccard < 0.05, $"{names[i]} and {names[j]} light different stars: {report}");
            }
        // Every kick lights a fresh set of stars.
        var next = Isolated(State((s, i, set) => set(i is 20 or 38 ? s with { KickOnsets = s.KickOnsets + 1, Kick = 0.9 } : s with { Kick = s.Kick * 0.8 })));
        var second = new HashSet<int>();
        var nextImage = _still.RenderStarTrails(next, width, height);
        for (var p = 0; p < nextImage.Length; p += 4)
            if (Math.Max(nextImage[p] - quiet[p], Math.Max(nextImage[p + 1] - quiet[p + 1], nextImage[p + 2] - quiet[p + 2])) > 60) second.Add(p / 4);
        var overlap = second.Intersect(lit["kick"]).Count() / (double)Math.Max(1, second.Union(lit["kick"]).Count());
        Assert.True(overlap < 0.3, $"consecutive kicks light different stars: {report}");
    }

    // Ported from StarTrailsTests.swift: testKickBrightensTheTrailAndCraft
    [Fact]
    public void KickBrightensTheTrailAndCraft()
    {
        const int w = 640, h = 360;
        Vector4[] Run(int? kickAt) => State((s, i, set) =>
            set(i == kickAt ? s with { KickOnsets = s.KickOnsets + 1, Kick = 0.9 } : s with { Kick = s.Kick * 0.8 }), level: 0.4);
        var quiet = Run(null);
        var kick = Run(41);
        for (var i = L.Onset; i < L.Trail; i++) kick[i] = quiet[i]; // no star flares or asteroid rims: only the craft and trails differ
        for (var c = 0; c < 2; c++) Assert.Equal(quiet[L.Craft + 2 * c], kick[L.Craft + 2 * c]); // A kick does not move a craft
        static double[] Luma(byte[] image)
        {
            var v = new double[image.Length / 4];
            for (var i = 0; i < v.Length; i++) v[i] = 0.2126 * image[i * 4 + 2] + 0.7152 * image[i * 4 + 1] + 0.0722 * image[i * 4];
            return v;
        }
        var a = Luma(_still.RenderStarTrails(quiet, w, h));
        var b = Luma(_still.RenderStarTrails(kick, w, h));
        double baseline = 0, added = 0;
        for (var c = 0; c < 2; c++)
        {
            var craft = quiet[L.Craft + 2 * c];
            double cx = craft.X * (double)h, cy = (1 - craft.Y) * (double)h, r = 0.1 * h;
            for (var y = Math.Max(0, (int)(cy - r)); y < Math.Min(h, (int)(cy + r) + 1); y++)
                for (var x = Math.Max(0, (int)(cx - r)); x < Math.Min(w, (int)(cx + r) + 1); x++)
                    if (Math.Sqrt(Math.Pow(x + 0.5 - cx, 2) + Math.Pow(y + 0.5 - cy, 2)) < r)
                    {
                        baseline += a[y * w + x];
                        added += b[y * w + x] - a[y * w + x];
                    }
        }
        Assert.True(added / Math.Max(baseline, 1) > 0.15, $"A kick brightens the craft and their trails by {(int)(100 * added / Math.Max(baseline, 1))} %");
    }
}
