// Ported from the Mac's MusicVideoCore.swift (YouTubeSearchPage: requestURL, results(fromHTML:), resultsOffMainActor,
// hasVerifiedArtistBadge; Lunalyr's own code), MusicVideoSearch.swift (requestURL(query:)) and MusicVideoServices.swift
// (URLSessionMusicVideoHTTP's WebKit User-Agent). Reads YouTube's own search results page, no API key: the videos in its
// ytInitialData. The ranking is YouTubeSearch.OfficialVideos.
//
// Windows' own: Inspect, the guardrail. A page that isn't results (a consent page, a "confirm you're not a bot" page, a
// sign-in wall, a captcha, a redirect, or no ytInitialData at all) is "unavailable" and is never worked around: no retry,
// no cookies, no other User-Agent, nothing solved. The resolver then falls back to the owner's API key, or says so.

using System.Text;
using System.Text.Json;

namespace Lunalyr.Core.MusicVideo;

/// <summary>Why YouTube's results page couldn't be used (Windows' guardrail). Each is treated the same: unavailable.</summary>
public enum YouTubeWall
{
    /// <summary>"Before you continue to YouTube": the cookie consent page (or a redirect to consent.youtube.com).</summary>
    Consent,
    /// <summary>"Confirm you're not a bot", "unusual traffic", or Google's /sorry/ page.</summary>
    BotCheck,
    /// <summary>A sign-in wall ("Sign in to confirm…", or a redirect to accounts.google.com).</summary>
    SignIn,
    /// <summary>A captcha.</summary>
    Captcha,
    /// <summary>A redirect anywhere else (redirects are never followed).</summary>
    Redirect,
    /// <summary>A page with no ytInitialData and none of the above: YouTube changed the page, or something else answered.</summary>
    NoData,
}

/// <summary>What one fetch of the results page came to.</summary>
public abstract record YouTubePageReply
{
    private YouTubePageReply()
    {
    }

    /// <summary>A results page: its videos in page order (none when the page had ytInitialData but no videos).</summary>
    public sealed record Results(IReadOnlyList<YouTubeVideoResult> Videos) : YouTubePageReply;

    /// <summary>Not a results page: never worked around.</summary>
    public sealed record Unavailable(YouTubeWall Wall) : YouTubePageReply;

    /// <summary>Some other status (a 404, a 500): a passing failure.</summary>
    public sealed record Failed(int Status) : YouTubePageReply;
}

public static class YouTubeSearchPage
{
    public const string Host = "www.youtube.com";
    public const string Path = "/results";

    /// <summary>
    /// The Mac's User-Agent for youtube.com (URLSessionMusicVideoHTTP.webKitAgent): YouTube serves its results data only to
    /// a browser. One fixed value, never rotated; every other host is told it is Lunalyr.
    /// </summary>
    public const string WebKitAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15";

    /// <summary>The Mac's Accept-Language for every music video request.</summary>
    public const string AcceptLanguage = "en";

    /// <summary>The automatic lookup's address: "&lt;artist&gt; &lt;title&gt; official music video".</summary>
    public static Uri RequestUrl(string title, string artist) => Build(YouTubeSearch.Query(title, artist));

    /// <summary>
    /// Search YouTube…'s address for exactly what the user typed, without the automatic lookup's "official music video", so
    /// lyric videos, live takes and covers show up too. Null for an empty query.
    /// </summary>
    public static Uri? RequestUrl(string query)
    {
        var trimmed = query.Trim();
        return trimmed.Length == 0 ? null : Build(trimmed);
    }

    private static Uri Build(string query) => new($"https://{Host}{Path}?search_query={Uri.EscapeDataString(query)}");

    /// <summary>Whether <paramref name="url"/> is a results-page address (the only youtube.com address ever asked).</summary>
    public static bool IsResultsPage(Uri url) =>
        url.Scheme == Uri.UriSchemeHttps && string.Equals(url.Host, Host, StringComparison.OrdinalIgnoreCase) && url.AbsolutePath == Path;

    /// <summary>
    /// <see cref="Results(string)"/> on a thread-pool thread, never the caller's: the page is about a megabyte (the Mac's
    /// resultsOffMainActor). <paramref name="parser"/> is a seam for tests.
    /// </summary>
    public static Task<IReadOnlyList<YouTubeVideoResult>> ResultsOffThreadAsync(byte[] data,
        Func<string, IReadOnlyList<YouTubeVideoResult>>? parser = null)
    {
        var parse = parser ?? Results;
        return Task.Run(() => parse(Encoding.UTF8.GetString(data)));
    }

    /// <summary>Video results in the page's ytInitialData, in page order; none when it has none (or can't be read).</summary>
    public static IReadOnlyList<YouTubeVideoResult> Results(string html)
    {
        if (InitialData(html) is not { } json) return [];
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 1024 });
            var found = new List<YouTubeVideoResult>();
            Collect(document.RootElement, found);
            return found;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Whether the page carries ytInitialData at all (the Mac's check before calling an empty page "no videos").</summary>
    public static bool HasInitialData(ReadOnlySpan<byte> body) => body.IndexOf("ytInitialData"u8) >= 0;

    /// <summary>
    /// What a reply from the results page is (Windows' guardrail). The page is parsed first: a page with videos is results,
    /// whatever else it mentions (a real results page mentions sign-in, consent and captcha in its scripts). Only a page
    /// without videos is looked at for a wall. 429 and 503 are the caller's (the Mac's back-off), so not classified here.
    /// </summary>
    /// <param name="videos">The page's videos, already parsed (off the caller's thread).</param>
    public static YouTubePageReply Inspect(MusicVideoHttpReply reply, IReadOnlyList<YouTubeVideoResult> videos)
    {
        if (reply.Status == 200 && videos.Count > 0) return new YouTubePageReply.Results(videos);
        if (reply.Status is >= 300 and < 400)
        {
            var location = reply.Header("Location") ?? "";
            return new YouTubePageReply.Unavailable(Wall(location) ?? YouTubeWall.Redirect);
        }
        var text = Encoding.UTF8.GetString(reply.Body);
        if (reply.Status == 200)
        {
            if (HasInitialData(reply.Body)) return new YouTubePageReply.Results([]);
            return new YouTubePageReply.Unavailable(Wall(text) ?? YouTubeWall.NoData);
        }
        // A 403 is how Google turns a client away; anything else with a wall in it is one too.
        if (Wall(text) is { } wall) return new YouTubePageReply.Unavailable(wall);
        return reply.Status == 403 ? new YouTubePageReply.Unavailable(YouTubeWall.BotCheck) : new YouTubePageReply.Failed(reply.Status);
    }

    /// <summary>The wall a page (or a redirect's address) shows, or null for none recognised.</summary>
    public static YouTubeWall? Wall(string text)
    {
        // Scripts often escape the slashes in addresses.
        var page = text.Replace("\\/", "/", StringComparison.Ordinal);
        bool Has(string marker) => page.Contains(marker, StringComparison.OrdinalIgnoreCase);
        if (Has("consent.youtube.com") || Has("consent.google.com") || Has("Before you continue")) return YouTubeWall.Consent;
        if (Has("captcha")) return YouTubeWall.Captcha;
        if (Has("not a bot") || Has("not a robot") || Has("unusual traffic") || Has("google.com/sorry") || Has("/sorry/index"))
            return YouTubeWall.BotCheck;
        if (Has("accounts.google.com") || Has("ServiceLogin") || Has("Sign in to confirm")) return YouTubeWall.SignIn;
        return null;
    }

    private static string? InitialData(string html)
    {
        foreach (var marker in (ReadOnlySpan<string>)["var ytInitialData = ", "window[\"ytInitialData\"] = ", "ytInitialData = "])
        {
            var start = html.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) continue;
            var rest = start + marker.Length;
            var end = html.IndexOf(";</script>", rest, StringComparison.Ordinal);
            if (end < 0) continue;
            return html[rest..end];
        }
        return null;
    }

    private static void Collect(JsonElement node, List<YouTubeVideoResult> found)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                if (node.TryGetProperty("videoRenderer", out var renderer) && renderer.ValueKind == JsonValueKind.Object
                    && renderer.TryGetProperty("videoId", out var idNode) && idNode.ValueKind == JsonValueKind.String
                    && idNode.GetString() is { } id && YouTubeVideoId.IsValid(id))
                {
                    var title = Text(renderer, "title") ?? "";
                    var channel = Text(renderer, "ownerText") ?? Text(renderer, "longBylineText") ?? "";
                    found.Add(new YouTubeVideoResult(id, title, channel,
                        VerifiedArtist: renderer.TryGetProperty("ownerBadges", out var badges) && HasVerifiedArtistBadge(badges),
                        Duration: Text(renderer, "lengthText") is { } length ? YouTubeSearch.LengthSeconds(length) : null,
                        Views: Shown(Text(renderer, "shortViewCountText") ?? Text(renderer, "viewCountText")),
                        Uploaded: Shown(Text(renderer, "publishedTimeText"))));
                }
                foreach (var property in node.EnumerateObject())
                {
                    if (property.Name != "videoRenderer") Collect(property.Value, found);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray()) Collect(item, found);
                break;
        }
    }

    /// <summary><c>ownerBadges: [{metadataBadgeRenderer: {style: "BADGE_STYLE_TYPE_VERIFIED_ARTIST"}}]</c>.</summary>
    public static bool HasVerifiedArtistBadge(JsonElement badges)
    {
        if (badges.ValueKind != JsonValueKind.Array) return false;
        foreach (var badge in badges.EnumerateArray())
        {
            if (badge.ValueKind == JsonValueKind.Object && badge.TryGetProperty("metadataBadgeRenderer", out var renderer)
                && renderer.ValueKind == JsonValueKind.Object && renderer.TryGetProperty("style", out var style)
                && style.ValueKind == JsonValueKind.String && style.GetString() == "BADGE_STYLE_TYPE_VERIFIED_ARTIST")
                return true;
        }
        return false;
    }

    /// <summary>Text as shown, trimmed; null when there is none.</summary>
    private static string? Shown(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>A text node's <c>simpleText</c>, else its <c>runs</c> joined; null for neither.</summary>
    private static string? Text(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.Object) return null;
        if (node.TryGetProperty("simpleText", out var simple) && simple.ValueKind == JsonValueKind.String) return simple.GetString();
        if (node.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
        {
            var joined = new StringBuilder();
            foreach (var run in runs.EnumerateArray())
            {
                if (run.ValueKind == JsonValueKind.Object && run.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    joined.Append(text.GetString());
            }
            return joined.Length == 0 ? null : joined.ToString();
        }
        return null;
    }
}
