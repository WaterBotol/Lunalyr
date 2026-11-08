import XCTest
@testable import VersoCustom

@MainActor
final class DiscordPresenceTests: XCTestCase {
    func testFrameIsOpcodeLengthThenJSON() throws {
        let data = try XCTUnwrap(DiscordPresence.frame(.handshake, ["v": 1]))
        XCTAssertEqual(Array(data.prefix(4)), [0, 0, 0, 0])
        let length = data.subdata(in: 4..<8).withUnsafeBytes { $0.loadUnaligned(as: UInt32.self) }.littleEndian
        XCTAssertEqual(Int(length), data.count - 8)
        let body = try JSONSerialization.jsonObject(with: data.suffix(from: 8)) as? [String: Int]
        XCTAssertEqual(body, ["v": 1])
        XCTAssertEqual(DiscordPresence.frame(.frame, [:])?.first, 1)
    }

    func testFieldsAreClippedAndPadded() {
        XCTAssertEqual(DiscordPresence.field("  hello  "), "hello")
        XCTAssertEqual(DiscordPresence.field(String(repeating: "a", count: 300)).count, 128)
        XCTAssertGreaterThanOrEqual(DiscordPresence.field("a").count, 2)
        XCTAssertGreaterThanOrEqual(DiscordPresence.field("").count, 2)
    }

    func testActivityPutsTheLineInTheStatusAndTheSongOnTheProfile() throws {
        let playing = DiscordPresence.Playing(title: "Song", artist: "Artist", album: "Album",
                                              line: "the line being sung", start: 1000, end: 1200)
        let activity = DiscordPresence.activity(for: playing)
        XCTAssertEqual(activity["type"] as? Int, 2)
        XCTAssertEqual(activity["status_display_type"] as? Int, 1)
        XCTAssertEqual(activity["state"] as? String, "the line being sung")
        XCTAssertEqual(activity["details"] as? String, "Song · Artist")
        let timestamps = try XCTUnwrap(activity["timestamps"] as? [String: Int])
        XCTAssertEqual(timestamps, ["start": 1000, "end": 1200])
        let assets = try XCTUnwrap(activity["assets"] as? [String: String])
        XCTAssertEqual(assets["large_text"], "Artist — Album")
        let buttons = try XCTUnwrap(activity["buttons"] as? [[String: String]])
        XCTAssertEqual(buttons.first?["label"], "Get Lunalyr")
        XCTAssertEqual(buttons.first?["url"], DiscordPresence.getLunalyrURL)
    }

    func testBetweenLinesTheStatusShowsTheArtist() {
        let playing = DiscordPresence.Playing(title: "Song", artist: "Artist", album: nil, line: nil, start: 0, end: nil)
        let activity = DiscordPresence.activity(for: playing)
        XCTAssertEqual(activity["state"] as? String, "Artist")
        XCTAssertEqual((activity["timestamps"] as? [String: Int])?["end"], nil)
    }

    func testClearingSendsNoActivity() {
        let payload = DiscordPresence.setActivityPayload(nil, pid: 42, nonce: "n")
        XCTAssertEqual(payload["cmd"] as? String, "SET_ACTIVITY")
        XCTAssertNil((payload["args"] as? [String: Any])?["activity"])
        XCTAssertEqual((payload["args"] as? [String: Any])?["pid"] as? Int, 42)
    }
}
