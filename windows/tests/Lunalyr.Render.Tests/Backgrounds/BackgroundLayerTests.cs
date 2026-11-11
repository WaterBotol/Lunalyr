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
        // Nothing at the screen's edge.
        Assert.Equal(Pixel(empty, 0, Height / 2), Pixel(shown, 0, Height / 2));
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
