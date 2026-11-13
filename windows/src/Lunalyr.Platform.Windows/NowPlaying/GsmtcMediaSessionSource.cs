using Lunalyr.Core.NowPlaying;
using Lunalyr.Core.Playback;
using Windows.Media.Control;
using Windows.Storage.Streams;
using CoreRepeatMode = Lunalyr.Core.NowPlaying.MediaRepeatMode;
using CorePlaybackType = Lunalyr.Core.NowPlaying.MediaPlaybackType;
using GsmtcManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using GsmtcSession = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using WinRepeatMode = Windows.Media.MediaPlaybackAutoRepeatMode;

namespace Lunalyr.Platform.NowPlaying;

/// <summary>
/// <see cref="IMediaSessionSource"/> over the real Global System Media Transport Controls
/// (<c>Windows.Media.Control</c>). Nothing touches GSMTC until <see cref="CreateAsync"/> is called.
/// </summary>
/// <remarks>
/// Reading is harmless, but the sessions' <c>Try…Async</c> commands control the user's real player. Only UI code may
/// call them, in direct response to an explicit user action. Tests and checks use a fake source instead. Every
/// command also asks the <see cref="PlaybackGuard"/> given to <see cref="CreateAsync"/>, which refuses them all while
/// <c>--no-playback</c> is set.
/// </remarks>
public sealed class GsmtcMediaSessionSource : IMediaSessionSource
{
    private readonly GsmtcManager _manager;
    private readonly PlaybackGuard _guard;
    private readonly Action<string>? _log;
    private readonly Lock _gate = new();
    private readonly List<GsmtcMediaSession> _sessions = [];
    private GsmtcMediaSession? _current;
    private bool _disposed;

    private GsmtcMediaSessionSource(GsmtcManager manager, PlaybackGuard guard, Action<string>? log)
    {
        _manager = manager;
        _guard = guard;
        _log = log;
    }

    public event EventHandler? SessionsChanged;

    public event EventHandler? CurrentSessionChanged;

    public IReadOnlyList<IMediaSession> Sessions
    {
        get { lock (_gate) return [.. _sessions]; }
    }

    public IMediaSession? CurrentSession
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>Connects to GSMTC (read-only) and starts tracking its sessions.</summary>
    /// <param name="guard">
    /// Consulted by every session's <c>Try…Async</c> command right before it would reach the player; while it is
    /// blocked (<c>--no-playback</c>) every command is refused. The app passes <see cref="PlaybackGuard.Process"/>.
    /// </param>
    /// <param name="log">Where read failures and refused commands are reported.</param>
    /// <param name="cancellationToken">Cancels connecting.</param>
    public static async Task<GsmtcMediaSessionSource> CreateAsync(PlaybackGuard guard, Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(guard);
        var manager = await GsmtcManager.RequestAsync().AsTask(cancellationToken).ConfigureAwait(false);
        var source = new GsmtcMediaSessionSource(manager, guard, log);
        // Subscribe first so a change during the initial listing isn't missed.
        manager.SessionsChanged += source.OnSessionsChanged;
        manager.CurrentSessionChanged += source.OnCurrentSessionChanged;
        source.Refresh();
        return source;
    }

    public void Dispose()
    {
        List<GsmtcMediaSession> sessions;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            sessions = [.. _sessions];
            _sessions.Clear();
            _current = null;
        }

        _manager.SessionsChanged -= OnSessionsChanged;
        _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
        foreach (var session in sessions) session.Detach();
    }

    private void OnSessionsChanged(GsmtcManager sender, SessionsChangedEventArgs args)
    {
        var (sessionsChanged, currentChanged) = Refresh();
        if (sessionsChanged) SessionsChanged?.Invoke(this, EventArgs.Empty);
        if (currentChanged) CurrentSessionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnCurrentSessionChanged(GsmtcManager sender, CurrentSessionChangedEventArgs args)
    {
        var (sessionsChanged, currentChanged) = Refresh();
        if (sessionsChanged) SessionsChanged?.Invoke(this, EventArgs.Empty);
        if (currentChanged) CurrentSessionChanged?.Invoke(this, EventArgs.Empty);
    }

    // Re-reads the session list and the current session, reusing wrappers for sessions already known so their
    // identity (and the subscribers on them) survive.
    private (bool SessionsChanged, bool CurrentChanged) Refresh()
    {
        IReadOnlyList<GsmtcSession> raw;
        GsmtcSession? rawCurrent;
        try
        {
            raw = _manager.GetSessions();
            rawCurrent = _manager.GetCurrentSession();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"GSMTC: couldn't list sessions: {ex.Message}");
            return (false, false);
        }

        List<GsmtcMediaSession> removed;
        bool sessionsChanged, currentChanged;
        lock (_gate)
        {
            if (_disposed) return (false, false);
            var previous = _sessions.ToList();
            var all = raw.Where(r => r is not null).ToList();
            if (rawCurrent is not null && !all.Any(r => ReferenceEquals(r, rawCurrent))) all.Add(rawCurrent);
            var appIds = all.Select(GsmtcMediaSession.SafeAppId).ToList();

            var next = new List<GsmtcMediaSession>(all.Count);
            GsmtcMediaSession? current = null;
            for (var i = 0; i < all.Count; i++)
            {
                var unique = appIds.Count(id => id == appIds[i]) == 1;
                var session = Resolve(all[i], appIds[i], unique, previous, next);
                next.Add(session);
                if (ReferenceEquals(all[i], rawCurrent)) current = session;
            }

            removed = previous.Where(p => !next.Contains(p)).ToList();
            sessionsChanged = removed.Count > 0 || next.Count != previous.Count || next.Except(previous).Any();
            currentChanged = !ReferenceEquals(current, _current);
            _sessions.Clear();
            _sessions.AddRange(next);
            _current = current;
        }

        foreach (var session in removed) session.Detach();
        return (sessionsChanged, currentChanged);
    }

    // Finds the wrapper for a raw session: the same projected object or, should the projection hand out a new object
    // for the same session, the only known session of the only raw session from that app. Otherwise a new wrapper.
    private GsmtcMediaSession Resolve(
        GsmtcSession raw, string appId, bool uniqueApp, List<GsmtcMediaSession> known, List<GsmtcMediaSession> claimed)
    {
        foreach (var k in known)
        {
            if (ReferenceEquals(k.Raw, raw) && !claimed.Contains(k)) return k;
        }

        if (uniqueApp && appId.Length > 0)
        {
            var sameApp = known.Where(k => k.SourceAppUserModelId == appId && !claimed.Contains(k)).ToList();
            if (sameApp.Count == 1)
            {
                sameApp[0].Rebind(raw);
                return sameApp[0];
            }
        }

        return new GsmtcMediaSession(raw, _guard, _log);
    }
}

/// <summary>
/// One real GSMTC session. Reads are wrapped so a session that has gone away reads as empty. Every command goes through
/// <see cref="Command"/>, which asks the <see cref="PlaybackGuard"/> before the session is touched.
/// </summary>
internal sealed class GsmtcMediaSession : IMediaSession
{
    private readonly Action<string>? _log;
    private readonly PlaybackGuard _guard;
    private readonly Lock _gate = new();
    private GsmtcSession _raw;
    private bool _attached;
    private int _commandsSent;

    public GsmtcMediaSession(GsmtcSession raw, PlaybackGuard guard, Action<string>? log)
    {
        _raw = raw;
        _guard = guard;
        _log = log;
        SourceAppUserModelId = SafeAppId(raw);
        SourceApp = MediaSourceApp.FromAppId(SourceAppUserModelId);
        Attach(raw);
    }

    /// <summary>
    /// Test hook: a session with no GSMTC session behind it (nothing to reach, nothing subscribed), so the command
    /// path can be checked without any real player. A command that got past the guard fails.
    /// </summary>
    internal GsmtcMediaSession(string appId, PlaybackGuard guard, Action<string>? log = null)
    {
        _raw = null!;
        _guard = guard;
        _log = log;
        SourceAppUserModelId = appId;
        SourceApp = MediaSourceApp.FromAppId(appId);
    }

    /// <summary>How many commands got past the guard and were handed to the session (for tests).</summary>
    internal int CommandsSent => Volatile.Read(ref _commandsSent);

    public event EventHandler? MediaPropertiesChanged;

    public event EventHandler? PlaybackInfoChanged;

    public event EventHandler? TimelinePropertiesChanged;

    public string SourceAppUserModelId { get; }

    public MediaSourceApp SourceApp { get; }

    internal GsmtcSession Raw
    {
        get { lock (_gate) return _raw; }
    }

    internal static string SafeAppId(GsmtcSession raw)
    {
        try
        {
            return raw.SourceAppUserModelId ?? "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    internal void Rebind(GsmtcSession raw)
    {
        lock (_gate)
        {
            if (ReferenceEquals(raw, _raw)) return;
            DetachCore();
            _raw = raw;
            Attach(raw);
        }
    }

    internal void Detach()
    {
        lock (_gate) DetachCore();
    }

    public async Task<MediaTrackInfo?> GetMediaPropertiesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var p = await Raw.TryGetMediaPropertiesAsync().AsTask(cancellationToken).ConfigureAwait(false);
            if (p is null) return null;
            // Apple Music for Windows joins "Artist — Album" in the artist field: split it back.
            return AppleMusicWindowsMetadata.Normalize(new MediaTrackInfo(
                p.Title ?? "",
                p.Artist ?? "",
                p.AlbumTitle ?? "",
                p.AlbumArtist ?? "",
                p.Subtitle ?? "",
                p.TrackNumber,
                p.AlbumTrackCount,
                p.Thumbnail is not null,
                p.PlaybackType is { } type ? (CorePlaybackType)(int)type : CorePlaybackType.Unknown,
                p.Genres?.FirstOrDefault(genre => !string.IsNullOrWhiteSpace(genre))?.Trim() ?? ""), SourceApp.Kind);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"GSMTC: couldn't read media properties from {SourceAppUserModelId}: {ex.Message}");
            return null;
        }
    }

    public async Task<Stream?> OpenThumbnailAsync(int maxBytes = 16 * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        try
        {
            var p = await Raw.TryGetMediaPropertiesAsync().AsTask(cancellationToken).ConfigureAwait(false);
            if (p?.Thumbnail is not { } reference) return null;
            using var stream = await reference.OpenReadAsync().AsTask(cancellationToken).ConfigureAwait(false);
            using var input = stream.GetInputStreamAt(0);
            using var reader = new DataReader(input);
            var buffer = new MemoryStream();
            const uint chunk = 64 * 1024;
            while (true)
            {
                var loaded = await reader.LoadAsync(chunk).AsTask(cancellationToken).ConfigureAwait(false);
                if (loaded == 0) break;
                if (buffer.Length + loaded > maxBytes)
                {
                    _log?.Invoke($"GSMTC: artwork from {SourceAppUserModelId} is over {maxBytes} bytes; skipped.");
                    return null;
                }

                var bytes = new byte[loaded];
                reader.ReadBytes(bytes);
                buffer.Write(bytes);
            }

            if (buffer.Length == 0) return null;
            buffer.Position = 0;
            return new MemoryStream(buffer.GetBuffer(), 0, (int)buffer.Length, writable: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"GSMTC: couldn't read artwork from {SourceAppUserModelId}: {ex.Message}");
            return null;
        }
    }

    public MediaPlaybackInfo GetPlaybackInfo()
    {
        try
        {
            var info = Raw.GetPlaybackInfo();
            if (info is null) return MediaPlaybackInfo.Closed;
            return new MediaPlaybackInfo(
                (PlaybackStatus)(int)info.PlaybackStatus,
                MapControls(info.Controls),
                info.PlaybackRate,
                info.IsShuffleActive,
                info.AutoRepeatMode is { } repeat ? (CoreRepeatMode)(int)repeat : null,
                info.PlaybackType is { } type ? (CorePlaybackType)(int)type : null);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"GSMTC: couldn't read playback info from {SourceAppUserModelId}: {ex.Message}");
            return MediaPlaybackInfo.Closed;
        }
    }

    public MediaTimeline? GetTimeline()
    {
        try
        {
            var t = Raw.GetTimelineProperties();
            if (t is null) return null;
            return new MediaTimeline(t.StartTime, t.EndTime, t.Position, t.LastUpdatedTime, t.MinSeekTime, t.MaxSeekTime);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"GSMTC: couldn't read the timeline from {SourceAppUserModelId}: {ex.Message}");
            return null;
        }
    }

    // ---- Commands: these control the user's real player. Explicit user action in the UI only. ----

    /// <inheritdoc/>
    public Task<bool> TryPlayAsync() => Command(s => s.TryPlayAsync().AsTask(), "play");

    /// <inheritdoc/>
    public Task<bool> TryPauseAsync() => Command(s => s.TryPauseAsync().AsTask(), "pause");

    /// <inheritdoc/>
    public Task<bool> TryTogglePlayPauseAsync() => Command(s => s.TryTogglePlayPauseAsync().AsTask(), "toggle play/pause");

    /// <inheritdoc/>
    public Task<bool> TrySkipNextAsync() => Command(s => s.TrySkipNextAsync().AsTask(), "next");

    /// <inheritdoc/>
    public Task<bool> TrySkipPreviousAsync() => Command(s => s.TrySkipPreviousAsync().AsTask(), "previous");

    /// <inheritdoc/>
    public Task<bool> TryChangePlaybackPositionAsync(TimeSpan position) =>
        Command(s => s.TryChangePlaybackPositionAsync(position.Ticks).AsTask(), "seek");

    /// <inheritdoc/>
    public Task<bool> TryChangePlaybackRateAsync(double rate) => Command(s => s.TryChangePlaybackRateAsync(rate).AsTask(), "rate");

    /// <inheritdoc/>
    public Task<bool> TryChangeShuffleActiveAsync(bool active) =>
        Command(s => s.TryChangeShuffleActiveAsync(active).AsTask(), "shuffle");

    /// <inheritdoc/>
    public Task<bool> TryChangeRepeatModeAsync(CoreRepeatMode mode) =>
        Command(s => s.TryChangeAutoRepeatModeAsync((WinRepeatMode)(int)mode).AsTask(), "repeat");

    // The one way a command reaches the player. The guard is asked first, before the session is even read.
    private async Task<bool> Command(Func<GsmtcSession, Task<bool>> send, string name)
    {
        if (!_guard.TryAllow(name, SourceAppUserModelId, _log)) return false;
        try
        {
            Interlocked.Increment(ref _commandsSent);
            return await send(Raw).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"GSMTC: {name} command to {SourceAppUserModelId} failed: {ex.Message}");
            return false;
        }
    }

    private static MediaControls MapControls(GlobalSystemMediaTransportControlsSessionPlaybackControls? c)
    {
        if (c is null) return MediaControls.None;
        var controls = MediaControls.None;
        if (c.IsPlayEnabled) controls |= MediaControls.Play;
        if (c.IsPauseEnabled) controls |= MediaControls.Pause;
        if (c.IsStopEnabled) controls |= MediaControls.Stop;
        if (c.IsRecordEnabled) controls |= MediaControls.Record;
        if (c.IsFastForwardEnabled) controls |= MediaControls.FastForward;
        if (c.IsRewindEnabled) controls |= MediaControls.Rewind;
        if (c.IsNextEnabled) controls |= MediaControls.Next;
        if (c.IsPreviousEnabled) controls |= MediaControls.Previous;
        if (c.IsChannelUpEnabled) controls |= MediaControls.ChannelUp;
        if (c.IsChannelDownEnabled) controls |= MediaControls.ChannelDown;
        if (c.IsPlayPauseToggleEnabled) controls |= MediaControls.PlayPauseToggle;
        if (c.IsShuffleEnabled) controls |= MediaControls.Shuffle;
        if (c.IsRepeatEnabled) controls |= MediaControls.Repeat;
        if (c.IsPlaybackRateEnabled) controls |= MediaControls.PlaybackRate;
        if (c.IsPlaybackPositionEnabled) controls |= MediaControls.Seek;
        return controls;
    }

    private void Attach(GsmtcSession raw)
    {
        raw.MediaPropertiesChanged += OnMediaPropertiesChanged;
        raw.PlaybackInfoChanged += OnPlaybackInfoChanged;
        raw.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
        _attached = true;
    }

    private void DetachCore()
    {
        if (!_attached) return;
        _attached = false;
        try
        {
            _raw.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _raw.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _raw.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"GSMTC: couldn't unsubscribe from {SourceAppUserModelId}: {ex.Message}");
        }
    }

    private void OnMediaPropertiesChanged(GsmtcSession sender, MediaPropertiesChangedEventArgs args) =>
        MediaPropertiesChanged?.Invoke(this, EventArgs.Empty);

    private void OnPlaybackInfoChanged(GsmtcSession sender, PlaybackInfoChangedEventArgs args) =>
        PlaybackInfoChanged?.Invoke(this, EventArgs.Empty);

    private void OnTimelinePropertiesChanged(GsmtcSession sender, TimelinePropertiesChangedEventArgs args) =>
        TimelinePropertiesChanged?.Invoke(this, EventArgs.Empty);
}
