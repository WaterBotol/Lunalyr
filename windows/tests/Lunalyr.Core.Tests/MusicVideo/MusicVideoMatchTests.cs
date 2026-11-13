using Lunalyr.Core.MusicVideo;

namespace Lunalyr.Core.Tests.MusicVideo;

// Ported from MusicVideoMatchTests.swift: the YouTube pick must be the song itself, from the artist, and never a live or
// fan upload. The library-pick tests aren't ported (Windows has no Apple Music library to match against: the user chooses
// the file), nor the caption-hiding one (undocumented player calls; see the M6 notes).
public sealed class MusicVideoMatchTests
{
    private static string? Pick(IReadOnlyList<YouTubeVideoResult> results, string title, string artist) =>
        YouTubeSearch.OfficialVideo(results, title, artist)?.VideoId;

    // Ported from MusicVideoMatchTests.swift: testAFansLiveRecordingOfAnotherSongIsNeverPicked
    [Fact]
    public void AFansLiveRecordingOfAnotherSongIsNeverPicked()
    {
        var fan = new YouTubeVideoResult("AAAAAAAAAAA",
            "Dig What You Dug - 4K - Wallows - Republik, Honolulu - 02/28/23 - PhonesUP", "PhonesUP");
        Assert.Null(Pick([fan], "I Don't Want to Talk", "Wallows"));
        Assert.Null(Pick([fan], "Dig What You Dug", "Wallows"));
        var verified = fan with { VerifiedArtist = true, Channel = "Wallows" };
        Assert.Null(Pick([verified], "Dig What You Dug", "Wallows"));
        Assert.True(MusicVideoTitleMatch.IsLiveOrFanUpload(fan.Title, "Dig What You Dug"));
    }

    // Ported from MusicVideoMatchTests.swift: testLoveIsNotCherryFlavoredLove
    [Fact]
    public void LoveIsNotCherryFlavoredLove()
    {
        YouTubeVideoResult[] results =
        [
            new("BBBBBBBBBBB", "Wallows – Cherry Flavored Love (Official Video)", "Wallows", VerifiedArtist: true),
            new("CCCCCCCCCCC", "Wallows - Love (Live)", "Wallows", VerifiedArtist: true),
            new("DDDDDDDDDDD", "Wallows - Lovesick (Official Video)", "Wallows"),
        ];
        Assert.Null(Pick(results, "Love", "Wallows"));
        var love = new YouTubeVideoResult("EEEEEEEEEEE", "Wallows – Love (Official Music Video)", "Wallows");
        Assert.Equal("EEEEEEEEEEE", Pick([.. results, love], "Love", "Wallows"));
        Assert.Equal("BBBBBBBBBBB", Pick(results, "Cherry Flavored Love", "Wallows"));
        Assert.Equal("DDDDDDDDDDD", Pick(results, "Lovesick", "Wallows"));
    }

    // Ported from MusicVideoMatchTests.swift: testTitleIsComparedWithoutArtistQualifiersAndCredits
    [Theory]
    [InlineData("Wallows - Are You Bored Yet? (feat. Clairo) [Official Video]", "Are You Bored Yet? (feat. Clairo)", "Wallows")]
    [InlineData("Wallows - Are You Bored Yet? (feat. Clairo) [Official Video]", "Are You Bored Yet?", "Wallows & Clairo")]
    [InlineData("The Weeknd - Blinding Lights (Official Video) [HD]", "Blinding Lights", "The Weeknd")]
    [InlineData("Blinding Lights | The Weeknd | Official Music Video", "Blinding Lights", "The Weeknd")]
    [InlineData("The Weeknd Blinding Lights", "Blinding Lights", "The Weeknd")]
    [InlineData("The Weeknd: Blinding Lights - Remastered 2021", "Blinding Lights", "The Weeknd")]
    [InlineData("Song 2 ft. Someone (Official Video)", "Song 2", "Blur")]
    [InlineData("The Rolling Stones - (I Can't Get No) Satisfaction (Official Lyric Video)", "(I Can't Get No) Satisfaction", "The Rolling Stones")]
    [InlineData("Train - Hey, Soul Sister (Official Video)", "Hey, Soul Sister", "Train")]
    public void TitleIsComparedWithoutArtistQualifiersAndCredits(string video, string song, string artist) =>
        Assert.True(MusicVideoTitleMatch.Matches(video, song, artist), video);

    // Ported from MusicVideoMatchTests.swift: testTitleIsComparedWithoutArtistQualifiersAndCredits (the misses and live uploads)
    [Fact]
    public void OtherSongsAndLiveUploadsDontMatch()
    {
        foreach (var (video, song) in new[]
                 {
                     ("Wallows - Cherry Flavored Love", "Love"), ("Wallows - Lovesick", "Love"),
                     ("Wallows - Love Me Back", "Love"), ("Wallows - Remember When / Love", "Love"),
                 })
            Assert.False(MusicVideoTitleMatch.Matches(video, song, "Wallows"), video);
        Assert.False(MusicVideoTitleMatch.IsLiveOrFanUpload("Train - Hey, Soul Sister (Official Video)", "Hey, Soul Sister"));
        foreach (var live in new[]
                 {
                     "Wallows - Love (Live at Red Rocks)", "Wallows - Love | Concert", "Wallows - Love [Fancam]",
                     "Wallows - Love (Pro-Shot)", "Wallows - Love 2023-05-01", "Wallows - Love - Tour Edition",
                 })
            Assert.True(MusicVideoTitleMatch.IsLiveOrFanUpload(live, "Love"), live);
        Assert.False(MusicVideoTitleMatch.IsLiveOrFanUpload("Live Forever (Official Video)", "Live Forever"));
    }

    // Ported from MusicVideoMatchTests.swift: testOnlyTheArtistsOwnVevoOrVerifiedChannelCounts
    [Fact]
    public void OnlyTheArtistsOwnVevoOrVerifiedChannelCounts()
    {
        const string title = "Wallows - Love (Official Video)";
        Assert.Null(Pick([new("AAAAAAAAAAA", title, "Random Uploads")], "Love", "Wallows"));
        Assert.Null(Pick([new("AAAAAAAAAAA", title, "Wallows - Topic")], "Love", "Wallows"));
        Assert.Equal("AAAAAAAAAAA", Pick([new("AAAAAAAAAAA", title, "WallowsVEVO")], "Love", "Wallows"));
        Assert.Equal("AAAAAAAAAAA", Pick([new("AAAAAAAAAAA", title, "Wallows Official")], "Love", "Wallows"));
        Assert.Equal("AAAAAAAAAAA", Pick([new("AAAAAAAAAAA", title, "Atlantic Records", VerifiedArtist: true)], "Love", "Wallows"));
        Assert.Equal("AAAAAAAAAAA", Pick([new("AAAAAAAAAAA", "Are You Bored Yet? [Official Video]", "Clairo")], "Are You Bored Yet?", "Wallows & Clairo"));
    }

    // Ported from MusicVideoMatchTests.swift: testVerifiedArtistBadgeIsReadFromTheResults
    [Fact]
    public void VerifiedArtistBadgeIsReadFromTheResults()
    {
        const string html = """
            <html><script>var ytInitialData = {"contents":[{"videoRenderer":{"videoId":"4NRXx6U8ABQ",
            "title":{"runs":[{"text":"Wallows - Love (Official Video)"}]},"ownerText":{"runs":[{"text":"Atlantic"}]},
            "ownerBadges":[{"metadataBadgeRenderer":{"icon":{"iconType":"OFFICIAL_ARTIST_BADGE"},
            "style":"BADGE_STYLE_TYPE_VERIFIED_ARTIST"}}]}},{"videoRenderer":{"videoId":"BBBBBBBBBBB",
            "title":{"runs":[{"text":"x"}]},"ownerText":{"runs":[{"text":"y"}]},
            "ownerBadges":[{"metadataBadgeRenderer":{"style":"BADGE_STYLE_TYPE_VERIFIED"}}]}}]};</script></html>
            """;
        var results = YouTubeSearchPage.Results(html);
        Assert.Equal([true, false], results.Select(r => r.VerifiedArtist));
    }

    // Windows' own, beside the badge test: the Data API (the fallback) has no badge, so a result from it never claims one,
    // and a label's channel then doesn't count.
    [Fact]
    public void ApiResultsNeverClaimTheVerifiedBadge()
    {
        var results = YouTubeDataApi.ParseSearch(ApiFixtures.Search(("4NRXx6U8ABQ", "Wallows - Love (Official Video)", "Atlantic")));
        Assert.False(results.Single().VerifiedArtist);
        Assert.Null(Pick(results, "Love", "Wallows"));
    }

    // Ported from MusicVideoMatchTests.swift: testAShortArtistNameInsideAnotherChannelIsNotTheArtists
    [Fact]
    public void AShortArtistNameInsideAnotherChannelIsNotTheArtists()
    {
        Assert.False(YouTubeSearch.Channel("liquidmusic", "iu"));
        Assert.True(YouTubeSearch.Channel("iuofficial", "iu"));
        Assert.True(YouTubeSearch.Channel("officialiu", "iu"));
        Assert.True(YouTubeSearch.Channel("thisiswallowschannel", "wallows"));
        const string title = "IU - Love Wins All (Official MV)";
        Assert.Null(Pick([new("AAAAAAAAAAA", title, "Liquid Music")], "Love Wins All", "IU"));
        Assert.Equal("AAAAAAAAAAA", Pick([new("AAAAAAAAAAA", title, "IU Official")], "Love Wins All", "IU"));
    }

    // Ported from MusicVideoMatchTests.swift: testEveryOlderCachedPickIsThrownAway
    [Fact]
    public void EveryOlderCachedPickIsThrownAway()
    {
        var storage = new MemoryMusicVideoStorage();
        var now = MusicVideoWorld.Start;
        Assert.Equal(3, MusicVideoCache.Version);
        storage.Set(MusicVideoCache.Key, "{\"song\":{\"at\":" + now.ToUnixTimeSeconds() + ",\"video\":\"4NRXx6U8ABQ\",\"v\":2}}");
        Assert.Null(new MusicVideoCache(storage).Lookup("song", now));
    }

    // Adapted from MusicVideoMatchTests.swift: testEmbedShowsOnlyTheVideo. The documented parameters, in the embed's address;
    // unlike the Mac, the player is not enlarged, cropped or hidden (YouTube's API policies don't allow obscuring, cropping
    // or covering its interface): it fills the page as YouTube draws it, fitted without cropping.
    [Fact]
    public void EmbedShowsThePlayerAsYouTubeDrawsIt()
    {
        var html = YouTubeEmbed.Html("4NRXx6U8ABQ", 0, page: 1, language: "fr");
        foreach (var needle in new[]
                 {
                     "controls=0", "disablekb=1", "fs=0", "iv_load_policy=3", "rel=0", "playsinline=1", "cc_load_policy=0", "hl=fr",
                     "mute=1", "pointer-events:none", "overflow:hidden",
                     "#frame{position:absolute;left:0;top:0;width:100%;height:100%;", "#frame iframe{width:100%;height:100%;",
                 })
            Assert.Contains(needle, html);
        foreach (var hiding in new[] { "scale(", "transform", "max(100vw", "177.78vh", "opacity", "visibility", "display:none" })
            Assert.DoesNotContain(hiding, html);
        Assert.Contains("post('state', state)", html);
        Assert.DoesNotContain("cc_lang_pref", html);
        Assert.DoesNotContain("alert", YouTubeEmbed.Html("4NRXx6U8ABQ", 0, language: "x'); alert(1"));
    }

    // Windows' own, in place of testCaptionsAreForcedOff (not ported: unloadModule and setOption('captions') aren't part of
    // the documented IFrame Player API): only documented calls are made, and the player's language follows Windows'.
    [Fact]
    public void OnlyDocumentedPlayerCallsAreMade()
    {
        var html = YouTubeEmbed.Html("4NRXx6U8ABQ", 0);
        Assert.DoesNotContain("unloadModule", html);
        Assert.DoesNotContain("setOption", html);
        Assert.DoesNotContain("host:", html);
        Assert.Contains("new YT.Player('player'", html);
        Assert.Equal("fr", YouTubeEmbed.PlayerLanguage(new System.Globalization.CultureInfo("fr-FR")));
        Assert.Equal("en", YouTubeEmbed.PlayerLanguage(new System.Globalization.CultureInfo("en-AU")));
    }

    // Windows' own, in place of testPlayerIsVisibleOnlyWhilePlaying (not ported: the Mac hides a paused, buffering or ended
    // player because YouTube draws its interface over it; YouTube's API policies don't allow covering that interface). The
    // player is on screen from ready, in every state, until it fails.
    [Fact]
    public void PlayerIsOnScreenInEveryStateOnceReady()
    {
        Assert.True(YouTubeEmbed.ShowsPlayer(ready: true, failed: false));
        Assert.False(YouTubeEmbed.ShowsPlayer(ready: false, failed: false));
        Assert.False(YouTubeEmbed.ShowsPlayer(ready: true, failed: true));
        Assert.True(YouTubeEmbed.IsPlaying(new MusicVideoDrift.Sample(3, true)));
        Assert.False(YouTubeEmbed.IsPlaying(new MusicVideoDrift.Sample(3, false)));
        Assert.False(YouTubeEmbed.IsPlaying(new MusicVideoDrift.Sample(3, true, true)));

        var session = new YouTubeEmbedSession(sleep: (_, ct) => Task.Delay(Timeout.Infinite, ct));
        var page = session.Attach("4NRXx6U8ABQ").Page;
        YouTubeEmbedSession.Effect State(int value, bool trusted = true) =>
            session.Receive(new(YouTubeEmbedSession.MessageKind.State, value, "4NRXx6U8ABQ", page), trusted);
        // Before ready: there is no player to show yet.
        Assert.Equal(YouTubeEmbedSession.Effect.Show(false), State(-1));
        Assert.False(session.ShowsPlayer);
        Assert.Equal(YouTubeEmbedSession.Effect.Ready,
            session.Receive(new(YouTubeEmbedSession.MessageKind.Ready, 0, "4NRXx6U8ABQ", page), true));
        Assert.True(session.ShowsPlayer);
        // Playing, paused, ended, buffering, cued: all on screen, as YouTube draws them.
        foreach (var state in new[] { 1, 2, 0, 3, 5, -1 })
            Assert.Equal(YouTubeEmbedSession.Effect.Show(true), State(state));
        Assert.Equal(YouTubeEmbedSession.Effect.None, State(1, trusted: false));
        Assert.Equal(new YouTubeEmbedSession.Message(YouTubeEmbedSession.MessageKind.State, 1, "4NRXx6U8ABQ", page),
            YouTubeEmbedSession.Message.Parse("{\"type\":\"state\",\"value\":1,\"video\":\"4NRXx6U8ABQ\",\"page\":" + page + "}"));
        session.Receive(new(YouTubeEmbedSession.MessageKind.Error, 150, "4NRXx6U8ABQ", page), true);
        Assert.Equal(YouTubeEmbedSession.Effect.Show(false), State(1));
        // A closed player (a long pause, the type changed) is not on screen, whatever arrives late.
        page = session.Attach("4NRXx6U8ABQ").Page;
        session.Receive(new(YouTubeEmbedSession.MessageKind.Ready, 0, "4NRXx6U8ABQ", page), true);
        Assert.True(session.ShowsPlayer);
        session.Stop();
        Assert.False(session.ShowsPlayer);
    }

    // Ported from MusicVideoMatchTests.swift: testNonLatinAndKpopTitlesMatch
    [Theory]
    [InlineData("YOASOBI「アイドル」 Official Music Video", "アイドル", "YOASOBI")]
    [InlineData("米津玄師 MV「Lemon」", "Lemon", "米津玄師")]
    [InlineData("ヨルシカ - 『ただ君に晴れ』 (Music Video)", "ただ君に晴れ", "ヨルシカ")]
    [InlineData("BLACKPINK - 'Shut Down' M/V", "Shut Down", "BLACKPINK")]
    [InlineData("NewJeans (뉴진스) 'Ditto' Official MV", "Ditto", "NewJeans")]
    [InlineData("IVE 아이브 'I AM' MV", "I AM", "IVE")]
    [InlineData("BTS (방탄소년단) ‘Dynamite’ Official MV", "Dynamite", "BTS")]
    [InlineData("Wallows - Don't Want to Talk (Official Video)", "Don't Want to Talk", "Wallows")]
    [InlineData("Artist - Rock 'n' Roll Star (Official Video)", "Rock 'n' Roll Star", "Artist")]
    [InlineData("IU 좋은 날", "좋은 날", "IU")]
    public void NonLatinAndKpopTitlesMatch(string video, string song, string artist) =>
        Assert.True(MusicVideoTitleMatch.Matches(video, song, artist), video);

    // Ported from MusicVideoMatchTests.swift: testNonLatinAndKpopTitlesMatch (still exact)
    [Fact]
    public void NonLatinTitlesStayExact()
    {
        Assert.False(MusicVideoTitleMatch.Matches("BLACKPINK - 'Shut Down' M/V", "Down", "BLACKPINK"));
        Assert.False(MusicVideoTitleMatch.Matches("YOASOBI「アイドル」 Official Music Video", "アイ", "YOASOBI"));
        Assert.False(MusicVideoTitleMatch.Matches("IVE 아이브 'I AM' MV", "I", "IVE"));
        Assert.Equal("AAAAAAAAAAA", Pick([new("AAAAAAAAAAA", "YOASOBI「アイドル」 Official Music Video", "Ayase / YOASOBI")], "アイドル", "YOASOBI"));
    }

    // Ported from MusicVideoMatchTests.swift: testTheSampleKeepsThePlayerMuted
    [Fact]
    public void TheSampleKeepsThePlayerMuted() =>
        Assert.Contains("if (player.isMuted && !player.isMuted()) player.mute();", YouTubeEmbed.Html("4NRXx6U8ABQ", 0));

    // Ported from MusicVideoMatchTests.swift: testAReadyPlayerThatNeverPlaysFallsBack
    [Fact]
    public void AReadyPlayerThatNeverPlaysFallsBack()
    {
        var session = new YouTubeEmbedSession(sleep: (_, ct) => Task.Delay(Timeout.Infinite, ct));
        var page = session.Attach("4NRXx6U8ABQ").Page;
        Assert.Equal(YouTubeEmbedSession.Effect.None, session.NotePlayback(true, false, 0));
        Assert.Equal(YouTubeEmbedSession.Effect.Ready, session.Receive(new(YouTubeEmbedSession.MessageKind.Ready, 0, "4NRXx6U8ABQ", page), true));
        Assert.Equal(YouTubeEmbedSession.Effect.None, session.NotePlayback(true, false, 100));
        Assert.Equal(YouTubeEmbedSession.Effect.None, session.NotePlayback(true, false, 110));
        Assert.Equal(YouTubeEmbedSession.Effect.None, session.NotePlayback(true, true, 115));
        Assert.Equal(YouTubeEmbedSession.Effect.None, session.NotePlayback(true, false, 116));
        Assert.Equal(YouTubeEmbedSession.Effect.None, session.NotePlayback(false, false, 130));
        Assert.Equal(YouTubeEmbedSession.Effect.None, session.NotePlayback(true, false, 131));
        Assert.Equal(YouTubeEmbedSession.Effect.None, session.NotePlayback(true, false, 150));
        Assert.Equal(YouTubeEmbedSession.Effect.Fail("4NRXx6U8ABQ", MusicVideoPlaybackFailure.Transient), session.NotePlayback(true, false, 151));
        Assert.Equal(YouTubeEmbedSession.Effect.None, session.NotePlayback(true, false, 200));
        var next = session.Attach("4NRXx6U8ABQ").Page;
        session.Receive(new(YouTubeEmbedSession.MessageKind.Ready, 0, "4NRXx6U8ABQ", next), true);
        Assert.Equal(YouTubeEmbedSession.Effect.None, session.NotePlayback(true, false, 300));
        session.Stop();
    }
}
