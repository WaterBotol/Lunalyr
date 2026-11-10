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
///
/// Throwaway preference suites (`UserDefaults(suiteName:)`) are files in the
/// account's own ~/Library/Preferences, and `removePersistentDomain` empties
/// one but leaves its file behind: thousands of them piled up over many
/// runs. Tests that can't clean up after themselves take a suite from
/// `isolatedDefaults(_:)`, kept inside the throwaway home; the rest are swept
/// up as the bundle starts and finishes (`sweepThrowawaySuites`).
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
        Self.sweepThrowawaySuites()
    }

    func testBundleDidFinish(_ testBundle: Bundle) {
        Self.sweepThrowawaySuites()
        try? FileManager.default.removeItem(at: Self.home)
    }

    // MARK: - Throwaway preference suites

    /// A preferences suite of the test's own, stored as a file inside the
    /// throwaway home (a suite named by an absolute path is kept at that
    /// path), so it disappears with the home and never needs cleaning up.
    /// Not under the home's Library/Preferences: a path ending that way is
    /// taken for the account's own preferences folder.
    static let isolatedPreferences = home.appendingPathComponent("TestPreferences", isDirectory: true)

    static func isolatedDefaults(_ label: String) -> UserDefaults {
        let folder = isolatedPreferences
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        return UserDefaults(suiteName: folder.appendingPathComponent("\(label).\(UUID().uuidString)").path)!
    }

    /// The names the tests give their suites, each followed by `.` or `-`
    /// and a UUID. Only files named exactly so are ever removed.
    static let throwawaySuitePrefixes = [
        "LunalyrTests", "LunalyrUISnapshots", "LunalyrOnboardingTests", "LayoutRotationChoiceTests",
        "MatchTheMusicTests", "WebVisualizerPlacementTests", "StarTrailsTests", "MenuBarTests",
        "LyrimuseFolderTests", "lunalyr.tests.fable", "MachineTranslationTests",
    ]

    /// Whether `fileName` is a throwaway suite's file: a known prefix,
    /// optionally a dotted word or two (`LunalyrTests.MusicVideo`), then a UUID.
    static func isThrowawaySuiteFile(_ fileName: String) -> Bool {
        let uuid = "[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}"
        let prefixes = throwawaySuitePrefixes.map(NSRegularExpression.escapedPattern(for:)).joined(separator: "|")
        let pattern = "^(?:\(prefixes))(?:\\.[A-Za-z]+)*[.-]\(uuid)\\.plist$"
        return fileName.range(of: pattern, options: .regularExpression) != nil
    }

    /// Removes throwaway suite files from `folder` (the account's
    /// ~/Library/Preferences by default): every empty one, which a test has
    /// already cleared, and any older than `staleAfter`, which no test still
    /// running can be using. Tests in other checkouts may run at the same
    /// time, so a recent suite that still holds values is left alone.
    @discardableResult
    static func sweepThrowawaySuites(in folder: URL? = accountHome?.appendingPathComponent("Library/Preferences"),
                                     staleAfter: TimeInterval = 3600, now: Date = Date()) -> Int {
        guard let folder,
              let names = try? FileManager.default.contentsOfDirectory(atPath: folder.path) else { return 0 }
        var removed = 0
        for name in names where isThrowawaySuiteFile(name) {
            let file = folder.appendingPathComponent(name)
            let modified = (try? file.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate
            let stale = modified.map { now.timeIntervalSince($0) > staleAfter } ?? false
            let empty = (try? Data(contentsOf: file))
                .flatMap { try? PropertyListSerialization.propertyList(from: $0, format: nil) as? [String: Any] }?
                .isEmpty == true
            guard empty || stale else { continue }
            if (try? FileManager.default.removeItem(at: file)) != nil { removed += 1 }
        }
        return removed
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
