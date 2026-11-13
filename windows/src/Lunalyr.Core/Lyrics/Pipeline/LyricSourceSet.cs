using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Core.Lyrics.Sources;

namespace Lunalyr.Core.Lyrics.Pipeline;

/// <summary>
/// The sources a lookup can ask, as one <see cref="LyricSourceFetch"/>: each fetch runs on the thread pool (so a
/// provider that blocks holds up nothing else), its <see cref="RawLyrics"/> goes through
/// <see cref="LyricCandidateBuilder"/>, and the Chinese catalogues are asked again under the artist's other names
/// when the library's name found nothing (<see cref="ArtistAliases"/>). A song listed under an English title is
/// searched again under its original one when the listed title found nothing usable (<see cref="OriginalTitles"/>,
/// around the artist-name search, as on the Mac); the artist's MusicBrainz country, kept with its other names, helps
/// tell whether Latin-only lyrics can be the song's. A source without a provider has nothing.
/// </summary>
public sealed class LyricSourceSet
{
    private readonly Dictionary<LyricSourceId, ILyricSource> _sources = [];
    private readonly ArtistAliases? _aliases;
    private readonly OriginalTitles? _titles;
    private readonly LyricParseOptions? _parse;

    /// <param name="sources">One provider per source; a later one replaces an earlier one for the same source.</param>
    /// <param name="aliases">Other names for artists, or null to search under the library's names only.</param>
    /// <param name="parse">Languages and reading detection for the parsers.</param>
    /// <param name="titles">Songs' original titles, or null to search under the listed titles only.</param>
    public LyricSourceSet(IEnumerable<ILyricSource> sources, ArtistAliases? aliases = null, LyricParseOptions? parse = null,
        OriginalTitles? titles = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        foreach (var source in sources)
        {
            _sources[source.Id] = source;
        }
        _aliases = aliases;
        _titles = titles;
        _parse = parse;
    }

    /// <summary>What the parsers are given: languages, readers and the word distributor.</summary>
    public LyricParseOptions? ParseOptions => _parse;

    /// <summary>The providers, by source.</summary>
    public IReadOnlyDictionary<LyricSourceId, ILyricSource> Sources => _sources;

    /// <summary>Whether to search the catalogues again under the artist's other names (the Mac's setting, on by default).</summary>
    public bool UsesArtistAliases { get; set; } = true;

    /// <summary>
    /// Whether to search again under a song's original title (on by default). The Mac shares the artist-name setting
    /// ("Search under other names"), so the app sets both from it.
    /// </summary>
    public bool UsesOriginalTitles { get; set; } = true;

    /// <summary>Why <paramref name="id"/> cannot be asked on this PC, or null when it can (or has no provider).</summary>
    public string? UnavailableReason(LyricSourceId id) =>
        _sources.TryGetValue(id, out var source) && source.Availability is { IsAvailable: false } availability
            ? availability.Reason ?? "Not available on this PC"
            : null;

    /// <summary>
    /// Why <paramref name="id"/> can never answer a lookup with <paramref name="folder"/> (the local source with no
    /// folder chosen), or null when it can be asked.
    /// </summary>
    public string? NotConfiguredReason(LyricSourceId id, string folder) =>
        _sources.TryGetValue(id, out var source) && source is IFolderLyricSource local ? local.NotConfiguredReason(folder) : null;

    /// <summary>Identity of the local lyric files that could hold this track's lyrics, for <see cref="LyricLookup.RefreshLocalAsync"/>.</summary>
    public string? LocalFingerprint(LyricQuery query, string folder) =>
        _sources.TryGetValue(LyricSourceId.Lyrimuse, out var source) && source is IFolderLyricSource local ? local.Fingerprint(query, folder) : null;

    public LyricSourceFetch Fetch => (id, query, folder, ct) =>
        _sources.TryGetValue(id, out var source)
            ? new ValueTask<LyricCandidate?>(Task.Run(() => FetchAsync(source, query, folder, ct).AsTask(), ct))
            : ValueTask.FromResult<LyricCandidate?>(null);

    private ValueTask<LyricCandidate?> FetchAsync(ILyricSource source, LyricQuery query, string folder, CancellationToken ct) =>
        OriginalTitles.WithOriginalTitleAsync(source.Id, query, _titles, UsesOriginalTitles,
            (titled, outer) => ArtistAliases.WithAliasesAsync(source.Id, titled, _aliases, UsesArtistAliases,
                (asked, token) => AskAsync(source, asked, folder, token), outer), ct, _aliases);

    private async ValueTask<LyricCandidate?> AskAsync(ILyricSource source, LyricQuery query, string folder, CancellationToken ct)
    {
        var raw = source is IFolderLyricSource local
            ? await local.FetchAsync(query, folder, ct).ConfigureAwait(false)
            : await source.FetchAsync(query, ct).ConfigureAwait(false);
        return raw is null ? null : LyricCandidateBuilder.Build(raw, query, _parse);
    }
}
