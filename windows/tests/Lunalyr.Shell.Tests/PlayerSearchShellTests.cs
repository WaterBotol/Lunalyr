using Lunalyr.Core.Playback;
using Lunalyr.Core.Player;
using Lunalyr.Core.Player.Spotify;
using Lunalyr.Shell.Player;
using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell.Tests;

/// <summary>The Spotify settings section's words and the demo search the card's checks use.</summary>
public sealed class PlayerSearchShellTests
{
    [Fact]
    public void TheSetUpStepsGiveTheExactRedirectAddress()
    {
        Assert.Equal("http://127.0.0.1:47823/callback", SpotifyCopy.RedirectUri);
        Assert.Contains(SpotifyCopy.Steps, step => step.Contains("http://127.0.0.1:47823/callback", StringComparison.Ordinal));
        Assert.Contains(SpotifyCopy.Steps, step => step.Contains("Web API", StringComparison.Ordinal));
        Assert.Equal(5, SpotifyCopy.Steps.Count);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData(" 0123456789abcdef0123456789abcdef ", true)]
    [InlineData("0123456789abcdef", false)]
    [InlineData("not a client id", false)]
    public void OnlyAWholeClientIdOrNothingIsSaved(string typed, bool savable)
    {
        Assert.Equal(savable, SpotifyCopy.IsSavable(typed));
        Assert.False(string.IsNullOrEmpty(SpotifyCopy.ClientIdCaption(typed)));
    }

    [Fact]
    public void EveryAccountStateIsWorded()
    {
        foreach (var state in Enum.GetValues<SpotifyAccountState>())
        {
            Assert.False(string.IsNullOrEmpty(SpotifyCopy.State(state)));
            Assert.False(string.IsNullOrEmpty(SpotifyCopy.StateDetail(state)));
        }

        Assert.Contains("demo", SpotifyCopy.Off("--demo"), StringComparison.Ordinal);
        Assert.Contains("--no-network", SpotifyCopy.Off("--no-network"), StringComparison.Ordinal);
        Assert.NotEqual(SpotifyCopy.HoverCaption(true), SpotifyCopy.HoverCaption(false));
    }

    [Fact]
    public async Task TheDemoSearchAnswersFromInventedSongsWithLocalCovers()
    {
        var search = new DemoPlayerSearch(Blocked(), () => new PlayedTrack("Neon Harbour", "The Lunalyr Demo Band"), TimeSpan.Zero);
        Assert.Empty(await search.SearchAsync("e", TestContext.Current.CancellationToken));
        var found = await search.SearchAsync("evening", TestContext.Current.CancellationToken);
        Assert.True(found.Count >= 5);
        Assert.Equal(PlayerSearchResultKind.Album, found[^1].Kind); // songs, then albums
        Assert.All(found, r => Assert.Equal(DemoPlayerSearch.CoverScheme, r.ArtworkUri!.Scheme));
        Assert.Empty(await search.SearchAsync("nothing like this", TestContext.Current.CancellationToken));
        var queue = await search.GetQueueAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Neon Harbour", queue.Items[0].Title);
        Assert.True(queue.Items[0].IsCurrent);
        Assert.DoesNotContain(queue.Items.Skip(1), q => q.Title == "Neon Harbour");
    }

    [Fact]
    public async Task TheDemoSearchNeverPlaysAnything()
    {
        var guard = Blocked();
        var search = new DemoPlayerSearch(guard, () => null, TimeSpan.Zero);
        var result = (await search.SearchAsync("evening", TestContext.Current.CancellationToken))[0];
        var refused = await Assert.ThrowsAsync<PlayerSearchException>(() => search.PlayAsync(result, TestContext.Current.CancellationToken));
        Assert.Equal(PlayerSearchError.PlaybackOff, refused.Error);
        await Assert.ThrowsAsync<PlayerSearchException>(() =>
            search.PlayQueueItemAsync(new PlayerQueueItem("q2", "x", "y", 2, PersistentId: "demo:q2"), TestContext.Current.CancellationToken));
        Assert.Equal(2, guard.RefusedCount);
        // Even with the guard off, it plays nothing.
        var open = new DemoPlayerSearch(new PlaybackGuard(), () => null, TimeSpan.Zero);
        await Assert.ThrowsAsync<PlayerSearchException>(() => open.PlayAsync(result, TestContext.Current.CancellationToken));
        Assert.Empty((await open.GetQueueAsync(TestContext.Current.CancellationToken)).Items);
    }

    private static PlaybackGuard Blocked()
    {
        var guard = new PlaybackGuard();
        guard.Block("--demo");
        return guard;
    }
}
