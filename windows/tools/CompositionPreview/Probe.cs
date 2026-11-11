using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Lunalyr.Tools.CompositionPreview;

/// <summary>Measures which Direct2D calls allocate managed memory (development aid).</summary>
public static class Probe
{
    public static void Run(Offscreen stage)
    {
        var dc = stage.Dc;
        using var bitmap = dc.CreateBitmap(new SizeI(64, 64), IntPtr.Zero, 0, new BitmapProperties1(
            new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target));
        using var blur = new Vortice.Direct2D1.Effects.GaussianBlur(dc);
        blur.SetInput(0, bitmap, true);
        using var opacity = new Vortice.Direct2D1.Effects.Opacity(dc);
        opacity.SetInputEffect(0, blur, true);
        using var output = opacity.Output;
        using var shadow = new Vortice.Direct2D1.Effects.Shadow(dc);
        shadow.SetInput(0, bitmap, true);
        using var shadowOut = shadow.Output;
        void Measure(string name, Action action)
        {
            action();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100; i++) action();
            Console.WriteLine($"{name}: {(GC.GetAllocatedBytesForCurrentThread() - before) / 100.0} B");
        }
        stage.Dc.Target = null;
        using var target = dc.CreateBitmap(new SizeI(64, 64), IntPtr.Zero, 0, new BitmapProperties1(
            new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target));
        dc.Target = target;
        dc.BeginDraw();
        Measure("blur sigma", () => blur.StandardDeviation = 2.5f);
        Measure("blur sigma float overload", () => blur.SetValue(0u, 2.5f));
        Measure("opacity", () => opacity.Value = 0.5f);
        Measure("shadow colour", () => shadow.Color = new Vector4(1, 1, 1, 0.5f));
        Measure("shadow sigma", () => shadow.BlurStandardDeviation = 3);
        Measure("transform", () => dc.Transform = Matrix3x2.CreateScale(2));
        Measure("draw bitmap", () => dc.DrawBitmap(bitmap, new Vortice.RawRectF(0, 0, 64, 64), 0.5f, Vortice.Direct2D1.InterpolationMode.Linear, null, null));
        Measure("draw bitmap src", () => dc.DrawBitmap(bitmap, new Vortice.RawRectF(0, 0, 32, 64), 0.5f, Vortice.Direct2D1.InterpolationMode.Linear, new Vortice.RawRectF(0, 0, 32, 64), null));
        Measure("draw image", () => dc.DrawImage(output, Vortice.Direct2D1.InterpolationMode.Linear, CompositeMode.SourceOver));
        Measure("draw image rect", () => dc.DrawImage(output, null, new Vortice.RawRectF(0, 0, 32, 64), Vortice.Direct2D1.InterpolationMode.Linear, CompositeMode.SourceOver));
        Measure("draw shadow", () => dc.DrawImage(shadowOut, Vortice.Direct2D1.InterpolationMode.Linear, CompositeMode.SourceOver));
        dc.EndDraw();
        dc.Target = null;
    }
}
