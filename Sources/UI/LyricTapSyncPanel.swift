import AppKit
import SwiftUI

/// What Tap to sync reads about the song. The app's is `.live`; tests pass
/// their own, so they never touch the player or real preferences.
struct LyricTapContext {
    var query: @MainActor () -> LyricQuery?
    var lineStarts: @MainActor () -> [Double]
    var isPlaying: @MainActor () -> Bool
    var playbackTime: @MainActor () -> Double
    var globalOffset: @MainActor () -> Double

    @MainActor static var live: Self {
        Self(query: { LyricsViewModel.shared.currentQuery },
             lineStarts: { LyricTapSync.lineStarts(LyricsViewModel.shared.lines) },
             isPlaying: { NowPlayingMonitor.shared.isPlaying },
             playbackTime: { NowPlayingMonitor.shared.playbackTime() },
             globalOffset: { Settings.shared.lyricOffset })
    }
}

/// Runs Tap to sync for lyrics: a small panel takes the keyboard, every
/// plain key press is a tap, and once the taps agree the song's offset is
/// saved in `SongOffsets` (for the lyric source on screen, like Earlier and
/// Later). Undo puts back the offset from before.
@MainActor
final class LyricTapSyncController: ObservableObject {
    static let shared = LyricTapSyncController()

    @Published private(set) var session = LyricTapSession()
    /// The last applied tap sync, while it can still be undone.
    @Published private(set) var lastApplied: Applied?

    struct Applied: Equatable {
        var recordingKey: String
        var query: LyricQuery
        var source: String?
        var previous: Double
        var offset: Double

        static func == (a: Self, b: Self) -> Bool {
            a.recordingKey == b.recordingKey && a.source == b.source && a.previous == b.previous && a.offset == b.offset
        }
    }

    static let menuTitle = "Tap to Sync Lyrics…"
    static let stopTitle = "Stop Tap to Sync"
    static let undoTitle = "Undo Lyric Tap Sync"

    private let offsets: SongOffsets
    private let context: LyricTapContext
    private let now: () -> Double
    private let presents: Bool
    private let notify: @MainActor (String, String) -> Void
    /// The song and lyric source being tapped.
    private var target: (query: LyricQuery, key: String, source: String?)?
    private var panel: LyricTapPanel?
    private var keyMonitor: Any?
    private var resignObserver: NSObjectProtocol?
    private var ticker: Task<Void, Never>?
    private var dismissal: Task<Void, Never>?
    /// The app in front before Lunalyr took the keyboard, if it had to.
    private var previousApp: NSRunningApplication?

    init(offsets: SongOffsets? = nil, context: LyricTapContext? = nil,
         now: @escaping () -> Double = { Date().timeIntervalSinceReferenceDate },
         presents: Bool = !AppRuntime.isUnitTesting,
         notify: (@MainActor (String, String) -> Void)? = nil) {
        self.offsets = offsets ?? .shared
        self.context = context ?? .live
        self.now = now
        self.presents = presents
        self.notify = notify ?? { LunalyrExtras.shared.post($0, symbol: $1) }
    }

    var isListening: Bool { session.isListening }

    /// The menu item's title.
    var actionTitle: String { isListening ? Self.stopTitle : Self.menuTitle }

    /// Starts, or stops a session already listening (the shortcut and the
    /// menu item do both).
    func toggle() {
        if isListening { cancel() } else { start() }
    }

    func start() {
        dismissal?.cancel()
        let query = context.query()
        let source = offsets.activeSource
        let prior = offsets.offset(for: query, source: source)
        session.start(lineStarts: query == nil ? [] : context.lineStarts(), prior: prior,
                      playing: query != nil && context.isPlaying(), now: now())
        guard session.isListening, let query else {
            target = nil
            finish()
            return
        }
        target = (query, query.recordingKey, source)
        if presents { present() }
        tick()
    }

    /// A tap: a lyric line starts now.
    func tap() {
        guard session.isListening, let target else { return }
        guard context.query()?.recordingKey == target.key, context.isPlaying() else {
            cancel()
            return
        }
        let time = LyricTapSync.tapTime(playbackTime: context.playbackTime(), globalOffset: context.globalOffset())
        if let estimate = session.tap(at: time, now: now()) {
            let previous = session.prior
            offsets.set(estimate.offset, for: target.query, source: target.source)
            lastApplied = Applied(recordingKey: target.key, query: target.query, source: target.source,
                                  previous: previous, offset: offsets.offset(for: target.query, source: target.source))
        }
        if !session.isListening { finish() } else { objectWillChange.send() }
    }

    /// Esc, the menu item again, or Lunalyr lost the keyboard.
    func cancel() {
        guard session.isListening else { return }
        session.cancel()
        finish()
    }

    /// Ends a session whose taps stopped coming. Called by the session's own
    /// timer; tests call it with their clock.
    func expireIfDue() {
        if session.expire(now: now()) { finish() }
    }

    /// Whether Undo applies to the song playing now: the tap sync's offset
    /// is still the one in force for it.
    var canUndo: Bool {
        guard let lastApplied, !isListening, context.query()?.recordingKey == lastApplied.recordingKey else { return false }
        return offsets.offset(for: lastApplied.query, source: lastApplied.source) == lastApplied.offset
    }

    /// Puts back the offset from before the last tap sync.
    func undo() {
        guard canUndo, let lastApplied else { return }
        offsets.set(lastApplied.previous, for: lastApplied.query, source: lastApplied.source)
        self.lastApplied = nil
        notify("Tap to sync undone: lyrics back to \(TimingNudge.format(lastApplied.previous))", "arrow.uturn.backward")
        dismiss()
    }

    /// Hides the result.
    func dismiss() {
        dismissal?.cancel()
        dismissal = nil
        session.dismiss()
        closePanel()
    }

    // MARK: - Finishing

    private func finish() {
        stopListening()
        switch session.phase {
        case .applied: notify(session.message, "hand.tap")
        case .failed(let failure) where failure != .cancelled: notify(session.message, "hand.tap")
        default: break
        }
        guard presents, panel != nil else { return }
        dismissal?.cancel()
        let applied: Bool
        if case .applied = session.phase { applied = true } else { applied = false }
        dismissal = Task { [weak self] in
            // Long enough to reach Undo on the panel.
            try? await Task.sleep(for: .seconds(applied ? 6 : 3))
            guard !Task.isCancelled else { return }
            self?.dismiss()
        }
    }

    private func tick() {
        ticker?.cancel()
        ticker = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .milliseconds(500))
                guard !Task.isCancelled, let self, self.session.isListening else { return }
                self.expireIfDue()
            }
        }
    }

    // MARK: - Keyboard

    /// Shows the panel and gives it the keyboard. The panel doesn't
    /// activate Lunalyr where it can help it; when it can't take the
    /// keyboard that way, Lunalyr comes forward and hands the front back
    /// to the app that had it once the session ends.
    private func present() {
        let panel = self.panel ?? LyricTapPanel(controller: self)
        self.panel = panel
        panel.show()
        watchKeys()
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) { [weak self] in
            MainActor.assumeIsolated {
                guard let self, self.session.isListening, let panel = self.panel, !panel.isKeyWindow else { return }
                self.previousApp = NSWorkspace.shared.frontmostApplication
                NSApp.activate(ignoringOtherApps: true)
                panel.makeKeyAndOrderFront(nil)
            }
        }
    }

    /// While listening, plain keys pressed in the panel are taps and Esc
    /// stops. Local to Lunalyr's own windows: no new permission, and a key
    /// pressed in a text field (Settings, the player's search) is typing.
    private func watchKeys() {
        stopWatchingKeys()
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            MainActor.assumeIsolated {
                guard let self, self.session.isListening, let panel = self.panel, event.window === panel else { return event }
                let typing = panel.firstResponder is NSText
                switch MusicVideoTapSync.keyAction(keyCode: event.keyCode, modifiers: event.modifierFlags,
                                                   isRepeat: event.isARepeat, textInputFocused: typing) {
                case .pass: return event
                case .cancel: self.cancel(); return nil
                case .tap: self.tap(); return nil
                }
            }
        }
        // Clicking elsewhere takes the keyboard away: the keys would no
        // longer reach Tap to sync, so it stops rather than waiting blind.
        resignObserver = NotificationCenter.default.addObserver(forName: NSWindow.didResignKeyNotification,
                                                                object: panel, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.cancel() }
        }
    }

    private func stopWatchingKeys() {
        if let keyMonitor { NSEvent.removeMonitor(keyMonitor) }
        keyMonitor = nil
        if let resignObserver { NotificationCenter.default.removeObserver(resignObserver) }
        resignObserver = nil
    }

    private func stopListening() {
        ticker?.cancel()
        ticker = nil
        stopWatchingKeys()
        // The result stays on screen, but the keyboard goes back: ordering
        // the panel out and in again hands key status on without activating
        // anything (as the desktop player does).
        if let panel, panel.isKeyWindow {
            panel.orderOut(nil)
            panel.orderFrontRegardless()
        }
        // Only if Lunalyr is still in front: a click on another app chose it.
        if let previousApp {
            self.previousApp = nil
            if NSApp.isActive { previousApp.activate() }
        }
    }

    private func closePanel() {
        stopWatchingKeys()
        panel?.orderOut(nil)
        panel = nil
    }
}

// MARK: - Panel

/// The small floating panel: what to do, how many taps, and the result with
/// Undo. Borderless and non-activating, at the top of the screen in use.
final class LyricTapPanel: NSPanel {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }

    static let size = NSSize(width: 420, height: 96)

    @MainActor
    init(controller: LyricTapSyncController) {
        super.init(contentRect: NSRect(origin: .zero, size: Self.size),
                   styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        level = .floating
        isOpaque = false
        backgroundColor = .clear
        hasShadow = true
        hidesOnDeactivate = false
        isReleasedWhenClosed = false
        isMovableByWindowBackground = true
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .ignoresCycle]
        title = "Lunalyr Tap to sync"
        isExcludedFromWindowsMenu = true
        let hosting = NSHostingView(rootView: LyricTapSyncView(controller: controller))
        hosting.frame = NSRect(origin: .zero, size: Self.size)
        hosting.autoresizingMask = [.width, .height]
        contentView = hosting
    }

    @MainActor
    func show() {
        let screen = NSScreen.screens.first { $0.frame.contains(NSEvent.mouseLocation) } ?? NSScreen.main
        if let visible = screen?.visibleFrame {
            setFrameOrigin(NSPoint(x: visible.midX - Self.size.width / 2, y: visible.maxY - Self.size.height - 24))
        }
        makeKeyAndOrderFront(nil)
    }
}

struct LyricTapSyncView: View {
    @ObservedObject var controller: LyricTapSyncController

    var body: some View {
        let session = controller.session
        HStack(spacing: LunalyrDesign.Spacing.medium) {
            Image(systemName: "hand.tap")
                .font(.title2)
                .foregroundStyle(.secondary)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: LunalyrDesign.Spacing.xSmall) {
                Text("Tap to sync lyrics")
                    .font(.headline)
                Text(session.message)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                if session.isListening {
                    TapDots(count: session.taps.count)
                }
            }
            Spacer(minLength: 0)
            VStack(spacing: LunalyrDesign.Spacing.xSmall) {
                if session.isListening {
                    Button("Stop") { controller.cancel() }
                } else {
                    if controller.canUndo, case .applied = session.phase {
                        Button("Undo") { controller.undo() }
                    }
                    Button("Done") { controller.dismiss() }
                }
            }
            .controlSize(.small)
        }
        .padding(LunalyrDesign.Spacing.large)
        .frame(width: LyricTapPanel.size.width, height: LyricTapPanel.size.height)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Tap to sync lyrics")
    }

    private struct TapDots: View {
        let count: Int
        var body: some View {
            HStack(spacing: 5) {
                ForEach(0..<max(LyricTapSync.requiredTaps, count), id: \.self) { index in
                    Circle()
                        .fill(index < count ? Color.accentColor : Color.secondary.opacity(0.3))
                        .frame(width: 7, height: 7)
                }
            }
            .accessibilityHidden(true)
        }
    }
}
