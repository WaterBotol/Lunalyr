import AppKit
import XCTest
@testable import VersoCustom

// "Show the video on". Displays are plain values here; nothing reads the
// real screens except where noted, and preferences are a UUID suite.

@MainActor
final class MusicVideoDisplayTests: XCTestCase {
    private var defaults: UserDefaults!

    override func setUp() async throws {
        let name = "LunalyrTests.MusicVideoDisplays.\(UUID().uuidString)"
        defaults = UserDefaults(suiteName: name)!
        addTeardownBlock { UserDefaults(suiteName: name)?.removePersistentDomain(forName: name) }
    }

    private let builtIn = MusicVideoDisplay(id: 1, uuid: "AAAA-BUILT-IN", name: "Built-in Retina Display")
    private let external = MusicVideoDisplay(id: 5, uuid: "BBBB-PRO-27Q", name: "Pro 27Q-10")
    private var both: [MusicVideoDisplay] { [builtIn, external] }

    private func shows(_ choice: MusicVideoDisplayChoice, heavy: Bool, on display: MusicVideoDisplay,
                       connected: [MusicVideoDisplay]? = nil) -> Bool {
        MusicVideoDisplays.shows(choice, heavyScenesOnMainDisplayOnly: heavy, display: display.id,
                                 main: builtIn.id, connected: connected ?? both)
    }

    func testDefaultFollowsHeavyScenesSoNothingChanges() {
        let settings = VersoCustom.Settings(defaults: defaults, notifications: NotificationCenter())
        XCTAssertEqual(settings.musicVideoDisplays, .followHeavyScenes)
        XCTAssertNil(defaults.object(forKey: MusicVideoDisplayChoice.key), "nothing stored until the user picks")
        XCTAssertTrue(shows(.followHeavyScenes, heavy: true, on: builtIn))
        XCTAssertFalse(shows(.followHeavyScenes, heavy: true, on: external))
        XCTAssertTrue(shows(.followHeavyScenes, heavy: false, on: external))
    }

    func testAPickOverridesHeavyScenesForTheVideo() {
        for heavy in [true, false] {
            XCTAssertTrue(shows(.all, heavy: heavy, on: external))
            XCTAssertTrue(shows(.all, heavy: heavy, on: builtIn))
            XCTAssertTrue(shows(.mainOnly, heavy: heavy, on: builtIn))
            XCTAssertFalse(shows(.mainOnly, heavy: heavy, on: external))
            let pick = MusicVideoDisplayChoice.display(uuid: external.uuid, name: external.name)
            XCTAssertTrue(shows(pick, heavy: heavy, on: external))
            XCTAssertFalse(shows(pick, heavy: heavy, on: builtIn))
        }
        // Through the controller's placement, as the wallpaper asks it.
        let source = MusicVideoSource.youTube("4NRXx6U8ABQ")
        XCTAssertEqual(MusicVideoController.placement(source: source, preview: false, heavyScenesOnMainDisplayOnly: true,
                                                      choice: .all, displays: both,
                                                      display: external.id, mainDisplay: builtIn.id), source)
        XCTAssertNil(MusicVideoController.placement(source: source, preview: false, heavyScenesOnMainDisplayOnly: false,
                                                    choice: .mainOnly, displays: both,
                                                    display: external.id, mainDisplay: builtIn.id))
        XCTAssertEqual(MusicVideoController.placement(source: source, preview: true, heavyScenesOnMainDisplayOnly: false,
                                                      choice: .mainOnly, displays: both,
                                                      display: nil, mainDisplay: builtIn.id), source,
                       "the Settings preview always shows it")
    }

    func testUnpluggedPickFallsBackToTheMainDisplay() {
        let pick = MusicVideoDisplayChoice.display(uuid: external.uuid, name: external.name)
        XCTAssertTrue(shows(pick, heavy: true, on: builtIn, connected: [builtIn]))
        XCTAssertEqual(MusicVideoDisplays.target(uuid: external.uuid, connected: [builtIn], main: builtIn.id), builtIn.id)
        // Plugged back in under a new CGDirectDisplayID: found by its UUID.
        let replugged = MusicVideoDisplay(id: 9, uuid: external.uuid, name: external.name)
        XCTAssertTrue(shows(pick, heavy: true, on: replugged, connected: [builtIn, replugged]))
        XCTAssertFalse(shows(pick, heavy: true, on: builtIn, connected: [builtIn, replugged]))
        XCTAssertEqual(MusicVideoDisplays.caption(pick, heavyScenesOnMainDisplayOnly: true, connected: [builtIn],
                                                  fallback: "Line patterns"),
                       "Pro 27Q-10 isn’t connected, so the main display shows the video for now.")
    }

    func testChoiceIsRememberedByUUID() {
        let settings = VersoCustom.Settings(defaults: defaults, notifications: NotificationCenter())
        settings.musicVideoDisplays = .display(uuid: external.uuid, name: external.name)
        let reloaded = VersoCustom.Settings(defaults: defaults, notifications: NotificationCenter())
        guard case .display(let uuid, let name) = reloaded.musicVideoDisplays else { return XCTFail("kept") }
        XCTAssertEqual(uuid, external.uuid)
        XCTAssertEqual(name, "Pro 27Q-10")
        settings.musicVideoDisplays = .all
        XCTAssertEqual(VersoCustom.Settings(defaults: defaults, notifications: NotificationCenter()).musicVideoDisplays, .all)
        settings.musicVideoDisplays = .followHeavyScenes
        XCTAssertNil(defaults.object(forKey: MusicVideoDisplayChoice.key))
        XCTAssertEqual(MusicVideoDisplayChoice(stored: ["mode": "display", "uuid": ""]), .followHeavyScenes)
        XCTAssertEqual(MusicVideoDisplayChoice(stored: "nonsense"), .followHeavyScenes)
        XCTAssertEqual(MusicVideoDisplayChoice.display(uuid: "X", name: "Old name"),
                       .display(uuid: "X", name: "New name"), "the same display, renamed")
    }

    func testMenuListsConnectedDisplaysByName() {
        let options = MusicVideoDisplays.options(selected: .followHeavyScenes, connected: both)
        XCTAssertEqual(options.map { MusicVideoDisplays.title($0, heavyScenesOnMainDisplayOnly: true, connected: both) },
                       ["Like heavy scenes (main display)", "Main display only", "All displays",
                        "Built-in Retina Display", "Pro 27Q-10"])
        let unplugged = MusicVideoDisplayChoice.display(uuid: "CCCC", name: "Studio Display")
        let withPick = MusicVideoDisplays.options(selected: unplugged, connected: both)
        XCTAssertEqual(withPick.last, unplugged, "an unplugged pick stays in the menu")
        XCTAssertEqual(MusicVideoDisplays.title(unplugged, heavyScenesOnMainDisplayOnly: true, connected: both),
                       "Studio Display")
        XCTAssertNotNil(MusicVideoDisplays.caption(.followHeavyScenes, heavyScenesOnMainDisplayOnly: true,
                                                   connected: both, fallback: "Line patterns"))
        XCTAssertNil(MusicVideoDisplays.caption(.all, heavyScenesOnMainDisplayOnly: true, connected: [builtIn],
                                                fallback: "Line patterns"))
    }

    func testDisplayListFollowsTheScreenConfiguration() async throws {
        final class Box { var displays: [MusicVideoDisplay] = [] }
        let box = Box(), center = NotificationCenter()
        box.displays = [builtIn]
        let monitor = MainDisplayMonitor(center: center, list: { box.displays }, current: { 1 })
        XCTAssertEqual(monitor.displays, [builtIn])
        box.displays = both
        center.post(name: NSApplication.didChangeScreenParametersNotification, object: nil)
        try await Task.sleep(for: .milliseconds(50))
        XCTAssertEqual(monitor.displays, both)
    }

    func testRealDisplaysHaveStableIDs() {
        // Reads only this Mac's screen list; no prompts, nothing changed.
        for display in MusicVideoDisplays.connected() {
            XCTAssertEqual(MusicVideoDisplays.uuid(of: display.id), display.uuid)
            XCTAssertFalse(display.name.isEmpty)
        }
    }

    /// With several displays, each surface is muted by construction and
    /// steered to the same song time, so they show the same frame.
    func testEveryDisplayFollowsTheSameSongTime() {
        final class FakeSurface: MusicVideoPlaybackSurface {
            var time: Double, playing = false
            var seeks: [Double] = []
            init(time: Double) { self.time = time }
            var seekLead: Double { 0 }
            func sample(_ done: @escaping @MainActor (MusicVideoDrift.Sample?, Double?) -> Void) {
                done(.init(videoTime: time, videoPlaying: playing), 300)
            }
            func playVideo() { playing = true }
            func pauseVideo() { playing = false }
            func seekVideo(to seconds: Double) { seeks.append(seconds); time = seconds }
        }
        let songTime = 42.0
        let surfaces = [FakeSurface(time: 0), FakeSurface(time: 80)]
        let loops = surfaces.map { surface -> MusicVideoSyncLoop in
            let loop = MusicVideoSyncLoop(songTime: { songTime }, songPlaying: { true }, offset: { 3 }, uptime: { 10 })
            loop.surface = surface
            loop.tick()
            return loop
        }
        XCTAssertEqual(surfaces.map(\.seeks), [[45], [45]])
        XCTAssertTrue(surfaces.allSatisfy(\.playing))
        loops.forEach { $0.stop() }
    }
}
