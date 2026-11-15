using System.Globalization;

namespace Lunalyr.Core.Lyrics.Sources;

/// <summary>Settings for <see cref="AmllSource"/>.</summary>
public sealed record AmllOptions
{
    /// <summary>Copies of the database, tried in order.</summary>
    public IReadOnlyList<string> Bases { get; init; } =
    [
        "https://raw.githubusercontent.com/amll-dev/amll-ttml-db/refs/heads/main",
        "https://raw.githubusercontent.com/Steve-xmh/amll-ttml-db/refs/heads/main",
    ];

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(8);
}

/// <summary>
/// The AMLL TTML database: community-made, word- and syllable-timed TTML, one public file per NetEase song id
/// (<c>ncm-lyrics/&lt;id&gt;.ttml</c>). NetEase's search finds the song; its id then addresses the file, so only a
/// song both know is found. Nothing is uploaded.
/// </summary>
public sealed class AmllSource : ILyricSource
{
    private const string Service = "AMLL TTML DB";
    private readonly LyricHttpClient _http;
    private readonly NetEaseCatalog _catalog;
    private readonly AmllOptions _options;
    private readonly ILyricPayloadProbe _probe;

    public AmllSource(LyricHttpClient http, AmllOptions? options = null, NetEaseOptions? netEase = null, ILyricPayloadProbe? probe = null)
    {
        _http = http;
        _catalog = new NetEaseCatalog(http, netEase);
        _options = options ?? new AmllOptions();
        _probe = probe ?? ParsedLyricPayloadProbe.Instance;
    }

    public LyricSourceId Id => LyricSourceId.Amll;

    /// <summary>The address of the database file for a NetEase song.</summary>
    public static string FileAddress(string baseAddress, long netEaseId) =>
        string.Create(CultureInfo.InvariantCulture, $"{baseAddress}/ncm-lyrics/{netEaseId}.ttml");

    public async Task<RawLyrics?> FetchAsync(LyricQuery query, CancellationToken ct)
    {
        if (await _catalog.MatchAsync(query, ct).ConfigureAwait(false) is not { } song)
        {
            return null;
        }
        foreach (var baseAddress in _options.Bases)
        {
            string text;
            try
            {
                var response = await _http.GetAsync(Service, new Uri(FileAddress(baseAddress, song.Id)), _options.RequestTimeout, null, ct)
                    .ConfigureAwait(false);
                if (!response.IsOk)
                {
                    continue;
                }
                text = response.Text;
            }
            catch (LyricSourceException exception) when (!exception.IsOffline)
            {
                // A copy that is missing or down: the next one may serve.
                continue;
            }
            if (!_probe.IsUsable(LyricFormat.Ttml, text))
            {
                return null;
            }
            return new RawLyrics(LyricSourceId.Amll, LyricFormat.Ttml, text, song.Title, song.Artist, song.Duration,
                new Dictionary<string, string> { [RawLyricsExtras.SongId] = song.Id.ToString(CultureInfo.InvariantCulture) });
        }
        return null;
    }
}
