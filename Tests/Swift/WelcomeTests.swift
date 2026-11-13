import AppKit
import XCTest
@testable import VersoCustom

/// Onboarding (Sources/UI/Onboarding): what it promises, the order of its
/// steps, and above all that nothing asks a permission before the person
/// continues past the step that says it will.
final class WelcomeTests: XCTestCase {
    private let musicID = "com.apple.Music"

    @MainActor
    func testTheWelcomeExplainsThePromptAndTheWayBack() {
        let note = OnboardingCopy.permissionNote(installed: PlayerKind.allCases)
            + OnboardingCopy.permissionDetail(installed: PlayerKind.allCases)
        XCTAssertTrue(note.contains("macOS asks"))
        XCTAssertTrue(note.contains("only reads the song and its position"))
        for kind in PlayerKind.apps {
            XCTAssertTrue(note.contains(kind.displayName))
        }
        for service in WebService.allCases {
            XCTAssertTrue(note.contains(service.displayName))
        }
        XCTAssertTrue(OnboardingCopy.menuBarNote.contains("notch"))
        XCTAssertTrue(OnboardingCopy.menuBarNote.contains("Applications"))
        XCTAssertTrue(PlayerAccessStatus.denied.summary.contains("Privacy & Security › Automation"))
        let statuses: [PlayerAccessStatus] = [.granted, .denied, .notDetermined, .notRunning, .notInstalled, .unknown]
        XCTAssertEqual(Set(statuses.map(\.symbol)).count, statuses.count, "Each status reads differently")
        // What lyric services receive, as About › Privacy says.
        XCTAssertTrue(OnboardingCopy.sources.contains("title, artist, album and length"))
        XCTAssertTrue(OnboardingCopy.translations.contains("Lyrics are never sent anywhere to be translated"))
        XCTAssertEqual(OnboardingCopy.asksOnContinue([.appleMusic]), "macOS asks about Music when you continue.")
        XCTAssertEqual(OnboardingCopy.asksOnContinue([.appleMusic, .spotify]),
                       "macOS asks about Music and Spotify when you continue.")
        XCTAssertTrue(OnboardingCopy.timing(earlier: "⌃⌥⌘[", later: "⌃⌥⌘]").contains("⌃⌥⌘[ and ⌃⌥⌘]"))
        XCTAssertEqual(OnboardingCopy.done(playing: nil, idlePrompt: "Play a song in Music"),
                       "Play a song in Music and its lyrics appear on your desktop.")
    }

    func testPermissionStatusesMapFromAppleEventResults() {
        XCTAssertEqual(PlayerAccessStatus(status: 0), .granted)
        XCTAssertEqual(PlayerAccessStatus(status: -1743), .denied)
        XCTAssertEqual(PlayerAccessStatus(status: -1744), .notDetermined)
        XCTAssertEqual(PlayerAccessStatus(status: -600), .notRunning)
        XCTAssertEqual(PlayerAccessStatus(status: -50), .unknown)
    }

    @MainActor
    func testStepsRunInOrderWithNoWayBackFromTheFirst() {
        let rig = OnboardingRig()
        defer { rig.tearDown() }
        let flow = OnboardingFlow(models: rig.models, firstRun: true)
        XCTAssertEqual(flow.step, .welcome)
        XCTAssertFalse(flow.canGoBack, "Back is absent on the first step")
        flow.back()
        XCTAssertEqual(flow.step, .welcome)
        var visited = [flow.step]
        while !flow.isLastStep {
            flow.advance()
            visited.append(flow.step)
        }
        XCTAssertEqual(visited, OnboardingFlow.Step.allCases)
        XCTAssertEqual(visited, [.welcome, .music, .look, .videos, .lyrics, .extras, .menuBar, .done])
        XCTAssertEqual(visited.map(\.title), ["Welcome", "Your music", "Pick a look", "Music videos", "Lyrics & readings",
                                              "Extras", "The menu bar", "Done"])
        flow.advance()
        XCTAssertEqual(flow.step, .done, "Continue stops at the end")
        flow.back()
        XCTAssertEqual(flow.step, .menuBar)
        XCTAssertTrue(flow.canGoBack)
        XCTAssertEqual(OnboardingFlow.Step.music.position, "Step 2 of 8")
        XCTAssertEqual(OnboardingFlow.Step.done.position, "Step 8 of 8")
    }

    /// The access step starts following the players without asking, so
    /// Allow Access… can reach them; Continue from it lets macOS ask about a
    /// running player it hasn't asked about, and about nothing else.
    @MainActor
    func testTheMusicStepFollowsSilentlyAndContinueAsksAboutUndecidedPlayers() {
        let rig = OnboardingRig()
        defer { rig.tearDown() }
        rig.harness.permission.statuses[musicID] = OSStatus(AppleEventCode.wouldRequireConsent)
        let flow = OnboardingFlow(models: rig.models, firstRun: true)
        XCTAssertTrue(rig.harness.monitor.runningPlayers.isEmpty, "nothing is followed on the welcome step")

        flow.advance()
        rig.harness.scheduler.drain()
        XCTAssertEqual(flow.step, .music)
        XCTAssertEqual(rig.harness.monitor.runningPlayers, [.appleMusic], "the monitor found the running player")
        XCTAssertEqual(rig.harness.permission.prompts(musicID), 0, "arriving at the step asks nothing")
        XCTAssertEqual(flow.playersMacOSWillAsk, [.appleMusic], "the step says macOS will ask about Music")

        // Back and forth again asks nothing either.
        flow.back()
        flow.advance()
        rig.harness.scheduler.drain()
        XCTAssertEqual(rig.harness.permission.prompts(musicID), 0)

        flow.advance()
        rig.harness.scheduler.drain()
        XCTAssertEqual(flow.step, .look)
        XCTAssertEqual(rig.harness.permission.prompts(musicID), 1, "Continue raised macOS's prompt once")

        // Every later Continue leaves the players alone.
        while !flow.isLastStep { flow.advance() }
        rig.harness.scheduler.drain()
        XCTAssertEqual(rig.harness.permission.prompts(musicID), 1)
    }

    @MainActor
    func testContinueLeavesDecidedUnfollowedAndClosedPlayersAlone() {
        // Allowed already: macOS has nothing to ask.
        let allowed = OnboardingRig()
        defer { allowed.tearDown() }
        let first = OnboardingFlow(models: allowed.models, firstRun: true, step: .welcome)
        first.advance()
        allowed.harness.scheduler.drain()
        XCTAssertTrue(first.playersMacOSWillAsk.isEmpty)
        first.advance()
        allowed.harness.scheduler.drain()
        XCTAssertEqual(allowed.harness.permission.prompts(musicID), 0)

        // Refused: only Open Privacy Settings can change that.
        let refused = OnboardingRig()
        defer { refused.tearDown() }
        refused.harness.permission.statuses[musicID] = OSStatus(AppleEventCode.notPermitted)
        let second = OnboardingFlow(models: refused.models, firstRun: true)
        second.advance()
        refused.harness.scheduler.drain()
        second.advance()
        refused.harness.scheduler.drain()
        XCTAssertEqual(refused.harness.permission.prompts(musicID), 0)

        // Undecided, but the person chose to follow Spotify only.
        let unfollowed = OnboardingRig()
        defer { unfollowed.tearDown() }
        unfollowed.harness.permission.statuses[musicID] = OSStatus(AppleEventCode.wouldRequireConsent)
        unfollowed.players.choice = .spotify
        let third = OnboardingFlow(models: unfollowed.models, firstRun: true)
        third.advance()
        unfollowed.harness.scheduler.drain()
        XCTAssertTrue(third.playersToAsk.isEmpty)
        third.advance()
        unfollowed.harness.scheduler.drain()
        XCTAssertEqual(unfollowed.harness.permission.prompts(musicID), 0)

        // Not open: nothing to ask until it is.
        let closed = OnboardingRig(running: [])
        defer { closed.tearDown() }
        let fourth = OnboardingFlow(models: closed.models, firstRun: true)
        fourth.advance()
        fourth.advance()
        closed.harness.scheduler.drain()
        XCTAssertTrue(closed.harness.permission.requests.allSatisfy { !$0.ask })
    }

    /// B1, B2: finishing by either button or by closing the window counts,
    /// follows the players (silently, if the music step was never reached),
    /// and only Open Settings opens Settings.
    @MainActor
    func testFinishingTheFirstRunCountsAndOnlyOpenSettingsOpensSettings() {
        for (ending, opensSettings) in [(OnboardingEnding.openSettings, true), (.done, false), (.closed, false)] {
            let rig = OnboardingRig()
            defer { rig.tearDown() }
            rig.harness.permission.statuses[musicID] = OSStatus(AppleEventCode.wouldRequireConsent)
            XCTAssertFalse(rig.models.settings.hasOnboarded)
            OnboardingWindowController.completeFirstRun(ending, models: rig.models)
            rig.harness.scheduler.drain()
            XCTAssertTrue(rig.models.settings.hasOnboarded, "\(ending)")
            XCTAssertTrue(rig.defaults.bool(forKey: "hasOnboarded"), "saved under the same key")
            XCTAssertEqual(rig.harness.monitor.runningPlayers, [.appleMusic], "\(ending): following the players")
            XCTAssertEqual(rig.harness.permission.prompts(musicID), 0, "\(ending): without asking")
            XCTAssertEqual(rig.navigated, opensSettings ? ["settings"] : [], "\(ending)")
        }
    }

    @MainActor
    func testFinishingSaysHowItEndedAndShownAgainItJustCloses() {
        let rig = OnboardingRig()
        defer { rig.tearDown() }
        var endings: [OnboardingEnding] = []
        let firstRun = OnboardingFlow(models: rig.models, firstRun: true, step: .done) { endings.append($0) }
        firstRun.finish(.openSettings)
        firstRun.finish(.done)
        let again = OnboardingFlow(models: rig.models, firstRun: false, step: .done) { endings.append($0) }
        again.finish(.openSettings)
        again.finish(.done)
        XCTAssertEqual(endings, [.openSettings, .done, .done, .done])
    }

    @MainActor
    func testTheWindowIsAFixedSizeWithASeeThroughTitleBarAndClosesWithCommandW() throws {
        let window = OnboardingWindow(contentRect: NSRect(origin: .zero, size: LunalyrDesign.Layout.onboarding),
                                      styleMask: OnboardingWindowController.styleMask, backing: .buffered, defer: true)
        window.isReleasedWhenClosed = false
        OnboardingWindowController.configure(window)
        XCTAssertEqual(window.title, "Welcome to Lunalyr", "VoiceOver and the Window menu name it")
        XCTAssertTrue(window.titlebarAppearsTransparent)
        XCTAssertTrue(window.styleMask.contains(.closable))
        XCTAssertTrue(window.styleMask.contains(.fullSizeContentView))
        XCTAssertFalse(window.styleMask.contains(.resizable))
        XCTAssertFalse(window.styleMask.contains(.miniaturizable))
        XCTAssertEqual(window.frame.size, LunalyrDesign.Layout.onboarding, "640 x 540, title bar included")
        // Fits a 13-inch Air at its Larger Text resolution (1024 x 665) with room to spare.
        XCTAssertLessThan(window.frame.height, 665 - 37 - 66)

        let closing = CloseRecorder()
        window.delegate = closing
        let commandW = try XCTUnwrap(NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: .command,
                                                      timestamp: 0, windowNumber: window.windowNumber, context: nil,
                                                      characters: "w", charactersIgnoringModifiers: "w",
                                                      isARepeat: false, keyCode: 13))
        XCTAssertTrue(window.performKeyEquivalent(with: commandW))
        XCTAssertTrue(closing.closed, "⌘W closes it, which counts as finishing")
    }

    /// Drawing every step asks no permission, opens nothing and writes no
    /// shared preference: the snapshot harness and the app's own first
    /// frames rely on it.
    @MainActor
    func testDrawingTheStepsAsksNothingAndOpensNothing() throws {
        let scene = UISnapshotScene(player: .needsConsent)
        defer { scene.tearDown() }
        let shared = VersoCustom.Settings.shared.hasOnboarded
        for step in OnboardingFlow.Step.allCases {
            let image = try UISnapshotRenderer.render(
                UISnapshotCatalogue.onboardingView(scene, step: step).lunalyrModels(scene.models)
                    .defaultAppStorage(scene.defaults),
                chrome: UISnapshotCatalogue.onboardingWindowChrome, size: .frame(LunalyrDesign.Layout.onboarding),
                look: .light, pump: scene.pump)
            XCTAssertEqual(image.width, 1280, "\(step.title): drawn at 2x, 640 points wide")
            XCTAssertTrue(UISnapshotTests.hasContent(image), step.title)
        }
        XCTAssertEqual(scene.harness.permission.prompts(musicID), 0, "drawing never raises macOS's prompt")
        XCTAssertEqual(scene.navigated, [], "nor opens Settings or System Settings")
        XCTAssertEqual(VersoCustom.Settings.shared.hasOnboarded, shared)
        XCTAssertFalse(scene.settings.hasOnboarded, "showing a step isn't finishing")
    }

    @MainActor
    func testEverySymbolOnboardingNamesExists() {
        let symbols = ["checkmark.circle.fill", "info.circle", "leaf.fill", "pause.fill", "battery.75percent", "wifi",
                       "switch.2"]
        for symbol in symbols {
            XCTAssertNotNil(NSImage(systemSymbolName: symbol, accessibilityDescription: nil), symbol)
        }
        XCTAssertNotNil(NSImage(named: "MenuBarIconTemplate"), "the menu-bar icon the illustration lights")
        let recording = try? XCTUnwrap(DiscordStatusRecording.image, "the Discord status recording is in the bundle")
        XCTAssertEqual(recording?.representations.first?.pixelsWide, 254, "drawn at 2x: 127 points wide")
    }
    // MARK: The reworked steps

    /// The new steps' promises: music videos say what YouTube receives (the
    /// same words as Settings), and Discord what it talks to.
    func testTheNewStepsSayWhatIsSentAndWhere() {
        XCTAssertEqual(OnboardingCopy.youTubePrivacy, MusicVideoSection.privacyCaption)
        XCTAssertTrue(OnboardingCopy.youTubePrivacy.hasPrefix("Off by default"))
        XCTAssertTrue(OnboardingCopy.videos.contains("need nothing sent"))
        XCTAssertTrue(OnboardingCopy.discordPrivacy.hasPrefix("Off by default"))
        XCTAssertTrue(OnboardingCopy.discordPrivacy.contains("only to the Discord app on this Mac"))
        XCTAssertTrue(OnboardingCopy.discordPrivacy.contains("clears when playback stops"))
        XCTAssertTrue(OnboardingCopy.discordPrivacy.contains("Apple"), "the cover lookup is named")
        XCTAssertEqual(MusicVideoFraming.allCases.map(\.title), ["Fill the screen", "Show the whole video"])
        XCTAssertEqual(OnboardingCopy.layoutCaption(nil), "Automatic picks a layout to suit each song.")
        XCTAssertEqual(OnboardingCopy.layoutCaption(.broadside), LyricComposition.broadside.detail)
        // Sentence case, no periods in titles.
        for step in OnboardingFlow.Step.allCases {
            XCTAssertFalse(step.title.hasSuffix("."), step.title)
            XCTAssertNotEqual(step.title, step.title.uppercased(), step.title)
        }
    }

    /// The switches write the real settings, here a throwaway suite: Discord
    /// under its own key, off until turned on; the music-video switch picks
    /// Music video and turns back to the look chosen before it.
    @MainActor
    func testTheNewSwitchesAreBoundToTheirSettings() {
        let rig = OnboardingRig()
        defer { rig.tearDown() }
        let extras = rig.models.extras
        XCTAssertFalse(extras.showsDiscordPresence, "off by default")
        XCTAssertNil(rig.defaults.object(forKey: "custom.discordPresence"))
        extras.showsDiscordPresence = true
        XCTAssertEqual(rig.defaults.object(forKey: "custom.discordPresence") as? Bool, true)
        XCTAssertTrue(LunalyrExtras(defaults: rig.defaults).showsDiscordPresence, "read back from the same suite")
        extras.showsDiscordPresence = false
        XCTAssertEqual(rig.defaults.object(forKey: "custom.discordPresence") as? Bool, false)

        let settings = rig.models.settings
        XCTAssertFalse(settings.musicVideoOnline, "searching YouTube is off by default")
        settings.backgroundMode = .pattern
        let videos = VideosStep.videoWallpaper(settings)
        XCTAssertFalse(videos.wrappedValue)
        videos.wrappedValue = true
        XCTAssertEqual(settings.backgroundMode, .musicVideo)
        XCTAssertTrue(videos.wrappedValue)
        videos.wrappedValue = false
        XCTAssertEqual(settings.backgroundMode, .pattern, "back to the look chosen before")
        settings.libraryVideoFraming = .whole
        XCTAssertEqual(rig.defaults.string(forKey: MusicVideoFraming.libraryKey), "whole")
    }

    /// The look step's layouts: a few reworked ones, the one in use always
    /// among them; the chosen one plays its line a few times, then holds.
    @MainActor
    func testTheLookStepShowsTheLayoutInUse() {
        XCTAssertEqual(LayoutChoices.shown(current: nil), LayoutChoices.featured)
        XCTAssertEqual(LayoutChoices.shown(current: .portal), LayoutChoices.featured)
        let withEditorial = LayoutChoices.shown(current: .constellationEditorial)
        XCTAssertEqual(withEditorial.first, .constellationEditorial)
        XCTAssertEqual(withEditorial.count, LayoutChoices.featured.count)
        XCTAssertEqual(LayoutThumbnail.time(after: 0), 0)
        XCTAssertEqual(LayoutThumbnail.time(after: LayoutThumbnail.cycle + 1), 1, accuracy: 0.0001)
        XCTAssertEqual(LayoutThumbnail.time(after: LayoutThumbnail.cycle * Double(LayoutThumbnail.plays) + 1),
                       LayoutThumbnail.held)
        XCTAssertEqual(LayoutThumbnail.words.count, SampleLyric.lines.flatMap { $0 }.count)

        let rig = OnboardingRig()
        defer { rig.tearDown() }
        let flow = OnboardingFlow(models: rig.models, firstRun: true, step: .look)
        XCTAssertEqual(flow.lookPart, .wallpaper)
        flow.lookPart = .lyrics
        flow.advance()
        flow.back()
        XCTAssertEqual(flow.lookPart, .lyrics, "Back returns to the half that was showing")
    }

}

/// Isolated stores around players that haven't been followed yet, as at
/// launch before onboarding's music step: a preferences suite of its own,
/// fake players (no Apple event is ever sent) and navigation that records.
@MainActor
private final class OnboardingRig {
    let suite = "LunalyrOnboardingTests-\(UUID().uuidString)"
    let defaults: UserDefaults
    let players: PlayerOptions
    let harness: PlayerHarness
    let models: LunalyrModels
    private(set) var navigated: [String] = []

    init(running: Set<String> = ["com.apple.Music"]) {
        defaults = UserDefaults(suiteName: suite)!
        players = PlayerOptions(defaults: defaults)
        var services = NowPlayingServices.offline
        services.followedPlayers = players.playersPublisher
        harness = PlayerHarness(running: running, services: services)
        let navigation = LunalyrNavigation(presentSettings: {}, presentOnboarding: {}, onboardingIsVisible: { false },
                                           bringOnboardingForward: {}, openURL: { _ in })
        models = LunalyrModels(
            settings: VersoCustom.Settings(defaults: defaults, notifications: NotificationCenter()),
            colours: LyricColourOptions(defaults: defaults), highlightColours: LyricHighlightOptions(defaults: defaults),
            styleMemory: StyleMemory(defaults: defaults),
            extras: LunalyrExtras(defaults: defaults),
            hotkeys: GlobalHotkeys(defaults: defaults, registrar: .init(register: { _, _, _ in true }, unregister: { _ in })),
            desktop: DesktopOptions(defaults: defaults), players: players,
            visualizer: VisualizerPreferences(defaults: defaults), offsets: SongOffsets(defaults: defaults),
            nowPlaying: harness.monitor, audio: AudioReactiveController(environment: UISnapshotScene.captureEnvironment()),
            updates: AppUpdates(defaults: defaults), navigation: navigation, installedPlayers: [.appleMusic, .spotify])
        navigation.presentSettings = { [weak self] in self?.navigated.append("settings") }
        navigation.openURL = { [weak self] url in self?.navigated.append(url.absoluteString) }
    }

    func tearDown() {
        defaults.removePersistentDomain(forName: suite)
    }
}

private final class CloseRecorder: NSObject, NSWindowDelegate {
    private(set) var closed = false
    func windowWillClose(_ notification: Notification) { closed = true }
}
