namespace Lunalyr.Core.NowPlaying;

/// <summary>Playback status as reported by the player. Values mirror GSMTC's <c>PlaybackStatus</c>.</summary>
public enum PlaybackStatus
{
    Closed = 0,
    Opened = 1,
    Changing = 2,
    Stopped = 3,
    Playing = 4,
    Paused = 5,
}

/// <summary>What kind of media the session is playing. Values mirror GSMTC's <c>MediaPlaybackType</c>.</summary>
public enum MediaPlaybackType
{
    Unknown = 0,
    Music = 1,
    Video = 2,
    Image = 3,
}

/// <summary>Repeat mode. Values mirror <c>MediaPlaybackAutoRepeatMode</c>.</summary>
public enum MediaRepeatMode
{
    None = 0,
    Track = 1,
    List = 2,
}

/// <summary>The controls a player says it accepts from other apps (GSMTC's <c>Is…Enabled</c> flags).</summary>
[Flags]
public enum MediaControls
{
    None = 0,
    Play = 1 << 0,
    Pause = 1 << 1,
    Stop = 1 << 2,
    Record = 1 << 3,
    FastForward = 1 << 4,
    Rewind = 1 << 5,
    Next = 1 << 6,
    Previous = 1 << 7,
    ChannelUp = 1 << 8,
    ChannelDown = 1 << 9,
    PlayPauseToggle = 1 << 10,
    Shuffle = 1 << 11,
    Repeat = 1 << 12,
    PlaybackRate = 1 << 13,
    /// <summary>Seeking (GSMTC's <c>IsPlaybackPositionEnabled</c>).</summary>
    Seek = 1 << 14,
}

/// <summary>
/// Track metadata (GSMTC's media properties). Strings are never null; missing values are empty. <see cref="Genre"/> is
/// the first genre the player reports ("J-Pop"), a hint at the language sung; not part of <see cref="TrackKey"/>.
/// </summary>
public sealed record MediaTrackInfo(
    string Title,
    string Artist,
    string AlbumTitle,
    string AlbumArtist,
    string Subtitle = "",
    int TrackNumber = 0,
    int AlbumTrackCount = 0,
    bool HasThumbnail = false,
    MediaPlaybackType PlaybackType = MediaPlaybackType.Unknown,
    string Genre = "")
{
    public static MediaTrackInfo Empty { get; } = new("", "", "", "");

    /// <summary>True when there's no usable title (players briefly report this during track changes).</summary>
    public bool IsEmpty => string.IsNullOrWhiteSpace(Title);

    /// <summary>Identity used to tell one track from the next: title, artist and album, case-insensitive.</summary>
    public string TrackKey => IsEmpty ? "" : $"{Title.Trim()}\u001f{Artist.Trim()}\u001f{AlbumTitle.Trim()}".ToUpperInvariant();
}

/// <summary>Playback info (GSMTC's <c>GetPlaybackInfo()</c>).</summary>
public sealed record MediaPlaybackInfo(
    PlaybackStatus Status,
    MediaControls Controls,
    double? PlaybackRate = null,
    bool? IsShuffleActive = null,
    MediaRepeatMode? RepeatMode = null,
    MediaPlaybackType? PlaybackType = null)
{
    public static MediaPlaybackInfo Closed { get; } = new(PlaybackStatus.Closed, MediaControls.None);

    public bool IsPlaying => Status == PlaybackStatus.Playing;
}

/// <summary>
/// One timeline snapshot (GSMTC's <c>GetTimelineProperties()</c>). <see cref="Position"/> is the playback
/// position as of <see cref="LastUpdatedTime"/> (wall clock), not as of now.
/// </summary>
public readonly record struct MediaTimeline(
    TimeSpan StartTime,
    TimeSpan EndTime,
    TimeSpan Position,
    DateTimeOffset LastUpdatedTime,
    TimeSpan MinSeekTime = default,
    TimeSpan MaxSeekTime = default)
{
    /// <summary>Song length, or null when the player doesn't know it (EndTime is 0 or not after StartTime).</summary>
    public TimeSpan? Duration => EndTime > StartTime ? EndTime - StartTime : null;

    /// <summary>Position from the start of the track (Position − StartTime), never negative.</summary>
    public TimeSpan RelativePosition => Position > StartTime ? Position - StartTime : TimeSpan.Zero;
}
