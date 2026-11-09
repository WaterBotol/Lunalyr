import AppKit
import Combine
import XCTest
@testable import VersoCustom

private enum Tunes {
    static let lights = FakeSpotifyApp.Song(id: "spotify:track:0VjIjW4GlUZAMYd2vXMi3b", name: "Blinding Lights",
                                            artist: "The Weeknd", album: "After Hours", milliseconds: 200_040)
    static let breathe = FakeSpotifyApp.Song(id: "spotify:track:4VqPOruhp5EdPBeR92t6lQ", name: "Breathe",
                                             artist: "Telepopmusik", album: "Genetic World", milliseconds: 279_000)
    static let song = FakeMusicApp.Song(id: "00000000000000A1", name: "First Light", album: "Dawn")
}

/// Runs twice, like `NowPlayingMonitorTests`: as is, and with split queues
/// in `SpotifyPlayerSplitQueueTests`.
class SpotifyPlayerTests: XCTestCase {
    private let spotifyID = "com.spotify.client"
    private let musicID = "com.apple.Music"

    class var splitQueues: Bool { false }

    override func setUp() {
        super.setUp()
        PlayerHarness.splitQueuesByDefault = Self.splitQueues
    }

    override func tearDown() {
        PlayerHarness.splitQueuesByDefault = false
        super.tearDown()
    }

    @MainActor
    func testSpotifyAloneIsFollowedAndMusicIsNeverAddressed() {
        let spotify = FakeSpotifyApp()
        spotify.queue = [Tunes.lights]
        spotify.position = 42
        let h = PlayerHarness(spotify: spotify, running: [spotifyID])
        h.start()
        XCTAssertEqual(h.monitor.activePlayer, .spotify)
        XCTAssertEqual(h.monitor.current?.title, "Blinding Lights")
        XCTAssertEqual(h.monitor.current?.artist, "The Weeknd")
        XCTAssertEqual(h.monitor.current?.duration ?? 0, 200.04, accuracy: 0.0001, "Milliseconds become seconds")
        XCTAssertEqual(h.monitor.current?.persistentID, Tunes.lights.id)
        XCTAssertEqual(h.monitor.playbackTime(), 42.16, accuracy: 0.001)
        XCTAssertEqual(h.music.log, [], "Music is never addressed, so it never opens over Spotify")
        XCTAssertEqual(h.monitor.idlePrompt, "Play a song in Music or Spotify")
        XCTAssertEqual(Set(h.environment.observedNotifications),
                       ["com.apple.Music.playerInfo", "com.spotify.client.PlaybackStateChanged"])

        h.monitor.playPause()
        h.monitor.seek(to: 100)
        h.scheduler.drain()
        XCTAssertEqual(spotify.count("spotify.playpause"), 1)
        XCTAssertEqual(spotify.count("spotify.seek"), 1)
        XCTAssertEqual(h.music.log, [])
    }

    @MainActor
    func testSpotifyNotificationsCarryTheTrackAndPositionWithoutAnAppleEvent() {
        let spotify = FakeSpotifyApp()
        spotify.queue = [Tunes.lights, Tunes.breathe]
        spotify.queue[1].artworkURL = nil
        let h = PlayerHarness(spotify: spotify, running: [spotifyID])
        h.start()
        spotify.clearLog()

        h.clock.now += 5
        spotify.index = 1
        h.postSpotify(Tunes.breathe, "Playing", at: 3.5)
        XCTAssertEqual(h.monitor.current?.title, "Breathe")
        XCTAssertEqual(h.monitor.current?.duration ?? 0, 279, accuracy: 0.0001)
        XCTAssertEqual(h.monitor.playbackTime(), 3.66, accuracy: 0.001, "Anchored from the notification's position")
        h.scheduler.drain()
        XCTAssertEqual(spotify.count("spotify.read"), 0)
        XCTAssertEqual(spotify.count("spotify.position"), 0)

        h.clock.now += 2
        h.postSpotify(Tunes.breathe, "Paused", at: 5.6)
        XCTAssertFalse(h.monitor.isPlaying)
        XCTAssertEqual(h.monitor.current?.title, "Breathe", "Paused keeps the track")
        XCTAssertEqual(h.monitor.playbackTime(), 5.6, accuracy: 0.001)
    }

    @MainActor
    func testSpotifyCoversAreDownloadedFromTheAddressItPublishes() async {
        let downloads = CallCounter()
        var services = NowPlayingServices.offline
        services.downloadArtwork = { url in
            downloads.record(url.absoluteString)
            return FakeImages.png
        }
        let spotify = FakeSpotifyApp()
        spotify.queue = [Tunes.lights, Tunes.breathe]
        spotify.queue[1].artworkURL = "http://i.scdn.co/image/breathe"
        let h = PlayerHarness(spotify: spotify, running: [spotifyID], services: services)
        h.start()
        await h.settle { h.monitor.currentTrackArtwork != nil }
        XCTAssertEqual(downloads.values, ["https://i.scdn.co/image/ab67616d0000b273cover"])

        // A notification carries no address: it is read, then upgraded to HTTPS.
        spotify.index = 1
        h.postSpotify(Tunes.breathe, "Playing", at: 0)
        XCTAssertNil(h.monitor.currentTrackArtwork, "Never the previous song's cover")
        h.scheduler.drain()
        await h.settle { h.monitor.currentTrackArtwork != nil }
        XCTAssertEqual(spotify.count("spotify.artwork"), 1)
        XCTAssertEqual(downloads.values.last, "https://i.scdn.co/image/breathe")
        XCTAssertEqual(h.monitor.artworkTrack?.title, "Breathe")
    }

    @MainActor
    func testTheShownPlayerIsTheOnePlayingThenTheMostRecent() {
        let music = FakeMusicApp()
        music.queue = [Tunes.song]
        music.state = .paused
        let spotify = FakeSpotifyApp()
        spotify.queue = [Tunes.lights]
        let h = PlayerHarness(music: music, spotify: spotify, running: [musicID, spotifyID])
        h.start()
        XCTAssertEqual(h.monitor.activePlayer, .spotify, "The one playing")
        XCTAssertEqual(h.monitor.current?.title, "Blinding Lights")

        h.clock.now += 1
        spotify.state = .paused
        h.postSpotify(Tunes.lights, "Paused", at: 30)
        XCTAssertEqual(h.monitor.activePlayer, .spotify, "Both paused: the one used last")

        h.clock.now += 1
        music.state = .playing
        h.postMusic(Tunes.song, "Playing")
        XCTAssertEqual(h.monitor.activePlayer, .appleMusic)
        XCTAssertEqual(h.monitor.current?.title, "First Light")

        h.clock.now += 1
        music.state = .paused
        h.postMusic(Tunes.song, "Paused")
        XCTAssertEqual(h.monitor.activePlayer, .appleMusic, "Music was used last")
        h.scheduler.drain()

        // Transport follows what is shown.
        h.monitor.playPause()
        h.scheduler.drain()
        XCTAssertEqual(music.count("music.playpause"), 1)
        XCTAssertEqual(spotify.count("spotify.playpause"), 0)
    }

    @MainActor
    func testChoosingOnePlayerNeverMessagesTheOther() {
        let choice = CurrentValueSubject<[PlayerKind], Never>(PlayerKind.allCases)
        var services = NowPlayingServices.offline
        services.followedPlayers = choice.eraseToAnyPublisher()
        let music = FakeMusicApp()
        music.queue = [Tunes.song]
        let spotify = FakeSpotifyApp()
        spotify.queue = [Tunes.lights]
        spotify.state = .paused
        let h = PlayerHarness(music: music, spotify: spotify, running: [musicID, spotifyID], services: services)
        h.start()
        XCTAssertEqual(h.monitor.activePlayer, .appleMusic)

        choice.send([.spotify])
        h.scheduler.drain()
        XCTAssertEqual(h.monitor.followedPlayers, [.spotify])
        XCTAssertEqual(h.monitor.activePlayer, .spotify)
        XCTAssertEqual(h.monitor.current?.title, "Blinding Lights")
        XCTAssertEqual(h.monitor.idlePrompt, "Play a song in Spotify")
        music.clearLog()
        for _ in 0..<10 { h.monitor.tick() }
        h.postMusic(Tunes.song, "Playing")
        h.monitor.playPause()
        h.scheduler.drain()
        XCTAssertEqual(music.log, [], "An unchosen player is never sent anything")
        XCTAssertEqual(spotify.count("spotify.playpause"), 1)

        choice.send(PlayerKind.allCases)
        h.scheduler.drain()
        XCTAssertEqual(h.monitor.followedPlayers, PlayerKind.allCases)
        XCTAssertTrue(music.log.contains("music.read"))
    }

    @MainActor
    func testAutomationIsTrackedPerPlayer() {
        let music = FakeMusicApp()
        music.queue = [Tunes.song]
        let spotify = FakeSpotifyApp()
        spotify.queue = [Tunes.lights]
        spotify.failure = AppleEventCode.notPermitted
        let h = PlayerHarness(music: music, spotify: spotify, running: [musicID, spotifyID])
        h.start()
        XCTAssertEqual(h.monitor.automation[.spotify], .denied)
        XCTAssertEqual(h.monitor.automation[.appleMusic], .granted)
        XCTAssertEqual(h.monitor.blockedPlayers, [.spotify])
        XCTAssertEqual(h.monitor.current?.title, "First Light", "Music still plays on")
        XCTAssertTrue(h.monitor.isPolling)
        // The check found Spotify allowed and its Apple events are refused
        // all the same, so its notifications don't check again either.
        let attempts = spotify.log.count
        h.postSpotify(Tunes.lights, "Playing", at: 1)
        for _ in 0..<10 { h.monitor.tick() }
        h.scheduler.drain()
        XCTAssertEqual(spotify.log.count, attempts, "Nothing more is sent to the refused player")
        XCTAssertEqual(h.monitor.activePlayer, .appleMusic)
    }

    /// An existing Music user who happens to have Spotify open.
    @MainActor
    func testAnOpenButUnusedSpotifyIsNeitherAskedAboutNorPolled() {
        let music = FakeMusicApp()
        music.queue = [Tunes.song]
        let spotify = FakeSpotifyApp()
        spotify.queue = [Tunes.lights, Tunes.breathe]
        spotify.state = .paused
        let h = PlayerHarness(music: music, spotify: spotify, running: [musicID, spotifyID])
        h.permission.statuses[spotifyID] = OSStatus(AppleEventCode.wouldRequireConsent)
        h.permission.answers[spotifyID] = 0
        h.start()
        XCTAssertEqual(h.monitor.current?.title, "First Light", "Music carries on as before")
        XCTAssertEqual(h.monitor.automation[.spotify], .needsConsent)
        XCTAssertEqual(h.permission.prompts(spotifyID), 0, "No unexplained prompt for an app that is only open")
        for _ in 0..<20 { h.monitor.tick() }
        h.postSpotify(Tunes.lights, "Paused", at: 12)
        h.scheduler.drain()
        XCTAssertEqual(spotify.log, [], "Not polled either")
        XCTAssertEqual(h.permission.prompts(spotifyID), 0)
        XCTAssertEqual(h.monitor.activePlayer, .appleMusic)
        XCTAssertEqual(h.monitor.deniedPlayers, [])

        // The user starts playing something in Spotify: now macOS asks, once.
        h.clock.now += 1
        spotify.state = .playing
        h.postSpotify(Tunes.lights, "Playing", at: 12)
        h.scheduler.drain()
        XCTAssertEqual(h.permission.prompts(spotifyID), 1)
        XCTAssertEqual(h.monitor.automation[.spotify], .granted)
        XCTAssertEqual(h.monitor.activePlayer, .spotify)
        XCTAssertEqual(h.monitor.current?.title, "Blinding Lights")
        spotify.index = 1
        h.postSpotify(Tunes.breathe, "Playing", at: 0)
        h.scheduler.drain()
        XCTAssertEqual(h.monitor.current?.title, "Breathe")
        XCTAssertEqual(h.permission.prompts(spotifyID), 1)
    }

    @MainActor
    func testChoosingSpotifyAsksAboutItAtOnce() {
        var services = NowPlayingServices.offline
        services.followedPlayers = Just([PlayerKind.spotify]).eraseToAnyPublisher()
        let spotify = FakeSpotifyApp()
        spotify.queue = [Tunes.lights]
        let h = PlayerHarness(spotify: spotify, running: [musicID, spotifyID], services: services)
        h.permission.statuses[spotifyID] = OSStatus(AppleEventCode.wouldRequireConsent)
        h.permission.answers[spotifyID] = 0
        h.start()
        XCTAssertEqual(h.permission.prompts(spotifyID), 1, "Choosing Spotify is asking Lunalyr to read it")
        XCTAssertEqual(h.permission.requests.filter { $0.bundleID == musicID }.count, 0, "Music is not followed")
        XCTAssertEqual(h.monitor.current?.title, "Blinding Lights")
    }

    @MainActor
    func testSpotifyListeningIsNeverSentToApplesCatalogue() async {
        let searches = CallCounter(), motions = CallCounter()
        var services = NowPlayingServices.offline
        services.catalogueArtwork = { track in
            searches.record(track.title)
            return FakeImages.png
        }
        services.motionArtwork = { track in
            motions.record(track.title)
            return nil
        }
        services.allowsMotionLookup = { _ in true }
        var local = Tunes.breathe
        local.artworkURL = nil // a local file: Spotify has no cover for it
        let spotify = FakeSpotifyApp()
        spotify.queue = [local]
        let h = PlayerHarness(spotify: spotify, running: [spotifyID], services: services)
        h.start()
        await h.settle(until: { false }, timeout: 0.2)
        XCTAssertEqual(h.monitor.current?.title, "Breathe")
        XCTAssertEqual(spotify.count("spotify.artwork"), 1)
        XCTAssertEqual(h.monitor.artworkTrack?.title, "Breathe")
        XCTAssertNil(h.monitor.artwork, "The default colours rather than an Apple search")
        XCTAssertEqual(searches.values, [])
        XCTAssertEqual(motions.values, [], "Animated covers are Apple Music's")
        XCTAssertFalse(h.monitor.isLookingUpMotion)
    }

    @MainActor
    func testASpotifyNotificationFromBeforeASeekDoesNotPullTheHighlightBack() {
        let spotify = FakeSpotifyApp()
        spotify.queue = [Tunes.lights]
        spotify.position = 42
        let h = PlayerHarness(spotify: spotify, running: [spotifyID], split: true)
        h.start()
        h.monitor.seek(to: 120)
        // Posted before Spotify applied the seek, handled while it is in flight.
        h.postSpotify(Tunes.lights, "Playing", at: 42.5)
        XCTAssertEqual(h.monitor.playbackTime(), 120.16, accuracy: 0.001)
        h.scheduler.drain()
        XCTAssertEqual(spotify.position, 120)
        XCTAssertEqual(h.monitor.playbackTime(), 120.16, accuracy: 0.001)
        h.clock.now += 2
        h.postSpotify(Tunes.lights, "Playing", at: 122)
        XCTAssertEqual(h.monitor.playbackTime(), 122.16, accuracy: 0.001, "After the seek its positions count again")
    }

    @MainActor
    func testAdvertsAreNotTreatedAsSongs() {
        let spotify = FakeSpotifyApp()
        spotify.queue = [Tunes.lights]
        let h = PlayerHarness(spotify: spotify, running: [spotifyID])
        h.start()
        let advert = FakeSpotifyApp.Song(id: "spotify:ad:000000012c8f8b2e", name: "Advertisement", artist: "", album: "",
                                         milliseconds: 30_000)
        h.postSpotify(advert, "Playing", at: 2)
        XCTAssertEqual(h.monitor.current?.isStream, true)
        XCTAssertEqual(h.monitor.current?.duration, 0, "No lyric lookup, no scrubber")
        // Named as what it is, not as something live.
        XCTAssertEqual(h.monitor.current?.isAdvert, true)
        XCTAssertEqual(MenuBarView.subtitle(for: h.monitor.current, idlePrompt: ""), "Advertisement")
        XCTAssertEqual(MenuBarView.placeholderSymbol(for: h.monitor.current), "megaphone")
        h.postSpotify(Tunes.lights, "Playing", at: 3)
        XCTAssertEqual(h.monitor.current?.isAdvert, false)
        let radio = NowPlayingMonitor.Track(title: "Radio One", artist: "", duration: 0, genre: nil, album: nil, isStream: true)
        XCTAssertEqual(MenuBarView.subtitle(for: radio, idlePrompt: ""), "Live stream")
    }

    func testSpotifyScriptsAndRepliesFollowItsDictionary() {
        let backend = SpotifyBackend()
        var scripts = [backend.readScript, backend.positionScript] + backend.artworkScripts
        scripts += [TransportCommand.playPause, .next, .previous].map(backend.commandScript)
        scripts.append(backend.seekScript(to: 61.25))
        for script in scripts {
            XCTAssertTrue(script.source.hasPrefix(#"if application id "com.spotify.client" is not running then return"#), script.name)
        }
        XCTAssertTrue(backend.seekScript(to: 61.25).source.contains("set player position to 61.250"))

        let reply = NSAppleEventDescriptor.scriptList([
            NSAppleEventDescriptor(string: "paused"), NSAppleEventDescriptor(string: "spotify:track:1"),
            NSAppleEventDescriptor(string: "Song"), NSAppleEventDescriptor(string: "Singer"),
            NSAppleEventDescriptor(string: "Record"), NSAppleEventDescriptor(int32: 183_500),
            NSAppleEventDescriptor(string: "https://i.scdn.co/image/abc"),
        ])
        guard case .value(let snapshot) = backend.parseRead(reply) else { return XCTFail("Unparsed read") }
        XCTAssertEqual(snapshot.state, .paused)
        XCTAssertEqual(snapshot.track?.duration ?? 0, 183.5, accuracy: 0.0001)
        XCTAssertEqual(snapshot.track?.persistentID, "spotify:track:1")
        XCTAssertEqual(snapshot.artworkURL?.absoluteString, "https://i.scdn.co/image/abc")
        XCTAssertNil(snapshot.track?.genre)

        XCTAssertEqual(SpotifyBackend.coverURL("http://i.scdn.co/image/abc")?.scheme, "https")
        XCTAssertNil(SpotifyBackend.coverURL("http://example.com/cover.jpg"), "Plain HTTP elsewhere is refused")
        XCTAssertNil(SpotifyBackend.coverURL(""))
        XCTAssertNil(backend.snapshot(fromNotification: ["Player State": "Playing", "Name": "No ID"]),
                     "Incomplete notifications are confirmed with the script")
        XCTAssertEqual(backend.snapshot(fromNotification: ["Player State": "Stopped"])?.state, .stopped)
    }

    @MainActor
    func testThePlayerChoiceIsRememberedUnderACustomKey() {
        let name = "LunalyrTests.\(UUID().uuidString)"
        let isolated = UserDefaults(suiteName: name)!
        defer { isolated.removePersistentDomain(forName: name) }
        XCTAssertEqual(PlayerOptions(defaults: isolated).choice, .automatic)
        let options = PlayerOptions(defaults: isolated)
        options.choice = .spotify
        XCTAssertEqual(isolated.string(forKey: "custom.player"), "spotify")
        XCTAssertEqual(PlayerOptions(defaults: isolated).choice, .spotify)
        isolated.set("winamp", forKey: "custom.player")
        XCTAssertEqual(PlayerOptions(defaults: isolated).choice, .automatic)
        XCTAssertEqual(PlayerOptions.Choice.automatic.players, [.appleMusic, .spotify, .vlc, .swinsian, .vox],
                       "Automatic follows browsers only once web players are on")
        XCTAssertEqual(PlayerOptions.Choice.appleMusic.players, [.appleMusic])

        var received: [[PlayerKind]] = []
        let watch = options.playersPublisher.sink { received.append($0) }
        options.choice = .appleMusic
        options.choice = .appleMusic
        watch.cancel()
        XCTAssertEqual(received, [[.spotify], [.appleMusic]])
    }
}

/// `SpotifyPlayerTests` in the app's order: each reply handled at once.
final class SpotifyPlayerSplitQueueTests: SpotifyPlayerTests {
    override class var splitQueues: Bool { true }
}
