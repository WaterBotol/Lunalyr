namespace Lunalyr.Render.Scene;

/// <summary>
/// The Wallpaper page's adjustments as the app sets them now (the Mac's "Adjustments" and "Legibility"), carried in the
/// scene so every background follows a slider at once without being rebuilt. A background multiplies them into its
/// own <c>BackgroundOptions</c> (whose values stay for tools and tests), so the defaults here change nothing.
/// </summary>
public sealed record WallpaperAdjustments
{
    public static WallpaperAdjustments Default { get; } = new();

    /// <summary>"Motion speed", 0.2...2 (the Mac's <c>effectSpeed</c>): multiplies every ambient clock.</summary>
    public double Speed { get; init; } = 1;

    /// <summary>"Colour richness", 0.4...1.8 (the Mac's <c>vividness</c>): saturation about each album colour's own luma.</summary>
    public double Vividness { get; init; } = 1;

    /// <summary>"Darkening", 0...2 (the Mac's <c>scrimStrength</c>): scales the scrim.</summary>
    public double Darkening { get; init; } = 1;

    /// <summary>"Text backing" set by hand, 0...1, or null for the automatic amount (the Mac's <c>custom.textBacking</c>).</summary>
    public double? ManualBacking { get; init; }

    /// <summary>"Pattern visibility", 0...1 (the Mac's <c>patternOpacity</c>): how strongly a line pattern shows over its field.</summary>
    public double PatternOpacity { get; init; } = 0.85;

    /// <summary>Covers › "Softening", 0...25 points (the Mac's <c>motionBlur</c>): the blur over an animated cover.</summary>
    public double Softening { get; init; } = 5;
}
