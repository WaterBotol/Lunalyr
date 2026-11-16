using System.Globalization;
using System.Text.Json;

namespace Lunalyr.Core.Lyrics.Sources;

/// <summary>
/// LyricFind's licensed lyrics as YouTube Music serves them, through its InnerTube API without sign-in: a filtered song
/// search, the track's lyric page ID (<c>next</c>), and the page's timed lines (<c>browse</c>, which only the Android
/// client gets). Text only: nothing is played. YouTube Music relays Musixmatch too; only lines credited
/// "Source: LyricFind" are kept. Any refusal (a status other than 200) is an error, never worked around. Ported from
/// the Mac's <c>MoreLyricsProviders.lyricFind</c> (dc56f8a).
/// </summary>
public sealed class LyricFindSource(LyricHttpClient http, ILyricPayloadProbe? probe = null) : ILyricSource
{
    private const string Service = "YouTube Music";
    private const string Base = "https://music.youtube.com/youtubei/v1/";
    private static readonly Dictionary<string, string> Parameters = new() { ["prettyPrint"] = "false", ["alt"] = "json" };
    private static readonly Dictionary<string, string> Headers = new()
    {
        ["Content-Type"] = "application/json", ["Origin"] = "https://music.youtube.com",
        ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:88.0) Gecko/20100101 Firefox/88.0",
    };
    private readonly ILyricPayloadProbe _probe = probe ?? ParsedLyricPayloadProbe.Instance;

    public LyricSourceId Id => LyricSourceId.LyricFind;

    public async Task<RawLyrics?> FetchAsync(LyricQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var version = "1." + http.Time.GetUtcNow().ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".01.00";
        void Web(Utf8JsonWriter w) => Client(w, "WEB_REMIX", version);
        List<MoreLyrics.Hit> hits;
        using (var search = await PostAsync("search", w =>
               {
                   Web(w);
                   w.WriteString("query", query.Artist + " " + query.Title);
                   w.WriteString("params", "EgWKAQIIAWoMEA4QChADEAQQCRAF");
               }, ct).ConfigureAwait(false))
        {
            hits = [.. Songs(search.RootElement).Select(s => MoreLyrics.MakeHit(s.VideoId, s.Title, s.Artist, s.Duration, query)).OfType<MoreLyrics.Hit>()];
        }
        if (MoreLyrics.Rank(hits, Id, query).FirstOrDefault() is not { } best) return null;
        string? page;
        using (var next = await PostAsync("next", w =>
               {
                   Web(w);
                   w.WriteString("videoId", best.Id);
                   w.WriteBoolean("isAudioOnly", true);
               }, ct).ConfigureAwait(false))
        {
            page = LyricPage(next.RootElement);
        }
        if (page is null) return null;
        string? lrc;
        using (var lyrics = await PostAsync("browse", w =>
               {
                   Client(w, "ANDROID_MUSIC", "7.21.50");
                   w.WriteString("browseId", page);
               }, ct).ConfigureAwait(false))
        {
            lrc = Lrc(lyrics.RootElement);
        }
        return lrc is null ? null : MoreLyrics.Offer(_probe, Id, lrc, best);
    }

    private static void Client(Utf8JsonWriter writer, string name, string version)
    {
        writer.WriteStartObject("context");
        writer.WriteStartObject("client");
        writer.WriteString("clientName", name);
        writer.WriteString("clientVersion", version);
        writer.WriteEndObject();
        writer.WriteStartObject("user");
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private async Task<JsonDocument> PostAsync(string endpoint, Action<Utf8JsonWriter> write, CancellationToken ct)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        return await MoreLyrics.JsonAsync(http, Service, Base + endpoint, ct, Parameters, HttpMethod.Post, stream.ToArray(), Headers)
            .ConfigureAwait(false);
    }

    /// <summary>Every object in <paramref name="node"/>, itself first, in document order.</summary>
    private static IEnumerable<JsonElement> Objects(JsonElement node)
    {
        var stack = new Stack<JsonElement>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            IEnumerable<JsonElement> children;
            if (current.ValueKind == JsonValueKind.Object)
            {
                yield return current;
                children = current.EnumerateObject().Select(p => p.Value);
            }
            else if (current.ValueKind == JsonValueKind.Array)
            {
                children = current.EnumerateArray();
            }
            else
            {
                continue;
            }
            foreach (var child in children.Reverse()) stack.Push(child);
        }
    }

    /// <summary>The search's songs: video ID, title, artist and length, each video once.</summary>
    public static IReadOnlyList<(string VideoId, string Title, string Artist, double? Duration)> Songs(JsonElement search)
    {
        var songs = new List<(string, string, string, double?)>();
        foreach (var node in Objects(search))
        {
            if (node.Get("musicResponsiveListItemRenderer") is not { ValueKind: JsonValueKind.Object } item
                || item.Get("flexColumns") is not { ValueKind: JsonValueKind.Array } columns || columns.GetArrayLength() < 2)
            {
                continue;
            }
            string Column(int index) => string.Concat(columns[index].Get("musicResponsiveListItemFlexColumnRenderer").Get("text").Get("runs").Items()
                .Select(run => run.Get("text")).Where(t => t?.ValueKind == JsonValueKind.String).Select(t => t!.Value.GetString()));
            var parts = Column(1).Split(" • ");
            var videoId = "";
            if (item.Get("overlay") is { } overlay)
            {
                foreach (var inner in Objects(overlay))
                {
                    if (inner.Get("watchEndpoint").Get("videoId") is { ValueKind: JsonValueKind.String } id)
                    {
                        videoId = id.GetString()!;
                        break;
                    }
                }
            }
            var title = Column(0);
            if (title.Length == 0 || parts.Length < 2 || videoId.Length == 0 || songs.Any(s => s.Item1 == videoId)) continue;
            songs.Add((videoId, title, parts[0], MoreLyrics.ClockSeconds(parts[^1])));
        }
        return songs;
    }

    /// <summary>The track's lyric page ID, from the <c>next</c> answer.</summary>
    public static string? LyricPage(JsonElement next)
    {
        foreach (var node in Objects(next))
        {
            if (node.Get("browseEndpoint") is { ValueKind: JsonValueKind.Object } endpoint
                && endpoint.Get("browseId") is { ValueKind: JsonValueKind.String } id
                && endpoint.Get("browseEndpointContextSupportedConfigs").Get("browseEndpointContextMusicConfig").Str("pageType") == "MUSIC_PAGE_TYPE_TRACK_LYRICS")
            {
                return id.GetString();
            }
        }
        return null;
    }

    /// <summary>The page's timed lines as LRC, when they are credited to LyricFind; null otherwise.</summary>
    public static string? Lrc(JsonElement browse)
    {
        foreach (var node in Objects(browse))
        {
            if (node.Get("timedLyricsData") is not { ValueKind: JsonValueKind.Array } timed || timed.GetArrayLength() == 0
                || node.Get("sourceMessage") is not { ValueKind: JsonValueKind.String } source
                || !source.GetString()!.Contains("LyricFind", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var lines = new List<string>();
            foreach (var entry in timed.EnumerateArray())
            {
                if (entry.Get("lyricLine") is not { ValueKind: JsonValueKind.String } line
                    || MoreLyrics.Number(entry.Get("cueRange").Get("startTimeMilliseconds")) is not { } start)
                {
                    continue;
                }
                var text = line.GetString()!;
                lines.Add(MoreLyrics.LrcLine(start / 1000, text == "♪" ? "" : text));
            }
            if (lines.Count > 0) return string.Join("\n", lines);
        }
        return null;
    }
}
