using System.Numerics;
using Lunalyr.Core.Colour;

namespace Lunalyr.Core.Tests.Colour;

/// <summary>
/// The Mac's LyricColourTests, case for case where they test colour maths. Not ported (they render SwiftUI views or use
/// the UserDefaults store): testSungSweepTravelsFromWhiteAcrossAWholeLine, testInkReachesEveryCompositionAndTheInterludeDots
/// (both draw lyric views; the compositions' own tests will cover them) and the store half of
/// testColourSettingsPersistInIsolatedDefaults (the settings workstream owns persistence; its pure rules are below).
/// </summary>
public class LyricColourTests
{
    private static readonly Vector3[] Vivid =
        [new(0.85f, 0.15f, 0.2f), new(0.1f, 0.3f, 0.9f), new(0.2f, 0.2f, 0.25f), new(0.95f, 0.75f, 0.1f), new(0.1f, 0.7f, 0.4f)];

    private static readonly Vector3[] Pastel =
        [new(0.95f, 0.9f, 0.85f), new(0.9f, 0.95f, 1), new(0.85f, 0.85f, 0.85f), new(1, 0.92f, 0.95f), new(0.92f, 1, 0.92f)];

    private static readonly Vector3[] Dark =
        [new(0.05f, 0.05f, 0.08f), new(0.1f, 0.08f, 0.12f), new(0.02f, 0.02f, 0.03f), new(0.12f, 0.1f, 0.1f), new(0.06f, 0.08f, 0.06f)];

    private static double Luminance(SrgbColour colour) => Perceptual.Luminance(colour.Rgb);

    private static OkLab Lab(SrgbColour colour) => Perceptual.ToOkLab(colour.Rgb);

    // Ported from LyricColourTests.swift: testOKLabRoundTripsAndAnchorsWhiteAndBlack
    [Fact]
    public void OKLabRoundTripsAndAnchorsWhiteAndBlack()
    {
        var white = Perceptual.ToOkLab(new Double3(1, 1, 1));
        Assert.Equal(1, white.L, 1e-4);
        Assert.Equal(0, white.Chroma, 1e-4);
        Assert.Equal(0, Perceptual.ToOkLab(Double3.Zero).L, 1e-9);
        var random = new Random(20261112);
        for (var i = 0; i < 500; i++)
        {
            var colour = new Double3(random.NextDouble(), random.NextDouble(), random.NextDouble());
            var back = Perceptual.Srgb(Perceptual.ToOkLab(colour));
            Assert.Equal(colour.X, back.X, 1e-5);
            Assert.Equal(colour.Y, back.Y, 1e-5);
            Assert.Equal(colour.Z, back.Z, 1e-5);
        }
    }

    // Ported from LyricColourTests.swift: testBlendsArePerceptualNotRGB
    [Fact]
    public void BlendsArePerceptualNotRGB()
    {
        // Half way from black to white is perceptual mid-grey (L 0.5); an sRGB blend would land noticeably lighter.
        var middle = Perceptual.Mix(new OkLab(0, 0, 0), OkLab.White, 0.5);
        Assert.Equal(0.5, middle.L, 1e-12);
        Assert.True(Perceptual.ToOkLab(new Double3(0.5, 0.5, 0.5)).L - middle.L > 0.05);
        // A blue-to-yellow drift keeps an even lightness ramp instead of dipping.
        var blue = Perceptual.ToOkLab(new Double3(0.1, 0.2, 0.9));
        var yellow = Perceptual.ToOkLab(new Double3(0.95, 0.85, 0.1));
        var steps = Enumerable.Range(0, 11).Select(i => Perceptual.Mix(blue, yellow, i / 10.0).L).ToArray();
        for (var i = 0; i + 1 < steps.Length; i++) Assert.Equal((yellow.L - blue.L) / 10, steps[i + 1] - steps[i], 1e-9);
    }

    // Ported from LyricColourTests.swift: testGamutClipKeepsLightnessAndHue
    [Fact]
    public void GamutClipKeepsLightnessAndHue()
    {
        var neon = OkLab.FromLch(0.86, 0.3, 2.5);
        Assert.False(Perceptual.InGamut(neon));
        var fitted = Perceptual.Clipped(neon);
        Assert.True(Perceptual.InGamut(fitted));
        Assert.Equal(neon.L, fitted.L, 1e-9);
        Assert.Equal(neon.Hue, fitted.Hue, 1e-6);
        Assert.True(fitted.Chroma < neon.Chroma);
    }

    // Ported from LyricColourTests.swift: testEveryModeStaysReadableOnEveryBackdrop
    [Fact]
    public void EveryModeStaysReadableOnEveryBackdrop()
    {
        var custom = new[] { "#0A1A5C", "#5C0A0A", "#F2C94C", "#1F8A3A" }.Select(ColourHex.ToOkLab).OfType<OkLab>().ToArray();
        foreach (var mode in LyricColourModes.All)
        foreach (var album in new[] { Vivid, Pastel, Dark })
        foreach (var backdrop in new[] { 0.0, 0.04, 0.1, 0.18, 0.3, 0.45 })
        {
            var director = new LyricInkDirector();
            for (var frame = 0; frame < 240; frame++)
            {
                var audio = new AudioLevels(frame % 20 < 10 ? 0.4 : 0.05, 0.3, 0.2, frame % 30 < 15 ? 0.4 : 0, 0);
                var ink = director.Ink(new LyricInkInputs(mode) { Custom = custom, Album = album, Backdrop = backdrop, Audio = audio },
                                       frame / 60.0);
                if (!(frame % 8 == 0 || frame > 230)) continue;
                for (var slot = 0.0; slot <= 1.0; slot += 0.25)
                {
                    var sample = ink.Sample(slot, (ulong)frame);
                    foreach (var colour in new[] { sample.Rest, sample.Sung })
                    {
                        var y = Luminance(colour);
                        var ratio = Perceptual.Contrast(y, ink.Backdrop);
                        // Where 3:1 would need white, a colour keeps its tint down to 2.5:1; beyond that the text is as
                        // light as it can be.
                        Assert.True(ratio >= Perceptual.TintedContrast - 0.02 || y > 0.9, $"{mode} on {backdrop}: {ratio}");
                    }
                }
            }
        }
    }

    // Ported from LyricColourTests.swift: testColourNeverFlashesEvenOnPulsingAudio
    [Fact]
    public void ColourNeverFlashesEvenOnPulsingAudio()
    {
        foreach (var mode in new[] { LyricColourMode.MusicReactive, LyricColourMode.AlbumDrift, LyricColourMode.SungSweep })
        {
            var director = new LyricInkDirector();
            var lightness = new List<double>();
            var luminances = new List<double>();
            for (var frame = 0; frame < 600; frame++)
            {
                var wall = frame / 60.0;
                // An 8 Hz full-scale pulse, and the album swapping five times a second.
                var on = frame % 8 < 4;
                var audio = new AudioLevels(on ? 1 : 0, on ? 1 : 0, 0.5, on ? 0 : 1, on ? 1 : 0);
                var ink = director.Ink(new LyricInkInputs(mode) { Album = frame % 24 < 12 ? Vivid : Dark, Backdrop = 0.05, Audio = audio }, wall);
                var colour = mode == LyricColourMode.SungSweep ? ink.Base.Sung : ink.Sample(0.3).Rest;
                lightness.Add(Lab(colour).L);
                luminances.Add(Luminance(colour));
            }
            // Never faster than the lightness rate limit (plus quantisation).
            for (var i = 0; i + 1 < lightness.Count; i++)
                Assert.True(Math.Abs(lightness[i + 1] - lightness[i]) <= LyricInkDirector.LightnessRate / 60 + 0.004, $"{mode}");
            // WCAG general flash: a pair of opposing changes in relative luminance of 10% or more.
            var extremes = new List<(double Time, double Value)> { (0, luminances[0]) };
            bool? rising = null;
            for (var index = 0; index + 1 < luminances.Count; index++)
            {
                double a = luminances[index], b = luminances[index + 1];
                if (b == a) continue;
                var up = b > a;
                if (rising is { } was && was != up) extremes.Add((index / 60.0, a));
                rising = up;
            }
            extremes.Add((luminances.Count / 60.0, luminances[^1]));
            var swings = extremes.Zip(extremes.Skip(1)).Where(p => Math.Abs(p.Second.Value - p.First.Value) >= 0.1).ToList();
            var flashes = swings.Zip(swings.Skip(1)).Count(p => p.Second.Second.Time - p.First.First.Time <= 1);
            Assert.True(flashes == 0, $"{mode} produced a flash");
            Assert.True(swings.Count <= 1, $"{mode}: only the opening fade may be large");
        }
    }

    // Ported from LyricColourTests.swift: testThirtyAndSixtyFramesPerSecondColoursAgree
    [Fact]
    public void ThirtyAndSixtyFramesPerSecondColoursAgree()
    {
        LyricInkDirector thirty = new(), sixty = new();
        for (var frame = 0; frame <= 600; frame++)
        {
            var wall = frame / 60.0;
            var audio = new AudioLevels(0.3 + 0.2 * Math.Sin(wall * 2), 0.3 + 0.2 * Math.Sin(wall), 0.2, 0.2 + 0.2 * Math.Cos(wall * 1.3), 0);
            var mode = wall < 5 ? LyricColourMode.AlbumDrift : LyricColourMode.MusicReactive;
            var fast = sixty.Ink(new LyricInkInputs(mode) { Album = Vivid, Backdrop = 0.06, Audio = audio }, wall);
            if (frame % 2 != 0) continue;
            var slow = thirty.Ink(new LyricInkInputs(mode) { Album = Vivid, Backdrop = 0.06, Audio = audio }, wall);
            OkLab a = slow.Gradient(0.3), b = fast.Gradient(0.3);
            Assert.Equal(a.L, b.L, 0.012);
            Assert.Equal(a.A, b.A, 0.012);
            Assert.Equal(a.B, b.B, 0.012);
        }
    }

    // Ported from LyricColourTests.swift: testReduceMotionHoldsColourNearlyStill
    [Fact]
    public void ReduceMotionHoldsColourNearlyStill()
    {
        static double Change(LyricColourMode mode, bool calm)
        {
            var director = new LyricInkDirector();
            OkLab? start = null;
            var end = OkLab.White;
            for (var frame = 0; frame <= 960; frame++)
            {
                var wall = frame / 60.0;
                var audio = new AudioLevels(frame % 30 < 15 ? 0.6 : 0, frame % 40 < 20 ? 0.6 : 0, 0, frame % 40 < 20 ? 0 : 0.6, 0);
                var ink = director.Ink(new LyricInkInputs(mode) { Album = Vivid, Backdrop = 0.05, Audio = audio, ReduceMotion = calm }, wall);
                // Measured once the opening fade from white has settled.
                if (frame == 360) start = ink.Gradient(0.5);
                end = ink.Gradient(0.5);
            }
            var first = start ?? OkLab.White;
            return Math.Sqrt((end.L - first.L) * (end.L - first.L) + (end.A - first.A) * (end.A - first.A)
                             + (end.B - first.B) * (end.B - first.B));
        }
        Assert.True(Change(LyricColourMode.AlbumDrift, true) < 0.03);
        Assert.True(Change(LyricColourMode.AlbumDrift, false) > 0.03);
        Assert.True(Change(LyricColourMode.MusicReactive, true) < 0.01);
    }

    // Ported from LyricColourTests.swift: testSwitchingModeFadesInFromWhite
    [Fact]
    public void SwitchingModeFadesInFromWhite()
    {
        var director = new LyricInkDirector();
        for (var frame = 0; frame < 30; frame++)
            Assert.Equal(LyricColourMode.Standard, director.Ink(new LyricInkInputs(LyricColourMode.Standard) { Album = Vivid }, frame / 60.0).Mode);
        var previous = 1.0;
        for (var frame = 30; frame < 150; frame++)
        {
            var ink = director.Ink(new LyricInkInputs(LyricColourMode.SungSweep) { Album = Vivid, Backdrop = 0.05 }, frame / 60.0);
            var colour = Lab(ink.Base.Sung);
            if (frame == 30) Assert.True(colour.L > 0.97, "starts at white");
            Assert.True(Math.Abs(colour.L - previous) <= LyricInkDirector.LightnessRate / 60 + 0.004);
            previous = colour.L;
        }
        var arrived = director.Ink(new LyricInkInputs(LyricColourMode.SungSweep) { Album = Vivid, Backdrop = 0.05 }, 3);
        Assert.True(Lab(arrived.Base.Sung).Chroma > 0.05, "arrives at the accent");
    }

    // Ported from LyricColourTests.swift: testColourSettingsPersistInIsolatedDefaults (the store-free rules only)
    [Fact]
    public void ColourSettingsPersistInIsolatedDefaults()
    {
        Assert.Equal(LyricColourMode.AlbumDrift, LyricColourModes.DefaultMode);
        Assert.DoesNotContain(LyricColourMode.Standard, LyricColourModes.Choices);
        Assert.Equal(LyricColourMode.AlbumDrift, LyricColourModes.SavedChoice("static"));
        Assert.Equal(LyricColourMode.CustomGradient, LyricColourModes.SavedChoice("customGradient"));
        Assert.Null(LyricColourModes.SavedChoice("sepia"));
        // Repairs: invalid colours dropped, at least two, at most four, strength clamped, NaN back to 1.
        Assert.Equal(2, LyricColourOptions.Sanitised(["nope", "#12345"]).Count);
        Assert.Equal(4, LyricColourOptions.Sanitised(["#ff0000", "#00ff00", "#0000ff", "#ffffff", "#000000"]).Count);
        Assert.Equal("#FF0000", LyricColourOptions.Sanitised(["#ff0000"])[0]);
        Assert.Equal(0, LyricColourOptions.ClampedStrength(-1));
        Assert.Equal(1, LyricColourOptions.ClampedStrength(double.NaN));
        Assert.Equal(1.6, LyricColourOptions.ClampedStrength(1.6));
        Assert.Equal("#FF0000", ColourHex.ToHex(new SrgbColour(1, 0, 0)));
    }

    // Ported from LyricColourTests.swift: testStrengthChangesEveryPastelModeInThePreview
    [Fact]
    public void StrengthChangesEveryPastelModeInThePreview()
    {
        foreach (var mode in LyricColourModes.Choices)
        {
            var preview = new LyricInkPreview();
            var inputs = new LyricInkInputs(mode)
            {
                Strength = 0,
                Custom = LyricColourOptions.DefaultCustom.Select(ColourHex.ToOkLab).OfType<OkLab>().ToArray(),
                Vocal = 0.6,
            };
            static double Chroma(LyricInk ink) => new[] { 0.2, 0.5, 0.8 }.Max(slot => Lab(ink.Sample(slot).Sung).Chroma);
            var neutral = Chroma(preview.Ink(inputs, 0));
            var soft = Chroma(preview.Ink(inputs with { Strength = 0.45 }, 0));
            var vivid = Chroma(preview.Ink(inputs with { Strength = 1.6 }, 0));
            Assert.True(neutral < 0.005, mode.Title());
            Assert.True(soft > neutral + 0.015, mode.Title());
            Assert.True(vivid > soft + 0.025, mode.Title());
        }
    }

    // Ported from LyricColourTests.swift: testPastelModesKeepColourOverBrightWallpaperWithAutomaticBacking
    [Fact]
    public void PastelModesKeepColourOverBrightWallpaperWithAutomaticBacking()
    {
        var backdrop = LyricBackdrop.Luminance(WallpaperBackgroundMode.Desktop, false, [], 1, measured: 0.90, pastelText: true);
        Vector3[] neutral = [new(0.9f), new(0.2f)];
        var custom = new[] { "#F18C97", "#8E7DE4", "#50AFC8" }.Select(ColourHex.ToOkLab).OfType<OkLab>().ToArray();
        foreach (var mode in LyricColourModes.Choices)
        {
            var director = new LyricInkDirector();
            var ink = LyricInk.White;
            for (var frame = 0; frame < 160; frame++)
                ink = director.Ink(new LyricInkInputs(mode) { Custom = custom, Album = neutral, Backdrop = backdrop, Vocal = 0.6 }, frame / 30.0);
            var sample = ink.Sample(0.5);
            foreach (var colour in new[] { sample.Rest, sample.Sung })
                Assert.True(Perceptual.Contrast(Luminance(colour), backdrop) >= 2.99, $"{mode}");
            var rest = Lab(sample.Rest);
            var sung = Lab(sample.Sung);
            Assert.True(sung.Chroma > 0.06, $"{mode} washed out on a bright wallpaper");
            if (mode == LyricColourMode.SungSweep)
            {
                Assert.True(rest.Chroma < 0.005);
                Assert.True(rest.L > 0.99);
            }
            else
            {
                Assert.True(rest.Chroma > 0.06);
            }
        }
    }

    // Ported from LyricColourTests.swift: testWhiteCoverUsesAnAccentAndWashedOutSweepUsesAnUnderline
    [Fact]
    public void WhiteCoverUsesAnAccentAndWashedOutSweepUsesAnUnderline()
    {
        static LyricInkSample Sweep(double backdrop, double strength = 1)
        {
            var director = new LyricInkDirector();
            var inputs = new LyricInkInputs(LyricColourMode.SungSweep)
            {
                Strength = strength, Album = [new Vector3(1)], Backdrop = backdrop, IncreaseContrast = true,
            };
            var ink = LyricInk.White;
            for (var step = 0; step <= 30; step++) ink = director.Ink(inputs, step * 0.25);
            return ink.Base;
        }

        var accent = Sweep(0.03);
        Assert.True(Lab(accent.Sung).Chroma > 0.08);
        Assert.Null(accent.SweepUnderline);
        var washedOut = Sweep(0.62);
        Assert.True(Lab(washedOut.Sung).L > 0.98);
        // No underline under sung words, even when the sung colour is near white.
        Assert.Null(washedOut.SweepUnderline);
        Assert.Null(Sweep(0.62, 0).SweepUnderline);
    }

    // Ported from LyricColourTests.swift: testSingleHueAlbumDriftMovesThroughShadesOfTheCoverHue
    [Fact]
    public void SingleHueAlbumDriftMovesThroughShadesOfTheCoverHue()
    {
        var director = new LyricInkDirector();
        Vector3[] album = [new(0.13f, 0.26f, 0.8f)];
        var inputs = new LyricInkInputs(LyricColourMode.AlbumDrift) { Album = album };
        var samples = new List<OkLab>();
        for (var step = 0; step <= 240; step++)
        {
            var ink = director.Ink(inputs, step * 0.25);
            if (step >= 24) samples.Add(ink.Gradient(0.5));
        }
        Assert.True(samples.Max(s => s.L) - samples.Min(s => s.L) > 0.08);
        var hue = Perceptual.ToOkLab(Double3.From(album[0])).Hue;
        Assert.All(samples, s => Assert.True(LyricHighlight.HueDistance(s.Hue, hue) < 0.03));
    }

    // Ported from LyricColourTests.swift: testAutomaticLyricsUseEvenAFaintCoverHueAmongBlackAndWhite
    [Fact]
    public void AutomaticLyricsUseEvenAFaintCoverHueAmongBlackAndWhite()
    {
        var tint = new Vector3(0.53f, 0.49f, 0.51f);
        var coverHue = Perceptual.ToOkLab(Double3.From(tint)).Hue;
        Vector3[] album = [Vector3.Zero, tint, new Vector3(1)];
        foreach (var mode in new[] { LyricColourMode.AlbumDrift, LyricColourMode.SungSweep, LyricColourMode.MusicReactive })
        {
            var director = new LyricInkDirector();
            var ink = LyricInk.White;
            for (var step = 0; step <= 32; step++)
            {
                var audio = new AudioLevels(0.3, 0.4, 0.2, 0.1, 0);
                ink = director.Ink(new LyricInkInputs(mode) { Album = album, Backdrop = 0.1, Audio = audio }, step / 4.0);
            }
            var colour = Lab(ink.Sample(0.5).Sung);
            Assert.True(LyricHighlight.HueDistance(colour.Hue, coverHue) < 0.04, mode.Title());
            Assert.True(colour.Chroma > 0.06, mode.Title());
            Assert.True(Perceptual.Contrast(Perceptual.Luminance(colour), 0.1) >= 2.99);
        }
    }

    // The Mac's next/tester-fixes (15 Nov 2026): Sung sweep varies from word to word along the drifting palette (your
    // colours, else the cover's), unsung words stay white, and the sung colours stay readable.
    [Fact]
    public void SungSweepGivesEachWordItsOwnColour()
    {
        const double backdrop = 0.14;
        OkLab[] custom = [ColourHex.ToOkLab("#F28FAD")!.Value, ColourHex.ToOkLab("#9AD0F5")!.Value];
        var director = new LyricInkDirector();
        var ink = LyricInk.White;
        for (var frame = 0; frame <= 32; frame++)
            ink = director.Ink(new LyricInkInputs(LyricColourMode.SungSweep) { Custom = custom, Backdrop = backdrop }, frame / 4.0);
        var first = ink.Sample(0);
        var later = ink.Sample(0.6);
        Assert.True(first.Sweeps && later.Sweeps);
        Assert.True(Luminance(first.Rest) > 0.99);
        Assert.True(LyricHighlight.HueDistance(Lab(first.Sung).Hue, Lab(later.Sung).Hue) > Math.PI / 12);
        foreach (var sample in new[] { first, later })
            Assert.True(Perceptual.Contrast(Luminance(sample.Sung), backdrop) >= 2.49);
    }

    // Ported from LyricColourTests.swift: testBackdropEstimateFollowsTheWallpaperAndScrim
    [Fact]
    public void BackdropEstimateFollowsTheWallpaperAndScrim()
    {
        var light = LyricBackdrop.BeforeBacking(WallpaperBackgroundMode.Pattern, false, Pastel, 0);
        var darkened = LyricBackdrop.BeforeBacking(WallpaperBackgroundMode.Pattern, false, Pastel, 2);
        Assert.True(darkened < light);
        Assert.True(light > LyricBackdrop.BeforeBacking(WallpaperBackgroundMode.Pattern, false, Dark, 0));
        // With the backing turned off by hand, that is what the lyrics sit on; the automatic backing only darkens it.
        Assert.Equal(light, LyricBackdrop.Luminance(WallpaperBackgroundMode.Pattern, false, Pastel, 0, backing: 0), 1e-12);
        Assert.True(LyricBackdrop.Luminance(WallpaperBackgroundMode.Pattern, false, Pastel, 0) <= light);
    }

    // Ported from LyricColourTests.swift: testColoursReachTheFloorAtTheEndsOfTheStageToo
    [Fact]
    public void ColoursReachTheFloorAtTheEndsOfTheStageToo()
    {
        const double side = 0.5 / 0.65;
        const double share = 0.9 + (0.45 - 0.9) * (side - 0.55) / (0.8 - 0.55);
        Assert.True(LyricBackdrop.EdgeBacking <= share);
        (WallpaperBackgroundMode Mode, Vector3[] Palette, double? Measured)[] cases =
        [
            (WallpaperBackgroundMode.Desktop, Dark, null), (WallpaperBackgroundMode.Desktop, Dark, 0.2),
            (WallpaperBackgroundMode.Custom, Pastel, null), (WallpaperBackgroundMode.Custom, Vivid, 0.25),
            (WallpaperBackgroundMode.Pattern, Pastel, null), (WallpaperBackgroundMode.Pattern, Vivid, null),
            (WallpaperBackgroundMode.Visualizer, Pastel, null),
        ];
        foreach (var (mode, palette, measured) in cases)
        {
            var before = LyricBackdrop.BeforeBacking(mode, false, palette, 1, measured, tinted: true);
            var backing = LyricLegibility.Backing(before, 0, 4.5);
            var middle = LyricLegibility.Darkened(before, backing);
            var ends = LyricLegibility.Darkened(before, backing * share);
            var estimate = LyricBackdrop.Luminance(mode, false, palette, 1, measured, tinted: true);
            if (LyricBackdrop.ForcesWhite(estimate)) continue;
            for (var hue = 0.0; hue < 6.28; hue += 0.5)
            {
                var colour = Perceptual.Readable(OkLab.FromLch(0.6, 0.15, hue), estimate);
                var lum = Perceptual.Luminance(colour);
                Assert.True(Perceptual.Contrast(lum, ends) >= LyricInk.MinimumContrast - 0.01, $"{mode} {measured ?? -1} hue {hue}");
                Assert.True(Perceptual.Contrast(lum, middle) >= LyricInk.MinimumContrast - 0.01);
            }
        }
    }

    // Ported from LyricColourTests.swift: testAnImportedPictureCountsTheAlbumTintLaidOverIt
    [Fact]
    public void AnImportedPictureCountsTheAlbumTintLaidOverIt()
    {
        var bare = LyricBackdrop.BeforeBacking(WallpaperBackgroundMode.Custom, false, Pastel, 1, 0.03);
        var tinted = LyricBackdrop.BeforeBacking(WallpaperBackgroundMode.Custom, false, Pastel, 1, 0.03, tinted: true);
        Assert.True(tinted > bare * 2);
        var lums = Pastel.Select(p => Perceptual.Luminance(Double3.From(p))).ToArray();
        var field = 0.35 * lums.Max() + 0.65 * lums.Sum() / lums.Length;
        var modelled = LyricLegibility.Darkened(LyricLegibility.Linear(0.82 * LyricLegibility.Encoded(0.03)
            + 0.18 * LyricLegibility.Encoded(field)), 0.12);
        Assert.Equal(modelled, tinted, 1e-9);
        // A dark cover over a dark photo changes little; with no cover there is no tint.
        var darkTint = LyricBackdrop.BeforeBacking(WallpaperBackgroundMode.Custom, false, Dark, 1, 0.03, tinted: true);
        Assert.Equal(bare, darkTint, 0.01);
        Assert.Equal(bare, LyricBackdrop.BeforeBacking(WallpaperBackgroundMode.Custom, false, [], 1, 0.03, tinted: true), 1e-12);
        // Your wallpaper has nothing laid over it.
        Assert.Equal(0.03, LyricBackdrop.BeforeBacking(WallpaperBackgroundMode.Desktop, false, Pastel, 1, 0.03, tinted: true), 1e-12);
    }

    // Ported from LyricColourTests.swift: testStaticWhiteHighlightsAreMadeReadableToo
    [Fact]
    public void StaticWhiteHighlightsAreMadeReadableToo()
    {
        // A pale yellow cover accent: Cascade's held words used it raw, and Cutout's labels set white type on it.
        var pale = Perceptual.ToOkLab(new Double3(0.96, 0.92, 0.55));
        foreach (var backdrop in new[] { 0.04, 0.18, 0.3 })
        {
            var director = new LyricInkDirector();
            var ink = director.Ink(new LyricInkInputs(LyricColourMode.Standard) { Backdrop = backdrop, Accent = pale }, 0);
            Assert.Equal(LyricColourMode.Standard, ink.Mode);
            var held = Luminance(Assert.NotNull(ink.Accent));
            Assert.True(Perceptual.Contrast(held, backdrop) >= LyricInk.MinimumContrast - 0.02 || held > 0.9, $"held words on {backdrop}");
            var tile = Luminance(Assert.NotNull(ink.Tile));
            Assert.True(Perceptual.Contrast(Luminance(ink.OnTile), tile) >= LyricInk.MinimumContrast - 0.02);
            // Asked again with nothing changed, the same ink comes back.
            var again = director.Ink(new LyricInkInputs(LyricColourMode.Standard) { Backdrop = backdrop, Accent = pale }, 1);
            Assert.Equal(tile, Luminance(Assert.NotNull(again.Tile)), 1e-9);
        }
        // The type is still plain white without an accent or a cover; the highlight has a legible colour (vermilion).
        var bare = new LyricInkDirector().Ink(new LyricInkInputs(LyricColourMode.Standard), 0);
        Assert.Equal(LyricColourMode.Standard, bare.Mode);
        Assert.Equal(LyricInkSample.White, bare.Base);
        var bareTile = Luminance(Assert.NotNull(bare.Tile));
        Assert.True(Perceptual.Contrast(Luminance(bare.OnTile), bareTile) >= LyricHighlight.TextContrast - 0.02);
    }

    // Ported from LyricColourTests.swift: testColourModesShowOverYourWallpaperAndImportedPictures
    [Fact]
    public void ColourModesShowOverYourWallpaperAndImportedPictures()
    {
        var unknown = LyricBackdrop.Luminance(WallpaperBackgroundMode.Desktop, false, Dark, 1);
        Assert.True(unknown < LyricLegibility.UnknownBackdropLuminance);
        Assert.False(LyricBackdrop.ForcesWhite(unknown));
        Assert.True(LyricBackdrop.ForcesWhite(0.35));
        Assert.Equal(0.03, LyricBackdrop.Luminance(WallpaperBackgroundMode.Desktop, false, Pastel, 1, measured: 0.03), 1e-12);
        var photo = LyricBackdrop.Luminance(WallpaperBackgroundMode.Custom, false, Pastel, 1, measured: 0.03);
        Assert.True(photo < 0.03);
        Assert.Equal(0.5, LyricBackdrop.Luminance(WallpaperBackgroundMode.Desktop, false, Dark, 1, measured: 0.5, backing: 0), 1e-12);
        var bright = LyricBackdrop.Luminance(WallpaperBackgroundMode.Desktop, false, Dark, 1, measured: 0.9);
        Assert.True(bright < 0.9);
        // Album drift from a red cover over that dark photo stays red rather than washing out to a pale pink.
        Vector3[] red = [new(0.78f, 0.08f, 0.10f), new(0.55f, 0.04f, 0.06f), new(0.2f, 0.02f, 0.03f), new(0.85f, 0.2f, 0.15f), new(0.4f, 0.05f, 0.05f)];
        double Chroma(double backdrop)
        {
            var director = new LyricInkDirector();
            var ink = LyricInk.White;
            for (var frame = 0; frame < 600; frame++)
                ink = director.Ink(new LyricInkInputs(LyricColourMode.AlbumDrift) { Album = red, Backdrop = backdrop }, frame / 60.0);
            return Perceptual.Readable(ink.Gradient(0.5), ink.Backdrop, ink.Contrast).Chroma;
        }
        const double previousEstimate = 0.24;
        Assert.True(Chroma(photo) > 0.12);
        Assert.True(Chroma(photo) > Chroma(previousEstimate) * 1.5);
    }
}
