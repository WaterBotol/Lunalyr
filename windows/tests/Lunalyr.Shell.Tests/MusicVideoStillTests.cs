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
/// The YouTube video's still in the app (the Mac's YouTubeThumbnail): fetched once the video is on screen, decoded, and
/// read by the layer through the feed; never fetched without a client (--demo, --no-network, --no-playback). A fake image
/// host; nothing reaches the network and nothing plays.
/// </summary>
public sealed class MusicVideoStillTests : IAsyncDisposable
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

    /// <summary>YouTube's image host, by path; records what was asked.</summary>
    private sealed class FakeImageHost : IMusicVideoHttp
    {
        private readonly List<Uri> _asked = [];

        public Dictionary<string, byte[]> Images { get; } = [];

        public IReadOnlyList<Uri> Asked
        {
            get { lock (_asked) return [.. _asked]; }
        }

        public Task<MusicVideoHttpReply> GetAsync(Uri url, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
        {
            lock (_asked) _asked.Add(url);
            return Task.FromResult(Images.TryGetValue(url.AbsolutePath, out var body) ? new MusicVideoHttpReply(200, body) : new MusicVideoHttpReply(404, []));
        }
    }

    private sealed class Playable : IMusicVideoPlayabilityCheck
    {
        public Task<MusicVideoPlayability> CheckAsync(string path, CancellationToken ct) => Task.FromResult(MusicVideoPlayability.Playable);
    }

    private AppController Start(IMusicVideoHttp? stills)
    {
        var app = new AppController(_source, new FakeLyricLookup(), _renderer, new AppSettings { BackgroundMode = "musicVideo" }, new SongOffsets(),
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
        app.UseMusicVideoStills(stills);
        app.Start();
        return app;
    }

    private FakeMediaSession Playing(string title)
    {
        var session = new FakeMediaSession("Spotify.exe", _time);
        session.SetTrack(title, "Artist").SetPlayback(Lunalyr.Core.NowPlaying.PlaybackStatus.Playing).SetTimeline(30, 200);
        return session;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "Timed out waiting for the controller.");
    }

    private static async Task<byte[]> Jpeg(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = ((byte)40, (byte)90, (byte)200, (byte)255);
        using var stream = new global::Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await global::Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(global::Windows.Graphics.Imaging.BitmapEncoder.JpegEncoderId, stream);
        encoder.SetPixelData(global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, global::Windows.Graphics.Imaging.BitmapAlphaMode.Ignore,
            (uint)width, (uint)height, 96, 96, pixels);
        await encoder.FlushAsync();
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        using var reader = new global::Windows.Storage.Streams.DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    private static string SongKey => new MusicVideoSong("Song A", "Artist", "Album", 200).Key;

    [Fact]
    public async Task TheVideosStillReachesTheLayerLargestFirst()
    {
        var host = new FakeImageHost();
        host.Images["/vi/4NRXx6U8ABQ/hqdefault.jpg"] = await Jpeg(48, 36);
        new MusicVideoLinks(_storage).Set("https://youtu.be/4NRXx6U8ABQ", SongKey);
        _source.Add(Playing("Song A"));
        var app = Start(host);
        await WaitUntil(() => _renderer.Layers.Background == "musicVideo.youtube");
        await WaitUntil(() => _feed.Read().Still is not null);
        var still = _feed.Read().Still!;
        Assert.Equal((48, 36), (still.Width, still.Height));
        Assert.Same(still, app.MusicVideoStill);
        // The 1280 px still first (this video has none), then the usual one; only YouTube's image host.
        Assert.Equal(["maxresdefault.jpg", "hqdefault.jpg"], host.Asked.Select(u => u.Segments[^1]));
        Assert.All(host.Asked, u => Assert.Equal("i.ytimg.com", u.Host));
    }

    [Fact]
    public async Task WithoutAClientNoStillIsFetched()
    {
        new MusicVideoLinks(_storage).Set("https://youtu.be/4NRXx6U8ABQ", SongKey);
        _source.Add(Playing("Song A"));
        Start(stills: null);
        await WaitUntil(() => _renderer.Layers.Background == "musicVideo.youtube");
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Null(_feed.Read().Still);
    }
}
