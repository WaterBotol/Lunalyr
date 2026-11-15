// Circuitry: right-angled traces on a grid with small round pads at their ends, the whole board drifting gently back
// and forth; each trace glows a little brighter and dimmer on its own slow beat. The traces are laid out once in
// Prepare (seeded walks on the grid); Draw only offsets them. Windows' own.

using System.Numerics;
using Vortice.Direct2D1;
using static Lunalyr.Render.Backgrounds.Gpu.Painters.PainterMath;

namespace Lunalyr.Render.Backgrounds.Gpu.Painters;

internal sealed class CircuitryPainter : ILinePainter
{
    private const int Traces = 46, MaxTurns = 5, MaxPoints = Traces * (MaxTurns + 1);
    private const float Pitch = 24f;    // points between grid lines
    private const float Drift = 18f;    // points
    private readonly Vector2[] _points = new Vector2[MaxPoints];
    private readonly int[] _first = new int[Traces];
    private readonly int[] _length = new int[Traces];   // points in the trace
    private readonly float[] _level = new float[Traces];
    private readonly float[] _beat = new float[Traces];

    public void Prepare(ID2D1DeviceContext dc, ID2D1Factory1 factory, Vector2 size, float scale, int seed)
    {
        var pitch = Pitch * scale;
        var margin = (Drift + 2 * Pitch) * scale;
        var columns = (int)((size.X + 2 * margin) / pitch);
        var rows = (int)((size.Y + 2 * margin) / pitch);
        var used = 0;
        for (var t = 0; t < Traces; t++)
        {
            var x = (int)(Hash(seed, t, 1) * columns);
            var y = (int)(Hash(seed, t, 2) * rows);
            var horizontal = Hash(seed, t, 3) < 0.5f;
            var sign = Hash(seed, t, 4) < 0.5f ? -1 : 1;
            var turns = 2 + (int)(Hash(seed, t, 5) * (MaxTurns - 1));
            _first[t] = used;
            _points[used++] = Node(x, y);
            for (var k = 0; k < turns; k++)
            {
                var run = 2 + (int)(Hash(seed, t * 8 + k, 6) * 6);
                if (horizontal) x += sign * run; else y += sign * run;
                _points[used++] = Node(x, y);
                horizontal = !horizontal;
                if (Hash(seed, t * 8 + k, 7) < 0.35f) sign = -sign;
            }
            _length[t] = turns + 1;
            _level[t] = Range(seed, t, 8, 0.06f, 0.12f);
            _beat[t] = Hash(seed, t, 9) * Tau;
        }

        Vector2 Node(int gx, int gy) => new(gx * pitch - margin, gy * pitch - margin);
    }

    public void Draw(ID2D1DeviceContext dc, in LinePatternFrame frame)
    {
        var drift = Drift * frame.Scale;
        var offset = new Vector2(drift * Wave(frame.Phase, 0.11), 0.7f * drift * Wave(frame.Phase, 0.083, 1.3));
        var width = 0.85f * frame.Scale;
        var pad = 2.6f * frame.Scale;
        for (var t = 0; t < Traces; t++)
        {
            var opacity = _level[t] * (0.75f + 0.25f * Wave(frame.Phase, 0.6, _beat[t]));
            var ink = frame.Ink(opacity);
            var first = _first[t];
            var last = first + _length[t] - 1;
            for (var k = first; k < last; k++) dc.DrawLine(_points[k] + offset, _points[k + 1] + offset, ink, width);
            dc.DrawEllipse(new Ellipse(_points[first] + offset, pad, pad), frame.Ink(opacity * 1.6f), width);
            dc.FillEllipse(new Ellipse(_points[last] + offset, pad * 0.8f, pad * 0.8f), frame.Ink(opacity * 1.8f));
        }
    }

    public void Release() { }

    public void Dispose() { }
}
