// Covers › "Exclude this album" and "Excluded albums" (the Mac's motionExclusions). The Mac's exclusion helpers sit on
// lines from the squashed Verso import in Settings.swift, so they are re-implemented from what they do: an exclusion
// belongs to an album, not a song (excluding one song would leave the rest of the album animating), and is stored as
// "artist|album" in lower case; a song with neither artist nor album can't be excluded. The key name and format are the
// Mac's, so a list carried over reads the same.

namespace Lunalyr.Shell.Settings;

public sealed partial record AppSettings
{
    /// <summary>Albums whose animated cover never plays, as "artist|album" in lower case. Mac <c>motionExclusions</c>.</summary>
    public IReadOnlyList<string> MotionExclusions { get; init; } = [];

    /// <summary>The key an album is excluded by: "artist|album", lower case.</summary>
    public static string MotionExclusionKey(string? artist, string? album) =>
        $"{(artist ?? "").ToLowerInvariant()}|{(album ?? "").ToLowerInvariant()}";

    /// <summary>Whether the album of <paramref name="artist"/> and <paramref name="album"/> is excluded.</summary>
    public bool ExcludesMotion(string? artist, string? album) =>
        (MotionExclusions ?? []).Contains(MotionExclusionKey(artist, album), StringComparer.Ordinal);

    /// <summary>These settings with the album excluded (added at the end) or allowed again. A song with no artist or album is left as it is.</summary>
    public AppSettings WithMotionExcluded(bool excluded, string? artist, string? album)
    {
        var key = MotionExclusionKey(artist, album);
        if (key == "|") return this;
        var list = (MotionExclusions ?? []).ToList();
        if (excluded)
        {
            if (list.Contains(key, StringComparer.Ordinal)) return this;
            list.Add(key);
        }
        else if (list.RemoveAll(k => k == key) == 0)
        {
            return this;
        }
        return this with { MotionExclusions = list };
    }

    /// <summary>How the Covers page names an excluded album: "artist · album".</summary>
    public static string MotionExclusionTitle(string key) => key.Replace("|", " · ", StringComparison.Ordinal);
}
