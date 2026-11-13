import XCTest
@testable import VersoCustom

/// The logo shown while the first Settings window builds. Nothing plays here.
@MainActor
final class SettingsLoaderTests: XCTestCase {
    func testShowsOncePerLaunchWithMotionAllowedOutsideTests() {
        XCTAssertTrue(SettingsLoader.shouldShow(reduceMotion: false, alreadyShown: false, isUnitTesting: false, hasVideo: true))
        XCTAssertFalse(SettingsLoader.shouldShow(reduceMotion: true, alreadyShown: false, isUnitTesting: false, hasVideo: true))
        XCTAssertFalse(SettingsLoader.shouldShow(reduceMotion: false, alreadyShown: true, isUnitTesting: false, hasVideo: true),
                       "only the first opening of a launch")
        XCTAssertFalse(SettingsLoader.shouldShow(reduceMotion: false, alreadyShown: false, isUnitTesting: true, hasVideo: true))
        XCTAssertFalse(SettingsLoader.shouldShow(reduceMotion: false, alreadyShown: false, isUnitTesting: false, hasVideo: false))
        XCTAssertFalse(SettingsLoader.shouldShow(alreadyShown: false), "never inside the unit-test host")
    }

    func testNeverAWaitOfItsOwnMaking() {
        XCTAssertEqual(SettingsLoader.remaining(after: 0), SettingsLoader.minimumShown)
        XCTAssertEqual(SettingsLoader.remaining(after: 0.5), SettingsLoader.minimumShown - 0.5, accuracy: 1e-9)
        XCTAssertEqual(SettingsLoader.remaining(after: 2), 0, "a slow build is not held up further")
        XCTAssertLessThanOrEqual(SettingsLoader.minimumShown, 1, "under a second on a fast Mac")
        XCTAssertGreaterThan(SettingsLoader.rate, 1, "faster than the welcome")
    }
}
