import Combine
import XCTest
@testable import VersoCustom

/// Power saving must say what it replaces, never speed anything up, and
/// follow the power source as it changes. Every Settings here reads and
/// writes its own suite and notification centre, never the app's.
@MainActor
final class PowerSavingTests: XCTestCase {
    private func isolatedSettings(center: NotificationCenter = NotificationCenter(),
                                  prepare: (UserDefaults) -> Void = { _ in }) -> (Settings, UserDefaults) {
        let name = "LunalyrTests.\(UUID().uuidString)"
        addTeardownBlock { UserDefaults(suiteName: name)?.removePersistentDomain(forName: name) }
        let defaults = UserDefaults(suiteName: name)!
        prepare(defaults)
        let settings = Settings(defaults: defaults, notifications: center)
        // The test Mac's own Low Power Mode and battery must not leak in.
        settings.respectLowPower = false
        settings.onBattery = .full
        return (settings, defaults)
    }

    func testPowerSavingNeverRaisesASlowerFrameRate() {
        XCTAssertEqual(Settings.effectiveRate(.economical, sparingGPU: true), .economical)
        XCTAssertEqual(Settings.effectiveRate(.balanced, sparingGPU: true), .balanced)
        XCTAssertEqual(Settings.effectiveRate(.smooth, sparingGPU: true), .balanced)
        for rate in Settings.AnimationRate.allCases {
            XCTAssertEqual(Settings.effectiveRate(rate, sparingGPU: false), rate)
        }

        let (settings, _) = isolatedSettings()
        settings.lowPowerVisuals = true
        settings.backgroundMode = .visualizer
        settings.animationRate = .economical
        XCTAssertEqual(settings.effectiveAnimationRate, .economical, "15 fps must stay 15 fps under power saving")
        settings.animationRate = .smooth
        XCTAssertEqual(settings.effectiveAnimationRate, .balanced)
        XCTAssertEqual(settings.effectiveBackgroundMode, .pattern)
        settings.lowPowerVisuals = false
        XCTAssertEqual(settings.effectiveAnimationRate, .smooth)
        XCTAssertEqual(settings.effectiveBackgroundMode, .visualizer)
    }

    func testStrictestConstraintWinsAndBatteryRulesNeedTheBattery() {
        func constraint(visuals: Bool = false, follows: Bool = true, lowPowerMode: Bool = false,
                        rule: Settings.PowerBehaviour = .full, battery: Bool = false) -> Settings.PowerConstraint? {
            Settings.powerConstraint(lowPowerVisuals: visuals, followsLowPowerMode: follows, lowPowerModeOn: lowPowerMode,
                                     onBattery: rule, isOnBattery: battery)
        }
        XCTAssertNil(constraint())
        XCTAssertNil(constraint(rule: .colourOnly), "a battery rule does nothing on mains power")
        XCTAssertNil(constraint(rule: .full, battery: true))
        XCTAssertNil(constraint(follows: false, lowPowerMode: true), "Low Power Mode is only followed when asked")
        XCTAssertEqual(constraint(lowPowerMode: true), .lowPowerMode)
        XCTAssertEqual(constraint(rule: .lightEffects, battery: true), .battery(.lightEffects))
        // Colour only on battery is stricter than Low Power Mode's patterns.
        XCTAssertEqual(constraint(lowPowerMode: true, rule: .colourOnly, battery: true), .battery(.colourOnly))
        XCTAssertEqual(constraint(lowPowerMode: true, rule: .lightEffects, battery: true), .lowPowerMode)
        // Ties go to Lunalyr's own switch, then Low Power Mode.
        XCTAssertEqual(constraint(visuals: true, lowPowerMode: true), .lowPowerVisuals)
        XCTAssertEqual(constraint(lowPowerMode: true, rule: .patterns, battery: true), .lowPowerMode)
    }

    func testEffectiveModeKeepsTheDesktopAndAppliesEachBehaviour() {
        for chosen in Settings.BackgroundMode.allCases {
            XCTAssertEqual(Settings.effectiveMode(chosen, under: nil), chosen)
            XCTAssertEqual(Settings.effectiveMode(chosen, under: .battery(.lightEffects)), chosen)
            let patterns = Settings.effectiveMode(chosen, under: .lowPowerMode)
            let colour = Settings.effectiveMode(chosen, under: .battery(.colourOnly))
            switch chosen {
            case .desktop:
                XCTAssertEqual(patterns, .desktop)
                XCTAssertEqual(colour, .desktop)
            case .colourOnly:
                XCTAssertEqual(patterns, .colourOnly)
                XCTAssertEqual(colour, .colourOnly)
            default:
                XCTAssertEqual(patterns, .pattern)
                XCTAssertEqual(colour, .colourOnly)
            }
        }
    }

    func testNoticeSaysWhatPowerSavingReplaced() throws {
        let replaced = try XCTUnwrap(Settings.powerSavingNotice(chosen: .visualizer, pinnedShader: nil, rate: .balanced,
                                                                constraint: .lowPowerMode))
        XCTAssertTrue(replaced.message.contains("Low Power Mode is on"))
        XCTAssertTrue(replaced.message.contains(Settings.BackgroundMode.pattern.title))
        XCTAssertTrue(replaced.message.contains(Settings.BackgroundMode.visualizer.title))
        XCTAssertNil(replaced.frameRateNote)
        XCTAssertEqual(replaced.constraint.overrideTitle, "Stop following Low Power Mode")

        let held = try XCTUnwrap(Settings.powerSavingNotice(chosen: .auto, pinnedShader: nil, rate: .smooth,
                                                            constraint: .battery(.colourOnly)))
        XCTAssertTrue(held.message.contains("battery"))
        XCTAssertTrue(held.message.contains(Settings.BackgroundMode.colourOnly.title))
        XCTAssertEqual(held.frameRateNote?.contains("30 fps"), true)

        // Nothing visible changes: no banner.
        XCTAssertNil(Settings.powerSavingNotice(chosen: .pattern, pinnedShader: nil, rate: .balanced, constraint: .lowPowerMode))
        XCTAssertNil(Settings.powerSavingNotice(chosen: .visualizer, pinnedShader: nil, rate: .smooth, constraint: nil))
        XCTAssertNil(Settings.powerSavingNotice(chosen: .shader, pinnedShader: .plasma, rate: .balanced,
                                                constraint: .battery(.lightEffects)))
        // Only the frame rate is held.
        let rateOnly = try XCTUnwrap(Settings.powerSavingNotice(chosen: .desktop, pinnedShader: nil, rate: .smooth,
                                                                constraint: .lowPowerVisuals))
        XCTAssertTrue(rateOnly.message.contains("30 fps"))
        // A pinned heavy effect is swapped for a light one.
        XCTAssertEqual(ShaderStyle.nebula.cost, .heavy)
        let swapped = try XCTUnwrap(Settings.powerSavingNotice(chosen: .shader, pinnedShader: .nebula, rate: .balanced,
                                                               constraint: .battery(.lightEffects)))
        XCTAssertTrue(swapped.message.contains(ShaderStyle.nebula.title))
    }

    func testSwapNoticeBlamesPowerSavingOnlyForSwapsItCauses() throws {
        // Only a heavy pinned effect is swapped; every heavy one is reported.
        for style in ShaderStyle.allCases where style.cost == .heavy {
            XCTAssertNotNil(Settings.powerSavingNotice(chosen: .shader, pinnedShader: style, rate: .balanced,
                                                       constraint: .battery(.lightEffects)), style.title)
        }
        // A light effect swaps nothing, and the held frame rate is still
        // reported.
        let rate = try XCTUnwrap(Settings.powerSavingNotice(chosen: .shader, pinnedShader: .plasma, rate: .smooth,
                                                            constraint: .battery(.lightEffects)))
        XCTAssertFalse(rate.message.contains(ShaderStyle.plasma.title))
        XCTAssertTrue(rate.message.contains("30 fps"))

        // Match the music draws a GPU effect only for some tracks, so the
        // notice says when the swap applies rather than that it is happening.
        let matched = try XCTUnwrap(Settings.powerSavingNotice(chosen: .auto, pinnedShader: .nebula, rate: .balanced,
                                                               constraint: .battery(.lightEffects)))
        XCTAssertTrue(matched.message.contains("when Match the music shows a GPU effect"))
        XCTAssertTrue(matched.message.contains(ShaderStyle.nebula.title))
        let pinned = try XCTUnwrap(Settings.powerSavingNotice(chosen: .shader, pinnedShader: .nebula, rate: .balanced,
                                                              constraint: .battery(.lightEffects)))
        XCTAssertFalse(pinned.message.contains("when"))
    }

    func testOverrideLiftsEachConstraintThroughItsOwnSetting() {
        let (settings, defaults) = isolatedSettings()
        settings.backgroundMode = .visualizer
        settings.lowPowerVisuals = true
        XCTAssertEqual(settings.powerConstraint, .lowPowerVisuals)
        XCTAssertNotNil(settings.powerSavingNotice)
        settings.overridePowerSaving(.lowPowerVisuals)
        XCTAssertFalse(settings.lowPowerVisuals)
        XCTAssertNil(settings.powerConstraint)
        XCTAssertNil(settings.powerSavingNotice)
        XCTAssertEqual(defaults.object(forKey: "custom.lowPowerVisuals") as? Bool, false)

        settings.respectLowPower = true
        settings.overridePowerSaving(.lowPowerMode)
        XCTAssertFalse(settings.respectLowPower)
        settings.onBattery = .colourOnly
        settings.overridePowerSaving(.battery(.colourOnly))
        XCTAssertEqual(settings.onBattery, .full)
        XCTAssertEqual(defaults.string(forKey: "onBattery"), "full")
    }

    func testPowerSourceMonitorReportsOnlyRealFlips() {
        final class Source { var onBattery = false }
        let source = Source()
        let center = NotificationCenter()
        let monitor = PowerSourceMonitor(read: { source.onBattery }, center: center, subscribe: { _ in nil })
        let flips = expectation(forNotification: PowerSource.didChange, object: nil, notificationCenter: center)
        flips.expectedFulfillmentCount = 2
        flips.assertForOverFulfill = true

        XCTAssertFalse(monitor.isOnBattery)
        monitor.refresh()                       // unchanged: charge-level noise
        source.onBattery = true
        monitor.refresh()                       // unplugged
        XCTAssertTrue(monitor.isOnBattery)
        monitor.refresh()                       // unchanged
        source.onBattery = false
        monitor.refresh()                       // plugged back in
        XCTAssertFalse(monitor.isOnBattery)
        wait(for: [flips], timeout: 1)
    }

    func testPowerSourceChangesReachTheMonitorThroughItsSubscription() {
        final class Source { var onBattery = false }
        let source = Source()
        let center = NotificationCenter()
        var changed: (() -> Void)?
        weak var heldToken: NSObject?
        var monitor: PowerSourceMonitor? = PowerSourceMonitor(read: { source.onBattery }, center: center, subscribe: { handler in
            changed = handler
            let token = NSObject()
            heldToken = token
            return token
        })
        XCTAssertNotNil(changed, "the monitor never subscribed to power-source changes")
        XCTAssertNotNil(heldToken, "the monitor must keep its subscription alive")
        XCTAssertEqual(monitor?.isOnBattery, false)

        let unplugged = expectation(forNotification: PowerSource.didChange, object: nil, notificationCenter: center)
        source.onBattery = true
        changed?()                              // what IOKit's callback does
        XCTAssertEqual(monitor?.isOnBattery, true)
        wait(for: [unplugged], timeout: 1)

        monitor = nil
        XCTAssertNil(heldToken, "releasing the monitor must end its subscription")
        changed?()                              // a late callback is harmless
    }

    func testIOKitSubscriptionCallsBackThroughItsContextUntilReleased() throws {
        var calls = 0
        var registered: CFRunLoopSource?
        do {
            let subscription = try XCTUnwrap(PowerSourceSubscription(runLoop: CFRunLoopGetCurrent()) { calls += 1 },
                                              "IOKit gave no power-source notification")
            let source = try XCTUnwrap(subscription.source)
            registered = source
            XCTAssertTrue(CFRunLoopContainsSource(CFRunLoopGetCurrent(), source, .commonModes))
            // IOKit calls the C callback with the context it was given.
            PowerSourceSubscription.callback(subscription.context)
            XCTAssertEqual(calls, 1)
        }
        // Released, it can no longer be called back into.
        let source = try XCTUnwrap(registered)
        XCTAssertFalse(CFRunLoopSourceIsValid(source))
        XCTAssertFalse(CFRunLoopContainsSource(CFRunLoopGetCurrent(), source, .commonModes))
    }

    func testSettingsRepublishesPowerChangesSoCaptureAndWallpaperFollow() {
        let center = NotificationCenter()
        let (settings, _) = isolatedSettings(center: center)
        for name in [PowerSource.didChange, Notification.Name.NSProcessInfoPowerStateDidChange] {
            let published = expectation(description: "\(name.rawValue) republished")
            published.assertForOverFulfill = false
            let subscription = settings.objectWillChange.sink { published.fulfill() }
            center.post(name: name, object: nil)
            wait(for: [published], timeout: 2)
            subscription.cancel()
        }
    }

    func testIncreaseContrastRaisesTheDarkeningFloorButKeepsTheChoice() {
        XCTAssertEqual(Settings.effectiveScrim(0.2, increaseContrast: true), Settings.increasedContrastScrimFloor)
        XCTAssertEqual(Settings.effectiveScrim(0.2, increaseContrast: false), 0.2)
        XCTAssertEqual(Settings.effectiveScrim(1.8, increaseContrast: true), 1.8)

        let (settings, defaults) = isolatedSettings()
        settings.scrimStrength = 0.3
        XCTAssertEqual(settings.preferredScrimStrength, 0.3)
        XCTAssertEqual(defaults.object(forKey: "scrimStrength") as? Double, 0.3, "the stored choice is never the floor")
        XCTAssertEqual(settings.scrimStrength, Settings.effectiveScrim(0.3, increaseContrast: settings.increaseContrast))
        settings.resetAdvanced()
        XCTAssertTrue(settings.advancedIsDefault)
    }

    func testDarkeningSliderOnlyOffersWhatIsDrawn() {
        let floor = Settings.increasedContrastScrimFloor
        XCTAssertEqual(Settings.scrimRange(increaseContrast: false), 0...2)
        XCTAssertEqual(Settings.scrimRange(increaseContrast: true), floor...2)
        // Whatever was chosen, the slider shows the drawn strength inside its
        // range, so the thumb and the wallpaper never disagree.
        for preferred in stride(from: 0.0, through: 2.0, by: 0.25) {
            for increaseContrast in [false, true] {
                let shown = Settings.effectiveScrim(preferred, increaseContrast: increaseContrast)
                XCTAssertTrue(Settings.scrimRange(increaseContrast: increaseContrast).contains(shown), "\(preferred)")
            }
        }

        XCTAssertNil(WorkspaceCopy.increasedContrastDarkening(preferred: 0.3, increaseContrast: false))
        let held = WorkspaceCopy.increasedContrastDarkening(preferred: 0.3, increaseContrast: true)
        XCTAssertEqual(held?.contains("returns when it’s off"), true, "a lighter choice is kept and must say so")
        let strong = WorkspaceCopy.increasedContrastDarkening(preferred: 1.8, increaseContrast: true)
        XCTAssertNotNil(strong)
        XCTAssertEqual(strong?.contains("returns"), false, "nothing lighter is waiting")
    }

    func testIsolatedSettingsReadAndWriteOnlyTheirOwnSuite() {
        let (settings, defaults) = isolatedSettings { defaults in
            defaults.set(true, forKey: "custom.lowPowerVisuals")
            defaults.set("Imported.mov", forKey: "custom.wallpaperName")
            defaults.set(0.6, forKey: "custom.nextWordPreviewLead")
            defaults.set(["pop"], forKey: "custom.wordEntranceMix")
            defaults.set("original", forKey: "custom.light3DColours")
            defaults.set(1.7, forKey: "scrimStrength")
        }
        XCTAssertTrue(settings.lowPowerVisuals)
        XCTAssertEqual(settings.customWallpaperName, "Imported.mov")
        XCTAssertEqual(settings.nextWordPreviewLead, 0.6)
        XCTAssertEqual(settings.wordEntranceMix, ["pop"])
        XCTAssertEqual(settings.light3DColours, .original)
        XCTAssertEqual(settings.preferredScrimStrength, 1.7)
        settings.editorialAmount = 0.25
        XCTAssertEqual(defaults.object(forKey: "custom.editorialAmount") as? Double, 0.25)
    }
}
