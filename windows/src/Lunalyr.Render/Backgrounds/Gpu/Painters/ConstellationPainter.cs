// Constellation: points scattered around the centre, each joined by fine lines to its nearest neighbours (fainter the
// longer the line); the whole figure turns slowly and breathes in and out. The joins are worked out once in Prepare:
// turning and breathing move every point together, so neighbours stay neighbours. Windows' own.

using System.Numerics;
using Vortice.Direct2D1;
using static Lunalyr.Render.Backgrounds.Gpu.Painters.PainterMath;

namespace Lunalyr.Render.Backgrounds.Gpu.Painters;

internal sealed class ConstellationPainter : ILinePainter
{
    private const int MaxPoints = 76, Neighbours = 3, MaxJoins = MaxPoints * Neighbours;
    private readonly Vector2[] _points = new Vector2[MaxPoints];     // pixels about the centre
    private readonly float[] _twinkle = new float[MaxPoints];
    private readonly int[] _from = new int[MaxJoins];
    private readonly int[] _to = new int[MaxJoins];
    private readonly float[] _strength = new float[MaxJoins];
    private int _count, _joins;

    public void Prepare(ID2D1DeviceContext dc, ID2D1Factory1 factory, Vector2 size, float scale, int seed)
    {
        _count = 54 + (int)(Hash(seed, 0, 1) * (MaxPoints - 54));
        var outer = 0.52f * size.Length();
        for (var i = 0; i < _count; i++)
        {
            // Uniform over a circle reaching past the corners, so the turning figure never shows an empty side.
            var r = outer * MathF.Sqrt(Hash(seed, i, 2));
            var a = Hash(seed, i, 3) * Tau;
            _points[i] = Polar(r, a);
            _twinkle[i] = Hash(seed, i, 4) * Tau;
        }

        var reach = 0.30f * MathF.Min(size.X, size.Y);
        _joins = 0;
        Span<int> best = stackalloc int[Neighbours];
        Span<float> bestDistance = stackalloc float[Neighbours];
        for (var i = 0; i < _count; i++)
        {
            best.Fill(-1);
            bestDistance.Fill(float.MaxValue);
            for (var j = 0; j < _count; j++)
            {
                if (j == i) continue;
                var d = Vector2.Distance(_points[i], _points[j]);
                for (var k = 0; k < Neighbours; k++)
                {
                    if (d >= bestDistance[k]) continue;
                    for (var m = Neighbours - 1; m > k; m--) { best[m] = best[m - 1]; bestDistance[m] = bestDistance[m - 1]; }
                    best[k] = j;
                    bestDistance[k] = d;
                    break;
                }
            }
            for (var k = 0; k < Neighbours; k++)
            {
                var j = best[k];
                if (j < 0 || bestDistance[k] > reach || Joined(i, j)) continue;
                var closeness = 1 - bestDistance[k] / reach;
                _from[_joins] = i;
                _to[_joins] = j;
                _strength[_joins] = 0.04f + 0.12f * closeness;
                _joins++;
            }
        }
    }

    private bool Joined(int a, int b)
    {
        for (var k = 0; k < _joins; k++)
            if ((_from[k] == a && _to[k] == b) || (_from[k] == b && _to[k] == a)) return true;
        return false;
    }

    public void Draw(ID2D1DeviceContext dc, in LinePatternFrame frame)
    {
        var centre = frame.Size * 0.5f;
        var breathe = 1 + 0.05f * Wave(frame.Phase, 0.45);
        var turn = Matrix3x2.CreateRotation(Angle(frame.Phase, 0.04)) * Matrix3x2.CreateScale(breathe) * Matrix3x2.CreateTranslation(centre);
        var width = 0.7f * frame.Scale;
        for (var k = 0; k < _joins; k++)
            dc.DrawLine(Vector2.Transform(_points[_from[k]], turn), Vector2.Transform(_points[_to[k]], turn), frame.Ink(_strength[k]), width);
        var dot = 1.7f * frame.Scale;
        for (var i = 0; i < _count; i++)
        {
            var glint = 0.7f + 0.3f * Wave(frame.Phase, 1.3, _twinkle[i]);
            dc.FillEllipse(new Ellipse(Vector2.Transform(_points[i], turn), dot, dot), frame.Ink(0.28f * glint));
        }
    }

    public void Release() { }

    public void Dispose() { }
}
