import AppKit
import CoreText
import SwiftUI

/// The font families installed on this Mac that can set lyrics: hidden
/// system families (".SF NS…") and symbol or dingbat fonts that can't draw
/// Latin letters are left out, as are the families Lunalyr registered for
/// itself (downloaded Google Fonts). Read once when first shown, again on
/// "Find new fonts", and whenever macOS says the installed fonts changed.
@MainActor
final class InstalledFontLibrary: ObservableObject {
    static let shared = InstalledFontLibrary()

    /// Posted when fonts are installed or removed (AppKit's and CoreText's).
    static let changeNotifications: [Notification.Name] = [
        NSFont.fontSetChangedNotification,
        Notification.Name(kCTFontManagerRegisteredFontsChangedNotification as String),
    ]

    @Published private(set) var families: [String] = []
    /// Whether the list has been read at least once.
    @Published private(set) var hasLoaded = false

    private let source: @MainActor () -> [String]
    private let isUsable: @Sendable (String) -> Bool
    private let notifications: NotificationCenter
    private var observers: [NSObjectProtocol] = []
    private var pendingRefresh: Task<Void, Never>?

    /// Tests pass their own families and filter, and a private centre.
    init(source: @escaping @MainActor () -> [String] = InstalledFontLibrary.systemFamilies,
         isUsable: @escaping @Sendable (String) -> Bool = InstalledFontLibrary.isListable,
         notifications: NotificationCenter = .default) {
        self.source = source
        self.isUsable = isUsable
        self.notifications = notifications
        for name in Self.changeNotifications {
            observers.append(notifications.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.scheduleRefresh() }
            })
        }
    }

    deinit {
        observers.forEach(notifications.removeObserver)
    }

    /// Reads the list the first time it is needed.
    func loadIfNeeded() {
        guard !hasLoaded, pendingRefresh == nil else { return }
        scheduleRefresh(delay: 0)
    }

    /// Rereads the installed families and returns how many are new since
    /// the last reading (0 on the first).
    @discardableResult
    func refresh() async -> Int {
        let raw = source()
        let isUsable = isUsable
        let usable = await Task.detached(priority: .userInitiated) {
            Self.usableFamilies(raw, isUsable: isUsable)
        }.value
        let added = hasLoaded ? Self.newFamilies(old: families, new: usable).count : 0
        if usable != families { families = usable }
        hasLoaded = true
        return added
    }

    /// A burst of change notifications (one per font file) reads the list
    /// once, shortly after the last.
    private func scheduleRefresh(delay: Double = 0.4) {
        pendingRefresh?.cancel()
        pendingRefresh = Task { [weak self] in
            if delay > 0 { try? await Task.sleep(for: .seconds(delay)) }
            guard !Task.isCancelled, let self else { return }
            await self.refresh()
            self.pendingRefresh = nil
        }
    }

    // MARK: Pure parts

    /// Visible, usable families, once each, in Finder's order.
    nonisolated static func usableFamilies(_ raw: [String], isUsable: (String) -> Bool) -> [String] {
        var seen = Set<String>()
        return raw.filter { family in
            !family.isEmpty && !family.hasPrefix(".") && seen.insert(family).inserted && isUsable(family)
        }
        .sorted { $0.localizedStandardCompare($1) == .orderedAscending }
    }

    /// Families in `new` that `old` didn't have.
    nonisolated static func newFamilies(old: [String], new: [String]) -> [String] {
        let before = Set(old)
        return new.filter { !before.contains($0) }
    }

    /// The families whose names contain `query`, ignoring case and accents.
    nonisolated static func filter(_ families: [String], matching query: String) -> [String] {
        let query = query.trimmingCharacters(in: .whitespaces)
        guard !query.isEmpty else { return families }
        return families.filter { $0.range(of: query, options: [.caseInsensitive, .diacriticInsensitive]) != nil }
    }

    /// What "Find new fonts" says afterwards.
    nonisolated static func foundMessage(_ count: Int) -> String {
        switch count {
        case 0: return "No new fonts found."
        case 1: return "Found 1 new family."
        default: return "Found \(count) new families."
        }
    }

    /// Whether the family can set ordinary Latin text: not a symbol face
    /// (Wingdings, Zapf Dingbats, Bodoni Ornaments), and it has letters.
    nonisolated static func drawsLatin(_ family: String) -> Bool {
        let descriptor = CTFontDescriptorCreateWithAttributes([kCTFontFamilyNameAttribute: family] as CFDictionary)
        let font = CTFontCreateWithFontDescriptor(descriptor, 12, nil)
        // CoreText substitutes a default face for a name it doesn't know.
        guard (CTFontCopyFamilyName(font) as String) == family else { return false }
        let style = CTFontGetSymbolicTraits(font).rawValue & CTFontSymbolicTraits.traitClassMask.rawValue
        if CTFontStylisticClass(rawValue: style) == .classSymbolic { return false }
        let covered = CTFontCopyCharacterSet(font) as CharacterSet
        return CharacterSet(charactersIn: "AaBbEeGgRrSs").isSubset(of: covered)
    }

    /// Every family AppKit lists.
    static func systemFamilies() -> [String] {
        NSFontManager.shared.availableFontFamilies
    }

    /// Draws Latin text and is installed on the Mac, not merely registered
    /// for Lunalyr (a downloaded Google family).
    nonisolated static func isListable(_ family: String) -> Bool {
        guard drawsLatin(family) else { return false }
        let descriptor = CTFontDescriptorCreateWithAttributes([kCTFontFamilyNameAttribute: family] as CFDictionary)
        let font = CTFontCreateWithFontDescriptor(descriptor, 12, nil)
        let scope = CTFontCopyAttribute(font, kCTFontRegistrationScopeAttribute) as? NSNumber
        return scope?.uint32Value != CTFontManagerScope.process.rawValue
    }
}

/// Typeface › Installed font: the chosen family, a searchable list of the
/// families on this Mac (each drawn in itself, built lazily so hundreds stay
/// quick), and "Find new fonts". A family that has since been uninstalled
/// says so, while the lyrics use the default typeface.
@MainActor
struct InstalledFontPicker: View {
    @Model(\.settings) private var settings
    @ObservedObject private var library: InstalledFontLibrary
    @State private var open: Bool
    @State private var query = ""
    @State private var found: String?
    @State private var finding = false
    @State private var foundClear: Task<Void, Never>?

    static let listHeight: CGFloat = 196
    static let rowHeight: CGFloat = 26

    init(library: InstalledFontLibrary? = nil, open: Bool = false) {
        self.library = library ?? .shared
        _open = State(initialValue: open)
    }

    private var family: String { settings.installedFontFamily }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            chosenRow
            if settings.installedFontMissing {
                WarningCaption("“\(family)” isn’t installed any more, so the lyrics use Manrope, the default. Reinstall it or pick another.")
                    .revealRow()
            }
            if open || family.isEmpty {
                search
                list
                    .revealRow()
            }
            HStack(spacing: LunalyrDesign.Spacing.small) {
                Button("Find new fonts", action: findNewFonts)
                    .disabled(finding)
                    .help("Look again for fonts installed since Lunalyr opened")
                if let found {
                    Text(found)
                        .lunalyrText(.caption)
                        .lineLimit(1)
                        .transition(.opacity)
                }
            }
            InspectorCaption("Fonts installed on this Mac.",
                             more: "Every font family installed on this Mac that can set lyrics. Install a font in Font Book, then Find new fonts.")
        }
        .onAppear { library.loadIfNeeded() }
        .animation(.easeOut(duration: 0.2), value: found)
    }

    /// The family in use, in its own face, and the switch for the list.
    private var chosenRow: some View {
        Button {
            withAnimation(.easeOut(duration: 0.2)) { open.toggle() }
        } label: {
            HStack(spacing: 6) {
                Text(family.isEmpty ? "Choose a family" : family)
                    .font(family.isEmpty || settings.installedFontMissing ? .system(size: 13) : Self.specimen(family))
                    .foregroundStyle(Color.studio(family.isEmpty ? .ink2 : .ink))
                    .lineLimit(1)
                    .truncationMode(.tail)
                Spacer(minLength: 4)
                Image(systemName: "chevron.down")
                    .font(.system(size: 10, weight: .semibold))
                    .foregroundStyle(Color.studio(.ink2))
                    .rotationEffect(.degrees(open || family.isEmpty ? 180 : 0))
                    .accessibilityHidden(true)
            }
            .padding(.horizontal, 8)
            .frame(height: 28)
            .frame(maxWidth: .infinity)
            .clipped()
            .background(RoundedRectangle(cornerRadius: 6, style: .continuous).fill(Color.studio(.control)))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel("Installed family")
        .accessibilityValue(family.isEmpty ? "None" : family)
        .accessibilityHint(open ? "Hides the list of installed fonts" : "Shows the list of installed fonts")
    }

    private var search: some View {
        HStack(spacing: 6) {
            Image(systemName: "magnifyingglass")
                .font(.system(size: 11))
                .foregroundStyle(Color.studio(.ink2))
                .accessibilityHidden(true)
            TextField("Search installed fonts", text: $query,
                      prompt: Text("Search \(library.families.count) fonts").foregroundStyle(Color.studio(.ink2)))
                .textFieldStyle(.plain)
                .font(.system(size: 12))
            if !query.isEmpty {
                Button { query = "" } label: { Image(systemName: "xmark.circle.fill") }
                    .buttonStyle(.plain)
                    .foregroundStyle(Color.studio(.ink2))
                    .accessibilityLabel("Clear search")
            }
        }
        .padding(.horizontal, 8)
        .frame(height: 26)
        .background(RoundedRectangle(cornerRadius: 6, style: .continuous).fill(Color.studio(.searchFill)))
    }

    private var list: some View {
        let shown = InstalledFontLibrary.filter(library.families, matching: query)
        return ScrollViewReader { proxy in
            ScrollView(.vertical) {
                LazyVStack(alignment: .leading, spacing: 0) {
                    ForEach(shown, id: \.self) { name in row(name).id(name) }
                }
                .padding(.vertical, 3)
            }
            .overlay {
                if shown.isEmpty {
                    Text(!library.hasLoaded ? "Reading fonts…" : query.isEmpty ? "No fonts found." : "No fonts match “\(query)”.")
                        .lunalyrText(.caption)
                        .multilineTextAlignment(.center)
                        .padding(.horizontal, 12)
                }
            }
            .onAppear { if !family.isEmpty { proxy.scrollTo(family, anchor: .center) } }
            .onChange(of: library.hasLoaded) { _, _ in
                if !family.isEmpty { proxy.scrollTo(family, anchor: .center) }
            }
        }
        .frame(height: Self.listHeight)
        .frame(maxWidth: .infinity)
        .background(RoundedRectangle(cornerRadius: 6, style: .continuous).fill(Color.studio(.control).opacity(0.5)))
        .overlay(RoundedRectangle(cornerRadius: 6, style: .continuous)
            .strokeBorder(Color.studio(.separator), lineWidth: 1))
        .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Installed fonts")
    }

    private func row(_ name: String) -> some View {
        let chosen = name == family && !settings.installedFontMissing
        return Button {
            settings.applyInstalledFont(family: name)
        } label: {
            HStack(spacing: 6) {
                Text(name)
                    .font(Self.specimen(name))
                    .foregroundStyle(Color.studio(.ink))
                    .lineLimit(1)
                    .truncationMode(.tail)
                Spacer(minLength: 2)
                if chosen {
                    Image(systemName: "checkmark")
                        .font(.system(size: 10, weight: .bold))
                        .foregroundStyle(Color.studio(.accent))
                        .accessibilityHidden(true)
                }
            }
            .padding(.horizontal, 8)
            .frame(height: Self.rowHeight)
            .frame(maxWidth: .infinity, alignment: .leading)
            .clipped()
            .background(chosen ? Color.studio(.accent).opacity(0.14) : Color.clear)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help(name)
        .accessibilityLabel(name)
        .accessibilityAddTraits(chosen ? [.isButton, .isSelected] : .isButton)
    }

    /// The family's regular face at list size, or the system face if
    /// AppKit can't make one.
    static func specimen(_ family: String) -> Font {
        if let font = NSFontManager.shared.font(withFamily: family, traits: [], weight: 5, size: 13) {
            return Font(font)
        }
        return .system(size: 13)
    }

    private func findNewFonts() {
        finding = true
        foundClear?.cancel()
        Task {
            let count = await library.refresh()
            settings.resolveInstalledFont()
            finding = false
            found = InstalledFontLibrary.foundMessage(count)
            foundClear = Task {
                try? await Task.sleep(for: .seconds(4))
                guard !Task.isCancelled else { return }
                found = nil
            }
        }
    }
}
