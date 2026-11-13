// Fakes for the music video tests: no test reaches the network, a player, a file or the real settings. The Mac's tests
// fake youtube.com's results page; these fake the YouTube Data API's documented reply shapes with the same videos.

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

    /// <summary>The search's <c>q</c>, decoded.</summary>
    public static string? QueryOf(Uri url)
    {
        foreach (var pair in url.Query.TrimStart('?').Split('&'))
        {
            if (pair.StartsWith("q=", StringComparison.Ordinal)) return Uri.UnescapeDataString(pair[2..]);
        }
        return null;
    }
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

    public MusicVideoResolver Resolver(FakeLibrary? library = null, FakePlayability? playability = null, FakeHttp? http = null,
        Func<double>? clock = null, string? key = ApiKey, Func<double, CancellationToken, Task>? sleep = null) =>
        new(library ?? new FakeLibrary(), playability ?? new FakePlayability(),
            new YouTubeDataApi(http ?? new FakeHttp(), () => key, () => Start), new MusicVideoCache(Storage),
            new MusicVideoLinks(Storage), now: () => Start, uptime: clock ?? (() => Time),
            sleep: sleep ?? ((seconds, _) =>
            {
                Time += seconds;
                return Task.CompletedTask;
            }));

    public MusicVideoController Controller(MusicVideoResolver resolver, Func<(double, bool)>? songClock = null,
        Func<MusicVideoSong, IReadOnlyList<MusicVideoLyricLine>?>? lyrics = null, Func<double>? uptime = null) =>
        new(resolver, new MusicVideoOffsets(Storage), songClock, lyrics, uptime ?? (() => 0));

    /// <summary>The Mac's officialPage(): a lyric upload, then the official video.</summary>
    public static void ServeOfficialPage(FakeHttp http) =>
        http.ByPath[ApiFixtures.SearchPath] = new FakeHttp.Reply(200, ApiFixtures.Search(
            ("BBBBBBBBBBB", "The Weeknd - Blinding Lights (Lyrics)", "Lyrics Hub"),
            ("4NRXx6U8ABQ", "The Weeknd - Blinding Lights (Official Video)", "The Weeknd")));

    public static void ServeSearch(FakeHttp http, params (string Id, string Title, string Channel)[] videos) =>
        http.ByPath[ApiFixtures.SearchPath] = new FakeHttp.Reply(200, ApiFixtures.Search(videos));
}

internal static class Sources
{
    public static MusicVideoSource YouTube(string id) => new MusicVideoSource.YouTube(id);

    public static MusicVideoSource Local(string path) => new MusicVideoSource.Local(path);

    public static MusicVideoResolution Found(MusicVideoSource source) => MusicVideoResolution.Of(source);

    public static MusicVideoResolution Missing(MusicVideoMiss miss) => MusicVideoResolution.Of(miss);
}
