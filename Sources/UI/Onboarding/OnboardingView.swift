import AppKit
import SwiftUI

/// Onboarding's whole window: the step, and beneath it the step dots, Back
/// and Continue. Steps slide sideways (a fade under Reduce Motion), and
/// VoiceOver moves to each new step's title.
///
///     OnboardingView(flow: OnboardingFlow(models: models, firstRun: true),
///                    previewDisplay: SettingsPreviewDisplay())
///         .lunalyrModels(models)
struct OnboardingView: View {
    @ObservedObject var flow: OnboardingFlow
    /// The desktop the look step's live preview is shaped like.
    @ObservedObject var previewDisplay: SettingsPreviewDisplay
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    /// The step on screen: it follows `flow.step` a moment later, once the
    /// outgoing step knows which way to leave.
    @State private var shown: OnboardingFlow.Step
    @State private var forward = true
    @AccessibilityFocusState private var focusedTitle: OnboardingFlow.Step?

    init(flow: OnboardingFlow, previewDisplay: SettingsPreviewDisplay) {
        self.flow = flow
        self.previewDisplay = previewDisplay
        _shown = State(initialValue: flow.step)
    }

    var body: some View {
        // Fills the window below its see-through title bar: the window is
        // 640 x 540 in all (`LunalyrDesign.Layout.onboarding`) and decides the
        // size, so the steps get what the title bar leaves on any macOS.
        VStack(spacing: 0) {
            ZStack {
                step(shown)
                    .id(shown)
                    .transition(LunalyrDesign.Motion.transition(forward ? .trailing : .leading, reduceMotion: reduceMotion))
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .clipped()
            Divider()
            OnboardingFooter(flow: flow)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color(nsColor: .windowBackgroundColor))
        .lunalyrTint()
        .onAppear {
            // VoiceOver starts on the first step's title, as on every later one.
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.35) { focusedTitle = shown }
        }
        .onChange(of: flow.step) { old, new in
            // First the step on screen learns which way it will leave, then
            // it leaves: a transition is read from the view as last drawn.
            forward = new > old
            DispatchQueue.main.async {
                // VoiceOver moves to the new title once the step has settled.
                withAnimation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.step, reduceMotion: reduceMotion),
                              completionCriteria: .logicallyComplete) {
                    shown = flow.step
                } completion: {
                    focusedTitle = shown
                }
            }
        }
    }

    @ViewBuilder private func step(_ step: OnboardingFlow.Step) -> some View {
        let title = $focusedTitle
        switch step {
        case .welcome: WelcomeStep(focus: title)
        case .music: MusicStep(flow: flow, focus: title)
        case .look: LookStep(previewDisplay: previewDisplay, focus: title)
        case .lyrics: LyricsStep(focus: title)
        case .menuBar: MenuBarStep(focus: title)
        case .done: DoneStep(flow: flow, focus: title)
        }
    }
}

/// The step dots, Back and Continue (Open Settings and Done at the end).
private struct OnboardingFooter: View {
    @ObservedObject var flow: OnboardingFlow
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        HStack(spacing: LunalyrDesign.Spacing.small) {
            StepDots(current: flow.step)
            Spacer(minLength: LunalyrDesign.Spacing.large)
            if flow.canGoBack {
                Button("Back") { flow.back() }
                    .keyboardShortcut("[", modifiers: .command)
                    .help("Back to \(OnboardingFlow.Step(rawValue: flow.step.rawValue - 1)?.title ?? "the last step") (⌘[)")
                    .transition(.opacity)
            }
            if flow.isLastStep {
                if flow.firstRun {
                    Button("Done") { flow.finish(.done) }
                        .help("Close and leave Lunalyr in the menu bar")
                        .transition(.opacity)
                    Button("Open Settings") { flow.finish(.openSettings) }
                        .keyboardShortcut(.defaultAction)
                        .buttonStyle(.lunalyrProminent)
                        .transition(.opacity)
                } else {
                    Button("Done") { flow.finish(.done) }
                        .keyboardShortcut(.defaultAction)
                        .buttonStyle(.lunalyrProminent)
                        .frame(minWidth: 88)
                }
            } else {
                Button("Continue") { flow.advance() }
                    .keyboardShortcut(.defaultAction)
                    .buttonStyle(.lunalyrProminent)
                    .frame(minWidth: 88)
            }
        }
        .controlSize(.large)
        .padding(.horizontal, LunalyrDesign.Spacing.xLarge)
        .padding(.vertical, LunalyrDesign.Spacing.medium + 2)
        // Back, Done and Open Settings fade in and out with the step.
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion), value: flow.step)
    }
}

/// Where the person is: a dot per step, the current one a lit bar. One
/// element to VoiceOver: "Step 2 of 6".
private struct StepDots: View {
    let current: OnboardingFlow.Step
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        HStack(spacing: 6) {
            ForEach(OnboardingFlow.Step.allCases) { step in
                Capsule(style: .continuous)
                    .fill(step == current ? AnyShapeStyle(Color.lunalyrAccent) : AnyShapeStyle(Color.secondary.opacity(0.35)))
                    .frame(width: step == current ? 18 : 7, height: 7)
            }
        }
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion), value: current)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(current.position)
        .accessibilityValue(current.title)
    }
}

// MARK: - Shared step pieces

/// A step's frame: its content vertically centred in the room between the
/// title bar and the buttons, 48 points from the sides, scrolling only if
/// larger text makes it taller than the window.
struct OnboardingStepLayout<Content: View>: View {
    let content: Content

    init(@ViewBuilder content: () -> Content) {
        self.content = content()
    }

    var body: some View {
        GeometryReader { proxy in
            ScrollView(.vertical) {
                VStack(spacing: 0) { content }
                    .padding(.horizontal, 48)
                    .padding(.vertical, LunalyrDesign.Spacing.small)
                    .frame(maxWidth: .infinity, minHeight: proxy.size.height)
            }
            .scrollBounceBehavior(.basedOnSize)
            .scrollIndicators(.automatic)
        }
    }
}

/// A step's symbol, title and explanation, centred. The title is a header,
/// and VoiceOver lands on it when the step appears.
struct OnboardingHeader: View {
    let step: OnboardingFlow.Step
    var symbol: String?
    var symbolColour: Color = .lunalyrAccentText
    let title: String
    var message: String?
    var focus: AccessibilityFocusState<OnboardingFlow.Step?>.Binding

    var body: some View {
        VStack(spacing: LunalyrDesign.Spacing.small) {
            if let symbol {
                Image(systemName: symbol)
                    .font(.system(size: 34, weight: .regular))
                    .symbolRenderingMode(.hierarchical)
                    .foregroundStyle(symbolColour)
                    .frame(height: 40)
                    .padding(.bottom, LunalyrDesign.Spacing.xSmall)
                    .accessibilityHidden(true)
            }
            Text(title)
                .lunalyrText(.largeTitle)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
                .accessibilityAddTraits(.isHeader)
                .accessibilityFocused(focus, equals: step)
            if let message {
                Text(message)
                    .font(.body)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                    .fixedSize(horizontal: false, vertical: true)
                    .frame(maxWidth: 520)
            }
        }
        .frame(maxWidth: .infinity)
    }
}

/// A rounded group of rows, as a grouped form draws its sections.
struct OnboardingCard<Content: View>: View {
    let content: Content

    init(@ViewBuilder content: () -> Content) {
        self.content = content()
    }

    var body: some View {
        VStack(alignment: .leading, spacing: LunalyrDesign.Spacing.medium) { content }
            .padding(LunalyrDesign.Spacing.medium + 2)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: LunalyrDesign.Radius.card, style: .continuous).fill(.quinary))
            .overlay(RoundedRectangle(cornerRadius: LunalyrDesign.Radius.card, style: .continuous)
                .strokeBorder(Color.lunalyrContrastBorder, lineWidth: 1))
    }
}

/// A symbol beside a short title and its explanation: a row of a card.
struct OnboardingFeatureRow<Accessory: View>: View {
    let symbol: String
    let title: String
    let text: String
    let accessory: Accessory

    init(symbol: String, title: String, text: String, @ViewBuilder accessory: () -> Accessory) {
        self.symbol = symbol
        self.title = title
        self.text = text
        self.accessory = accessory()
    }

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: LunalyrDesign.Spacing.medium) {
            Image(systemName: symbol)
                .font(.system(size: 15, weight: .medium))
                .foregroundStyle(Color.lunalyrAccentText)
                .frame(width: 22)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: LunalyrDesign.Spacing.xSmall) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(title).lunalyrText(.emphasis)
                    Text(text).lunalyrText(.caption)
                }
                .accessibilityElement(children: .combine)
                accessory
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }
}

extension OnboardingFeatureRow where Accessory == EmptyView {
    init(symbol: String, title: String, text: String) {
        self.init(symbol: symbol, title: title, text: text) { EmptyView() }
    }
}
