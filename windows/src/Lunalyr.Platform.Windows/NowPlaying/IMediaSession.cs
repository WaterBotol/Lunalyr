using Lunalyr.Core.NowPlaying;

namespace Lunalyr.Platform.NowPlaying;

/// <summary>
/// The system's media sessions (one per player that reports to Windows), as Global System Media Transport Controls
/// (GSMTC) exposes them. Implemented by <see cref="GsmtcMediaSessionSource"/> and by a silent fake in tests.
/// </summary>
/// <remarks>Events are raised on arbitrary threads.</remarks>
public interface IMediaSessionSource : IDisposable
{
    /// <summary>All sessions, in the order Windows lists them.</summary>
    IReadOnlyList<IMediaSession> Sessions { get; }

    /// <summary>The session Windows considers current (the one its own media flyout shows), or null.</summary>
    IMediaSession? CurrentSession { get; }

    /// <summary>A session appeared or went away.</summary>
    event EventHandler? SessionsChanged;

    /// <summary><see cref="CurrentSession"/> changed.</summary>
    event EventHandler? CurrentSessionChanged;
}

/// <summary>One player's media session.</summary>
/// <remarks>
/// <para>Reads never throw for a session that has gone away: they return null / <see cref="MediaPlaybackInfo.Closed"/>.</para>
/// <para>Events are raised on arbitrary threads, with the session as the sender.</para>
/// <para>
/// <b>Commands</b> (the <c>Try…Async</c> methods) control the user's real player: they can start sound, skip their
/// music or move it. Call them only from UI code in direct response to an explicit user action (a click on Lunalyr's
/// own play/next/seek control). Never call them from background logic, diagnostics, start-up, tests or checks.
/// </para>
/// </remarks>
public interface IMediaSession
{
    /// <summary>GSMTC's <c>SourceAppUserModelId</c>, e.g. <c>Spotify.exe</c>, <c>AppleInc.AppleMusicWin_…!App</c>, <c>MSEdge</c>.</summary>
    string SourceAppUserModelId { get; }

    /// <summary>The recognised app behind <see cref="SourceAppUserModelId"/>.</summary>
    MediaSourceApp SourceApp { get; }

    /// <summary>Title, artist, album and so on, or null if the player has none or the session is gone.</summary>
    Task<MediaTrackInfo?> GetMediaPropertiesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the current track's artwork into memory and returns it as a read-only stream (the caller disposes it),
    /// or null if there is none. <paramref name="maxBytes"/> caps what will be read.
    /// </summary>
    Task<Stream?> OpenThumbnailAsync(int maxBytes = 16 * 1024 * 1024, CancellationToken cancellationToken = default);

    /// <summary>Status, enabled controls, rate, shuffle and repeat.</summary>
    MediaPlaybackInfo GetPlaybackInfo();

    /// <summary>The latest timeline snapshot, or null if the session is gone.</summary>
    MediaTimeline? GetTimeline();

    event EventHandler? MediaPropertiesChanged;

    event EventHandler? PlaybackInfoChanged;

    event EventHandler? TimelinePropertiesChanged;

    /// <summary>Starts playback. <b>Controls the user's real player: explicit user action only.</b></summary>
    Task<bool> TryPlayAsync();

    /// <summary>Pauses. <b>Controls the user's real player: explicit user action only.</b></summary>
    Task<bool> TryPauseAsync();

    /// <summary>Toggles play/pause. <b>Controls the user's real player: explicit user action only.</b></summary>
    Task<bool> TryTogglePlayPauseAsync();

    /// <summary>Skips to the next track. <b>Controls the user's real player: explicit user action only.</b></summary>
    Task<bool> TrySkipNextAsync();

    /// <summary>Goes back a track. <b>Controls the user's real player: explicit user action only.</b></summary>
    Task<bool> TrySkipPreviousAsync();

    /// <summary>Seeks to <paramref name="position"/>. <b>Controls the user's real player: explicit user action only.</b></summary>
    Task<bool> TryChangePlaybackPositionAsync(TimeSpan position);

    /// <summary>Changes the playback rate. <b>Controls the user's real player: explicit user action only.</b></summary>
    Task<bool> TryChangePlaybackRateAsync(double rate);

    /// <summary>Turns shuffle on or off. <b>Controls the user's real player: explicit user action only.</b></summary>
    Task<bool> TryChangeShuffleActiveAsync(bool active);

    /// <summary>Changes repeat. <b>Controls the user's real player: explicit user action only.</b></summary>
    Task<bool> TryChangeRepeatModeAsync(MediaRepeatMode mode);
}
