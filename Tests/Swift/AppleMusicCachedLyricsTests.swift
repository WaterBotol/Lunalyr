import Foundation
import SQLite3
import XCTest
@testable import VersoCustom

final class AppleMusicCachedLyricsTests: XCTestCase {
    private let query = LyricQuery(title: "A Song", artist: "A Singer", album: "An Album", duration: 180)

    private func payload(id: String = "100", album: String = "An Album", duration: Int = 180_100,
                         ttml: String = "<tt><body><div><p begin='1' end='3'><span begin='1' end='2'>One</span> <span begin='2' end='3'>two</span></p></div></body></tt>") throws -> Data {
        try JSONSerialization.data(withJSONObject: [
            "data": [[
                "id": id, "type": "songs",
                "attributes": ["name": "A Song", "artistName": "A Singer", "albumName": album, "durationInMillis": duration] as [String: Any],
                "relationships": ["syllable-lyrics": ["data": [[
                    "type": "syllable-lyrics", "attributes": ["ttmlLocalizations": ttml],
                ]]]],
            ]],
        ])
    }

    func testExactRecordingRequiredAndAmbiguityFallsBack() throws {
        let decoder = JSONDecoder()
        func songs(_ bytes: Data) throws -> [AppleMusicCachedLyrics.Song] {
            try decoder.decode(AppleMusicCachedLyrics.Envelope.self, from: bytes).data
        }
        let right = try songs(payload())
        let wrong = try songs(payload(album: "An Album Live")) + songs(payload(duration: 184_000))
        XCTAssertNil(AppleMusicCachedLyrics.candidate(query, songs: wrong))
        XCTAssertNil(try AppleMusicCachedLyrics.candidate(query, songs: right + songs(payload(id: "200"))))
        let match = try XCTUnwrap(AppleMusicCachedLyrics.candidate(query, songs: wrong + right + right))
        XCTAssertEqual(match.provenance.precision, "Word-by-word")
    }

    func testReadsOnlyExpectedCacheEntriesAndHandlesMissingSchema() throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let responses = folder.appendingPathComponent("fsCachedData", isDirectory: true)
        try FileManager.default.createDirectory(at: responses, withIntermediateDirectories: true)
        let path = folder.appendingPathComponent("Cache.db").path
        var handle: OpaquePointer?
        XCTAssertEqual(sqlite3_open(path, &handle), SQLITE_OK)
        let db = try XCTUnwrap(handle)
        defer { sqlite3_close(db) }
        func run(_ sql: String) {
            XCTAssertEqual(sqlite3_exec(db, sql, nil, nil, nil), SQLITE_OK)
        }
        XCTAssertNil(AppleMusicCachedLyrics.candidate(query, directory: folder))
        run("CREATE TABLE cfurl_cache_response(entry_ID INTEGER, request_key TEXT, time_stamp TEXT)")
        run("CREATE TABLE cfurl_cache_receiver_data(entry_ID INTEGER, isDataOnFS INTEGER, receiver_data BLOB)")
        let name = UUID().uuidString
        let body = responses.appendingPathComponent(name)
        try payload().write(to: body)
        run("INSERT INTO cfurl_cache_response VALUES(1,'https://amp-api.music.apple.com/v1/catalog/au/songs?include=syllable-lyrics','2026-11-04')")
        run("INSERT INTO cfurl_cache_receiver_data VALUES(1,1,'\(name)')")
        XCTAssertEqual(AppleMusicCachedLyrics.candidate(query, directory: folder)?.source, .appleMusicCache)
        try payload(ttml: "<invalid>").write(to: body)
        XCTAssertNil(AppleMusicCachedLyrics.candidate(query, directory: folder))
        try payload().write(to: body)
        run("UPDATE cfurl_cache_response SET request_key='https://example.com/v1/catalog/au/songs?include=syllable-lyrics'")
        XCTAssertNil(AppleMusicCachedLyrics.candidate(query, directory: folder))
    }
}
