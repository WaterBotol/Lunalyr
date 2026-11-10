import Foundation
import ServiceManagement
import XCTest
@testable import VersoCustom

/// The test run's own housekeeping: throwaway preference suites must not
/// pile up in the account's ~/Library/Preferences.
final class ThrowawaySuiteSweepTests: XCTestCase {
    private func folder() throws -> URL {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("SweepTests-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: folder) }
        return folder
    }

    private func write(_ values: [String: Any], named name: String, in folder: URL, modified: Date? = nil) throws {
        let file = folder.appendingPathComponent(name)
        try PropertyListSerialization.data(fromPropertyList: values, format: .binary, options: 0).write(to: file)
        if let modified { try FileManager.default.setAttributes([.modificationDate: modified], ofItemAtPath: file.path) }
    }

    func testOnlyTheTestsOwnSuiteFilesAreRecognised() {
        let uuid = UUID().uuidString
        for name in ["LunalyrTests.\(uuid).plist", "LunalyrTests.MusicVideoSync.\(uuid).plist",
                     "LunalyrUISnapshots-\(uuid).plist", "lunalyr.tests.fable.\(uuid).plist",
                     "MenuBarTests-\(uuid).plist", "LayoutRotationChoiceTests.\(uuid).plist"] {
            XCTAssertTrue(LunalyrTestEnvironment.isThrowawaySuiteFile(name), name)
        }
        for name in ["local.verso.custom.plist", "local.verso.custom.debug.plist", "LunalyrTests.plist",
                     "LunalyrTests.\(uuid).plist.lockfile", "com.apple.Music.plist", "LunalyrTests.notauuid.plist",
                     "SomeoneElse.\(uuid).plist", "xLunalyrTests.\(uuid).plist", "LunalyrTests.\(uuid.lowercased()).plist"] {
            XCTAssertFalse(LunalyrTestEnvironment.isThrowawaySuiteFile(name), name)
        }
    }

    /// Emptied suites go at once; one still holding values goes only once
    /// it is too old for any running test (another checkout's, say) to use.
    func testSweepRemovesEmptiedAndStaleSuitesOnly() throws {
        let folder = try folder()
        let now = Date()
        let emptied = "LunalyrTests.\(UUID().uuidString).plist"
        let inUse = "MatchTheMusicTests.\(UUID().uuidString).plist"
        let stale = "LayoutRotationChoiceTests.\(UUID().uuidString).plist"
        let unrelated = "com.example.app.plist"
        try write([:], named: emptied, in: folder)
        try write(["custom.rotateLayoutsCursor": 2], named: inUse, in: folder)
        try write(["custom.rotateLayoutCounts": ["echo": 0]], named: stale, in: folder, modified: now.addingTimeInterval(-7200))
        try write([:], named: unrelated, in: folder, modified: now.addingTimeInterval(-7200))

        XCTAssertEqual(LunalyrTestEnvironment.sweepThrowawaySuites(in: folder, now: now), 2)
        let left = Set(try FileManager.default.contentsOfDirectory(atPath: folder.path))
        XCTAssertEqual(left, [inUse, unrelated])
    }

    /// Suites a test can't clean up after live in the throwaway home.
    func testIsolatedDefaultsAreKeptInTheThrowawayHome() throws {
        let defaults = LunalyrTestEnvironment.isolatedDefaults("LunalyrTests")
        let key = "probe-\(UUID().uuidString)"
        defaults.set(7, forKey: key)
        XCTAssertEqual(defaults.integer(forKey: key), 7)
        XCTAssertTrue(defaults.synchronize())
        let mine = LunalyrTestEnvironment.isolatedPreferences
        let written = try FileManager.default.contentsOfDirectory(atPath: mine.path).filter { $0.hasPrefix("LunalyrTests.") }
        XCTAssertFalse(written.isEmpty, "the suite's file belongs in the throwaway home")
        let account = try XCTUnwrap(LunalyrTestEnvironment.accountHome).appendingPathComponent("Library/Preferences")
        for name in (try? FileManager.default.contentsOfDirectory(atPath: account.path)) ?? []
        where LunalyrTestEnvironment.isThrowawaySuiteFile(name) {
            let values = (try? Data(contentsOf: account.appendingPathComponent(name)))
                .flatMap { try? PropertyListSerialization.propertyList(from: $0, format: nil) as? [String: Any] }
            XCTAssertNil(values?[key], name)
        }
    }
}
