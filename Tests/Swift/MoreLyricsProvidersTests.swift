import XCTest
@testable import VersoCustom

/// Fixture parsing for the sources in MoreLyricsProviders (no network) and
/// the migration that switches newly added sources on. Lyric text is made up.
final class MoreLyricsProvidersTests: XCTestCase {
    private let query = LyricQuery(title: "Our Song", artist: "Our Artist", album: nil, duration: 180)

    private func tier(_ source: LyricSourceID, _ lrc: String?, file: StaticString = #filePath, line: UInt = #line) -> Int? {
        guard let lrc else { XCTFail("no LRC", file: file, line: line); return nil }
        guard let candidate = AdditionalLyricsProviders.candidate(source, lrc: lrc, query: query, title: "Our Song", artist: "Our Artist", duration: 180) else {
            XCTFail("no candidate", file: file, line: line); return nil
        }
        return LyricsFetcher.timingTier(candidate.result)
    }
    private func lines(_ lrc: String?) -> [LyricsFetcher.Line] { LyricsFetcher.parseLRC(lrc ?? "", trackDuration: 180) }
    private func object(_ json: String) -> [String: Any] {
        (try? JSONSerialization.jsonObject(with: Data(json.utf8)) as? [String: Any]) ?? [:]
    }

    // MARK: Shared

    func testEnhancedLineIsWordTimedLRC() {
        let row = MoreLyricsProviders.enhancedLine([.init(start: 1, end: 1.5, text: "Sing "), .init(start: 1.5, end: 2.25, text: "along")])
        XCTAssertEqual(row, "[00:01.000]<00:01.000>Sing <00:01.500>along<00:02.250>")
        XCTAssertNil(MoreLyricsProviders.enhancedLine([.init(start: 1, end: 2, text: " ")]))
        XCTAssertEqual(MoreLyricsProviders.clockSeconds("4:13"), 253)
        XCTAssertNil(MoreLyricsProviders.clockSeconds("00:00"))
    }

    // MARK: Soda Music

    func testSodaSearchAndKRCShapedLyricBecomeWordTiming() {
        let search = object(#"{"result_groups":[{"id":"artists","data":[]},{"id":"tracks","data":[{"entity":{"track":{"id":"7001","name":"Our Song","duration":180500,"artists":[{"name":"Our Artist"}]}}}]}]}"#)
        let tracks = MoreLyricsProviders.sodaTracks(search)
        XCTAssertEqual(tracks.map(\.id), ["7001"])
        XCTAssertEqual(tracks.first?.duration ?? 0, 180.5, accuracy: 0.001)
        let page = object(#"{"lyric":{"content":"[1000,1600]<0,400,0>Sing <400,600,0>along <1000,300,0>with <1300,300,0>me\n[4000,1500]<0,500,0>Next <500,500,0>line <1000,500,0>here\n[6000,1500]<0,500,0>Last <500,500,0>words <1000,500,0>now"},"seo_track":{"track":{"id":"7001"}}}"#)
        let lrc = MoreLyricsProviders.sodaLyrics(page)
        XCTAssertEqual(lines(lrc).first?.text, "Sing along with me")
        XCTAssertEqual(lines(lrc).first?.start ?? 0, 1, accuracy: 0.001)
        XCTAssertEqual(tier(.sodaMusic, lrc), 3)
    }

    func testSodaLineLyricIsKeptAndEmptyLyricIsNone() {
        let page = object(#"{"lyric":{"content":"[00:01.00]Sing along\n[00:04.00]Next line"},"seo_track":{"track":{"id":"7001"}}}"#)
        XCTAssertEqual(tier(.sodaMusic, MoreLyricsProviders.sodaLyrics(page)), 1)
        XCTAssertNil(MoreLyricsProviders.sodaLyrics(object(#"{"lyric":{"content":""},"seo_track":{"track":{"id":"7001"},"lyric":{"content":""}}}"#)))
    }

    // MARK: PetitLyrics

    func testPetitAnswerAndCharacterTimedLyric() {
        let wsy = """
        <wsy><line><linestring>Sing it now</linestring>        <word><starttime>1000</starttime><endtime>1200</endtime><wordstring>S</wordstring></word>        <word><starttime>1200</starttime><endtime>1400</endtime><wordstring>i</wordstring></word>        <word><starttime>1400</starttime><endtime>1500</endtime><wordstring>ng</wordstring></word>        <word><starttime>1500</starttime><endtime>1600</endtime><wordstring> </wordstring></word>        <word><starttime>1600</starttime><endtime>2000</endtime><wordstring>it</wordstring></word>        <word><starttime>2000</starttime><endtime>2100</endtime><wordstring> </wordstring></word>        <word><starttime>2100</starttime><endtime>2600</endtime><wordstring>now</wordstring></word></line>        <line><linestring>Then again</linestring>        <word><starttime>3000</starttime><endtime>3500</endtime><wordstring>Then</wordstring></word>        <word><starttime>3500</starttime><endtime>3600</endtime><wordstring> </wordstring></word>        <word><starttime>3400</starttime><endtime>4200</endtime><wordstring>again</wordstring></word></line>        <line><linestring>We&#039;re done</linestring>        <word><starttime>5000</starttime><endtime>5300</endtime><wordstring>We</wordstring></word>        <word><starttime>5300</starttime><endtime>5400</endtime><wordstring>&#039;</wordstring></word>        <word><starttime>5400</starttime><endtime>5600</endtime><wordstring>re</wordstring></word>        <word><starttime>5600</starttime><endtime>5700</endtime><wordstring> </wordstring></word>        <word><starttime>5700</starttime><endtime>6400</endtime><wordstring>done</wordstring></word></line></wsy>
        """
        let encoded = Data(wsy.utf8).base64EncodedString()
        let answer = "<?xml version='1.0' encoding='UTF-8' ?><response><status>00000000</status><songs><song><lyricsId>11</lyricsId>"
            + "<title>Our Song</title><artist>Our Artist</artist><duration>180200</duration><lyricsType>3</lyricsType><isOfficial>1</isOfficial>"
            + "<lyricsData>\(encoded)</lyricsData></song><song><lyricsId>12</lyricsId><title>Our Song</title><lyricsType>1</lyricsType></song></songs></response>"
        let parsed = MoreLyricsProviders.petitSongs(Data(answer.utf8))
        XCTAssertEqual(parsed.status, "00000000")
        XCTAssertEqual(parsed.songs.map { $0["lyricsId"] }, ["11", "12"])
        XCTAssertEqual(parsed.songs.first?["isOfficial"], "1")
        let lrc = MoreLyricsProviders.petitEnhancedLRC(parsed.songs.first?["lyricsData"] ?? "")
        let rows = lines(lrc)
        XCTAssertEqual(rows.map(\.text), ["Sing it now", "Then again", "We're done"])
        // Letters timed one by one become one word; a timed space joins the word before it.
        XCTAssertEqual(rows.first?.words.map(\.text), ["Sing", "it", "now"])
        XCTAssertEqual(rows.last?.words.map(\.text), ["We're", "done"])
        // A word running into the next is cut at the next one's start.
        XCTAssertEqual(rows[1].words.first?.end ?? 0, 3.4, accuracy: 0.001)
        XCTAssertEqual(tier(.petitLyrics, lrc), 3)
    }

    func testPetitRejectedClientStatusIsRead() {
        let parsed = MoreLyricsProviders.petitSongs(Data("<response><status>8170f003</status></response>".utf8))
        XCTAssertEqual(parsed.status, "8170f003")
        XCTAssertTrue(parsed.songs.isEmpty)
        XCTAssertNil(MoreLyricsProviders.petitEnhancedLRC("not base64 xml"))
    }

    // MARK: Deezer

    private func deezerLyrics(wordShift: Int) -> [String: Any] {
        func words(_ start: Int, _ pieces: [String]) -> String {
            pieces.enumerated().map { #"{"start":\#(start + wordShift + $0.offset * 400),"end":\#(start + wordShift + $0.offset * 400 + 350),"word":"\#($0.element)"}"# }.joined(separator: ",")
        }
        return object(#"""
        {"data":{"track":{"id":"1","lyrics":{"synchronizedLines":[
          {"lrcTimestamp":"[00:01.00]","milliseconds":1000,"line":"Sing along with me"},
          {"lrcTimestamp":"[00:04.00]","milliseconds":4000,"line":"Next line here"},
          {"lrcTimestamp":"[00:06.00]","milliseconds":6000,"line":""},
          {"lrcTimestamp":"[00:07.00]","milliseconds":7000,"line":"Last words now"}],
         "synchronizedWordByWordLines":[
          {"start":1000,"end":2600,"words":[\#(words(1000, ["Sing", "along", "with", "me"]))]},
          {"start":4000,"end":5200,"words":[\#(words(4000, ["Next", "line", "here"]))]},
          {"start":7000,"end":8200,"words":[\#(words(7000, ["Last", "words", "now"]))]}]}}}}
        """#)
    }

    func testDeezerSearchAndAgreeingWordTrack() {
        let search = object(#"{"data":{"search":{"results":{"tracks":{"edges":[{"node":{"id":"42","title":"Our Song","duration":181,"hasSynchronizedLyrics":true,"contributors":{"edges":[{"node":{"name":"Our Artist"}}]},"album":{"displayTitle":"Ours"}}},{"node":{"id":"43","title":"Our Song (Cover)","duration":200,"hasSynchronizedLyrics":false,"contributors":{"edges":[]}}}]}}}}}"#)
        let tracks = MoreLyricsProviders.deezerTracks(search)
        XCTAssertEqual(tracks.map(\.id), ["42", "43"])
        XCTAssertEqual(tracks.map(\.synced), [true, false])
        XCTAssertEqual(tracks.first?.artist, "Our Artist")
        let lrc = MoreLyricsProviders.deezerLRC(deezerLyrics(wordShift: 0))
        XCTAssertEqual(lines(lrc).first?.words.map(\.text), ["Sing", "along", "with", "me"])
        XCTAssertEqual(tier(.deezer, lrc), 3)
    }

    func testDeezerShiftedWordTrackFallsBackToLines() {
        let lrc = MoreLyricsProviders.deezerLRC(deezerLyrics(wordShift: 9000))
        XCTAssertEqual(lines(lrc).map(\.text), ["Sing along with me", "Next line here", "Last words now"])
        XCTAssertEqual(tier(.deezer, lrc), 1)
        XCTAssertNil(MoreLyricsProviders.deezerLRC(object(#"{"data":{"track":null},"errors":[{"type":"LyricsNotFoundError"}]}"#)))
    }

    func testDeezerLanguageAndTokenExpiry() {
        XCTAssertEqual(MoreLyricsProviders.deezerLanguage("YOASOBI 夜に駆ける"), "ja-JP")
        XCTAssertEqual(MoreLyricsProviders.deezerLanguage("아이유 밤편지"), "ko-KR")
        XCTAssertEqual(MoreLyricsProviders.deezerLanguage("周杰伦 稻香"), "zh-CN")
        XCTAssertEqual(MoreLyricsProviders.deezerLanguage("Our Artist Our Song"), "en-US")
        let claims = Data(#"{"exp":1800000000}"#.utf8).base64EncodedString().replacingOccurrences(of: "=", with: "")
        XCTAssertEqual(DeezerToken.expiry("head.\(claims).sig"), Date(timeIntervalSince1970: 1_800_000_000))
        XCTAssertNil(DeezerToken.expiry("garbage"))
    }

    // MARK: Migu

    /// Encrypted with an independent XXTEA implementation from made-up text.
    private let miguMRC = "df4af43148739d779022526ad4b15b57cc0c581ceee4238425e2b06f8ff979bfdbd288b4ad37aba9f4007854d2643c4fb343a06ae496923a8b8f161563b0b88cf3b83483bfa2f8132278f05109fa3db36919c8f68e551cd0b6793aacb40a44a46d73ea69e0c086d17f73525f7c9ccff271cf8b9e9d6153f97821886fb61aeb33c37c88770fa71e6d14db60793219f4a1590cc909d2172fe6d9a69533f0f99174105bf417134c6e987096c3eb06b34de7b83d309c0cc45a7f02fa60de31f168beea7529faeae612f4f9dc907123ec9c51ba2e7594e9693a663176bd2271545076366258e01fe2b7fa5d6afcde83cb73fff3b76cee1a5158806f80c6bea4aa75b5a26b456341b7d4f96b5bf479cfbc13cbf892964e6765c11b21edfcbed83bbc1a2194d7eb8f803375ee2e5979363b0a29c812115d3c7dc7db90598072382d7b12b680b388d816bebb22c42c4fa3b9eb7601e2ff0105c77a50d0ca027dc6083dd92a82ddca734b7c54c9e06ec5af2d74f2237d0fdad1eb606af1fced239edceeba90a1385ad206ae81e8a61af0b4a8524a636b1a71befe1525007042b119e0ecd3df5980f0c83325c227228e2a57c6aae470c7aa6d0e66f9b1"

    func testMiguMRCDecryptsToWordTiming() {
        let text = MoreLyricsProviders.miguDecrypt(miguMRC)
        XCTAssertTrue(text?.hasPrefix("[ti:Our Song]\n[0,0]Our(0,0)") == true)
        let lrc = MoreLyricsProviders.miguEnhancedLRC(text ?? "")
        let rows = lines(lrc)
        XCTAssertEqual(rows.map(\.text), ["Sing along with me", "Next line here", "Last words now"])
        XCTAssertEqual(rows.first?.words.map(\.start), [1, 1.4, 2, 2.3])
        XCTAssertEqual(tier(.migu, lrc), 3)
        XCTAssertNil(MoreLyricsProviders.miguDecrypt("abc"))
    }

    func testMiguSearchLengthAndLRCHeader() {
        let search = object(#"{"code":"000000","songResultData":{"result":[{"copyrightId":"600","name":"Our Song","singers":[{"name":"Our Artist"}],"lyricUrl":"https://example.invalid/l","mrcurl":"https://example.invalid/m","newRateFormats":[{"format":"020007","size":"2880000","fileType":"mp3"}]},{"copyrightId":"601","name":"No Lyric","lyricUrl":""}]}}"#)
        let items = MoreLyricsProviders.miguItems(search)
        XCTAssertEqual(items.map(\.id), ["600"])
        XCTAssertEqual(items.first?.duration ?? 0, 180, accuracy: 0.001)
        let lrc = MoreLyricsProviders.miguStripHeader("[00:01.00]歌曲名 Our Song\n[00:02.00]歌手名 Our Artist\n[00:10.00]Sing along\n[00:14.00]Next line")
        XCTAssertEqual(lines(lrc).map(\.text), ["Sing along", "Next line"])
        XCTAssertEqual(tier(.migu, lrc), 1)
    }

    // MARK: VIBE

    func testVibeLyricKeepsTheOriginalTrack() {
        let answer = object(#"{"response":{"result":{"lyric":{"hasSyncLyric":true,"syncLyric":{"startTimeIndex":[1.0,4.5,8.0],"contents":[{"languageType":"default","text":["Sing along","","Next line"]},{"languageType":"ko","text":["번역","","다음"]}]}}}}}"#)
        let lrc = MoreLyricsProviders.vibeLRC(answer)
        XCTAssertEqual(lines(lrc).filter { !$0.text.isEmpty }.map(\.text), ["Sing along", "Next line"])
        XCTAssertEqual(tier(.vibe, lrc), 1)
        XCTAssertNil(MoreLyricsProviders.vibeLRC(object(#"{"response":{"result":{"lyric":{"hasSyncLyric":false,"normalLyric":{"text":"Sing"}}}}}"#)))
    }

    func testKoreanReadingAndQueryCleanup() {
        XCTAssertEqual(MoreLyricsProviders.koreanReadingRemoved("晴天(청천)"), "晴天")
        XCTAssertEqual(MoreLyricsProviders.koreanReadingRemoved("밤편지 (Through the Night)"), "밤편지 (Through the Night)")
        XCTAssertEqual(MoreLyricsProviders.koreanReadingRemoved("Song (라이브)"), "Song (라이브)")
        XCTAssertEqual(MoreLyricsProviders.vibeQueryText("I Don't Love You (feat. Someone) A & B"), "I Dont Love You A B")
    }

    // MARK: Genie

    func testGenieJSONPDropsTheTitleArtistHeader() {
        let body = #"GenieCallback({"0":"Our Song","500":"Our Artist","1000":"Sing along","4000":"Next line","9000":""});"#
        let lrc = MoreLyricsProviders.genieLRC(body, title: "Our Song", artist: "Our Artist")
        XCTAssertEqual(lines(lrc).filter { !$0.text.isEmpty }.map(\.text), ["Sing along", "Next line"])
        XCTAssertEqual(tier(.genie, lrc), 1)
        // A first line that only repeats the title may be sung: kept.
        let sung = MoreLyricsProviders.genieLRC(#"GenieCallback({"1000":"Our Song","4000":"Next line"});"#, title: "Our Song", artist: "Our Artist")
        XCTAssertEqual(lines(sung).first?.text, "Our Song")
        XCTAssertNil(MoreLyricsProviders.genieLRC("NOT FOUND LYRICS", title: "Our Song", artist: "Our Artist"))
    }

    // MARK: LyricFind (YouTube Music)

    func testYouTubeMusicSearchPageAndLyricFindCredit() {
        let search = object(#"""
        {"contents":{"tabs":[{"musicShelfRenderer":{"contents":[{"musicResponsiveListItemRenderer":{
          "flexColumns":[{"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"Our Song"}]}}},
                         {"musicResponsiveListItemFlexColumnRenderer":{"text":{"runs":[{"text":"Our Artist"},{"text":" • "},{"text":"Ours"},{"text":" • "},{"text":"3:00"}]}}}],
          "overlay":{"musicItemThumbnailOverlayRenderer":{"content":{"musicPlayButtonRenderer":{"playNavigationEndpoint":{"watchEndpoint":{"videoId":"vid1"}}}}}}}}]}}]}}
        """#)
        let songs = MoreLyricsProviders.youTubeSongs(search)
        XCTAssertEqual(songs.map(\.videoID), ["vid1"])
        XCTAssertEqual(songs.first?.artist, "Our Artist")
        XCTAssertEqual(songs.first?.duration, 180)
        let next = object(#"{"tabs":[{"endpoint":{"browseEndpoint":{"browseId":"MPLYt_x","browseEndpointContextSupportedConfigs":{"browseEndpointContextMusicConfig":{"pageType":"MUSIC_PAGE_TYPE_TRACK_LYRICS"}}}}}]}"#)
        XCTAssertEqual(MoreLyricsProviders.youTubeLyricPage(next), "MPLYt_x")
        func browse(_ source: String) -> [String: Any] {
            object(#"{"contents":{"elementRenderer":{"timedLyricsModel":{"lyricsData":{"timedLyricsData":[{"lyricLine":"Sing along","cueRange":{"startTimeMilliseconds":"1000","endTimeMilliseconds":"3000"}},{"lyricLine":"Next line","cueRange":{"startTimeMilliseconds":"4000","endTimeMilliseconds":"6000"}}],"sourceMessage":"Source: \#(source)"}}}}}"#)
        }
        let lrc = MoreLyricsProviders.lyricFindLRC(browse("LyricFind"))
        XCTAssertEqual(lines(lrc).map(\.text), ["Sing along", "Next line"])
        XCTAssertEqual(tier(.lyricFind, lrc), 1)
        // Musixmatch's lyrics relayed by YouTube Music are not LyricFind's.
        XCTAssertNil(MoreLyricsProviders.lyricFindLRC(browse("Musixmatch")))
    }

    // MARK: Whole lookups through a stubbed session

    func testStubbedLookupsReturnTheirSources() async throws {
        let config = URLSessionConfiguration.ephemeral; config.protocolClasses = [MoreProvidersFixtureProtocol.self]
        let session = URLSession(configuration: config); defer { session.invalidateAndCancel() }
        let genie = try await MoreLyricsProviders.genie(query, session: session)
        XCTAssertEqual(genie?.source, .genie)
        XCTAssertEqual(genie.map { LyricsFetcher.timingTier($0.result) }, 1)
        await DeezerToken.shared.clear()
        let deezer = try await MoreLyricsProviders.deezer(query, session: session)
        XCTAssertEqual(deezer?.source, .deezer)
        XCTAssertEqual(deezer.map { LyricsFetcher.timingTier($0.result) }, 3)
        await DeezerToken.shared.clear()
    }

    // MARK: Source list

    func testNewSourcesAreWiredAndOrdered() {
        let order = LyricSourceOptions.recommendedOrder
        for source in LyricSourceOptions.addedLater {
            XCTAssertTrue(order.contains(source))
            XCTAssertGreaterThan(order.firstIndex(of: source)!, order.firstIndex(of: .kuwo)!)
            XCTAssertLessThan(order.firstIndex(of: source)!, order.firstIndex(of: .lyricsOVH)!)
            XCTAssertFalse(source.timing.isEmpty)
            XCTAssertTrue(LyricsFetcher.originalTitleSources.contains(source))
        }
        XCTAssertEqual(Set(LyricSourceID.allCases.filter { $0.timingCeiling == 1 }), [.kuwo, .vibe, .genie, .lyricFind])
        XCTAssertTrue(ArtistAliases.catalogues.isSuperset(of: [.sodaMusic, .migu, .petitLyrics, .vibe, .genie]))
    }

    func testSavedOrderTakesNewSourcesAfterKuwo() {
        let old: [LyricSourceID] = [.lrclib, .appleMusicCache, .lyricsPlus, .netEase, .qqMusic, .kugou, .kuwo, .lyricsOVH, .amll, .lyrimuse]
        let merged = LyricSourceOptions.merged(old)
        XCTAssertEqual(Array(merged.prefix(7)), Array(old.prefix(7)))
        XCTAssertEqual(Array(merged[7..<14]), LyricSourceOptions.addedLater)
        XCTAssertEqual(Array(merged.suffix(3)), [.lyricsOVH, .amll, .lyrimuse])
        XCTAssertEqual(LyricSourceOptions.merged([]), LyricSourceOptions.recommendedOrder)
        // An older source missing from a saved order still goes to the end.
        let beforeAMLL: [LyricSourceID] = [.lrclib, .appleMusicCache, .lyricsPlus, .netEase, .qqMusic, .kugou, .kuwo, .lyricsOVH]
        XCTAssertEqual(LyricSourceOptions.merged(beforeAMLL).suffix(3), [.lyricsOVH, .lyrimuse, .amll])
        XCTAssertEqual(Array(LyricSourceOptions.merged(beforeAMLL)[7..<14]), LyricSourceOptions.addedLater)
    }

    func testStoredChoicesTakeNewSourcesOnceAndKeepThemOffWhenTurnedOff() throws {
        let name = "MoreLyricsProvidersTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: name)); defer { defaults.removePersistentDomain(forName: name) }
        // Someone who saved their sources before these existed.
        defaults.set(["lrclib", "netEase", "amll"], forKey: "custom.lyricSources")
        defaults.set(true, forKey: "custom.lyricSourcesSeenAMLL")
        let first = LyricSourceOptions.loadEnabled(defaults)
        XCTAssertTrue(first.isSuperset(of: LyricSourceOptions.addedLater))
        XCTAssertTrue(first.isSuperset(of: [.lrclib, .netEase, .amll]))
        // Written back: assigning `enabled` in init does not save it.
        XCTAssertEqual(Set(defaults.stringArray(forKey: "custom.lyricSources") ?? []), Set(first.map(\.rawValue)))
        XCTAssertEqual(LyricSourceOptions.loadEnabled(defaults), first)
        // Turned off later, it stays off.
        defaults.set(first.subtracting([.deezer]).map(\.rawValue), forKey: "custom.lyricSources")
        XCTAssertFalse(LyricSourceOptions.loadEnabled(defaults).contains(.deezer))

        // A fresh install has every online source on and stores nothing yet.
        let freshName = name + ".fresh"
        let fresh = try XCTUnwrap(UserDefaults(suiteName: freshName)); defer { fresh.removePersistentDomain(forName: freshName) }
        let enabled = LyricSourceOptions.loadEnabled(fresh)
        XCTAssertEqual(enabled, Set(LyricSourceID.allCases.filter { !$0.isLocal }))
        XCTAssertNil(fresh.stringArray(forKey: "custom.lyricSources"))
    }
}

private final class MoreProvidersFixtureProtocol: URLProtocol {
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        let url = request.url!
        let body: String
        switch (url.host ?? "", url.path) {
        case ("app.genie.co.kr", _):
            body = #"{"searchResult":{"result":{"songs":{"items":[{"song_id":5,"song_name":{"original":"Our Song"},"artist_name":{"original":"Our Artist"},"album_name":{"original":"Ours"},"duration":{"time":180},"service_available":{"lyrics_service_yn":"Y"}}]}}}}"#
        case ("dn.genie.co.kr", _):
            body = #"GenieCallback({"1000":"Sing along","4000":"Next line"});"#
        case ("auth.deezer.com", _):
            body = #"{"jwt":"head.eyJleHAiOjQxMDI0NDQ4MDB9.sig"}"#
        case ("pipe.deezer.com", _):
            let text = String(decoding: Self.body(of: request), as: UTF8.self)
            body = text.contains("SearchTracks")
                ? #"{"data":{"search":{"results":{"tracks":{"edges":[{"node":{"id":"42","title":"Our Song","duration":180,"hasSynchronizedLyrics":true,"contributors":{"edges":[{"node":{"name":"Our Artist"}}]}}}]}}}}}"#
                : #"{"data":{"track":{"lyrics":{"synchronizedLines":[{"milliseconds":1000,"line":"Sing along"},{"milliseconds":4000,"line":"Next line"}],"synchronizedWordByWordLines":[{"start":1000,"end":2000,"words":[{"start":1000,"end":1400,"word":"Sing"},{"start":1400,"end":2000,"word":"along"}]},{"start":4000,"end":5000,"words":[{"start":4000,"end":4400,"word":"Next"},{"start":4400,"end":5000,"word":"line"}]}]}}}}"#
        default:
            body = "{}"
        }
        let response = HTTPURLResponse(url: url, statusCode: 200, httpVersion: "HTTP/1.1", headerFields: [:])!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: Data(body.utf8)); client?.urlProtocolDidFinishLoading(self)
    }
    override func stopLoading() {}
    /// URLSession hands a protocol the body as a stream.
    static func body(of request: URLRequest) -> Data {
        if let data = request.httpBody { return data }
        guard let stream = request.httpBodyStream else { return Data() }
        stream.open(); defer { stream.close() }
        var data = Data(), buffer = [UInt8](repeating: 0, count: 4096)
        while stream.hasBytesAvailable {
            let read = stream.read(&buffer, maxLength: buffer.count)
            if read <= 0 { break }
            data.append(buffer, count: read)
        }
        return data
    }
}
