namespace Lunalyr.Core.NowPlaying;

/// <summary>
/// Recovers the fraction of a second a player throws away when it reports its position in whole seconds.
/// </summary>
/// <remarks>
/// <para>
/// Apple Music for Windows (seen live on 13 Nov 2026) publishes its timeline about every 1.02 s with the position cut to
/// a whole second (1:29, 1:30, 1:31…) and a precise LastUpdatedTime. Taken at face value the song clock runs between
/// 0 and 1 s behind the music, half a second on average, so word-timed lyrics arrive late.
/// </para>
/// <para>
/// While playing, the true position is <c>base + elapsed × rate</c> for one unknown <c>base</c>. Each snapshot that
/// reports whole second <c>P</c> at time <c>t</c> says <c>P ≤ base + (t − t0) × rate &lt; P + 1</c>, an interval of
/// one second for <c>base</c>. Intersecting those intervals pins <c>base</c> down; because the player's period isn't
/// exactly a second, the intervals slide across each other and the intersection narrows to a few tens of milliseconds
/// within a minute. Until then the middle of the interval halves the worst error at once.
/// </para>
/// <para>
/// It switches itself on only after <see cref="WholeSnapshotsRequired"/> whole-second positions in a row while
/// playing, and off at the first position with a fraction, so players that report precise positions are untouched.
/// A pause, rate change, seek, new track or inconsistent snapshot starts the estimate again. Not thread-safe: the
/// song clock calls it under its lock.
/// </para>
/// </remarks>
public sealed class WholeSecondPhase
{
    /// <summary>Whole-second positions in a row (while playing) before positions are taken to be cut to the second.</summary>
    public const int WholeSnapshotsRequired = 3;

    // How far outside the current interval a snapshot may land before it is taken as a jump (seek, stall) and the
    // estimate restarts rather than collapsing to nothing.
    private const double Slack = 0.25;

    private int _wholeInARow;
    private bool _hasRun;
    private long _t0;
    private double _rate = 1;
    private double _low, _high; // the interval for base, in seconds

    /// <summary>True while positions are being treated as cut to whole seconds.</summary>
    public bool Active => _wholeInARow >= WholeSnapshotsRequired;

    /// <summary>How wide the remaining uncertainty is, in seconds (1 at the start of a run), or null when inactive.</summary>
    public double? Uncertainty => Active && _hasRun ? Math.Max(0, _high - _low) : null;

    /// <summary>
    /// Takes in a snapshot and returns what to add to its reported position: 0 unless whole-second positions are
    /// being corrected. <paramref name="snapshotTimestamp"/> is the monotonic time the position was true at (the
    /// snapshot's LastUpdatedTime); <paramref name="frequency"/> is the timestamp frequency.
    /// </summary>
    public TimeSpan Correct(TimeSpan reported, long snapshotTimestamp, long frequency, bool playing, double rate)
    {
        var whole = reported.Ticks % TimeSpan.TicksPerSecond == 0;
        if (!whole)
        {
            _wholeInARow = 0;
            _hasRun = false;
            return TimeSpan.Zero;
        }
        if (!playing)
        {
            // A paused position can't be pinned down (nothing moves); keep the count, start afresh when playing again.
            _hasRun = false;
            return TimeSpan.Zero;
        }

        if (_wholeInARow < WholeSnapshotsRequired) _wholeInARow++;
        var p = reported.TotalSeconds;
        if (!_hasRun || rate != _rate)
        {
            Start(p, snapshotTimestamp, rate);
        }
        else
        {
            var elapsed = (snapshotTimestamp - _t0) / (double)frequency * _rate;
            double low = p - elapsed, high = p + 1 - elapsed;
            if (high <= _low - Slack || low >= _high + Slack)
            {
                Start(p, snapshotTimestamp, rate); // a jump the clock may not have called a seek: begin again
            }
            else
            {
                var newLow = Math.Max(_low, low);
                var newHigh = Math.Min(_high, high);
                if (newHigh > newLow)
                {
                    _low = newLow;
                    _high = newHigh;
                }
                else
                {
                    // Barely inconsistent (timestamp jitter): keep the nearest edge rather than an empty interval.
                    _low = _high = (newLow + newHigh) / 2;
                }
            }
        }

        if (!Active) return TimeSpan.Zero;
        var sinceStart = (snapshotTimestamp - _t0) / (double)frequency * _rate;
        var fraction = Math.Clamp((_low + _high) / 2 + sinceStart - p, 0, 0.999);
        return TimeSpan.FromSeconds(fraction);
    }

    /// <summary>Forgets the current run (pause, seek, new track, new player).</summary>
    public void Restart() => _hasRun = false;

    private void Start(double p, long timestamp, double rate)
    {
        _t0 = timestamp;
        _rate = rate;
        _low = p;
        _high = p + 1;
        _hasRun = true;
    }
}
