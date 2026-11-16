// Ported from the Mac's MusicVideoController.swift (Lunalyr's own code). Finds the playing song's video while "Music video"
// is the wallpaper, keeps its offset (nudges, Tap to sync, Align), and says what it found for Settings. The Mac's controller
// lives on the main actor; Windows calls this one from the player's thread, the UI thread and the renderer, so every member
// takes one lock and Changed is raised outside it. Windows' differences: the library is a file the user chose for the
// song (UseVideoFile), keys come from the UI as virtual keys (TapKey), and the notes say what the Data API needs.

using System.Globalization;

namespace Lunalyr.Core.MusicVideo;

/// <summary>What the controller is doing for the playing song.</summary>
public abstract record MusicVideoStatus
{
    private MusicVideoStatus()
    {
    }

    /// <summary>Another wallpaper type is chosen.</summary>
    public sealed record Off : MusicVideoStatus;

    /// <summary>Nothing is playing.</summary>
    public sealed record Idle : MusicVideoStatus;

    public sealed record Searching(string Title) : MusicVideoStatus;

    public sealed record Showing(MusicVideoSource Source, string Title) : MusicVideoStatus;

    public sealed record Missing(MusicVideoMiss Miss, string Title) : MusicVideoStatus;

    public static MusicVideoStatus OffState { get; } = new Off();
    public static MusicVideoStatus IdleState { get; } = new Idle();
}

public sealed class MusicVideoController
{
    /// <summary>The menu's fine step: "Video earlier" / "Video later".</summary>
    public const double FineNudge = 0.25;

    /// <summary>A player that reported its position this recently can be sought.</summary>
    public const double RespondingWithin = 2;

    private const string NotASongKey = "\u0000not-a-song";

    private readonly MusicVideoResolver _resolver;
    private readonly MusicVideoFiles? _files;
    private readonly Func<(double Time, bool Playing)> _songClock;
    private readonly Func<MusicVideoSong, IReadOnlyList<MusicVideoLyricLine>?> _lyrics;
    private readonly Func<double> _uptime;
    private readonly Lock _gate = new();

    private MusicVideoStatus _status = MusicVideoStatus.OffState;
    private MusicVideoSong? _song;
    private string? _playingKey;
    private bool _enabled;
    private bool _online;
    private CancellationTokenSource? _lookupCts;
    private Task _lookup = Task.CompletedTask;
    private CancellationTokenSource? _retryCts;
    private string? _retried;

    private MusicVideoTapState _tapState = MusicVideoTapState.Idle;
    private readonly List<double> _taps = [];
    private (string Video, double Time, bool Playing, double At)? _videoSample;
    private CancellationTokenSource? _tapExpiry;

    private readonly Dictionary<string, double> _videoLengths = new(StringComparer.Ordinal);
    private MusicVideoAlignSession? _align;
    private MusicVideoAlignDrag? _alignDrag;
    private bool _alignTouched;

    // Caption sync (the Mac's deb366e, 9fbdb73, 3c6c931, dcaa3c0): the cues the showing video's player passed on, by
    // video key; the song and video pairs measured, and those whose wait for lyrics was logged.
    private (string Video, IReadOnlyList<MusicVideoCaptionCue> Cues)? _captionCues;
    private readonly HashSet<string> _captionMeasured = new(StringComparer.Ordinal);
    private readonly HashSet<string> _captionWaitLogged = new(StringComparer.Ordinal);
    private readonly List<string> _captionLog = [];

    public MusicVideoController(MusicVideoResolver resolver, MusicVideoOffsets offsets,
        Func<(double Time, bool Playing)>? songClock = null, Func<MusicVideoSong, IReadOnlyList<MusicVideoLyricLine>?>? lyrics = null,
        Func<double>? uptime = null, MusicVideoFiles? files = null)
    {
        _resolver = resolver;
        Offsets = offsets;
        _files = files;
        _songClock = songClock ?? (() => (0, false));
        _lyrics = lyrics ?? (_ => null);
        var started = System.Diagnostics.Stopwatch.StartNew();
        _uptime = uptime ?? (() => started.Elapsed.TotalSeconds);
    }

    public MusicVideoOffsets Offsets { get; }
    public MusicVideoResolver Resolver => _resolver;

    /// <summary>Each caption-sync log line as it is decided (<see cref="MusicVideoCaptionLog"/>; counts and offsets only).</summary>
    public event Action<string>? CaptionLogged;

    /// <summary>The latest caption-sync log lines (the last 20), for tests and diagnostics.</summary>
    public IReadOnlyList<string> CaptionLog
    {
        get { lock (_gate) return [.. _captionLog]; }
    }

    /// <summary>The latest caption measurement, trusted or not.</summary>
    public MusicVideoCaptionSync.Result? LastCaptionResult { get; private set; }

    /// <summary>Seconds before a lookup that failed for a passing reason is retried.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>The wallpaper shows the video's still instead of playing it (Windows' Animation effects off).</summary>
    public Func<bool> ReduceMotion { get; set; } = () => false;

    /// <summary>Whether a mouse button is down: a press whose release never came can't hold the video.</summary>
    public Func<bool> ButtonsHeld { get; set; } = () => true;

    /// <summary>Raised after anything Settings, the card or the wallpaper shows changes (any thread).</summary>
    public event Action? Changed;

    public MusicVideoStatus Status
    {
        get { lock (_gate) return _status; }
    }

    public MusicVideoTapState TapState
    {
        get { lock (_gate) return _tapState; }
    }

    public bool Enabled
    {
        get { lock (_gate) return _enabled; }
    }

    public bool Online
    {
        get { lock (_gate) return _online; }
    }

    private void Raise() => Changed?.Invoke();

    // ---- Following the song ----

    /// <summary>Whether <paramref name="source"/> is the video found for the playing song.</summary>
    public bool IsShowing(MusicVideoSource source)
    {
        lock (_gate) return _status is MusicVideoStatus.Showing showing && showing.Source == source;
    }

    /// <summary>The video to show for <paramref name="track"/>, when one was found for it.</summary>
    public MusicVideoSource? SourceFor(MusicVideoTrack? track)
    {
        if (track is null) return null;
        lock (_gate)
            return _status is MusicVideoStatus.Showing showing && _song is { } song && track.Song.Key == song.Key ? showing.Source : null;
    }

    /// <summary>The video on screen for the playing song, or null.</summary>
    public MusicVideoSource? ShowingSource
    {
        get { lock (_gate) return (_status as MusicVideoStatus.Showing)?.Source; }
    }

    public void Update(MusicVideoTrack? track, bool enabled, bool online)
    {
        var next = track is null || track.IsStream || track.IsAdvert ? null : track.Song;
        var advertOrStream = track is not null && (track.IsStream || track.IsAdvert);
        var playing = next?.Key ?? (advertOrStream ? NotASongKey : null);
        lock (_gate)
        {
            var changed = playing != _playingKey || enabled != _enabled || online != _online;
            _enabled = enabled;
            _online = online;
            if (!changed) return;
            _playingKey = playing;
            _song = next;
            _lookupCts?.Cancel();
            _retryCts?.Cancel();
            ResetTapsLocked();
            EndAlignLocked();
            if (!enabled) _status = MusicVideoStatus.OffState;
            else if (next is null) _status = advertOrStream ? new MusicVideoStatus.Missing(MusicVideoMiss.NotASong, track?.Title ?? "") : MusicVideoStatus.IdleState;
            else
            {
                _status = new MusicVideoStatus.Searching(next.Title);
                StartLocked(next);
            }
        }
        Raise();
    }

    private void StartLocked(MusicVideoSong song)
    {
        _lookupCts?.Cancel();
        var cts = new CancellationTokenSource();
        _lookupCts = cts;
        var online = _online;
        _lookup = Task.Run(async () =>
        {
            MusicVideoResolution result;
            try
            {
                result = await _resolver.ResolveAsync(song, online, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (cts.IsCancellationRequested) return;
            Apply(result, song);
        });
    }

    private void Apply(MusicVideoResolution result, MusicVideoSong song)
    {
        lock (_gate)
        {
            if (_song?.Key != song.Key) return;
            switch (result)
            {
                case MusicVideoResolution.Found found:
                    _status = new MusicVideoStatus.Showing(found.Source, song.Title);
                    break;
                case MusicVideoResolution.Missing missing:
                    _status = new MusicVideoStatus.Missing(missing.Miss, song.Title);
                    if (missing.Miss is MusicVideoMiss.RateLimited or MusicVideoMiss.LookupFailed && _retried != song.Key)
                        ScheduleRetryLocked(song);
                    break;
            }
        }
        Raise();
    }

    private void ScheduleRetryLocked(MusicVideoSong song)
    {
        _retryCts?.Cancel();
        var cts = new CancellationTokenSource();
        _retryCts = cts;
        var delay = RetryDelay;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            lock (_gate)
            {
                if (cts.IsCancellationRequested || !_enabled || _song?.Key != song.Key) return;
                _retried = song.Key;
                _status = new MusicVideoStatus.Searching(song.Title);
                StartLocked(song);
            }
            Raise();
        });
    }

    /// <summary>Waits for the lookup in flight; for tests.</summary>
    public async Task SettleAsync()
    {
        Task lookup;
        lock (_gate) lookup = _lookup;
        await lookup.ConfigureAwait(false);
        // A lookup that started another (a refused video's next candidate) is waited for too.
        lock (_gate) lookup = _lookup;
        await lookup.ConfigureAwait(false);
    }

    /// <summary>
    /// The player could not show the video, and the wallpaper falls back for this song. Only a report about the video on
    /// screen counts. YouTube refused it for good: the song's next official video is tried (a few at most), then "can't be
    /// embedded" is remembered; never for the user's own link. Something passing: nothing remembered, tried once more later.
    /// The song's own file would not play: said as such.
    /// </summary>
    public void ReportPlaybackFailure(MusicVideoSource source, MusicVideoPlaybackFailure failure = MusicVideoPlaybackFailure.Refused)
    {
        lock (_gate)
        {
            if (_song is not { } song || _status is not MusicVideoStatus.Showing showing || showing.Source != source) return;
            EndAlignLocked();
            switch (source)
            {
                case MusicVideoSource.Local:
                    _status = new MusicVideoStatus.Missing(MusicVideoMiss.LocalFileFailed, song.Title);
                    break;
                case MusicVideoSource.YouTube when failure == MusicVideoPlaybackFailure.Transient:
                    _status = new MusicVideoStatus.Missing(MusicVideoMiss.PlayerUnreachable, song.Title);
                    if (_retried != song.Key) ScheduleRetryLocked(song);
                    break;
                case MusicVideoSource.YouTube youTube:
                    _status = new MusicVideoStatus.Missing(MusicVideoMiss.NotEmbeddable, song.Title);
                    // The user's own link is kept as it is, so they can fix it.
                    if (_resolver.Links.VideoId(song.Key) == youTube.VideoId) break;
                    if (_resolver.MarkNotEmbeddable(song, _online ? youTube.VideoId : null)) RelookLocked(song);
                    break;
            }
        }
        Raise();
    }

    // ---- Open on YouTube ----

    /// <summary>The YouTube video on screen for the playing song, null for a file or none.</summary>
    public string? ShowingYouTubeId
    {
        get { lock (_gate) return (_status as MusicVideoStatus.Showing)?.Source is MusicVideoSource.YouTube y ? y.VideoId : null; }
    }

    /// <summary>A watch link at the song's position, offset included (no <c>t</c> at 0).</summary>
    public static string? WatchUrl(string videoId, double songTime, double offset)
    {
        if (!YouTubeVideoId.IsValid(videoId)) return null;
        var at = MusicVideoTiming.VideoTime(songTime, offset, null) ?? 0;
        var seconds = (int)Math.Max(0, Math.Floor(at));
        return $"https://www.youtube.com/watch?v={videoId}" + (seconds > 0 ? $"&t={seconds.ToString(CultureInfo.InvariantCulture)}s" : "");
    }

    /// <summary>The video on screen, where the song is now.</summary>
    public string? CurrentWatchUrl => ShowingYouTubeId is { } id ? WatchUrl(id, _songClock().Time, CurrentOffset) : null;

    // ---- Pasted links, picked files ----

    /// <summary>The YouTube link pasted for the playing song, as a watch link.</summary>
    public string? PastedLink
    {
        get
        {
            lock (_gate) return _resolver.Links.VideoId(_song?.Key) is { } id ? $"https://www.youtube.com/watch?v={id}" : null;
        }
    }

    /// <summary>Uses a pasted YouTube link (or bare video ID) for the playing song. False when it is not one.</summary>
    public bool UsePastedLink(string text)
    {
        lock (_gate)
        {
            if (_song is not { } song || !_resolver.Links.Set(text, song.Key)) return false;
            _resolver.Cache.Remove(song.Key);
            RelookLocked(song);
        }
        Raise();
        return true;
    }

    /// <summary>Forgets the pasted link and whatever was remembered for the song.</summary>
    public void ClearPastedLink()
    {
        lock (_gate)
        {
            if (_song is not { } song) return;
            _resolver.Links.Remove(song.Key);
            _resolver.Cache.Remove(song.Key);
            RelookLocked(song);
        }
        Raise();
    }

    /// <summary>The video file chosen for the playing song (Windows' library), or null.</summary>
    public string? VideoFile
    {
        get { lock (_gate) return _files?.Path(_song?.Key); }
    }

    /// <summary>Uses <paramref name="path"/> for the playing song. False with nothing playing.</summary>
    public bool UseVideoFile(string path)
    {
        lock (_gate)
        {
            if (_files is null || _song is not { } song) return false;
            _files.Set(path, song.Key);
            RelookLocked(song);
        }
        Raise();
        return true;
    }

    public void ClearVideoFile()
    {
        lock (_gate)
        {
            if (_files is null || _song is not { } song) return;
            _files.Remove(song.Key);
            RelookLocked(song);
        }
        Raise();
    }

    // ---- Search YouTube… ----

    public MusicVideoSong? SearchSong
    {
        get { lock (_gate) return _song; }
    }

    public string? PickedVideoId
    {
        get { lock (_gate) return _resolver.Links.VideoId(_song?.Key); }
    }

    public Task<MusicVideoSearchOutcome> SearchYouTubeAsync(string query, CancellationToken ct) => _resolver.SearchResultsAsync(query, ct);

    /// <summary>Windows: YouTube's results page came back as something other than results lately (a consent or bot-check page).</summary>
    public bool YouTubeSearchUnavailable => _resolver.PageUnavailable;

    /// <summary>Uses a video picked in Search YouTube… for the song it was searched for, exactly as a pasted link.</summary>
    public bool UsePickedVideo(string videoId, string songKey)
    {
        lock (_gate)
        {
            if (_song?.Key != songKey || !YouTubeVideoId.IsValid(videoId)) return false;
        }
        return UsePastedLink(videoId);
    }

    private void RelookLocked(MusicVideoSong song)
    {
        _lookupCts?.Cancel();
        _retryCts?.Cancel();
        ResetTapsLocked();
        EndAlignLocked();
        if (!_enabled) return;
        _status = new MusicVideoStatus.Searching(song.Title);
        StartLocked(song);
    }

    // ---- Offsets ----

    private string? ShowingVideoKeyLocked => _status is MusicVideoStatus.Showing showing ? MusicVideoOffsets.VideoKey(showing.Source) : null;

    /// <summary>
    /// How far into the video the song starts, for the playing song: the user's offset for this video, else the length
    /// estimate, else 0. While the video's bar is dragged, the offset under the hand. Read by the renderer every frame.
    /// </summary>
    public double CurrentOffset
    {
        get { lock (_gate) return CurrentOffsetLocked; }
    }

    private double CurrentOffsetLocked =>
        IsAlignDraggingLocked && _alignDrag is { } drag
            ? MusicVideoTiming.ClampedOffset(drag.Offset)
            : Offsets.Offset(_song?.Key, ShowingVideoKeyLocked);

    public MusicVideoOffsetOrigin OffsetOrigin
    {
        get { lock (_gate) return Offsets.Origin(_song?.Key, ShowingVideoKeyLocked); }
    }

    public double OffsetFor(MusicVideoTrack? track)
    {
        var key = track?.Song.Key;
        lock (_gate)
        {
            if (key is not null && key == _song?.Key) return CurrentOffsetLocked;
        }
        return Offsets.Offset(key);
    }

    /// <summary>The user's nudge, from the offset in force; it wins from then on, for the video on screen.</summary>
    public void NudgeOffset(double delta)
    {
        lock (_gate)
        {
            if (_song is not { } song) return;
            Offsets.Nudge(delta, song.Key, ShowingVideoKeyLocked);
            NoteUserTimingLocked();
            ResetTapsLocked();
        }
        Raise();
    }

    /// <summary>Logs the user's timing when it was set over the captions' offset.</summary>
    private void NoteUserTimingLocked()
    {
        if (_song is not { } song || ShowingVideoKeyLocked is not { } video || Offsets.CaptionedOffset(song.Key, video) is not { } captions) return;
        LogCaptionLocked(MusicVideoCaptionLog.UserLine(Offsets.Offset(song.Key, video), captions));
    }

    private void LogCaptionLocked(string line)
    {
        _captionLog.Add(line);
        if (_captionLog.Count > 20) _captionLog.RemoveRange(0, _captionLog.Count - 20);
        CaptionLogged?.Invoke(line);
    }

    /// <summary>Logs a line about captions that the player side decided (asked for, ignored, empty, none in time).</summary>
    public void LogCaption(string line)
    {
        lock (_gate) LogCaptionLocked(line);
    }

    /// <summary>The video's moments come a quarter second sooner (it lagged the music).</summary>
    public void VideoEarlier() => NudgeOffset(FineNudge);

    /// <summary>The video's moments come a quarter second later (it ran ahead).</summary>
    public void VideoLater() => NudgeOffset(-FineNudge);

    /// <summary>Drops the user's timing for the video on screen: back to the timing from captions, else the estimate.</summary>
    public void ResetOffset()
    {
        lock (_gate)
        {
            if (_song is not { } song) return;
            Offsets.ClearManual(song.Key, ShowingVideoKeyLocked);
            ResetTapsLocked();
        }
        Raise();
    }

    public bool HasSong
    {
        get { lock (_gate) return _song is not null; }
    }

    /// <summary>A video's length, from its player: the starting guess for its intro when nothing better is known.</summary>
    public void NoteVideoDuration(double duration, MusicVideoSource source)
    {
        bool changed;
        lock (_gate)
        {
            if (_song is not { } song || _status is not MusicVideoStatus.Showing showing || showing.Source != source
                || MusicVideoOffsets.VideoKey(source) is not { } video) return;
            if (double.IsFinite(duration) && duration > 0) _videoLengths[video] = duration;
            var estimate = MusicVideoDurationEstimate.Intro(duration, song.Duration);
            var before = CurrentOffsetLocked;
            Offsets.SetEstimate(estimate, song.Key, video);
            changed = CurrentOffsetLocked != before;
        }
        if (changed) Raise();
    }

    /// <summary>The words beside the nudge buttons.</summary>
    public static string? TimingLabel(MusicVideoOffsetOrigin origin) => origin switch
    {
        MusicVideoOffsetOrigin.Manual => "Offset set by you",
        MusicVideoOffsetOrigin.Estimated => "Estimated",
        MusicVideoOffsetOrigin.Captions => "From captions",
        _ => null,
    };

    // ---- Tap to sync ----

    /// <summary>Tap to sync is offered while a video shows for the playing song.</summary>
    public bool CanTapSync
    {
        get { lock (_gate) return _song is not null && ShowingVideoKeyLocked is not null; }
    }

    /// <summary>The video's own position, from its player, read at uptime <paramref name="at"/> (by default now).</summary>
    public void NoteVideoSample(double time, bool playing, MusicVideoSource source, double? at = null)
    {
        if (!double.IsFinite(time) || MusicVideoOffsets.VideoKey(source) is not { } video) return;
        bool changed;
        lock (_gate)
        {
            if (video != ShowingVideoKeyLocked) return;
            _videoSample = (video, time, playing, at ?? _uptime());
            changed = _captionCues?.Video == video && MeasureCaptionsLocked();
        }
        if (changed) Raise();
    }

    // ---- From captions ----

    /// <summary>
    /// Whether the player for <paramref name="source"/> should load its captions: always, for the YouTube video showing
    /// (even one the user timed: the measurement is kept for Reset, while the user's timing stays).
    /// </summary>
    public bool WantsCaptions(MusicVideoSource source)
    {
        lock (_gate)
            return _song is not null && _status is MusicVideoStatus.Showing showing && showing.Source == source && source is MusicVideoSource.YouTube;
    }

    /// <summary>
    /// The language of the song's lyrics, for the caption track to ask for: from the script they are written in (Hangul,
    /// kana, Han); null otherwise, so the player takes the video's own track. The Mac asks NaturalLanguage, which Windows
    /// has no counterpart of here.
    /// </summary>
    public string? CaptionLanguage()
    {
        IReadOnlyList<MusicVideoLyricLine>? lines;
        lock (_gate) lines = _song is { } song ? _lyrics(song) : null;
        if (lines is not { Count: > 0 }) return null;
        int hangul = 0, kana = 0, han = 0, letters = 0;
        foreach (var line in lines.Take(40))
        {
            foreach (var rune in line.Text.EnumerateRunes())
            {
                if (!System.Text.Rune.IsLetter(rune)) continue;
                letters++;
                if (rune.Value is >= 0xAC00 and <= 0xD7AF or >= 0x1100 and <= 0x11FF) hangul++;
                else if (rune.Value is >= 0x3040 and <= 0x30FF) kana++;
                else if (rune.Value is >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF) han++;
            }
        }
        if (letters == 0) return null;
        if (hangul * 4 >= letters) return "ko";
        if (kana > 0 && (kana + han) * 4 >= letters) return "ja";
        if (han * 4 >= letters) return "zh";
        return null;
    }

    /// <summary>A caption track the showing video's player loaded.</summary>
    public void NoteCaptions(ReadOnlySpan<byte> data, MusicVideoSource source)
    {
        var cues = MusicVideoCaptionSync.Cues(data);
        bool changed;
        lock (_gate)
        {
            if (_status is not MusicVideoStatus.Showing showing || showing.Source != source || MusicVideoOffsets.VideoKey(source) is not { } video
                || cues.Count == 0)
            {
                return;
            }
            _captionCues = (video, cues);
            // New captions (a later load of the video): measured afresh. YouTube sometimes sends its automatic track
            // without word timing, which seldom gives a trustworthy offset; the next load may do better.
            if (_song is { } song) _captionMeasured.Remove(song.Key + "\u0001" + video);
            changed = MeasureCaptionsLocked();
        }
        if (changed) Raise();
    }

    /// <summary>
    /// Measures the offset once both the captions and the song's timed lyrics are here (whichever comes second), once per
    /// song and video. Used only when trustworthy; then it sets the timing from captions, while the user's own timing for
    /// this video, if any, stays in force. True when the timing in force may have changed.
    /// </summary>
    private bool MeasureCaptionsLocked()
    {
        if (_song is not { } song || ShowingVideoKeyLocked is not { } video || _captionCues is not { } captured || captured.Video != video) return false;
        var attempt = song.Key + "\u0001" + video;
        if (_captionMeasured.Contains(attempt)) return false;
        if (_lyrics(song) is not { Count: > 0 } lines)
        {
            if (_captionWaitLogged.Add(attempt)) LogCaptionLocked(MusicVideoCaptionLog.WaitingLine);
            return false;
        }
        _captionMeasured.Add(attempt);
        var result = MusicVideoCaptionSync.Measure(captured.Cues, lines, MusicVideoTiming.OffsetMin, MusicVideoTiming.OffsetMax);
        LastCaptionResult = result;
        if (result.Offset is not { } offset)
        {
            LogCaptionLocked(MusicVideoCaptionLog.MeasuredLine(result, userOffset: null));
            return false;
        }
        Offsets.SetCaptioned(offset, song.Key, video);
        double? user = Offsets.Origin(song.Key, video) == MusicVideoOffsetOrigin.Manual ? Offsets.Offset(song.Key, video) : null;
        LogCaptionLocked(MusicVideoCaptionLog.MeasuredLine(result, user));
        return true;
    }

    /// <summary>Where the video is now: its player's last report, moved on while it plays; else where the sync keeps it.</summary>
    private double VideoTimeNowLocked(double songTime)
    {
        if (_videoSample is { } sample && sample.Video == ShowingVideoKeyLocked)
        {
            var age = _uptime() - sample.At;
            if (age is >= 0 and < 1.5) return sample.Time + (sample.Playing ? age : 0);
        }
        return songTime + CurrentOffsetLocked;
    }

    /// <summary>The button: arms, takes the tap, or arms again to refine.</summary>
    public void TapSyncButton()
    {
        if (TapState is MusicVideoTapState.Armed) Tap();
        else BeginTapSync();
    }

    /// <summary>Waits for the tap: a key, or the button again.</summary>
    public void BeginTapSync()
    {
        lock (_gate)
        {
            if (_song is not { } song || ShowingVideoKeyLocked is null) return;
            if (_lyrics(song) is not { Count: > 0 } lines)
            {
                _tapState = new MusicVideoTapState.Failed(MusicVideoTapFailure.NoLyrics);
            }
            else
            {
                var clock = _songClock();
                if (!clock.Playing)
                {
                    _tapState = new MusicVideoTapState.Failed(MusicVideoTapFailure.NotPlaying);
                }
                else
                {
                    _tapState = new MusicVideoTapState.Armed(MusicVideoTapSync.ArmedBeforeFirstLine(clock.Time, lines));
                    StartTapExpiryLocked();
                }
            }
        }
        Raise();
    }

    /// <summary>
    /// The tap: the singing starts (or the next line is sung) in the video now. The offset becomes the user's, averaged
    /// with this song and video's earlier taps.
    /// </summary>
    public void Tap()
    {
        lock (_gate)
        {
            if (_tapState is not MusicVideoTapState.Armed armed) return;
            StopTapExpiryLocked();
            if (_song is not { } song || ShowingVideoKeyLocked is not { } video)
            {
                _tapState = MusicVideoTapState.Idle;
            }
            else if (_lyrics(song) is not { Count: > 0 } lines)
            {
                _tapState = new MusicVideoTapState.Failed(MusicVideoTapFailure.NoLyrics);
            }
            else
            {
                var clock = _songClock();
                if (!clock.Playing)
                {
                    _tapState = new MusicVideoTapState.Failed(MusicVideoTapFailure.NotPlaying);
                }
                else
                {
                    var marked = VideoTimeNowLocked(clock.Time) - MusicVideoTapSync.ReactionTime;
                    // The song time the video shows, by the offset in force.
                    var reference = marked - CurrentOffsetLocked;
                    if (MusicVideoTapSync.Line(lines, reference, armed.FirstLine) is not { } line)
                    {
                        _tapState = new MusicVideoTapState.Failed(MusicVideoTapFailure.NoLyrics);
                    }
                    else
                    {
                        _taps.Add(MusicVideoTapSync.Offset(marked, line.Start, reaction: 0));
                        Offsets.Set(MusicVideoTapSync.Averaged(_taps) ?? 0, song.Key, video);
                        NoteUserTimingLocked();
                        _tapState = new MusicVideoTapState.Done(CurrentOffsetLocked, _taps.Count, armed.FirstLine);
                    }
                }
            }
        }
        Raise();
    }

    public void CancelTapSync()
    {
        lock (_gate)
        {
            StopTapExpiryLocked();
            if (_tapState is MusicVideoTapState.Armed)
                _tapState = _taps.Count == 0 ? MusicVideoTapState.Idle : new MusicVideoTapState.Done(CurrentOffsetLocked, _taps.Count, false);
        }
        Raise();
    }

    /// <summary>
    /// A key pressed in one of Lunalyr's own windows while Tap to sync is armed (the Mac watches keys in its own windows the
    /// same way). True when the key was Tap to sync's.
    /// </summary>
    public bool TapKey(int virtualKey, bool shortcut, bool isRepeat, bool textInputFocused)
    {
        if (TapState is not MusicVideoTapState.Armed) return false;
        switch (MusicVideoTapSync.KeyActionFor(virtualKey, shortcut, isRepeat, textInputFocused))
        {
            case MusicVideoTapSync.KeyAction.Cancel:
                CancelTapSync();
                return true;
            case MusicVideoTapSync.KeyAction.Tap:
                Tap();
                return true;
            default:
                return false;
        }
    }

    private void ResetTapsLocked()
    {
        StopTapExpiryLocked();
        _taps.Clear();
        _tapState = MusicVideoTapState.Idle;
    }

    private void StartTapExpiryLocked()
    {
        StopTapExpiryLocked();
        var cts = new CancellationTokenSource();
        _tapExpiry = cts;
        _ = Task.Delay(TimeSpan.FromSeconds(MusicVideoTapSync.ArmedLimit), cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) CancelTapSync();
        }, TaskScheduler.Default);
    }

    private void StopTapExpiryLocked()
    {
        _tapExpiry?.Cancel();
        _tapExpiry = null;
    }

    // ---- Align by hand ----

    public MusicVideoAlignSession? Align
    {
        get { lock (_gate) return _align; }
    }

    public MusicVideoAlignDrag? AlignDrag
    {
        get { lock (_gate) return _alignDrag; }
    }

    public bool AlignTouched
    {
        get { lock (_gate) return _alignTouched; }
    }

    /// <summary>Align mode is on, for the song and the video on screen now.</summary>
    public bool IsAligning
    {
        get { lock (_gate) return IsAligningLocked; }
    }

    private bool IsAligningLocked => _align is { } align && _song is { } song && align.SongKey == song.Key && align.Video == ShowingVideoKeyLocked;

    /// <summary>A drag on the video's bar is in flight, and a button is down.</summary>
    public bool IsAlignDragging
    {
        get { lock (_gate) return IsAlignDraggingLocked; }
    }

    private bool IsAlignDraggingLocked => _alignDrag is not null && IsAligningLocked && ButtonsHeld();

    private bool VideoRespondingLocked
    {
        get
        {
            if (_videoSample is not { } sample || sample.Video != ShowingVideoKeyLocked) return false;
            var age = _uptime() - sample.At;
            return age >= 0 && age < RespondingWithin;
        }
    }

    /// <summary>Whether align mode can start (or carry on), and if not, why.</summary>
    public MusicVideoAlignAvailability AlignAvailability
    {
        get { lock (_gate) return AlignAvailabilityLocked; }
    }

    private MusicVideoAlignAvailability AlignAvailabilityLocked =>
        MusicVideoAlignAvailability.Of(_enabled && _song is not null && ShowingVideoKeyLocked is not null, ReduceMotion(),
            _songClock().Playing, VideoRespondingLocked);

    /// <summary>The video's length on screen, or null before its player said.</summary>
    public double? ShowingVideoLength
    {
        get { lock (_gate) return ShowingVideoKeyLocked is { } key && _videoLengths.TryGetValue(key, out var length) ? length : null; }
    }

    /// <summary>What the video's bar draws now, null outside align mode.</summary>
    public MusicVideoAlignDisplay? AlignDisplay(double? songTime = null)
    {
        lock (_gate)
        {
            if (!IsAligningLocked || _song is not { } song) return null;
            var offset = CurrentOffsetLocked;
            var reported = ShowingVideoKeyLocked is { } key && _videoLengths.TryGetValue(key, out var length) ? length : (double?)null;
            return new MusicVideoAlignDisplay(MusicVideoAlignBar.VideoLength(reported, song.Duration, offset), song.Duration, offset,
                songTime ?? _songClock().Time, Offsets.Origin(song.Key, ShowingVideoKeyLocked), _alignDrag is not null,
                _alignDrag?.RateLabel, _alignTouched, AlignAvailabilityLocked.IsAvailable);
        }
    }

    /// <summary>Starts align mode. False (nothing changes) when it can't start.</summary>
    public bool BeginAlign()
    {
        bool cancelTap;
        lock (_gate)
        {
            if (IsAligningLocked) return true;
            if (!AlignAvailabilityLocked.IsAvailable || _song is not { } song || ShowingVideoKeyLocked is not { } video) return false;
            cancelTap = _tapState is MusicVideoTapState.Armed;
            _align = new MusicVideoAlignSession(song.Key, video, Offsets.ManualEntryFor(song.Key));
            _alignDrag = null;
            _alignTouched = false;
        }
        if (cancelTap) CancelTapSync();
        Raise();
        return true;
    }

    /// <summary>A press on the video's bar at <paramref name="x"/> (DIPs from its leading edge, <paramref name="width"/> wide), <paramref name="y"/> from its centre line.</summary>
    public void AlignMouseDown(double x, double y, double width, bool fine = false)
    {
        lock (_gate)
        {
            // A press left over from a release that never came is replaced.
            if (!IsAligningLocked || _song is not { } song) return;
            var clock = _songClock();
            _alignDrag = null;
            var offset = CurrentOffsetLocked;
            var reported = ShowingVideoKeyLocked is { } key && _videoLengths.TryGetValue(key, out var l) ? l : (double?)null;
            var length = MusicVideoAlignBar.VideoLength(reported, song.Duration, offset);
            var knob = MusicVideoAlignBar.LayoutOf(length, song.Duration, offset, clock.Time, width).Knob;
            _alignDrag = new MusicVideoAlignDrag(x, y, knob, offset, MusicVideoAlignBar.SecondsPerPoint(length, width),
                MusicVideoAlignBar.Bounds(length, clock.Time, offset), fine);
        }
        Raise();
    }

    /// <summary>The pointer moved with the button down. True when the knob just reached an end of the video.</summary>
    public bool AlignMouseDragged(double x, double y, bool fine = false)
    {
        bool reached;
        lock (_gate)
        {
            if (!IsAligningLocked || _alignDrag is not { } drag) return false;
            reached = drag.Move(x, y, fine);
        }
        Raise();
        return reached;
    }

    public void AlignModifiersChanged(bool fine)
    {
        lock (_gate)
        {
            if (!IsAligningLocked || _alignDrag is not { } drag) return;
            drag.ModifiersChanged(fine);
        }
        Raise();
    }

    /// <summary>The release keeps the offset under the hand, for this song's video, as the user's own.</summary>
    public void AlignMouseUp(double x, double y, bool fine = false)
    {
        lock (_gate)
        {
            if (!IsAligningLocked || _alignDrag is null)
            {
                _alignDrag = null;
                return;
            }
        }
        AlignMouseDragged(x, y, fine);
        lock (_gate)
        {
            if (_alignDrag is not { } drag || _song is not { } song)
            {
                _alignDrag = null;
                return;
            }
            _alignDrag = null;
            if (!drag.Changed) return;
            Offsets.Set(drag.Offset, song.Key, ShowingVideoKeyLocked);
            NoteUserTimingLocked();
            ResetTapsLocked();
            _alignTouched = true;
        }
        Raise();
    }

    /// <summary>← → (± a tenth), Shift+← Shift+→ (± a second): kept at once, like Video earlier and later.</summary>
    public void AlignNudge(double delta)
    {
        lock (_gate)
        {
            if (!IsAligningLocked || _alignDrag is not null) return;
        }
        NudgeOffset(delta);
        lock (_gate) _alignTouched = true;
        Raise();
    }

    /// <summary>Reset: back to the estimate; align mode stays on to see it.</summary>
    public void AlignReset()
    {
        lock (_gate)
        {
            if (!IsAligningLocked) return;
            _alignDrag = null;
        }
        ResetOffset();
        lock (_gate) _alignTouched = true;
        Raise();
    }

    /// <summary>Esc: a drag in flight springs back; otherwise the offset from before align mode is put back and align mode ends.</summary>
    public void AlignCancel()
    {
        var reset = false;
        lock (_gate)
        {
            if (!IsAligningLocked || _align is not { } align)
            {
                EndAlignLocked();
            }
            else if (_alignDrag is not null)
            {
                _alignDrag = null;
            }
            else if (_alignTouched)
            {
                EndAlignLocked();
                if (align.Restore is not null)
                {
                    Offsets.RestoreManual(align.Restore, align.SongKey);
                    ResetTapsLocked();
                }
                else
                {
                    reset = true;
                }
            }
            else
            {
                EndAlignLocked();
            }
        }
        if (reset) ResetOffset();
        Raise();
    }

    /// <summary>Done (Enter): keeps what was set and ends align mode.</summary>
    public void AlignDone()
    {
        // A press still in flight (its release never came): kept as it is.
        if (AlignDrag is not null) AlignMouseUp(double.NaN, double.NaN);
        lock (_gate) EndAlignLocked();
        Raise();
    }

    /// <summary>The film button: on, or off keeping what was set.</summary>
    public void ToggleAlign()
    {
        if (IsAligning) AlignDone();
        else BeginAlign();
    }

    /// <summary>Ends align mode where it is (a song change, a new video).</summary>
    public void EndAlign()
    {
        lock (_gate) EndAlignLocked();
        Raise();
    }

    private void EndAlignLocked()
    {
        _align = null;
        _alignDrag = null;
        _alignTouched = false;
    }

    // ---- Settings copy ----

    /// <summary>Windows: YouTube answered its search with a consent, bot-check or sign-in page, which Lunalyr never works around.</summary>
    public const string UnavailableNote = "YouTube’s search isn’t available right now.";

    /// <summary>One line for Settings about what the wallpaper is showing.</summary>
    /// <param name="fallback">What shows without a video, by the type's name ("Line patterns").</param>
    /// <param name="searchUnavailable">Windows: YouTube's search page isn't available (<see cref="YouTubeSearchUnavailable"/>).</param>
    public static string? Note(MusicVideoStatus status, bool online, string fallback, bool searchUnavailable = false)
    {
        switch (status)
        {
            case MusicVideoStatus.Off:
                return null;
            case MusicVideoStatus.Idle:
                return "Play a song to see its music video.";
            case MusicVideoStatus.Searching s:
                return $"Looking for the video for “{s.Title}”…";
            case MusicVideoStatus.Showing { Source: MusicVideoSource.Local } s:
                return $"Showing your video file for “{s.Title}”.";
            case MusicVideoStatus.Showing s:
                return $"Showing the official video for “{s.Title}” from YouTube.";
            case MusicVideoStatus.Missing m:
                var instead = $"Showing {fallback} instead.";
                var title = m.Title;
                return m.Miss switch
                {
                    MusicVideoMiss.NotInLibrary => $"No video file is chosen for “{title}”. {instead}"
                        + (online ? "" : " Choose a file, turn on Search YouTube, or paste a link below."),
                    MusicVideoMiss.ProtectedInLibrary => $"The video file for “{title}” is copy-protected, so Lunalyr can’t play it. {instead}",
                    MusicVideoMiss.NotFound => $"No official video found for “{title}”. {instead} You can paste a YouTube link for it below.",
                    MusicVideoMiss.NotEmbeddable => $"The video for “{title}” can’t be shown outside YouTube. {instead}",
                    MusicVideoMiss.RateLimited => $"Too many lookups just now; trying again shortly. {instead}",
                    MusicVideoMiss.LookupFailed when searchUnavailable => $"{UnavailableNote} {instead} You can paste a YouTube link for it below.",
                    MusicVideoMiss.LookupFailed => $"Couldn’t search YouTube just now. {instead} You can paste a YouTube link for it below.",
                    MusicVideoMiss.NotASong => $"Radio and adverts have no video. {instead}",
                    MusicVideoMiss.PlayerUnreachable => $"Couldn’t load YouTube’s player for “{title}” just now; trying again shortly. {instead}",
                    MusicVideoMiss.LocalFileFailed => $"The video file for “{title}” wouldn’t play here. {instead}",
                    MusicVideoMiss.NoApiKey => $"Searching with your API key needs a YouTube Data API key: add yours below, or search YouTube’s site instead. {instead}",
                    MusicVideoMiss.KeyRejected => $"YouTube refused the API key, so it can’t be searched. Check the key below. {instead}",
                    MusicVideoMiss.QuotaExceeded => $"The API key’s daily YouTube search allowance is used up; searching again once it resets. {instead}",
                    _ => instead,
                };
            default:
                return null;
        }
    }
}
