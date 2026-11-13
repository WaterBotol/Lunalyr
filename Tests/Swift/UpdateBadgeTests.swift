import AppKit
import XCTest
@testable import VersoCustom

/// The update badge: what Sparkle's callbacks do to it, without Sparkle,
/// a feed or the network. No updater is ever started here, and the only
/// defaults touched are a throwaway suite.
final class UpdateBadgeTests: XCTestCase {
    private let scheduled = UpdateBadgeState.Event.found(version: "2.1.0", userInitiated: false, sparkleShowsIt: false)

    func testAScheduledCheckThatFindsAnUpdateBadgesTheIcon() {
        var state = UpdateBadgeState(configured: true)
        XCTAssertFalse(state.showsBadge)
        XCTAssertNil(state.pendingVersion)
        state.handle(scheduled)
        XCTAssertTrue(state.showsBadge)
        XCTAssertEqual(state.pendingVersion, "2.1.0")
    }

    func testTheBadgeGoesOnceThePersonAttendsOrTheSessionEnds() {
        for ending in [UpdateBadgeState.Event.userAttention, .sessionFinished] {
            var state = UpdateBadgeState(configured: true)
            state.handle(scheduled)
            state.handle(ending)
            XCTAssertFalse(state.showsBadge, "\(ending)")
            XCTAssertNil(state.pendingVersion, "\(ending)")
        }
    }

    func testLaterHidesTheBadgeButTheUpdateStillWaits() {
        var state = UpdateBadgeState(configured: true)
        state.handle(scheduled)
        state.handle(.dismissed)
        XCTAssertFalse(state.showsBadge)
        XCTAssertEqual(state.pendingVersion, "2.1.0", "Settings › General still says it")
        // The next daily check finds it again.
        state.handle(scheduled)
        XCTAssertTrue(state.showsBadge)
        state.handle(.sessionFinished)
        XCTAssertNil(state.pendingVersion)
    }

    func testACheckThePersonStartsNeverBadges() {
        var state = UpdateBadgeState(configured: true)
        state.handle(.found(version: "2.1.0", userInitiated: true, sparkleShowsIt: true))
        XCTAssertFalse(state.showsBadge)
        XCTAssertNil(state.pendingVersion)
        // Check for Updates… while a badge shows: Sparkle's window takes over.
        state.handle(scheduled)
        state.handle(.found(version: "2.1.0", userInitiated: true, sparkleShowsIt: true))
        XCTAssertFalse(state.showsBadge)
    }

    func testAnUpdateSparkleShowsItselfNeverBadges() {
        var state = UpdateBadgeState(configured: true)
        state.handle(.found(version: "2.1.0", userInitiated: false, sparkleShowsIt: true))
        XCTAssertFalse(state.showsBadge)
    }

    func testABuildWithoutAnUpdateServiceNeverBadges() {
        var state = UpdateBadgeState(configured: false)
        state.handle(scheduled)
        XCTAssertFalse(state.showsBadge)
        XCTAssertNil(state.pendingVersion)
    }

    func testTheHeadlineNamesTheVersion() {
        XCTAssertEqual(UpdateBadgeState.headline(for: "2.1.0"), "Lunalyr 2.1.0 is available")
        XCTAssertEqual(UpdateBadgeState.headline(for: " "), "A new version of Lunalyr is available")
        XCTAssertEqual(UpdateBadgeState.headline(for: nil), "A new version of Lunalyr is available")
    }

    // MARK: AppUpdates

    private func suite() -> (UserDefaults, () -> Void) {
        let name = "LunalyrUpdateBadgeTests-\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: name)!
        return (defaults, { defaults.removePersistentDomain(forName: name) })
    }

    @MainActor
    func testAppUpdatesPublishesTheBadgeAndSaysSoInSettings() {
        let (defaults, cleanUp) = suite()
        defer { cleanUp() }
        let updates = AppUpdates(configured: true, defaults: defaults)
        XCTAssertEqual(updates.status, "Lunalyr checks for updates once a day and asks before installing one.")
        updates.receive(scheduled)
        XCTAssertTrue(updates.badge.showsBadge)
        XCTAssertEqual(updates.status, "Lunalyr 2.1.0 is available.")
        updates.dismissBadge()
        XCTAssertFalse(updates.badge.showsBadge)
        XCTAssertEqual(updates.status, "Lunalyr 2.1.0 is available.", "still waiting")
        updates.receive(.sessionFinished)
        XCTAssertEqual(updates.status, "Lunalyr checks for updates once a day and asks before installing one.")

        let unconfigured = AppUpdates(configured: false, defaults: defaults)
        unconfigured.receive(scheduled)
        XCTAssertFalse(unconfigured.badge.showsBadge)
        XCTAssertEqual(unconfigured.status, "An update service hasn’t been set up for this build yet.")
    }

    func testExistingInstallsAdoptDailyChecksOnce() {
        let (defaults, cleanUp) = suite()
        defer { cleanUp() }
        // A value left from before, which 2.0.0 never let anyone choose.
        defaults.set(false, forKey: "SUEnableAutomaticChecks")
        AppUpdates.adoptDailyChecks(in: defaults)
        XCTAssertNil(defaults.object(forKey: "SUEnableAutomaticChecks"), "Info.plist's daily checks apply")
        XCTAssertTrue(defaults.bool(forKey: AppUpdates.adoptedDailyChecksKey))
        // Switched off afterwards in Settings: that choice stays.
        defaults.set(false, forKey: "SUEnableAutomaticChecks")
        AppUpdates.adoptDailyChecks(in: defaults)
        XCTAssertEqual(defaults.object(forKey: "SUEnableAutomaticChecks") as? Bool, false)
    }

    func testTheBundleChecksDailyAndNeverInstallsOnItsOwn() throws {
        let info = try XCTUnwrap(Bundle.main.infoDictionary)
        XCTAssertEqual(info["SUEnableAutomaticChecks"] as? Bool, true)
        XCTAssertEqual((info["SUScheduledCheckInterval"] as? NSNumber)?.intValue, 86400)
        XCTAssertEqual(info["SUAutomaticallyUpdate"] as? Bool, false)
        XCTAssertEqual(info["SUSendProfileInfo"] as? Bool, false)
    }

    // MARK: The icon

    @MainActor
    func testAWaitingUpdatePutsADotInTheIconsUpperRightCorner() throws {
        let plain = NSImage(size: NSSize(width: 18, height: 18), flipped: false) { rect in
            NSColor.black.setFill()
            rect.fill()
            return true
        }
        plain.isTemplate = true
        XCTAssertTrue(MenuBarView.badgedIcon(from: plain, paused: false, updateAvailable: false) === plain)
        let badged = MenuBarView.badgedIcon(from: plain, paused: false, updateAvailable: true, style: .dot)
        XCTAssertTrue(badged.isTemplate, "the system colours it like the plain icon, in light and dark menu bars")
        XCTAssertEqual(badged.size, plain.size)
        XCTAssertEqual(badged.accessibilityDescription, "Lunalyr, update available")
        XCTAssertTrue(badged.representations.contains { $0.pixelsWide == 36 }, "sharp on a Retina menu bar")
        XCTAssertEqual(MenuBarView.accessibilityLabel(paused: true, updateAvailable: true),
                       "Lunalyr, wallpaper paused, update available")

        let before = try alpha(of: plain), after = try alpha(of: badged)
        var changed: [(row: Int, column: Int)] = []
        for row in before.indices {
            for column in before[row].indices where before[row][column] != after[row][column] {
                changed.append((row, column))
            }
        }
        XCTAssertGreaterThan(changed.count, 20, "a ring is cut out around the dot")
        // Rows run top to bottom: all of it in the upper right corner.
        XCTAssertTrue(changed.allSatisfy { $0.column >= 16 && $0.row < 20 })
        XCTAssertGreaterThan(after[4][32], 200, "the dot itself is solid")
        XCTAssertTrue(after[30][4] == before[30][4] && after[30][4] > 200, "the mark itself is untouched")
    }

    @MainActor
    func testTheShippedUpdateIconIsADifferentIconInColour() throws {
        XCTAssertEqual(MenuBarView.updateIconStyle, .arrowMoon)
        let plain = NSImage(size: NSSize(width: 18, height: 18), flipped: false) { rect in
            NSColor.black.setFill()
            rect.fill()
            return true
        }
        plain.isTemplate = true
        for paused in [false, true] {
            let icon = MenuBarView.badgedIcon(from: plain, paused: paused, updateAvailable: true, style: .arrowMoon)
            XCTAssertFalse(icon.isTemplate, "drawn in its own colours on any menu bar")
            XCTAssertEqual(icon.size, plain.size)
            XCTAssertTrue(icon.representations.contains { $0.pixelsWide == 36 })
            let bitmap = try XCTUnwrap(icon.representations.compactMap { $0 as? NSBitmapImageRep }.first { $0.pixelsWide == 36 })
            // Not the moon: its corners are clear, where the plain one is ink.
            XCTAssertEqual(bitmap.colorAt(x: 0, y: 35)?.alphaComponent ?? 1, 0, accuracy: 0.01)
            // The violet disc, and the white arrow on it.
            let violet = try XCTUnwrap(bitmap.colorAt(x: paused ? 6 : 8, y: paused ? 18 : 18)?.usingColorSpace(.sRGB))
            XCTAssertGreaterThan(violet.blueComponent, violet.greenComponent + 0.2, "\(violet)")
            XCTAssertEqual(violet.alphaComponent, 1, accuracy: 0.01)
            let arrow = try XCTUnwrap(bitmap.colorAt(x: paused ? 14 : 16, y: paused ? 14 : 18)?.usingColorSpace(.sRGB))
            XCTAssertGreaterThan(arrow.greenComponent, 0.95, "the arrow is white: \(arrow)")
            // The bite out of the upper right.
            XCTAssertEqual(bitmap.colorAt(x: 33, y: 2)?.alphaComponent ?? 1, 0, accuracy: 0.01)
        }
    }

    /// The icon's alpha at 2x, a row of pixels at a time, top row first.
    @MainActor
    private func alpha(of image: NSImage) throws -> [[UInt8]] {
        let width = Int(image.size.width * 2), height = Int(image.size.height * 2)
        let bitmap = try XCTUnwrap(NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: width, pixelsHigh: height,
                                                    bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                                    colorSpaceName: .deviceRGB, bytesPerRow: width * 4, bitsPerPixel: 32))
        bitmap.size = image.size
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: bitmap)
        image.draw(in: NSRect(origin: .zero, size: image.size))
        NSGraphicsContext.restoreGraphicsState()
        let data = try XCTUnwrap(bitmap.bitmapData)
        return (0..<height).map { row in (0..<width).map { data[row * width * 4 + $0 * 4 + 3] } }
    }
}
