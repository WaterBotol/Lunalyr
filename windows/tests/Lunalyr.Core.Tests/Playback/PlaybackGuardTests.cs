using Lunalyr.Core.Playback;

namespace Lunalyr.Core.Tests.Playback;

public sealed class PlaybackGuardTests
{
    [Fact]
    public void AllowsUntilBlocked()
    {
        var guard = new PlaybackGuard();
        Assert.False(guard.IsBlocked);
        Assert.True(guard.AllowsSound);
        Assert.True(guard.TryAllow("play", "Spotify.exe"));
        Assert.Equal(0, guard.RefusedCount);
    }

    [Fact]
    public void BlockedRefusesCountsAndLogs()
    {
        var guard = new PlaybackGuard();
        var log = new List<string>();
        guard.Block("--no-playback");

        Assert.True(guard.IsBlocked);
        Assert.False(guard.AllowsSound);
        Assert.False(guard.TryAllow("play", "Spotify.exe", log.Add));
        Assert.False(guard.TryAllow("seek", "", log.Add));
        Assert.Equal(2, guard.RefusedCount);
        Assert.Contains("play", log[0]);
        Assert.Contains("--no-playback", log[0]);
    }

    [Fact]
    public void BlockingIsOneWayAndKeepsTheFirstReason()
    {
        var guard = new PlaybackGuard();
        guard.Block("--no-playback");
        guard.Block("something else");
        Assert.Equal("--no-playback", guard.Reason);
        Assert.Throws<ArgumentException>(() => guard.Block(" "));
        Assert.True(guard.IsBlocked);
    }
}
