using Lunalyr.Core.Lyrics;
using Lunalyr.Core.MusicVideo;
using Lunalyr.Core.NowPlaying;
using Lunalyr.Render.Backgrounds;
using Lunalyr.Shell.MusicVideo;
using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell;

// The Music video wallpaper type (milestone 6; the Mac's MusicVideoController.start and NowPlayingView's music video
// layer choice, Lunalyr's own code). The controller follows the playing song once its length is known (the recording
// key needs it), looks its video up only while Music video is the type, and the background is:
// - the video file's layer ("musicVideo") or YouTube's see-through layer ("musicVideo.youtube") when one shows;
// - the album colours (the "musicVideo" layer with no file: the Mac's musicVideoPending) while the song's video is still
//   being looked for, for up to MusicVideoWait after the song changed;
// - otherwise the type chosen as "Without a video" (musicVideoFallback), drawn as that type draws.
// The video follows the player's own position (never the lyrics' clock or the scrub preview), plus the video offset.
public sealed partial class AppController
{
    /// <summary>How long a song change shows the album colours while its video is looked for (the Mac's musicVideoWait).</summary>
    public static readonly TimeSpan MusicVideoWait = TimeSpan.FromSeconds(3);

    /// <summary>The wait used (<see cref="MusicVideoWait"/>; tests shorten it).</summary>
    public TimeSpan MusicVideoPendingWait { get; set; } = MusicVideoWait;

    private MusicVideoController? _musicVideo;
    private bool _youTubePlayer;
    private MusicVideoTrack? _musicVideoTrack;
    private bool _musicVideoAwaiting;
    private bool _musicVideoOverdue;
    private CancellationTokenSource? _musicVideoWaitCts;
    private string? _musicVideoLayer;
    private volatile MusicVideoSource? _musicVideoShowing;

    /// <summary>The music video controller, or null when this run has none.</summary>
    public MusicVideoController? MusicVideo => _musicVideo;

    /// <summary>A YouTube player can be shown on the desktop this run (its host was made).</summary>
    public bool YouTubePlayerAvailable => _youTubePlayer;

    /// <summary>The track the music video follows, once its length is known.</summary>
    public MusicVideoTrack? MusicVideoTrack
    {
        get { lock (_gate) return _musicVideoTrack; }
    }

    /// <summary>
    /// Connects the music video: the controller (built by the app with this controller's clock and lyrics), the layers'
    /// feed, and whether a YouTube player can be shown. Call once, before or after <see cref="Start"/>.
    /// </summary>
    public void UseMusicVideo(MusicVideoController controller, MusicVideoFeed feed, bool youTubePlayer)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(feed);
        _musicVideo = controller;
        _youTubePlayer = youTubePlayer;
        controller.ReduceMotion = () => Scene.ReduceMotion;
        lock (_gate)
        {
            // Connected after start-up: the song already playing (its length known or still awaited) is taken up now.
            if (_query is { } query && _lookupTrack is { } track)
                _musicVideoTrack = new MusicVideoTrack(track.Title, track.Artist, query.Duration, track.AlbumTitle.Length > 0 ? track.AlbumTitle : null);
            else _musicVideoAwaiting = _lookupTrack is not null;
            if (_musicVideoAwaiting) _musicVideoWaitCts = new CancellationTokenSource();
        }
        if (_musicVideoWaitCts is { } wait) Track(WaitForMusicVideoAsync(wait));
        feed.Attach(ReadMusicVideo, ReportMusicVideo, MusicVideoFileFailed);
        controller.Changed += OnMusicVideoChanged;
        UpdateMusicVideo();
    }

    /// <summary>The player's own position and whether it moves (no lyric offsets, no scrub preview); NaN with none.</summary>
    public (double Time, bool Playing) ReadSongPosition()
    {
        if (Volatile.Read(ref _hasSession) == 0) return (double.NaN, false);
        var reading = Monitor.Clock.Read();
        return reading.HasTimeline ? (reading.Position.TotalSeconds, reading.IsPlaying) : (double.NaN, false);
    }

    /// <summary>
    /// The song's timed lines in the song's own time (the lyric offsets taken back off), for the video's Tap to sync; null
    /// while none are shown for <paramref name="song"/> (the Mac's MusicVideoTapSync.currentLyrics).
    /// </summary>
    public IReadOnlyList<MusicVideoLyricLine>? MusicVideoLyrics(MusicVideoSong song)
    {
        LyricResult? shown;
        LyricQuery? query;
        lock (_gate)
        {
            shown = _shown;
            query = _query;
        }
        if (query is null || query.RecordingKey != song.Key || shown is not LyricResult.Synced synced) return null;
        var shift = Volatile.Read(ref _globalOffset) + _offsets.OffsetFor(query.RecordingKey);
        var lines = synced.Lines.Where(l => !l.IsInstrumental && l.Credit != true && !string.IsNullOrWhiteSpace(l.Text))
            .Select(l => new MusicVideoLyricLine(l.Start - shift, l.Text)).ToList();
        return lines.Count == 0 ? null : lines;
    }

    // ---- Following the song (hooks called from AppController.cs and AppController.Wallpaper.cs) ----

    /// <summary>The track changed (or nothing plays now); its length isn't known yet.</summary>
    partial void MusicVideoTrackChanging(bool nothingPlaying);

    /// <summary>The song's length arrived: its recording key can be made.</summary>
    partial void MusicVideoSongKnown(MediaTrackInfo track, double duration);

    /// <summary>The track has no length: a stream or radio, which has no video.</summary>
    partial void MusicVideoNotASong(MediaTrackInfo track);

    partial void MusicVideoSettingsChanged(AppSettings old, AppSettings settings);

    partial void MusicVideoBackground(AppSettings settings, ref string? layer);

    partial void MusicVideoTrackChanging(bool nothingPlaying)
    {
        if (_musicVideo is null) return;
        CancellationTokenSource? wait = null;
        lock (_gate)
        {
            _musicVideoTrack = null;
            _musicVideoShowing = null;
            _musicVideoWaitCts?.Cancel();
            _musicVideoWaitCts = null;
            _musicVideoAwaiting = !nothingPlaying;
            _musicVideoOverdue = false;
            if (!nothingPlaying)
            {
                wait = new CancellationTokenSource();
                _musicVideoWaitCts = wait;
            }
        }
        if (wait is not null) Track(WaitForMusicVideoAsync(wait));
        UpdateMusicVideo();
    }

    private async Task WaitForMusicVideoAsync(CancellationTokenSource wait)
    {
        try
        {
            await Task.Delay(MusicVideoPendingWait, wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        lock (_gate)
        {
            if (!ReferenceEquals(_musicVideoWaitCts, wait)) return;
            _musicVideoOverdue = true;
        }
        RefreshMusicVideoLayers();
    }

    partial void MusicVideoSongKnown(MediaTrackInfo track, double duration)
    {
        if (_musicVideo is null) return;
        lock (_gate)
        {
            _musicVideoTrack = new MusicVideoTrack(track.Title, track.Artist, duration, track.AlbumTitle.Length > 0 ? track.AlbumTitle : null);
            _musicVideoAwaiting = false;
        }
        UpdateMusicVideo();
    }

    partial void MusicVideoNotASong(MediaTrackInfo track)
    {
        if (_musicVideo is null) return;
        lock (_gate)
        {
            _musicVideoTrack = new MusicVideoTrack(track.Title, track.Artist, 0, null, IsStream: true);
            _musicVideoAwaiting = false;
        }
        UpdateMusicVideo();
    }

    partial void MusicVideoSettingsChanged(AppSettings old, AppSettings settings)
    {
        if (_musicVideo is null) return;
        if (old.BackgroundMode != settings.BackgroundMode || old.MusicVideoOnline != settings.MusicVideoOnline) UpdateMusicVideo();
        if (old.MusicVideoFraming != settings.MusicVideoFraming)
        {
            FollowMusicVideoShape();
            // YouTube's player host listens: it sizes the WebView2 and the page's box at once rather than at its next check.
            RaiseStatusChanged();
        }
    }

    private void UpdateMusicVideo()
    {
        if (_musicVideo is not { } controller) return;
        AppSettings settings;
        MusicVideoTrack? track;
        lock (_gate)
        {
            if (_disposed) return;
            settings = _settings;
            track = _musicVideoTrack;
        }
        controller.Update(track, settings.BackgroundMode == "musicVideo", settings.MusicVideoOnline);
        OnMusicVideoChanged();
    }

    private void OnMusicVideoChanged()
    {
        if (_musicVideo is not { } controller) return;
        MusicVideoTrack? track;
        lock (_gate) track = _musicVideoTrack;
        _musicVideoShowing = controller.SourceFor(track);
        FollowMusicVideoStill();
        FollowMusicVideoShape();
        RefreshMusicVideoLayers();
        RaiseStatusChanged();
    }

    /// <summary>Hands the renderer the music video's background whenever what it shows changes.</summary>
    private void RefreshMusicVideoLayers()
    {
        AppSettings settings;
        lock (_gate)
        {
            if (!_started || _disposed) return;
            settings = _settings;
        }
        if (settings.BackgroundMode != "musicVideo") return;
        var layer = BackgroundLayerFor(settings);
        lock (_gate)
        {
            if (layer == _musicVideoLayer) return;
            _musicVideoLayer = layer;
        }
        _app.Info($"Music video: the wallpaper draws {layer} ({DescribeMusicVideo()}).");
        PublishLayers();
    }

    private string DescribeMusicVideo() => _musicVideo?.Status switch
    {
        MusicVideoStatus.Showing { Source: MusicVideoSource.Local } => "the song's video file",
        MusicVideoStatus.Showing => "a YouTube video",
        MusicVideoStatus.Searching => "looking for the video",
        MusicVideoStatus.Missing m => $"no video: {m.Miss}",
        MusicVideoStatus.Idle => "nothing playing",
        _ => "off",
    };

    /// <summary>The music video's background for <paramref name="settings"/> (hook in AppController.Wallpaper.cs).</summary>
    partial void MusicVideoBackground(AppSettings settings, ref string? layer)
    {
        if (_musicVideo is not { } controller) return;
        MusicVideoTrack? track;
        bool awaiting, overdue;
        lock (_gate)
        {
            track = _musicVideoTrack;
            awaiting = _musicVideoAwaiting;
            overdue = _musicVideoOverdue;
        }
        switch (controller.SourceFor(track))
        {
            case MusicVideoSource.Local:
                layer = MusicVideoLayerFactory.LayerId;
                return;
            case MusicVideoSource.YouTube when _youTubePlayer:
                layer = YouTubeVideoLayerFactory.LayerId;
                return;
        }
        var pending = !overdue && Volatile.Read(ref _hasSession) != 0
            && (awaiting || (track is not null && controller.Status is MusicVideoStatus.Searching));
        if (pending) layer = MusicVideoLayerFactory.LayerId;
    }

    // ---- The layers' feed ----

    private MusicVideoInputs ReadMusicVideo()
    {
        var (time, playing) = ReadSongPosition();
        var settings = _settings;
        var path = _musicVideoShowing is MusicVideoSource.Local local ? local.Path : null;
        return new MusicVideoInputs(path, time, playing, _musicVideo?.CurrentOffset ?? 0, settings.MusicVideoDim, settings.MusicVideoBlur,
            Still: _musicVideoStill, YouTubeFraming: settings.YouTubeFraming, FileFraming: settings.FileFraming,
            YouTubeAspect: MusicVideoAspect);
    }

    private void ReportMusicVideo(string path, double? duration, double videoTime, bool playing)
    {
        if (_musicVideo is not { } controller) return;
        var source = new MusicVideoSource.Local(path);
        if (duration is { } length) controller.NoteVideoDuration(length, source);
        controller.NoteVideoSample(videoTime, playing, source);
    }

    private void MusicVideoFileFailed(string path, string reason)
    {
        _app.Warn($"Music video: the song's video file can't be shown ({reason}).");
        _musicVideo?.ReportPlaybackFailure(new MusicVideoSource.Local(path));
    }
}
