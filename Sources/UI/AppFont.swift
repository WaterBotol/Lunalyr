import SwiftUI

/// Manrope, used for every piece of interface text and as the default lyric
/// face. It is fetched from Google Fonts once and registered for this process
/// only, exactly like a user-chosen family -- so nothing is installed on the
/// system and there is no licensing baggage in the bundle.
///
/// Until it arrives, calls fall back to the system font at the same size, so
/// the first frames after launch are laid out sensibly rather than blank.
@MainActor
enum AppFont {
    private(set) static var regular: String?
    private(set) static var bold: String?

    static func ui(_ size: CGFloat, _ weight: Font.Weight = .regular) -> Font {
        let wantsBold: Bool
        switch weight {
        case .semibold, .bold, .heavy, .black: wantsBold = true
        default: wantsBold = false
        }
        if let name = wantsBold ? (bold ?? regular) : regular {
            return .custom(name, fixedSize: size)
        }
        return .system(size: size, weight: weight)
    }

    /// Display type -- the headings that carry the settings and onboarding.
    static func display(_ size: CGFloat) -> Font {
        if let name = bold ?? regular { return .custom(name, fixedSize: size) }
        return .system(size: size, weight: .bold)
    }

    private static var retry: Task<Void, Never>?
    private static var filesObserver: NSObjectProtocol?

    /// Uses Manrope if it is installed on this Mac; otherwise fetches it.
    /// Offline at launch, it keeps trying (backing off to every ten
    /// minutes), and it tries again at once whenever cached font files are
    /// announced, so the default face is not lost for the whole session.
    static func preload() {
        if filesObserver == nil {
            filesObserver = NotificationCenter.default.addObserver(forName: .fontFilesAvailable, object: nil, queue: .main) { _ in
                Task { @MainActor in AppFont.load(attempt: 0) }
            }
        }
        load(attempt: 0)
    }

    private static func load(attempt: Int) {
        guard regular == nil else { return }
        if let local = installed(family: "Manrope") {
            apply(regular: local.regular, bold: local.bold)
            return
        }
        retry?.cancel()
        retry = Task {
            if let font = await GoogleFontsProvider.shared.font(family: "Manrope") {
                apply(regular: font.regular, bold: font.bold)
                return
            }
            let delay = min(600, 15 * pow(2, Double(attempt)))
            try? await Task.sleep(nanoseconds: UInt64(delay * 1_000_000_000))
            guard !Task.isCancelled else { return }
            load(attempt: attempt + 1)
        }
    }

    private static func apply(regular name: String, bold heavy: String?) {
        regular = name
        bold = heavy
        retry?.cancel()
        retry = nil
        // Nudge the interface so already-rendered text picks up the face.
        NotificationCenter.default.post(name: .appFontLoaded, object: nil)
    }

    /// A family installed on this Mac -- including licensed fonts the user
    /// owns -- as the PostScript names of its upright regular and bold
    /// cuts. Nil when the family is not installed.
    static func installed(family: String) -> (regular: String, bold: String?)? {
        let typed = family.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !typed.isEmpty else { return nil }
        // Families are matched case-insensitively, as typed in Settings.
        let name = NSFontManager.shared.availableFontFamilies
            .first { $0.caseInsensitiveCompare(typed) == .orderedSame } ?? typed
        guard let members = NSFontManager.shared.availableMembers(ofFontFamily: name),
              !members.isEmpty else { return nil }
        // Each member: PostScript name, style name, weight (5 regular,
        // 9 bold on AppKit's 0-15 scale), traits.
        let upright = members.compactMap { member -> (name: String, weight: Int)? in
            guard member.count >= 4, let postScript = member[0] as? String,
                  let weight = (member[2] as? NSNumber)?.intValue,
                  let traits = (member[3] as? NSNumber)?.uintValue else { return nil }
            return NSFontTraitMask(rawValue: traits).contains(.italicFontMask) ? nil : (postScript, weight)
        }
        func nearest(_ target: Int) -> String? { upright.min { abs($0.weight - target) < abs($1.weight - target) }?.name }
        guard let regular = nearest(5) else { return nil }
        let bold = nearest(9)
        return (regular, bold == regular ? nil : bold)
    }
}

extension Notification.Name {
    static let appFontLoaded = Notification.Name("LunalyrAppFontLoaded")
    /// Post when downloaded font files have been cached on disk, so a face
    /// that could not load earlier (offline at launch) is tried again.
    static let fontFilesAvailable = Notification.Name("LunalyrFontFilesAvailable")
}

/// Redraws its content once Manrope finishes downloading.
struct AppFontScope<Content: View>: View {
    @State private var revision = 0
    @ViewBuilder var content: Content

    var body: some View {
        content
            .id(revision)
            .onReceive(NotificationCenter.default.publisher(for: .appFontLoaded)) { _ in
                revision += 1
            }
    }
}
