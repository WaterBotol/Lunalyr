using System.Numerics;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Lunalyr.Render.Compositions;

/// <summary>The faces a composition sets type in.</summary>
internal enum Face
{
    /// <summary>The lyric family (the style's), regular words.</summary>
    Regular,
    /// <summary>The lyric family, held words and Spoken (the Mac's heavy cut).</summary>
    Heavy,
    /// <summary>Editorial's contrasting face: a serif against the sans.</summary>
    Contrast,
    /// <summary>Readings and notices: the lyric family, lighter.</summary>
    Quiet,
}

/// <summary>
/// Fonts, text layouts and baked text for one surface. Text is rasterised once per line into bitmaps (grayscale
/// antialiasing, colour fonts on) on a device context of its own, never the engine's, so a frame only draws bitmaps.
/// </summary>
internal sealed class TextKit : IDisposable
{
    private readonly IDWriteFactory _dwrite;
    private readonly ID2D1DeviceContext _bake;
    private readonly ID2D1DeviceContext _draw;
    private readonly ID2D1SolidColorBrush _brush;
    private readonly IDWriteFontCollection _fonts;
    private readonly Dictionary<(string Family, FontWeight Weight, float Size, string Locale), IDWriteTextFormat> _formats = [];
    private readonly Vortice.Direct2D1.Effects.Shadow _shadow;
    private readonly ID2D1Image _shadowOut;
    private string _family = "";
    private string _resolvedFamily = "Segoe UI";
    private string _contrastFamily = "Georgia";

    public TextKit(ID2D1DeviceContext dc, IDWriteFactory dwrite)
    {
        _dwrite = dwrite;
        _draw = dc;
        using (var device = dc.Device) _bake = device.CreateDeviceContext(DeviceContextOptions.None);
        _bake.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        _bake.SetDpi(96, 96);
        _brush = _bake.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
        _fonts = dwrite.GetSystemFontCollection(false);
        _shadow = new Vortice.Direct2D1.Effects.Shadow(_bake) { Optimization = ShadowOptimization.Quality };
        _shadowOut = _shadow.Output;
    }

    /// <summary>The device context words are drawn on (the engine's): their effects are created on it.</summary>
    public ID2D1DeviceContext Draw => _draw;

    /// <summary>Uses <paramref name="family"/> for lyrics when it is installed (Segoe UI otherwise).</summary>
    public void SetFamily(string family)
    {
        if (family == _family) return;
        _family = family;
        _resolvedFamily = Installed(family) ? family : Installed("Segoe UI Variable Display") ? "Segoe UI Variable Display" : "Segoe UI";
        _contrastFamily = Installed("Georgia") ? "Georgia" : Installed("Cambria") ? "Cambria" : _resolvedFamily;
        foreach (var format in _formats.Values) format.Dispose();
        _formats.Clear();
    }

    private bool Installed(string family)
    {
        return !string.IsNullOrWhiteSpace(family) && _fonts.FindFamilyName(family, out _);
    }

    public IDWriteTextFormat Format(Face face, float size, string locale)
    {
        var (family, weight) = face switch
        {
            Face.Heavy => (_resolvedFamily, FontWeight.Bold),
            Face.Contrast => (_contrastFamily, FontWeight.Normal),
            Face.Quiet => (_resolvedFamily, FontWeight.Normal),
            _ => (_resolvedFamily, FontWeight.Medium),
        };
        size = MathF.Max(1, MathF.Round(size * 4) / 4);
        var key = (family, weight, size, locale);
        if (_formats.TryGetValue(key, out var found)) return found;
        if (_formats.Count > 96)
        {
            foreach (var format in _formats.Values) format.Dispose();
            _formats.Clear();
        }
        var made = _dwrite.CreateTextFormat(family, null, weight, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, size, locale);
        made.WordWrapping = WordWrapping.NoWrap;
        _formats[key] = made;
        return made;
    }

    /// <summary>
    /// A laid-out label on one line (or wrapped and centred at <paramref name="wrapWidth"/>), right to left when its
    /// script reads that way. The caller disposes it.
    /// </summary>
    public IDWriteTextLayout Layout(string text, Face face, float size, string locale, bool rightToLeft, float wrapWidth = 0)
    {
        var format = Format(face, size, locale);
        var layout = _dwrite.CreateTextLayout(text, format, 100_000, 100_000);
        layout.ReadingDirection = rightToLeft ? ReadingDirection.RightToLeft : ReadingDirection.LeftToRight;
        if (wrapWidth > 0)
        {
            layout.WordWrapping = WordWrapping.Wrap;
            layout.TextAlignment = TextAlignment.Center;
            layout.MaxWidth = wrapWidth;
            var natural = layout.Metrics.WidthIncludingTrailingWhitespace;
            layout.MaxWidth = MathF.Min(wrapWidth, MathF.Ceiling(natural + 1));
        }
        else
        {
            layout.MaxWidth = MathF.Ceiling(layout.Metrics.WidthIncludingTrailingWhitespace + 1);
        }
        layout.MaxHeight = MathF.Ceiling(layout.Metrics.Height + 1);
        return layout;
    }

    /// <summary>
    /// Bakes a layout into an <see cref="Ink"/>: the text in <paramref name="colour"/> at <paramref name="raster"/>
    /// bitmap pixels per layout pixel, and its soft shadow at half that resolution.
    /// </summary>
    public Ink Bake(IDWriteTextLayout layout, Vector4 colour, float raster, ShadowSpec shadow)
    {
        var metrics = layout.Metrics;
        var overhang = layout.OverhangMetrics;
        var box = new Vector2(MathF.Max(1, metrics.Width), MathF.Max(1, metrics.Height));
        var reach = MathF.Max(MathF.Max(overhang.Left, overhang.Right), MathF.Max(overhang.Top, overhang.Bottom));
        var pad = MathF.Ceiling(MathF.Max(0, reach) + 2);
        raster = Math.Clamp(raster, 0.1f, 4f);
        var width = (int)MathF.Ceiling((box.X + 2 * pad) * raster);
        var height = (int)MathF.Ceiling((box.Y + 2 * pad) * raster);
        var properties = new BitmapProperties1(new PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target);
        var text = _bake.CreateBitmap(new SizeI(Math.Max(1, width), Math.Max(1, height)), IntPtr.Zero, 0, properties);
        _brush.Color = new Color4(colour.X, colour.Y, colour.Z, colour.W);
        var origin = new Vector2(pad - metrics.Left, pad - metrics.Top);
        Render(text, Matrix3x2.CreateScale(raster), layout, origin);

        ID2D1Bitmap1? soft = null;
        float softRaster = raster / 2, softPad = 0;
        if (shadow.Alpha > 0)
        {
            // The shadow is a blur: half resolution is plenty.
            var sigma = shadow.Sigma;
            softPad = MathF.Ceiling(pad + 3 * sigma + MathF.Abs(shadow.OffsetY) + 2);
            var sw = (int)MathF.Ceiling((box.X + 2 * softPad) * softRaster);
            var sh = (int)MathF.Ceiling((box.Y + 2 * softPad) * softRaster);
            using var source = _bake.CreateBitmap(new SizeI(Math.Max(1, sw), Math.Max(1, sh)), IntPtr.Zero, 0, properties);
            _brush.Color = new Color4(0, 0, 0, 1);
            Render(source, Matrix3x2.CreateScale(softRaster), layout, new Vector2(softPad - metrics.Left, softPad - metrics.Top + shadow.OffsetY));
            soft = _bake.CreateBitmap(new SizeI(Math.Max(1, sw), Math.Max(1, sh)), IntPtr.Zero, 0, properties);
            _shadow.SetInput(0, source, true);
            _shadow.BlurStandardDeviation = MathF.Max(0.01f, sigma * softRaster);
            _shadow.Color = new Vector4(shadow.Colour, shadow.Alpha);
            _bake.Target = soft;
            _bake.BeginDraw();
            _bake.Clear(new Color4(0, 0, 0, 0));
            _bake.Transform = Matrix3x2.Identity;
            _bake.DrawImage(_shadowOut, InterpolationMode.Linear, CompositeMode.SourceOver);
            _bake.EndDraw();
            _bake.Target = null;
            _shadow.SetInput(0, null!, true);
        }
        return new Ink(_draw, text, raster, new Vector2(pad + box.X / 2, pad + box.Y / 2) * raster, box, soft, softRaster,
                       new Vector2(softPad + box.X / 2, softPad + box.Y / 2) * softRaster);
    }

    /// <summary>
    /// Bakes a label's tile into an <see cref="Ink"/>: a rounded rectangle of <paramref name="size"/> layout pixels
    /// (corners of <paramref name="radius"/>) in <paramref name="colour"/>, and its soft shadow at half resolution.
    /// </summary>
    public Ink BakeTile(Vector2 size, float radius, Vector4 colour, float raster, ShadowSpec shadow)
    {
        var box = new Vector2(MathF.Max(1, size.X), MathF.Max(1, size.Y));
        const float pad = 2;
        raster = Math.Clamp(raster, 0.1f, 4f);
        var properties = new BitmapProperties1(new PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), 96, 96, BitmapOptions.Target);
        var width = (int)MathF.Ceiling((box.X + 2 * pad) * raster);
        var height = (int)MathF.Ceiling((box.Y + 2 * pad) * raster);
        var tile = _bake.CreateBitmap(new SizeI(Math.Max(1, width), Math.Max(1, height)), IntPtr.Zero, 0, properties);
        _brush.Color = new Color4(colour.X, colour.Y, colour.Z, colour.W);
        FillRounded(tile, raster, new Vector2(pad, pad), box, radius);

        ID2D1Bitmap1? soft = null;
        float softRaster = raster / 2, softPad = 0;
        if (shadow.Alpha > 0)
        {
            var sigma = shadow.Sigma;
            softPad = MathF.Ceiling(pad + 3 * sigma + MathF.Abs(shadow.OffsetY) + 2);
            var sw = (int)MathF.Ceiling((box.X + 2 * softPad) * softRaster);
            var sh = (int)MathF.Ceiling((box.Y + 2 * softPad) * softRaster);
            using var source = _bake.CreateBitmap(new SizeI(Math.Max(1, sw), Math.Max(1, sh)), IntPtr.Zero, 0, properties);
            _brush.Color = new Color4(0, 0, 0, 1);
            FillRounded(source, softRaster, new Vector2(softPad, softPad + shadow.OffsetY), box, radius);
            soft = _bake.CreateBitmap(new SizeI(Math.Max(1, sw), Math.Max(1, sh)), IntPtr.Zero, 0, properties);
            _shadow.SetInput(0, source, true);
            _shadow.BlurStandardDeviation = MathF.Max(0.01f, sigma * softRaster);
            _shadow.Color = new Vector4(shadow.Colour, shadow.Alpha);
            _bake.Target = soft;
            _bake.BeginDraw();
            _bake.Clear(new Color4(0, 0, 0, 0));
            _bake.Transform = Matrix3x2.Identity;
            _bake.DrawImage(_shadowOut, InterpolationMode.Linear, CompositeMode.SourceOver);
            _bake.EndDraw();
            _bake.Target = null;
            _shadow.SetInput(0, null!, true);
        }
        return new Ink(_draw, tile, raster, new Vector2(pad + box.X / 2, pad + box.Y / 2) * raster, box, soft, softRaster,
                       new Vector2(softPad + box.X / 2, softPad + box.Y / 2) * softRaster);
    }

    private void FillRounded(ID2D1Bitmap1 target, float raster, Vector2 origin, Vector2 box, float radius)
    {
        _bake.Target = target;
        _bake.BeginDraw();
        _bake.Clear(new Color4(0, 0, 0, 0));
        _bake.Transform = Matrix3x2.CreateScale(raster);
        _bake.FillRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(origin.X, origin.Y, box.X, box.Y), radius, radius), _brush);
        _bake.Transform = Matrix3x2.Identity;
        _bake.EndDraw();
        _bake.Target = null;
    }

    private void Render(ID2D1Bitmap1 target, Matrix3x2 transform, IDWriteTextLayout layout, Vector2 origin)
    {
        _bake.Target = target;
        _bake.BeginDraw();
        _bake.Clear(new Color4(0, 0, 0, 0));
        _bake.Transform = transform;
        _bake.DrawTextLayout(origin, layout, _brush, DrawTextOptions.EnableColorFont);
        _bake.Transform = Matrix3x2.Identity;
        _bake.EndDraw();
        _bake.Target = null;
    }

    /// <summary>
    /// A locale for text, so CJK fallback picks the right forms: Japanese when there is kana, Korean for Hangul, Chinese
    /// for Han alone; otherwise English.
    /// </summary>
    public static string LocaleFor(string text)
    {
        bool han = false, hangul = false;
        foreach (var rune in text.EnumerateRunes())
        {
            var v = rune.Value;
            if (v is >= 0x3040 and <= 0x30FF or >= 0x31F0 and <= 0x31FF or >= 0xFF66 and <= 0xFF9F) return "ja-jp";
            if (v is >= 0xAC00 and <= 0xD7AF or >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F) hangul = true;
            else if (v is >= 0x3400 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x323AF) han = true;
        }
        return hangul ? "ko-kr" : han ? "zh-cn" : "en-us";
    }

    public void Dispose()
    {
        foreach (var format in _formats.Values) format.Dispose();
        _formats.Clear();
        _shadowOut.Dispose();
        _shadow.Dispose();
        _fonts.Dispose();
        _brush.Dispose();
        _bake.Dispose();
    }
}

/// <summary>A soft drop shadow baked under text: opacity, blur (standard deviation, pixels), drop (pixels) and colour (black by default).</summary>
internal readonly record struct ShadowSpec(float Alpha, float Sigma, float OffsetY, Vector3 Colour = default)
{
    public static ShadowSpec None => default;
}
