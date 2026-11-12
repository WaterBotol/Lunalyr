using System.Text.Json;
using Lunalyr.Core.Playback;
using Lunalyr.Core.Player;
using Lunalyr.Core.Player.Spotify;
using Lunalyr.Core.Tests.Lyrics.Sources;

namespace Lunalyr.Core.Tests.Player;

/// <summary>
/// Search, queue and play through Spotify's Web API, over hand-built replies (Fixtures/Spotify). Plays reach only the
/// fake: no request leaves the test, and the guard is the test's own.
/// </summary>
public sealed class SpotifyPlayerSearchTests
{
    private static readonly TimeSpan Bounded = TimeSpan.FromSeconds(20);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PlayerSearchResult Song(string uri = "spotify:track:0VjIjW4GlUZAMYd2vXMi3b", string? album = "spotify:album:4yP0hdKOZPNshxUOjY0cZj",
        int track = 9, int disc = 1, int tracks = 14) =>
        new($"sp-t-{uri}", PlayerSearchResultKind.Song, "Blinding Lights", "The Weeknd", "After Hours", null,
            new PlayerSearchTarget(uri, album, track, disc, tracks));

    private static JsonElement Body(SpotifyRequest request) => JsonDocument.Parse(request.Body).RootElement;

    // ---- Search ----

    [Fact]
    public async Task SearchFindsSongsThenAlbums()
    {
        var kit = new SpotifyKit().Refreshes();
        kit.Fake.On("GET search", SpotifyFake.Json("search.json"));
        var results = await kit.Search.SearchAsync("  blinding & lights ", Ct);

        var request = Assert.Single(kit.Fake.SentTo("GET search"));
        var uri = new Uri(request.Address);
        Assert.Equal("blinding & lights", FixtureHandler.QueryOf(uri, "q"));
        Assert.Equal("track,album", FixtureHandler.QueryOf(uri, "type"));
        Assert.Equal("8", FixtureHandler.QueryOf(uri, "limit"));
        Assert.Equal("from_token", FixtureHandler.QueryOf(uri, "market"));
        Assert.Equal("AT-refreshed", request.Bearer);

        Assert.Equal(["sp-t-0VjIjW4GlUZAMYd2vXMi3b", "sp-t-4VqPOruhp5EdPBeR92t6lQ", "sp-a-4yP0hdKOZPNshxUOjY0cZj", "sp-a-a2", "sp-a-a3", "sp-a-a4"],
            results.Select(r => r.Id));
        var lights = results[0];
        Assert.Equal(PlayerSearchResultKind.Song, lights.Kind);
        Assert.Equal(("Blinding Lights", "The Weeknd", "After Hours"), (lights.Title, lights.Artist, lights.Album));
        Assert.Equal(new Uri("https://i.scdn.co/image/after-hours-300"), lights.ArtworkUri);
        Assert.Equal(new PlayerSearchTarget("spotify:track:0VjIjW4GlUZAMYd2vXMi3b", "spotify:album:4yP0hdKOZPNshxUOjY0cZj", 9, 1, 14), lights.Target);
        Assert.False(lights.InLibrary);

        var breathe = results[1];
        Assert.Equal("Telepopmusik, Angela McCluskey", breathe.Artist);
        Assert.Null(breathe.ArtworkUri); // only a plain-HTTP cover: refused

        var album = results[2];
        Assert.Equal(PlayerSearchResultKind.Album, album.Kind);
        Assert.Equal(("After Hours", "The Weeknd", "After Hours"), (album.Title, album.Artist, album.Album));
        Assert.Equal(new Uri("https://i.scdn.co/image/after-hours-640"), album.ArtworkUri); // the smallest at least 200 px wide
        Assert.Equal(new PlayerSearchTarget("spotify:album:4yP0hdKOZPNshxUOjY0cZj", AlbumTracks: 14), album.Target);
        Assert.Null(results[3].ArtworkUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" a ")]
    [InlineData("\u6771")]
    [InlineData("\U0001F44D\U0001F3FD")] // one character on screen (a thumb with a skin tone), four UTF-16 units
    public async Task FewerThanTwoCharactersFindNothingAndAskNobody(string query)
    {
        var kit = new SpotifyKit().Refreshes();
        Assert.Empty(await kit.Search.SearchAsync(query, Ct));
        Assert.Empty(kit.Fake.Sent);
    }

    [Fact]
    public async Task TwoCharactersAreASearch()
    {
        var kit = new SpotifyKit().Refreshes();
        kit.Fake.On("GET search", SpotifyFake.Json("search.json"));
        await kit.Search.SearchAsync("\u6771\u4EAC", Ct);
        Assert.Equal("\u6771\u4EAC", FixtureHandler.QueryOf(new Uri(Assert.Single(kit.Fake.SentTo("GET search")).Address), "q"));
    }

    [Fact]
    public async Task AnUnreadableSearchReplyIsAPlainFailure()
    {
        var kit = new SpotifyKit().Refreshes();
        kit.Fake.On("GET search", Reply.Text(200, "<html>"));
        var error = await Assert.ThrowsAsync<PlayerSearchException>(() => kit.Search.SearchAsync("blinding", Ct));
        Assert.Equal(PlayerSearchError.Failed, error.Error);
    }

    // ---- Errors ----

    [Fact]
    public async Task ARefusedTokenIsRefreshedOnceAndTheRequestRetried()
    {
        var kit = new SpotifyKit();
        kit.Fake.On("POST token", SpotifyFake.Json("token-refresh.json"), SpotifyFake.Json("token-refresh-rotated.json"));
        kit.Fake.On("GET search", SpotifyFake.Json("error-expired-token.json", 401), SpotifyFake.Json("search.json"));
        var results = await kit.Search.SearchAsync("blinding", Ct);
        Assert.NotEmpty(results);
        Assert.Equal(["AT-refreshed", "AT-rotated"], kit.Fake.SentTo("GET search").Select(r => r.Bearer));
        Assert.Equal(2, kit.Fake.SentTo("POST token").Count);
        Assert.Equal("RT-rotated", kit.Store.Token!.RefreshToken);
    }

    [Fact]
    public async Task ATokenRefusedTwiceIsNotTriedAThirdTime()
    {
        var kit = new SpotifyKit().Refreshes();
        kit.Fake.On("GET search", SpotifyFake.Json("error-expired-token.json", 401));
        var error = await Assert.ThrowsAsync<PlayerSearchException>(() => kit.Search.SearchAsync("blinding", Ct));
        Assert.Equal(PlayerSearchError.SignInExpired, error.Error);
        Assert.Equal(2, kit.Fake.SentTo("GET search").Count);
        Assert.Equal(2, kit.Fake.SentTo("POST token").Count);
        Assert.NotNull(kit.Store.Token); // a 401 alone never deletes the sign-in
    }

    [Fact]
    public async Task PlayingWithoutPremiumSaysSo()
    {
        var kit = new SpotifyKit().Refreshes();
        kit.Fake.On("PUT me/player/play", SpotifyFake.Json("error-premium.json", 403));
        var error = await Assert.ThrowsAsync<PlayerSearchException>(() => kit.Search.PlayAsync(Song(), Ct));
        Assert.Equal(PlayerSearchError.PremiumRequired, error.Error);
        Assert.Equal("PREMIUM_REQUIRED", error.Reason);
        Assert.Equal("Playing from Lunalyr needs Spotify Premium. Search still works.", PlayerSearchText.Words(error, "Spotify"));
        Assert.Single(kit.Fake.SentTo("PUT me/player/play")); // not a request problem: no second try

        var bare = new SpotifyKit().Refreshes();
        bare.Fake.On("PUT me/player/play", SpotifyFake.Empty(403));
        Assert.Equal(PlayerSearchError.PremiumRequired,
            (await Assert.ThrowsAsync<PlayerSearchException>(() => bare.Search.PlayAsync(Song(), Ct))).Error);
    }

    [Fact]
    public async Task ARefusedReadIsNotAboutPremium()
    {
        var kit = new SpotifyKit().Refreshes();
        kit.Fake.On("GET search", SpotifyFake.Json("error-not-registered.json", 403));
        var error = await Assert.ThrowsAsync<PlayerSearchException>(() => kit.Search.SearchAsync("blinding", Ct));
        Assert.Equal(PlayerSearchError.NotAllowed, error.Error);
        Assert.Contains("user list", PlayerSearchText.Words(error, "Spotify"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlayingWithNoActiveDeviceSaysSo()
    {
        var kit = new SpotifyKit().Refreshes();
        kit.Fake.On("PUT me/player/play", SpotifyFake.Json("error-no-device.json", 404));
        var error = await Assert.ThrowsAsync<PlayerSearchException>(() => kit.Search.PlayAsync(Song(), Ct));
        Assert.Equal(PlayerSearchError.NoActiveDevice, error.Error);
        Assert.Equal("Spotify isn\u2019t playing on any device. Start it playing, then try again.", PlayerSearchText.Words(error, "Spotify"));
        Assert.Single(kit.Fake.SentTo("PUT me/player/play"));
    }

    [Fact]
    public async Task ABare404OnPlayStillReadsAsNoDeviceOnceThePlainFormFailsToo()
    {
        var kit = new SpotifyKit().Refreshes();
        kit.Fake.On("PUT me/player/play", SpotifyFake.Empty(404));
        var error = await Assert.ThrowsAsync<PlayerSearchException>(() => kit.Search.PlayAsync(Song(), Ct));
        Assert.Equal(PlayerSearchError.NoActiveDevice, error.Error);
        Assert.Equal(2, kit.Fake.SentTo("PUT me/player/play").Count);
    }

    [Fact]
    public async Task RateLimitingWaitsOutRetryAfterWithoutSendingAnything()
    {
        var kit = new SpotifyKit().Refreshes();
        kit.Fake.On("GET search", SpotifyFake.Json("error-rate-limited.json", 429, new Dictionary<string, string> { ["Retry-After"] = "30" }),
            SpotifyFake.Json("search.json"));
        var limited = await Assert.ThrowsAsync<PlayerSearchException>(() => kit.Search.SearchAsync("blinding", Ct));
        Assert.Equal(PlayerSearchError.RateLimited, limited.Error);
        Assert.Equal(TimeSpan.FromSeconds(30), limited.RetryAfter);
        Assert.Equal("Spotify asked Lunalyr to slow down. Try again in 30 seconds.", PlayerSearchText.Words(limited, "Spotify"));

        kit.Time.AdvanceSeconds(10);
        var still = await Assert.ThrowsAsync<PlayerSearchException>(() => kit.Search.SearchAsync("blinding", Ct));
        Assert.Equal(PlayerSearchError.RateLimited, still.Error);
        Assert.Equal(TimeSpan.FromSeconds(20), still.RetryAfter);
        Assert.Single(kit.Fake.SentTo("GET search"));

        kit.Time.AdvanceSeconds(21);
        Assert.NotEmpty(await kit.Search.SearchAsync("blinding", Ct));
        Assert.Equal(2, kit.Fake.SentTo("GET search").Count);
    }

    [Fact]
    public async Task ARateLimitWithoutRetryAfterHoldsOffForTheDefault()
    {
        var kit = new SpotifyKit().Refreshes();
        kit.Fake.On("GET me/player", SpotifyFake.Empty(429));
        var limited = await Assert.ThrowsAsync<PlayerSearchException>(() => kit.Search.GetQueueAsync(Ct));
        Assert.Equal(TimeSpan.FromSeconds(5), limited.RetryAfter);
    }

    [Fact]
    public async Task OfflineIsSaidCalmly()
    {
        var kit = new SpotifyKit().Refreshes();
        kit.Fake.On("GET search", Reply.Broken);
        kit.NetworkUp = false;
        var error = await Assert.ThrowsAsync<PlayerSearchException>(() => kit.Search.SearchAsync("blinding", Ct));
        Assert.Equal(PlayerSearchError.Offline, error.Error);

        kit.NetworkUp = true;
        var down = await Assert.ThrowsAsync<PlayerSearchException>(() => kit.Search.SearchAsync("blinding", Ct));
        Assert.Equal(PlayerSearchError.Failed, down.Error); // reachable PC, unreachable Spotify
    }

    [Theory]
    [InlineData(403, "", "", 0, PlayerSearchError.NotAllowed)]
    [InlineData(403, "", "", 2, PlayerSearchError.PremiumRequired)]
    [InlineData(403, "PREMIUM_REQUIRED", "", 1, PlayerSearchError.PremiumRequired)]
    [InlineData(404, "", "", 0, PlayerSearchError.Failed)]
    [InlineData(404, "", "", 1, PlayerSearchError.NoActiveDevice)]
    [InlineData(404, "NO_ACTIVE_DEVICE", "", 2, PlayerSearchError.NoActiveDevice)]
    [InlineData(400, "", "Invalid offset", 2, PlayerSearchError.Failed)]
    [InlineData(500, "", "", 2, PlayerSearchError.Failed)]
    [InlineData(502, "", "", 0, PlayerSearchError.Failed)]
    public void ErrorRepliesAreReadByStatusAndRequest(int status, string reason, string message, int call, PlayerSearchError expected) =>
        Assert.Equal(expected, SpotifyApiClient.Classify(status, reason, message, (SpotifyCall)call));

    // ---- Queue ----

    [Fact]
    public async Task TheQueueIsTheSongPlayingAndWhatSpotifyListsAfterIt()
    {
        var kit = new SpotifyKit().Refreshes();
        kit.Fake.On("GET me/player", SpotifyFake.Json("player-state.json"));
        kit.Fake.On("GET me/player/queue", SpotifyFake.Json("queue.json"));
        var snapshot = await kit.Search.GetQueueAsync(Ct);

        Assert.True(snapshot.Shuffle);
        Assert.Equal("spotify:playlist:37i9dQZF1DXcBWIGoYBM5M", snapshot.ContextUri);
        Assert.Equal([1, 2, 3, 4, 5], snapshot.Items.Select(i => i.Index));
        Assert.Equal(["Blinding Lights", "Breathe", "My own recording", "An Episode", "Save Your Tears"], snapshot.Items.Select(i => i.Title));
        Assert.True(snapshot.Items[0].IsCurrent);
        Assert.All(snapshot.Items.Skip(1), i => Assert.False(i.IsCurrent));
        Assert.Equal("spotify:track:0VjIjW4GlUZAMYd2vXMi3b", snapshot.Items[0].PersistentId);
        Assert.Equal("q-spotify:track:4VqPOruhp5EdPBeR92t6lQ-2", snapshot.Items[1].Id);
        Assert.Equal(("Telepopmusik", "Genetic World"), (snapshot.Items[1].Artist, snapshot.Items[1].Album));
        Assert.Equal(new Uri("https://i.scdn.co/image/genetic-640"), snapshot.Items[1].ArtworkUri);
        Assert.Null(snapshot.Items[2].PersistentId); // a local file can't be started from here
        Assert.False(snapshot.Items[2].IsPlayable);
        Assert.Equal(("A Podcast", "A Podcast", "spotify:episode:episode1"), (snapshot.Items[3].Artist, snapshot.Items[3].Album, snapshot.Items[3].PersistentId));
        Assert.Equal(new Uri("https://i.scdn.co/image/show-300"), snapshot.Items[3].ArtworkUri);

        var state = new Uri(Assert.Single(kit.Fake.SentTo("GET me/player")).Address);
        Assert.Equal("track,episode", FixtureHandler.QueryOf(state, "additional_types"));
        Assert.Empty(kit.Fake.SentTo("PUT me/player/play"));
    }

    [Fact]
    public async Task NothingPlayingIsAnEmptyQueueWithoutAskingForIt()
    {
        var kit = new SpotifyKit().Refreshes();
        kit.Fake.On("GET me/player", SpotifyFake.Empty(204));
        var snapshot = await kit.Search.GetQueueAsync(Ct);
        Assert.Empty(snapshot.Items);
        Assert.Empty(kit.Fake.SentTo("GET me/player/queue"));
        Assert.Equal("Nothing is playing.", PlayerSearchText.EmptyQueueMessage(kit.Search.PlayerName, kit.Search.SharesQueue));
    }

    // ---- Play ----

    [Fact]
    public async Task ASongFromSearchPlaysOnThroughItsAlbum()
    {
        var kit = new SpotifyKit().Refreshes();
        kit.Fake.On("PUT me/player/play", SpotifyFake.Empty(204));
        var outcome = await kit.Search.PlayAsync(Song(), Ct);

        var put = Assert.Single(kit.Fake.SentTo("PUT me/player/play"));
        Assert.Equal("AT-refreshed", put.Bearer);
        var body = Body(put);
        Assert.Equal("spotify:album:4yP0hdKOZPNshxUOjY0cZj", body.GetProperty("context_uri").GetString());
        Assert.Equal("spotify:track:0VjIjW4GlUZAMYd2vXMi3b", body.GetProperty("offset").GetProperty("uri").GetString());
        Assert.False(body.TryGetProperty("uris", out _));
        Assert.Equal(PlayOutcome.Queued(6, 6, fromAlbum: true), outcome);
        Assert.Equal("Then the rest of its album.", outcome.Note);
        Assert.Equal(0, kit.Guard.RefusedCount); // asked, and allowed
    }

    [Fact]
    public async Task ASongWithNothingAfterItPlaysAloneAndSaysWhy()
    {
        var kit = new SpotifyKit().Refreshes();
        kit.Fake.On("PUT me/player/play", SpotifyFake.Empty(204));

        var noAlbum = await kit.Search.PlayAsync(Song(album: null), Ct);
        Assert.Equal(PlayOutcome.Alone(QueueSolo.Single), noAlbum);
        var single = await kit.Search.PlayAsync(Song(track: 1, tracks: 1), Ct);
        Assert.Equal("Plays this song only.", single.Note);
        var last = await kit.Search.PlayAsync(Song(track: 14, tracks: 14), Ct);
        Assert.Equal(PlayOutcome.Alone(QueueSolo.LastOnAlbum), last);

        Assert.All(kit.Fake.SentTo("PUT me/player/play"), put =>
        {
            Assert.Equal(["spotify:track:0VjIjW4GlUZAMYd2vXMi3b"], Body(put).GetProperty("uris").EnumerateArray().Select(u => u.GetString()));
            Assert.False(Body(put).TryGetProperty("context_uri", out _));
        });
    }

    [Fact]
    public async Task AnAlbumPlaysFromItsStart()
    {
        var kit = new SpotifyKit().Refreshes();
        kit.Fake.On("PUT me/player/play", SpotifyFake.Empty(204));
        var album = new PlayerSearchResult("sp-a-x", PlayerSearchResultKind.Album, "After Hours", "The Weeknd", "After Hours", null,
            new PlayerSearchTarget("spotify:album:4yP0hdKOZPNshxUOjY0cZj", AlbumTracks: 14));
        var outcome = await kit.Search.PlayAsync(album, Ct);
        var body = Body(Assert.Single(kit.Fake.SentTo("PUT me/player/play")));
        Assert.Equal("spotify:album:4yP0hdKOZPNshxUOjY0cZj", body.GetProperty("context_uri").GetString());
        Assert.False(body.TryGetProperty("offset", out _));
        Assert.Null(outcome.Note);
    }

    // Adapted from QueueContinuationTests.swift: testAFailedBuildStillPlaysTheSongAndDeletesItsHalfBuiltPlaylist (the rescue)
    [Fact]
    public async Task WhenTheAlbumCantBeLinedUpTheSongStillPlays()
    {
        var kit = new SpotifyKit().Refreshes();
        kit.Fake.On("PUT me/player/play", SpotifyFake.Json("error-bad-offset.json", 400), SpotifyFake.Empty(204));
        var outcome = await kit.Search.PlayAsync(Song(), Ct);
        var puts = kit.Fake.SentTo("PUT me/player/play");
        Assert.Equal(2, puts.Count);
        Assert.Equal(["spotify:track:0VjIjW4GlUZAMYd2vXMi3b"], Body(puts[1]).GetProperty("uris").EnumerateArray().Select(u => u.GetString()));
        Assert.Equal(PlayOutcome.Alone(QueueSolo.Rescued), outcome);
        Assert.Equal("Couldn\u2019t line up what comes next, so this song plays on its own.", outcome.Note);
    }

    [Fact]
    public async Task AQueueClickPlaysThatSongAndWhatWasListedAfterIt()
    {
        var kit = new SpotifyKit().Refreshes();
        kit.Fake.On("GET me/player", SpotifyFake.Json("player-state.json"));
        kit.Fake.On("GET me/player/queue", SpotifyFake.Json("queue.json"));
        kit.Fake.On("PUT me/player/play", SpotifyFake.Empty(204));
        var snapshot = await kit.Search.GetQueueAsync(Ct);

        var outcome = await kit.Search.PlayQueueItemAsync(snapshot.Items[1], Ct);
        var body = Body(Assert.Single(kit.Fake.SentTo("PUT me/player/play")));
        Assert.Equal(["spotify:track:4VqPOruhp5EdPBeR92t6lQ", "spotify:episode:episode1", "spotify:track:saveyourtears"],
            body.GetProperty("uris").EnumerateArray().Select(u => u.GetString()));
        Assert.Equal(PlayOutcome.Queued(3, 4, fromAlbum: false), outcome);
        Assert.Equal("Some songs after it couldn\u2019t be added, so they\u2019re left out.", outcome.Note);

        Assert.Same(PlayOutcome.Unchanged, await kit.Search.PlayQueueItemAsync(snapshot.Items[0], Ct));
        var local = await Assert.ThrowsAsync<PlayerSearchException>(() => kit.Search.PlayQueueItemAsync(snapshot.Items[2], Ct));
        Assert.Equal(PlayerSearchError.Failed, local.Error);
        Assert.Single(kit.Fake.SentTo("PUT me/player/play"));

        var last = await kit.Search.PlayQueueItemAsync(snapshot.Items[4], Ct);
        Assert.Equal(PlayOutcome.Queued(1, 1, fromAlbum: false), last);
        Assert.Null(last.Note);
    }

    [Fact]
    public void ALineUpStopsAtTheLimit()
    {
        var items = Enumerable.Range(1, 80).Select(i => new PlayerQueueItem($"q{i}", $"t{i}", "a", i, i == 1, $"spotify:track:{i}")).ToList();
        var (uris, wanted) = SpotifyPlayerSearch.LineUp(new PlayerQueueSnapshot(items), items[1], 50);
        Assert.Equal(50, uris.Count);
        Assert.Equal(50, wanted);
        Assert.Equal("spotify:track:2", uris[0]);
        var (alone, one) = SpotifyPlayerSearch.LineUp(PlayerQueueSnapshot.Empty, items[5], 50);
        Assert.Equal(["spotify:track:6"], alone);
        Assert.Equal(1, one);
    }

    [Fact]
    public async Task WithPlaybackOffNothingIsSentAtAll()
    {
        var guard = new PlaybackGuard();
        guard.Block("--no-playback");
        var kit = new SpotifyKit(guard: guard).Refreshes();
        kit.Fake.On("PUT me/player/play", SpotifyFake.Empty(204));

        var play = await Assert.ThrowsAsync<PlayerSearchException>(() => kit.Search.PlayAsync(Song(), Ct));
        Assert.Equal(PlayerSearchError.PlaybackOff, play.Error);
        Assert.Equal("Playback commands are off for this run.", PlayerSearchText.Words(play, "Spotify"));
        var queued = await Assert.ThrowsAsync<PlayerSearchException>(
            () => kit.Search.PlayQueueItemAsync(new PlayerQueueItem("q", "t", "a", 2, PersistentId: "spotify:track:x"), Ct));
        Assert.Equal(PlayerSearchError.PlaybackOff, queued.Error);

        Assert.Empty(kit.Fake.Sent); // not even a token refresh
        Assert.Equal(2, guard.RefusedCount);
        Assert.False(PlaybackGuard.Process.IsBlocked, "the test's own guard, never the process's");
    }

    [Fact]
    public async Task ABlockArrivingWhileAPlayWaitsForItsTokenStillStopsIt()
    {
        var guard = new PlaybackGuard();
        var kit = new SpotifyKit(guard: guard);
        var release = new TaskCompletionSource();
        kit.Fake.On("POST token", SpotifyFake.Json("token-refresh.json") with { Wait = release.Task });
        kit.Fake.On("PUT me/player/play", SpotifyFake.Empty(204));

        var play = kit.Search.PlayAsync(Song(), Ct);
        await SpotifyAccountTests.Until(() => kit.Fake.SentTo("POST token").Count == 1);
        guard.Block("--no-playback (from a second launch)");
        release.SetResult();
        var error = await Assert.ThrowsAsync<PlayerSearchException>(() => play.WaitAsync(Bounded, Ct));
        Assert.Equal(PlayerSearchError.PlaybackOff, error.Error);
        Assert.Empty(kit.Fake.SentTo("PUT me/player/play"));
    }

    // Adapted from QueueContinuationTests.swift: testOnlyTheLatestOfQuickClicksPlays
    [Fact]
    public async Task OnlyTheLatestOfQuickClicksPlays()
    {
        var kit = new SpotifyKit();
        var release = new TaskCompletionSource();
        kit.Fake.On("POST token", SpotifyFake.Json("token-refresh.json") with { Wait = release.Task });
        kit.Fake.On("PUT me/player/play", SpotifyFake.Empty(204));

        var first = kit.Search.PlayAsync(Song("spotify:track:0A"), Ct);
        await SpotifyAccountTests.Until(() => kit.Fake.SentTo("POST token").Count == 1);
        var second = kit.Search.PlayAsync(Song("spotify:track:0B"), Ct);
        release.SetResult();

        Assert.Same(PlayOutcome.Superseded, await first.WaitAsync(Bounded, Ct));
        Assert.Equal(PlayOutcome.Queued(6, 6, fromAlbum: true), await second.WaitAsync(Bounded, Ct));
        var put = Assert.Single(kit.Fake.SentTo("PUT me/player/play"));
        Assert.Equal("spotify:track:0B", Body(put).GetProperty("offset").GetProperty("uri").GetString());
        Assert.Single(kit.Fake.SentTo("POST token"));
    }
}
