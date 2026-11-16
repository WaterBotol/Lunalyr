import SwiftUI
import XCTest
@testable import VersoCustom

/// Both lyric styles at once (`LyricStyles`): the desktop and the floating
/// lyrics each read their own, whichever one Settings is editing. Throwaway
/// preferences only.
@MainActor
final class LyricStylesTests: XCTestCase {
    private struct Stores {
        let defaults: UserDefaults
        let settings: VersoCustom.Settings
        let colours: LyricColourOptions
        let highlights: LyricHighlightOptions
        let styles: LyricStyles
        let options: FloatingLyricsOptions
    }

    private func stores(prepare: (UserDefaults) -> Void = { _ in }) -> Stores {
        let defaults = LunalyrTestEnvironment.isolatedDefaults("LunalyrTests.Styles")
        prepare(defaults)
        let settings = VersoCustom.Settings(defaults: defaults, notifications: NotificationCenter())
        let colours = LyricColourOptions(defaults: defaults)
        let highlights = LyricHighlightOptions(defaults: defaults)
        let styles = LyricStyles(defaults: defaults, edited: LyricStyle(settings: settings, colours: colours,
                                                                       highlights: highlights, defaults: defaults),
                                 notifications: NotificationCenter())
        let options = FloatingLyricsOptions(defaults: defaults) {
            settings.reloadLyricStyle()
            colours.reload()
            highlights.reload()
            styles.followEditing()
        }
        return Stores(defaults: defaults, settings: settings, colours: colours, highlights: highlights,
                      styles: styles, options: options)
    }

    /// The edited monitor's Floating style as kept. Each monitor keeps its
    /// own now (`MonitorLookSlots`), no longer under `custom.lyricStyle.floating`.
    private func keptFloating(_ s: Stores) -> [String: Any]? {
        MonitorLookSlots(defaults: s.defaults).saved(LookSlot(display: s.styles.editingDisplay, surface: .floating))
    }

    func testTheFloatingStyleStartsFromTheDesktopsLessItsSize() {
        let s = stores { defaults in
            defaults.set("echo", forKey: "lyricComposition")
            defaults.set(1.4, forKey: "lyricScale")
        }
        let floating = s.styles.style(for: .floating)
        XCTAssertFalse(floating === s.styles.style(for: .desktop))
        XCTAssertTrue(s.styles.style(for: .desktop).settings === s.settings, "the desktop draws with the app's stores")
        XCTAssertEqual(floating.settings.lyricComposition, .echo)
        XCTAssertEqual(floating.settings.lyricScale, 1)
        XCTAssertNil(keptFloating(s), "not kept until floating lyrics are used")
    }

    func testTheFloatingStyleIsKeptWhenFloatingLyricsAreFirstUsed() {
        let s = stores { $0.set("echo", forKey: "lyricComposition") }
        // Months later, the desktop has moved on before anything floats.
        s.settings.lyricComposition = .torque
        s.styles.keepFloatingStyle()
        XCTAssertEqual(keptFloating(s)?["lyricComposition"] as? String, "torque",
                       "it starts from the desktop's look of the day floating lyrics are first used")
        XCTAssertEqual(s.styles.style(for: .floating).settings.lyricComposition, .torque)
        // From then on the two are apart.
        s.settings.lyricComposition = .hinge
        s.styles.keepFloatingStyle()
        XCTAssertEqual(s.styles.style(for: .floating).settings.lyricComposition, .torque)
    }

    func testBothSurfacesReadTheirOwnStyleAtOnce() {
        let s = stores()
        s.settings.lyricComposition = .portal
        s.settings.fontChoice = .slab
        s.colours.mode = .albumDrift
        s.options.surface = .floating
        s.settings.lyricComposition = .cascade
        s.settings.fontChoice = .serif
        s.colours.mode = .customGradient
        s.defaults.set(false, forKey: BackingVocals.settingKey)

        // Editing Floating: the floating lyrics read the live stores, the desktop the kept ones.
        let desktop = s.styles.style(for: .desktop), floating = s.styles.style(for: .floating)
        XCTAssertEqual(desktop.settings.lyricComposition, .portal)
        XCTAssertEqual(desktop.settings.fontChoice, .slab)
        XCTAssertEqual(desktop.colours.mode, .albumDrift)
        XCTAssertTrue(desktop.backingVocals)
        XCTAssertEqual(floating.settings.lyricComposition, .cascade)
        XCTAssertEqual(floating.settings.fontChoice, .serif)
        XCTAssertEqual(floating.colours.mode, .customGradient)
        XCTAssertFalse(floating.backingVocals)

        // Back to editing the desktop: the same two looks, the stores swapped.
        s.options.surface = .desktop
        XCTAssertEqual(s.styles.style(for: .desktop).settings.lyricComposition, .portal)
        XCTAssertEqual(s.styles.style(for: .desktop).settings.fontChoice, .slab)
        XCTAssertEqual(s.styles.style(for: .floating).settings.lyricComposition, .cascade)
        XCTAssertEqual(s.styles.style(for: .floating).settings.fontChoice, .serif)
        XCTAssertEqual(s.styles.style(for: .floating).colours.mode, .customGradient)
        XCTAssertFalse(s.styles.style(for: .floating).backingVocals)
        XCTAssertTrue(s.styles.style(for: .desktop).backingVocals)
    }

    func testEditingTheDesktopLeavesTheFloatingLyricsAlone() {
        let s = stores()
        s.options.surface = .floating
        s.settings.lyricComposition = .torque
        s.options.surface = .desktop
        s.settings.lyricComposition = .hinge
        s.settings.echoStrength = 0.3
        let floating = s.styles.style(for: .floating).settings
        XCTAssertEqual(floating.lyricComposition, .torque)
        XCTAssertEqual(floating.echoStrength, 1)
        XCTAssertEqual(s.styles.style(for: .desktop).settings.lyricComposition, .hinge)
    }

    func testTheKeptStyleNeverWritesThePreferences() {
        let s = stores()
        let parked = s.styles.parked
        parked.settings.lyricComposition = .mosaic
        parked.settings.lyricOffset = 3
        XCTAssertNil(s.defaults.object(forKey: "custom.lyricOffset"), "settings outside the style are not written")
        XCTAssertNotEqual(keptFloating(s)?["lyricComposition"] as? String,
                          "mosaic", "nor is the kept style")
        XCTAssertNil(s.defaults.object(forKey: "lyricComposition"))
    }

    func testSharedListsAndSwitchesPerStyle() {
        let s = stores()
        s.options.surface = .floating
        s.settings.actionWords = false
        s.options.surface = .desktop
        s.settings.actionWordList = ["jump": WordAction.allCases.first!.rawValue]
        let desktop = s.styles.style(for: .desktop).settings, floating = s.styles.style(for: .floating).settings
        XCTAssertEqual(floating.actionWordList, desktop.actionWordList, "the word lists are shared")
        XCTAssertNotNil(desktop.wordAccentLists.actions)
        XCTAssertNil(floating.wordAccentLists.actions, "the switch is the Floating style's own")
    }

    func testReadingsAndTranslationsPerStyle() {
        let s = stores()
        s.options.surface = .floating
        s.defaults.set(RomanizationOptions.Display.replace.rawValue, forKey: RomanizationOptions.displayKey)
        s.defaults.set(false, forKey: LunalyrExtras.Keys.translations)
        s.options.surface = .desktop
        let floating = s.styles.style(for: .floating), desktop = s.styles.style(for: .desktop)
        XCTAssertEqual(floating.readingDisplay, .replace)
        XCTAssertFalse(floating.showsTranslations)
        XCTAssertEqual(desktop.readingDisplay, .below)
        XCTAssertTrue(desktop.showsTranslations)
    }

    func testTheEnvironmentPicksTheStyle() {
        var environment = EnvironmentValues()
        XCTAssertNil(environment.lyricSurface, "unset: the style being edited (lyric cards, Settings' samples)")
        environment.lyricSurface = .floating
        XCTAssertEqual(environment.lyricSurface, .floating)
        XCTAssertNil(environment.lyricStyles)
        let s = stores()
        XCTAssertTrue(s.styles.style(for: nil) === s.styles.edited)
        s.options.surface = .floating
        XCTAssertTrue(s.styles.style(for: nil) === s.styles.style(for: .floating))
    }
}
