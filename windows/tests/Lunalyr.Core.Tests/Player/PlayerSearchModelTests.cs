using Lunalyr.Core.NowPlaying;
using Lunalyr.Core.Player;

namespace Lunalyr.Core.Tests.Player;

/// <summary>The desktop player's search and queue models and wording, with the Mac's inputs where they apply.</summary>
public sealed class PlayerSearchModelTests
{
    // Ported from DesktopPlayerSearchTests.swift: testSongsWithoutALibraryIDCannotBeClicked
    [Fact]
    public void SongsWithoutALibraryIDCannotBeClicked()
    {
        Assert.False(new PlayerQueueItem("h-1", "Radio", "", 0).IsPlayable);
        Assert.True(new PlayerQueueItem("h-2", "Song", "", 0, PersistentId: "AB12").IsPlayable);
        Assert.True(new PlayerQueueItem("now", "Now", "", 0, IsCurrent: true).IsPlayable);
    }

    // Ported from QueueContinuationTests.swift: testEveryWayOfPlayingAloneSaysWhyInPlainWords
    // (the toast's timing, PlayerNoteToast.noteSeconds, belongs to the card and isn't ported here)
    [Fact]
    public void EveryWayOfPlayingAloneSaysWhyInPlainWords()
    {
        foreach (var why in Enum.GetValues<QueueSolo>())
        {
            var note = PlayOutcome.SoloNote(why);
            Assert.False(string.IsNullOrEmpty(note));
            Assert.True(note.Length < 110, $"short enough for the strip: {why}");
            Assert.Equal(note, PlayOutcome.Alone(why).Note);
        }
        Assert.Null(PlayOutcome.Queued(songs: 150, wanted: 150, fromAlbum: false).Note);
        Assert.Null(PlayOutcome.Superseded.Note);
    }

    // Ported from MorePlayersTests.swift: testTheDesktopPlayersSwitchShowsWhatIsThere (its wording asserts; the switch
    // itself is the card's). The Mac's player kinds become the player's name and whether it shares its queue.
    [Fact]
    public void TheDesktopPlayersSwitchShowsWhatIsThere()
    {
        Assert.Equal("VLC doesn’t share what plays next.", PlayerSearchText.EmptyQueueMessage("VLC", sharesQueue: false));
        Assert.Equal("Nothing is playing.", PlayerSearchText.EmptyQueueMessage("Music", sharesQueue: true));
        Assert.Equal("VOX has no search Lunalyr can use.",
            PlayerSearchText.Words(new PlayerSearchException(PlayerSearchError.Unsupported), "VOX"));
    }

    // Adapted from QueueContinuationTests.swift: testFailuresKeepTheirAppleScriptErrorNumber. AppleScript error numbers
    // become HTTP statuses: the log keeps the number, the user sees the same words as any failure.
    [Fact]
    public void FailuresKeepTheirStatusForTheLogButReadAsAnyFailure()
    {
        var withStatus = new PlayerSearchException(PlayerSearchError.Failed, statusCode: 503);
        Assert.Equal("HTTP 503", PlayerSearchText.LogText(withStatus));
        Assert.Equal("unexpected reply", PlayerSearchText.LogText(new PlayerSearchException(PlayerSearchError.Failed)));
        Assert.Equal("cancelled", PlayerSearchText.LogText(new OperationCanceledException()));
        Assert.Equal(PlayerSearchText.Words(withStatus, "Spotify"), PlayerSearchText.Words(PlayerSearchError.Failed, "Spotify"));
        Assert.Equal("Spotify did not answer. Try again.", PlayerSearchText.Words(new InvalidOperationException(), "Spotify"));
    }

    // Adapted from PlayerSearch.swift's QueueOutcome.note: a line-up that lost songs says so; an album says it carries on.
    [Fact]
    public void QueuedNotesSayWhatCarriesOn()
    {
        Assert.Equal("Some songs after it couldn’t be added, so they’re left out.", PlayOutcome.Queued(3, 4, fromAlbum: false).Note);
        Assert.Equal("Then the rest of its album.", PlayOutcome.Queued(6, 6, fromAlbum: true).Note);
        Assert.Null(PlayOutcome.Unchanged.Note);
    }

    [Fact]
    public void EveryErrorHasCalmWordsThatNameNoTechnicalDetail()
    {
        foreach (var error in Enum.GetValues<PlayerSearchError>())
        {
            var words = PlayerSearchText.Words(error, "Spotify");
            Assert.False(string.IsNullOrWhiteSpace(words));
            Assert.EndsWith(".", words, StringComparison.Ordinal);
            Assert.DoesNotContain("HTTP", words, StringComparison.Ordinal);
            Assert.DoesNotContain("token", words, StringComparison.OrdinalIgnoreCase);
            Assert.True(words.Length < 140, $"{error}: {words}");
        }
        Assert.Equal("Spotify asked Lunalyr to slow down. Try again in 30 seconds.",
            PlayerSearchText.Words(PlayerSearchError.RateLimited, "Spotify", TimeSpan.FromSeconds(29.2)));
        Assert.Equal("Spotify asked Lunalyr to slow down. Try again in a moment.", PlayerSearchText.Words(PlayerSearchError.RateLimited, "Spotify"));
    }

    [Fact]
    public async Task OtherPlayersHaveNoSearchAndAnEmptyQueue()
    {
        var none = new NoPlayerSearch("VLC");
        var search = await Assert.ThrowsAsync<PlayerSearchException>(() => none.SearchAsync("blinding lights", TestContext.Current.CancellationToken));
        Assert.Equal(PlayerSearchError.Unsupported, search.Error);
        var result = new PlayerSearchResult("x", PlayerSearchResultKind.Song, "t", "a", "al", null, new PlayerSearchTarget("spotify:track:x"));
        Assert.Equal(PlayerSearchError.Unsupported, (await Assert.ThrowsAsync<PlayerSearchException>(() => none.PlayAsync(result, TestContext.Current.CancellationToken))).Error);
        Assert.Empty((await none.GetQueueAsync(TestContext.Current.CancellationToken)).Items);
        Assert.Same(PlayOutcome.Unchanged, await none.PlayQueueItemAsync(new PlayerQueueItem("q", "t", "a", 1, PersistentId: "x"), TestContext.Current.CancellationToken));
        Assert.False(none.SharesQueue);
    }

    [Fact]
    public void OnlyTheSpotifyAppGetsSpotifysSearch()
    {
        Assert.True(PlayerSearches.IsSpotify(MediaSourceApp.FromAppId("Spotify.exe")));
        Assert.True(PlayerSearches.IsSpotify(MediaSourceApp.FromAppId("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify")));
        Assert.False(PlayerSearches.IsSpotify(MediaSourceApp.FromAppId("MSEdge")), "the web player reports as its browser");
        Assert.False(PlayerSearches.IsSpotify(MediaSourceApp.FromAppId("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App")));
        Assert.False(PlayerSearches.IsSpotify(null));

        var kit = new SpotifyKit();
        var searches = new PlayerSearches(kit.Search);
        Assert.Same(kit.Search, searches.For(MediaSourceApp.FromAppId("Spotify.exe")));
        Assert.Equal(PlayerSearchAvailability.Ready, searches.Availability(MediaSourceApp.FromAppId("Spotify.exe")));
        var other = searches.For(MediaSourceApp.FromAppId("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic"));
        Assert.IsType<NoPlayerSearch>(other);
        Assert.Equal("Media Player", other.PlayerName);
        Assert.Equal(PlayerSearchAvailability.None, searches.Availability(MediaSourceApp.FromAppId("Chrome")));

        Assert.IsType<NoPlayerSearch>(PlayerSearches.Off.For(MediaSourceApp.FromAppId("Spotify.exe")));
        Assert.Equal(PlayerSearchAvailability.None, PlayerSearches.Off.Availability(MediaSourceApp.FromAppId("Spotify.exe")));
        Assert.Equal(PlayerSearchAvailability.NeedsSignIn, new PlayerSearches(new SpotifyKit(signedIn: false).Search).Availability(MediaSourceApp.FromAppId("Spotify.exe")));
        Assert.Equal(PlayerSearchAvailability.NeedsSetUp, new PlayerSearches(new SpotifyKit(clientId: "").Search).Availability(MediaSourceApp.FromAppId("Spotify.exe")));
    }

    [Fact]
    public void LastSongOnAOneDiscAlbumIsKnownOtherwiseNeverAssumed()
    {
        Assert.True(new PlayerSearchTarget("u", "a", TrackNumber: 14, DiscNumber: 1, AlbumTracks: 14).IsLastOnAlbum);
        Assert.False(new PlayerSearchTarget("u", "a", TrackNumber: 9, DiscNumber: 1, AlbumTracks: 14).IsLastOnAlbum);
        Assert.False(new PlayerSearchTarget("u", "a", TrackNumber: 14, DiscNumber: 2, AlbumTracks: 14).IsLastOnAlbum, "a later disc: unknown");
        Assert.False(new PlayerSearchTarget("u", "a").IsLastOnAlbum, "nothing known");
    }
}
