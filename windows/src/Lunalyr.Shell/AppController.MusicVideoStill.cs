using Lunalyr.Core.Colour;
using Lunalyr.Core.MusicVideo;
using Lunalyr.Platform.Imaging;

namespace Lunalyr.Shell;

// The YouTube video's still (the Mac's YouTubeThumbnail): fetched from YouTube's image host once the video is the one on
// screen, decoded off the render thread, and handed to the music video layer through the feed. The layer shows it
// wherever the player isn't: under Reduce Motion (the player is never opened then), in the settings preview, and on the
// other displays. Fetched only when the app hands over a client (never under --demo, --no-network or --no-playback).
public sealed partial class AppController
{
    /// <summary>The still is decoded to at most this many pixels a side (YouTube's largest is 1280 × 720).</summary>
    public const int MusicVideoStillMaxSide = 1280;

    private IMusicVideoHttp? _stillHttp;
    private string? _stillVideo;
    private CancellationTokenSource? _stillCts;
    private volatile CoverArt? _musicVideoStill;

    /// <summary>The YouTube video's still, decoded, once it has loaded; null otherwise.</summary>
    public CoverArt? MusicVideoStill => _musicVideoStill;

    /// <summary>
    /// Where YouTube videos' stills come from (YouTube's image host), and their shapes when shown whole (YouTube's oEmbed,
    /// AppController.MusicVideoShape.cs); null to fetch neither this run.
    /// </summary>
    public void UseMusicVideoStills(IMusicVideoHttp? http)
    {
        lock (_gate) _stillHttp = http;
        FollowMusicVideoStill();
        FollowMusicVideoShape();
    }

    /// <summary>Starts fetching the still of the YouTube video now on screen, or lets the last one go.</summary>
    private void FollowMusicVideoStill()
    {
        var video = (_musicVideoShowing as MusicVideoSource.YouTube)?.VideoId;
        IMusicVideoHttp? http;
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_disposed || video == _stillVideo) return;
            _stillVideo = video;
            _musicVideoStill = null;
            CancelMusicVideoStillLocked();
            http = _stillHttp;
            if (video is null || http is null) return;
            _stillCts = cts = new CancellationTokenSource();
        }
        // Not tracked as lookup work: a slow image host never holds up shutdown.
        _ = LoadMusicVideoStillAsync(video, http, cts.Token);
    }

    /// <summary>Stops fetching a still (another video, or shutting down). Called under <c>_gate</c>.</summary>
    private void CancelMusicVideoStillLocked()
    {
        _stillCts?.Cancel();
        _stillCts = null;
    }

    private async Task LoadMusicVideoStillAsync(string video, IMusicVideoHttp http, CancellationToken ct)
    {
        try
        {
            var bytes = await YouTubeThumbnail.LoadAsync(video, http, large: true, ct).ConfigureAwait(false);
            var still = bytes is null ? null
                : await Task.Run(() => ArtworkDecoder.DecodeCover(bytes, MusicVideoStillMaxSide), ct).ConfigureAwait(false);
            lock (_gate)
            {
                if (ct.IsCancellationRequested || _stillVideo != video) return;
                _musicVideoStill = still;
            }
            if (still is null) _app.Info("Music video: the video's still couldn't be loaded; the album colours show where the player isn't.");
        }
        catch (OperationCanceledException)
        {
        }
    }
}
