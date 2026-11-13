// Fakes for the music video tests: no test reaches the network, a player, a file or the real settings. As on the Mac, they
// fake YouTube's results page (PageFixtures, the Mac's Fixtures.searchPage, plus the pages recorded in Fixtures/YouTube);
// the Data API fallback's tests fake its documented reply shapes (ApiFixtures).

using System.Text;
using System.Text.Json;
using Lunalyr.Core.MusicVideo;

namespace Lunalyr.Core.Tests.MusicVideo;

/// <summary>Replies by URL path; records every request (and its headers).</summary>
internal sealed class FakeHttp : IMusicVideoHttp
{
    public sealed record Reply(int Status, byte[] Body, Dictionary<string, string>? Headers = null);

    private readonly Lock _gate = new();
    private readonly List<Uri> _urls = [];
    private readonly List<IReadOnlyDictionary<string, string>> _headers = [];

    public Dictionary<string, Reply> ByPath { get; } = new(StringComparer.Ordinal);
    public bool Failing { get; set; }

    public IReadOnlyList<Uri> Urls
    {
        get { lock (_gate) return [.. _urls]; }
    }

    public IReadOnlyList<IReadOnlyDictionary<string, string>> Headers
    {
        get { lock (_gate) return [.. _headers]; }
    }

    public IEnumerable<string> Paths => Urls.Select(u => u.AbsolutePath);

    public Task<MusicVideoHttpReply> GetAsync(Uri url, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        lock (_gate)
        {
            _urls.Add(url);
            _headers.Add(headers);
        }
        if (Failing) throw new HttpRequestException("offline");
        var reply = ByPath.GetValueOrDefault(url.AbsolutePath) ?? new Reply(404, []);
        return Task.FromResult(new MusicVideoHttpReply(reply.Status, reply.Body, reply.Headers));
    }

    /// <summary>The Data API search's <c>q</c>, decoded.</summary>
    public static string? QueryOf(Uri url) => Parameter(url, "q");

    /// <summary>The results page's <c>search_query</c>, decoded.</summary>
    public static string? SearchQueryOf(Uri url) => Parameter(url, "search_query");

    private static string? Parameter(Uri url, string name)
    {
        foreach (var pair in url.Query.TrimStart('?').Split('&'))
        {
            if (pair.StartsWith(name + "=", StringComparison.Ordinal)) return Uri.UnescapeDataString(pair[(name.Length + 1)..]);
        }
        return null;
    }
}

/// <summary>YouTube's results page: the Mac's fixture builder, and the pages recorded for these tests.</summary>
internal static class PageFixtures
{
    public const string Path = YouTubeSearchPage.Path;

    /// <summary>The Mac's Fixtures.searchPage: a results page with these videos, in order.</summary>
    public static byte[] SearchPage(params (string Id, string Title, string Channel)[] videos)
    {
        var renderers = string.Join(",", videos.Select(v =>
            "{\"videoRenderer\":{\"videoId\":\"" + v.Id + "\",\"title\":{\"runs\":[{\"text\":\"" + v.Title + "\"}]},\n"
            + " \"ownerText\":{\"runs\":[{\"text\":\"" + v.Channel + "\"}]}}}"));
        return Encoding.UTF8.GetBytes(
            "<html><script>var ytInitialData = {\"contents\":{\"twoColumnSearchResultsRenderer\":{\"primaryContents\":\n"
            + "{\"sectionListRenderer\":{\"contents\":[{\"itemSectionRenderer\":{\"contents\":[" + renderers + "]}}]}}}}};</script></html>");
    }

    /// <summary>A page recorded from youtube.com, or a hand-made wall page (Fixtures/YouTube/README.md).</summary>
    public static byte[] Recorded(string name) =>
        File.ReadAllBytes(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "YouTube", name));

    public static string RecordedText(string name) => Encoding.UTF8.GetString(Recorded(name));
}

internal static class ApiFixtures
{
    public const string SearchPath = "/youtube/v3/search";
    public const string VideosPath = "/youtube/v3/videos";

    /// <summary>A <c>search.list</c> reply with these videos, in order.</summary>
    public static byte[] Search(params (string Id, string Title, string Channel)[] videos)
    {
        var items = videos.Select(v => new Dictionary<string, object>
        {
            ["kind"] = "youtube#searchResult",
            ["id"] = new Dictionary<string, string> { ["kind"] = "youtube#video", ["videoId"] = v.Id },
            ["snippet"] = new Dictionary<string, object>
            {
                ["title"] = v.Title.Replace("'", "&#39;").Replace("&", "&amp;").Replace("&amp;#39;", "&#39;"),
                ["channelTitle"] = v.Channel,
                ["publishedAt"] = "2020-01-21T17:00:00Z",
                ["thumbnails"] = new Dictionary<string, object>
                {
                    ["medium"] = new Dictionary<string, object> { ["url"] = $"https://i.ytimg.com/vi/{v.Id}/mqdefault.jpg" },
                },
            },
        });
        return Json(new Dictionary<string, object> { ["kind"] = "youtube#searchListResponse", ["items"] = items.ToList() });
    }

    /// <summary>A <c>videos.list</c> reply: each video's ISO 8601 length, view count and embeddability.</summary>
    public static byte[] Videos(params (string Id, string Duration, long Views, bool Embeddable)[] videos) =>
        Json(new Dictionary<string, object>
        {
            ["items"] = videos.Select(v => new Dictionary<string, object>
            {
                ["id"] = v.Id,
                ["contentDetails"] = new Dictionary<string, object> { ["duration"] = v.Duration },
                ["statistics"] = new Dictionary<string, object> { ["viewCount"] = v.Views.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                ["status"] = new Dictionary<string, object> { ["embeddable"] = v.Embeddable },
            }).ToList(),
        });

    public static byte[] Error(int code, string reason) => Encoding.UTF8.GetBytes(
        "{\"error\":{\"code\":" + code + ",\"message\":\"m\",\"errors\":[{\"message\":\"m\",\"domain\":\"youtube\",\"reason\":\"" + reason + "\"}]}}");

    private static byte[] Json(object value) => Encoding.UTF8.GetBytes(Write(value));

    // A tiny writer for the fixtures (reflection-free, as the test project is AOT-checked too).
    private static string Write(object value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteValue(writer, value);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case System.Collections.IDictionary map:
                writer.WriteStartObject();
                foreach (System.Collections.DictionaryEntry entry in map)
                {
                    writer.WritePropertyName((string)entry.Key);
                    WriteValue(writer, entry.Value);
                }
                writer.WriteEndObject();
                break;
            case System.Collections.IEnumerable list:
                writer.WriteStartArray();
                foreach (var item in list) WriteValue(writer, item);
                writer.WriteEndArray();
                break;
        }
    }
}

internal sealed class FakeLibrary(string? video = null) : IMusicVideoLibrary
{
    private readonly List<string> _asked = [];

    public string? Video { get; set; } = video;

    public IReadOnlyList<string> Asked
    {
        get { lock (_asked) return [.. _asked]; }
    }

    public Task<string?> LocalVideoAsync(MusicVideoSong song, CancellationToken ct)
    {
        lock (_asked) _asked.Add(song.Title);
        return Task.FromResult(Video);
    }
}

internal sealed class FakePlayability(MusicVideoPlayability result = MusicVideoPlayability.Playable) : IMusicVideoPlayabilityCheck
{
    public Task<MusicVideoPlayability> CheckAsync(string path, CancellationToken ct) => Task.FromResult(result);
}

/// <summary>Shared state for a test: one storage (the Mac's UserDefaults suite), a fixed date and a clock moved by sleeps.</summary>
internal sealed class MusicVideoWorld
{
    public static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    public const string ApiKey = "test-key-not-real";

    public MemoryMusicVideoStorage Storage { get; } = new();
    public double Time { get; set; }

    public MusicVideoSong Song { get; } = new("Blinding Lights", "The Weeknd", "After Hours", 200);

    public MusicVideoTrack Track { get; } = new("Blinding Lights", "The Weeknd", 200, "After Hours");

    public const string LocalPath = @"C:\Music\Music Videos\Blinding Lights.mp4";

    /// <summary>The parse seam: the real reader when null, or a test's.</summary>
    public Func<byte[], Task<IReadOnlyList<YouTubeVideoResult>>>? Parse { get; set; }

    /// <summary>
    /// A resolver over <paramref name="http"/>: YouTube's results page with no key, as on the Mac, unless a test passes a
    /// <paramref name="key"/> (the Data API fallback) or <see cref="MusicVideoSearchMode.ApiKey"/>.
    /// </summary>
    public MusicVideoResolver Resolver(FakeLibrary? library = null, FakePlayability? playability = null, FakeHttp? http = null,
        Func<double>? clock = null, string? key = null, Func<double, CancellationToken, Task>? sleep = null,
        MusicVideoSearchMode mode = MusicVideoSearchMode.Site, bool page = true)
    {
        var client = http ?? new FakeHttp();
        return new(library ?? new FakeLibrary(), playability ?? new FakePlayability(),
            new YouTubeDataApi(client, () => key, () => Start), new MusicVideoCache(Storage),
            new MusicVideoLinks(Storage), now: () => Start, uptime: clock ?? (() => Time),
            sleep: sleep ?? ((seconds, _) =>
            {
                Time += seconds;
                return Task.CompletedTask;
            }), page: page ? client : null, searchMode: () => mode, parse: Parse);
    }

    /// <summary>A resolver that searches the Data API only ("Use my API key"), with the test key.</summary>
    public MusicVideoResolver ApiResolver(FakeHttp http, Func<double>? clock = null) =>
        Resolver(http: http, clock: clock, key: ApiKey, mode: MusicVideoSearchMode.ApiKey);

    public MusicVideoController Controller(MusicVideoResolver resolver, Func<(double, bool)>? songClock = null,
        Func<MusicVideoSong, IReadOnlyList<MusicVideoLyricLine>?>? lyrics = null, Func<double>? uptime = null) =>
        new(resolver, new MusicVideoOffsets(Storage), songClock, lyrics, uptime ?? (() => 0));

    /// <summary>The Mac's officialPage(): a lyric upload, then the official video.</summary>
    public static readonly (string Id, string Title, string Channel)[] OfficialVideos =
    [
        ("BBBBBBBBBBB", "The Weeknd - Blinding Lights (Lyrics)", "Lyrics Hub"),
        ("4NRXx6U8ABQ", "The Weeknd - Blinding Lights (Official Video)", "The Weeknd"),
    ];

    /// <summary>The Mac's officialPage(), on YouTube's results page.</summary>
    public static void ServeOfficialPage(FakeHttp http) => ServeSearch(http, OfficialVideos);

    /// <summary>A results page with these videos (the Mac's replies["www.youtube.com"]).</summary>
    public static void ServeSearch(FakeHttp http, params (string Id, string Title, string Channel)[] videos) =>
        http.ByPath[PageFixtures.Path] = new FakeHttp.Reply(200, PageFixtures.SearchPage(videos));

    /// <summary>The same videos from the Data API (its search only; the details request gets a 404 and is done without).</summary>
    public static void ServeApiSearch(FakeHttp http, params (string Id, string Title, string Channel)[] videos) =>
        http.ByPath[ApiFixtures.SearchPath] = new FakeHttp.Reply(200, ApiFixtures.Search(videos));

    public static void ServeApiOfficial(FakeHttp http) => ServeApiSearch(http, OfficialVideos);
}

internal static class Sources
{
    public static MusicVideoSource YouTube(string id) => new MusicVideoSource.YouTube(id);

    public static MusicVideoSource Local(string path) => new MusicVideoSource.Local(path);

    public static MusicVideoResolution Found(MusicVideoSource source) => MusicVideoResolution.Of(source);

    public static MusicVideoResolution Missing(MusicVideoMiss miss) => MusicVideoResolution.Of(miss);
}
