using Lunalyr.Core.Lyrics.Ranking;

namespace Lunalyr.Core.Lyrics.Sources;

/// <summary>How a source orders its own search hits before fetching lyrics for them.</summary>
internal static class SourceHits
{
    /// <summary>
    /// Hits that name the playing recording first (<see cref="LyricRanking.NamesRecording"/>), then by score, so a
    /// close name with the wrong length is not fetched ahead of the right recording. Ties keep the service's order.
    /// </summary>
    public static IReadOnlyList<T> Ordered<T>(IEnumerable<T> hits, LyricSourceId source, LyricQuery query,
        Func<T, (string Title, string Artist, double? Duration, double Score)> fields) =>
        [.. hits.Select(hit => (Hit: hit, Fields: fields(hit)))
            .Select(x => (x.Hit, Passes: LyricRanking.NamesRecording(source, x.Fields.Title, x.Fields.Artist, x.Fields.Duration, query), x.Fields.Score))
            .OrderByDescending(x => x.Passes).ThenByDescending(x => x.Score)
            .Select(x => x.Hit)];
}
