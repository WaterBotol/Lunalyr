import SwiftUI

/// The Animation inspector: how words enter (and, under More options, how
/// early the next word's preview appears), the orb in instrumental breaks,
/// word symbols and action words, and Constellation's and Echo's own
/// controls while that layout is in use. Like Text, it edits the Desktop or
/// the Floating style, as the switch under the picture says; word controls
/// are kept per layout, chosen under the picture.
@MainActor
struct AnimationPage: View {
    @Model(\.settings) private var s
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @ObservedObject private var floating: FloatingLyricsOptions

    init(floating: FloatingLyricsOptions? = nil) {
        self.floating = floating ?? .shared
    }

    /// The layout the word controls read from. Custom Mix saves them
    /// for every layout it picks; Scattered, its general-purpose pick, stands
    /// in when reading them back.
    private var style: LyricComposition { s.lyricComposition ?? .scattered }
    private var automatic: Bool { s.lyricComposition == nil }

    var body: some View {
        LookInspectorForm {
            InspectorTitle(title: "Animation",
                           subtitle: floating.isFloating ? "The Floating style. The desktop keeps its own." : nil)
            wordEntrances
            InspectorSection("Instrumental breaks") {
                Toggle("Show a glowing orb during instrumental breaks", isOn: $s.showInterludes)
                    .toggleStyle(StudioCheckboxStyle())
                HStack(spacing: 12) {
                    ChoiceMenu("Orb style", selection: $s.thinkingOrbStyle,
                               selectedTitle: { ThinkingOrb.title(of: $0) }) {
                        ForEach(ThinkingOrb.styles, id: \.self) { Text(ThinkingOrb.title(of: $0)).tag($0) }
                    }
                    OrbStylePreview(style: s.thinkingOrbStyle)
                }
            }
            WordAccentsSection()
        }
        // Sub-options glide in and out with the choice that shows them.
        .lunalyrReveal(s.lyricComposition)
        .lunalyrReveal(floating.surface)
        .lunalyrReveal(s.wordSymbols)
        .lunalyrReveal(s.actionWords)
        .lunalyrReveal(s.previewsNextWord(in: style))
        .lunalyrReveal(EntranceSupport.note(selected: s.arrival(in: style), layout: s.lyricComposition, mix: chosenMix))
    }

    /// Constellation's and Echo's own fine-tuning, under Word entrances'
    /// More options (they were sections of their own until 26 Nov 2026).
    /// Custom Mix can pick either, so both stay reachable there.
    private var showsConstellation: Bool { style.hasConnections || automatic }
    private var showsEcho: Bool { style == .echo || automatic }

    private func layoutHeader(_ title: String) -> some View {
        Text(title)
            .font(.system(size: 12, weight: .semibold))
            .foregroundStyle(Color.studio(.ink2))
            .accessibilityAddTraits(.isHeader)
    }

    @ViewBuilder private var layoutTuning: some View {
        if showsConstellation {
            layoutHeader("Constellation")
            ValueSlider("Spread", value: $s.constellationSpread, in: 0.4...1.8, step: 0.01,
                        format: SettingsFormat.percent)
            ValueSlider("Links", value: $s.constellationLinkOpacity, in: 0...0.6, step: 0.01,
                        format: SettingsFormat.percent)
        }
        if showsEcho {
            layoutHeader("Echo")
            ValueSlider("Repeats", value: $s.echoStrength, in: 0...1.8, step: 0.01,
                        format: SettingsFormat.percent)
        }
        if automatic {
            InspectorCaption("Constellation and Echo use these when Custom Mix picks them.")
        }
    }

    private var wordEntrances: some View {
        InspectorSection("Word entrances") {
            ChoiceMenu("Entrance", selection: Binding(get: { s.arrival(in: style) },
                                                      set: { s.wordArrivalByStyle[s.wordControlsKey] = $0.rawValue }),
                       selectedTitle: \.title) {
                // An entrance this layout leaves out for word-timed lyrics is
                // marked: lines without word timing still enter with it.
                ForEach(WordArrival.allCases) { Text(EntranceSupport.menuTitle($0, in: s.lyricComposition)).tag($0) }
            }
            if let note = EntranceSupport.note(selected: s.arrival(in: style), layout: s.lyricComposition,
                                               mix: chosenMix) {
                InspectorCaption(note)
                    .revealRow()
            }
            if reduceMotion {
                InspectorCaption(WorkspaceCopy.reduceMotionEntrances)
            }
            if s.arrival(in: style) == .mix {
                ForEach(WordArrival.mixChoices) { option in
                    Toggle(option.title, isOn: Binding(
                        get: { s.wordEntranceMix.contains(option.rawValue) },
                        set: { enabled in
                            if enabled { s.wordEntranceMix.append(option.rawValue) }
                            else { s.wordEntranceMix.removeAll { $0 == option.rawValue } }
                        }
                    ))
                }
            }
            Toggle("Reveal sung words", isOn: Binding(get: { s.revealsWords(in: style) },
                                                       set: { s.wordRevealByStyle[s.wordControlsKey] = $0 }))
                .disabled(s.requiresWordEntrances(in: style))
            if let reason = WorkspaceCopy.revealLockReason(s.wordRevealLock(in: style)) {
                DisabledReason(reason)
            }
            Toggle("Faint next-word preview", isOn: Binding(get: { s.previewsNextWord(in: style) },
                                                             set: { s.nextWordPreviewByStyle[s.wordControlsKey] = $0 }))
            InspectorCaption(automatic ? "These controls apply to layouts picked by Custom Mix."
                                       : "These controls apply to the selected layout.")
            if s.previewsNextWord(in: style) || showsConstellation || showsEcho {
                MoreOptions {
                    if s.previewsNextWord(in: style) {
                        ValueSlider("Preview lead", value: $s.nextWordPreviewLead, in: 0.15...1.25, step: 0.05,
                                    format: SettingsFormat.seconds,
                                    spoken: { SettingsFormat.spokenSeconds($0) + " before the next word" })
                            .help("How long before each word its faint preview appears")
                            .revealRow()
                    }
                    layoutTuning
                }
                .revealRow()
            }
        }
    }

    /// The custom mix's entrances, in the order the page lists them.
    private var chosenMix: [WordArrival] {
        WordArrival.mixChoices.filter { s.wordEntranceMix.contains($0.rawValue) }
    }

}

/// A small live thinking orb beside the "Orb style" picker. It animates only
/// while its window is on screen, and holds upstream's still frame with
/// Reduce Motion (the orb's own rule).
private struct OrbStylePreview: View {
    let style: String
    @State private var visible = false
    @State private var started = Date()
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        TimelineView(.animation(minimumInterval: 1.0 / 30, paused: !visible)) { timeline in
            let elapsed = timeline.date.timeIntervalSince(started)
            // A Mix shows a new style every four seconds, as it would break by break.
            let index = UInt64(max(0, elapsed / 4))
            let previous = index > 0 ? ThinkingOrb.state(style: style, seed: index - 1) : nil
            let state = ThinkingOrb.state(style: style, seed: index, avoiding: ThinkingOrb.normalised(style) == ThinkingOrb.mixStyle ? previous : nil)
            ThinkingOrbView(cue: InstrumentalCue(elapsed: elapsed, opacity: 1, progress: nil, seed: index),
                            accent: scheme == .dark ? .white : .black, baseSize: 40, state: state)
        }
        .frame(width: 64, height: 64)
        .background(WindowVisibility(visible: $visible))
        .accessibilityHidden(true)
    }
}

/// Reports whether the hosting window is on screen (not closed, minimised or covered).
private struct WindowVisibility: NSViewRepresentable {
    @Binding var visible: Bool

    func makeNSView(context: Context) -> NSView {
        let view = TrackingView()
        view.onChange = { visible = $0 }
        return view
    }

    func updateNSView(_ view: NSView, context: Context) {}

    final class TrackingView: NSView {
        var onChange: (Bool) -> Void = { _ in }
        private var tokens: [NSObjectProtocol] = []

        override func viewDidMoveToWindow() {
            tokens.forEach(NotificationCenter.default.removeObserver)
            tokens = []
            guard let window else { return onChange(false) }
            let center = NotificationCenter.default
            for name in [NSWindow.didChangeOcclusionStateNotification, NSWindow.didMiniaturizeNotification,
                         NSWindow.didDeminiaturizeNotification, NSWindow.willCloseNotification] {
                tokens.append(center.addObserver(forName: name, object: window, queue: .main) { [weak self] _ in
                    self?.report()
                })
            }
            report()
        }

        private func report() {
            guard let window else { return onChange(false) }
            onChange(window.isVisible && !window.isMiniaturized && window.occlusionState.contains(.visible))
        }

        deinit { tokens.forEach(NotificationCenter.default.removeObserver) }
    }
}
