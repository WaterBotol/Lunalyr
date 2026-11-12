using System.Globalization;
using System.Text.Json;

using Lunalyr.Core.Lyrics.Ranking;

namespace Lunalyr.Core.Lyrics.Sources;

/// <summary>Settings for the NetEase Cloud Music sources (<see cref="NetEaseSource"/>, and the search <see cref="AmllSource"/> uses).</summary>
public sealed record NetEaseOptions
{
    public string SearchAddress { get; init; } = "https://music.163.com/api/search/get";
    public string LyricAddress { get; init; } = "https://music.163.com/api/song/lyric";
    public string Referer { get; init; } = "https://music.163.com/";
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(6);
    public int SearchLimit { get; init; } = 8;
}

/// <summary>The NetEase catalogue entry a search settled on.</summary>
public sealed record NetEaseSong(long Id, string Title, string Artist, double? Duration);

/// <summary>One NetEase search result, as <see cref="NetEaseCatalog.BestSong"/> compares it. Duration in milliseconds.</summary>
public sealed record NetEaseSearchHit(long Id, string Name, IReadOnlyList<string> Artists, long? DurationMs);

/// <summary>
/// NetEase's song search (public web API, no key): finds the catalogue entry for a query. Shared by the NetEase source
/// and AMLL, whose database is addressed by NetEase song id.
/// </summary>
public sealed class NetEaseCatalog(LyricHttpClient http, NetEaseOptions? options = null)
{
    internal const string Service = "NetEase";

    public NetEaseOptions Options { get; } = options ?? new NetEaseOptions();

    /// <summary>
    /// The entry that best fits the query, or null when none does. Throws <see cref="LyricSourceException"/> when NetEase
    /// could not be reached or answered with an error ("code": -460 and the like) rather than a result.
    /// </summary>
    public async Task<NetEaseSong?> MatchAsync(LyricQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var uri = LyricHttpClient.BuildUri(Options.SearchAddress, new Dictionary<string, string>
        {
            ["s"] = $"{query.Title} {query.Artist}",
            ["type"] = "1",
            ["limit"] = Options.SearchLimit.ToString(CultureInfo.InvariantCulture),
        });
        using var document = await http.GetJsonAsync(Service, uri, Options.RequestTimeout, Options.Referer, ct).ConfigureAwait(false);
        // A reply without a result is an error; a result without songs is a real "no match".
        if (document.RootElement.Get("result") is not { ValueKind: JsonValueKind.Object } result)
        {
            throw new LyricSourceException(LyricSourceFailure.BadResponse, "NetEase: unexpected search response");
        }
        var hits = result.Items("songs").Select(ReadHit).OfType<NetEaseSearchHit>();
        return BestSong(hits, query.Title, query.Artist, query.Duration);
    }

    /// <summary>
    /// Search returns many editions of a popular track (radio edits, live cuts, remixes) with similar names. A wrong
    /// edit means every timestamp drifts, so every hit is scored and the best kept: an edition whose length fits first,
    /// then the closest title and artist, then the smallest length difference. Editions eight or more seconds off, and
    /// entries without a length when the playing length is known, are not offered.
    /// </summary>
    public static NetEaseSong? BestSong(IEnumerable<NetEaseSearchHit> songs, string title, string artist, double duration)
    {
        ArgumentNullException.ThrowIfNull(songs);
        var query = new LyricQuery(title, artist, null, duration);
        var hasArtist = query.HasArtist;
        (NetEaseSong Song, double[] Key)? best = null;
        foreach (var song in songs)
        {
            var names = song.Artists.Where(name => name.Length > 0).ToList();
            var credit = string.Join(", ", names);
            var titleMatch = LyricQuery.Similarity(title, song.Name);
            var artistMatch = hasArtist
                ? names.Select(name => LyricQuery.Similarity(artist, name)).Aggregate(LyricQuery.Similarity(artist, credit), Math.Max)
                : 1;
            if (titleMatch < 0.75 || artistMatch < 0.65)
            {
                continue;
            }
            // An entry without a length cannot be told apart from other edits.
            double? length = song.DurationMs is long ms && ms > 0 ? ms / 1000.0 : null;
            if (duration > 0 && length is null)
            {
                continue;
            }
            var delta = duration > 0 && length is double known ? Math.Abs(known - duration) : 0;
            if (duration > 0 && delta >= 8)
            {
                continue;
            }
            var fits = duration <= 0 || delta <= Math.Max(3, duration * 0.02);
            double[] key = [fits ? 1 : 0, titleMatch, artistMatch, -delta];
            if (best is { } current && !Precedes(current.Key, key))
            {
                continue;
            }
            best = (new NetEaseSong(song.Id, song.Name, credit, length), key);
        }
        return best?.Song;
    }

    /// <summary>Whether <paramref name="a"/> sorts strictly before <paramref name="b"/>, element by element.</summary>
    private static bool Precedes(double[] a, double[] b)
    {
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            if (a[i] != b[i])
            {
                return a[i] < b[i];
            }
        }
        return a.Length < b.Length;
    }

    private static NetEaseSearchHit? ReadHit(JsonElement song)
    {
        if (song.Num("id") is not double id || song.Str("name") is not { } name)
        {
            return null;
        }
        var artists = song.Items("artists").Select(a => a.Str("name")).OfType<string>().ToList();
        return new NetEaseSearchHit((long)id, name, artists, song.Num("duration") is double ms ? (long)ms : null);
    }
}

/// <summary>
/// NetEase Cloud Music: the search's best entry, then its lyrics. Word-timed YRC when NetEase has it, with the line LRC,
/// NetEase's romanised lines and its translation alongside; line-timed LRC otherwise.
/// </summary>
public sealed class NetEaseSource : ILyricSource
{
    private readonly LyricHttpClient _http;
    private readonly NetEaseCatalog _catalog;
    private readonly ILyricPayloadProbe _probe;

    public NetEaseSource(LyricHttpClient http, NetEaseOptions? options = null, ILyricPayloadProbe? probe = null)
    {
        _http = http;
        _catalog = new NetEaseCatalog(http, options);
        _probe = probe ?? ParsedLyricPayloadProbe.Instance;
    }

    public LyricSourceId Id => LyricSourceId.NetEase;

    public async Task<RawLyrics?> FetchAsync(LyricQuery query, CancellationToken ct)
    {
        if (await _catalog.MatchAsync(query, ct).ConfigureAwait(false) is not { } song)
        {
            return null;
        }
        var options = _catalog.Options;
        // The Mac's request (spec §2.4): line LRC, karaoke and word-timed YRC (lv, kv, yv = 1), the latest translation
        // (tv = -1) and romanised lines (rv = -1, `romalrc`).
        var uri = LyricHttpClient.BuildUri(options.LyricAddress, new Dictionary<string, string>
        {
            ["id"] = song.Id.ToString(CultureInfo.InvariantCulture),
            ["lv"] = "1",
            ["kv"] = "1",
            ["tv"] = "-1",
            ["yv"] = "1",
            ["rv"] = "-1",
        });
        using var document = await _http.GetJsonAsync(NetEaseCatalog.Service, uri, options.RequestTimeout, options.Referer, ct).ConfigureAwait(false);
        var root = document.RootElement;
        var yrc = Lyric(root, "yrc");
        var lrc = Lyric(root, "lrc");
        var extras = new Dictionary<string, string> { [RawLyricsExtras.SongId] = song.Id.ToString(CultureInfo.InvariantCulture) };
        if (Lyric(root, "romalrc") is { } roma)
        {
            extras[RawLyricsExtras.RomanizationLrc] = roma;
        }
        if (Lyric(root, "tlyric") is { } translation)
        {
            extras[RawLyricsExtras.TranslationLrc] = translation;
        }
        LyricFormat format;
        string payload;
        if (yrc is not null && _probe.IsUsable(LyricFormat.Yrc, yrc))
        {
            (format, payload) = (LyricFormat.Yrc, yrc);
            if (lrc is not null)
            {
                extras[RawLyricsExtras.Lrc] = lrc;
            }
        }
        else if (lrc is not null && _probe.IsUsable(LyricFormat.Lrc, lrc))
        {
            (format, payload) = (LyricFormat.Lrc, lrc);
        }
        else
        {
            return null;
        }
        return new RawLyrics(LyricSourceId.NetEase, format, payload, song.Title, song.Artist, song.Duration, extras);
    }

    private static string? Lyric(JsonElement root, string kind) =>
        root.Get(kind).Str("lyric") is { Length: > 0 } text && !string.IsNullOrWhiteSpace(text) ? text : null;
}
