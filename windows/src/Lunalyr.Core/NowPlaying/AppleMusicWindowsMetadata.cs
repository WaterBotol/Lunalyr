namespace Lunalyr.Core.NowPlaying;

/// <summary>
/// Apple Music for Windows reports a song's artist and album joined in the artist field ("Wallows — Nothing Happens",
/// with an em dash and spaces) and leaves the album empty; the album artist carries the same joined text. Seen live on
/// 13 Nov 2026 (AppleInc.AppleMusicWin). This splits them back, so lyric searches get the artist alone and the album is
/// known (opening title, version tags, original-title lookups). Other players are left exactly as they report.
/// </summary>
public static class AppleMusicWindowsMetadata
{
    /// <summary>The separator Apple Music for Windows puts between the artist and the album.</summary>
    public const string Separator = " — ";

    /// <summary>
    /// <paramref name="track"/> with the artist and album split, when it comes from Apple Music, has no album of its own,
    /// and its artist holds the separator. The split is at the first separator: artist names with an em dash are
    /// rarer than album titles with one.
    /// </summary>
    public static MediaTrackInfo Normalize(MediaTrackInfo track, MediaSourceKind source)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (source != MediaSourceKind.AppleMusic || !string.IsNullOrWhiteSpace(track.AlbumTitle)) return track;
        if (Split(track.Artist) is not { } joined) return track;
        var albumArtist = Split(track.AlbumArtist) is { } a ? a.Artist : track.AlbumArtist;
        return track with { Artist = joined.Artist, AlbumTitle = joined.Album, AlbumArtist = albumArtist };
    }

    private static (string Artist, string Album)? Split(string text)
    {
        var at = text.IndexOf(Separator, StringComparison.Ordinal);
        if (at <= 0) return null;
        var artist = text[..at].Trim();
        var album = text[(at + Separator.Length)..].Trim();
        return artist.Length == 0 || album.Length == 0 ? null : (artist, album);
    }
}
