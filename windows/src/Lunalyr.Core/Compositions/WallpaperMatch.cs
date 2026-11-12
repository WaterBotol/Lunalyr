// "Match the music" for the wallpaper: which kind of background a song gets (a GPU effect or a line pattern) and which
// effect or pattern. Re-implemented from a description of the Mac's behaviour (NowPlayingView.layer(for:) and
// ShaderStyle.matching, both on lines that trace back to the squashed Verso import, so nothing is translated); see
// docs/compositions/matching.md, "The wallpaper". The catalogue of effects and patterns (ids, titles and where each
// sits on the three axes) comes from the layers that draw them, never from the Mac's tables.

namespace Lunalyr.Core.Compositions;

/// <summary>
/// Where a song, an effect or a pattern sits on three axes, each 0...1, so a song can be matched to the nearest
/// rather than drawn out of a bucket.
/// </summary>
/// <param name="Energy">0 still, 1 frantic.</param>
/// <param name="Density">0 open and sparse, 1 busy (a busy backdrop competes with the lyrics).</param>
/// <param name="Organic">0 geometric and hard-edged, 1 flowing and hand-made.</param>
public readonly record struct WallpaperCharacter(double Energy, double Density, double Organic)
{
    public static WallpaperCharacter Neutral => new(0.5, 0.5, 0.5);

    /// <summary>
    /// A song's character from <see cref="TrackCharacter"/>. Windows knows no separate density for a song, so its
    /// energy stands in: a lively song can carry a busier backdrop, a slow one gets an open one.
    /// </summary>
    public static WallpaperCharacter Of(TrackCharacter song) =>
        new(Clamp(song.Energy), Clamp(song.Energy), Clamp(song.Organic));

    private static double Clamp(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0.5;
}

/// <summary>One effect or pattern a layer can draw, as "Match the music" sees it.</summary>
/// <param name="Id">The style's id ("aurora"); its layer is registered as <c>shader.&lt;id&gt;</c> or <c>pattern.&lt;id&gt;</c>.</param>
/// <param name="Character">Where it sits on the three axes.</param>
/// <param name="Heavy">Expensive per pixel: left out of the match while power is being saved.</param>
public sealed record WallpaperCandidate(string Id, WallpaperCharacter Character, bool Heavy = false);

/// <summary>The kind of background "Match the music" gives a song.</summary>
public enum WallpaperKind
{
    /// <summary>A GPU effect (<c>shader.&lt;id&gt;</c>).</summary>
    Effect,

    /// <summary>A line pattern over the album colours (<c>pattern.&lt;id&gt;</c>).</summary>
    Pattern,
}

/// <summary>What a song's wallpaper is: its kind and the effect or pattern id (null when there is none to draw).</summary>
public readonly record struct WallpaperPick(WallpaperKind Kind, string? StyleId)
{
    /// <summary>The background layer id this pick draws (<c>shader.aurora</c>), or null when there is no style.</summary>
    public string? LayerId => StyleId is null ? null : (Kind == WallpaperKind.Effect ? "shader." : "pattern.") + StyleId;
}
