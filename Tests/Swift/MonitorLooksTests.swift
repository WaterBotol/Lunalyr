import SwiftUI
import XCTest
@testable import VersoCustom

/// Each monitor's look (`MonitorLookSlots`, `LyricStyles`): every display
/// keeps its own wallpaper and Desktop and Floating styles, Settings edits
/// one at a time in the app's stores, and the others draw from read-only
/// copies. Throwaway preferences and made-up displays only.
@MainActor
final class MonitorLooksTests: XCTestCase {
    private static let a = "DISPLAY-A", b = "DISPLAY-B", c = "DISPLAY-C"

    private final class Displays {
        var connected: [String]
        init(_ connected: [String]) { self.connected = connected }
    }

    private struct Stores {
        let defaults: UserDefaults
        let settings: VersoCustom.Settings
        let colours: LyricColourOptions
        let highlights: LyricHighlightOptions
        let visualizer: VisualizerPreferences
        let desktop: DesktopOptions
        let styles: LyricStyles
        let options: FloatingLyricsOptions
        let displays: Displays
        var slots: MonitorLookSlots { MonitorLookSlots(defaults: defaults) }
    }

    private func stores(_ connected: [String] = [a, b], shows: @escaping (String) -> DisplayLyrics = { _ in .desktop },
                        prepare: (UserDefaults) -> Void = { _ in }) -> Stores {
        let defaults = LunalyrTestEnvironment.isolatedDefaults("LunalyrTests.MonitorLooks")
        prepare(defaults)
        let displays = Displays(connected)
        let settings = VersoCustom.Settings(defaults: defaults, notifications: NotificationCenter())
        let colours = LyricColourOptions(defaults: defaults)
        let highlights = LyricHighlightOptions(defaults: defaults)
        let visualizer = VisualizerPreferences(defaults: defaults)
        let desktop = DesktopOptions(defaults: defaults)
        let edited = LyricStyle(settings: settings, colours: colours, highlights: highlights, defaults: defaults,
                                visualizer: visualizer, desktop: desktop)
        let styles = LyricStyles(defaults: defaults, edited: edited, notifications: NotificationCenter(),
                                 connected: { displays.connected }, shows: shows)
        let options = FloatingLyricsOptions(defaults: defaults, mainDisplay: { displays.connected.first }) {
            edited.reload()
            styles.followEditing()
        }
        return Stores(defaults: defaults, settings: settings, colours: colours, highlights: highlights,
                      visualizer: visualizer, desktop: desktop, styles: styles, options: options, displays: displays)
    }

    // MARK: Keys

    func testSlotsAreKeptPerDisplayAndSurface() {
        XCTAssertEqual(MonitorLookSlots.storeKey(LookSlot(display: "UUID-1", surface: .desktop)), "custom.look.UUID-1.desktop")
        XCTAssertEqual(MonitorLookSlots.storeKey(LookSlot(display: "UUID-1", surface: .floating)), "custom.look.UUID-1.floating")
        XCTAssertEqual(Set(MonitorLookKeys.keys(.floating)), Set(MonitorLookKeys.style), "a Floating slot holds a style")
        XCTAssertEqual(Set(MonitorLookKeys.keys(.desktop)), Set(MonitorLookKeys.all), "a Desktop slot holds the wallpaper too")
        XCTAssertTrue(Set(MonitorLookKeys.style).isDisjoint(with: MonitorLookKeys.wallpaper))
        XCTAssertEqual(Set(MonitorLookKeys.all).count, MonitorLookKeys.all.count, "no key twice")
        XCTAssertTrue(MonitorLookKeys.wallpaper.contains("backgroundMode"))
        XCTAssertTrue(MonitorLookKeys.style.contains("lyricComposition"))
        XCTAssertTrue(MonitorLookKeys.style.contains(LookStore.Keys.current), "each style remembers its own Look")
    }

    /// Everything on the Look pages is either kept per monitor or shared
    /// on purpose, with the reason.
    func testEveryLookPageSettingIsPerMonitorOrSharedForAReason() {
        let lookPages: Set<SettingsPage> = [.wallpaper, .typography, .animation, .covers]
        let keys = Set(SettingsCatalogue.all.filter { lookPages.contains($0.page) }.flatMap(\.keys))
        let perMonitor = Set(MonitorLookKeys.all), shared = Set(MonitorLookKeys.shared.keys)
        XCTAssertTrue(perMonitor.isDisjoint(with: shared))
        XCTAssertEqual(keys.subtracting(perMonitor).subtracting(shared).sorted(), [],
                       "on a Look page but neither per monitor nor shared")
        for (key, reason) in MonitorLookKeys.shared { XCTAssertFalse(reason.isEmpty, key) }
    }

    // MARK: Migration

    func testEveryConnectedDisplayStartsWithTodaysLook() {
        let s = stores([Self.a, Self.b]) { defaults in
            defaults.set("echo", forKey: "lyricComposition")
            defaults.set("serif", forKey: "fontChoice")
            defaults.set("visualizer", forKey: "backgroundMode")
            defaults.set(0.4, forKey: "vividness")
            // Today's Floating style, kept while the desktop was edited.
            defaults.set(["lyricComposition": "cascade", "lyricScale": 1.0], forKey: LyricStyleProfiles.storeKey(.floating))
        }
        XCTAssertEqual(s.styles.editingDisplay, Self.a, "Settings edits the main display")
        XCTAssertEqual(s.defaults.string(forKey: MonitorLookSlots.editingKey), Self.a)
        for display in [Self.a, Self.b] {
            let desktop = s.styles.style(for: .desktop, display: display).settings
            XCTAssertEqual(desktop.lyricComposition, .echo, display)
            XCTAssertEqual(desktop.fontChoice, .serif, display)
            let wallpaper = s.styles.wallpaperStyle(for: display).settings
            XCTAssertEqual(wallpaper.backgroundMode, .visualizer, display)
            XCTAssertEqual(wallpaper.vividness, 0.4, display)
            XCTAssertEqual(s.styles.style(for: .floating, display: display).settings.lyricComposition, .cascade, display)
        }
        let keptB = s.slots.saved(LookSlot(display: Self.b, surface: .desktop))
        XCTAssertEqual(keptB?["lyricComposition"] as? String, "echo")
        XCTAssertEqual(keptB?["backgroundMode"] as? String, "visualizer")
        XCTAssertNil(s.slots.saved(LookSlot(display: Self.a, surface: .desktop)), "the edited slot stays in the app's stores")
        XCTAssertNotNil(s.defaults.dictionary(forKey: LyricStyleProfiles.storeKey(.floating)),
                        "today's styles stay readable for a release")
    }

    func testMigratingWhileFloatingIsEditedKeepsTheLiveFloatingStyle() {
        let s = stores([Self.a, Self.b]) { defaults in
            // Editing Floating: the desktop's style is the kept one, the live keys are Floating's.
            defaults.set(LyricsSurface.floating.rawValue, forKey: FloatingLyricsOptions.Keys.surface)
            defaults.set("torque", forKey: "lyricComposition")
            defaults.set(["lyricComposition": "portal"], forKey: LyricStyleProfiles.storeKey(.desktop))
            defaults.set(["lyricComposition": "stale"], forKey: LyricStyleProfiles.storeKey(.floating))
        }
        XCTAssertEqual(s.styles.style(for: .desktop, display: Self.a).settings.lyricComposition, .portal)
        XCTAssertEqual(s.styles.style(for: .floating, display: Self.a).settings.lyricComposition, .torque)
        XCTAssertEqual(s.styles.style(for: .desktop, display: Self.b).settings.lyricComposition, .portal)
        XCTAssertEqual(s.styles.style(for: .floating, display: Self.b).settings.lyricComposition, .torque,
                       "copied from the live Floating style, not the stale kept one")
    }

    func testADisplayConnectedLaterStartsAsTheMainDisplaysLook() {
        let s = stores([Self.a])
        s.settings.lyricComposition = .hinge
        s.settings.backgroundMode = .pattern
        s.displays.connected = [Self.a, Self.c]
        s.styles.displaysChanged()
        XCTAssertEqual(s.styles.style(for: .desktop, display: Self.c).settings.lyricComposition, .hinge)
        XCTAssertEqual(s.styles.wallpaperStyle(for: Self.c).settings.backgroundMode, .pattern)
        // A copy: changing the main display afterwards leaves it alone.
        s.settings.lyricComposition = .portal
        s.styles.reloadParked()
        XCTAssertEqual(s.styles.style(for: .desktop, display: Self.c).settings.lyricComposition, .hinge)
    }

    func testUnpluggingTheEditedMonitorEditsTheMainOneAndKeepsItsLook() {
        let s = stores([Self.a, Self.b])
        s.styles.edit(display: Self.b)
        s.settings.lyricComposition = .mosaic
        s.displays.connected = [Self.a]
        s.styles.displaysChanged()
        XCTAssertEqual(s.styles.editingDisplay, Self.a)
        XCTAssertEqual(s.slots.saved(LookSlot(display: Self.b, surface: .desktop))?["lyricComposition"] as? String, "mosaic")
    }

    // MARK: Drawing

    func testTwoDisplaysDrawDifferentLooksAtOnce() {
        let s = stores([Self.a, Self.b])
        s.settings.lyricComposition = .echo
        s.settings.fontChoice = .slab
        s.settings.backgroundMode = .visualizer
        s.styles.edit(display: Self.b)
        s.settings.lyricComposition = .cascade
        s.settings.fontChoice = .serif
        s.settings.backgroundMode = .pattern

        let onA = s.styles.style(for: .desktop, display: Self.a), onB = s.styles.style(for: .desktop, display: Self.b)
        XCTAssertTrue(onB === s.styles.edited, "the edited monitor draws with the app's stores")
        XCTAssertFalse(onA === onB)
        XCTAssertEqual(onA.settings.lyricComposition, .echo)
        XCTAssertEqual(onA.settings.fontChoice, .slab)
        XCTAssertEqual(s.styles.wallpaperStyle(for: Self.a).settings.backgroundMode, .visualizer)
        XCTAssertEqual(onB.settings.lyricComposition, .cascade)
        XCTAssertEqual(onB.settings.fontChoice, .serif)
        XCTAssertEqual(s.styles.wallpaperStyle(for: Self.b).settings.backgroundMode, .pattern)
        XCTAssertTrue(s.styles.anyWallpaper { $0.backgroundMode == .visualizer })
        XCTAssertTrue(s.styles.anyWallpaper { $0.backgroundMode == .pattern })
        XCTAssertFalse(s.styles.anyWallpaper { $0.backgroundMode == .musicVideo })
    }

    func testTheWallpaperFollowsTheEditedMonitorWhicheverStyleIsEdited() {
        let s = stores([Self.a, Self.b])
        s.options.surface = .floating
        s.settings.backgroundMode = .colourOnly
        XCTAssertTrue(s.styles.wallpaperStyle(for: Self.a) === s.styles.edited, "Monitor A's wallpaper is being edited")
        XCTAssertEqual(s.styles.wallpaperStyle(for: Self.a).settings.backgroundMode, .colourOnly)
        XCTAssertNotEqual(s.styles.wallpaperStyle(for: Self.b).settings.backgroundMode, .colourOnly)
    }

    func testACopyFollowsTheAppsOwnSettings() {
        let s = stores([Self.a, Self.b])
        let copy = s.styles.style(for: .desktop, display: Self.b).settings
        XCTAssertFalse(copy === s.settings)
        s.settings.lowPowerVisuals = true
        s.settings.animationRate = .smooth
        let followed = expectation(description: "followed")
        DispatchQueue.main.async { followed.fulfill() }
        wait(for: [followed], timeout: 1)
        XCTAssertTrue(copy.lowPowerVisuals, "power settings are the app's")
        XCTAssertEqual(copy.animationRate, .smooth)
        XCTAssertNil(s.slots.saved(LookSlot(display: Self.b, surface: .desktop))?["custom.lowPowerVisuals"])
    }

    func testEachDistinctWayOfShowingReadingsIsWorkedOutOnce() {
        XCTAssertEqual(LyricsViewModel.otherReadingDisplays([.below, .below, .off], base: .below), [])
        XCTAssertEqual(LyricsViewModel.otherReadingDisplays([.below, .replace, .replace], base: .below), [.replace])
        XCTAssertEqual(LyricsViewModel.otherReadingDisplays([.below, .replace], base: .off), [.below, .replace])
    }

    func testLiveStylesAreEachDisplaysAsItShowsThem() {
        let s = stores([Self.a, Self.b], shows: { $0 == Self.b ? .floating : .desktop })
        let live = s.styles.liveStyles
        XCTAssertTrue(live.contains { $0 === s.styles.edited })
        XCTAssertTrue(live.contains { $0 === s.styles.style(for: .floating, display: Self.b) })
        XCTAssertFalse(live.contains { $0 === s.styles.style(for: .desktop, display: Self.b) },
                       "B shows no desktop lyrics, so its Desktop style draws no lyrics")
    }

    // MARK: Editing

    func testSwitchingMonitorsKeepsTheOneLeftAndLoadsThePickedOne() {
        let s = stores([Self.a, Self.b])
        s.settings.lyricComposition = .echo
        s.settings.backgroundMode = .musicVideo
        s.settings.musicVideoFallback = .pattern
        s.settings.vividness = 0.6
        s.colours.mode = .albumDrift
        s.visualizer.wavefieldColours = .original
        s.desktop.textBacking = 0.8

        s.styles.edit(display: Self.b)
        XCTAssertEqual(s.styles.editingDisplay, Self.b)
        XCTAssertEqual(s.defaults.string(forKey: MonitorLookSlots.editingKey), Self.b)
        let keptA = s.slots.saved(LookSlot(display: Self.a, surface: .desktop))
        XCTAssertEqual(keptA?["lyricComposition"] as? String, "echo")
        XCTAssertEqual(keptA?["backgroundMode"] as? String, "musicVideo")
        // B started as a copy of A's look before it was edited.
        s.settings.lyricComposition = .torque
        s.settings.backgroundMode = .shader
        s.settings.vividness = 1.2
        s.colours.mode = .customGradient
        s.visualizer.wavefieldColours = .album
        s.desktop.textBacking = 0.1

        s.styles.edit(display: Self.a)
        XCTAssertEqual(s.settings.lyricComposition, .echo)
        XCTAssertEqual(s.settings.backgroundMode, .musicVideo)
        XCTAssertEqual(s.settings.musicVideoFallback, .pattern, "not overwritten by the type it left")
        XCTAssertEqual(s.settings.vividness, 0.6)
        XCTAssertEqual(s.colours.mode, .albumDrift)
        XCTAssertEqual(s.visualizer.wavefieldColours, .original)
        XCTAssertEqual(s.desktop.textBacking, 0.8)
        XCTAssertEqual(s.styles.style(for: .desktop, display: Self.b).settings.lyricComposition, .torque)
        XCTAssertEqual(s.styles.wallpaperStyle(for: Self.b).settings.backgroundMode, .shader)

        // The app's stores read exactly what fresh ones would.
        let fresh = VersoCustom.Settings(defaults: s.defaults, notifications: NotificationCenter())
        XCTAssertEqual(fresh.backgroundMode, s.settings.backgroundMode)
        XCTAssertEqual(fresh.musicVideoFallback, s.settings.musicVideoFallback)
        XCTAssertEqual(fresh.lyricComposition, s.settings.lyricComposition)
        XCTAssertEqual(fresh.vividness, s.settings.vividness)
        XCTAssertEqual(fresh.fontChoice, s.settings.fontChoice)
    }

    func testSwitchingStylesStaysOnTheEditedMonitor() {
        let s = stores([Self.a, Self.b])
        s.styles.edit(display: Self.b)
        s.options.surface = .floating
        s.settings.lyricComposition = .portal
        XCTAssertEqual(s.styles.editingSlot, LookSlot(display: Self.b, surface: .floating))
        s.options.surface = .desktop
        XCTAssertEqual(s.slots.saved(LookSlot(display: Self.b, surface: .floating))?["lyricComposition"] as? String, "portal")
        XCTAssertNil(s.slots.saved(LookSlot(display: Self.a, surface: .floating)), "A's floating style was never used")
        XCTAssertNotEqual(s.styles.style(for: .floating, display: Self.a).settings.lyricComposition, .portal)
    }

    func testCopyToAllMonitors() {
        let s = stores([Self.a, Self.b, Self.c])
        s.settings.lyricComposition = .mosaic
        s.settings.backgroundMode = .colourOnly
        s.options.surface = .floating
        s.settings.lyricComposition = .hinge
        s.styles.copyToAllMonitors()
        for display in [Self.b, Self.c] {
            XCTAssertEqual(s.styles.style(for: .desktop, display: display).settings.lyricComposition, .mosaic, display)
            XCTAssertEqual(s.styles.style(for: .floating, display: display).settings.lyricComposition, .hinge, display)
            XCTAssertEqual(s.styles.wallpaperStyle(for: display).settings.backgroundMode, .colourOnly, display)
        }
    }

    func testSameOnEveryMonitorDrawsTheEditedLookEverywhere() {
        let s = stores([Self.a, Self.b])
        s.styles.edit(display: Self.b)
        s.settings.lyricComposition = .portal
        s.styles.edit(display: Self.a)
        s.settings.lyricComposition = .echo
        s.styles.setSameEverywhere(true)
        XCTAssertTrue(s.styles.style(for: .desktop, display: Self.b) === s.styles.edited, "one set of stores, no copies")
        XCTAssertTrue(s.styles.wallpaperStyle(for: Self.b) === s.styles.edited)
        s.styles.setSameEverywhere(false)
        XCTAssertEqual(s.styles.style(for: .desktop, display: Self.b).settings.lyricComposition, .echo,
                       "turned off, each monitor keeps the look it showed")
    }

    // MARK: Looks

    func testALookAppliesToThePickedMonitorOnly() throws {
        let s = stores([Self.a, Self.b])
        s.settings.backgroundMode = .pattern
        s.settings.lyricComposition = .echo
        let looks = LookStore(defaults: s.defaults, settings: s.settings, highlights: s.highlights)
        s.styles.edit(display: Self.b)
        looks.reloadCurrent()
        let look = try XCTUnwrap(Look.builtIns.first { $0.values.wallpaper != .pattern && $0.values.layout != .echo })
        looks.apply(look)
        XCTAssertEqual(s.settings.backgroundMode, look.values.wallpaper)
        XCTAssertEqual(looks.current, look.id)
        XCTAssertEqual(s.styles.wallpaperStyle(for: Self.a).settings.backgroundMode, .pattern, "monitor A is left alone")
        XCTAssertEqual(s.styles.style(for: .desktop, display: Self.a).settings.lyricComposition, .echo)
        // Each monitor remembers its own Look.
        s.styles.edit(display: Self.a)
        looks.reloadCurrent()
        XCTAssertNil(looks.current)
        s.styles.edit(display: Self.b)
        looks.reloadCurrent()
        XCTAssertEqual(looks.current, look.id)
    }

    // MARK: Names

    func testMonitorsAreNamedAndTwinsNumbered() {
        let names = FloatingDisplayChoice.named([("1", "Pro 27Q-10"), ("2", "Built-in Retina Display"), ("3", "Pro 27Q-10")])
            .map(\.name)
        XCTAssertEqual(names, ["Pro 27Q-10 1", "Built-in Retina Display", "Pro 27Q-10 2"])
    }

    func testSystemAudioListensForWhicheverMonitorShowsAVisualiser() {
        let defaults = LunalyrTestEnvironment.isolatedDefaults("LunalyrTests.MonitorLooks.Audio")
        let pattern = VersoCustom.Settings(defaults: defaults, notifications: NotificationCenter())
        pattern.backgroundMode = .pattern
        let other = LunalyrTestEnvironment.isolatedDefaults("LunalyrTests.MonitorLooks.Audio2")
        let visualiser = VersoCustom.Settings(defaults: other, notifications: NotificationCenter())
        visualiser.backgroundMode = .visualizer
        XCTAssertTrue(AudioCapturePreferences.listening(among: [pattern, visualiser]) === visualiser)
        XCTAssertNil(AudioCapturePreferences.listening(among: [pattern]))
        XCTAssertTrue(AudioCapturePreferences.current(pattern, wallpapers: [pattern, visualiser]).visualiserChosen)
    }
}
