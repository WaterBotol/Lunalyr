using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Pipeline;
using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Shell.Lyrics;

namespace Lunalyr.Shell.Demo;

/// <summary>
/// The lyric lookup <c>--demo</c> uses: answers the demo songs from <see cref="DemoSongs"/>, and nothing else. It never
/// touches the network or the lyric cache.
/// </summary>
public sealed class DemoLyricLookup(IReadOnlyList<DemoSong>? songs = null) : ILyricLookup
{
    private readonly IReadOnlyList<DemoSong> _songs = songs ?? DemoSongs.Songs;

    public string? DisabledReason => null;

    public WordDistributor? WordDistributor => null;

    public Task<LyricLookupReport> LookupAsync(LyricQuery query, LyricSourceSnapshot options,
        Func<LyricLookupReport, ValueTask>? onUpdate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var song in _songs)
        {
            if (string.Equals(song.Title, query.Title, StringComparison.Ordinal))
                return Task.FromResult(new LyricLookupReport { Result = song.Lyrics, Revision = 1 });
        }

        return Task.FromResult(new LyricLookupReport());
    }
}
