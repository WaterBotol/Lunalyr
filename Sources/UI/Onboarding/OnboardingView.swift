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
    /// The glass logo opening is over the window (`WelcomeGlassOpening`).
    @State private var intro = false
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
        ZStack {
            VStack(spacing: 0) {
                ZStack {
                    step(shown)
                        .id(shown)
                        .transition(LunalyrDesign.Motion.transition(forward ? .trailing : .leading, reduceMotion: reduceMotion))
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .clipped()
                OnboardingFooter(flow: flow, enabled: !intro)
            }
            .accessibilityHidden(intro)
            if intro, let url = WelcomeGlass.url {
                WelcomeGlassOpening(url: url, onFinish: finishIntro, skip: { endIntro(after: 0) })
                    .transition(.opacity)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color.studio(.window))
        .tint(Color.studio(.accent))
        .onAppear(perform: appear)
        .onChange(of: flow.step) { old, new in
            // First the step on screen learns which way it will leave, then
            // it leaves: a transition is read from the view as last drawn.
            forward = new > old
            DispatchQueue.main.async {
                // VoiceOver moves to the new title once the step has settled.
                withAnimation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.standard, reduceMotion: reduceMotion),
                              completionCriteria: .logicallyComplete) {
                    shown = flow.step
                } completion: {
                    focusedTitle = shown
                }
            }
        }
    }

    /// Opens on the glass logo the first time in a launch; otherwise
    /// VoiceOver starts on the first step's title, as on every later one.
    private func appear() {
        if WelcomeGlass.opensOnIntro(step: flow.step, reduceMotion: reduceMotion, alreadyPlayed: WelcomeGlass.played) {
            WelcomeGlass.played = true
            intro = true
        } else {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.35) { focusedTitle = shown }
        }
    }

    /// The video ended: its last frame (the plain logo) holds a moment.
    private func finishIntro() {
        endIntro(after: WelcomeGlass.hold)
    }

    /// Cross-fades from the opening into the welcome, then VoiceOver lands
    /// on its title.
    private func endIntro(after delay: TimeInterval) {
        DispatchQueue.main.asyncAfter(deadline: .now() + delay) {
            guard intro else { return }
            withAnimation(.easeInOut(duration: WelcomeGlass.fade), completionCriteria: .logicallyComplete) {
                intro = false
            } completion: {
                focusedTitle = shown
            }
        }
    }

    @ViewBuilder private func step(_ step: OnboardingFlow.Step) -> some View {
        let title = $focusedTitle
        switch step {
        case .welcome: WelcomeStep(focus: title)
        case .music: MusicStep(flow: flow, focus: title)
        case .look: LookStep(flow: flow, previewDisplay: previewDisplay, focus: title)
        case .videos: VideosStep(focus: title)
        case .lyrics: LyricsStep(focus: title)
        case .extras: ExtrasStep(focus: title)
        case .menuBar: MenuBarStep(focus: title)
        case .done: DoneStep(flow: flow, focus: title)
        }
    }
}

/// The step dots, Back and Continue (Open Settings and Done at the end).
private struct OnboardingFooter: View {
    @ObservedObject var flow: OnboardingFlow
    /// Off under the opening: hidden, and its shortcuts don't fire.
    var enabled = true
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
                        .buttonStyle(.borderedProminent)
                        .transition(.opacity)
                } else {
                    Button("Done") { flow.finish(.done) }
                        .keyboardShortcut(.defaultAction)
                        .buttonStyle(.borderedProminent)
                        .frame(minWidth: 88)
                }
            } else {
                Button("Continue") { flow.advance() }
                    .keyboardShortcut(.defaultAction)
                    .buttonStyle(.borderedProminent)
                    .frame(minWidth: 88)
            }
        }
        .controlSize(.large)
        .disabled(!enabled)
        .opacity(enabled ? 1 : 0)
        .padding(.horizontal, LunalyrDesign.Spacing.xLarge)
        .padding(.vertical, LunalyrDesign.Spacing.medium + 2)
        // Back, Done and Open Settings fade in and out with the step.
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion), value: flow.step)
    }
}

/// Where the person is: a dot per step, the current one a lit bar. One
/// element to VoiceOver: "Step 2 of 8".
private struct StepDots: View {
    let current: OnboardingFlow.Step
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        HStack(spacing: LunalyrDesign.Spacing.xSmall + LunalyrDesign.Spacing.hairline) {
            ForEach(OnboardingFlow.Step.allCases) { step in
                Capsule(style: .continuous)
                    .fill(step == current ? Color.studio(.accent) : Color.studio(.ink2).opacity(0.35))
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

/// A step's page: its title at the same height on every step, a line under
/// it, then the step's content; scrolling only if larger text makes it
/// taller than the window.
///
///     OnboardingPage(step: .lyrics, title: OnboardingCopy.lyricsTitle, focus: focus) {
///         OnboardingCard { … }
///     }
struct OnboardingPage<Content: View>: View {
    let step: OnboardingFlow.Step
    let title: String
    var message: String?
    var focus: OnboardingTitleFocus
    let content: Content

    init(step: OnboardingFlow.Step, title: String, message: String? = nil, focus: OnboardingTitleFocus,
         @ViewBuilder content: () -> Content) {
        self.step = step
        self.title = title
        self.message = message
        self.focus = focus
        self.content = content()
    }

    var body: some View {
        OnboardingStepLayout(alignment: .top) {
            OnboardingHeader(step: step, title: title, message: message, focus: focus)
                .padding(.bottom, LunalyrDesign.Spacing.large)
            content
        }
    }
}

/// A step's frame: 48 points from the sides, its content from the shared
/// title line down (`.top`) or centred in the room between the title bar and
/// the buttons (`.center`, the welcome and the end), scrolling only if larger
/// text makes it taller than the window.
struct OnboardingStepLayout<Content: View>: View {
    var alignment: VerticalAlignment = .center
    let content: Content

    init(alignment: VerticalAlignment = .center, @ViewBuilder content: () -> Content) {
        self.alignment = alignment
        self.content = content()
    }

    var body: some View {
        GeometryReader { proxy in
            ScrollView(.vertical) {
                VStack(spacing: 0) { content }
                    .padding(.horizontal, LunalyrDesign.Onboarding.sideInset)
                    .padding(.top, alignment == .top ? LunalyrDesign.Onboarding.titleTop : LunalyrDesign.Spacing.small)
                    .padding(.bottom, LunalyrDesign.Spacing.large)
                    .frame(maxWidth: .infinity, minHeight: proxy.size.height,
                           alignment: alignment == .top ? .top : .center)
            }
            .scrollBounceBehavior(.basedOnSize)
            .scrollIndicators(.automatic)
        }
    }
}

/// A step's title and the line under it, centred. The title is a header,
/// and VoiceOver lands on it when the step appears.
struct OnboardingHeader: View {
    let step: OnboardingFlow.Step
    let title: String
    var message: String?
    var focus: OnboardingTitleFocus

    var body: some View {
        VStack(spacing: LunalyrDesign.Spacing.small) {
            Text(title)
                .font(OnboardingType.title)
                .foregroundStyle(Color.studio(.ink))
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
                .accessibilityAddTraits(.isHeader)
                .accessibilityFocused(focus, equals: step)
            if let message {
                Text(message)
                    .font(LunalyrDesign.Studio.TypeScale.row)
                    .foregroundStyle(Color.studio(.ink2))
                    .multilineTextAlignment(.center)
                    .fixedSize(horizontal: false, vertical: true)
                    .frame(maxWidth: LunalyrDesign.Onboarding.messageWidth)
            }
        }
        .frame(maxWidth: .infinity)
    }
}

/// A group of rows on the window itself: no card, no edge, no dividers
/// (the redesign's language, DESIGN.md slop 6 and 8); the space between
/// rows separates them.
struct OnboardingCard<Content: View>: View {
    let content: Content

    init(@ViewBuilder content: () -> Content) {
        self.content = content()
    }

    var body: some View {
        VStack(alignment: .leading, spacing: OnboardingType.rowSpacing) { content }
            .frame(maxWidth: .infinity, alignment: .leading)
    }
}

/// Onboarding's type, from the redesign's scale (SF Pro, regular and
/// semibold): a step's title 22 semibold (a welcome window reads larger than
/// a Settings page's 17), row names 13 semibold, captions 11.5 in ink2.
enum OnboardingType {
    static let title = Font.system(size: 22, weight: .semibold)
    static let rowSpacing: CGFloat = 16
}

extension View {
    /// A caption: 11.5 regular, ink2.
    func onboardingCaption() -> some View {
        font(LunalyrDesign.Studio.TypeScale.caption).foregroundStyle(Color.studio(.ink2))
    }

    /// A row's name: 13 semibold, ink.
    func onboardingEmphasis() -> some View {
        font(LunalyrDesign.Studio.TypeScale.section).foregroundStyle(Color.studio(.ink))
    }
}

/// A short title and its explanation, with whatever the row offers beneath:
/// a row of a card.
struct OnboardingFeatureRow<Accessory: View>: View {
    let title: String
    let text: String
    let accessory: Accessory

    init(title: String, text: String, @ViewBuilder accessory: () -> Accessory) {
        self.title = title
        self.text = text
        self.accessory = accessory()
    }

    var body: some View {
        VStack(alignment: .leading, spacing: LunalyrDesign.Spacing.small) {
            VStack(alignment: .leading, spacing: LunalyrDesign.Spacing.hairline) {
                Text(title).onboardingEmphasis()
                Text(text).onboardingCaption()
            }
            .accessibilityElement(children: .combine)
            accessory
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

extension OnboardingFeatureRow where Accessory == EmptyView {
    init(title: String, text: String) {
        self.init(title: title, text: text) { EmptyView() }
    }
}

/// A switch at the trailing edge, as a grouped form lays one out; to
/// VoiceOver, the switch named by its title, with the caption as a hint.
struct OnboardingSwitchRow: View {
    let title: String
    let caption: String
    @Binding var isOn: Bool

    var body: some View {
        HStack(spacing: LunalyrDesign.Spacing.medium) {
            VStack(alignment: .leading, spacing: LunalyrDesign.Spacing.hairline) {
                Text(title)
                    .font(LunalyrDesign.Studio.TypeScale.row)
                    .foregroundStyle(Color.studio(.ink))
                Text(caption).onboardingCaption()
            }
            .accessibilityHidden(true)
            Spacer(minLength: 0)
            Toggle(title, isOn: $isOn)
                .labelsHidden()
                .toggleStyle(.switch)
                .controlSize(.small)
                .accessibilityHint(caption)
        }
    }
}
