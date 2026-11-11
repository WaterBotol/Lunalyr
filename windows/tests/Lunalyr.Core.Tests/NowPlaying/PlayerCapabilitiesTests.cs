using Lunalyr.Core.NowPlaying;

namespace Lunalyr.Core.Tests.NowPlaying;

public class PlayerCapabilitiesTests
{
    private static readonly MediaSourceApp Spotify = MediaSourceApp.FromAppId("Spotify.exe");

    [Fact]
    public void EverythingEnabledMeansEverythingSupported()
    {
        var all = (MediaControls)((1 << 15) - 1);
        var caps = PlayerCapabilities.From(all, PlaybackStatus.Playing, Spotify);
        foreach (var control in Enum.GetValues<PlayerControl>())
        {
            Assert.True(caps[control].IsSupported, control.ToString());
            Assert.Null(caps[control].Reason);
        }

        Assert.True(caps.CanPlayPause && caps.CanSkipNext && caps.CanSkipPrevious && caps.CanSeek
            && caps.CanChangePlaybackRate && caps.CanShuffle && caps.CanRepeat);
        Assert.Same(Spotify, caps.App);
    }

    [Fact]
    public void MissingSeekSaysWhichAppRefuses()
    {
        var caps = PlayerCapabilities.From(MediaControls.Play | MediaControls.Pause | MediaControls.Next, PlaybackStatus.Playing, Spotify);
        Assert.False(caps.CanSeek);
        Assert.Equal("Spotify doesn't allow seeking from other apps.", caps[PlayerControl.Seek].Reason);
        Assert.True(caps.CanSkipNext);
        Assert.False(caps.CanSkipPrevious);
        Assert.Contains("going back", caps[PlayerControl.Previous].Reason);
    }

    [Theory]
    [InlineData(MediaControls.Pause, PlaybackStatus.Playing, true)]
    [InlineData(MediaControls.Play, PlaybackStatus.Playing, false)]
    [InlineData(MediaControls.Play, PlaybackStatus.Paused, true)]
    [InlineData(MediaControls.Pause, PlaybackStatus.Paused, false)]
    [InlineData(MediaControls.PlayPauseToggle, PlaybackStatus.Playing, true)]
    [InlineData(MediaControls.PlayPauseToggle, PlaybackStatus.Stopped, true)]
    [InlineData(MediaControls.None, PlaybackStatus.Paused, false)]
    public void PlayPauseDependsOnTheStatus(MediaControls controls, PlaybackStatus status, bool expected)
    {
        var caps = PlayerCapabilities.From(controls, status, Spotify);
        Assert.Equal(expected, caps.CanPlayPause);
        if (!expected)
            Assert.Contains(status == PlaybackStatus.Playing ? "pausing" : "starting playback", caps[PlayerControl.PlayPause].Reason);
    }

    [Fact]
    public void NoneExplainsThatNothingIsPlaying()
    {
        var caps = PlayerCapabilities.None;
        Assert.Null(caps.App);
        foreach (var control in Enum.GetValues<PlayerControl>())
            Assert.Equal("Nothing is playing.", caps[control].Reason);
    }

    [Fact]
    public void FromPlaybackInfoUsesItsControlsAndStatus()
    {
        var info = new MediaPlaybackInfo(PlaybackStatus.Paused, MediaControls.Play | MediaControls.Seek);
        var caps = PlayerCapabilities.From(info, MediaSourceApp.FromAppId("MSEdge"));
        Assert.True(caps.CanPlayPause);
        Assert.True(caps.CanSeek);
        Assert.Equal(MediaControls.Play | MediaControls.Seek, caps.Controls);
        Assert.Equal("Microsoft Edge doesn't allow changing the playback speed from other apps.", caps[PlayerControl.PlaybackRate].Reason);
    }

    [Fact]
    public void UnknownAppStillGetsAReadableReason()
    {
        var caps = PlayerCapabilities.From(MediaControls.None, PlaybackStatus.Playing, MediaSourceApp.FromAppId(null));
        Assert.Equal("Unknown app doesn't allow seeking from other apps.", caps[PlayerControl.Seek].Reason);
    }
}
