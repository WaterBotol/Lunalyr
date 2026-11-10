import AppKit
import SwiftUI

/// The line under the seek bar: the player switch on the left, transport in
/// the middle, the search on the right. 680 × 32 pt.
struct PlayerControlsRow: View {
    static let width: CGFloat = 680
    static let height: CGFloat = 32

    let kinds: [PlayerKind]
    let selected: PlayerKind
    let onSelect: (PlayerKind) -> Void
    let canControl: Bool
    let hasSong: Bool
    let isPlaying: Bool
    let onPrevious: () -> Void
    let onPlayPause: () -> Void
    let onNext: () -> Void
    @Binding var query: String
    var searchFocused: FocusState<Bool>.Binding
    let onSubmit: () -> Void
    @Environment(\.playerLook) private var look
    @Environment(\.playerPreview) private var preview

    var body: some View {
        ZStack {
            HStack(spacing: 16) {
                playerSwitch
                Spacer(minLength: 0)
                searchField
            }
            transport
        }
        .shadow(color: .black.opacity(0.5), radius: 4)
        .frame(width: Self.width, height: Self.height)
    }

    // MARK: Transport

    /// Previous 36, play 44, next 36 pt wide, touching: the hover highlight
    /// never flickers between buttons and there is no dead gap to miss.
    private var transport: some View {
        HStack(spacing: 0) {
            Button(action: onPrevious) { glyph("backward.fill", 13).frame(width: 36, height: Self.height) }
                .buttonStyle(TransportButtonStyle())
                .opacity(hasSong ? 1 : 0.3)
                .disabled(!hasSong)
                .help("Previous").accessibilityLabel("Previous track")
            Button(action: onPlayPause) {
                Image(systemName: isPlaying ? "pause.fill" : "play.fill")
                    .font(.system(size: 17, weight: .semibold))
                    .contentTransition(look.reduceMotion ? .opacity : .symbolEffect(.replace))
                    .frame(width: 44, height: Self.height)
                    .contentShape(Rectangle())
            }
            .buttonStyle(TransportButtonStyle())
            .help(isPlaying ? "Pause" : "Play").accessibilityLabel(isPlaying ? "Pause" : "Play")
            Button(action: onNext) { glyph("forward.fill", 13).frame(width: 36, height: Self.height) }
                .buttonStyle(TransportButtonStyle())
                .opacity(hasSong ? 1 : 0.3)
                .disabled(!hasSong)
                .help("Next").accessibilityLabel("Next track")
        }
        .disabled(!canControl)
    }

    private func glyph(_ symbol: String, _ size: CGFloat) -> some View {
        Image(systemName: symbol).font(.system(size: size, weight: .semibold)).contentShape(Rectangle())
    }

    // MARK: Switch

    /// Both players side by side; the one in use is lit in its own colour.
    private var playerSwitch: some View {
        HStack(spacing: 14) {
            ForEach(kinds) { kind in
                let on = kind == selected
                Button { onSelect(kind) } label: {
                    VStack(spacing: 3) {
                        Text(kind.shortName).font(.system(size: 11, weight: on ? .bold : .medium))
                            .foregroundStyle(on ? kind.tint : .white.opacity(look.increaseContrast ? 0.85 : 0.55))
                        Capsule().fill(on ? kind.tint : .clear).frame(height: 2)
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .help(kind.isBrowser ? "\(kind.shortName) in \(kind.shortBrowserName)" : "Search and play in \(kind.shortName)")
            }
        }
        .fixedSize()
        .animation(.easeOut(duration: 0.2), value: selected)
    }

    // MARK: Search

    /// A small field with a hairline under it, not a pill.
    private var searchField: some View {
        VStack(spacing: 3) {
            HStack(spacing: 5) {
                Image(systemName: "magnifyingglass").font(.system(size: 11)).foregroundStyle(.white.opacity(0.7))
                if preview != nil {
                    Text(query.isEmpty ? "Search" : query).font(.system(size: 12))
                        .foregroundStyle(.white.opacity(query.isEmpty ? 0.45 : 1))
                        .frame(maxWidth: .infinity, alignment: .leading)
                } else {
                    TextField("Search", text: $query)
                        .textFieldStyle(.plain).font(.system(size: 12))
                        .focused(searchFocused)
                        .onSubmit(onSubmit)
                }
                if !query.isEmpty {
                    Button { query = "" } label: {
                        Image(systemName: "xmark.circle.fill").foregroundStyle(.white.opacity(0.5))
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel("Clear search")
                }
            }
            Rectangle().fill(searchFocused.wrappedValue || (preview != nil && !query.isEmpty) ? selected.tint : .white.opacity(0.3))
                .frame(height: 1)
        }
        .frame(width: 170)
    }
}

/// Highlight on mouse-down, a quick press-in, and a release with a little
/// momentum (the one bounce in the player). A faint circle on hover.
struct TransportButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        TransportButtonBody(configuration: configuration)
    }
}

private struct TransportButtonBody: View {
    let configuration: ButtonStyleConfiguration
    @State private var hovering = false
    @Environment(\.isEnabled) private var enabled
    @Environment(\.playerLook) private var look
    @Environment(\.playerPreview) private var preview
    @Environment(\.playerMaskRender) private var maskRender

    var body: some View {
        let pressed = configuration.isPressed
        configuration.label
            .background {
                Circle().fill(.white.opacity(pressed ? 0.14 : hovering && enabled ? 0.08 : 0))
                    .frame(width: 30, height: 30)
                    .animation(.easeOut(duration: 0.10), value: hovering)
            }
            .scaleEffect(pressed && !look.reduceMotion ? 0.88 : 1)
            .animation(look.reduceMotion ? nil
                       : pressed ? .spring(duration: 0.10, bounce: 0) : .spring(duration: 0.30, bounce: 0.15),
                       value: pressed)
            .background { if preview == nil && !maskRender { PointerHover { hovering = $0 } } }
            .onChange(of: pressed) { _, down in if down { PlayerInteraction.shared.noteClick() } }
    }
}

/// Hover that also works while Lunalyr is in the background and its panel
/// is not key: an AppKit tracking area with `.activeAlways`. It takes no
/// clicks (it is invisible to hit testing).
struct PointerHover: NSViewRepresentable {
    let changed: (Bool) -> Void

    func makeNSView(context: Context) -> PointerHoverView {
        let view = PointerHoverView()
        view.changed = changed
        return view
    }

    func updateNSView(_ view: PointerHoverView, context: Context) { view.changed = changed }

    final class PointerHoverView: NSView {
        var changed: ((Bool) -> Void)?
        private var tracking: NSTrackingArea?
        private var inside = false

        override func hitTest(_ point: NSPoint) -> NSView? { nil }

        override func updateTrackingAreas() {
            super.updateTrackingAreas()
            if let tracking { removeTrackingArea(tracking) }
            let area = NSTrackingArea(rect: .zero, options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect],
                                      owner: self, userInfo: nil)
            addTrackingArea(area)
            tracking = area
        }

        override func mouseEntered(with event: NSEvent) { set(true) }
        override func mouseExited(with event: NSEvent) { set(false) }

        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            if window == nil { set(false) }
        }

        private func set(_ value: Bool) {
            guard value != inside else { return }
            inside = value
            changed?(value)
        }
    }
}
