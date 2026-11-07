import Compression
import XCTest
@testable import VersoCustom

final class AMLLAndKRCTests: XCTestCase {
    private func krc(_ text: String) -> Data {
        let key: [UInt8] = [0x40, 0x47, 0x61, 0x77, 0x5E, 0x32, 0x74, 0x47, 0x51, 0x36, 0x31, 0x2D, 0xCE, 0xD2, 0x6E, 0x69]
        let raw = (try? (Data(text.utf8) as NSData).compressed(using: .zlib) as Data) ?? Data()
        var bytes = [0x78, 0x9C] + [UInt8](raw)
        for index in bytes.indices { bytes[index] ^= key[index % key.count] }
        return Data("krc1".utf8) + Data(bytes)
    }

    private let sample = "[id:$00000000]\n[ar:An Artist]\n[1000,2000]<0,500,0>Hello <500,600,0>world <1100,900,0>again\n[4000,1500]<0,700,0>Second<700,800,0> line"

    func testKRCDecodesToTheLinesAndWords() throws {
        let text = try XCTUnwrap(KugouKRC.decode(krc(sample)))
        XCTAssertTrue(text.contains("<0,500,0>Hello"))
    }

    func testKRCBecomesWordTimedLines() throws {
        let lrc = try XCTUnwrap(KugouKRC.enhancedLRC(from: krc(sample)))
        let lines = LyricsFetcher.parseLRC(lrc, trackDuration: 10)
        XCTAssertEqual(lines.count, 2)
        XCTAssertTrue(lines[0].wordTimed)
        XCTAssertEqual(lines[0].words.map { $0.text.trimmingCharacters(in: .whitespaces) }, ["Hello", "world", "again"])
        XCTAssertEqual(lines[0].words[1].start, 1.5, accuracy: 0.02)
        XCTAssertEqual(lines[0].words[0].end, 1.5, accuracy: 0.1)
    }

    func testAnythingThatIsNotKRCIsRefused() {
        XCTAssertNil(KugouKRC.decode(Data("[00:01.00]plain".utf8)))
        XCTAssertNil(KugouKRC.enhancedLRC(from: Data(repeating: 1, count: 40)))
    }

    func testAMLLAddressesTheFileByNetEaseID() {
        XCTAssertEqual(AMLLLyricsProvider.fileAddress(base: "https://example.test/db", neteaseID: 12345),
                       "https://example.test/db/ncm-lyrics/12345.ttml")
    }

    func testAnAMLLFileBecomesAWordTimedCandidate() throws {
        let ttml = """
        <tt xmlns="http://www.w3.org/ns/ttml" xmlns:itunes="http://music.apple.com/lyric-ttml-internal" itunes:timing="Word">
        <body><div><p begin="1.000" end="3.000"><span begin="1.000" end="1.500">Hello</span> <span begin="1.500" end="2.200">world</span></p></div></body></tt>
        """
        let song = NetEaseLyricsProvider.MatchedSong(id: 7, title: "Hello World", artist: "Somebody", duration: 200)
        let candidate = try XCTUnwrap(AMLLLyricsProvider.candidate(ttml: ttml, song: song,
            query: LyricQuery(title: "Hello World", artist: "Somebody", album: nil, duration: 200)))
        XCTAssertEqual(candidate.source, .amll)
        guard case .synced(let lines, let provenance) = candidate.result else { return XCTFail("synced") }
        XCTAssertTrue(lines.contains { $0.wordTimed })
        XCTAssertEqual(provenance.source, "AMLL TTML DB")
    }

    func testAMLLIsAListedSourceAndOnByDefault() {
        XCTAssertTrue(LyricSourceID.allCases.contains(.amll))
        XCTAssertTrue(LyricSourceOptions.recommendedOrder.contains(.amll))
        XCTAssertFalse(LyricSourceID.amll.isLocal)
    }

    /// Set LUNALYR_AMLL_SAMPLE to a downloaded ncm-lyrics file to check a real one (not stored in the repo).
    func testARealAMLLFileParsesToWordTimedLines() throws {
        guard let path = ProcessInfo.processInfo.environment["LUNALYR_AMLL_SAMPLE"] else { throw XCTSkip("no sample") }
        let text = try String(contentsOfFile: path, encoding: .utf8)
        let lines = TTMLLyricParser.parse(text, duration: 200)
        let vocal = lines.filter { !$0.isInstrumental }
        print("AMLL-REAL lines=\(vocal.count) wordTimed=\(vocal.filter(\.wordTimed).count) valid=\(LyricsFetcher.hasWordTiming(.synced(lines, .init(precision: "", source: "")))) first=\(vocal.first?.words.count ?? 0) words")
        XCTAssertGreaterThan(vocal.count, 10)
        XCTAssertGreaterThanOrEqual(Double(vocal.filter(\.wordTimed).count), Double(vocal.count) * 0.9)
    }
}
