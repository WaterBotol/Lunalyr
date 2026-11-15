namespace Lunalyr.Core.Lyrics.Timing;

/// <summary>Why a Tap to sync session ended without an offset.</summary>
public enum TapSyncFailure
{
    /// <summary>No song, or it is paused.</summary>
    NotPlaying,
    /// <summary>The song has no timed lyrics (or they haven't loaded).</summary>
    NoLyrics,
    /// <summary>The taps never agreed on one offset.</summary>
    NoAgreement,
    /// <summary>No tap came in time.</summary>
    TimedOut,
    /// <summary>Esc, or Lunalyr lost the keyboard.</summary>
    Cancelled,
    /// <summary>Another song started mid-session.</summary>
    SongChanged,
    /// <summary>Other lyrics (another source) replaced the ones being tapped.</summary>
    LyricsChanged,
}

/// <summary>Where a Tap to sync session is.</summary>
public abstract record TapSyncPhase
{
    private TapSyncPhase() { }

    public static TapSyncPhase Idle { get; } = new IdlePhase();
    public static TapSyncPhase Listening { get; } = new ListeningPhase();

    public sealed record IdlePhase : TapSyncPhase;

    public sealed record ListeningPhase : TapSyncPhase;

    /// <summary>The offset was set; <paramref name="Previous"/> is what Undo goes back to.</summary>
    public sealed record Applied(double Offset, double Previous, int Used, int Total) : TapSyncPhase;

    public sealed record Failed(TapSyncFailure Failure) : TapSyncPhase;
}

/// <summary>
/// One Tap to sync session, start to finish, without timers or windows: the controller feeds it taps and the clock
/// (seconds on any monotonic clock).
/// </summary>
public sealed class LyricTapSession
{
    private readonly List<double> _taps = [];
    private double[] _lineStarts = [];

    public TapSyncPhase Phase { get; private set; } = TapSyncPhase.Idle;
    public IReadOnlyList<double> Taps => _taps;
    public IReadOnlyList<double> LineStarts => _lineStarts;
    public double Prior { get; private set; }
    public double StartedAt { get; private set; }
    public double? LastTapAt { get; private set; }

    public bool IsListening => Phase is TapSyncPhase.ListeningPhase;

    /// <summary>Starts listening, or says why it can't.</summary>
    public void Start(IReadOnlyList<double> lineStarts, double prior, bool playing, double now)
    {
        ArgumentNullException.ThrowIfNull(lineStarts);
        _taps.Clear();
        LastTapAt = null;
        _lineStarts = [];
        if (!playing) { Phase = new TapSyncPhase.Failed(TapSyncFailure.NotPlaying); return; }
        if (lineStarts.Count == 0) { Phase = new TapSyncPhase.Failed(TapSyncFailure.NoLyrics); return; }
        _lineStarts = [.. lineStarts];
        Prior = prior;
        StartedAt = now;
        Phase = TapSyncPhase.Listening;
    }

    /// <summary>
    /// A tap at lyric time <paramref name="tapTime"/> (<see cref="LyricTapSync.TapTime"/>), at clock time
    /// <paramref name="now"/>. Returns the estimate once the taps agree; the phase is then
    /// <see cref="TapSyncPhase.Applied"/>, and the caller saves the offset.
    /// </summary>
    public LyricTapSync.Estimate? Tap(double tapTime, double now)
    {
        if (!IsListening) return null;
        _taps.Add(tapTime);
        LastTapAt = now;
        if (LyricTapSync.EstimateOffset(_taps, _lineStarts, Prior) is { } estimate)
        {
            Phase = new TapSyncPhase.Applied(estimate.Offset, Prior, estimate.Used, estimate.Total);
            return estimate;
        }
        if (_taps.Count >= LyricTapSync.MaximumTaps) Phase = new TapSyncPhase.Failed(TapSyncFailure.NoAgreement);
        return null;
    }

    /// <summary>Ends a session that has waited too long for a tap. True if it ended.</summary>
    public bool Expire(double now)
    {
        if (!IsListening || Deadline is not { } deadline || now < deadline) return false;
        Phase = new TapSyncPhase.Failed(_taps.Count >= LyricTapSync.RequiredTaps ? TapSyncFailure.NoAgreement : TapSyncFailure.TimedOut);
        return true;
    }

    /// <summary>When the session gives up without another tap; null unless listening.</summary>
    public double? Deadline
    {
        get
        {
            if (!IsListening) return null;
            return LastTapAt is { } last ? last + LyricTapSync.NextTapLimit : StartedAt + LyricTapSync.FirstTapLimit;
        }
    }

    public void Cancel() => Stop(TapSyncFailure.Cancelled);

    /// <summary>Ends a listening session for <paramref name="reason"/> (the song paused or changed, other lyrics arrived).</summary>
    public void Stop(TapSyncFailure reason)
    {
        if (!IsListening) return;
        Phase = new TapSyncPhase.Failed(reason);
    }

    /// <summary>Back to idle (the confirmation has been seen).</summary>
    public void Dismiss()
    {
        if (IsListening) return;
        Phase = TapSyncPhase.Idle;
    }

    /// <summary>The line on the on-screen panel.</summary>
    public string Message => Phase switch
    {
        TapSyncPhase.IdlePhase => "",
        TapSyncPhase.ListeningPhase => ListeningMessage(),
        TapSyncPhase.Applied a =>
            $"Lyrics synced: {TimingNudge.Format(a.Offset)} for this song{(a.Used == a.Total ? "" : $" ({a.Used} of {a.Total} taps agreed)")}.",
        // Refused at the start, or the song paused mid-session.
        TapSyncPhase.Failed { Failure: TapSyncFailure.NotPlaying } => _lineStarts.Length == 0
            ? "Play a song first: the taps are timed against it."
            : "The song paused, so Tap to sync stopped. The timing is unchanged.",
        TapSyncPhase.Failed { Failure: TapSyncFailure.SongChanged } =>
            "The song changed, so Tap to sync stopped. The timing is unchanged.",
        TapSyncPhase.Failed { Failure: TapSyncFailure.LyricsChanged } =>
            "Other lyrics loaded, so Tap to sync stopped. Start again to time these.",
        TapSyncPhase.Failed { Failure: TapSyncFailure.NoLyrics } =>
            "Tap to sync needs this song’s timed lyrics, and they haven’t loaded.",
        TapSyncPhase.Failed { Failure: TapSyncFailure.NoAgreement } =>
            "The taps didn’t line up with the lyrics, so the timing is unchanged. Try again.",
        TapSyncPhase.Failed { Failure: TapSyncFailure.TimedOut } => "No taps, so the timing is unchanged.",
        TapSyncPhase.Failed { Failure: TapSyncFailure.Cancelled } => "Tap to sync stopped. The timing is unchanged.",
        _ => "",
    };

    private string ListeningMessage()
    {
        var left = Math.Max(0, LyricTapSync.RequiredTaps - _taps.Count);
        if (_taps.Count == 0) return "Press any key each time a lyric line starts. Esc stops.";
        if (left > 0) return $"{_taps.Count} tapped, {left} more. Esc stops.";
        var more = Math.Max(0, LyricTapSync.MaximumTaps - _taps.Count);
        return $"{_taps.Count} tapped. Keep going: the taps don’t agree yet (up to {more} more).";
    }
}
