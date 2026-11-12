using System.Numerics;
using Lunalyr.Core.Visualisers;
using Lunalyr.Render.Diagnostics;
using Lunalyr.Render.Scene;
using Lunalyr.Render.Visualisers;

namespace Lunalyr.Render.Tests.Visualisers;

/// <summary>
/// The photosensitivity limiter on the GPU (WARP): the probe pass, the measuring pass and the limited full-size pass, as
/// the wallpaper draws them. These protect people with photosensitivity.
/// </summary>
public sealed class VisualiserLimiterTests : IDisposable
{
    private const int Width = 160, Height = 90;
    // The Mac's rig draws the Tunnel. Windows' Tunnel is written fresh and darker, so its loudest frame barely
    // brightens; Fosfora Aurora, all seven ribbons lit by the same flat level, brightens most.
    private const VisualizerStyle Scene = VisualizerStyle.FosforaAurora;
    private readonly OffscreenSurface _surface = new(Width, Height);
    private readonly VisualiserGpu _gpu;

    public VisualiserLimiterTests() => _gpu = VisualiserGpu.For(_surface.Context);

    public void Dispose()
    {
        var device = _gpu.Device;
        _gpu.Dispose();
        device.Dispose();
        _surface.Dispose();
    }

    /// <summary>Draws the scene at a flat audio level through <paramref name="scene"/>'s limiter (calm), as the Mac's LimiterRig.</summary>
    private (float Shown, float Gain, float Displayed) Draw(VisualiserScene scene, float level, double time, bool calm = true,
        VisualiserGpu? on = null, OffscreenSurface? surface = null)
    {
        var gpu = on ?? _gpu;
        var dc = (surface ?? _surface).Context;
        var sound = new AudioSnapshot { Level = level, Bass = level, Mid = level, Treble = level };
        for (var i = 0; i < 64; i++) sound.Bands[i] = level;
        for (var i = 0; i < 8; i++) sound.Components[i] = level;
        var u = VisualizerUniforms.Build(Scene, Width, Height, 12, sound, 3, 1, reduced: calm);
        var audio = new float[VisualiserGpu.AudioFloats];
        ((ReadOnlySpan<float>)sound.Bands).CopyTo(audio);
        gpu.Write(gpu.Uniforms, u);
        gpu.Write<float>(gpu.Audio, audio);
        gpu.Write<Vector4>(gpu.Album, new Vector4[5]);
        gpu.Write<Vector4>(gpu.Scene, new Vector4[VisualiserGpu.SceneFloat4s]);
        Assert.True(scene.Encode(gpu, dc, Width, Height, calm, time));
        var (gain, shown) = calm ? scene.ReadExposure(gpu) : (1f, 0f);
        return (shown, gain, VisualiserSceneTests.MeanLuminance(scene.ReadPixels(gpu)));
    }

    private static VisualiserHost Host() => new(() => VisualiserSettings.Default with { Style = Scene });

    // Ported from VisualizerSafetyTests.swift: testNativeLimiterHoldsBackASuddenBrightFrameOnTheGPU
    [Fact]
    public void NativeLimiterHoldsBackASuddenBrightFrameOnTheGPU()
    {
        using var scene = new VisualiserScene(Host(), Scene, "1");
        scene.AdoptCurrentBrightness();
        var dark = Draw(scene, 0, 0);
        Assert.True(Math.Abs(dark.Gain - 1) <= 0.001, "Turning the limiter on keeps the current frame");
        var jump = Draw(scene, 1, 1.0 / 60);
        Assert.True(jump.Gain < 0.9, "The sudden bright frame is dimmed");
        Assert.True(jump.Shown - dark.Shown <= ExposureLimiter.MaxRisePerSecond / 60 + 0.0005);
        Assert.True(jump.Displayed - dark.Displayed < 0.02, "The limited frame on screen barely brightens");
        var later = jump;
        for (var frame = 2; frame <= 90; frame++) later = Draw(scene, 1, frame / 60.0);
        Assert.True(later.Displayed > jump.Displayed + 0.01, "Brightness still arrives, gradually");
        var fall = Draw(scene, 0, 91.0 / 60);
        Assert.True(Math.Abs(fall.Gain - 1) <= 0.001, "Falls are not delayed");
        Assert.True(fall.Displayed < later.Displayed);
    }

    // Ported from VisualizerSafetyTests.swift: testANewSurfaceForAVisibleSceneKeepsItsBrightness (the GPU half)
    [Fact]
    public void ANewSurfaceForAVisibleSceneKeepsItsBrightness()
    {
        var host = Host();
        using var showing = new VisualiserScene(host, Scene, "1");
        var settled = Draw(showing, 0.8f, 0);
        Assert.True(Math.Abs(settled.Gain) <= 0.001, "A scene coming on screen fades in");
        for (var frame = 1; frame <= 300; frame++)
        {
            settled = Draw(showing, 0.8f, frame / 60.0);
            host.Exposure.Record(settled.Shown, new ExposureMemory.Key(Scene, "1", false), frame / 60.0);
        }
        Assert.True(Math.Abs(settled.Gain - 1) <= 0.001);
        Assert.True(settled.Displayed > 0.02);

        // The crossfade's copy of the same scene, one frame later.
        var now = 301.0 / 60;
        using var copy = new VisualiserScene(host, Scene, "1");
        var first = Draw(copy, 0.8f, now);
        Assert.True(Math.Abs(first.Gain - 1) <= 0.01, "The copy continues at the brightness on screen");
        Assert.True(Math.Abs(first.Displayed - settled.Displayed) <= 0.005);

        // Another surface fades in.
        using var other = new VisualiserScene(host, Scene, "2");
        Assert.True(Math.Abs(Draw(other, 0.8f, now).Gain) <= 0.001, "Another surface is not on screen");
        var later = now + ExposureMemory.Freshness + 0.1;
        using var stale = new VisualiserScene(host, Scene, "1");
        Assert.True(Math.Abs(Draw(stale, 0.8f, later).Gain) <= 0.001, "The scene left the screen");
    }

    // Ported from VisualizerSafetyTests.swift: testACalmSurfaceWaitsForItsLimiter. Windows' limiter is built in with the
    // scene (no asynchronous build), so the first calm frame is always limited: it fades in from black.
    [Fact]
    public void ACalmSurfaceFadesInFromBlack()
    {
        using var scene = new VisualiserScene(Host(), Scene, "1");
        var first = Draw(scene, 1, 70_000);
        Assert.True(first.Displayed < 0.002, "The scene fades in from black");
    }

    // Ported from VisualizerSafetyTests.swift: testMovingToAnotherGPUKeepsTheLimitedBrightness
    [Fact]
    public void MovingToAnotherGPUKeepsTheLimitedBrightness()
    {
        using var scene = new VisualiserScene(Host(), Scene, "1");
        var shown = 0f;
        for (var frame = 0; frame < 12; frame++) shown = Draw(scene, 1, 80_000 + frame / 60.0).Displayed;
        // A second set of GPU objects stands in for the other GPU: the move always starts a new probe.
        using var other = new OffscreenSurface(Width, Height);
        var second = VisualiserGpu.For(other.Context);
        try
        {
            var moved = Draw(scene, 1, 80_000 + 12.0 / 60, on: second, surface: other).Displayed;
            Assert.True(moved - shown < 0.005, $"The move does not release the limited brightness ({moved:0.0000} vs {shown:0.0000})");
            var unlimited = Draw(scene, 1, 80_000 + 12.0 / 60 + 0.001, calm: false, on: second, surface: other).Displayed;
            Assert.True(unlimited > shown + 0.02, $"The scene is well above what the limiter allowed ({unlimited:0.0000} vs {shown:0.0000})");        }
        finally
        {
            scene.Release();
            var device = second.Device;
            second.Dispose();
            device.Dispose();
        }
    }

    // Windows' own: the whole wallpaper layer under Reduce Motion, silence then loud noise-like sound at 60 fps: the
    // displayed mean never rises faster than the limit, and the same jump without Reduce Motion is immediate.
    [Fact]
    public void TheWallpaperLayerLimitsAJumpToLoudSound()
    {
        double Rise(bool calm, out double worstPerSecond)
        {
            var sound = new SteppedSound();
            var host = new VisualiserHost(() => VisualiserSettings.Default with { Style = Scene, Sensitivity = 3 }, sound);
            using var surface = new OffscreenSurface(Width, Height);
            using var layer = new VisualiserLayerFactory(host).Create();
            surface.Prepare(layer);
            var means = new List<double>();
            for (var frame = 0; frame < 150; frame++)
            {
                sound.Time = 1_000 + frame / 60.0;
                surface.Draw(layer, new SceneSnapshot { ReduceMotion = calm }, frame / 60.0);
                means.Add(VisualiserSceneTests.MeanLuminance(surface.ReadPixels()));
            }
            worstPerSecond = 0;
            for (var start = 30; start + 60 < means.Count; start++)
            {
                var risen = 0.0;
                for (var i = start; i < start + 60; i++) risen += Math.Max(0, means[i + 1] - means[i]);
                worstPerSecond = Math.Max(worstPerSecond, risen);
            }
            return means[^1] - means[59];
        }
        var unlimited = Rise(false, out _);
        var limited = Rise(true, out var worst);
        Assert.True(unlimited > 0.03, $"The jump is a real brightening ({unlimited:0.000})");
        Assert.True(worst <= ExposureLimiter.MaxRisePerSecond + 0.002, $"At most the capped rise in any second ({worst:0.0000})");
        Assert.True(limited > 0, "Brightness still arrives");
    }

    // Ported from VisualizerSafetyTests.swift: testANewSurfaceForAVisibleSceneKeepsItsBrightness, at the layer: a second
    // layer from the same factory (a rebuild while the scene is on screen) starts at the brightness shown, not black.
    [Fact]
    public void ANewLayerForAVisibleSceneKeepsItsBrightness()
    {
        var sound = new SteppedSound { Time = 1_002 };
        var host = new VisualiserHost(() => VisualiserSettings.Default with { Style = Scene, Sensitivity = 3 }, sound);
        var factory = new VisualiserLayerFactory(host, new Lunalyr.Render.Backgrounds.BackgroundOptions { Grain = false });
        var calm = new SceneSnapshot { ReduceMotion = true };
        using var surface = new OffscreenSurface(Width, Height);
        float settled;
        using (var first = factory.Create())
        {
            surface.Prepare(first);
            for (var frame = 0; frame < 600; frame++)
            {
                sound.Time += 1 / 60.0;
                surface.Draw(first, calm, frame / 60.0);
            }
            settled = ((VisualiserLayer)first).Current!.LastShown;
            Assert.True(settled > 0.01, $"read back {settled}, state {((VisualiserLayer)first).ReadExposure()}");
        }
        Assert.True(settled > 0.01, $"The scene is on screen at {settled:0.000}");
        using var second = factory.Create();
        surface.Prepare(second);
        sound.Time += 1 / 60.0;
        surface.Draw(second, calm, 10);
        // The new layer's limiter starts at the level shown (read back a few frames late, so this is its seed).
        var seeded = ((VisualiserLayer)second).Current!.LastShown;
        Assert.True(Math.Abs(seeded - settled) <= 0.01, $"The new layer carries on at the brightness shown ({seeded:0.000} vs {settled:0.000})");
        // Another host (the settings preview's) has no memory of it: its scene fades in from black.
        using var other = new VisualiserLayerFactory(new VisualiserHost(() => VisualiserSettings.Default with { Style = Scene }, sound),
            new Lunalyr.Render.Backgrounds.BackgroundOptions { Grain = false }).Create();
        surface.Prepare(other);
        surface.Draw(other, calm, 10);
        Assert.Equal(0, ((VisualiserLayer)other).Current!.LastShown);
    }
    // Windows' own: while power saving replaces the visualiser, the layer draws the album colours (Colour only).
    [Fact]
    public void PowerSavingDrawsTheAlbumColoursInstead()
    {
        var options = new Lunalyr.Render.Backgrounds.BackgroundOptions { Grain = false };
        var replaced = true;
        var host = new VisualiserHost(() => VisualiserSettings.Default with { Style = Scene }) { ReplacedForPower = () => replaced };
        using var layer = new VisualiserLayerFactory(host, options).Create();
        using var colour = new Lunalyr.Render.Backgrounds.ColourLayerFactory(options).Create();
        _surface.Prepare(layer);
        _surface.Prepare(colour);
        var scene = new SceneSnapshot();
        _surface.Draw(colour, scene, 1);
        var expected = _surface.ReadPixels();
        _surface.Draw(layer, scene, 1);
        Assert.Equal(expected, _surface.ReadPixels());
        replaced = false;
        _surface.Draw(layer, scene, 1.1);
        Assert.NotEqual(expected, _surface.ReadPixels());
    }

    // Windows' own: Direct3D's scene pass and Direct2D's batch share one device context. A layer drawn before the
    // visualiser is covered by it, and one drawn after it lands on top, in the same frame and the next.
    [Fact]
    public void DirectTwoDLayersBeforeAndAfterKeepTheirOrder()
    {
        var host = new VisualiserHost(() => VisualiserSettings.Default with { Style = VisualizerStyle.ColourStrata });
        using var layer = new VisualiserLayerFactory(host, new Lunalyr.Render.Backgrounds.BackgroundOptions { Grain = false }).Create();
        using var before = new FillLayer(new Vortice.Mathematics.Color4(1, 0, 0, 1), whole: true);
        using var after = new FillLayer(new Vortice.Mathematics.Color4(0, 1, 0, 1), whole: false);
        IRenderLayer[] layers = [before, layer, after];
        foreach (var l in layers) _surface.Prepare(l);
        for (var frame = 0; frame < 3; frame++)
        {
            _surface.Draw(layers, new SceneSnapshot(), double.NaN, false, frame / 60.0, 1 / 60.0);
            var pixels = _surface.ReadPixels();
            (int B, int G, int R) At(int x, int y) { var i = (y * Width + x) * 4; return (pixels[i], pixels[i + 1], pixels[i + 2]); }
            Assert.Equal((0, 255, 0), At(5, 5)); // The layer after the visualiser is on top
            var scene = At(Width - 10, Height - 10);
            Assert.True(scene.R < 200 && scene.B > 10, $"The visualiser covers the layer before it: {scene}");
        }
    }

    private sealed class FillLayer(Vortice.Mathematics.Color4 colour, bool whole) : IRenderLayer
    {
        private Vortice.Direct2D1.ID2D1SolidColorBrush? _brush;

        public bool IsAnimating => false;

        public void CreateResources(Vortice.Direct2D1.ID2D1DeviceContext dc, Vortice.DirectWrite.IDWriteFactory dwrite, Vector2 size, float scale) =>
            _brush = dc.CreateSolidColorBrush(colour);

        public void Resize(Vector2 size, float scale) { }

        public void Render(in FrameContext frame) =>
            frame.Dc.FillRectangle(whole ? new Vortice.RawRectF(0, 0, frame.Size.X, frame.Size.Y) : new Vortice.RawRectF(0, 0, 20, 20), _brush!);

        public void ReleaseResources() { _brush?.Dispose(); _brush = null; }

        public void Dispose() => ReleaseResources();
    }

    /// <summary>Silence for a second, then every band, register and the level at full.</summary>
    private sealed class SteppedSound : IVisualiserSound
    {
        public double Time { get; set; }

        public double Now => Time;

        public AudioSnapshot Latest()
        {
            var sound = new AudioSnapshot();
            if (Time - 1_000 < 1) return sound;
            sound.Level = sound.Bass = sound.Mid = sound.Treble = 1;
            for (var i = 0; i < 64; i++) sound.Bands[i] = 1;
            for (var i = 0; i < 8; i++) sound.Components[i] = 1;
            var wavefield = sound.Wavefield;
            wavefield.Rms = 1;
            wavefield.IsReceiving = true;
            sound.Wavefield = wavefield;
            return sound;
        }
    }
}
