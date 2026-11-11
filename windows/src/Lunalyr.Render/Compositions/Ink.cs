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
/// </summary>
internal sealed class Ink : IDisposable
{
    private readonly Vortice.Direct2D1.Effects.GaussianBlur _blur;
    private readonly Vortice.Direct2D1.Effects.Opacity _opacity;
    private readonly ID2D1Image _blurred;
    private readonly ID2D1DeviceContext _owner;
    private Vortice.Direct2D1.Effects.Shadow? _glow;
    private ID2D1Image? _glowOut;

    public Ink(ID2D1DeviceContext owner, ID2D1Bitmap1 text, float raster, Vector2 centre, Vector2 box, ID2D1Bitmap1? soft,
               float softRaster, Vector2 softCentre)
    {
        _owner = owner;
        Text = text;
        Raster = raster;
        Centre = centre;
        Box = box;
        Soft = soft;
        SoftRaster = softRaster;
        SoftCentre = softCentre;
        var pixels = text.PixelSize;
        PixelWidth = pixels.Width;
        PixelHeight = pixels.Height;
        if (soft is not null)
        {
            var softPixels = soft.PixelSize;
            SoftWidth = softPixels.Width;
            SoftHeight = softPixels.Height;
        }
        _blur = new Vortice.Direct2D1.Effects.GaussianBlur(owner) { BorderMode = BorderMode.Soft, Optimization = GaussianBlurOptimization.Balanced };
        _blur.SetInput(0, text, true);
        _opacity = new Vortice.Direct2D1.Effects.Opacity(owner);
        _opacity.SetInputEffect(0, _blur, true);
        _blurred = _opacity.Output;
    }

    public ID2D1Bitmap1 Text { get; }
    /// <summary>Bitmap pixels per layout pixel.</summary>
    public float Raster { get; }
    /// <summary>The centre of the text's layout box, in bitmap pixels.</summary>
    public Vector2 Centre { get; }
    /// <summary>The layout box, in layout pixels.</summary>
    public Vector2 Box { get; }
    public ID2D1Bitmap1? Soft { get; }
    public float SoftRaster { get; }
    public Vector2 SoftCentre { get; }
    public int PixelWidth { get; }
    public int PixelHeight { get; }
    public int SoftWidth { get; }
    public int SoftHeight { get; }

    /// <summary>
    /// Draws the text with its shadow and glow. <paramref name="world"/> maps layout pixels (origin at the centre of
    /// the layout box) to the device; <paramref name="worldScale"/> is its scale. Blur and glow radii are in device
    /// pixels. With <paramref name="boundaries"/> and <paramref name="tokenOpacities"/>, each token's share of the
    /// label (by character, in reading order) is drawn at its own opacity, so a glued run's syllables arrive one by one.
    /// </summary>
    public void Draw(ID2D1DeviceContext dc, in Matrix3x2 world, float worldScale, float opacity, float blur, GlowSpec glow,
                     float shadowOpacity, ReadOnlySpan<double> boundaries = default,
                     ReadOnlySpan<double> tokenOpacities = default, bool rightToLeft = false)
    {
        if (!(opacity > 0.002f) || !(worldScale > 0.0001f)) return;
        var sliced = tokenOpacities.Length > 1 && boundaries.Length == tokenOpacities.Length + 1;
        var textTransform = Matrix3x2.CreateTranslation(-Centre) * Matrix3x2.CreateScale(1 / Raster) * world;

        if (Soft is not null && shadowOpacity * opacity > 0.004f)
        {
            dc.Transform = Matrix3x2.CreateTranslation(-SoftCentre) * Matrix3x2.CreateScale(1 / SoftRaster) * world;
            if (!sliced)
            {
                dc.DrawBitmap(Soft, new RawRectF(0, 0, SoftWidth, SoftHeight), opacity * shadowOpacity, InterpolationMode.Linear, null, null);
            }
            else
            {
                for (var k = 0; k < tokenOpacities.Length; k++)
                {
                    var alpha = (float)tokenOpacities[k] * opacity * shadowOpacity;
                    if (alpha <= 0.004f) continue;
                    var slice = SliceRect(k, boundaries, tokenOpacities.Length, rightToLeft, SoftCentre, SoftRaster, SoftWidth, SoftHeight);
                    dc.DrawBitmap(Soft, slice, alpha, InterpolationMode.Linear, slice, null);
                }
            }
        }

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
            dc.DrawImage(_glowOut!, InterpolationMode.Linear, CompositeMode.SourceOver);
        }

        var sigma = blur * 0.5f * Raster / worldScale;
        if (!sliced)
        {
            if (sigma < 0.2f)
            {
                dc.DrawBitmap(Text, new RawRectF(0, 0, PixelWidth, PixelHeight), opacity, InterpolationMode.Linear, null, null);
            }
            else
            {
                SetBlur(sigma);
                _opacity.Value = opacity;
                dc.DrawImage(_blurred, InterpolationMode.Linear, CompositeMode.SourceOver);
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
            var span = new RawRectF(MathF.Min(first.Left, last.Left), 0, MathF.Max(first.Right, last.Right), PixelHeight);
            SetBlur(sigma);
            _opacity.Value = opacity;
            dc.DrawImage(_blurred, null, span, InterpolationMode.Linear, CompositeMode.SourceOver);
            start = settled;
        }
        for (var k = start; k < tokenOpacities.Length; k++)
        {
            var alpha = (float)tokenOpacities[k] * opacity;
            if (alpha <= 0.002f) continue;
            var slice = SliceRect(k, boundaries, tokenOpacities.Length, rightToLeft, Centre, Raster, PixelWidth, PixelHeight);
            dc.DrawBitmap(Text, slice, alpha, InterpolationMode.Linear, slice, null);
        }
    }

    /// <summary>The blur's standard deviation (property 0), set through the float overload: the typed setter boxes.</summary>
    private void SetBlur(float sigma) => _blur.SetValue(0u, sigma);

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
        _glowOut?.Dispose();
        _glow?.Dispose();
        _blurred.Dispose();
        _opacity.Dispose();
        _blur.Dispose();
        Soft?.Dispose();
        Text.Dispose();
    }
}
