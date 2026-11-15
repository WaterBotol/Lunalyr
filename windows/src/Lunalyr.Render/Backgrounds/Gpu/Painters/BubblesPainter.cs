// Bubbles: outlined circles of varied size rising slowly from below the screen with a slight sway, recycled once they
// have left the top. Windows' own.

using System.Numerics;
using Vortice.Direct2D1;
using static Lunalyr.Render.Backgrounds.Gpu.Painters.PainterMath;

namespace Lunalyr.Render.Backgrounds.Gpu.Painters;

internal sealed class BubblesPainter : ILinePainter
{
    private const int Count = 26;
    private readonly float[] _lane = new float[Count];
    private readonly float[] _radius = new float[Count];
    private readonly float[] _rate = new float[Count];
    private readonly float[] _start = new float[Count];
    private readonly float[] _swayRate = new float[Count];
    private readonly float[] _opacity = new float[Count];

    public void Prepare(ID2D1DeviceContext dc, ID2D1Factory1 factory, Vector2 size, float scale, int seed)
    {
        for (var i = 0; i < Count; i++)
        {
            var big = Hash(seed, i, 2);
            _lane[i] = (i + Range(seed, i, 1, 0.1f, 0.9f)) / Count;   // one bubble per lane, so they never bunch
            _radius[i] = 5 + 24 * big * big;
            _rate[i] = Range(seed, i, 3, 0.030f, 0.065f);
            _start[i] = Hash(seed, i, 4);
            _swayRate[i] = Range(seed, i, 5, 0.35f, 0.8f);
            _opacity[i] = Range(seed, i, 6, 0.09f, 0.16f);
        }
        // Shuffle lanes so neighbouring bubbles differ in size and speed (a fixed swap pattern from the seed).
        for (var i = Count - 1; i > 0; i--)
        {
            var j = (int)(Hash(seed, i, 7) * (i + 1));
            (_lane[i], _lane[j]) = (_lane[j], _lane[i]);
        }
    }

    public void Draw(ID2D1DeviceContext dc, in LinePatternFrame frame)
    {
        var width = 0.9f * frame.Scale;
        var height = frame.Size.Y;
        for (var i = 0; i < Count; i++)
        {
            var r = _radius[i] * frame.Scale;
            var u = Cycle(frame.Phase, _rate[i], _start[i]);
            var y = height + r - u * (height + 2 * r);
            var x = _lane[i] * frame.Size.X + 14 * frame.Scale * Wave(frame.Phase, _swayRate[i], i * 2.3);
            var fade = SmoothStep(0, 0.08f, u) * (1 - SmoothStep(0.9f, 1, u));
            dc.DrawEllipse(new Ellipse(new Vector2(x, y), r, r), frame.Ink(_opacity[i] * fade), width);
        }
    }

    public void Release() { }

    public void Dispose() { }
}
