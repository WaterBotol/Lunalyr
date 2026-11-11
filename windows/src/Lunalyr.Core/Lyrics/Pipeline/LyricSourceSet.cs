using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Core.Lyrics.Sources;

namespace Lunalyr.Core.Lyrics.Pipeline;

/// <summary>
/// The sources a lookup can ask, as one <see cref="LyricSourceFetch"/>: each fetch runs on the thread pool (so a
/// provider that blocks holds up nothing else), its <see cref="RawLyrics"/> goes through
/// <see cref="LyricCandidateBuilder"/>, and the Chinese catalogues are asked again under the artist's other names
/// when the library's name found nothing (<see cref="ArtistAliases"/>). A source without a provider has nothing.
/// </summary>
public sealed class LyricSourceSet
{
    private readonly Dictionary<LyricSourceId, ILyricSource> _sources = [];
    private readonly ArtistAliases? _aliases;
    private readonly LyricParseOptions? _parse;

    /// <param name="sources">One provider per source; a later one replaces an earlier one for the same source.</param>
    /// <param name="aliases">Other names for artists, or null to search under the library's names only.</param>
    /// <param name="parse">Languages and reading detection for the parsers.</param>
    public LyricSourceSet(IEnumerable<ILyricSource> sources, ArtistAliases? aliases = null, LyricParseOptions? parse = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        foreach (var source in sources)
        {
            _sources[source.Id] = source;
        }
        _aliases = aliases;
        _parse = parse;
    }

    /// <summary>The providers, by source.</summary>
    public IReadOnlyDictionary<LyricSourceId, ILyricSource> Sources => _sources;

    /// <summary>Whether to search the catalogues again under the artist's other names (the Mac's setting, on by default).</summary>
    public bool UsesArtistAliases { get; set; } = true;

    /// <summary>Why <paramref name="id"/> cannot be asked on this PC, or null when it can (or has no provider).</summary>
    public string? UnavailableReason(LyricSourceId id) =>
        _sources.TryGetValue(id, out var source) && source.Availability is { IsAvailable: false } availability
            ? availability.Reason ?? "Not available on this PC"
            : null;

    /// <summary>Identity of the local lyric files that could hold this track's lyrics, for <see cref="LyricLookup.RefreshLocalAsync"/>.</summary>
    public string? LocalFingerprint(LyricQuery query, string folder) =>
        _sources.TryGetValue(LyricSourceId.Lyrimuse, out var source) && source is IFolderLyricSource local ? local.Fingerprint(query, folder) : null;

    public LyricSourceFetch Fetch => (id, query, folder, ct) =>
        _sources.TryGetValue(id, out var source)
            ? new ValueTask<LyricCandidate?>(Task.Run(() => FetchAsync(source, query, folder, ct).AsTask(), ct))
            : ValueTask.FromResult<LyricCandidate?>(null);

    private ValueTask<LyricCandidate?> FetchAsync(ILyricSource source, LyricQuery query, string folder, CancellationToken ct) =>
        ArtistAliases.WithAliasesAsync(source.Id, query, _aliases, UsesArtistAliases, (asked, token) => AskAsync(source, asked, folder, token), ct);

    private async ValueTask<LyricCandidate?> AskAsync(ILyricSource source, LyricQuery query, string folder, CancellationToken ct)
    {
        var raw = source is IFolderLyricSource local
            ? await local.FetchAsync(query, folder, ct).ConfigureAwait(false)
            : await source.FetchAsync(query, ct).ConfigureAwait(false);
        return raw is null ? null : LyricCandidateBuilder.Build(raw, query, _parse);
    }
}
