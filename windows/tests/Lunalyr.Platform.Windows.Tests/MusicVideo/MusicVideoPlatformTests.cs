using Lunalyr.Core.MusicVideo;
using Lunalyr.Platform.Media;
using Lunalyr.Platform.MusicVideo;

namespace Lunalyr.Platform.Windows.Tests.MusicVideo;

/// <summary>
/// Windows' music video services: the silent reader seeking to a time (read back from the frame number each frame of the
/// generated timecode.mp4 carries, tools/WallpaperTestMedia), the playability check, the HTTP client's host rule and the
/// DPAPI-kept API key. Nothing here reaches the network or plays a sound.
/// </summary>
public sealed class MusicVideoPlatformTests
{
    private const int Fps = 15;
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Wallpaper", name);

    /// <summary>The frame number a timecode frame carries (12 blocks across its top third, bit 0 on the left).</summary>
    internal static unsafe int FrameNumber(byte* pixels, int pitch, int width, int height)
    {
        var number = 0;
        var y = height / 6;
        for (var bit = 0; bit < 12; bit++)
        {
            var x = (int)((bit + 0.5) * width / 12);
            var p = pixels + y * pitch + x * 4;
            if ((p[0] + p[1] + p[2]) / 3 > 128) number |= 1 << bit;
        }
        return number;
    }

    private static unsafe (double Time, int Frame) ReadAtOrAfter(VideoReader reader, double target)
    {
        for (var i = 0; i < 400; i++)
        {
            var read = reader.Read(out var frame);
            using (frame)
            {
                Assert.NotEqual(VideoRead.Failed, read);
                Assert.NotEqual(VideoRead.EndOfStream, read);
                if (read != VideoRead.Frame || frame.Time < target - 0.5 / Fps) continue;
                return (frame.Time, FrameNumber(frame.Pixels, frame.Pitch, reader.Width, reader.Height));
            }
        }
        throw new InvalidOperationException("No frame reached the target.");
    }

    [Fact]
    public void TheTimecodeClipCarriesItsOwnFrameNumbers()
    {
        using var reader = VideoReader.Open(Fixture("timecode.mp4"), 0, out var error);
        Assert.True(reader is not null, error);
        Assert.Equal(8, reader.Duration, 1);
        for (var expected = 0; expected < 10; expected++)
        {
            var (time, frame) = ReadAtOrAfter(reader, expected / (double)Fps);
            Assert.Equal(expected, frame);
            Assert.Equal(expected / (double)Fps, time, 2);
        }
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(2.4)]
    [InlineData(5.0)]
    [InlineData(1.0)]
    [InlineData(7.6)]
    public void SeekingThenSkippingEarlierFramesLandsOnTheFrameAskedFor(double seconds)
    {
        using var reader = VideoReader.Open(Fixture("timecode.mp4"), 0, out var error);
        Assert.True(reader is not null, error);
        // Read ahead first, so a seek back is checked too.
        ReadAtOrAfter(reader, 3.0);
        Assert.True(reader.Seek(seconds));
        var (time, frame) = ReadAtOrAfter(reader, seconds);
        var expected = (int)Math.Round(seconds * Fps);
        Assert.Equal(expected, frame);
        Assert.Equal(seconds, time, 1);
    }

    [Fact]
    public async Task OnlyFilesWithAVideoTrackArePlayable()
    {
        var check = new MediaFoundationPlayability();
        Assert.Equal(MusicVideoPlayability.Playable, await check.CheckAsync(Fixture("timecode.mp4"), TestContext.Current.CancellationToken));
        Assert.Equal(MusicVideoPlayability.Unplayable, await check.CheckAsync(Fixture("turned.jpg"), TestContext.Current.CancellationToken));
        Assert.Equal(MusicVideoPlayability.Unplayable, await check.CheckAsync(Fixture("missing.mp4"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheHttpClientAsksOnlyTheDataApiAndItsStillsOverHttps()
    {
        Assert.True(MusicVideoHttpClient.Allows(new Uri("https://www.googleapis.com/youtube/v3/search?q=x")));
        Assert.True(MusicVideoHttpClient.Allows(new Uri("https://i.ytimg.com/vi/4NRXx6U8ABQ/mqdefault.jpg")));
        Assert.False(MusicVideoHttpClient.Allows(new Uri("http://www.googleapis.com/youtube/v3/search")));
        Assert.False(MusicVideoHttpClient.Allows(new Uri("https://www.youtube.com/results?search_query=x")));
        Assert.False(MusicVideoHttpClient.Allows(new Uri("https://example.com/")));
        // Refused before anything is sent: the handler would throw if it were reached.
        using var client = new MusicVideoHttpClient(new RefusingHandler());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetAsync(new Uri("https://www.youtube.com/watch?v=4NRXx6U8ABQ"), new Dictionary<string, string>(), TestContext.Current.CancellationToken));
    }

    private sealed class RefusingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("No request may be sent.");
    }

    [Fact]
    public void TheApiKeyIsKeptEncryptedAndOnlyInItsOwnFile()
    {
        using var folder = new TempFolder();
        var path = folder.File("youtube-key.bin");
        // Invented, the shape of a Google API key only.
        const string key = "AIzaSyTESTONLY0000000000000000000000000";
        Assert.True(YouTubeApiKeyStore.LooksLikeKey(key));
        Assert.False(YouTubeApiKeyStore.LooksLikeKey("not a key"));
        Assert.False(YouTubeApiKeyStore.LooksLikeKey(key + "x"));
        var store = new YouTubeApiKeyStore(path);
        Assert.Null(store.Key);
        Assert.False(store.Set("nope"));
        Assert.True(store.Set(" " + key + " "));
        Assert.Equal(key, new YouTubeApiKeyStore(path).Key);
        var onDisk = File.ReadAllBytes(path);
        Assert.DoesNotContain("AIzaSy", System.Text.Encoding.UTF8.GetString(onDisk), StringComparison.Ordinal);
        Assert.DoesNotContain("AIzaSy", System.Text.Encoding.Unicode.GetString(onDisk), StringComparison.Ordinal);
        store.Clear();
        Assert.False(File.Exists(path));
        Assert.Null(new YouTubeApiKeyStore(path).Key);
    }
}
