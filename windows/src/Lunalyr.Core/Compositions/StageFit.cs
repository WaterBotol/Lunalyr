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

    /// <summary>
    /// A text size set outright (the floating lyrics' own, in points) instead of one following the screen: the stage
    /// then fills nearly the whole window, 94% × 88% (the Mac's <c>LyricViewportMetrics(size:textSize:)</c>).
    /// </summary>
    /// <param name="titleRow">
    /// The song title is kept on screen: a row above the lyrics, which give it room (<see cref="TitleBand"/>, the Mac's
    /// d849c49), the stage shortened by it.
    /// </param>
    /// <param name="rowScale">The row's scale (60–160%).</param>
    /// <param name="rowAtBottom">The row sits at the window's foot: the band is there instead.</param>
    public static ViewportMetrics ForText(double width, double height, double scale, double textSize,
                                          double sizeFraction = NeutralSizeFraction, bool titleRow = false, double rowScale = 1,
                                          bool rowAtBottom = false)
    {
        if (!double.IsFinite(textSize) || textSize <= 0) return For(width, height, scale, sizeFraction);
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;
        if (!double.IsFinite(width) || width < 0) width = 0;
        if (!double.IsFinite(height) || height < 0) height = 0;
        var user = double.IsFinite(sizeFraction) && sizeFraction > 0 ? sizeFraction / NeutralSizeFraction : 1;
        var baseSize = textSize * scale;
        var band = titleRow ? OpeningTitle.RowBand(baseSize, rowScale) : 0;
        return new ViewportMetrics(baseSize, new SizeD(width * 0.94, Math.Max(baseSize, height * 0.88 - band)), Math.Clamp(user, 0.7, 1.5))
        {
            TitleBand = band,
            TitleAtBottom = titleRow && rowAtBottom,
        };
    }

    /// <summary>
    /// Kept clear for the floating window's title row and its margin, at the window's top (or foot,
    /// <see cref="TitleAtBottom"/>): the lyrics are centred in what is left. None on the desktop, where the card sits
    /// beside them.
    /// </summary>
    public double TitleBand { get; init; }

    /// <summary>The band is at the window's foot (the row put at the bottom).</summary>
    public bool TitleAtBottom { get; init; }

    /// <summary>How far the stage's centre moves from the window's: down past a band at the top, up from one at the foot.</summary>
    public double CentreShift => (TitleAtBottom ? -1 : 1) * TitleBand / 2;
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
    /// <param name="marginY">The margin above and below, when it differs from the one at the sides (title-safe on a
    /// wide stage); null for <paramref name="margin"/>.</param>
    public static double Scale(SizeD ink, SizeD stage, PointD centre, double scale, double angle, double margin,
                               double? marginY = null)
    {
        var verticalMargin = marginY ?? margin;
        if (!double.IsFinite(ink.Width) || !double.IsFinite(ink.Height) || !double.IsFinite(stage.Width)
            || !double.IsFinite(stage.Height) || !double.IsFinite(centre.X) || !double.IsFinite(centre.Y)
            || !double.IsFinite(scale) || !double.IsFinite(angle) || !double.IsFinite(margin)
            || !double.IsFinite(verticalMargin) || ink.Width <= 0
            || ink.Height <= 0 || stage.Width <= 0 || stage.Height <= 0 || scale <= 0) return 0;
        var radians = angle * Math.PI / 180;
        double cosine = Math.Abs(Math.Cos(radians)), sine = Math.Abs(Math.Sin(radians));
        var rotatedWidth = ink.Width * cosine + ink.Height * sine;
        var rotatedHeight = ink.Height * cosine + ink.Width * sine;
        var inset = Math.Max(0, margin);
        var insetY = Math.Max(0, verticalMargin);
        var clearanceX = Math.Max(0, Math.Min(centre.X, stage.Width - centre.X) - inset);
        var clearanceY = Math.Max(0, Math.Min(centre.Y, stage.Height - centre.Y) - insetY);
        return Math.Min(1, Math.Min(clearanceX * 2 / Math.Max(1, rotatedWidth * scale),
                                    clearanceY * 2 / Math.Max(1, rotatedHeight * scale)));
    }
}
