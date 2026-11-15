// Starfield: small dots drifting sideways in three depth layers; nearer layers move faster with larger, brighter dots
// (parallax), and every dot twinkles at its own rate. Windows' own.

using System.Numerics;
using Vortice.Direct2D1;
using static Lunalyr.Render.Backgrounds.Gpu.Painters.PainterMath;

namespace Lunalyr.Render.Backgrounds.Gpu.Painters;

internal sealed class StarfieldPainter : ILinePainter
{
    private const int Layers = 3, PerLayer = 64, Count = Layers * PerLayer;
    private static readonly float[] Speed = [0.010f, 0.020f, 0.040f];   // screen widths per phase unit
    private static readonly float[] Radius = [0.8f, 1.1f, 1.5f];     // points
    private static readonly float[] Bright = [0.13f, 0.20f, 0.28f];
    private readonly float[] _x = new float[Count];
    private readonly float[] _y = new float[Count];
    private readonly float[] _twinkleRate = new float[Count];
    private readonly float[] _twinkleStart = new float[Count];
    private readonly float[] _level = new float[Count];

    public void Prepare(ID2D1DeviceContext dc, ID2D1Factory1 factory, Vector2 size, float scale, int seed)
    {
        for (var i = 0; i < Count; i++)
        {
            _x[i] = Hash(seed, i, 1);
            _y[i] = Hash(seed, i, 2);
            _twinkleRate[i] = Range(seed, i, 3, 0.8f, 2.6f);
            _twinkleStart[i] = Hash(seed, i, 4) * Tau;
            _level[i] = Range(seed, i, 5, 0.55f, 1f);
        }
    }

    public void Draw(ID2D1DeviceContext dc, in LinePatternFrame frame)
    {
        var margin = 4 * frame.Scale;
        var span = frame.Size.X + 2 * margin;
        for (var layer = 0; layer < Layers; layer++)
        {
            var r = Radius[layer] * frame.Scale;
            for (var k = 0; k < PerLayer; k++)
            {
                var i = layer * PerLayer + k;
                var x = Cycle(frame.Phase, -Speed[layer], _x[i]) * span - margin;
                var twinkle = 0.55f + 0.45f * Wave(frame.Phase, _twinkleRate[i], _twinkleStart[i]);
                dc.FillEllipse(new Ellipse(new Vector2(x, _y[i] * frame.Size.Y), r, r), frame.Ink(Bright[layer] * _level[i] * twinkle));
            }
        }
    }

    public void Release() { }

    public void Dispose() { }
}
