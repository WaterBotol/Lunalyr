using Lunalyr.Core.NowPlaying;
using Lunalyr.Platform.NowPlaying;
using Lunalyr.Tests.Shared;

namespace Lunalyr.Platform.Windows.Tests.NowPlaying;

/// <summary>A scriptable, silent stand-in for GSMTC. Nothing here talks to Windows or any player.</summary>
internal sealed class FakeMediaSessionSource : IMediaSessionSource
{
    private readonly List<FakeMediaSession> _sessions = [];

    public event EventHandler? SessionsChanged;

    public event EventHandler? CurrentSessionChanged;

    public IReadOnlyList<IMediaSession> Sessions => [.. _sessions];

    public FakeMediaSession? Current { get; private set; }

    public IMediaSession? CurrentSession => Current;

    public bool HasSubscribers => SessionsChanged is not null || CurrentSessionChanged is not null;

    public FakeMediaSession Add(FakeMediaSession session, bool makeCurrent = true)
    {
        _sessions.Add(session);
        SessionsChanged?.Invoke(this, EventArgs.Empty);
        if (makeCurrent) SetCurrent(session);
        return session;
    }

    public void Remove(FakeMediaSession session)
    {
        _sessions.Remove(session);
        var wasCurrent = ReferenceEquals(Current, session);
        if (wasCurrent) Current = _sessions.LastOrDefault();
        SessionsChanged?.Invoke(this, EventArgs.Empty);
        if (wasCurrent) CurrentSessionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetCurrent(FakeMediaSession? session)
    {
        Current = session;
        CurrentSessionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// One fake player. Tests script it with explicit timestamps; its commands only record that they were called
/// (tests assert they never are) and do nothing.
/// </summary>
internal sealed class FakeMediaSession(string appId, ManualTimeProvider time) : IMediaSession
{
    private MediaTrackInfo? _track;
    private MediaPlaybackInfo _playback = new(PlaybackStatus.Closed, MediaControls.None);
    private MediaTimeline? _timeline;
    private readonly List<TaskCompletionSource<MediaTrackInfo?>> _heldProperties = [];

    public event EventHandler? MediaPropertiesChanged;

    public event EventHandler? PlaybackInfoChanged;

    private EventHandler? _timelineChanged;
    private readonly List<EventHandler> _everTimelineHandlers = [];

    public event EventHandler? TimelinePropertiesChanged
    {
        add
        {
            _timelineChanged += value;
            if (value is not null) _everTimelineHandlers.Add(value);
        }
        remove => _timelineChanged -= value;
    }

    public string SourceAppUserModelId { get; } = appId;

    public MediaSourceApp SourceApp { get; } = MediaSourceApp.FromAppId(appId);

    public List<string> Commands { get; } = [];

    public byte[]? Thumbnail { get; set; }

    /// <summary>When true, metadata reads stay pending until <see cref="ReleaseProperties"/> (out-of-order tests).</summary>
    /// <remarks>Scripting calls (<see cref="SetTrack"/>) still update what a later read returns.</remarks>
    public bool HoldPropertyReads { get; set; }

    public int PropertyReads { get; private set; }

    public bool HasSubscribers =>
        MediaPropertiesChanged is not null || PlaybackInfoChanged is not null || _timelineChanged is not null;

    // ---- Scripting ----

    public FakeMediaSession SetTrack(string title, string artist = "Artist", string album = "Album")
    {
        _track = new MediaTrackInfo(title, artist, album, artist, HasThumbnail: Thumbnail is not null);
        MediaPropertiesChanged?.Invoke(this, EventArgs.Empty);
        return this;
    }

    public FakeMediaSession SetPlayback(PlaybackStatus status, double? rate = 1.0, MediaControls controls = MediaControls.Play | MediaControls.Pause | MediaControls.Next | MediaControls.Previous | MediaControls.Seek)
    {
        _playback = new MediaPlaybackInfo(status, controls, rate);
        PlaybackInfoChanged?.Invoke(this, EventArgs.Empty);
        return this;
    }

    /// <summary>Reports a timeline: <paramref name="position"/> as of <paramref name="ageSeconds"/> ago (wall clock).</summary>
    public FakeMediaSession SetTimeline(double position, double? duration = 240, double ageSeconds = 0)
    {
        _timeline = new MediaTimeline(
            TimeSpan.Zero,
            duration is { } d ? TimeSpan.FromSeconds(d) : TimeSpan.Zero,
            TimeSpan.FromSeconds(position),
            time.UtcNow - TimeSpan.FromSeconds(ageSeconds),
            TimeSpan.Zero,
            duration is { } d2 ? TimeSpan.FromSeconds(d2) : TimeSpan.Zero);
        _timelineChanged?.Invoke(this, EventArgs.Empty);
        return this;
    }

    /// <summary>Raises the timeline event without changing anything (players do this).</summary>
    public void RaiseTimelineChanged() => _timelineChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Delivers a timeline event to every handler ever subscribed, including unsubscribed ones: an event that was
    /// already in flight on another thread when the subscriber unsubscribed.
    /// </summary>
    public void RaiseInFlightTimelineChanged()
    {
        foreach (var handler in _everTimelineHandlers) handler(this, EventArgs.Empty);
    }

    public int PendingPropertyReads => _heldProperties.Count;

    /// <summary>Completes the <paramref name="index"/>-th pending metadata read (0 = oldest), synchronously.</summary>
    public void ReleaseProperties(int index, MediaTrackInfo? result)
    {
        var held = _heldProperties[index];
        _heldProperties.RemoveAt(index);
        held.SetResult(result);
    }

    // ---- IMediaSession ----

    public Task<MediaTrackInfo?> GetMediaPropertiesAsync(CancellationToken cancellationToken = default)
    {
        PropertyReads++;
        if (!HoldPropertyReads) return Task.FromResult(_track);
        var held = new TaskCompletionSource<MediaTrackInfo?>(); // Continuations run inline, so tests stay deterministic.
        _heldProperties.Add(held);
        return held.Task;
    }

    public Task<Stream?> OpenThumbnailAsync(int maxBytes = 16 * 1024 * 1024, CancellationToken cancellationToken = default) =>
        Task.FromResult<Stream?>(Thumbnail is { Length: > 0 } bytes && bytes.Length <= maxBytes ? new MemoryStream(bytes, writable: false) : null);

    /// <summary>Playback and timeline reads so far.</summary>
    public int Reads { get; private set; }

    /// <summary>Reads made while nobody was subscribed to the change events (an update in that gap would be lost).</summary>
    public int ReadsWithoutSubscribers { get; private set; }

    public MediaPlaybackInfo GetPlaybackInfo()
    {
        CountRead();
        return _playback;
    }

    public MediaTimeline? GetTimeline()
    {
        CountRead();
        return _timeline;
    }

    private void CountRead()
    {
        Reads++;
        if (!HasSubscribers) ReadsWithoutSubscribers++;
    }

    public Task<bool> TryPlayAsync() => Record("play");

    public Task<bool> TryPauseAsync() => Record("pause");

    public Task<bool> TryTogglePlayPauseAsync() => Record("toggle");

    public Task<bool> TrySkipNextAsync() => Record("next");

    public Task<bool> TrySkipPreviousAsync() => Record("previous");

    public Task<bool> TryChangePlaybackPositionAsync(TimeSpan position) => Record($"seek {position}");

    public Task<bool> TryChangePlaybackRateAsync(double rate) => Record($"rate {rate}");

    public Task<bool> TryChangeShuffleActiveAsync(bool active) => Record($"shuffle {active}");

    public Task<bool> TryChangeRepeatModeAsync(MediaRepeatMode mode) => Record($"repeat {mode}");

    private Task<bool> Record(string command)
    {
        Commands.Add(command);
        return Task.FromResult(false);
    }
}
