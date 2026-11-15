using Lunalyr.Core.Covers;
using Lunalyr.Core.Logging;

namespace Lunalyr.Shell.Covers;

/// <summary>What the controller asks for: the playing song's album, and whether a portrait display wants the tall cover.</summary>
public sealed record MotionCoverRequest(string? Album, string Artist, string Title, bool Portrait);

/// <summary>An animated cover ready to play: local files, the tall one only for portrait displays when the album has it.</summary>
public sealed record MotionCoverFiles(string Square, string? Tall);

/// <summary>
/// The answer for one album: its files, or none, with why (<see cref="Unreachable"/> when Apple couldn't be asked, so
/// it isn't taken as "this album has none").
/// </summary>
public sealed record MotionCoverAnswer(MotionCoverFiles? Files, bool Unreachable = false)
{
    public static MotionCoverAnswer None { get; } = new(Files: null, Unreachable: false);
}

/// <summary>Finds an album's animated cover and makes it playable from disk.</summary>
public interface IMotionCoverSource
{
    Task<MotionCoverAnswer> FindAsync(MotionCoverRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The real source: <see cref="MotionArtworkFinder"/> over Apple's public pages, then the chosen rendition from
/// <see cref="MotionCoverCache"/> (downloaded once into the data folder), checked to open with a video track before
/// it is handed on. A cover Apple publishes only as a raw HLS playlist (no single MP4) counts as none: Media
/// Foundation's Source Reader has no HLS source on Windows (it refuses the playlist with
/// MF_E_UNSUPPORTED_BYTESTREAM_TYPE), and a file can be kept and looped.
/// </summary>
public sealed class MotionCoverService(MotionArtworkFinder finder, MotionCoverCache cache, Func<string, bool> playable, Logger? logger = null)
    : IMotionCoverSource
{
    private readonly CategoryLogger _log = (logger ?? Logger.Null).Player;
    private readonly HashSet<string> _checked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public async Task<MotionCoverAnswer> FindAsync(MotionCoverRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var motion = await finder.FindAsync(request.Album, request.Artist, request.Title, cancellationToken).ConfigureAwait(false);
        if (motion is null) return MotionCoverAnswer.None;
        if (!MotionArtwork.IsMp4(motion.Square))
        {
            _log.Info("Animated cover: Apple publishes this album's cover only as a streaming playlist, which Windows can't play; showing the still cover.");
            return MotionCoverAnswer.None;
        }
        var square = await FileAsync(motion.Square, cancellationToken).ConfigureAwait(false);
        if (square is null) return new MotionCoverAnswer(null, Unreachable: true);
        string? tall = null;
        if (request.Portrait && motion.Rendition(portrait: true) is { } wanted && wanted != motion.Square)
            tall = await FileAsync(wanted, cancellationToken).ConfigureAwait(false);
        return new MotionCoverAnswer(new MotionCoverFiles(square, tall));
    }

    /// <summary>The rendition's local file, once it is known to open with a video track; null otherwise (a bad file is removed).</summary>
    private async Task<string?> FileAsync(Uri video, CancellationToken ct)
    {
        var path = await cache.GetAsync(video, ct).ConfigureAwait(false);
        if (path is null) return null;
        lock (_gate)
        {
            if (_checked.Contains(path)) return path;
        }
        var ok = await Task.Run(() => playable(path), ct).ConfigureAwait(false);
        if (!ok)
        {
            _log.Info("Animated cover: the downloaded video has no playable video track; showing the still cover.");
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return null;
        }
        lock (_gate) _checked.Add(path);
        return path;
    }
}
