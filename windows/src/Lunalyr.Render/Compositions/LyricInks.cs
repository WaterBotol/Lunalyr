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
    public static LyricInk Settled(Palette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        return new LyricInkPreview().Ink(new LyricInkInputs(LyricColourMode.AlbumDrift)
        {
            Album = palette.ArtworkStops,
            Backdrop = palette.TextBackdrop,
        }, 0);
    }

    /// <summary>The drift's phase at a song time, quantised as the director quantises it (1/720 of a loop).</summary>
    public static double DriftPhase(double songSeconds)
    {
        if (!double.IsFinite(songSeconds)) return 0;
        var phase = songSeconds * DriftPerSecond % 1;
        if (phase < 0) phase += 1;
        return Math.Round(phase * 720) / 720 % 1;
    }

    /// <summary>Whether two palettes give the same ink (a new snapshot of the same cover keeps every baked word).</summary>
    public static bool SameInk(Palette a, Palette b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Text != b.Text || !a.TextBackdrop.Equals(b.TextBackdrop) || a.ArtworkStops.Count != b.ArtworkStops.Count) return false;
        for (var i = 0; i < a.ArtworkStops.Count; i++)
            if (a.ArtworkStops[i] != b.ArtworkStops[i]) return false;
        return true;
    }

    public static Vector4 Vector(SrgbColour colour) => new((float)colour.R, (float)colour.G, (float)colour.B, (float)colour.A);
}
