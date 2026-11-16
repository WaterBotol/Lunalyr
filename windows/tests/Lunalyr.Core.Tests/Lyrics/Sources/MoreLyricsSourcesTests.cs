using System.Text;
using System.Text.Json;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Pipeline;
using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Core.Lyrics.Sources;

namespace Lunalyr.Core.Tests.Lyrics.Sources;

/// <summary>
/// The sources added on 16 Nov 2026, from fixtures (no network): ported from the Mac's MoreLyricsProvidersTests.swift
/// (dc56f8a). Lyric text is made up. The Mac's stored-defaults half lives in the Shell tests (AddedLyricSourceTests).
/// </summary>
public sealed class MoreLyricsSourcesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly LyricQuery Query = new("Our Song", "Our Artist", null, 180);

    /// <summary>The timing tier of the lyrics a source would offer, read as the lookup reads them.</summary>
    private static int Tier(LyricSourceId source, string? lrc)
    {
        Assert.NotNull(lrc);
        Assert.True(ParsedLyricPayloadProbe.Instance.IsUsable(LyricFormat.Lrc, lrc), "no timed line");
        var candidate = LyricCandidateBuilder.Build(new RawLyrics(source, LyricFormat.Lrc, lrc, "Our Song", "Our Artist", 180), Query);
        Assert.NotNull(candidate);
        return LyricTimingRules.TimingTier(candidate.Result);
    }

    private static IReadOnlyList<LyricLine> Lines(string? lrc) => LrcParser.Parse(lrc ?? "", 180);

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    // ---- Shared ----

    // Ported from MoreLyricsProvidersTests.swift: testEnhancedLineIsWordTimedLRC
    [Fact]
    public void EnhancedLineIsWordTimedLrc()
    {
        var row = MoreLyrics.EnhancedLine([new(1, 1.5, "Sing "), new(1.5, 2.25, "along")]);
        Assert.Equal("[00:01.000]<00:01.000>Sing <00:01.500>along<00:02.250>", row);
        Assert.Null(MoreLyrics.EnhancedLine([new(1, 2, " ")]));
        Assert.Equal(253, MoreLyrics.ClockSeconds("4:13"));
        Assert.Null(MoreLyrics.ClockSeconds("00:00"));
    }

    [Fact]
    public void TheFormBodyIsSortedAndEncodedAsTheMacsIs()
    {
        var body = Encoding.UTF8.GetString(MoreLyrics.Form(new Dictionary<string, string> { ["b"] = "a b+c", ["a"] = "夜", ["c"] = "x-y_z.*~" }));
        Assert.Equal("a=%E5%A4%9C&b=a%20b%2Bc&c=x-y_z.*%7E", body);
    }

    // ---- Soda Music ----

    // Ported from MoreLyricsProvidersTests.swift: testSodaSearchAndKRCShapedLyricBecomeWordTiming
    [Fact]
    public void SodaSearchAndKrcShapedLyricBecomeWordTiming()
    {
        var search = Json("""{"result_groups":[{"id":"artists","data":[]},{"id":"tracks","data":[{"entity":{"track":{"id":"7001","name":"Our Song","duration":180500,"artists":[{"name":"Our Artist"}]}}}]}]}""");
        var tracks = SodaMusicSource.Tracks(search);
        Assert.Equal(["7001"], tracks.Select(t => t.Id));
        Assert.Equal(180.5, tracks[0].Duration!.Value, 3);
        var page = Json("""{"lyric":{"content":"[1000,1600]<0,400,0>Sing <400,600,0>along <1000,300,0>with <1300,300,0>me\n[4000,1500]<0,500,0>Next <500,500,0>line <1000,500,0>here\n[6000,1500]<0,500,0>Last <500,500,0>words <1000,500,0>now"},"seo_track":{"track":{"id":"7001"}}}""");
        var lrc = SodaMusicSource.Lyrics(page);
        Assert.Equal("Sing along with me", Lines(lrc)[0].Text);
        Assert.Equal(1, Lines(lrc)[0].Start, 3);
        Assert.Equal(3, Tier(LyricSourceId.SodaMusic, lrc));
    }

    // Ported from MoreLyricsProvidersTests.swift: testSodaLineLyricIsKeptAndEmptyLyricIsNone
    [Fact]
    public void SodaLineLyricIsKeptAndEmptyLyricIsNone()
    {
        var page = Json("""{"lyric":{"content":"[00:01.00]Sing along\n[00:04.00]Next line"},"seo_track":{"track":{"id":"7001"}}}""");
        Assert.Equal(1, Tier(LyricSourceId.SodaMusic, SodaMusicSource.Lyrics(page)));
        Assert.Null(SodaMusicSource.Lyrics(Json("""{"lyric":{"content":""},"seo_track":{"track":{"id":"7001"},"lyric":{"content":""}}}""")));
    }

    // ---- PetitLyrics ----

    // Ported from MoreLyricsProvidersTests.swift: testPetitAnswerAndCharacterTimedLyric
    [Fact]
    public void PetitAnswerAndCharacterTimedLyric()
    {
        const string Wsy = "<wsy><line><linestring>Sing it now</linestring>        <word><starttime>1000</starttime><endtime>1200</endtime><wordstring>S</wordstring></word>        <word><starttime>1200</starttime><endtime>1400</endtime><wordstring>i</wordstring></word>        <word><starttime>1400</starttime><endtime>1500</endtime><wordstring>ng</wordstring></word>        <word><starttime>1500</starttime><endtime>1600</endtime><wordstring> </wordstring></word>        <word><starttime>1600</starttime><endtime>2000</endtime><wordstring>it</wordstring></word>        <word><starttime>2000</starttime><endtime>2100</endtime><wordstring> </wordstring></word>        <word><starttime>2100</starttime><endtime>2600</endtime><wordstring>now</wordstring></word></line>        <line><linestring>Then again</linestring>        <word><starttime>3000</starttime><endtime>3500</endtime><wordstring>Then</wordstring></word>        <word><starttime>3500</starttime><endtime>3600</endtime><wordstring> </wordstring></word>        <word><starttime>3400</starttime><endtime>4200</endtime><wordstring>again</wordstring></word></line>        <line><linestring>We&#039;re done</linestring>        <word><starttime>5000</starttime><endtime>5300</endtime><wordstring>We</wordstring></word>        <word><starttime>5300</starttime><endtime>5400</endtime><wordstring>&#039;</wordstring></word>        <word><starttime>5400</starttime><endtime>5600</endtime><wordstring>re</wordstring></word>        <word><starttime>5600</starttime><endtime>5700</endtime><wordstring> </wordstring></word>        <word><starttime>5700</starttime><endtime>6400</endtime><wordstring>done</wordstring></word></line></wsy>";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(Wsy));
        var answer = "<?xml version='1.0' encoding='UTF-8' ?><response><status>00000000</status><songs><song><lyricsId>11</lyricsId>"
            + "<title>Our Song</title><artist>Our Artist</artist><duration>180200</duration><lyricsType>3</lyricsType><isOfficial>1</isOfficial>"
            + $"<lyricsData>{encoded}</lyricsData></song><song><lyricsId>12</lyricsId><title>Our Song</title><lyricsType>1</lyricsType></song></songs></response>";
        var (status, songs) = PetitLyricsSource.Songs(Encoding.UTF8.GetBytes(answer));
        Assert.Equal("00000000", status);
        Assert.Equal(["11", "12"], songs.Select(s => s["lyricsId"]));
        Assert.Equal("1", songs[0]["isOfficial"]);
        var lrc = PetitLyricsSource.EnhancedLrc(songs[0]["lyricsData"]);
        var rows = Lines(lrc);
        Assert.Equal(["Sing it now", "Then again", "We're done"], rows.Select(r => r.Text));
        // Letters timed one by one become one word; a timed space joins the word before it.
        Assert.Equal(["Sing", "it", "now"], rows[0].Words.Select(w => w.Text.Trim()));
        Assert.Equal(["We're", "done"], rows[^1].Words.Select(w => w.Text.Trim()));
        // A word running into the next is cut at the next one's start.
        Assert.Equal(3.4, rows[1].Words[0].End, 3);
        Assert.Equal(3, Tier(LyricSourceId.PetitLyrics, lrc));
    }

    // Ported from MoreLyricsProvidersTests.swift: testPetitRejectedClientStatusIsRead
    [Fact]
    public void PetitRejectedClientStatusIsRead()
    {
        var (status, songs) = PetitLyricsSource.Songs(Encoding.UTF8.GetBytes("<response><status>8170f003</status></response>"));
        Assert.Equal("8170f003", status);
        Assert.Empty(songs);
        Assert.Null(PetitLyricsSource.EnhancedLrc("not base64 xml"));
    }

    [Fact]
    public void PetitXmlWithADocumentTypeIsRefused()
    {
        var hostile = "<?xml version='1.0'?><!DOCTYPE r [<!ENTITY e 'x'>]><response><status>&e;</status></response>";
        Assert.Null(PetitLyricsSource.Songs(Encoding.UTF8.GetBytes(hostile)).Status);
    }

    // ---- Deezer ----

    private static JsonElement DeezerLyrics(int wordShift)
    {
        string Words(int start, string[] pieces) => string.Join(",", pieces.Select((word, i) =>
            $"{{\"start\":{start + wordShift + (i * 400)},\"end\":{start + wordShift + (i * 400) + 350},\"word\":\"{word}\"}}"));
        return Json("""
            {"data":{"track":{"id":"1","lyrics":{"synchronizedLines":[
              {"lrcTimestamp":"[00:01.00]","milliseconds":1000,"line":"Sing along with me"},
              {"lrcTimestamp":"[00:04.00]","milliseconds":4000,"line":"Next line here"},
              {"lrcTimestamp":"[00:06.00]","milliseconds":6000,"line":""},
              {"lrcTimestamp":"[00:07.00]","milliseconds":7000,"line":"Last words now"}],
             "synchronizedWordByWordLines":[
              {"start":1000,"end":2600,"words":[
            """ + Words(1000, ["Sing", "along", "with", "me"]) + """
            ]},
              {"start":4000,"end":5200,"words":[
            """ + Words(4000, ["Next", "line", "here"]) + """
            ]},
              {"start":7000,"end":8200,"words":[
            """ + Words(7000, ["Last", "words", "now"]) + """
            ]}]}}}}
            """);
    }

    // Ported from MoreLyricsProvidersTests.swift: testDeezerSearchAndAgreeingWordTrack
    [Fact]
    public void DeezerSearchAndAgreeingWordTrack()
    {
        var search = Json("""{"data":{"search":{"results":{"tracks":{"edges":[{"node":{"id":"42","title":"Our Song","duration":181,"hasSynchronizedLyrics":true,"contributors":{"edges":[{"node":{"name":"Our Artist"}}]},"album":{"displayTitle":"Ours"}}},{"node":{"id":"43","title":"Our Song (Cover)","duration":200,"hasSynchronizedLyrics":false,"contributors":{"edges":[]}}}]}}}}}""");
        var tracks = DeezerSource.Tracks(search);
        Assert.Equal(["42", "43"], tracks.Select(t => t.Id));
        Assert.Equal([true, false], tracks.Select(t => t.Synced));
        Assert.Equal("Our Artist", tracks[0].Artist);
        var lrc = DeezerSource.Lrc(DeezerLyrics(wordShift: 0));
        Assert.Equal(["Sing", "along", "with", "me"], Lines(lrc)[0].Words.Select(w => w.Text.Trim()));
        Assert.Equal(3, Tier(LyricSourceId.Deezer, lrc));
    }

    // Ported from MoreLyricsProvidersTests.swift: testDeezerShiftedWordTrackFallsBackToLines
    [Fact]
    public void DeezerShiftedWordTrackFallsBackToLines()
    {
        var lrc = DeezerSource.Lrc(DeezerLyrics(wordShift: 9000));
        Assert.Equal(["Sing along with me", "Next line here", "Last words now"], Lines(lrc).Select(l => l.Text));
        Assert.Equal(1, Tier(LyricSourceId.Deezer, lrc));
        Assert.Null(DeezerSource.Lrc(Json("""{"data":{"track":null},"errors":[{"type":"LyricsNotFoundError"}]}""")));
    }

    // Ported from MoreLyricsProvidersTests.swift: testDeezerLanguageAndTokenExpiry
    [Fact]
    public void DeezerLanguageAndTokenExpiry()
    {
        Assert.Equal("ja-JP", DeezerSource.Language("YOASOBI 夜に駆ける"));
        Assert.Equal("ko-KR", DeezerSource.Language("아이유 밤편지"));
        Assert.Equal("zh-CN", DeezerSource.Language("周杰伦 稻香"));
        Assert.Equal("en-US", DeezerSource.Language("Our Artist Our Song"));
        var claims = Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"exp":1800000000}""")).Replace("=", "", StringComparison.Ordinal);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), DeezerSource.Expiry($"head.{claims}.sig"));
        Assert.Null(DeezerSource.Expiry("garbage"));
    }

    // ---- Migu ----

    /// <summary>Encrypted with an independent XXTEA implementation from made-up text (the Mac's fixture).</summary>
    private const string MiguMrc = "df4af43148739d779022526ad4b15b57cc0c581ceee4238425e2b06f8ff979bfdbd288b4ad37aba9f4007854d2643c4fb343a06ae496923a8b8f161563b0b88cf3b83483bfa2f8132278f05109fa3db36919c8f68e551cd0b6793aacb40a44a46d73ea69e0c086d17f73525f7c9ccff271cf8b9e9d6153f97821886fb61aeb33c37c88770fa71e6d14db60793219f4a1590cc909d2172fe6d9a69533f0f99174105bf417134c6e987096c3eb06b34de7b83d309c0cc45a7f02fa60de31f168beea7529faeae612f4f9dc907123ec9c51ba2e7594e9693a663176bd2271545076366258e01fe2b7fa5d6afcde83cb73fff3b76cee1a5158806f80c6bea4aa75b5a26b456341b7d4f96b5bf479cfbc13cbf892964e6765c11b21edfcbed83bbc1a2194d7eb8f803375ee2e5979363b0a29c812115d3c7dc7db90598072382d7b12b680b388d816bebb22c42c4fa3b9eb7601e2ff0105c77a50d0ca027dc6083dd92a82ddca734b7c54c9e06ec5af2d74f2237d0fdad1eb606af1fced239edceeba90a1385ad206ae81e8a61af0b4a8524a636b1a71befe1525007042b119e0ecd3df5980f0c83325c227228e2a57c6aae470c7aa6d0e66f9b1";

    // Ported from MoreLyricsProvidersTests.swift: testMiguMRCDecryptsToWordTiming
    [Fact]
    public void MiguMrcDecryptsToWordTiming()
    {
        var text = MiguSource.Decrypt(MiguMrc);
        Assert.StartsWith("[ti:Our Song]\n[0,0]Our(0,0)", text, StringComparison.Ordinal);
        var lrc = MiguSource.EnhancedLrc(text!);
        var rows = Lines(lrc);
        Assert.Equal(["Sing along with me", "Next line here", "Last words now"], rows.Select(r => r.Text));
        Assert.Equal([1, 1.4, 2, 2.3], rows[0].Words.Select(w => Math.Round(w.Start, 3)));
        Assert.Equal(3, Tier(LyricSourceId.Migu, lrc));
        Assert.Null(MiguSource.Decrypt("abc"));
    }

    // Ported from MoreLyricsProvidersTests.swift: testMiguSearchLengthAndLRCHeader
    [Fact]
    public void MiguSearchLengthAndLrcHeader()
    {
        var search = Json("""{"code":"000000","songResultData":{"result":[{"copyrightId":"600","name":"Our Song","singers":[{"name":"Our Artist"}],"lyricUrl":"https://example.invalid/l","mrcurl":"https://example.invalid/m","newRateFormats":[{"format":"020007","size":"2880000","fileType":"mp3"}]},{"copyrightId":"601","name":"No Lyric","lyricUrl":""}]}}""");
        var items = MiguSource.Items(search);
        Assert.Equal(["600"], items.Select(i => i.Id));
        Assert.Equal(180, items[0].Duration!.Value, 3);
        var lrc = MiguSource.StripHeader("[00:01.00]歌曲名 Our Song\n[00:02.00]歌手名 Our Artist\n[00:10.00]Sing along\n[00:14.00]Next line");
        Assert.Equal(["Sing along", "Next line"], Lines(lrc).Select(l => l.Text));
        Assert.Equal(1, Tier(LyricSourceId.Migu, lrc));
    }

    // ---- VIBE ----

    // Ported from MoreLyricsProvidersTests.swift: testVibeLyricKeepsTheOriginalTrack
    [Fact]
    public void VibeLyricKeepsTheOriginalTrack()
    {
        var answer = Json("""{"response":{"result":{"lyric":{"hasSyncLyric":true,"syncLyric":{"startTimeIndex":[1.0,4.5,8.0],"contents":[{"languageType":"default","text":["Sing along","","Next line"]},{"languageType":"ko","text":["번역","","다음"]}]}}}}}""");
        var lrc = VibeSource.Lrc(answer);
        Assert.Equal(["Sing along", "Next line"], Lines(lrc).Where(l => l.Text.Length > 0).Select(l => l.Text));
        Assert.Equal(1, Tier(LyricSourceId.Vibe, lrc));
        Assert.Null(VibeSource.Lrc(Json("""{"response":{"result":{"lyric":{"hasSyncLyric":false,"normalLyric":{"text":"Sing"}}}}}""")));
    }

    // Ported from MoreLyricsProvidersTests.swift: testKoreanReadingAndQueryCleanup
    [Fact]
    public void KoreanReadingAndQueryCleanup()
    {
        Assert.Equal("晴天", VibeSource.KoreanReadingRemoved("晴天(청천)"));
        Assert.Equal("밤편지 (Through the Night)", VibeSource.KoreanReadingRemoved("밤편지 (Through the Night)"));
        Assert.Equal("Song (라이브)", VibeSource.KoreanReadingRemoved("Song (라이브)"));
        Assert.Equal("I Dont Love You A B", VibeSource.QueryText("I Don't Love You (feat. Someone) A & B"));
    }

    // ---- Genie ----

    // Ported from MoreLyricsProvidersTests.swift: testGenieJSONPDropsTheTitleArtistHeader
    [Fact]
    public void GenieJsonpDropsTheTitleArtistHeader()
    {
        const string Body = """GenieCallback({"0":"Our Song","500":"Our Artist","1000":"Sing along","4000":"Next line","9000":""});""";
        var lrc = GenieSource.Lrc(Body, "Our Song", "Our Artist");
        Assert.Equal(["Sing along", "Next line"], Lines(lrc).Where(l => l.Text.Length > 0).Select(l => l.Text));
        Assert.Equal(1, Tier(LyricSourceId.Genie, lrc));
        // A first line that only repeats the title may be sung: kept.
        var sung = GenieSource.Lrc("""GenieCallback({"1000":"Our Song","4000":"Next line"});""", "Our Song", "Our Artist");
        Assert.Equal("Our Song", Lines(sung)[0].Text);
        Assert.Null(GenieSource.Lrc("NOT FOUND LYRICS", "Our Song", "Our Artist"));
    }

    // ---- LyricFind (YouTube Music) ----

    // Ported from MoreLyricsProvidersTests.swift: testYouTubeMusicSearchPageAndLyricFindCredit
    [Fact]
    public void YouTubeMusicSearchPageAndLyricFindCredit()
    {
        var search = Json("""
            {"contents":{"tabs":[{"musicShelfRenderer":{"contents":[{"musicResponsiveListItemRenderer":{
              "flexColumns":[{"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"Our Song"}]}}},
                             {"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"Our Artist"},{"text":" • "},{"text":"Ours"},{"text":" • "},{"text":"3:00"}]}}}],
              "overlay":{"musicItemThumbnailOverlayRenderer":{"content":{"musicPlayButtonRenderer":{"playNavigationEndpoint":{"watchEndpoint":{"videoId":"vid1"}}}}}}}}]}}]}}
            """);
        var songs = LyricFindSource.Songs(search);
        Assert.Equal(["vid1"], songs.Select(s => s.VideoId));
        Assert.Equal("Our Artist", songs[0].Artist);
        Assert.Equal(180, songs[0].Duration);
        var next = Json("""{"tabs":[{"endpoint":{"browseEndpoint":{"browseId":"MPLYt_x","browseEndpointContextSupportedConfigs":{"browseEndpointContextMusicConfig":{"pageType":"MUSIC_PAGE_TYPE_TRACK_LYRICS"}}}}}]}""");
        Assert.Equal("MPLYt_x", LyricFindSource.LyricPage(next));
        JsonElement Browse(string source) => Json("""{"contents":{"elementRenderer":{"timedLyricsModel":{"lyricsData":{"timedLyricsData":[{"lyricLine":"Sing along","cueRange":{"startTimeMilliseconds":"1000","endTimeMilliseconds":"3000"}},{"lyricLine":"Next line","cueRange":{"startTimeMilliseconds":"4000","endTimeMilliseconds":"6000"}}],"sourceMessage":"Source: """
            + source + "\"}}}}}");
        var lrc = LyricFindSource.Lrc(Browse("LyricFind"));
        Assert.Equal(["Sing along", "Next line"], Lines(lrc).Select(l => l.Text));
        Assert.Equal(1, Tier(LyricSourceId.LyricFind, lrc));
        // Musixmatch's lyrics relayed by YouTube Music are not LyricFind's.
        Assert.Null(LyricFindSource.Lrc(Browse("Musixmatch")));
    }

    // ---- Whole lookups through stubbed replies ----

    // Ported from MoreLyricsProvidersTests.swift: testStubbedLookupsReturnTheirSources
    [Fact]
    public async Task StubbedLookupsReturnTheirSources()
    {
        var handler = new FixtureHandler { Fallback = Reply.Text(200, "{}") }
            .Set("app.genie.co.kr/search/category/songs.json", Reply.Text(200, """{"searchResult":{"result":{"songs":{"items":[{"song_id":5,"song_name":{"original":"Our Song"},"artist_name":{"original":"Our Artist"},"album_name":{"original":"Ours"},"duration":{"time":180},"service_available":{"lyrics_service_yn":"Y"}}]}}}}"""))
            .Set("dn.genie.co.kr/app/purchase/get_msl.asp", Reply.Text(200, """GenieCallback({"1000":"Sing along","4000":"Next line"});"""))
            .Set("auth.deezer.com/login/anonymous", Reply.Text(200, """{"jwt":"head.eyJleHAiOjQxMDI0NDQ4MDB9.sig"}"""));
        handler.Responder = request => request.RequestUri!.Host == "pipe.deezer.com"
            ? Reply.Text(200, request.Content!.ReadAsStringAsync().GetAwaiter().GetResult().Contains("SearchTracks", StringComparison.Ordinal)
                ? """{"data":{"search":{"results":{"tracks":{"edges":[{"node":{"id":"42","title":"Our Song","duration":180,"hasSynchronizedLyrics":true,"contributors":{"edges":[{"node":{"name":"Our Artist"}}]}}}]}}}}}"""
                : """{"data":{"track":{"lyrics":{"synchronizedLines":[{"milliseconds":1000,"line":"Sing along"},{"milliseconds":4000,"line":"Next line"}],"synchronizedWordByWordLines":[{"start":1000,"end":2000,"words":[{"start":1000,"end":1400,"word":"Sing"},{"start":1400,"end":2000,"word":"along"}]},{"start":4000,"end":5000,"words":[{"start":4000,"end":4400,"word":"Next"},{"start":4400,"end":5000,"word":"line"}]}]}}}}""")
            : null;
        var client = handler.Client();
        var genie = await new GenieSource(client).FetchAsync(Query, Ct);
        Assert.Equal(LyricSourceId.Genie, genie?.Source);
        Assert.Equal(1, Tier(LyricSourceId.Genie, genie!.Payload));
        var deezer = await new DeezerSource(client).FetchAsync(Query, Ct);
        Assert.Equal(LyricSourceId.Deezer, deezer?.Source);
        Assert.Equal(3, Tier(LyricSourceId.Deezer, deezer!.Payload));
        // The token is asked for once, the search and lyrics carry it, and every request names a browser.
        Assert.Equal(1, handler.Count("auth.deezer.com"));
        Assert.All(handler.Requests.Where(r => r.RequestUri!.Host == "pipe.deezer.com"),
            r => Assert.Equal("Bearer head.eyJleHAiOjQxMDI0NDQ4MDB9.sig", r.Headers.Authorization?.ToString()));
        Assert.All(handler.Requests, r => Assert.DoesNotContain("Lunalyr", r.Headers.UserAgent.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnExpiredDeezerTokenIsRenewedOnce()
    {
        var tokens = 0;
        var handler = new FixtureHandler();
        handler.Responder = request => request.RequestUri!.Host switch
        {
            "auth.deezer.com" => Reply.Text(200, $$"""{"jwt":"t{{++tokens}}"}"""),
            "pipe.deezer.com" when request.Headers.Authorization?.Parameter == "t1" => Reply.Text(200, """{"errors":[{"type":"JwtTokenExpiredError"}]}"""),
            "pipe.deezer.com" => Reply.Text(200, """{"data":{"search":{"results":{"tracks":{"edges":[]}}}}}"""),
            _ => null,
        };
        Assert.Null(await new DeezerSource(handler.Client()).FetchAsync(Query, Ct));
        Assert.Equal(2, tokens);
    }

    [Fact]
    public async Task PetitLyricsTriesTheSecondClientOnlyForAnUnknownClientId()
    {
        var handler = new FixtureHandler();
        var forms = new List<string>();
        handler.Responder = request =>
        {
            forms.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return request.RequestUri!.Host switch
            {
                "p1.petitlyrics.com" => Reply.Text(200, "<response><status>8170f003</status></response>"),
                "p0.petitlyrics.com" => Reply.Text(200, "<response><status>00000000</status><songs></songs></response>"),
                _ => null,
            };
        };
        Assert.Null(await new PetitLyricsSource(handler.Client()).FetchAsync(Query, Ct));
        Assert.Equal(["p1.petitlyrics.com/api/GetPetitLyricsData.php", "p0.petitlyrics.com/api/GetPetitLyricsData.php"], handler.Addresses);
        var form = forms[0];
        Assert.Contains("clientAppId=p1110417", form, StringComparison.Ordinal);
        Assert.Contains("key_duration=180000", form, StringComparison.Ordinal);

        // Any other status is an error, not a reason to ask again.
        var refused = new FixtureHandler { Fallback = Reply.Text(200, "<response><status>81700001</status></response>") };
        var failure = await Assert.ThrowsAsync<LyricSourceException>(() => new PetitLyricsSource(refused.Client()).FetchAsync(Query, Ct));
        Assert.Equal(LyricSourceFailure.BadResponse, failure.Failure);
        Assert.Single(refused.Requests);
    }

    [Fact]
    public async Task VibeSkipsALyricItMayNotShowAndMiguRefusesABadCode()
    {
        var handler = new FixtureHandler()
            .Set("apis.naver.com/vibeWeb/musicapiweb/v4/search/track", Reply.Text(200, """{"response":{"result":{"tracks":[{"trackId":1,"trackTitle":"Our Song","artists":[{"artistName":"Our Artist"}],"playTime":"3:00"},{"trackId":2,"trackTitle":"Our Song","isAdult":true,"artists":[{"artistName":"Our Artist"}],"playTime":"3:00"}]}}}"""))
            .Set("apis.naver.com/vibeWeb/musicapiweb/vibe/v4/lyric/1", Reply.Text(403, ""));
        Assert.Null(await new VibeSource(handler.Client()).FetchAsync(Query, Ct));
        // The adult track was never asked for.
        Assert.Equal(2, handler.Requests.Count);

        var migu = new FixtureHandler { Fallback = Reply.Text(200, """{"code":"100001"}""") };
        await Assert.ThrowsAsync<LyricSourceException>(() => new MiguSource(migu.Client()).FetchAsync(Query, Ct));
        Assert.Single(migu.Requests);
    }

    // ---- Source list ----

    // Ported from MoreLyricsProvidersTests.swift: testNewSourcesAreWiredAndOrdered
    [Fact]
    public void NewSourcesAreWiredAndOrdered()
    {
        var order = LyricSourceSnapshot.RecommendedOrder.ToList();
        foreach (var source in LyricSourceSnapshot.AddedLater.Where(s => s != LyricSourceId.Musixmatch).Append(LyricSourceId.SodaMusic))
        {
            Assert.True(order.IndexOf(source) > order.IndexOf(LyricSourceId.Kuwo), source.ToString());
            Assert.True(order.IndexOf(source) < order.IndexOf(LyricSourceId.LyricsOvh), source.ToString());
            Assert.NotEmpty(source.TimingDescription());
            Assert.Contains(source, OriginalTitles.Sources);
        }
        Assert.Equal([LyricSourceId.Vibe, LyricSourceId.Genie, LyricSourceId.LyricFind],
            LyricSourceInfo.All.Where(s => s.TimingCeiling() == 1));
        Assert.Subset(ArtistAliases.Catalogues.ToHashSet(),
            new HashSet<LyricSourceId> { LyricSourceId.SodaMusic, LyricSourceId.Migu, LyricSourceId.PetitLyrics, LyricSourceId.Vibe, LyricSourceId.Genie });
        Assert.Equal([LyricSourceId.SodaMusic], LyricSourceSnapshot.OffByDefault.ToArray());
        // Every source the catalogue builds, once.
        var built = LyricSourceCatalog.CreateDefault(new FixtureHandler().Client()).Select(s => s.Id).ToList();
        Assert.Equal(built.Distinct().Count(), built.Count);
        Assert.Equal(LyricSourceInfo.All.Where(s => s != LyricSourceId.AppleMusicCache).ToHashSet(), built.ToHashSet());
    }

    // Ported from MoreLyricsProvidersTests.swift: testSavedOrderTakesNewSourcesAfterKuwo
    [Fact]
    public void SavedOrderTakesNewSourcesAfterKuwo()
    {
        LyricSourceId[] old = [LyricSourceId.Lrclib, LyricSourceId.AppleMusicCache, LyricSourceId.LyricsPlus, LyricSourceId.NetEase, LyricSourceId.QQMusic,
            LyricSourceId.Kugou, LyricSourceId.Kuwo, LyricSourceId.LyricsOvh, LyricSourceId.Amll, LyricSourceId.Lyrimuse];
        static List<LyricSourceId> Placed(IEnumerable<LyricSourceId> order) => LyricSourceSnapshot.OrderFor(order.Select(s => s.RawValue()));
        LyricSourceId[] added = [LyricSourceId.SodaMusic, LyricSourceId.PetitLyrics, LyricSourceId.Deezer, LyricSourceId.Migu, LyricSourceId.Vibe,
            LyricSourceId.Genie, LyricSourceId.LyricFind];
        var merged = Placed(old);
        Assert.Equal(old.Take(7), merged.Take(7));
        Assert.Equal(added, merged.Skip(7).Take(7));
        Assert.Equal([LyricSourceId.LyricsOvh, LyricSourceId.Amll, LyricSourceId.Lyrimuse, LyricSourceId.Musixmatch], merged.TakeLast(4));
        Assert.Equal(LyricSourceSnapshot.RecommendedOrder, LyricSourceSnapshot.OrderFor(null));
        // An older source missing from a saved order still goes to the end.
        LyricSourceId[] beforeAmll = [LyricSourceId.Lrclib, LyricSourceId.AppleMusicCache, LyricSourceId.LyricsPlus, LyricSourceId.NetEase, LyricSourceId.QQMusic,
            LyricSourceId.Kugou, LyricSourceId.Kuwo, LyricSourceId.LyricsOvh];
        Assert.Equal([LyricSourceId.LyricsOvh, LyricSourceId.Lyrimuse, LyricSourceId.Musixmatch, LyricSourceId.Amll], Placed(beforeAmll).TakeLast(4));
        Assert.Equal(added, Placed(beforeAmll).Skip(7).Take(7));
    }
}
