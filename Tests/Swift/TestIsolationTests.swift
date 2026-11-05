import XCTest

/// Guards the separation between the test run and the installed app.
final class TestIsolationTests: XCTestCase {
    func testHostUsesTheDebugPreferencesDomain() {
        // UserDefaults.standard belongs to the host app's bundle identifier.
        XCTAssertEqual(Bundle.main.bundleIdentifier, "local.verso.custom.debug")
        XCTAssertNotEqual(Bundle.main.bundleIdentifier, LunalyrTestEnvironment.releaseBundleIdentifier)
    }

    func testHomeRelativeFoldersResolveInsideTheThrowawayHome() throws {
        let manager = FileManager.default
        let support = try XCTUnwrap(manager.urls(for: .applicationSupportDirectory, in: .userDomainMask).first)
        let caches = try XCTUnwrap(manager.urls(for: .cachesDirectory, in: .userDomainMask).first)
        for url in [support, caches, manager.homeDirectoryForCurrentUser,
                    URL(fileURLWithPath: NSHomeDirectory(), isDirectory: true)] {
            XCTAssertTrue(LunalyrTestEnvironment.contains(url), "\(url.path) is outside the test home")
        }
        let accountHome = try XCTUnwrap(LunalyrTestEnvironment.accountHome)
        XCTAssertFalse(LunalyrTestEnvironment.contains(accountHome))
        XCTAssertFalse(LunalyrTestEnvironment.canonicalPath(support)
            .hasPrefix(LunalyrTestEnvironment.canonicalPath(accountHome) + "/Library/"))
    }
}
