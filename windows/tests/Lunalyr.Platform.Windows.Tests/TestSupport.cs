namespace Lunalyr.Platform.Windows.Tests;

/// <summary>A fresh folder under the temp directory, deleted on dispose. Tests never touch %LOCALAPPDATA%\Lunalyr.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lunalyr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public string[] FileNames() =>
        [.. Directory.GetFiles(Path).Select(System.IO.Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase)!];

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
/// The Windows reading services for tests, built once per test process, with the Japanese dictionary expanded into a
/// folder under the temp directory (never %LOCALAPPDATA%\Lunalyr). The folder is shared between runs, so the
/// dictionary is expanded once and then mapped, as in the app.
/// </summary>
internal static class TestReadingServices
{
    public static string DictionaryFolder { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lunalyr-tests", "dictionaries");

    private static readonly Lazy<Lunalyr.Core.Lyrics.Readings.ReadingServices> Shared =
        new(() => Lunalyr.Platform.Readings.WindowsReadingServices.Create(DictionaryFolder));

    public static Lunalyr.Core.Lyrics.Readings.ReadingServices Windows => Shared.Value;
}

/// <summary>A clock tests advance by hand. Timers fire synchronously inside <see cref="Advance"/>.</summary>
/// <summary>Fake time with fake timers, for the settings and logging tests. The song-clock tests use
/// <c>Lunalyr.Tests.Shared.ManualTimeProvider</c>, which also fakes the monotonic timestamp. TODO: merge the two.</summary>
internal sealed class TimerTimeProvider(DateTimeOffset start, TimeSpan? localOffset = null) : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = start.ToUniversalTime();

    public TimerTimeProvider()
        : this(new DateTimeOffset(2026, 11, 12, 9, 0, 0, TimeSpan.Zero))
    {
    }

    public override TimeZoneInfo LocalTimeZone { get; } =
        TimeZoneInfo.CreateCustomTimeZone("test", localOffset ?? TimeSpan.Zero, "test", "test");

    public int ActiveTimerCount
    {
        get
        {
            lock (_gate)
            {
                return _timers.Count;
            }
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (_gate)
        {
            target = _now + by;
        }
        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.Due <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }
                _now = next.Due;
                _timers.Remove(next);
            }
            next.Fire();
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    private sealed class ManualTimer(TimerTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public DateTimeOffset Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
                _period = period;
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    Due = owner._now + dueTime;
                    owner._timers.Add(this);
                }
            }
            return true;
        }

        public void Fire()
        {
            if (_period != Timeout.InfiniteTimeSpan && _period > TimeSpan.Zero)
            {
                lock (owner._gate)
                {
                    Due += _period;
                    owner._timers.Add(this);
                }
            }
            callback(state);
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
