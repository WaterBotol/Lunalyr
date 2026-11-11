using Lunalyr.Core.NowPlaying;
using Lunalyr.Platform.NowPlaying;

namespace Lunalyr.Shell.Demo;

/// <summary>
/// The media sessions <c>--demo</c> shows instead of the system's: one silent, built-in "player" that plays
/// <see cref="DemoSongs"/> in a loop on a clock that advances in real time. Nothing plays and nothing is heard: there is
/// no audio at all, only a position. Every playback command is refused.
/// </summary>
public sealed class DemoMediaSessionSource : IMediaSessionSource
{
    private readonly DemoMediaSession _session;

    public DemoMediaSessionSource(TimeProvider? time = null, IReadOnlyList<DemoSong>? songs = null)
    {
        _session = new DemoMediaSession(time ?? TimeProvider.System, songs ?? DemoSongs.Songs);
    }

    public IReadOnlyList<IMediaSession> Sessions => [_session];

    public IMediaSession? CurrentSession => _session;

    /// <summary>The demo player (for tests).</summary>
    public DemoMediaSession Session => _session;

    // The one session is there from the start and never goes away.
    public event EventHandler? SessionsChanged { add { } remove { } }

    public event EventHandler? CurrentSessionChanged { add { } remove { } }

    public void Dispose() => _session.Dispose();
}

/// <summary>The demo's one player: a song, a position that runs in real time, and a cover. Commands are refused.</summary>
public sealed class DemoMediaSession : IMediaSession, IDisposable
{
    /// <summary>The demo player's app id (shown as "Lunalyr demo").</summary>
    public const string AppId = "Lunalyr.Demo";

    private readonly TimeProvider _time;
    private readonly IReadOnlyList<DemoSong> _songs;
    private readonly Lock _gate = new();
    private readonly ITimer _next;
    private int _index;
    private DateTimeOffset _started;
    private bool _disposed;

    internal DemoMediaSession(TimeProvider time, IReadOnlyList<DemoSong> songs)
    {
        ArgumentOutOfRangeException.ThrowIfZero(songs.Count);
        _time = time;
        _songs = songs;
        _started = time.GetUtcNow();
        _next = time.CreateTimer(_ => NextSong(), null, Length(0), Timeout.InfiniteTimeSpan);
    }

    public string SourceAppUserModelId => AppId;

    public MediaSourceApp SourceApp { get; } = new(AppId, MediaSourceKind.Other, "Lunalyr demo");

    /// <summary>The song playing now.</summary>
    public DemoSong Song
    {
        get { lock (_gate) return _songs[_index]; }
    }

    public event EventHandler? MediaPropertiesChanged;

    public event EventHandler? PlaybackInfoChanged { add { } remove { } }

    public event EventHandler? TimelinePropertiesChanged;

    private TimeSpan Length(int index) => TimeSpan.FromSeconds(_songs[index].Duration);

    private void NextSong()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _index = (_index + 1) % _songs.Count;
            _started = _time.GetUtcNow();
            _next.Change(Length(_index), Timeout.InfiniteTimeSpan);
        }

        MediaPropertiesChanged?.Invoke(this, EventArgs.Empty);
        TimelinePropertiesChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task<MediaTrackInfo?> GetMediaPropertiesAsync(CancellationToken cancellationToken = default)
    {
        var song = Song;
        return Task.FromResult<MediaTrackInfo?>(new MediaTrackInfo(song.Title, song.Artist, song.Album, song.Artist,
            HasThumbnail: true, PlaybackType: MediaPlaybackType.Music));
    }

    public Task<Stream?> OpenThumbnailAsync(int maxBytes = 16 * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        var cover = Song.Cover;
        return Task.FromResult<Stream?>(cover.Length <= maxBytes ? new MemoryStream(cover, writable: false) : null);
    }

    public MediaPlaybackInfo GetPlaybackInfo() =>
        new(PlaybackStatus.Playing, MediaControls.None, 1.0, false, MediaRepeatMode.List, MediaPlaybackType.Music);

    public MediaTimeline? GetTimeline()
    {
        DemoSong song;
        DateTimeOffset started;
        lock (_gate)
        {
            song = _songs[_index];
            started = _started;
        }

        var now = _time.GetUtcNow();
        var length = TimeSpan.FromSeconds(song.Duration);
        var position = now - started;
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (position > length) position = length;
        return new MediaTimeline(TimeSpan.Zero, length, position, now, TimeSpan.Zero, length);
    }

    // Every command is refused: the demo has nothing to play, pause or skip, and makes no sound.
    public Task<bool> TryPlayAsync() => Refused;

    public Task<bool> TryPauseAsync() => Refused;

    public Task<bool> TryTogglePlayPauseAsync() => Refused;

    public Task<bool> TrySkipNextAsync() => Refused;

    public Task<bool> TrySkipPreviousAsync() => Refused;

    public Task<bool> TryChangePlaybackPositionAsync(TimeSpan position) => Refused;

    public Task<bool> TryChangePlaybackRateAsync(double rate) => Refused;

    public Task<bool> TryChangeShuffleActiveAsync(bool active) => Refused;

    public Task<bool> TryChangeRepeatModeAsync(MediaRepeatMode mode) => Refused;

    private static Task<bool> Refused => Task.FromResult(false);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _next.Dispose();
    }
}
