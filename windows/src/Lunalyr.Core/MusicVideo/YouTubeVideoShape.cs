// Ported from the Mac's MusicVideoView.swift (YouTubeVideoShape; Lunalyr's own code, Test Build 88).

using System.Collections.Concurrent;
using System.Text.Json;

namespace Lunalyr.Core.MusicVideo;

/// <summary>
/// A YouTube video's own shape, from YouTube's oEmbed endpoint (its width and height; only the video's ID is sent, and only
/// for a video already being shown whole). YouTube's player letterboxes or pillarboxes any other shape inside 16:9; knowing
/// it lets "Show the whole video" leave those bars out. Black bars baked into a video's own picture stay: guessing from dark
/// edges mistook dark videos for narrow ones. Remembered per video for the run.
/// </summary>
public static class YouTubeVideoShape
{
    /// <summary>The oEmbed endpoint's host and path.</summary>
    public const string Host = "www.youtube.com";

    public const string Path = "/oembed";

    private static readonly ConcurrentDictionary<string, double> Memory = new(StringComparer.Ordinal);

    /// <summary>The aspect already found for <paramref name="videoId"/> this run, if any.</summary>
    public static double? Cached(string videoId) => Memory.TryGetValue(videoId, out var aspect) ? aspect : null;

    /// <summary>The oEmbed address for a video: the watch page's address, which carries only its ID; null for a bad ID.</summary>
    public static Uri? Url(string videoId)
    {
        if (!YouTubeVideoId.IsValid(videoId)) return null;
        return new Uri($"https://{Host}{Path}?format=json&url=https%3A%2F%2Fwww.youtube.com%2Fwatch%3Fv%3D{videoId}");
    }

    /// <summary>Whether <paramref name="url"/> is exactly an oEmbed address <see cref="Url"/> builds (for the HTTP client's allow list).</summary>
    public static bool IsOEmbed(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps || !string.Equals(url.Host, Host, StringComparison.OrdinalIgnoreCase) || url.AbsolutePath != Path)
            return false;
        const string prefix = "?format=json&url=https%3A%2F%2Fwww.youtube.com%2Fwatch%3Fv%3D";
        var query = url.Query;
        return query.StartsWith(prefix, StringComparison.Ordinal) && YouTubeVideoId.IsValid(query[prefix.Length..]);
    }

    /// <summary>The aspect from an oEmbed reply, null when it is not believable (outside 0.4…3).</summary>
    public static double? AspectFromOEmbed(ReadOnlySpan<byte> json)
    {
        try
        {
            var reader = new Utf8JsonReader(json);
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("width", out var w) || w.ValueKind != JsonValueKind.Number
                || !root.TryGetProperty("height", out var h) || h.ValueKind != JsonValueKind.Number) return null;
            var width = w.GetDouble();
            var height = h.GetDouble();
            if (!(width > 0) || !(height > 0)) return null;
            var aspect = width / height;
            return aspect is >= 0.4 and <= 3 ? aspect : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The video's aspect, or null (then 16:9 is assumed). Asked once per video per run.</summary>
    public static async Task<double?> AspectAsync(string videoId, IMusicVideoHttp http, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (Cached(videoId) is { } known) return known;
        if (Url(videoId) is not { } url) return null;
        MusicVideoHttpReply reply;
        try
        {
            reply = await http.GetAsync(url, new Dictionary<string, string>(), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException)
        {
            return null;
        }
        if (reply.Status != 200 || AspectFromOEmbed(reply.Body) is not { } aspect) return null;
        Memory[videoId] = aspect;
        return aspect;
    }
}
