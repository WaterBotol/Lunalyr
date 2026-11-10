import AppKit
import SwiftUI

// MARK: - Accessibility look

/// Reduce Motion, Reduce Transparency and Increase Contrast as the player
/// draws them. Read from the system; a preview can set them.
struct PlayerLook: Equatable {
    var reduceMotion = false
    var reduceTransparency = false
    var increaseContrast = false
}

private struct PlayerLookOverrideKey: EnvironmentKey { static let defaultValue: PlayerLook? = nil }
private struct PlayerLookKey: EnvironmentKey { static let defaultValue = PlayerLook() }

extension EnvironmentValues {
    /// Set by previews to draw one accessibility look whatever the Mac's.
    var playerLookOverride: PlayerLook? {
        get { self[PlayerLookOverrideKey.self] }
        set { self[PlayerLookOverrideKey.self] = newValue }
    }

    /// The look the player's parts draw with (resolved by `DesktopPlayerView`).
    var playerLook: PlayerLook {
        get { self[PlayerLookKey.self] }
        set { self[PlayerLookKey.self] = newValue }
    }
}

extension View {
    /// A capsule's backing: frosted, or solid under Reduce Transparency.
    @ViewBuilder
    func playerCapsule(_ look: PlayerLook) -> some View {
        if look.reduceTransparency {
            background(Color(white: 0.12).opacity(0.95), in: Capsule())
        } else {
            background(.ultraThinMaterial, in: Capsule())
                .background(Color.black.opacity(0.35), in: Capsule())
        }
    }
}

// MARK: - Seek row

/// What the row under the strip can be for the song playing.
enum ScrubRowKind: Equatable {
    /// A song that can be scrubbed.
    case seekable
    /// Shows the song's progress but cannot seek it.
    case readOnly
    /// Only the elapsed time (no known length).
    case elapsedOnly
    case live
    case advert
    /// Nothing at all (nothing playing, or searching).
    case hidden

    static func of(track: NowPlayingMonitor.Track?, canSeek: Bool) -> ScrubRowKind {
        guard let track else { return .hidden }
        if track.isAdvert { return .advert }
        if track.isStream { return .live }
        guard track.duration > 0 else { return .elapsedOnly }
        return canSeek ? .seekable : .readOnly
    }
}

/// The seek bar with its times: 560 × 28 pt, the bar 456 pt in the middle.
/// Hairline at rest; on hover it thickens, shows the knob, the line ticks
/// and a time and lyric capsule at the pointer; under a press it thickens
/// again, takes the player's colour, and the capsules show the target.
struct ScrubRow: View {
    static let rowWidth: CGFloat = 560
    static let height: CGFloat = 28
    static let barWidth = CGFloat(ScrubMath.barWidth)
    static let labelWidth: CGFloat = 44

    @ObservedObject var controller: ScrubController
    let kind: ScrubRowKind
    let tint: Color
    let isPlaying: Bool
    /// The player is on screen: the clock only ticks while it is.
    let shown: Bool
    @Binding var showsRemaining: Bool
    /// Lines (original, romanised) for the capsule, by index into `controller.context.lines`.
    @Environment(\.playerMaskRender) private var maskRender
    @Environment(\.playerLook) private var look
    @Environment(\.playerPreview) private var preview

    var body: some View {
        Group {
            if maskRender {
                mask
            } else {
                switch kind {
                case .seekable, .readOnly, .advert:
                    TimelineView(.animation(minimumInterval: 0.1, paused: !shown || !isPlaying)) { _ in
                        row(elapsed: controller.displayedTime(playback: controller.playback()))
                    }
                case .elapsedOnly:
                    TimelineView(.animation(minimumInterval: 0.25, paused: !shown || !isPlaying)) { _ in
                        Text(ScrubMath.timestamp(controller.playback()))
                            .font(.system(size: 11).monospacedDigit())
                            .foregroundStyle(.white.opacity(timeOpacity(active: false)))
                    }
                case .live:
                    LiveCapsule(shown: shown)
                case .hidden:
                    EmptyView()
                }
            }
        }
        .frame(width: Self.rowWidth, height: Self.height)
    }

    /// The whole row is a block in the hit mask (taller than the row, so
    /// the pointer is "in" before it reaches the track); anything less and
    /// clicks between the hairline's pixels would reach Finder.
    @ViewBuilder
    private var mask: some View {
        switch kind {
        case .seekable:
            Color.black.opacity(0.3).frame(width: Self.rowWidth, height: Self.height + 16)
        case .readOnly, .advert, .elapsedOnly, .live:
            // The time label toggles; the rest only shows.
            Color.black.opacity(0.3).frame(width: Self.rowWidth, height: Self.height)
        case .hidden:
            EmptyView()
        }
    }

    private var active: Bool { kind == .seekable && (controller.hover != nil || controller.session != nil) }
    private var dragging: Bool { controller.session != nil }

    private func timeOpacity(active: Bool) -> Double {
        look.increaseContrast ? 0.95 : active ? 0.85 : 0.55
    }

    private func row(elapsed: Double) -> some View {
        let duration = controller.context.duration
        return HStack(spacing: 8) {
            Text(kind == .advert ? "" : ScrubMath.timestamp(elapsed))
                .frame(width: Self.labelWidth, alignment: .trailing)
                .accessibilityHidden(true)
            bar(elapsed: elapsed)
                .frame(width: Self.barWidth, height: Self.height)
            trailingLabel(elapsed: elapsed, duration: duration)
                .frame(width: Self.labelWidth, alignment: .leading)
        }
        .font(.system(size: 11).monospacedDigit())
        .foregroundStyle(.white.opacity(timeOpacity(active: active)))
        .animation(look.reduceMotion ? nil : .easeOut(duration: 0.12), value: active)
    }

    @ViewBuilder
    private func trailingLabel(elapsed: Double, duration: Double) -> some View {
        if kind == .advert {
            EmptyView()
        } else {
            let text = ScrubMath.trailingLabel(elapsed: elapsed, duration: duration, showsRemaining: showsRemaining)
            Button { showsRemaining.toggle(); PlayerInteraction.shared.noteClick() } label: {
                Text(text).contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .help(showsRemaining ? "Show the song\u{2019}s length" : "Show the time left")
            .accessibilityLabel(showsRemaining ? "Time remaining \(ScrubMath.timestamp(max(0, duration - elapsed)))"
                                               : "Song length \(ScrubMath.timestamp(duration))")
            .accessibilityAction(named: showsRemaining ? "Show total time" : "Show time remaining") { showsRemaining.toggle() }
        }
    }

    // MARK: The bar

    private var thickness: CGFloat {
        guard kind == .seekable else { return 3 }
        return dragging ? 8 : active ? 6 : 3
    }

    private func bar(elapsed: Double) -> some View {
        let context = controller.context
        let session = controller.session
        let duration = context.duration
        let playX = CGFloat(session?.displayX ?? ScrubMath.x(time: elapsed, duration: duration))
        let overshoot = CGFloat(session?.overshoot ?? 0)
        let showsStructure = active && context.hasLyrics
        let gaps = showsStructure ? context.breaks.map { CGFloat(ScrubMath.x(time: $0, duration: duration)) } : []
        let unplayed = look.increaseContrast ? 0.40 : 0.22
        let fillOpacity = kind == .advert ? 0.5 : isPlaying || dragging ? 1.0 : 0.6
        let snappedTime = session?.snappedTime ?? controller.hover.flatMap { hover in hover.snapped.map { context.targets[$0] } }
        return ZStack(alignment: .leading) {
            ZStack(alignment: .leading) {
                TrackShape(gaps: gaps, thickness: thickness)
                    .fill(Color.white.opacity(unplayed))
                if showsStructure, !context.repeated.isEmpty {
                    // Lines that come back (most often the chorus), a shade lighter.
                    TrackShape(gaps: gaps, thickness: thickness)
                        .fill(Color.white.opacity(0.10))
                        .mask(alignment: .leading) {
                            Canvas { canvas, size in
                                for span in context.repeated {
                                    let from = CGFloat(ScrubMath.x(time: span.lowerBound, duration: duration))
                                    let to = CGFloat(ScrubMath.x(time: span.upperBound, duration: duration))
                                    canvas.fill(Path(CGRect(x: from, y: 0, width: max(1, to - from), height: size.height)),
                                                with: .color(.white))
                                }
                            }
                        }
                        .transition(.opacity)
                }
                TrackShape(gaps: gaps, thickness: thickness)
                    .fill(dragging ? AnyShapeStyle(tint.opacity(0.9)) : AnyShapeStyle(Color.white.opacity(0.92)))
                    .mask(alignment: .leading) {
                        Rectangle().frame(width: max(0, playX + max(0, overshoot)))
                    }
                    .opacity(fillOpacity)
                if showsStructure {
                    LineTicks(ticks: context.ticks.map { CGFloat($0) },
                              highlighted: snappedTime.map { CGFloat(ScrubMath.x(time: $0, duration: duration)) },
                              thickness: thickness, opacity: look.increaseContrast ? 0.6 : 0.30)
                        .transition(.opacity)
                }
            }
            .frame(width: Self.barWidth, height: Self.height)
            // The rubber band: the bar stretches a little past an end.
            .scaleEffect(x: 1 + abs(overshoot) / Self.barWidth, y: 1, anchor: overshoot < 0 ? .trailing : .leading)
            if kind == .seekable, let hover = controller.hover, session == nil {
                Rectangle().fill(.white.opacity(0.5)).frame(width: 1, height: 14)
                    .offset(x: CGFloat(hover.x) - 0.5)
                    .allowsHitTesting(false)
            }
            if kind == .seekable, active {
                Circle().fill(.white)
                    .frame(width: dragging ? 12 : 10, height: dragging ? 12 : 10)
                    .shadow(color: .black.opacity(0.35), radius: 3, y: 1)
                    .offset(x: playX - (dragging ? 6 : 5))
                    .transition(.opacity)
                    .allowsHitTesting(false)
            }
            if kind == .seekable, preview == nil {
                ScrubberEvents(controller: controller)
                    .frame(width: Self.barWidth + 16, height: Self.height)
                    .offset(x: -8)
            }
            if kind == .advert {
                Text("Advertisement").font(.system(size: 10, weight: .medium))
                    .foregroundStyle(.white.opacity(0.55))
                    .frame(width: Self.barWidth).offset(y: -11)
            }
        }
        .frame(width: Self.barWidth, height: Self.height, alignment: .leading)
        .animation(look.reduceMotion ? nil : .spring(duration: 0.18, bounce: 0), value: thickness)
        .animation(look.reduceMotion ? .linear(duration: 0.1) : .spring(duration: 0.18, bounce: 0), value: active)
        .overlay(alignment: .top) { capsules(elapsed: elapsed) }
        .accessibilityElement(children: .ignore)
        .modifier(ScrubAccessibility(controller: controller, kind: kind, elapsed: elapsed))
    }

    // MARK: Capsules

    /// Time and lyric under the pointer (or the drag's target, or the time
    /// a key just moved to), clamped inside the bar, above it.
    @ViewBuilder
    private func capsules(elapsed: Double) -> some View {
        let context = controller.context
        let duration = context.duration
        let shown: (x: Double, time: Double)? = {
            if let session = controller.session { return (session.displayX, session.targetTime) }
            if let hover = controller.hover { return (hover.x, hover.time) }
            if let flash = controller.flash { return (ScrubMath.x(time: flash, duration: duration), flash) }
            return nil
        }()
        ZStack {
            if kind == .seekable, let shown {
                ClampedCenter(x: CGFloat(shown.x)) {
                    VStack(spacing: 4) {
                        lyricCapsule(at: shown.time)
                        VStack(spacing: 0) {
                            Text(ScrubMath.timestamp(shown.time))
                                .font(.system(size: 11, weight: .semibold).monospacedDigit())
                            if let rate = controller.session?.rateLabel {
                                Text(rate).font(.system(size: 9, weight: .medium)).opacity(0.75)
                            }
                        }
                        .foregroundStyle(.white)
                        .padding(.horizontal, 8).padding(.vertical, 3)
                        .playerCapsule(look)
                    }
                    .fixedSize()
                }
                .frame(width: Self.barWidth)
                .transition(look.reduceMotion ? .opacity : .opacity.combined(with: .offset(y: 4)))
            }
        }
        .alignmentGuide(.top) { $0[.bottom] + 2 }
        .animation(shown == nil ? .easeIn(duration: 0.10) : .easeOut(duration: 0.12), value: shown == nil)
        .allowsHitTesting(false)
    }

    @ViewBuilder
    private func lyricCapsule(at time: Double) -> some View {
        let context = controller.context
        if context.hasLyrics {
            switch ScrubMath.lyric(at: time, lines: context.lines) {
            case .line(let index):
                let line = context.lines[index]
                let replaced = context.romanization == .replace ? line.romanized : nil
                VStack(spacing: 1) {
                    Text(replaced ?? line.text)
                        .font(.system(size: 13, weight: .semibold))
                        .lineLimit(1)
                        .contentTransition(.opacity)
                    if context.romanization == .below, let roman = line.romanized {
                        Text(roman).font(.system(size: 11)).foregroundStyle(.white.opacity(0.6)).lineLimit(1)
                    }
                }
                .animation(.easeOut(duration: 0.12), value: index)
                .foregroundStyle(.white)
                .frame(maxWidth: 420)
                .padding(.horizontal, 12).padding(.vertical, 5)
                .playerCapsule(look)
            case .instrumental:
                Text("\u{266A} instrumental").font(.system(size: 13, weight: .semibold).italic())
                    .foregroundStyle(.white.opacity(0.6))
                    .padding(.horizontal, 12).padding(.vertical, 5)
                    .playerCapsule(look)
            case .none:
                EmptyView()
            }
        }
    }
}

/// VoiceOver for the bar: one adjustable element, stepping by lyric line
/// when there are synced lyrics, else by 10 s.
private struct ScrubAccessibility: ViewModifier {
    @ObservedObject var controller: ScrubController
    let kind: ScrubRowKind
    let elapsed: Double

    func body(content: Content) -> some View {
        let context = controller.context
        let line: String? = {
            guard context.hasLyrics, case .line(let index) = ScrubMath.lyric(at: elapsed, lines: context.lines) else { return nil }
            return context.lines[index].text
        }()
        if kind == .seekable {
            content
                .accessibilityLabel("Playback position")
                .accessibilityValue(ScrubMath.accessibilityValue(elapsed: elapsed, duration: context.duration, line: line))
                .accessibilityAdjustableAction { direction in
                    switch direction {
                    case .increment: controller.seekLine(forward: true)
                    case .decrement: controller.seekLine(forward: false)
                    @unknown default: break
                    }
                }
                .accessibilityAction(named: "Restart song") { controller.seekNow(to: 0, flash: false) }
                .accessibilityAction(named: "Next line") { controller.seekLine(forward: true) }
                .accessibilityAction(named: "Previous line") { controller.seekLine(forward: false) }
        } else {
            content
                .accessibilityLabel(kind == .advert ? "Advertisement" : "Playback position")
                .accessibilityValue(ScrubMath.accessibilityValue(elapsed: elapsed, duration: context.duration, line: nil))
        }
    }
}

/// The track: a capsule, broken by 2 pt gaps where sections start.
struct TrackShape: Shape {
    var gaps: [CGFloat]
    var thickness: CGFloat

    var animatableData: CGFloat {
        get { thickness }
        set { thickness = newValue }
    }

    func path(in rect: CGRect) -> Path {
        var path = Path()
        let y = rect.midY - thickness / 2
        var start = rect.minX
        let radius = thickness / 2
        for gap in gaps.sorted() where gap > rect.minX + 2 && gap < rect.maxX - 2 {
            let end = rect.minX + gap - 1
            if end > start { path.addRoundedRect(in: CGRect(x: start, y: y, width: end - start, height: thickness),
                                                 cornerSize: CGSize(width: radius, height: radius)) }
            start = rect.minX + gap + 1
        }
        if rect.maxX > start {
            path.addRoundedRect(in: CGRect(x: start, y: y, width: rect.maxX - start, height: thickness),
                                cornerSize: CGSize(width: radius, height: radius))
        }
        return path
    }
}

/// 1 × 3 pt ticks under the track at each line start; the one the knob
/// is snapped to is 2 pt wide and white.
private struct LineTicks: View {
    let ticks: [CGFloat]
    let highlighted: CGFloat?
    let thickness: CGFloat
    let opacity: Double

    var body: some View {
        Canvas { context, size in
            let top = size.height / 2 + thickness / 2 + 2
            for x in ticks {
                context.fill(Path(CGRect(x: x - 0.5, y: top, width: 1, height: 3)), with: .color(.white.opacity(opacity)))
            }
            if let highlighted {
                context.fill(Path(CGRect(x: highlighted - 1, y: top, width: 2, height: 3)), with: .color(.white))
            }
        }
        .allowsHitTesting(false)
    }
}

/// Places one view centred on `x`, but never past either edge.
struct ClampedCenter: Layout {
    var x: CGFloat

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let size = subviews.first?.sizeThatFits(.unspecified) ?? .zero
        return CGSize(width: proposal.width ?? size.width, height: size.height)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        guard let view = subviews.first else { return }
        let size = view.sizeThatFits(.unspecified)
        let left = min(max(bounds.minX + x - size.width / 2, bounds.minX), max(bounds.minX, bounds.maxX - size.width))
        view.place(at: CGPoint(x: left, y: bounds.minY), proposal: ProposedViewSize(size))
    }
}

/// A radio or stream: no times, a "LIVE" capsule whose dot breathes while
/// the player is up (still under Reduce Motion, and while hidden).
private struct LiveCapsule: View {
    let shown: Bool
    @Environment(\.playerLook) private var look

    var body: some View {
        TimelineView(.animation(minimumInterval: 1.0 / 20, paused: !shown || look.reduceMotion)) { timeline in
            let phase = timeline.date.timeIntervalSinceReferenceDate.truncatingRemainder(dividingBy: 2) / 2
            let breath = look.reduceMotion ? 1 : 0.75 + 0.25 * cos(phase * 2 * .pi)
            HStack(spacing: 6) {
                Circle().fill(Color.red).frame(width: 6, height: 6).opacity(breath)
                Text("LIVE").font(.system(size: 11, weight: .bold)).foregroundStyle(.white)
            }
            .padding(.horizontal, 10).padding(.vertical, 4)
            .playerCapsule(look)
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("Live stream")
    }
}

// MARK: - Mouse events

/// The bar's mouse handling, in AppKit: hover arrives through a tracking
/// area that works while Lunalyr is in the background and its panel is not
/// key (`.activeAlways`), and a press keeps receiving drags wherever the
/// pointer goes, so fine-scrubbing far below the bar keeps working.
private struct ScrubberEvents: NSViewRepresentable {
    let controller: ScrubController

    func makeNSView(context: Context) -> ScrubberEventView {
        let view = ScrubberEventView()
        view.controller = controller
        return view
    }

    func updateNSView(_ view: ScrubberEventView, context: Context) {
        view.controller = controller
    }
}

final class ScrubberEventView: NSView {
    weak var controller: ScrubController?
    /// The bar starts this far in from the view's leading edge.
    static let inset: CGFloat = 8
    private var tracking: NSTrackingArea?

    override var isFlipped: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override var needsPanelToBecomeKey: Bool { true }
    override var mouseDownCanMoveWindow: Bool { false }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let tracking { removeTrackingArea(tracking) }
        let area = NSTrackingArea(rect: .zero, options: [.mouseMoved, .mouseEnteredAndExited, .activeAlways, .inVisibleRect],
                                  owner: self, userInfo: nil)
        addTrackingArea(area)
        tracking = area
    }

    override func resetCursorRects() {
        addCursorRect(bounds, cursor: .pointingHand)
    }

    private func local(_ event: NSEvent) -> (x: Double, y: Double) {
        let point = convert(event.locationInWindow, from: nil)
        return (Double(point.x - Self.inset), Double(point.y - bounds.midY))
    }

    private func flags(_ event: NSEvent) -> (option: Bool, command: Bool) {
        (event.modifierFlags.contains(.option), event.modifierFlags.contains(.command))
    }

    override func mouseEntered(with event: NSEvent) { mouseMoved(with: event) }

    override func mouseMoved(with event: NSEvent) {
        let p = local(event)
        controller?.pointerMoved(x: p.x, y: p.y, command: flags(event).command)
    }

    override func mouseExited(with event: NSEvent) {
        controller?.pointerExited()
    }

    override func mouseDown(with event: NSEvent) {
        let p = local(event), f = flags(event)
        controller?.mouseDown(x: p.x, y: p.y, option: f.option, command: f.command)
    }

    override func mouseDragged(with event: NSEvent) {
        let p = local(event), f = flags(event)
        controller?.mouseDragged(x: p.x, y: p.y, option: f.option, command: f.command)
    }

    override func mouseUp(with event: NSEvent) {
        let p = local(event), f = flags(event)
        controller?.mouseUp(x: p.x, y: p.y, option: f.option, command: f.command)
        if !bounds.contains(convert(event.locationInWindow, from: nil)) { controller?.pointerExited() }
    }
}
