using Lunalyr.Core.Lyrics.Ranking;

namespace Lunalyr.Core.Lyrics.Sources;

/// <summary>Settings for <see cref="LyricsOvhSource"/>.</summary>
public sealed record LyricsOvhOptions
{
    public string BaseAddress { get; init; } = "https://api.lyrics.ovh/v1/";
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(6);
}

/// <summary>
/// lyrics.ovh: untimed text, addressed by artist and title, the last resort. It reports no recording of its own, so the
/// query's names stand in.
/// </summary>
public sealed class LyricsOvhSource(LyricHttpClient http, LyricsOvhOptions? options = null) : ILyricSource
{
    private const string Service = "lyrics.ovh";
    private readonly LyricsOvhOptions _options = options ?? new LyricsOvhOptions();

    public LyricSourceId Id => LyricSourceId.LyricsOvh;

    public async Task<RawLyrics?> FetchAsync(LyricQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        // The endpoint is addressed by artist; there is nothing to ask without one.
        if (!query.HasArtist || string.IsNullOrWhiteSpace(query.Title))
        {
            return null;
        }
        var uri = new Uri(_options.BaseAddress + Uri.EscapeDataString(query.Artist) + "/" + Uri.EscapeDataString(query.Title));
        var response = await http.GetAsync(Service, uri, _options.RequestTimeout, null, ct).ConfigureAwait(false);
        // As on the Mac, a 404 (lyrics.ovh's "No lyrics found") is an error rather than an answer: it is not remembered
        // as a miss, so lyrics.ovh is asked again at the next lookup (parity check of 13 Nov 2026, row 11).
        if (response.StatusCode == 404)
        {
            throw new LyricSourceException(LyricSourceFailure.HttpStatus, "lyrics.ovh: no lyrics found (HTTP 404).", 404);
        }
        LyricHttpClient.EnsureOk(Service, response);
        using var document = LyricHttpClient.ParseJson(Service, response.Body);
        if (document.RootElement.Str("lyrics") is not { } text)
        {
            return null;
        }
        var lines = LrcText.Lines(text).Select(line => line.Trim()).ToList();
        if (!lines.Any(line => line.Length > 0))
        {
            return null;
        }
        return new RawLyrics(LyricSourceId.LyricsOvh, LyricFormat.Plain, string.Join('\n', lines), query.Title, query.Artist);
    }
}
