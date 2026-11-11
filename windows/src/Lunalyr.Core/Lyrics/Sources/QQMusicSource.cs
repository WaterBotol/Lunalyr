using System.Text;
using System.Text.Json;

namespace Lunalyr.Core.Lyrics.Sources;

/// <summary>Settings for <see cref="QQMusicSource"/>.</summary>
public sealed record QQMusicOptions
{
    public string SearchAddress { get; init; } = "https://c.y.qq.com/soso/fcgi-bin/client_search_cp";
    public string LyricAddress { get; init; } = "https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg";
    public string Referer { get; init; } = "https://y.qq.com/";
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>Songs whose lyrics are fetched, best first, until one has usable lyrics.</summary>
    public int SongsTried { get; init; } = 3;
}

/// <summary>QQ Music's public web search and lyric endpoints (no account): line-timed LRC, plus a translation when QQ has one.</summary>
public sealed class QQMusicSource(LyricHttpClient http, QQMusicOptions? options = null, ILyricPayloadProbe? probe = null) : ILyricSource
{
    private const string Service = "QQ Music";
    private readonly QQMusicOptions _options = options ?? new QQMusicOptions();
    private readonly ILyricPayloadProbe _probe = probe ?? BasicLyricPayloadProbe.Instance;

    public LyricSourceId Id => LyricSourceId.QQMusic;

    public async Task<RawLyrics?> FetchAsync(LyricQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        List<Hit> hits;
        using (var search = await GetJsonAsync(_options.SearchAddress, new Dictionary<string, string>
               {
                   ["format"] = "json", ["p"] = "1", ["n"] = "8", ["w"] = $"{query.Title} {query.Artist}",
               }, ct).ConfigureAwait(false))
        {
            hits = [.. search.RootElement.Get("data").Get("song").Get("list").Items()
                .Select(song => Hit.From(song, query)).OfType<Hit>().Where(h => h.Score > 0)];
        }
        var ordered = SourceMatching.Ordered(hits, LyricSourceId.QQMusic, query, h => (h.Title, h.Artist, h.Duration, h.Score));
        foreach (var hit in ordered.Take(_options.SongsTried))
        {
            ct.ThrowIfCancellationRequested();
            if (hit.Mid is null)
            {
                continue;
            }
            using var reply = await GetJsonAsync(_options.LyricAddress, new Dictionary<string, string>
            {
                ["songmid"] = hit.Mid, ["format"] = "json", ["nobase64"] = "1", ["g_tk"] = "5381",
            }, ct).ConfigureAwait(false);
            if (Text(reply.RootElement.Str("lyric")) is not { } lrc || !_probe.IsUsable(LyricFormat.Lrc, lrc))
            {
                continue;
            }
            var extras = new Dictionary<string, string> { [RawLyricsExtras.SongId] = hit.Mid };
            if (Text(reply.RootElement.Str("trans")) is { } translation && LrcText.HasTimedLine(translation))
            {
                extras[RawLyricsExtras.TranslationLrc] = translation;
            }
            return new RawLyrics(LyricSourceId.QQMusic, LyricFormat.Lrc, lrc, hit.Title, hit.Artist, hit.Duration, extras);
        }
        return null;
    }

    /// <summary>Lyric text as QQ sends it: plain, or base64 when it holds no stamp; entities decoded.</summary>
    private static string? Text(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }
        if (!value.Contains('[', StringComparison.Ordinal))
        {
            try
            {
                value = LyricHttpClient.Utf8Text(Convert.FromBase64String(value));
            }
            catch (FormatException)
            {
            }
            catch (DecoderFallbackException)
            {
            }
        }
        return LrcText.DecodeEntities(value);
    }

    private Task<JsonDocument> GetJsonAsync(string address, Dictionary<string, string> parameters, CancellationToken ct) =>
        http.GetJsonAsync(Service, LyricHttpClient.BuildUri(address, parameters), _options.RequestTimeout, _options.Referer, ct);

    private sealed record Hit(string? Mid, string Title, string Artist, double? Duration, double Score)
    {
        public static Hit? From(JsonElement song, LyricQuery query)
        {
            if (song.Str("songname") is not { } title)
            {
                return null;
            }
            var singers = string.Join(" & ", song.Items("singer").Select(s => s.Str("name")).OfType<string>());
            var duration = song.Num("interval");
            var mid = song.Str("songmid") is { Length: > 0 } value ? value : null;
            return new Hit(mid, title, singers, duration, SourceMatching.HitScore(query, title, singers, duration));
        }
    }
}
