// Stroked shapes tessellated once (Direct2D geometry realizations), for painters that draw a fixed shape every frame
// turned or moved: drawing a path or a rotated ellipse with DrawGeometry tessellates it again on every frame, which
// measured 1–5 ms of render-thread time per frame for Orbits, Petals and Spiral Arms. Windows' own.

using System.Numerics;
using Vortice.Direct2D1;

namespace Lunalyr.Render.Backgrounds.Gpu.Painters;

internal sealed class Realizations : IDisposable
{
    /// <summary>Direct2D's default flattening tolerance; the shapes are only turned and moved, never scaled.</summary>
    private const float Tolerance = 0.25f;

    private ID2D1DeviceContext1? _dc;

    /// <summary>Takes up the device context the shapes are made and drawn with.</summary>
    public void Use(ID2D1DeviceContext dc)
    {
        _dc?.Dispose();
        _dc = dc.QueryInterface<ID2D1DeviceContext1>();
    }

    /// <summary><paramref name="geometry"/> stroked <paramref name="width"/> pixels wide, ready to draw with any transform.</summary>
    public ID2D1GeometryRealization Stroke(ID2D1Geometry geometry, float width) =>
        (_dc ?? throw new InvalidOperationException("No device context yet.")).CreateStrokedGeometryRealization(geometry, Tolerance, width, null);

    /// <summary>Draws <paramref name="shape"/> with <paramref name="brush"/> under the context's current transform.</summary>
    public void Draw(ID2D1GeometryRealization? shape, ID2D1Brush brush)
    {
        if (shape is not null) _dc?.DrawGeometryRealization(shape, brush);
    }

    public static Matrix3x2 Turned(float angle, Vector2 centre, Matrix3x2 found) =>
        Matrix3x2.CreateRotation(angle) * Matrix3x2.CreateTranslation(centre) * found;

    public void Dispose()
    {
        _dc?.Dispose();
        _dc = null;
    }
}
