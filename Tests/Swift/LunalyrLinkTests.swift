import XCTest
@testable import VersoCustom

// lunalyr:// links: strict parsing (a link can come from any page) and
// routing over fake actions. No preferences, network or app state.

final class LunalyrLinkParsingTests: XCTestCase {
    private func link(_ text: String) -> LunalyrLink? {
        URL(string: text).flatMap(LunalyrLink.init)
    }

    func testKnownLinks() {
        XCTAssertEqual(link("lunalyr://join/abc123defg"), .join(room: "abc123defg"))
        XCTAssertEqual(link("lunalyr://join/abc123defg/"), .join(room: "abc123defg"))
        XCTAssertEqual(link("lunalyr://toggle"), .command(.toggleLyrics))
        XCTAssertEqual(link("lunalyr://toggle/"), .command(.toggleLyrics))
        XCTAssertEqual(link("lunalyr://card"), .command(.lyricCard))
        XCTAssertEqual(link("lunalyr://settings"), .command(.openSettings))
        XCTAssertEqual(link("LUNALYR://Toggle"), .command(.toggleLyrics))
    }

    func testRoomMustBeExactlyTenLowerCaseLettersOrDigits() {
        for bad in ["abc123def", "abc123defgh", "ABC123DEFG", "abc123def-", "abc123def_", "abc%3123defg",
                    "abc123defg%00", "%61bc123defg", "abc 123def", "ábc123defg"] {
            XCTAssertNil(link("lunalyr://join/\(bad)"), bad)
        }
        XCTAssertNil(link("lunalyr://join"))
        XCTAssertNil(link("lunalyr://join/"))
        XCTAssertNil(link("lunalyr://join/abc123defg/extra"))
        XCTAssertNil(link("lunalyr://join//abc123defg"))
        XCTAssertNil(link("lunalyr://join/../abc123defg"))
    }

    func testAnythingElseIsIgnored() {
        for bad in ["lunalyr://", "lunalyr:toggle", "lunalyr:///toggle", "lunalyr://unknown", "lunalyr://toggle/now",
                    "lunalyr://toggle?x=1", "lunalyr://toggle#x", "lunalyr://user@toggle", "lunalyr://toggle:80",
                    "lunalyr://join/abc123defg?relay=https://evil.example", "https://join/abc123defg",
                    "verso://toggle", "lunalyr://settings/../toggle", "lunalyr://quit", "lunalyr://share"] {
            XCTAssertNil(link(bad), bad)
        }
    }

    func testJoinURLRoundTrips() throws {
        let url = try XCTUnwrap(LunalyrLink.joinURL(room: "abc123defg"))
        XCTAssertEqual(url.absoluteString, "lunalyr://join/abc123defg")
        XCTAssertEqual(LunalyrLink(url), .join(room: "abc123defg"))
        XCTAssertNil(LunalyrLink.joinURL(room: "nope"))
    }
}

@MainActor
final class LunalyrLinkRouterTests: XCTestCase {
    private var joined: [String] = []
    private var ran: [LunalyrCommand] = []
    private var notes: [String] = []
    private var relaySet = true

    private func router(ready: Bool = true) -> LunalyrLinkRouter {
        LunalyrLinkRouter(actions: .init(
            join: { [unowned self] room in
                guard relaySet else { return false }
                joined.append(room)
                return true
            },
            run: { [unowned self] in ran.append($0) },
            note: { [unowned self] text, _ in notes.append(text) }), ready: ready)
    }

    func testRoutesKnownLinksAndIgnoresTheRest() {
        let router = router()
        router.open([URL(string: "lunalyr://toggle")!, URL(string: "lunalyr://nope")!,
                     URL(string: "lunalyr://join/abc123defg")!, URL(string: "lunalyr://card")!,
                     URL(string: "lunalyr://settings")!, URL(string: "lunalyr://join/BAD")!])
        XCTAssertEqual(ran, [.toggleLyrics, .lyricCard, .openSettings])
        XCTAssertEqual(joined, ["abc123defg"])
        XCTAssertTrue(notes.isEmpty)
    }

    func testJoinWithoutARelaySaysWhereToAddOne() {
        relaySet = false
        router().open([URL(string: "lunalyr://join/abc123defg")!])
        XCTAssertTrue(joined.isEmpty)
        XCTAssertEqual(notes.count, 1)
        XCTAssertTrue(notes[0].contains("relay"))
    }

    func testLinksThatLaunchTheAppWaitUntilItHasStarted() {
        let router = router(ready: false)
        router.open([URL(string: "lunalyr://join/abc123defg")!, URL(string: "lunalyr://toggle")!])
        XCTAssertTrue(joined.isEmpty)
        XCTAssertTrue(ran.isEmpty)
        router.becameReady()
        XCTAssertEqual(joined, ["abc123defg"])
        XCTAssertEqual(ran, [.toggleLyrics])
        router.becameReady()
        XCTAssertEqual(ran, [.toggleLyrics])
    }
}
