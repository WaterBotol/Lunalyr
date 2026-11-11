using System.Text;
using System.Text.Json;

namespace Lunalyr.Core.Lyrics.Sources;

/// <summary>Settings for <see cref="KuwoSource"/>.</summary>
public sealed record KuwoOptions
{
    public string SearchAddress { get; init; } = "https://search.kuwo.cn/r.s";
    public string LyricAddress { get; init; } = "https://www.kuwo.cn/openapi/v1/www/lyric/getlyric";
    public string Referer { get; init; } = "https://www.kuwo.cn/";
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(6);
    public int SongsTried { get; init; } = 2;
}

/// <summary>
/// Kuwo Music's search and line-lyric web endpoints (contract as documented by Yudaotor/lyrimuse's kuwo.go). Kuwo
/// sends a list of timed lines, which is written out as line-timed LRC.
/// </summary>
public sealed class KuwoSource(LyricHttpClient http, KuwoOptions? options = null, ILyricPayloadProbe? probe = null) : ILyricSource
{
    private const string Service = "Kuwo";
    private readonly KuwoOptions _options = options ?? new KuwoOptions();
    private readonly ILyricPayloadProbe _probe = probe ?? BasicLyricPayloadProbe.Instance;

    public LyricSourceId Id => LyricSourceId.Kuwo;

    public async Task<RawLyrics?> FetchAsync(LyricQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        List<Hit> hits;
        using (var search = await GetJsonAsync(_options.SearchAddress, new Dictionary<string, string>
               {
                   ["all"] = $"{query.Title} {query.Artist}", ["ft"] = "music", ["itemset"] = "web_2013", ["client"] = "kt",
                   ["pn"] = "0", ["rn"] = "8", ["rformat"] = "json", ["encoding"] = "utf8", ["pcjson"] = "1", ["vipver"] = "1",
               }, ct).ConfigureAwait(false))
        {
            hits = [.. search.RootElement.Items("abslist").Select(song => Hit.From(song, query)).OfType<Hit>().Where(h => h.Score > 0)];
        }
        var ordered = SourceMatching.Ordered(hits, LyricSourceId.Kuwo, query, h => (h.Title, h.Artist, h.Duration, h.Score));
        foreach (var hit in ordered.Take(_options.SongsTried))
        {
            ct.ThrowIfCancellationRequested();
            using var reply = await GetJsonAsync(_options.LyricAddress, new Dictionary<string, string> { ["musicId"] = hit.Id }, ct)
                .ConfigureAwait(false);
            if (reply.RootElement.Get("data") is not { ValueKind: JsonValueKind.Object } data
                || data.Get("lrclist") is not { ValueKind: JsonValueKind.Array } rows)
            {
                continue;
            }
            var lrc = LrcText.DecodeEntities(ToLrc(rows));
            if (!_probe.IsUsable(LyricFormat.Lrc, lrc))
            {
                continue;
            }
            return new RawLyrics(LyricSourceId.Kuwo, LyricFormat.Lrc, lrc, hit.Title, hit.Artist, hit.Duration,
                new Dictionary<string, string> { [RawLyricsExtras.SongId] = hit.Id });
        }
        return null;
    }

    /// <summary>Kuwo's line list (<c>time</c> in seconds, <c>lineLyric</c>) as LRC.</summary>
    public static string ToLrc(JsonElement rows)
    {
        var builder = new StringBuilder();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.Num("time") is not double time || time < 0 || row.Get("lineLyric") is not { ValueKind: JsonValueKind.String } text)
            {
                continue;
            }
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }
            builder.Append('[').Append(LrcText.Clock(time)).Append(']').Append(text.GetString());
        }
        return builder.ToString();
    }

    private Task<JsonDocument> GetJsonAsync(string address, Dictionary<string, string> parameters, CancellationToken ct) =>
        http.GetJsonAsync(Service, LyricHttpClient.BuildUri(address, parameters), _options.RequestTimeout, _options.Referer, ct);

    private sealed record Hit(string Id, string Title, string Artist, double? Duration, double Score)
    {
        public static Hit? From(JsonElement song, LyricQuery query)
        {
            if (song.Str("MUSICRID") is not { } rid || song.Str("SONGNAME") is not { } title || song.Str("ARTIST") is not { } artist)
            {
                return null;
            }
            var id = rid.Split('_', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (id is null)
            {
                return null;
            }
            var duration = song.Num("DURATION");
            return new Hit(id, title, artist, duration, SourceMatching.HitScore(query, title, artist, duration));
        }
    }
}
