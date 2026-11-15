// Apple Music's animated album covers ("motion artwork"), as the Mac's MotionArtworkProvider finds them: the public iTunes
// search names the album's public music.apple.com page, the page's embedded JSON ("serialized-server-data") names the
// cover's HLS playlists on Apple's video CDN, and a playlist that is one complete, unencrypted MP4 read by byte ranges
// gives that MP4. No key, token or sign-in is involved, and an encrypted playlist (#EXT-X-KEY) is never used.
//
// Ported faithfully from Lunalyr's own lines of MotionArtworkProvider.swift (parse, singleMP4, matchingPages, the variant
// choice, the album key) and MotionArtworkView.swift's rendition(portrait:) extension. The file's Verso-attributed lines
// (the Motion struct, the search request, the fetch helpers and the search reply's shape) are re-implemented from what
// they do; see docs/PROVENANCE.md, "Animated covers".

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lunalyr.Core.Covers;

/// <summary>
/// An album's animated cover: the square rendition every display can play, and the tall (portrait) one when the album
/// has it. Each is either one progressive MP4 or, when Apple publishes none, the HLS playlist as found.
/// </summary>
public sealed record MotionArtwork(Uri Square, Uri? Tall)
{
    /// <summary>
    /// The rendition a display plays. A portrait display takes the tall cover, but only as a single progressive MP4: a
    /// tall cover left as Apple's raw playlist may never start or loop cleanly, so the square one fills the display then.
    /// </summary>
    public Uri Rendition(bool portrait) =>
        portrait && Tall is { } tall && IsMp4(tall) ? tall : Square;

    /// <summary>Whether <paramref name="uri"/> names an MP4 file (any case), not a playlist.</summary>
    public static bool IsMp4(Uri uri) =>
        Path.GetExtension(uri.AbsolutePath).Equals(".mp4", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One entry of the iTunes search's reply that the lookup reads.</summary>
public sealed record MotionSearchResult(string? CollectionViewUrl, string? CollectionName, string? ArtistName, string? TrackName = null);

/// <summary>The pure parts of the lookup: reading Apple's replies and choosing what to fetch next.</summary>
public static partial class MotionArtworkPages
{
    /// <summary>The rendition width the Mac prefers among a cover's H.264 variants (the closest wins).</summary>
    public const int PreferredWidth = 768;

    /// <summary>How many album pages, and how many H.264 variants, are tried at most.</summary>
    public const int MaxPages = 3, MaxVariants = 3;

    /// <summary>
    /// Motion artwork belongs to a release, so every track of an album shares one key (and one lookup). An album-less
    /// single is keyed by its own title, so two such singles never share a cover.
    /// </summary>
    public static string CacheKey(string? album, string artist, string title) =>
        $"{(string.IsNullOrEmpty(album) ? title : album)}|{artist}";

    /// <summary>
    /// The square and tall renditions on an album page, from the first section of the page's
    /// <c>serialized-server-data</c> only: that section is the album's own header, and recommendations further down can
    /// carry other albums' motion art. Null when the page has no such cover.
    /// </summary>
    public static MotionArtwork? Parse(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var open = ServerDataScript().Match(html);
        if (!open.Success) return null;
        var start = open.Index + open.Length;
        var end = html.IndexOf("</script>", start, StringComparison.Ordinal);
        if (end < 0) return null;
        try
        {
            using var document = JsonDocument.Parse(html.AsMemory(start, end - start));
            if (!document.RootElement.TryGetProperty("data", out var payloads) || payloads.ValueKind != JsonValueKind.Array) return null;
            // The first payload whose data has sections: its first section's items.
            foreach (var payload in payloads.EnumerateArray())
            {
                if (!Object(payload, "data", out var data) || !Array(data, "sections", out var sections)) continue;
                using var each = sections.EnumerateArray();
                if (!each.MoveNext()) continue;
                if (!Array(each.Current, "items", out var items)) continue;
                foreach (var item in items.EnumerateArray())
                {
                    if (!Object(item, "videoArtwork", out var artwork) || !Object(artwork, "dictionary", out var dictionary)) continue;
                    var square = Video(dictionary, "motionDetailSquare");
                    var tall = Video(dictionary, "motionDetailTall");
                    if ((square ?? tall) is { } primary) return new MotionArtwork(primary, tall);
                }
                return null;
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static Uri? Video(JsonElement dictionary, string name) =>
        Object(dictionary, name, out var asset) && asset.TryGetProperty("video", out var video) && video.ValueKind == JsonValueKind.String
            ? MediaUri(video.GetString())
            : null;

    private static bool Object(JsonElement element, string name, out JsonElement value) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object
        || Fail(out value);

    private static bool Array(JsonElement element, string name, out JsonElement value) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Array
        || Fail(out value);

    private static bool Fail(out JsonElement value)
    {
        value = default;
        return false;
    }

    /// <summary>
    /// A cover playlist's address, accepted only as HTTPS on Apple's video hosts (<c>mvod.itunes.apple.com</c> or
    /// <c>*.mzstatic.com</c>) ending in <c>.m3u8</c>.
    /// </summary>
    public static Uri? MediaUri(string? raw)
    {
        if (raw is null || !Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        if (!Path.GetExtension(uri.AbsolutePath).Equals(".m3u8", StringComparison.OrdinalIgnoreCase)) return null;
        return IsAppleVideoHost(uri.Host) ? uri : null;
    }

    /// <summary>The hosts Apple serves motion artwork from.</summary>
    public static bool IsAppleVideoHost(string host) =>
        host.Equals("mvod.itunes.apple.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".mzstatic.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The one MP4 a playlist reads by byte ranges, when it is exactly that: a finished playlist (<c>#EXT-X-ENDLIST</c>)
    /// of byte ranges over a single segment that is also its initialisation map, unencrypted (no <c>#EXT-X-KEY</c>),
    /// an HTTPS <c>.mp4</c> on the playlist's own host. Null for anything else.
    /// </summary>
    public static Uri? SingleMp4(string playlist, Uri relativeTo)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        ArgumentNullException.ThrowIfNull(relativeTo);
        var lines = Lines(playlist).Select(l => l.Trim()).ToList();
        var segments = lines.Where(l => l.Length > 0 && !l.StartsWith('#')).ToHashSet(StringComparer.Ordinal);
        if (!lines.Contains("#EXT-X-ENDLIST") || !lines.Any(l => l.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal))
            || lines.Any(l => l.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal)) || segments.Count != 1)
            return null;
        var segment = segments.First();
        if (!lines.Any(l => l.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal) && l.Contains($"URI=\"{segment}\"", StringComparison.Ordinal)))
            return null;
        if (!Uri.TryCreate(relativeTo, segment, out var video) || video.Scheme != Uri.UriSchemeHttps
            || !string.Equals(video.Host, relativeTo.Host, StringComparison.OrdinalIgnoreCase)
            || !Path.GetExtension(video.AbsolutePath).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            return null;
        return video;
    }

    /// <summary>
    /// A master playlist's H.264 (<c>avc1</c>) variants on Apple's hosts, nearest to <see cref="PreferredWidth"/> wide
    /// first, at most <see cref="MaxVariants"/>: the ones worth asking for a single MP4.
    /// </summary>
    public static IReadOnlyList<Uri> Variants(string master, Uri relativeTo)
    {
        ArgumentNullException.ThrowIfNull(master);
        var lines = Lines(master).ToList();
        var variants = new List<(Uri Uri, int Distance, int Order)>();
        for (var i = 0; i < lines.Count - 1; i++)
        {
            var tag = lines[i];
            if (!tag.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal) || !tag.Contains("avc1.", StringComparison.Ordinal)) continue;
            var size = Resolution().Match(tag);
            if (!size.Success || !int.TryParse(size.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var width)) continue;
            if (!Uri.TryCreate(relativeTo, lines[i + 1].Trim(), out var uri) || MediaUri(uri.AbsoluteUri) is null) continue;
            variants.Add((uri, Math.Abs(width - PreferredWidth), variants.Count));
        }
        // A stable sort: equally near variants keep the playlist's order.
        return [.. variants.OrderBy(v => v.Distance).ThenBy(v => v.Order).Take(MaxVariants).Select(v => v.Uri)];
    }

    /// <summary>
    /// The album pages among the search's results: HTTPS <c>music.apple.com</c> album pages whose album and artist match
    /// (ignoring case, accents, spaces and punctuation; an empty wanted album or artist matches any), and, for a song
    /// search, whose track matches too. Each page once, at most <see cref="MaxPages"/>.
    /// </summary>
    public static IReadOnlyList<Uri> MatchingPages(IEnumerable<MotionSearchResult> candidates, string? album, string artist, string? song)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var wantedAlbum = album is null ? "" : Normalized(album);
        var wantedArtist = Normalized(artist ?? "");
        var wantedSong = song is null ? null : Normalized(song);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pages = new List<Uri>();
        foreach (var candidate in candidates)
        {
            if (candidate.CollectionViewUrl is not { } raw || !Uri.TryCreate(raw, UriKind.Absolute, out var url)
                || url.Scheme != Uri.UriSchemeHttps || !url.Host.Equals("music.apple.com", StringComparison.OrdinalIgnoreCase)
                || !url.Segments.Any(s => s.TrimEnd('/') == "album"))
                continue;
            if (wantedAlbum.Length > 0 && Normalized(candidate.CollectionName ?? "") != wantedAlbum) continue;
            if (wantedArtist.Length > 0 && Normalized(candidate.ArtistName ?? "") != wantedArtist) continue;
            if (wantedSong is not null && Normalized(candidate.TrackName ?? "") != wantedSong) continue;
            if (!seen.Add(url.AbsolutePath)) continue;
            pages.Add(url);
            if (pages.Count == MaxPages) break;
        }
        return pages;
    }

    /// <summary>Lower case, accents removed, only letters and digits kept (the Mac's case- and diacritic-insensitive fold).</summary>
    public static string Normalized(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var rune in decomposed.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark) continue;
            if (Rune.IsLetter(rune) || Rune.IsNumber(rune)) builder.Append(Rune.ToLowerInvariant(rune).ToString());
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// The iTunes search for an album (entity <c>album</c>) or a song (entity <c>song</c>) by name and artist in one
    /// storefront, twenty results.
    /// </summary>
    public static Uri SearchUri(string country, string entity, string artist, string name) =>
        new("https://itunes.apple.com/search?term=" + Uri.EscapeDataString($"{artist} {name}")
            + "&entity=" + Uri.EscapeDataString(entity) + "&country=" + Uri.EscapeDataString(country) + "&limit=20");

    /// <summary>The results of an iTunes search reply, or null when the reply isn't one.</summary>
    public static IReadOnlyList<MotionSearchResult>? SearchResults(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                return null;
            var list = new List<MotionSearchResult>();
            foreach (var result in results.EnumerateArray())
            {
                if (result.ValueKind != JsonValueKind.Object) continue;
                list.Add(new MotionSearchResult(Text(result, "collectionViewUrl"), Text(result, "collectionName"),
                    Text(result, "artistName"), Text(result, "trackName")));
            }
            return list;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static IEnumerable<string> Lines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

    [GeneratedRegex("""<script\b[^>]*\bid=["']serialized-server-data["'][^>]*>""", RegexOptions.CultureInvariant)]
    private static partial Regex ServerDataScript();

    [GeneratedRegex("RESOLUTION=([0-9]+)", RegexOptions.CultureInvariant)]
    private static partial Regex Resolution();
}
