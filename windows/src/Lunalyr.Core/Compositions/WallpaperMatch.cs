// "Custom Mix" for the wallpaper: which kind of background a song gets (a GPU effect or a line pattern) and which
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

/// <summary>One effect or pattern a layer can draw, as "Custom Mix" sees it.</summary>
/// <param name="Id">The style's id ("aurora"); its layer is registered as <c>shader.&lt;id&gt;</c> or <c>pattern.&lt;id&gt;</c>.</param>
/// <param name="Character">Where it sits on the three axes.</param>
/// <param name="Heavy">Expensive per pixel: left out of the match while power is being saved.</param>
public sealed record WallpaperCandidate(string Id, WallpaperCharacter Character, bool Heavy = false);

/// <summary>The kind of background "Custom Mix" gives a song.</summary>
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

/// <summary>
/// Chooses a song's wallpaper for "Custom Mix", GPU effects and Line patterns (docs/compositions/matching.md, "The
/// wallpaper"). Deterministic: the same song, settings and catalogue always give the same pick.
/// </summary>
public static class WallpaperMatch
{
    /// <summary>Above this energy "Custom Mix" gives a song a GPU effect; at or below it, a line pattern.</summary>
    public const double EffectEnergy = 0.72;

    /// <summary>How many of the nearest styles a song chooses between, by its seed, so like songs don't all look alike.</summary>
    public const int Nearest = 4;

    /// <summary>
    /// The kind and style for a song. <paramref name="mode"/> is the <c>backgroundMode</c> ("auto", "shader" or
    /// "pattern"); a pinned id that is in its catalogue is used as it is (an effect marked heavy only while power isn't
    /// being saved); otherwise the song is matched among <paramref name="effects"/> or <paramref name="patterns"/>.
    /// "Custom Mix" with nothing in the kind it would choose takes the other kind. The pick's style is null when
    /// its catalogue is empty.
    /// </summary>
    public static WallpaperPick Pick(string mode, TrackCharacter song, string? pinnedEffect, string? pinnedPattern,
        IReadOnlyList<WallpaperCandidate> effects, IReadOnlyList<WallpaperCandidate> patterns, bool savingPower = false)
    {
        ArgumentNullException.ThrowIfNull(effects);
        ArgumentNullException.ThrowIfNull(patterns);
        var kind = mode switch
        {
            "shader" => WallpaperKind.Effect,
            "pattern" => WallpaperKind.Pattern,
            _ => song.Energy > EffectEnergy ? WallpaperKind.Effect : WallpaperKind.Pattern,
        };
        if (mode is not ("shader" or "pattern"))
        {
            if (kind == WallpaperKind.Effect && effects.Count == 0 && patterns.Count > 0) kind = WallpaperKind.Pattern;
            else if (kind == WallpaperKind.Pattern && patterns.Count == 0 && effects.Count > 0) kind = WallpaperKind.Effect;
        }
        var target = WallpaperCharacter.Of(song);
        return kind == WallpaperKind.Effect
            ? new WallpaperPick(kind, Style(effects, pinnedEffect, target, song.Seed, savingPower))
            : new WallpaperPick(kind, Style(patterns, pinnedPattern, target, song.Seed, savingPower: false));
    }

    /// <summary>The pinned style when it can be drawn, else the song's match among the nearest few; null for none.</summary>
    public static string? Style(IReadOnlyList<WallpaperCandidate> catalogue, string? pinned, WallpaperCharacter target, int seed,
        bool savingPower)
    {
        if (catalogue.Count == 0) return null;
        if (!string.IsNullOrEmpty(pinned))
        {
            foreach (var style in catalogue)
                if (style.Id == pinned && !(savingPower && style.Heavy)) return style.Id;
        }
        var pool = savingPower && catalogue.Any(s => !s.Heavy) ? catalogue.Where(s => !s.Heavy).ToArray() : [.. catalogue];
        var ranked = pool.OrderBy(s => Distance(s.Character, target)).ThenBy(s => s.Id, StringComparer.Ordinal).Take(Nearest).ToArray();
        var index = (int)(Noise(seed) * ranked.Length) % ranked.Length;
        return ranked[index].Id;
    }

    /// <summary>
    /// How far a style sits from a song: squared differences, energy counting most (it is what a listener notices
    /// first), then organic (what tells two styles of the same pace apart), then density.
    /// </summary>
    public static double Distance(WallpaperCharacter style, WallpaperCharacter song)
    {
        double energy = style.Energy - song.Energy, organic = style.Organic - song.Organic, density = style.Density - song.Density;
        return energy * energy + 0.75 * organic * organic + 0.45 * density * density;
    }

    /// <summary>A stable number in [0, 1) from a song's seed (SplitMix64), the same on every run and machine.</summary>
    public static double Noise(int seed)
    {
        var z = unchecked((ulong)(long)seed + 0x9E3779B97F4A7C15UL);
        z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
        z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
        z ^= z >> 31;
        return (z >> 11) * (1.0 / (1UL << 53));
    }
}
