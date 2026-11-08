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

    func testAnInstrumentalBreakIsShownInTheStatus() {
        let playing = DiscordPresence.Playing(title: "Song", artist: "Artist", album: nil,
                                              line: DiscordPresence.instrumentalText, start: 0, end: nil)
        XCTAssertEqual(DiscordPresence.activity(for: playing)["state"] as? String, "♪ Instrumental")
    }

    func testOptionsChangeWhatIsShared() throws {
        var options = DiscordOptions()
        options.showsButton = false
        options.showsProgress = false
        options.showsLyric = false
        options.statusText = .song
        let playing = DiscordPresence.Playing(title: "Song", artist: "Artist", album: "Album", line: "a line",
                                              start: 10, end: 90, coverURL: "https://example.com/c.jpg", options: options)
        let activity = DiscordPresence.activity(for: playing)
        XCTAssertNil(activity["buttons"])
        XCTAssertNil(activity["timestamps"])
        XCTAssertEqual(activity["state"] as? String, "Artist")
        XCTAssertEqual(activity["status_display_type"] as? Int, 2)
        let assets = try XCTUnwrap(activity["assets"] as? [String: String])
        XCTAssertEqual(assets["large_image"], "https://example.com/c.jpg")
        XCTAssertEqual(assets["small_text"], "Lunalyr")
    }

    func testPausedShowsNoProgressBar() {
        let playing = DiscordPresence.Playing(title: "Song", artist: "Artist", album: nil, line: nil,
                                              start: 10, end: 90, paused: true)
        let activity = DiscordPresence.activity(for: playing)
        XCTAssertNil(activity["timestamps"])
        XCTAssertEqual(activity["details"] as? String, "Paused · Song · Artist")
    }

    func testCoverComesFromTheBestMatchingSearchResult() {
        let reply = """
        {"results":[{"trackName":"Other Song","artistName":"Someone","artworkUrl100":"https://a/1/100x100bb.jpg"},
                    {"trackName":"Golden Hour Static","artistName":"Lunar Tides","artworkUrl100":"https://a/2/100x100bb.jpg"}]}
        """
        XCTAssertEqual(DiscordPresence.artworkURL(fromSearch: Data(reply.utf8), title: "Golden Hour Static", artist: "Lunar Tides"),
                       "https://a/2/512x512bb.jpg")
        XCTAssertNil(DiscordPresence.artworkURL(fromSearch: Data(reply.utf8), title: "Missing", artist: "Nobody"))
        XCTAssertNil(DiscordPresence.artworkURL(fromSearch: Data("nope".utf8), title: "x", artist: "y"))
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
