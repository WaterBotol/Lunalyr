using Lunalyr.Core.Visualisers;
using Lunalyr.Render.Diagnostics;
using Lunalyr.Render.Scene;
using Lunalyr.Render.Visualisers;

namespace Lunalyr.Render.Tests.Visualisers;

/// <summary>A visualiser frame allocates nothing once its scene's texture exists.</summary>
public sealed class VisualiserAllocationTests
{
    private sealed class Steady : IVisualiserSound
    {
        public double Time { get; set; } = 500;
        public double Now => Time;
        public AudioSnapshot Latest()
        {
            var sound = new AudioSnapshot { Level = 0.5f, Bass = 0.6f, Mid = 0.4f, Treble = 0.3f };
            for (var i = 0; i < 64; i++) sound.Bands[i] = 0.4f;
            var wavefield = sound.Wavefield;
            wavefield.Rms = 0.3f;
            wavefield.IsReceiving = true;
            sound.Wavefield = wavefield;
            return sound;
        }
    }

    [Theory]
    [InlineData(VisualizerStyle.Silk, false)]
    [InlineData(VisualizerStyle.StarTrails, false)]
    [InlineData(VisualizerStyle.Wavefield, true)]
    [InlineData(VisualizerStyle.ColourStrata, true)]
    public void FramesDoNotAllocate(VisualizerStyle style, bool calm)
    {
        var sound = new Steady();
        var settings = VisualiserSettings.Default with { Style = style };
        var host = new VisualiserHost(() => settings, sound);
        using var surface = new OffscreenSurface(160, 90);
        using var layer = new VisualiserLayerFactory(host, new Lunalyr.Render.Backgrounds.BackgroundOptions { Grain = false }).Create();
        surface.Prepare(layer);
        var scene = new SceneSnapshot { ReduceMotion = calm };
        long allocated = 0;
        for (var frame = 0; frame < 600; frame++)
        {
            sound.Time += 1 / 60.0;
            var context = new FrameContext
            {
                Dc = surface.Context, DWrite = surface.DWrite, D2DFactory = surface.Factory, Size = surface.Size, Scale = 1,
                WallSeconds = frame / 60.0, SongSeconds = double.NaN, IsPlaying = false, Scene = scene, DeltaSeconds = 1 / 60.0,
            };
            surface.Context.BeginDraw();
            var before = GC.GetAllocatedBytesForCurrentThread();
            layer.Render(context);
            var after = GC.GetAllocatedBytesForCurrentThread();
            surface.Context.EndDraw();
            if (frame >= 540) allocated += after - before;
        }
        Assert.True(allocated == 0, $"{style}: {allocated} bytes over 60 frames");
    }
}
