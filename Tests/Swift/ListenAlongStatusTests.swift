import XCTest
@testable import VersoCustom

// What the menu says about listen-along: reconnecting is not "listening",
// sharing says when it is waiting or can't reach the relay, and an ending is
// kept in view. All over fakes: no socket, no network, no Apple event.

@MainActor
final class ListenAlongStatusTests: XCTestCase {
    private let base = URL(string: "https://relay.example.com")!
    private let track = ListenProtocol.Track(title: "Song", artist: "Artist", album: nil, duration: 100)

    private func stateText() -> String {
        let state = ListenProtocol.State(seq: 1, track: nil, playing: false, position: 0, lyrics: nil,
                                         host: .init(name: "Sam"))
        return String(decoding: ListenProtocol.encode(state), as: UTF8.self)
    }

    func testADroppedConnectionReadsAsReconnectingUntilAStateArrives() async throws {
        let sockets = FakeListenSockets()
        let follower = ListenFollower(room: "abc123defg", base: base, sockets: sockets, player: FakeListenPlayer())
        var followed = 0
        follower.onFollowing = { followed += 1 }
        follower.start()
        let first = try XCTUnwrap(sockets.opened.first)
        first.push(stateText())
        await waitUntil { follower.phase == .following }
        XCTAssertEqual(followed, 1, "a join is confirmed once, on the first state")
        XCTAssertFalse(follower.isReconnecting)

        first.drop()
        await waitUntil { follower.isReconnecting }
        XCTAssertEqual(MenuBarListenAlong.followingTitle(phase: follower.phase, reconnecting: true, name: "Sam"),
                       "Reconnecting to Sam\u{2026}")
        await waitUntil("retried") { sockets.opened.count == 2 }
        sockets.opened[1].push(stateText())
        await waitUntil { !follower.isReconnecting }
        XCTAssertEqual(followed, 1, "a reconnect is not a new join")
        follower.stop()
    }

    func testFollowingTitles() {
        XCTAssertEqual(MenuBarListenAlong.followingTitle(phase: .connecting, reconnecting: false, name: "Sam"),
                       "Joining Sam\u{2026}")
        XCTAssertEqual(MenuBarListenAlong.followingTitle(phase: .following, reconnecting: false, name: "Sam"),
                       "Listening along with Sam")
    }

    func testSharingSaysWhatItIsWaitingFor() {
        XCTAssertEqual(ListenAlong.sharingState(room: nil, listeners: 0, retrying: false), .waitingForMusic)
        XCTAssertEqual(ListenAlong.sharingState(room: nil, listeners: 0, retrying: true), .retrying)
        XCTAssertEqual(ListenAlong.sharingState(room: "abc123defg", listeners: 2, retrying: true), .retrying,
                       "a room the relay dropped is not live")
        XCTAssertEqual(ListenAlong.sharingState(room: "abc123defg", listeners: 2, retrying: false),
                       .live(room: "abc123defg", listeners: 2))
        XCTAssertTrue(MenuBarListenAlong.sharingTitle(.waitingForMusic).contains("when something plays"))
        XCTAssertTrue(MenuBarListenAlong.sharingTitle(.retrying).contains("relay"))
        XCTAssertEqual(MenuBarListenAlong.sharingTitle(.live(room: "abc123defg", listeners: 1)), "Sharing \u{00B7} 1 listening")
    }

    func testAHostThatCannotReachTheRelaySaysItIsRetrying() async {
        let http = FakeListenHTTP()
        http.fails = true
        let host = ListenHost(base: base, http: http, sockets: FakeListenSockets(), clock: { 0 })
        XCTAssertFalse(host.retrying)
        host.update(ListenHostSnapshot(track: track, playing: true, position: 1))
        await waitUntil { host.retrying }
        host.end()
    }

    func testAnEndingStaysInTheMenuUntilDismissed() async throws {
        let sockets = FakeListenSockets()
        let confirmer = FakeListenConfirmer()
        let listen = ListenAlong(sockets: sockets, makePlayer: { FakeListenPlayer() }, confirmer: confirmer,
                                 connects: true, observesPlayback: false)
        listen.configure(relay: "https://relay.example.com", sharing: false)
        XCTAssertTrue(listen.requestJoin(code: "abc123defg"))
        await waitUntil { confirmer.isAsking }
        confirmer.answer(true)
        await waitUntil { sockets.opened.count == 1 }
        sockets.opened[0].push(#"{"type":"ended"}"#)
        await waitUntil { listen.follower == nil }
        XCTAssertNotNil(listen.lastEnded)
        listen.dismissLastEnded()
        XCTAssertNil(listen.lastEnded)
    }
}
