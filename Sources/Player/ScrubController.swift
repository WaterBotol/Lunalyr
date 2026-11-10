import AppKit
import Combine
import SwiftUI

/// Trackpad haptics for the seek bar. Only the two events Apple documents
/// for a scrubber: reaching an end, and snapping onto something. Silent
/// without a finger on a Force Touch trackpad, and never in tests.
struct PlayerHaptics {
    var alignment: () -> Void

    static let system = PlayerHaptics {
        guard !AppRuntime.isUnitTesting else { return }
        NSHapticFeedbackManager.defaultPerformer.perform(.alignment, performanceTime: .now)
    }
    static let silent = PlayerHaptics {}
}

/// The time a drag on the seek bar points at, for the wallpaper on the same
/// display to show that moment's lyric while the hand moves. Nothing is
/// sent to the player until the release.
@MainActor
final class ScrubPreview: ObservableObject {
    static let shared = ScrubPreview()

    struct State: Equatable {
        var displayID: CGDirectDisplayID
        /// Song time (no lyric offsets).
        var songTime: Double
    }

    @Published private(set) var state: State?
    /// A preview only stands while a button is down, so a drag whose view
    /// went away (a display change) can never leave the lyrics frozen.
    var buttonsHeld: () -> Bool = { NSEvent.pressedMouseButtons != 0 }

    func show(_ songTime: Double, on displayID: CGDirectDisplayID) {
        let next = State(displayID: displayID, songTime: songTime)
        if state != next { state = next }
    }

    func clear(on displayID: CGDirectDisplayID) {
        if state?.displayID == displayID { state = nil }
    }

    /// The previewed song time on `displayID`, if a drag is in flight there.
    func songTime(on displayID: CGDirectDisplayID?) -> Double? {
        guard let state, let displayID, state.displayID == displayID, buttonsHeld() else { return nil }
        return state.songTime
    }
}

/// Play/pause that answers at once: the glyph flips on the press and the
/// player's own answer settles it. If the player has not changed within
/// `timeout`, the glyph goes back to what is really happening.
struct OptimisticToggle: Equatable {
    static let timeout: Double = 1.5
    private(set) var promised: Bool?
    private(set) var since: Double = 0

    func displayed(actual: Bool, now: Double) -> Bool {
        guard let promised, now - since < Self.timeout else { return actual }
        return promised
    }

    mutating func press(actual: Bool, now: Double) {
        promised = !displayed(actual: actual, now: now)
        since = now
    }

    /// The player reported `actual`.
    mutating func reconcile(actual: Bool, now: Double) {
        if promised == actual || now - since >= Self.timeout { promised = nil }
    }
}

/// Whether the pointer is over the bar, whether a drag is in flight, and
/// whether a key's time capsule is up: published only when one changes, so
/// the strip can make way without being redrawn on every mouse move.
@MainActor
final class ScrubActivity: ObservableObject {
    @Published fileprivate(set) var hovering = false
    @Published fileprivate(set) var dragging = false
    @Published fileprivate(set) var flashing = false

    /// The time (and lyric) capsules are over the strip's labels.
    var capsulesUp: Bool { hovering || dragging || flashing }
}

/// One seek bar's live state: hover, the press in flight, what was just
/// committed, and the keyboard's brief time capsule. One per display.
@MainActor
final class ScrubController: ObservableObject {
    /// What the bar is drawn against: the song and its lyric structure.
    struct Context: Equatable {
        var duration: Double = 0
        var trackKey = ""
        var lines: [ScrubLine] = []
        var targets: [Double] = []
        var breaks: [Double] = []
        var ticks: [Double] = []
        /// Lines that come back (often the chorus), lightened on hover.
        var repeated: [ClosedRange<Double>] = []
        var canSeek = false
        var romanization: RomanizationOptions.Display = .off

        init() {}

        init(duration: Double, trackKey: String, lines: [ScrubLine], canSeek: Bool,
             romanization: RomanizationOptions.Display = .off) {
            self.duration = duration
            self.trackKey = trackKey
            self.lines = lines
            self.canSeek = canSeek
            self.romanization = romanization
            targets = lines.isEmpty ? [] : ScrubMath.snapTargets(lines: lines, duration: duration)
            breaks = ScrubMath.sectionBreaks(lines: lines, duration: duration)
            ticks = ScrubMath.ticks(targets: targets, duration: duration)
            repeated = ScrubMath.repeatedSpans(lines: lines, duration: duration)
        }

        var hasLyrics: Bool { targets.count > 1 }
        var pointsPerSecond: Double { duration > 0 ? ScrubMath.barWidth / duration : 0 }
    }

    struct Hover: Equatable {
        var x: Double
        var y: Double
        /// The time a click here would seek to (snapped when it would snap).
        var time: Double
        var snapped: Int?
    }

    /// A committed seek (or skip) the bar keeps showing for a moment.
    struct Held: Equatable {
        var time: Double
        var since: Double
        var holdFor: Double
    }

    @Published private(set) var hover: Hover? {
        didSet { if activity.hovering != (hover != nil) { activity.hovering = hover != nil } }
    }
    @Published private(set) var session: ScrubSession? {
        didSet { if activity.dragging != (session != nil) { activity.dragging = session != nil } }
    }
    let activity = ScrubActivity()
    @Published private(set) var held: Held?
    /// The time capsule shown for a moment after a key press.
    @Published private(set) var flash: Double? {
        didSet { if activity.flashing != (flash != nil) { activity.flashing = flash != nil } }
    }
    @Published private(set) var context = Context()

    let displayID: CGDirectDisplayID
    var seek: @MainActor (Double) -> Void
    var playback: @MainActor () -> Double
    var haptics: PlayerHaptics
    var now: () -> Double
    var reduceMotion: () -> Bool
    /// Wallpaper preview of the lyric under a drag; off in tests and previews.
    var previewsOnWallpaper: Bool
    private var hoverLast: (x: Double, time: Double)?
    private var hoverSpeed: Double = 0
    private var expiry: Task<Void, Never>?
    private var flashTask: Task<Void, Never>?
    private var monitor: Any?

    init(displayID: CGDirectDisplayID,
         seek: @escaping @MainActor (Double) -> Void = { NowPlayingMonitor.shared.seek(to: $0) },
         playback: @escaping @MainActor () -> Double = { NowPlayingMonitor.shared.playbackTime() },
         haptics: PlayerHaptics = .system,
         now: @escaping () -> Double = { ProcessInfo.processInfo.systemUptime },
         reduceMotion: @escaping () -> Bool = { NSWorkspace.shared.accessibilityDisplayShouldReduceMotion },
         previewsOnWallpaper: Bool = !AppRuntime.isUnitTesting) {
        self.displayID = displayID
        self.seek = seek
        self.playback = playback
        self.haptics = haptics
        self.now = now
        self.reduceMotion = reduceMotion
        self.previewsOnWallpaper = previewsOnWallpaper
    }

    var isDragging: Bool { session != nil }

    // MARK: Context

    func update(_ next: Context) {
        guard next != context else { return }
        if let session, session.trackKey != next.trackKey || !next.canSeek {
            // A new song under the hand: never seek it to the old song's place.
            end(commit: false, animated: false)
        }
        if next.trackKey != context.trackKey { held = nil; hover = nil }
        context = next
    }

    /// The time the bar shows: the drag's target, a just-committed seek, or
    /// the player's clock.
    func displayedTime(playback: Double) -> Double {
        if let session { return session.targetTime }
        guard let held else { return playback }
        return ScrubMath.displayedTime(playback: playback, held: held.time, heldSince: held.since, now: now(),
                                       holdFor: held.holdFor)
    }

    // MARK: Pointer (bar-local points; y from the centre line)

    func pointerMoved(x: Double, y: Double, command: Bool = false) {
        guard context.canSeek, session == nil else { return }
        let t = now()
        if let last = hoverLast, t - last.time > 0.001 {
            hoverSpeed = hoverSpeed * 0.5 + abs(x - last.x) / (t - last.time) * 0.5
        }
        hoverLast = (x, t)
        let raw = ScrubMath.time(x: x, duration: context.duration)
        let snapped = ScrubMath.snap(time: raw, targets: context.targets, pointsPerSecond: context.pointsPerSecond,
                                     speed: hoverSpeed, disabled: command)
        let time = snapped.map { context.targets[$0] } ?? raw
        let next = Hover(x: min(max(x, 0), ScrubMath.barWidth), y: y, time: time, snapped: snapped)
        if hover != next { hover = next }
    }

    func pointerExited() {
        hoverLast = nil
        hoverSpeed = 0
        if hover != nil { hover = nil }
    }

    func mouseDown(x: Double, y: Double, option: Bool = false, command: Bool = false) {
        guard context.canSeek, context.duration > 0 else { return }
        let t = now()
        let knob = ScrubMath.x(time: displayedTime(playback: playback()), duration: context.duration)
        var next = ScrubSession(x: x, y: y, knobX: knob, duration: context.duration, trackKey: context.trackKey,
                                targets: context.targets, now: t, command: command)
        if option { next.modifiersChanged(option: true, command: command, now: t) }
        hover = nil
        PlayerInteraction.shared.dragging = true
        PlayerInteraction.shared.noteClick()
        set(next, animation: next.grabbedKnob ? nil : .snappy(duration: 0.14))
        preview()
        installMonitor()
    }

    func mouseDragged(x: Double, y: Double, option: Bool = false, command: Bool = false) {
        guard var next = session else { return }
        let before = next.snapped
        let events = next.move(x: x, y: y, now: now(), option: option, command: command)
        feel(events)
        let animation: Animation? = next.snapped != before && next.snapped != nil
            ? .snappy(duration: 0.14) : next.caughtUp ? .smooth(duration: 0.15) : nil
        set(next, animation: animation)
        preview()
    }

    func modifiersChanged(option: Bool, command: Bool) {
        guard var next = session else { return }
        let before = next.snapped
        feel(next.modifiersChanged(option: option, command: command, now: now()))
        set(next, animation: next.snapped != before ? .snappy(duration: 0.14) : next.caughtUp ? .smooth(duration: 0.15) : nil)
        preview()
    }

    func mouseUp(x: Double, y: Double, option: Bool = false, command: Bool = false) {
        guard session != nil else { return }
        mouseDragged(x: x, y: y, option: option, command: command)
        end(commit: true, animated: true)
        pointerMoved(x: x, y: y, command: command)
    }

    /// Esc: the knob springs back to the song and nothing is sent.
    func cancel() {
        guard session != nil else { return }
        end(commit: false, animated: true)
    }

    private func end(commit: Bool, animated: Bool) {
        guard let finished = session else { return }
        removeMonitor()
        PlayerInteraction.shared.dragging = false
        if previewsOnWallpaper { ScrubPreview.shared.clear(on: displayID) }
        let t = now()
        if commit, let target = finished.commit(at: t) {
            hold(target, for: 0.6)
            seek(target)
        }
        let animation: Animation? = animated ? (reduceMotion() ? nil : .spring(duration: 0.3, bounce: 0)) : nil
        if let animation { withAnimation(animation) { session = nil } } else { session = nil }
        PlayerInteraction.shared.noteClick()
    }

    // MARK: Keyboard and VoiceOver

    /// A seek from a key or VoiceOver: sent at once, held on the bar, and
    /// the time shown in a capsule for a second.
    func seekNow(to target: Double, flash showsCapsule: Bool = true) {
        guard context.canSeek, context.duration > 0, session == nil else { return }
        let clamped = ScrubMath.commitTarget(time: target, duration: context.duration)
        hold(clamped, for: 0.6)
        seek(clamped)
        PlayerInteraction.shared.noteClick()
        guard showsCapsule else { return }
        flash = clamped
        flashTask?.cancel()
        flashTask = Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: 1_000_000_000)
            guard !Task.isCancelled else { return }
            self?.flash = nil
        }
    }

    var currentTime: Double { displayedTime(playback: playback()) }

    func seek(by step: Double) { seekNow(to: currentTime + step) }

    /// ⌥← / ⌥→ and VoiceOver's adjust: by lyric line, else by `fallback` seconds.
    func seekLine(forward: Bool, fallback: Double = PlayerKeyMap.lineFallbackStep) {
        let time = currentTime
        if context.hasLyrics, let start = ScrubMath.adjacentLine(from: time, targets: context.targets, forward: forward) {
            seekNow(to: ScrubMath.commitTarget(time: start, snappedTo: start, duration: context.duration))
        } else if !context.hasLyrics {
            seek(by: forward ? fallback : -fallback)
        }
    }

    func jump(digit: Int) {
        if let target = ScrubMath.percentJump(digit: digit, duration: context.duration) { seekNow(to: target) }
    }

    /// After an optimistic skip: the bar shows 0:00 until the new song is in.
    func showStart(for seconds: Double = 1.5) { hold(0, for: seconds) }

    // MARK: Private

    private func hold(_ time: Double, for seconds: Double) {
        held = Held(time: time, since: now(), holdFor: seconds)
        expiry?.cancel()
        expiry = Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: UInt64((seconds + 0.05) * 1_000_000_000))
            guard !Task.isCancelled else { return }
            self?.held = nil
        }
    }

    private func set(_ next: ScrubSession, animation: Animation?) {
        if let animation, !reduceMotion() { withAnimation(animation) { session = next } } else { session = next }
    }

    private func feel(_ events: [ScrubSession.Event]) {
        if !events.isEmpty { haptics.alignment() }
    }

    private func preview() {
        guard previewsOnWallpaper, let session else { return }
        ScrubPreview.shared.show(session.targetTime, on: displayID)
    }

    /// Esc and ⌥/⌘ while the button is down. The panel became key on the press.
    private func installMonitor() {
        guard monitor == nil, !AppRuntime.isUnitTesting else { return }
        monitor = NSEvent.addLocalMonitorForEvents(matching: [.keyDown, .flagsChanged]) { [weak self] event in
            guard let self, self.session != nil else { return event }
            if event.type == .keyDown, event.keyCode == PlayerKeyMap.KeyCode.escape {
                self.cancel()
                return nil
            }
            if event.type == .flagsChanged {
                self.modifiersChanged(option: event.modifierFlags.contains(.option),
                                      command: event.modifierFlags.contains(.command))
            }
            return event.type == .keyDown ? nil : event
        }
    }

    private func removeMonitor() {
        if let monitor { NSEvent.removeMonitor(monitor) }
        monitor = nil
    }
}

/// How the wallpaper's lyrics sit under the player: blurred and faint while
/// it is up, so the player reads; clearer while its seek bar is dragged, so
/// the lyric under the hand can be seen moving with it. Under Reduce Motion
/// there is no blur, only a quick fade (and a little fainter, to make up).
struct PlayerHoverFocus: ViewModifier {
    let hovered: Bool
    let displayID: CGDirectDisplayID?
    @ObservedObject private var preview = ScrubPreview.shared
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    static func look(hovered: Bool, previewing: Bool, reduceMotion: Bool = false) -> (blur: CGFloat, opacity: Double) {
        if previewing { return (reduceMotion ? 0 : 4, 0.8) }
        guard hovered else { return (0, 1) }
        return reduceMotion ? (0, 0.15) : (22, 0.3)
    }

    /// The fades: a soft spring that reverses smoothly from where it was
    /// when the pointer moves off and back on mid-fade, or the Reduce
    /// Motion dissolve.
    static func animations(reduceMotion: Bool) -> (hover: Animation, preview: Animation) {
        (LunalyrDesign.Motion.animation(LunalyrDesign.Motion.step, reduceMotion: reduceMotion),
         LunalyrDesign.Motion.animation(.spring(response: 0.3, dampingFraction: 1), reduceMotion: reduceMotion))
    }

    func body(content: Content) -> some View {
        let look = Self.look(hovered: hovered, previewing: preview.songTime(on: displayID) != nil,
                             reduceMotion: reduceMotion)
        let animations = Self.animations(reduceMotion: reduceMotion)
        content
            .blur(radius: look.blur)
            .opacity(look.opacity)
            .animation(animations.hover, value: hovered)
            .animation(animations.preview, value: look.opacity)
    }
}
