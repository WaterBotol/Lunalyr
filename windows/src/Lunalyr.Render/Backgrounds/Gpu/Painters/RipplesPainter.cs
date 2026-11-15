// Ripples: rings expanding from three points, fading as they grow, recycling. Windows' own.

using System.Numerics;
using Vortice.Direct2D1;
using static Lunalyr.Render.Backgrounds.Gpu.Painters.PainterMath;

namespace Lunalyr.Render.Backgrounds.Gpu.Painters;

internal sealed class RipplesPainter : ILinePainter
{
    private const int Sources = 3, RingsPerSource = 6;
    private readonly Vector2[] _centres = new Vector2[Sources];
    private readonly float[] _offsets = new float[Sources];
    private readonly float[] _rates = new float[Sources];
    private float _reach;

    public void Prepare(ID2D1DeviceContext dc, ID2D1Factory1 factory, Vector2 size, float scale, int seed)
    {
        _reach = 0.62f * MathF.Max(size.X, size.Y);
        for (var i = 0; i < Sources; i++)
        {
            // Spread the three sources across the width so their rings overlap without stacking.
            var x = (i + Range(seed, i, 1, 0.2f, 0.8f)) / Sources;
            var y = Range(seed, i, 2, 0.25f, 0.75f);
            _centres[i] = new Vector2(x * size.X, y * size.Y);
            _offsets[i] = Hash(seed, i, 3);
            _rates[i] = Range(seed, i, 4, 0.07f, 0.10f);
        }
    }

    public void Draw(ID2D1DeviceContext dc, in LinePatternFrame frame)
    {
        var width = 1.0f * frame.Scale;
        for (var i = 0; i < Sources; i++)
        {
            for (var k = 0; k < RingsPerSource; k++)
            {
                var u = Cycle(frame.Phase, _rates[i], _offsets[i] + (double)k / RingsPerSource);
                var radius = u * _reach;
                if (radius < 1) continue;
                var fade = (1 - u) * (1 - u) * SmoothStep(0, 0.06f, u);
                dc.DrawEllipse(new Ellipse(_centres[i], radius, radius), frame.Ink(0.16f * fade), width);
            }
        }
    }

    public void Release() { }

    public void Dispose() { }
}
