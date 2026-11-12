// Ported from the Mac's Sources/Player/ScrubController.swift (ScrubController, ScrubPreview, ScrubActivity; Lunalyr's
// own code, no Verso lines). Differences: no trackpad haptics on Windows (the events still reach Feel, which the card
// uses to light the snapped tick); the held target and the key's time capsule expire by the clock instead of by a
// timer task, so nothing here schedules work; the preview and the seek are callbacks the app wires to the wallpaper and
// to the player.

using Lunalyr.Core.Lyrics.Readings;

namespace Lunalyr.Core.Player;

/// <summary>
/// The time a drag on the seek bar points at, for the wallpaper on the same display to show that moment's lyric while
/// the hand moves. Nothing is sent to the player until the release.
/// </summary>
public sealed class ScrubPreview
{
    private readonly Lock _gate = new();
    private (long Display, double SongTime)? _state;

    /// <summary>A preview only stands while a button is down, so a drag whose view went away can never leave the lyrics frozen.</summary>
    public Func<bool> ButtonsHeld { get; set; } = static () => true;

    /// <summary>The preview started, moved or ended.</summary>
    public event Action? Changed;

    public void Show(double songTime, long display)
    {
        lock (_gate)
        {
            if (_state == (display, songTime)) return;
            _state = (display, songTime);
        }

        Changed?.Invoke();
    }

    public void Clear(long display)
    {
        lock (_gate)
        {
            if (_state?.Display != display) return;
            _state = null;
        }

        Changed?.Invoke();
    }

    /// <summary>The previewed song time on <paramref name="display"/>, if a drag is in flight there.</summary>
    public double? SongTime(long? display)
    {
        (long Display, double SongTime)? state;
        lock (_gate) state = _state;
        if (state is not { } s || display != s.Display || !ButtonsHeld()) return null;
        return s.SongTime;
    }

    /// <summary>The previewed song time on any display (the Windows wallpaper has one clock for all of them).</summary>
    public double? AnySongTime()
    {
        (long Display, double SongTime)? state;
        lock (_gate) state = _state;
        return state is { } s && ButtonsHeld() ? s.SongTime : null;
    }
}

/// <summary>
/// One seek bar's live state: hover, the press in flight, what was just committed, and the keyboard's brief time
/// capsule. Called on the UI thread only.
/// </summary>
public sealed class ScrubController
{
    /// <summary>How long a key's time capsule stays up.</summary>
    public const double FlashSeconds = 1.0;

    /// <summary>What the bar is drawn against: the song and its lyric structure.</summary>
    public sealed record Context
    {
        public static Context Empty { get; } = new();

        public Context()
        {
        }

        public Context(double duration, string trackKey, ScrubLine[] lines, bool canSeek,
            RomanizationDisplay romanization = RomanizationDisplay.Off)
        {
            Duration = duration;
            TrackKey = trackKey;
            Lines = lines;
            CanSeek = canSeek;
            Romanization = romanization;
            Targets = lines.Length == 0 ? [] : ScrubMath.SnapTargets(lines, duration);
            Breaks = ScrubMath.SectionBreaks(lines, duration);
            Ticks = ScrubMath.Ticks(Targets, duration);
            Repeated = ScrubMath.RepeatedSpans(lines, duration);
        }

        public double Duration { get; init; }
        public string TrackKey { get; init; } = "";
        public ScrubLine[] Lines { get; init; } = [];
        public double[] Targets { get; init; } = [];
        public double[] Breaks { get; init; } = [];
        public double[] Ticks { get; init; } = [];

        /// <summary>Lines that come back (often the chorus), lightened on hover.</summary>
        public (double Start, double End)[] Repeated { get; init; } = [];

        public bool CanSeek { get; init; }
        public RomanizationDisplay Romanization { get; init; }

        public bool HasLyrics => Targets.Length > 1;

        public double PointsPerSecond => Duration > 0 ? ScrubMath.BarWidth / Duration : 0;

        /// <summary>Whether two contexts draw the same bar (the line arrays compared by content).</summary>
        public bool SameAs(Context other) =>
            Duration == other.Duration && TrackKey == other.TrackKey && CanSeek == other.CanSeek
            && Romanization == other.Romanization && Lines.AsSpan().SequenceEqual(other.Lines);
    }

    public readonly record struct Hover(double X, double Y, double Time, int? Snapped);

    /// <summary>A committed seek (or skip) the bar keeps showing for a moment.</summary>
    public readonly record struct Held(double Time, double Since, double HoldFor);

    private readonly Func<double> _now;
    private (double X, double Time)? _hoverLast;
    private double _hoverSpeed;
    private double? _flash;
    private double _flashSince;

    public ScrubController(long display, Action<double> seek, Func<double> playback, Func<double> now,
        PlayerInteraction? interaction = null, ScrubPreview? preview = null)
    {
        Display = display;
        Seek = seek;
        Playback = playback;
        _now = now;
        Interaction = interaction ?? new PlayerInteraction();
        Preview = preview;
    }

    public long Display { get; }

    /// <summary>Sends one seek to the player (song seconds). The app's goes through the playback guard.</summary>
    public Action<double> Seek { get; set; }

    /// <summary>The player's position now, in song seconds.</summary>
    public Func<double> Playback { get; set; }

    /// <summary>What a move was worth feeling (snapped, reached an end): the Mac's haptics.</summary>
    public Action<ScrubEvents>? Feel { get; set; }

    public PlayerInteraction Interaction { get; }

    /// <summary>Where the drag's time goes for the wallpaper; null in tests that don't look.</summary>
    public ScrubPreview? Preview { get; }

    public Context Current { get; private set; } = Context.Empty;

    public Hover? HoverState { get; private set; }

    public ScrubSession? Session { get; private set; }

    public Held? HeldTarget { get; private set; }

    /// <summary>Bumped whenever something the bar draws changed (hover, the drag, a commit, a key's capsule).</summary>
    public int Version { get; private set; }

    public bool IsDragging => Session is not null;

    /// <summary>The time capsule shown for a moment after a key press, or null.</summary>
    public double? Flash => _flash is { } f && _now() - _flashSince < FlashSeconds ? f : null;

    public bool Hovering => HoverState is not null;

    public bool Flashing => Flash is not null;

    /// <summary>The time (and lyric) capsules are over the song's titles.</summary>
    public bool CapsulesUp => Hovering || IsDragging || Flashing;

    // ---- Context ----

    public void Update(Context next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (next.SameAs(Current)) return;
        if (Session is { } session && (session.TrackKey != next.TrackKey || !next.CanSeek))
        {
            // A new song under the hand: never seek it to the old song's place.
            End(commit: false);
        }

        if (next.TrackKey != Current.TrackKey)
        {
            HeldTarget = null;
            HoverState = null;
        }

        Current = next;
        Version++;
    }

    /// <summary>The time the bar shows: the drag's target, a just-committed seek, or the player's clock.</summary>
    public double DisplayedTime(double playback)
    {
        if (Session is { } session) return session.TargetTime;
        if (HeldTarget is not { } held) return playback;
        return ScrubMath.DisplayedTime(playback, held.Time, held.Since, _now(), held.HoldFor);
    }

    // ---- Pointer (bar-local DIPs; y from the centre line) ----

    public void PointerMoved(double x, double y, bool noSnap = false)
    {
        if (!Current.CanSeek || Session is not null) return;
        var t = _now();
        if (_hoverLast is { } last && t - last.Time > 0.001)
            _hoverSpeed = _hoverSpeed * 0.5 + Math.Abs(x - last.X) / (t - last.Time) * 0.5;
        _hoverLast = (x, t);
        var raw = ScrubMath.Time(x, Current.Duration);
        var snapped = ScrubMath.Snap(raw, Current.Targets, Current.PointsPerSecond, speed: _hoverSpeed, disabled: noSnap);
        var time = snapped is { } i ? Current.Targets[i] : raw;
        var next = new Hover(Math.Clamp(x, 0, ScrubMath.BarWidth), y, time, snapped);
        if (HoverState != next)
        {
            HoverState = next;
            Version++;
        }
    }

    public void PointerExited()
    {
        _hoverLast = null;
        _hoverSpeed = 0;
        if (HoverState is null) return;
        HoverState = null;
        Version++;
    }

    public void MouseDown(double x, double y, bool fine = false, bool noSnap = false)
    {
        if (!Current.CanSeek || !(Current.Duration > 0)) return;
        var t = _now();
        var knob = ScrubMath.X(DisplayedTime(Playback()), Current.Duration);
        var next = new ScrubSession(x, y, knob, Current.Duration, Current.TrackKey, Current.Targets, t, noSnap: noSnap);
        if (fine) next.ModifiersChanged(fine: true, noSnap, t);
        HoverState = null;
        Interaction.Dragging = true;
        Interaction.NoteClick(t);
        Session = next;
        Version++;
        ShowPreview();
    }

    public void MouseDragged(double x, double y, bool fine = false, bool noSnap = false)
    {
        if (Session is not { } session) return;
        Felt(session.Move(x, y, _now(), fine, noSnap));
        Version++;
        ShowPreview();
    }

    public void ModifiersChanged(bool fine, bool noSnap)
    {
        if (Session is not { } session) return;
        Felt(session.ModifiersChanged(fine, noSnap, _now()));
        Version++;
        ShowPreview();
    }

    public void MouseUp(double x, double y, bool fine = false, bool noSnap = false)
    {
        if (Session is null) return;
        MouseDragged(x, y, fine, noSnap);
        End(commit: true);
        PointerMoved(x, y, noSnap);
    }

    /// <summary>Esc: the knob springs back to the song and nothing is sent.</summary>
    public void Cancel()
    {
        if (Session is null) return;
        End(commit: false);
    }

    private void End(bool commit)
    {
        if (Session is not { } finished) return;
        Interaction.Dragging = false;
        Preview?.Clear(Display);
        var t = _now();
        Session = null;
        if (commit && finished.Commit(t) is { } target)
        {
            Hold(target, 0.6);
            Seek(target);
        }

        Interaction.NoteClick(t);
        Version++;
    }

    // ---- Keyboard and screen readers ----

    /// <summary>A seek from a key or a screen reader: sent at once, held on the bar, and the time shown in a capsule for a second.</summary>
    public void SeekNow(double target, bool flash = true)
    {
        if (!Current.CanSeek || !(Current.Duration > 0) || Session is not null) return;
        var clamped = ScrubMath.CommitTarget(target, Current.Duration);
        Hold(clamped, 0.6);
        Seek(clamped);
        var t = _now();
        Interaction.NoteClick(t);
        if (flash)
        {
            _flash = clamped;
            _flashSince = t;
        }

        Version++;
    }

    public double CurrentTime => DisplayedTime(Playback());

    public void SeekBy(double step) => SeekNow(CurrentTime + step);

    /// <summary>Alt+← / Alt+→ and a screen reader's adjust: by lyric line, else by <paramref name="fallback"/> seconds.</summary>
    public void SeekLine(bool forward, double fallback = PlayerKeyMap.LineFallbackStep)
    {
        var time = CurrentTime;
        if (Current.HasLyrics)
        {
            if (ScrubMath.AdjacentLine(time, Current.Targets, forward) is { } start)
                SeekNow(ScrubMath.CommitTarget(start, Current.Duration, start));
        }
        else
        {
            SeekBy(forward ? fallback : -fallback);
        }
    }

    public void Jump(int digit)
    {
        if (ScrubMath.PercentJump(digit, Current.Duration) is { } target) SeekNow(target);
    }

    /// <summary>After an optimistic skip: the bar shows 0:00 until the new song is in.</summary>
    public void ShowStart(double seconds = 1.5) => Hold(0, seconds);

    /// <summary>The player refused the seek: the bar goes back to its clock at once.</summary>
    public void Release()
    {
        if (HeldTarget is null) return;
        HeldTarget = null;
        Version++;
    }

    // ---- Private ----

    private void Hold(double time, double seconds) => HeldTarget = new Held(time, _now(), seconds);

    private void Felt(ScrubEvents events)
    {
        if (events != ScrubEvents.None) Feel?.Invoke(events);
    }

    private void ShowPreview()
    {
        if (Preview is { } preview && Session is { } session) preview.Show(session.TargetTime, Display);
    }
}
