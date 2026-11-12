// Ported from the Mac's MusicVideoView.swift (MusicVideoPlaybackSurface, MusicVideoSyncLoop) and MusicVideoFreeze.swift
// (MusicVideoHold, MusicVideoFrozenFrame's rules), Lunalyr's own code. The views and players around them are platform code
// and are not translated: Windows' YouTube player is a WebView2 host (Lunalyr.App), and a local file needs no loop (its
// frames are chosen by the song's clock, MusicVideoFrameSync). Timers are a seam, so the tests drive every check by hand.

namespace Lunalyr.Core.MusicVideo;

/// <summary>A player the sync loop can steer.</summary>
public interface IMusicVideoPlaybackSurface
{
    /// <summary>Added to seek targets: YouTube takes a moment to resume after one.</summary>
    double SeekLead { get; }

    /// <summary>How drift is corrected on this player.</summary>
    MusicVideoCorrectionStyle CorrectionStyle => MusicVideoCorrectionStyle.Seek;

    /// <summary>The video's position and state, and its duration once known; null when there is nothing to sample.</summary>
    void Sample(Action<MusicVideoDrift.Sample?, double?> done);

    void PlayVideo();

    void PauseVideo();

    void SeekVideo(double seconds);

    /// <summary>Plays a little faster or slower; only asked of Rate players.</summary>
    void SetVideoRate(double rate)
    {
    }

    /// <summary>Told on every check, before the sample, why the video is moving or not.</summary>
    void NoteHold(MusicVideoHold hold)
    {
    }
}

/// <summary>Why the video is (or is not) moving, as the sync loop sees it on each check.</summary>
public enum MusicVideoHold
{
    /// <summary>The song plays inside the video's span, on a live wallpaper.</summary>
    Live,
    /// <summary>The user paused the song, the wallpaper is live, the song is inside the video's span: the video holds its frame.</summary>
    UserPaused,
    /// <summary>The wallpaper is paused, covered or hidden while the song is inside the video's span: the frame is held too.</summary>
    WallpaperPaused,
    /// <summary>Another song's video, fading out after a song change: it holds its last frame for the fade.</summary>
    Outgoing,
    /// <summary>Before the video starts or past its end.</summary>
    Other,
}

public static class MusicVideoHolds
{
    /// <summary>The holds that keep the video's last frame on screen.</summary>
    public static bool KeepsFrame(this MusicVideoHold hold) =>
        hold is MusicVideoHold.UserPaused or MusicVideoHold.WallpaperPaused or MusicVideoHold.Outgoing;

    /// <param name="target">Where the video should be, null before its start or past its end.</param>
    public static MusicVideoHold Of(bool surfacePaused, bool songPlaying, double? target)
    {
        if (target is null) return MusicVideoHold.Other;
        if (surfacePaused) return MusicVideoHold.WallpaperPaused;
        return songPlaying ? MusicVideoHold.Live : MusicVideoHold.UserPaused;
    }
}

/// <summary>
/// Checks the video against the song four times a second and corrects it (<see cref="MusicVideoCorrector"/>): follows play,
/// pause and seeks in the player, eases small drift with the rate where it can, and seeks YouTube rarely, led by its
/// measured seek latency. Stops checking once a hidden surface's video is paused. Not thread-safe: one thread drives it.
/// </summary>
public sealed class MusicVideoSyncLoop
{
    public const double Interval = 0.25;

    /// <summary>The check while the video's bar is dragged: the video follows the hand.</summary>
    public const double DragInterval = 1.0 / 15;

    private readonly Func<double> _songTime;
    private readonly Func<bool> _songPlaying;
    private readonly Func<double> _offset;
    private readonly Func<double> _uptime;
    private readonly Func<double, Action, IDisposable> _schedule;
    private IDisposable? _timer;
    private bool _inFlight;
    private double? _lastDuration;
    private double? _lastOffset;

    /// <param name="schedule">Starts a repeating timer every <c>interval</c> seconds; disposing it stops it.</param>
    public MusicVideoSyncLoop(Func<double> songTime, Func<bool> songPlaying, Func<double> offset, Func<double> uptime,
        Func<double, Action, IDisposable>? schedule = null)
    {
        _songTime = songTime;
        _songPlaying = songPlaying;
        _offset = offset;
        _uptime = uptime;
        _schedule = schedule ?? ((_, _) => new NoTimer());
    }

    public IMusicVideoPlaybackSurface? Surface { get; set; }
    public MusicVideoCorrector? Corrector { get; private set; }
    public MusicVideoAlignSeeker? Seeker { get; private set; }
    public bool SurfacePaused { get; private set; }

    /// <summary>Told the video's length once it is known (for the intro estimate).</summary>
    public Action<double>? OnDuration { get; set; }

    /// <summary>Told each sample of the video's own position, with its drift from the target and the uptime it was read at.</summary>
    public Action<MusicVideoDrift.Sample, double?, double>? OnSample { get; set; }

    /// <summary>Whether this surface's video is still the playing song's (a video fading out after a song change is not).</summary>
    public Func<bool> IsCurrent { get; set; } = () => true;

    /// <summary>The video's bar is being dragged (align mode): the video is sought with the hand and not corrected.</summary>
    public Func<bool> AlignDragging { get; set; } = () => false;

    /// <summary>The pace of the checks right now, or null while resting.</summary>
    public double? CheckInterval { get; private set; }

    public bool IsRunning => _timer is not null;

    /// <summary>When the loop last sought the video (uptime).</summary>
    public double LastSeek => Corrector?.LastSeek ?? double.NegativeInfinity;

    public void Start()
    {
        if (_timer is not null) return;
        Schedule(AlignDragging() ? DragInterval : Interval);
        Tick();
    }

    private void Schedule(double interval)
    {
        _timer?.Dispose();
        _timer = _schedule(interval, Tick);
        CheckInterval = interval;
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        CheckInterval = null;
    }

    public void SetSurfacePaused(bool paused)
    {
        if (paused == SurfacePaused) return;
        SurfacePaused = paused;
        // Paused: one more check pauses the video, then the loop rests.
        Start();
    }

    /// <summary>Why the video is moving or not, right now.</summary>
    public MusicVideoHold Hold(double? videoDuration)
    {
        if (!IsCurrent()) return MusicVideoHold.Outgoing;
        var target = MusicVideoTiming.VideoTime(_songTime(), _offset(), videoDuration);
        return MusicVideoHolds.Of(SurfacePaused, _songPlaying(), target);
    }

    /// <summary>One check.</summary>
    public void Tick()
    {
        if (Surface is not { } surface) return;
        if (_timer is not null)
        {
            var wanted = AlignDragging() ? DragInterval : Interval;
            if (CheckInterval != wanted) Schedule(wanted);
        }
        // Before the sample: a closed player (a long pause) has none, and still needs to hear that the song plays again.
        surface.NoteHold(Hold(_lastDuration));
        if (_inFlight) return;
        _inFlight = true;
        // The player answers a moment later: its position was read somewhere between asking and the answer, so it is
        // compared with the song's position halfway between.
        var askedSong = _songTime();
        var askedAt = _uptime();
        surface.Sample((sample, duration) =>
        {
            _inFlight = false;
            var answeredSong = _songTime();
            var answeredAt = _uptime();
            var songTime = Midpoint(askedSong, answeredSong, _songPlaying());
            var readAt = askedAt + Math.Max(0, answeredAt - askedAt) / 2;
            if (sample is not { } read || Surface is not { } current)
            {
                // Nothing to sample behind a hidden surface: the loop rests until the surface shows again.
                if (SurfacePaused) Stop();
                return;
            }
            if (!IsCurrent())
            {
                // Another song's video, on its way out: hold it still.
                if (read.VideoPlaying || read.Buffering) current.PauseVideo();
                return;
            }
            var offset = _offset();
            var target = MusicVideoTiming.VideoTime(songTime, offset, duration);
            if (duration is { } length)
            {
                _lastDuration = length;
                OnDuration?.Invoke(length);
            }
            OnSample?.Invoke(read, target is { } t ? read.VideoTime - t : null, readAt);
            var shouldPlay = !SurfacePaused && _songPlaying();
            var style = current.CorrectionStyle;
            if (Corrector?.Style != style) Corrector = new MusicVideoCorrector(style);
            MusicVideoAction action;
            if (AlignDragging())
            {
                // The hand moves the video: seek with it, correct nothing.
                if (Seeker?.Style != style) Seeker = new MusicVideoAlignSeeker(style, _lastOffset ?? offset);
                var lead = style == MusicVideoCorrectionStyle.LedSeek ? Corrector.Latency.Lead : current.SeekLead;
                action = Seeker.Decide(offset, target, read, shouldPlay, _uptime(), lead);
                _lastOffset = offset;
            }
            else
            {
                if (Seeker is { } seeker)
                {
                    // Released: correction resumes from the drag's last seek, with the new offset applied.
                    Seeker = null;
                    Corrector.NoteSeek(seeker.LastSeek);
                    Corrector.TargetMoved();
                }
                // A change of offset (a nudge, a tap) is applied at once, however small.
                if (_lastOffset is { } last && Math.Abs(offset - last) >= 0.05) Corrector.TargetMoved();
                _lastOffset = offset;
                action = Corrector.Decide(target, read, shouldPlay, _uptime(), current.SeekLead);
            }
            switch (action.Kind)
            {
                case MusicVideoActionKind.Play:
                    current.PlayVideo();
                    break;
                case MusicVideoActionKind.Pause:
                    current.PauseVideo();
                    break;
                case MusicVideoActionKind.Rate:
                    current.SetVideoRate(action.Value);
                    break;
                case MusicVideoActionKind.Seek:
                    current.SeekVideo(Math.Max(0, action.Value));
                    if (action.ThenPlay && !read.VideoPlaying) current.PlayVideo();
                    // A seek leaves a rate-steered player's rate where it was: back to 1.
                    else if (action.ThenPlay && style == MusicVideoCorrectionStyle.Rate) current.SetVideoRate(1);
                    break;
            }
            if (SurfacePaused && !read.VideoPlaying) Stop();
        });
    }

    /// <summary>The song's position between two reads: their middle while it plays on (a jump between them keeps the later read).</summary>
    public static double Midpoint(double asked, double answered, bool playing)
    {
        if (!playing || answered < asked || answered - asked >= 1) return answered;
        return (asked + answered) / 2;
    }

    private sealed class NoTimer : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

/// <summary>What the frozen frame needs from the YouTube player around it.</summary>
public interface IMusicVideoFreezeHost
{
    /// <summary>A picture of the player as drawn now; null when none could be taken.</summary>
    void CaptureFrame(Action<object?> done);

    /// <summary>Shows the picture over the (hidden) player, at once.</summary>
    void ShowFrozenFrame(object image);

    /// <summary>Hides it. <paramref name="handOver"/>: the player is playing again; fade only once it has faded in.</summary>
    void HideFrozenFrame(bool handOver);

    void SendPause();

    /// <summary>A long pause: the player is closed to save power.</summary>
    void ReleasePlayer();

    /// <summary>Playback resumed after <see cref="ReleasePlayer"/>: a fresh player.</summary>
    void RecreatePlayer();
}

/// <summary>
/// A paused YouTube video's last frame. YouTube draws a pause button and its title bar over a paused player, so the player
/// is hidden whenever it is not playing. When the video is paused for a hold that keeps its frame, a picture of the player
/// is taken just before it is paused and shown in its place. A drift seek takes a picture too, shown only if the seek
/// buffers. After a long pause the player is closed; the picture stays until a new one plays.
/// </summary>
public sealed class MusicVideoFrozenFrame
{
    /// <summary>After this long not playing, the player is closed (about 10 minutes).</summary>
    public const double DefaultLongPause = 600;

    /// <summary>A seek's picture waits this long for the player to hide; a seek that lands at once drops it.</summary>
    public const double SeekHold = 2;

    private readonly double _longPause;
    private readonly Func<double, Action, Action> _schedule;
    private bool _seekCapturing;
    private int _generation;
    private Action? _cancelLongPause;
    private Action? _cancelSeekHold;

    /// <param name="schedule">Runs the work after a delay and returns a cancel; a seam for tests.</param>
    public MusicVideoFrozenFrame(double longPause = DefaultLongPause, Func<double, Action, Action>? schedule = null)
    {
        _longPause = longPause;
        _schedule = schedule ?? After;
    }

    public IMusicVideoFreezeHost? Host { get; set; }
    public MusicVideoHold Hold { get; private set; } = MusicVideoHold.Live;
    public bool PlayerShowing { get; private set; }
    public object? Image { get; private set; }
    public bool FrameShown { get; private set; }
    public bool Capturing { get; private set; }
    public bool SeekFrame { get; private set; }
    public bool Released { get; private set; }

    private static Action After(double delay, Action work)
    {
        var cts = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromSeconds(delay), cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) work();
        }, TaskScheduler.Default);
        return () => cts.Cancel();
    }

    /// <summary>
    /// The visibility rule. The frozen frame shows while a hold keeps it, and while live until the player plays again (a
    /// resume, a seek that buffers); never outside the video's span.
    /// </summary>
    public static bool ShowsFrame(MusicVideoHold hold, bool playerShowing, bool hasImage)
    {
        if (!hasImage) return false;
        return hold switch
        {
            MusicVideoHold.UserPaused or MusicVideoHold.WallpaperPaused or MusicVideoHold.Outgoing => true,
            MusicVideoHold.Live => !playerShowing,
            _ => false,
        };
    }

    /// <summary>From each check of the sync loop.</summary>
    public void NoteHold(MusicVideoHold hold)
    {
        Hold = hold;
        // Paused for another reason: a pause takes its own, newer picture.
        if (hold != MusicVideoHold.Live && SeekFrame) DropSeekFrame();
        if (hold == MusicVideoHold.Live)
        {
            StopLongPause();
            if (Released)
            {
                Released = false;
                Host?.RecreatePlayer();
            }
        }
        else if (_cancelLongPause is null && !Released)
        {
            StartLongPause();
        }
        Refresh();
    }

    /// <summary>The player's state: visible (playing) or hidden.</summary>
    public void NotePlayer(bool showing)
    {
        PlayerShowing = showing;
        Refresh();
    }

    /// <summary>
    /// The sync loop pauses the video. Paused for a hold that keeps its frame, a playing video's picture is taken first and
    /// shown, then it is paused; anything else just pauses.
    /// </summary>
    public void Pause()
    {
        if (Capturing) return;
        if (!Hold.KeepsFrame() || !PlayerShowing || Released || Image is not null || Host is not { } host)
        {
            Host?.SendPause();
            return;
        }
        Capturing = true;
        var mine = _generation;
        host.CaptureFrame(picture =>
        {
            if (_generation != mine) return;
            Capturing = false;
            switch (Hold)
            {
                case MusicVideoHold.Live:
                    // Resumed meanwhile: nothing to pause.
                    return;
                case MusicVideoHold.UserPaused or MusicVideoHold.WallpaperPaused or MusicVideoHold.Outgoing:
                    if (picture is not null && Image is null) Image = picture;
                    // Shown before the pause, so YouTube's pause button is never seen.
                    Refresh();
                    break;
            }
            Host?.SendPause();
        });
    }

    /// <summary>
    /// The sync loop is about to seek the playing video. Its picture is taken now and shown only if the player hides to
    /// buffer the seek. The seek itself is never held up for it.
    /// </summary>
    public void NoteSeek()
    {
        if (Hold != MusicVideoHold.Live || !PlayerShowing || Released || Capturing || _seekCapturing || Image is not null
            || Host is not { } host) return;
        _seekCapturing = true;
        var mine = _generation;
        host.CaptureFrame(picture =>
        {
            if (_generation != mine) return;
            _seekCapturing = false;
            if (Hold != MusicVideoHold.Live || Image is not null || picture is null) return;
            Image = picture;
            SeekFrame = true;
            _cancelSeekHold?.Invoke();
            _cancelSeekHold = _schedule(SeekHold, () =>
            {
                _cancelSeekHold = null;
                if (!SeekFrame) return;
                SeekFrame = false;
                Refresh();
            });
            Refresh();
        });
    }

    /// <summary>A new video, a failure, or the player gone: no frame.</summary>
    public void Reset()
    {
        _generation++;
        Capturing = false;
        _seekCapturing = false;
        StopLongPause();
        StopSeekHold();
        SeekFrame = false;
        Released = false;
        Image = null;
        PlayerShowing = false;
        if (FrameShown)
        {
            FrameShown = false;
            Host?.HideFrozenFrame(false);
        }
    }

    private void Refresh()
    {
        var show = ShowsFrame(Hold, PlayerShowing, Image is not null);
        if (show && !FrameShown && Image is { } image)
        {
            FrameShown = true;
            // In use now: from here it is an ordinary frame, kept until the player plays again.
            if (SeekFrame)
            {
                SeekFrame = false;
                StopSeekHold();
            }
            Host?.ShowFrozenFrame(image);
        }
        else if (!show && FrameShown)
        {
            FrameShown = false;
            Host?.HideFrozenFrame(Hold == MusicVideoHold.Live && PlayerShowing);
        }
        // Playing again: the picture is out of date (a seek's picture still waiting to be needed is kept for its while).
        if (Hold == MusicVideoHold.Live && PlayerShowing && Image is not null && !SeekFrame)
        {
            Image = null;
            StopLongPause();
        }
    }

    private void DropSeekFrame()
    {
        SeekFrame = false;
        StopSeekHold();
        Image = null;
    }

    private void StopSeekHold()
    {
        _cancelSeekHold?.Invoke();
        _cancelSeekHold = null;
    }

    private void StartLongPause()
    {
        StopLongPause();
        _cancelLongPause = _schedule(_longPause, () =>
        {
            _cancelLongPause = null;
            if (Hold == MusicVideoHold.Live || Released) return;
            Released = true;
            PlayerShowing = false;
            Host?.ReleasePlayer();
        });
    }

    private void StopLongPause()
    {
        _cancelLongPause?.Invoke();
        _cancelLongPause = null;
    }

    /// <summary>
    /// Whether a picture is all but black (a capture that left the video out): read on an 8 × 8 grid of BGRA pixels. Such a
    /// picture counts as none.
    /// </summary>
    public static bool LooksBlank(ReadOnlySpan<byte> bgra, int width, int height, byte threshold = 6)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4) return false;
        for (var gy = 0; gy < 8; gy++)
        {
            for (var gx = 0; gx < 8; gx++)
            {
                var x = Math.Min(width - 1, (int)((gx + 0.5) * width / 8));
                var y = Math.Min(height - 1, (int)((gy + 0.5) * height / 8));
                var i = (y * width + x) * 4;
                if (Math.Max(bgra[i], Math.Max(bgra[i + 1], bgra[i + 2])) > threshold) return false;
            }
        }
        return true;
    }
}
