// Windows' own: YouTube search through the official YouTube Data API v3 (search.list, then videos.list for lengths, view
// counts and embeddability), with an API key the owner supplies. The fallback when YouTube's own results page (the Mac's
// way, YouTubeSearchPage.cs) isn't available, and the owner's choice under "Use my API key". Nothing here runs without a
// key, and nothing at all under --demo or --no-network (the app never builds the client then). Not verified live: it
// runs only against fixtures shaped like the API's documented replies.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Lunalyr.Core.MusicVideo;

/// <summary>One HTTP reply, as the seam hands it back.</summary>
public sealed record MusicVideoHttpReply(int Status, byte[] Body, IReadOnlyDictionary<string, string>? Headers = null)
{
    public string? Header(string name)
    {
        if (Headers is null) return null;
        foreach (var (key, value) in Headers)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return value;
        }
        return null;
    }
}

/// <summary>HTTP GET. The app's sends with no cookies and nothing cached; tests pass a fake, so no test reaches the network.</summary>
public interface IMusicVideoHttp
{
    Task<MusicVideoHttpReply> GetAsync(Uri url, IReadOnlyDictionary<string, string> headers, CancellationToken ct);
}

/// <summary>Why a Data API request gave no results.</summary>
public enum YouTubeApiFailure
{
    /// <summary>Offline, no answer, or a reply that couldn't be read: passing.</summary>
    Failed,
    /// <summary>429 or 503: wait (<see cref="YouTubeApiException.RetryAfter"/>).</summary>
    RateLimited,
    /// <summary>The key's daily quota is used up (403 quotaExceeded / dailyLimitExceeded).</summary>
    QuotaExceeded,
    /// <summary>The key was refused: not valid, expired, restricted, or the API not enabled for its project.</summary>
    KeyRejected,
}

public sealed class YouTubeApiException(YouTubeApiFailure failure, double? retryAfter = null, string? reason = null)
    : Exception($"YouTube Data API: {failure}{(reason is null ? "" : $" ({reason})")}")
{
    public YouTubeApiFailure Failure { get; } = failure;
    public double? RetryAfter { get; } = retryAfter;
}

/// <summary>
/// The YouTube Data API v3 client: <c>search.list</c> (type=video, embeddable only, up to 12 results, the Mac's prefix)
/// then <c>videos.list</c> for each result's length, views and embeddability. The key goes in the <c>X-Goog-Api-Key</c>
/// header, never in the address, so it can't reach a log. Each search costs 101 quota units (100 + 1) of the key's daily
/// allowance (10,000 by default, as Google documents it at the time of writing: to confirm for the owner's project).
/// </summary>
public sealed class YouTubeDataApi(IMusicVideoHttp http, Func<string?> apiKey, Func<DateTimeOffset>? now = null)
{
    public const string Host = "www.googleapis.com";
    public const string KeyHeader = "X-Goog-Api-Key";
    public const int MaxResults = 12;

    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);

    /// <summary>Whether a key is set at all.</summary>
    public bool HasKey => !string.IsNullOrWhiteSpace(apiKey());

    /// <summary>The <c>search.list</c> address for <paramref name="query"/> (no key in it).</summary>
    public static Uri SearchUrl(string query) => new(
        $"https://{Host}/youtube/v3/search?part=snippet&type=video&videoEmbeddable=true&maxResults={MaxResults}&q={Uri.EscapeDataString(query)}");

    /// <summary>The <c>videos.list</c> address for <paramref name="ids"/> (no key in it).</summary>
    public static Uri VideosUrl(IEnumerable<string> ids) => new(
        $"https://{Host}/youtube/v3/videos?part=contentDetails,statistics,status&id={Uri.EscapeDataString(string.Join(',', ids))}");

    /// <summary>
    /// The search's video results, in YouTube's order, each with its length when <c>videos.list</c> gave it. Videos the
    /// API says can't be embedded are left out. <paramref name="beforeDetails"/> runs between the two requests (the
    /// resolver's rate limit checks a back-off there). Throws <see cref="YouTubeApiException"/>.
    /// </summary>
    public async Task<IReadOnlyList<YouTubeVideoResult>> SearchAsync(string query, CancellationToken ct, Action? beforeDetails = null)
    {
        var reply = await SendAsync(SearchUrl(query), ct).ConfigureAwait(false);
        var found = ParseSearch(reply.Body);
        if (found.Count == 0) return found;
        beforeDetails?.Invoke();
        MusicVideoHttpReply details;
        try
        {
            details = await SendAsync(VideosUrl(found.Select(r => r.VideoId)), ct).ConfigureAwait(false);
        }
        catch (YouTubeApiException ex) when (ex.Failure == YouTubeApiFailure.Failed)
        {
            // Lengths only help the ranking: the results stand without them.
            return found;
        }
        return Merge(found, ParseVideos(details.Body), _now());
    }

    private async Task<MusicVideoHttpReply> SendAsync(Uri url, CancellationToken ct)
    {
        var key = apiKey()?.Trim();
        if (string.IsNullOrEmpty(key)) throw new YouTubeApiException(YouTubeApiFailure.KeyRejected, reason: "no key");
        MusicVideoHttpReply reply;
        try
        {
            reply = await http.GetAsync(url, new Dictionary<string, string> { [KeyHeader] = key }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not YouTubeApiException)
        {
            throw new YouTubeApiException(YouTubeApiFailure.Failed, reason: ex.GetType().Name);
        }
        if (reply.Status == 200) return reply;
        throw Classify(reply);
    }

    /// <summary>What a reply other than 200 means.</summary>
    public static YouTubeApiException Classify(MusicVideoHttpReply reply)
    {
        if (reply.Status is 429 or 503)
        {
            var retry = double.TryParse(reply.Header("Retry-After"), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : (double?)null;
            return new YouTubeApiException(YouTubeApiFailure.RateLimited, retry);
        }
        var reason = ErrorReason(reply.Body);
        if (reason is "quotaExceeded" or "dailyLimitExceeded" or "rateLimitExceeded" or "userRateLimitExceeded")
            return reason is "quotaExceeded" or "dailyLimitExceeded"
                ? new YouTubeApiException(YouTubeApiFailure.QuotaExceeded, reason: reason)
                : new YouTubeApiException(YouTubeApiFailure.RateLimited, reason: reason);
        if (reply.Status is 400 or 401 or 403
            && reason is "keyInvalid" or "keyExpired" or "accessNotConfigured" or "forbidden" or "ipRefererBlocked"
                or "API_KEY_INVALID" or "API_KEY_SERVICE_BLOCKED" or "SERVICE_DISABLED" or "badRequest" or null)
            return new YouTubeApiException(YouTubeApiFailure.KeyRejected, reason: reason ?? reply.Status.ToString(CultureInfo.InvariantCulture));
        return new YouTubeApiException(YouTubeApiFailure.Failed, reason: reason ?? reply.Status.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The first <c>error.errors[].reason</c> (or <c>error.details[].reason</c>) of an error reply.</summary>
    public static string? ErrorReason(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object) return null;
            // The detailed reason (API_KEY_INVALID…) says more than the general one (badRequest).
            foreach (var list in new[] { "details", "errors" })
            {
                if (!error.TryGetProperty(list, out var items) || items.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in items.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("reason", out var reason)
                        && reason.ValueKind == JsonValueKind.String)
                        return reason.GetString();
                }
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The video results of a <c>search.list</c> reply, in order (titles HTML-decoded, as the API escapes them).</summary>
    public static IReadOnlyList<YouTubeVideoResult> ParseSearch(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                throw new YouTubeApiException(YouTubeApiFailure.Failed, reason: "no items");
            var found = new List<YouTubeVideoResult>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Object
                    || String(id, "kind") is { } kind && kind != "youtube#video" || String(id, "videoId") is not { } videoId
                    || !YouTubeVideoId.IsValid(videoId) || !seen.Add(videoId)) continue;
                item.TryGetProperty("snippet", out var snippet);
                var title = snippet.ValueKind == JsonValueKind.Object ? Decode(String(snippet, "title")) : "";
                var channel = snippet.ValueKind == JsonValueKind.Object ? Decode(String(snippet, "channelTitle")) : "";
                found.Add(new YouTubeVideoResult(videoId, title, channel,
                    Uploaded: snippet.ValueKind == JsonValueKind.Object ? String(snippet, "publishedAt") : null));
            }
            return found;
        }
        catch (JsonException)
        {
            throw new YouTubeApiException(YouTubeApiFailure.Failed, reason: "unreadable reply");
        }
    }

    /// <summary>What <c>videos.list</c> says about each video: its length, views and whether it may be embedded.</summary>
    public sealed record VideoDetails(double? Duration, long? Views, bool Embeddable);

    public static IReadOnlyDictionary<string, VideoDetails> ParseVideos(byte[] body)
    {
        var details = new Dictionary<string, VideoDetails>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return details;
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || String(item, "id") is not { } id) continue;
                double? duration = item.TryGetProperty("contentDetails", out var content) && content.ValueKind == JsonValueKind.Object
                    ? Iso8601Seconds(String(content, "duration")) : null;
                long? views = item.TryGetProperty("statistics", out var stats) && stats.ValueKind == JsonValueKind.Object
                    && long.TryParse(String(stats, "viewCount"), NumberStyles.None, CultureInfo.InvariantCulture, out var count)
                    ? count : null;
                var embeddable = !(item.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Object
                    && status.TryGetProperty("embeddable", out var flag) && flag.ValueKind == JsonValueKind.False);
                details[id] = new VideoDetails(duration, views, embeddable);
            }
        }
        catch (JsonException)
        {
            // Lengths only help the ranking.
        }
        return details;
    }

    /// <summary>The search's results with their details: lengths, YouTube-style views and age; non-embeddable ones left out.</summary>
    public static IReadOnlyList<YouTubeVideoResult> Merge(IReadOnlyList<YouTubeVideoResult> found,
        IReadOnlyDictionary<string, VideoDetails> details, DateTimeOffset now)
    {
        var merged = new List<YouTubeVideoResult>(found.Count);
        foreach (var result in found)
        {
            var uploaded = DateTimeOffset.TryParse(result.Uploaded, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                ? Age(at, now) : null;
            if (!details.TryGetValue(result.VideoId, out var info))
            {
                merged.Add(result with { Uploaded = uploaded });
                continue;
            }
            if (!info.Embeddable) continue;
            merged.Add(result with { Duration = info.Duration, Views = ViewsText(info.Views), Uploaded = uploaded });
        }
        return merged;
    }

    /// <summary>An ISO 8601 duration ("PT4M22S", "PT1H2M3S", "P1DT2H") in seconds; null for anything else ("P0D" is a live stream).</summary>
    public static double? Iso8601Seconds(string? text)
    {
        if (string.IsNullOrEmpty(text) || text[0] != 'P') return null;
        double total = 0;
        var inTime = false;
        var number = new StringBuilder();
        var any = false;
        for (var i = 1; i < text.Length; i++)
        {
            var c = text[i];
            if (c == 'T')
            {
                if (inTime || number.Length > 0) return null;
                inTime = true;
                continue;
            }
            if (char.IsAsciiDigit(c) || c == '.')
            {
                number.Append(c);
                continue;
            }
            if (number.Length == 0 || !double.TryParse(number.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return null;
            number.Clear();
            var unit = (c, inTime) switch
            {
                ('W', false) => 604800.0,
                ('D', false) => 86400.0,
                ('H', true) => 3600.0,
                ('M', true) => 60.0,
                ('S', true) => 1.0,
                _ => double.NaN,
            };
            if (double.IsNaN(unit)) return null;
            total += value * unit;
            any = true;
        }
        return any && number.Length == 0 && total > 0 ? total : null;
    }

    /// <summary>A view count as YouTube shows it: "843 views", "1.2K views", "12M views", "1.1B views".</summary>
    public static string? ViewsText(long? views)
    {
        if (views is not { } count || count < 0) return null;
        if (count == 1) return "1 view";
        if (count < 1000) return $"{count} views";
        (double value, string suffix) = count switch
        {
            < 1_000_000 => (count / 1e3, "K"),
            < 1_000_000_000 => (count / 1e6, "M"),
            _ => (count / 1e9, "B"),
        };
        // One decimal below ten, as YouTube shows it, truncated rather than rounded up.
        var shown = value < 10 ? Math.Floor(value * 10) / 10 : Math.Floor(value);
        return shown.ToString(value < 10 ? "0.#" : "0", CultureInfo.InvariantCulture) + suffix + " views";
    }

    /// <summary>How long ago, as YouTube shows it: "3 years ago", "1 month ago", "5 days ago".</summary>
    public static string? Age(DateTimeOffset uploaded, DateTimeOffset now)
    {
        var seconds = (now - uploaded).TotalSeconds;
        if (!double.IsFinite(seconds) || seconds < 0) return null;
        (double amount, string unit) = seconds switch
        {
            < 60 => (seconds, "second"),
            < 3600 => (seconds / 60, "minute"),
            < 86400 => (seconds / 3600, "hour"),
            < 7 * 86400 => (seconds / 86400, "day"),
            < 30 * 86400 => (seconds / (7 * 86400), "week"),
            < 365 * 86400 => (seconds / (30 * 86400), "month"),
            _ => (seconds / (365 * 86400), "year"),
        };
        var whole = Math.Max(1, (int)Math.Floor(amount));
        return $"{whole} {unit}{(whole == 1 ? "" : "s")} ago";
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Decode(string? text) => string.IsNullOrEmpty(text) ? "" : WebUtility.HtmlDecode(text);
}
