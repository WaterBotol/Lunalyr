using Lunalyr.Core.MusicVideo;
using static Lunalyr.Core.Tests.MusicVideo.Sources;

namespace Lunalyr.Core.Tests.MusicVideo;

// Ported from MusicVideoSearchTests.swift: Search YouTube…. Every seam is faked; no test reaches the network, plays
// anything, or touches the real settings. The Mac's request-address and page-parsing tests become the Data API's.
public sealed class MusicVideoSearchTests
{
    private readonly MusicVideoWorld _world = new();
    private MusicVideoSong Song => _world.Song;

    private sealed class FakeSearching(MusicVideoSong? song) : IMusicVideoSearching
    {
        public MusicVideoSong? SearchSong { get; set; } = song;
        public string? ShowingYouTubeId { get; set; }
        public string? PickedVideoId { get; set; }
        public MusicVideoSearchOutcome Outcome { get; set; } = new MusicVideoSearchOutcome.Results([]);
        public List<string> Queries { get; } = [];
        public List<(string Id, string Song)> Picks { get; } = [];
        public int Cleared { get; private set; }

        public Task<MusicVideoSearchOutcome> SearchYouTubeAsync(string query, CancellationToken ct)
        {
            Queries.Add(query);
            return Task.FromResult(Outcome);
        }

        public bool UsePickedVideo(string videoId, string songKey)
        {
            if (SearchSong?.Key != songKey) return false;
            Picks.Add((videoId, songKey));
            PickedVideoId = videoId;
            return true;
        }

        public void ClearPastedLink()
        {
            Cleared++;
            PickedVideoId = null;
        }
    }

    private static YouTubeVideoResult Result(string id, double? duration = null, string? views = null, string? uploaded = null) =>
        new(id, $"Video {id}", "Channel", Duration: duration, Views: views, Uploaded: uploaded);

    // Ported from MusicVideoSearchTests.swift: testFreeTextQueryIsSentAsTypedWithoutOfficialMusicVideo
    [Fact]
    public async Task FreeTextQueryIsSentAsTypedWithoutOfficialMusicVideo()
    {
        var http = new FakeHttp();
        MusicVideoWorld.ServeOfficialPage(http);
        await _world.Resolver(http: http).SearchResultsAsync("  The Weeknd Blinding Lights live  ", TestContext.Current.CancellationToken);
        Assert.Equal(YouTubeDataApi.Host, http.Urls[0].Host);
        Assert.Equal(ApiFixtures.SearchPath, http.Urls[0].AbsolutePath);
        Assert.Equal("The Weeknd Blinding Lights live", FakeHttp.QueryOf(http.Urls[0]));
        Assert.IsType<MusicVideoSearchOutcome.Failed>(await _world.Resolver(http: http).SearchResultsAsync("   ", TestContext.Current.CancellationToken));
        Assert.Equal("The Weeknd Blinding Lights official music video", YouTubeSearch.Query("Blinding Lights", "The Weeknd"));
    }

    // Ported from MusicVideoSearchTests.swift: testQueryWithSymbolsIsEncoded
    [Fact]
    public void QueryWithSymbolsIsEncoded()
    {
        var url = YouTubeDataApi.SearchUrl("AC/DC T.N.T. & more #1");
        Assert.DoesNotContain(" ", url.AbsoluteUri);
        Assert.Equal("AC/DC T.N.T. & more #1", FakeHttp.QueryOf(url));
    }

    // Ported from MusicVideoSearchTests.swift: testViewsAndUploadAgeAreReadFromEachShape (the API's counts and dates, shown
    // as YouTube shows them)
    [Fact]
    public void ViewsAndUploadAgeAreReadFromTheApi()
    {
        var merged = YouTubeDataApi.Merge(
            YouTubeDataApi.ParseSearch(ApiFixtures.Search(("4NRXx6U8ABQ", "The Weeknd - Blinding Lights (Official Video)", "TheWeekndVEVO"),
                ("fHI8X4OXluQ", "Blinding Lights (Lyrics)", "Lyrics Hub"), ("J7p4bzqLvCw", "Blinding Lights (Live)", "The Weeknd"))),
            YouTubeDataApi.ParseVideos(ApiFixtures.Videos(("4NRXx6U8ABQ", "PT4M22S", 1_034_567_890, true), ("fHI8X4OXluQ", "PT3M20S", 12_345, true))),
            MusicVideoWorld.Start);
        Assert.Equal(["4NRXx6U8ABQ", "fHI8X4OXluQ", "J7p4bzqLvCw"], merged.Select(r => r.VideoId));
        Assert.Equal("1B views", merged[0].Views);
        Assert.Equal(262, merged[0].Duration);
        Assert.Equal("12K views", merged[1].Views);
        Assert.Equal("6 years ago", merged[0].Uploaded);
        Assert.Null(merged[2].Views);
        Assert.Null(merged[2].Duration);
        Assert.False(merged[2].VerifiedArtist);
    }

    // Ported from MusicVideoSearchTests.swift: testResultsDefaultToNoViewsOrAge
    [Fact]
    public void ResultsDefaultToNoViewsOrAge()
    {
        var old = new YouTubeVideoResult("4NRXx6U8ABQ", "T", "C");
        Assert.Null(old.Views);
        Assert.Null(old.Uploaded);
    }

    // Ported from MusicVideoSearchTests.swift: testDurationIsFormattedLikeYouTube
    [Fact]
    public void DurationIsFormattedLikeYouTube()
    {
        Assert.Equal("0:07", MusicVideoSearchRow.Duration(7));
        Assert.Equal("3:45", MusicVideoSearchRow.Duration(225));
        Assert.Equal("10:00", MusicVideoSearchRow.Duration(600));
        Assert.Equal("1:02:03", MusicVideoSearchRow.Duration(3723));
        Assert.Null(MusicVideoSearchRow.Duration(null));
        Assert.Null(MusicVideoSearchRow.Duration(double.NaN));
        Assert.Null(MusicVideoSearchRow.Duration(-1));
    }

    // Ported from MusicVideoSearchTests.swift: testDetailsJoinViewsAndAgeAndLeaveOutWhatIsMissing
    [Fact]
    public void DetailsJoinViewsAndAgeAndLeaveOutWhatIsMissing()
    {
        Assert.Equal("12M views · 3 years ago", MusicVideoSearchRow.DetailsOf("12M views", "3 years ago"));
        Assert.Equal("12M views", MusicVideoSearchRow.DetailsOf("12M views", null));
        Assert.Equal("2 days ago", MusicVideoSearchRow.DetailsOf(" ", "2 days ago"));
        Assert.Null(MusicVideoSearchRow.DetailsOf(null, null));
    }

    // Ported from MusicVideoSearchTests.swift: testSameLengthHintWithinTheLengthMatch
    [Fact]
    public void SameLengthHintWithinTheLengthMatch()
    {
        var rows = MusicVideoSearchRow.Rows([Result("AAAAAAAAAAA", 201.5), Result("BBBBBBBBBBB", 203), Result("CCCCCCCCCCC")], 200, null);
        Assert.Equal([true, false, false], rows.Select(r => r.SameLength));
        Assert.False(MusicVideoSearchRow.IsSameLength(200, 0));
        Assert.False(MusicVideoSearchRow.IsSameLength(200, null));
    }

    // Ported from MusicVideoSearchTests.swift: testPlayingVideoIsMarkedAndFloatsToTheTop
    [Fact]
    public void PlayingVideoIsMarkedAndFloatsToTheTop()
    {
        var rows = MusicVideoSearchRow.Rows([Result("AAAAAAAAAAA"), Result("BBBBBBBBBBB"), Result("CCCCCCCCCCC")], 200, "CCCCCCCCCCC");
        Assert.Equal(["CCCCCCCCCCC", "AAAAAAAAAAA", "BBBBBBBBBBB"], rows.Select(r => r.VideoId));
        Assert.Equal([true, false, false], rows.Select(r => r.PlayingNow));
        Assert.EndsWith(MusicVideoSearchRow.PlayingNowLabel, rows[0].AccessibilityLabel);
    }

    // Ported from MusicVideoSearchTests.swift: testRowsAreDeduplicatedAndCapped
    [Fact]
    public void RowsAreDeduplicatedAndCapped()
    {
        var results = Enumerable.Range(0, 30).Select(i => Result($"video{i:000000}")).ToList();
        var rows = MusicVideoSearchRow.Rows([results[0], .. results], 200, null);
        Assert.Equal(MusicVideoSearchRow.Limit, rows.Count);
        Assert.Equal(rows.Count, rows.Select(r => r.Id).Distinct().Count());
        Assert.Equal(results[0].VideoId, rows[0].VideoId);
    }

    // Ported from MusicVideoSearchTests.swift: testRowLabelsAndThumbnail (the still is the one the API returned)
    [Fact]
    public void RowLabelsAndThumbnail()
    {
        var result = new YouTubeVideoResult("4NRXx6U8ABQ", "Blinding Lights (Official Video)", "The Weeknd", true, 201, "1B views",
            "5 years ago", "https://i.ytimg.com/vi/4NRXx6U8ABQ/mqdefault.jpg");
        var row = new MusicVideoSearchRow(result, 200, null);
        Assert.Equal("3:21", row.DurationText);
        Assert.Equal("1B views · 5 years ago", row.Details);
        Assert.Equal("Blinding Lights (Official Video), The Weeknd, Verified artist, 3:21, 1B views · 5 years ago, Same length as the song",
            row.AccessibilityLabel);
        Assert.Equal("https://i.ytimg.com/vi/4NRXx6U8ABQ/mqdefault.jpg", row.ThumbnailUrl);
        Assert.Equal("Untitled video", new MusicVideoSearchRow(new YouTubeVideoResult("4NRXx6U8ABQ", "", ""), null, null).Title);
        Assert.Null(new MusicVideoSearchRow(new YouTubeVideoResult("4NRXx6U8ABQ", "", ""), null, null).ThumbnailUrl);
    }

    // Ported from MusicVideoSearchTests.swift: testQueryIsPrefilledWithArtistAndTitleAndSearchedOnOpen
    [Fact]
    public async Task QueryIsPrefilledWithArtistAndTitleAndSearchedOnOpen()
    {
        var source = new FakeSearching(Song) { Outcome = new MusicVideoSearchOutcome.Results([Result("AAAAAAAAAAA")]) };
        var model = new MusicVideoSearchModel(source);
        model.Prepare();
        Assert.Equal("The Weeknd Blinding Lights", model.Query);
        Assert.Equal("“Blinding Lights” by The Weeknd", model.SongLabel);
        await model.SettleAsync();
        Assert.Equal(["The Weeknd Blinding Lights"], source.Queries);
        Assert.Equal(MusicVideoSearchModel.SearchPhase.Results, model.Phase);
        Assert.Equal("AAAAAAAAAAA", model.Selection);
        Assert.Equal("Song", MusicVideoSearchModel.Prefill(new MusicVideoSong("Song", " ", null, 1)));
    }

    // Ported from MusicVideoSearchTests.swift: testReopeningForTheSameSongKeepsResultsAndANewSongSearchesAgain
    [Fact]
    public async Task ReopeningForTheSameSongKeepsResultsAndANewSongSearchesAgain()
    {
        var source = new FakeSearching(Song) { Outcome = new MusicVideoSearchOutcome.Results([Result("AAAAAAAAAAA"), Result("BBBBBBBBBBB")]) };
        var model = new MusicVideoSearchModel(source);
        model.Prepare();
        await model.SettleAsync();
        source.ShowingYouTubeId = "BBBBBBBBBBB";
        model.Prepare();
        await model.SettleAsync();
        Assert.Single(source.Queries);
        Assert.Equal("BBBBBBBBBBB", model.Rows[0].VideoId);
        source.SearchSong = new MusicVideoSong("Save Your Tears", "The Weeknd", null, 215);
        model.Prepare();
        await model.SettleAsync();
        Assert.Equal("The Weeknd Save Your Tears", source.Queries[^1]);
    }

    // Ported from MusicVideoSearchTests.swift: testEditedQueryIsSearchedAsTyped
    [Fact]
    public async Task EditedQueryIsSearchedAsTyped()
    {
        var source = new FakeSearching(Song);
        var model = new MusicVideoSearchModel(source);
        model.Prepare();
        await model.SettleAsync();
        model.Query = "blinding lights live coachella";
        model.Search();
        await model.SettleAsync();
        Assert.Equal("blinding lights live coachella", source.Queries[^1]);
        Assert.Equal(MusicVideoSearchModel.SearchPhase.Empty, model.Phase);
    }

    // Ported from MusicVideoSearchTests.swift: testRateLimitedAndFailedStates
    [Fact]
    public async Task RateLimitedAndFailedStates()
    {
        var source = new FakeSearching(Song) { Outcome = new MusicVideoSearchOutcome.RateLimited() };
        var model = new MusicVideoSearchModel(source);
        model.Prepare();
        await model.SettleAsync();
        Assert.Equal(MusicVideoSearchModel.SearchPhase.RateLimited, model.Phase);
        source.Outcome = new MusicVideoSearchOutcome.Failed();
        model.Search();
        await model.SettleAsync();
        Assert.Equal(MusicVideoSearchModel.SearchPhase.Failed, model.Phase);
        source.Outcome = new MusicVideoSearchOutcome.Results([Result("AAAAAAAAAAA")]);
        model.Prepare();
        await model.SettleAsync();
        Assert.Equal(MusicVideoSearchModel.SearchPhase.Results, model.Phase);
    }

    // Windows' own: without a key (or with one YouTube refused) the picker says so instead of searching.
    [Fact]
    public async Task WithoutAKeyThePickerAsksForOne()
    {
        var source = new FakeSearching(Song) { Outcome = new MusicVideoSearchOutcome.NeedsKey(false) };
        var model = new MusicVideoSearchModel(source);
        model.Prepare();
        await model.SettleAsync();
        Assert.Equal(MusicVideoSearchModel.SearchPhase.NeedsKey, model.Phase);
        Assert.False(model.KeyRejected);
        var http = new FakeHttp();
        Assert.IsType<MusicVideoSearchOutcome.NeedsKey>(await _world.Resolver(http: http, key: null).SearchResultsAsync("x", TestContext.Current.CancellationToken));
        Assert.Empty(http.Urls);
    }

    // Ported from MusicVideoSearchTests.swift: testNothingPlayingSearchesNothing
    [Fact]
    public async Task NothingPlayingSearchesNothing()
    {
        var source = new FakeSearching(null);
        var model = new MusicVideoSearchModel(source);
        model.Prepare();
        model.Search();
        await model.SettleAsync();
        Assert.Equal(MusicVideoSearchModel.SearchPhase.NoSong, model.Phase);
        Assert.Empty(source.Queries);
    }

    // Ported from MusicVideoSearchTests.swift: testPickingUsesTheVideoForTheSongAndCloses
    [Fact]
    public async Task PickingUsesTheVideoForTheSongAndCloses()
    {
        var source = new FakeSearching(Song) { Outcome = new MusicVideoSearchOutcome.Results([Result("AAAAAAAAAAA"), Result("BBBBBBBBBBB")]) };
        var closed = 0;
        var model = new MusicVideoSearchModel(source) { OnClose = () => closed++ };
        model.Prepare();
        await model.SettleAsync();
        model.MoveSelection(1);
        Assert.Equal("BBBBBBBBBBB", model.Selection);
        model.MoveSelection(1);
        Assert.Equal("BBBBBBBBBBB", model.Selection);
        model.PickSelection();
        Assert.Equal(["BBBBBBBBBBB"], source.Picks.Select(p => p.Id));
        Assert.Equal([Song.Key], source.Picks.Select(p => p.Song));
        Assert.Equal(1, closed);
    }

    // Ported from MusicVideoSearchTests.swift: testPickAfterTheSongChangedUsesNothingAndSaysSo
    [Fact]
    public async Task PickAfterTheSongChangedUsesNothingAndSaysSo()
    {
        var source = new FakeSearching(Song) { Outcome = new MusicVideoSearchOutcome.Results([Result("AAAAAAAAAAA")]) };
        var closed = 0;
        var model = new MusicVideoSearchModel(source) { OnClose = () => closed++ };
        model.Prepare();
        await model.SettleAsync();
        source.SearchSong = new MusicVideoSong("Save Your Tears", "The Weeknd", null, 215);
        model.Pick("AAAAAAAAAAA");
        Assert.Empty(source.Picks);
        Assert.Equal(0, closed);
        Assert.Equal(MusicVideoSearchModel.SongChangedNote, model.Notice);
        Assert.Equal("The Weeknd Save Your Tears", model.Query);
        Assert.Empty(model.Rows);
    }

    // Ported from MusicVideoSearchTests.swift: testBackToAutomaticClearsThePick
    [Fact]
    public async Task BackToAutomaticClearsThePick()
    {
        var source = new FakeSearching(Song);
        var closed = 0;
        var model = new MusicVideoSearchModel(source) { OnClose = () => closed++ };
        model.Prepare();
        await model.SettleAsync();
        Assert.False(model.CanGoBackToAutomatic);
        model.BackToAutomatic();
        Assert.Equal(0, source.Cleared);
        source.PickedVideoId = "AAAAAAAAAAA";
        Assert.True(model.CanGoBackToAutomatic);
        model.BackToAutomatic();
        Assert.Equal(1, source.Cleared);
        Assert.Equal(1, closed);
    }

    // Ported from MusicVideoSearchTests.swift: testResolverSearchSendsOnlyTheQueryAndCachesNothing
    [Fact]
    public async Task ResolverSearchSendsOnlyTheQueryAndCachesNothing()
    {
        var http = new FakeHttp();
        MusicVideoWorld.ServeSearch(http, ("4NRXx6U8ABQ", "a", "b"), ("fHI8X4OXluQ", "c", "d"), ("J7p4bzqLvCw", "e", "f"));
        var resolver = _world.Resolver(http: http);
        var outcome = Assert.IsType<MusicVideoSearchOutcome.Results>(await resolver.SearchResultsAsync("The Weeknd Blinding Lights", TestContext.Current.CancellationToken));
        Assert.Equal(3, outcome.Videos.Count);
        Assert.Equal([ApiFixtures.SearchPath, ApiFixtures.VideosPath], http.Paths);
        Assert.Equal("The Weeknd Blinding Lights", FakeHttp.QueryOf(http.Urls[0]));
        Assert.Equal(0, resolver.Cache.Count);
        Assert.Single(resolver.RateLimit.Sent);
    }

    // Ported from MusicVideoSearchTests.swift: testResolverSearchStates
    [Fact]
    public async Task ResolverSearchStates()
    {
        var http = new FakeHttp();
        var resolver = _world.Resolver(http: http);
        http.ByPath[ApiFixtures.SearchPath] = new FakeHttp.Reply(200, "<html>Before you continue to YouTube</html>"u8.ToArray());
        Assert.IsType<MusicVideoSearchOutcome.Failed>(await resolver.SearchResultsAsync("x", TestContext.Current.CancellationToken));
        http.ByPath[ApiFixtures.SearchPath] = new FakeHttp.Reply(200, """{"items":[]}"""u8.ToArray());
        var none = Assert.IsType<MusicVideoSearchOutcome.Results>(await resolver.SearchResultsAsync("x", TestContext.Current.CancellationToken));
        Assert.Empty(none.Videos);
        http.ByPath[ApiFixtures.SearchPath] = new FakeHttp.Reply(429, [], new() { ["Retry-After"] = "120" });
        Assert.IsType<MusicVideoSearchOutcome.RateLimited>(await resolver.SearchResultsAsync("x", TestContext.Current.CancellationToken));
        var sentBefore = http.Urls.Count;
        Assert.IsType<MusicVideoSearchOutcome.RateLimited>(await resolver.SearchResultsAsync("x", TestContext.Current.CancellationToken));
        Assert.Equal(sentBefore, http.Urls.Count);
        Assert.IsType<MusicVideoSearchOutcome.Failed>(await resolver.SearchResultsAsync("  ", TestContext.Current.CancellationToken));
    }

    // Ported from MusicVideoSearchTests.swift: testPickedVideoGoesThroughThePastedLinkPath
    [Fact]
    public async Task PickedVideoGoesThroughThePastedLinkPath()
    {
        var controller = _world.Controller(_world.Resolver());
        controller.Update(_world.Track, enabled: true, online: false);
        await controller.SettleAsync();
        var key = _world.Track.Song.Key;
        Assert.Equal(key, controller.SearchSong?.Key);
        Assert.False(controller.UsePickedVideo("4NRXx6U8ABQ", "another song"));
        Assert.False(controller.UsePickedVideo("not an id", key));
        Assert.Null(controller.PickedVideoId);
        Assert.True(controller.UsePickedVideo("4NRXx6U8ABQ", key));
        await controller.SettleAsync();
        Assert.Equal("4NRXx6U8ABQ", controller.PickedVideoId);
        Assert.Equal("https://www.youtube.com/watch?v=4NRXx6U8ABQ", controller.PastedLink);
        Assert.Equal(YouTube("4NRXx6U8ABQ"), controller.SourceFor(_world.Track));
        controller.ClearPastedLink();
        await controller.SettleAsync();
        Assert.Null(controller.PickedVideoId);
    }
}
