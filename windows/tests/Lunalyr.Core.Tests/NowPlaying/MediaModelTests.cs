using Lunalyr.Core.NowPlaying;

namespace Lunalyr.Core.Tests.NowPlaying;

public class MediaModelTests
{
    [Theory]
    [InlineData("Spotify.exe", MediaSourceKind.Spotify, "Spotify")]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", MediaSourceKind.Spotify, "Spotify")]
    [InlineData("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App", MediaSourceKind.AppleMusic, "Apple Music")]
    [InlineData("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", MediaSourceKind.MediaPlayer, "Media Player")]
    [InlineData("Chrome", MediaSourceKind.Browser, "Google Chrome")]
    [InlineData("chrome.exe", MediaSourceKind.Browser, "Google Chrome")]
    [InlineData("MSEdge", MediaSourceKind.Browser, "Microsoft Edge")]
    [InlineData("308046B0AF4A39CB", MediaSourceKind.Browser, "Firefox")]
    [InlineData("firefox.exe", MediaSourceKind.Browser, "Firefox")]
    [InlineData("Brave", MediaSourceKind.Browser, "Brave")]
    [InlineData("Contoso.SuperPlayer_8wekyb3d8bbwe!App", MediaSourceKind.Other, "SuperPlayer")]
    [InlineData("foobar2000.exe", MediaSourceKind.Other, "foobar2000")]
    [InlineData(@"C:\Tools\mpv.exe", MediaSourceKind.Other, "mpv")]
    [InlineData("", MediaSourceKind.Unknown, "Unknown app")]
    [InlineData(null, MediaSourceKind.Unknown, "Unknown app")]
    public void RecognisesSourceApps(string? appId, MediaSourceKind kind, string name)
    {
        var app = MediaSourceApp.FromAppId(appId);
        Assert.Equal(kind, app.Kind);
        Assert.Equal(name, app.DisplayName);
        Assert.Equal(kind == MediaSourceKind.Browser, app.IsBrowser);
        if (!string.IsNullOrEmpty(appId)) Assert.Equal(appId, app.AppId);
    }

    [Fact]
    public void TimelineDurationIsUnknownWhenEndTimeIsZero()
    {
        var t = new MediaTimeline(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(10), DateTimeOffset.UnixEpoch);
        Assert.Null(t.Duration);
        Assert.Equal(TimeSpan.FromSeconds(10), t.RelativePosition);
    }

    [Fact]
    public void TimelineIsRelativeToStartTime()
    {
        var t = new MediaTimeline(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(65), TimeSpan.FromSeconds(3), DateTimeOffset.UnixEpoch);
        Assert.Equal(TimeSpan.FromSeconds(60), t.Duration);
        Assert.Equal(TimeSpan.Zero, t.RelativePosition); // Before StartTime: clamped.
    }

    [Fact]
    public void TrackKeyIgnoresCaseAndSurroundingSpace()
    {
        var a = new MediaTrackInfo("Lemon ", "Kenshi Yonezu", "STRAY SHEEP", "");
        var b = new MediaTrackInfo("lemon", " kenshi yonezu", "Stray Sheep", "Someone");
        Assert.Equal(a.TrackKey, b.TrackKey);
        Assert.NotEqual(a.TrackKey, (a with { Title = "Flamingo" }).TrackKey);
    }

    [Fact]
    public void EmptyTitleMeansNoTrack()
    {
        var t = new MediaTrackInfo("  ", "Artist", "Album", "");
        Assert.True(t.IsEmpty);
        Assert.Equal("", t.TrackKey);
        Assert.True(MediaTrackInfo.Empty.IsEmpty);
    }
}
