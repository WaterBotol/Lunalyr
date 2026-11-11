using System.Net.Sockets;

namespace Lunalyr.Core.Lyrics.Ranking;

/// <summary>
/// Whether this PC has a usable network path. A lookup that found nothing while offline is not remembered as a miss,
/// and is worth repeating once a path returns. The platform layer implements it (Windows network list events).
/// </summary>
public interface ILyricConnectivity
{
    bool IsOnline { get; }

    /// <summary>Raised when a usable path returns after being lost.</summary>
    event EventHandler? Restored;
}

/// <summary>
/// Asks one source for its best match. The lookup calls it once per source (twice for Apple Music's second look) and
/// cancels <paramref name="cancellationToken"/> when the source is stopped or times out.
/// </summary>
/// <returns>The source's best match (it may still fail the recording check), or null when it has nothing.</returns>
/// <exception cref="Exception">
/// Any failure means the source could not answer, so the miss is not remembered. Throw
/// <see cref="LyricSourceException"/> with <c>IsOffline</c> when the network is down (or let a socket error through).
/// </exception>
public delegate ValueTask<LyricCandidate?> LyricSourceFetch(LyricSourceId source, LyricQuery query, string localFolder,
    CancellationToken cancellationToken);

/// <summary>One lyric source. Providers implement this; <see cref="LyricSourceSet"/> turns a set of them into a <see cref="LyricSourceFetch"/>.</summary>
public interface ILyricSource
{
    LyricSourceId Id { get; }

    ValueTask<LyricCandidate?> FetchAsync(LyricQuery query, string localFolder, CancellationToken cancellationToken);
}

/// <summary>Dispatches a lookup's fetches to the registered sources; a source without a provider has nothing.</summary>
public sealed class LyricSourceSet
{
    private readonly Dictionary<LyricSourceId, ILyricSource> _sources = [];

    public LyricSourceSet(IEnumerable<ILyricSource> sources)
    {
        foreach (var source in sources)
        {
            _sources[source.Id] = source;
        }
    }

    public LyricSourceFetch Fetch => (id, query, folder, ct) =>
        _sources.TryGetValue(id, out var source) ? source.FetchAsync(query, folder, ct) : ValueTask.FromResult<LyricCandidate?>(null);
}

/// <summary>A source could not answer. <see cref="IsOffline"/> marks a missing connection rather than one service failing.</summary>
public sealed class LyricSourceException : Exception
{
    public LyricSourceException()
    {
    }

    public LyricSourceException(string message)
        : base(message)
    {
    }

    public LyricSourceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public LyricSourceException(string message, bool isOffline, Exception? innerException = null)
        : base(message, innerException)
    {
        IsOffline = isOffline;
    }

    public bool IsOffline { get; }

    /// <summary>
    /// Whether <paramref name="error"/> means this PC has no connection, as opposed to one service being slow, missing
    /// or broken: a <see cref="LyricSourceException"/> marked offline, or a socket error saying the network is down or
    /// unreachable anywhere in its chain.
    /// </summary>
    public static bool IsOfflineError(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
        {
            if (current is LyricSourceException { IsOffline: true })
            {
                return true;
            }
            if (current is SocketException { SocketErrorCode: SocketError.NetworkDown or SocketError.NetworkUnreachable or SocketError.NetworkReset })
            {
                return true;
            }
        }
        return false;
    }
}

/// <summary>Builds candidates from what a provider fetched, so every provider labels and checks names the same way.</summary>
public static class LyricCandidates
{
    /// <summary>
    /// A candidate for parsed lyrics. The names the service reported are used when they can be checked
    /// (<see cref="LyricQuery.Checkable"/>); otherwise the search's names stand in and
    /// <see cref="LyricCandidate.MetadataReported"/> is false, so only the length is checked. The provenance's
    /// precision is recomputed from the lines.
    /// </summary>
    /// <param name="raw">What the provider fetched, with the names the service reported.</param>
    /// <param name="parsed">The parsed lyrics (the parser workstream turns <see cref="RawLyrics"/> into these).</param>
    /// <param name="query">The lookup.</param>
    /// <param name="score">The provider's own score, used only to pick among its hits.</param>
    /// <param name="sourceLabel">The provenance's source label; the source's title when null.</param>
    public static LyricCandidate FromRaw(RawLyrics raw, LyricResult parsed, LyricQuery query, double score, string? sourceLabel = null)
    {
        var title = LyricQuery.Checkable(raw.ReportedTitle, query.Title);
        var artist = LyricQuery.Checkable(raw.ReportedArtist, query.Artist);
        var label = sourceLabel ?? raw.Source.Title();
        LyricResult result = parsed switch
        {
            LyricResult.Synced synced => new LyricResult.Synced(synced.Lines, new LyricProvenance(LyricTimingRules.Precision(synced.Lines), label)),
            LyricResult.Plain plain => new LyricResult.Plain(plain.Lines, new LyricProvenance(plain.Provenance.Precision, label)),
            _ => parsed,
        };
        return new LyricCandidate(raw.Source, result, title ?? query.Title, artist ?? query.Artist, raw.ReportedDuration, score)
        {
            MetadataReported = title is not null && artist is not null,
        };
    }
}
