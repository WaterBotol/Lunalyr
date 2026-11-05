import Foundation
import XCTest

/// Keeps the unit tests away from the person's real Lunalyr state.
///
/// The tests run inside a Debug build of the app. Its preferences already
/// live in their own domain (`local.verso.custom.debug`, see project.yml), but
/// Application Support, Caches and the other home-relative folders would still
/// be the real ones: the lyric cache, imported wallpapers and downloaded fonts
/// all live there under names the installed app shares.
///
/// XCTest creates this class (the test bundle's `NSPrincipalClass`) before any
/// test runs. It points `CFFIXED_USER_HOME` at a fresh temporary folder, which
/// Foundation honours for `NSHomeDirectory()`, `homeDirectoryForCurrentUser`
/// and every user-domain search path from then on, and removes that folder
/// when the bundle finishes. Preferences are unaffected by it, which is why
/// the separate bundle identifier is still needed.
@objc(LunalyrTestEnvironment)
final class LunalyrTestEnvironment: NSObject, XCTestObservation {
    /// The installed app's identifier and preferences domain.
    static let releaseBundleIdentifier = "local.verso.custom"

    /// The throwaway home for this test run.
    static let home = FileManager.default.temporaryDirectory
        .appendingPathComponent("LunalyrTests-\(UUID().uuidString)", isDirectory: true)

    override init() {
        super.init()
        guard Bundle.main.bundleIdentifier != Self.releaseBundleIdentifier else {
            // Every Settings change a test makes would be written into the
            // installed app's preferences, so refuse to run at all.
            FileHandle.standardError.write(Data(("Refusing to run the unit tests inside "
                + "\(Self.releaseBundleIdentifier): they would change the installed app's "
                + "preferences. Test the Debug configuration (local.verso.custom.debug).\n").utf8))
            exit(EXIT_FAILURE)
        }
        try? FileManager.default.createDirectory(
            at: Self.home.appendingPathComponent("Library/Application Support", isDirectory: true),
            withIntermediateDirectories: true)
        setenv("CFFIXED_USER_HOME", Self.home.path, 1)
        XCTestObservationCenter.shared.addTestObserver(self)
    }

    func testBundleDidFinish(_ testBundle: Bundle) {
        try? FileManager.default.removeItem(at: Self.home)
    }

    /// The account's actual home folder, which the redirect does not change.
    static var accountHome: URL? {
        guard let entry = getpwuid(getuid()), let directory = entry.pointee.pw_dir else { return nil }
        return URL(fileURLWithPath: String(cString: directory), isDirectory: true)
    }

    /// Whether `url` is the throwaway home or lies inside it.
    static func contains(_ url: URL) -> Bool {
        let root = canonicalPath(home), path = canonicalPath(url)
        return path == root || path.hasPrefix(root + "/")
    }

    /// `/var` and `/tmp` are links into `/private`; either spelling may come back.
    static func canonicalPath(_ url: URL) -> String {
        let path = url.standardizedFileURL.path
        for prefix in ["/private/var/", "/private/tmp/"] where path.hasPrefix(prefix) {
            return String(path.dropFirst("/private".count))
        }
        return path
    }
}
