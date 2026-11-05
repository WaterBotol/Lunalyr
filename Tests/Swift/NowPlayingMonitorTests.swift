import AppKit
import Combine
import XCTest
@testable import VersoCustom

private enum Songs {
    static let first = FakeMusicApp.Song(id: "00000000000000A1", name: "First Light", album: "Dawn")
    static let second = FakeMusicApp.Song(id: "00000000000000A2", name: "Second Wind", album: "Dawn")
    static let other = FakeMusicApp.Song(id: "00000000000000B1", name: "Elsewhere", artist: "Someone Else", album: "Far")

    static func numbered(_ count: Int) -> [FakeMusicApp.Song] {
        (1...count).map { FakeMusicApp.Song(id: String(format: "%016X", $0), name: "Song \($0)", album: "Album \($0)") }
    }
}

/// Music, refusing every Apple event until allowed. `whileRefused` runs as
/// the first refused event is in flight: what the user does meanwhile.
private final class RefusedUntilAllowed: AppleScriptRunning {
    let music: FakeMusicApp
    var refusing = true
    var whileRefused: (() -> Void)?

    init(_ music: FakeMusicApp) {
        self.music = music
    }

    func run(_ script: PlayerScript) -> Result<NSAppleEventDescriptor, ScriptFailure> {
        guard refusing else { return music.run(script) }
        whileRefused?()
        whileRefused = nil
        return .failure(ScriptFailure(code: AppleEventCode.notPermitted))
    }
}

/// Every test here runs twice: as is, where a reply is handled only after
/// the work queued before it, and in `NowPlayingMonitorSplitQueueTests`,
/// where the main actor handles each reply as soon as it is posted while
/// the script queue is still busy -- the order the app usually sees.
class NowPlayingMonitorTests: XCTestCase {
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
    private func harness(_ songs: [FakeMusicApp.Song], state: PlayerSnapshot.State = .playing, position: TimeInterval = 0,
                         services: NowPlayingServices = .offline, split: Bool? = nil) -> PlayerHarness {
        let music = FakeMusicApp()
        music.queue = songs
        music.state = state
        music.position = position
        return PlayerHarness(music: music, services: services, split: split)
    }

    @MainActor
    func testTheTestHostNeverStartsTheSharedMonitorOrMessagesRealPlayers() {
        XCTAssertTrue(AppRuntime.isUnitTesting)
        XCTAssertFalse(NowPlayingMonitor.shared.isPolling)
        XCTAssertTrue(NowPlayingMonitor.shared.runningPlayers.isEmpty)
        guard case .failure(let failure) = LiveAppleScriptRunner().run(MusicBackend().readScript) else {
            return XCTFail("The live runner must refuse inside the test host")
        }
        XCTAssertEqual(failure.code, AppleEventCode.processNotFound)
        XCTAssertFalse(LivePlayerEnvironment().isRunning("com.apple.Music"))
        XCTAssertEqual(LivePlayerEnvironment.automationPermission("com.apple.Music", false),
                       OSStatus(AppleEventCode.processNotFound))
    }

    @MainActor
    func testNothingIsSentToMusicWhileItIsNotRunning() {
        let music = FakeMusicApp()
        music.queue = [Songs.first]
        music.running = false
        let h = PlayerHarness(music: music, running: [])
        h.start()
        XCTAssertFalse(h.monitor.isPolling)
        XCTAssertFalse(h.monitor.canControlPlayback)
        XCTAssertNotNil(h.monitor.idleSince)
        for _ in 0..<12 { h.monitor.tick() }
        h.monitor.playPause()
        h.monitor.nextTrack()
        h.monitor.previousTrack()
        h.monitor.seek(to: 30)
        h.monitor.refresh()
        h.monitor.recheckAutomation()
        h.scheduler.drain()
        XCTAssertEqual(music.log, [], "AppleScript would launch Music")

        music.running = true
        h.environment.launch("com.apple.Music")
        XCTAssertEqual(music.log, [], "Nothing until it has finished launching and access is known")
        h.scheduler.fireDelayed()
        XCTAssertTrue(h.monitor.isPolling)
        XCTAssertEqual(h.monitor.current?.title, "First Light")
        XCTAssertEqual(h.permission.requests.map(\.ask), [false], "Access was checked silently first")

        music.running = false
        h.environment.terminate("com.apple.Music")
        XCTAssertNil(h.monitor.current)
        XCTAssertFalse(h.monitor.isPolling)
        let sent = music.log.count
        for _ in 0..<12 { h.monitor.tick() }
        h.monitor.playPause()
        h.scheduler.drain()
        XCTAssertEqual(music.log.count, sent)

        // A notification Music posts as it quits arrives after the system said it had.
        h.postMusic(Songs.first, "Playing")
        h.scheduler.drain()
        XCTAssertNil(h.monitor.current, "A player that has quit is not shown again")
        XCTAssertFalse(h.monitor.isPolling)
        XCTAssertEqual(music.log.count, sent)
    }

    func testEveryScriptChecksThePlayerIsRunningBeforeAddressingIt() {
        let backend = MusicBackend()
        var scripts = [backend.readScript, backend.positionScript] + backend.artworkScripts
        scripts += [TransportCommand.playPause, .next, .previous].map(backend.commandScript)
        scripts.append(backend.seekScript(to: 12.5))
        for script in scripts {
            XCTAssertTrue(script.source.hasPrefix(#"if application id "com.apple.Music" is not running then return"#), script.name)
            if script.source.contains("on error") {
                // Missing properties are tolerated; anything else ends the script.
                XCTAssertTrue(script.source.contains("error number errorNumber"), script.name)
            }
        }
        XCTAssertTrue(backend.seekScript(to: 12.5).source.contains("set player position to 12.500"))
        XCTAssertFalse(backend.seekScript(to: 12.5).cacheable)
        XCTAssertEqual(PlayerResponse<Bool>(failureCode: -1743), .blocked(.denied))
        XCTAssertEqual(PlayerResponse<Bool>(failureCode: -1744), .blocked(.needsConsent))
        XCTAssertEqual(PlayerResponse<Bool>(failureCode: -609), .notRunning)
        XCTAssertEqual(PlayerResponse<Bool>(failureCode: -1712), .failed(-1712))
    }

    @MainActor
    func testPausingKeepsTheTrackItsControlsAndItsIdentity() {
        let h = harness([Songs.first], position: 30)
        var published: [String?] = []
        let watch = h.monitor.$current.removeDuplicates().sink { published.append($0?.title) }
        defer { watch.cancel() }
        h.start()
        XCTAssertEqual(h.monitor.current?.title, "First Light")
        XCTAssertTrue(h.monitor.isPlaying)
        XCTAssertEqual(h.monitor.playbackTime(), 30.16, accuracy: 0.001)

        h.music.state = .paused
        h.music.position = 42
        h.clock.now += 12
        h.postMusic(Songs.first, "Paused")
        XCTAssertEqual(h.monitor.current?.title, "First Light", "Paused is not nothing playing")
        XCTAssertFalse(h.monitor.isPlaying)
        XCTAssertTrue(h.monitor.canControlPlayback, "Play stays available")
        XCTAssertNotNil(h.monitor.currentTrackArtwork)
        XCTAssertEqual(h.monitor.playbackTime(), 42, accuracy: 0.001)
        h.scheduler.drain()
        h.clock.now += 30
        XCTAssertEqual(h.monitor.playbackTime(), 42, accuracy: 0.001, "A paused clock holds")

        h.music.state = .playing
        h.postMusic(Songs.first, "Playing")
        XCTAssertTrue(h.monitor.isPlaying)
        h.scheduler.drain()
        h.clock.now += 1
        XCTAssertEqual(h.monitor.playbackTime(), 43.16, accuracy: 0.001)
        XCTAssertEqual(published, [nil, "First Light"], "One identity across pause and resume: no new lyric lookup")

        h.monitor.playPause()
        h.scheduler.drain()
        XCTAssertEqual(h.music.count("music.playpause"), 1)
    }

    @MainActor
    func testLaunchingWhilePausedShowsThePausedTrack() {
        let h = harness([Songs.first], state: .paused, position: 61)
        h.start()
        XCTAssertEqual(h.monitor.current?.title, "First Light")
        XCTAssertFalse(h.monitor.isPlaying)
        XCTAssertTrue(h.monitor.canControlPlayback)
        XCTAssertEqual(h.monitor.playbackTime(), 61, accuracy: 0.001)
        XCTAssertNotNil(h.monitor.currentTrackArtwork)
    }

    @MainActor
    func testStoppingOrQuittingClearsTheTrack() {
        let h = harness([Songs.first])
        h.start()
        XCTAssertNotNil(h.monitor.artwork)
        h.music.state = .stopped
        h.environment.post("com.apple.Music.playerInfo", ["Player State": "Stopped"])
        XCTAssertNil(h.monitor.current)
        XCTAssertFalse(h.monitor.isPlaying)
        XCTAssertNil(h.monitor.artwork)
        XCTAssertNotNil(h.monitor.idleSince)
        XCTAssertEqual(h.monitor.playbackTime(), 0)
        XCTAssertTrue(h.monitor.canControlPlayback, "Music is open, so Play can start it")

        h.music.state = .playing
        h.postMusic(Songs.first, "Playing")
        h.scheduler.drain()
        XCTAssertNotNil(h.monitor.current)
        h.music.running = false
        h.environment.terminate("com.apple.Music")
        XCTAssertNil(h.monitor.current)
        XCTAssertNil(h.monitor.activePlayer)
        XCTAssertFalse(h.monitor.canControlPlayback)
    }

    @MainActor
    func testAPlayerQuittingMidPollIsForgottenWithoutBeingReopened() {
        let h = harness([Songs.first])
        h.start()
        // Music has gone, but the system has not said so yet.
        h.music.running = false
        h.monitor.tick()
        h.scheduler.drain()
        XCTAssertNil(h.monitor.current)
        XCTAssertEqual(h.music.log.last, "music.position", "The script's own check answered; nothing relaunched it")
        h.environment.terminate("com.apple.Music")
        XCTAssertFalse(h.monitor.isPolling)
    }

    @MainActor
    func testANotificationFromAPlayerThatHasQuitIsIgnored() {
        let h = harness([Songs.first])
        h.start()
        // Music quits; a re-sync finds it gone before the system's own word arrives...
        h.music.running = false
        h.environment.running.remove("com.apple.Music")
        h.monitor.tick()
        h.scheduler.drain()
        XCTAssertNil(h.monitor.current)
        XCTAssertFalse(h.monitor.isPolling)
        // ...and a notification it posted on the way out lands after that.
        let sent = h.music.log.count
        h.postMusic(Songs.first, "Paused")
        h.scheduler.drain()
        XCTAssertNil(h.monitor.current, "A player that has quit is never shown again")
        XCTAssertNil(h.monitor.activePlayer)
        XCTAssertFalse(h.monitor.canControlPlayback)
        XCTAssertEqual(h.music.log.count, sent)
    }

    @MainActor
    func testAPlayerThatQuitsBetweenMetadataAndPositionIsNotShown() {
        let music = FakeMusicApp()
        music.queue = [Songs.first]
        music.onReply = { name in
            guard name == "music.read" else { return }
            music.onReply = nil
            music.running = false
        }
        let h = PlayerHarness(music: music)
        h.start()
        XCTAssertNil(h.monitor.current)
        XCTAssertNil(h.monitor.activePlayer)
    }

    /// Music has answered this session, then stops answering for a while
    /// (a dialog of its own, say). A first event that times out is another
    /// matter: see `AutomationConsentTests`.
    @MainActor
    func testATimeoutBacksOffInsteadOfResendingOnEveryTick() {
        let h = harness([Songs.first])
        h.start()
        h.music.clearLog()
        h.music.failure = AppleEventCode.timedOut
        h.monitor.tick()
        h.scheduler.drain()
        XCTAssertEqual(h.music.log, ["music.position"])
        XCTAssertEqual(h.monitor.current?.title, "First Light", "What is on screen stays")
        XCTAssertEqual(h.monitor.automation[.appleMusic], .granted, "Busy, not asking")
        XCTAssertTrue(h.monitor.isPolling)

        for _ in 0..<3 {
            h.clock.now += 1
            h.monitor.tick()
            h.monitor.refresh()
        }
        h.scheduler.drain()
        XCTAssertEqual(h.music.log.count, 1, "Four seconds before the next try, not two")
        h.clock.now += 1.5
        h.monitor.tick()
        h.scheduler.drain()
        XCTAssertEqual(h.music.log.count, 2)
        h.clock.now += 7
        h.monitor.tick()
        h.scheduler.drain()
        XCTAssertEqual(h.music.log.count, 2, "Twice as long after a second timeout")

        // The buttons still reach Music: the user asked.
        h.monitor.playPause()
        h.scheduler.drain()
        XCTAssertEqual(h.music.count("music.playpause"), 1)

        h.music.failure = nil
        h.clock.now += 60
        h.monitor.tick()
        h.scheduler.drain()
        XCTAssertEqual(h.music.count("music.position"), 3)
        h.monitor.tick()
        h.scheduler.drain()
        XCTAssertEqual(h.music.count("music.position"), 4, "Answering again ends the back-off")
        XCTAssertEqual(h.monitor.current?.title, "First Light")
    }

    @MainActor
    func testPlayStartsAStoppedPlayerThatIsOpen() {
        let h = harness([Songs.first], state: .stopped)
        h.start()
        XCTAssertNil(h.monitor.current)
        XCTAssertTrue(h.monitor.canControlPlayback)
        h.monitor.playPause()
        h.scheduler.drain()
        XCTAssertEqual(h.music.count("music.playpause"), 1)
        XCTAssertEqual(h.monitor.current?.title, "First Light")
        XCTAssertTrue(h.monitor.isPlaying)
    }

    @MainActor
    func testDeniedAutomationIsReportedAndPollingStopsUntilAllowed() {
        let h = harness([Songs.first])
        h.music.failure = AppleEventCode.notPermitted
        h.start()
        XCTAssertEqual(h.monitor.automation[.appleMusic], .denied)
        XCTAssertEqual(h.monitor.blockedPlayers, [.appleMusic])
        XCTAssertNil(h.monitor.current)
        XCTAssertFalse(h.monitor.isPolling)
        XCTAssertFalse(h.monitor.canControlPlayback)

        // The check found Music allowed, and its first Apple event was
        // refused all the same, and a second check still says allowed:
        // Music's notifications don't check again, since each would cost
        // another refused read.
        let attempts = h.music.log.count
        for _ in 0..<10 { h.monitor.tick() }
        h.postMusic(Songs.first, "Playing")
        h.monitor.playPause()
        h.scheduler.drain()
        XCTAssertEqual(h.music.log.count, attempts, "Nothing more is sent while access is off")
        XCTAssertNil(h.monitor.current)

        h.permission.status = OSStatus(AppleEventCode.notPermitted)
        h.environment.activate()
        h.scheduler.drain()
        XCTAssertEqual(h.monitor.automation[.appleMusic], .denied)
        XCTAssertEqual(h.monitor.deniedPlayers, [.appleMusic])
        XCTAssertEqual(h.permission.asks, [false, false, false],
                       "The first check, the one asked about the refusal, and the re-check never prompt")

        // Allowed in System Settings; Lunalyr becomes active again.
        h.music.failure = nil
        h.permission.status = 0
        h.environment.activate()
        h.scheduler.drain()
        XCTAssertEqual(h.monitor.automation[.appleMusic], .granted)
        XCTAssertTrue(h.monitor.blockedPlayers.isEmpty)
        XCTAssertEqual(h.monitor.current?.title, "First Light")
        XCTAssertTrue(h.monitor.isPolling)
    }

    /// Open Privacy Settings from the banner, switch Music on, close System
    /// Settings: focus goes back to Music, not Lunalyr, and Play is pressed
    /// there. Music's own notification is what notices.
    @MainActor
    func testARefusedPlayersOwnNotificationsNoticeAccessSwitchedOn() {
        let h = harness([Songs.first], state: .paused)
        h.permission.status = OSStatus(AppleEventCode.notPermitted)
        h.start()
        XCTAssertEqual(h.monitor.deniedPlayers, [.appleMusic])
        XCTAssertEqual(h.music.log, [])

        h.postMusic(Songs.first, "Paused")
        h.scheduler.drain()
        XCTAssertEqual(h.music.log, [], "Still refused: nothing is sent")
        XCTAssertEqual(h.permission.asks, [false, false], "Checked again, silently")

        h.permission.status = 0
        h.music.state = .playing
        h.postMusic(Songs.first, "Playing")
        h.scheduler.drain()
        XCTAssertEqual(h.monitor.automation[.appleMusic], .granted)
        XCTAssertEqual(h.monitor.deniedPlayers, [])
        XCTAssertEqual(h.monitor.current?.title, "First Light")
        XCTAssertTrue(h.monitor.isPlaying)
        XCTAssertTrue(h.monitor.isPolling)
        XCTAssertFalse(h.permission.asks.contains(true), "Never a prompt")
    }

    /// Access turned off mid-song, then on again, without Lunalyr ever
    /// becoming active: the next song's notification brings it back.
    @MainActor
    func testAPlayerRefusedMidSongIsFollowedAgainOnceAllowed() {
        let h = harness([Songs.first, Songs.second])
        h.start()
        XCTAssertEqual(h.monitor.current?.title, "First Light")
        h.permission.status = OSStatus(AppleEventCode.notPermitted)
        h.music.failure = AppleEventCode.notPermitted
        h.monitor.tick()
        h.scheduler.drain()
        XCTAssertNil(h.monitor.current)
        XCTAssertEqual(h.monitor.deniedPlayers, [.appleMusic])
        XCTAssertFalse(h.monitor.isPolling)

        let sent = h.music.log.count
        h.postMusic(Songs.first, "Playing")
        h.scheduler.drain()
        XCTAssertEqual(h.music.log.count, sent, "Checked silently; nothing is sent while refused")
        XCTAssertNil(h.monitor.current)

        h.permission.status = 0
        h.music.failure = nil
        h.music.index = 1
        h.postMusic(Songs.second, "Playing")
        h.scheduler.drain()
        XCTAssertEqual(h.monitor.automation[.appleMusic], .granted)
        XCTAssertEqual(h.monitor.current?.title, "Second Wind")
        XCTAssertTrue(h.monitor.isPolling)
        XCTAssertFalse(h.permission.asks.contains(true))
    }

    /// Music refused before Lunalyr started: the wallpaper's idle notice
    /// goes up and names it, instead of a blank desktop over audible music.
    @MainActor
    func testAPlayerRefusedAtLaunchPutsUpTheIdleNoticeNamingIt() throws {
        let h = harness([Songs.first])
        XCTAssertEqual(h.monitor.idleTitle, "Nothing playing")
        XCTAssertNil(h.monitor.idleDetail)
        h.permission.status = OSStatus(AppleEventCode.notPermitted)
        h.start()
        XCTAssertNil(h.monitor.current)
        XCTAssertEqual(h.monitor.deniedPlayers, [.appleMusic])
        let since = try XCTUnwrap(h.monitor.idleSince, "The notice never went up")
        XCTAssertEqual(h.monitor.idleTitle, "Lunalyr can’t read Music")
        XCTAssertEqual(h.monitor.idleDetail, "Allow it in System Settings › Privacy & Security › Automation")

        // The same refusal found again, on a re-check, leaves the notice to fade.
        h.environment.activate()
        h.scheduler.drain()
        XCTAssertEqual(h.monitor.idleSince, since)

        // Nothing is refused once Music has quit.
        h.music.running = false
        h.environment.terminate("com.apple.Music")
        XCTAssertEqual(h.monitor.idleTitle, "Nothing playing")
        XCTAssertNil(h.monitor.idleDetail)
    }

    /// Access turned off while Music sits stopped: the re-sync that runs
    /// into the refusal puts the notice up again, though playback stopped
    /// long before.
    @MainActor
    func testARefusalFoundWhileNothingPlaysPutsTheNoticeUpAgain() throws {
        let h = harness([Songs.first], state: .stopped)
        h.start()
        XCTAssertNil(h.monitor.current)
        let stopped = try XCTUnwrap(h.monitor.idleSince)
        XCTAssertEqual(h.monitor.idleTitle, "Nothing playing")
        Thread.sleep(forTimeInterval: 0.002)

        h.permission.status = OSStatus(AppleEventCode.notPermitted)
        h.music.failure = AppleEventCode.notPermitted
        for _ in 0..<5 { h.monitor.tick() }
        h.scheduler.drain()
        XCTAssertEqual(h.monitor.deniedPlayers, [.appleMusic])
        let refused = try XCTUnwrap(h.monitor.idleSince)
        XCTAssertGreaterThan(refused, stopped)
        XCTAssertEqual(h.monitor.idleTitle, "Lunalyr can’t read Music")
    }

    /// A player refused while another plays on: nothing to explain, since
    /// the wallpaper shows the other one's lyrics.
    @MainActor
    func testARefusalWhileAnotherPlayerIsShownLeavesTheIdleNoticeAlone() {
        let music = FakeMusicApp()
        music.queue = [Songs.first]
        let spotify = FakeSpotifyApp()
        spotify.queue = [FakeSpotifyApp.Song(id: "spotify:track:1", name: "Elsewhere")]
        spotify.state = .paused
        let h = PlayerHarness(music: music, spotify: spotify, running: ["com.apple.Music", "com.spotify.client"])
        h.permission.statuses["com.spotify.client"] = OSStatus(AppleEventCode.notPermitted)
        h.start()
        XCTAssertEqual(h.monitor.current?.title, "First Light")
        XCTAssertEqual(h.monitor.deniedPlayers, [.spotify])
        XCTAssertNil(h.monitor.idleSince, "Music's song is on screen")
    }

    /// Spotify refused and unused beside Music: stopping Music must not put
    /// "Lunalyr can’t read Spotify" on the desktop each time. Spotify is
    /// named once its own notification says it is playing, or once nothing
    /// Lunalyr can read runs beside it.
    @MainActor
    func testARefusedPlayerIsNamedOnlyWhenItMayBeTheOnePlaying() throws {
        let music = FakeMusicApp()
        music.queue = [Songs.first]
        music.state = .stopped
        let spotify = FakeSpotifyApp()
        let elsewhere = FakeSpotifyApp.Song(id: "spotify:track:1", name: "Elsewhere")
        spotify.queue = [elsewhere]
        spotify.state = .paused
        let h = PlayerHarness(music: music, spotify: spotify, running: ["com.apple.Music", "com.spotify.client"])
        h.permission.statuses["com.spotify.client"] = OSStatus(AppleEventCode.notPermitted)
        h.start()
        XCTAssertNil(h.monitor.current)
        XCTAssertEqual(h.monitor.deniedPlayers, [.spotify])
        XCTAssertNil(h.monitor.idleRefusal)
        XCTAssertEqual(h.monitor.idleTitle, "Nothing playing")
        XCTAssertNil(h.monitor.idleDetail)
        let stopped = try XCTUnwrap(h.monitor.idleSince)

        h.postMusic(Songs.first, "Stopped")
        h.scheduler.drain()
        XCTAssertEqual(h.monitor.idleTitle, "Nothing playing", "Music stopping says nothing about Spotify")
        XCTAssertEqual(h.monitor.idleSince, stopped)

        Thread.sleep(forTimeInterval: 0.002)
        h.postSpotify(elsewhere, "Playing", at: 0)
        h.scheduler.drain()
        XCTAssertEqual(h.monitor.idleRefusal, .spotify)
        XCTAssertEqual(h.monitor.idleTitle, "Lunalyr can’t read Spotify")
        XCTAssertEqual(h.monitor.idleDetail, "Allow it in System Settings › Privacy & Security › Automation")
        XCTAssertGreaterThan(try XCTUnwrap(h.monitor.idleSince), stopped, "Its music may be playing: the notice goes up")

        h.postSpotify(elsewhere, "Paused", at: 3)
        h.scheduler.drain()
        XCTAssertEqual(h.monitor.idleTitle, "Nothing playing")

        // With Music gone, Spotify is the player that may be in use.
        h.music.running = false
        h.environment.terminate("com.apple.Music")
        XCTAssertEqual(h.monitor.idleTitle, "Lunalyr can’t read Spotify")
        XCTAssertEqual(spotify.log, [], "Nothing is ever sent to it")
    }

    /// Music refused, quit and opened again while Lunalyr runs: the check
    /// after it relaunches finds the refusal afresh and puts the notice up,
    /// and so does pressing Play in it, though the notice faded long ago.
    @MainActor
    func testARefusedPlayerThatRelaunchesOrStartsPlayingPutsTheNoticeUpAgain() throws {
        let h = harness([Songs.first], state: .paused)
        h.permission.status = OSStatus(AppleEventCode.notPermitted)
        h.start()
        let launched = try XCTUnwrap(h.monitor.idleSince)

        h.music.running = false
        h.environment.terminate("com.apple.Music")
        XCTAssertNil(h.monitor.automation[.appleMusic], "Checked afresh when it next launches")
        Thread.sleep(forTimeInterval: 0.002)
        h.music.running = true
        h.environment.launch("com.apple.Music")
        XCTAssertEqual(h.monitor.deniedPlayers, [], "Not called refused before it is checked")
        h.scheduler.fireDelayed()
        XCTAssertEqual(h.monitor.deniedPlayers, [.appleMusic])
        XCTAssertEqual(h.monitor.idleTitle, "Lunalyr can’t read Music")
        let relaunched = try XCTUnwrap(h.monitor.idleSince)
        XCTAssertGreaterThan(relaunched, launched)

        Thread.sleep(forTimeInterval: 0.002)
        h.postMusic(Songs.first, "Playing")
        h.scheduler.drain()
        let played = try XCTUnwrap(h.monitor.idleSince)
        XCTAssertGreaterThan(played, relaunched, "Pressing Play is when the lyrics are looked for")
        h.postMusic(Songs.first, "Playing")
        h.scheduler.drain()
        XCTAssertEqual(h.monitor.idleSince, played, "Still playing: the notice fades as it was")
        XCTAssertEqual(h.music.log, [], "Nothing is ever sent to it")
        XCTAssertFalse(h.permission.asks.contains(true))
    }

    /// The check says Music is allowed while macOS refuses its Apple events.
    /// Each notification would cost a check that says yes and a read that
    /// is refused, so after one such round only Lunalyr becoming active, or
    /// Music relaunching, tries again.
    @MainActor
    func testACheckThatSaysAllowedWhileEventsAreRefusedIsNotRetriedOnEveryNotification() throws {
        let h = harness([Songs.first, Songs.second])
        h.start()
        XCTAssertEqual(h.monitor.current?.title, "First Light")
        h.music.failure = AppleEventCode.notPermitted
        h.monitor.tick()
        h.scheduler.drain()
        XCTAssertEqual(h.monitor.deniedPlayers, [.appleMusic])

        let sent = h.music.log.count
        h.postMusic(Songs.second, "Playing")
        h.scheduler.drain()
        XCTAssertEqual(h.music.log.count, sent + 1, "One read, once the check said yes")
        XCTAssertEqual(h.monitor.automation[.appleMusic], .denied)
        let since = try XCTUnwrap(h.monitor.idleSince)
        for song in [Songs.first, Songs.second, Songs.first] { h.postMusic(song, "Playing") }
        h.scheduler.drain()
        XCTAssertEqual(h.music.log.count, sent + 1, "No more, however many notifications")
        XCTAssertEqual(h.monitor.idleSince, since, "Nor does the notice keep coming back")
        XCTAssertFalse(h.permission.asks.contains(true))

        // Becoming active still tries, and a read that is answered ends it.
        h.music.failure = nil
        h.environment.activate()
        h.scheduler.drain()
        XCTAssertEqual(h.monitor.automation[.appleMusic], .granted)
        XCTAssertEqual(h.monitor.current?.title, "First Light")
        XCTAssertTrue(h.monitor.isPolling)
    }

    /// The check said allowed, then the first Apple event was refused --
    /// the user chose Don't Allow while it was in flight, say. The check
    /// agrees from then on, so the refusal is an ordinary one: Music's
    /// notifications keep checking, and notice access switched on later.
    @MainActor
    func testARefusalTheCheckThenAgreesWithIsStillRecheckedOnNotifications() {
        let music = FakeMusicApp()
        music.queue = [Songs.first]
        let environment = FakePlayerEnvironment(running: ["com.apple.Music"])
        let scheduler = ManualScheduler(split: Self.splitQueues)
        let permission = PermissionStub()
        let runner = RefusedUntilAllowed(music)
        runner.whileRefused = { permission.status = OSStatus(AppleEventCode.notPermitted) }
        let monitor = NowPlayingMonitor(runner: runner, environment: environment, scheduler: scheduler,
                                        permission: { permission.check($0, $1) }, services: .offline,
                                        uptime: { 1_000 }, pollInterval: nil)
        monitor.start()
        scheduler.drain()
        XCTAssertEqual(monitor.deniedPlayers, [.appleMusic])

        let checks = permission.asks.count
        environment.post("com.apple.Music.playerInfo", FakeMusicApp.notification(Songs.first, state: "Playing"))
        scheduler.drain()
        XCTAssertEqual(permission.asks.count, checks + 1, "Music's notification checks again")

        permission.status = 0
        runner.refusing = false
        environment.post("com.apple.Music.playerInfo", FakeMusicApp.notification(Songs.first, state: "Playing"))
        scheduler.drain()
        XCTAssertEqual(monitor.automation[.appleMusic], .granted)
        XCTAssertEqual(monitor.current?.title, "First Light")
        XCTAssertFalse(permission.asks.contains(true))
    }

    @MainActor
    func testUndecidedAutomationPromptsOnlyWhenAsked() {
        // An existing user whose Music access macOS has never asked about.
        let h = harness([Songs.first])
        h.permission.status = OSStatus(AppleEventCode.wouldRequireConsent)
        h.start()
        XCTAssertEqual(h.monitor.automation[.appleMusic], .needsConsent)
        XCTAssertEqual(h.music.log, [], "An Apple event would raise macOS's prompt unannounced")
        XCTAssertEqual(h.monitor.blockedPlayers, [.appleMusic])
        XCTAssertEqual(h.monitor.deniedPlayers, [], "Not refused, only not asked")
        XCTAssertFalse(h.monitor.isPolling)
        h.monitor.recheckAutomation()
        h.scheduler.drain()
        h.environment.activate()
        for _ in 0..<10 { h.monitor.tick() }
        h.scheduler.drain()
        XCTAssertEqual(h.permission.asks, [false, false, false], "Re-checks never prompt")
        XCTAssertEqual(h.music.log, [])

        // Allow Access in the menu: macOS asks, the user chooses OK.
        h.permission.answers["com.apple.Music"] = 0
        h.monitor.requestAutomationAccess(for: .appleMusic)
        h.scheduler.drain()
        XCTAssertEqual(h.permission.asks, [false, false, false, true])
        XCTAssertEqual(h.monitor.automation[.appleMusic], .granted)
        XCTAssertEqual(h.monitor.current?.title, "First Light")
        XCTAssertTrue(h.monitor.isPolling)
    }

    @MainActor
    func testAnUndecidedPlayerIsAskedAboutOnceTheUserPlaysSomethingInIt() {
        let h = harness([Songs.first], state: .paused)
        h.permission.status = OSStatus(AppleEventCode.wouldRequireConsent)
        h.permission.answers["com.apple.Music"] = 0
        h.start()
        h.postMusic(Songs.first, "Paused")
        h.scheduler.drain()
        XCTAssertEqual(h.permission.prompts("com.apple.Music"), 0, "A paused player is not reason enough")
        XCTAssertEqual(h.music.log, [])

        h.music.state = .playing
        h.postMusic(Songs.first, "Playing")
        h.scheduler.drain()
        XCTAssertEqual(h.permission.prompts("com.apple.Music"), 1, "Playing something in it asks")
        XCTAssertEqual(h.monitor.current?.title, "First Light", "Read once allowed")
        XCTAssertTrue(h.monitor.isPlaying)
    }

    @MainActor
    func testARefusedPromptIsNotRaisedAgain() {
        let h = harness([Songs.first])
        h.permission.status = OSStatus(AppleEventCode.wouldRequireConsent)
        h.permission.answers["com.apple.Music"] = OSStatus(AppleEventCode.notPermitted)
        h.start()
        h.postMusic(Songs.first, "Playing")
        h.scheduler.drain()
        XCTAssertEqual(h.permission.prompts("com.apple.Music"), 1)
        XCTAssertEqual(h.monitor.deniedPlayers, [.appleMusic], "Don't Allow: the banner points to System Settings")
        for song in Songs.numbered(3) { h.postMusic(song, "Playing") }
        h.environment.activate()
        h.scheduler.drain()
        XCTAssertEqual(h.permission.prompts("com.apple.Music"), 1)
        XCTAssertEqual(h.music.log, [])
    }

    @MainActor
    func testTheFirstRunWelcomeLetsMacOSAskAtOnce() {
        // The welcome has explained the prompt that follows Continue.
        let h = harness([Songs.first])
        h.permission.status = OSStatus(AppleEventCode.wouldRequireConsent)
        h.permission.answers["com.apple.Music"] = 0
        h.start(promptsExplained: true)
        XCTAssertEqual(h.permission.requests.map(\.ask), [false, true])
        XCTAssertEqual(h.monitor.current?.title, "First Light")
    }

    @MainActor
    func testACheckThatLandsWhileMacOSIsAskingChangesNothing() {
        let h = harness([Songs.first])
        h.permission.status = OSStatus(AppleEventCode.wouldRequireConsent)
        h.permission.answers["com.apple.Music"] = 0
        var seen: [AutomationAccess?] = []
        let watch = h.monitor.$automation.sink { seen.append($0[.appleMusic]) }
        defer { watch.cancel() }
        h.monitor.start(promptsExplained: true)
        h.scheduler.step() // the silent check: macOS has not asked yet
        h.monitor.recheckAutomation() // the menu opens meanwhile
        h.scheduler.drain() // macOS asks; the menu's re-check is answered first
        XCTAssertEqual(h.permission.requests.map(\.ask), [false, false, true])
        XCTAssertFalse(seen.contains(.needsConsent), "No Allow Access banner over macOS's own prompt")
        XCTAssertEqual(h.monitor.automation[.appleMusic], .granted)
        XCTAssertEqual(h.monitor.current?.title, "First Light")
    }

    @MainActor
    func testRapidSkipsCoalesceIntoOneReadOfTheLatestTrack() {
        let h = harness(Songs.numbered(6))
        h.start()
        h.music.clearLog()
        for _ in 0..<5 { h.monitor.nextTrack() }
        h.scheduler.drain()
        XCTAssertEqual(h.music.count("music.next"), 5, "Every press reaches Music")
        XCTAssertEqual(h.music.count("music.read"), 1, "Reads coalesce to the latest request")
        XCTAssertEqual(h.music.count("music.artwork.data"), 1)
        XCTAssertEqual(h.monitor.current?.title, "Song 6")
        XCTAssertEqual(h.monitor.artworkTrack?.title, "Song 6")
    }

    @MainActor
    func testRapidNotificationsNeedNoMetadataScriptsAndCoalesceTheRest() {
        let h = harness(Songs.numbered(5))
        h.start()
        h.music.clearLog()
        for index in 1..<5 {
            h.music.index = index
            h.postMusic(h.music.queue[index], "Playing")
        }
        XCTAssertEqual(h.monitor.current?.title, "Song 5")
        XCTAssertNil(h.monitor.currentTrackArtwork, "Never the previous song's cover")
        XCTAssertNotNil(h.monitor.artwork, "Its colours stay until the new cover resolves")
        h.scheduler.drain()
        XCTAssertEqual(h.music.count("music.read"), 0, "The notification carried the metadata")
        XCTAssertEqual(h.music.count("music.position"), 1)
        XCTAssertEqual(h.music.count("music.artwork.data"), 1)
        XCTAssertEqual(h.monitor.artworkTrack?.title, "Song 5")
        XCTAssertNotNil(h.monitor.currentTrackArtwork)
        XCTAssertEqual(h.monitor.current?.persistentID, String(format: "%016X", 5))
    }

    @MainActor
    func testAReplyInFlightDuringANotificationIsDiscarded() {
        let h = harness([Songs.first], position: 10)
        h.start()
        let music = h.music
        // The re-sync asks while playing; the user pauses in Music before the reply lands.
        music.onReply = { name in
            guard name == "music.position" else { return }
            music.onReply = nil
            music.state = .paused
            MainActor.assumeIsolated { h.postMusic(Songs.first, "Paused") }
        }
        h.monitor.tick()
        h.scheduler.drain()
        XCTAssertFalse(h.monitor.isPlaying, "The stale 'playing' reply must not undo the pause")
        XCTAssertEqual(h.monitor.current?.title, "First Light")
    }

    @MainActor
    func testATrackChangeBetweenMetadataAndPositionIsReadAgain() {
        let music = FakeMusicApp()
        music.queue = [Songs.first, Songs.second]
        music.onReply = { name in
            guard name == "music.read" else { return }
            music.onReply = nil
            music.index = 1
        }
        let h = PlayerHarness(music: music)
        h.start()
        XCTAssertEqual(music.count("music.read"), 2)
        XCTAssertEqual(h.monitor.current?.title, "Second Wind")
        XCTAssertEqual(h.monitor.current?.persistentID, Songs.second.id)
    }

    @MainActor
    func testStreamsPublishNoDurationAndCannotBeSeeked() {
        let radio = FakeMusicApp.Song(id: "00000000000000C1", name: "Radio Paradise", artist: "", album: "", genre: "",
                                      duration: nil, streamTitle: "Ozric Tentacles - Erpland", artwork: nil)
        let h = harness([radio], position: 1234)
        h.start()
        let track = h.monitor.current
        XCTAssertEqual(track?.isStream, true)
        XCTAssertEqual(track?.duration, 0)
        XCTAssertEqual(track?.title, "Ozric Tentacles - Erpland", "Titled by what the station is playing")
        XCTAssertEqual(track?.artist, "Radio Paradise")
        XCTAssertNil(track?.album)
        XCTAssertNil(h.monitor.artwork, "Streams are never matched against the catalogue")
        h.monitor.seek(to: 10)
        h.scheduler.drain()
        XCTAssertEqual(h.music.count("music.seek"), 0)

        // A notification without a duration is confirmed with the script, not trusted.
        h.music.clearLog()
        h.postMusic(radio, "Playing")
        h.scheduler.drain()
        XCTAssertEqual(h.music.count("music.read"), 1)
        XCTAssertEqual(h.monitor.current?.isStream, true)
    }

    @MainActor
    func testTheResyncReanchorsWhilePausedAndFindsUnannouncedPlayback() {
        let h = harness([Songs.first], state: .paused, position: 42)
        h.start()
        h.music.position = 90
        h.monitor.tick()
        h.scheduler.drain()
        XCTAssertEqual(h.monitor.playbackTime(), 90, accuracy: 0.001, "Scrubbed in Music while paused")

        h.music.state = .stopped
        h.environment.post("com.apple.Music.playerInfo", ["Player State": "Stopped"])
        XCTAssertNil(h.monitor.current)
        h.music.state = .playing
        for _ in 0..<5 { h.monitor.tick() }
        h.scheduler.drain()
        XCTAssertEqual(h.monitor.current?.title, "First Light", "Playing without a notification is found")
        XCTAssertTrue(h.monitor.isPlaying)
    }

    @MainActor
    func testTheResyncEasesSmallDriftInsteadOfJumping() {
        let h = harness([Songs.first], position: 10)
        h.start()
        h.clock.now += 2
        h.music.position = 12.1
        h.monitor.tick()
        h.scheduler.drain()
        XCTAssertEqual(h.monitor.playbackTime(), 12.16, accuracy: 1e-6, "No jump at the re-sync")
        h.clock.now += PlaybackClock.slewDuration
        XCTAssertEqual(h.monitor.playbackTime(), 12.1 + PlaybackClock.slewDuration + 0.16, accuracy: 1e-6)
    }

    @MainActor
    func testASeekMovesAtOnceAndIgnoresSamplesFromBeforeIt() {
        let h = harness([Songs.first], position: 10)
        h.start()
        h.monitor.tick()
        h.monitor.seek(to: 100)
        XCTAssertEqual(h.monitor.playbackTime(), 100.16, accuracy: 0.001, "The highlight moves at once")
        h.scheduler.drain()
        XCTAssertEqual(h.music.position, 100)
        XCTAssertEqual(h.monitor.playbackTime(), 100.16, accuracy: 0.001, "The queued pre-seek sample is dropped")
    }

    /// As in the app: the main actor handles the re-sync's reply while the
    /// seek's Apple event is still on its way to Music.
    @MainActor
    func testASampleFromBeforeASeekNeverPullsTheHighlightBack() {
        let h = harness([Songs.first], position: 10, split: true)
        h.start()
        h.monitor.tick()
        h.monitor.seek(to: 100)
        XCTAssertEqual(h.monitor.playbackTime(), 100.16, accuracy: 0.001)
        h.scheduler.runBackground() // the re-sync queued first: Music is still at 10
        h.scheduler.runMain()
        XCTAssertEqual(h.monitor.playbackTime(), 100.16, accuracy: 0.001, "Sampled before the seek took effect")
        h.scheduler.drain()
        XCTAssertEqual(h.music.position, 100)
        XCTAssertEqual(h.monitor.playbackTime(), 100.16, accuracy: 0.001)

        // Two seeks in a row: what Music reports between them is stale too.
        h.monitor.seek(to: 50)
        h.monitor.tick()
        h.monitor.seek(to: 150)
        XCTAssertEqual(h.monitor.playbackTime(), 150.16, accuracy: 0.001)
        while h.scheduler.pending > 0 {
            h.scheduler.runBackground()
            h.scheduler.runMain()
            XCTAssertEqual(h.monitor.playbackTime(), 150.16, accuracy: 0.001)
        }
        XCTAssertEqual(h.music.position, 150)
        h.clock.now += 1
        XCTAssertEqual(h.monitor.playbackTime(), 151.16, accuracy: 0.001, "Re-anchored after the last seek")
    }

    @MainActor
    func testANewSongNeverShowsThePreviousSongsCover() async {
        var bare = Songs.second
        bare.artwork = nil
        let searches = CallCounter()
        var services = NowPlayingServices.offline
        services.catalogueArtwork = { track in
            searches.record(track.title)
            return nil
        }
        let h = harness([Songs.first, bare], services: services)
        h.start()
        XCTAssertEqual(h.monitor.artworkTrack?.title, "First Light")
        h.music.index = 1
        h.postMusic(bare, "Playing")
        XCTAssertNil(h.monitor.currentTrackArtwork)
        XCTAssertNotNil(h.monitor.artwork)
        h.scheduler.drain()
        await h.settle { h.monitor.artworkTrack?.title == "Second Wind" }
        XCTAssertEqual(searches.values, ["Second Wind"])
        XCTAssertNil(h.monitor.artwork, "No cover found: fall back rather than keep another song's")
        XCTAssertNil(h.monitor.currentTrackArtwork)
    }

    /// Music moves on while the previous song's cover is being read, as it
    /// does when the user skips again quickly. With either queue order.
    @MainActor
    func testACoverReadAsTheSongChangesIsNeverShownForTheNextSong() async {
        for split in [false, true] {
            var bare = Songs.second
            bare.artwork = nil
            let music = FakeMusicApp()
            music.queue = [Songs.first, bare]
            let h = PlayerHarness(music: music, split: split)
            music.onReply = { name in
                guard name == "music.artwork.data" else { return }
                music.onReply = nil
                music.index = 1
                MainActor.assumeIsolated { h.postMusic(bare, "Playing") }
            }
            h.start()
            XCTAssertEqual(h.monitor.current?.title, "Second Wind")
            XCTAssertEqual(h.monitor.current?.persistentID, bare.id, "Known from the notification itself")
            XCTAssertNil(h.monitor.currentTrackArtwork, "First Light's cover is not Second Wind's (split: \(split))")
            await h.settle { h.monitor.artworkTrack?.title == "Second Wind" }
            XCTAssertNil(h.monitor.currentTrackArtwork, "Second Wind has no cover (split: \(split))")
            XCTAssertNil(h.monitor.artwork)
        }
    }

    /// The previous song had no cover. Its reply must not start a catalogue
    /// search for the next song, which has one -- nor end it.
    @MainActor
    func testAPreviousSongsMissingCoverStartsNoSearchForTheNextSong() async {
        for split in [false, true] {
            var bare = Songs.first
            bare.artwork = nil
            let searches = CallCounter()
            var services = NowPlayingServices.offline
            services.catalogueArtwork = { track in
                searches.record(track.title)
                return nil
            }
            let music = FakeMusicApp()
            music.queue = [bare, Songs.second]
            let h = PlayerHarness(music: music, services: services, split: split)
            music.onReply = { name in
                guard name == "music.artwork.data" else { return }
                music.onReply = nil
                music.index = 1
                MainActor.assumeIsolated { h.postMusic(Songs.second, "Playing") }
            }
            h.start()
            await h.settle(until: { false }, timeout: 0.2)
            XCTAssertEqual(h.monitor.current?.title, "Second Wind")
            XCTAssertEqual(searches.values, [], "Second Wind has its own cover (split: \(split))")
            XCTAssertEqual(h.monitor.artworkTrack?.title, "Second Wind")
            XCTAssertNotNil(h.monitor.currentTrackArtwork, "Its own cover survives (split: \(split))")
        }
    }

    @MainActor
    func testABurstOfSkipsReadsOnlyTheLastSongsCover() {
        for split in [false, true] {
            let h = harness(Songs.numbered(6), split: split)
            h.start()
            h.music.clearLog()
            for index in 1..<6 {
                h.music.index = index
                h.postMusic(h.music.queue[index], "Playing")
            }
            h.scheduler.drain()
            XCTAssertEqual(h.music.count("music.artwork.data"), 1, "Superseded reads send nothing (split: \(split))")
            XCTAssertEqual(h.monitor.artworkTrack?.title, "Song 6")
            XCTAssertNotNil(h.monitor.currentTrackArtwork)
        }
    }

    @MainActor
    func testANotificationForTheSameSongIsNeverANewTrack() {
        let h = harness([Songs.first], position: 30)
        h.start()
        let covers = h.music.count("music.artwork.data")
        h.clock.now += 5
        // The same song, its identifier spelled some other way.
        var info = FakeMusicApp.notification(Songs.first, state: "Playing")
        info["PersistentID"] = NSNumber(value: Int64(12_345))
        h.environment.post("com.apple.Music.playerInfo", info)
        XCTAssertEqual(h.monitor.playbackTime(), 35.16, accuracy: 0.001, "The clock carries on; the song does not restart")
        h.scheduler.drain()
        XCTAssertEqual(h.music.count("music.artwork.data"), covers, "Its cover is not read again")
        XCTAssertEqual(h.monitor.current?.persistentID, Songs.first.id)
    }

    func testMusicsNotificationAndScriptsSpellTheSameIdentifier() {
        // Music's notification states the 64 bits as a signed number.
        XCTAssertEqual(MusicBackend.persistentID(fromNotification: NSNumber(value: Int64(-4_835_784_534_528_829_385))),
                       "BCE3D7964114D837")
        XCTAssertEqual(MusicBackend.persistentID(fromNotification: NSNumber(value: Int64(0x0123_4567_89AB_CDEF))),
                       "0123456789ABCDEF")
        XCTAssertEqual(MusicBackend.persistentID(fromNotification: "-4835784534528829385"), "BCE3D7964114D837")
        XCTAssertNil(MusicBackend.persistentID(fromNotification: nil))
        XCTAssertNil(MusicBackend.persistentID(fromNotification: NSNumber(value: 0)))
        // AppleScript states them as hexadecimal text.
        XCTAssertEqual(MusicBackend.canonicalID("bce3d7964114d837"), "BCE3D7964114D837")
        XCTAssertEqual(MusicBackend.canonicalID(" 123ABC "), "0000000000123ABC")
        XCTAssertEqual(MusicBackend.canonicalID("not-hex"), "not-hex")
        XCTAssertNil(MusicBackend.canonicalID(""))
        let info = FakeMusicApp.notification(Songs.second, state: "Playing")
        XCTAssertEqual(MusicBackend().snapshot(fromNotification: info)?.track?.persistentID, Songs.second.id)
    }

    @MainActor
    func testAnimatedCoversLookUpOnlyWhereShownAndHoldAcrossAnAlbum() async {
        let lookups = CallCounter()
        let dawn = MotionArtworkProvider.Motion(square: URL(string: "https://mvod.itunes.apple.com/dawn.m3u8")!, tall: nil)
        var allowed = false
        let settingsChanged = PassthroughSubject<Void, Never>()
        var services = NowPlayingServices.offline
        services.motionArtwork = { track in
            lookups.record(track.album ?? "")
            return track.album == "Dawn" ? dawn : nil
        }
        services.allowsMotionLookup = { _ in allowed }
        services.motionSettingsChanged = settingsChanged.eraseToAnyPublisher()
        let h = harness([Songs.first, Songs.second, Songs.other], services: services)
        var shown: [URL?] = []
        let watch = h.monitor.$motionArtwork.removeDuplicates().sink { shown.append($0?.square) }
        defer { watch.cancel() }
        h.start()
        XCTAssertFalse(h.monitor.isLookingUpMotion)
        XCTAssertEqual(lookups.values, [], "Not looked up while covers cannot be shown")

        allowed = true
        settingsChanged.send()
        await h.settle { h.monitor.motionArtwork != nil }
        XCTAssertEqual(h.monitor.motionArtwork, dawn)

        h.music.index = 1
        h.postMusic(Songs.second, "Playing")
        h.scheduler.drain()
        XCTAssertEqual(h.monitor.motionArtwork, dawn, "Same album: the cover keeps playing")
        XCTAssertFalse(h.monitor.isLookingUpMotion)

        h.music.index = 2
        h.postMusic(Songs.other, "Playing")
        XCTAssertEqual(h.monitor.motionArtwork, dawn, "Kept until a different answer arrives")
        await h.settle { !h.monitor.isLookingUpMotion }
        XCTAssertNil(h.monitor.motionArtwork)
        XCTAssertEqual(lookups.values, ["Dawn", "Far"])
        XCTAssertEqual(shown, [nil, dawn.square, nil], "No cut away and back within an album")
    }

    func testThePlayerShownIsThePlayingOneElseTheMostRecent() {
        let track = NowPlayingMonitor.Track(title: "T", artist: "A", duration: 100, genre: nil, album: nil)
        let paused = PlayerSnapshot(state: .paused, track: track)
        let stopped = PlayerSnapshot(state: .stopped, track: track)
        XCTAssertEqual(NowPlayingMonitor.choosePlayer(from: [.appleMusic: paused], activity: [:], among: [.appleMusic]), .appleMusic)
        XCTAssertNil(NowPlayingMonitor.choosePlayer(from: [.appleMusic: stopped], activity: [:], among: [.appleMusic]))
        XCTAssertNil(NowPlayingMonitor.choosePlayer(from: [.appleMusic: paused], activity: [:], among: []))
    }
}

/// `NowPlayingMonitorTests` in the app's order: each reply handled at once.
final class NowPlayingMonitorSplitQueueTests: NowPlayingMonitorTests {
    override class var splitQueues: Bool { true }
}
