import XCTest
@testable import VersoCustom

/// Capture demand is pure: no audio device, permission or user setting is touched.
final class AudioCaptureGateTests: XCTestCase {
    private func decide(_ change: (inout AudioCaptureGate.Inputs) -> Void = { _ in }) -> AudioCaptureGate.Decision {
        var inputs = AudioCaptureGate.Inputs(enabled: true, visualiserChosen: true)
        change(&inputs)
        return AudioCaptureGate.decide(inputs)
    }

    func testCaptureNeedsAnEnabledAwakeVisualiser() {
        XCTAssertEqual(decide(), .capture(recheckAfter: nil))
        XCTAssertEqual(decide { $0.enabled = false }, .off)
        XCTAssertEqual(decide { $0.visualiserChosen = false }, .paused(.wallpaper))
        XCTAssertEqual(decide { $0.displaysAwake = false }, .paused(.displaysAsleep))
        XCTAssertEqual(decide { $0.powerPause = "Paused: power." }, .paused(.power("Paused: power.")))
        // Switching off wins over everything else.
        XCTAssertEqual(decide { $0.enabled = false; $0.displaysAwake = false }, .off)
    }

    func testNothingPlayingStopsCaptureAfterTheHangTime() {
        // Nothing has played since capture was armed: no IO at all, so the
        // output device can idle and the Mac can sleep.
        XCTAssertEqual(decide { $0.outputIdleFor = .infinity }, .standby)
        // A pause or a gap between tracks keeps capture through the hang time.
        XCTAssertEqual(decide { $0.outputIdleFor = 4 }, .capture(recheckAfter: 6))
        XCTAssertEqual(decide { $0.outputIdleFor = AudioCaptureGate.outputHangTime }, .standby)
    }

    func testCoveredWallpapersPauseAfterTheHangTime() {
        XCTAssertEqual(decide { $0.surfacesHiddenFor = 3 }, .capture(recheckAfter: 7))
        XCTAssertEqual(decide { $0.surfacesHiddenFor = 10 }, .paused(.hidden))
        // The earliest expiry is the next re-evaluation.
        XCTAssertEqual(decide { $0.surfacesHiddenFor = 3; $0.outputIdleFor = 8 }, .capture(recheckAfter: 2))
    }

    func testAHangTimeThatStartsThisInstantIsScheduled() {
        // The controller stamps the moment and decides at that same moment,
        // so zero is what it sees when the last wallpaper is covered or
        // playback stops. Nothing else may re-evaluate while music plays.
        XCTAssertEqual(decide { $0.surfacesHiddenFor = 0 }, .capture(recheckAfter: AudioCaptureGate.hiddenHangTime))
        XCTAssertEqual(decide { $0.outputIdleFor = 0 }, .capture(recheckAfter: AudioCaptureGate.outputHangTime))
        XCTAssertEqual(decide { $0.surfacesHiddenFor = 0; $0.outputIdleFor = 4 }, .capture(recheckAfter: 6))
        // Drawing and playing need no re-evaluation.
        XCTAssertEqual(decide { $0.surfacesHiddenFor = nil; $0.outputIdleFor = nil }, .capture(recheckAfter: nil))
    }

    func testPlaybackIsFollowedOnlyWhileItDecidesWhatHappens() {
        XCTAssertTrue(AudioCaptureGate.Decision.capture(recheckAfter: nil).followsPlayback)
        XCTAssertTrue(AudioCaptureGate.Decision.standby.followsPlayback)
        // Covered or paused wallpapers keep a suspended tap, whose route
        // must be followed, and resume from what is playing.
        XCTAssertTrue(AudioCaptureGate.Decision.paused(.hidden).followsPlayback)
        XCTAssertTrue(AudioCaptureGate.Decision.paused(.wallpaperPaused).followsPlayback)
        // Nothing playback does matters until these change.
        XCTAssertFalse(AudioCaptureGate.Decision.off.followsPlayback)
        XCTAssertFalse(AudioCaptureGate.Decision.paused(.wallpaper).followsPlayback)
        XCTAssertFalse(AudioCaptureGate.Decision.paused(.power("Paused.")).followsPlayback)
        XCTAssertFalse(AudioCaptureGate.Decision.paused(.displaysAsleep).followsPlayback)
    }

    func testUserStartRunsThroughThePromptWindowButNotThroughSleepOrPowerLimits() {
        // Started by the user with nothing playing: connect now, so macOS can
        // ask for permission in context, then re-evaluate when the grace ends.
        XCTAssertEqual(decide { $0.sinceUserStart = 5; $0.outputIdleFor = .infinity }, .capture(recheckAfter: 55))
        XCTAssertEqual(decide { $0.sinceUserStart = 5; $0.surfacesHiddenFor = 30 }, .capture(recheckAfter: 55))
        XCTAssertEqual(decide { $0.sinceUserStart = 61; $0.outputIdleFor = .infinity }, .standby)
        XCTAssertEqual(decide { $0.sinceUserStart = 5; $0.displaysAwake = false }, .paused(.displaysAsleep))
        XCTAssertEqual(decide { $0.sinceUserStart = 5; $0.powerPause = "Paused." }, .paused(.power("Paused.")))
    }

    func testPowerLimitsExplainWhatReplacedTheVisualiser() {
        XCTAssertNil(AudioCaptureGate.powerPause(chosen: .visualizer, effective: .visualizer,
            lowPowerVisuals: false, lowPowerMode: true, onBattery: true))
        XCTAssertNil(AudioCaptureGate.powerPause(chosen: .pattern, effective: .pattern,
            lowPowerVisuals: true, lowPowerMode: false, onBattery: false))
        let lowPower = AudioCaptureGate.powerPause(chosen: .visualizer, effective: .pattern,
            lowPowerVisuals: false, lowPowerMode: true, onBattery: false)
        XCTAssertEqual(lowPower, "Paused: Low Power Mode is showing line patterns. System audio resumes with the visualiser.")
        let manual = AudioCaptureGate.powerPause(chosen: .visualizer, effective: .pattern,
            lowPowerVisuals: true, lowPowerMode: true, onBattery: true)
        XCTAssertTrue(manual?.contains("Low power visuals") ?? false)
        let battery = AudioCaptureGate.powerPause(chosen: .visualizer, effective: .colourOnly,
            lowPowerVisuals: false, lowPowerMode: false, onBattery: true)
        XCTAssertEqual(battery, "Paused: on battery, Lunalyr is showing colour only. System audio resumes with the visualiser.")
        XCTAssertEqual(AudioCaptureGate.PauseReason.power(battery ?? "").message, battery)
    }

    /// Capture asks each surface what its own frame clock does.
    func testSurfacesDrawByTheRuleTheirFrameClocksFollow() {
        typealias State = WindowRenderActivity.State
        func state(visible: Bool, asleep: Bool = false, window: Bool = false, pause: Bool,
                   paused: Bool = false) -> State {
            WindowRenderActivity.state(visible: visible, screensAsleep: asleep, followsWindow: window,
                                       pauseWhenCovered: pause, wallpaperPaused: paused)
        }
        // A covered wallpaper keeps drawing unless covered wallpapers pause.
        XCTAssertEqual(state(visible: false, pause: false), .drawing)
        XCTAssertEqual(state(visible: false, pause: true), .hidden)
        XCTAssertEqual(state(visible: true, pause: true), .drawing)
        // The Settings preview stops whenever it cannot be seen.
        XCTAssertEqual(state(visible: false, window: true, pause: false), .hidden)
        XCTAssertEqual(state(visible: true, window: true, pause: true), .drawing)
        // Nothing draws while the screens sleep, lock or show the screen saver.
        XCTAssertEqual(state(visible: true, asleep: true, pause: false), .hidden)
        XCTAssertEqual(state(visible: true, asleep: true, window: true, pause: false), .hidden)
        // Pause wallpaper freezes every wallpaper, seen or not, but leaves
        // the Settings preview to its own window.
        XCTAssertEqual(state(visible: true, pause: false, paused: true), .frozen)
        XCTAssertEqual(state(visible: false, asleep: true, pause: true, paused: true), .frozen)
        XCTAssertEqual(state(visible: true, window: true, pause: true, paused: true), .drawing)
        XCTAssertEqual(state(visible: false, window: true, pause: false, paused: true), .hidden)

        XCTAssertFalse(AudioCaptureGate.anySurfaceDrawing([.hidden, .frozen]))
        XCTAssertTrue(AudioCaptureGate.anySurfaceDrawing([.frozen, .drawing]))
        // No surfaces yet: do not gate on an unknown.
        XCTAssertTrue(AudioCaptureGate.anySurfaceDrawing([]))
    }

    /// Pause wallpaper is a rule of the surfaces: it leaves nothing drawing
    /// only when every surface is a wallpaper it froze. A Settings preview
    /// still moving draws, and one that is hidden may be back at any moment.
    func testPauseWallpaperLeavesNothingDrawingOnlyWithoutAPreview() {
        XCTAssertTrue(AudioCaptureGate.frozen([.frozen, .frozen], wallpaperPaused: true))
        // No wallpaper yet: any made now is frozen.
        XCTAssertTrue(AudioCaptureGate.frozen([], wallpaperPaused: true))
        XCTAssertFalse(AudioCaptureGate.frozen([.frozen, .drawing], wallpaperPaused: true))
        XCTAssertFalse(AudioCaptureGate.frozen([.frozen, .hidden], wallpaperPaused: true))
        XCTAssertFalse(AudioCaptureGate.frozen([.hidden], wallpaperPaused: false))
        XCTAssertFalse(AudioCaptureGate.frozen([], wallpaperPaused: false))
    }

    func testPauseWallpaperPausesCaptureAtOnce() {
        XCTAssertEqual(decide { $0.surfacesFrozen = true }, .paused(.wallpaperPaused))
        // No hang time, and a user start waits for the wallpaper as well.
        XCTAssertEqual(decide { $0.surfacesFrozen = true; $0.sinceUserStart = 5 }, .paused(.wallpaperPaused))
        XCTAssertEqual(decide { $0.surfacesFrozen = true; $0.outputIdleFor = 3 }, .paused(.wallpaperPaused))
        XCTAssertEqual(decide { $0.surfacesFrozen = true; $0.surfacesHiddenFor = 0 }, .paused(.wallpaperPaused))
        // Switching off, another wallpaper type, power limits and sleep say more.
        XCTAssertEqual(decide { $0.surfacesFrozen = true; $0.enabled = false }, .off)
        XCTAssertEqual(decide { $0.surfacesFrozen = true; $0.visualiserChosen = false }, .paused(.wallpaper))
        XCTAssertEqual(decide { $0.surfacesFrozen = true; $0.powerPause = "Paused." }, .paused(.power("Paused.")))
        XCTAssertEqual(decide { $0.surfacesFrozen = true; $0.displaysAwake = false }, .paused(.displaysAsleep))
        XCTAssertTrue(AudioCaptureGate.PauseReason.wallpaperPaused.message.contains("wallpaper is paused"))
    }

    func testOnlyOtherProcessesCountAsPlaying() {
        let own: pid_t = 4_242
        XCTAssertFalse(AudioOutputActivityMonitor.anyOtherProcessPlaying([], ownPID: own))
        // Lunalyr's own capture IO must not keep capture alive.
        XCTAssertFalse(AudioOutputActivityMonitor.anyOtherProcessPlaying([(own, true), (77, false)], ownPID: own))
        XCTAssertTrue(AudioOutputActivityMonitor.anyOtherProcessPlaying([(own, false), (77, true)], ownPID: own))
    }

    func testAPlayerCountsAsPlayingThroughItsOwnProcessesOnly() {
        let spotify = PlayerKind.spotify.bundleID
        func playing(_ processes: [(bundleID: String?, runningOutput: Bool)]) -> Bool? {
            AudioOutputActivityMonitor.app(spotify, isPlayingAmong: processes)
        }
        XCTAssertEqual(playing([(spotify, true)]), true)
        // Its helpers count; a look-alike identifier and other apps do not.
        XCTAssertEqual(playing([("com.spotify.client.helper", true)]), true)
        XCTAssertEqual(playing([("com.spotify.clientx", true), ("com.apple.Safari", true), (spotify, false)]), false)
        // Connected but silent, or not connected at all: not playing here.
        XCTAssertEqual(playing([(spotify, false), ("com.apple.Music", false)]), false)
        // No process said who it is: unknown.
        XCTAssertNil(playing([(nil, true)]))
        XCTAssertNil(playing([]))
    }

    /// Spotify Connect, or a player's own AirPlay: the player reports playing
    /// and the tap rightly hears silence.
    func testAPlayerPlayingElsewhereIsNeitherAFaultNorAReasonToRecover() {
        let connect = AudioPlayback(playerPlaying: true, playerName: "Spotify", playerOutputHere: false, anyOutput: false)
        XCTAssertTrue(connect.playingElsewhere)
        XCTAssertFalse(connect.playingHere)
        XCTAssertFalse(connect.outputActive, "Nothing on this Mac needs capturing.")
        let silence = AudioCaptureHealth.evaluate(elapsed: 30, packets: 600, packetAge: 0, digitalSilenceFor: 30,
            quiet: true, musicPlaying: connect.playingHere, outputActive: connect.outputActive)
        XCTAssertEqual(silence, .quiet)
        let status = silence.status(playback: connect, methodLabel: "System audio", permission: "System Audio Recording",
                                    firstPrompt: false, note: "")
        XCTAssertEqual(status.state, .quiet)
        XCTAssertTrue(status.message.hasPrefix("Nothing from Spotify is playing on this Mac"), status.message)
        XCTAssertFalse(AudioCaptureRecovery.shouldUseCompatibility(method: .automatic, usingTap: true, elapsed: 30,
            packets: 600, packetAge: 0, quietFor: 30, playing: connect.playingHere))
        XCTAssertTrue(AudioCaptureGate.standbyMessage(connect).contains("nothing from Spotify is playing on this Mac"))

        // Another app holding a silent stream open does not change that.
        let idleStream = AudioPlayback(playerPlaying: true, playerName: "Spotify", playerOutputHere: false, anyOutput: true)
        let quiet = AudioCaptureHealth.evaluate(elapsed: 30, packets: 600, packetAge: 0, digitalSilenceFor: 30,
            quiet: true, musicPlaying: idleStream.playingHere, outputActive: idleStream.outputActive)
        XCTAssertEqual(quiet.status(playback: idleStream, methodLabel: "System audio", permission: "System Audio Recording",
                                    firstPrompt: false, note: "").message, idleStream.elsewhereMessage)

        // Playing here, the same silence is the permission problem -- for
        // whichever player it is.
        let here = AudioPlayback(playerPlaying: true, playerName: "Spotify", playerOutputHere: true, anyOutput: true)
        let withdrawn = AudioCaptureHealth.evaluate(elapsed: 30, packets: 600, packetAge: 0, digitalSilenceFor: 8,
            quiet: true, musicPlaying: here.playingHere, outputActive: here.outputActive)
        XCTAssertEqual(withdrawn, .notReceiving)
        let warning = withdrawn.status(playback: here, methodLabel: "System audio", permission: "System Audio Recording",
                                       firstPrompt: false, note: "")
        XCTAssertEqual(warning.state, .attention(.notReceiving))
        XCTAssertTrue(warning.message.contains("while Spotify plays"), warning.message)
        XCTAssertEqual(AudioCaptureGate.standbyMessage(here), AudioCaptureGate.standbyMessage)

        // Where its sound goes is unknown: the player counts as playing here.
        let unknown = AudioPlayback(playerPlaying: true, playerName: "Music", playerOutputHere: nil, anyOutput: nil)
        XCTAssertTrue(unknown.playingHere)
        XCTAssertTrue(unknown.outputActive)
        XCTAssertTrue(AudioPlayback(anyOutput: nil).outputActive, "Unknown output counts as playing")
        XCTAssertFalse(AudioPlayback(anyOutput: false).outputActive)
    }

    func testCaptureStateFlagsProblemsForTheMenuBar() {
        XCTAssertTrue(AudioCaptureState.attention(.notReceiving).needsAttention)
        XCTAssertFalse(AudioCaptureState.standby.needsAttention)
        XCTAssertFalse(AudioCaptureState.paused.needsAttention)
        XCTAssertNotEqual(AudioCaptureState.attention(.stalled), .attention(.failed))
    }
}
