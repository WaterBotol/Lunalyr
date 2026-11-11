import AppKit
import SwiftUI

/// The row under the seek bar: the transport on the left; search, the
/// queue and the film button (lining up the music video) on the right.
/// The player switch is not here: it only steers search, so it sits by
/// the search box.
struct PlayerControlsRow<Transport: View, Film: View>: View {
    let searchOpen: Bool
    let queueOpen: Bool
    let onSearch: () -> Void
    let onQueue: () -> Void
    @ViewBuilder let transport: () -> Transport
    @ViewBuilder let film: () -> Film

    var body: some View {
        HStack(spacing: 4) {
            transport()
            Spacer(minLength: 0)
            PlayerIconButton(symbol: "magnifyingglass", on: searchOpen, label: "Search",
                             help: "Search and play a song (\u{2318}F)", action: onSearch)
            PlayerIconButton(symbol: "list.bullet", on: queueOpen, label: "Queue",
                             help: "What played and what plays next", action: onQueue)
            film()
        }
        .frame(height: PlayerTransportRowHeight.value)
    }
}

/// Previous, a round white play button, next. Large in the song's column,
/// small under the cover while search or the queue is open.
struct PlayerTransport: View {
    enum Size {
        case large, small

        var cell: CGFloat { self == .large ? 34 : 30 }
        var disc: CGFloat { self == .large ? 44 : 34 }
        var glyph: CGFloat { self == .large ? 17 : 13 }
    }

    let size: Size
    let canControl: Bool
    let hasSong: Bool
    let isPlaying: Bool
    let onPrevious: () -> Void
    let onPlayPause: () -> Void
    let onNext: () -> Void
    @Environment(\.playerLook) private var look

    var body: some View {
        HStack(spacing: size == .large ? 6 : 4) {
            Button(action: onPrevious) { glyph("backward.fill") }
                .buttonStyle(TransportButtonStyle())
                .opacity(hasSong ? 1 : 0.35)
                .disabled(!hasSong)
                .help("Previous").accessibilityLabel("Previous track")
            Button(action: onPlayPause) {
                ZStack {
                    Circle().fill(.white)
                        .shadow(color: .black.opacity(0.3), radius: 4, y: 2)
                    Image(systemName: isPlaying ? "pause.fill" : "play.fill")
                        .font(.system(size: size.disc * 0.4, weight: .bold))
                        .foregroundStyle(.black)
                        .offset(x: isPlaying ? 0 : size.disc * 0.03)
                        .contentTransition(look.reduceMotion ? .opacity : .symbolEffect(.replace))
                }
                .frame(width: size.disc, height: size.disc)
                .contentShape(Circle())
            }
            .buttonStyle(TransportButtonStyle(halo: false))
            .help(isPlaying ? "Pause" : "Play").accessibilityLabel(isPlaying ? "Pause" : "Play")
            Button(action: onNext) { glyph("forward.fill") }
                .buttonStyle(TransportButtonStyle())
                .opacity(hasSong ? 1 : 0.35)
                .disabled(!hasSong)
                .help("Next").accessibilityLabel("Next track")
        }
        .disabled(!canControl)
        .opacity(canControl ? 1 : 0.6)
    }

    private func glyph(_ symbol: String) -> some View {
        Image(systemName: symbol).font(.system(size: size.glyph, weight: .semibold))
            .frame(width: size.cell, height: size.cell)
            .contentShape(Rectangle())
    }
}

/// One of the row's round icon buttons: lit while what it opens is open.
struct PlayerIconButton: View {
    let symbol: String
    let on: Bool
    let label: String
    let help: String
    let action: () -> Void
    @Environment(\.playerLook) private var look

    var body: some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: 13, weight: .semibold))
                .foregroundStyle(.white.opacity(on || look.increaseContrast ? 1 : 0.75))
                .frame(width: 30, height: 30)
                .background(Circle().fill(.white.opacity(on ? 0.2 : 0)))
                .contentShape(Circle())
        }
        .buttonStyle(TransportButtonStyle())
        .help(help)
        .accessibilityLabel(label)
        .accessibilityAddTraits(on ? .isSelected : [])
    }
}

/// Highlight on mouse-down, a quick press-in, and a release with a little
/// momentum (the one bounce in the player). A faint circle on hover.
struct TransportButtonStyle: ButtonStyle {
    /// The faint circle behind on hover (not for the play disc, which is its own circle).
    var halo = true

    func makeBody(configuration: Configuration) -> some View {
        TransportButtonBody(configuration: configuration, halo: halo)
    }
}

private struct TransportButtonBody: View {
    let configuration: ButtonStyleConfiguration
    let halo: Bool
    @State private var hovering = false
    @Environment(\.isEnabled) private var enabled
    @Environment(\.playerLook) private var look
    @Environment(\.playerPreview) private var preview
    @Environment(\.playerMaskRender) private var maskRender

    var body: some View {
        let pressed = configuration.isPressed
        configuration.label
            .background {
                Circle().fill(.white.opacity(!halo ? 0 : pressed ? 0.14 : hovering && enabled ? 0.1 : 0))
                    .frame(width: 32, height: 32)
                    .animation(.easeOut(duration: 0.10), value: hovering)
            }
            .brightness(!halo && hovering && enabled ? -0.06 : 0)
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
