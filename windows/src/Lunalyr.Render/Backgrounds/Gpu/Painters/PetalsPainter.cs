// Petals: a flower of petal outlines in three nested layers around the centre, each layer turning at its own speed and
// direction; the song's seed picks the petal count. Each layer is one path geometry built in Prepare (in pixels, about
// the origin) and drawn with a rotation. Windows' own.

using System.Numerics;
using Vortice.Direct2D1;
using static Lunalyr.Render.Backgrounds.Gpu.Painters.PainterMath;

namespace Lunalyr.Render.Backgrounds.Gpu.Painters;

internal sealed class PetalsPainter : ILinePainter
{
    private const int Layers = 3;
    private static readonly float[] Reach = [0.17f, 0.30f, 0.45f];     // petal length, as a share of the shorter side
    private static readonly float[] Opacity = [0.13f, 0.11f, 0.09f];
    private readonly ID2D1PathGeometry?[] _layers = new ID2D1PathGeometry?[Layers];
    private readonly float[] _rate = new float[Layers];
    private readonly float[] _start = new float[Layers];

    public void Prepare(ID2D1DeviceContext dc, ID2D1Factory1 factory, Vector2 size, float scale, int seed)
    {
        Release();
        var petals = 5 + (int)(Hash(seed, 0, 1) * 5);   // 5...9
        var unit = MathF.Min(size.X, size.Y);
        for (var layer = 0; layer < Layers; layer++)
        {
            var length = Reach[layer] * unit * (layer == 2 ? 1.25f : 1f);
            var breadth = length * MathF.Tan(MathF.PI / petals) * Range(seed, layer, 2, 0.75f, 1.0f);
            var inner = length * (layer == 0 ? 0.02f : 0.25f);
            var offset = layer % 2 == 1 ? MathF.PI / petals : 0;
            var path = factory.CreatePathGeometry();
            using (var sink = path.Open())
            {
                for (var k = 0; k < petals; k++)
                {
                    var rot = Matrix3x2.CreateRotation(offset + k * Tau / petals);
                    Vector2 P(float x, float y) => Vector2.Transform(new Vector2(x, y), rot);
                    sink.BeginFigure(P(inner, 0), FigureBegin.Hollow);
                    sink.AddBezier(new BezierSegment { Point1 = P(inner + 0.30f * length, -breadth), Point2 = P(0.85f * length, -0.75f * breadth), Point3 = P(length, 0) });
                    sink.AddBezier(new BezierSegment { Point1 = P(0.85f * length, 0.75f * breadth), Point2 = P(inner + 0.30f * length, breadth), Point3 = P(inner, 0) });
                    sink.EndFigure(FigureEnd.Closed);
                }
                sink.Close();
            }
            _layers[layer] = path;
            var direction = (layer % 2 == 0) == (Hash(seed, layer, 3) < 0.5f) ? 1f : -1f;
            _rate[layer] = direction * Range(seed, layer, 4, 0.025f, 0.06f) / (1 + layer * 0.5f);
            _start[layer] = Hash(seed, layer, 5) * Tau;
        }
    }

    public void Draw(ID2D1DeviceContext dc, in LinePatternFrame frame)
    {
        var found = dc.Transform;
        var centre = frame.Size * 0.5f;
        var width = 0.9f * frame.Scale;
        for (var layer = 0; layer < Layers; layer++)
        {
            var geometry = _layers[layer];
            if (geometry is null) continue;
            dc.Transform = Matrix3x2.CreateRotation(Angle(frame.Phase, _rate[layer], _start[layer])) * Matrix3x2.CreateTranslation(centre) * found;
            dc.DrawGeometry(geometry, frame.Ink(Opacity[layer]), width);
        }
        dc.Transform = found;
    }

    public void Release()
    {
        for (var i = 0; i < Layers; i++)
        {
            _layers[i]?.Dispose();
            _layers[i] = null;
        }
    }

    public void Dispose() => Release();
}
