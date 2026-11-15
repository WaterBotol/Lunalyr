// Rain: thin slanted streaks falling at different speeds; faster streaks are longer and a little brighter. Each streak
// starts above the screen and is recycled once it has fallen out of the bottom. Windows' own.

using System.Numerics;
using Vortice.Direct2D1;
using static Lunalyr.Render.Backgrounds.Gpu.Painters.PainterMath;

namespace Lunalyr.Render.Backgrounds.Gpu.Painters;

internal sealed class DiagonalRainPainter : ILinePainter
{
    private const int Count = 120;
    private readonly float[] _x = new float[Count];
    private readonly float[] _rate = new float[Count];
    private readonly float[] _start = new float[Count];
    private readonly float[] _length = new float[Count];   // points
    private readonly float[] _opacity = new float[Count];
    private Vector2 _fall;

    public void Prepare(ID2D1DeviceContext dc, ID2D1Factory1 factory, Vector2 size, float scale, int seed)
    {
        var slant = Range(seed, 0, 1, 0.16f, 0.30f) * (Hash(seed, 0, 2) < 0.5f ? -1 : 1);
        _fall = new Vector2(MathF.Sin(slant), MathF.Cos(slant));
        for (var i = 0; i < Count; i++)
        {
            var fast = Hash(seed, i, 3);
            _x[i] = Hash(seed, i, 4);
            _rate[i] = 0.22f + 0.6f * fast * fast;
            _start[i] = Hash(seed, i, 5);
            _length[i] = 16 + 60 * fast;
            _opacity[i] = 0.05f + 0.09f * fast;
        }
    }

    public void Draw(ID2D1DeviceContext dc, in LinePatternFrame frame)
    {
        var width = 0.8f * frame.Scale;
        var height = frame.Size.Y;
        // Streaks start along a line above the screen wide enough that slanted ones still cover every corner.
        var drift = _fall.X / _fall.Y * height;
        var left = MathF.Min(0, -drift);
        var across = frame.Size.X + MathF.Abs(drift);
        for (var i = 0; i < Count; i++)
        {
            var length = _length[i] * frame.Scale;
            var travel = (height + length) / _fall.Y;
            var s = Cycle(frame.Phase, _rate[i], _start[i]) * travel;
            var top = new Vector2(left + _x[i] * across, 0);
            var head = top + _fall * s;
            dc.DrawLine(head - _fall * length, head, frame.Ink(_opacity[i]), width);
        }
    }

    public void Release() { }

    public void Dispose() { }
}
