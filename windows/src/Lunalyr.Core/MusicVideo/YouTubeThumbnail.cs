// The YouTube video's own still (the Mac's YouTubeThumbnail, MusicVideoView.swift; Lunalyr's own lines): what the
// Music video wallpaper shows instead of the player under Reduce Motion (Windows' Animation effects off), in the
// settings preview, and wherever no player is on screen. Only the public still images on YouTube's image host
// (i.ytimg.com) are fetched, the ones the YouTube Data API itself lists as a video's thumbnails (high and maxres), as the
// Search YouTube window's rows load theirs; no player, page or protected resource is asked for.

namespace Lunalyr.Core.MusicVideo;

/// <summary>Where a YouTube video's still is, and fetching it.</summary>
public static class YouTubeThumbnail
{
    /// <summary>The image host, the only one asked.</summary>
    public const string Host = "i.ytimg.com";

    /// <summary>How long the still takes to fade in once loaded (seconds).</summary>
    public const double FadeIn = 0.4;

    /// <summary>The usual still (480 × 360), or null for an id that isn't a video id.</summary>
    public static Uri? Url(string videoId) =>
        YouTubeVideoId.IsValid(videoId) ? new Uri($"https://{Host}/vi/{videoId}/hqdefault.jpg") : null;

    /// <summary>The stills to try, best first. Full screen tries the 1280 px one first: not every video has it.</summary>
    public static IReadOnlyList<Uri> Urls(string videoId, bool large)
    {
        if (Url(videoId) is not { } usual) return [];
        return large ? [new Uri($"https://{Host}/vi/{videoId}/maxresdefault.jpg"), usual] : [usual];
    }

    /// <summary>The still's bytes (a JPEG), or null when none could be fetched. Never throws for a network failure.</summary>
    public static async Task<byte[]?> LoadAsync(string videoId, IMusicVideoHttp http, bool large = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        foreach (var url in Urls(videoId, large))
        {
            if (ct.IsCancellationRequested) return null;
            try
            {
                var reply = await http.GetAsync(url, NoHeaders, ct).ConfigureAwait(false);
                if (reply.Status == 200 && reply.Body.Length > 0) return reply.Body;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
            {
                if (ct.IsCancellationRequested) return null;
            }
        }
        return null;
    }

    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();
}
