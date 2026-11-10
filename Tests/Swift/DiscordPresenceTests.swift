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

    func testBothButtonsShowWithThePlaySongButtonFirst() throws {
        var playing = DiscordPresence.Playing(title: "Song", artist: "Artist", album: nil, line: nil, start: 0, end: nil)
        playing.songURL = "https://song.link/i/123"
        let buttons = try XCTUnwrap(DiscordPresence.activity(for: playing)["buttons"] as? [[String: String]])
        XCTAssertEqual(buttons.map { $0["label"] }, ["Play this song", "Get Lunalyr"])
        XCTAssertEqual(buttons.first?["url"], "https://song.link/i/123")
        XCTAssertEqual(buttons.last?["url"], DiscordPresence.getLunalyrURL)
    }

    func testGetLunalyrStaysWhenTheSongIsNotFoundOrItsButtonIsOff() throws {
        var playing = DiscordPresence.Playing(title: "Song", artist: "Artist", album: nil, line: nil, start: 0, end: nil)
        var buttons = try XCTUnwrap(DiscordPresence.activity(for: playing)["buttons"] as? [[String: String]])
        XCTAssertEqual(buttons.map { $0["label"] }, ["Get Lunalyr"])
        playing.songURL = "https://song.link/i/123"
        playing.options.showsSongButton = false
        buttons = try XCTUnwrap(DiscordPresence.activity(for: playing)["buttons"] as? [[String: String]])
        XCTAssertEqual(buttons.map { $0["label"] }, ["Get Lunalyr"])
        playing.options.showsButton = false
        XCTAssertNil(DiscordPresence.activity(for: playing)["buttons"])
    }

    func testSongLinkPrefersSpotifyThenApple() {
        XCTAssertEqual(DiscordPresence.songLink(spotifyURI: "spotify:track:0VjIjW4GlUZAMYd2vXMi3b", appleTrackID: 5),
                       "https://song.link/s/0VjIjW4GlUZAMYd2vXMi3b")
        XCTAssertEqual(DiscordPresence.songLink(spotifyURI: "spotify:local:a:b:c:1", appleTrackID: 1440857781),
                       "https://song.link/i/1440857781")
        XCTAssertEqual(DiscordPresence.songLink(spotifyURI: nil, appleTrackID: 42), "https://song.link/i/42")
        XCTAssertNil(DiscordPresence.songLink(spotifyURI: nil, appleTrackID: nil))
        XCTAssertNil(DiscordPresence.songLink(spotifyURI: "spotify:track:", appleTrackID: 0))
    }

    func testSearchMatchCarriesTheAppleTrackID() {
        let reply = """
        {"results":[{"trackId":111,"trackName":"Other","artistName":"Someone","artworkUrl100":"https://a/1/100x100bb.jpg"},
                    {"trackId":1440857781,"trackName":"Golden Hour Static","artistName":"Lunar Tides","artworkUrl100":"https://a/2/100x100bb.jpg"}]}
        """
        let match = DiscordPresence.searchMatch(fromSearch: Data(reply.utf8), title: "Golden Hour Static", artist: "Lunar Tides")
        XCTAssertEqual(match, DiscordPresence.SearchMatch(cover: "https://a/2/512x512bb.jpg", trackID: 1440857781))
    }

    func testANearMissGivesACoverButNoPublicLink() {
        let reply = """
        {"results":[{"trackId":1,"trackName":"Love","artistName":"Taylor Swift","artworkUrl100":"https://a/1/100x100bb.jpg"},
                    {"trackId":2,"trackName":"Love Story (Live)","artistName":"Taylor Swift","artworkUrl100":"https://a/2/100x100bb.jpg"},
                    {"trackId":3,"trackName":"Love Story","artistName":"Taylor Swift Tribute Band","artworkUrl100":"https://a/3/100x100bb.jpg"}]}
        """
        let match = DiscordPresence.searchMatch(fromSearch: Data(reply.utf8), title: "Love Story", artist: "Taylor Swift")
        XCTAssertEqual(match?.cover, "https://a/1/512x512bb.jpg", "a loose match is still fine for the cover")
        XCTAssertNil(match?.trackID, "none of these is the song, so no link is published")
        XCTAssertNil(DiscordPresence.songLink(spotifyURI: nil, appleTrackID: match?.trackID))
    }

    func testTheLinkNeedsTheSameTitleAndPrimaryArtist() {
        let reply = """
        {"results":[{"trackId":7,"trackName":"This Is What You Came For","artistName":"Calvin Harris & Rihanna","artworkUrl100":"https://a/7/100x100bb.jpg"}]}
        """
        let match = DiscordPresence.searchMatch(fromSearch: Data(reply.utf8), title: "This Is What You Came For",
                                                artist: "Calvin Harris feat. Rihanna")
        XCTAssertEqual(match?.trackID, 7)
        XCTAssertEqual(DiscordPresence.primaryArtist("Calvin Harris feat. Rihanna"), "calvinharris")
        XCTAssertEqual(DiscordPresence.primaryArtist("Beyoncé"), "beyonce")
    }

    func testWithTheCoverOffAppleIsNotAskedUnlessTheSongButtonWasTurnedOn() {
        let name = "LunalyrTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: name)!
        defer { defaults.removePersistentDomain(forName: name) }
        defaults.set(false, forKey: LunalyrExtras.Keys.discordCover)
        let extras = LunalyrExtras(defaults: defaults)
        // An existing user who had switched the cover off: the new button follows it.
        XCTAssertFalse(extras.discordShowsSongButton)
        XCTAssertFalse(DiscordPresence.needsAppleSearch(extras.discordOptions, spotifyURI: nil), "an Apple Music song")
        // Turned on by the user: the Apple Music song's link is looked up.
        extras.discordShowsSongButton = true
        XCTAssertTrue(DiscordPresence.needsAppleSearch(extras.discordOptions, spotifyURI: nil))
        XCTAssertFalse(DiscordPresence.needsAppleSearch(extras.discordOptions, spotifyURI: "spotify:track:abc"),
                       "Spotify's own link needs no search")
        XCTAssertEqual(defaults.object(forKey: LunalyrExtras.Keys.discordSongButton) as? Bool, true)
        // A new user: cover and button both on.
        let freshName = "LunalyrTests.\(UUID().uuidString)"
        let fresh = UserDefaults(suiteName: freshName)!
        defer { fresh.removePersistentDomain(forName: freshName) }
        XCTAssertTrue(LunalyrExtras(defaults: fresh).discordShowsSongButton)
    }

    // MARK: Limits Discord enforces

    func testFieldsAreMeasuredTheWayDiscordCounts() {
        // 100 graphemes but 200 UTF-16 units: Discord would refuse the whole activity.
        let emoji = DiscordPresence.field(String(repeating: "\u{1F3B5}", count: 100))
        XCTAssertLessThanOrEqual(emoji.utf16.count, 128)
        XCTAssertTrue(emoji.hasSuffix("…"))
        XCTAssertTrue(emoji.dropLast().allSatisfy { $0 == "\u{1F3B5}" }, "cut between whole characters")
        let thai = DiscordPresence.field(String(repeating: "\u{0E01}\u{0E34}", count: 90))
        XCTAssertLessThanOrEqual(thai.utf16.count, 128)
        XCTAssertEqual(DiscordPresence.field(String(repeating: "é", count: 128)).utf16.count, 128)
    }

    func testABadButtonIsDroppedRatherThanLosingTheWholeActivity() throws {
        var playing = DiscordPresence.Playing(title: "Song", artist: "Artist", album: nil, line: nil, start: 0, end: nil)
        for bad in ["javascript:alert(1)", "https://", "not a url", "https://x.example/" + String(repeating: "a", count: 520)] {
            playing.songURL = bad
            let buttons = try XCTUnwrap(DiscordPresence.activity(for: playing)["buttons"] as? [[String: String]])
            XCTAssertEqual(buttons.map { $0["label"] }, ["Get Lunalyr"], bad)
        }
        playing.party = DiscordPresence.ListenParty(room: "abc", listeners: 0, webURL: "file:///etc/passwd")
        playing.songURL = nil
        let buttons = try XCTUnwrap(DiscordPresence.activity(for: playing)["buttons"] as? [[String: String]])
        XCTAssertEqual(buttons.map { $0["label"] }, ["Get Lunalyr"])
        XCTAssertTrue(DiscordPresence.isValidButton(label: "Play this song", url: "https://song.link/i/1"))
        XCTAssertFalse(DiscordPresence.isValidButton(label: String(repeating: "a", count: 33), url: "https://song.link/i/1"))
    }

    func testASpotifyIDWithForeignCharactersMakesNoLink() {
        XCTAssertNil(DiscordPresence.songLink(spotifyURI: "spotify:track:abcé٣", appleTrackID: nil))
        XCTAssertEqual(DiscordPresence.songLink(spotifyURI: "spotify:track:abcé", appleTrackID: 7), "https://song.link/i/7")
    }

    func testUpdatesStayWithinDiscordsRateLimitWithRoomForASongChange() {
        var budget = DiscordUpdateBudget()
        let start = Date(timeIntervalSince1970: 1000)
        var sentLines = 0
        // A line change every second for twenty seconds.
        for second in 0..<20 {
            let now = start.addingTimeInterval(Double(second))
            if budget.allows(urgent: false, at: now) { budget.record(now); sentLines += 1 }
        }
        XCTAssertEqual(sentLines, 4)
        // A new song right then still goes out at once.
        let change = start.addingTimeInterval(19.5)
        XCTAssertTrue(budget.allows(urgent: true, at: change))
        budget.record(change)
        XCTAssertFalse(budget.allows(urgent: true, at: change.addingTimeInterval(0.1)), "five in twenty seconds at most")
        // Never more than five in any twenty seconds.
        var any = DiscordUpdateBudget()
        var times: [Double] = []
        for tick in 0..<400 {
            let t = Double(tick) * 0.5
            let now = start.addingTimeInterval(t)
            if any.allows(urgent: tick % 7 == 0, at: now) { any.record(now); times.append(t) }
        }
        for t in times { XCTAssertLessThanOrEqual(times.filter { $0 >= t && $0 < t + 20 }.count, 5) }
    }

    func testOnlyASongChangePauseOrClearIsUrgent() {
        let a = DiscordPresence.Playing(title: "A", artist: "X", album: nil, line: "one", start: 0, end: nil)
        var b = a; b.line = "two"
        var paused = a; paused.paused = true
        var other = a; other.title = "B"
        XCTAssertFalse(DiscordPresence.isUrgent(from: .some(a), to: b))
        XCTAssertTrue(DiscordPresence.isUrgent(from: .some(a), to: paused))
        XCTAssertTrue(DiscordPresence.isUrgent(from: .some(a), to: other))
        XCTAssertTrue(DiscordPresence.isUrgent(from: .some(a), to: nil))
        XCTAssertTrue(DiscordPresence.isUrgent(from: nil, to: a))
        XCTAssertTrue(DiscordPresence.isUrgent(from: .some(nil), to: a))
    }

    // MARK: The socket (a socket pair stands in for Discord)

    private final class FakeDiscord: @unchecked Sendable {
        private let lock = NSLock()
        private var running = false
        private var pairs: [[Int32]] = []
        var up: Bool {
            get { lock.withLock { running } }
            set { lock.withLock { running = newValue } }
        }
        /// Discord's end of the latest connection.
        var peer: Int32 { lock.withLock { pairs.last?[1] ?? -1 } }
        var connections: Int { lock.withLock { pairs.count } }

        func dial() -> Int32? {
            lock.withLock {
                guard running else { return nil }
                var pair: [Int32] = [-1, -1]
                guard socketpair(AF_UNIX, SOCK_STREAM, 0, &pair) == 0 else { return nil }
                // READY waits in the socket for the handshake to read.
                let ready = DiscordPresence.frame(.frame, ["evt": "READY", "data": ["user": ["username": "tester"]]])!
                _ = ready.withUnsafeBytes { Darwin.write(pair[1], $0.baseAddress!, ready.count) }
                pairs.append(pair)
                return pair[0]
            }
        }

        /// The next packet Discord's end receives, within `timeout`.
        func next(timeout: TimeInterval = 3) -> (opcode: UInt32, body: [String: Any])? {
            let fd = peer
            func read(_ count: Int) -> Data? {
                var data = Data()
                let deadline = Date().addingTimeInterval(timeout)
                while data.count < count {
                    var descriptor = pollfd(fd: fd, events: Int16(POLLIN), revents: 0)
                    guard poll(&descriptor, 1, Int32(max(1, deadline.timeIntervalSinceNow * 1000))) > 0 else { return nil }
                    var buffer = [UInt8](repeating: 0, count: count - data.count)
                    let n = Darwin.read(fd, &buffer, buffer.count)
                    guard n > 0 else { return nil }
                    data.append(contentsOf: buffer[..<n])
                }
                return data
            }
            guard let header = read(8) else { return nil }
            let opcode = header.withUnsafeBytes { $0.loadUnaligned(fromByteOffset: 0, as: UInt32.self) }.littleEndian
            let length = Int(header.withUnsafeBytes { $0.loadUnaligned(fromByteOffset: 4, as: UInt32.self) }.littleEndian)
            guard let body = read(length) else { return nil }
            return (opcode, (try? JSONSerialization.jsonObject(with: body)) as? [String: Any] ?? [:])
        }

        func hangUp() {
            lock.withLock {
                guard let last = pairs.indices.last else { return }
                close(pairs[last][1]); pairs[last][1] = -1
            }
        }
        /// Closes Discord's ends; the transport closes its own.
        func closeAll() { lock.withLock { for pair in pairs where pair[1] >= 0 { close(pair[1]) } } }

        func waitFor(connections count: Int) async throws {
            var waited = 0
            while connections < count, waited < 60 { try await Task.sleep(nanoseconds: 50_000_000); waited += 1 }
        }
    }

    private func activityPayload(_ details: String) -> [String: Any] {
        let playing = DiscordPresence.Playing(title: details, artist: "Artist", album: nil, line: nil, start: 0, end: nil)
        return DiscordPresence.setActivityPayload(DiscordPresence.activity(for: playing), pid: 1, nonce: UUID().uuidString)
    }

    private func details(_ packet: (opcode: UInt32, body: [String: Any])?) -> String? {
        ((packet?.body["args"] as? [String: Any])?["activity"] as? [String: Any])?["details"] as? String
    }

    func testDiscordStartedLaterStillGetsTheSong() async throws {
        let discord = FakeDiscord()
        defer { discord.closeAll() }
        let transport = DiscordTransport(connects: false, retryInterval: 0.2, dial: { discord.dial() })
        transport.send(activityPayload("Song"))
        try await Task.sleep(nanoseconds: 300_000_000)
        XCTAssertEqual(discord.connections, 0)
        // Discord opens; nothing new is playing, so nothing new is sent.
        discord.up = true
        try await discord.waitFor(connections: 1)
        XCTAssertEqual(discord.next()?.opcode, DiscordPresence.Opcode.handshake.rawValue)
        XCTAssertEqual(details(discord.next()), "Song · Artist", "the song shown before Discord opened is sent")
    }

    func testARestartedDiscordIsFoundAgainAtOnce() async throws {
        let discord = FakeDiscord()
        discord.up = true
        defer { discord.closeAll() }
        let transport = DiscordTransport(connects: false, retryInterval: 60, dial: { discord.dial() })
        transport.send(activityPayload("First"))
        try await discord.waitFor(connections: 1)
        XCTAssertEqual(discord.next()?.opcode, DiscordPresence.Opcode.handshake.rawValue)
        XCTAssertEqual(details(discord.next()), "First · Artist")
        discord.hangUp()
        try await Task.sleep(nanoseconds: 100_000_000)
        transport.send(activityPayload("Second"))
        try await discord.waitFor(connections: 2)
        XCTAssertEqual(discord.next()?.opcode, DiscordPresence.Opcode.handshake.rawValue)
        XCTAssertEqual(details(discord.next()), "Second · Artist")
        XCTAssertEqual(discord.connections, 2)
    }

    func testAClearedPresenceIsNotSentAgain() async throws {
        let discord = FakeDiscord()
        defer { discord.closeAll() }
        let transport = DiscordTransport(connects: false, retryInterval: 0.2, dial: { discord.dial() })
        transport.send(activityPayload("Song"))
        transport.send(DiscordPresence.setActivityPayload(nil, pid: 1, nonce: "x"), thenDisconnect: true)
        try await Task.sleep(nanoseconds: 100_000_000) // both handled while Discord is closed
        discord.up = true
        try await Task.sleep(nanoseconds: 700_000_000)
        XCTAssertEqual(discord.connections, 0, "nothing to show, so Discord isn't dialled")
    }

    // MARK: Ticking and caching

    func testTheTickIsSlowWhileNothingPlays() {
        XCTAssertEqual(DiscordPresence.tickInterval(playing: true, heldBack: false), DiscordPresence.tickInterval)
        XCTAssertEqual(DiscordPresence.tickInterval(playing: false, heldBack: true), DiscordPresence.tickInterval,
                       "a held-back pause or clear still goes out promptly")
        XCTAssertEqual(DiscordPresence.tickInterval(playing: false, heldBack: false), DiscordPresence.idleTickInterval)
        XCTAssertGreaterThanOrEqual(DiscordPresence.idleTickInterval, 5)
    }

    func testTheCatalogueCacheKeepsTheMostRecentSongs() {
        var cache = RecentCache<Int>(capacity: 3)
        for (index, key) in ["a", "b", "c"].enumerated() { cache.set(index, for: key) }
        XCTAssertEqual(cache.value(for: "a"), 0) // a is now the most recent
        cache.set(3, for: "d")
        XCTAssertEqual(cache.count, 3)
        XCTAssertNil(cache.value(for: "b"), "the least recently used goes first")
        XCTAssertEqual(cache.value(for: "a"), 0)
        cache.set(9, for: "a")
        XCTAssertEqual(cache.value(for: "a"), 9)
        cache.remove("a")
        XCTAssertNil(cache.value(for: "a"))
        XCTAssertEqual(cache.count, 2)
        for index in 0..<1000 { cache.set(index, for: "k\(index)") }
        XCTAssertEqual(cache.count, 3)
    }
}
