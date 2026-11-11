using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Core.Lyrics.Storage;

namespace Lunalyr.Core.Tests.Lyrics.Ranking;

/// <summary>Small builders for lyric fixtures, so ranking tests need no parser.</summary>
internal static class L
{
    /// <summary>
    /// A line-timed line, as an LRC row would give it: trimmed text, and an empty or note-only row is an instrumental
    /// break. Estimated words are left out; ranking never reads them.
    /// </summary>
    public static LyricLine Line(string text, double start, double end)
    {
        var cleaned = text.Trim();
        var instrumental = cleaned.All(c => "♪♫•·-–—".Contains(c) || char.IsWhiteSpace(c));
        return new LyricLine { Start = start, End = end, Text = instrumental ? "" : cleaned, IsInstrumental = instrumental };
    }

    public static LyricWord Word(string text, double start, double end) => new(text, start, end);

    /// <summary>A line with its own word timing.</summary>
    public static LyricLine Timed(LyricLine line, params LyricWord[] words) => line with { Words = words, WordTimed = true };

    public static LyricProvenance Provenance(string precision, string source) => new(precision, source);

    public static LyricResult.Synced Synced(IReadOnlyList<LyricLine> lines, string precision, string source) =>
        new(lines, new LyricProvenance(precision, source));

    public static LyricResult.Plain Plain(IReadOnlyList<string> lines, string precision, string source) =>
        new(lines, new LyricProvenance(precision, source));

    public static LyricSourceSnapshot Options(LyricMatchMode mode, params LyricSourceId[] enabled) => new(enabled, mode);

    /// <summary>The source label of a result's provenance.</summary>
    public static string? SourceOf(LyricResult? result) => result?.Provenance.Source;
}

/// <summary>A temporary folder removed at the end of the test.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LunalyrTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Sub(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// A <see cref="TimeProvider"/> whose timers fire only when the test (or a lookup's idle hook) moves the clock.
/// Callbacks run synchronously on the thread that moves it.
/// </summary>
internal sealed class FakeClock : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<FakeTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 11, 12, 20, 0, 0, TimeSpan.Zero);
    private long _sequence;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _now.UtcTicks;
        }
    }

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock to the earliest pending timer and fires it. False when no timer is pending.</summary>
    public bool AdvanceToNextTimer()
    {
        FakeTimer? next;
        lock (_gate)
        {
            next = _timers.Where(t => t.Due is not null).OrderBy(t => t.Due).ThenBy(t => t.Sequence).FirstOrDefault();
            if (next is null)
            {
                return false;
            }
            if (next.Due > _now)
            {
                _now = next.Due!.Value;
            }
            next.Fired();
        }
        next.Invoke();
        return true;
    }

    /// <summary>Moves the clock forward, firing every timer that falls due on the way, in order.</summary>
    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (_gate)
        {
            target = _now + by;
        }
        while (true)
        {
            FakeTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.Due is not null && t.Due <= target).OrderBy(t => t.Due).ThenBy(t => t.Sequence).FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }
                _now = next.Due!.Value;
                next.Fired();
            }
            next.Invoke();
        }
    }

    public void AdvanceSeconds(double seconds) => Advance(TimeSpan.FromSeconds(seconds));

    private sealed class FakeTimer(FakeClock clock, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public DateTimeOffset? Due { get; private set; }

        public long Sequence { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                _period = period;
                Sequence = ++clock._sequence;
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    Due = null;
                    clock._timers.Remove(this);
                }
                else
                {
                    Due = clock._now + (dueTime < TimeSpan.Zero ? TimeSpan.Zero : dueTime);
                    if (!clock._timers.Contains(this))
                    {
                        clock._timers.Add(this);
                    }
                }
            }
            return true;
        }

        /// <summary>Called under the clock's lock when the timer fires.</summary>
        public void Fired()
        {
            if (_period == Timeout.InfiniteTimeSpan || _period <= TimeSpan.Zero)
            {
                Due = null;
                clock._timers.Remove(this);
            }
            else
            {
                Due += _period;
            }
        }

        public void Invoke() => callback(state);

        public void Dispose()
        {
            lock (clock._gate)
            {
                Due = null;
                clock._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Holds a stub source until the test releases it, so arrival order does not depend on timers. Waiters resume on
/// the releasing thread, so a release inside a lookup's progress callback is seen before the lookup moves on.
/// </summary>
internal sealed class Gate
{
    private readonly Lock _gate = new();
    private readonly List<TaskCompletionSource<bool>> _waiters = [];
    private bool _open;

    public void Release()
    {
        List<TaskCompletionSource<bool>> waiters;
        lock (_gate)
        {
            _open = true;
            waiters = [.. _waiters];
            _waiters.Clear();
        }
        foreach (var waiter in waiters)
        {
            waiter.TrySetResult(true);
        }
    }

    /// <summary>True once released; false when <paramref name="cancellationToken"/> fires first.</summary>
    public Task<bool> WaitAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool> waiter;
        lock (_gate)
        {
            if (_open)
            {
                return Task.FromResult(true);
            }
            waiter = new TaskCompletionSource<bool>();
            _waiters.Add(waiter);
        }
        cancellationToken.Register(() => waiter.TrySetResult(false));
        return waiter.Task;
    }
}

/// <summary>Records what stub sources were asked, which saw cancellation, and every progress update.</summary>
internal sealed class LookupLog
{
    private readonly Lock _gate = new();
    private readonly List<LyricSourceId> _calls = [];
    private readonly HashSet<LyricSourceId> _cancelled = [];
    private readonly List<LyricLookupReport> _updates = [];

    public void Call(LyricSourceId id)
    {
        lock (_gate)
        {
            _calls.Add(id);
        }
    }

    public void Cancel(LyricSourceId id)
    {
        lock (_gate)
        {
            _cancelled.Add(id);
        }
    }

    public ValueTask Update(LyricLookupReport report)
    {
        lock (_gate)
        {
            _updates.Add(report);
        }
        return ValueTask.CompletedTask;
    }

    public IReadOnlyList<LyricSourceId> Calls
    {
        get
        {
            lock (_gate)
            {
                return [.. _calls];
            }
        }
    }

    public IReadOnlySet<LyricSourceId> Cancelled
    {
        get
        {
            lock (_gate)
            {
                return new HashSet<LyricSourceId>(_cancelled);
            }
        }
    }

    public IReadOnlyList<LyricLookupReport> Updates
    {
        get
        {
            lock (_gate)
            {
                return [.. _updates];
            }
        }
    }

    public int Count(LyricSourceId id) => Calls.Count(c => c == id);

    public LyricLookupReport? Shown(int revision) => Updates.FirstOrDefault(u => u.Revision == revision);
}

internal sealed class FakeConnectivity(bool online = true) : ILyricConnectivity
{
    public bool IsOnline { get; set; } = online;

    public event EventHandler? Restored;

    public void Restore()
    {
        IsOnline = true;
        Restored?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>Builds lookups on a fake clock, whose idle hook moves the clock to the next timer.</summary>
internal static class Lookups
{
    /// <summary>The Mac tests' fast timing: grace 0.2 s, settle 0.1 s, Apple retry 0.3 s, limits 3 s.</summary>
    public static LyricLookupTiming Fast { get; } = new()
    {
        Grace = TimeSpan.FromSeconds(0.2),
        UpgradeSettle = TimeSpan.FromSeconds(0.1),
        AppleRetry = TimeSpan.FromSeconds(0.3),
        LrclibLimit = TimeSpan.FromSeconds(3),
        SourceLimit = TimeSpan.FromSeconds(3),
    };

    public static LyricLookup Create(string cacheFolder, LyricSourceFetch fetch, FakeClock clock, LyricLookupTiming? timing = null,
        LyricMissCache? misses = null, bool online = true, Func<LyricQuery, string, string?>? localFingerprint = null)
    {
        var lookup = new LyricLookup(new SavedLyrics(cacheFolder, clock), fetch, new FakeConnectivity(online), timing ?? Fast,
            misses ?? new LyricMissCache(timeProvider: clock), clock, localFingerprint)
        {
            WhenIdle = () => clock.AdvanceToNextTimer(),
        };
        return lookup;
    }
}
