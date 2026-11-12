using System.Numerics;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Motion;
using Lunalyr.Render.Backgrounds;
using Lunalyr.Render.Diagnostics;
using Lunalyr.Render.Scene;

namespace Lunalyr.Render.Tests.Backgrounds;

/// <summary>The "gradient" and "colour" backgrounds, drawn offscreen on WARP and read back.</summary>
public sealed class BackgroundLayerTests : IDisposable
{
    private const int Width = 320, Height = 180;
    private static readonly BackgroundOptions Plain = new() { Grain = false };
    private readonly OffscreenSurface _surface = new(Width, Height);

    public void Dispose() => _surface.Dispose();

    private static Palette Uniform(float r, float g, float b)
    {
        var colour = new Vector4(r, g, b, 1);
        return new Palette(colour, [colour, colour, colour, colour, colour], Vector4.One, Vector4.One);
    }

    private static SceneSnapshot Quiet(Palette palette) => new() { Palette = palette };

    private (byte R, byte G, byte B) Pixel(byte[] pixels, int x, int y)
    {
        var i = (y * Width + x) * 4;
        return (pixels[i + 2], pixels[i + 1], pixels[i]);
    }

    private byte[] Frame(IRenderLayer layer, SceneSnapshot scene, double wall)
    {
        _surface.Draw(layer, scene, wall);
        return _surface.ReadPixels();
    }

    private static double Mean(byte[] pixels)
    {
        double sum = 0;
        for (var i = 0; i < pixels.Length; i += 4) sum += pixels[i] + pixels[i + 1] + pixels[i + 2];
        return sum / (pixels.Length / 4 * 3);
    }

    private static double Difference(byte[] a, byte[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return sum / a.Length;
    }

    public static TheoryData<string> Layers => ["gradient", "colour"];

    private static IRenderLayerFactory Factory(string id, BackgroundOptions? options = null) =>
        id == "colour" ? new ColourLayerFactory(options ?? Plain) : new GradientLayerFactory(options ?? Plain);

    [Theory]
    [MemberData(nameof(Layers))]
    public void FactoriesHaveTheirIds(string id) => Assert.Equal(id, Factory(id).Id);

    [Theory]
    [MemberData(nameof(Layers))]
    public void AOneColourPaletteDrawsThatColourUnderTheScrim(string id)
    {
        using var layer = Factory(id).Create();
        _surface.Prepare(layer);
        var pixels = Frame(layer, Quiet(Uniform(0.2f, 0.4f, 0.6f)), 1);
        // In the middle the scrim is at its lightest, 12% black, blended as the screen blends (gamma-encoded).
        var middle = Pixel(pixels, Width / 2, Height / 2);
        Assert.InRange(middle.R, 0.2 * 0.88 * 255 - 3, 0.2 * 0.88 * 255 + 3);
        Assert.InRange(middle.G, 0.4 * 0.88 * 255 - 3, 0.4 * 0.88 * 255 + 3);
        Assert.InRange(middle.B, 0.6 * 0.88 * 255 - 3, 0.6 * 0.88 * 255 + 3);
        // The scrim: darker at the top than the middle, darkest at the bottom.
        var top = Pixel(pixels, Width / 2, 0);
        var bottom = Pixel(pixels, Width / 2, Height - 1);
        Assert.True(top.B < middle.B && bottom.B < top.B, $"{top} {middle} {bottom}");
    }

    [Theory]
    [MemberData(nameof(Layers))]
    public void APaletteChangeFadesOverSurfaceFade(string id)
    {
        using var layer = Factory(id).Create();
        _surface.Prepare(layer);
        var red = Uniform(0.8f, 0.1f, 0.1f);
        var blue = Uniform(0.1f, 0.1f, 0.8f);
        Frame(layer, Quiet(red), 10);
        var start = Pixel(Frame(layer, Quiet(blue), 10), Width / 2, Height / 2);
        Assert.True(start.R > start.B * 3, $"the change starts from what was on screen: {start}");
        Assert.True(layer.IsAnimating);
        var middle = Pixel(Frame(layer, Quiet(blue), 10 + WallpaperMotion.SurfaceFade / 2), Width / 2, Height / 2);
        Assert.InRange(middle.R, middle.B - 6, middle.B + 6);
        var end = Pixel(Frame(layer, Quiet(blue), 10 + WallpaperMotion.SurfaceFade), Width / 2, Height / 2);
        Assert.True(end.B > end.R * 3, $"{end}");
        // An equal palette in a new object is not a change.
        var again = Pixel(Frame(layer, Quiet(Uniform(0.1f, 0.1f, 0.8f)), 12), Width / 2, Height / 2);
        Assert.Equal(end, again);
    }

    [Theory]
    [MemberData(nameof(Layers))]
    public void TheTextBackingShadesTheStageOnlyWhileItShowsSomething(string id)
    {
        using var layer = Factory(id).Create();
        _surface.Prepare(layer);
        var pale = Uniform(0.95f, 0.92f, 0.8f);
        var empty = Frame(layer, Quiet(pale), 1);
        using var playing = Factory(id).Create();
        _surface.Prepare(playing);
        var shown = Frame(playing, new SceneSnapshot { Palette = pale, Title = "Song" }, 1);
        var centreEmpty = Pixel(empty, Width / 2, Height / 2);
        var centreShown = Pixel(shown, Width / 2, Height / 2);
        Assert.True(centreShown.R < centreEmpty.R - 40, $"{centreShown} vs {centreEmpty}");
        // Nothing in the screen's corners (the feathered shade reaches past the sides, see FeatheredBacking).
        Assert.InRange(Pixel(empty, 0, 0).R - Pixel(shown, 0, 0).R, 0, 2);
        // White type reaches the pastel target (6.5:1) in the middle of the stage. The colour field is judged by the
        // Mac's estimate (its brightest mass at 85% over the base), which a one-colour field slightly exceeds.
        var luminance = LyricLegibility.Luminance(new Double3(centreShown.R / 255.0, centreShown.G / 255.0, centreShown.B / 255.0));
        Assert.True(LyricLegibility.WhiteContrast(luminance) >= (id == "colour" ? 6.3 : 5.5), $"{LyricLegibility.WhiteContrast(luminance)}");
        // When the stage empties, the backing eases away over 0.6 s rather than vanishing.
        Frame(playing, Quiet(pale), 1.3);
        var leaving = Pixel(Frame(playing, Quiet(pale), 1.6), Width / 2, Height / 2);
        Assert.InRange(leaving.R, centreShown.R + 5, centreEmpty.R - 5);
        Assert.Equal(centreEmpty, Pixel(Frame(playing, Quiet(pale), 1.95), Width / 2, Height / 2));
    }

    // Adapted from LyricLegibilityTests.swift: testTheDrawnBackingFollowsTheShapeAndAManualAmountKeepsItsEllipse. The
    // Mac draws the shade alone at 0.5 over white; here the whole layer draws it over a white field with no scrim, at
    // 0.6, the cap the automatic amount reaches over white, so the two can be compared at the centre. Windows'
    // automatic shape differs on purpose (FeatheredBacking: the Mac's stage rectangle showed as a dark box with light
    // edges over bright covers), so the automatic half checks the feathered shape; the manual ellipse is the Mac's.
    [Fact]
    public void TheDrawnBackingIsFeatheredAndAManualAmountKeepsItsEllipse()
    {
        var white = Uniform(1, 1, 1);
        var song = new SceneSnapshot { Palette = white, Title = "Song" };
        Func<double, double, double> Darkening(BackgroundOptions options)
        {
            using var layer = new GradientLayerFactory(options).Create();
            _surface.Prepare(layer);
            var pixels = Frame(layer, song, 1);
            // The same field with an empty stage, so only the backing is measured (the field itself is not quite
            // even at the screen's edges).
            using var quiet = new GradientLayerFactory(options).Create();
            _surface.Prepare(quiet);
            var field = Frame(quiet, Quiet(white), 1);
            return (x, y) =>
            {
                var column = Math.Clamp((int)(x * Width), 0, Width - 1);
                var row = Math.Clamp((int)(y * Height), 0, Height - 1);
                return 1 - Pixel(pixels, column, row).R / Math.Max(1.0, Pixel(field, column, row).R);
            };
        }
        var half = LyricLegibility.StageHalfExtent;
        double left = 0.5 - half.X, top = 0.5 - half.Y;
        var automatic = Darkening(Plain with { ScrimStrength = 0 });
        var centre = automatic(0.5, 0.5);
        Assert.True(centre > 0.2);
        // As dark where the lyrics are set (the middle 60% × 50% of the screen, corners included) as in the middle...
        foreach (var (x, y) in new[] { (0.2, 0.5), (0.8, 0.5), (0.5, 0.25), (0.5, 0.75), (0.2, 0.25), (0.8, 0.75) })
            Assert.Equal(centre, automatic(x, y), 0.03);
        // ...still most of it at the stage's sides...
        Assert.True(automatic(left, 0.5) > centre * 0.85);
        Assert.True(automatic(1 - left, 0.5) > centre * 0.85);
        // ...then easing out (only ever lighter towards the edges; FeatheredBackingTests checks the shape's smoothness),
        // to nothing in the screen's corners.
        for (var column = Width / 2 + 1; column < Width; column++)
        {
            double inner = automatic((column - 1.0) / Width, 0.5), outer = automatic((double)column / Width, 0.5);
            Assert.True(outer <= inner + 0.01, $"darker outwards at column {column}");
        }
        for (var row = Height / 2 + 1; row < Height; row++)
        {
            double inner = automatic(0.5, (row - 1.0) / Height), outer = automatic(0.5, (double)row / Height);
            Assert.True(outer <= inner + 0.01, $"darker outwards at row {row}");
        }
        // Across the band where the Mac's rectangle ends, a gentle slope: no more than half the backing over the last
        // tenth of the screen's width (the Mac's goes from all of it to nothing there).
        Assert.True(automatic(left, 0.5) - automatic(0.0, 0.5) < centre * 0.55);
        Assert.True(automatic(0.02, 0.5) < centre * 0.6, "the shade has faded by the screen's sides");
        Assert.True(automatic(0.5, 0.02) < centre * 0.4, "the shade has faded by the screen's top");
        foreach (var (x, y) in new[] { (0.0, 0.0), (0.999, 0.0), (0.0, 0.999), (0.999, 0.999) })
            Assert.True(automatic(x, y) < 0.03, $"at ({x}, {y})");
        // The user's own amount keeps its old look: lighter at the stage's ends than its middle, nothing at its corners.
        var ellipse = Darkening(Plain with { ScrimStrength = 0, ManualBacking = 0.6 });
        var middle = ellipse(0.5, 0.5);
        Assert.Equal(centre, middle, 0.03);
        Assert.True(ellipse(left, 0.5) < middle * 0.75);
        Assert.True(ellipse(left, top) < 0.03);
    }

    // Ported from WallpaperEnergyTests.swift: testBoundedColourFieldLooksLikeTheNativeOne (with this layer's field)
    [Fact]
    public void BoundedColourFieldLooksLikeTheNativeOne()
    {
        (int Width, int Height, double Budget)[] cases =
        [
            (640, 360, ColourFieldBudget.Standard * (640 * 360) / (3024.0 * 1964)), (640, 360, 26_000), (1000, 777, 26_000), (333, 777, 5_000),
        ];
        var palette = new SceneSnapshot { Palette = PaletteMapper.FromAlbum(new AlbumWallpaperPalette(
        [
            new(0.02, 0.85, 0.95), new(0.1, 0.5, 0.5), new(0.58, 0.75, 0.85), new(0.3, 0.4, 0.6), new(0.8, 0.6, 0.7),
            new(0.5, 0.5, 0.5), new(0.12, 0.9, 0.9), new(0.4, 0.3, 0.3), new(0.7, 0.7, 0.75),
        ])) };
        foreach (var (width, height, budget) in cases)
        {
            using var surface = new OffscreenSurface(width, height);
            byte[] Render(double pixels)
            {
                using var layer = new GradientLayerFactory(Plain with { Vividness = 1.2, PixelBudget = pixels }).Create();
                surface.Prepare(layer);
                surface.Draw(layer, palette, 40 / ColourField.ClockRate);
                return surface.ReadPixels();
            }
            var native = Render(double.PositiveInfinity);
            var bounded = Render(budget);
            double difference = 0, level = 0;
            int worst = 0, seams = 0;
            for (var i = 0; i < native.Length; i += 4)
            {
                for (var channel = 0; channel < 3; channel++)
                {
                    var delta = Math.Abs(native[i + channel] - bounded[i + channel]);
                    difference += delta / 255.0;
                    worst = Math.Max(worst, delta);
                    level += native[i + channel] / 255.0;
                }
                if (bounded[i + 3] < 250) seams += 1;
            }
            var samples = native.Length / 4 * 3.0;
            var label = $"{width}x{height} at {(int)budget} px";
            Assert.True(level / samples > 0.05, $"{label}: the field rendered something");
            Assert.True(difference / samples < 0.008, $"{label}: mean channel difference {difference / samples}");
            Assert.True(worst <= 16, $"{label}: largest channel difference {worst}");
            Assert.Equal(0, seams);
        }
    }

    [Fact]
    public void TheFieldDriftsSlowly()
    {
        using var layer = new GradientLayerFactory(Plain).Create();
        _surface.Prepare(layer);
        var palette = PaletteMapper.FromAlbum(new AlbumWallpaperPalette(AlbumColourGrid.FallbackHsb));
        var a = Frame(layer, Quiet(palette), 100);
        var b = Frame(layer, Quiet(palette), 100 + 1 / 60.0);
        var c = Frame(layer, Quiet(palette), 160);
        Assert.True(Difference(a, b) < 0.2, $"one frame apart: {Difference(a, b)}");
        Assert.True(Difference(a, c) > 1, $"a minute apart: {Difference(a, c)}");
        Assert.True(layer.IsAnimating);
        // Not flat: the masses show.
        var row = Enumerable.Range(0, Width).Select(x => Pixel(a, x, Height / 2)).ToArray();
        Assert.True(row.Max(p => p.R + p.G + p.B) - row.Min(p => p.R + p.G + p.B) > 20);
    }

    // The Mac slows the ambient wallpaper under Reduce Motion (NowPlayingView: speed × reducedMotionRate) on a clock that
    // advances at the current rate, so the field never jumps when the setting changes.
    [Fact]
    public void TheFieldSlowsUnderTheScenesReduceMotionWithoutAJump()
    {
        var palette = PaletteMapper.FromAlbum(new AlbumWallpaperPalette(AlbumColourGrid.FallbackHsb));
        var calm = Quiet(palette) with { ReduceMotion = true };
        using var layer = new GradientLayerFactory(Plain).Create();
        _surface.Prepare(layer);
        var before = Frame(layer, Quiet(palette), 100);
        Assert.Equal(0, Difference(before, Frame(layer, calm, 100)));
        var slowed = Frame(layer, calm, 160);
        using var full = new GradientLayerFactory(Plain).Create();
        _surface.Prepare(full);
        Frame(full, Quiet(palette), 100);
        var moved = Frame(full, Quiet(palette), 160);
        Assert.True(Difference(before, slowed) < Difference(before, moved) * 0.5,
            $"a minute under Reduce Motion: {Difference(before, slowed)} vs {Difference(before, moved)}");
        Assert.True(layer.IsAnimating); // still moving, at 15%
        // The options' own Reduce Motion is the same 15% (ColourField.ReducedMotionRate).
        Assert.Equal(0.15 * ColourField.Clock(60, 1, false), ColourField.Clock(60, 1, true), 9);
    }

    [Fact]
    public void TheColourGradientHoldsStillUnderTheScenesReduceMotion()
    {
        var two = new Palette(new Vector4(0.1f, 0.1f, 0.1f, 1),
            [new(0.9f, 0.2f, 0.2f, 1), new(0.9f, 0.2f, 0.2f, 1), new(0.5f, 0.5f, 0.5f, 1), new(0.2f, 0.2f, 0.9f, 1), new(0.2f, 0.2f, 0.9f, 1)],
            Vector4.One, Vector4.One);
        using var layer = new ColourLayerFactory(Plain).Create();
        _surface.Prepare(layer);
        var calm = Quiet(two) with { ReduceMotion = true };
        var c = Frame(layer, calm, 0);
        Assert.Equal(0, Difference(c, Frame(layer, calm, 10)));
        Assert.False(layer.IsAnimating);
        Frame(layer, Quiet(two), 10);
        Assert.True(layer.IsAnimating);
    }

    [Fact]
    public void TheColourGradientTiltsSlowlyAndHoldsStillUnderReduceMotion()
    {
        var two = new Palette(new Vector4(0.1f, 0.1f, 0.1f, 1),
            [new(0.9f, 0.2f, 0.2f, 1), new(0.9f, 0.2f, 0.2f, 1), new(0.5f, 0.5f, 0.5f, 1), new(0.2f, 0.2f, 0.9f, 1), new(0.2f, 0.2f, 0.9f, 1)],
            Vector4.One, Vector4.One);
        using var moving = new ColourLayerFactory(Plain).Create();
        _surface.Prepare(moving);
        var a = Frame(moving, Quiet(two), 0);
        var b = Frame(moving, Quiet(two), 10);
        Assert.True(Difference(a, b) > 0.5);
        // Left is the cover's left, right its right.
        Assert.True(Pixel(a, 2, Height / 2).R > Pixel(a, Width - 3, Height / 2).R);
        Assert.Equal(0, ColourLayer.Turn(5, 1, reduceMotion: true));
        using var still = new ColourLayerFactory(Plain with { ReduceMotion = true }).Create();
        _surface.Prepare(still);
        var c = Frame(still, Quiet(two), 0);
        var d = Frame(still, Quiet(two), 10);
        Assert.Equal(0, Difference(c, d));
        Assert.False(still.IsAnimating);
    }

    [Theory]
    [MemberData(nameof(Layers))]
    public void SurvivesLosingItsResourcesAndAResize(string id)
    {
        using var layer = Factory(id).Create();
        _surface.Prepare(layer);
        var palette = Uniform(0.3f, 0.5f, 0.2f);
        var before = Frame(layer, Quiet(palette), 3);
        layer.ReleaseResources();
        _surface.Draw(layer, Quiet(palette), 3.1);  // draws nothing, does not throw
        _surface.Prepare(layer);
        layer.Resize(new Vector2(Width, Height), 2);
        var after = Frame(layer, Quiet(palette), 3);
        Assert.True(Difference(before, after) < 1);
        Assert.True(Mean(after) > 20);
    }

    [Theory]
    [MemberData(nameof(Layers))]
    public void AFrameAllocatesNothing(string id)
    {
        using var layer = Factory(id, BackgroundOptions.Default).Create();
        _surface.Prepare(layer);
        var red = new SceneSnapshot { Palette = Uniform(0.8f, 0.1f, 0.1f), Title = "Song" };
        var palette = new SceneSnapshot { Palette = PaletteMapper.Fallback, Title = "Song" };
        for (var i = 0; i < 30; i++) _surface.Draw(layer, red, i / 60.0);
        // A fade is under way through the measured frames too.
        _surface.Draw(layer, palette, 0.5);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 120; i++) _surface.Draw(layer, palette, 0.5 + i / 60.0);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }
}
