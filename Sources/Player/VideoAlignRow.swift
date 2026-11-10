import AppKit
import SwiftUI

// Align mode in the desktop player: while a music video is the wallpaper, a
// small film button beside the seek bar turns the seek row into the video's
// own bar. Dragging it moves only the video (the song plays on) until the
// picture matches the music; the release keeps that offset for the video.
// The rules are `MusicVideoAlign*`; these views only draw and forward.

/// The seek row, or the video's bar in align mode, with the film button
/// beside it while the wallpaper is a music video.
struct SeekOrAlignRow: View {
    @ObservedObject var musicVideo: MusicVideoController
    let scrub: ScrubController
    let kind: ScrubRowKind
    let tint: Color
    let isPlaying: Bool
    let shown: Bool
    @Binding var showsRemaining: Bool
    @Environment(\.playerMaskRender) private var maskRender

    var body: some View {
        Group {
            if musicVideo.isAligning {
                VideoAlignLiveRow(musicVideo: musicVideo, tint: tint, shown: shown)
            } else {
                ScrubRow(controller: scrub, kind: kind, tint: tint, isPlaying: isPlaying, shown: shown,
                         showsRemaining: $showsRemaining)
            }
        }
        .overlay(alignment: .trailing) {
            if offersAlign {
                VideoAlignButton(on: musicVideo.isAligning,
                                 reason: { musicVideo.isAligning ? nil : musicVideo.alignAvailability.reason },
                                 tint: tint) {
                    PlayerInteraction.shared.noteClick()
                    musicVideo.toggleAlign()
                }
                .offset(x: VideoAlignButton.outset)
            }
        }
    }

    /// Only while the wallpaper is a music video and a song plays that
    /// could have one.
    private var offersAlign: Bool {
        guard musicVideo.status != .off else { return false }
        if musicVideo.isAligning { return true }
        switch kind {
        case .seekable, .readOnly, .elapsedOnly: return true
        case .live, .advert, .hidden: return false
        }
    }
}

/// The film button: starts align mode, and ends it (keeping what was set).
/// Faint at rest, like the hairline. When the video can't be lined up it is
/// dimmed, and a click says why for a moment (a disabled button's help
/// would not show).
struct VideoAlignButton: View {
    /// How far right of the seek row's trailing edge it sits.
    static let outset: CGFloat = 34
    let on: Bool
    /// Why the video can't be lined up now, nil when it can. Asked again on
    /// the click: the song may have started since this was drawn.
    let reason: () -> String?
    let tint: Color
    let action: () -> Void
    @Environment(\.playerMaskRender) private var maskRender
    @Environment(\.playerLook) private var look
    @State private var shownReason: String?
    @State private var reasonTask: Task<Void, Never>?

    var body: some View {
        let reason = self.reason()
        if maskRender {
            Color.black.opacity(0.3).frame(width: 28, height: 28)
        } else {
            Button {
                if let now = self.reason() { say(now) } else { action() }
            } label: {
                Image(systemName: on ? "film.fill" : "film")
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundStyle(on ? tint : .white.opacity(reason == nil ? (look.increaseContrast ? 0.9 : 0.6) : 0.25))
                    .frame(width: 28, height: 28)
                    .contentShape(Rectangle())
            }
            .buttonStyle(TransportButtonStyle())
            .help(on ? "Done lining up the video" : reason ?? "Line up the music video: drag it until the picture matches the music")
            .overlay(alignment: .topTrailing) {
                if let shownReason {
                    Text(shownReason)
                        .font(.system(size: 11, weight: .medium))
                        .foregroundStyle(.white)
                        .padding(.horizontal, 10).padding(.vertical, 4)
                        .playerCapsule(look)
                        .fixedSize()
                        .alignmentGuide(.top) { $0[.bottom] + 4 }
                        .transition(.opacity)
                        .allowsHitTesting(false)
                }
            }
            .animation(look.reduceMotion ? nil : .easeOut(duration: 0.15), value: shownReason)
            .accessibilityLabel(on ? "Done aligning video" : "Align video")
            .accessibilityHint(reason ?? (on ? "Keeps the video\u{2019}s timing" : "Turns the seek bar into the video\u{2019}s own bar"))
        }
    }

    private func say(_ reason: String) {
        shownReason = reason
        reasonTask?.cancel()
        reasonTask = Task { @MainActor in
            try? await Task.sleep(nanoseconds: 2_500_000_000)
            guard !Task.isCancelled else { return }
            shownReason = nil
        }
    }
}

/// The video's bar, redrawn while the song plays so the knob moves with it.
private struct VideoAlignLiveRow: View {
    @ObservedObject var musicVideo: MusicVideoController
    let tint: Color
    let shown: Bool
    @Environment(\.playerMaskRender) private var maskRender

    var body: some View {
        if maskRender {
            Color.black.opacity(0.3).frame(width: ScrubRow.rowWidth, height: ScrubRow.height + 16)
        } else {
            let dragging = musicVideo.alignDrag != nil
            TimelineView(.animation(minimumInterval: dragging ? 1.0 / 30 : 0.1, paused: !shown)) { _ in
                if let display = musicVideo.alignDisplay() {
                    VideoAlignRow(display: display, tint: tint, events: VideoAlignEvents(musicVideo: musicVideo))
                }
            }
            .frame(width: ScrubRow.rowWidth, height: ScrubRow.height)
        }
    }
}

/// What the video's bar does with the pointer and VoiceOver.
@MainActor
struct VideoAlignEvents {
    let musicVideo: MusicVideoController
    var haptics: PlayerHaptics = .system

    func down(x: Double, y: Double, option: Bool) {
        PlayerInteraction.shared.dragging = true
        PlayerInteraction.shared.noteClick()
        musicVideo.alignMouseDown(x: x, y: y, width: ScrubMath.barWidth, option: option)
    }

    func dragged(x: Double, y: Double, option: Bool) {
        if musicVideo.alignMouseDragged(x: x, y: y, option: option) { haptics.alignment() }
    }

    func up(x: Double, y: Double, option: Bool) {
        musicVideo.alignMouseUp(x: x, y: y, option: option)
        PlayerInteraction.shared.dragging = false
        PlayerInteraction.shared.noteClick()
    }

    func nudge(_ delta: Double) { musicVideo.alignNudge(by: delta) }
}

/// The video's bar, 560 × 28 pt like the seek row: the video's time and
/// length at the sides; the whole video as the track, the song's span over
/// it in the player's colour, the knob where the video is now, and above it
/// the offset ("Video +3.4 s").
struct VideoAlignRow: View {
    let display: MusicVideoAlignDisplay
    let tint: Color
    /// Nil in previews: nothing to press.
    var events: VideoAlignEvents?
    @Environment(\.playerLook) private var look

    static let thickness: CGFloat = 6

    var body: some View {
        HStack(spacing: 8) {
            Text(ScrubMath.timestamp(display.videoTime))
                .frame(width: ScrubRow.labelWidth, alignment: .trailing)
                .accessibilityHidden(true)
            bar
                .frame(width: ScrubRow.barWidth, height: ScrubRow.height)
            Text(ScrubMath.timestamp(display.videoLength))
                .frame(width: ScrubRow.labelWidth, alignment: .leading)
                .accessibilityHidden(true)
        }
        .font(.system(size: 11).monospacedDigit())
        .foregroundStyle(.white.opacity(look.increaseContrast ? 0.95 : 0.8))
        .frame(width: ScrubRow.rowWidth, height: ScrubRow.height)
    }

    private var bar: some View {
        let layout = display.layout(width: ScrubMath.barWidth)
        let knob = CGFloat(layout.knob)
        let spanStart = CGFloat(layout.spanStart), spanEnd = CGFloat(layout.spanEnd)
        let thickness = display.dragging ? Self.thickness + 2 : Self.thickness
        let knobSize: CGFloat = display.dragging ? 14 : 12
        return ZStack(alignment: .leading) {
            // The whole video.
            Capsule().fill(.white.opacity(look.increaseContrast ? 0.4 : 0.22))
                .frame(width: ScrubRow.barWidth, height: thickness)
            // The video so far, lightly.
            Capsule().fill(.white.opacity(0.18))
                .frame(width: max(0, knob), height: thickness)
            // The song inside the video.
            RoundedRectangle(cornerRadius: thickness / 2)
                .fill(tint.opacity(display.dragging ? 0.95 : 0.75))
                .frame(width: max(2, spanEnd - spanStart), height: thickness)
                .offset(x: spanStart)
            // Where the song starts and ends in the video.
            Rectangle().fill(.white.opacity(0.7)).frame(width: 1, height: 14).offset(x: spanStart - 0.5)
            Rectangle().fill(.white.opacity(0.7)).frame(width: 1, height: 14).offset(x: spanEnd - 0.5)
            Circle().fill(.white)
                .frame(width: knobSize, height: knobSize)
                .overlay(Circle().strokeBorder(tint, lineWidth: display.dragging ? 2 : 0))
                .shadow(color: .black.opacity(0.4), radius: 3, y: 1)
                .offset(x: knob - knobSize / 2)
                .allowsHitTesting(false)
            if let events {
                VideoAlignEventSurface(events: events)
                    .frame(width: ScrubRow.barWidth + 16, height: ScrubRow.height)
                    .offset(x: -8)
            }
        }
        .frame(width: ScrubRow.barWidth, height: ScrubRow.height, alignment: .leading)
        .opacity(display.available ? 1 : 0.6)
        .animation(look.reduceMotion ? nil : .spring(duration: 0.18, bounce: 0), value: display.dragging)
        .overlay(alignment: .top) { capsule(knob: knob) }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("Music video position")
        .accessibilityValue(MusicVideoAlignBar.spoken(offset: display.offset))
        .accessibilityHint("Adjust to move the video a tenth of a second")
        .accessibilityAdjustableAction { direction in
            switch direction {
            case .increment: events?.nudge(MusicVideoAlignKey.step)
            case .decrement: events?.nudge(-MusicVideoAlignKey.step)
            @unknown default: break
            }
        }
    }

    /// The offset over the knob: what the release keeps.
    private func capsule(knob: CGFloat) -> some View {
        ClampedCenter(x: knob) {
            VStack(spacing: 0) {
                Text(display.title)
                    .font(.system(size: 12, weight: .semibold).monospacedDigit())
                    .contentTransition(.numericText())
                Text(display.detail)
                    .font(.system(size: 9, weight: .medium))
                    .opacity(0.75)
            }
            .foregroundStyle(.white)
            .padding(.horizontal, 10).padding(.vertical, 3)
            .playerCapsule(look)
            .fixedSize()
        }
        .frame(width: ScrubRow.barWidth)
        .alignmentGuide(.top) { $0[.bottom] + 2 }
        .allowsHitTesting(false)
    }
}

/// The line under the video's bar in align mode, in place of the
/// transport: back to automatic, the keys, Cancel and Done.
struct VideoAlignControls: View {
    let canReset: Bool
    let tint: Color
    let onReset: () -> Void
    let onCancel: () -> Void
    let onDone: () -> Void
    @Environment(\.playerMaskRender) private var maskRender
    @Environment(\.playerLook) private var look

    static let hint = "Drag down for fine steps \u{00B7} \u{2190} \u{2192} 0.1 s \u{00B7} \u{21E7} 1 s"

    var body: some View {
        if maskRender {
            Color.black.opacity(0.3).frame(width: PlayerControlsRow.width, height: PlayerControlsRow.height)
        } else {
            ZStack {
                HStack(spacing: 14) {
                    Button(action: onReset) {
                        Label("Automatic", systemImage: "arrow.counterclockwise")
                            .font(.system(size: 11, weight: .medium))
                    }
                    .buttonStyle(.plain)
                    .opacity(canReset ? 0.85 : 0.35)
                    .disabled(!canReset)
                    .help("Forget your timing for this video and line it up automatically again")
                    Spacer(minLength: 0)
                    Button("Cancel", action: onCancel)
                        .buttonStyle(.plain)
                        .font(.system(size: 12, weight: .medium))
                        .opacity(0.8)
                        .help("Put the video\u{2019}s timing back as it was (Esc)")
                    Button(action: onDone) {
                        Text("Done").font(.system(size: 12, weight: .semibold))
                            .padding(.horizontal, 12).padding(.vertical, 4)
                            .background(tint.opacity(0.9), in: Capsule())
                    }
                    .buttonStyle(.plain)
                    .help("Keep this timing for the video (Return)")
                }
                Text(Self.hint)
                    .font(.system(size: 11))
                    .foregroundStyle(.white.opacity(look.increaseContrast ? 0.9 : 0.6))
                    .accessibilityHidden(true)
            }
            .foregroundStyle(.white)
            .shadow(color: .black.opacity(0.5), radius: 4)
            .frame(width: PlayerControlsRow.width, height: PlayerControlsRow.height)
        }
    }
}

/// The transport, or the align controls while the video is being lined up.
struct ControlsOrAlign<Controls: View>: View {
    @ObservedObject var musicVideo: MusicVideoController
    let tint: Color
    @ViewBuilder let controls: () -> Controls

    var body: some View {
        if musicVideo.isAligning {
            VideoAlignControls(canReset: musicVideo.offsetOrigin == .manual, tint: tint,
                               onReset: { PlayerInteraction.shared.noteClick(); musicVideo.alignReset() },
                               onCancel: { PlayerInteraction.shared.noteClick(); musicVideo.alignCancel() },
                               onDone: { PlayerInteraction.shared.noteClick(); musicVideo.alignDone() })
        } else {
            controls()
        }
    }
}

// MARK: - Mouse events

/// The video bar's pointer handling in AppKit, as the seek bar's: a press
/// keeps receiving drags wherever the pointer goes, so dragging far below
/// the bar for fine steps keeps working.
private struct VideoAlignEventSurface: NSViewRepresentable {
    let events: VideoAlignEvents

    func makeNSView(context: Context) -> VideoAlignEventView {
        let view = VideoAlignEventView()
        view.events = events
        return view
    }

    func updateNSView(_ view: VideoAlignEventView, context: Context) { view.events = events }
}

final class VideoAlignEventView: NSView {
    var events: VideoAlignEvents?

    override var isFlipped: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override var needsPanelToBecomeKey: Bool { true }
    override var mouseDownCanMoveWindow: Bool { false }

    override func resetCursorRects() {
        addCursorRect(bounds, cursor: .resizeLeftRight)
    }

    private func local(_ event: NSEvent) -> (x: Double, y: Double, option: Bool) {
        let point = convert(event.locationInWindow, from: nil)
        return (Double(point.x - ScrubberEventView.inset), Double(point.y - bounds.midY),
                event.modifierFlags.contains(.option))
    }

    override func mouseDown(with event: NSEvent) {
        let p = local(event)
        events?.down(x: p.x, y: p.y, option: p.option)
    }

    override func mouseDragged(with event: NSEvent) {
        let p = local(event)
        events?.dragged(x: p.x, y: p.y, option: p.option)
    }

    override func mouseUp(with event: NSEvent) {
        let p = local(event)
        events?.up(x: p.x, y: p.y, option: p.option)
    }

    override func flagsChanged(with event: NSEvent) {
        super.flagsChanged(with: event)
        events?.musicVideo.alignModifiersChanged(option: event.modifierFlags.contains(.option))
    }
}
