namespace Lunalyr.Core.Lyrics.Timing;

/// <summary>What Tap to sync reads about the song. The app wires the live model; tests pass their own.</summary>
/// <param name="RecordingKey">The song playing now (<c>LyricQuery.RecordingKey</c>), or null when there is none.</param>
/// <param name="LineStarts">The sung lines' starts of the lyrics on screen (<see cref="LyricTapSync.LineStarts"/>).</param>
/// <param name="IsPlaying">Whether the song is playing.</param>
/// <param name="PlaybackTime">The song position, seconds.</param>
/// <param name="GlobalOffset">The global lyric offset, seconds.</param>
public sealed record LyricTapContext(
    Func<string?> RecordingKey,
    Func<IReadOnlyList<double>> LineStarts,
    Func<bool> IsPlaying,
    Func<double> PlaybackTime,
    Func<double> GlobalOffset);

/// <summary>
/// Runs Tap to sync for lyrics, headless: every tap the UI forwards is timed against the song, and once the taps agree
/// the song's offset is saved in <see cref="SongOffsets"/> for the lyric source on screen. Undo puts back the offset
/// from before. The UI owns the panel, the keyboard and the timers: it calls <see cref="ExpireIfDue"/> about twice a
/// second while listening and dismisses a result after <see cref="DismissDelay"/>. Use from one thread (the UI's).
/// </summary>
public sealed class LyricTapSyncController
{
    public const string MenuTitle = "Tap to Sync Lyrics…";
    public const string StopTitle = "Stop Tap to Sync";
    public const string UndoTitle = "Undo Lyric Tap Sync";

    private readonly SongOffsets _offsets;
    private readonly LyricTapContext _context;
    private readonly Func<double> _now;
    private readonly Action<string, string> _notify;
    private (string Key, string? Source)? _target;

    /// <summary>The last applied tap sync, while it can still be undone.</summary>
    public sealed record AppliedSync(string RecordingKey, string? Source, double Previous, double Offset);

    /// <param name="offsets">Where offsets are saved.</param>
    /// <param name="context">The song and lyrics.</param>
    /// <param name="now">Seconds on a monotonic clock.</param>
    /// <param name="notify">Shows a note: its text and a symbol name ("hand.tap", "arrow.uturn.backward").</param>
    public LyricTapSyncController(SongOffsets offsets, LyricTapContext context, Func<double> now,
                                  Action<string, string>? notify = null)
    {
        ArgumentNullException.ThrowIfNull(offsets);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(now);
        _offsets = offsets;
        _context = context;
        _now = now;
        _notify = notify ?? ((_, _) => { });
    }

    public LyricTapSession Session { get; } = new();

    public AppliedSync? LastApplied { get; private set; }

    public bool IsListening => Session.IsListening;

    /// <summary>The menu item's title.</summary>
    public string ActionTitle => IsListening ? StopTitle : MenuTitle;

    /// <summary>Starts, or stops a session already listening (the shortcut and the menu item do both).</summary>
    public void Toggle()
    {
        if (IsListening) Cancel();
        else Start();
    }

    public void Start()
    {
        var key = _context.RecordingKey();
        var source = _offsets.ActiveSource;
        var prior = _offsets.OffsetFor(key, source);
        Session.Start(key is null ? [] : _context.LineStarts(), prior, key is not null && _context.IsPlaying(), _now());
        if (!Session.IsListening || key is null)
        {
            _target = null;
            Finish();
            return;
        }
        _target = (key, source);
    }

    /// <summary>A tap: a lyric line starts now.</summary>
    public void Tap()
    {
        if (!Session.IsListening || _target is not { } target) return;
        // Another song, a pause, or other lyrics on screen (a better source arrived): the taps would be timed against
        // lines no longer shown, and the offset saved for a source no longer in use.
        if (StopReason(target) is { } reason)
        {
            Session.Stop(reason);
            Finish();
            return;
        }
        var time = LyricTapSync.TapTime(_context.PlaybackTime(), _context.GlobalOffset());
        if (Session.Tap(time, _now()) is { } estimate)
        {
            var previous = Session.Prior;
            _offsets.Set(estimate.Offset, target.Key, target.Source);
            LastApplied = new AppliedSync(target.Key, target.Source, previous, _offsets.OffsetFor(target.Key, target.Source));
        }
        if (!Session.IsListening) Finish();
    }

    private TapSyncFailure? StopReason((string Key, string? Source) target)
    {
        if (_context.RecordingKey() != target.Key) return TapSyncFailure.SongChanged;
        if (!_context.IsPlaying()) return TapSyncFailure.NotPlaying;
        if (_offsets.ActiveSource != target.Source || !_context.LineStarts().SequenceEqual(Session.LineStarts))
            return TapSyncFailure.LyricsChanged;
        return null;
    }

    /// <summary>Esc, the menu item again, or Lunalyr lost the keyboard.</summary>
    public void Cancel()
    {
        if (!Session.IsListening) return;
        Session.Cancel();
        Finish();
    }

    /// <summary>Ends a session whose taps stopped coming.</summary>
    public void ExpireIfDue()
    {
        if (Session.Expire(_now())) Finish();
    }

    /// <summary>Whether Undo applies to the song playing now: the tap sync's offset is still the one in force for it.</summary>
    public bool CanUndo =>
        LastApplied is { } applied && !IsListening && _context.RecordingKey() == applied.RecordingKey
        && _offsets.OffsetFor(applied.RecordingKey, applied.Source) == applied.Offset;

    /// <summary>Puts back the offset from before the last tap sync.</summary>
    public void Undo()
    {
        if (!CanUndo || LastApplied is not { } applied) return;
        _offsets.Set(applied.Previous, applied.RecordingKey, applied.Source);
        LastApplied = null;
        _notify($"Tap to sync undone: lyrics back to {TimingNudge.Format(applied.Previous)}", "arrow.uturn.backward");
        Dismiss();
    }

    /// <summary>Hides the result.</summary>
    public void Dismiss() => Session.Dismiss();

    private void Finish()
    {
        switch (Session.Phase)
        {
            case TapSyncPhase.Applied:
            case TapSyncPhase.Failed { Failure: not TapSyncFailure.Cancelled }:
                _notify(Session.Message, "hand.tap");
                break;
        }
    }

    /// <summary>How long a result stays up: long enough to reach Undo when it's offered.</summary>
    public static double DismissDelay(TapSyncPhase phase) => phase is TapSyncPhase.Applied ? 8 : 3;
}
