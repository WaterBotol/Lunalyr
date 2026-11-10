import AppKit
import SwiftUI

/// A brief confirmation just under the menu bar of the screen the pointer is
/// on, for actions taken where the wallpaper's own note cannot be seen: a
/// global shortcut, a Shortcuts action or a `lunalyr://` link, a paused
/// wallpaper (which draws nothing new), or a desktop covered by windows.
///
/// It never takes focus or clicks: a borderless, non-activating panel that
/// ignores the mouse and cannot become key, ordered out whenever it is not on
/// screen, so it costs nothing at idle. A second note while one shows
/// replaces the text in place and restarts only the hold. It is left out of
/// screenshots and screen sharing where macOS honours `sharingType`.
@MainActor
final class ActionHUD {
    static let shared = ActionHUD()

    /// What the panel draws; changed in place so repeats animate.
    final class Model: ObservableObject {
        @Published var text = ""
        @Published var symbol = "checkmark"
        @Published var opaque = false
    }

    /// Room around the capsule inside the clear panel, for its shadow.
    nonisolated static let margin: CGFloat = 10
    /// The panel's size; the capsule inside sizes to its text and is centred.
    nonisolated static let panelSize = CGSize(width: 560, height: 36 + 2 * margin)
    /// Gap between the menu bar and the capsule's top.
    nonisolated static let gapBelowMenuBar: CGFloat = 6
    /// How far the HUD settles downwards as it appears.
    static let settle: CGFloat = 6

    private let model = Model()
    private var panel: NSPanel?
    private var hideWork: DispatchWorkItem?
    /// Bumped by every show and hide, so a stale fade's completion or a
    /// cancelled hold never acts on a newer note.
    private var generation = 0
    /// Fading in or holding (as against fading out or ordered out).
    private(set) var isShowing = false

    func show(_ text: String, symbol: String) {
        let workspace = NSWorkspace.shared
        let calm = workspace.accessibilityDisplayShouldReduceMotion
        model.opaque = workspace.accessibilityDisplayShouldReduceTransparency
        model.text = text
        model.symbol = symbol
        guard let screen = Self.screenWithPointer() else { return }
        let panel = self.panel ?? makePanel()
        self.panel = panel
        let target = Self.frame(panelSize: Self.panelSize, screenFrame: screen.frame, visibleFrame: screen.visibleFrame)
        generation += 1
        hideWork?.cancel()
        if isShowing, panel.isVisible, panel.frame == target {
            // A repeat on the same screen: the text changes in place.
        } else {
            if !panel.isVisible {
                panel.alphaValue = 0
                panel.setFrame(calm ? target : target.offsetBy(dx: 0, dy: Self.settle), display: false)
                panel.orderFrontRegardless()
            } else if panel.frame.size != target.size || abs(panel.frame.midX - target.midX) > 1 {
                // Visible on another screen: move there, then fade in from where it is.
                panel.setFrame(target, display: false)
            }
            NSAnimationContext.runAnimationGroup { context in
                context.duration = LunalyrDesign.Motion.CA.quick
                context.timingFunction = LunalyrDesign.Motion.CA.easeOut
                panel.animator().alphaValue = 1
                panel.animator().setFrame(target, display: true)
            }
        }
        isShowing = true
        let shown = generation
        let work = DispatchWorkItem { [weak self] in
            MainActor.assumeIsolated { self?.hide(generation: shown) }
        }
        hideWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + LunalyrDesign.Motion.Hold.hud, execute: work)
    }

    private func hide(generation shown: Int) {
        guard shown == generation, let panel else { return }
        isShowing = false
        generation += 1
        let hiding = generation
        LunalyrDesign.Motion.CA.fade(panel, to: 0, duration: LunalyrDesign.Motion.CA.standard) { [weak self] in
            MainActor.assumeIsolated {
                guard let self, self.generation == hiding, !self.isShowing else { return }
                panel.orderOut(nil)
            }
        }
    }

    private func makePanel() -> NSPanel {
        let panel = HUDPanel(contentRect: NSRect(origin: .zero, size: Self.panelSize),
                             styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: true)
        Self.configure(panel)
        let host = NSHostingView(rootView: ActionHUDView(model: model))
        host.setAccessibilityElement(false)
        panel.contentView = host
        return panel
    }

    /// Everything that keeps the panel out of the way: no focus, no clicks,
    /// on every Space and over full-screen apps, out of Mission Control and
    /// the window cycle, and out of screen captures.
    static func configure(_ panel: NSPanel) {
        panel.isFloatingPanel = true
        panel.level = .statusBar
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .transient, .ignoresCycle]
        panel.ignoresMouseEvents = true
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.hidesOnDeactivate = false
        panel.isReleasedWhenClosed = false
        panel.becomesKeyOnlyIfNeeded = true
        panel.animationBehavior = .none
        panel.sharingType = .none
        panel.isExcludedFromWindowsMenu = true
    }

    // MARK: - Placement

    /// Centred on the screen, just under its menu bar.
    nonisolated static func frame(panelSize: CGSize, screenFrame: CGRect, visibleFrame: CGRect) -> CGRect {
        // The visible frame stops at the menu bar; with the menu bar hidden it
        // reaches the top, so keep clear of where the menu bar drops in.
        let top = min(visibleFrame.maxY, screenFrame.maxY - 24) - gapBelowMenuBar + margin
        let width = min(panelSize.width, screenFrame.width)
        return CGRect(x: (screenFrame.midX - width / 2).rounded(), y: (top - panelSize.height).rounded(),
                      width: width, height: panelSize.height)
    }

    /// The index of the frame holding `point`, edges included (the pointer
    /// can sit on the very top row, in the menu bar); nil when none does.
    nonisolated static func index(of point: CGPoint, in frames: [CGRect]) -> Int? {
        frames.firstIndex { point.x >= $0.minX && point.x <= $0.maxX && point.y >= $0.minY && point.y <= $0.maxY }
    }

    /// The screen with the pointer, else the one with the menu bar.
    static func screenWithPointer() -> NSScreen? {
        let screens = NSScreen.screens
        if let index = index(of: NSEvent.mouseLocation, in: screens.map(\.frame)) { return screens[index] }
        return screens.first ?? NSScreen.main
    }

    /// The display the pointer is on, for "is the wallpaper there covered".
    static func displayWithPointer() -> CGDirectDisplayID? {
        screenWithPointer()?.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? CGDirectDisplayID
    }
}

/// Can never become key or main, so showing it never takes focus from the
/// app in front.
private final class HUDPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

struct ActionHUDView: View {
    @ObservedObject var model: ActionHUD.Model
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        Label {
            Text(model.text)
                .lineLimit(1)
                .truncationMode(.tail)
                .contentTransition(reduceMotion ? .opacity : .numericText())
        } icon: {
            Image(systemName: model.symbol)
                .contentTransition(LunalyrDesign.Motion.symbolSwap(reduceMotion: reduceMotion))
        }
        .font(.system(size: 13, weight: .medium))
        .foregroundStyle(.primary)
        .padding(.horizontal, 14)
        .frame(height: 36)
        .background {
            if model.opaque {
                Capsule().fill(Color(nsColor: .windowBackgroundColor))
            } else {
                Capsule().fill(.regularMaterial)
            }
        }
        .overlay(Capsule().strokeBorder(Color.primary.opacity(0.12), lineWidth: 0.5))
        .shadow(color: .black.opacity(0.18), radius: 6, y: 2)
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion),
                   value: model.text)
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion),
                   value: model.symbol)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding(ActionHUD.margin)
    }
}
