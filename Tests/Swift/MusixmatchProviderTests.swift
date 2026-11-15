import XCTest
@testable import VersoCustom

/// Answers Musixmatch requests from fixtures, recording what was asked. No network.
private final class MusixmatchFixtureProtocol: URLProtocol {
    static var lock = NSLock()
    static var replies: [String: [(status: Int, body: String)]] = [:]
    static var asked: [URLRequest] = []
    static func reset(_ replies: [String: [(status: Int, body: String)]]) {
        lock.withLock { self.replies = replies; asked = [] }
    }
    static var actions: [String] { lock.withLock { asked.compactMap { $0.url?.lastPathComponent } } }

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        let action = request.url?.lastPathComponent ?? ""
        let reply: (status: Int, body: String) = Self.lock.withLock {
            Self.asked.append(request)
            var queue = Self.replies[action] ?? []
            let next = queue.isEmpty ? (200, #"{"message":{"header":{"status_code":404},"body":[]}}"#) : queue.removeFirst()
            Self.replies[action] = queue
            return next
        }
        let response = HTTPURLResponse(url: request.url!, statusCode: reply.status, httpVersion: "HTTP/1.1", headerFields: [:])!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: Data(reply.body.utf8))
        client?.urlProtocolDidFinishLoading(self)
    }
    override func stopLoading() {}
}

final class MusixmatchProviderTests: XCTestCase {
    private let query = LyricQuery(title: "Our Song", artist: "Our Artist", album: nil, duration: 200)
    private var session: URLSession!
    private var folder: URL!

    override func setUp() {
        super.setUp()
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [MusixmatchFixtureProtocol.self]
        session = URLSession(configuration: config)
        folder = FileManager.default.temporaryDirectory.appendingPathComponent("MusixmatchTests-\(UUID().uuidString)", isDirectory: true)
    }
    override func tearDown() {
        session.invalidateAndCancel()
        try? FileManager.default.removeItem(at: folder)
        super.tearDown()
    }

    // MARK: Fixtures

    private static let token = #"{"message":{"header":{"status_code":200},"body":{"user_token":"2511abcdef0123456789"}}}"#
    private static let captcha = #"{"message":{"header":{"status_code":401,"hint":"captcha"},"body":""}}"#
    private static let renew = #"{"message":{"header":{"status_code":401,"hint":"renew"},"body":""}}"#
    private static let subtitle = "[00:01.00] Sing along\n[00:04.00] Next line\n[00:08.00] "
    /// Richsync as Musixmatch sends it: a JSON string, spaces as their own pieces.
    private static let richsync = #"[{"ts":1.0,"te":3.5,"l":[{"c":"Sing","o":0},{"c":" ","o":0.4},{"c":"along","o":0.6}],"x":"Sing along"},{"ts":4.0,"te":6.0,"l":[{"c":"Next","o":0},{"c":" ","o":0.5},{"c":"line","o":1.0}],"x":"Next line"}]"#

    private static func macro(subtitle: String? = MusixmatchProviderTests.subtitle, richsync: String? = MusixmatchProviderTests.richsync,
                              restricted: Int = 0, matcher: Int = 200) -> String {
        func quoted(_ s: String) -> String { String(data: try! JSONSerialization.data(withJSONObject: [s]), encoding: .utf8)!.dropFirst().dropLast().description }
        let track = #"{"track":{"track_id":123,"track_name":"Our Song","artist_name":"Our Artist","track_length":0,"has_richsync":1}}"#
        let subs = subtitle.map { #"{"subtitle_list":[{"subtitle":{"subtitle_body":\#(quoted($0)),"subtitle_length":200,"restricted":\#(restricted)}}]}"# } ?? "[]"
        let rich = richsync.map { #"{"richsync":{"richsync_body":\#(quoted($0))}}"# } ?? "[]"
        return #"{"message":{"header":{"status_code":200},"body":{"macro_calls":{"#
            + #""matcher.track.get":{"message":{"header":{"status_code":\#(matcher)},"body":\#(matcher == 200 ? track : "[]")}},"#
            + #""track.subtitles.get":{"message":{"header":{"status_code":\#(subtitle == nil ? 404 : 200)},"body":\#(subs)}},"#
            + #""track.richsync.get":{"message":{"header":{"status_code":\#(richsync == nil ? 404 : 200)},"body":\#(rich)}}"#
            + "}}}}"
    }
    private func json(_ text: String) -> [String: Any] {
        (try? JSONSerialization.jsonObject(with: Data(text.utf8))) as? [String: Any] ?? [:]
    }
    private func state(clock: @escaping () -> Date = Date.init) -> MusixmatchState {
        MusixmatchState(file: folder.appendingPathComponent("session.json"), clock: clock)
    }

    // MARK: Parsing

    func testRichsyncBecomesWordTimedLines() throws {
        let lrc = try XCTUnwrap(MusixmatchLyricsProvider.enhancedLRC(richsync: Self.richsync))
        XCTAssertEqual(lrc, "[00:01.000]<00:01.000>Sing <00:01.600>along<00:03.500>\n[00:04.000]<00:04.000>Next <00:05.000>line<00:06.000>")
        let match = try XCTUnwrap(MusixmatchLyricsProvider.match(from: json(Self.macro())))
        XCTAssertEqual(match.duration, 200)
        let candidate = try XCTUnwrap(MusixmatchLyricsProvider.candidate(from: match, query: query))
        XCTAssertEqual(candidate.source, .musixmatch)
        guard case .synced(let lines, _) = candidate.result else { return XCTFail("not synced") }
        XCTAssertEqual(lines.map(\.text), ["Sing along", "Next line"])
        XCTAssertTrue(lines.allSatisfy(\.wordTimed))
        XCTAssertEqual(lines[0].words.map(\.text).map { $0.trimmingCharacters(in: .whitespaces) }, ["Sing", "along"])
        XCTAssertEqual(lines[0].words[1].start, 1.6, accuracy: 0.001)
        XCTAssertTrue(LyricsFetcher.hasWordTiming(candidate.result))
    }

    func testSubtitleAloneGivesLineTiming() throws {
        let match = try XCTUnwrap(MusixmatchLyricsProvider.match(from: json(Self.macro(richsync: nil))))
        XCTAssertNil(match.words)
        let candidate = try XCTUnwrap(MusixmatchLyricsProvider.candidate(from: match, query: query))
        guard case .synced(let lines, _) = candidate.result else { return XCTFail("not synced") }
        XCTAssertEqual(lines.filter { !$0.text.isEmpty }.map(\.text), ["Sing along", "Next line"])
        XCTAssertFalse(lines.contains(where: \.wordTimed))
    }

    func testRestrictedOrMissingLyricsAndLineLevelRichsyncAreNotUsed() throws {
        let restricted = try XCTUnwrap(MusixmatchLyricsProvider.match(from: json(Self.macro(richsync: nil, restricted: 1))))
        XCTAssertNil(restricted.subtitle)
        XCTAssertNil(MusixmatchLyricsProvider.candidate(from: restricted, query: query))
        XCTAssertNil(MusixmatchLyricsProvider.match(from: json(Self.macro(matcher: 404))))
        let wholeLines = (1...6).map { #"{"ts":\#($0 * 4),"te":\#($0 * 4 + 3),"l":[{"c":"Whole line \#($0)","o":0}]}"# }
        XCTAssertNil(MusixmatchLyricsProvider.enhancedLRC(richsync: "[" + wholeLines.joined(separator: ",") + "]"))
        XCTAssertNil(MusixmatchLyricsProvider.enhancedLRC(richsync: "not json"))
    }

    func testVerdictsAndTokens() {
        typealias P = MusixmatchLyricsProvider
        XCTAssertEqual(P.verdict(httpStatus: 200, json: json(Self.captcha)), .blocked)
        XCTAssertEqual(P.verdict(httpStatus: 200, json: json(Self.renew)), .badToken)
        XCTAssertEqual(P.verdict(httpStatus: 429, json: nil), .blocked)
        XCTAssertEqual(P.verdict(httpStatus: 200, json: json(#"{"message":{"header":{"status_code":404}}}"#)), .missing)
        XCTAssertEqual(P.token(from: json(Self.token)), "2511abcdef0123456789")
        XCTAssertNil(P.token(from: json(#"{"message":{"header":{"status_code":200},"body":{"user_token":"UpgradeOnlyUpgradeOnly"}}}"#)))
        XCTAssertNil(P.token(from: json(#"{"message":{"header":{"status_code":200},"body":{"user_token":"0000000000"}}}"#)))
        XCTAssertNil(P.token(from: json(Self.captcha)))
    }

    // MARK: Lookups against fixtures

    func testLookupGetsTokenOnceAndAsksOncePerSong() async throws {
        MusixmatchFixtureProtocol.reset(["token.get": [(200, Self.token)], "macro.subtitles.get": [(200, Self.macro())]])
        let state = state()
        let found = try await MusixmatchLyricsProvider.lookup(query, session: session, state: state)
        XCTAssertTrue(found.map { LyricsFetcher.hasWordTiming($0.result) } ?? false)
        _ = try await MusixmatchLyricsProvider.lookup(query, session: session, state: state)
        XCTAssertEqual(MusixmatchFixtureProtocol.actions, ["token.get", "macro.subtitles.get"])
        let asked = try XCTUnwrap(MusixmatchFixtureProtocol.lock.withLock { MusixmatchFixtureProtocol.asked.last })
        let items = URLComponents(url: asked.url!, resolvingAgainstBaseURL: false)?.queryItems ?? []
        func value(_ name: String) -> String? { items.first { $0.name == name }?.value }
        XCTAssertEqual(asked.url?.host, "apic-appmobile.musixmatch.com")
        XCTAssertEqual(asked.value(forHTTPHeaderField: "User-Agent"), "Mozilla/5.0")
        XCTAssertEqual(value("app_id"), "mac-ios-v2.0")
        XCTAssertEqual(value("usertoken"), "2511abcdef0123456789")
        XCTAssertEqual(value("q_track"), "Our Song")
        XCTAssertEqual(value("q_artist"), "Our Artist")
        XCTAssertEqual(value("q_duration"), "200")
        XCTAssertEqual(value("optional_calls"), "track.richsync")

        // The token is kept on disk with its time: a new state (a relaunch) reuses it.
        let reloaded = MusixmatchState(file: folder.appendingPathComponent("session.json"))
        XCTAssertEqual(reloaded.freshToken(), "2511abcdef0123456789")
        XCTAssertNotNil(reloaded.snapshot.tokenFetchedAt)
    }

    func testTokenExpiresAfterADay() {
        var now = Date(timeIntervalSince1970: 1_000_000)
        let state = state(clock: { now })
        state.store(token: "abc123")
        now += 23 * 3600
        XCTAssertEqual(state.freshToken(), "abc123")
        now += 2 * 3600
        XCTAssertNil(state.freshToken())
    }

    func testRejectedTokenIsRenewedOnce() async throws {
        MusixmatchFixtureProtocol.reset([
            "token.get": [(200, Self.token), (200, Self.token)],
            "macro.subtitles.get": [(200, Self.renew), (200, Self.macro(richsync: nil))],
        ])
        let state = state()
        state.store(token: "stale-token-1")
        let found = try await MusixmatchLyricsProvider.lookup(query, session: session, state: state)
        XCTAssertNotNil(found)
        XCTAssertEqual(MusixmatchFixtureProtocol.actions, ["macro.subtitles.get", "token.get", "macro.subtitles.get"])
        XCTAssertEqual(state.freshToken(), "2511abcdef0123456789")
    }

    func testCaptchaPausesTheSourceWithDoublingBackoff() async throws {
        var now = Date(timeIntervalSince1970: 2_000_000)
        let state = state(clock: { now })
        MusixmatchFixtureProtocol.reset(["token.get": [(200, Self.captcha)]])
        let first = try await MusixmatchLyricsProvider.lookup(query, session: session, state: state)
        XCTAssertNil(first)
        XCTAssertEqual(state.blockedFor() ?? 0, 600, accuracy: 1)

        // While paused, nothing is asked.
        var other = query; other.title = "Another Song"
        _ = try await MusixmatchLyricsProvider.lookup(other, session: session, state: state)
        XCTAssertEqual(MusixmatchFixtureProtocol.actions, ["token.get"])

        // After the pause, a 429 on the lookup doubles it.
        now += 601
        MusixmatchFixtureProtocol.reset(["token.get": [(200, Self.token)], "macro.subtitles.get": [(429, "")]])
        _ = try await MusixmatchLyricsProvider.lookup(other, session: session, state: state)
        XCTAssertEqual(state.blockedFor() ?? 0, 1200, accuracy: 1)
        for _ in 0..<6 { state.noteBlocked() }
        XCTAssertEqual(state.blockedFor() ?? 0, 7200, accuracy: 1, "at most two hours")

        // The pause survives a relaunch; a proper answer clears it.
        XCTAssertNotNil(MusixmatchState(file: folder.appendingPathComponent("session.json"), clock: { now }).blockedFor())
        now += 7201
        MusixmatchFixtureProtocol.reset(["macro.subtitles.get": [(200, Self.macro())]])
        let found = try await MusixmatchLyricsProvider.lookup(query, session: session, state: state)
        XCTAssertNotNil(found)
        XCTAssertEqual(state.snapshot.blocks, 0)
    }

    func testPlaceholderTokenPausesToo() async throws {
        MusixmatchFixtureProtocol.reset(["token.get": [(200, #"{"message":{"header":{"status_code":200},"body":{"user_token":"UpgradeOnlyUpgradeOnlyUpgradeOnly"}}}"#)]])
        let state = state()
        let found = try await MusixmatchLyricsProvider.lookup(query, session: session, state: state)
        XCTAssertNil(found)
        XCTAssertNil(state.freshToken())
        XCTAssertNotNil(state.blockedFor())
        var other = query; other.title = "Another Song"
        _ = try await MusixmatchLyricsProvider.lookup(other, session: session, state: state)
        XCTAssertEqual(MusixmatchFixtureProtocol.actions, ["token.get"])
    }

    // MARK: Source list

    @MainActor
    func testMusixmatchJoinsSavedOrderAfterLyrimuseSwitchedOn() {
        let saved = ["lrclib", "netEase", "lyrimuse", "lyricsPlus", "appleMusicCache", "qqMusic", "kugou", "kuwo", "lyricsOVH", "amll"]
        let order = LyricSourceOptions.order(saved: saved)
        XCTAssertEqual(order.firstIndex(of: .musixmatch), order.firstIndex(of: .lyrimuse)! + 1)
        XCTAssertEqual(order.filter { $0 != .musixmatch }.map(\.rawValue), saved)
        XCTAssertEqual(LyricSourceOptions.order(saved: nil), LyricSourceOptions.recommendedOrder)
        XCTAssertEqual(Array(LyricSourceOptions.recommendedOrder.prefix(3)), [.appleMusicCache, .lyrimuse, .musixmatch])
        XCTAssertEqual(Set(LyricSourceOptions.order(saved: saved)), Set(LyricSourceID.allCases))

        // Someone who switched sources off before Musixmatch existed gets it on, once.
        var seen: Set<String> = ["custom.lyricSourcesSeenAMLL"]
        let first = LyricSourceOptions.enabled(saved: ["lrclib", "amll"], seen: { seen.contains($0) })
        XCTAssertEqual(first.enabled, [.lrclib, .amll, .musixmatch])
        XCTAssertEqual(first.newlySeen, ["custom.lyricSourcesSeenMusixmatch"])
        seen.formUnion(first.newlySeen)
        let later = LyricSourceOptions.enabled(saved: ["lrclib", "amll"], seen: { seen.contains($0) })
        XCTAssertEqual(later.enabled, [.lrclib, .amll], "switching it off again sticks")
        XCTAssertTrue(LyricSourceOptions.enabled(saved: nil, seen: { _ in false }).enabled.contains(.musixmatch))

        // Across launches against stored defaults: still on at the second launch,
        // and off for good once switched off.
        let suite = "MusixmatchMigration-\(UUID().uuidString)"
        let defaults = try! XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        defaults.set(["lrclib", "amll"], forKey: "custom.lyricSources")
        defaults.set(true, forKey: "custom.lyricSourcesSeenAMLL")
        XCTAssertTrue(LyricSourceOptions.loadEnabled(defaults).contains(.musixmatch), "first launch")
        XCTAssertTrue(LyricSourceOptions.loadEnabled(defaults).contains(.musixmatch), "second launch")
        defaults.set(["lrclib", "amll"], forKey: "custom.lyricSources") // the user switches it off
        XCTAssertFalse(LyricSourceOptions.loadEnabled(defaults).contains(.musixmatch), "stays off")
        let fresh = "MusixmatchMigration-\(UUID().uuidString)"
        let newUser = try! XCTUnwrap(UserDefaults(suiteName: fresh))
        defer { newUser.removePersistentDomain(forName: fresh) }
        XCTAssertTrue(LyricSourceOptions.loadEnabled(newUser).contains(.musixmatch))
        XCTAssertNil(newUser.stringArray(forKey: "custom.lyricSources"), "a never-saved choice stays unsaved")
        XCTAssertEqual(LyricSourceID.musixmatch.timingCeiling, 3)
        XCTAssertEqual(LyricSourceID.musixmatch.title, "Musixmatch")
    }
}
