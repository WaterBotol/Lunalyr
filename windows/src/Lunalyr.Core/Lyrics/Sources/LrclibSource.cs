using System.Globalization;
using System.Text.Json;

namespace Lunalyr.Core.Lyrics.Sources;

/// <summary>Settings for <see cref="LrclibSource"/>.</summary>
public sealed record LrclibOptions
{
    public string BaseAddress { get; init; } = "https://lrclib.net/api/";

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>A busy reply (429/503) is retried once when the server asks for at most this long a pause.</summary>
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How many search rows are weighed.</summary>
    public int SearchLimit { get; init; } = 30;
}

/// <summary>
/// LRCLIB (lrclib.net): the exact-match endpoint first, then search when the exact match is missing or its timing is
/// unsound. Delivers the Lyricsfile (word tokens) when present, else synced LRC, else plain text. An exact match marked
/// instrumental means there are no lyrics.
/// </summary>
public sealed class LrclibSource(LyricHttpClient http, LrclibOptions? options = null, ILyricPayloadProbe? probe = null) : ILyricSource
{
    private const string Service = "LRCLIB";
    private readonly LrclibOptions _options = options ?? new LrclibOptions();
    private readonly ILyricPayloadProbe _probe = probe ?? BasicLyricPayloadProbe.Instance;

    public LyricSourceId Id => LyricSourceId.Lrclib;

    public async Task<RawLyrics?> FetchAsync(LyricQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var rows = new List<Row>();
        LyricSourceException? exactError = null;
        // The exact endpoint needs an artist; without one only search can match.
        if (SourceMatching.HasArtist(query))
        {
            var parameters = new Dictionary<string, string> { ["track_name"] = query.Title, ["artist_name"] = query.Artist };
            if (!string.IsNullOrEmpty(query.Album))
            {
                parameters["album_name"] = query.Album;
            }
            if (query.Duration > 0)
            {
                parameters["duration"] = ((long)Math.Round(query.Duration, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);
            }
            try
            {
                if (await RequestAsync("get", parameters, ct).ConfigureAwait(false) is { } body && TryParse(body) is { } document)
                {
                    using (document)
                    {
                        if (document.RootElement.ValueKind == JsonValueKind.Object)
                        {
                            rows.Add(Row.From(document.RootElement));
                        }
                    }
                }
            }
            catch (LyricSourceException exception)
            {
                exactError = exception;
            }
        }
        // The exact recording is marked instrumental: a search would only find a sung edition with the same name and
        // length (an album's instrumental version), so there are no lyrics to show.
        if (rows.Count > 0 && rows[0].Instrumental && SourceMatching.Similarity(query.Title, rows[0].TrackName ?? query.Title) >= 0.95)
        {
            return null;
        }
        // An exact match whose timing is unsound (listener-made) is not the last word: other uploads are searched too.
        var exactTimed = rows.Any(row => row.TimedPayload() is { } timed && _probe.HasSoundTiming(timed.Format, timed.Payload));
        if (!exactTimed)
        {
            var search = new Dictionary<string, string> { ["track_name"] = query.Title };
            if (SourceMatching.HasArtist(query))
            {
                search["artist_name"] = query.Artist;
            }
            try
            {
                if (await RequestAsync("search", search, ct).ConfigureAwait(false) is { } body && TryParse(body) is { } document)
                {
                    using (document)
                    {
                        if (document.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            rows.AddRange(document.RootElement.EnumerateArray().Take(_options.SearchLimit)
                                .Where(e => e.ValueKind == JsonValueKind.Object).Select(Row.From));
                        }
                    }
                }
            }
            catch (LyricSourceException) when (rows.Count > 0)
            {
            }
        }
        if (rows.Count == 0 && exactError is not null)
        {
            throw exactError;
        }
        Offer? best = null;
        foreach (var row in rows)
        {
            if (Offer.From(row, query, _probe) is { } offer && (best is null || offer.Outranks(best)))
            {
                best = offer;
            }
        }
        return best?.Lyrics;
    }

    /// <summary>One LRCLIB request: the body on 200, null on 404, one retry on a short 429/503, otherwise an error.</summary>
    private async Task<byte[]?> RequestAsync(string path, IReadOnlyDictionary<string, string> parameters, CancellationToken ct)
    {
        var uri = LyricHttpClient.BuildUri(_options.BaseAddress + path, parameters);
        for (var attempt = 0; ; attempt++)
        {
            var response = await http.GetAsync(Service, uri, _options.RequestTimeout, referer: null, ct).ConfigureAwait(false);
            switch (response.StatusCode)
            {
                case 200:
                    return response.Body;
                case 404:
                    return null;
                case 429 or 503 when attempt == 0:
                    var delay = response.RetryAfter ?? TimeSpan.FromSeconds(1);
                    // Long server backoffs are reported rather than keeping a lookup alive.
                    if (delay > _options.MaxRetryAfter)
                    {
                        throw new LyricSourceException(LyricSourceFailure.Busy,
                            $"LRCLIB is busy. Try again later (HTTP {response.StatusCode}).", response.StatusCode);
                    }
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, http.Time, ct).ConfigureAwait(false);
                    }
                    continue;
                default:
                    LyricHttpClient.EnsureOk(Service, response);
                    return null;
            }
        }
    }

    private static JsonDocument? TryParse(byte[] body)
    {
        try
        {
            return LyricHttpClient.ParseJson(Service, body);
        }
        catch (LyricSourceException)
        {
            return null;
        }
    }

    private sealed record Row(long? Id, string? TrackName, string? ArtistName, string? AlbumName, double? Duration,
        string? SyncedLyrics, string? Lyricsfile, string? PlainLyrics, bool Instrumental)
    {
        public static Row From(JsonElement e) => new(
            e.Num("id") is double id ? (long)id : null, e.Str("trackName"), e.Str("artistName"), e.Str("albumName"),
            e.Num("duration"), e.Str("syncedLyrics"), e.Str("lyricsfile"), e.Str("plainLyrics"), e.Bool("instrumental") == true);

        /// <summary>The richest timed payload: the Lyricsfile, else synced LRC.</summary>
        public (LyricFormat Format, string Payload)? TimedPayload() =>
            !string.IsNullOrEmpty(Lyricsfile) ? (LyricFormat.Lyricsfile, Lyricsfile)
            : !string.IsNullOrEmpty(SyncedLyrics) ? (LyricFormat.Lrc, SyncedLyrics)
            : null;
    }

    /// <summary>A row weighed as the Mac weighs LRCLIB's hits.</summary>
    private sealed record Offer(RawLyrics Lyrics, double Score, bool Passes)
    {
        /// <summary>Higher when it names the playing recording, then by score; the first of equals stays.</summary>
        public bool Outranks(Offer other) => Passes != other.Passes ? Passes : Score > other.Score;

        public static Offer? From(Row row, LyricQuery query, ILyricPayloadProbe probe)
        {
            var title = row.TrackName ?? query.Title;
            var artist = row.ArtistName ?? query.Artist;
            var delta = row.Duration is double d ? Math.Abs(d - query.Duration) : 0;
            var albumMatches = SourceMatching.Normalize(row.AlbumName ?? "") == SourceMatching.Normalize(query.Album ?? "");
            var score = SourceMatching.MatchScore(SourceMatching.Similarity(query.Title, title), SourceMatching.ArtistScore(query, artist),
                delta, row.Duration is not null && query.Duration > 0, wordTimed: false, lineTimed: true, albumMatches);
            if (score <= 0)
            {
                return null;
            }
            var extras = new Dictionary<string, string>();
            if (row.Id is long id)
            {
                extras[RawLyricsExtras.SongId] = id.ToString(CultureInfo.InvariantCulture);
            }
            LyricFormat format;
            string payload;
            if (TimedRow(row, probe) is { } timed)
            {
                (format, payload) = timed;
                if (format == LyricFormat.Lyricsfile && !string.IsNullOrEmpty(row.SyncedLyrics))
                {
                    extras[RawLyricsExtras.Lrc] = row.SyncedLyrics;
                }
                if (!string.IsNullOrEmpty(row.PlainLyrics))
                {
                    extras[RawLyricsExtras.Plain] = row.PlainLyrics;
                }
                // Unsound timing ranks below every upload with sound timing, above plain text.
                if (!probe.HasSoundTiming(format, payload))
                {
                    score -= 6;
                }
            }
            else if (!string.IsNullOrEmpty(row.PlainLyrics))
            {
                (format, payload) = (LyricFormat.Plain, row.PlainLyrics);
                score -= 12;
            }
            else
            {
                return null;
            }
            var passes = SourceMatching.NamesRecording(LyricSourceId.Lrclib, title, artist, row.Duration, query);
            var lyrics = new RawLyrics(LyricSourceId.Lrclib, format, payload, title, artist, row.Duration, extras);
            return new Offer(lyrics, score, passes);
        }

        /// <summary>The Lyricsfile when it holds lines, else synced LRC when it does; null for untimed rows.</summary>
        private static (LyricFormat, string)? TimedRow(Row row, ILyricPayloadProbe probe)
        {
            if (!string.IsNullOrEmpty(row.Lyricsfile) && probe.IsUsable(LyricFormat.Lyricsfile, row.Lyricsfile))
            {
                return (LyricFormat.Lyricsfile, row.Lyricsfile);
            }
            if (!string.IsNullOrEmpty(row.SyncedLyrics) && probe.IsUsable(LyricFormat.Lrc, row.SyncedLyrics))
            {
                return (LyricFormat.Lrc, row.SyncedLyrics);
            }
            return null;
        }
    }
}
