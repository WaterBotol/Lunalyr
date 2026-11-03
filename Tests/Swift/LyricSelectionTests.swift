import XCTest
@testable import VersoCustom

final class LyricSelectionTests: XCTestCase {
    private let query = LyricQuery(title: "Our Song", artist: "Our Artist", album: nil, duration: 180)
    private func candidate(_ source: LyricSourceID, word: Bool, score: Double = 100, title: String = "Our Song", duration: Double = 180) -> LyricCandidate {
        var line = LyricsFetcher.makeLine(text: "Sing along", start: 1, end: 3)
        if word { line.wordTimed = true; line.words = [.init(text: "Sing", start: 1, end: 2), .init(text: "along", start: 2, end: 3)] }
        return .init(source: source, result: .synced([line], .init(precision: "fixture", source: source.title)), title: title, artist: "Our Artist", duration: duration, score: score)
    }
    func testLineOnlyPriorityDoesNotHideAvailableRealWords() {
        let options = LyricSourceOptions.Snapshot(enabled: [.lrclib, .netEase], mode: .priority, localFolder: "", localVersion: "")
        let values = [candidate(.lrclib, word: false, score: 999), candidate(.netEase, word: true, score: 1)]
        XCTAssertEqual(LyricsFetcher.choose(LyricsFetcher.ranked(values, query: query, options: options), options: options)?.source, .netEase)
    }
    func testExplicitStrictPriorityIsStillRespected() {
        let options = LyricSourceOptions.Snapshot(enabled: [.lrclib, .netEase], mode: .priority, preferWordTimedLRCLIB: false, preferWordTiming: false, localFolder: "", localVersion: "")
        XCTAssertEqual(LyricsFetcher.choose([candidate(.lrclib, word: false), candidate(.netEase, word: true)], options: options)?.source, .lrclib)
    }
    func testAccuracyModeUsesTimingRatherThanProviderScoreOrCatalogue() {
        let options = LyricSourceOptions.Snapshot(enabled: [.qqMusic, .netEase], mode: .smart, localFolder: "", localVersion: "")
        let values = [candidate(.qqMusic, word: false, score: 999), candidate(.netEase, word: true, score: 1)]
        XCTAssertEqual(LyricsFetcher.ranked(values, query: query, options: options).first?.source, .netEase)
    }
    func testWrongRecordingCannotWinWithPerfectWordTiming() {
        let options = LyricSourceOptions.Snapshot(enabled: [.lyricsPlus, .lrclib], mode: .smart, localFolder: "", localVersion: "")
        let values = [candidate(.lyricsPlus, word: true, title: "Unrelated Song", duration: 230), candidate(.lrclib, word: false)]
        XCTAssertEqual(LyricsFetcher.ranked(values, query: query, options: options).map(\.source), [.lrclib])
    }
    func testSourceOrderBreaksEqualQualityTies() {
        let options = LyricSourceOptions.Snapshot(enabled: [.netEase, .lyricsPlus], mode: .smart, localFolder: "", localVersion: "")
        let values = [candidate(.lyricsPlus, word: true, score: 999), candidate(.netEase, word: true, score: 1)]
        XCTAssertEqual(LyricsFetcher.ranked(values, query: query, options: options).first?.source, .netEase)
    }
    func testPunctuationDifferencesDoNotDiscardRealWordTiming() {
        guard case .synced(var lines, _) = candidate(.netEase, word: true).result else { return XCTFail() }
        lines[0].text = "Sing, along!"
        XCTAssertTrue(LyricsFetcher.hasWordTiming(.synced(lines, .init(precision: "", source: ""))))
        lines[0].words.removeLast()
        XCTAssertFalse(LyricsFetcher.validWordTiming(lines[0]))
    }
}

private final class ProviderFixtureProtocol: URLProtocol {
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        let body: String
        switch request.url!.host! {
        case "search.kuwo.cn": body = #"{"abslist":[{"MUSICRID":"MUSIC_123","SONGNAME":"Our Song","ARTIST":"Our Artist","DURATION":"180"}]}"#
        case "www.kuwo.cn": body = #"{"code":200,"data":{"lrclist":[{"time":"1.0","lineLyric":"Sing along"},{"time":"4.0","lineLyric":"Next line"}]}}"#
        default: body = #"{"lyrics":"Sing along\nNext line"}"#
        }
        let response = HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: "HTTP/1.1", headerFields: [:])!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: Data(body.utf8)); client?.urlProtocolDidFinishLoading(self)
    }
    override func stopLoading() {}
}
extension LyricSelectionTests {
    func testNewProvidersKeepTimingProvenanceHonest() async throws {
        let config = URLSessionConfiguration.ephemeral; config.protocolClasses = [ProviderFixtureProtocol.self]
        let session = URLSession(configuration: config); defer { session.invalidateAndCancel() }
        let kuwo = try await AdditionalLyricsProviders.kuwo(query, session: session)
        XCTAssertEqual(kuwo?.source, .kuwo)
        XCTAssertEqual(kuwo?.lineCount, 2)
        XCTAssertEqual(kuwo.map { LyricsFetcher.timingTier($0.result) }, 1)
        let plain = try await AdditionalLyricsProviders.lyricsOVH(query, session: session)
        XCTAssertEqual(plain?.source, .lyricsOVH)
        XCTAssertEqual(plain.map { LyricsFetcher.timingTier($0.result) }, 0)
    }
}
