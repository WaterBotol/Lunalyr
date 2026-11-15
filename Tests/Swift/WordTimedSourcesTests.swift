import Compression
import XCTest
@testable import VersoCustom

/// QQ Music QRC and Kuwo lrcx word timing, from fixtures (no network).
final class WordTimedSourcesTests: XCTestCase {
    // MARK: - Fixture builders

    private func zlib(_ data: Data) -> Data {
        let raw = (try? (data as NSData).compressed(using: .zlib) as Data) ?? Data()
        return Data([0x78, 0x9C]) + raw
    }
    private func hex(_ bytes: [UInt8]) -> String { bytes.map { String(format: "%02X", $0) }.joined() }
    private func bytes(_ hex: String) -> [UInt8] {
        var out: [UInt8] = [], index = hex.startIndex
        while index < hex.endIndex {
            let next = hex.index(index, offsetBy: 2)
            out.append(UInt8(hex[index..<next], radix: 16)!)
            index = next
        }
        return out
    }
    /// QQ's encoding: zlib, zero-padded to whole blocks, triple-DES, hex.
    private func qrcCipher(_ xml: String) -> String {
        var plain = [UInt8](zlib(Data(xml.utf8)))
        while plain.count % 8 != 0 { plain.append(0) }
        return hex(QQMusicTripleDES.encrypt(plain)!)
    }
    private func qrcXML(_ content: String) -> String {
        """
        <?xml version="1.0" encoding="utf-8"?>
        <QrcInfos>
        <QrcHeadInfo SaveTime="269" Version="100"/>
        <LyricInfo LyricCount="1">
        <Lyric_1 LyricType="1" LyricContent="\(content)"/>
        </LyricInfo>
        </QrcInfos>
        """
    }
    private func download(_ field: String) -> String {
        "<!--\n<cmd value=\"1031\" verson=\"4\"><miniversion=\"1\" /><result>0</result><lyric musicid=\"7\"><content type=\"file\"><![CDATA[\(field)]]></content><contentts><![CDATA[]]></contentts></lyric></cmd>\n-->"
    }
    /// Kuwo's encoding: XOR "yeelion", base64, zlib, behind a `TP=content` header.
    private func lrcx(_ text: String) -> Data {
        let key = Array("yeelion".utf8)
        let xored = Data(Array(text.utf8).enumerated().map { $0.element ^ key[$0.offset % key.count] })
        return Data("TP=content\r\nlrcx=1\r\n\r\n".utf8) + zlib(Data(xored.base64EncodedString().utf8))
    }
    private func words(_ line: LyricsFetcher.Line) -> [String] { line.words.map { $0.text.trimmingCharacters(in: .whitespaces) } }

    /// QRC content: the title and first credit lines are from a real reply for 晴天 by 周杰伦 (lyric_download.fcg,
    /// header trimmed); the last line is made up to carry an unescaped quote. The real ciphertext sample is the
    /// three-block vector in `testTheTripleDESPortDecryptsRealQQCiphertext`.
    private let realQRCOpening = """
    [ti:晴天]
    [ar:周杰伦]
    [offset:0]
    [0,2250]晴(0,160)天(160,160) (320,160)-(480,160) (640,160)周(800,160)杰(960,160)伦(1120,160) (1280,160)((1440,160)Jay(1600,160) (1760,160)Chou(1920,160))(2080,160)
    [2250,2250]词(2250,450)：(2700,450)周(3150,450)杰(3600,450)伦(4050,450)
    [20000,3000]Hello (20000,500)"quoted" (20500,700)world(21200,800)
    """

    // MARK: - QQ Music QRC

    func testTheTripleDESPortDecryptsRealQQCiphertext() {
        // The first three blocks of the real 晴天 reply and what they decrypt to:
        // a zlib header (78 9C) and deflate data. Standard DES gives garbage here.
        let cipher = bytes("E5D94A70C91F702238E3DD4AEC7EC2A6247F320388EA1F8F")
        XCTAssertEqual(QQMusicTripleDES.decrypt(cipher).map(hex), "789C4D9A5BAF56D77586EFF91588ABF5495F9A35CF735A71")
        XCTAssertEqual(QQMusicTripleDES.encrypt(bytes("789C4D9A5BAF56D77586EFF91588ABF5495F9A35CF735A71")).map(hex),
                       "E5D94A70C91F702238E3DD4AEC7EC2A6247F320388EA1F8F")
    }

    func testQRCDecryptsAndKeepsTheWholeLyricContent() throws {
        let xml = try XCTUnwrap(QQMusicQRC.decrypt(hex: qrcCipher(qrcXML(realQRCOpening))))
        let content = try XCTUnwrap(QQMusicQRC.lyricContent(xml))
        // A literal, unescaped quote inside the lyric does not end it early.
        XCTAssertTrue(content.contains(#""quoted""#))
        XCTAssertTrue(content.hasSuffix("world(21200,800)"))
        XCTAssertNil(QQMusicQRC.decrypt(hex: "nothex"))
        XCTAssertNil(QQMusicQRC.decrypt(hex: String(repeating: "AB", count: 16)))
    }

    func testQRCBecomesWordTimedEnhancedLRC() throws {
        let lrc = try XCTUnwrap(QQMusicQRC.enhancedLRC(fromQRC: realQRCOpening))
        XCTAssertTrue(lrc.contains("[offset:0]"))
        // Each word comes before its stamp in QRC and after it in enhanced LRC; stamps are absolute.
        XCTAssertTrue(lrc.contains("[00:02.250]<00:02.250>词<00:02.700>："), lrc)
        let lines = LyricsFetcher.parseLRC(lrc, trackDuration: 60)
        let sung = try XCTUnwrap(lines.first { $0.text.contains("world") })
        XCTAssertTrue(sung.wordTimed)
        XCTAssertEqual(words(sung), ["Hello", #""quoted""#, "world"])
        XCTAssertEqual(sung.start, 20, accuracy: 0.01)
        XCTAssertEqual(sung.words[1].start, 20.5, accuracy: 0.01)
        XCTAssertEqual(sung.words[2].start, 21.2, accuracy: 0.01)
        // The last word runs to the line's end, [20000,3000], as with Kugou KRC.
        XCTAssertEqual(sung.words[2].end, 23.0, accuracy: 0.01)
        // Literal parentheses in the text stay text; only (start,length) pairs are stamps.
        XCTAssertTrue(lrc.contains("<00:01.440>(<00:01.600>Jay"), lrc)
    }

    func testALyricDownloadReplyGivesWordTimingOrLineLRC() throws {
        let qrc = try XCTUnwrap(QQMusicQRC.download(download(qrcCipher(qrcXML(realQRCOpening)))))
        XCTAssertNotNil(qrc.enhanced)
        XCTAssertNil(qrc.line)
        // A song without QRC is sent as line LRC through the same field.
        let lineOnly = try XCTUnwrap(QQMusicQRC.download(download(qrcCipher("[00:01.00]Sing along\n[00:04.00]Next line"))))
        XCTAssertNil(lineOnly.enhanced)
        XCTAssertEqual(lineOnly.line, "[00:01.00]Sing along\n[00:04.00]Next line")
        // Plain text (no lyric file) is not decryptable: nothing.
        XCTAssertNil(QQMusicQRC.download(download("Sing along")))
        XCTAssertNil(QQMusicQRC.download("<html>blocked</html>"))
    }

    // MARK: - Kuwo lrcx

    private let lrcxSample = """
    [kuwo:071]
    [ver:v1.0]
    [ti:Our Song]
    [offset:0]
    [00:19.205]<1183,-1183>I <2231,-541>never <3000,1000>stop
    [00:27.326]<0,0>我<0,0>从<0,0>未
    [00:27.326]<1113,-1113>Second <2000,-500>line
    """

    func testKuwoLrcxDecodes() throws {
        XCTAssertEqual(KuwoLrcx.decode(lrcx(lrcxSample)), lrcxSample)
        XCTAssertNil(KuwoLrcx.decode(Data("<html>blocked</html>".utf8)))
        XCTAssertNil(KuwoLrcx.decode(Data(#"{"lyrics":"Sing along"}"#.utf8)))
    }

    func testKuwoLrcxCoefficientsAreOctal() {
        // 071 octal = 57: k1 = 5, k2 = 7.
        XCTAssertEqual(KuwoLrcx.coefficients("[ver:v1.0]\n[kuwo:071]").map { [$0.k1, $0.k2] }, [5, 7])
        XCTAssertEqual(KuwoLrcx.coefficients("[kuwo:127]").map { [$0.k1, $0.k2] }, [8, 7])
        XCTAssertNil(KuwoLrcx.coefficients("[kuwo:010]"), "a zero coefficient cannot be decoded")
        XCTAssertNil(KuwoLrcx.coefficients("[ti:no header]"))
    }

    func testKuwoLrcxBecomesWordTimedLinesWithoutTheTranslation() throws {
        let lrc = try XCTUnwrap(KuwoLrcx.enhancedLRC(from: lrcx(lrcxSample)))
        XCTAssertFalse(lrc.contains("我"), "translation lines carry only <0,0> stamps and are dropped")
        let lines = LyricsFetcher.parseLRC(lrc, trackDuration: 60).filter { !$0.isInstrumental }
        XCTAssertEqual(lines.count, 2)
        XCTAssertTrue(lines.allSatisfy(\.wordTimed))
        XCTAssertEqual(words(lines[0]), ["I", "never", "stop"])
        // <a,b>: start |a+b|/(2·k1), length |a−b|/(2·k2) ms after the line.
        XCTAssertEqual(lines[0].words[0].start, 19.205, accuracy: 0.002)
        XCTAssertEqual(lines[0].words[1].start, 19.205 + 0.169, accuracy: 0.002)
        XCTAssertEqual(lines[0].words[2].start, 19.205 + 0.4, accuracy: 0.002)
        XCTAssertEqual(lines[1].start, 27.326, accuracy: 0.002)
    }

    // MARK: - Kugou KRC

    func testKRCWordsThatStartBeforeTheirLineStayWordTimed() throws {
        let lrc = try XCTUnwrap(KugouKRC.enhancedLRC(fromText: "[1000,2000]<-11,500,0>Early <500,600,0>word"))
        XCTAssertFalse(lrc.contains("<-11"), lrc)
        let line = try XCTUnwrap(LyricsFetcher.parseLRC(lrc, trackDuration: 10).first)
        XCTAssertTrue(line.wordTimed)
        XCTAssertEqual(words(line), ["Early", "word"])
        XCTAssertEqual(line.words[0].start, 1.0, accuracy: 0.001)
    }

    // MARK: - Providers, with fallback to line LRC

    private let query = LyricQuery(title: "Our Song", artist: "Our Artist", album: nil, duration: 180)

    private func session() -> URLSession {
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [Stub.self]
        return URLSession(configuration: config)
    }
    private func qqSearch(_ ids: [Int]) -> String {
        let rows = ids.map { #"{"songid":\#($0),"songmid":"mid\#($0)","songname":"Our Song","singer":[{"name":"Our Artist"}],"interval":180}"# }
        return #"{"data":{"song":{"list":["# + rows.joined(separator: ",") + "]}}}"
    }
    private let qqLine = #"{"lyric":"[00:01.00]Sing along\n[00:04.00]Next line"}"#

    func testQQPrefersQRCWordTiming() async throws {
        Stub.reset([
            "c.y.qq.com/soso/fcgi-bin/client_search_cp": qqSearch([7]),
            "c.y.qq.com/qqmusic/fcgi-bin/lyric_download.fcg": download(qrcCipher(qrcXML(realQRCOpening))),
        ])
        let session = session(); defer { session.invalidateAndCancel() }
        let found = try await XCTUnwrapAsync(await AdditionalLyricsProviders.qqMusic(query, session: session))
        XCTAssertEqual(found.source, .qqMusic)
        XCTAssertTrue(LyricsFetcher.hasWordTiming(found.result))
        XCTAssertEqual(Stub.paths, ["c.y.qq.com/soso/fcgi-bin/client_search_cp", "c.y.qq.com/qqmusic/fcgi-bin/lyric_download.fcg"],
                       "word timing needs one request beyond the search")
        XCTAssertEqual(Stub.methods.last, "POST")
    }

    func testQQFallsBackToLineLRCWhenThereIsNoQRC() async throws {
        Stub.reset([
            "c.y.qq.com/soso/fcgi-bin/client_search_cp": qqSearch([7]),
            "c.y.qq.com/qqmusic/fcgi-bin/lyric_download.fcg": download("not encrypted"),
            "c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg": qqLine,
        ])
        let session = session(); defer { session.invalidateAndCancel() }
        let found = try await XCTUnwrapAsync(await AdditionalLyricsProviders.qqMusic(query, session: session))
        XCTAssertEqual(found.lineCount, 2)
        XCTAssertEqual(LyricsFetcher.timingTier(found.result), 1)
        // A failed QRC request is not an outage either.
        Stub.reset([
            "c.y.qq.com/soso/fcgi-bin/client_search_cp": qqSearch([7]),
            "c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg": qqLine,
        ])
        let again = try await AdditionalLyricsProviders.qqMusic(query, session: session)
        XCTAssertEqual(again?.lineCount, 2)
    }

    func testQQUsesTheLineLRCInTheDownloadReplyWithoutAnotherRequest() async throws {
        Stub.reset([
            "c.y.qq.com/soso/fcgi-bin/client_search_cp": qqSearch([7]),
            "c.y.qq.com/qqmusic/fcgi-bin/lyric_download.fcg": download(qrcCipher("[00:01.00]Sing along\n[00:04.00]Next line")),
        ])
        let session = session(); defer { session.invalidateAndCancel() }
        let found = try await XCTUnwrapAsync(await AdditionalLyricsProviders.qqMusic(query, session: session))
        XCTAssertEqual(found.lineCount, 2)
        XCTAssertFalse(Stub.paths.contains("c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg"))
    }

    func testQQPrefersALaterHitWithQRCOverAnEarlierLineOnlyHit() async throws {
        let qrc = download(qrcCipher(qrcXML(realQRCOpening))), line = download(qrcCipher("[00:01.00]Sing along\n[00:04.00]Next line"))
        Stub.reset(["c.y.qq.com/soso/fcgi-bin/client_search_cp": qqSearch([7, 8, 9])]) { url in
            guard url.path.hasSuffix("lyric_download.fcg") else { return nil }
            // Hit 7 has line LRC only, hit 8 has QRC.
            return Data((Stub.requestCount(matching: "lyric_download.fcg") == 1 ? line : qrc).utf8)
        }
        let session = session(); defer { session.invalidateAndCancel() }
        let found = try await XCTUnwrapAsync(await AdditionalLyricsProviders.qqMusic(query, session: session))
        XCTAssertTrue(LyricsFetcher.hasWordTiming(found.result))
        XCTAssertEqual(Stub.requestCount(matching: "lyric_download.fcg"), 2, "stops at the first hit with QRC")
    }

    private func kuwoStubs(lrcx reply: String?) -> [String: String] {
        var stubs = [
            "search.kuwo.cn/r.s": #"{"abslist":[{"MUSICRID":"MUSIC_123","SONGNAME":"Our Song","ARTIST":"Our Artist","DURATION":"180"}]}"#,
            "www.kuwo.cn/openapi/v1/www/lyric/getlyric": #"{"code":200,"data":{"lrclist":[{"time":"1.0","lineLyric":"Sing along"},{"time":"4.0","lineLyric":"Next line"}]}}"#,
        ]
        stubs["mlyric.kuwo.cn/mobi.s"] = reply
        return stubs
    }

    func testKuwoPrefersLrcxWordTiming() async throws {
        let reply = lrcx(lrcxSample)
        Stub.reset(kuwoStubs(lrcx: nil)) { url in
            url.host == "mlyric.kuwo.cn" ? reply : nil
        }
        let session = session(); defer { session.invalidateAndCancel() }
        let found = try await XCTUnwrapAsync(await AdditionalLyricsProviders.kuwo(query, session: session))
        XCTAssertEqual(found.source, .kuwo)
        XCTAssertTrue(LyricsFetcher.hasWordTiming(found.result))
        XCTAssertFalse(Stub.paths.contains("www.kuwo.cn/openapi/v1/www/lyric/getlyric"))
        let rid = Stub.urls.first { $0.host == "mlyric.kuwo.cn" }.flatMap { URLComponents(url: $0, resolvingAgainstBaseURL: false) }?
            .queryItems?.first { $0.name == "rid" }?.value
        XCTAssertEqual(rid, "123")
    }

    func testKuwoFallsBackToTheLineList() async throws {
        Stub.reset(kuwoStubs(lrcx: "<html>blocked</html>"))
        let session = session(); defer { session.invalidateAndCancel() }
        let found = try await XCTUnwrapAsync(await AdditionalLyricsProviders.kuwo(query, session: session))
        XCTAssertEqual(found.lineCount, 2)
        XCTAssertEqual(LyricsFetcher.timingTier(found.result), 1)
    }

    func testKuwoAndQQAdvertiseWordTiming() {
        XCTAssertEqual(LyricSourceID.qqMusic.timing, "Word or line timing")
        XCTAssertEqual(LyricSourceID.kuwo.timing, "Word or line timing")
        XCTAssertEqual(LyricSourceID.kuwo.timingCeiling, 3)
        XCTAssertEqual(LyricSourceID.qqMusic.timingCeiling, 3)
    }

    // MARK: - Live (opt-in)

    /// Set LUNALYR_LIVE_WORDTIMING=1 (TEST_RUNNER_LUNALYR_LIVE_WORDTIMING=1 through
    /// xcodebuild) to ask the real services; prints what each one returned.
    func testLiveWordTiming() async throws {
        guard ProcessInfo.processInfo.environment["LUNALYR_LIVE_WORDTIMING"] != nil else { throw XCTSkip("live check is opt-in") }
        let songs = [LyricQuery(title: "晴天", artist: "周杰伦", album: nil, duration: 269),
                     LyricQuery(title: "Blinding Lights", artist: "The Weeknd", album: nil, duration: 200),
                     LyricQuery(title: "稻香", artist: "周杰伦", album: nil, duration: 223)]
        for song in songs {
            for (name, fetch) in [("QQ", AdditionalLyricsProviders.qqMusic(_:session:)), ("Kuwo", AdditionalLyricsProviders.kuwo(_:session:)),
                                  ("Kugou", AdditionalLyricsProviders.kugou(_:session:))] {
                let found = try? await fetch(song, .shared)
                var detail = "none"
                if let found, case .synced(let lines, _) = found.result {
                    let timed = lines.filter(\.wordTimed)
                    detail = "\(found.title) / \(found.artist) lines=\(lines.count) wordTimed=\(timed.count) word=\(LyricsFetcher.hasWordTiming(found.result))"
                    if let sample = timed.dropFirst(min(8, max(0, timed.count - 1))).first {
                        detail += " sample=" + sample.words.prefix(4).map { String(format: "%.2f-%.2f", $0.start, $0.end) }.joined(separator: ",")
                    }
                }
                print("LIVE-WORDTIMING \(name) \(song.title): \(detail)")
            }
        }
    }
}

private func XCTUnwrapAsync<T>(_ value: T?, file: StaticString = #filePath, line: UInt = #line) throws -> T {
    try XCTUnwrap(value, file: file, line: line)
}

/// Answers by host and path, and remembers each request.
private final class Stub: URLProtocol {
    private static let lock = NSLock()
    nonisolated(unsafe) private static var replies: [String: String] = [:]
    nonisolated(unsafe) private static var dynamic: ((URL) -> Data?)?
    nonisolated(unsafe) private static var seen: [(url: URL, method: String)] = []

    static func reset(_ stubs: [String: String], _ handler: ((URL) -> Data?)? = nil) {
        lock.withLock { replies = stubs; dynamic = handler; seen = [] }
    }
    static var urls: [URL] { lock.withLock { seen.map(\.url) } }
    static var paths: [String] { urls.map { ($0.host ?? "") + $0.path } }
    static var methods: [String] { lock.withLock { seen.map(\.method) } }
    static func requestCount(matching suffix: String) -> Int { paths.filter { $0.hasSuffix(suffix) }.count }

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        let url = request.url!
        let fixed: String? = Self.lock.withLock {
            Self.seen.append((url, request.httpMethod ?? "GET"))
            return Self.replies[(url.host ?? "") + url.path]
        }
        let handler = Self.lock.withLock { Self.dynamic }
        let body = handler?(url) ?? fixed.map { Data($0.utf8) }
        let response = HTTPURLResponse(url: url, statusCode: body == nil ? 500 : 200, httpVersion: "HTTP/1.1", headerFields: [:])!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: body ?? Data())
        client?.urlProtocolDidFinishLoading(self)
    }
    override func stopLoading() {}
}
