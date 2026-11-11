using System.Numerics;
using Lunalyr.Render.Scene;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Lunalyr.Render.Layers;

/// <summary>
/// The simplest background: one colour over the whole surface, the scene palette's base colour unless the factory
/// fixes one. It never animates, so a wallpaper showing it (and no advancing lyric) idles on a still frame.
/// </summary>
public sealed class SolidColorLayer(Vector4? color = null) : IRenderLayer
{
    private ID2D1SolidColorBrush? _brush;
    private Vector2 _size;

    public bool IsAnimating => false;

    public void CreateResources(ID2D1DeviceContext dc, IDWriteFactory dwrite, Vector2 size, float scale)
    {
        _brush = dc.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
        _size = size;
    }

    public void Resize(Vector2 size, float scale) => _size = size;

    public void Render(in FrameContext frame)
    {
        if (_brush is null) return;
        var c = color ?? frame.Scene.Palette.Base;
        _brush.Color = new Color4(c.X, c.Y, c.Z, 1);
        frame.Dc.FillRectangle(new Rect(0, 0, _size.X, _size.Y), _brush);
    }

    public void ReleaseResources()
    {
        _brush?.Dispose();
        _brush = null;
    }

    public void Dispose() => ReleaseResources();
}

/// <summary>Registers <see cref="SolidColorLayer"/> under <paramref name="id"/> ("solid" by default).</summary>
public sealed class SolidColorLayerFactory(string id = "solid", Vector4? color = null) : IRenderLayerFactory
{
    public string Id => id;
    public IRenderLayer Create() => new SolidColorLayer(color);
}
