import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// Floating lyrics: the surface choice, the two separate lyric styles, the
/// window's frame maths and when it shows. Everything here uses throwaway
/// preferences and opens no window.
@MainActor
final class FloatingLyricsTests: XCTestCase {
    private struct Stores {
        let defaults: UserDefaults
        let settings: VersoCustom.Settings
        let colours: LyricColourOptions
        let highlights: LyricHighlightOptions
        let options: FloatingLyricsOptions
    }

    private func stores() -> Stores {
        let defaults = LunalyrTestEnvironment.isolatedDefaults("LunalyrTests.Floating")
        let settings = VersoCustom.Settings(defaults: defaults, notifications: NotificationCenter())
        let colours = LyricColourOptions(defaults: defaults)
        let highlights = LyricHighlightOptions(defaults: defaults)
        let options = FloatingLyricsOptions(defaults: defaults) {
            settings.reloadLyricStyle()
            colours.reload()
            highlights.reload()
        }
        return Stores(defaults: defaults, settings: settings, colours: colours, highlights: highlights, options: options)
    }

    // MARK: Settings

    func testDefaultsLeaveTheDesktopAsItWas() {
        let options = stores().options
        XCTAssertEqual(options.surface, .desktop, "off by default: existing users see no change")
        XCTAssertFalse(options.isFloating)
        XCTAssertEqual(options.textSize, FloatingLyricsOptions.SizePreset.medium.rawValue)
        XCTAssertEqual(options.placement, .bottom)
        XCTAssertTrue(options.fadesUnderPointer)
        XCTAssertEqual(options.display, "")
    }

    func testStoredValuesAreReadBackAndClamped() {
        let defaults = LunalyrTestEnvironment.isolatedDefaults("LunalyrTests.Floating")
        defaults.set("floating", forKey: FloatingLyricsOptions.Keys.surface)
        defaults.set(999.0, forKey: FloatingLyricsOptions.Keys.textSize)
        defaults.set(-3.0, forKey: FloatingLyricsOptions.Keys.backdrop)
        defaults.set("topRight", forKey: FloatingLyricsOptions.Keys.placement)
        defaults.set("nonsense", forKey: FloatingLyricsOptions.Keys.display)
        let options = FloatingLyricsOptions(defaults: defaults)
        XCTAssertEqual(options.surface, .floating)
        XCTAssertEqual(options.textSize, FloatingLyricsOptions.textSizeRange.upperBound)
        XCTAssertEqual(options.backdrop, 0)
        XCTAssertEqual(options.placement, .topRight)
        XCTAssertEqual(FloatingLyricsOptions.clampedTextSize(.nan), FloatingLyricsOptions.defaultTextSize)
        defaults.set("sideways", forKey: FloatingLyricsOptions.Keys.placement)
        XCTAssertEqual(FloatingLyricsOptions(defaults: defaults).placement, .bottom)
    }

    func testCustomFramesAreKeptPerDisplayAndResetClears() {
        let options = stores().options
        options.storeCustomFrame(CGRect(x: 40, y: 60, width: 500, height: 160), for: "A")
        XCTAssertEqual(options.placement(for: "A"), .custom, "dragging the window makes its display Custom")
        XCTAssertEqual(options.placement(for: "B"), .bottom, "another display keeps its place")
        XCTAssertEqual(options.placement, .bottom)
        XCTAssertEqual(options.customFrame(for: "A"), CGRect(x: 40, y: 60, width: 500, height: 160))
        XCTAssertEqual(options.savedSize(for: "A"), CGSize(width: 500, height: 160))
        XCTAssertNil(options.customFrame(for: "B"), "another display keeps its own")
        options.resetFrames()
        XCTAssertNil(options.customFrame(for: "A"))
        XCTAssertNil(options.savedSize(for: "A"))
        XCTAssertEqual(options.placement(for: "A"), .bottom)
    }

    func testPickingAPlaceAppliesToEveryDisplay() {
        let options = stores().options
        options.storeCustomFrame(CGRect(x: 40, y: 60, width: 500, height: 160), for: "A")
        options.placement = .topRight
        XCTAssertEqual(options.placement(for: "A"), .topRight)
        XCTAssertEqual(options.placement(for: "B"), .topRight)
        XCTAssertEqual(options.customFrame(for: "A"), CGRect(x: 40, y: 60, width: 500, height: 160),
                       "the frame placed by hand is kept for Custom")
    }

    // MARK: What each display shows

    func testEachDisplayDrawsWhatItsChoiceSays() {
        XCTAssertTrue(NowPlayingView.drawsLyrics(settingsPreview: false, choice: .desktop))
        XCTAssertTrue(NowPlayingView.drawsLyrics(settingsPreview: false, choice: .both))
        XCTAssertFalse(NowPlayingView.drawsLyrics(settingsPreview: false, choice: .floating),
                       "the wallpaper draws no lyrics where they float")
        XCTAssertFalse(NowPlayingView.drawsLyrics(settingsPreview: false, choice: .none))
        XCTAssertTrue(NowPlayingView.drawsLyrics(settingsPreview: true, choice: .none),
                      "the Settings preview shows the style being edited")
        XCTAssertEqual(DisplayLyrics.allCases.filter(\.onDesktop), [.desktop, .both])
        XCTAssertEqual(DisplayLyrics.allCases.filter(\.floats), [.floating, .both])
        var input = playing
        input.floats = false
        XCTAssertFalse(FloatingLyricsVisibility.shows(input), "no floating window on a display that does not float")
        input.floats = true
        XCTAssertTrue(FloatingLyricsVisibility.shows(input))
    }

    func testChoicesAreStoredPerDisplayAndNewDisplaysGetDesktopLyrics() {
        let s = stores()
        XCTAssertEqual(s.options.choice(for: "A"), .desktop, "a display never seen shows the lyrics on its desktop")
        s.options.setChoice(.both, for: "A")
        s.options.setChoice(.none, for: "B")
        XCTAssertEqual(s.options.choice(for: "A"), .both)
        XCTAssertEqual(s.options.choice(for: "B"), .none)
        XCTAssertEqual(s.options.choice(for: "C"), .desktop)
        XCTAssertEqual(s.defaults.dictionary(forKey: FloatingLyricsOptions.Keys.displays) as? [String: String],
                       ["A": "both", "B": "none"])
        let reread = FloatingLyricsOptions(defaults: s.defaults, connected: ["A", "B", "C"])
        XCTAssertEqual(reread.choice(for: "A"), .both)
        XCTAssertEqual(reread.choice(for: "B"), .none)
        XCTAssertEqual(reread.choice(for: "C"), .desktop, "a stored choice is never migrated again")
    }

    func testWhichDisplaysGetAFloatingWindow() {
        let s = stores()
        let connected = ["A", "B", "C", "D"]
        XCTAssertEqual(s.options.floatingDisplays(among: connected), [], "nothing floats by default")
        s.options.setChoice(.floating, for: "A")
        s.options.setChoice(.both, for: "C")
        s.options.setChoice(.none, for: "D")
        s.options.setChoice(.floating, for: "gone")
        XCTAssertEqual(s.options.floatingDisplays(among: connected), ["A", "C"], "one window per floating display")
        XCTAssertTrue(s.options.floats(among: connected))
        XCTAssertFalse(s.options.floats(among: ["B", "D"]))
    }

    func testMigrationFromTheDesktopKeepsEveryDisplaysLyrics() {
        let defaults = LunalyrTestEnvironment.isolatedDefaults("LunalyrTests.Floating")
        defaults.set("desktop", forKey: FloatingLyricsOptions.Keys.surface)
        let options = FloatingLyricsOptions(defaults: defaults, connected: ["main", "side"])
        XCTAssertEqual(options.choice(for: "main"), .desktop)
        XCTAssertEqual(options.choice(for: "side"), .desktop)
        XCTAssertEqual(options.floatingDisplays(among: ["main", "side"]), [])
        // Never set at all (a fresh install) reads the same.
        let fresh = FloatingLyricsOptions(defaults: LunalyrTestEnvironment.isolatedDefaults("LunalyrTests.Floating"),
                                          connected: ["main", "side"])
        XCTAssertEqual(fresh.floatingDisplays(among: ["main", "side"]), [])
        XCTAssertEqual(fresh.choice(for: "side"), .desktop)
    }

    func testMigrationFromFloatingOnAChosenDisplay() {
        let defaults = LunalyrTestEnvironment.isolatedDefaults("LunalyrTests.Floating")
        defaults.set("floating", forKey: FloatingLyricsOptions.Keys.surface)
        defaults.set("side", forKey: FloatingLyricsOptions.Keys.display)
        let options = FloatingLyricsOptions(defaults: defaults, connected: ["main", "side", "third"])
        XCTAssertEqual(options.choice(for: "side"), .floating, "the window stays on the display it was on")
        XCTAssertEqual(options.choice(for: "main"), .none, "Floating left every wallpaper without lyrics")
        XCTAssertEqual(options.choice(for: "third"), .none)
        XCTAssertEqual(options.choice(for: "plugged-in-later"), .desktop)
        XCTAssertEqual(options.surface, .floating, "and Settings still edits the Floating style")
    }

    func testMigrationFromFloatingWithNoDisplayChosen() {
        for chosen in ["", "disconnected"] {
            let defaults = LunalyrTestEnvironment.isolatedDefaults("LunalyrTests.Floating")
            defaults.set("floating", forKey: FloatingLyricsOptions.Keys.surface)
            defaults.set(chosen, forKey: FloatingLyricsOptions.Keys.display)
            let options = FloatingLyricsOptions(defaults: defaults, connected: ["main", "side"])
            XCTAssertEqual(options.choice(for: "main"), .floating, "the main display, where the window was: \(chosen)")
            XCTAssertEqual(options.choice(for: "side"), .none)
        }
    }

    func testMigrationWaitsForADisplay() {
        let defaults = LunalyrTestEnvironment.isolatedDefaults("LunalyrTests.Floating")
        defaults.set("floating", forKey: FloatingLyricsOptions.Keys.surface)
        let options = FloatingLyricsOptions(defaults: defaults, connected: [])
        XCTAssertNil(defaults.object(forKey: FloatingLyricsOptions.Keys.displays), "nothing to migrate yet")
        options.migrateDisplaysIfNeeded(connected: ["main"])
        XCTAssertEqual(options.choice(for: "main"), .floating)
        options.setChoice(.desktop, for: "main")
        options.migrateDisplaysIfNeeded(connected: ["main"])
        XCTAssertEqual(options.choice(for: "main"), .desktop, "only once")
    }

    func testFloatingQuickToggle() {
        let s = stores()
        let connected = ["main", "side"]
        s.options.setChoice(.none, for: "side")
        s.options.toggleFloating(connected: connected, target: "side")
        XCTAssertEqual(s.options.choice(for: "side"), .floating, "on for the display with the menu bar")
        XCTAssertEqual(s.options.choice(for: "main"), .desktop, "the others keep their choice")
        s.options.setChoice(.both, for: "main")
        s.options.toggleFloating(connected: connected, target: "side")
        XCTAssertEqual(s.options.choice(for: "main"), .desktop, "off everywhere: back on the desktop")
        XCTAssertEqual(s.options.choice(for: "side"), .desktop)
        s.options.toggleFloating(connected: connected, target: nil)
        XCTAssertEqual(s.options.choice(for: "main"), .floating, "no target: the main display")
    }

    func testSwitchingTheEditedStyleIsStoredAndToggles() {
        let s = stores()
        s.options.toggleSurface()
        XCTAssertEqual(s.defaults.string(forKey: FloatingLyricsOptions.Keys.surface), "floating")
        XCTAssertEqual(FloatingLyricsOptions(defaults: s.defaults).surface, .floating)
        s.options.toggleSurface()
        XCTAssertEqual(s.options.surface, .desktop)
        XCTAssertEqual(s.options.floatingDisplays(among: ["A"]), [], "editing a style never moves the lyrics")
    }

    // MARK: Two separate styles

    func testFloatingStyleIsSeededOnceFromTheDesktop() {
        let s = stores()
        s.settings.lyricComposition = .echo
        s.settings.lyricScale = 1.3
        s.settings.fontChoice = .serif
        s.colours.mode = .sungSweep
        s.highlights.mode = .album
        s.options.surface = .floating
        XCTAssertEqual(s.settings.lyricComposition, .echo, "Floating starts from the desktop's style")
        XCTAssertEqual(s.settings.fontChoice, .serif)
        XCTAssertEqual(s.colours.mode, .sungSweep)
        XCTAssertEqual(s.highlights.mode, .album)
        XCTAssertEqual(s.settings.lyricScale, 1, "Floating's size is its own text size")

        // Change the desktop's style: the floating one, already seeded, keeps its own.
        s.options.surface = .desktop
        s.settings.lyricComposition = .portal
        s.colours.mode = .standard
        s.options.surface = .floating
        XCTAssertEqual(s.settings.lyricComposition, .echo, "seeding happens once")
        XCTAssertEqual(s.colours.mode, .sungSweep)
    }

    func testChangingFloatingNeverChangesTheDesktop() {
        let s = stores()
        s.settings.lyricComposition = .echo
        s.settings.lyricScale = 1.2
        s.settings.showInterludes = true
        s.colours.mode = .albumDrift
        s.options.surface = .floating
        s.settings.lyricComposition = .scattered
        s.settings.lyricScale = 0.8
        s.settings.showInterludes = false
        s.settings.wordArrivalByStyle = ["scattered": WordArrival.pop.rawValue]
        s.colours.mode = .customGradient
        s.highlights.mode = .custom
        s.defaults.set(false, forKey: BackingVocals.settingKey)

        s.options.surface = .desktop
        XCTAssertEqual(s.settings.lyricComposition, .echo)
        XCTAssertEqual(s.settings.lyricScale, 1.2)
        XCTAssertTrue(s.settings.showInterludes)
        XCTAssertEqual(s.settings.wordArrivalByStyle, [:])
        XCTAssertEqual(s.colours.mode, .albumDrift)
        XCTAssertEqual(s.highlights.mode, .complement)
        XCTAssertNil(s.defaults.object(forKey: BackingVocals.settingKey), "a switch the desktop never set stays unset")

        // And the floating style is still there when it comes back.
        s.options.surface = .floating
        XCTAssertEqual(s.settings.lyricComposition, .scattered)
        XCTAssertEqual(s.settings.lyricScale, 0.8)
        XCTAssertFalse(s.settings.showInterludes)
        XCTAssertEqual(s.colours.mode, .customGradient)
        XCTAssertEqual(s.highlights.mode, .custom)
        XCTAssertEqual(s.defaults.object(forKey: BackingVocals.settingKey) as? Bool, false)
    }

    func testChangingTheDesktopNeverChangesFloating() {
        let s = stores()
        s.options.surface = .floating
        s.settings.lyricComposition = .cascade
        s.options.surface = .desktop
        s.settings.lyricComposition = .hinge
        s.settings.echoStrength = 0.4
        s.options.surface = .floating
        XCTAssertEqual(s.settings.lyricComposition, .cascade)
        XCTAssertEqual(s.settings.echoStrength, 1.0)
        s.options.surface = .desktop
        XCTAssertEqual(s.settings.lyricComposition, .hinge)
        XCTAssertEqual(s.settings.echoStrength, 0.4)
    }

    func testStylesSurviveARelaunch() {
        let s = stores()
        s.settings.lyricComposition = .mosaic
        s.options.surface = .floating
        s.settings.lyricComposition = .torque
        // A relaunch reads the style in use from the ordinary keys.
        let settings = VersoCustom.Settings(defaults: s.defaults, notifications: NotificationCenter())
        XCTAssertEqual(settings.lyricComposition, .torque)
        let options = FloatingLyricsOptions(defaults: s.defaults) { settings.reloadLyricStyle() }
        XCTAssertEqual(options.surface, .floating)
        options.surface = .desktop
        XCTAssertEqual(settings.lyricComposition, .mosaic)
    }

    func testProfileSwapIsANoOpForTheSameSurface() {
        let defaults = LunalyrTestEnvironment.isolatedDefaults("LunalyrTests.Floating")
        defaults.set("echo", forKey: "lyricComposition")
        let profiles = LyricStyleProfiles(defaults: defaults)
        XCTAssertFalse(profiles.switchSurface(from: .desktop, to: .desktop))
        XCTAssertNil(profiles.saved(.desktop))
        XCTAssertTrue(profiles.switchSurface(from: .desktop, to: .floating), "the first switch seeds")
        XCTAssertFalse(profiles.switchSurface(from: .floating, to: .desktop))
        XCTAssertFalse(profiles.switchSurface(from: .desktop, to: .floating), "and only the first")
    }

    func testFloatingOnlySettingsAreNotPartOfAStyle() {
        for key in [FloatingLyricsOptions.Keys.surface, FloatingLyricsOptions.Keys.textSize,
                    FloatingLyricsOptions.Keys.placement, "backgroundMode", "custom.lyricOffset"] {
            XCTAssertFalse(LyricStyleProfiles.keys.contains(key), key)
        }
        XCTAssertEqual(Set(LyricStyleProfiles.keys).count, LyricStyleProfiles.keys.count, "no key twice")
    }

    // MARK: Frame maths

    /// A 1512 by 982 laptop screen with a notch: a 38-point menu bar and a
    /// 70-point Dock at the bottom.
    private let screen = CGRect(x: 0, y: 0, width: 1512, height: 982)
    private let visible = CGRect(x: 0, y: 70, width: 1512, height: 982 - 70 - 38)

    func testUsableAreaKeepsClearOfMenuBarDockAndNotch() {
        let area = FloatingFrame.usableArea(screenFrame: screen, visibleFrame: visible, safeTopInset: 32)
        XCTAssertEqual(area, visible)
        // Menu bar hidden: the visible frame reaches the top, the notch does not.
        let hidden = FloatingFrame.usableArea(screenFrame: screen, visibleFrame: CGRect(x: 0, y: 70, width: 1512, height: 912),
                                              safeTopInset: 32)
        XCTAssertEqual(hidden.maxY, 982 - 32)
        XCTAssertEqual(hidden.minY, 70)
    }

    func testPresetsSitInsideTheAreaWithMargins() {
        let area = FloatingFrame.usableArea(screenFrame: screen, visibleFrame: visible, safeTopInset: 32)
        let size = CGSize(width: 800, height: 200)
        let m = FloatingFrame.margin
        for placement in FloatingPlacement.presets {
            let frame = FloatingFrame.preset(placement, size: size, in: area)
            XCTAssertTrue(area.insetBy(dx: m - 0.5, dy: m - 0.5).contains(frame) || placement == .centre, "\(placement)")
            XCTAssertEqual(frame.size, size)
        }
        XCTAssertEqual(FloatingFrame.preset(.bottom, size: size, in: area).minY, area.minY + m, "just above the Dock")
        XCTAssertEqual(FloatingFrame.preset(.top, size: size, in: area).maxY, area.maxY - m, "just below the menu bar")
        XCTAssertEqual(FloatingFrame.preset(.bottom, size: size, in: area).midX, area.midX)
        XCTAssertEqual(FloatingFrame.preset(.topLeft, size: size, in: area).minX, area.minX + m)
        XCTAssertEqual(FloatingFrame.preset(.bottomRight, size: size, in: area).maxX, area.maxX - m)
        XCTAssertEqual(FloatingFrame.preset(.centre, size: size, in: area).midY, area.midY)
    }

    func testPresetsFollowADockOnTheSide() {
        // Dock on the left: the visible frame starts after it.
        let left = CGRect(x: 80, y: 0, width: 1432, height: 944)
        let area = FloatingFrame.usableArea(screenFrame: screen, visibleFrame: left, safeTopInset: 0)
        XCTAssertEqual(FloatingFrame.preset(.bottomLeft, size: CGSize(width: 400, height: 120), in: area).minX,
                       80 + FloatingFrame.margin)
    }

    func testOversizedWindowsShrinkToTheArea() {
        let area = CGRect(x: 0, y: 0, width: 600, height: 300)
        let frame = FloatingFrame.preset(.bottom, size: CGSize(width: 2000, height: 900), in: area)
        XCTAssertEqual(frame.size, CGSize(width: 600, height: 300))
        XCTAssertTrue(area.contains(frame))
        XCTAssertEqual(FloatingFrame.fitted(CGSize(width: 10, height: 10), in: area), FloatingFrame.minimumSize)
    }

    func testDefaultSizeFollowsTextSize() {
        let area = CGRect(x: 0, y: 0, width: 1512, height: 874)
        let small = FloatingFrame.defaultSize(textSize: 30, in: area)
        let large = FloatingFrame.defaultSize(textSize: 64, in: area)
        XCTAssertLessThan(small.width, large.width)
        XCTAssertLessThan(small.height, large.height)
        XCTAssertLessThanOrEqual(large.width, area.width - 2 * FloatingFrame.margin)
        let tiny = CGRect(x: 0, y: 0, width: 300, height: 200)
        XCTAssertLessThanOrEqual(FloatingFrame.defaultSize(textSize: 110, in: tiny).width, 300)
    }

    func testCustomFramesAreClampedOntoTheScreen() {
        let area = FloatingFrame.usableArea(screenFrame: screen, visibleFrame: visible, safeTopInset: 32)
        // Saved on a bigger display, now off the edge of this one.
        let frame = FloatingFrame.resolve(.custom, custom: CGRect(x: 1400, y: -50, width: 700, height: 180), savedSize: nil,
                                          textSize: 44, screenFrame: screen, area: area)
        XCTAssertTrue(area.contains(frame))
        XCTAssertEqual(frame.size, CGSize(width: 700, height: 180))
        // Relative to its display: a second display to the right gets the same spot.
        let second = CGRect(x: 1512, y: 0, width: 1920, height: 1080)
        let there = FloatingFrame.resolve(.custom, custom: CGRect(x: 100, y: 100, width: 500, height: 150), savedSize: nil,
                                          textSize: 44, screenFrame: second, area: second)
        XCTAssertEqual(there.origin, CGPoint(x: 1612, y: 100))
        XCTAssertEqual(FloatingFrame.relative(there, to: second).origin, CGPoint(x: 100, y: 100))
        // Custom with nothing saved falls back to a preset.
        let fallback = FloatingFrame.resolve(.custom, custom: nil, savedSize: nil, textSize: 44, screenFrame: screen, area: area)
        XCTAssertTrue(area.contains(fallback))
        // A preset keeps the size given on that display.
        let sized = FloatingFrame.resolve(.top, custom: nil, savedSize: CGSize(width: 520, height: 130), textSize: 44,
                                          screenFrame: screen, area: area)
        XCTAssertEqual(sized.size, CGSize(width: 520, height: 130))
    }

    func testDraggingMovesWithinTheAreaAndResizesFromTheCorner() {
        let area = CGRect(x: 0, y: 0, width: 1000, height: 800)
        let start = CGRect(x: 100, y: 100, width: 400, height: 150)
        XCTAssertEqual(FloatingFrame.dragged(start, by: CGSize(width: 50, height: 20), resizing: false, in: area),
                       CGRect(x: 150, y: 120, width: 400, height: 150))
        XCTAssertEqual(FloatingFrame.dragged(start, by: CGSize(width: -500, height: 0), resizing: false, in: area).minX, 0)
        // Down and right grows it, top left corner fixed.
        let grown = FloatingFrame.dragged(start, by: CGSize(width: 100, height: -50), resizing: true, in: area)
        XCTAssertEqual(grown, CGRect(x: 100, y: 50, width: 500, height: 200))
        XCTAssertEqual(grown.maxY, start.maxY)
        let shrunk = FloatingFrame.dragged(start, by: CGSize(width: -1000, height: 1000), resizing: true, in: area)
        XCTAssertEqual(shrunk.size, FloatingFrame.minimumSize)
        let huge = FloatingFrame.dragged(start, by: CGSize(width: 5000, height: -5000), resizing: true, in: area)
        XCTAssertLessThanOrEqual(huge.maxX, area.maxX)
        XCTAssertGreaterThanOrEqual(huge.minY, area.minY)
    }

    // MARK: The box and the window round it

    func testSpillMarginFollowsTextSize() {
        XCTAssertEqual(FloatingFrame.spillMargin(textSize: 30), 160, "never less than 160 points")
        XCTAssertEqual(FloatingFrame.spillMargin(textSize: 44), 160)
        XCTAssertEqual(FloatingFrame.spillMargin(textSize: 110), 165, "1.5 times a large text size")
        XCTAssertEqual(FloatingFrame.spillMargin(textSize: .nan), 160)
        for size in stride(from: 22.0, through: 110, by: 0.7) {
            let margin = FloatingFrame.spillMargin(textSize: CGFloat(size))
            XCTAssertGreaterThanOrEqual(margin, CGFloat(size) * 1.5)
            XCTAssertEqual(margin, margin.rounded(), "whole points, so the box stays on whole points")
        }
    }

    func testWindowGoesRoundTheBoxAndBack() {
        let box = CGRect(x: 412, y: 94, width: 688, height: 202)
        let window = FloatingFrame.window(forBox: box, margin: 160)
        XCTAssertEqual(window, CGRect(x: 252, y: -66, width: 1008, height: 522), "160 points more on every side")
        XCTAssertEqual(window.midX, box.midX)
        XCTAssertEqual(window.midY, box.midY)
        XCTAssertEqual(FloatingFrame.box(forWindow: window, margin: 160), box)
        // Off the screen's edge is fine; the conversion does not clamp.
        let corner = CGRect(x: 0, y: 0, width: FloatingFrame.minimumSize.width, height: FloatingFrame.minimumSize.height)
        XCTAssertEqual(FloatingFrame.box(forWindow: FloatingFrame.window(forBox: corner, margin: 165), margin: 165), corner)
        // No margin: the window is the box.
        XCTAssertEqual(FloatingFrame.window(forBox: box, margin: 0), box)
    }

    /// The saved frame means the box: placing it, putting the window round
    /// it and reading the box back gives the frame that was saved, on any display.
    func testSavedFrameRoundTripsThroughTheWindowUnchanged() {
        let store = stores()
        let area = FloatingFrame.usableArea(screenFrame: screen, visibleFrame: visible, safeTopInset: 32)
        let second = CGRect(x: -1920, y: 0, width: 1920, height: 1080)
        for (key, screenFrame, area) in [("A", screen, area), ("B", second, second)] {
            let saved = CGRect(x: 120, y: 160, width: 640, height: 180)
            store.options.storeCustomFrame(saved, for: key)
            XCTAssertEqual(store.defaults.dictionary(forKey: FloatingLyricsOptions.Keys.frames)?[key] as? [Double],
                           FloatingFrame.encode(saved), "the box is stored, not the window")
            for textSize in [22.0, 44, 110] {
                let box = FloatingFrame.resolve(.custom, custom: store.options.customFrame(for: key), savedSize: nil,
                                                textSize: CGFloat(textSize), screenFrame: screenFrame, area: area)
                let margin = FloatingFrame.spillMargin(textSize: CGFloat(textSize))
                let window = FloatingFrame.window(forBox: box, margin: margin)
                XCTAssertGreaterThan(window.width, box.width)
                let back = FloatingFrame.box(forWindow: window, margin: margin)
                XCTAssertEqual(back, box)
                XCTAssertEqual(FloatingFrame.relative(back, to: screenFrame), saved, "\(key) at \(textSize)")
                // Saving it again (a drag that went nowhere) changes nothing.
                store.options.storeCustomFrame(FloatingFrame.relative(back, to: screenFrame), for: key)
                XCTAssertEqual(store.options.customFrame(for: key), saved)
                XCTAssertEqual(store.options.savedSize(for: key), saved.size)
            }
        }
    }

    /// What the stage draws past the box shows in the margin; the shade
    /// stays on the box.
    func testStageDrawsIntoTheMarginAndTheShadeStaysInTheBox() throws {
        let box = CGSize(width: 300, height: 100), margin: CGFloat = 60
        let window = CGSize(width: box.width + 2 * margin, height: box.height + 2 * margin)
        let frame = FloatingLyricsFrame(backdrop: 1, reduceTransparency: false, albumAccent: SIMD3(0.5, 0.5, 0.5),
                                        margin: margin) {
            // A pill wider than the box, as a word's tile or a spin can be.
            Capsule().fill(Color.red)
                .frame(width: box.width + 80, height: 30)
                .frame(width: box.width, height: box.height)
        }
        .frame(width: window.width, height: window.height)
        let renderer = ImageRenderer(content: frame)
        renderer.scale = 1
        let bitmap = NSBitmapImageRep(cgImage: try XCTUnwrap(renderer.cgImage))
        XCTAssertEqual(bitmap.pixelsWide, Int(window.width))
        func pixel(_ x: CGFloat, _ y: CGFloat) -> NSColor {
            bitmap.colorAt(x: Int(x), y: Int(y))?.usingColorSpace(.sRGB) ?? .clear
        }
        let middle = window.height / 2
        for x in [margin - 30, window.width - margin + 30] {
            let spilled = pixel(x, middle)
            XCTAssertGreaterThan(spilled.alphaComponent, 0.9, "drawn in the margin at x \(x)")
            XCTAssertGreaterThan(spilled.redComponent, spilled.blueComponent + 0.5, "the pill's own red at x \(x)")
        }
        XCTAssertEqual(pixel(4, 4).alphaComponent, 0, accuracy: 0.01, "no shade in the margin")
        XCTAssertEqual(pixel(4, middle).alphaComponent, 0, accuracy: 0.01, "nothing past the pill")
        XCTAssertGreaterThan(pixel(window.width / 2, margin + 30).alphaComponent, 0.3, "the shade covers the box")
    }

    func testFramesEncodeAndDisplaysFallBackToTheMainOne() {
        let rect = CGRect(x: 1, y: 2, width: 3, height: 4)
        XCTAssertEqual(FloatingFrame.decode(FloatingFrame.encode(rect)), rect)
        XCTAssertNil(FloatingFrame.decode([1, 2, 3]))
        XCTAssertNil(FloatingFrame.decode([1, 2, 0, 4]))
        XCTAssertNil(FloatingFrame.decode([.nan, 2, 3, 4]))
        XCTAssertEqual(FloatingFrame.chosenIndex("", among: ["A", "B"]), 0)
        XCTAssertEqual(FloatingFrame.chosenIndex("B", among: ["A", "B"]), 1)
        XCTAssertEqual(FloatingFrame.chosenIndex("gone", among: ["A", "B"]), 0, "a disconnected choice uses the main display")
        XCTAssertNil(FloatingFrame.chosenIndex("A", among: []))
    }

    func testPickerGridHasEveryPresetOnce() {
        let cells = FloatingPlacement.presets.compactMap(\.cell).map { "\($0.column),\($0.row)" }
        XCTAssertEqual(Set(cells).count, FloatingPlacement.presets.count)
        XCTAssertNil(FloatingPlacement.custom.cell)
        XCTAssertFalse(FloatingPlacement.presets.contains(.custom))
    }

    // MARK: When the window shows

    private var playing: FloatingLyricsVisibility.Input {
        .init(floats: true, lyricsVisible: true, hasTrack: true, isPlaying: true, hasTimedLines: true)
    }

    func testShowsOnlyWithLyricsPlaying() {
        XCTAssertTrue(FloatingLyricsVisibility.shows(playing))
        var input = playing
        input.hasTrack = false
        input.isPlaying = false
        XCTAssertFalse(FloatingLyricsVisibility.shows(input), "nothing playing")
        input = playing
        input.hasTimedLines = false
        XCTAssertFalse(FloatingLyricsVisibility.shows(input), "no lyrics (still loading)")
        input.hasPlainLines = true
        XCTAssertTrue(FloatingLyricsVisibility.shows(input), "untimed lyrics are shown")
        input = playing
        input.notFound = true
        XCTAssertFalse(FloatingLyricsVisibility.shows(input), "no lyrics found")
        input = playing
        input.allInstrumental = true
        XCTAssertFalse(FloatingLyricsVisibility.shows(input), "an instrumental")
        input = playing
        input.lyricsVisible = false
        XCTAssertFalse(FloatingLyricsVisibility.shows(input), "Hide lyrics or mirroring")
    }

    func testAShortPauseKeepsTheLyricsUp() {
        var input = playing
        input.isPlaying = false
        input.pausedFor = 3
        XCTAssertTrue(FloatingLyricsVisibility.shows(input))
        XCTAssertEqual(FloatingLyricsVisibility.hidesAfter(input) ?? -1, FloatingLyricsVisibility.pauseGrace - 3, accuracy: 0.001)
        input.pausedFor = FloatingLyricsVisibility.pauseGrace + 1
        XCTAssertFalse(FloatingLyricsVisibility.shows(input))
        XCTAssertNil(FloatingLyricsVisibility.hidesAfter(input))
        XCTAssertNil(FloatingLyricsVisibility.hidesAfter(playing))
    }

    func testEditPositionShowsTheWindowEvenWithNothingPlaying() {
        var input = FloatingLyricsVisibility.Input(floats: true, editing: true)
        XCTAssertTrue(FloatingLyricsVisibility.shows(input))
        input.floats = false
        XCTAssertFalse(FloatingLyricsVisibility.shows(input), "nothing to place on a display without floating lyrics")
    }

    // MARK: The window and its look

    func testWindowStaysOutOfTheWayOfAppsAndTheSystem() {
        let behaviour = FloatingLyricsController.collectionBehavior
        XCTAssertTrue(behaviour.contains(.canJoinAllSpaces))
        XCTAssertTrue(behaviour.contains(.fullScreenAuxiliary))
        XCTAssertTrue(behaviour.contains(.ignoresCycle))
        XCTAssertTrue(behaviour.contains(.stationary))
        let level = FloatingLyricsController.level.rawValue
        XCTAssertGreaterThan(level, NSWindow.Level.normal.rawValue, "above app windows")
        XCTAssertLessThan(level, NSWindow.Level.mainMenu.rawValue, "under the menu bar")
        XCTAssertLessThan(level, Int(CGWindowLevelForKey(.dockWindow)), "under the Dock")
        XCTAssertLessThan(level, NSWindow.Level.modalPanel.rawValue, "under alerts")
    }

    func testBackdropDarkensWhatTheColoursAreJudgedAgainst() {
        let none = FloatingBackdrop.luminance(strength: 0, reduceTransparency: false)
        let full = FloatingBackdrop.luminance(strength: 1, reduceTransparency: false)
        let solid = FloatingBackdrop.luminance(strength: 0, reduceTransparency: true)
        XCTAssertEqual(none, LyricLegibility.unknownBackdropLuminance, accuracy: 0.0001)
        XCTAssertLessThan(full, none)
        XCTAssertLessThan(solid, full, "Reduce Transparency draws a nearly solid panel")
    }

    func testFloatingTextSizeReplacesTheScreenBasedSize() {
        let window = LyricViewportMetrics(size: CGSize(width: 800, height: 200), textSize: 44)
        XCTAssertEqual(window.baseSize, 44)
        XCTAssertGreaterThan(window.stageSize.width, 800 * 0.8, "the stage fills nearly the whole window")
        let screen = LyricViewportMetrics(size: CGSize(width: 1512, height: 982))
        XCTAssertEqual(screen.stageSize.width, 1512 * 0.8, accuracy: 0.001, "the wallpaper's is unchanged")
    }

    // MARK: Links

    func testFloatingLinkSwitchesSurface() {
        XCTAssertEqual(LunalyrLink(URL(string: "lunalyr://floating")!), .command(.toggleFloating))
        XCTAssertNil(LunalyrLink(URL(string: "lunalyr://floating/on")!))
        XCTAssertFalse(LunalyrCommand.toggleFloating.doneText.isEmpty)
    }
}
