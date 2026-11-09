import Combine
import XCTest
@testable import VersoCustom

/// The controller's state machine, driven through a pretend system: its own
/// clock and timers, settings, wallpaper visibility, playback and sessions.
/// Nothing here opens an audio device, raises a permission prompt, talks to
/// Music or reads the user's settings.
final class AudioCaptureControllerTests: XCTestCase {
    @MainActor
    private func makeController(_ world: CaptureWorld) throws -> AudioReactiveController {
        guard #available(macOS 14.2, *) else { throw XCTSkip("Process taps need macOS 14.2.") }
        return AudioReactiveController(environment: world.environment)
    }

    @MainActor
    func testCoveringTheLastWallpaperWhileMusicPlaysPausesCaptureAfterTheHangTime() async throws {
        let world = CaptureWorld()
        let controller = try makeController(world)
        defer { controller.stop() }
        controller.resumeIfEnabled()
        await controller.pendingStart?.value
        XCTAssertTrue(controller.running)
        let session = try XCTUnwrap(world.sessions.first)

        // Music keeps playing, so nothing but the hang time can pause capture.
        world.visibility = [false]
        controller.handle(.surfacesChanged)
        XCTAssertTrue(controller.running, "A brief cover keeps capture.")
        world.advance(AudioCaptureGate.hiddenHangTime - 0.5)
        XCTAssertTrue(controller.running)
        XCTAssertTrue(session.ioRunning)
        world.advance(1)
        XCTAssertFalse(controller.running)
        XCTAssertEqual(controller.state, .paused)
        XCTAssertEqual(controller.status, AudioCaptureGate.PauseReason.hidden.message)
        XCTAssertFalse(session.ioRunning)
        XCTAssertEqual(session.stops, 0, "The tap keeps its devices while covered.")

        // Uncovering resumes the same session at once.
        world.visibility = [true]
        controller.handle(.surfacesChanged)
        XCTAssertTrue(controller.running)
        XCTAssertTrue(session.ioRunning)
        XCTAssertEqual(world.sessions.count, 1)
    }

    @MainActor
    func testCoveredWallpapersOnlyCountWhenTheyPause() async throws {
        let world = CaptureWorld()
        world.preferences.pauseWhenCovered = false
        let controller = try makeController(world)
        defer { controller.stop() }
        controller.resumeIfEnabled()
        await controller.pendingStart?.value
        world.visibility = [false]
        controller.handle(.surfacesChanged)
        world.advance(60)
        XCTAssertTrue(controller.running, "Covered wallpapers keep drawing unless they pause.")
    }

    /// Settings opened over a maximised window: every wallpaper is covered,
    /// but the preview draws the visualiser and the meters beside it read
    /// the live audio, so capture must keep running for them.
    @MainActor
    func testAVisibleSettingsPreviewKeepsCaptureWhileEveryWallpaperIsCovered() async throws {
        let world = CaptureWorld()
        world.visibility = [false, false]
        world.previewVisible = true
        let controller = try makeController(world)
        defer { controller.stop() }
        controller.resumeIfEnabled()
        await controller.pendingStart?.value
        let session = try XCTUnwrap(world.sessions.first)
        world.advance(AudioCaptureGate.hiddenHangTime + 0.5)
        XCTAssertTrue(controller.running, "The preview is a drawing surface.")
        XCTAssertTrue(session.ioRunning)

        // Covered or minimised, the preview stops too: after the hang time,
        // nothing is left to draw what capture hears.
        world.previewVisible = false
        controller.handle(.surfacesChanged)
        world.advance(AudioCaptureGate.hiddenHangTime - 0.5)
        XCTAssertTrue(controller.running)
        world.advance(1)
        XCTAssertEqual(controller.state, .paused)
        XCTAssertEqual(controller.status, AudioCaptureGate.PauseReason.hidden.message)
        XCTAssertFalse(session.ioRunning)

        // Settings back in view resumes the same session at once; closing it
        // (or hiding its preview) pauses again after the hang time.
        world.previewVisible = true
        controller.handle(.surfacesChanged)
        XCTAssertTrue(controller.running)
        XCTAssertEqual(world.sessions.count, 1)
        world.previewVisible = nil
        controller.handle(.surfacesChanged)
        world.advance(AudioCaptureGate.hiddenHangTime + 0.5)
        XCTAssertEqual(controller.state, .paused)
    }

    /// Pause wallpaper freezes the desktop wallpapers. With no Settings
    /// preview showing the visualiser, nothing is left to draw what capture
    /// hears: the tap's IO and the analysis stop at once -- the user asked
    /// for it, so there is no hang time -- and resuming the wallpaper resumes
    /// the same session.
    @MainActor
    func testPauseWallpaperSuspendsCaptureAtOnceAndResumingResumesIt() async throws {
        let world = CaptureWorld()
        world.visibility = [true, true]
        let controller = try makeController(world)
        defer { controller.stop() }
        controller.resumeIfEnabled()
        await controller.pendingStart?.value
        XCTAssertTrue(controller.running)
        let session = try XCTUnwrap(world.sessions.first)

        world.preferences.wallpaperPaused = true
        controller.handle(.settingsChanged)
        XCTAssertFalse(controller.running)
        XCTAssertEqual(controller.state, .paused)
        XCTAssertEqual(controller.status, AudioCaptureGate.PauseReason.wallpaperPaused.message)
        XCTAssertFalse(session.ioRunning, "Nothing holds the output device while the wallpaper is paused.")
        XCTAssertEqual(session.stops, 0, "The tap keeps its devices for a quick resume.")
        XCTAssertTrue(world.output.started, "Playback and the route are followed, as for covered wallpapers.")
        XCTAssertTrue(controller.diagnosticsReport().contains("visualiser surfaces: 0 of 2 drawing, 2 frozen by Pause wallpaper"))
        world.advance(60)
        XCTAssertFalse(controller.running)

        // Switching System audio on while paused waits for the wallpaper.
        controller.start()
        XCTAssertFalse(controller.running)
        XCTAssertEqual(world.requests.count, 1)
        XCTAssertEqual(controller.status, AudioCaptureGate.PauseReason.wallpaperPaused.message)

        world.preferences.wallpaperPaused = false
        controller.handle(.settingsChanged)
        XCTAssertTrue(controller.running)
        XCTAssertTrue(session.ioRunning)
        XCTAssertEqual(world.sessions.count, 1)
    }

    /// The Settings preview keeps moving through Pause wallpaper -- it is
    /// where changes are judged -- so while it shows the visualiser, capture
    /// keeps running for it and for the meters beside it. Hidden, it waits
    /// out the hang time like any surface; closed, capture pauses at once.
    @MainActor
    func testASettingsPreviewStillMovingKeepsCaptureThroughPauseWallpaper() async throws {
        let world = CaptureWorld()
        world.preferences.wallpaperPaused = true
        world.previewVisible = true
        let controller = try makeController(world)
        defer { controller.stop() }
        controller.resumeIfEnabled()
        await controller.pendingStart?.value
        XCTAssertTrue(controller.running, "The preview draws the visualiser.")
        let session = try XCTUnwrap(world.sessions.first)
        world.advance(AudioCaptureGate.hiddenHangTime + 0.5)
        XCTAssertTrue(controller.running)

        world.previewVisible = false
        controller.handle(.surfacesChanged)
        world.advance(AudioCaptureGate.hiddenHangTime - 0.5)
        XCTAssertTrue(controller.running, "A covered preview may be back at any moment.")
        world.advance(1)
        XCTAssertEqual(controller.state, .paused)
        XCTAssertEqual(controller.status, AudioCaptureGate.PauseReason.hidden.message)

        world.previewVisible = true
        controller.handle(.surfacesChanged)
        XCTAssertTrue(controller.running)

        // Settings closed: only frozen wallpapers are left.
        world.previewVisible = nil
        controller.handle(.surfacesChanged)
        XCTAssertFalse(controller.running)
        XCTAssertEqual(controller.status, AudioCaptureGate.PauseReason.wallpaperPaused.message)
        XCTAssertFalse(session.ioRunning)
        XCTAssertEqual(world.sessions.count, 1)
    }

    @MainActor
    func testNothingPlayingStandsByAfterTheHangTimeAndPlaybackResumesIt() async throws {
        let world = CaptureWorld()
        let controller = try makeController(world)
        defer { controller.stop() }
        controller.resumeIfEnabled()
        await controller.pendingStart?.value
        let session = try XCTUnwrap(world.sessions.first)

        world.play(false)
        world.advance(AudioCaptureGate.outputHangTime - 0.5)
        XCTAssertTrue(controller.running, "A gap between tracks keeps capture.")
        world.advance(1)
        XCTAssertEqual(controller.state, .standby)
        XCTAssertEqual(controller.status, AudioCaptureGate.standbyMessage)
        XCTAssertFalse(session.ioRunning, "Nothing holds the output device or idle sleep now.")

        world.play(true)
        XCTAssertTrue(controller.running)
        XCTAssertTrue(session.ioRunning)
        XCTAssertEqual(controller.state, .connecting)

        // Another app playing counts as well.
        world.play(false)
        world.advance(AudioCaptureGate.outputHangTime + 0.5)
        XCTAssertEqual(controller.state, .standby)
        world.output.report(true)
        XCTAssertTrue(controller.running)
        XCTAssertEqual(session.resumes, 2)
        XCTAssertEqual(world.sessions.count, 1)
    }

    /// Spotify Connect to a speaker: Spotify reports playing, but none of its
    /// sound is on this Mac. Nothing here is worth capturing, and nothing is
    /// wrong with the permission.
    @MainActor
    func testAPlayerPlayingOnAnotherDeviceStandsByAndSaysSo() async throws {
        let world = CaptureWorld()
        world.activePlayer = .spotify
        world.output.report(PlayerKind.spotify.bundleID, playing: false)
        let controller = try makeController(world)
        defer { controller.stop() }
        controller.resumeIfEnabled()
        XCTAssertEqual(controller.state, .standby)
        XCTAssertTrue(world.requests.isEmpty, "No tap holds the output device for silence.")
        XCTAssertTrue(controller.status.lowercased().contains("nothing from spotify is playing on this mac"),
                      controller.status)
        XCTAssertFalse(controller.state.needsAttention)

        // Moving playback to this Mac starts capture at once.
        world.output.report(PlayerKind.spotify.bundleID, playing: true)
        await controller.pendingStart?.value
        XCTAssertTrue(controller.running)
        XCTAssertEqual(world.requests.count, 1)
        XCTAssertEqual(world.requests.first?.tap, true)

        // And back to the speaker: standing by again after the hang time.
        world.output.report(PlayerKind.spotify.bundleID, playing: false)
        world.advance(AudioCaptureGate.outputHangTime + 0.5)
        XCTAssertEqual(controller.state, .standby)
        XCTAssertFalse(try XCTUnwrap(world.sessions.first).ioRunning)
        XCTAssertTrue(controller.status.lowercased().contains("nothing from spotify"), controller.status)
        XCTAssertEqual(world.requests.count, 1)
    }

    /// Copy audio diagnostics says how the visuals are aligned with the
    /// sound and what the web scenes report, which a late or early visual
    /// (an output change, a Core Audio restart) could not be told from.
    @MainActor
    func testDiagnosticsReportTheAlignmentThePlayerAndTheWebScenes() async throws {
        let world = CaptureWorld()
        world.activePlayer = .spotify
        world.output.report(PlayerKind.spotify.bundleID, playing: true)
        world.outputLatency = 0.182
        world.visualizer.setOutputLatency(0.182)
        world.webScenes = "Wavefield: Rendering with WebGL. Restarted 2 times.\nLight 3D: Rendering with WebGPU."
        let controller = try makeController(world)
        defer { controller.stop() }
        controller.resumeIfEnabled()
        await controller.pendingStart?.value

        let report = controller.diagnosticsReport()
        XCTAssertTrue(report.contains("Alignment: output latency 182 ms; visuals delayed 137 ms"), report)
        XCTAssertTrue(report.contains("Player: Spotify playing; on this Mac: yes"), report)
        XCTAssertTrue(report.contains("visualiser surfaces: 1 of 1 drawing"), report)
        XCTAssertTrue(report.contains("Web scenes:\n  Wavefield: Rendering with WebGL. Restarted 2 times.\n  Light 3D: Rendering with WebGPU."),
                      report)
        world.webScenes = ""
        XCTAssertTrue(controller.diagnosticsReport().contains("Web scenes: none reported"))
    }

    @MainActor
    func testDisplaySleepReleasesCaptureAndWakingReconnects() async throws {
        let world = CaptureWorld()
        let controller = try makeController(world)
        defer { controller.stop() }
        controller.resumeIfEnabled()
        await controller.pendingStart?.value
        let first = try XCTUnwrap(world.sessions.first)

        controller.handle(.displaysSlept)
        XCTAssertEqual(controller.state, .paused)
        XCTAssertEqual(controller.status, AudioCaptureGate.PauseReason.displaysAsleep.message)
        XCTAssertEqual(first.stops, 1)
        XCTAssertFalse(world.output.started, "Playback cannot matter while the displays sleep.")
        XCTAssertEqual(world.pendingTimers, 0, "No timer runs while the displays sleep.")

        controller.handle(.displaysWoke)
        world.advance(0.3)
        XCTAssertEqual(world.sessions.count, 1, "The output route settles first.")
        world.advance(0.3)
        await controller.pendingStart?.value
        XCTAssertTrue(controller.running)
        XCTAssertEqual(world.sessions.count, 2)
        XCTAssertTrue(world.output.started)
    }

    @MainActor
    func testPowerLimitsKeepTheSwitchOnAndSayWhy() async throws {
        let world = CaptureWorld()
        world.preferences.enabled = false
        let reason = try XCTUnwrap(AudioCaptureGate.powerPause(chosen: .visualizer, effective: .pattern,
            lowPowerVisuals: false, lowPowerMode: true, onBattery: false))
        world.preferences.powerPause = reason
        let controller = try makeController(world)
        defer { controller.stop() }
        controller.start()
        XCTAssertTrue(world.preferences.enabled, "The switch stays on.")
        XCTAssertEqual(controller.state, .paused)
        XCTAssertEqual(controller.status, reason)
        XCTAssertTrue(world.requests.isEmpty)
        XCTAssertFalse(world.output.started)

        // Low Power Mode ends long after the switch was turned on: capture
        // resumes by itself, but must not raise a prompt out of context.
        world.advance(AudioCaptureRecovery.userStartGrace + 1)
        world.preferences.powerPause = nil
        controller.handle(.settingsChanged)
        await controller.pendingStart?.value
        XCTAssertTrue(controller.running)
        XCTAssertEqual(world.requests.count, 1)
        XCTAssertEqual(world.requests.first?.userInitiated, false)
    }

    @MainActor
    func testAUserStartConnectsAtOnceThenStandsByWhenTheGraceEnds() async throws {
        let world = CaptureWorld()
        world.preferences.enabled = false
        world.musicPlaying = false
        let controller = try makeController(world)
        defer { controller.stop() }
        controller.start()
        await controller.pendingStart?.value
        XCTAssertTrue(controller.running, "Connect now, so macOS can ask for permission in context.")
        XCTAssertEqual(world.requests.first?.userInitiated, true)
        world.advance(AudioCaptureRecovery.userStartGrace - 1)
        XCTAssertTrue(controller.running)
        world.advance(2)
        XCTAssertEqual(controller.state, .standby)
        XCTAssertFalse(try XCTUnwrap(world.sessions.first).ioRunning)
    }

    @MainActor
    func testTheWatchdogNeverStacksASecondPromptWhileTheFirstMayBeOpen() async throws {
        let world = CaptureWorld()
        world.preferences.method = .automatic
        world.screenRecordingAllowed = true
        world.holdsConnections = true
        let controller = try makeController(world)
        defer { controller.stop() }
        controller.start()
        await world.settle()
        XCTAssertEqual(world.requests.count, 1)
        world.advance(12.5)
        XCTAssertEqual(controller.state, .attention(.slowStart))
        XCTAssertEqual(world.requests.count, 1, "No compatibility capture, and so no Screen Recording prompt.")
        XCTAssertTrue(controller.offersCompatibilityCapture)

        // The user may choose it, and that start may prompt.
        world.holdsConnections = false
        controller.tryCompatibilityCapture()
        await controller.pendingStart?.value
        XCTAssertEqual(world.requests.count, 2)
        XCTAssertEqual(world.requests.last?.tap, false)
        XCTAssertEqual(world.requests.last?.userInitiated, true)
        XCTAssertTrue(controller.running)
    }

    @MainActor
    func testAFailedStartWaitsForTheUserOrTheNextPlayback() async throws {
        let world = CaptureWorld()
        world.openError = LyricHTTP.Failure(message: "Not allowed.")
        let controller = try makeController(world)
        defer { controller.stop() }
        controller.resumeIfEnabled()
        await controller.pendingStart?.value
        XCTAssertEqual(controller.state, .attention(.failed))
        XCTAssertTrue(controller.status.contains("Not allowed."))
        XCTAssertTrue(controller.suggestsPrivacySettings)

        controller.handle(.settingsChanged)
        controller.handle(.surfacesChanged)
        world.advance(30)
        XCTAssertEqual(world.requests.count, 1, "No retry loop.")

        // The next playback tries once more.
        world.play(false)
        world.advance(AudioCaptureGate.outputHangTime + 0.5)
        world.play(true)
        await controller.pendingStart?.value
        XCTAssertEqual(world.requests.count, 2)
        XCTAssertEqual(controller.state, .attention(.failed))

        world.openError = nil
        controller.reconnect()
        await controller.pendingStart?.value
        XCTAssertTrue(controller.running)
        XCTAssertEqual(world.requests.count, 3)
    }

    @MainActor
    func testCompatibilityCaptureStopsWhileIdleAndStartsAgainOnDemand() async throws {
        let world = CaptureWorld()
        world.preferences.method = .screenRecording
        let controller = try makeController(world)
        defer { controller.stop() }
        controller.resumeIfEnabled()
        await controller.pendingStart?.value
        XCTAssertEqual(world.requests.first?.tap, false)
        let first = try XCTUnwrap(world.sessions.first)

        world.play(false)
        world.advance(AudioCaptureGate.outputHangTime + 0.5)
        XCTAssertEqual(controller.state, .standby)
        XCTAssertEqual(first.stops, 1, "Stopping the stream also removes the recording indicator.")
        XCTAssertEqual(first.suspends, 0)

        world.play(true)
        await controller.pendingStart?.value
        XCTAssertTrue(controller.running)
        XCTAssertEqual(world.sessions.count, 2)
    }

    @MainActor
    func testPlaybackIsNotObservedWhileCaptureCannotRun() async throws {
        let world = CaptureWorld()
        let controller = try makeController(world)
        defer { controller.stop() }
        controller.resumeIfEnabled()
        await controller.pendingStart?.value
        XCTAssertTrue(world.output.started)

        world.preferences.visualiserChosen = false
        controller.handle(.settingsChanged)
        XCTAssertEqual(controller.state, .paused)
        XCTAssertFalse(world.output.started, "No per-process listeners while another wallpaper is shown.")
        XCTAssertEqual(world.pendingTimers, 0, "No poll or health timer either.")

        // Nothing playback or the system does is followed, or republished.
        var publications = 0
        let watch = controller.objectWillChange.sink { _ in publications += 1 }
        world.play(false)
        world.output.report(true)
        controller.handle(.powerSourceChanged)
        controller.handle(.surfacesChanged)
        controller.handle(.settingsChanged)
        XCTAssertEqual(publications, 0)
        watch.cancel()

        world.preferences.visualiserChosen = true
        controller.handle(.settingsChanged)
        XCTAssertTrue(world.output.started)
        await controller.pendingStart?.value
        XCTAssertTrue(controller.running, "Another app is playing.")
    }

    @MainActor
    func testSuspendAndResumeDoNotWaitForTheDevice() async throws {
        let world = CaptureWorld()
        let controller = try makeController(world)
        defer { controller.stop() }
        controller.resumeIfEnabled()
        await controller.pendingStart?.value
        let first = try XCTUnwrap(world.sessions.first)

        // A failed suspend that a resume has already overtaken is ignored.
        first.suspendSucceeds = false
        world.play(false)
        world.advance(AudioCaptureGate.outputHangTime + 0.5)
        XCTAssertEqual(controller.state, .standby, "Standing by doesn't wait for the device.")
        world.play(true)
        await world.settle()
        XCTAssertEqual(first.stops, 0)
        XCTAssertTrue(controller.running)

        // The latest one stops the session instead.
        world.play(false)
        world.advance(AudioCaptureGate.outputHangTime + 0.5)
        await world.settle()
        XCTAssertEqual(first.stops, 1)
        XCTAssertEqual(controller.state, .standby)

        // A failed resume builds a new session.
        world.play(true)
        await controller.pendingStart?.value
        let second = try XCTUnwrap(world.sessions.last)
        XCTAssertFalse(first === second)
        second.resumeError = LyricHTTP.Failure(message: "The device went away.")
        world.play(false)
        world.advance(AudioCaptureGate.outputHangTime + 0.5)
        world.play(true)
        XCTAssertTrue(controller.running)
        await world.settle()
        await controller.pendingStart?.value
        XCTAssertEqual(second.stops, 1)
        XCTAssertEqual(world.sessions.count, 3)
        XCTAssertTrue(controller.running)
    }

    @MainActor
    func testRouteAndFormatChangesRebuildTheSession() async throws {
        let world = CaptureWorld()
        let controller = try makeController(world)
        defer { controller.stop() }
        controller.resumeIfEnabled()
        await controller.pendingStart?.value

        controller.handle(.outputRouteChanged)
        world.advance(0.5)
        await controller.pendingStart?.value
        XCTAssertEqual(world.sessions.count, 2)
        XCTAssertEqual(world.sessions.first?.stops, 1)
        XCTAssertTrue(controller.running)

        world.requests.last?.onFormatChange()
        world.advance(0.5)
        await controller.pendingStart?.value
        XCTAssertEqual(world.sessions.count, 3)

        // Standing by: the old devices go now, new ones when audio plays.
        world.play(false)
        world.advance(AudioCaptureGate.outputHangTime + 0.5)
        controller.handle(.outputRouteChanged)
        world.advance(0.5)
        XCTAssertEqual(world.sessions.last?.stops, 1)
        XCTAssertEqual(controller.state, .standby)
        XCTAssertEqual(world.sessions.count, 3)
        world.play(true)
        await controller.pendingStart?.value
        XCTAssertEqual(world.sessions.count, 4)
        XCTAssertTrue(controller.running)
    }
}

// MARK: - Brief listening for the music video

extension AudioCaptureControllerTests {
    /// The Music video wallpaper: no visualiser chosen, System audio on.
    @MainActor
    private func musicVideoWorld() -> CaptureWorld {
        let world = CaptureWorld()
        world.preferences.visualiserChosen = false
        return world
    }

    @MainActor
    func testABriefListenNeedsAPermissionAlreadyGranted() async throws {
        let world = musicVideoWorld()
        let controller = try makeController(world)
        defer { controller.stop() }
        controller.resumeIfEnabled()
        XCTAssertFalse(controller.running, "paused behind the Music video wallpaper")
        XCTAssertFalse(controller.mayListenBriefly, "never worked here: it could ask for permission")
        XCTAssertFalse(controller.listenBriefly(for: 25))
        await world.settle()
        XCTAssertTrue(world.requests.isEmpty, "no capture without the permission")

        world.preferences.enabled = false
        world.captureConfirmed = true
        XCTAssertFalse(controller.mayListenBriefly, "System audio switched off")
        world.preferences.enabled = true
        world.preferences.method = .screenRecording
        XCTAssertFalse(controller.mayListenBriefly, "compatibility capture needs Screen Recording allowed")
        world.screenRecordingAllowed = true
        XCTAssertTrue(controller.mayListenBriefly)
    }

    @MainActor
    func testABriefListenCapturesThenStopsAtItsLimitOrWhenEnded() async throws {
        let world = musicVideoWorld()
        world.captureConfirmed = true
        let controller = try makeController(world)
        defer { controller.stop() }
        controller.resumeIfEnabled()
        XCTAssertTrue(controller.listenBriefly(for: 25))
        XCTAssertTrue(controller.listeningBriefly)
        await controller.pendingStart?.value
        XCTAssertTrue(controller.running)
        XCTAssertFalse(world.requests.first?.userInitiated ?? true, "never a prompting start")
        let session = try XCTUnwrap(world.sessions.first)
        world.advance(24)
        XCTAssertTrue(controller.running)
        world.advance(1.5)
        XCTAssertFalse(controller.listeningBriefly)
        XCTAssertFalse(controller.running, "stopped at the limit")
        XCTAssertEqual(controller.state, .paused)
        XCTAssertFalse(session.ioRunning)

        XCTAssertTrue(controller.listenBriefly(for: 25))
        await controller.pendingStart?.value
        XCTAssertTrue(controller.running)
        controller.endBriefListen()
        XCTAssertFalse(controller.running, "stopped as soon as the alignment is done")
    }

    @MainActor
    func testABriefListenStillWaitsForAudioToPlay() async throws {
        let world = musicVideoWorld()
        world.captureConfirmed = true
        world.musicPlaying = false
        let controller = try makeController(world)
        defer { controller.stop() }
        controller.resumeIfEnabled()
        XCTAssertTrue(controller.listenBriefly(for: 25))
        await controller.pendingStart?.value
        XCTAssertFalse(controller.running, "nothing playing: standing by")
        XCTAssertEqual(controller.state, .standby)
    }
}

/// A pretend system for one controller.
@MainActor
private final class CaptureWorld {
    var now = 1_000.0
    var preferences = AudioCapturePreferences(enabled: true, visualiserChosen: true, powerPause: nil,
                                              pauseWhenCovered: true, method: .systemAudio)
    /// Whether each display's wallpaper can be seen.
    var visibility: [Bool] = [true]
    /// Whether the Settings preview can be seen; nil while it is not shown.
    var previewVisible: Bool?
    var musicPlaying = true
    var activePlayer: PlayerKind? = .appleMusic
    var screenRecordingAllowed = false
    /// Capture has delivered audio here before (its permission is granted).
    var captureConfirmed = false
    /// Thrown by connection attempts.
    var openError: Error?
    /// Connection attempts wait, as behind an open permission prompt.
    var holdsConnections = false
    let output = FakeOutputActivity()
    var outputLatency = 0.0
    var webScenes = ""
    private let music = PassthroughSubject<Bool, Never>()
    let visualizer = VisualizerAudio()
    private(set) var requests: [AudioCaptureRequest] = []
    private(set) var sessions: [FakeCaptureSession] = []

    private struct Scheduled {
        let id: Int
        var due: Double
        let interval: Double?
        let action: @MainActor () -> Void
    }
    private var scheduled: [Scheduled] = []
    private var nextID = 0

    var environment: AudioCaptureEnvironment {
        AudioCaptureEnvironment(
            now: { self.now },
            schedule: { delay, repeating, action in self.schedule(delay, repeating, action) },
            preferences: { self.preferences },
            setEnabled: { self.preferences.enabled = $0 },
            preferencesChanged: { Empty(completeImmediately: false).eraseToAnyPublisher() },
            surfaces: { self.surfaces(pauseWhenCovered: $0, wallpaperPaused: $1) },
            surfacesChanged: { Empty(completeImmediately: false).eraseToAnyPublisher() },
            isMusicPlaying: { self.musicPlaying },
            musicPlayingChanges: { self.music.eraseToAnyPublisher() },
            activePlayer: { self.activePlayer },
            makeOutputActivity: { self.output },
            observesSystem: false,
            screenRecordingAllowed: { self.screenRecordingAllowed },
            visualizer: visualizer,
            outputLatency: { self.outputLatency },
            webScenes: { self.webScenes },
            openSession: { try await self.open($0) },
            captureConfirmed: { self.captureConfirmed },
            confirmCapture: { self.captureConfirmed = true })
    }

    var pendingTimers: Int { scheduled.count }

    /// The wallpapers and the preview, by the rule their frame clocks follow.
    private func surfaces(pauseWhenCovered: Bool, wallpaperPaused: Bool) -> [WindowRenderActivity.State] {
        let wallpapers = visibility.map {
            WindowRenderActivity.state(visible: $0, screensAsleep: false, followsWindow: false,
                                       pauseWhenCovered: pauseWhenCovered, wallpaperPaused: wallpaperPaused)
        }
        let preview = previewVisible.map {
            WindowRenderActivity.state(visible: $0, screensAsleep: false, followsWindow: true,
                                       pauseWhenCovered: pauseWhenCovered, wallpaperPaused: wallpaperPaused)
        }
        return wallpapers + (preview.map { [$0] } ?? [])
    }

    func play(_ playing: Bool) {
        musicPlaying = playing
        music.send(playing)
    }

    /// Moves the clock on, firing whatever falls due, in order.
    func advance(_ seconds: Double) {
        let end = now + seconds
        while let next = scheduled.filter({ $0.due <= end }).min(by: { ($0.due, $0.id) < ($1.due, $1.id) }) {
            now = max(now, next.due)
            if let interval = next.interval, let index = scheduled.firstIndex(where: { $0.id == next.id }) {
                scheduled[index].due += max(interval, 0.001)
            } else {
                scheduled.removeAll { $0.id == next.id }
            }
            next.action()
        }
        now = end
    }

    /// Lets work already queued on the main thread run: session completions
    /// and the start of a connection.
    func settle() async {
        for _ in 0..<3 {
            await withCheckedContinuation { continuation in DispatchQueue.main.async { continuation.resume() } }
        }
    }

    private func schedule(_ delay: Double, _ repeating: Double?, _ action: @escaping @MainActor () -> Void) -> AnyCancellable {
        let id = nextID
        nextID += 1
        scheduled.append(Scheduled(id: id, due: now + max(0, delay), interval: repeating, action: action))
        return AnyCancellable { [weak self] in
            MainActor.assumeIsolated { self?.scheduled.removeAll { $0.id == id } }
        }
    }

    private func open(_ request: AudioCaptureRequest) async throws -> AudioCaptureSession {
        requests.append(request)
        if holdsConnections { try await Task.sleep(nanoseconds: 3_600_000_000_000) }
        if let openError { throw openError }
        let session = FakeCaptureSession(tap: request.tap)
        sessions.append(session)
        return session
    }
}

/// Other processes' output, as the controller sees it.
@MainActor
private final class FakeOutputActivity: AudioOutputActivitySource {
    private(set) var isActive: Bool?
    var onChange: (() -> Void)?
    private(set) var started = false
    private var othersPlaying = false
    /// Apps whose own processes are known to be playing, or not.
    private var apps: [String: Bool] = [:]
    private var anyPlaying: Bool { othersPlaying || apps.values.contains(true) }

    func isRunningOutput(bundleID: String) -> Bool? { started ? apps[bundleID] : nil }
    func start() { started = true; isActive = anyPlaying }
    func stop() { started = false; isActive = nil }
    func refresh() {
        guard started, isActive != anyPlaying else { return }
        isActive = anyPlaying
        onChange?()
    }
    func report(_ playing: Bool) {
        othersPlaying = playing
        refresh()
    }
    func report(_ app: String, playing: Bool) {
        apps[app] = playing
        refresh()
    }
}

/// A session whose device work completes on the next main-queue turn.
private final class FakeCaptureSession: AudioCaptureSession {
    let tap: Bool
    private(set) var stops = 0, suspends = 0, resumes = 0
    var suspendSucceeds = true
    var resumeError: Error?
    init(tap: Bool) { self.tap = tap }

    /// A tap keeps its devices while idle; compatibility capture can't.
    var canSuspend: Bool { tap }
    /// Started or resumed, and not since suspended or stopped.
    var ioRunning: Bool { stops == 0 && suspends == resumes }
    func stop() { stops += 1 }
    func suspend(completion: @escaping (Bool) -> Void) {
        suspends += 1
        let succeeds = suspendSucceeds
        DispatchQueue.main.async { completion(succeeds) }
    }
    func resume(completion: @escaping (Error?) -> Void) {
        resumes += 1
        let error = resumeError
        DispatchQueue.main.async { completion(error) }
    }
}
