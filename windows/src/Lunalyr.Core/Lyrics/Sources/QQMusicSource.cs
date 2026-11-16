using System.Text;
using System.Text.Json;

using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Ranking;

namespace Lunalyr.Core.Lyrics.Sources;

/// <summary>Settings for <see cref="QQMusicSource"/>.</summary>
public sealed record QQMusicOptions
{
    public string SearchAddress { get; init; } = "https://c.y.qq.com/soso/fcgi-bin/client_search_cp";
    public string LyricAddress { get; init; } = "https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg";

    /// <summary>QRC word timing by the search's numeric song id (the Mac's 7ddb08a; Lyrimuse's qqfallback.go <c>qqLyricDownload</c>).</summary>
    public string DownloadAddress { get; init; } = "https://c.y.qq.com/qqmusic/fcgi-bin/lyric_download.fcg";
    public string Referer { get; init; } = "https://y.qq.com/";
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>Songs whose lyrics are fetched, best first, until one has usable lyrics.</summary>
    public int SongsTried { get; init; } = 3;
}

/// <summary>
/// QQ Music's public web search and lyric endpoints (no account): QRC word timing from <c>lyric_download.fcg</c>
/// (triple-DES over zlib, <see cref="QQMusicQrc"/>), else line-timed LRC from the same reply or
/// <c>fcg_query_lyric_new.fcg</c>, with a translation when QQ has one. A later hit with QRC wins over an earlier
/// line-only one unless only the line hit names the playing recording (the Mac's 7ddb08a).
/// </summary>
public sealed class QQMusicSource(LyricHttpClient http, QQMusicOptions? options = null, ILyricPayloadProbe? probe = null) : ILyricSource
{
    private const string Service = "QQ Music";
    private readonly QQMusicOptions _options = options ?? new QQMusicOptions();
    private readonly ILyricPayloadProbe _probe = probe ?? ParsedLyricPayloadProbe.Instance;

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
        var ordered = SourceHits.Ordered(hits, LyricSourceId.QQMusic, query, h => (h.Title, h.Artist, h.Duration, h.Score));
        var held = new HeldLine();
        foreach (var hit in ordered.Take(_options.SongsTried))
        {
            ct.ThrowIfCancellationRequested();
            var passes = HeldLine.Names(LyricSourceId.QQMusic, hit.Title, hit.Artist, hit.Duration, query);
            // Nothing from this hit could replace the line timing held.
            if (!held.YieldsTo(passes)) continue;
            RawLyrics? Offer(string lrc, Dictionary<string, string>? extras = null) => _probe.IsUsable(LyricFormat.Lrc, lrc)
                ? new RawLyrics(LyricSourceId.QQMusic, LyricFormat.Lrc, lrc, hit.Title, hit.Artist, hit.Duration,
                                extras ?? new Dictionary<string, string> { [RawLyricsExtras.SongId] = hit.Mid ?? hit.Id ?? "" })
                : null;
            // Word timing first: QRC. A failed request is not an outage: the line lyrics below say whether QQ is failing.
            if (hit.Id is { } id && id != "0" && await DownloadAsync(id, ct).ConfigureAwait(false) is { } download)
            {
                if (download.Enhanced is { } enhanced && Offer(enhanced) is { } found && HeldLine.WordTimed(found) && held.YieldsTo(passes))
                    return found;
                if ((download.Line ?? download.Enhanced) is { } line) held.Hold(Offer(LrcText.DecodeEntities(line)), passes);
            }
            // Line LRC is fetched only until one is held; later hits are read for word timing alone.
            if (held.Lyrics is not null || hit.Mid is null) continue;
            using var reply = await GetJsonAsync(_options.LyricAddress, new Dictionary<string, string>
            {
                ["songmid"] = hit.Mid, ["format"] = "json", ["nobase64"] = "1", ["g_tk"] = "5381",
            }, ct).ConfigureAwait(false);
            if (Text(reply.RootElement.Str("lyric")) is not { } lrc) continue;
            var extras = new Dictionary<string, string> { [RawLyricsExtras.SongId] = hit.Mid };
            if (Text(reply.RootElement.Str("trans")) is { } translation && LrcText.HasTimedLine(translation))
            {
                extras[RawLyricsExtras.TranslationLrc] = translation;
            }
            held.Hold(Offer(lrc, extras), passes);
        }
        return held.Lyrics;
    }

    /// <summary>One <c>lyric_download.fcg</c> reply (a form POST), or null when it failed or held nothing readable.</summary>
    private async Task<QQMusicQrc.Download?> DownloadAsync(string id, CancellationToken ct)
    {
        try
        {
            var response = await http.SendAsync(Service, () => new HttpRequestMessage(HttpMethod.Post, _options.DownloadAddress)
            {
                Headers = { Referrer = new Uri(_options.Referer) },
                Content = new FormUrlEncodedContent(new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["lrctype"] = "4", ["miniversion"] = "82", ["musicid"] = id, ["version"] = "15",
                }),
            }, _options.RequestTimeout, ct).ConfigureAwait(false);
            return response.IsOk ? QQMusicQrc.DownloadOf(LyricHttpClient.Utf8Text(response.Body)) : null;
        }
        catch (LyricSourceException)
        {
            return null;
        }
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

    /// <param name="Id">The numeric song id QRC is asked for by.</param>
    private sealed record Hit(string? Mid, string? Id, string Title, string Artist, double? Duration, double Score)
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
            var id = song.Str("songid") is { Length: > 0 } number ? number : null;
            return new Hit(mid, id, title, singers, duration, LyricMatchScore.HitScore(query, title, singers, duration));
        }
    }
}
