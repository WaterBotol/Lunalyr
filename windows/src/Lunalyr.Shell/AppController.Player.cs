using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.NowPlaying;
using Lunalyr.Render.Scene;

namespace Lunalyr.Shell;

/// <summary>What the desktop player draws of the song, read in one go on the UI thread.</summary>
/// <param name="Position">The player's position in song seconds (no lyric offsets), or NaN without a timeline.</param>
/// <param name="Duration">The song's length in seconds, or 0 when unknown (a stream).</param>
/// <param name="LyricOffset">What the lyric layer adds to the song's time (global plus this song's offset).</param>
/// <param name="HasTimeline">The player shares its position.</param>
public sealed record PlayerNow(
    string? Player,
    string? Title,
    string? Artist,
    string? Album,
    string? TrackKey,
    bool IsPlaying,
    double Position,
    double Duration,
    bool HasTimeline,
    double LyricOffset,
    PlayerCapabilities Capabilities,
    LyricResult? Lyrics,
    IReadOnlyDictionary<int, string> Readings,
    Core.Colour.CoverArt? Cover,
    MediaPlaybackType? PlaybackType)
{
    public bool HasSong => !string.IsNullOrEmpty(Title);
}

// The desktop player's side of the controller: what it reads, and the scrub preview. While the seek bar is dragged the
// wallpaper shows the dragged moment (the renderer's clock reads the preview instead of the player); nothing is sent to
// the player until the release, and that goes through PlayerCommands, never through here: the controller stays
// read-only towards players.
public sealed partial class AppController
{
    // Song seconds the seek bar points at, or NaN when no drag is in flight. Read every frame without the lock.
    private double _scrubPreview = double.NaN;

    /// <summary>
    /// The renderer's clock: the player's, or while the seek bar is dragged the dragged moment, held still (not playing)
    /// so a pause in the hand is a pause on the wallpaper. Called every frame: no allocation.
    /// </summary>
    public (double Seconds, bool Playing) ReadRendererClock()
    {
        var preview = Volatile.Read(ref _scrubPreview);
        if (double.IsNaN(preview) || Volatile.Read(ref _hasSession) == 0) return ReadClock();
        return (SongOffsets.LyricTime(preview, Volatile.Read(ref _globalOffset), SongOffset()), false);
    }

    /// <summary>The seek bar is being dragged to <paramref name="songSeconds"/> (song time, no offsets): the wallpaper follows.</summary>
    public void SetScrubPreview(double songSeconds)
    {
        if (!double.IsFinite(songSeconds)) return;
        Volatile.Write(ref _scrubPreview, Math.Max(0, songSeconds));
    }

    /// <summary>The drag ended (released, cancelled, or the button let go elsewhere): the wallpaper follows the player again.</summary>
    public void ClearScrubPreview() => Volatile.Write(ref _scrubPreview, double.NaN);

    /// <summary>Whether a scrub preview is showing on the wallpaper.</summary>
    public bool IsPreviewing => !double.IsNaN(Volatile.Read(ref _scrubPreview));

    /// <summary>The song as the desktop player draws it.</summary>
    public PlayerNow ReadPlayer()
    {
        var session = Monitor.Session;
        var reading = Monitor.Clock.Read();
        SceneSnapshot scene;
        AppStatus status;
        lock (_gate)
        {
            scene = _scene;
            status = _status;
        }

        var playback = Monitor.PlaybackInfo;
        var duration = reading.Duration is { } d && d > TimeSpan.Zero ? d.TotalSeconds : scene.Duration ?? 0;
        return new PlayerNow(
            session?.SourceApp.DisplayName,
            session is null ? null : scene.Title ?? status.Title,
            session is null ? null : scene.Artist ?? status.Artist,
            session is null ? null : scene.Album,
            scene.TrackKey,
            reading.HasTimeline ? reading.IsPlaying : playback.IsPlaying,
            reading.HasTimeline ? reading.Position.TotalSeconds : double.NaN,
            duration,
            reading.HasTimeline,
            Volatile.Read(ref _globalOffset) + SongOffset(),
            Monitor.Capabilities,
            session is null ? null : scene.Lyrics,
            scene.Readings,
            session is null ? null : scene.Cover,
            playback.PlaybackType);
    }

    private double SongOffset()
    {
        var key = Volatile.Read(ref _recordingKey);
        return key is null ? 0 : _offsets.OffsetFor(key);
    }
}
