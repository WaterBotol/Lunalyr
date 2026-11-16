using System.Numerics;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Lunalyr.Render.Compositions;

/// <summary>A glow drawn around text: colour (straight alpha in W) and blur radius in device pixels.</summary>
internal readonly record struct GlowSpec(Vector4 Colour, float Radius)
{
    public static GlowSpec None => default;
    public bool Visible => Colour.W > 0.004f && Radius > 0;
}

/// <summary>
/// One baked piece of text (a word, a run, a reading) and its effect graph, built once per line: the text bitmap with
/// a blur and an opacity effect over it, a soft shadow bitmap, and a glow made when first needed. A settled word is a
/// plain bitmap draw; only a word in transit goes through its effects.
/// <para>
/// Its size and placement are known as soon as it is laid out; its bitmaps may be baked later (<see cref="TextKit.Defers"/>):
/// a frame or two after, a slice at a time, or at the latest when it is first drawn, so what is drawn never changes.
/// </para>
/// </summary>
internal sealed class Ink : IDisposable
{
    private readonly ID2D1DeviceContext _owner;
    private ID2D1Bitmap1? _text;
    private ID2D1Bitmap1? _soft;
    private Vortice.Direct2D1.Effects.GaussianBlur? _blur;
    private Vortice.Direct2D1.Effects.Opacity? _opacity;
    private ID2D1Image? _blurred;
    private Vortice.Direct2D1.Effects.Shadow? _glow;
    private ID2D1Image? _glowOut;
    private Vortice.Direct2D1.Effects.Shadow? _tint;
    private ID2D1Image? _tintOut;
    private bool _disposed;

    /// <summary>An ink still to be baked by <paramref name="kit"/> (see <see cref="TextKit.Bake"/>).</summary>
    /// <param name="pixels">The text bitmap's size; <paramref name="softPixels"/> the shadow's (zero for none).</param>
    internal Ink(ID2D1DeviceContext owner, TextKit kit, float raster, Vector2 centre, Vector2 box, (int Width, int Height) pixels,
                 bool hasSoft, float softRaster, Vector2 softCentre, (int Width, int Height) softPixels)
    {
        _owner = owner;
        Kit = kit;
        Raster = raster;
        Centre = centre;
        Box = box;
        HasSoft = hasSoft;
        SoftRaster = softRaster;
        SoftCentre = softCentre;
        PixelWidth = pixels.Width;
        PixelHeight = pixels.Height;
        if (hasSoft)
        {
            SoftWidth = softPixels.Width;
            SoftHeight = softPixels.Height;
        }
    }

    /// <summary>
    /// Where each letter sits (a one-line text's clusters, left to right), for an action word's per-letter pass; null
    /// for a tile, a symbol or a text on several lines.
    /// </summary>
    public InkLetters? Letters { get; set; }

    /// <summary>The kit baking it, until it is baked.</summary>
    internal TextKit? Kit { get; private set; }

    /// <summary>What is left of its bake (the kit's), or null once baked or let go.</summary>
    internal object? Pending { get; set; }

    /// <summary>True once its bitmaps are made.</summary>
    public bool IsBaked => _text is not null && !_disposed;

    /// <summary>The text bitmap (baked now if it was not yet).</summary>
    public ID2D1Bitmap1 Text => Bake() ? _text! : throw new ObjectDisposedException(nameof(Ink));
    /// <summary>Bitmap pixels per layout pixel.</summary>
    public float Raster { get; }
    /// <summary>The centre of the text's layout box, in bitmap pixels.</summary>
    public Vector2 Centre { get; }
    /// <summary>The layout box, in layout pixels.</summary>
    public Vector2 Box { get; }
    /// <summary>The soft shadow bitmap (baked now if it was not yet), or null for none.</summary>
    public ID2D1Bitmap1? Soft => HasSoft && Bake() ? _soft : null;
    /// <summary>Whether it has a soft shadow.</summary>
    public bool HasSoft { get; }
    public float SoftRaster { get; }
    public Vector2 SoftCentre { get; }
    public int PixelWidth { get; }
    public int PixelHeight { get; }
    public int SoftWidth { get; }
    public int SoftHeight { get; }

    /// <summary>Bakes it now if its bake is still waiting; false once it has been let go.</summary>
    public bool Bake()
    {
        if (Kit is { } kit) kit.Complete(this);
        return !_disposed && _text is not null;
    }

    /// <summary>Takes its baked bitmaps (the kit's last step) and makes the effects a frame draws it through.</summary>
    internal void Finish(ID2D1Bitmap1 text, ID2D1Bitmap1? soft)
    {
        _text = text;
        _soft = soft;
        _blur = new Vortice.Direct2D1.Effects.GaussianBlur(_owner) { BorderMode = BorderMode.Soft, Optimization = GaussianBlurOptimization.Balanced };
        _blur.SetInput(0, text, true);
        _opacity = new Vortice.Direct2D1.Effects.Opacity(_owner);
        _opacity.SetInputEffect(0, _blur, true);
        _blurred = _opacity.Output;
        Kit = null;
        Pending = null;
    }

    /// <summary>
    /// Draws the text with its shadow and glow. <paramref name="world"/> maps layout pixels (origin at the centre of
    /// the layout box) to the device; <paramref name="worldScale"/> is its scale. Blur and glow radii are in device
    /// pixels. With <paramref name="boundaries"/> and <paramref name="tokenOpacities"/>, each token's share of the
    /// label (by character, in reading order) is drawn at its own opacity, so a glued run's syllables arrive one by one.
    /// </summary>
    /// <param name="clip">Only this stretch across the text is drawn (a cutout unmasked from its leading edge, a sweep).</param>
    public void Draw(ID2D1DeviceContext dc, in Matrix3x2 world, float worldScale, float opacity, float blur, GlowSpec glow,
                     float shadowOpacity, ReadOnlySpan<double> boundaries = default,
                     ReadOnlySpan<double> tokenOpacities = default, bool rightToLeft = false, InkClip clip = default)
    {
        if (!(opacity > 0.002f) || !(worldScale > 0.0001f)) return;
        if (clip.IsSet && !(clip.Right > clip.Left)) return;
        if (!Bake()) return;
        var sliced = tokenOpacities.Length > 1 && boundaries.Length == tokenOpacities.Length + 1;
        var textTransform = Matrix3x2.CreateTranslation(-Centre) * Matrix3x2.CreateScale(1 / Raster) * world;

        DrawShadow(dc, world, opacity, shadowOpacity, boundaries, tokenOpacities, rightToLeft, clip);

        dc.Transform = textTransform;
        if (glow.Visible)
        {
            if (_glow is null)
            {
                _glow = new Vortice.Direct2D1.Effects.Shadow(_owner) { Optimization = ShadowOptimization.Balanced };
                _glow.SetInput(0, Text, true);
                _glowOut = _glow.Output;
            }
            _glow.BlurStandardDeviation = MathF.Max(0.1f, glow.Radius * 0.5f * Raster / worldScale);
            _glow.Color = new Vector4(glow.Colour.X, glow.Colour.Y, glow.Colour.Z, Math.Clamp(glow.Colour.W * opacity, 0, 1));
            if (!clip.IsSet) dc.DrawImage(_glowOut!, InterpolationMode.Linear, CompositeMode.SourceOver);
            else dc.DrawImage(_glowOut!, null, Spill(clip), InterpolationMode.Linear, CompositeMode.SourceOver);
        }

        var sigma = blur * 0.5f * Raster / worldScale;
        if (!sliced)
        {
            if (sigma < 0.2f)
            {
                if (!clip.IsSet)
                {
                    dc.DrawBitmap(Text, new RawRectF(0, 0, PixelWidth, PixelHeight), opacity, InterpolationMode.Linear, null, null);
                }
                else
                {
                    var whole = Clipped(new RawRectF(0, 0, PixelWidth, PixelHeight), clip, Centre, Raster);
                    if (whole.Right > whole.Left) dc.DrawBitmap(Text, whole, opacity, InterpolationMode.Linear, whole, null);
                }
            }
            else
            {
                SetBlur(sigma);
                _opacity!.Value = opacity;
                if (!clip.IsSet) dc.DrawImage(_blurred!, InterpolationMode.Linear, CompositeMode.SourceOver);
                else dc.DrawImage(_blurred!, null, Spill(clip), InterpolationMode.Linear, CompositeMode.SourceOver);
            }
            return;
        }
        // Tokens arrive in reading order: the ones fully there share the run's blur; any still arriving fade in sharp.
        var settled = 0;
        while (settled < tokenOpacities.Length && tokenOpacities[settled] >= 0.98) settled++;
        var start = 0;
        if (sigma >= 0.2f && settled > 0)
        {
            var first = SliceRect(0, boundaries, tokenOpacities.Length, rightToLeft, Centre, Raster, PixelWidth, PixelHeight);
            var last = SliceRect(settled - 1, boundaries, tokenOpacities.Length, rightToLeft, Centre, Raster, PixelWidth, PixelHeight);
            var span = Clipped(new RawRectF(MathF.Min(first.Left, last.Left), 0, MathF.Max(first.Right, last.Right), PixelHeight),
                               clip, Centre, Raster);
            SetBlur(sigma);
            _opacity!.Value = opacity;
            if (span.Right > span.Left) dc.DrawImage(_blurred!, null, span, InterpolationMode.Linear, CompositeMode.SourceOver);
            start = settled;
        }
        for (var k = start; k < tokenOpacities.Length; k++)
        {
            var alpha = (float)tokenOpacities[k] * opacity;
            if (alpha <= 0.002f) continue;
            var slice = Clipped(SliceRect(k, boundaries, tokenOpacities.Length, rightToLeft, Centre, Raster, PixelWidth, PixelHeight),
                                clip, Centre, Raster);
            if (slice.Right > slice.Left) dc.DrawBitmap(Text, slice, alpha, InterpolationMode.Linear, slice, null);
        }
    }

    /// <summary>
    /// Draws only the soft shadow (as <see cref="Draw"/> would under the text), for a piece whose text goes on top of
    /// something drawn after its shadow, such as a label's tile.
    /// </summary>
    /// <param name="clip">Only this stretch across the text casts a shadow (as in <see cref="Draw"/>).</param>
    public void DrawShadow(ID2D1DeviceContext dc, in Matrix3x2 world, float opacity, float shadowOpacity,
                           ReadOnlySpan<double> boundaries = default, ReadOnlySpan<double> tokenOpacities = default,
                           bool rightToLeft = false, InkClip clip = default)
    {
        if (!HasSoft || !(shadowOpacity * opacity > 0.004f)) return;
        if (clip.IsSet && !(clip.Right > clip.Left)) return;
        if (!Bake()) return;
        var sliced = tokenOpacities.Length > 1 && boundaries.Length == tokenOpacities.Length + 1;
        dc.Transform = Matrix3x2.CreateTranslation(-SoftCentre) * Matrix3x2.CreateScale(1 / SoftRaster) * world;
        if (!sliced)
        {
            if (!clip.IsSet)
            {
                dc.DrawBitmap(_soft, new RawRectF(0, 0, SoftWidth, SoftHeight), opacity * shadowOpacity, InterpolationMode.Linear, null, null);
                return;
            }
            var whole = Clipped(new RawRectF(0, 0, SoftWidth, SoftHeight), clip, SoftCentre, SoftRaster);
            if (whole.Right > whole.Left) dc.DrawBitmap(_soft, whole, opacity * shadowOpacity, InterpolationMode.Linear, whole, null);
            return;
        }
        for (var k = 0; k < tokenOpacities.Length; k++)
        {
            var alpha = (float)tokenOpacities[k] * opacity * shadowOpacity;
            if (alpha <= 0.004f) continue;
            var slice = Clipped(SliceRect(k, boundaries, tokenOpacities.Length, rightToLeft, SoftCentre, SoftRaster, SoftWidth, SoftHeight),
                                clip, SoftCentre, SoftRaster);
            if (slice.Right > slice.Left) dc.DrawBitmap(_soft, slice, alpha, InterpolationMode.Linear, slice, null);
        }
    }

    /// <summary>
    /// Draws the text (no shadow, no glow) only between <paramref name="left"/> and <paramref name="right"/>, in layout
    /// pixels from the centre of the layout box: where a label's tile has pressed in. Glued runs arrive by token as in
    /// <see cref="Draw"/>.
    /// </summary>
    public void DrawBetween(ID2D1DeviceContext dc, in Matrix3x2 world, float worldScale, float opacity, float blur,
                            float left, float right, ReadOnlySpan<double> boundaries = default,
                            ReadOnlySpan<double> tokenOpacities = default, bool rightToLeft = false)
    {
        if (!(opacity > 0.002f) || !(worldScale > 0.0001f) || !(right > left)) return;
        var from = Math.Clamp(Centre.X + left * Raster, 0, PixelWidth);
        var to = Math.Clamp(Centre.X + right * Raster, 0, PixelWidth);
        if (!(to > from)) return;
        if (!Bake()) return;
        var sliced = tokenOpacities.Length > 1 && boundaries.Length == tokenOpacities.Length + 1;
        dc.Transform = Matrix3x2.CreateTranslation(-Centre) * Matrix3x2.CreateScale(1 / Raster) * world;
        var sigma = blur * 0.5f * Raster / worldScale;
        if (!sliced)
        {
            var span = new RawRectF(from, 0, to, PixelHeight);
            if (sigma < 0.2f)
            {
                dc.DrawBitmap(Text, span, opacity, InterpolationMode.Linear, span, null);
            }
            else
            {
                // The blur spills past the bitmap: an open side keeps its spill, as a full draw would.
                var spill = new RawRectF(float.IsNegativeInfinity(left) ? -PixelWidth : from, -PixelHeight,
                                         float.IsPositiveInfinity(right) ? 2f * PixelWidth : to, 2f * PixelHeight);
                SetBlur(sigma);
                _opacity!.Value = opacity;
                dc.DrawImage(_blurred!, new Vector2(spill.Left, spill.Top), spill, InterpolationMode.Linear, CompositeMode.SourceOver);
            }
            return;
        }
        // As in Draw: the tokens fully there share the run's blur; any still arriving fade in sharp.
        var settled = 0;
        while (settled < tokenOpacities.Length && tokenOpacities[settled] >= 0.98) settled++;
        var start = 0;
        if (sigma >= 0.2f && settled > 0)
        {
            var first = SliceRect(0, boundaries, tokenOpacities.Length, rightToLeft, Centre, Raster, PixelWidth, PixelHeight);
            var last = SliceRect(settled - 1, boundaries, tokenOpacities.Length, rightToLeft, Centre, Raster, PixelWidth, PixelHeight);
            var a = MathF.Max(MathF.Min(first.Left, last.Left), from);
            var b = MathF.Min(MathF.Max(first.Right, last.Right), to);
            if (b > a)
            {
                SetBlur(sigma);
                _opacity!.Value = opacity;
                dc.DrawImage(_blurred!, new Vector2(a, 0), new RawRectF(a, 0, b, PixelHeight), InterpolationMode.Linear, CompositeMode.SourceOver);
            }
            start = settled;
        }
        for (var k = start; k < tokenOpacities.Length; k++)
        {
            var alpha = (float)tokenOpacities[k] * opacity;
            if (alpha <= 0.002f) continue;
            var slice = SliceRect(k, boundaries, tokenOpacities.Length, rightToLeft, Centre, Raster, PixelWidth, PixelHeight);
            var a = MathF.Max(slice.Left, from);
            var b = MathF.Min(slice.Right, to);
            if (!(b > a)) continue;
            var part = new RawRectF(a, 0, b, PixelHeight);
            dc.DrawBitmap(Text, part, alpha, InterpolationMode.Linear, part, null);
        }
    }

    /// <summary>
    /// The text's shape in one flat <paramref name="colour"/> (straight alpha), within <paramref name="clip"/>: a word
    /// set on a highlight tile. Made through an effect created when first needed.
    /// </summary>
    /// <param name="boundaries">With <paramref name="opacities"/>, each share of the text (by fraction across it, left to
    /// right) is drawn at its own opacity, as in <see cref="Draw"/>: a reading filling in as its word is sung.</param>
    public void DrawTinted(ID2D1DeviceContext dc, in Matrix3x2 world, Vector4 colour, InkClip clip,
                           ReadOnlySpan<double> boundaries = default, ReadOnlySpan<double> opacities = default)
    {
        if (!(colour.W > 0.004f) || (clip.IsSet && !(clip.Right > clip.Left))) return;
        if (!Bake()) return;
        if (_tint is null)
        {
            _tint = new Vortice.Direct2D1.Effects.Shadow(_owner) { Optimization = ShadowOptimization.Speed, BlurStandardDeviation = 0 };
            _tint.SetInput(0, Text, true);
            _tintOut = _tint.Output;
        }
        dc.Transform = Matrix3x2.CreateTranslation(-Centre) * Matrix3x2.CreateScale(1 / Raster) * world;
        if (!(opacities.Length > 1 && boundaries.Length == opacities.Length + 1))
        {
            _tint.Color = new Vector4(colour.X, colour.Y, colour.Z, Math.Clamp(colour.W, 0, 1));
            var rect = Clipped(new RawRectF(0, 0, PixelWidth, PixelHeight), clip, Centre, Raster);
            // The image rectangle's corner is drawn at the target offset, so a cut rectangle keeps its place.
            if (rect.Right > rect.Left)
                dc.DrawImage(_tintOut!, new Vector2(rect.Left, rect.Top), rect, InterpolationMode.Linear, CompositeMode.SourceOver);
            return;
        }
        for (var k = 0; k < opacities.Length; k++)
        {
            var alpha = Math.Clamp(colour.W * (float)opacities[k], 0, 1);
            if (alpha <= 0.004f) continue;
            var slice = Clipped(SliceRect(k, boundaries, opacities.Length, false, Centre, Raster, PixelWidth, PixelHeight),
                                clip, Centre, Raster);
            if (!(slice.Right > slice.Left)) continue;
            _tint.Color = new Vector4(colour.X, colour.Y, colour.Z, alpha);
            dc.DrawImage(_tintOut!, new Vector2(slice.Left, slice.Top), slice, InterpolationMode.Linear, CompositeMode.SourceOver);
        }
    }

    /// <summary>
    /// Draws one rectangle of the text (<paramref name="left"/>…<paramref name="bottom"/> in layout pixels from the centre
    /// of the layout box; an infinite side reaches the bitmap's edge) through <paramref name="world"/>: its soft shadow,
    /// the text at <paramref name="opacity"/>, and <paramref name="tint"/> (straight alpha) laid over it. An action word's
    /// letters, bands and cells are drawn this way (the Mac's per-glyph pass), each through its own transform.
    /// </summary>
    public void DrawRegion(ID2D1DeviceContext dc, in Matrix3x2 world, float left, float top, float right, float bottom,
                           float opacity, float shadowOpacity = 0, Vector4 tint = default)
    {
        if (!(right > left) || !(bottom > top)) return;
        if (!(opacity > 0.002f) && !(tint.W > 0.004f)) return;
        if (!Bake()) return;
        if (HasSoft && shadowOpacity * opacity > 0.004f)
        {
            var soft = Region(left, top, right, bottom, SoftCentre, SoftRaster, SoftWidth, SoftHeight);
            if (soft.Right > soft.Left && soft.Bottom > soft.Top)
            {
                dc.Transform = Matrix3x2.CreateTranslation(-SoftCentre) * Matrix3x2.CreateScale(1 / SoftRaster) * world;
                dc.DrawBitmap(_soft, soft, opacity * shadowOpacity, InterpolationMode.Linear, soft, null);
            }
        }
        var rect = Region(left, top, right, bottom, Centre, Raster, PixelWidth, PixelHeight);
        if (!(rect.Right > rect.Left) || !(rect.Bottom > rect.Top)) return;
        dc.Transform = Matrix3x2.CreateTranslation(-Centre) * Matrix3x2.CreateScale(1 / Raster) * world;
        if (opacity > 0.002f) dc.DrawBitmap(Text, rect, Math.Min(1, opacity), InterpolationMode.Linear, rect, null);
        if (tint.W > 0.004f)
        {
            if (_tint is null)
            {
                _tint = new Vortice.Direct2D1.Effects.Shadow(_owner) { Optimization = ShadowOptimization.Speed, BlurStandardDeviation = 0 };
                _tint.SetInput(0, Text, true);
                _tintOut = _tint.Output;
            }
            _tint.Color = new Vector4(tint.X, tint.Y, tint.Z, Math.Clamp(tint.W, 0, 1));
            dc.DrawImage(_tintOut!, new Vector2(rect.Left, rect.Top), rect, InterpolationMode.Linear, CompositeMode.SourceOver);
        }
    }

    /// <summary>Only the glow around the text (as <see cref="Draw"/> would draw it), for a word drawn letter by letter.</summary>
    public void DrawGlow(ID2D1DeviceContext dc, in Matrix3x2 world, float worldScale, float opacity, GlowSpec glow)
    {
        if (!glow.Visible || !(opacity > 0.002f) || !(worldScale > 0.0001f) || !Bake()) return;
        if (_glow is null)
        {
            _glow = new Vortice.Direct2D1.Effects.Shadow(_owner) { Optimization = ShadowOptimization.Balanced };
            _glow.SetInput(0, Text, true);
            _glowOut = _glow.Output;
        }
        _glow.BlurStandardDeviation = MathF.Max(0.1f, glow.Radius * 0.5f * Raster / worldScale);
        _glow.Color = new Vector4(glow.Colour.X, glow.Colour.Y, glow.Colour.Z, Math.Clamp(glow.Colour.W * opacity, 0, 1));
        dc.Transform = Matrix3x2.CreateTranslation(-Centre) * Matrix3x2.CreateScale(1 / Raster) * world;
        dc.DrawImage(_glowOut!, InterpolationMode.Linear, CompositeMode.SourceOver);
    }

    private static RawRectF Region(float left, float top, float right, float bottom, Vector2 centre, float raster, int width, int height) =>
        new(Math.Clamp(centre.X + left * raster, 0, width), Math.Clamp(centre.Y + top * raster, 0, height),
            Math.Clamp(centre.X + right * raster, 0, width), Math.Clamp(centre.Y + bottom * raster, 0, height));

    /// <summary><paramref name="rect"/> (bitmap pixels) cut to the clip's stretch across the text.</summary>
    private RawRectF Clipped(RawRectF rect, InkClip clip, Vector2 centre, float raster)
    {
        if (!clip.IsSet) return rect;
        var left = MathF.Max(rect.Left, centre.X + clip.Left * raster);
        var right = MathF.Min(rect.Right, centre.X + clip.Right * raster);
        return new RawRectF(left, rect.Top, MathF.Max(left, right), rect.Bottom);
    }

    /// <summary>The clip as an image rectangle for an effect's output, which spills past the bitmap (blur, glow).</summary>
    private RawRectF Spill(InkClip clip)
    {
        const float far = 100_000;
        var left = float.IsNegativeInfinity(clip.Left) ? -far : Centre.X + clip.Left * Raster;
        var right = float.IsPositiveInfinity(clip.Right) ? far : Centre.X + clip.Right * Raster;
        return new RawRectF(left, -far, MathF.Max(left, right), far);
    }

    /// <summary>The blur's standard deviation (property 0), set through the float overload: the typed setter boxes.</summary>
    private void SetBlur(float sigma) => _blur!.SetValue(0u, sigma);

    /// <summary>Token <paramref name="k"/>'s share of the label, in bitmap pixels; the first and last reach the bitmap's edges.</summary>
    private RawRectF SliceRect(int k, ReadOnlySpan<double> boundaries, int count, bool rightToLeft, Vector2 centre,
                               float raster, int width, int height)
    {
        var w = Box.X;
        float a, b;
        if (!rightToLeft)
        {
            a = k == 0 ? float.NegativeInfinity : -w / 2 + (float)boundaries[k] * w;
            b = k == count - 1 ? float.PositiveInfinity : -w / 2 + (float)boundaries[k + 1] * w;
        }
        else
        {
            a = k == count - 1 ? float.NegativeInfinity : w / 2 - (float)boundaries[k + 1] * w;
            b = k == 0 ? float.PositiveInfinity : w / 2 - (float)boundaries[k] * w;
        }
        var left = Math.Clamp(centre.X + a * raster, 0, width);
        var right = Math.Clamp(centre.X + b * raster, 0, width);
        return new RawRectF(left, 0, MathF.Max(left, right), height);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // A bake still waiting is let go (the kit skips it).
        Kit?.Cancel(this);
        Kit = null;
        _tintOut?.Dispose();
        _tint?.Dispose();
        _glowOut?.Dispose();
        _glow?.Dispose();
        _blurred?.Dispose();
        _opacity?.Dispose();
        _blur?.Dispose();
        _soft?.Dispose();
        _text?.Dispose();
    }
}

/// <summary>
/// A stretch across a piece of text, in layout pixels from the centre of its layout box (left to right); only that
/// stretch is drawn. The default draws everything.
/// </summary>
internal readonly record struct InkClip(float Left, float Right, bool IsSet = true)
{
    public static InkClip None => default;

    /// <summary>The leading <paramref name="shown"/> share of a box <paramref name="width"/> wide (the trailing share when <paramref name="fromRight"/>).</summary>
    public static InkClip Leading(double shown, double width, bool fromRight)
    {
        if (!(shown < 1)) return default;
        var w = (float)width;
        var cut = (float)(Math.Max(0, shown) * width);
        return fromRight ? new InkClip(w / 2 - cut, float.PositiveInfinity) : new InkClip(float.NegativeInfinity, -w / 2 + cut);
    }
}
