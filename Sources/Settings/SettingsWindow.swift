import AppKit
import SwiftUI

/// Preferences. Each pane gives its main idea the room -- the sample on the
/// typeface pane, the preview on the background pane -- instead of stacking
/// equal-weight cards down the middle.
@MainActor
struct SettingsWindowView: View {
    @ObservedObject private var licensing = LicenseManager.shared
    @State private var tab: Tab = .composition

    enum Tab: String, CaseIterable, Identifiable {
        case typeface, composition, background, lyrics, romanization, appleMusic, behaviour, licence
        var id: String { rawValue }

        /// The licence pane only exists while the paid tier does.
        @MainActor static var visible: [Tab] {
            LicenseManager.paywallEnabled ? allCases : allCases.filter { $0 != .licence }
        }

        var title: String {
            switch self {
            case .typeface: return "Typeface"
            case .composition: return "Composition"
            case .background: return "Background"
            case .lyrics: return "Lyric sources"
            case .romanization: return "Romanisation"
            case .appleMusic: return "Apple Music"
            case .behaviour: return "Behaviour"
            case .licence: return "Licence"
            }
        }

        var symbol: String {
            switch self {
            case .typeface: return "textformat"
            case .composition: return "square.stack.3d.up"
            case .background: return "sparkles"
            case .lyrics: return "text.magnifyingglass"
            case .romanization: return "character.book.closed"
            case .appleMusic: return "music.note.tv"
            case .behaviour: return "slider.horizontal.3"
            case .licence: return "checkmark.seal"
            }
        }
    }

    var body: some View {
        NavigationSplitView {
            List(Tab.visible, selection: $tab) { item in
                Label(item.title, systemImage: item.symbol).tag(item)
            }
            .navigationTitle("Lunalyr")
            .navigationSplitViewColumnWidth(min: 155, ideal: 175, max: 210)
            .safeAreaInset(edge: .bottom) { signature }
        } detail: {
            VStack(spacing: 0) {
                SettingsLivePreview().frame(height: 185)
                Divider()
                pane.frame(maxWidth: .infinity, maxHeight: .infinity)
            }
            .navigationTitle(tab.title)
        }
        .frame(minWidth: 760, minHeight: 640)
    }

    /// Sits at the foot of the sidebar, quiet enough to be found rather than
    /// noticed. The version comes from the bundle, so it cannot drift out of
    /// step with what was actually shipped.
    private var signature: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(spacing: 4) {
                Text("Built with")
                    .font(.system(size: 10.5, weight: .medium))
                Image(systemName: "heart.fill")
                    .font(.system(size: 8))
                    .foregroundStyle(Verso.violet.opacity(0.9))
            }
            Text("Lunalyr \(Self.version)")
                .font(.system(size: 9.5))
                .foregroundStyle(Verso.cream.opacity(0.20))
        }
        .foregroundStyle(Verso.cream.opacity(0.40))
        .padding(.horizontal, 22)
        .padding(.bottom, LicenseManager.paywallEnabled ? 12 : 20)
    }

    private static let version: String =
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.1.0"

    // MARK: - Panes

    @ViewBuilder
    private var pane: some View {
        switch tab {
        case .typeface: TypefacePane()
        case .composition: CompositionPane()
        case .background: BackgroundPane()
        case .lyrics: LyricSourcesPane()
        case .romanization: RomanizationPane()
        case .appleMusic: AppleMusicPane()
        case .behaviour: BehaviourPane()
        case .licence: LicensePane()
        }
    }
}

// MARK: - Window

/// Presents the settings in its own window. Accessory apps have no ordinary
/// window, so this is managed by hand.
@MainActor
final class SettingsWindowController: NSObject {
    static let shared = SettingsWindowController()
    private var window: NSWindow?

    func present() {
        // Verso is an accessory app -- no Dock icon, since the wallpaper is
        // the interface. But an accessory app's window cannot become properly
        // key, so Settings would open without a working menu bar and could be
        // lost behind other windows with no way to bring it back. Becoming a
        // regular app for as long as Settings is open fixes both, and the Dock
        // icon gives somewhere to click back to.
        NSApp.setActivationPolicy(.regular)

        if let window {
            window.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps: true)
            return
        }

        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 900, height: 740),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false
        )
        window.title = "Lunalyr"
        window.minSize = NSSize(width: 760, height: 640)
        window.titlebarAppearsTransparent = false
        window.titleVisibility = .visible
        window.isMovableByWindowBackground = true
        window.backgroundColor = NSColor(Verso.surface)
        // Created in code, so it would otherwise be released on close while
        // this controller still points at it -- an over-release crash.
        window.isReleasedWhenClosed = false
        window.center()
        window.contentView = NSHostingView(rootView: SettingsWindowView())
        window.delegate = self
        window.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
        self.window = window
    }
}

extension SettingsWindowController: NSWindowDelegate {
    /// Back to an accessory app once Settings goes away, so the Dock icon and
    /// menu bar do not linger for an app with nothing left on screen.
    func windowWillClose(_ notification: Notification) {
        guard let closing = notification.object as? NSWindow else { return }
        // Drop the hosting view after the close event so preview timelines
        // and audio-meter subscriptions do not stay alive in a closed window.
        DispatchQueue.main.async { [weak self] in
            guard let self, self.window === closing, !closing.isVisible else { return }
            closing.contentView = nil
            self.window = nil
            NSApp.setActivationPolicy(.accessory)
        }
    }
}

/// Verso's wordmark, from the app icon with its background keyed out.
///
/// Loaded through `NSImage` rather than `Image("name")`: the latter resolves
/// against asset catalogs, and this ships as a loose bundle resource, so the
/// SwiftUI form silently renders nothing.
struct Wordmark: View {
    var width: CGFloat = 84

    var body: some View {
        Text("Lunalyr")
            .font(.system(size: width * 0.25, weight: .semibold, design: .rounded))
            .foregroundStyle(.primary)
            .accessibilityLabel("Lunalyr")
    }
}

// MARK: - Shared pane chrome

struct PaneHeader: View {
    let title: String
    let subtitle: String

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title)
                .font(AppFont.display(30))
                .foregroundStyle(Verso.brandGradient)
            Text(subtitle)
                .font(AppFont.ui(12.5))
                .foregroundStyle(Verso.cream.opacity(0.52))
                .fixedSize(horizontal: false, vertical: true)
        }
    }
}

/// Pill selector used across the panes instead of stock menu pickers.
struct ChipRow<Item: Identifiable & Equatable>: View {
    let items: [Item]
    let title: (Item) -> String
    @Binding var selection: Item

    var body: some View {
        HStack(spacing: 6) {
            ForEach(items) { item in
                Button { selection = item } label: {
                    Text(title(item))
                        .font(AppFont.ui(11.5, selection == item ? .semibold : .regular))
                        .padding(.horizontal, 12)
                        .padding(.vertical, 6)
                        .background(Capsule().fill(selection == item ? .white : .white.opacity(0.10)))
                        .foregroundStyle(selection == item ? .black : .white.opacity(0.78))
                }
                .buttonStyle(.plain)
            }
        }
    }
}
