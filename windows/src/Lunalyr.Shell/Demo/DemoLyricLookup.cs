using System.Collections.Concurrent;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Pipeline;
using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Shell.Lyrics;

namespace Lunalyr.Shell.Demo;

/// <summary>
/// The lyric lookup <c>--demo</c> uses: answers the demo songs from <see cref="DemoSongs"/>, and nothing else. It never
/// touches the network or the lyric cache. A search (Settings › Timing › Find lyrics) offers canned matches for a demo
/// song, ranked and labelled by the real ranking: its own lyrics as LRCLIB's, a line-timed copy as NetEase's, and a
/// longer recording from Kugou that fails the recording check. A choice saved for a song lasts until Lunalyr quits.
/// </summary>
public sealed class DemoLyricLookup(IReadOnlyList<DemoSong>? songs = null) : ILyricLookup
{
    private readonly IReadOnlyList<DemoSong> _songs = songs ?? DemoSongs.Songs;
    private readonly ConcurrentDictionary<string, LyricResult> _pins = new(StringComparer.Ordinal);

    public string? DisabledReason => null;

    public WordDistributor? WordDistributor => null;

    public Task<LyricLookupReport> LookupAsync(LyricQuery query, LyricSourceSnapshot options,
        Func<LyricLookupReport, ValueTask>? onUpdate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        if (_pins.TryGetValue(query.RecordingKey, out var pinned))
            return Task.FromResult(new LyricLookupReport { Result = pinned, Pinned = true, Cached = true, Revision = 1 });
        return Task.FromResult(Song(query.Title) is { } song
            ? new LyricLookupReport { Result = song.Lyrics, Revision = 1 }
            : new LyricLookupReport());
    }

    public Task<LyricLookupReport> SearchAsync(LyricQuery query, LyricSourceSnapshot options,
        Func<LyricLookupReport, ValueTask>? onUpdate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        var states = options.Enabled.ToDictionary(id => id, _ => LyricLookup.NoUsableMatch);
        var song = Song(query.Title);
        var screened = LyricRanking.Screen(song is null ? [] : Candidates(song), query, options);
        var best = screened.Accepted.FirstOrDefault();
        // A saved choice still shows; the search only lists the alternatives (as LyricLookup does with force).
        var pinned = _pins.TryGetValue(query.RecordingKey, out var pin) ? pin : null;
        return Task.FromResult(new LyricLookupReport
        {
            Result = pinned ?? best?.Result,
            Pinned = pinned is not null,
            Candidates = screened.Accepted,
            Rejected = screened.Rejected,
            States = LyricRanking.Annotate(states, screened, pinned is null ? best?.Source : null, query),
            Revision = pinned is not null || best is not null ? 1 : 0,
        });
    }

    public bool Pin(LyricQuery query, LyricResult result)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(result);
        _pins[query.RecordingKey] = result;
        return true;
    }

    public void Unpin(LyricQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        _pins.TryRemove(query.RecordingKey, out _);
    }

    private DemoSong? Song(string title) =>
        _songs.FirstOrDefault(song => string.Equals(song.Title, title.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The canned matches for <paramref name="song"/>: invented, like the songs.</summary>
    private static IEnumerable<LyricCandidate> Candidates(DemoSong song)
    {
        var lines = ((LyricResult.Synced)song.Lyrics).Lines;
        yield return Candidate(LyricSourceId.Lrclib, "LRCLIB", lines, song.Duration);
        // The same words timed by line only.
        LyricLine[] byLine = [.. lines.Select(line => line with { Words = [], WordTimed = false })];
        yield return Candidate(LyricSourceId.NetEase, "NetEase", byLine, song.Duration);
        // A much longer recording of the same song: the recording check turns it away, with its reason.
        yield return Candidate(LyricSourceId.Kugou, "Kugou", byLine, 238);

        LyricCandidate Candidate(LyricSourceId source, string label, IReadOnlyList<LyricLine> timed, double duration) =>
            new(source, new LyricResult.Synced(timed, new LyricProvenance(LyricTimingRules.Precision(timed), label)),
                song.Title, song.Artist, duration, 1);
    }
}
