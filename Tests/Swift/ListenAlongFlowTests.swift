import XCTest
@testable import VersoCustom

// End-to-end follower and host flows around song changes, over fakes: no
// socket, no network, no Apple event.

@MainActor
final class ListenAlongFlowTests: XCTestCase {
    private let base = URL(string: "https://relay.example.com")!
    private let track = ListenProtocol.Track(title: "Blinding Lights", artist: "The Weeknd", album: "After Hours",
                                             duration: 200, spotifyId: "0VjIjW4GlUZAMYd2vXMi3b", appleId: nil)
    private let nextTrack = ListenProtocol.Track(title: "Save Your Tears", artist: "The Weeknd", album: "After Hours",
                                                 duration: 215, spotifyId: "5QO79kh1waicV47BqGRL3g", appleId: nil)

    private func stateText(playing: Bool = true, position: Double, track: ListenProtocol.Track? = nil) -> String {
        let state = ListenProtocol.State(seq: 1, track: track ?? self.track, playing: playing,
                                         position: position, lyrics: nil, host: .init(name: "Tommy"))
        return String(decoding: ListenProtocol.encode(state), as: UTF8.self)
    }

    private func deliver(_ socket: FakeListenSocket, _ text: String) async {
        socket.push(text)
        try? await Task.sleep(nanoseconds: 30_000_000)
    }

    private func settle(_ clock: ListenTestClock, _ follower: ListenFollower, by seconds: Double = 4) async {
        clock.advance(seconds)
        follower.sync()
        try? await Task.sleep(nanoseconds: 30_000_000)
    }

    private func following(_ player: FakeListenPlayer, clock: ListenTestClock,
                           first: String? = nil) async throws -> (ListenFollower, FakeListenSocket) {
        let sockets = FakeListenSockets()
        let follower = ListenFollower(room: "abc123defg", base: base, sockets: sockets, player: player, clock: { clock.now })
        follower.start()
        let socket = try XCTUnwrap(sockets.opened.first)
        await deliver(socket, first ?? stateText(position: 10))
        await waitUntil { player.current != nil }
        await settle(clock, follower)
        return (follower, socket)
    }

    /// The follower's copy ends a moment before the host's, and the host's
    /// next song arrives before the follower's own check sees it: that is
    /// the song ending, not the user skipping.
    func testTheHostsNextSongArrivingFirstIsNotTakenForASkip() async throws {
        let player = FakeListenPlayer()
        let clock = ListenTestClock()
        let (follower, socket) = try await following(player, clock: clock)
        await deliver(socket, stateText(position: 197))
        clock.advance(2.5)
        // The follower's song ran out and its player went on to its own next one.
        player.current = ListenPlayerReading(kind: .spotify, title: "", artist: "", trackID: "spotify:track:queued",
                                             position: 0.5, playing: true)
        await deliver(socket, stateText(position: 0, track: nextTrack))
        await settle(clock, follower)
        XCTAssertNil(follower.hold, "a song running out is not the user taking over")
        await waitUntil { player.log.contains { $0.hasPrefix("play spotify(id: \"5QO79kh1waicV47BqGRL3g\")") } }
    }

    /// A skip mid-song on the follower's side still pauses following, even
    /// right after the host's song changed.
    func testASkipRightAfterTheHostChangesSongStillHolds() async throws {
        let player = FakeListenPlayer()
        let clock = ListenTestClock()
        let (follower, socket) = try await following(player, clock: clock)
        // The host skips at 60 s (not the end of their song).
        await deliver(socket, stateText(position: 60))
        await deliver(socket, stateText(position: 0, track: nextTrack))
        await settle(clock, follower)
        await waitUntil { player.log.contains { $0.hasPrefix("play spotify(id: \"5QO79kh1waicV47BqGRL3g\")") } }
        await settle(clock, follower)
        player.current = ListenPlayerReading(kind: .spotify, title: "Mine", artist: "Me", trackID: "spotify:track:mine",
                                             position: 3, playing: true)
        await settle(clock, follower)
        XCTAssertEqual(follower.hold, .paused)
    }

    /// The host's player repeats the song; the follower's copy had ended
    /// and was let go. It is played again from the top.
    func testAHostRepeatingTheSongPlaysItAgain() async throws {
        let player = FakeListenPlayer()
        let clock = ListenTestClock()
        let (follower, socket) = try await following(player, clock: clock)
        await deliver(socket, stateText(position: 195))
        player.current = ListenPlayerReading(kind: .spotify, title: "", artist: "", trackID: "spotify:track:queued",
                                             position: 1, playing: true)
        await settle(clock, follower)
        XCTAssertNil(follower.hold)
        XCTAssertNil(follower.target, "the ended song was let go")
        await deliver(socket, stateText(position: 1))
        await settle(clock, follower)
        await waitUntil { player.log.filter { $0.hasPrefix("play spotify(id: \"0VjIjW4GlUZAMYd2vXMi3b\")") }.count == 2 }
        XCTAssertNil(follower.hold)
    }

    /// A lookup for a song the host has already moved past must not clear
    /// the newer song's lookup: until it is done, the old song is left alone.
    func testAnOutdatedLookupDoesNotCutTheCurrentOneShort() async throws {
        let player = FakeListenPlayer()
        player.kind = .appleMusic
        player.library = [LibraryCandidate(persistentID: "00000000000000A1", title: "Blinding Lights", artist: "The Weeknd", duration: 200)]
        let clock = ListenTestClock()
        let (follower, socket) = try await following(player, clock: clock)
        XCTAssertEqual(player.current?.trackID, "00000000000000A1")

        player.holdsSearches = true
        var third = nextTrack
        third.title = "In Your Eyes"; third.spotifyId = nil; third.duration = 237
        var second = nextTrack
        second.spotifyId = nil
        await deliver(socket, stateText(position: 0, track: second))
        await deliver(socket, stateText(position: 0, track: third))
        XCTAssertEqual(player.pendingSearches, 2)
        player.library = [LibraryCandidate(persistentID: "00000000000000C3", title: "In Your Eyes", artist: "The Weeknd", duration: 237)]
        player.releaseSearch()   // the outdated one
        try await Task.sleep(nanoseconds: 30_000_000)
        await settle(clock, follower)
        XCTAssertFalse(player.log.contains { $0.hasPrefix("pause") }, "nothing happens while the current song is looked up")
        player.releaseSearch()
        await waitUntil { player.log.contains { $0.hasPrefix("play library(persistentID: \"00000000000000C3\")") } }
        XCTAssertNil(follower.hold)
    }

    // MARK: Host

    /// A burst of skips stays under the relay's five states a second (it
    /// would drop the rest, perhaps the last song), and the held state
    /// goes with the next update.
    func testABurstOfSkipsStaysUnderTheRelaysLimitAndTheLastSongGoes() async throws {
        let http = FakeListenHTTP()
        http.reply("https://relay.example.com/rooms", #"{"room":"abc123defg","hostKey":"0123456789abcdef0123456789abcdef"}"#)
        let sockets = FakeListenSockets()
        let clock = ListenTestClock()
        let host = ListenHost(base: base, http: http, sockets: sockets, clock: { clock.now })
        host.update(ListenHostSnapshot(track: track, playing: true, position: 10))
        await waitUntil { sockets.opened.first?.sent.count == 1 }
        let socket = try XCTUnwrap(sockets.opened.first)

        clock.advance(2)
        var last = track
        for index in 0..<8 {
            last = ListenProtocol.Track(title: "Song \(index)", artist: "Artist", album: nil, duration: 180)
            host.update(ListenHostSnapshot(track: last, playing: true, position: 0))
            clock.advance(0.05)
        }
        try await Task.sleep(nanoseconds: 30_000_000)
        XCTAssertEqual(socket.sent.count, 1 + ListenHost.maximumStatesPerSecond)

        clock.advance(1)
        host.update(ListenHostSnapshot(track: last, playing: true, position: 1.4))
        await waitUntil { socket.sent.count == 2 + ListenHost.maximumStatesPerSecond }
        guard case .state(let sent)? = ListenProtocol.decode(socket.sent.last!) else { return XCTFail("not a state") }
        XCTAssertEqual(sent.track?.title, "Song 7")
        host.end()
    }
}
