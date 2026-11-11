using System.Globalization;
using System.Text.Json;

namespace Lunalyr.Core.Lyrics.Sources;

/// <summary>Settings for <see cref="KugouSource"/>.</summary>
public sealed record KugouOptions
{
    /// <summary>
    /// Kugou's song search. mobilecdn.kugou.com stopped presenting a certificate for its own name;
    /// mobileservice.kugou.com serves the same API.
    /// </summary>
    public string SearchAddress { get; init; } = "https://mobileservice.kugou.com/api/v3/search/song";
    public string LyricSearchAddress { get; init; } = "https://lyrics.kugou.com/search";
    public string DownloadAddress { get; init; } = "https://lyrics.kugou.com/download";
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>Songs whose lyrics are looked up, best first.</summary>
    public int SongsTried { get; init; } = 2;

    /// <summary>Lyric files tried per song.</summary>
    public int LyricsTriedPerSong { get; init; } = 2;
}

/// <summary>
/// Kugou Music: song search, then the lyric search for the best songs (by hash and length), then the lyric file.
/// Word-timed KRC is asked for first and decrypted to text; line LRC is the fallback.
/// </summary>
public sealed class KugouSource(LyricHttpClient http, KugouOptions? options = null, ILyricPayloadProbe? probe = null) : ILyricSource
{
    private const string Service = "Kugou";
    private readonly KugouOptions _options = options ?? new KugouOptions();
    private readonly ILyricPayloadProbe _probe = probe ?? BasicLyricPayloadProbe.Instance;

    public LyricSourceId Id => LyricSourceId.Kugou;

    public async Task<RawLyrics?> FetchAsync(LyricQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var keyword = $"{query.Title} {query.Artist}";
        List<Hit> hits;
        using (var search = await GetJsonAsync(_options.SearchAddress, new Dictionary<string, string>
               {
                   ["keyword"] = keyword, ["page"] = "1", ["pagesize"] = "8", ["format"] = "json",
               }, ct).ConfigureAwait(false))
        {
            hits = [.. search.RootElement.Get("data").Get("info").Items().Select(song => Hit.From(song, query)).OfType<Hit>().Where(h => h.Score > 0)];
        }
        var ordered = SourceMatching.Ordered(hits, LyricSourceId.Kugou, query, h => (h.Title, h.Artist, h.Duration, h.Score));
        foreach (var hit in ordered.Take(_options.SongsTried))
        {
            ct.ThrowIfCancellationRequested();
            if (hit.Hash is null)
            {
                continue;
            }
            List<(string Id, string Key)> files;
            using (var found = await GetJsonAsync(_options.LyricSearchAddress, new Dictionary<string, string>
                   {
                       ["ver"] = "1", ["man"] = "yes", ["client"] = "pc", ["keyword"] = keyword,
                       ["duration"] = ((long)(query.Duration * 1000)).ToString(CultureInfo.InvariantCulture), ["hash"] = hit.Hash,
                   }, ct).ConfigureAwait(false))
            {
                files = [.. found.RootElement.Items("candidates")
                    .Select(item => (Id: item.Str("id"), Key: item.Str("accesskey")))
                    .Where(item => item.Id is not null && item.Key is not null)
                    .Select(item => (item.Id!, item.Key!))
                    .Take(_options.LyricsTriedPerSong)];
            }
            foreach (var (id, key) in files)
            {
                ct.ThrowIfCancellationRequested();
                // KRC carries each word's timing; plain LRC is the fallback.
                try
                {
                    if (await DownloadAsync(id, key, "krc", ct).ConfigureAwait(false) is { } encrypted
                        && KugouKrc.Decode(encrypted) is { } krc && LrcText.DecodeEntities(krc) is var text
                        && _probe.IsUsable(LyricFormat.Krc, text))
                    {
                        return Lyrics(LyricFormat.Krc, text, hit);
                    }
                }
                catch (LyricSourceException)
                {
                    // The LRC download below says whether Kugou is really failing.
                }
                if (await DownloadAsync(id, key, "lrc", ct).ConfigureAwait(false) is { } bytes
                    && LrcText.DecodeEntities(LyricHttpClient.Utf8Text(bytes)) is var lrc && _probe.IsUsable(LyricFormat.Lrc, lrc))
                {
                    return Lyrics(LyricFormat.Lrc, lrc, hit);
                }
            }
        }
        return null;
    }

    private static RawLyrics Lyrics(LyricFormat format, string payload, Hit hit) =>
        new(LyricSourceId.Kugou, format, payload, hit.Title, hit.Artist, hit.Duration,
            new Dictionary<string, string> { [RawLyricsExtras.SongId] = hit.Hash ?? "" });

    /// <summary>A lyric file's bytes (the reply's base64 <c>content</c>), or null when the reply has none.</summary>
    private async Task<byte[]?> DownloadAsync(string id, string key, string format, CancellationToken ct)
    {
        using var reply = await GetJsonAsync(_options.DownloadAddress, new Dictionary<string, string>
        {
            ["ver"] = "1", ["client"] = "pc", ["id"] = id, ["accesskey"] = key, ["fmt"] = format, ["charset"] = "utf8",
        }, ct).ConfigureAwait(false);
        if (reply.RootElement.Str("content") is not { Length: > 0 } content)
        {
            return null;
        }
        try
        {
            return Convert.FromBase64String(content);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private Task<JsonDocument> GetJsonAsync(string address, Dictionary<string, string> parameters, CancellationToken ct) =>
        http.GetJsonAsync(Service, LyricHttpClient.BuildUri(address, parameters), _options.RequestTimeout, null, ct);

    private sealed record Hit(string? Hash, string Title, string Artist, double? Duration, double Score)
    {
        public static Hit? From(JsonElement song, LyricQuery query)
        {
            var hash = song.Str("hash") is { Length: > 0 } value ? value : null;
            var filename = (song.Str("filename") ?? "").Split(" - ");
            var title = song.Str("songname") ?? filename[^1];
            var artist = song.Str("singername") ?? filename[0];
            var duration = song.Num("duration");
            return new Hit(hash, title, artist, duration, SourceMatching.HitScore(query, title, artist, duration));
        }
    }
}
