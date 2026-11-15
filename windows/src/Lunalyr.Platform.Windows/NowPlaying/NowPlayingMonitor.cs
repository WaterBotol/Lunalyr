using Lunalyr.Core.NowPlaying;

namespace Lunalyr.Platform.NowPlaying;

/// <summary>
/// Follows the system's current media session and keeps a <see cref="SongClock"/>, the track and the player's
/// capabilities up to date from it. Read-only: it never sends a command to any player.
/// </summary>
/// <remarks>
/// <para>
/// On a session switch the clock is reset for the new player (its learned timeline quality included) and the old
/// session's events are ignored from then on. On a track change (a different title/artist/album) the clock is reset
/// for the new track. Metadata reads that complete out of order are dropped.
/// </para>
/// <para>
/// Events are raised on whichever thread the source raised its event on. <see cref="SongClock.SeekDetected"/> is
/// raised from inside the monitor's update, so its handlers must be quick and must not wait on other threads.
/// </para>
/// </remarks>
public sealed class NowPlayingMonitor : IDisposable
{
    private readonly IMediaSessionSource _source;
    private readonly Action<string>? _log;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _disposeCts = new();

    private IMediaSession? _session;
    private MediaTrackInfo? _track;
    private MediaPlaybackInfo _playback = MediaPlaybackInfo.Closed;
    private PlayerCapabilities _capabilities = PlayerCapabilities.None;
    private long _propertiesRequest;
    private bool _started;
    private bool _disposed;

    public NowPlayingMonitor(IMediaSessionSource source, TimeProvider time, SongClockOptions? clockOptions = null, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(time);
        _source = source;
        _log = log;
        Clock = new SongClock(time, clockOptions, log);
    }

    /// <summary>The song position for the followed session. The same instance for the monitor's lifetime.</summary>
    public SongClock Clock { get; }

    /// <summary>The followed session, or null when nothing is playing.</summary>
    public IMediaSession? Session
    {
        get { lock (_gate) return _session; }
    }

    /// <summary>The current track, or null until its metadata has been read.</summary>
    public MediaTrackInfo? Track
    {
        get { lock (_gate) return _track; }
    }

    public MediaPlaybackInfo PlaybackInfo
    {
        get { lock (_gate) return _playback; }
    }

    public PlayerCapabilities Capabilities
    {
        get { lock (_gate) return _capabilities; }
    }

    /// <summary>The followed session changed (possibly to none).</summary>
    public event EventHandler? SessionChanged;

    /// <summary>A different track started (or the first track of a session became known).</summary>
    public event EventHandler? TrackChanged;

    /// <summary>Status, rate or enabled controls changed (so <see cref="Capabilities"/> may have).</summary>
    public event EventHandler? PlaybackChanged;

    /// <summary>Subscribes to the source and attaches to its current session.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return;
            _started = true;
        }

        _source.CurrentSessionChanged += OnSourceChanged;
        _source.SessionsChanged += OnSourceChanged;
        FollowCurrentSession();
    }

    public void Dispose()
    {
        IMediaSession? session;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            session = _session;
            _session = null;
        }

        _source.CurrentSessionChanged -= OnSourceChanged;
        _source.SessionsChanged -= OnSourceChanged;
        if (session is not null) Unsubscribe(session);
        _disposeCts.Cancel();
        _disposeCts.Dispose();
    }

    private void OnSourceChanged(object? sender, EventArgs e) => FollowCurrentSession();

    private void FollowCurrentSession()
    {
        IMediaSession? current;
        try
        {
            current = _source.CurrentSession;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Now playing: couldn't read the current session: {ex.Message}");
            return;
        }

        IMediaSession? old;
        long request;
        lock (_gate)
        {
            if (_disposed || ReferenceEquals(current, _session)) return;
            old = _session;
            _session = current;
            _track = null;
            request = ++_propertiesRequest;

            // Subscribe before the first reads so no update falls in between (a seek-only player wouldn't repeat it).
            // Handlers check the sender against _session, and block on the lock until this attach is done.
            if (old is not null) Unsubscribe(old);
            if (current is not null) Subscribe(current);

            Clock.ResetSession();
            // Apple Music for Windows cuts its position to whole seconds: let the clock put the fraction back.
            var appleMusic = current?.SourceApp.Kind == MediaSourceKind.AppleMusic;
            Clock.CorrectsWholeSeconds = appleMusic;
            // …and it publishes the position from its previous tick, about a second behind the sound.
            Clock.ReportLag = appleMusic ? SongClock.AppleMusicReportLag : TimeSpan.Zero;
            if (current is null)
            {
                _playback = MediaPlaybackInfo.Closed;
                _capabilities = PlayerCapabilities.None;
            }
            else
            {
                _playback = current.GetPlaybackInfo();
                _capabilities = PlayerCapabilities.From(_playback, current.SourceApp);
                Clock.UpdatePlayback(_playback);
                if (current.GetTimeline() is { } timeline) Clock.UpdateTimeline(timeline);
            }
        }

        _log?.Invoke(current is null ? "Now playing: no session." : $"Now playing: following {current.SourceAppUserModelId}.");
        SessionChanged?.Invoke(this, EventArgs.Empty);
        PlaybackChanged?.Invoke(this, EventArgs.Empty);
        if (current is not null) _ = RefreshTrackAsync(current, request);
    }

    private void Subscribe(IMediaSession session)
    {
        session.MediaPropertiesChanged += OnMediaPropertiesChanged;
        session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        session.TimelinePropertiesChanged += OnTimelineChanged;
    }

    private void Unsubscribe(IMediaSession session)
    {
        session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
        session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        session.TimelinePropertiesChanged -= OnTimelineChanged;
    }

    private void OnTimelineChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (!IsCurrent(sender, out var session)) return;
            if (session.GetTimeline() is { } timeline) Clock.UpdateTimeline(timeline);
        }
    }

    private void OnPlaybackInfoChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (!IsCurrent(sender, out var session)) return;
            _playback = session.GetPlaybackInfo();
            _capabilities = PlayerCapabilities.From(_playback, session.SourceApp);
            Clock.UpdatePlayback(_playback);
        }

        PlaybackChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnMediaPropertiesChanged(object? sender, EventArgs e)
    {
        long request;
        IMediaSession session;
        lock (_gate)
        {
            if (!IsCurrent(sender, out session)) return;
            request = ++_propertiesRequest;
        }

        _ = RefreshTrackAsync(session, request);
    }

    private async Task RefreshTrackAsync(IMediaSession session, long request)
    {
        MediaTrackInfo? info;
        try
        {
            info = await session.GetMediaPropertiesAsync(_disposeCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Now playing: couldn't read the track from {session.SourceAppUserModelId}: {ex.Message}");
            return;
        }

        var changed = false;
        lock (_gate)
        {
            // A newer read was started, or the session changed, while this one was in flight.
            if (_disposed || request != _propertiesRequest || !ReferenceEquals(session, _session)) return;

            // Players briefly report an empty title between tracks: keep the last real track until the next one.
            if (info is null || info.IsEmpty) return;

            if (_track is null)
            {
                changed = true;
            }
            else if (info.TrackKey != _track.TrackKey)
            {
                changed = true;
                Clock.ResetTrack();
                if (session.GetTimeline() is { } timeline) Clock.UpdateTimeline(timeline);
            }

            _track = info;
        }

        if (changed)
        {
            _log?.Invoke("Now playing: track changed.");
            TrackChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool IsCurrent(object? sender, out IMediaSession session)
    {
        session = _session!;
        return !_disposed && _session is not null && ReferenceEquals(sender, _session);
    }
}
