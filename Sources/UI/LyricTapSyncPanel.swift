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
        // Another song, a pause, or other lyrics on screen (a better source
        // arrived): the taps would be timed against lines no longer shown,
        // and the offset saved for a source no longer in use.
        if let reason = stopReason(target) {
            // Ends like a result, not like Esc: the keyboard stays with the
            // panel while it shows, so the next tap can't reach the app behind.
            session.stop(reason)
            finish()
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

    /// Why taps against `target` can't go on, if they can't.
    private func stopReason(_ target: (query: LyricQuery, key: String, source: String?)) -> LyricTapSession.Failure? {
        if context.query()?.recordingKey != target.key { return .songChanged }
        if !context.isPlaying() { return .notPlaying }
        if offsets.activeSource != target.source || context.lineStarts() != session.lineStarts { return .lyricsChanged }
        return nil
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
        ticker?.cancel()
        ticker = nil
        // Stopping hands the keyboard back at once. A result keeps it while
        // it shows (`keyResponse`): the listener is mid-rhythm, and the next
        // tap would otherwise land in the app behind, typing into it or
        // pausing the song with Space.
        if session.phase == .failed(.cancelled) || !presents || panel == nil { handBackKeyboard() }
        switch session.phase {
        case .applied: notify(session.message, "hand.tap")
        case .failed(let failure) where failure != .cancelled: notify(session.message, "hand.tap")
        default: break
        }
        guard presents, panel != nil else { return }
        scheduleDismissal()
    }

    /// How long a result stays up: long enough to reach Undo when it's offered.
    nonisolated static func dismissDelay(for phase: LyricTapSession.Phase) -> TimeInterval {
        if case .applied = phase { return 8 }
        return 3
    }

    /// The pointer is over the panel: the result stays while it is.
    private var hovering = false

    func pointerHovering(_ inside: Bool) {
        hovering = inside
        guard !session.isListening, session.phase != .idle, panel != nil else { return }
        if inside {
            dismissal?.cancel()
            dismissal = nil
        } else {
            scheduleDismissal()
        }
    }

    private func scheduleDismissal() {
        dismissal?.cancel()
        dismissal = nil
        guard !hovering else { return }
        let delay = Self.dismissDelay(for: session.phase)
        dismissal = Task { [weak self] in
            try? await Task.sleep(for: .seconds(delay))
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
                // Not Lunalyr itself (a result still showing kept the keyboard).
                if self.previousApp == nil, let front = NSWorkspace.shared.frontmostApplication,
                   front.processIdentifier != NSRunningApplication.current.processIdentifier {
                    self.previousApp = front
                }
                NSApp.activate(ignoringOtherApps: true)
                panel.makeKeyAndOrderFront(nil)
            }
        }
    }

    /// What a key pressed in the panel does.
    enum KeyResponse: Equatable {
        /// Not for Tap to sync (a shortcut, a modifier, a held key).
        case pass
        case tap
        /// Esc while listening.
        case stop
        /// A tap after the result: kept from the app behind.
        case swallow
        /// Esc or Return on the result: closes it and hands the keyboard back.
        case close
    }

    nonisolated static func keyResponse(listening: Bool, keyCode: UInt16, modifiers: NSEvent.ModifierFlags,
                                        isRepeat: Bool, textInputFocused: Bool) -> KeyResponse {
        switch MusicVideoTapSync.keyAction(keyCode: keyCode, modifiers: modifiers, isRepeat: isRepeat,
                                           textInputFocused: textInputFocused) {
        case .pass: return .pass
        case .cancel: return listening ? .stop : .close
        case .tap:
            if listening { return .tap }
            // Return and keypad Enter.
            return keyCode == 36 || keyCode == 76 ? .close : .swallow
        }
    }

    /// While listening, plain keys pressed in the panel are taps and Esc
    /// stops; while a result shows, they are kept from the app behind.
    /// Local to Lunalyr's own windows: no new permission, and a key pressed
    /// in a text field (Settings, the player's search) is typing.
    private func watchKeys() {
        stopWatchingKeys()
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            MainActor.assumeIsolated {
                guard let self, let panel = self.panel, event.window === panel else { return event }
                let typing = panel.firstResponder is NSText
                switch Self.keyResponse(listening: self.session.isListening, keyCode: event.keyCode,
                                        modifiers: event.modifierFlags, isRepeat: event.isARepeat,
                                        textInputFocused: typing) {
                case .pass: return event
                case .tap: self.tap(); return nil
                case .stop: self.cancel(); return nil
                case .swallow: return nil
                case .close: self.dismiss(); return nil
                }
            }
        }
        // Clicking elsewhere takes the keyboard away: the keys would no
        // longer reach Tap to sync, so it stops rather than waiting blind.
        // The listener chose where the keyboard went, so no app is brought
        // back in front afterwards.
        resignObserver = NotificationCenter.default.addObserver(forName: NSWindow.didResignKeyNotification,
                                                                object: panel, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.previousApp = nil
                if self.session.isListening { self.cancel() } else { self.stopWatchingKeys() }
            }
        }
    }

    private func stopWatchingKeys() {
        if let keyMonitor { NSEvent.removeMonitor(keyMonitor) }
        keyMonitor = nil
        if let resignObserver { NotificationCenter.default.removeObserver(resignObserver) }
        resignObserver = nil
    }

    /// Gives the keyboard back to where it was before Tap to sync took it.
    private func handBackKeyboard() {
        stopWatchingKeys()
        // The result stays on screen, but the keyboard goes back: ordering
        // the panel out and in again hands key status on without activating
        // anything (as the desktop player does).
        if let panel, panel.isKeyWindow {
            panel.orderOut(nil)
            panel.orderFrontRegardless()
        }
        restorePreviousApp()
    }

    /// Only if Lunalyr is still in front: a click on another app chose it.
    private func restorePreviousApp() {
        if let previousApp {
            self.previousApp = nil
            if NSApp.isActive { previousApp.activate() }
        }
    }

    private func closePanel() {
        stopWatchingKeys()
        panel?.hide()
        panel = nil
        hovering = false
        restorePreviousApp()
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

    /// A fade out is under way; a show before it ends turns it back.
    private var hiding = false

    /// Fades in at the top of the screen with the pointer, or back from a
    /// fade out where it is. A fade, so Reduce Motion keeps it.
    @MainActor
    func show() {
        if !isVisible {
            let screen = NSScreen.screens.first { $0.frame.contains(NSEvent.mouseLocation) } ?? NSScreen.main
            if let visible = screen?.visibleFrame {
                setFrameOrigin(NSPoint(x: visible.midX - Self.size.width / 2, y: visible.maxY - Self.size.height - 24))
            }
            alphaValue = 0
        }
        hiding = false
        makeKeyAndOrderFront(nil)
        LunalyrDesign.Motion.CA.fade(self, to: 1, duration: LunalyrDesign.Motion.CA.reduced)
    }

    /// Fades out from wherever the fade in got to, then leaves the screen.
    @MainActor
    func hide() {
        guard isVisible else { return }
        hiding = true
        LunalyrDesign.Motion.CA.fade(self, to: 0, duration: LunalyrDesign.Motion.CA.quick) { [weak self] in
            MainActor.assumeIsolated {
                guard let self, self.hiding else { return }
                self.hiding = false
                self.orderOut(nil)
                self.alphaValue = 1
            }
        }
    }
}

struct LyricTapSyncView: View {
    private typealias Motion = LunalyrDesign.Motion
    @ObservedObject var controller: LyricTapSyncController
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    private func animation(_ animation: Animation) -> Animation {
        Motion.animation(animation, reduceMotion: reduceMotion)
    }

    var body: some View {
        let session = controller.session
        HStack(spacing: LunalyrDesign.Spacing.medium) {
            Image(systemName: Self.symbol(for: session.phase))
                .font(.title2)
                .foregroundStyle(Self.tint(for: session.phase))
                .contentTransition(Motion.symbolSwap(reduceMotion: reduceMotion))
                .symbolEffect(.bounce, options: .nonRepeating, value: reduceMotion ? 0 : session.taps.count)
                .frame(width: 28)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: LunalyrDesign.Spacing.xSmall) {
                Text("Tap to sync lyrics")
                    .font(.headline)
                Text(session.message)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .contentTransition(.opacity)
                // Always laid out, so the text doesn't jump when the result shows.
                TapDots(count: session.taps.count, reduceMotion: reduceMotion)
                    .opacity(session.isListening ? 1 : 0)
            }
            Spacer(minLength: 0)
            VStack(spacing: LunalyrDesign.Spacing.xSmall) {
                if session.isListening {
                    Button("Stop") { controller.cancel() }
                        .transition(.opacity)
                } else {
                    if controller.canUndo, case .applied = session.phase {
                        Button("Undo") { controller.undo() }
                            .transition(.opacity)
                    }
                    Button("Done") { controller.dismiss() }
                        .transition(.opacity)
                }
            }
            .controlSize(.small)
        }
        .animation(animation(Motion.quick), value: session.phase)
        .padding(LunalyrDesign.Spacing.large)
        .frame(width: LyricTapPanel.size.width, height: LyricTapPanel.size.height)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: LunalyrDesign.Radius.card, style: .continuous))
        .onHover { controller.pointerHovering($0) }
        .onChange(of: session.taps.count) { _, count in
            guard count > 0, controller.session.isListening else { return }
            AccessibilityNotification.Announcement(Self.tapAnnouncement(count)).post()
        }
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Tap to sync lyrics")
    }

    /// The symbol beside the message: the hand while tapping, then how it ended.
    static func symbol(for phase: LyricTapSession.Phase) -> String {
        switch phase {
        case .idle, .listening: return "hand.tap"
        case .applied: return LunalyrStatus.success.symbol
        case .failed(.cancelled): return "xmark.circle"
        case .failed: return LunalyrStatus.warning.symbol
        }
    }

    static func tint(for phase: LyricTapSession.Phase) -> Color {
        switch phase {
        case .applied: return LunalyrStatus.success.text
        case .failed(.cancelled), .idle, .listening: return .secondary
        case .failed: return LunalyrStatus.warning.text
        }
    }

    /// What VoiceOver says after each tap.
    static func tapAnnouncement(_ count: Int) -> String {
        count <= LyricTapSync.requiredTaps ? "Tap \(count) of \(LyricTapSync.requiredTaps)" : "Tap \(count)"
    }

    private struct TapDots: View {
        let count: Int
        let reduceMotion: Bool
        var body: some View {
            HStack(spacing: 5) {
                ForEach(0..<max(LyricTapSync.requiredTaps, count), id: \.self) { index in
                    ZStack {
                        Circle().fill(Color.secondary.opacity(0.3))
                        if index < count {
                            // The newest dot pops in; a plain fade under Reduce Motion.
                            Circle().fill(Color.accentColor)
                                .transition(reduceMotion ? .opacity : .scale.combined(with: .opacity))
                        }
                    }
                    .frame(width: 7, height: 7)
                    .transition(reduceMotion ? .opacity : .scale.combined(with: .opacity))
                }
            }
            .frame(height: 7)
            .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion), value: count)
            .accessibilityHidden(true)
        }
    }
}
