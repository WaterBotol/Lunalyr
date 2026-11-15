namespace Lunalyr.Tests.Shared;

/// <summary>
/// A <see cref="TimeProvider"/> that only moves when a test says so. Monotonic time and wall-clock time advance
/// together through <see cref="Advance"/>; <see cref="ShiftWallClock"/> moves the wall clock alone (a system clock
/// change), which the monotonic clock never sees.
/// </summary>
internal sealed class ManualTimeProvider(DateTimeOffset? start = null) : TimeProvider
{
    private readonly Lock _gate = new();
    private DateTimeOffset _utcNow = start ?? new DateTimeOffset(2026, 11, 12, 20, 0, 0, TimeSpan.Zero);
    private long _timestamp = 1_000_000_000; // Arbitrary, non-zero: code must not assume timestamps start at 0.

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate) return _timestamp;
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _utcNow;
    }

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public DateTimeOffset UtcNow => GetUtcNow();

    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);
        lock (_gate)
        {
            _timestamp += by.Ticks;
            _utcNow += by;
        }
    }

    public void AdvanceSeconds(double seconds) => Advance(TimeSpan.FromSeconds(seconds));

    public void AdvanceMilliseconds(double ms) => Advance(TimeSpan.FromMilliseconds(ms));

    public void ShiftWallClock(TimeSpan by)
    {
        lock (_gate) _utcNow += by;
    }
}
