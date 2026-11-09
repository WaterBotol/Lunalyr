import AppKit
import XCTest
@testable import VersoCustom

final class ListenProtocolTests: XCTestCase {
    private let track = ListenProtocol.Track(title: "Blinding Lights", artist: "The Weeknd", album: "After Hours",
                                             duration: 200, spotifyId: "0VjIjW4GlUZAMYd2vXMi3b", appleId: 1499378607,
                                             isrc: "USUG11904206")

    private func object(_ data: Data) throws -> [String: Any] {
        try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
    }

    func testStateEncodesEveryFieldOfTheContract() throws {
        let state = ListenProtocol.State(seq: 42, track: track, playing: true, position: 63.2,
                                         lyrics: [.init(start: 12.1, end: 15, text: "I said, ooh")],
                                         host: .init(name: "Tommy"))
        let json = try object(ListenProtocol.encode(state))
        XCTAssertEqual(json["type"] as? String, "state")
        XCTAssertEqual(json["v"] as? Int, 1)
        XCTAssertEqual(json["seq"] as? Int, 42)
        XCTAssertEqual(json["playing"] as? Bool, true)
        XCTAssertEqual(json["position"] as? Double, 63.2)
        XCTAssertNil(json["age"], "only the relay sets age")
        let t = try XCTUnwrap(json["track"] as? [String: Any])
        XCTAssertEqual(t["title"] as? String, "Blinding Lights")
        XCTAssertEqual(t["artist"] as? String, "The Weeknd")
        XCTAssertEqual(t["album"] as? String, "After Hours")
        XCTAssertEqual(t["duration"] as? Double, 200)
        XCTAssertEqual(t["spotifyId"] as? String, "0VjIjW4GlUZAMYd2vXMi3b")
        XCTAssertEqual(t["appleId"] as? Int, 1499378607)
        XCTAssertEqual(t["isrc"] as? String, "USUG11904206")
        let lyrics = try XCTUnwrap(json["lyrics"] as? [[String: Any]])
        XCTAssertEqual(lyrics.first?["start"] as? Double, 12.1)
        XCTAssertEqual(lyrics.first?["end"] as? Double, 15)
        XCTAssertEqual(lyrics.first?["text"] as? String, "I said, ooh")
        XCTAssertEqual((json["host"] as? [String: Any])?["name"] as? String, "Tommy")
    }

    func testNothingPlayingIsAnExplicitNullTrackAndOptionalFieldsAreLeftOut() throws {
        let data = ListenProtocol.encode(.init(seq: 1, track: nil, playing: false, position: 0))
        let json = try object(data)
        XCTAssertTrue(json["track"] is NSNull)
        XCTAssertNil(json["lyrics"])
        XCTAssertNil(json["host"])
        var bare = track
        bare.spotifyId = nil; bare.appleId = nil; bare.isrc = nil; bare.album = nil
        let t = try XCTUnwrap(try object(ListenProtocol.encode(.init(seq: 2, track: bare, playing: true, position: 1)))["track"] as? [String: Any])
        XCTAssertEqual(Set(t.keys), ["title", "artist", "duration"])
    }

    func testStateRoundTripsAndKeepsTheRelaysAge() throws {
        var state = ListenProtocol.State(seq: 7, track: track, playing: true, position: 63.2,
                                         lyrics: [.init(start: 1, end: 2, text: "a")], host: .init(name: "Tommy"))
        state.age = 1500
        XCTAssertEqual(ListenProtocol.decode(ListenProtocol.encode(state)), .state(state))
    }

    func testOtherMessagesDecode() {
        XCTAssertEqual(ListenProtocol.decode(#"{"type":"listeners","count":3}"#), .listeners(3))
        XCTAssertEqual(ListenProtocol.decode(#"{"type":"ended"}"#), .ended)
        XCTAssertEqual(ListenProtocol.decode(#"{"type":"hello","x":1}"#), .other("hello"))
        XCTAssertNil(ListenProtocol.decode("not json"))
        XCTAssertNil(ListenProtocol.decode(#"{"count":3}"#))
        XCTAssertNil(ListenProtocol.decode(#"{"type":"state","playing":true}"#), "a state needs a position")
    }

    func testARelayStateWithANullTrackDecodes() {
        let text = #"{"type":"state","v":1,"seq":3,"track":null,"playing":false,"position":0,"age":250}"#
        guard case .state(let state)? = ListenProtocol.decode(text) else { return XCTFail("not a state") }
        XCTAssertNil(state.track)
        XCTAssertEqual(state.age, 250)
        XCTAssertEqual(state.seq, 3)
    }

    func testABadSpotifyIDFromTheRelayIsDropped() {
        let text = #"{"type":"state","seq":1,"track":{"title":"x","artist":"y","duration":10,"spotifyId":"a\" & quit"},"playing":true,"position":1}"#
        guard case .state(let state)? = ListenProtocol.decode(text) else { return XCTFail("not a state") }
        XCTAssertNil(state.track?.spotifyId)
    }

    func testRoomCreationReply() {
        XCTAssertEqual(ListenProtocol.roomCreated(from: Data(#"{"room":"abc123defg","hostKey":"0123456789abcdef0123456789abcdef"}"#.utf8)),
                       .init(room: "abc123defg", hostKey: "0123456789abcdef0123456789abcdef"))
        XCTAssertNil(ListenProtocol.roomCreated(from: Data(#"{"room":"../x","hostKey":"k"}"#.utf8)))
        XCTAssertNil(ListenProtocol.roomCreated(from: Data("oops".utf8)))
    }

    func testRoomsComeFromCodesAndLinks() {
        XCTAssertEqual(ListenProtocol.room(fromInput: " ABC123DEFG "), "abc123defg")
        XCTAssertEqual(ListenProtocol.room(fromInput: "https://relay.example.com/listen/abc123defg"), "abc123defg")
        XCTAssertEqual(ListenProtocol.room(fromInput: "https://relay.example.com/rooms/abc123defg/state"), "abc123defg")
        XCTAssertNil(ListenProtocol.room(fromInput: "abc"))
        XCTAssertNil(ListenProtocol.room(fromInput: "https://relay.example.com/listen/abc-123"))
    }

    func testSpotifyIDComesFromATrackURIOnly() {
        XCTAssertEqual(ListenProtocol.spotifyID(fromURI: "spotify:track:0VjIjW4GlUZAMYd2vXMi3b"), "0VjIjW4GlUZAMYd2vXMi3b")
        XCTAssertNil(ListenProtocol.spotifyID(fromURI: "spotify:local:Artist:Album:Song:200"))
        XCTAssertNil(ListenProtocol.spotifyID(fromURI: "spotify:episode:abc"))
        XCTAssertNil(ListenProtocol.spotifyID(fromURI: "0123456789ABCDEF"))
        XCTAssertNil(ListenProtocol.spotifyID(fromURI: nil))
    }

    func testAddresses() throws {
        XCTAssertNil(ListenProtocol.base(""))
        XCTAssertNil(ListenProtocol.base("   "))
        XCTAssertNil(ListenProtocol.base("ftp://relay.example.com"))
        let base = try XCTUnwrap(ListenProtocol.base("relay.example.com/"))
        XCTAssertEqual(base.absoluteString, "https://relay.example.com")
        XCTAssertEqual(ListenProtocol.roomsURL(base).absoluteString, "https://relay.example.com/rooms")
        XCTAssertEqual(ListenProtocol.hostSocketURL(base, room: "abc123defg", key: "k1")?.absoluteString,
                       "wss://relay.example.com/rooms/abc123defg/host?key=k1")
        XCTAssertEqual(ListenProtocol.listenSocketURL(base, room: "abc123defg").absoluteString,
                       "wss://relay.example.com/rooms/abc123defg/listen")
        XCTAssertEqual(ListenProtocol.webPageURL(base, room: "abc123defg").absoluteString,
                       "https://relay.example.com/listen/abc123defg")
        let local = try XCTUnwrap(ListenProtocol.base("http://127.0.0.1:8787"))
        XCTAssertEqual(ListenProtocol.listenSocketURL(local, room: "abc123defg").absoluteString,
                       "ws://127.0.0.1:8787/rooms/abc123defg/listen")
    }

    // MARK: Timing

    func testPositionCountsTheRelaysAgeAndTheTimeSinceArrival() {
        var state = ListenProtocol.State(seq: 1, track: track, playing: true, position: 63.2)
        state.age = 1500
        XCTAssertEqual(ListenClock.position(of: state, receivedAt: 100, now: 100), 64.7, accuracy: 0.0001)
        XCTAssertEqual(ListenClock.position(of: state, receivedAt: 100, now: 104), 68.7, accuracy: 0.0001)
        state.age = nil
        XCTAssertEqual(ListenClock.position(of: state, receivedAt: 100, now: 102), 65.2, accuracy: 0.0001)
    }

    func testAPausedPositionStaysPut() {
        var state = ListenProtocol.State(seq: 1, track: track, playing: false, position: 63.2)
        state.age = 60_000
        XCTAssertEqual(ListenClock.position(of: state, receivedAt: 100, now: 130), 63.2)
    }

    func testPositionStopsAtTheEndOfTheSong() {
        var state = ListenProtocol.State(seq: 1, track: track, playing: true, position: 199)
        state.age = 30_000
        XCTAssertEqual(ListenClock.position(of: state, receivedAt: 0, now: 50), 200)
    }

    func testDriftAboveASecondAndAHalfSeeks() {
        XCTAssertEqual(ListenDrift.decide(expected: 60, actual: 59), .none)
        XCTAssertEqual(ListenDrift.decide(expected: 60, actual: 61.5), .none, "exactly at the threshold is close enough")
        XCTAssertEqual(ListenDrift.decide(expected: 60, actual: 58.4), .seek(to: 60))
        XCTAssertEqual(ListenDrift.decide(expected: 60, actual: 64), .seek(to: 60))
        XCTAssertEqual(ListenDrift.decide(expected: .nan, actual: 3), .none)
    }

    func testHostSendsOnChangesAndAHeartbeat() {
        var schedule = ListenHostSchedule()
        let playing = ListenHostSchedule.Sample(track: track, playing: true, position: 10)
        XCTAssertEqual(schedule.reason(for: playing, now: 0), .first)
        XCTAssertEqual(schedule.sent(playing, now: 0, withLyrics: true), 1)
        // Moving on with the music says nothing new.
        XCTAssertNil(schedule.reason(for: .init(track: track, playing: true, position: 15), now: 5))
        XCTAssertEqual(schedule.reason(for: .init(track: track, playing: true, position: 20), now: 10), .heartbeat)
        XCTAssertEqual(schedule.reason(for: .init(track: track, playing: false, position: 15), now: 5), .playPause)
        XCTAssertEqual(schedule.reason(for: .init(track: track, playing: true, position: 40), now: 5), .seek)
        XCTAssertNil(schedule.reason(for: .init(track: track, playing: true, position: 16.4), now: 5), "under 1.5 s is not a seek")
        var next = track
        next.title = "Save Your Tears"; next.spotifyId = "5QO79kh1waicV47BqGRL3g"
        XCTAssertEqual(schedule.reason(for: .init(track: next, playing: true, position: 0), now: 1), .track)
        XCTAssertEqual(schedule.reason(for: .init(track: nil, playing: false, position: 0), now: 1), .track)
    }

    func testLyricsGoOncePerSong() {
        var schedule = ListenHostSchedule()
        XCTAssertTrue(schedule.needsLyrics(for: track))
        _ = schedule.sent(.init(track: track, playing: true, position: 0), now: 0, withLyrics: true)
        XCTAssertFalse(schedule.needsLyrics(for: track))
        var next = track
        next.title = "Save Your Tears"; next.spotifyId = "5QO79kh1waicV47BqGRL3g"
        XCTAssertTrue(schedule.needsLyrics(for: next))
        XCTAssertFalse(schedule.needsLyrics(for: nil))
        schedule.reset()
        XCTAssertTrue(schedule.needsLyrics(for: track), "a new socket gets them again")
    }

    func testBackoffDoublesUpToAMinute() {
        XCTAssertEqual((1...8).map(ListenBackoff.delay(afterFailures:)), [1, 2, 4, 8, 16, 32, 60, 60])
    }

    func testLyricLinesSkipBreaksAndFollowTheUsersOffset() {
        let lines = [
            LyricsFetcher.Line(start: 10, end: 12, text: "first", words: [], isInstrumental: false),
            LyricsFetcher.Line(start: 12, end: 20, text: "", words: [], isInstrumental: true),
            LyricsFetcher.Line(start: 20, end: 24, text: " second ", words: [], isInstrumental: false),
        ]
        XCTAssertEqual(ListenAlong.lines(lines, shift: 0.5),
                       [.init(start: 9.5, end: 11.5, text: "first"), .init(start: 19.5, end: 23.5, text: "second")])
    }
}

// MARK: - Discord

final class DiscordListenAlongTests: XCTestCase {
    private func frame(_ json: String) -> Data { Data(json.utf8) }

    func testJoinEventsParse() {
        XCTAssertEqual(DiscordPresence.event(fromFrame: frame(#"{"cmd":"DISPATCH","evt":"ACTIVITY_JOIN","data":{"secret":"abc123defg"}}"#)),
                       .join(secret: "abc123defg"))
        XCTAssertEqual(DiscordPresence.event(fromFrame: frame(#"{"cmd":"DISPATCH","evt":"ACTIVITY_JOIN_REQUEST","data":{"user":{"id":"53908232506183680","username":"mason","global_name":"Mason","avatar":"a"}}}"#)),
                       .joinRequest(userID: "53908232506183680", name: "Mason"))
        XCTAssertEqual(DiscordPresence.event(fromFrame: frame(#"{"cmd":"DISPATCH","evt":"READY","data":{"v":1,"user":{"id":"1","username":"tommy","global_name":null}}}"#)),
                       .ready(userName: "tommy"))
        XCTAssertEqual(DiscordPresence.event(fromFrame: frame(#"{"cmd":"SET_ACTIVITY","evt":"ERROR","data":{"code":4000,"message":"secrets cannot currently be sent with buttons"}}"#)),
                       .error(code: 4000, message: "secrets cannot currently be sent with buttons"))
    }

    func testOtherFramesAreIgnored() {
        XCTAssertNil(DiscordPresence.event(fromFrame: frame(#"{"cmd":"SET_ACTIVITY","data":{},"evt":null,"nonce":"x"}"#)))
        XCTAssertNil(DiscordPresence.event(fromFrame: frame(#"{"cmd":"SUBSCRIBE","data":{"evt":"ACTIVITY_JOIN"},"evt":null}"#)))
        XCTAssertNil(DiscordPresence.event(fromFrame: frame(#"{"cmd":"DISPATCH","evt":"ACTIVITY_JOIN","data":{}}"#)))
        XCTAssertNil(DiscordPresence.event(fromFrame: frame("garbage")))
    }

    func testSubscribeAndAcceptPayloads() {
        let subscribe = DiscordPresence.subscribePayload("ACTIVITY_JOIN", nonce: "n")
        XCTAssertEqual(subscribe["cmd"] as? String, "SUBSCRIBE")
        XCTAssertEqual(subscribe["evt"] as? String, "ACTIVITY_JOIN")
        XCTAssertEqual(subscribe["nonce"] as? String, "n")
        XCTAssertNotNil(DiscordPresence.frame(.frame, subscribe))
        let accept = DiscordPresence.acceptJoinPayload(userID: "42", nonce: "m")
        XCTAssertEqual(accept["cmd"] as? String, "SEND_ACTIVITY_JOIN_INVITE")
        XCTAssertEqual((accept["args"] as? [String: String])?["user_id"], "42")
        XCTAssertEqual(DiscordPresence.joinEvents, ["ACTIVITY_JOIN", "ACTIVITY_JOIN_REQUEST"])
    }

    @MainActor
    private func activity(party: DiscordPresence.ListenParty?, join: Bool, songButton: Bool = true) -> [String: Any] {
        var options = DiscordOptions()
        options.offersJoin = join
        options.showsSongButton = songButton
        let playing = DiscordPresence.Playing(title: "Song", artist: "Artist", album: nil, line: nil, start: 0, end: 100,
                                              songURL: "https://song.link/s/abc", options: options, party: party)
        return DiscordPresence.activity(for: playing)
    }

    @MainActor
    func testWithoutSharingTheButtonsAreUnchanged() throws {
        let activity = activity(party: nil, join: true)
        XCTAssertNil(activity["party"])
        XCTAssertNil(activity["secrets"])
        let buttons = try XCTUnwrap(activity["buttons"] as? [[String: String]])
        XCTAssertEqual(buttons.map { $0["label"] }, ["Play this song", "Get Lunalyr"])
    }

    @MainActor
    func testSharingByDefaultKeepsGetLunalyrAndLinksTheWebPage() throws {
        let party = DiscordPresence.ListenParty(room: "abc123defg", listeners: 2, webURL: "https://relay.example.com/listen/abc123defg")
        let activity = activity(party: party, join: false)
        XCTAssertNil(activity["secrets"], "Discord refuses secrets with buttons")
        XCTAssertNil(activity["party"])
        let buttons = try XCTUnwrap(activity["buttons"] as? [[String: String]])
        XCTAssertEqual(buttons.map { $0["label"] }, ["Listen along", "Get Lunalyr"])
        XCTAssertEqual(buttons.first?["url"], "https://relay.example.com/listen/abc123defg")
    }

    @MainActor
    func testJoinOptInSendsPartyAndSecretWithoutButtons() throws {
        let party = DiscordPresence.ListenParty(room: "abc123defg", listeners: 2, webURL: "https://relay.example.com/listen/abc123defg")
        let activity = activity(party: party, join: true)
        XCTAssertNil(activity["buttons"], "secrets and buttons never go together")
        let sentParty = try XCTUnwrap(activity["party"] as? [String: Any])
        XCTAssertEqual(sentParty["id"] as? String, "abc123defg")
        XCTAssertEqual(sentParty["size"] as? [Int], [3, 51])
        XCTAssertEqual(activity["secrets"] as? [String: String], ["join": "abc123defg"])
        XCTAssertNotNil(DiscordPresence.frame(.frame, DiscordPresence.setActivityPayload(activity, pid: 1, nonce: "n")))
    }

    @MainActor
    func testPartySizeStaysWithinTheRoomsLimit() throws {
        let party = DiscordPresence.ListenParty(room: "abc123defg", listeners: 80, webURL: nil)
        let sentParty = try XCTUnwrap(activity(party: party, join: true)["party"] as? [String: Any])
        XCTAssertEqual(sentParty["size"] as? [Int], [51, 51])
    }
}

// MARK: - Matching and the follower's player

final class ListenMatchingTests: XCTestCase {
    func testTitlesAreComparedWithoutDecorations() {
        XCTAssertEqual(ListenTrackMatching.normalized("Blinding Lights"), "blindinglights")
        XCTAssertEqual(ListenTrackMatching.normalized("Here Comes the Sun - Remastered 2009"), "herecomesthesun")
        XCTAssertEqual(ListenTrackMatching.normalized("Señorita (feat. Camila Cabello)"), "senorita")
        XCTAssertEqual(ListenTrackMatching.searchTerm("Señorita (feat. Camila Cabello)"), "Señorita")
        XCTAssertEqual(ListenTrackMatching.searchTerm("(Intro)"), "(Intro)")
    }

    func testTheBestLibraryMatchIsTheSameSongByTheArtistNearestInLength() {
        let candidates = [
            LibraryCandidate(persistentID: "A", title: "Blinding Lights", artist: "Someone Else", duration: 200),
            LibraryCandidate(persistentID: "B", title: "Blinding Lights (Live)", artist: "The Weeknd", duration: 260),
            LibraryCandidate(persistentID: "C", title: "Blinding Lights", artist: "The Weeknd", duration: 201),
            LibraryCandidate(persistentID: "D", title: "Blinding Lights", artist: "The Weeknd & Rosalía", duration: 230),
        ]
        XCTAssertEqual(ListenTrackMatching.best(candidates, title: "Blinding Lights", artist: "The Weeknd", duration: 200)?.persistentID, "C")
        XCTAssertNil(ListenTrackMatching.best(candidates, title: "Save Your Tears", artist: "The Weeknd", duration: 215))
        XCTAssertNil(ListenTrackMatching.best([candidates[0]], title: "Blinding Lights", artist: "The Weeknd", duration: 200))
    }

    func testTargetsMatchTheRightPlayerAndID() {
        let spotify = ListenPlayerReading(kind: .spotify, title: "", artist: "", trackID: "spotify:track:abc", position: 0, playing: true)
        XCTAssertTrue(ListenPlayTarget.spotify(id: "abc").matches(spotify))
        XCTAssertFalse(ListenPlayTarget.spotify(id: "abd").matches(spotify))
        let music = ListenPlayerReading(kind: .appleMusic, title: "", artist: "", trackID: "00000000000000AB", position: 0, playing: true)
        XCTAssertTrue(ListenPlayTarget.library(persistentID: "ab").matches(music))
        XCTAssertFalse(ListenPlayTarget.library(persistentID: "ab").matches(spotify))
    }

    func testScriptsQuoteWhatTheyAreGivenAndNeverLaunchAPlayer() {
        let play = LiveListenPlayer.playScript(.library(persistentID: "A\"B"), at: 63.25, playing: false).source
        XCTAssertTrue(play.hasPrefix("if application id \"com.apple.Music\" is not running then return \"notRunning\""))
        XCTAssertTrue(play.contains(#"whose persistent ID is "A\"B""#))
        XCTAssertTrue(play.contains("set player position to 63.250"))
        XCTAssertTrue(play.contains("\npause"))
        let spotify = LiveListenPlayer.playScript(.spotify(id: "abc"), at: 1, playing: true).source
        XCTAssertTrue(spotify.contains(#"play track "spotify:track:abc""#))
        XCTAssertFalse(spotify.contains("\npause"))
        let search = LiveListenPlayer.librarySearchScript("Back \"Last\" Stop").source
        XCTAssertTrue(search.contains(#"for "Back \"Last\" Stop" only songs"#))
        XCTAssertTrue(LiveListenPlayer.setShuffleScript(true, .spotify).source.contains("set shuffling to true"))
        XCTAssertTrue(LiveListenPlayer.setShuffleScript(false, .appleMusic).source.contains("set shuffle enabled to false"))
        // Reserved words used as names break a script: none are.
        for source in [play, spotify, search] {
            for word in ["set back ", "set last ", "set stop ", " back to", " last to"] {
                XCTAssertFalse(source.contains(word), word)
            }
        }
    }

    func testLibraryReplyParses() {
        let row = NSAppleEventDescriptor.scriptList([
            NSAppleEventDescriptor(string: "00000000000000AB"), NSAppleEventDescriptor(string: "Song"),
            NSAppleEventDescriptor(string: "Artist"), NSAppleEventDescriptor(double: 201.5),
        ])
        let reply = NSAppleEventDescriptor.scriptList([NSAppleEventDescriptor(string: "ok"), .scriptList([row])])
        XCTAssertEqual(LiveListenPlayer.parseLibrary(reply),
                       [LibraryCandidate(persistentID: "00000000000000AB", title: "Song", artist: "Artist", duration: 201.5)])
        XCTAssertEqual(LiveListenPlayer.parseLibrary(NSAppleEventDescriptor.scriptList([NSAppleEventDescriptor(string: "notRunning")])), [])
    }
}

// MARK: - Following and hosting, end to end over fakes

@MainActor
final class ListenSessionTests: XCTestCase {
    private let base = URL(string: "https://relay.example.com")!
    private let track = ListenProtocol.Track(title: "Blinding Lights", artist: "The Weeknd", album: "After Hours",
                                             duration: 200, spotifyId: "0VjIjW4GlUZAMYd2vXMi3b", appleId: 1499378607)

    private func stateText(playing: Bool = true, position: Double, age: Double? = nil, track: ListenProtocol.Track? = nil,
                           lyrics: [ListenProtocol.LyricLine]? = nil) -> String {
        var state = ListenProtocol.State(seq: 1, track: track ?? self.track, playing: playing, position: position,
                                         lyrics: lyrics, host: .init(name: "Tommy"))
        state.age = age
        return String(decoding: ListenProtocol.encode(state), as: UTF8.self)
    }

    private func makeFollower(_ player: FakeListenPlayer, clock: ListenTestClock) -> (ListenFollower, FakeListenSockets) {
        let sockets = FakeListenSockets()
        let follower = ListenFollower(room: "abc123defg", base: base, sockets: sockets, player: player,
                                      clock: { clock.now })
        return (follower, sockets)
    }

    func testFollowerPlaysTheSongAtTheHostsMomentAndCorrectsDrift() async throws {
        let player = FakeListenPlayer()
        let clock = ListenTestClock()
        let (follower, sockets) = makeFollower(player, clock: clock)
        follower.start()
        let socket = try XCTUnwrap(sockets.opened.first)
        XCTAssertEqual(socket.url.absoluteString, "wss://relay.example.com/rooms/abc123defg/listen")

        socket.push(stateText(position: 60, age: 500, lyrics: [.init(start: 1, end: 2, text: "line")]))
        await waitUntil { player.log.contains { $0.hasPrefix("play spotify") } }
        XCTAssertEqual(player.log.first { $0.hasPrefix("play") }, "play spotify(id: \"0VjIjW4GlUZAMYd2vXMi3b\") at 60.5 playing")
        XCTAssertEqual(follower.phase, .following)
        XCTAssertEqual(follower.displayName, "Tommy")
        XCTAssertEqual(follower.lyrics.count, 1)
        await waitUntil { player.log.contains("shuffle off") }

        // Five seconds on, the player hasn't moved: more than 1.5 s out.
        clock.advance(5)
        follower.sync()
        await waitUntil { player.log.contains("seek 65.5") }

        // A second later and a second out is close enough.
        clock.advance(4)
        player.current?.position = 68.5
        let before = player.log.count
        follower.sync()
        try await Task.sleep(nanoseconds: 20_000_000)
        XCTAssertEqual(player.log.count, before)

        // A heartbeat without lyrics keeps the ones already sent.
        socket.push(stateText(position: 70))
        try await Task.sleep(nanoseconds: 20_000_000)
        XCTAssertEqual(follower.lyrics.count, 1)
    }

    func testFollowerPausesWithTheHostAndGivesShuffleBackAtTheEnd() async throws {
        let player = FakeListenPlayer()
        let clock = ListenTestClock()
        let (follower, sockets) = makeFollower(player, clock: clock)
        var endedWith: ListenFollower.Phase?
        follower.onEnd = { endedWith = $0 }
        follower.start()
        let socket = try XCTUnwrap(sockets.opened.first)
        socket.push(stateText(position: 10))
        await waitUntil { player.current != nil && player.log.contains("shuffle off") }
        clock.advance(5)
        socket.push(stateText(playing: false, position: 15))
        await waitUntil { player.log.contains("pause") }

        socket.push(#"{"type":"ended"}"#)
        await waitUntil { endedWith == .ended }
        await waitUntil { player.log.last == "shuffle on" }
        XCTAssertTrue(player.shuffleOn)
        XCTAssertTrue(socket.isClosed)
    }

    func testAShuffleThatWasOffStaysOff() async throws {
        let player = FakeListenPlayer()
        player.shuffleOn = false
        let clock = ListenTestClock()
        let (follower, sockets) = makeFollower(player, clock: clock)
        follower.start()
        try XCTUnwrap(sockets.opened.first).push(stateText(position: 10))
        await waitUntil { player.current != nil }
        follower.stop()
        try await Task.sleep(nanoseconds: 20_000_000)
        XCTAssertFalse(player.log.contains { $0.hasPrefix("shuffle") })
    }

    func testASongTheFollowerCantPlayLeavesANoteAndKeepsFollowing() async throws {
        let player = FakeListenPlayer()
        player.kind = .appleMusic
        player.library = [LibraryCandidate(persistentID: "01", title: "Blinding Lights", artist: "Someone Else", duration: 200)]
        let clock = ListenTestClock()
        let (follower, sockets) = makeFollower(player, clock: clock)
        follower.start()
        let socket = try XCTUnwrap(sockets.opened.first)
        socket.push(stateText(position: 10))
        await waitUntil { follower.note != nil }
        XCTAssertEqual(follower.note, "\u{201C}Blinding Lights\u{201D} isn\u{2019}t in your Music library \u{2014} still following")
        XCTAssertEqual(follower.songPageURL?.absoluteString, "https://song.link/s/0VjIjW4GlUZAMYd2vXMi3b")
        XCTAssertEqual(player.log.filter { $0.hasPrefix("search") }, ["search Blinding Lights"])
        XCTAssertFalse(player.log.contains { $0.hasPrefix("play") })
        XCTAssertEqual(follower.phase, .following)

        // The next song is in the library.
        player.library = [LibraryCandidate(persistentID: "00000000000000AB", title: "Save Your Tears", artist: "The Weeknd", duration: 215)]
        var next = track
        next.title = "Save Your Tears"; next.spotifyId = nil; next.appleId = nil; next.duration = 215
        socket.push(stateText(position: 3, track: next))
        await waitUntil { player.log.contains { $0.hasPrefix("play library") } }
        XCTAssertNil(follower.note)
        XCTAssertNil(follower.songPageURL)
        XCTAssertEqual(follower.target, .library(persistentID: "00000000000000AB"))
    }

    func testASpotifyFollowerWithoutTheHostsSpotifyIDGetsANoteAndTheSongPage() async throws {
        let player = FakeListenPlayer()
        let (follower, sockets) = makeFollower(player, clock: ListenTestClock())
        follower.start()
        var musicOnly = track
        musicOnly.spotifyId = nil
        try XCTUnwrap(sockets.opened.first).push(stateText(position: 10, track: musicOnly))
        await waitUntil { follower.note != nil }
        XCTAssertEqual(follower.note, "\u{201C}Blinding Lights\u{201D} isn\u{2019}t on your Spotify \u{2014} still following")
        XCTAssertEqual(follower.songPageURL?.absoluteString, "https://song.link/i/1499378607")
        XCTAssertFalse(player.log.contains { $0.hasPrefix("play") })
    }

    func testWithNoPlayerOpenItWaitsAndPlaysOnceOneIs() async throws {
        let player = FakeListenPlayer()
        player.kind = nil
        let (follower, sockets) = makeFollower(player, clock: ListenTestClock())
        follower.start()
        try XCTUnwrap(sockets.opened.first).push(stateText(position: 10))
        await waitUntil { follower.note == "Open Music or Spotify to listen along" }
        player.kind = .spotify
        follower.sync()
        await waitUntil { player.log.contains { $0.hasPrefix("play spotify") } }
        XCTAssertNil(follower.note)
    }

    func testTheLivePlayerSendsNothingFromATestHost() async {
        let live = LiveListenPlayer()
        let played = await live.play(.spotify(id: "abc"), at: 1, playing: true)
        XCTAssertFalse(played)
        let shuffle = await live.shuffle(.appleMusic)
        XCTAssertNil(shuffle)
        let found = await live.searchLibrary(title: "Song")
        XCTAssertEqual(found, [])
        ListenAlong.shared.configure(relay: "https://relay.example.com", sharing: true)
        XCTAssertFalse(ListenAlong.shared.isAvailable, "a test host never connects to a relay")
        XCTAssertNil(ListenAlong.shared.host)
    }

    func testAnUnknownRoomEndsFollowing() async throws {
        let player = FakeListenPlayer()
        let (follower, sockets) = makeFollower(player, clock: ListenTestClock())
        var endedWith: ListenFollower.Phase?
        follower.onEnd = { endedWith = $0 }
        follower.start()
        try XCTUnwrap(sockets.opened.first).drop(unknownRoom: true)
        await waitUntil { endedWith == .notFound }
    }

    func testHostMakesARoomSendsLyricsOnceCountsListenersAndEnds() async throws {
        let http = FakeListenHTTP()
        http.reply("https://relay.example.com/rooms", #"{"room":"abc123defg","hostKey":"0123456789abcdef0123456789abcdef"}"#)
        let sockets = FakeListenSockets()
        let clock = ListenTestClock()
        let host = ListenHost(base: base, http: http, sockets: sockets, clock: { clock.now })

        host.update(ListenHostSnapshot(track: nil, playing: false, position: 0))
        try await Task.sleep(nanoseconds: 20_000_000)
        XCTAssertTrue(http.requests.isEmpty, "no room until something plays")

        let lyrics = [ListenProtocol.LyricLine(start: 12.1, end: 15, text: "I said, ooh")]
        host.update(ListenHostSnapshot(track: track, playing: true, position: 10, lyrics: lyrics, hostName: "Tommy"))
        await waitUntil { sockets.opened.first?.sent.isEmpty == false }
        XCTAssertEqual(http.requests, ["POST https://relay.example.com/rooms"])
        XCTAssertEqual(host.room, "abc123defg")
        let socket = try XCTUnwrap(sockets.opened.first)
        XCTAssertEqual(socket.url.absoluteString, "wss://relay.example.com/rooms/abc123defg/host?key=0123456789abcdef0123456789abcdef")
        guard case .state(let first)? = ListenProtocol.decode(socket.sent[0]) else { return XCTFail("not a state") }
        XCTAssertEqual(first.lyrics, lyrics)
        XCTAssertEqual(first.host?.name, "Tommy")

        socket.push(#"{"type":"listeners","count":3}"#)
        await waitUntil { host.listeners == 3 }

        clock.advance(4)
        host.update(ListenHostSnapshot(track: track, playing: true, position: 14, lyrics: lyrics))
        try await Task.sleep(nanoseconds: 20_000_000)
        XCTAssertEqual(socket.sent.count, 1, "nothing changed")

        clock.advance(6)
        host.update(ListenHostSnapshot(track: track, playing: true, position: 20, lyrics: lyrics))
        await waitUntil { socket.sent.count == 2 }
        guard case .state(let heartbeat)? = ListenProtocol.decode(socket.sent[1]) else { return XCTFail("not a state") }
        XCTAssertNil(heartbeat.lyrics, "lyrics go once per song")
        XCTAssertEqual(heartbeat.seq, 2)

        clock.advance(1)
        host.update(ListenHostSnapshot(track: track, playing: false, position: 21, lyrics: lyrics))
        await waitUntil { socket.sent.count == 3 }

        host.end()
        await waitUntil { socket.sent.last == #"{"type":"end"}"# }
        await waitUntil { socket.isClosed }
    }

    func testHostWhoseRoomExpiredMakesANewOne() async throws {
        let http = FakeListenHTTP()
        http.reply("https://relay.example.com/rooms", #"{"room":"abc123defg","hostKey":"k"}"#)
        let sockets = FakeListenSockets()
        let host = ListenHost(base: base, http: http, sockets: sockets, clock: { 0 })
        host.update(ListenHostSnapshot(track: track, playing: true, position: 1))
        await waitUntil { sockets.opened.count == 1 }
        sockets.opened[0].drop(unknownRoom: true)
        await waitUntil { host.room == nil }
        XCTAssertFalse(host.connected)
        host.end()
    }
}

// MARK: - Settings

@MainActor
final class ListenAlongSettingsTests: XCTestCase {
    func testListenAlongIsOffAndHiddenByDefault() {
        let name = "LunalyrTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: name)!
        defer { defaults.removePersistentDomain(forName: name) }
        let extras = LunalyrExtras(defaults: defaults)
        XCTAssertFalse(extras.listenSharing)
        XCTAssertEqual(extras.listenRelayURL, "")
        XCTAssertFalse(extras.listenAlongAvailable)
        XCTAssertFalse(extras.discordOffersJoin)
        XCTAssertFalse(extras.discordOptions.offersJoin)
        extras.listenRelayURL = "relay.example.com"
        extras.discordOffersJoin = true
        XCTAssertTrue(extras.listenAlongAvailable)
        let reread = LunalyrExtras(defaults: defaults)
        XCTAssertEqual(reread.listenRelayURL, "relay.example.com")
        XCTAssertTrue(reread.discordOptions.offersJoin)
    }
}
