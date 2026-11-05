import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// The shared design system (Sources/UI/Design): its colours keep their
/// contrast, its symbols exist on macOS 14, and its model helpers act on the
/// models they are given.
@MainActor
final class DesignSystemTests: XCTestCase {
    private func grey(_ value: Int) -> NSColor {
        NSColor(srgbRed: CGFloat(value) / 255, green: CGFloat(value) / 255, blue: CGFloat(value) / 255, alpha: 1)
    }

    private func luminance(_ colour: NSColor) throws -> CGFloat {
        let rgb = try XCTUnwrap(colour.usingColorSpace(.sRGB))
        func linear(_ channel: CGFloat) -> CGFloat {
            channel <= 0.04045 ? channel / 12.92 : pow((channel + 0.055) / 1.055, 2.4)
        }
        return 0.2126 * linear(rgb.redComponent) + 0.7152 * linear(rgb.greenComponent) + 0.0722 * linear(rgb.blueComponent)
    }

    private func contrast(_ a: NSColor, _ b: NSColor) throws -> CGFloat {
        let la = try luminance(a), lb = try luminance(b)
        return (max(la, lb) + 0.05) / (min(la, lb) + 0.05)
    }

    /// `top` (with alpha) laid over the opaque `bottom`.
    private func over(_ top: NSColor, _ bottom: NSColor) throws -> NSColor {
        let t = try XCTUnwrap(top.usingColorSpace(.sRGB)), b = try XCTUnwrap(bottom.usingColorSpace(.sRGB))
        let a = t.alphaComponent
        return NSColor(srgbRed: t.redComponent * a + b.redComponent * (1 - a),
                       green: t.greenComponent * a + b.greenComponent * (1 - a),
                       blue: t.blueComponent * a + b.blueComponent * (1 - a), alpha: 1)
    }

    func testStatusTextKeepsItsContrastOnEverySurfaceItSitsOn() throws {
        for dark in [false, true] {
            // Windows and grouped rows across releases; plain status text
            // also sits on sidebars and tiles (#3A3A3A), banners never do.
            let surfaces = dark ? [grey(0x1E), grey(0x28), grey(0x32)] : [grey(0xFF), grey(0xF5), grey(0xEC)]
            for increaseContrast in [false, true] {
                let look = LunalyrAppearance(dark: dark, increaseContrast: increaseContrast)
                for status in LunalyrStatus.allCases {
                    let text = LunalyrPalette.text(for: status, look)
                    for surface in surfaces + (dark ? [grey(0x3A)] : []) {
                        let label = "\(status) on \(surface), dark: \(dark), increase contrast: \(increaseContrast)"
                        XCTAssertGreaterThanOrEqual(try contrast(text, surface), 4.5, label)
                    }
                    for surface in surfaces {
                        let label = "\(status) on \(surface), dark: \(dark), increase contrast: \(increaseContrast)"
                        // On its own banner fill, too.
                        let banner = try over(LunalyrPalette.fill(for: status, look), surface)
                        XCTAssertGreaterThanOrEqual(try contrast(text, banner), 4.5, "on its fill: \(label)")
                    }
                }
            }
        }
    }

    func testHighContrastAppearancesCountAsIncreaseContrast() throws {
        for name in [NSAppearance.Name.accessibilityHighContrastAqua, .accessibilityHighContrastDarkAqua,
                     .accessibilityHighContrastVibrantLight, .accessibilityHighContrastVibrantDark] {
            XCTAssertTrue(LunalyrAppearance.isHighContrast(name), name.rawValue)
        }
        for name in [NSAppearance.Name.aqua, .darkAqua, .vibrantLight, .vibrantDark] {
            XCTAssertFalse(LunalyrAppearance.isHighContrast(name), name.rawValue)
        }
        XCTAssertTrue(LunalyrAppearance(try XCTUnwrap(NSAppearance(named: .darkAqua))).dark)
        XCTAssertFalse(LunalyrAppearance(try XCTUnwrap(NSAppearance(named: .aqua))).dark)
        // What the snapshot harness's Increase Contrast looks use.
        LunalyrAppearance.simulatedIncreaseContrast = true
        defer { LunalyrAppearance.simulatedIncreaseContrast = nil }
        XCTAssertTrue(LunalyrAppearance(try XCTUnwrap(NSAppearance(named: .aqua))).increaseContrast)
    }

    func testEverySymbolTheDesignSystemNamesExists() {
        var symbols = Settings.BackgroundMode.allCases.map(\.symbol) + LunalyrStatus.allCases.map(\.symbol)
        symbols += [PlayerAccessStatus.granted, .denied, .notDetermined, .notRunning, .notInstalled, .unknown].map(\.symbol)
        // The spec's sidebar icons and the components' own.
        symbols += ["photo.artframe", "textformat", "square.stack.fill", "clock.fill", "character.book.closed.fill",
                    "gearshape.fill", "command", "leaf.fill", "info.circle.fill", "lock.shield.fill", "pause.rectangle.fill",
                    "rectangle.on.rectangle", "music.note", "megaphone", "dot.radiowaves.left.and.right", "backward.fill",
                    "forward.fill", "play.fill", "pause.fill", "eye.slash", "eye", "photo.on.rectangle", "minus", "plus",
                    "arrow.counterclockwise", "info.circle", "music.note.list", "menubar.rectangle"]
        for symbol in Set(symbols) {
            XCTAssertNotNil(NSImage(systemSymbolName: symbol, accessibilityDescription: nil), "no SF Symbol named \(symbol)")
        }
    }

    func testEveryWallpaperTypeHasATileNameAndSummary() {
        XCTAssertEqual(Set(Settings.BackgroundMode.generated + Settings.BackgroundMode.own),
                       Set(Settings.BackgroundMode.allCases), "the grid shows every type")
        XCTAssertEqual((Settings.BackgroundMode.generated + Settings.BackgroundMode.own).count,
                       Settings.BackgroundMode.allCases.count, "and each once")
        for mode in Settings.BackgroundMode.allCases {
            XCTAssertFalse(mode.shortTitle.isEmpty)
            XCTAssertLessThanOrEqual(mode.shortTitle.count, Settings.BackgroundMode.own.contains(mode) ? 14 : 10,
                                     "\(mode.shortTitle) won't fit a tile")
            XCTAssertTrue(mode.summary.hasSuffix("."), mode.summary)
        }
    }

    func testTimingNudgeWordsTheOffsetBothWays() {
        XCTAssertEqual(TimingNudge.format(0), "0.00 s")
        XCTAssertEqual(TimingNudge.format(0.25), "+0.25 s")
        XCTAssertEqual(TimingNudge.format(-0.3), "−0.30 s")
        XCTAssertEqual(TimingNudge.spoken(0), "No offset")
        XCTAssertEqual(TimingNudge.spoken(0.25), "Lyrics 0.25 seconds earlier")
        XCTAssertEqual(TimingNudge.spoken(-0.3), "Lyrics 0.30 seconds later")
    }

    func testNudgingSavesTheOffsetForThePlayingSongInTheModelsGiven() throws {
        let shared = SongOffsets.shared.values
        let scene = UISnapshotScene(player: .playing)
        defer { scene.tearDown() }
        var announced: [String] = []
        scene.extras.announce = { announced.append($0) }
        XCTAssertEqual(scene.models.songOffset, 0)
        XCTAssertEqual(scene.models.nudgeSongOffset(by: 0.1), 0.1)
        XCTAssertEqual(scene.models.nudgeSongOffset(by: 0.1), 0.2)
        XCTAssertEqual(scene.models.songOffset, 0.2, accuracy: 0.0001)
        XCTAssertEqual(announced.count, 2, "each nudge is announced, as the shortcuts' are")
        scene.models.resetSongOffset()
        XCTAssertEqual(scene.models.songOffset, 0)
        XCTAssertEqual(SongOffsets.shared.values, shared, "the app's own offsets are untouched")

        let idle = UISnapshotScene(player: .idle)
        defer { idle.tearDown() }
        idle.extras.announce = { _ in }
        XCTAssertNil(idle.models.nudgeSongOffset(by: 0.1), "nothing playing: nothing to nudge")
    }

    func testNavigationRecordsRequestsAndDefersToOnboarding() {
        var events: [String] = []
        var onboardingUp = false
        let navigation = LunalyrNavigation(presentSettings: { events.append("settings") },
                                           presentOnboarding: { events.append("onboarding") },
                                           onboardingIsVisible: { onboardingUp },
                                           bringOnboardingForward: { events.append("forward") },
                                           openURL: { events.append($0.absoluteString) })
        navigation.openSettings(.timing)
        XCTAssertEqual(navigation.settingsRequest?.destination, .timing)
        XCTAssertEqual(navigation.consumeSettingsRequest(), .timing)
        XCTAssertNil(navigation.settingsRequest)
        navigation.openOnboarding()
        onboardingUp = true
        navigation.openOnboarding()
        navigation.openSystemSettings(.automation)
        XCTAssertEqual(events, ["settings", "onboarding", "forward", AutomationStatusBanner.privacySettingsURL.absoluteString])
    }
}
