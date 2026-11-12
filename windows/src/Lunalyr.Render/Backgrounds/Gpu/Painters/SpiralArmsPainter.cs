// Spiral Arms: several logarithmic spiral arms from the centre, slowly turning; the song's seed picks how many (3...7).
// One arm (a polyline in pixels about the origin) is built in Prepare and drawn once per arm with a rotation, with a
// fainter companion trailing just behind it. Windows' own.

using System.Numerics;
using Vortice.Direct2D1;
using static Lunalyr.Render.Backgrounds.Gpu.Painters.PainterMath;

namespace Lunalyr.Render.Backgrounds.Gpu.Painters;

internal sealed class SpiralArmsPainter : ILinePainter
{
    private const int Steps = 120;
    private ID2D1PathGeometry? _arm;
    private int _arms;
    private float _rate, _start;

    public void Prepare(ID2D1DeviceContext dc, ID2D1Factory1 factory, Vector2 size, float scale, int seed)
    {
        Release();
        _arms = 3 + (int)(Hash(seed, 0, 1) * 5);
        var direction = Hash(seed, 0, 2) < 0.5f ? -1f : 1f;
        _rate = direction * Range(seed, 0, 3, 0.05f, 0.09f);
        _start = Hash(seed, 0, 4) * Tau;

        // r = inner × e^(k θ), from near the centre out past the corners.
        var inner = 0.025f * MathF.Min(size.X, size.Y);
        var outer = 0.62f * size.Length();
        var turns = Range(seed, 0, 5, 1.1f, 1.7f);
        var k = MathF.Log(outer / inner) / (turns * Tau);
        _arm = factory.CreatePathGeometry();
        using var sink = _arm.Open();
        sink.BeginFigure(new Vector2(inner, 0), FigureBegin.Hollow);
        for (var s = 1; s <= Steps; s++)
        {
            var theta = turns * Tau * s / Steps;
            sink.AddLine(Polar(inner * MathF.Exp(k * theta), direction * theta));
        }
        sink.EndFigure(FigureEnd.Open);
        sink.Close();
    }

    public void Draw(ID2D1DeviceContext dc, in LinePatternFrame frame)
    {
        if (_arm is null) return;
        var found = dc.Transform;
        var place = Matrix3x2.CreateTranslation(frame.Size * 0.5f) * found;
        var turn = Angle(frame.Phase, _rate, _start);
        var width = 0.9f * frame.Scale;
        for (var i = 0; i < _arms; i++)
        {
            var angle = turn + i * Tau / _arms;
            dc.Transform = Matrix3x2.CreateRotation(angle) * place;
            dc.DrawGeometry(_arm, frame.Ink(0.13f), width);
            dc.Transform = Matrix3x2.CreateRotation(angle - 0.16f * MathF.Sign(_rate)) * place;
            dc.DrawGeometry(_arm, frame.Ink(0.055f), width * 0.8f);
        }
        dc.Transform = found;
    }

    public void Release()
    {
        _arm?.Dispose();
        _arm = null;
    }

    public void Dispose() => Release();
}
