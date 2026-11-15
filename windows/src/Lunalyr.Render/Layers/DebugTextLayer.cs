using System.Globalization;
using System.Numerics;
using Lunalyr.Render.Scene;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Lunalyr.Render.Layers;

/// <summary>
/// A diagnostic "lyric" layer: the scene's title/status and song time, a status block from the engine, and a bar
/// sweeping along the bottom every frame (so frame pacing is visible and the surface never idles). Text is
/// re-formatted a few times a second, not every frame.
/// </summary>
public sealed class DebugTextLayer(Func<string>? status = null) : IRenderLayer
{
    private const double TextRefreshSeconds = 0.25;

    private ID2D1SolidColorBrush? _text, _shade, _bar;
    private IDWriteTextFormat? _big, _small;
    private Vector2 _size;
    private float _scale = 1;
    private string _headline = "", _detail = "";
    private double _nextText = double.NegativeInfinity;

    public bool IsAnimating => true;

    public void CreateResources(ID2D1DeviceContext dc, IDWriteFactory dwrite, Vector2 size, float scale)
    {
        _size = size;
        _scale = scale;
        _text = dc.CreateSolidColorBrush(new Color4(1, 1, 1, 0.95f));
        _shade = dc.CreateSolidColorBrush(new Color4(0, 0, 0, 0.35f));
        _bar = dc.CreateSolidColorBrush(new Color4(1, 1, 1, 0.8f));
        CreateFormats(dwrite);
        _nextText = double.NegativeInfinity;
    }

    private void CreateFormats(IDWriteFactory dwrite)
    {
        _big?.Dispose();
        _small?.Dispose();
        _big = dwrite.CreateTextFormat("Segoe UI Variable Display", null, FontWeight.SemiBold, FontStyle.Normal, FontStretch.Normal,
            Math.Max(12, _size.Y * 0.06f), "en-us");
        _big.TextAlignment = TextAlignment.Center;
        _big.ParagraphAlignment = ParagraphAlignment.Center;
        _small = dwrite.CreateTextFormat("Cascadia Mono", null, FontWeight.Normal, FontStyle.Normal, FontStretch.Normal,
            14 * _scale, "en-us");
    }

    public void Resize(Vector2 size, float scale)
    {
        _size = size;
        _scale = scale;
        _nextText = double.NegativeInfinity;
    }

    public void Render(in FrameContext frame)
    {
        if (_text is null || _big is null || _small is null || _shade is null || _bar is null) return;
        if (frame.Size != _size)
        {
            _size = frame.Size;
            CreateFormats(frame.DWrite);
        }
        var dc = frame.Dc;
        if (frame.WallSeconds >= _nextText)
        {
            _nextText = frame.WallSeconds + TextRefreshSeconds;
            var scene = frame.Scene;
            string song = double.IsNaN(frame.SongSeconds) ? "--:--"
                : TimeSpan.FromSeconds(Math.Max(0, frame.SongSeconds)).ToString(@"m\:ss\.f", CultureInfo.InvariantCulture);
            _headline = scene.Status ?? (scene.Title is { } t ? $"{t}\n{scene.Artist}" : "Lunalyr render engine");
            _headline += $"\n{song}{(frame.IsPlaying ? " ▶" : "")}";
            _detail = $"{(int)_size.X}x{(int)_size.Y} @{_scale * 100:0}%  {DateTime.Now:HH:mm:ss}\n{status?.Invoke()}";
        }
        float pad = 16 * _scale;
        dc.FillRectangle(new Rect(0, _size.Y * 0.3f, _size.X, _size.Y * 0.4f), _shade);
        dc.DrawText(_headline, _big, new Rect(0, _size.Y * 0.3f, _size.X, _size.Y * 0.4f), _text);
        dc.DrawText(_detail, _small, new Rect(pad, pad, _size.X - 2 * pad, _size.Y * 0.25f), _text);
        // A bar sweeping across every 4 s: smooth motion shows dropped or uneven frames at a glance.
        float barW = _size.X * 0.08f, barH = 6 * _scale;
        float x = (float)((frame.WallSeconds % 4.0) / 4.0) * (_size.X + barW) - barW;
        dc.FillRectangle(new Rect(x, _size.Y - barH - pad * 4, barW, barH), _bar);
    }

    public void ReleaseResources()
    {
        _text?.Dispose(); _text = null;
        _shade?.Dispose(); _shade = null;
        _bar?.Dispose(); _bar = null;
        _big?.Dispose(); _big = null;
        _small?.Dispose(); _small = null;
    }

    public void Dispose() => ReleaseResources();
}

/// <summary>Registers <see cref="DebugTextLayer"/> as "debug"; <paramref name="status"/> supplies extra lines (e.g. engine stats).</summary>
public sealed class DebugTextLayerFactory(Func<string>? status = null, string id = "debug") : IRenderLayerFactory
{
    public string Id => id;
    public IRenderLayer Create() => new DebugTextLayer(status);
}
