// Fractal Tree: a binary branching tree growing up from the bottom centre, eight levels deep (255 branches). The
// shape (each branch's angle and length, varied by the song's seed) is fixed in Prepare; each frame the branches sway a
// little, more toward the tips, and the tree is walked from the trunk out. Branches get thinner and fainter toward the
// tips. Windows' own.

using System.Numerics;
using Vortice.Direct2D1;
using static Lunalyr.Render.Backgrounds.Gpu.Painters.PainterMath;

namespace Lunalyr.Render.Backgrounds.Gpu.Painters;

internal sealed class FractalTreePainter : ILinePainter
{
    private const int Depth = 8, Count = (1 << Depth) - 1;
    // Branch i's children are 2i+1 and 2i+2 (a heap), so parents always come first.
    private readonly float[] _bend = new float[Count];     // angle relative to the parent, radians
    private readonly float[] _length = new float[Count];   // pixels
    private readonly int[] _level = new int[Count];
    private readonly float[] _angle = new float[Count];
    private readonly Vector2[] _end = new Vector2[Count];

    public void Prepare(ID2D1DeviceContext dc, ID2D1Factory1 factory, Vector2 size, float scale, int seed)
    {
        var spread = Range(seed, 0, 1, 0.34f, 0.50f);
        var shrink = Range(seed, 0, 2, 0.70f, 0.77f);
        _length[0] = 0.21f * size.Y;
        _bend[0] = 0;
        _level[0] = 0;
        for (var i = 1; i < Count; i++)
        {
            var parent = (i - 1) / 2;
            var side = i % 2 == 1 ? -1f : 1f;
            _level[i] = _level[parent] + 1;
            _bend[i] = side * spread * Range(seed, i, 3, 0.7f, 1.25f) + Range(seed, i, 4, -0.08f, 0.08f);
            _length[i] = _length[parent] * shrink * Range(seed, i, 5, 0.85f, 1.12f);
        }
    }

    public void Draw(ID2D1DeviceContext dc, in LinePatternFrame frame)
    {
        var root = new Vector2(frame.Size.X * 0.5f, frame.Size.Y + 2 * frame.Scale);
        var gust = Wave(frame.Phase, 0.37);
        for (var i = 0; i < Count; i++)
        {
            var level = _level[i];
            var sway = (0.012f + 0.010f * level) * (0.6f * gust + 0.4f * Wave(frame.Phase, 0.9 + 0.13 * level, i * 0.7));
            Vector2 start;
            if (i == 0)
            {
                _angle[0] = -MathF.PI / 2 + sway;
                start = root;
            }
            else
            {
                var parent = (i - 1) / 2;
                _angle[i] = _angle[parent] + _bend[i] + sway;
                start = _end[parent];
            }
            _end[i] = start + Polar(_length[i], _angle[i]);
            var width = MathF.Max(0.6f, 2.6f * MathF.Pow(0.72f, level)) * frame.Scale;
            dc.DrawLine(start, _end[i], frame.Ink(0.16f - 0.010f * level), width);
        }
    }

    public void Release() { }

    public void Dispose() { }
}
