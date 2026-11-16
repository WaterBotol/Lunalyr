using System.Numerics;
using Lunalyr.Core.Colour;
using Lunalyr.Render.Scene;

namespace Lunalyr.Render.Compositions;

/// <summary>
/// The lyric colours the compositions bake their words with: the Mac's default "Album drift" ink, settled for the
/// scene's palette (<see cref="Palette.ArtworkStops"/> made readable against <see cref="Palette.TextBackdrop"/>).
/// </summary>
internal static class LyricInks
{
    /// <summary>The Mac's drift speed: <c>LyricInkDirector</c> moves 0.022 of a loop per second at normal speed.</summary>
    public const double DriftPerSecond = 0.022;

    /// <summary>
    /// The ink a palette settles on: the director run to rest for its cover (as the Mac's settings preview does), so the
    /// first line is in the album's colours rather than part way through a fade from white.
    /// </summary>
    public static LyricInk Settled(Palette palette) => Settled(palette, LyricStyle.Default);

    /// <summary>
    /// The ink a palette settles on in the style's colour mode, strength and custom colours ("Colour"), with its
    /// highlight tiles (mode and custom colour). <paramref name="vocal"/> is how much the voice carries (0…1), which
    /// "Music reactive" colours by.
    /// </summary>
    public static LyricInk Settled(Palette palette, LyricStyle style, double vocal = ReactiveRest)
    {
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentNullException.ThrowIfNull(style);
        var mode = HighlightMode(style.HighlightMode);
        return new LyricInkPreview().Ink(new LyricInkInputs(style.ColourMode)
        {
            Speed = style.ColourSpeed,
            Strength = style.ColourStrength,
            Custom = CustomColours(style),
            Pastel = LyricColourOptions.PastelApplies(style.ColourPastel, style.ColourMode),
            Album = palette.ArtworkStops,
            Backdrop = palette.TextBackdrop,
            Unbacked = palette.TextUnbacked,
            Vocal = double.IsFinite(vocal) ? Math.Clamp(vocal, 0, 1) : ReactiveRest,
            Highlight = mode,
            HighlightCustom = mode == LyricHighlightMode.Custom
                ? ColourHex.ToOkLab(style.HighlightCustom) ?? ColourHex.ToOkLab(LyricHighlight.DefaultCustomHex)
                : null,
        }, 0);
    }

    /// <summary>The voice level "Music reactive" rests at between lines, and holds under Reduce Motion (the Mac's director).</summary>
    public const double ReactiveRest = 0.3;

    /// <summary>"Music reactive" settles its colours at this many voice levels, so a song needs at most this many inks.</summary>
    public const int ReactiveLevels = 6;

    /// <summary>The voice level a line is coloured at: its loudest moment, one of <see cref="ReactiveLevels"/> steps.</summary>
    public static int ReactiveLevel(double intensity) =>
        double.IsFinite(intensity) ? (int)Math.Round(Math.Clamp(intensity, 0, 1) * (ReactiveLevels - 1)) : 0;

    /// <summary>The style's custom colours in OKLab (bad ones dropped).</summary>
    public static IReadOnlyList<OkLab> CustomColours(LyricStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        var colours = new List<OkLab>();
        foreach (var hex in style.ColourCustom)
            if (ColourHex.ToOkLab(hex) is { } colour) colours.Add(colour);
        return colours;
    }

    /// <summary>Whether two styles give the same settled ink: the same colour mode, strength, custom colours and highlight.</summary>
    public static bool SameInk(LyricStyle a, LyricStyle b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a.ColourMode == b.ColourMode && a.ColourStrength.Equals(b.ColourStrength) && Equals(a.ColourCustom, b.ColourCustom)
            && a.HighlightMode == b.HighlightMode && a.HighlightCustom == b.HighlightCustom;
    }

    /// <summary>A highlight mode as the Mac stores it ("complement", "album", "custom"); unknown ones are "complement".</summary>
    public static LyricHighlightMode HighlightMode(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "album" => LyricHighlightMode.Album,
        "custom" => LyricHighlightMode.Custom,
        _ => LyricHighlightMode.Complement,
    };

    /// <summary>The drift under Reduce Motion: ten times slower (the Mac's director, <c>calm ? 0.1 : 1</c>).</summary>
    public const double ReducedDrift = 0.1;

    /// <summary>
    /// The drift's phase at a song time, quantised as the director quantises it (1/720 of a loop); ten times slower
    /// under Reduce Motion.
    /// </summary>
    public static double DriftPhase(double songSeconds, bool reduceMotion = false, double speed = 1)
    {
        if (!double.IsFinite(songSeconds)) return 0;
        // "Colour speed" (0.25–2) scales the drift, as the Mac's director scales its phase.
        var pace = double.IsFinite(speed) ? Math.Clamp(speed, LyricColourOptions.SpeedMin, LyricColourOptions.SpeedMax) : 1;
        var phase = songSeconds * DriftPerSecond * pace * (reduceMotion ? ReducedDrift : 1) % 1;
        if (phase < 0) phase += 1;
        return Math.Round(phase * 720) / 720 % 1;
    }

    /// <summary>Whether two palettes give the same ink (a new snapshot of the same cover keeps every baked word).</summary>
    public static bool SameInk(Palette a, Palette b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Text != b.Text || !a.TextBackdrop.Equals(b.TextBackdrop) || !Nullable.Equals(a.TextUnbacked, b.TextUnbacked)
            || a.ArtworkStops.Count != b.ArtworkStops.Count) return false;
        for (var i = 0; i < a.ArtworkStops.Count; i++)
            if (a.ArtworkStops[i] != b.ArtworkStops[i]) return false;
        return true;
    }

    public static Vector4 Vector(SrgbColour colour) => new((float)colour.R, (float)colour.G, (float)colour.B, (float)colour.A);
}
