using System.Globalization;
using System.IO.Compression;
using System.Text.Json;

namespace Lunalyr.Core.Lyrics.Sources;

/// <summary>Settings for <see cref="LyricsPlusSource"/>.</summary>
public sealed record LyricsPlusOptions
{
    /// <summary>Base addresses of the Lyrics+ (KPoe) servers; the first healthy one is asked first.</summary>
    public IReadOnlyList<string> Hosts { get; init; } =
    [
        "https://lyricsplus.prjktla.workers.dev",
        "https://lyricsplus.binimum.org",
        "https://lyricsplus.prjktla.my.id",
    ];

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>A second opinion after a node's "not found" starts only this soon after the lookup began.</summary>
    public TimeSpan SecondOpinionWindow { get; init; } = TimeSpan.FromSeconds(3.5);

    /// <summary>How long a failing mirror, or one that said "not found" for lyrics another had, rests.</summary>
    public TimeSpan FailureRest { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How long a mirror that answered "busy" (HTTP 429) rests.</summary>
    public TimeSpan BusyRest { get; init; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// Lyrics+ / KPoe servers, which answer with syllable-timed lyrics as JSON. The payload is the reply body as JSON
/// text (see <see cref="Inflate"/>).
/// </summary>
/// <remarks>
/// A node's "not found" is checked with one other healthy mirror while time allows; if that one has the lyrics, the
/// empty-handed node rests like a failing one. Failing mirrors rest and are tried last, so one dead node does not cost
/// every lookup its timeout. Throws when no mirror gave a usable answer. Mirror health belongs to the instance.
/// </remarks>
public sealed class LyricsPlusSource(LyricHttpClient http, LyricsPlusOptions? options = null) : ILyricSource
{
    private const string Service = "Lyrics+";
    private readonly LyricsPlusOptions _options = options ?? new LyricsPlusOptions();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _restingUntil = new(StringComparer.OrdinalIgnoreCase);

    public LyricSourceId Id => LyricSourceId.LyricsPlus;

    public async Task<RawLyrics?> FetchAsync(LyricQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var failures = new List<string>();
        var began = http.Time.GetTimestamp();
        var emptyHanded = new List<string>();
        var checkedOnce = false;
        foreach (var host in Ordered(_options.Hosts))
        {
            ct.ThrowIfCancellationRequested();
            if (emptyHanded.Count > 0)
            {
                // One second opinion, from a mirror that is not resting, and only while its timeout still fits.
                if (checkedOnce || !IsHealthy(host) || http.Time.GetElapsedTime(began) >= _options.SecondOpinionWindow)
                {
                    break;
                }
                checkedOnce = true;
            }
            var attempt = await AttemptAsync(host, query, withAlbum: true, ct).ConfigureAwait(false);
            // An album the service files the song under differently (a single, a soundtrack) makes it answer "not found"
            // although it has the song: Heathens by twenty one pilots, 14 Nov 2026. On Windows this is the only way to
            // Apple Music's lyrics, so the same node is asked once more without the album.
            if (attempt.Kind == AttemptKind.NotFound && !string.IsNullOrEmpty(query.Album))
                attempt = await AttemptAsync(host, query, withAlbum: false, ct).ConfigureAwait(false);
            switch (attempt.Kind)
            {
                case AttemptKind.Lyrics:
                    Answered(host);
                    foreach (var node in emptyHanded)
                    {
                        Failed(node, _options.FailureRest);
                    }
                    return attempt.Lyrics;
                case AttemptKind.NotFound:
                    emptyHanded.Add(host);
                    break;
                default:
                    Failed(host, attempt.Rest);
                    failures.Add(attempt.Reason!);
                    break;
            }
        }
        if (emptyHanded.Count > 0)
        {
            return null;
        }
        throw new LyricSourceException(LyricSourceFailure.Unreachable, failures.Count == 0
            ? "Lyrics+ is unavailable"
            : "Lyrics+ mirrors unavailable (" + string.Join(", ", failures) + ")");
    }

    private enum AttemptKind { Lyrics, NotFound, Failed }

    private sealed record Attempt(AttemptKind Kind, RawLyrics? Lyrics = null, string? Reason = null, TimeSpan Rest = default);

    private async Task<Attempt> AttemptAsync(string host, LyricQuery query, bool withAlbum, CancellationToken ct)
    {
        var parameters = new List<KeyValuePair<string, string>> { new("title", query.Title), new("artist", query.Artist) };
        if (query.Duration > 0)
        {
            parameters.Add(new("duration", ((long)query.Duration).ToString(CultureInfo.InvariantCulture)));
        }
        if (withAlbum && !string.IsNullOrEmpty(query.Album))
        {
            parameters.Add(new("album", query.Album));
        }
        var uri = LyricHttpClient.BuildUri(host.TrimEnd('/') + "/v2/lyrics/get", parameters, sorted: false);
        LyricHttpResponse response;
        try
        {
            response = await http.GetAsync(Service, uri, _options.RequestTimeout, null, ct).ConfigureAwait(false);
        }
        catch (LyricSourceException exception) when (!exception.IsOffline)
        {
            return new Attempt(AttemptKind.Failed, Reason: exception.Failure == LyricSourceFailure.TimedOut ? "timed out" : "no response",
                Rest: _options.FailureRest);
        }
        // An offline exception and the caller's cancellation propagate: no other mirror can help.
        var body = Inflate(response.Body);
        switch (response.StatusCode)
        {
            case 200:
                return Read(body) is { } lyrics ? new Attempt(AttemptKind.Lyrics, lyrics)
                    : IsJson(body) ? new Attempt(AttemptKind.NotFound)
                    : new Attempt(AttemptKind.Failed, Reason: "unreadable reply", Rest: _options.FailureRest);
            case 404 when IsJson(body):
                // The service's own "no lyrics" answer, as opposed to a missing node's error page.
                return new Attempt(AttemptKind.NotFound);
            case 429:
                // The node says how long to wait ("Please wait 7 seconds"): rest it that long, not a fixed minute (the
                // lyric-sourcing evaluation, 15 Nov 2026: one node allows 2 requests per 10 s).
                return new Attempt(AttemptKind.Failed, Reason: "busy", Rest: BusyWait(body) ?? _options.BusyRest);
            default:
                return new Attempt(AttemptKind.Failed, Reason: $"HTTP {response.StatusCode}", Rest: _options.FailureRest);
        }
    }

    /// <summary>
    /// The lyrics in a node's reply, or null when it has none: at least one line with text. A "type":"Line" reply has no
    /// syllables and still counts, as line timing (the Mac's next/integration 1cdde21).
    /// </summary>
    private static RawLyrics? Read(byte[] body)
    {
        try
        {
            using var document = LyricHttpClient.ParseJson(Service, body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.Items("lyrics").Any(HasText))
            {
                return null;
            }
            var metadata = root.Get("metadata");
            var extras = new Dictionary<string, string>();
            // metadata.source can carry a bracketed note after the provider's name; only the name is kept.
            if (Name(metadata, "source") is { } source)
            {
                var cut = source.IndexOf(" (", StringComparison.Ordinal);
                var upstream = cut >= 0 ? source[..cut] : source;
                // One node says "qApple" for Apple Music's lyrics: label them "Apple" whichever node answered.
                extras[RawLyricsExtras.Upstream] = Ranking.LyricSourceRanking.RelayLabel(upstream);
            }
            // Only `source` is documented; the matched song's names are read under the spellings nodes have used.
            var title = Name(metadata, "title") ?? Name(metadata, "name");
            var artist = Name(metadata, "artist") ?? Name(metadata, "artists") ?? Name(metadata, "artistName");
            return new RawLyrics(LyricSourceId.LyricsPlus, LyricFormat.KPoeJson, LyricHttpClient.Utf8Text(body), title, artist, null, extras);
        }
        catch (LyricSourceException)
        {
            return null;
        }
    }

    private static bool HasText(JsonElement line) => !string.IsNullOrWhiteSpace(line.Str("text"));

    /// <summary>The wait a busy node asks for ("wait 7 seconds"), between 1 s and a minute; null when it names none.</summary>
    public static TimeSpan? BusyWait(byte[] body)
    {
        string text;
        try
        {
            text = System.Text.Encoding.UTF8.GetString(body);
        }
        catch (ArgumentException)
        {
            return null;
        }
        var match = System.Text.RegularExpressions.Regex.Match(text, @"(\d+(?:\.\d+)?)\s*(?:s\b|sec|second)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(50));
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            return null;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 60));
    }

    /// <summary>A metadata name: a non-empty string, or a list of names joined with ", ".</summary>
    private static string? Name(JsonElement? metadata, string key)
    {
        var value = metadata.Get(key);
        if (value?.ValueKind == JsonValueKind.String)
        {
            var text = value.Value.GetString()!.Trim();
            return text.Length == 0 ? null : text;
        }
        if (value?.ValueKind == JsonValueKind.Array)
        {
            var names = value.Value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String)
                .Select(v => v.GetString()!.Trim()).Where(v => v.Length > 0).ToList();
            return names.Count == 0 ? null : string.Join(", ", names);
        }
        return null;
    }

    private static bool IsJson(byte[] body)
    {
        try
        {
            using var _ = LyricHttpClient.ParseJson(Service, body);
            return true;
        }
        catch (LyricSourceException)
        {
            return false;
        }
    }

    /// <summary>
    /// The body decompressed when it starts with the gzip magic bytes; HttpClient only does that itself when the
    /// reply declares its encoding.
    /// </summary>
    internal static byte[] Inflate(byte[] body)
    {
        if (body.Length <= 18 || body[0] != 0x1F || body[1] != 0x8B)
        {
            return body;
        }
        try
        {
            using var input = new MemoryStream(body);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return output.ToArray();
        }
        catch (InvalidDataException)
        {
            return body;
        }
    }

    // Mirror health: healthy mirrors in their usual order, then at most one resting mirror (the one due back soonest)
    // in case it has recovered.
    private List<string> Ordered(IReadOnlyList<string> hosts)
    {
        var now = http.Time.GetUtcNow();
        lock (_gate)
        {
            var healthy = hosts.Where(h => !_restingUntil.TryGetValue(h, out var until) || until <= now).ToList();
            var resting = hosts.Where(h => _restingUntil.TryGetValue(h, out var until) && until > now)
                .OrderBy(h => _restingUntil[h]).Take(1);
            healthy.AddRange(resting);
            return healthy;
        }
    }

    private bool IsHealthy(string host)
    {
        lock (_gate)
        {
            return !_restingUntil.TryGetValue(host, out var until) || until <= http.Time.GetUtcNow();
        }
    }

    private void Failed(string host, TimeSpan rest)
    {
        lock (_gate)
        {
            _restingUntil[host] = http.Time.GetUtcNow() + rest;
        }
    }

    private void Answered(string host)
    {
        lock (_gate)
        {
            _restingUntil.Remove(host);
        }
    }
}
