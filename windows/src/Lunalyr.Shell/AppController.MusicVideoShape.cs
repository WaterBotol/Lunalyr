using Lunalyr.Core.MusicVideo;

namespace Lunalyr.Shell;

// The YouTube video's own shape for "Show the whole video" (the Mac's YouTubeVideoShape, Test Build 88): asked of YouTube's
// oEmbed endpoint (only the video's ID is sent) once a YouTube video is the one on screen and YouTube videos are shown
// whole, through the stills' client (so never under --demo, --no-network or --no-playback), and remembered for the run.
// Until it arrives (or when it can't be had) the video is taken as 16:9.
public sealed partial class AppController
{
    private string? _shapeVideo;
    private CancellationTokenSource? _shapeCts;
    private double _musicVideoAspect = double.NaN;

    /// <summary>The YouTube video's own aspect (width / height) once known; null means 16:9.</summary>
    public double? MusicVideoAspect
    {
        get
        {
            var aspect = Volatile.Read(ref _musicVideoAspect);
            return double.IsFinite(aspect) ? aspect : null;
        }
    }

    /// <summary>Asks for the shape of the YouTube video now on screen when it is shown whole, or lets the last one go.</summary>
    private void FollowMusicVideoShape()
    {
        var video = (_musicVideoShowing as MusicVideoSource.YouTube)?.VideoId;
        IMusicVideoHttp? http;
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_disposed) return;
            var whole = _settings.YouTubeFraming.IsWhole();
            var wanted = whole ? video : null;
            http = _stillHttp;
            if (wanted == _shapeVideo && (_shapeCts is not null || http is null)) return;
            if (wanted != _shapeVideo)
            {
                _shapeVideo = wanted;
                _shapeCts?.Cancel();
                _shapeCts = null;
                Volatile.Write(ref _musicVideoAspect, wanted is not null && YouTubeVideoShape.Cached(wanted) is { } known ? known : double.NaN);
            }
            if (wanted is null || http is null || double.IsFinite(_musicVideoAspect)) return;
            _shapeCts = cts = new CancellationTokenSource();
            video = wanted;
        }
        _ = LoadMusicVideoShapeAsync(video, http, cts.Token);
    }

    private async Task LoadMusicVideoShapeAsync(string video, IMusicVideoHttp http, CancellationToken ct)
    {
        var aspect = await YouTubeVideoShape.AspectAsync(video, http, ct).ConfigureAwait(false);
        lock (_gate)
        {
            if (_disposed || ct.IsCancellationRequested || _shapeVideo != video) return;
            if (aspect is { } found) Volatile.Write(ref _musicVideoAspect, found);
        }
        if (aspect is { } shape) _app.Info($"Music video: the YouTube video's shape is {shape:0.###}:1 (YouTube's oEmbed).");
        RaiseStatusChanged();
    }
}
