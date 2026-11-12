using Lunalyr.Core.Colour;
using Lunalyr.Render.Backgrounds;

namespace Lunalyr.Shell.MusicVideo;

/// <summary>
/// What the music video layers read every frame, filled by the app controller (<c>AppController.MusicVideo.cs</c>) and
/// by YouTube's player host (whether it is playing, its frozen frame). The layers are registered before the controller
/// exists (<c>WallpaperLayers</c>), so they hold this and the controller attaches itself; until then they draw the album
/// colours with nothing playing.
/// </summary>
public sealed class MusicVideoFeed : IMusicVideoFeed
{
    /// <summary>The app's one feed (the desktop's layers and the settings preview share it).</summary>
    public static MusicVideoFeed Shared { get; } = new();

    private volatile Reader? _reader;
    private volatile bool _playerVisible;
    private volatile CoverArt? _frozen;

    private sealed record Reader(Func<MusicVideoInputs> Read, Action<string, double?, double, bool> Report, Action<string, string> Failed);

    public void Attach(Func<MusicVideoInputs> read, Action<string, double?, double, bool> report, Action<string, string> failed) =>
        _reader = new Reader(read, report, failed);

    public void Detach() => _reader = null;

    /// <summary>YouTube's player is playing and shown (set by its host).</summary>
    public bool PlayerVisible
    {
        get => _playerVisible;
        set => _playerVisible = value;
    }

    /// <summary>YouTube's last frame while it is paused, or null (set by its host).</summary>
    public CoverArt? FrozenFrame
    {
        get => _frozen;
        set => _frozen = value;
    }

    public MusicVideoInputs Read()
    {
        var inputs = _reader?.Read() ?? new MusicVideoInputs(null, double.NaN, false, 0, 0.35, 0);
        return inputs with { PlayerVisible = _playerVisible };
    }

    public void Report(string path, double? duration, double videoTime, bool playing) => _reader?.Report(path, duration, videoTime, playing);

    public void Failed(string path, string reason) => _reader?.Failed(path, reason);
}
