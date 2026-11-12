// Orbits: a dozen nested ellipses around the centre, each turning slowly at its own rate and direction, each with a
// small bead travelling along it. Windows' own.

using System.Numerics;
using Vortice.Direct2D1;
using static Lunalyr.Render.Backgrounds.Gpu.Painters.PainterMath;

namespace Lunalyr.Render.Backgrounds.Gpu.Painters;

internal sealed class OrbitsPainter : ILinePainter
{
    private const int Count = 12;
    private readonly float[] _rx = new float[Count];
    private readonly float[] _ry = new float[Count];
    private readonly float[] _turnRate = new float[Count];
    private readonly float[] _turnStart = new float[Count];
    private readonly float[] _beadRate = new float[Count];
    private readonly float[] _beadStart = new float[Count];
    private readonly ID2D1GeometryRealization?[] _rings = new ID2D1GeometryRealization?[Count];
    private readonly Realizations _shapes = new();

    public void Prepare(ID2D1DeviceContext dc, ID2D1Factory1 factory, Vector2 size, float scale, int seed)
    {
        Release();
        _shapes.Use(dc);
        var reach = 0.5f * MathF.Max(size.X, size.Y);
        for (var i = 0; i < Count; i++)
        {
            var step = (i + 1f) / Count;
            _rx[i] = reach * (0.08f + 0.92f * step) * Range(seed, i, 1, 0.92f, 1.05f);
            _ry[i] = _rx[i] * Range(seed, i, 2, 0.38f, 0.72f);
            var direction = Hash(seed, i, 3) < 0.5f ? -1f : 1f;
            _turnRate[i] = direction * Range(seed, i, 4, 0.03f, 0.11f);
            _turnStart[i] = Hash(seed, i, 5) * Tau;
            _beadRate[i] = direction * Range(seed, i, 6, 0.15f, 0.45f) / (0.4f + step);
            _beadStart[i] = Hash(seed, i, 7) * Tau;
            // Tessellated once here, turned each frame.
            using var ellipse = factory.CreateEllipseGeometry(new Ellipse(Vector2.Zero, _rx[i], _ry[i]));
            _rings[i] = _shapes.Stroke(ellipse, 0.8f * scale);
        }
    }

    public void Draw(ID2D1DeviceContext dc, in LinePatternFrame frame)
    {
        var found = dc.Transform;
        var centre = frame.Size * 0.5f;
        var bead = 1.7f * frame.Scale;
        for (var i = 0; i < Count; i++)
        {
            dc.Transform = Realizations.Turned(Angle(frame.Phase, _turnRate[i], _turnStart[i]), centre, found);
            _shapes.Draw(_rings[i], frame.Ink(0.07f + 0.05f * (1 - i / (float)Count)));
            var a = Angle(frame.Phase, _beadRate[i], _beadStart[i]);
            var at = new Vector2(_rx[i] * MathF.Cos(a), _ry[i] * MathF.Sin(a));
            dc.FillEllipse(new Ellipse(at, bead, bead), frame.Ink(0.26f));
        }
        dc.Transform = found;
    }

    public void Release()
    {
        for (var i = 0; i < Count; i++)
        {
            _rings[i]?.Dispose();
            _rings[i] = null;
        }
        _shapes.Dispose();
    }

    public void Dispose() => Release();
}
