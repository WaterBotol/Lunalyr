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
}
