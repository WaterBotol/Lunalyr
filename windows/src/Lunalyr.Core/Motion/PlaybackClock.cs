namespace Lunalyr.Core.Motion;

/// <summary>
/// Playback position between player samples (the Mac's <c>PlaybackClock</c>).
/// </summary>
/// <remarks>
/// The player is asked where it is every so often and the position is extrapolated in between. Each fresh sample
/// disagrees with the extrapolation by a few tens of milliseconds (round-trip jitter, not a seek), and jumping to it
/// made the lyric highlight twitch, sometimes backwards, on every re-sync. A small disagreement is therefore eased out
/// over a fraction of a second instead: the clock briefly runs a little fast or slow, never backwards, and lands exactly
/// on the sample. A real seek, a new track or a pause still moves at once.
/// <para>
/// Times are plain seconds on a monotonic clock supplied by the caller, so the result depends only on when it is read,
/// not on how often. On Windows, GSMTC players are followed by <c>NowPlaying.SongClock</c>, which does the same job
/// for timeline snapshots; this struct serves any source sampled by polling and keeps the Mac's behaviour and tests.
/// A mutable struct: keep it in a field.
/// </para>
/// </remarks>
public struct PlaybackClock
{
    /// <summary>A sample: the position at a time.</summary>
    public readonly record struct Anchor(double Position, double Time);

    /// <summary>How long a small correction takes to settle, seconds.</summary>
    public const double SlewDuration = 0.4;

    /// <summary>
    /// Errors at or above this are treated as a seek and applied at once. Below <see cref="SlewDuration"/>, so the eased
    /// clock always still moves forward.
    /// </summary>
    public const double SlewLimit = 0.25;

    private double _correctionOffset;
    private double _correctionStart;
    private bool _correcting;

    public Anchor? Current { get; private set; }

    public bool IsRunning { get; private set; }

    /// <summary>The position to show at <paramref name="now"/>, or null before the first sample.</summary>
    public readonly double? Position(double now)
    {
        if (Current is not { } anchor) return null;
        var basePosition = IsRunning ? anchor.Position + Math.Max(0, now - anchor.Time) : anchor.Position;
        return basePosition + RemainingCorrection(now);
    }

    /// <summary>Whether a correction is still being eased out at <paramref name="now"/>.</summary>
    public readonly bool IsSlewing(double now) => RemainingCorrection(now) != 0;

    /// <summary>
    /// Re-anchors to a sample taken at <paramref name="sampledAt"/>, applied at <paramref name="now"/>.
    /// <paramref name="continuous"/> means nothing but time passed since the previous sample: same track, playing
    /// throughout.
    /// </summary>
    public void Sync(double position, double sampledAt, bool running, double now, bool continuous)
    {
        var target = running ? position + Math.Max(0, now - sampledAt) : position;
        var correcting = false;
        double offset = 0;
        if (continuous && IsRunning && running && Position(now) is { } shown)
        {
            var error = target - shown;
            if (Math.Abs(error) < SlewLimit && error != 0)
            {
                // Start from what is on screen and ease onto the sample.
                correcting = true;
                offset = -error;
            }
        }
        Current = new Anchor(position, sampledAt);
        IsRunning = running;
        _correcting = correcting;
        _correctionOffset = offset;
        _correctionStart = now;
    }

    /// <summary>Pauses or resumes from what is on screen now.</summary>
    public void SetRunning(bool running, double now)
    {
        if (running == IsRunning) return;
        if (Position(now) is { } shown) Current = new Anchor(shown, now);
        _correcting = false;
        IsRunning = running;
    }

    public void Reset()
    {
        Current = null;
        IsRunning = false;
        _correcting = false;
    }

    private readonly double RemainingCorrection(double now)
    {
        if (!_correcting) return 0;
        var progress = (now - _correctionStart) / SlewDuration;
        if (!(progress < 1)) return 0;
        return _correctionOffset * (1 - Math.Max(0, progress));
    }
}
