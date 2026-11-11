// Ported from the Mac app's LyricStageFit.swift (LyricViewportMetrics, LyricStageFit) and SpokenTypographyView.swift
// (WordStageFit): Lunalyr's own code. Units are whatever the caller measures in (physical pixels on Windows), except
// where a parameter says points.

namespace Lunalyr.Core.Compositions;

/// <summary>A width and height.</summary>
public readonly record struct SizeD(double Width, double Height)
{
    public static SizeD Zero => default;
}

/// <summary>A point.</summary>
public readonly record struct PointD(double X, double Y);

/// <summary>
/// The lyric stage on one screen: the base type size, the stage the lyrics are set in, and the size slider. The Mac's
/// base size is <c>clamp(min(w, h·16/9) · 0.046, 24, 124)</c> points and its stage 80% × 72% of the screen.
/// </summary>
/// <param name="BaseSize">The base type size, in the caller's units (pixels).</param>
/// <param name="Stage">The stage, in the caller's units.</param>
/// <param name="UserScale">The size slider, 0.7–1.5 (the Mac's <c>lyricScale</c>).</param>
public readonly record struct ViewportMetrics(double BaseSize, SizeD Stage, double UserScale)
{
    /// <summary>The contract's default <c>SizeFraction</c>, which is the Mac's slider at 1.</summary>
    public const double NeutralSizeFraction = 0.075;

    /// <param name="width">Surface width in pixels.</param>
    /// <param name="height">Surface height in pixels.</param>
    /// <param name="scale">Pixels per point (the monitor's DPI scale).</param>
    /// <param name="sizeFraction">The lyric size setting; <see cref="NeutralSizeFraction"/> is the Mac's 1.0.</param>
    public static ViewportMetrics For(double width, double height, double scale, double sizeFraction = NeutralSizeFraction)
    {
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;
        if (!double.IsFinite(width) || width < 0) width = 0;
        if (!double.IsFinite(height) || height < 0) height = 0;
        var points = Math.Min(width, height * 16 / 9) / scale;
        var baseSize = Math.Clamp(points * 0.046, 24, 124) * scale;
        var user = double.IsFinite(sizeFraction) && sizeFraction > 0 ? sizeFraction / NeutralSizeFraction : 1;
        return new ViewportMetrics(baseSize, new SizeD(width * 0.80, height * 0.72), Math.Clamp(user, 0.7, 1.5));
    }
}

/// <summary>How a line (with its readings) is scaled to the stage. No mask: it only ever shrinks or grows as a whole.</summary>
public static class LyricStageFit
{
    /// <summary>The scale that fits <paramref name="content"/> into <paramref name="available"/>, at most <paramref name="maximum"/>; 1 for bad input.</summary>
    public static double Scale(SizeD content, SizeD available, double maximum = 1)
    {
        if (!double.IsFinite(content.Width) || !double.IsFinite(content.Height) || !double.IsFinite(available.Width)
            || !double.IsFinite(available.Height) || content.Width <= 0 || content.Height <= 0 || available.Width <= 0
            || available.Height <= 0) return 1;
        return Math.Min(maximum, Math.Min(available.Width / content.Width, available.Height / content.Height));
    }

    /// <summary>
    /// The scale a line is drawn at: fitted to <paramref name="neutralShare"/> of the stage (never enlarged past
    /// <paramref name="maximumNaturalScale"/>), times the size slider, but never past the whole stage. Stages that lay
    /// themselves out across the whole stage pass a share of 1.
    /// </summary>
    public static double PresentationScale(SizeD content, SizeD available, double userScale, double neutralShare = 0.75,
                                           double maximumNaturalScale = 1)
    {
        var neutral = Scale(content, new SizeD(available.Width * neutralShare, available.Height * neutralShare), maximumNaturalScale);
        var requested = neutral * Math.Min(1.5, Math.Max(0.7, userScale));
        if (!double.IsFinite(content.Width) || !double.IsFinite(content.Height) || available.Width <= 0
            || available.Height <= 0 || content.Width <= 0 || content.Height <= 0) return requested;
        return Math.Min(requested, Math.Min(available.Width / content.Width, available.Height / content.Height));
    }
}

/// <summary>
/// Fits a word's actual turned ink around the centre it was placed at, inside the stage less a margin. Never enlarges;
/// 0 for bad input (draws nothing).
/// </summary>
public static class WordStageFit
{
    public static double Scale(SizeD ink, SizeD stage, PointD centre, double scale, double angle, double margin)
    {
        if (!double.IsFinite(ink.Width) || !double.IsFinite(ink.Height) || !double.IsFinite(stage.Width)
            || !double.IsFinite(stage.Height) || !double.IsFinite(centre.X) || !double.IsFinite(centre.Y)
            || !double.IsFinite(scale) || !double.IsFinite(angle) || !double.IsFinite(margin) || ink.Width <= 0
            || ink.Height <= 0 || stage.Width <= 0 || stage.Height <= 0 || scale <= 0) return 0;
        var radians = angle * Math.PI / 180;
        double cosine = Math.Abs(Math.Cos(radians)), sine = Math.Abs(Math.Sin(radians));
        var rotatedWidth = ink.Width * cosine + ink.Height * sine;
        var rotatedHeight = ink.Height * cosine + ink.Width * sine;
        var inset = Math.Max(0, margin);
        var clearanceX = Math.Max(0, Math.Min(centre.X, stage.Width - centre.X) - inset);
        var clearanceY = Math.Max(0, Math.Min(centre.Y, stage.Height - centre.Y) - inset);
        return Math.Min(1, Math.Min(clearanceX * 2 / Math.Max(1, rotatedWidth * scale),
                                    clearanceY * 2 / Math.Max(1, rotatedHeight * scale)));
    }
}
