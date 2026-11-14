using System.Text;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Lyrics.Timing;
using Lunalyr.Core.MusicVideo;
using Lunalyr.Core.Playback;
using Lunalyr.Platform.Windows.Tests.NowPlaying;
using Lunalyr.Shell.MusicVideo;
using Lunalyr.Shell.Settings;
using Lunalyr.Shell.Tests.Fakes;
using Lunalyr.Tests.Shared;

namespace Lunalyr.Shell.Tests;

/// <summary>
/// The YouTube video's shape in the app (the Mac's YouTubeVideoShape, Test Build 88): asked of oEmbed only while a YouTube
/// video is on screen and shown whole, and handed to the layer through the feed. A fake YouTube; nothing reaches the
/// network and nothing plays. Each test uses its own video ID, as the shapes are remembered for the run.
/// </summary>
public sealed class MusicVideoShapeTests : IAsyncDisposable
{
    private readonly ManualTimeProvider _time = new();
    private readonly FakeMediaSessionSource _source = new();
    private readonly FakeRenderer _renderer = new();
    private readonly MemoryMusicVideoStorage _storage = new();
    private readonly MusicVideoFeed _feed = new();
    private AppController? _controller;

    public async ValueTask DisposeAsync()
    {
        if (_controller is not null) await _controller.DisposeAsync();
    }

    /// <summary>oEmbed answers 4:3; the image host has no stills. Records what was asked.</summary>
    private sealed class FakeYouTube : IMusicVideoHttp
    {
        private readonly List<Uri> _asked = [];

        public IReadOnlyList<Uri> OEmbed
        {
            get { lock (_asked) return [.. _asked.Where(u => u.AbsolutePath == YouTubeVideoShape.Path)]; }
        }

        public Task<MusicVideoHttpReply> GetAsync(Uri url, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
        {
            lock (_asked) _asked.Add(url);
            return Task.FromResult(url.AbsolutePath == YouTubeVideoShape.Path
                ? new MusicVideoHttpReply(200, Encoding.UTF8.GetBytes("{\"width\":200,\"height\":150}"))
                : new MusicVideoHttpReply(404, []));
        }
    }

    private sealed class Playable : IMusicVideoPlayabilityCheck
    {
        public Task<MusicVideoPlayability> CheckAsync(string path, CancellationToken ct) => Task.FromResult(MusicVideoPlayability.Playable);
    }

    private AppController Start(string framing, IMusicVideoHttp? http, bool clientFirst = true)
    {
        var app = new AppController(_source, new FakeLyricLookup(), _renderer,
            new AppSettings { BackgroundMode = "musicVideo", MusicVideoFraming = framing }, new SongOffsets(),
            ReadingServices.Basic, new PlaybackGuard(), Logger.Null, options: new AppControllerOptions
            {
                TimeProvider = _time, ArtworkRetries = [], TimelineWait = TimeSpan.FromMilliseconds(60),
                TimelinePoll = TimeSpan.FromMilliseconds(10), LateTimelinePoll = TimeSpan.FromMilliseconds(20),
            });
        _controller = app;
        var files = new MusicVideoFiles(_storage);
        var resolver = new MusicVideoResolver(files, new Playable(), null, new MusicVideoCache(_storage), new MusicVideoLinks(_storage));
        var video = new MusicVideoController(resolver, new MusicVideoOffsets(_storage), app.ReadSongPosition, app.MusicVideoLyrics, files: files);
        app.UseMusicVideo(video, _feed, youTubePlayer: true);
        if (clientFirst) app.UseMusicVideoStills(http);
        app.Start();
        return app;
    }

    private void PlayWithLink(string videoId)
    {
        new MusicVideoLinks(_storage).Set($"https://youtu.be/{videoId}", new MusicVideoSong("Song A", "Artist", "Album", 200).Key);
        var session = new FakeMediaSession("Spotify.exe", _time);
        session.SetTrack("Song A", "Artist").SetPlayback(Lunalyr.Core.NowPlaying.PlaybackStatus.Playing).SetTimeline(30, 200);
        _source.Add(session);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "Timed out waiting for the controller.");
    }

    [Fact]
    public async Task FilledVideosNeverAskForTheShape()
    {
        var youTube = new FakeYouTube();
        PlayWithLink("ShapeTest01");
        Start("fill", youTube);
        await WaitUntil(() => _renderer.Layers.Background == "musicVideo.youtube");
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Empty(youTube.OEmbed);
        Assert.Null(_feed.Read().YouTubeAspect);
        Assert.Equal(MusicVideoFraming.Fill, _feed.Read().YouTubeFraming);
    }

    [Fact]
    public async Task ShownWholeTheShapeIsAskedOnceAndReachesTheLayer()
    {
        var youTube = new FakeYouTube();
        PlayWithLink("ShapeTest02");
        var app = Start("whole", youTube);
        await WaitUntil(() => _feed.Read().YouTubeAspect is not null);
        Assert.Equal(4.0 / 3.0, _feed.Read().YouTubeAspect!.Value, 3);
        Assert.Equal(MusicVideoFraming.Whole, _feed.Read().YouTubeFraming);
        var asked = Assert.Single(youTube.OEmbed);
        Assert.True(YouTubeVideoShape.IsOEmbed(asked));
        Assert.Contains("ShapeTest02", asked.AbsoluteUri);
        // Back to fill: the shape is let go; whole again: remembered, not asked again.
        app.ApplySettings(app.Settings with { MusicVideoFraming = "fill" });
        await WaitUntil(() => _feed.Read().YouTubeAspect is null);
        app.ApplySettings(app.Settings with { MusicVideoFraming = "whole" });
        await WaitUntil(() => _feed.Read().YouTubeAspect is not null);
        Assert.Single(youTube.OEmbed);
    }

    [Fact]
    public async Task ChoosingWholeMidSongAsksForTheShape()
    {
        var youTube = new FakeYouTube();
        PlayWithLink("ShapeTest03");
        var app = Start("fill", youTube);
        await WaitUntil(() => _renderer.Layers.Background == "musicVideo.youtube");
        Assert.Empty(youTube.OEmbed);
        app.ApplySettings(app.Settings with { MusicVideoFraming = "whole" });
        await WaitUntil(() => _feed.Read().YouTubeAspect is not null);
        Assert.Single(youTube.OEmbed);
    }

    [Fact]
    public async Task AClientThatArrivesLateStillAsks()
    {
        var youTube = new FakeYouTube();
        PlayWithLink("ShapeTest04");
        var app = Start("whole", youTube, clientFirst: false);
        await WaitUntil(() => _renderer.Layers.Background == "musicVideo.youtube");
        Assert.Null(_feed.Read().YouTubeAspect); // no client: 16:9 assumed, nothing asked
        app.UseMusicVideoStills(youTube);
        await WaitUntil(() => _feed.Read().YouTubeAspect is not null);
        Assert.Single(youTube.OEmbed);
    }
}
