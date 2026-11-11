using Lunalyr.Core.NowPlaying;
using Lunalyr.Core.Playback;
using Lunalyr.Platform.NowPlaying;

namespace Lunalyr.Platform.Windows.Tests.NowPlaying;

/// <summary>
/// The GSMTC adapter's command path with <c>--no-playback</c>. These sessions have no GSMTC session behind them, so
/// nothing here can reach a real player either way.
/// </summary>
public sealed class GsmtcPlaybackGuardTests
{
    private static async Task<bool[]> SendEveryCommand(IMediaSession session) =>
    [
        await session.TryPlayAsync(),
        await session.TryPauseAsync(),
        await session.TryTogglePlayPauseAsync(),
        await session.TrySkipNextAsync(),
        await session.TrySkipPreviousAsync(),
        await session.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(30)),
        await session.TryChangePlaybackRateAsync(1.5),
        await session.TryChangeShuffleActiveAsync(true),
        await session.TryChangeRepeatModeAsync(MediaRepeatMode.List),
    ];

    [Fact]
    public async Task TheAdapterRefusesEveryCommandWhenPlaybackIsOff()
    {
        var guard = new PlaybackGuard();
        guard.Block("--no-playback");
        var log = new List<string>();
        var session = new GsmtcMediaSession("Spotify.exe", guard, log.Add);

        var results = await SendEveryCommand(session);

        Assert.All(results, Assert.False);
        Assert.Equal(0, session.CommandsSent); // the session was never touched
        Assert.Equal(9, guard.RefusedCount);
        Assert.Equal(9, log.Count(line => line.Contains("Refused", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task WithoutTheFlagCommandsGetPastTheGuard()
    {
        // The contrast that shows the guard is what stops them above. With no session behind it, each command fails.
        var guard = new PlaybackGuard();
        var session = new GsmtcMediaSession("Spotify.exe", guard);

        var results = await SendEveryCommand(session);

        Assert.All(results, Assert.False);
        Assert.Equal(9, session.CommandsSent);
        Assert.Equal(0, guard.RefusedCount);
    }

    [Fact]
    public async Task BlockingLaterStopsCommandsFromThenOn()
    {
        var guard = new PlaybackGuard();
        var session = new GsmtcMediaSession("Spotify.exe", guard);
        await session.TryPauseAsync();
        guard.Block("--no-playback (from a second launch)");
        await session.TryPlayAsync();

        Assert.Equal(1, session.CommandsSent);
        Assert.Equal(1, guard.RefusedCount);
    }
}
