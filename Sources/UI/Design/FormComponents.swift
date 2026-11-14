import SwiftUI

// MARK: - Form and panel pieces
//
// Small building blocks for Settings pages, the Look inspector, onboarding
// and the menu-bar panel, so every explanation, disabled reason and value
// reads the same everywhere (SPEC.md §2 and §4):
//
//     ThemedSection("Music response") {
//         Toggle("System audio", isOn: $on).disabled(powerSaving)
//         if powerSaving { DisabledReason("Available once Visualisers return.") }
//         ValueSlider("Sensitivity", value: $sensitivity, in: 0.3...3,
//                     format: { $0.formatted(.percent.precision(.fractionLength(0))) })
//         InspectorCaption("Lunalyr analyses system audio.", more: "The longer text about what is sent where.")
//     }
//
//     PanelSectionHeader("Wallpaper")                      // menu-bar panel groups

/// The short caption under a control: 11.5 pt ink2, 3 points under it. With
/// `more`, the caption ends in an accent "More" that opens the longer text
/// in a popover (the spec's replacement for ⓘ buttons). In a settings form
/// (`compactCaptions`), any caption over about 90 characters folds the same
/// way at a sentence's end, so a page stays short.
struct InspectorCaption: View {
    private let text: Text?
    private let string: String?
    private let more: String?
    @Environment(\.compactCaptions) private var compact
    @Environment(\.captionFollowsControl) private var followsControl
    @Environment(\.formDensity) private var density
    @State private var shown = false

    /// The link's words.
    static let moreTitle = "More"
    /// The link the "More" carries; opened in place, never sent anywhere.
    static let moreURL = URL(string: "lunalyr-caption:more")!

    init(_ string: String) {
        text = nil
        self.string = string
        more = nil
    }

    init(_ string: String, more: String) {
        text = nil
        self.string = string
        self.more = more
    }

    init(_ text: Text) {
        self.text = text
        string = nil
        more = nil
    }

    /// The line to show and the full text behind "More", if any. Only ever
    /// folds at a sentence's end: a caption is never cut off mid-sentence
    /// ("…with animated covers where…"). A long first sentence shows whole.
    static func split(_ string: String, limit: Int = 90) -> (short: String, full: String?) {
        guard string.count > limit else { return (string, nil) }
        guard let end = string.range(of: ". ") else { return (string, nil) }
        let first = String(string[..<end.lowerBound]) + "."
        guard first.count <= limit + 30 else { return (string, nil) }
        return (first, string)
    }

    /// What a caption shows and what its "More" opens (nil: no "More").
    static func content(_ string: String, more: String?, compact: Bool) -> (shown: String, more: String?) {
        if let more { return (string, more) }
        guard compact else { return (string, nil) }
        let split = split(string)
        return (split.short, split.full)
    }

    var body: some View {
        caption
            .font(LunalyrDesign.Studio.TypeScale.caption)
            .foregroundStyle(Color.studio(.ink2))
            .fixedSize(horizontal: false, vertical: true)
            .frame(maxWidth: .infinity, alignment: .leading)
            // In a form the caption belongs to the control above it: 3
            // points under it rather than a whole row's gap away.
            .padding(.top, compact && followsControl ? LunalyrDesign.Studio.Layout.captionGap - density.rowSpacing : 0)
    }

    @ViewBuilder private var caption: some View {
        if let string {
            let content = Self.content(string, more: more, compact: compact)
            if let full = content.more {
                Text(Self.attributed(content.shown))
                    .tint(Color.studio(.accentText))
                    .environment(\.openURL, OpenURLAction { url in
                        guard url == Self.moreURL else { return .systemAction }
                        shown = true
                        return .handled
                    })
                    .popover(isPresented: $shown, arrowEdge: .bottom) {
                        ExplanationPopover(text: full)
                    }
                    .accessibilityElement(children: .ignore)
                    .accessibilityLabel(content.shown)
                    .accessibilityAction(named: "More") { shown = true }
                    .help(Self.moreTitle)
            } else {
                Text(content.shown)
            }
        } else if let text {
            text
        }
    }

    /// The caption with its "More" link after two spaces.
    static func attributed(_ shown: String) -> AttributedString {
        var link = AttributedString(moreTitle)
        link.link = moreURL
        link.foregroundColor = Color.studio(.accentText)
        return AttributedString(shown + "  ") + link
    }
}

private struct CompactCaptionsKey: EnvironmentKey { static let defaultValue = false }
private struct CaptionFollowsControlKey: EnvironmentKey { static let defaultValue = true }

extension EnvironmentValues {
    /// Set by `ThemedForm`: captions pull up under their control, and long
    /// ones fold behind "More".
    var compactCaptions: Bool {
        get { self[CompactCaptionsKey.self] }
        set { self[CompactCaptionsKey.self] = newValue }
    }

    /// False where a caption sits inside its control's own label (under a
    /// checkbox's title): it keeps its place instead of pulling up.
    var captionFollowsControl: Bool {
        get { self[CaptionFollowsControlKey.self] }
        set { self[CaptionFollowsControlKey.self] = newValue }
    }
}

/// A checkbox's title with its caption under it, lined up with the title.
struct CheckboxLabel: View {
    let title: String
    let caption: String?
    let more: String?

    init(_ title: String, caption: String? = nil, more: String? = nil) {
        self.title = title
        self.caption = caption
        self.more = more
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(title)
            if let caption {
                Group {
                    if let more { InspectorCaption(caption, more: more) } else { InspectorCaption(caption) }
                }
                .environment(\.captionFollowsControl, false)
            }
        }
    }
}

extension UndoManager {
    /// Registers a settings change under `name` (Edit › Undo “name”): undoing
    /// runs `undo` and registers `redo`, so Redo puts the change back.
    @MainActor
    func registerSettingsChange(_ name: String, undo: @escaping @MainActor () -> Void,
                                redo: @escaping @MainActor () -> Void) {
        registerUndo(withTarget: SettingsUndoTarget.shared) { [weak self] _ in
            MainActor.assumeIsolated {
                undo()
                self?.registerSettingsChange(name, undo: redo, redo: undo)
            }
        }
        setActionName(name)
    }
}

/// What settings changes are registered against: the changes capture their
/// own stores, so any long-lived object will do.
private final class SettingsUndoTarget {
    static let shared = SettingsUndoTarget()
}

/// A small spinner that appears only once a wait has passed
/// `Motion.Hold.spinnerDelay`, so quick work never flashes one.
struct DelayedSpinner: View {
    @State private var shown = false

    var body: some View {
        ProgressView()
            .controlSize(.small)
            .opacity(shown ? 1 : 0)
            .task {
                try? await Task.sleep(for: .seconds(LunalyrDesign.Motion.Hold.spinnerDelay))
                guard !Task.isCancelled else { return }
                withAnimation(LunalyrDesign.Motion.quick) { shown = true }
            }
            .accessibilityHidden(!shown)
    }
}

/// Something that went wrong, in the warning colour with its triangle, so it

/// Something that went wrong: the warning glyph in the warning colour, the
/// words in ink (status colours are for glyphs, never text), so it never
/// reads like an informational caption (failed imports and downloads).
struct WarningCaption: View {
    let text: String

    init(_ text: String) { self.text = text }

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 5) {
            Image(systemName: "exclamationmark.triangle.fill")
                .foregroundStyle(Color.studio(.warning))
                .accessibilityHidden(true)
            Text(text)
                .foregroundStyle(Color.studio(.ink))
                .fixedSize(horizontal: false, vertical: true)
        }
        .font(LunalyrDesign.Studio.TypeScale.caption)
        .frame(maxWidth: .infinity, alignment: .leading)
        .accessibilityElement(children: .combine)
        .accessibilityLabel("Warning: \(text)")
    }
}

/// Why the control above is unavailable, and when it will be: a caption
/// under it. Every disabled control that isn't self-explanatory gets one.
struct DisabledReason: View {
    let text: String

    init(_ text: String) { self.text = text }

    var body: some View {
        InspectorCaption(text)
            .accessibilityLabel(text)
    }
}

/// A slider: title left (13 pt), value right (12 pt monospaced digits, ink2),
/// the 4 pt track under them, optional end labels (11 pt ink2) under that.
///
///     ValueSlider("Lyric size", value: $scale, in: 0.7...1.5,
///                 format: { $0.formatted(.percent.precision(.fractionLength(0))) })
struct ValueSlider: View {
    let title: String
    @Binding var value: Double
    let range: ClosedRange<Double>
    let step: Double?
    let format: (Double) -> String
    let spoken: ((Double) -> String)?
    let minimumLabel: String?
    let maximumLabel: String?
    var bold = false
    @Environment(\.isEnabled) private var enabled
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    init(_ title: String, value: Binding<Double>, in range: ClosedRange<Double>, step: Double? = nil,
         minimumLabel: String? = nil, maximumLabel: String? = nil, bold: Bool = false,
         format: @escaping (Double) -> String, spoken: ((Double) -> String)? = nil) {
        self.title = title
        _value = value
        self.range = range
        self.step = step
        self.minimumLabel = minimumLabel
        self.maximumLabel = maximumLabel
        self.bold = bold
        self.format = format
        self.spoken = spoken
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            HStack(alignment: .firstTextBaseline) {
                Text(title)
                    .font(.system(size: 13, weight: bold ? .semibold : .regular))
                    .foregroundStyle(Color.studio(.ink))
                    .fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: 8)
                Text(format(value))
                    .font(LunalyrDesign.Studio.TypeScale.value)
                    .foregroundStyle(Color.studio(.ink2))
                    .fixedSize()
                    // Digits roll to a stepped or reset value.
                    .contentTransition(.numericText(value: value))
                    .animation(reduceMotion ? nil : LunalyrDesign.Motion.quick, value: value)
            }
            .opacity(enabled ? 1 : ThemedSliderTrack.disabledOpacity)
            .accessibilityHidden(true)
            ThemedSliderTrack(value: $value, range: range, step: step)
                .frame(maxWidth: .infinity)
                .accessibilityLabel(title)
                .accessibilityValue(spoken?(value) ?? format(value))
            if minimumLabel != nil || maximumLabel != nil {
                HStack {
                    if let minimumLabel { Text(minimumLabel) }
                    Spacer(minLength: 8)
                    if let maximumLabel { Text(maximumLabel) }
                }
                .font(.system(size: 11))
                .foregroundStyle(Color.studio(.ink2))
                .opacity(enabled ? 1 : ThemedSliderTrack.disabledOpacity)
                .accessibilityHidden(true)
            }
        }
    }
}

/// A pop-up: label left, the menu right as 12.5 pt text and an up-down
/// chevron on the control fill (24 high, radius 6). The label moves above
/// the menu when the column can't fit both on one line.
struct ChoiceMenu<Selection: Hashable, Options: View>: View {
    let title: String
    @Binding var selection: Selection
    let selectedTitle: (Selection) -> String
    let options: Options

    init(_ title: String, selection: Binding<Selection>, selectedTitle: @escaping (Selection) -> String,
         @ViewBuilder options: () -> Options) {
        self.title = title
        _selection = selection
        self.selectedTitle = selectedTitle
        self.options = options()
    }

    var body: some View {
        ViewThatFits(in: .horizontal) {
            HStack(spacing: 8) {
                label.fixedSize()
                Spacer(minLength: 8)
                menu.fixedSize()
            }
            VStack(alignment: .leading, spacing: 6) {
                label
                menu
            }
        }
        .frame(maxWidth: .infinity, minHeight: 24, alignment: .leading)
    }

    private var label: some View {
        Text(title)
            .font(LunalyrDesign.Studio.TypeScale.row)
            .foregroundStyle(Color.studio(.ink))
            .accessibilityHidden(true)
    }

    private var menu: some View {
        Menu {
            Picker(title, selection: $selection) { options }
                .pickerStyle(.inline)
        } label: {
            ThemedPopUpLabel(selectedTitle(selection))
        }
        .menuStyle(.button)
        .buttonStyle(ThemedPopUpButtonStyle())
        .menuIndicator(.hidden)
        .help(selectedTitle(selection))
        .accessibilityLabel(title)
        .accessibilityValue(selectedTitle(selection))
    }
}

/// A pop-up's face: the choice and an up-down chevron.
struct ThemedPopUpLabel: View {
    let text: String

    init(_ text: String) { self.text = text }

    var body: some View {
        HStack(spacing: 5) {
            Text(text).lineLimit(1)
            Image(systemName: "chevron.up.chevron.down")
                .font(.system(size: 8.5, weight: .semibold))
                .accessibilityHidden(true)
        }
    }
}

/// A pop-up's button: as `ThemedButtonStyle`, 12.5 pt, padding 9.
struct ThemedPopUpButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        ThemedPopUpBody(configuration: configuration)
    }
}

private struct ThemedPopUpBody: View {
    let configuration: ButtonStyleConfiguration
    @Environment(\.isEnabled) private var enabled
    @State private var hovering = false

    var body: some View {
        configuration.label
            .font(.system(size: 12.5))
            .foregroundStyle(Color.studio(.ink))
            .padding(.horizontal, 9)
            .frame(minHeight: 24)
            .themedControl(radius: 6, state: !enabled ? .disabled : configuration.isPressed ? .pressed : hovering ? .hover : .normal)
            .contentShape(Rectangle())
            .onHover { hovering = $0 }
    }
}

/// A short list easier to choose from with every option visible: label
/// left, segments right; the label moves above when they don't fit.
struct ChoiceSegments<Selection: Hashable>: View {
    let title: String
    @Binding var selection: Selection
    let cases: [Selection]
    let label: (Selection) -> String

    init(_ title: String, selection: Binding<Selection>, cases: [Selection], label: @escaping (Selection) -> String) {
        self.title = title
        _selection = selection
        self.cases = cases
        self.label = label
    }

    var body: some View {
        ViewThatFits(in: .horizontal) {
            HStack(spacing: 8) {
                titleText.fixedSize()
                Spacer(minLength: 8)
                segments
            }
            VStack(alignment: .leading, spacing: 6) {
                titleText
                segments
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var titleText: some View {
        Text(title)
            .font(LunalyrDesign.Studio.TypeScale.row)
            .foregroundStyle(Color.studio(.ink))
            .accessibilityHidden(true)
    }

    private var segments: some View {
        ThemedSegments(title, selection: $selection, options: cases.map { ($0, label($0)) })
    }
}

/// A labelled text field: the label in a 52 pt column, the field filling the rest.
struct LabeledField: View {
    let label: String
    @Binding var text: String
    let prompt: String
    var onSubmit: () -> Void = {}

    init(_ label: String, text: Binding<String>, prompt: String, onSubmit: @escaping () -> Void = {}) {
        self.label = label
        _text = text
        self.prompt = prompt
        self.onSubmit = onSubmit
    }

    var body: some View {
        HStack(spacing: 10) {
            Text(label)
                .font(LunalyrDesign.Studio.TypeScale.row)
                .foregroundStyle(Color.studio(.ink))
                .frame(width: 52, alignment: .leading)
                .accessibilityHidden(true)
            TextField(label, text: $text, prompt: Text(prompt).foregroundStyle(Color.studio(.ink2)))
                .textFieldStyle(ThemedTextFieldStyle())
                .onSubmit(onSubmit)
                .accessibilityLabel(label)
        }
    }
}

/// A status glyph for access and connection rows: success green, warning
/// orange, otherwise ink2. Never text.
struct StatusGlyph: View {
    let status: LunalyrStatus
    var symbol: String?

    var body: some View {
        Image(systemName: symbol ?? status.symbol)
            .font(.system(size: 13))
            .foregroundStyle(Self.color(status))
            .accessibilityHidden(true)
    }

    static func color(_ status: LunalyrStatus) -> Color {
        switch status {
        case .success: return Color.studio(.success)
        case .warning: return Color.studio(.warning)
        case .critical: return status.text
        case .info, .neutral: return Color.studio(.ink2)
        }
    }
}

/// A group's title inside a panel that isn't a `Form` (the menu-bar panel,
/// onboarding): secondary, semibold, a header to VoiceOver.
struct PanelSectionHeader<Accessory: View>: View {
    let title: String
    let accessory: Accessory

    init(_ title: String, @ViewBuilder accessory: () -> Accessory) {
        self.title = title
        self.accessory = accessory()
    }

    var body: some View {
        HStack(alignment: .firstTextBaseline) {
            Text(title)
                .lunalyrText(.groupTitle)
                .accessibilityAddTraits(.isHeader)
            Spacer(minLength: LunalyrDesign.Spacing.small)
            accessory
        }
    }
}

extension PanelSectionHeader where Accessory == EmptyView {
    init(_ title: String) {
        self.init(title) { EmptyView() }
    }
}

/// A Settings page's icon: a violet SF Symbol, one calm colour for every page
/// (`tint` is kept for callers). No badge behind it (docs/DESIGN.md: no squircle
/// tiles as ornament). Decorative: the row's title names it.
struct SettingsIcon: View {
    let symbol: String
    let tint: Color
    var size: CGFloat = LunalyrDesign.Size.sidebarIcon

    var body: some View {
        Image(systemName: symbol)
            .font(.system(size: size * 0.6, weight: .medium))
            .frame(width: size, height: size)
            .accessibilityHidden(true)
    }
}


/// A caption with a symbol: 11.5 pt ink2, the symbol before it.
struct CaptionLabelStyle: LabelStyle {
    func makeBody(configuration: Configuration) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 5) {
            configuration.icon
            configuration.title
                .fixedSize(horizontal: false, vertical: true)
        }
        .font(LunalyrDesign.Studio.TypeScale.caption)
        .foregroundStyle(Color.studio(.ink2))
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}
