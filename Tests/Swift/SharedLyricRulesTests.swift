import Foundation
import XCTest
@testable import VersoCustom

final class SharedLyricRulesTests: XCTestCase {
    private struct Fixtures: Decodable {
        struct Recording: Decodable {
            let title: String
            let artist: String
            let duration: Double
            let match: Bool
        }
        struct Timeline: Decodable {
            let name: String
            let lrc: String
            let starts: [Double]
            let wordTimed: [Bool]
            let firstEnd: Double
        }
        let recordings: [Recording]
        let timelines: [Timeline]
    }

    private func fixtures() throws -> Fixtures {
        let root = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
            .deletingLastPathComponent().deletingLastPathComponent()
        let file = root.appendingPathComponent("Shared/LyricRules/Fixtures.json")
        return try JSONDecoder().decode(Fixtures.self, from: Data(contentsOf: file))
    }

    func testRecordingMatches() throws {
        let query = LyricQuery(title: "Rain Again", artist: "Lunar Tides", album: nil, duration: 180)
        for row in try fixtures().recordings {
            let candidate = LyricCandidate(source: .lrclib,
                result: .plain(["A lyric"], .init(precision: "Plain", source: "Fixture")),
                title: row.title, artist: row.artist, duration: row.duration, score: 0)
            XCTAssertEqual(LyricsFetcher.rejection(candidate, query: query) == nil, row.match,
                "\(row.title), \(row.artist), \(row.duration)s")
        }
    }

    func testMixedTiming() throws {
        for row in try fixtures().timelines {
            let lines = LyricLineIndex.removingMixedTimingCopies(LyricsFetcher.parseLRC(row.lrc, trackDuration: 12))
            XCTAssertEqual(lines.map(\.start), row.starts, row.name)
            XCTAssertEqual(lines.map(\.wordTimed), row.wordTimed, row.name)
            XCTAssertEqual(lines.first?.end, row.firstEnd, row.name)
        }
    }
}
