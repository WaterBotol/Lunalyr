// Ported from the Mac's Sources/UI/LyricColour.swift ("Modes", "Ink", "Backdrop") and the pure parts of
// Sources/Settings/LyricExtrasOptions.swift (LyricColourOptions). Lunalyr's own code, translated faithfully; the
// SwiftUI environment plumbing and the UserDefaults store are not ported (settings belong to their own workstream).

using System.Numerics;
using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Colour;

/// <summary>How lyric text is coloured. Standard is neutral white ink (saved as "static").</summary>
public enum LyricColourMode
{
    Standard,
    AlbumDrift,
    SungSweep,
    MusicReactive,
    CustomGradient,
}

public static class LyricColourModes
{
    public static LyricColourMode DefaultMode => LyricColourMode.AlbumDrift;

    /// <summary>The modes offered in Settings (Standard is no longer one).</summary>
    public static IReadOnlyList<LyricColourMode> Choices { get; } =
        [LyricColourMode.AlbumDrift, LyricColourMode.SungSweep, LyricColourMode.MusicReactive, LyricColourMode.CustomGradient];

    public static IReadOnlyList<LyricColourMode> All { get; } =
        [LyricColourMode.Standard, .. Choices];

    /// <summary>The Mac's stored raw value.</summary>
    public static string RawValue(this LyricColourMode mode) => mode switch
    {
        LyricColourMode.Standard => "static",
        LyricColourMode.AlbumDrift => "albumDrift",
        LyricColourMode.SungSweep => "sungSweep",
        LyricColourMode.MusicReactive => "musicReactive",
        _ => "customGradient",
    };

    /// <summary>A saved raw value as a choice: the old "static" now means the default; unknown values are null.</summary>
    public static LyricColourMode? SavedChoice(string? raw)
    {
        foreach (var mode in All)
            if (mode.RawValue() == raw) return mode == LyricColourMode.Standard ? DefaultMode : mode;
        return null;
    }

    public static string Title(this LyricColourMode mode) => mode switch
    {
        LyricColourMode.Standard => "Static white",
        LyricColourMode.AlbumDrift => "Album drift",
        LyricColourMode.SungSweep => "Sung sweep",
        LyricColourMode.MusicReactive => "Music reactive",
        _ => "Custom gradient",
    };

    /// <summary>The modes whose gradient drifts along the line over time (Sung sweep's sung colours too, the Mac's 15 Nov 2026 rule).</summary>
    public static bool Drifts(this LyricColourMode mode) =>
        mode is LyricColourMode.AlbumDrift or LyricColourMode.CustomGradient or LyricColourMode.SungSweep;
}

/// <summary>The pure parts of the text colour preferences (the Mac's <c>LyricColourOptions</c>).</summary>
public static class LyricColourOptions
{
    public static IReadOnlyList<string> DefaultCustom { get; } = ["#F9D29D", "#F28FAD", "#9AD0F5"];
    public const double StrengthMin = 0, StrengthMax = 2;
    public const double SpeedMin = 0.25, SpeedMax = 2;

    public static double ClampedStrength(double value) =>
        double.IsFinite(value) ? Math.Min(StrengthMax, Math.Max(StrengthMin, value)) : 1;

    /// <summary>Two to four valid colours, upper-cased; missing ones come from the defaults.</summary>
    public static List<string> Sanitised(IEnumerable<string> values)
    {
        var valid = new List<string>();
        foreach (var value in values) if (ColourHex.ToOkLab(value) is not null) valid.Add(value.ToUpperInvariant());
        if (valid.Count > 4) valid.RemoveRange(4, valid.Count - 4);
        while (valid.Count < 2) valid.Add(DefaultCustom[valid.Count]);
        return valid;
    }
}

/// <summary>Colours for one word on one frame, already made readable.</summary>
/// <param name="Rest">The word's colour before it is sung, and its whole colour when the ink does not sweep.</param>
/// <param name="Sung">The colour a sweep fills the word with as it is sung.</param>
/// <param name="Sweeps">Whether <paramref name="Sung"/> differs from <paramref name="Rest"/>, so a masked second layer is needed.</param>
public record struct LyricInkSample(SrgbColour Rest, SrgbColour Sung, SrgbColour Glow, SrgbColour Reading, bool Sweeps)
{
    /// <summary>Opacity of words not yet sung, in karaoke layouts. Higher with Increase Contrast.</summary>
    public double Dim { get; set; } = LyricInk.UnsungDim;

    /// <summary>
    /// Never set: there is no underline under sung words (the owner, 14 Nov 2026), and no layer draws one.
    /// Kept so the sample matches the Mac's `LyricInk`.
    /// </summary>
    public SrgbColour? SweepUnderline { get; set; }

    public static LyricInkSample White { get; } =
        new(SrgbColour.White, SrgbColour.White, SrgbColour.White, SrgbColour.White.WithOpacity(0.7), false);
}

/// <summary>Four gradient stops, without an array (the ink is built every frame).</summary>
public record struct InkStops(OkLab S0, OkLab S1, OkLab S2, OkLab S3)
{
    public static InkStops AllWhite => new(OkLab.White, OkLab.White, OkLab.White, OkLab.White);

    public readonly OkLab this[int index] => index switch { 0 => S0, 1 => S1, 2 => S2, _ => S3 };
}

/// <summary>
/// The lyric text palette for one frame: every lyric view, reading, interlude dot and the settings preview are coloured
/// from it. A value: copy it to change a field.
/// </summary>
public record struct LyricInk
{
    /// <summary>
    /// WCAG AA for large text. Lyrics are display type and keep a dark shadow under every glyph, so 3:1 against the
    /// wallpaper is the floor; colours only get lighter.
    /// </summary>
    public const double MinimumContrast = 3.0;

    /// <summary>With Increase Contrast: WCAG AA for normal text.</summary>
    public const double IncreasedContrast = 4.5;

    public const double UnsungDim = 0.28;
    public const double IncreasedUnsungDim = 0.55;

    public LyricInk() { }

    public LyricColourMode Mode { get; set; } = LyricColourMode.Standard;
    public LyricInkSample Base { get; set; } = LyricInkSample.White;

    /// <summary>Cascade's held words: the highlight's hue as type of its own. Null keeps the album accent.</summary>
    public SrgbColour? Accent { get; set; }

    /// <summary>Highlight tiles and the text set on them. Null only for ink no director made.</summary>
    public SrgbColour? Tile { get; set; }

    public SrgbColour OnTile { get; set; } = SrgbColour.White;

    /// <summary>The same highlight in OKLab, as worked out (see <see cref="LyricHighlight"/>).</summary>
    public LyricHighlightTone? Highlight { get; set; }

    /// <summary>The tile opacity at which the word on a fading tile changes back to its own colour.</summary>
    public double TileTextSwitch { get; set; } = 0.4;

    public readonly SrgbColour TileColour => Tile ?? LyricHighlight.FallbackTile;

    public readonly SrgbColour TileText => Tile is null ? LyricHighlight.FallbackText : OnTile;

    /// <summary>Constellation hairlines.</summary>
    public SrgbColour Link { get; set; } = SrgbColour.White;

    /// <summary>Interlude dots. Null keeps the album accent.</summary>
    public SrgbColour? Dot { get; set; }

    /// <summary>Gradient stops for the drifting and reactive modes.</summary>
    public InkStops Stops { get; set; } = InkStops.AllWhite;

    public int StopCount { get; set; } = 1;

    /// <summary>0...1 drift through the gradient.</summary>
    public double Phase { get; set; }

    /// <summary>Estimated luminance behind the lyrics.</summary>
    public double Backdrop { get; set; } = 0.08;

    /// <summary>Minimum contrast against <see cref="Backdrop"/>; higher with Increase Contrast.</summary>
    public double Contrast { get; set; } = MinimumContrast;

    /// <summary>Opacity of words not yet sung.</summary>
    public double Dim { get; set; } = UnsungDim;

    /// <summary>False while the colours are still easing towards new ones: the frame clock must keep running.</summary>
    public bool Settled { get; set; } = true;

    private readonly OkLab Stop(int index) => Stops[index % Math.Max(1, StopCount)];

    // ---- Dark lyrics (the Mac's next/tester-fixes 8a52096, 15 Nov 2026) ----

    /// <summary>0 light lyrics … 1 dark lyrics: on a light wallpaper every colour turns to its deep version.</summary>
    public double Dark { get; set; }

    /// <summary>What the deep colours are measured against: the wallpaper without the automatic backing, smoothed.</summary>
    public double DeepBackdrop { get; set; } = 0.6;

    /// <summary>The neutral the unsung words of Sung sweep deepen to (a faint tint of the palette's second colour).</summary>
    public OkLab UnsungDeep { get; set; } = OkLab.FromLch(0.32, 0, 0);

    /// <summary>The neutral Static white deepens to.</summary>
    public static OkLab WhiteDeep => OkLab.FromLch(0.32, 0, 0);

    /// <summary>
    /// <paramref name="lightRest"/> (and <paramref name="lightSung"/>) as drawn now: mixed toward the deep versions of
    /// <paramref name="restSource"/> (and <paramref name="sungSource"/>) by <see cref="Dark"/>, the glow toward a light
    /// halo. Unchanged while the lyrics are light.
    /// </summary>
    public readonly LyricInkSample Toned(OkLab lightRest, OkLab restSource, OkLab? lightSung = null, OkLab? sungSource = null)
    {
        if (!(Dark > 0.0005)) return SampleOf(lightRest, lightSung);
        var dark = Math.Min(1, Dark);
        var rest = Perceptual.Mix(lightRest, Perceptual.Deep(restSource, DeepBackdrop, Contrast), dark);
        OkLab? sung = lightSung is { } ls ? Perceptual.Mix(ls, Perceptual.Deep(sungSource ?? ls, DeepBackdrop, Contrast), dark) : null;
        var sample = SampleOf(rest, sung);
        var source = lightSung ?? lightRest;
        var lightGlow = Perceptual.Clipped(new OkLab(Math.Min(1, source.L + 0.06), source.A * 0.8, source.B * 0.8));
        var halo = new OkLab(0.97, lightGlow.A * 0.1, lightGlow.B * 0.1);
        return sample with { Glow = Perceptual.ToColour(Perceptual.Mix(lightGlow, halo, dark)) };
    }

    /// <summary>The colours for a word at <paramref name="slot"/>, its 0...1 position along the line.</summary>
    public readonly LyricInkSample Sample(double slot, ulong seed = 0)
    {
        switch (Mode)
        {
            case LyricColourMode.Standard:
                return Base;
            case LyricColourMode.SungSweep:
            {
                // Unsung words stay white; each word sweeps to its own colour on the drifting gradient (the Mac's
                // next/tester-fixes, 15 Nov 2026: not one colour for every word).
                var palette = Gradient(slot, seed);
                var sung = Perceptual.Readable(palette, Backdrop, Contrast, keepingColour: true);
                var swept = Toned(Perceptual.Readable(OkLab.White, Backdrop, Contrast), UnsungDeep, sung, palette);
                swept.Dim = Dim;
                return swept;
            }
            default:
            {
                // Lifted keeping its colour (chroma kept at 2.5:1 where it can be), so a custom gradient doesn't turn
                // white on a mid or light wallpaper (the owner's approval, relayed 15 Nov 2026).
                var palette = Gradient(slot, seed);
                var colour = Perceptual.Readable(palette, Backdrop, Contrast, keepingColour: true);
                var sample = Toned(colour, palette);
                sample.Dim = Dim;
                return sample;
            }
        }
    }

    /// <summary>A point on the line's gradient, blended round the hue wheel so different pastels keep their colour.</summary>
    public readonly OkLab Gradient(double slot, ulong seed = 0)
    {
        if (Mode == LyricColourMode.MusicReactive) return Perceptual.Mix(Stops.S0, Stops.S1, slot);
        if (StopCount <= 1) return Stops.S0;
        var t = (Phase + slot * 0.42 + LyricMotion.Jitter(seed, 21) * 0.02) % 1;
        if (t < 0) t += 1;
        var position = t * StopCount;
        var index = (int)Math.Floor(position);
        return Turning(Stop(index), Stop(index + 1), position - index);
    }

    /// <summary>A blend in OKLCH: lightness and chroma straight, hue the short way round. A colour with next to no hue takes the other's.</summary>
    public static OkLab Turning(OkLab from, OkLab to, double amount)
    {
        var t = Math.Min(1, Math.Max(0, amount));
        double l = from.L + (to.L - from.L) * t, chroma = from.Chroma + (to.Chroma - from.Chroma) * t;
        if (from.Chroma < 0.02) return OkLab.FromLch(l, chroma, to.Hue);
        if (to.Chroma < 0.02) return OkLab.FromLch(l, chroma, from.Hue);
        var turn = (to.Hue - from.Hue) % (2 * Math.PI);
        if (turn > Math.PI) turn -= 2 * Math.PI; else if (turn < -Math.PI) turn += 2 * Math.PI;
        return OkLab.FromLch(l, chroma, from.Hue + turn * t);
    }

    /// <summary>Keep the source hue, with more colour and room for contrast lifting. An intentionally neutral custom stop stays neutral.</summary>
    public static OkLab Pastel(OkLab colour, double? fallbackHue = null)
    {
        var lightness = Math.Min(0.86, Math.Max(0.76, colour.L));
        if (colour.Chroma < 0.02)
            return fallbackHue is { } hue ? OkLab.FromLch(lightness, 0.11, hue) : colour;
        return OkLab.FromLch(lightness, Math.Min(0.21, Math.Max(0.105, colour.Chroma * 1.4)), colour.Hue);
    }

    public static OkLab ApplyingStrength(double strength, OkLab colour)
    {
        // Very pale colours run out of sRGB chroma quickly. Deepen them slightly above 100% to make room for richer
        // colour. The usual contrast pass still lifts the result on bright backgrounds.
        var depth = 0.13 * Math.Max(0, strength - 1) * Math.Min(1, colour.Chroma / 0.09);
        return new OkLab(colour.L - depth, colour.A * strength, colour.B * strength);
    }

    /// <summary>The drawn colours for a readable lyric colour, and the sung colour when it sweeps.</summary>
    public static LyricInkSample SampleOf(OkLab colour, OkLab? sung = null)
    {
        var rest = Perceptual.ToColour(colour);
        var fill = sung is { } s ? Perceptual.ToColour(s) : rest;
        var source = sung ?? colour;
        var glowSource = Perceptual.Clipped(new OkLab(Math.Min(1, source.L + 0.06), source.A * 0.8, source.B * 0.8));
        return new LyricInkSample(rest, fill, Perceptual.ToColour(glowSource), Perceptual.ToColour(colour, 0.7), sung is not null);
    }

    public static LyricInk White => new();

    /// <summary>White ink, with the unsung level raised for Increase Contrast.</summary>
    public static LyricInk WhiteFor(bool increasedContrast)
    {
        if (!increasedContrast) return White;
        var ink = new LyricInk { Contrast = IncreasedContrast, Dim = IncreasedUnsungDim };
        var sample = ink.Base;
        sample.Dim = IncreasedUnsungDim;
        ink.Base = sample;
        return ink;
    }
}

/// <summary>What the wallpaper shows (the Mac's <c>Settings.BackgroundMode</c>).</summary>
public enum WallpaperBackgroundMode
{
    Auto,
    Pattern,
    Shader,
    ColourOnly,
    Visualizer,
    Desktop,
    Custom,
    MusicVideo,
}

/// <summary>
/// How bright the wallpaper is behind the lyric stage, as WCAG relative luminance, once everything the wallpaper lays
/// over it is counted: its scrim and the text backing. Generated wallpapers are estimated from the album palette; a
/// picture from its measured luminance, and as a cautious mid tone until then.
/// </summary>
public static class LyricBackdrop
{
    /// <summary>The share of the text backing a line gets where it reaches the side of the stage.</summary>
    public const double EdgeBacking = 0.5;

    /// <summary>
    /// The most an unmeasured music video is taken to be (the Mac's c1c33d69): under the light-text line, so lyrics over
    /// it stay light. Footage changes every shot; guessed at 0.35 it read as light enough for dark text, which turned
    /// muddy with a pale glow over a dim video.
    /// </summary>
    public const double UnmeasuredVideoCeiling = 0.2;

    /// <summary>The backdrop at the brightest place a line reaches: the ends of the stage.</summary>
    /// <param name="measured">The luminance behind the stage measured from the picture shown, when there is one.</param>
    /// <param name="backing">The backing the user set by hand, or null for the automatic amount.</param>
    /// <param name="tinted">Whether the wallpaper lays the album's colours over an imported picture.</param>
    /// <param name="videoDim">A music video's "Video dimming" (0–1).</param>
    public static double Luminance(WallpaperBackgroundMode mode, bool motion, IReadOnlyList<Vector3> palette, double scrim,
                                   double? measured = null, double? backing = null, double target = 4.5,
                                   bool tinted = false, bool pastelText = false, bool increaseContrast = false,
                                   double videoDim = 0)
    {
        var behind = BeforeBacking(mode, motion, palette, scrim, measured, tinted, videoDim);
        if (backing is null && pastelText)
        {
            var pastelTarget = LyricLegibility.PastelBackingTarget(increaseContrast);
            var shade = LyricLegibility.StageBacking(behind, 0, pastelTarget);
            // Automatic backing holds at full strength across the lyric stage.
            return LyricLegibility.Darkened(behind, shade);
        }
        var amount = backing ?? LyricLegibility.Backing(behind, 0, target);
        return LyricLegibility.Darkened(behind, Math.Min(1, Math.Max(0, amount)) * EdgeBacking);
    }

    /// <summary>Whether no colour can reach <paramref name="ratio"/> against <paramref name="backdrop"/>, so every mode is drawn white.</summary>
    public static bool ForcesWhite(double backdrop, double ratio = LyricInk.MinimumContrast) =>
        ratio * (Math.Min(1, Math.Max(0, backdrop)) + 0.05) - 0.05 >= 1;

    /// <summary>The wallpaper and its scrim, before the text backing.</summary>
    public static double BeforeBacking(WallpaperBackgroundMode mode, bool motion, IReadOnlyList<Vector3> palette,
                                       double scrim, double? measured = null, bool tinted = false, double videoDim = 0)
    {
        // Called every frame: a plain loop, no intermediate arrays.
        double brightest = 0, total = 0;
        for (var i = 0; i < palette.Count; i++)
        {
            var value = Perceptual.Luminance(Double3.From(palette[i]));
            brightest = Math.Max(brightest, value);
            total += value;
        }
        if (palette.Count == 0) { brightest = 0.1; total = 0.1; }
        var average = total / Math.Max(1, palette.Count);
        // The blobs are blurred into one another, so the field is mostly their average, with a margin for passing
        // over the brightest.
        var field = 0.35 * brightest + 0.65 * average;
        var strength = Math.Min(2, Math.Max(0, scrim));
        switch (mode)
        {
            case WallpaperBackgroundMode.Desktop:
                // Nothing is laid over the desktop picture but the backing.
                return Math.Min(1, Math.Max(0, measured ?? LyricLegibility.UnknownBackdropLuminance));
            case WallpaperBackgroundMode.Custom:
            {
                // The album's colours at 18% over the picture, then the scrim, lightest where the lyrics sit.
                var picture = Math.Min(1, Math.Max(0, measured ?? LyricLegibility.UnknownBackdropLuminance));
                if (tinted && palette.Count > 0)
                    picture = LyricLegibility.Linear(0.82 * LyricLegibility.Encoded(picture)
                        + 0.18 * LyricLegibility.Encoded(Math.Min(1, field)));
                return LyricLegibility.Darkened(picture, Math.Min(1, 0.12 * strength));
            }
            case WallpaperBackgroundMode.MusicVideo:
            {
                // Footage under the video's dimming, then the scrim. Unmeasured footage is never guessed light enough
                // for dark text: light text and its backing hold on any shot.
                var footage = Math.Min(1, Math.Max(0, measured ?? LyricLegibility.UnknownBackdropLuminance));
                var dimmed = LyricLegibility.Darkened(footage, Math.Min(1, Math.Max(0, videoDim)));
                var shaded = LyricLegibility.Darkened(dimmed, Math.Min(1, 0.12 * strength));
                return measured is null ? Math.Min(shaded, UnmeasuredVideoCeiling) : shaded;
            }
            case WallpaperBackgroundMode.ColourOnly when measured is not null:
                return LyricLegibility.Darkened(measured ?? field, WallpaperShade.ScrimAtStage(strength));
            case WallpaperBackgroundMode.Visualizer:
                return Math.Min(1, field * (1 - Math.Min(0.9, 0.25 * strength)) + 0.06);
            default:
                if (motion) return field * (1 - Math.Min(0.9, 0.4 * strength)) * 0.6;
                return Math.Min(1, field * (1 - Math.Min(0.9, 0.25 * strength)) + 0.03);
        }
    }
}
