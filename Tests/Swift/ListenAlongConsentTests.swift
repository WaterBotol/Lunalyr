import XCTest
@testable import VersoCustom

// Listen-along never makes sound the user didn't ask for: a change they make
// themselves pauses following, a join is asked about first, a paused host's
// song is never started. All over fakes: no socket, no network, no Apple event.

@MainActor
final class ListenFollowerConsentTests: XCTestCase {
    private let base = URL(string: "https://relay.example.com")!
    private let track = ListenProtocol.Track(title: "Blinding Lights", artist: "The Weeknd", album: "After Hours",
                                             duration: 200, spotifyId: "0VjIjW4GlUZAMYd2vXMi3b", appleId: nil)
    private let nextTrack = ListenProtocol.Track(title: "Save Your Tears", artist: "The Weeknd", album: "After Hours",
                                                 duration: 215, spotifyId: "5QO79kh1waicV47BqGRL3g", appleId: nil)

    private func stateText(playing: Bool = true, position: Double, track: ListenProtocol.Track? = nil, noTrack: Bool = false) -> String {
        let state = ListenProtocol.State(seq: 1, track: noTrack ? nil : (track ?? self.track), playing: playing,
                                         position: position, lyrics: nil, host: .init(name: "Tommy"))
        return String(decoding: ListenProtocol.encode(state), as: UTF8.self)
    }

    /// A follower that is playing the host's song, settled.
    private func following(_ player: FakeListenPlayer, clock: ListenTestClock,
                           playing: Bool = true) async throws -> (ListenFollower, FakeListenSocket) {
        let sockets = FakeListenSockets()
        let follower = ListenFollower(room: "abc123defg", base: base, sockets: sockets, player: player, clock: { clock.now })
        follower.start()
        let socket = try XCTUnwrap(sockets.opened.first)
        await deliver(socket, stateText(playing: playing, position: 10))
        if playing { await waitUntil { player.current != nil } }
        await waitUntil { follower.phase == .following }
        return (follower, socket)
    }

    /// A message from the relay, given time to arrive.
    private func deliver(_ socket: FakeListenSocket, _ text: String) async {
        socket.push(text)
        try? await Task.sleep(nanoseconds: 30_000_000)
    }

    private func settle(_ clock: ListenTestClock, _ follower: ListenFollower) async {
        clock.advance(ListenFollower.settleTime + 1)
        follower.sync()
        try? await Task.sleep(nanoseconds: 30_000_000)
    }

    // MARK: R2-1

    func testALocalPauseHoldsFollowingAndNothingResumesWithoutAClick() async throws {
        let player = FakeListenPlayer()
        let clock = ListenTestClock()
        let (follower, socket) = try await following(player, clock: clock)
        await waitUntil { player.log.contains("shuffle off") }

        // A pause, a media key, headphones unplugged, sleep: Music is paused.
        player.current?.playing = false
        await settle(clock, follower)
        XCTAssertEqual(follower.hold, .paused)
        XCTAssertEqual(MenuBarListenAlong.holdTitle(.paused), "Paused")
        XCTAssertEqual(MenuBarListenAlong.holdAction(.paused), "Resume")
        await waitUntil { player.log.last == "shuffle on" }

        // The host plays on, seeks, changes song: nothing reaches the player.
        let before = player.log
        await deliver(socket, stateText(position: 40))
        await settle(clock, follower)
        await deliver(socket, stateText(position: 0, track: nextTrack))
        for _ in 0..<5 { await settle(clock, follower) }
        XCTAssertEqual(player.log, before, "nothing is re-asserted until Resume")
        XCTAssertFalse(player.current?.playing ?? true)

        // Resume: the host's current song, at their moment.
        follower.resume()
        await waitUntil { player.log.contains { $0.hasPrefix("play spotify(id: \"5QO79kh1waicV47BqGRL3g\")") } }
        XCTAssertNil(follower.hold)
        XCTAssertEqual(player.current?.playing, true)
    }

    func testPickingAnotherSongHoldsFollowing() async throws {
        let player = FakeListenPlayer()
        let clock = ListenTestClock()
        let (follower, _) = try await following(player, clock: clock)
        player.current = ListenPlayerReading(kind: .spotify, title: "Mine", artist: "Me", trackID: "spotify:track:mine",
                                             position: 3, playing: true)
        let before = player.log.count
        await settle(clock, follower)
        XCTAssertEqual(follower.hold, .paused)
        XCTAssertEqual(player.log.dropFirst(before).filter { !$0.hasPrefix("shuffle") }, [], "their song is left playing")
    }

    func testAnotherPlayerTakingOverHoldsFollowingAndIsNeverCommanded() async throws {
        let player = FakeListenPlayer()
        let clock = ListenTestClock()
        let (follower, socket) = try await following(player, clock: clock)
        // VLC (or a browser) is now the active player.
        player.current = ListenPlayerReading(kind: .vlc, title: "Holiday", artist: "", trackID: nil, position: 3, playing: true)
        await settle(clock, follower)
        XCTAssertEqual(follower.hold, .paused)
        let before = player.log
        await deliver(socket, stateText(playing: false, position: 30))
        socket.push(stateText(position: 0, noTrack: true))
        for _ in 0..<3 { await settle(clock, follower) }
        XCTAssertEqual(player.log, before)
    }

    func testThePlayerClosingHoldsFollowing() async throws {
        let player = FakeListenPlayer()
        let clock = ListenTestClock()
        let (follower, _) = try await following(player, clock: clock)
        player.current = nil
        await settle(clock, follower)
        XCTAssertEqual(follower.hold, .paused)
    }

    func testASongThatRunsOutJustBeforeTheHostsIsNotTakenForASkip() async throws {
        let player = FakeListenPlayer()
        let clock = ListenTestClock()
        let (follower, socket) = try await following(player, clock: clock)
        // 195 s into a 200 s song the player has moved to its next song.
        await deliver(socket, stateText(position: 195))
        player.current = ListenPlayerReading(kind: .spotify, title: "", artist: "", trackID: "spotify:track:queued",
                                             position: 1, playing: true)
        await settle(clock, follower)
        XCTAssertNil(follower.hold)
        XCTAssertEqual(player.log.filter { $0.hasPrefix("play spotify") }.count, 1, "the ended song isn't started again")
        await deliver(socket, stateText(position: 0, track: nextTrack))
        await waitUntil { player.log.contains { $0.hasPrefix("play spotify(id: \"5QO79kh1waicV47BqGRL3g\")") } }
    }

    // MARK: R2-8

    func testAPausedHostsSongIsNeverStartedUntilTheyPlay() async throws {
        let player = FakeListenPlayer()
        let clock = ListenTestClock()
        let (follower, socket) = try await following(player, clock: clock, playing: false)
        for _ in 0..<3 { await settle(clock, follower) }
        XCTAssertEqual(follower.target, .spotify(id: "0VjIjW4GlUZAMYd2vXMi3b"), "remembered")
        XCTAssertFalse(player.everPlayed, "nothing is heard while the host is paused")

        await deliver(socket, stateText(playing: true, position: 12))
        await waitUntil { player.log.contains { $0.hasPrefix("play spotify") } }
        XCTAssertNil(follower.hold)
    }

    func testAHostWhoChangesSongWhilePausedStartsNothing() async throws {
        let player = FakeListenPlayer()
        let clock = ListenTestClock()
        let (follower, socket) = try await following(player, clock: clock)
        await settle(clock, follower)
        await deliver(socket, stateText(playing: false, position: 20))
        await settle(clock, follower)
        await waitUntil { player.log.last == "pause spotify" }
        await deliver(socket, stateText(playing: false, position: 0, track: nextTrack))
        for _ in 0..<3 { await settle(clock, follower) }
        XCTAssertEqual(player.log.filter { $0.hasPrefix("play") }.count, 1, "only the first song, while the host played")
        XCTAssertNil(follower.hold)
        await deliver(socket, stateText(playing: true, position: 1, track: nextTrack))
        await settle(clock, follower)
        await waitUntil { player.log.contains { $0.hasPrefix("play spotify(id: \"5QO79kh1waicV47BqGRL3g\")") } }
    }

    // MARK: R2-12

    func testPauseGoesOnlyToTheFollowedSongsPlayer() async throws {
        let player = FakeListenPlayer()
        let clock = ListenTestClock()
        let (follower, socket) = try await following(player, clock: clock)
        await settle(clock, follower)
        await deliver(socket, stateText(position: 0, noTrack: true))
        await settle(clock, follower)
        await waitUntil { player.log.last == "pause spotify" }

        XCTAssertTrue(LiveListenPlayer.mayCommand(.spotify, active: .spotify))
        XCTAssertTrue(LiveListenPlayer.mayCommand(.appleMusic, active: nil))
        XCTAssertFalse(LiveListenPlayer.mayCommand(.appleMusic, active: .swinsian))
        XCTAssertFalse(LiveListenPlayer.mayCommand(.spotify, active: .appleMusic))
        XCTAssertFalse(LiveListenPlayer.mayCommand(.vlc, active: .vlc))
    }

    func testNothingIsCommandedWithoutASongLunalyrStarted() async throws {
        let player = FakeListenPlayer()
        player.kind = .appleMusic   // the host's song isn't in the library
        player.current = ListenPlayerReading(kind: .appleMusic, title: "Mine", artist: "Me", trackID: "01", position: 0, playing: true)
        let clock = ListenTestClock()
        let (follower, socket) = try await following(player, clock: clock, playing: false)
        await waitUntil { follower.note != nil }
        await deliver(socket, stateText(position: 0, noTrack: true))
        for _ in 0..<3 { await settle(clock, follower) }
        XCTAssertFalse(player.log.contains { $0.hasPrefix("pause") || $0.hasPrefix("play") })
    }

    // MARK: Relay closes

    func testAFullRoomSaysSo() async throws {
        let sockets = FakeListenSockets()
        let follower = ListenFollower(room: "abc123defg", base: base, sockets: sockets, player: FakeListenPlayer())
        var endedWith: ListenFollower.Phase?
        follower.onEnd = { endedWith = $0 }
        follower.start()
        try XCTUnwrap(sockets.opened.first).drop(closeCode: ListenProtocol.roomFullCloseCode)
        await waitUntil { endedWith == .full }
        XCTAssertEqual(sockets.opened.count, 1, "not retried")
        XCTAssertEqual(ListenAlong.endNote(.full, name: "Tommy", connected: false), "That listen-along is full")
        XCTAssertEqual(ListenAlong.endNote(.ended, name: "tommy", connected: true), "Tommy stopped sharing")
        XCTAssertTrue(ListenAlong.endNote(.notFound, name: "a friend", connected: false).contains("another relay"))
    }

    func testTheFollowerIsReleasedWhenItEnds() async throws {
        weak var released: ListenFollower?
        do {
            let follower = ListenFollower(room: "abc123defg", base: base, sockets: FakeListenSockets(), player: FakeListenPlayer())
            follower.onEnd = { [weak follower] _ in _ = follower }
            follower.start()
            follower.stop()
            released = follower
        }
        await waitUntil { released == nil }
    }
}

// MARK: - R2-2: joining is asked about first

@MainActor
final class ListenJoinConsentTests: XCTestCase {
    private func makeListen() -> (ListenAlong, FakeListenSockets, FakeListenConfirmer, FakeListenPlayer) {
        let sockets = FakeListenSockets()
        let confirmer = FakeListenConfirmer()
        let player = FakeListenPlayer()
        let listen = ListenAlong(sockets: sockets, makePlayer: { player }, confirmer: confirmer,
                                 connects: true, observesPlayback: false)
        listen.configure(relay: "https://relay.example.com", sharing: false)
        return (listen, sockets, confirmer, player)
    }

    func testADiscordJoinAsksFirstAndNothingConnectsUntilAccepted() async {
        let (listen, sockets, confirmer, player) = makeListen()
        XCTAssertTrue(listen.requestJoin(discordSecret: "abc123defg|Tommy"))
        await waitUntil { confirmer.isAsking }
        XCTAssertEqual(confirmer.asked.map(\.question), ["Listen along with Tommy?"])
        XCTAssertTrue(sockets.opened.isEmpty, "nothing connects while asking")
        XCTAssertNil(listen.follower)

        // A second click while asking doesn't stack another question.
        listen.requestJoin(discordSecret: "abc123defg|Tommy")
        XCTAssertEqual(confirmer.asked.count, 1)

        confirmer.answer(false)
        await waitUntil { listen.asking == nil }
        XCTAssertTrue(sockets.opened.isEmpty)
        XCTAssertNil(listen.follower)
        XCTAssertFalse(player.everPlayed)

        listen.requestJoin(discordSecret: "abc123defg|Tommy")
        await waitUntil { confirmer.isAsking }
        confirmer.answer(true)
        await waitUntil { listen.follower != nil }
        XCTAssertEqual(sockets.opened.map(\.url.absoluteString), ["wss://relay.example.com/rooms/abc123defg/listen"])
        listen.stopFollowing()
    }

    func testAJoinWithACodeIsAskedAboutToo() async {
        let (listen, sockets, confirmer, _) = makeListen()
        XCTAssertFalse(listen.requestJoin(code: "not a code"))
        XCTAssertTrue(listen.requestJoin(code: "https://relay.example.com/listen/abc123defg"))
        await waitUntil { confirmer.isAsking }
        XCTAssertEqual(confirmer.asked.first?.question, "Listen along with room abc123defg?")
        XCTAssertTrue(sockets.opened.isEmpty)
        confirmer.answer(false)
        await waitUntil { listen.asking == nil }
        XCTAssertNil(listen.follower)
    }

    func testAnOldInviteWithoutANameIsStillAsked() async {
        let (listen, _, confirmer, _) = makeListen()
        listen.requestJoin(discordSecret: "abc123defg")
        await waitUntil { confirmer.isAsking }
        XCTAssertNil(confirmer.asked.first?.hostName)
        confirmer.answer(false)
        await waitUntil { listen.asking == nil }
    }

    func testWithoutARelayNothingIsAsked() async {
        let confirmer = FakeListenConfirmer()
        let listen = ListenAlong(sockets: FakeListenSockets(), makePlayer: { FakeListenPlayer() }, confirmer: confirmer,
                                 connects: true, observesPlayback: false)
        XCTAssertFalse(listen.requestJoin(discordSecret: "abc123defg|Tommy"))
        try? await Task.sleep(nanoseconds: 20_000_000)
        XCTAssertTrue(confirmer.asked.isEmpty)
    }

    func testTheAlertSaysWhoAndWhat() {
        XCTAssertEqual(ListenInvite(room: "abc123defg", hostName: "Tommy").question, "Listen along with Tommy?")
        XCTAssertTrue(ListenInvite.explanation.contains("pauses listening along"))
        XCTAssertEqual(ListenInvite.acceptTitle, "Listen Along")
    }
}

// MARK: - R2-10, R2-15: what the host sends, and Discord

@MainActor
final class ListenSharingPrivacyTests: XCTestCase {
    func testLyricsAreSharedByDefaultAndCanBeTurnedOff() {
        let name = "LunalyrTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: name)!
        defer { defaults.removePersistentDomain(forName: name) }
        let extras = LunalyrExtras(defaults: defaults)
        XCTAssertTrue(extras.listenSharesLyrics)
        extras.listenSharesLyrics = false
        XCTAssertFalse(LunalyrExtras(defaults: defaults).listenSharesLyrics)

        let listen = ListenAlong(sockets: FakeListenSockets(), makePlayer: { FakeListenPlayer() }, confirmer: FakeListenConfirmer(),
                                 connects: true, observesPlayback: false)
        XCTAssertTrue(listen.sharesLyrics)
        listen.configure(relay: "https://relay.example.com", sharing: false, sharesLyrics: false)
        XCTAssertFalse(listen.sharesLyrics)
    }

    func testTurningDiscordOffForgetsTheDisplayName() {
        let name = "LunalyrTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: name)!
        defer { defaults.removePersistentDomain(forName: name) }
        let presence = DiscordPresence(extras: LunalyrExtras(defaults: defaults))
        ListenAlong.shared.discordUserName = "Tommy"
        presence.stop()
        XCTAssertNil(ListenAlong.shared.discordUserName)
    }

    func testAStopAndStartInsideOneReadLeavesOneLoop() async throws {
        let transport = DiscordTransport(connects: false)
        for _ in 0..<4 {
            transport.startListening(for: ["ACTIVITY_JOIN"])
            transport.stopListening()
        }
        transport.startListening(for: ["ACTIVITY_JOIN"])
        try await Task.sleep(nanoseconds: UInt64((DiscordTransport.readInterval * 2.5) * 1e9))
        XCTAssertEqual(transport.runningLoops, 1)
        transport.stopListening()
        try await Task.sleep(nanoseconds: UInt64((DiscordTransport.readInterval * 1.5) * 1e9))
        XCTAssertEqual(transport.runningLoops, 0)
    }

    func testARateLimitedHostWaitsAsLongAsTheRelayAsks() async throws {
        let http = FakeListenHTTP()
        http.reply("https://relay.example.com/rooms", #"{"error":"rate_limited"}"#, status: 429)
        let host = ListenHost(base: URL(string: "https://relay.example.com")!, http: http, sockets: FakeListenSockets(), clock: { 0 })
        let track = ListenProtocol.Track(title: "Song", artist: "Artist", album: nil, duration: 100)
        host.update(ListenHostSnapshot(track: track, playing: true, position: 1))
        await waitUntil { host.retryDelay != nil }
        XCTAssertEqual(host.retryDelay, ListenProtocol.roomCreationRetryAfter)
        XCTAssertEqual(http.requests.count, 1)
        host.end()
    }

    func testAHostWhoseKeyIsRefusedMakesANewRoom() async throws {
        let http = FakeListenHTTP()
        http.reply("https://relay.example.com/rooms", #"{"room":"abc123defg","hostKey":"k"}"#)
        let sockets = FakeListenSockets()
        let host = ListenHost(base: URL(string: "https://relay.example.com")!, http: http, sockets: sockets, clock: { 0 })
        let track = ListenProtocol.Track(title: "Song", artist: "Artist", album: nil, duration: 100)
        host.update(ListenHostSnapshot(track: track, playing: true, position: 1))
        await waitUntil { sockets.opened.count == 1 }
        sockets.opened[0].drop(closeCode: ListenProtocol.badKeyCloseCode)
        await waitUntil { host.room == nil }
        host.end()
    }
}
