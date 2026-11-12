// Ported from the Mac's Sources/Player/ScrubSession.swift (Lunalyr's own code, no Verso lines). The Mac's ⌘ (no
// snapping) is Ctrl here and its ⌥ (fine scrub) is Alt; the events are flags instead of an array, so a move allocates
// nothing.

namespace Lunalyr.Core.Player;

/// <summary>What a move on the seek bar is worth feeling (the Mac plays a trackpad haptic for each).</summary>
[Flags]
public enum ScrubEvents
{
    None = 0,

    /// <summary>The knob was pulled onto a line start.</summary>
    Snapped = 1,

    /// <summary>The knob reached the start or the end.</summary>
    ReachedEnd = 2,
}

/// <summary>
/// One press on the seek bar, from button-down to release, as plain values: where the knob is, how fast it follows
/// the hand, whether it is held by a lyric line, and what the release would seek to. Nothing here talks to a player;
/// <see cref="ScrubController"/> sends the one seek a release commits.
/// </summary>
/// <remarks>Coordinates are bar-local DIPs: x from the bar's leading edge, y from its centre line (either sign).</remarks>
public sealed class ScrubSession
{
    /// <summary>A press this close to the knob keeps its grab offset: the knob does not jump.</summary>
    public const double GrabRadius = 8;

    /// <summary>Snap events no closer together than this.</summary>
    public const double SnapHapticInterval = 0.09;

    /// <summary>Less travel than this, released this soon, is a click.</summary>
    public const double ClickTravel = 3;

    public const double ClickTime = 0.3;

    private readonly double[] _targets;
    private bool _atEnd;
    private double _lastSnapHaptic = double.NegativeInfinity;
    private double _lastX;
    private double _lastY;
    private double _lastTime;
    private bool _lastNoSnap;

    /// <summary>A press at (<paramref name="x"/>, <paramref name="y"/>) with the knob at <paramref name="knobX"/> (the play position).</summary>
    /// <param name="noSnap">The no-snap modifier is held (Ctrl; the Mac's ⌘).</param>
    public ScrubSession(double x, double y, double knobX, double duration, string trackKey, double[] targets, double now,
        double width = ScrubMath.BarWidth, bool noSnap = false)
    {
        Width = Math.Max(1, width);
        Duration = duration;
        TrackKey = trackKey;
        _targets = targets;
        StartedAt = now;
        var onKnob = Math.Abs(x - knobX) <= GrabRadius;
        GrabbedKnob = onKnob;
        GrabOffset = onKnob ? x - knobX : 0;
        Knob = onKnob ? knobX : x;
        _lastX = x;
        _lastY = y;
        _lastTime = now;
        _lastNoSnap = noSnap;
        Gain = ScrubMath.Gain(y);
        _atEnd = Clamped <= 0 || Clamped >= Width;
        // A press on the bar snaps as a slow hover would; one on the knob stays exactly where the song is.
        if (!onKnob) UpdateSnap(now, noSnap, announce: false);
    }

    public double Width { get; }

    public double Duration { get; }

    /// <summary>The song the press began on: a change of song cancels the press.</summary>
    public string TrackKey { get; }

    public IReadOnlyList<double> Targets => _targets;

    public double StartedAt { get; }

    /// <summary>Pointer x minus knob x, kept for the whole press.</summary>
    public double GrabOffset { get; }

    /// <summary>Whether the press landed on the knob.</summary>
    public bool GrabbedKnob { get; }

    /// <summary>The knob before clamping and snapping; past an end only at full speed.</summary>
    public double Knob { get; private set; }

    public double Gain { get; private set; } = 1;

    public int? Snapped { get; private set; }

    /// <summary>Smoothed horizontal pointer speed, DIP/s.</summary>
    public double Speed { get; private set; }

    public double Travel { get; private set; }

    /// <summary>Set when the knob caught up with the pointer on returning to full speed: the view animates that one move.</summary>
    public bool CaughtUp { get; private set; }

    // ---- Reading ----

    public double PointsPerSecond => Duration > 0 ? Width / Duration : 0;

    public double Clamped => Math.Min(Math.Max(Knob, 0), Width);

    public double? SnappedTime => Snapped is { } i && i >= 0 && i < _targets.Length ? _targets[i] : null;

    /// <summary>Where the knob is drawn.</summary>
    public double DisplayX => SnappedTime is { } t ? ScrubMath.X(t, Duration, Width) : Clamped;

    /// <summary>The time the capsule shows and the wallpaper previews.</summary>
    public double TargetTime => SnappedTime ?? ScrubMath.Time(Clamped, Duration, Width);

    /// <summary>Visual stretch past an end (signed), rubber-banded.</summary>
    public double Overshoot
    {
        get
        {
            if (Knob > Width) return ScrubMath.RubberBand(Knob - Width);
            if (Knob < 0) return ScrubMath.RubberBand(Knob);
            return 0;
        }
    }

    public string? RateLabel => ScrubMath.RateLabel(Gain);

    public bool IsClick(double now) => Travel < ClickTravel && now - StartedAt < ClickTime;

    /// <summary>What a release at <paramref name="now"/> seeks to, or null for nothing: a click on the knob itself is not a seek.</summary>
    public double? Commit(double now)
    {
        if (GrabbedKnob && IsClick(now)) return null;
        if (!(Duration > 0)) return null;
        return ScrubMath.CommitTarget(TargetTime, Duration, SnappedTime);
    }

    // ---- Moving ----

    /// <summary>
    /// The pointer moved to (<paramref name="x"/>, <paramref name="y"/>). Returns the events worth feeling. At full
    /// speed the knob follows the pointer 1:1 (keeping the grab offset) and may stretch past the ends; slower, it moves
    /// by the gained relative delta and stops at the ends, so changing speed never jumps.
    /// </summary>
    /// <param name="fine">The fine-scrub modifier is held (Alt; the Mac's ⌥).</param>
    /// <param name="noSnap">The no-snap modifier is held (Ctrl; the Mac's ⌘).</param>
    public ScrubEvents Move(double x, double y, double now, bool fine = false, bool noSnap = false)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) return ScrubEvents.None;
        var dx = x - _lastX;
        var dt = now - _lastTime;
        // Two events in one instant keep the old estimate: it cannot be judged.
        if (dt > 0.001) Speed = Speed * 0.5 + Math.Abs(dx) / dt * 0.5;
        Travel += Math.Abs(dx) + Math.Abs(y - _lastY);
        var newGain = ScrubMath.Gain(y, fine);
        CaughtUp = false;
        if (newGain >= 1)
        {
            var follow = x - GrabOffset;
            CaughtUp = Gain < 1 && Math.Abs(follow - Knob) > 0.5;
            Knob = follow;
        }
        else
        {
            Knob = Math.Min(Math.Max(Knob, 0), Width) + dx * newGain;
            Knob = Math.Min(Math.Max(Knob, 0), Width);
        }

        Gain = newGain;
        _lastX = x;
        _lastY = y;
        _lastTime = now;
        var events = ScrubEvents.None;
        var end = Knob <= 0 || Knob >= Width;
        if (end && !_atEnd) events |= ScrubEvents.ReachedEnd;
        _atEnd = end;
        if (UpdateSnap(now, noSnap, announce: true)) events |= ScrubEvents.Snapped;
        return events;
    }

    /// <summary>The fine-scrub or no-snap modifier went down or up without the pointer moving.</summary>
    public ScrubEvents ModifiersChanged(bool fine, bool noSnap, double now)
    {
        var newGain = ScrubMath.Gain(_lastY, fine);
        if (newGain != Gain || noSnap != _lastNoSnap)
        {
            if (newGain >= 1 && Gain < 1)
            {
                var follow = _lastX - GrabOffset;
                CaughtUp = Math.Abs(follow - Knob) > 0.5;
                Knob = follow;
            }

            Gain = newGain;
        }

        return UpdateSnap(now, noSnap, announce: true) ? ScrubEvents.Snapped : ScrubEvents.None;
    }

    // Snaps or lets go; true when a new snap should be felt.
    private bool UpdateSnap(double now, bool noSnap, bool announce)
    {
        _lastNoSnap = noSnap;
        var time = ScrubMath.Time(Clamped, Duration, Width);
        var next = ScrubMath.Snap(time, _targets, PointsPerSecond, Gain, Speed, noSnap);
        var felt = announce && next is not null && next != Snapped && now - _lastSnapHaptic >= SnapHapticInterval;
        if (felt) _lastSnapHaptic = now;
        Snapped = next;
        return felt;
    }
}
