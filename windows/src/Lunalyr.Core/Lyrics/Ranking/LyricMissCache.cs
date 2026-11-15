namespace Lunalyr.Core.Lyrics.Ranking;

/// <summary>What recently answered without an acceptable match for a lookup key.</summary>
/// <param name="Sources">The sources that answered.</param>
/// <param name="Rejected">Candidates among those answers that failed the recording check, so they can still be listed (and chosen).</param>
public sealed record LyricMissRecord(IReadOnlySet<LyricSourceId> Sources, IReadOnlyList<LyricCandidate> Rejected);

/// <summary>
/// Recent "no lyrics" answers, so replays within a few minutes do not ask every service again. Kept in memory per
/// lookup key (recording key and options fingerprint) and per source: a source that failed or timed out is asked
/// again next time, and nothing is recorded while the connection is down. Each source's answer expires on its own
/// clock, so replays never extend an older one. Thread-safe.
/// </summary>
public sealed class LyricMissCache
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Dictionary<LyricSourceId, Answer>> _misses = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    private readonly record struct Answer(DateTimeOffset At, LyricCandidate? Rejected);

    public LyricMissCache(TimeSpan? lifetime = null, TimeProvider? timeProvider = null)
    {
        Lifetime = lifetime ?? TimeSpan.FromMinutes(10);
        _time = timeProvider ?? TimeProvider.System;
    }

    public TimeSpan Lifetime { get; }

    /// <summary>Sources that recently answered that they have nothing usable for <paramref name="key"/>.</summary>
    public LyricMissRecord Answered(string key, DateTimeOffset? now = null)
    {
        var at = now ?? _time.GetUtcNow();
        lock (_gate)
        {
            if (!_misses.TryGetValue(key, out var answers))
            {
                return new LyricMissRecord(new HashSet<LyricSourceId>(), []);
            }
            var live = Live(answers, at);
            if (live.Count == 0)
            {
                _misses.Remove(key);
            }
            else
            {
                _misses[key] = live;
            }
            return new LyricMissRecord(new HashSet<LyricSourceId>(live.Keys),
                [.. live.OrderBy(pair => pair.Key).Select(pair => pair.Value.Rejected).OfType<LyricCandidate>()]);
        }
    }

    /// <summary>
    /// Records the sources that just answered without an acceptable match. <paramref name="rejected"/> holds what any
    /// of them offered that failed the check. Sources already recorded and not passed keep their own time.
    /// </summary>
    public void Insert(string key, IEnumerable<LyricSourceId> sources, IEnumerable<LyricCandidate>? rejected = null, DateTimeOffset? now = null)
    {
        var at = now ?? _time.GetUtcNow();
        var offered = rejected?.ToList() ?? [];
        lock (_gate)
        {
            foreach (var stale in _misses.Keys.ToList())
            {
                var live = Live(_misses[stale], at);
                if (live.Count == 0)
                {
                    _misses.Remove(stale);
                }
                else
                {
                    _misses[stale] = live;
                }
            }
            var ids = sources.ToList();
            if (ids.Count == 0)
            {
                return;
            }
            if (!_misses.TryGetValue(key, out var answers))
            {
                answers = [];
                _misses[key] = answers;
            }
            foreach (var id in ids)
            {
                answers[id] = new Answer(at, offered.FirstOrDefault(c => c.Source == id));
            }
        }
    }

    public void Remove(string key)
    {
        lock (_gate)
        {
            _misses.Remove(key);
        }
    }

    private Dictionary<LyricSourceId, Answer> Live(Dictionary<LyricSourceId, Answer> answers, DateTimeOffset now) =>
        answers.Where(pair => now - pair.Value.At < Lifetime).ToDictionary(pair => pair.Key, pair => pair.Value);
}
