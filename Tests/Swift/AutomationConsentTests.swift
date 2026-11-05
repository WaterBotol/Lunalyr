import AppKit
import Combine
import XCTest
@testable import VersoCustom

private enum Tracks {
    static let first = FakeMusicApp.Song(id: "00000000000000A1", name: "First Light", album: "Dawn")
    static let second = FakeMusicApp.Song(id: "00000000000000A2", name: "Second Wind", album: "Dawn")
    static let lights = FakeSpotifyApp.Song(id: "spotify:track:0VjIjW4GlUZAMYd2vXMi3b", name: "Blinding Lights",
                                            artist: "The Weeknd", album: "After Hours", milliseconds: 200_040)
    static let breathe = FakeSpotifyApp.Song(id: "spotify:track:breathe", name: "Breathe", artist: "Télépopmusik",
                                             album: "Genetic World", milliseconds: 280_000)
}

/// macOS's Automation prompt as the first Apple event to a player meets it.
/// The event waits on the prompt only as long as its own timeout, and the
/// prompt goes when the event gives up -- so with a six-second timeout and a
/// two-second poll, anyone who did not answer at once got a fresh prompt
/// every few seconds. Most tests here have the silent permission check
/// report the previous build's grant, as it can after an update with a new
/// ad-hoc signature, while macOS still asks on the first event.
///
/// Every test runs twice, as `NowPlayingMonitorTests` do: as is, and with
/// split queues in `AutomationConsentSplitQueueTests`.
class AutomationConsentTests: XCTestCase {
    let musicID = "com.apple.Music"
    let spotifyID = "com.spotify.client"
    private let consentTimeout = TimeInterval(PlayerScriptSource.consentTimeout)

    class var splitQueues: Bool { false }

    override func setUp() {
        super.setUp()
        PlayerHarness.splitQueuesByDefault = Self.splitQueues
    }

    override func tearDown() {
        PlayerHarness.splitQueuesByDefault = false
        super.tearDown()
    }

    /// Music playing; the silent check reports a grant macOS will ask about anyway.
    @MainActor
    private func staleGrant(_ songs: [FakeMusicApp.Song] = [Tracks.first]) -> PlayerHarness {
        let music = FakeMusicApp()
        music.queue = songs
        let h = PlayerHarness(music: music)
        h.permission.status = 0
        h.consent.undecided = [musicID]
        return h
    }

    @MainActor
    func testAStaleGrantStillGetsOnePromptThatWaitsForTheAnswer() {
        let h = staleGrant()
        h.consent.answers[musicID] = .init(allow: true, after: 45)
        h.consent.whileShown = { _ in
            MainActor.assumeIsolated {
                // The user takes their time. Lunalyr carries on meanwhile.
                for _ in 0..<10 { h.monitor.tick() }
                h.postMusic(Tracks.first, "Playing")
                h.monitor.refresh()
                h.monitor.recheckAutomation()
                h.environment.activate()
                h.monitor.requestAutomationAccess(for: .appleMusic)
            }
        }
        h.start()
        XCTAssertEqual(h.consent.prompts(musicID), 1)
        XCTAssertEqual(h.consent.shown.first?.timeout, consentTimeout, "The first event waits for the answer")
        XCTAssertGreaterThanOrEqual(consentTimeout, 120, "At least as long as AppleScript's own two minutes")
        XCTAssertEqual(h.permission.prompts(musicID), 0, "Nothing asked over the prompt")
        XCTAssertEqual(h.monitor.automation[.appleMusic], .granted)
        XCTAssertEqual(h.monitor.current?.title, "First Light")
        XCTAssertTrue(h.monitor.isPolling)

        // Allowed: polling as before, with the short timeout.
        h.players.clearSent()
        for _ in 0..<5 {
            h.clock.now += 2
            h.monitor.tick()
            h.scheduler.drain()
        }
        XCTAssertEqual(h.players.sent.map(\.name), Array(repeating: "music.position", count: 5))
        XCTAssertEqual(Set(h.players.sent.map(FakeAutomationPrompt.timeout(of:))),
                       [TimeInterval(PlayerScriptSource.replyTimeout)])
        XCTAssertEqual(h.consent.prompts(musicID), 1)
    }

    /// Asking again is the user's doing: Allow Access here, playing
    /// something in the player in `testPlayingInThePlayerAsksAgainAfterAnUnansweredPrompt`.
    @MainActor
    func testAnUnansweredPromptIsConsentPendingUntilTheUserAsksAgain() {
        let h = staleGrant([Tracks.first, Tracks.second])
        h.start()
        XCTAssertEqual(h.consent.prompts(musicID), 1)
        XCTAssertEqual(h.monitor.automation[.appleMusic], .needsConsent, "Consent pending, not a dead player")
        XCTAssertEqual(h.monitor.blockedPlayers, [.appleMusic], "The menu offers Allow Access")
        XCTAssertEqual(h.monitor.deniedPlayers, [], "Nobody refused anything")
        XCTAssertEqual(h.monitor.runningPlayers, [.appleMusic])
        XCTAssertNil(h.monitor.current)
        XCTAssertFalse(h.monitor.isPolling)
        XCTAssertFalse(h.monitor.canControlPlayback)

        // Minutes of ticks, re-checks, activations and notifications that
        // are not someone starting to play.
        let sent = h.players.sent.count, checks = h.permission.requests.count
        var published: [AutomationAccess?] = []
        let watch = h.monitor.$automation.dropFirst().sink { published.append($0[.appleMusic]) }
        for _ in 0..<60 {
            h.clock.now += 2
            h.monitor.tick()
        }
        h.postMusic(Tracks.first, "Paused")
        h.postMusic(Tracks.second, "Paused")
        h.monitor.refresh()
        h.monitor.playPause()
        h.monitor.recheckAutomation()
        h.environment.activate()
        h.scheduler.drain()
        watch.cancel()
        XCTAssertEqual(published, [], "The Allow Access banner never blinks away for an old grant")
        XCTAssertEqual(h.players.sent.count, sent, "Nothing re-sends")
        XCTAssertEqual(h.consent.prompts(musicID), 1)
        XCTAssertEqual(h.permission.prompts(musicID), 0, "Nothing re-asks")
        XCTAssertGreaterThan(h.permission.requests.count, checks, "Activation still re-checks, silently")
        XCTAssertEqual(h.monitor.automation[.appleMusic], .needsConsent, "The old grant a re-check reports changes nothing")

        // Allow Access: one new prompt, answered this time.
        h.consent.answers[musicID] = .init(allow: true, after: 4)
        h.monitor.requestAutomationAccess(for: .appleMusic)
        h.scheduler.drain()
        XCTAssertEqual(h.permission.prompts(musicID), 1)
        XCTAssertEqual(h.consent.prompts(musicID), 2)
        XCTAssertEqual(h.monitor.automation[.appleMusic], .granted)
        XCTAssertEqual(h.monitor.current?.title, "First Light")
        XCTAssertTrue(h.monitor.isPolling)
    }

    @MainActor
    func testWorkQueuedWhileThePromptIsUpIsNeverSentIfNobodyAnswers() {
        let h = staleGrant()
        h.consent.whileShown = { _ in
            MainActor.assumeIsolated {
                // The track shows from Music's own notification; its position
                // and cover and re-syncs queue up meanwhile. A seek and a
                // skip are dropped at once (`testButtonsPressedWhileAPromptHolds…`).
                h.postMusic(Tracks.first, "Playing")
                h.clock.now += 1
                h.postMusic(Tracks.first, "Paused")
                for _ in 0..<5 { h.monitor.tick() }
                h.monitor.seek(to: 30)
                h.monitor.nextTrack()
                h.monitor.refresh()
            }
        }
        h.start()
        XCTAssertEqual(h.players.sent.map(\.name), ["music.read"], "Only the event that raised the prompt")
        XCTAssertEqual(h.music.log, [], "And it never got past it")
        XCTAssertEqual(h.consent.prompts(musicID), 1)
        XCTAssertEqual(h.monitor.automation[.appleMusic], .needsConsent)
        XCTAssertNil(h.monitor.current, "Nothing is shown from a player Lunalyr may not read")
        XCTAssertFalse(h.monitor.isPolling)
    }

    @MainActor
    func testDontAllowOnTheFirstEventsPromptStopsEverySend() {
        let h = staleGrant()
        h.consent.answers[musicID] = .init(allow: false, after: 8)
        h.start()
        XCTAssertEqual(h.consent.prompts(musicID), 1)
        XCTAssertEqual(h.monitor.automation[.appleMusic], .denied)
        XCTAssertEqual(h.monitor.deniedPlayers, [.appleMusic], "The banner points to System Settings")
        XCTAssertFalse(h.monitor.isPolling)

        let sent = h.players.sent.count
        for _ in 0..<10 {
            h.clock.now += 2
            h.monitor.tick()
        }
        h.postMusic(Tracks.first, "Playing")
        h.monitor.playPause()
        h.monitor.refresh()
        h.scheduler.drain()
        XCTAssertEqual(h.players.sent.count, sent, "Nothing more is sent")

        // Activation keeps its cheap silent re-check, which finds the refusal.
        h.permission.status = OSStatus(AppleEventCode.notPermitted)
        for _ in 0..<3 {
            h.environment.activate()
            h.scheduler.drain()
        }
        XCTAssertEqual(h.players.sent.count, sent)
        XCTAssertEqual(h.monitor.deniedPlayers, [.appleMusic])

        // Should the check go on reporting the old grant, an activation costs
        // one event, refused at once: macOS never asks again.
        h.permission.status = 0
        h.environment.activate()
        h.scheduler.drain()
        XCTAssertEqual(h.players.sent.count, sent + 1)
        XCTAssertEqual(h.monitor.deniedPlayers, [.appleMusic])
        XCTAssertEqual(h.consent.prompts(musicID), 1)
        XCTAssertFalse(h.permission.asks.contains(true), "Never prompts")
    }

    @MainActor
    func testAnUnansweredPromptForOnePlayerLeavesTheOtherPolling() {
        let music = FakeMusicApp()
        music.queue = [Tracks.first]
        let spotify = FakeSpotifyApp()
        spotify.queue = [Tracks.lights]
        spotify.state = .paused
        let h = PlayerHarness(music: music, spotify: spotify, running: [musicID, spotifyID])
        h.consent.undecided = [spotifyID] // only Spotify's grant is the old build's
        h.start()
        XCTAssertEqual(h.consent.prompts(spotifyID), 1)
        XCTAssertEqual(h.consent.prompts(musicID), 0)
        XCTAssertEqual(h.monitor.automation[.spotify], .needsConsent)
        XCTAssertEqual(h.monitor.automation[.appleMusic], .granted)
        XCTAssertEqual(h.monitor.activePlayer, .appleMusic)
        XCTAssertTrue(h.monitor.isPolling)

        let toSpotify = h.players.sent(to: spotifyID).count
        let toMusic = h.players.sent(to: musicID).count
        for _ in 0..<10 {
            h.clock.now += 2
            h.monitor.tick()
            h.scheduler.drain()
        }
        h.postSpotify(Tracks.lights, "Paused", at: 3)
        h.scheduler.drain()
        XCTAssertEqual(h.players.sent(to: spotifyID).count, toSpotify, "Spotify waits to be asked again")
        XCTAssertEqual(h.players.sent(to: musicID).count, toMusic + 10, "Music carries on")
        XCTAssertEqual(h.consent.prompts(spotifyID), 1)
        XCTAssertEqual(h.monitor.activePlayer, .appleMusic)
    }

    @MainActor
    func testChoosingThePlayersAgainDoesNotRaiseAnUnansweredPromptAgain() {
        let choice = CurrentValueSubject<[PlayerKind], Never>(PlayerKind.allCases)
        var services = NowPlayingServices.offline
        services.followedPlayers = choice.eraseToAnyPublisher()
        let music = FakeMusicApp()
        music.queue = [Tracks.first]
        let h = PlayerHarness(music: music, services: services)
        h.permission.status = 0
        h.consent.undecided = [musicID]
        h.start()
        XCTAssertEqual(h.monitor.automation[.appleMusic], .needsConsent)
        choice.send([.spotify])
        h.scheduler.drain()
        choice.send(PlayerKind.allCases)
        h.scheduler.drain()
        h.postMusic(Tracks.first, "Paused")
        h.scheduler.drain()
        XCTAssertEqual(h.consent.prompts(musicID), 1)
        XCTAssertEqual(h.permission.prompts(musicID), 0)
        XCTAssertEqual(h.players.sent(to: musicID).count, 1)
        XCTAssertEqual(h.monitor.automation[.appleMusic], .needsConsent, "Allow Access still offered")
    }

    /// Reopening Music is not the user asking. Consent is Lunalyr's with the
    /// app, so a prompt left unanswered stays that way across launches, and
    /// each relaunch sends Music nothing -- until the user plays in it.
    @MainActor
    func testReopeningThePlayerDoesNotRaiseAnUnansweredPromptAgain() {
        let h = staleGrant()
        h.start()
        XCTAssertEqual(h.monitor.automation[.appleMusic], .needsConsent)
        // Music plays on, and the user lets the prompt that raises go too.
        h.postMusic(Tracks.first, "Playing")
        h.scheduler.drain()
        XCTAssertEqual(h.consent.prompts(musicID), 2)
        for _ in 0..<3 {
            h.music.running = false
            h.environment.terminate(musicID)
            h.music.running = true
            h.environment.launch(musicID)
            h.scheduler.fireDelayed()
        }
        XCTAssertEqual(h.consent.prompts(musicID), 2)
        XCTAssertEqual(h.permission.prompts(musicID), 1)
        XCTAssertEqual(h.players.sent(to: musicID).count, 2)
        XCTAssertEqual(h.monitor.automation[.appleMusic], .needsConsent, "Allow Access still offered")

        // Music quit while playing; in this launch, Playing is someone starting it.
        h.consent.answers[musicID] = .init(allow: true, after: 2)
        h.postMusic(Tracks.first, "Playing")
        h.scheduler.drain()
        XCTAssertEqual(h.consent.prompts(musicID), 3, "Playing in the new launch asks")
        XCTAssertEqual(h.monitor.automation[.appleMusic], .granted)
        XCTAssertEqual(h.monitor.current?.title, "First Light")
        XCTAssertTrue(h.monitor.isPolling)
    }

    /// Music answered, quit and reopened, and its first event after
    /// that times out while it finishes launching: a busy player polled
    /// again soon, not one that needs permission. Nor is the new launch kept
    /// waiting by the back-off of the one before.
    @MainActor
    func testAPlayerThatAnsweredBeforeItRelaunchedIsTrustedAfter() {
        let music = FakeMusicApp()
        music.queue = [Tracks.first]
        let h = PlayerHarness(music: music)
        h.start()
        XCTAssertEqual(h.monitor.current?.title, "First Light")
        h.music.failure = AppleEventCode.timedOut
        h.clock.now += 2
        h.monitor.tick()
        h.scheduler.drain()
        h.music.running = false
        h.environment.terminate(musicID)
        h.music.running = true
        h.environment.launch(musicID)
        h.music.clearLog()
        h.scheduler.fireDelayed()
        XCTAssertEqual(h.music.log, ["music.read"], "Read at once: the back-off was the last launch's")
        XCTAssertEqual(h.players.sent.filter(\.waitsForConsent).count, 1, "Only the session's first event waited")
        XCTAssertEqual(h.players.sent.last.map(FakeAutomationPrompt.timeout(of:)), TimeInterval(PlayerScriptSource.replyTimeout))
        XCTAssertEqual(h.monitor.automation[.appleMusic], .granted, "Busy, not asking")
        XCTAssertTrue(h.monitor.isPolling)

        h.music.failure = nil
        h.postMusic(Tracks.first, "Playing")
        h.scheduler.drain()
        XCTAssertEqual(h.monitor.current?.title, "First Light")
        XCTAssertEqual(h.consent.prompts(musicID), 0)
        XCTAssertEqual(h.permission.prompts(musicID), 0)
    }

    /// The common case: macOS has no decision on record, says so, and asks
    /// with AEDeterminePermissionToAutomateTarget, which waits for as long as
    /// the user takes. Nothing is sent meanwhile, and the first event after
    /// the answer still allows for the prompt, though it goes straight through.
    @MainActor
    func testNothingIsSentWhileMacOSAsksAndTheAnswerHolds() {
        let music = FakeMusicApp()
        music.queue = [Tracks.first]
        let h = PlayerHarness(music: music)
        h.permission.status = OSStatus(AppleEventCode.wouldRequireConsent)
        h.permission.answers[musicID] = 0
        var sentWhileAsking: [Int] = []
        h.permission.whileAsking = { _ in
            // Only the first prompt acts: a second would be the bug.
            guard sentWhileAsking.isEmpty else { return }
            sentWhileAsking.append(-1)
            MainActor.assumeIsolated {
                for _ in 0..<5 { h.monitor.tick() }
                h.postMusic(Tracks.first, "Playing")
                h.monitor.recheckAutomation()
                h.monitor.requestAutomationAccess(for: .appleMusic)
                // The script queue does not wait for the permission queue.
                h.scheduler.drain()
                sentWhileAsking.append(h.players.sent.count)
            }
        }
        h.start(promptsExplained: true)
        XCTAssertEqual(sentWhileAsking, [-1, 0])
        XCTAssertEqual(h.permission.prompts(musicID), 1, "One prompt")
        XCTAssertEqual(h.consent.prompts(musicID), 0)
        XCTAssertEqual(h.players.sent.first.map(FakeAutomationPrompt.timeout(of:)), consentTimeout)
        XCTAssertEqual(h.monitor.current?.title, "First Light")
        XCTAssertTrue(h.monitor.isPolling)
    }

    @MainActor
    func testOnlyTheFirstEventOfASessionWaitsForConsent() {
        let music = FakeMusicApp()
        music.queue = [Tracks.first, Tracks.second]
        let h = PlayerHarness(music: music)
        h.start()
        h.monitor.nextTrack()
        h.monitor.seek(to: 20)
        h.scheduler.drain()
        for _ in 0..<3 {
            h.clock.now += 2
            h.monitor.tick()
            h.scheduler.drain()
        }
        let timeouts = h.players.sent.map(FakeAutomationPrompt.timeout(of:))
        XCTAssertEqual(timeouts.first, consentTimeout)
        XCTAssertTrue(timeouts.dropFirst().allSatisfy { $0 <= 10 }, "\(timeouts)")
        XCTAssertEqual(h.players.sent.filter(\.waitsForConsent).count, 1)

        h.music.running = false
        h.environment.terminate(musicID)
        h.music.running = true
        h.environment.launch(musicID)
        h.scheduler.fireDelayed()
        XCTAssertEqual(h.players.sent.filter(\.waitsForConsent).count, 1, "Proven before Music relaunched")
        XCTAssertEqual(h.monitor.current?.title, "Second Wind")
    }

    /// Music's prompt went unanswered while Spotify's paused song is
    /// on screen, so the menu shows no Allow Access. Playing something in
    /// Music asks again: the first time it plays, then each time the user
    /// starts it again -- not as it moves on to its next track.
    @MainActor
    func testPlayingInThePlayerAsksAgainAfterAnUnansweredPrompt() {
        let music = FakeMusicApp()
        music.queue = [Tracks.first, Tracks.second]
        music.state = .paused
        let spotify = FakeSpotifyApp()
        spotify.queue = [Tracks.lights]
        spotify.state = .paused
        let h = PlayerHarness(music: music, spotify: spotify, running: [musicID, spotifyID])
        h.consent.undecided = [musicID] // only Music's grant is the old build's
        h.start()
        XCTAssertEqual(h.consent.prompts(musicID), 1)
        XCTAssertEqual(h.monitor.automation[.appleMusic], .needsConsent)
        XCTAssertEqual(h.monitor.current?.title, "Blinding Lights", "So the banner, and its Allow Access, stay hidden")

        // The user plays something in Music, and lets that prompt go too.
        h.music.state = .playing
        h.postMusic(Tracks.first, "Playing")
        h.scheduler.drain()
        XCTAssertEqual(h.consent.prompts(musicID), 2, "Playing in it asks again")
        XCTAssertEqual(h.consent.shown.last?.timeout, consentTimeout, "And the prompt waits for the answer")
        XCTAssertEqual(h.monitor.automation[.appleMusic], .needsConsent)

        // Music moving on to its next track, minutes of re-syncs, activations.
        h.music.index = 1
        h.postMusic(Tracks.second, "Playing")
        for _ in 0..<30 {
            h.clock.now += 2
            h.monitor.tick()
        }
        h.environment.activate()
        h.scheduler.drain()
        XCTAssertEqual(h.consent.prompts(musicID), 2, "Nobody asked")

        // Paused and played again: asked again, and answered this time.
        h.consent.answers[musicID] = .init(allow: true, after: 20)
        h.postMusic(Tracks.second, "Paused")
        h.scheduler.drain()
        XCTAssertEqual(h.consent.prompts(musicID), 2)
        h.postMusic(Tracks.second, "Playing")
        h.scheduler.drain()
        XCTAssertEqual(h.consent.prompts(musicID), 3)
        XCTAssertEqual(h.permission.prompts(musicID), 2, "Each asked first, and the old grant answered at once")
        XCTAssertEqual(h.players.sent(to: musicID).filter(\.waitsForConsent).count, 3, "One event per prompt")
        XCTAssertEqual(h.monitor.automation[.appleMusic], .granted)
        XCTAssertEqual(h.monitor.activePlayer, .appleMusic)
        XCTAssertEqual(h.monitor.current?.title, "Second Wind")
        XCTAssertTrue(h.monitor.isPolling)
    }

    /// After an unanswered prompt macOS has the grant on record
    /// after all. A silent re-check cannot tell it from an old build's, so
    /// activations change nothing; playing something in Music asks, and the
    /// event after that goes straight through without a prompt.
    @MainActor
    func testAGrantMacOSHasLaterIsTakenUpWhenTheUserPlays() {
        let h = staleGrant()
        h.start()
        XCTAssertEqual(h.monitor.automation[.appleMusic], .needsConsent)
        h.consent.undecided = []
        let sent = h.players.sent.count
        for _ in 0..<3 {
            h.environment.activate()
            h.scheduler.drain()
        }
        XCTAssertEqual(h.players.sent.count, sent)
        XCTAssertEqual(h.monitor.automation[.appleMusic], .needsConsent)

        h.postMusic(Tracks.first, "Playing")
        h.scheduler.drain()
        XCTAssertEqual(h.consent.prompts(musicID), 1, "No second prompt: macOS had the answer")
        XCTAssertEqual(h.permission.prompts(musicID), 1)
        XCTAssertEqual(h.monitor.automation[.appleMusic], .granted)
        XCTAssertEqual(h.monitor.current?.title, "First Light")
    }

    /// Spotify is on screen when Music opens with an old build's
    /// grant, and Music's first event holds the script queue on the prompt.
    /// A skip and a seek pressed meanwhile would reach Spotify only once the
    /// user answered -- a minute and a half later, say. Whenever that is,
    /// they are dropped, and the highlight stays where Spotify is.
    @MainActor
    func testButtonsPressedWhileAPromptHoldsTheQueueAreDroppedNotSentLate() {
        for answer in [TimeInterval(2), 90] {
            let music = FakeMusicApp()
            music.queue = [Tracks.first]
            music.state = .paused
            music.running = false
            let spotify = FakeSpotifyApp()
            spotify.queue = [Tracks.lights, Tracks.breathe]
            spotify.position = 30
            let h = PlayerHarness(music: music, spotify: spotify, running: [spotifyID])
            h.start()
            XCTAssertEqual(h.monitor.activePlayer, .spotify)
            h.consent.undecided = [musicID]
            h.consent.answers[musicID] = .init(allow: true, after: answer)
            var shownAt: [TimeInterval] = []
            h.consent.whileShown = { _ in
                MainActor.assumeIsolated {
                    h.monitor.nextTrack()
                    h.monitor.previousTrack()
                    h.monitor.seek(to: 100)
                    shownAt.append(h.monitor.playbackTime())
                }
            }
            h.music.running = true
            h.environment.launch(musicID)
            h.scheduler.fireDelayed()
            h.consent.whileShown = nil
            XCTAssertEqual(h.consent.prompts(musicID), 1)
            XCTAssertEqual(shownAt.count, 1)
            XCTAssertEqual(shownAt.first ?? 0, 30.16, accuracy: 0.001, "The highlight never went where Spotify did not (\(answer) s)")
            XCTAssertEqual(h.spotify.count("spotify.next"), 0, "Not sent \(answer) s after the press")
            XCTAssertEqual(h.spotify.count("spotify.previous"), 0)
            XCTAssertEqual(h.spotify.count("spotify.seek"), 0)
            XCTAssertEqual(h.spotify.index, 0)
            XCTAssertEqual(h.monitor.automation[.appleMusic], .granted)
            XCTAssertEqual(h.monitor.activePlayer, .spotify)

            // Answered: the buttons work at once again.
            h.monitor.nextTrack()
            h.scheduler.drain()
            h.monitor.seek(to: 100)
            h.scheduler.drain()
            XCTAssertEqual(h.spotify.count("spotify.next"), 1)
            XCTAssertEqual(h.spotify.count("spotify.seek"), 1)
            XCTAssertEqual(h.spotify.position, 100)
        }
    }

    /// The deadline behind that: a press that waited longer than a few
    /// seconds to be sent -- queued behind an event about to wait on the
    /// prompt, say, or behind a hung player -- is dropped, and a seek's
    /// optimistic highlight goes back to where the player is.
    @MainActor
    func testACommandOrSeekThatWaitedTooLongToBeSentIsDropped() {
        let spotify = FakeSpotifyApp()
        spotify.queue = [Tracks.lights, Tracks.breathe]
        spotify.position = 30
        let h = PlayerHarness(spotify: spotify, running: [spotifyID])
        h.start()
        h.monitor.nextTrack()
        h.monitor.seek(to: 100)
        XCTAssertEqual(h.monitor.playbackTime(), 100.16, accuracy: 0.001)
        h.clock.now += PlayerConnection.commandDeadline + 1 // the script queue was held meanwhile
        h.scheduler.drain()
        XCTAssertEqual(h.spotify.count("spotify.next"), 0)
        XCTAssertEqual(h.spotify.count("spotify.seek"), 0)
        XCTAssertEqual(h.monitor.playbackTime(), 30.16, accuracy: 0.001, "Re-sampled: back where Spotify is")

        h.clock.now += PlayerConnection.commandDeadline
        h.monitor.nextTrack()
        h.clock.now += PlayerConnection.commandDeadline
        h.scheduler.drain()
        XCTAssertEqual(h.spotify.count("spotify.next"), 1, "Within the deadline it goes")
        XCTAssertEqual(h.monitor.current?.title, "Breathe")
    }

    /// Music has answered this session, then macOS forgets the
    /// grant (`tccutil reset AppleEvents` while Lunalyr runs). The next
    /// re-sync meets the prompt with its six-second timeout; the silent check
    /// that timeout sets off finds Music undecided, and from then on nothing
    /// is sent until the user plays something in it or chooses Allow Access.
    /// Not a six-second prompt a minute, and another for every button press.
    @MainActor
    func testConsentLostMidSessionWaitsForTheUserInsteadOfPromptingEveryMinute() {
        let music = FakeMusicApp()
        music.queue = [Tracks.first]
        let h = PlayerHarness(music: music)
        h.start()
        XCTAssertEqual(h.monitor.automation[.appleMusic], .granted)
        h.consent.undecided = [musicID]
        h.permission.status = OSStatus(AppleEventCode.wouldRequireConsent)
        let sent = h.players.sent.count
        for _ in 0..<300 {
            h.clock.now += 2
            h.monitor.tick()
            h.scheduler.drain()
        }
        for _ in 0..<3 {
            h.monitor.playPause()
            h.scheduler.drain()
        }
        XCTAssertEqual(h.consent.prompts(musicID), 1, "Only the re-sync that met it, in ten minutes")
        XCTAssertEqual(h.consent.shown.first?.timeout, TimeInterval(PlayerScriptSource.replyTimeout))
        XCTAssertEqual(h.players.sent.count, sent + 1)
        XCTAssertEqual(h.permission.prompts(musicID), 0, "Nobody asked")
        XCTAssertEqual(h.monitor.automation[.appleMusic], .needsConsent, "The menu offers Allow Access")
        XCTAssertNil(h.monitor.current)

        // The user plays something in Music: macOS asks with a prompt that
        // stays up, the user chooses OK, and the next event goes straight through.
        h.permission.answers[musicID] = 0
        h.permission.whileAsking = { [consent = h.consent] id in consent.undecided.remove(id) }
        h.postMusic(Tracks.first, "Playing")
        h.scheduler.drain()
        XCTAssertEqual(h.permission.prompts(musicID), 1)
        XCTAssertEqual(h.consent.prompts(musicID), 1)
        XCTAssertEqual(h.monitor.automation[.appleMusic], .granted)
        XCTAssertEqual(h.monitor.current?.title, "First Light")
        XCTAssertTrue(h.monitor.isPolling)
    }

    /// The same, where the prompt is expected (the welcome explained it), so
    /// macOS is asked at once. Its prompt blocks the permission check, not
    /// the script queue: the re-syncs, refreshes and buttons for the player
    /// it is asking about send nothing meanwhile, or the next event would
    /// raise a second prompt over the first.
    @MainActor
    func testWhileMacOSAsksAboutAPlayerLunalyrWasReadingNothingIsSentToIt() {
        let music = FakeMusicApp()
        music.queue = [Tracks.first]
        let h = PlayerHarness(music: music)
        h.start(promptsExplained: true)
        XCTAssertEqual(h.monitor.current?.title, "First Light")
        h.consent.undecided = [musicID]
        h.permission.status = OSStatus(AppleEventCode.wouldRequireConsent)
        h.permission.answers[musicID] = 0
        var sentWhileAsking: [Int] = []
        h.permission.whileAsking = { [consent = h.consent] id in
            MainActor.assumeIsolated {
                let before = h.players.sent.count
                for _ in 0..<5 {
                    h.clock.now += 2
                    h.monitor.tick()
                }
                h.monitor.refresh()
                h.monitor.playPause()
                h.postMusic(Tracks.first, "Playing")
                // The script queue does not wait for the permission queue.
                h.scheduler.drain()
                sentWhileAsking.append(h.players.sent.count - before)
            }
            consent.undecided.remove(id) // the user chooses OK
        }
        h.clock.now += 2
        h.monitor.tick()
        h.scheduler.drain()
        XCTAssertEqual(sentWhileAsking, [0])
        XCTAssertEqual(h.consent.prompts(musicID), 1, "Only the re-sync that met it")
        XCTAssertEqual(h.permission.prompts(musicID), 1)
        XCTAssertEqual(h.monitor.automation[.appleMusic], .granted)
        XCTAssertEqual(h.monitor.current?.title, "First Light")
        XCTAssertTrue(h.monitor.isPolling)
    }

    /// One re-sync of Spotify times out, and a second later Spotify
    /// moves on to its next track. Its notification shows it is up, so the
    /// new track's cover is read rather than held back with the re-syncs.
    @MainActor
    func testANotificationEndsTheBackOffSoTheNewTracksCoverIsRead() {
        let spotify = FakeSpotifyApp()
        spotify.queue = [Tracks.lights, Tracks.breathe]
        let h = PlayerHarness(spotify: spotify, running: [spotifyID])
        h.start()
        XCTAssertEqual(h.monitor.current?.title, "Blinding Lights")
        h.spotify.failure = AppleEventCode.timedOut
        h.clock.now += 2
        h.monitor.tick()
        h.scheduler.drain()
        h.spotify.failure = nil
        h.spotify.clearLog()
        h.clock.now += 1
        h.monitor.tick()
        h.scheduler.drain()
        XCTAssertEqual(h.spotify.log, [], "Backing off")

        h.spotify.index = 1
        h.postSpotify(Tracks.breathe, "Playing", at: 0)
        h.scheduler.drain()
        XCTAssertEqual(h.monitor.current?.title, "Breathe")
        XCTAssertEqual(h.spotify.count("spotify.artwork"), 1, "The new track's cover is read")
        XCTAssertEqual(h.monitor.automation[.spotify], .granted)
    }

    func testEveryScriptHasACopyThatWaitsForConsent() {
        XCTAssertGreaterThanOrEqual(PlayerScriptSource.consentTimeout, 120)
        XCTAssertEqual(PlayerScriptSource.replyTimeout, 6)
        let backends: [any PlayerBackend] = [MusicBackend(), SpotifyBackend()]
        for backend in backends {
            var scripts = [backend.readScript, backend.positionScript] + backend.artworkScripts
            scripts += [TransportCommand.playPause, .next, .previous].map(backend.commandScript)
            scripts.append(backend.seekScript(to: 42))
            for script in scripts {
                let waiting = script.waitingForConsent
                XCTAssertEqual(waiting.name, script.name, "Fakes and logs know it by name")
                XCTAssertEqual(waiting.cacheable, script.cacheable)
                XCTAssertTrue(waiting.waitsForConsent)
                XCTAssertFalse(script.waitsForConsent)
                XCTAssertNotEqual(waiting, script, "Compiled and cached apart")
                XCTAssertLessThanOrEqual(FakeAutomationPrompt.timeout(of: script), 10, script.name)
                XCTAssertEqual(FakeAutomationPrompt.timeout(of: waiting), consentTimeout, script.name)
                let blocks = script.source.components(separatedBy: "with timeout of ").count - 1
                XCTAssertGreaterThan(blocks, 0, script.name)
                XCTAssertEqual(waiting.source.components(separatedBy: "with timeout of \(PlayerScriptSource.consentTimeout) seconds").count - 1,
                               blocks, script.name)
                XCTAssertEqual(PlayerScriptSource.withTimeout(PlayerScriptSource.replyTimeout, waiting.source),
                               PlayerScriptSource.withTimeout(PlayerScriptSource.replyTimeout, script.source),
                               "Nothing else changes (\(script.name))")
                XCTAssertTrue(waiting.source.hasPrefix(#"if application id ""#), "Still checks the player is running first")
            }
        }
    }
}

/// `AutomationConsentTests` in the app's order: each reply handled at once.
final class AutomationConsentSplitQueueTests: AutomationConsentTests {
    override class var splitQueues: Bool { true }
}

/// Replies from a list of outcomes, then `fallback`.
private final class ScriptedRunner: AppleScriptRunning {
    var outcomes: [Result<NSAppleEventDescriptor, ScriptFailure>] = []
    var fallback: Result<NSAppleEventDescriptor, ScriptFailure> = .success(ScriptedRunner.playing)
    var onRun: ((PlayerScript) -> Void)?
    private(set) var ran: [PlayerScript] = []

    static let playing = NSAppleEventDescriptor.scriptList([
        NSAppleEventDescriptor(string: "playing"), NSAppleEventDescriptor(string: "00000000000000A1"),
        NSAppleEventDescriptor(double: 12),
    ])

    func run(_ script: PlayerScript) -> Result<NSAppleEventDescriptor, ScriptFailure> {
        ran.append(script)
        onRun?(script)
        return outcomes.isEmpty ? fallback : outcomes.removeFirst()
    }
}

/// `PlayerConnection`'s consent ledger on its own.
final class PlayerConsentTests: XCTestCase {
    private let backend = MusicBackend()
    private let clock = TestClock()
    private let runner = ScriptedRunner()
    private lazy var connection: PlayerConnection = {
        let clock = clock
        return PlayerConnection(runner: runner, permission: { _, _ in 0 }, uptime: { clock.now })
    }()

    override func tearDown() {
        runner.onRun = nil
        super.tearDown()
    }

    private func failure(_ code: Int) -> Result<NSAppleEventDescriptor, ScriptFailure> {
        .failure(ScriptFailure(code: code))
    }

    func testOnlyAReplyFromThePlayerProvesConsent() {
        runner.outcomes = [.success(.scriptList([NSAppleEventDescriptor(string: "notRunning")]))]
        XCTAssertEqual(connection.samplePosition(backend), .notRunning)
        XCTAssertEqual(connection.consent(.appleMusic), .unproven, "Its own running check addressed nothing")
        XCTAssertNotNil(connection.samplePosition(backend).value)
        XCTAssertEqual(connection.consent(.appleMusic), .proven)
        XCTAssertNotNil(connection.samplePosition(backend).value)
        XCTAssertEqual(runner.ran.map(\.waitsForConsent), [true, true, false])
        XCTAssertEqual(connection.consent(.spotify), .unproven, "Each player proves its own")
    }

    func testAFirstEventThatTimesOutLeavesConsentPendingAndHoldsEverySend() {
        runner.outcomes = [failure(AppleEventCode.timedOut)]
        XCTAssertEqual(connection.samplePosition(backend), .blocked(.needsConsent), "Pending, not a busy player")
        XCTAssertEqual(connection.consent(.appleMusic), .unanswered)
        let ran = runner.ran.count
        XCTAssertEqual(connection.read(backend), .blocked(.needsConsent))
        XCTAssertEqual(connection.refresh(backend).read, .blocked(.needsConsent))
        XCTAssertEqual(connection.artwork(backend), .blocked(.needsConsent))
        XCTAssertEqual(connection.perform(backend.commandScript(.next), on: .appleMusic, requestedAt: clock.now),
                       .blocked(.needsConsent))
        XCTAssertEqual(runner.ran.count, ran, "Nothing is sent")

        XCTAssertFalse(connection.admits(.granted, prompted: false, for: .appleMusic), "A silent check may report an old grant")
        XCTAssertFalse(connection.admits(.unknown, prompted: false, for: .appleMusic))
        XCTAssertTrue(connection.admits(.notDetermined, prompted: false, for: .appleMusic),
                      "Undecided: whether to ask now is the monitor's call")
        XCTAssertEqual(connection.consent(.appleMusic), .unanswered)
        XCTAssertEqual(runner.ran.count, ran)
        XCTAssertTrue(connection.admits(.granted, prompted: true, for: .appleMusic), "Allow Access")
        XCTAssertEqual(connection.consent(.appleMusic), .unproven)
        XCTAssertNotNil(connection.samplePosition(backend).value)
        XCTAssertEqual(runner.ran.last?.waitsForConsent, true, "It waits for the prompt again")
    }

    func testARefusalHoldsEverySendUntilACheckFindsItAllowed() {
        runner.outcomes = [failure(AppleEventCode.notPermitted)]
        XCTAssertEqual(connection.samplePosition(backend), .blocked(.denied))
        XCTAssertEqual(connection.consent(.appleMusic), .refused)
        let ran = runner.ran.count
        XCTAssertEqual(connection.samplePosition(backend), .blocked(.denied))
        XCTAssertEqual(connection.perform(backend.commandScript(.playPause), on: .appleMusic, requestedAt: clock.now),
                       .blocked(.denied))
        XCTAssertEqual(runner.ran.count, ran)
        XCTAssertTrue(connection.admits(.denied, prompted: false, for: .appleMusic))
        XCTAssertEqual(connection.consent(.appleMusic), .refused)
        XCTAssertTrue(connection.admits(.granted, prompted: false, for: .appleMusic), "Turned on in System Settings")
        XCTAssertEqual(connection.consent(.appleMusic), .unproven)
    }

    func testAProvenPlayerThatTimesOutIsPolledLessAndLessOften() {
        XCTAssertNotNil(connection.samplePosition(backend).value)
        runner.fallback = failure(AppleEventCode.timedOut)
        var sentAt: [TimeInterval] = []
        for _ in 0..<240 {
            let before = runner.ran.count
            let response = connection.samplePosition(backend)
            if runner.ran.count > before {
                sentAt.append(clock.now)
                XCTAssertEqual(response, .failed(AppleEventCode.timedOut))
            } else {
                XCTAssertEqual(response, .failed(AppleEventCode.notSent), "Held back is not a timeout of its own")
            }
            clock.now += 1
        }
        XCTAssertEqual(Array(zip(sentAt.dropFirst(), sentAt).map { $0 - $1 }.prefix(6)), [4, 8, 16, 32, 60, 60])
        XCTAssertEqual(connection.consent(.appleMusic), .proven, "Busy, not asking")
        XCTAssertFalse(runner.ran.dropFirst().contains(where: \.waitsForConsent))

        // The user's commands are never held back, and a reply ends the back-off.
        XCTAssertEqual(connection.samplePosition(backend), .failed(AppleEventCode.timedOut))
        runner.fallback = .success(ScriptedRunner.playing)
        let ran = runner.ran.count
        XCTAssertEqual(connection.samplePosition(backend), .failed(AppleEventCode.notSent), "A minute's wait now")
        XCTAssertEqual(runner.ran.count, ran)
        XCTAssertEqual(connection.perform(backend.commandScript(.playPause), on: .appleMusic, requestedAt: clock.now),
                       .value(true))
        XCTAssertEqual(runner.ran.count, ran + 1)
        XCTAssertNotNil(connection.samplePosition(backend).value)
        XCTAssertEqual(runner.ran.count, ran + 2)
    }

    func testAnEventFromBeforeAResetSaysNothingAboutTheNewLaunch() {
        let connection = connection
        runner.onRun = { _ in connection.reset(.appleMusic) } // Music quit while the event waited
        runner.outcomes = [failure(AppleEventCode.timedOut)]
        XCTAssertEqual(connection.samplePosition(backend), .failed(AppleEventCode.timedOut))
        XCTAssertEqual(connection.consent(.appleMusic), .unproven)
        runner.onRun = nil
        XCTAssertNotNil(connection.samplePosition(backend).value)
        XCTAssertEqual(runner.ran.map(\.waitsForConsent), [true, true])
    }

    func testAPositionThatWaitedForTheAnswerIsStampedWhenItCameBack() {
        let clock = clock
        runner.onRun = { _ in clock.now += 45 }
        XCTAssertEqual(connection.samplePosition(backend).value?.time, 1_045, "Sampled after the user answered")
        runner.onRun = { _ in clock.now += 0.2 }
        XCTAssertEqual(connection.samplePosition(backend).value?.time ?? 0, 1_045.1, accuracy: 1e-9,
                       "Otherwise the middle of the round trip")
    }

    func testAnEventThatMayBeShowingThePromptIsKnownToBeInFlight() {
        let connection = connection
        var during: [Bool] = [], queueDuring: [Bool] = []
        runner.onRun = { _ in
            during.append(connection.isAwaitingConsent(.appleMusic))
            queueDuring.append(connection.queueAwaitsConsent)
        }
        XCTAssertFalse(connection.isAwaitingConsent(.appleMusic))
        XCTAssertFalse(connection.queueAwaitsConsent)
        _ = connection.samplePosition(backend)
        _ = connection.samplePosition(backend)
        _ = connection.samplePosition(SpotifyBackend())
        XCTAssertEqual(during, [true, false, false], "Only while the player's consent is unproven")
        XCTAssertEqual(queueDuring, [true, false, true], "Any player's")
        XCTAssertFalse(connection.isAwaitingConsent(.appleMusic))
        XCTAssertFalse(connection.queueAwaitsConsent)
    }

    func testConsentOutlivesARelaunchButItsBackOffDoesNot() {
        XCTAssertNotNil(connection.samplePosition(backend).value)
        runner.fallback = failure(AppleEventCode.timedOut)
        XCTAssertEqual(connection.samplePosition(backend), .failed(AppleEventCode.timedOut))
        XCTAssertEqual(connection.samplePosition(backend), .failed(AppleEventCode.notSent), "Backing off")
        connection.reset(.appleMusic) // Music quit and reopened
        XCTAssertEqual(connection.consent(.appleMusic), .proven, "Lunalyr's consent is with Music, not with one launch")
        runner.fallback = .success(ScriptedRunner.playing)
        XCTAssertNotNil(connection.samplePosition(backend).value, "The new launch is read at once")
        XCTAssertEqual(runner.ran.map(\.waitsForConsent), [true, false, false], "With the short timeout")

        // A prompt left unanswered stays so: reopening Spotify is not asking.
        let spotify = SpotifyBackend()
        runner.outcomes = [failure(AppleEventCode.timedOut)]
        XCTAssertEqual(connection.samplePosition(spotify), .blocked(.needsConsent))
        connection.reset(.spotify)
        XCTAssertEqual(connection.consent(.spotify), .unanswered)
        let ran = runner.ran.count
        XCTAssertEqual(connection.read(spotify), .blocked(.needsConsent))
        XCTAssertEqual(runner.ran.count, ran)
        // Nor does a refusal lift.
        XCTAssertTrue(connection.admits(.denied, prompted: false, for: .spotify))
        connection.reset(.spotify)
        XCTAssertEqual(connection.consent(.spotify), .refused)
    }

    func testANotificationEndsTheBackOffButNotItsGrowth() {
        XCTAssertNotNil(connection.samplePosition(backend).value)
        runner.fallback = failure(AppleEventCode.timedOut)
        XCTAssertEqual(connection.samplePosition(backend), .failed(AppleEventCode.timedOut))
        XCTAssertEqual(connection.artwork(backend), .failed(AppleEventCode.notSent), "Backing off for four seconds")
        connection.resume(.appleMusic) // Music posted a notification
        let ran = runner.ran.count
        XCTAssertEqual(connection.samplePosition(backend), .failed(AppleEventCode.timedOut))
        XCTAssertEqual(runner.ran.count, ran + 1, "Sent at once")
        clock.now += 4
        XCTAssertEqual(connection.samplePosition(backend), .failed(AppleEventCode.notSent), "Eight seconds after a second timeout")
        clock.now += 4
        XCTAssertEqual(connection.samplePosition(backend), .failed(AppleEventCode.timedOut))
        XCTAssertEqual(runner.ran.count, ran + 2)
    }

    func testACommandThatWaitedPastTheDeadlineIsNotSent() {
        let pressed = clock.now
        clock.now += PlayerConnection.commandDeadline + 0.5
        XCTAssertEqual(connection.perform(backend.commandScript(.next), on: .appleMusic, requestedAt: pressed),
                       .failed(AppleEventCode.notSent))
        XCTAssertEqual(runner.ran.count, 0, "Not sent, nor taken as a first event that waits on the prompt")
        XCTAssertEqual(connection.consent(.appleMusic), .unproven)
        XCTAssertEqual(connection.perform(backend.commandScript(.next), on: .appleMusic,
                                          requestedAt: clock.now - PlayerConnection.commandDeadline), .value(true))
        XCTAssertEqual(runner.ran.count, 1)
        XCTAssertGreaterThanOrEqual(PlayerConnection.commandDeadline, 2, "Room for a re-sync queued ahead")
        XCTAssertLessThan(PlayerConnection.commandDeadline, TimeInterval(PlayerScriptSource.replyTimeout))
    }

    func testAnUndecidedCheckHoldsEverySendUntilTheUserIsAsked() {
        XCTAssertNotNil(connection.samplePosition(backend).value)
        XCTAssertTrue(connection.admits(.notDetermined, prompted: false, for: .appleMusic))
        XCTAssertEqual(connection.consent(.appleMusic), .unanswered, "macOS forgot the grant")
        let ran = runner.ran.count
        XCTAssertEqual(connection.samplePosition(backend), .blocked(.needsConsent))
        XCTAssertEqual(connection.perform(backend.commandScript(.playPause), on: .appleMusic, requestedAt: clock.now),
                       .blocked(.needsConsent))
        XCTAssertEqual(runner.ran.count, ran, "No event raises the prompt while macOS asks, or before it does")
        XCTAssertFalse(connection.admits(.granted, prompted: false, for: .appleMusic))
        XCTAssertTrue(connection.admits(.granted, prompted: true, for: .appleMusic), "The user chose OK")
        XCTAssertNotNil(connection.samplePosition(backend).value)
        XCTAssertEqual(runner.ran.last?.waitsForConsent, true)
    }

    func testAFirstEventThatTimesOutAfterACheckFoundThePlayerUndecidedIsStillConsentPending() {
        let connection = connection
        runner.onRun = { _ in _ = connection.admits(.notDetermined, prompted: false, for: .appleMusic) }
        runner.outcomes = [failure(AppleEventCode.timedOut)]
        XCTAssertEqual(connection.samplePosition(backend), .blocked(.needsConsent), "Not a busy player")
        XCTAssertEqual(connection.consent(.appleMusic), .unanswered)
    }
}
