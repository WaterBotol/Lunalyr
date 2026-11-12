// The official-video ranking, ported from the Mac's MusicVideoCore.swift (YouTubeSearchPage.officialVideos, channel(isOf:),
// lengthSeconds; Lunalyr's own code). The Mac fills it by reading youtube.com's results page; that is scraping, so
// Windows fills it from the YouTube Data API (YouTubeDataApi.cs) and the page reader is not ported.

using System.Globalization;
using Lunalyr.Core.Lyrics;

namespace Lunalyr.Core.MusicVideo;

/// <summary>One video in a search's results.</summary>
/// <param name="VerifiedArtist">
/// The channel carries YouTube's verified-artist badge. The Mac reads it from the results page; the Data API has no field
/// for it, so on Windows it is always false and the channel's name (or VEVO) has to show it is the artist's.
/// </param>
/// <param name="Duration">The video's length in seconds, when known.</param>
/// <param name="Views">YouTube-style view count ("12M views"), when known.</param>
/// <param name="Uploaded">How long ago it was uploaded ("3 years ago"), when known.</param>
/// <param name="ThumbnailUrl">The still the API returned for it (never built by hand), or null.</param>
public sealed record YouTubeVideoResult(string VideoId, string Title, string Channel, bool VerifiedArtist = false,
    double? Duration = null, string? Views = null, string? Uploaded = null, string? ThumbnailUrl = null);

public static class YouTubeSearch
{
    /// <summary>What the automatic lookup searches for: "&lt;artist&gt; &lt;title&gt; official music video".</summary>
    public static string Query(string title, string artist) => $"{artist} {title} official music video";

    /// <summary>The official video among <paramref name="results"/>: the first of <see cref="OfficialVideos"/>.</summary>
    public static YouTubeVideoResult? OfficialVideo(IReadOnlyList<YouTubeVideoResult> results, string title, string artist,
        double? songDuration = null) => OfficialVideos(results, title, artist, songDuration).FirstOrDefault();

    /// <summary>An upload this close to the song's length plays it from its first second: no intro to guess.</summary>
    public const double LengthMatch = 2.0;

    /// <summary>
    /// Words in a title that mark an upload as something other than the video (whole words, any case): audio and art
    /// tracks, lyric and visualiser uploads, live, cover and edited versions. Allowed when the song's own title has them.
    /// </summary>
    public static readonly IReadOnlyList<string> ExcludedWords =
    [
        "lyric", "lyrics", "audio", "official audio", "audio only", "art track", "visualizer", "visualiser", "live", "cover",
        "karaoke", "instrumental", "reaction", "remix", "sped up", "slowed", "8d", "nightcore", "tutorial",
    ];

    /// <summary>
    /// Candidate official videos among <paramref name="results"/>, best first. Each must be this song
    /// (<see cref="MusicVideoTitleMatch"/>), come from the artist (the channel is the artist's own, a VEVO channel, or
    /// carries the verified-artist badge; never a "- Topic" channel), and not be a live, concert or fan upload, nor any of
    /// <see cref="ExcludedWords"/>. "Official music video" ranks above "official video", above "official" or "music video";
    /// ties keep YouTube's order. Given the song's length, a video named as one that lasts as long as the song comes first.
    /// </summary>
    public static IReadOnlyList<YouTubeVideoResult> OfficialVideos(IReadOnlyList<YouTubeVideoResult> results, string title,
        string artist, double? songDuration = null)
    {
        var song = LyricQuery.Normalize(title);
        var singers = MusicVideoTitleMatch.ArtistParts(artist);
        if (song.Length == 0 || singers.Count == 0) return [];
        var ranked = new List<(YouTubeVideoResult Result, int Score, bool SameLength, int Index)>();
        for (var index = 0; index < Math.Min(12, results.Count); index++)
        {
            var result = results[index];
            var name = LyricQuery.Normalize(result.Title);
            var channel = LyricQuery.Normalize(result.Channel);
            var channelKey = MusicVideoTitleMatch.Normal(result.Channel);
            if (!MusicVideoTitleMatch.Matches(result.Title, title, artist)) continue;
            if (channel.EndsWith(" topic", StringComparison.Ordinal)) continue;
            var ownChannel = singers.Any(s => Channel(channelKey, s));
            var vevo = channelKey.EndsWith("vevo", StringComparison.Ordinal);
            if (!ownChannel && !vevo && !result.VerifiedArtist) continue;
            if (MusicVideoTitleMatch.IsLiveOrFanUpload(result.Title, title)) continue;
            if (ExcludedWords.Any(w => ContainsWords(name, w) && !ContainsWords(song, w))) continue;
            var score = 1;
            if (ContainsWords(name, "official music video")) score += 4;
            else if (ContainsWords(name, "official video")) score += 3;
            else if (ContainsWords(name, "official") || ContainsWords(name, "music video") || ContainsWords(name, "mv")) score += 1;
            var namedAsVideo = score > 1;
            if (vevo || ownChannel) score += 1;
            var sameLength = namedAsVideo && songDuration is > 0 && result.Duration is { } length
                && Math.Abs(length - songDuration.Value) <= LengthMatch;
            ranked.Add((result, score, sameLength, index));
        }
        ranked.Sort((a, b) =>
        {
            if (a.SameLength != b.SameLength) return a.SameLength ? -1 : 1;
            return a.Score != b.Score ? b.Score.CompareTo(a.Score) : a.Index.CompareTo(b.Index);
        });
        return [.. ranked.Select(r => r.Result)];
    }

    /// <summary>
    /// A channel named after the artist: the name itself, or starting or ending with it ("WallowsOfficial", "Ayase /
    /// YOASOBI"). A short name inside another ("IU" in "Liquid Music") is not the artist's.
    /// </summary>
    public static bool Channel(string channelKey, string artist)
    {
        if (artist.Length == 0) return false;
        return channelKey == artist || channelKey.StartsWith(artist, StringComparison.Ordinal)
            || channelKey.EndsWith(artist, StringComparison.Ordinal)
            || (artist.Length >= 5 && channelKey.Contains(artist, StringComparison.Ordinal));
    }

    /// <summary>Whole-word containment on normalised text.</summary>
    private static bool ContainsWords(string text, string words) =>
        words.Length > 0 && (" " + text + " ").Contains(" " + words + " ", StringComparison.Ordinal);

    /// <summary>"3:45" or "1:02:03" as seconds; null for anything else ("LIVE").</summary>
    public static double? LengthSeconds(string text)
    {
        var parts = text.Trim().Split(':');
        if (parts.Length is < 2 or > 3) return null;
        var total = 0.0;
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0 || (part.Length > 2 && i != 0) || !part.All(char.IsAsciiDigit)
                || !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var value)) return null;
            total = total * 60 + value;
        }
        return total;
    }
}
