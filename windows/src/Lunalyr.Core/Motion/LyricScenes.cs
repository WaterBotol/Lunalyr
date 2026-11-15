using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Timing;

namespace Lunalyr.Core.Motion;

/// <summary>
/// Playback position as the lyrics see it. Players report where they are only now and then, each report with tens of
/// milliseconds of error; used raw, every re-anchor jumped every word mid-entrance. This follows the reported position
/// by bending the rate a few per cent instead (<see cref="LyricClockState"/>), and adopts a real jump (a seek, a new
/// song, a large offset change) at once, reporting it so the lyric layer can cross-fade rather than cut.
/// Every display evaluates it; a repeated or slightly older wall time returns the current value unchanged.
/// </summary>
public sealed class LyricClock
{
    /// <summary>A jump the clock adopted at once.</summary>
    public readonly record struct Jump(double From, double To, double Wall);

    private LyricClockState _state;

    public Jump? LastJump { get; private set; }

    /// <summary>The lyric time at wall time <paramref name="wall"/>, given the player's raw position.</summary>
    public double Time(double wall, double raw, bool playing)
    {
        var before = _state.Valid ? _state.Time : raw;
        if (_state.Advance(wall, raw, playing)) LastJump = new Jump(before, _state.Time, wall);
        return _state.Time;
    }
}

/// <summary>What kind of scene the lyric stage shows.</summary>
public enum LyricSceneKind
{
    Idle,
    Loading,
    Plain,
    Missing,
    Synced,
}

/// <summary>What the lyric layer is showing. Compared frame to frame, so a change of song or lyric source, or a seek,
/// cross-fades instead of cutting.</summary>
/// <param name="Kind">The kind of scene.</param>
/// <param name="Track">A hash of title and artist.</param>
/// <param name="Fingerprint">A hash of the lyrics (<see cref="FingerprintOf"/>).</param>
public readonly record struct LyricSceneKey(LyricSceneKind Kind, int Track, int Fingerprint)
{
    public static int FingerprintOf(IReadOnlyList<LyricLine> lines, IReadOnlyList<string> plain)
    {
        var hash = new HashCode();
        hash.Add(lines.Count);
        hash.Add(plain.Count);
        if (lines.Count > 0)
        {
            hash.Add(lines[0].Start);
            hash.Add(lines[0].Text);
            hash.Add(lines[^1].End);
            hash.Add(lines[^1].Text);
        }
        hash.Add(plain.Count > 0 ? plain[0] : null);
        return hash.ToHashCode();
    }
}

/// <summary>The data one lyric scene is drawn from, captured so an outgoing scene can finish fading after the model has
/// moved on. Immutable; build one per change, not per frame.</summary>
public sealed record LyricSceneContent
{
    public IReadOnlyList<LyricLine> Lines { get; init; } = [];
    public IReadOnlyList<string> PlainLines { get; init; } = [];
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    /// <summary>Wall time the "no lyrics" notice went up, if it did.</summary>
    public double? NotFoundAt { get; init; }
    /// <summary>Wall time playback stopped, for the idle notice.</summary>
    public double? IdleSince { get; init; }
    public double Duration { get; init; }
    /// <summary>The song's seed, for its credits: an outgoing scene keeps its own.</summary>
    public int Seed { get; init; }
    /// <summary>Where the voice rests, for the interlude dots: an outgoing scene keeps its own.</summary>
    public IReadOnlyList<VocalWindow> VocalWindows { get; init; } = [];
    /// <summary>Wall time the lyrics started loading, for the loading notice.</summary>
    public double? LoadingSince { get; init; }
    /// <summary>No lyrics were found while offline.</summary>
    public bool Offline { get; init; }
}

/// <summary>
/// Holds the scenes being replaced while they fade. Consulted while a frame is being built; use from the render thread.
/// </summary>
/// <remarks>
/// Every fade is interruptible: a scene that is replaced part-way through its own entrance leaves from the presence it
/// had reached, never from full strength, and the incoming scene is never pushed back to nothing. Up to two scenes can
/// be leaving at once, so scrubbing through a song cross-fades frame to frame instead of dropping what was on screen.
/// Nothing is allocated on a frame where the scene does not change.
/// </remarks>
public sealed class LyricSceneMemory
{
    /// <summary>How a scene leaves and the next arrives.</summary>
    public enum Fade
    {
        /// <summary>A new song or a new kind of scene: a soft dissolve with a little blur.</summary>
        Song,
        /// <summary>
        /// A jump within the same lyrics. Both scenes draw mostly the same pixels, so the incoming one comes up first
        /// and the outgoing one goes after it: the line never dims or blurs.
        /// </summary>
        Seek,
        /// <summary>A better lyric source for the same song showing a different line.</summary>
        Upgrade,
    }

    /// <summary>A source upgrade's cross-fade, both ways.</summary>
    public const double UpgradeDuration = 0.25;

    /// <summary>A scene below this presence is dropped rather than kept leaving.</summary>
    public const double DropBelow = 0.05;

    /// <summary>At most this many scenes leave at once.</summary>
    public const int MaxLeaving = 2;

    /// <summary>The outgoing scene's share, 0…1, <paramref name="age"/> seconds into a fade.</summary>
    public static double Outgoing(Fade fade, double age) => fade switch
    {
        Fade.Song => 1 - LyricMotion.Settle(age, WallpaperMotion.SceneOut),
        Fade.Seek => 1 - Easing.Smoothstep((age - WallpaperMotion.SeekOutDelay) / WallpaperMotion.SeekOut),
        _ => 1 - Easing.Smoothstep(age / UpgradeDuration),
    };

    /// <summary>The incoming scene's presence, 0…1, <paramref name="age"/> seconds into a fade.</summary>
    public static double Incoming(Fade fade, double age) => fade switch
    {
        Fade.Song => LyricMotion.Settle(age, WallpaperMotion.SceneIn),
        Fade.Seek => Easing.Smoothstep(age / WallpaperMotion.SeekIn),
        _ => Easing.Smoothstep(age / UpgradeDuration),
    };

    /// <summary>A scene on its way out.</summary>
    public sealed class OutgoingScene
    {
        private readonly double _nextLineStart;

        public OutgoingScene(LyricSceneKey key, LyricSceneContent content, double time, double wall, Fade fade = Fade.Song,
                             double startPresence = 1, int id = 0)
        {
            ArgumentNullException.ThrowIfNull(content);
            Key = key;
            Content = content;
            Time = time;
            Wall = wall;
            FadeKind = fade;
            StartPresence = Math.Min(1, Math.Max(0, startPresence));
            Id = id;
            var row = new LyricLineIndex(content.Lines).LineAt(time) ?? -1;
            _nextLineStart = row + 1 < content.Lines.Count ? content.Lines[row + 1].Start : double.PositiveInfinity;
        }

        public LyricSceneKey Key { get; }
        public LyricSceneContent Content { get; }
        /// <summary>Playback time of the outgoing scene when it was replaced.</summary>
        public double Time { get; }
        /// <summary>Wall time the cross-fade began.</summary>
        public double Wall { get; }
        public Fade FadeKind { get; }
        /// <summary>How present the scene was when it was replaced: it leaves from there.</summary>
        public double StartPresence { get; }
        /// <summary>Tells two leaving scenes apart.</summary>
        public int Id { get; }

        /// <summary>
        /// The line that was on screen keeps moving while it fades, up to the next cue. The incoming scene owns all new
        /// lyric entries.
        /// </summary>
        public double RenderTime(double wall) =>
            Math.Min(Time + Math.Max(0, wall - Wall), Math.BitDecrement(_nextLineStart));

        /// <summary>0…1 presence at <paramref name="wall"/>.</summary>
        public double Presence(double wall) => StartPresence * Math.Min(1, Math.Max(0, Outgoing(FadeKind, wall - Wall)));

        /// <summary>A song change dissolves softly; a seek or a source upgrade, which redraw much the same lines in place,
        /// must not blur them.</summary>
        public bool Blurs => FadeKind == Fade.Song;
    }

    /// <summary>A scene leaving, with its presence on this frame.</summary>
    public readonly record struct Leaving(OutgoingScene Scene, double Presence);

    /// <summary>What to draw on this frame. Valid until the next <see cref="Observe"/> call (its buffer is reused).</summary>
    public readonly struct Transition
    {
        private readonly Leaving[]? _leaving;
        private readonly int _count;

        internal Transition(Leaving[]? leaving, int count, double incomingPresence)
        {
            _leaving = leaving;
            _count = count;
            IncomingPresence = incomingPresence;
        }

        /// <summary>The scenes on their way out, oldest first.</summary>
        public ReadOnlySpan<Leaving> LeavingScenes => _leaving is null ? default : _leaving.AsSpan(0, _count);

        /// <summary>0…1 presence of the incoming scene.</summary>
        public double IncomingPresence { get; }

        /// <summary>The most visible leaving scene.</summary>
        public OutgoingScene? Outgoing
        {
            get
            {
                OutgoingScene? best = null;
                var presence = double.NegativeInfinity;
                foreach (var leaving in LeavingScenes)
                {
                    if (leaving.Presence > presence) { presence = leaving.Presence; best = leaving.Scene; }
                }
                return best;
            }
        }

        /// <summary>0…1 presence of <see cref="Outgoing"/>.</summary>
        public double OutgoingPresence
        {
            get
            {
                double best = 0;
                var any = false;
                foreach (var leaving in LeavingScenes)
                {
                    best = any ? Math.Max(best, leaving.Presence) : leaving.Presence;
                    any = true;
                }
                return best;
            }
        }
    }

    private readonly List<OutgoingScene> _leaving = new(MaxLeaving + 1);
    private readonly Leaving[] _frame = new Leaving[MaxLeaving + 1];
    private (LyricSceneKey Key, LyricSceneContent Content, double Time)? _last;
    private double _lastJumpWall = double.NegativeInfinity;
    private (double Wall, Fade Fade)? _arriving;
    private int _nextId;

    /// <summary>The scenes on their way out.</summary>
    public IReadOnlyList<OutgoingScene> LeavingScenes => _leaving;

    private double IncomingPresence(double wall) =>
        _arriving is { } a ? Math.Min(1, Math.Max(0, Incoming(a.Fade, wall - a.Wall))) : 1;

    private void Push(LyricSceneKey key, LyricSceneContent content, double time, double wall, Fade fade, double presence,
                      double now)
    {
        RemoveFaint(now, DropBelow);
        _leaving.Add(new OutgoingScene(key, content, time, wall, fade, presence, _nextId));
        _nextId = unchecked(_nextId + 1);
        while (_leaving.Count > MaxLeaving)
        {
            var faintest = 0;
            for (var i = 1; i < _leaving.Count; i++)
                if (_leaving[i].Presence(now) < _leaving[faintest].Presence(now)) faintest = i;
            _leaving.RemoveAt(faintest);
        }
    }

    private void RemoveFaint(double wall, double below)
    {
        for (var i = _leaving.Count - 1; i >= 0; i--)
            if (_leaving[i].Presence(wall) <= below) _leaving.RemoveAt(i);
    }

    /// <summary>
    /// Whether two sheets of the same song show different lines at <paramref name="time"/>: a different text, or the
    /// line starting somewhere else.
    /// </summary>
    public static bool ActiveLineDiffers(LyricSceneContent old, LyricSceneContent @new, double time)
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(@new);
        var before = Active(old, time);
        var after = Active(@new, time);
        if (before is null || after is null) return (before is null) != (after is null);
        return !string.Equals(before.Text.Trim(), after.Text.Trim(), StringComparison.OrdinalIgnoreCase)
            || Math.Abs(before.Start - after.Start) > 0.3;
    }

    private static LyricLine? Active(LyricSceneContent content, double time) =>
        new LyricLineIndex(content.Lines).LineAt(time) is { } row && row < content.Lines.Count ? content.Lines[row] : null;

    /// <summary>
    /// Takes in this frame's scene and returns what to draw: the scenes leaving and how present the incoming one is.
    /// </summary>
    /// <param name="key">What the stage shows now.</param>
    /// <param name="content">Its content (the same instance frame to frame while nothing changes).</param>
    /// <param name="time">Lyric time.</param>
    /// <param name="wall">Wall time, seconds.</param>
    /// <param name="jump">The lyric clock's last jump (<see cref="LyricClock.LastJump"/>).</param>
    public Transition Observe(LyricSceneKey key, LyricSceneContent content, double time, double wall, LyricClock.Jump? jump)
    {
        ArgumentNullException.ThrowIfNull(content);
        // The system clock stepped back during a cross-fade: it is finished rather than held for as long as the clock
        // went back.
        if (_arriving is { } arriving && arriving.Wall > wall + 1) _arriving = null;
        for (var i = _leaving.Count - 1; i >= 0; i--)
            if (_leaving[i].Wall > wall + 1) _leaving.RemoveAt(i);
        // How present the scene being replaced is: it leaves from there. A song change often arrives in two steps (new
        // title, then cleared lines); the barely-arrived middle step is dropped rather than displacing the old lyrics
        // that are still visibly leaving.
        var shown = IncomingPresence(wall);
        if (_last is { } last && last.Key != key)
        {
            if (last.Key.Track == key.Track && last.Key.Kind == LyricSceneKind.Synced && key.Kind == LyricSceneKind.Synced)
            {
                // A better timing source for the current recording takes over the stage in place: both sheets usually
                // show this line, and two copies would double it. Only when the new sheet shows another line does it
                // cross-fade, briefly and without blur.
                if (shown >= 0.999 && ActiveLineDiffers(last.Content, content, time))
                {
                    Push(last.Key, last.Content, last.Time, wall, Fade.Upgrade, 1, wall);
                    _arriving = (wall, Fade.Upgrade);
                }
            }
            else
            {
                if (shown > DropBelow) Push(last.Key, last.Content, last.Time, wall, Fade.Song, shown, wall);
                _arriving = (wall, Fade.Song);
            }
        }
        else if (jump is { } j && j.Wall > _lastJumpWall && key.Kind == LyricSceneKind.Synced && _last is { } current)
        {
            // A seek, or the clock adopting a correction: the scene on screen leaves from the presence it reached.
            // Jumps that come while that cross-fade is still in flight (scrubbing) join it: the arriving scene shows
            // the new place and keeps arriving, rather than piling up scenes or restarting from nothing.
            var inFlight = _arriving is not null && _leaving.Count > 0;
            if (!inFlight)
            {
                if (shown > DropBelow) Push(key, current.Content, j.From, j.Wall, Fade.Seek, shown, wall);
                _arriving = (j.Wall, Fade.Seek);
            }
        }
        if (jump is { } seen) _lastJumpWall = Math.Max(_lastJumpWall, seen.Wall);
        if (_last is not { } previous || previous.Key != key)
        {
            _last = (key, content, time);
        }
        else
        {
            // Readings arrive after the lines; keep the newest copy.
            _last = (key, key.Kind == LyricSceneKind.Synced ? content : previous.Content, time);
        }
        RemoveFaint(wall, 0.001);
        var incoming = IncomingPresence(wall);
        if (incoming >= 1) _arriving = null;
        for (var i = 0; i < _leaving.Count; i++) _frame[i] = new Leaving(_leaving[i], _leaving[i].Presence(wall));
        return new Transition(_frame, _leaving.Count, incoming);
    }
}
