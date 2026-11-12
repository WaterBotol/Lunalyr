using Lunalyr.Core.Colour;
using Lunalyr.Render.Backgrounds;
using Lunalyr.Render.Diagnostics;
using Lunalyr.Render.Scene;

namespace Lunalyr.Render.Tests.Backgrounds;

/// <summary>
/// The animated cover's layer, drawn offscreen on WARP over the silent generated clip (160×96, 1 s, no audio track):
/// it plays, holds still under Reduce Motion, softens, falls back to the still cover, and picks the tall rendition on a
/// portrait surface. Also the video layer's loop arithmetic, which Apple's covers need.
/// </summary>
public sealed class MotionCoverLayerTests
{
    private static string Clip => Path.Combine(AppContext.BaseDirectory, "Fixtures", "clip.mp4");

    private static readonly SceneSnapshot Scene = new() { Title = "Song" };

    /// <summary>A solid cover of one colour, as the opening title's cover arrives (premultiplied BGRA).</summary>
    private static CoverArt Solid(byte r, byte g, byte b, int side = 32)
    {
        var pixels = new byte[side * side * 4];
        for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (b, g, r, 255);
        return new CoverArt(side, side, pixels);
    }

    /// <summary>Draws until the layer has its first frame (or 10 s pass), then <paramref name="more"/> frames 1/30 s apart.</summary>
    private static double Play(OffscreenSurface surface, IRenderLayer layer, SceneSnapshot scene, double start = 0, int more = 0)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var t = start;
        while (layer is IFirstFrameLayer { HasFirstFrame: false } && clock.Elapsed.TotalSeconds < 10)
        {
            surface.Draw(layer, scene, t, 1 / 30.0);
            t += 1 / 30.0;
            Thread.Sleep(10);
        }
        for (var i = 0; i < more; i++)
        {
            surface.Draw(layer, scene, t, 1 / 30.0);
            t += 1 / 30.0;
            Thread.Sleep(5);
        }
        return t;
    }

    private static double MeanDifference(byte[] a, byte[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i += 4)
            for (var c = 0; c < 3; c++) sum += Math.Abs(a[i + c] - b[i + c]);
        return sum / (a.Length / 4 * 3);
    }

    /// <summary>The mean absolute difference between neighbouring pixels across the frame: high for detail, low once blurred.</summary>
    private static double Detail(byte[] pixels, int width)
    {
        double sum = 0;
        var count = 0;
        for (var i = 4; i < pixels.Length; i += 4)
        {
            if (i / 4 % width == 0) continue;
            sum += Math.Abs(pixels[i + 1] - pixels[i - 3]);
            count++;
        }
        return sum / count;
    }

    [Fact]
    public void AFragmentedCoverStartingLateLoopsWithoutAHold()
    {
        // Apple's 768-pixel "Midnights" cover: no stated duration, first frame at 10 s, last at 32.6 s, 30 fps.
        Assert.Equal(22.6 + 1 / 30.0, VideoLayer.PassLength(0, 10, 32.6, 1 / 30.0), 6);
        // A file that states its length keeps it; a broken step falls back to one 30 fps frame.
        Assert.Equal(6, VideoLayer.PassLength(6, 0, 5.9, 0.1));
        Assert.Equal(1 / 30.0, VideoLayer.PassLength(0, double.NaN, 0, -1), 6);
    }

    [Fact]
    public void TheCoverPlaysAndMoves()
    {
        using var surface = new OffscreenSurface(160, 96);
        using var layer = new MotionCoverLayerFactory(Clip, null, WallpaperPlacement.Fill).Create();
        surface.Prepare(layer);
        var t = Play(surface, layer, Scene with { Adjustments = new WallpaperAdjustments { Softening = 0 } });
        Assert.True(((IFirstFrameLayer)layer).HasFirstFrame);
        var first = surface.ReadPixels();
        Play(surface, layer, Scene with { Adjustments = new WallpaperAdjustments { Softening = 0 } }, t, more: 12);
        var later = surface.ReadPixels();
        Assert.True(first.Where((v, i) => i % 4 != 3).Average(v => (double)v) > 8, "the cover shows, darkened but not black");
        Assert.True(MeanDifference(first, later) > 1, "the cover moves");
        Assert.True(layer.IsAnimating);
    }

    [Fact]
    public void ReduceMotionHoldsTheFirstFrame()
    {
        using var surface = new OffscreenSurface(160, 96);
        using var layer = new MotionCoverLayerFactory(Clip, null, WallpaperPlacement.Fill).Create();
        surface.Prepare(layer);
        var still = Scene with { ReduceMotion = true };
        var t = Play(surface, layer, still, more: 2);
        var first = surface.ReadPixels();
        Play(surface, layer, still, t, more: 12);
        Assert.Equal(0, MeanDifference(first, surface.ReadPixels()), 3);
        Assert.False(layer.IsAnimating);
    }

    [Fact]
    public void SofteningBlursTheCover()
    {
        // A fine checkerboard (as the still cover, through the same softening as the video): detail a blur removes.
        var checker = new byte[32 * 32 * 4];
        for (var i = 0; i < 32 * 32; i++)
        {
            var v = (byte)(((i % 32) / 2 + i / 32 / 2) % 2 == 0 ? 230 : 30);
            (checker[i * 4], checker[i * 4 + 1], checker[i * 4 + 2], checker[i * 4 + 3]) = (v, v, v, 255);
        }
        byte[] Draw(double softening)
        {
            using var surface = new OffscreenSurface(320, 192);
            using var layer = new MotionCoverLayerFactory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "missing.mp4"), null,
                WallpaperPlacement.Fill).Create();
            surface.Prepare(layer);
            var scene = Scene with { Cover = new CoverArt(32, 32, checker), Adjustments = new WallpaperAdjustments { Softening = softening } };
            Play(surface, layer, scene, more: 2);
            return surface.ReadPixels();
        }
        var sharp = Detail(Draw(0), 320);
        var soft = Detail(Draw(25), 320);
        Assert.True(soft < sharp * 0.8, $"softening 25 leaves less detail ({soft:0.000} against {sharp:0.000})");
    }

    [Fact]
    public void AVideoThatCantPlayShowsTheStillCover()
    {
        using var surface = new OffscreenSurface(64, 64);
        using var layer = new MotionCoverLayerFactory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "missing.mp4"), null,
            WallpaperPlacement.Fill).Create();
        surface.Prepare(layer);
        var scene = Scene with { Cover = Solid(220, 40, 40) };
        Play(surface, layer, scene, more: 3);
        Assert.True(((IFirstFrameLayer)layer).HasFirstFrame, "a cover that can't play never holds the crossfade");
        var pixels = surface.ReadPixels();
        var centre = (32 * 64 + 32) * 4;
        Assert.True(pixels[centre + 2] > pixels[centre + 1] + 20, "the still cover's red shows, darkened, in its place");
    }

    [Fact]
    public void APortraitSurfacePlaysTheTallRendition()
    {
        using var portrait = new OffscreenSurface(96, 160);
        using var tall = (MotionCoverLayer)new MotionCoverLayerFactory("square.mp4", Clip, WallpaperPlacement.Fill).Create();
        portrait.Prepare(tall);
        Assert.Equal(Clip, tall.Playing);
        using var landscape = new OffscreenSurface(160, 96);
        using var square = (MotionCoverLayer)new MotionCoverLayerFactory("square.mp4", Clip, WallpaperPlacement.Fill).Create();
        landscape.Prepare(square);
        Assert.Equal("square.mp4", square.Playing);
    }

    [Fact]
    public void FitShowsTheAlbumColoursBesideTheCover()
    {
        using var surface = new OffscreenSurface(320, 96);
        using var layer = new MotionCoverLayerFactory(Clip, null, WallpaperPlacement.Fit).Create();
        surface.Prepare(layer);
        var palette = PaletteMapper.FromAlbum(new AlbumWallpaperPalette(
            [.. Enumerable.Range(0, 9).Select(_ => new Double3(0.33, 0.9, 0.9))]));
        Play(surface, layer, Scene with { Palette = palette, ReduceMotion = true }, more: 1);
        var pixels = surface.ReadPixels();
        var edge = (48 * 320 + 2) * 4; // the left edge, beside a 160-wide cover centred in 320
        Assert.True(pixels[edge + 1] > pixels[edge + 2] + 10, "the album's green lies beside the fitted cover, not black");
    }

    [Fact]
    public void TheBackingCountsTheCoversOwnShade()
    {
        // The even shade (40% at Darkening 1) under the round shade's 50% middle: 70% already at the stage.
        Assert.Equal(0.7, MotionCoverLayer.StageShadeFor(1), 6);
        Assert.Equal(0.5, MotionCoverLayer.StageShadeFor(0), 6);
        Assert.Equal(0.9, MotionCoverLayer.StageShadeFor(2), 6);
        Assert.Equal(5 * 0.5 * 2, MotionCoverLayer.SigmaFor(5, 2), 6);
    }
}
