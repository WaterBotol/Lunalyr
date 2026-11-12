// The album colour field: where its colour masses are at a moment, and how a palette change fades.
//
// Behaviour, as the Mac's AnimatedGradientView draws it (that view is largely Verso-attributed, so this is written
// from a description, not translated): a dark base of the palette's average at 45% brightness; one large, soft colour
// mass per palette stop, dense at its heart and fading to nothing; the masses drift slowly and independently on paths
// that never visibly repeat, and swell and shrink by a few per cent; they diffuse into one another so the field reads
// as one wash with no visible edges. Colour richness scales saturation. It is drawn at a bounded resolution and
// stretched. The rates (the field's clock at 0.70 × 0.34 of Motion speed, 0.15 of that with Reduce Motion), the base
// and the pixel budget are Lunalyr's own lines and are kept.
//
// The motion model, the layout, sizes, periods and falloff below are our own: each mass hovers near the part of the
// screen that matches where its colour sits on the cover (the stops are the cover's corners and centre), wandering on
// a sum of two incommensurate sinusoids per axis, with a biweight falloff. The peak opacity is the 85% that Lunalyr's
// legibility estimate (LyricLegibility.colourFieldLuminance) assumes for the field's masses, so the text backing is
// worked out for what is drawn. ColourFieldBudget (Lunalyr's own, in AnimatedGradientView.swift) is ported as it is.

using System.Numerics;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Colour;

/// <summary>Where the colour field's masses are and how big, at a moment of the field's own clock.</summary>
public static class ColourField
{
    /// <summary>One mass per palette stop.</summary>
    public const int MassCount = 5;

    /// <summary>The field's clock against wall seconds at Motion speed 1 (the Mac's ambient flow 0.70, a third of it for the field).</summary>
    public const double ClockRate = 0.70 * 0.34;

    /// <summary>Ambient motion under Reduce Motion (the Mac's reducedMotionRate).</summary>
    public const double ReducedMotionRate = 0.15;

    /// <summary>The base under the masses: the stops' average at this brightness.</summary>
    public const float BaseBrightness = 0.45f;

    /// <summary>At most this many pixels are drawn per frame; the field is stretched over the rest (the Mac's budget).</summary>
    public const double PixelBudget = ColourFieldBudget.Standard;

    /// <summary>A mass's centre (pixels) and radius (pixels) on a field of the given size.</summary>
    public readonly record struct Mass(double X, double Y, double Radius);

    private readonly record struct Path(double HomeX, double HomeY, double ReachX, double ReachY,
                                        double P1, double P2, double P3, double P4, double Size, double Breath);

    // Homes follow the stops' places on the cover: top-left, top-right, centre, bottom-left, bottom-right.
    private static readonly Path[] Paths =
    [
        new(0.20, 0.24, 0.14, 0.13, 23.7, 37.1, 29.3, 43.9, 0.60, 17.3),
        new(0.80, 0.22, 0.14, 0.13, 27.1, 41.3, 21.9, 33.7, 0.56, 19.9),
        new(0.52, 0.50, 0.18, 0.15, 31.3, 47.9, 26.3, 39.1, 0.66, 22.7),
        new(0.22, 0.80, 0.14, 0.13, 25.1, 35.3, 30.7, 45.7, 0.58, 18.1),
        new(0.78, 0.78, 0.14, 0.13, 28.9, 44.3, 24.1, 36.7, 0.54, 21.1),
    ];

    /// <summary>The golden angle: phases spaced by it never line up.</summary>
    private const double Golden = 2.399963229728653;

    private const double Turn = 2 * Math.PI;

    /// <summary>The field's clock at <paramref name="wallSeconds"/> for a Motion speed and Reduce Motion.</summary>
    public static double Clock(double wallSeconds, double speed, bool reduceMotion) =>
        double.IsFinite(wallSeconds) ? wallSeconds * ClockRate * Math.Max(0, speed) * (reduceMotion ? ReducedMotionRate : 1) : 0;

    /// <summary>Mass <paramref name="index"/> (0...4) at field time <paramref name="time"/> on a <paramref name="width"/> × <paramref name="height"/> field.</summary>
    public static Mass At(int index, double time, double width, double height)
    {
        var path = Paths[((index % MassCount) + MassCount) % MassCount];
        var phase = index * 4 * Golden;
        var wanderX = 0.62 * Math.Sin(Turn * time / path.P1 + phase) + 0.38 * Math.Sin(Turn * time / path.P2 + phase + Golden);
        var wanderY = 0.62 * Math.Sin(Turn * time / path.P3 + phase + 2 * Golden) + 0.38 * Math.Sin(Turn * time / path.P4 + phase + 3 * Golden);
        var halfDiagonal = 0.5 * Math.Sqrt(width * width + height * height);
        var breathe = 1 + 0.06 * Math.Sin(Turn * time / path.Breath + phase * 0.5);
        return new Mass((path.HomeX + path.ReachX * wanderX) * width, (path.HomeY + path.ReachY * wanderY) * height,
                        path.Size * halfDiagonal * breathe);
    }

    /// <summary>
    /// A mass's opacity from its centre (0) to its rim (1): a biweight bump, dense at the heart, soft all the way out
    /// and reaching nothing with no edge.
    /// </summary>
    public static double Falloff(double distance)
    {
        if (!(distance < 1)) return 0;
        var d = Math.Max(0, distance);
        var s = 1 - d * d;
        return Peak * s * s;
    }

    /// <summary>A mass's opacity at its centre: the 85% the legibility estimate assumes (see the file's header).</summary>
    public const double Peak = 0.85;

    /// <summary>The stops a drawing layer approximates <see cref="Falloff"/> with (positions 0...1, evenly spaced).</summary>
    public const int FalloffStops = 17;

    /// <summary>
    /// The canvas the field is drawn at for a surface of <paramref name="pixelWidth"/> × <paramref name="pixelHeight"/>
    /// physical pixels at DPI scale <paramref name="scale"/>, within <paramref name="budget"/> pixels: whole pixels, at
    /// least 4 a side, the surface's own size when it fits.
    /// </summary>
    public static (int Width, int Height) CanvasSize(double pixelWidth, double pixelHeight, double scale = 1, double budget = PixelBudget)
    {
        var safeScale = double.IsFinite(scale) && scale > 0 ? scale : 1;
        var canvas = ColourFieldBudget.Canvas(pixelWidth / safeScale, pixelHeight / safeScale, safeScale, budget);
        return (Math.Max(4, (int)Math.Round(canvas.Width * safeScale)), Math.Max(4, (int)Math.Round(canvas.Height * safeScale)));
    }

    /// <summary>The colour field's luminance estimate from the stops alone (see <see cref="LyricLegibility.ColourFieldLuminance"/>).</summary>
    public static double Luminance(ReadOnlySpan<Vector4> stops) => Luminance(stops, null);

    /// <summary>
    /// The same with a line pattern's faint white lines over the field at <paramref name="patternOpacity"/> (null for
    /// none), as <see cref="LyricLegibility.ColourFieldLuminance"/> estimates it.
    /// </summary>
    public static double Luminance(ReadOnlySpan<Vector4> stops, double? patternOpacity)
    {
        if (stops.IsEmpty) return LyricLegibility.UnknownBackdropLuminance;
        Double3 brightest = default, sum = default;
        var best = double.NegativeInfinity;
        foreach (var stop in stops)
        {
            var rgb = new Double3(stop.X, stop.Y, stop.Z);
            sum += rgb;
            var value = LyricLegibility.Luminance(rgb);
            if (best < value) { brightest = rgb; best = value; }
        }
        var colour = brightest * 0.85 + sum / stops.Length * BaseBrightness * 0.15;
        if (patternOpacity is { } opacity) colour += Double3.Repeating(0.03 * Math.Min(1, Math.Max(0, opacity)));
        return LyricLegibility.Luminance(colour.Clamped(0, 1));
    }
}

/// <summary>
/// How many pixels the colour field may draw per frame (ported from the Mac's <c>ColourFieldBudget</c>, sizes in points).
/// </summary>
public static class ColourFieldBudget
{
    /// <summary>A quarter of a megapixel: a 14-inch MacBook Pro's 6 MP and a 5K display's 15 MP both come down to it.</summary>
    public const double Standard = 250_000.0;

    public static double Pixels(bool economical) => economical ? Standard * 0.6 : Standard;

    /// <summary>Where the field is drawn (points, a whole number of pixels at the scale) and how far it is stretched.</summary>
    public readonly record struct CanvasSize(double Width, double Height, double StretchX, double StretchY)
    {
        /// <summary>The pixels drawn per frame.</summary>
        public double PixelsAt(double scale) => Width * scale * (Height * scale);
    }

    /// <summary>The share of each side the field is drawn at: native when it fits the budget, else the same aspect scaled down.</summary>
    public static double Factor(double width, double height, double scale, double budget)
    {
        if (!(width > 0 && height > 0 && double.IsFinite(width) && double.IsFinite(height))) return 1;
        var safeScale = double.IsFinite(scale) && scale > 0 ? scale : 2;
        var native = width * safeScale * (height * safeScale);
        if (!double.IsFinite(budget)) return 1;
        return Math.Min(1, Math.Sqrt(Math.Max(1, budget) / native));
    }

    /// <summary>
    /// A canvas of whole pixels, so nothing is rounded off it, stretched to reach one of its pixels past each edge of
    /// the screen: an offscreen pass rounded down by a pixel would otherwise leave a seam there.
    /// </summary>
    public static CanvasSize Canvas(double width, double height, double scale, double budget)
    {
        var factor = Factor(width, height, scale, budget);
        if (!(factor < 1)) return new CanvasSize(width, height, 1, 1);
        var safeScale = double.IsFinite(scale) && scale > 0 ? scale : 2;
        double pixelsX = Math.Max(4, Math.Floor(width * factor * safeScale)), pixelsY = Math.Max(4, Math.Floor(height * factor * safeScale));
        double sizeX = pixelsX / safeScale, sizeY = pixelsY / safeScale;
        return new CanvasSize(sizeX, sizeY, width / sizeX * (pixelsX + 2) / pixelsX, height / sizeY * (pixelsY + 2) / pixelsY);
    }
}

/// <summary>
/// A palette change, faded: colours ease from what was on screen to the new ones over
/// <see cref="WallpaperMotion.SurfaceFade"/> with a smoothstep, as the Mac fades its wallpaper palette. A change that
/// arrives mid-fade starts from the colours on screen at that moment. No allocation after construction.
/// </summary>
public sealed class ColourFade
{
    private readonly Vector4[] _from;
    private readonly Vector4[] _to;
    private double _start = double.NegativeInfinity;

    public ColourFade(int count, double duration = WallpaperMotion.SurfaceFade)
    {
        _from = new Vector4[count];
        _to = new Vector4[count];
        Duration = duration;
    }

    public double Duration { get; }

    public int Count => _to.Length;

    /// <summary>The colours being faded to.</summary>
    public ReadOnlySpan<Vector4> Target => _to;

    /// <summary>The colours being faded from.</summary>
    public ReadOnlySpan<Vector4> Source => _from;

    /// <summary>Shows <paramref name="colours"/> at once.</summary>
    public void Reset(ReadOnlySpan<Vector4> colours)
    {
        Fill(_to, colours);
        Fill(_from, colours);
        _start = double.NegativeInfinity;
    }

    /// <summary>Starts a fade at <paramref name="now"/> from whatever is on screen to <paramref name="colours"/>.</summary>
    public void Change(ReadOnlySpan<Vector4> colours, double now)
    {
        var t = (float)Progress(now);
        for (var i = 0; i < _from.Length; i++) _from[i] = Vector4.Lerp(_from[i], _to[i], t);
        Fill(_to, colours);
        _start = now;
    }

    /// <summary>0 at the start of a fade, 1 once it is over (smoothstep).</summary>
    public double Progress(double now) =>
        double.IsNegativeInfinity(_start) ? 1 : Easing.Smoothstep((now - _start) / Math.Max(1e-6, Duration));

    public bool IsFading(double now) => Progress(now) < 1;

    /// <summary>The colours on screen at <paramref name="now"/>.</summary>
    public void Current(double now, Span<Vector4> into)
    {
        var t = (float)Progress(now);
        for (var i = 0; i < Math.Min(into.Length, _to.Length); i++) into[i] = Vector4.Lerp(_from[i], _to[i], t);
    }

    private static void Fill(Vector4[] into, ReadOnlySpan<Vector4> colours)
    {
        for (var i = 0; i < into.Length; i++)
            into[i] = colours.IsEmpty ? Vector4.UnitW : colours[i % colours.Length];
    }
}
