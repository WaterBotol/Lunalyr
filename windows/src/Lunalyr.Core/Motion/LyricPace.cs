namespace Lunalyr.Core.Motion;

/// <summary>What to show once a track turns out to have no lyrics.</summary>
public enum NoLyricsDisplay
{
    /// <summary>Say so briefly.</summary>
    Notice,
    /// <summary>Show title and artist.</summary>
    TrackName,
    /// <summary>Show nothing.</summary>
    Nothing,
}

/// <summary>How long the stage's notices hold and fade, seconds.</summary>
public readonly record struct NoticeTiming(double IdleHold, double NoticeHold, double NoticeFade)
{
    /// <summary>The wallpaper's timing: idle notice 5 s, "no lyrics" 4 s, both fading over 1 s.</summary>
    public static NoticeTiming Default => new(WallpaperMotion.IdleNoticeHold, WallpaperMotion.NoticeHold, WallpaperMotion.NoticeFade);
}

/// <summary>
/// How fast the lyric stage's own frame clock needs to run (the Mac's <c>LyricPace</c>, the "lyric stage clock"). The
/// stage reports a level each frame; the level is published only when it changes and only after the frame
/// (<see cref="Flush"/>), so it never interrupts the update that measured it.
/// </summary>
/// <remarks>
/// The clock runs at the display rate only while words are entering, being sung or leaving; 24 fps in a break with
/// only slow drift; 12 when nothing changes but the float; twice a second while the song is paused; and not at all
/// once nothing on the stage can change (nothing playing, no lyrics, hidden lyrics) until an input does.
/// Use from the render (UI) thread.
/// </remarks>
public sealed class LyricPace(TimeProvider time)
{
    public enum Level
    {
        /// <summary>Something is entering, being sung, swelling or leaving.</summary>
        Active,
        /// <summary>Only slow motion: wandering interlude dots, drifting colour, scrolling unsynced lyrics.</summary>
        Ambient,
        /// <summary>Nothing visible changes beyond the slow float.</summary>
        Still,
        /// <summary>
        /// Playback is paused: nothing moves, but a seek in the player still changes the line. Looked at twice a second.
        /// </summary>
        Waiting,
        /// <summary>
        /// Nothing on the stage can change until an input does. The clock stops; any input change rebuilds the stage,
        /// which reports a new level and starts it again.
        /// </summary>
        Resting,
    }

    /// <summary>How far behind the real time a frame may be drawn, seconds.</summary>
    public const double Staleness = 0.05;

    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private Level? _pending;
    private double _lastWall = double.NegativeInfinity;

    /// <summary>The published level.</summary>
    public Level Current { get; private set; } = Level.Active;

    /// <summary>Raised by <see cref="Flush"/> when the published level changes.</summary>
    public event Action<Level>? Changed;

    /// <summary>Whether the clock is stopped.</summary>
    public bool Rests => Current == Level.Resting;

    /// <summary>
    /// Reports this frame's level. The newest report wins: one waiting to be published is replaced, not kept. Holding
    /// on to the first of two reports in one turn (a scene changing as a notice finishes) could publish Resting after
    /// an Active, and a resting clock draws no frame that would correct it. Returns true when a flush is now needed
    /// (the caller schedules one after the frame).
    /// </summary>
    public bool Report(Level next)
    {
        if (next == (_pending ?? Current)) return false;
        var scheduled = _pending is not null;
        _pending = next;
        return !scheduled;
    }

    /// <summary>Publishes the newest report, after the frame. True when the level changed.</summary>
    public bool Flush()
    {
        if (_pending is not { } pending) return false;
        _pending = null;
        if (pending == Current) return false;
        Current = pending;
        Changed?.Invoke(pending);
        return true;
    }

    /// <summary>The frame interval for the current level, never faster than <paramref name="full"/>.</summary>
    public double Interval(double full) => Current switch
    {
        Level.Active => full,
        Level.Ambient => Math.Max(full, 1.0 / 24),
        Level.Still => Math.Max(full, 1.0 / 12),
        Level.Waiting => Math.Max(full, 0.5),
        _ => Math.Max(full, 1),
    };

    /// <summary>The real wall time, seconds since the Unix epoch, from the clock this pace was given.</summary>
    public double Now => _time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;

    /// <summary>
    /// The wall time to draw a frame at. A tick is drawn at its own date, so frames step evenly. But a rebuild between
    /// ticks (any input changing) is handed the date of the last tick: up to half a second old while paused, hours
    /// while resting. A cross-fade or a hide it started would begin in the past and be mostly over by the next frame,
    /// so such a frame is drawn at the real time, give or take <see cref="Staleness"/>. Never earlier than a frame
    /// already drawn, as a rebuild and the next tick can arrive slightly out of order, unless the system clock stepped
    /// back: then the frames follow it, rather than every fade and note holding still until the old time comes round.
    /// </summary>
    /// <param name="date">The tick's date, seconds since the Unix epoch.</param>
    public double Wall(double date)
    {
        var now = Math.Max(date, Now - Staleness);
        if (now < _lastWall - 1) _lastWall = now;
        _lastWall = Math.Max(_lastWall, now);
        return _lastWall;
    }

    /// <summary>What the clock needs on a frame where no word is being sung.</summary>
    /// <param name="Kind">The scene.</param>
    /// <param name="Shown">How shown the lyrics are (hiding fades them out).</param>
    /// <param name="Target">Where that is heading: 1 while the lyrics are shown or coming back, 0 while hidden or going.</param>
    /// <param name="Playing">Whether the song is playing.</param>
    /// <param name="ColourMoves">Colour that changes by itself: drifting, music reactive, or still easing.</param>
    /// <param name="NoLyrics">What a song without lyrics shows.</param>
    /// <param name="NoticeAge">Seconds since the idle or "lyrics unavailable" notice went up.</param>
    public readonly record struct Quiet(LyricSceneKind Kind, double Shown = 1, double Target = 1, bool Playing = false,
                                        bool ColourMoves = false, NoLyricsDisplay NoLyrics = NoLyricsDisplay.Notice,
                                        double? NoticeAge = null);

    /// <summary>
    /// The level for a quiet frame, or null while a song is being sung: its words decide then. Notices hold still,
    /// fade on every frame and then need nothing; hidden lyrics and an empty stage need nothing; paused lyrics only
    /// wait for a seek unless their colour moves.
    /// </summary>
    public static Level? QuietLevel(Quiet quiet, NoticeTiming timing)
    {
        // Only hidden lyrics that have finished fading out rest. Shown again, their first frame (a millisecond into the
        // fade, presence still about a millionth) must start the clock, or nothing would ever draw the rest of the fade.
        if (quiet.Target <= 0 && quiet.Shown <= 0.001) return Level.Resting;
        if (Math.Abs(quiet.Shown - quiet.Target) > 0.001) return Level.Active;
        switch (quiet.Kind)
        {
            case LyricSceneKind.Idle:
                return Notice(quiet.NoticeAge, timing.IdleHold, timing.NoticeFade, arrival: 0.6, quiet.ColourMoves);
            case LyricSceneKind.Missing:
                return quiet.NoLyrics switch
                {
                    NoLyricsDisplay.Nothing => Level.Resting,
                    // The title and artist are set in the text colour, which may still be drifting or easing.
                    NoLyricsDisplay.TrackName => quiet.ColourMoves ? Level.Ambient : Level.Resting,
                    _ => Notice(quiet.NoticeAge, timing.NoticeHold, timing.NoticeFade, colourMoves: quiet.ColourMoves),
                };
            case LyricSceneKind.Loading:
                return Level.Still;
            case LyricSceneKind.Plain:
                return quiet.Playing || quiet.ColourMoves ? Level.Ambient : Level.Waiting;
            default:
                if (quiet.Playing) return null;
                return quiet.ColourMoves ? Level.Ambient : Level.Waiting;
        }
    }

    /// <summary>
    /// A notice that arrives, holds for <paramref name="hold"/> seconds and fades over <paramref name="fade"/>: every
    /// frame while it moves, few while it holds (more if its colour moves), none once it has gone or if it never went
    /// up.
    /// </summary>
    public static Level Notice(double? age, double hold, double fade, double arrival = 0, bool colourMoves = false)
    {
        if (age is not { } a || !(a < hold + fade + 0.05)) return Level.Resting;
        if (a < arrival || a >= hold - 0.1) return Level.Active;
        return colourMoves ? Level.Ambient : Level.Still;
    }
}

/// <summary>
/// How present the lyrics are while they are shown or hidden. Each change starts from the current presence, so
/// toggling mid-fade reverses smoothly instead of jumping to the far end first. A mutable struct: keep it in a field.
/// </summary>
public struct LyricVisibility
{
    public LyricVisibility() : this(true) { }

    public LyricVisibility(bool visible)
    {
        From = visible ? 1 : 0;
        Target = From;
        ChangedAt = double.NegativeInfinity;
    }

    public double From { get; private set; }
    public double Target { get; private set; }
    public double ChangedAt { get; private set; }

    public void Set(bool visible, double wall)
    {
        var goal = visible ? 1.0 : 0.0;
        if (goal == Target) return;
        From = Presence(wall);
        Target = goal;
        ChangedAt = wall;
    }

    /// <summary>0…1 at wall time <paramref name="wall"/>: arriving over 0.6 s, leaving over 0.45 s.</summary>
    public readonly double Presence(double wall)
    {
        if (!double.IsFinite(ChangedAt)) return Target;
        return From + (Target - From) * LyricMotion.Settle(wall - ChangedAt, Target > From ? 0.6 : 0.45);
    }
}
