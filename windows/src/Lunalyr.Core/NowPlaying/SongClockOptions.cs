namespace Lunalyr.Core.NowPlaying;

/// <summary>How well a player keeps its timeline up to date, as learned by <see cref="SongClock"/>.</summary>
public enum TimelineQuality
{
    /// <summary>Not enough evidence yet (a new session, or nothing has played for long enough).</summary>
    Unknown = 0,

    /// <summary>The player sends timeline updates every few seconds while playing.</summary>
    Live = 1,

    /// <summary>
    /// The player only updates its timeline on seek, pause/resume or track change (several browsers do). Silence
    /// while playing is normal for it, so the clock keeps extrapolating instead of treating it as a stall.
    /// </summary>
    SeekOnly = 2,
}

/// <summary>Tuning for <see cref="SongClock"/>. The defaults are explained on each property.</summary>
public sealed record SongClockOptions
{
    public static SongClockOptions Default { get; } = new();

    /// <summary>
    /// A snapshot that disagrees with the clock by more than this is a seek: the clock jumps and raises
    /// <see cref="SongClock.SeekDetected"/>. Smaller disagreements are drift and are slewed out. 1.5 s sits above
    /// the update latency seen from GSMTC (a few hundred ms, more for browsers) yet below a typical lyric line
    /// (2–5 s), so a real seek always moves the wallpaper to the right line.
    /// </summary>
    public TimeSpan SeekThreshold { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// How long a drift correction takes. 400 ms is short enough that a word highlight lands on the beat again within
    /// a word or two, and long enough not to look like a skip.
    /// </summary>
    public TimeSpan SlewDuration { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// When the clock is ahead and has to fall back, the shown position may slow down by at most this fraction of the
    /// playback rate (0.75 = it still moves at ≥ 25 % speed), so time never runs backwards. Large backward drift
    /// therefore takes longer than <see cref="SlewDuration"/> to correct. Must be in (0, 1).
    /// </summary>
    public double MaxBackwardSlewFraction { get; init; } = 0.75;

    /// <summary>
    /// No timeline update for this long while playing (and no status change) marks the player
    /// <see cref="TimelineQuality.SeekOnly"/>. Players that update live do so every 1–5 s, so 6 s avoids misfiring.
    /// </summary>
    public TimeSpan SeekOnlyAfter { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>Matching mid-playback updates (each within <see cref="SeekOnlyAfter"/> of the last) needed for Live.</summary>
    public int LiveEvidenceRequired { get; init; } = 2;

    /// <summary>
    /// Timeline updates within this long of a status change or track change are reactions to that change (every
    /// player sends those), so they don't count as evidence of live updates.
    /// </summary>
    public TimeSpan StatusSettleTime { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>A snapshot counts as live evidence only if its LastUpdatedTime is at most this old when it arrives.</summary>
    public TimeSpan FreshSnapshotAge { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>A Live player that has been silent for this long while playing gets a lower confidence.</summary>
    public TimeSpan LiveStaleAfter { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// LastUpdatedTime may be up to this far in the future without comment (clock rounding); beyond it the snapshot is
    /// logged and treated as taken now.
    /// </summary>
    public TimeSpan FutureTolerance { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// A LastUpdatedTime older than this is bogus (players with no timeline report 1601-01-01) and is treated as
    /// taken now. Seek-only players legitimately report timestamps minutes old, so this is deliberately generous;
    /// a known duration bounds the result anyway.
    /// </summary>
    public TimeSpan MaxSnapshotAge { get; init; } = TimeSpan.FromHours(12);

    /// <summary>
    /// A snapshot whose duration differs from the current one by more than this belongs to a different track (it
    /// arrived before the metadata change), so the clock re-syncs without raising a seek.
    /// </summary>
    public TimeSpan DurationChangeTolerance { get; init; } = TimeSpan.FromSeconds(1);

    internal void Validate()
    {
        Positive(SeekThreshold, nameof(SeekThreshold));
        Positive(SlewDuration, nameof(SlewDuration));
        Positive(SeekOnlyAfter, nameof(SeekOnlyAfter));
        Positive(LiveStaleAfter, nameof(LiveStaleAfter));
        Positive(MaxSnapshotAge, nameof(MaxSnapshotAge));
        NonNegative(StatusSettleTime, nameof(StatusSettleTime));
        NonNegative(FreshSnapshotAge, nameof(FreshSnapshotAge));
        NonNegative(FutureTolerance, nameof(FutureTolerance));
        NonNegative(DurationChangeTolerance, nameof(DurationChangeTolerance));
        if (!(MaxBackwardSlewFraction > 0 && MaxBackwardSlewFraction < 1))
            throw new ArgumentOutOfRangeException(nameof(MaxBackwardSlewFraction), MaxBackwardSlewFraction, "Must be in (0, 1).");
        if (LiveEvidenceRequired < 1)
            throw new ArgumentOutOfRangeException(nameof(LiveEvidenceRequired), LiveEvidenceRequired, "Must be at least 1.");
    }

    private static void Positive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(name, value, "Must be positive.");
    }

    private static void NonNegative(TimeSpan value, string name)
    {
        if (value < TimeSpan.Zero) throw new ArgumentOutOfRangeException(name, value, "Must not be negative.");
    }
}
