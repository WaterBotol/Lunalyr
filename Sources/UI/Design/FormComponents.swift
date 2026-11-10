import SwiftUI

// MARK: - Form and panel pieces
//
// Small building blocks for the Settings inspector, onboarding and the
// menu-bar panel, so every explanation, disabled reason and value reads the
// same everywhere:
//
//     Form {
//         Section("Music response") {
//             Toggle("System audio", isOn: $on).disabled(powerSaving)
//             if powerSaving { DisabledReason("Available once Visualisers return.") }
//             ValueSlider("Sensitivity", value: $sensitivity, in: 0.3...3,
//                         format: { $0.formatted(.percent.precision(.fractionLength(0))) })
//             InspectorCaption("Lunalyr analyses system audio on this Mac.")
//         }
//     }
//     .formStyle(.grouped)
//
//     PanelSectionHeader("Wallpaper")                      // menu-bar panel groups
//     SettingsIcon(symbol: "photo.artframe", tint: .indigo) // sidebar rows

/// The explanation under a control: what it does, or what it changes.
/// Secondary, 11 points, wrapping. Pass a `Text` for inline markdown. A
/// caption with `more` shows a short line and an ⓘ that opens the rest; in a
/// settings page (`ThemedForm`), any caption over about 90 characters is cut
/// the same way, so a page stays short.
struct InspectorCaption: View {
    private let text: Text
    private let string: String?
    private let more: String?
    @Environment(\.compactCaptions) private var compact

    init(_ string: String) {
        text = Text(string)
        self.string = string
        more = nil
    }

    init(_ string: String, more: String) {
        text = Text(string)
        self.string = string
        self.more = more
    }

    init(_ text: Text) {
        self.text = text
        string = nil
        more = nil
    }

    /// The line to show and the full text behind the ⓘ, if any.
    static func split(_ string: String, limit: Int = 90) -> (short: String, full: String?) {
        guard string.count > limit else { return (string, nil) }
        if let end = string.range(of: ". "), string.distance(from: string.startIndex, to: end.lowerBound) <= limit {
            return (String(string[..<end.lowerBound]) + ".", string)
        }
        let cut = string.prefix(limit - 10)
        let trimmed = cut.lastIndex(of: " ").map { String(cut[..<$0]) } ?? String(cut)
        return (trimmed + "…", string)
    }

    var body: some View {
        if let more, let string {
            row(Text(string), full: more)
        } else if compact, let string, case let split = Self.split(string), let full = split.full {
            row(Text(split.short), full: full)
        } else {
            text.lunalyrText(.caption)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    private func row(_ shown: Text, full: String) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 4) {
            shown.lunalyrText(.caption)
            ThemedInfoButton(text: full, label: "this setting")
            Spacer(minLength: 0)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

private struct CompactCaptionsKey: EnvironmentKey { static let defaultValue = false }

extension EnvironmentValues {
    /// Set by `ThemedForm`: long captions fold behind an ⓘ.
    var compactCaptions: Bool {
        get { self[CompactCaptionsKey.self] }
        set { self[CompactCaptionsKey.self] = newValue }
    }
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
/// never reads like an informational caption (failed imports and downloads).
struct WarningCaption: View {
    let text: String

    init(_ text: String) { self.text = text }

    var body: some View {
        Label {
            Text(text).fixedSize(horizontal: false, vertical: true)
        } icon: {
            Image(systemName: "exclamationmark.triangle.fill")
        }
        .labelStyle(CaptionLabelStyle())
        .foregroundStyle(LunalyrStatus.warning.text)
        .frame(maxWidth: .infinity, alignment: .leading)
        .accessibilityElement(children: .combine)
    }
}

/// Why the control above is unavailable, and when it will be. Every disabled
/// control that isn't self-explanatory gets one, directly beneath it.
struct DisabledReason: View {
    let text: String

    init(_ text: String) { self.text = text }

    var body: some View {
        Label {
            Text(text)
        } icon: {
            Image(systemName: "info.circle")
        }
        .labelStyle(CaptionLabelStyle())
        .accessibilityElement(children: .combine)
    }
}

/// A full-width native slider with a stable readout above the track.
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
    @Environment(\.isEnabled) private var enabled
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    init(_ title: String, value: Binding<Double>, in range: ClosedRange<Double>, step: Double? = nil,
         minimumLabel: String? = nil, maximumLabel: String? = nil,
         format: @escaping (Double) -> String, spoken: ((Double) -> String)? = nil) {
        self.title = title
        _value = value
        self.range = range
        self.step = step
        self.minimumLabel = minimumLabel
        self.maximumLabel = maximumLabel
        self.format = format
        self.spoken = spoken
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack(alignment: .firstTextBaseline) {
                Text(title)
                    .fixedSize(horizontal: false, vertical: true)
                Spacer(minLength: LunalyrDesign.Spacing.small)
                Text(format(value))
                    .lunalyrText(.value)
                    .foregroundStyle(.secondary)
                    .fixedSize()
                    // Digits roll to a stepped or reset value.
                    .contentTransition(.numericText(value: value))
                    .animation(reduceMotion ? nil : LunalyrDesign.Motion.quick, value: value)
            }
            .opacity(enabled ? 1 : ThemedSliderTrack.disabledOpacity)
            .accessibilityHidden(true)
            slider
                .labelsHidden()
                .frame(maxWidth: .infinity)
                .accessibilityLabel(title)
                .accessibilityValue(spoken?(value) ?? format(value))
            if minimumLabel != nil || maximumLabel != nil {
                HStack {
                    if let minimumLabel { Text(minimumLabel) }
                    Spacer(minLength: LunalyrDesign.Spacing.small)
                    if let maximumLabel { Text(maximumLabel) }
                }
                .lunalyrText(.footnote)
                .opacity(enabled ? 1 : ThemedSliderTrack.disabledOpacity)
                .accessibilityHidden(true)
            }
        }
        .padding(.vertical, 2)
    }

    private var slider: some View {
        ThemedSliderTrack(value: $value, range: range, step: step)
    }
}

/// A native selection menu with a readable current choice. The title moves
/// above the menu when the inspector can't fit both on one line.
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
            HStack(spacing: LunalyrDesign.Spacing.small) {
                label.fixedSize()
                Spacer(minLength: LunalyrDesign.Spacing.small)
                menu.fixedSize()
            }
            VStack(alignment: .leading, spacing: LunalyrDesign.Spacing.small) {
                label
                menu
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var label: some View {
        Text(title).accessibilityHidden(true)
    }

    private var menu: some View {
        Menu {
            Picker(title, selection: $selection) { options }
                .pickerStyle(.inline)
        } label: {
            Text(selectedTitle(selection))
        }
        .menuStyle(.button)
        .buttonStyle(ThemedButtonStyle())
        .menuIndicator(.visible)
        .help(selectedTitle(selection))
        .accessibilityLabel(title)
        .accessibilityValue(selectedTitle(selection))
    }
}

/// Short lists that are easier to choose from with every option visible.
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
        VStack(alignment: .leading, spacing: LunalyrDesign.Spacing.small) {
            Text(title).accessibilityHidden(true)
            ThemedSegments(title, selection: $selection, options: cases.map { ($0, label($0)) })
        }
        .padding(.vertical, 2)
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

/// A Settings page's icon: a violet SF Symbol on a soft violet rounded square,
/// one calm colour for every page (`tint` is kept for callers). Decorative: the row's title names it.
struct SettingsIcon: View {
    let symbol: String
    let tint: Color
    var size: CGFloat = LunalyrDesign.Size.sidebarIcon

    var body: some View {
        Image(systemName: symbol)
            .font(.system(size: size * 0.56, weight: .semibold))
            .foregroundStyle(Color.lunalyrAccentText)
            .frame(width: size, height: size)
            .background(
                RoundedRectangle(cornerRadius: LunalyrDesign.Radius.badge * size / LunalyrDesign.Size.sidebarIcon,
                                 style: .continuous)
                    .fill(Color.lunalyrAccent.opacity(0.18))
            )
            .overlay(
                RoundedRectangle(cornerRadius: LunalyrDesign.Radius.badge * size / LunalyrDesign.Size.sidebarIcon,
                                 style: .continuous)
                    .strokeBorder(Color.lunalyrContrastBorder, lineWidth: 1)
            )
            .accessibilityHidden(true)
    }
}

/// Footnote-sized label with a secondary icon, for captions with a symbol.
struct CaptionLabelStyle: LabelStyle {
    func makeBody(configuration: Configuration) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: LunalyrDesign.Spacing.xSmall + 2) {
            configuration.icon
                .foregroundStyle(.secondary)
            configuration.title
                .fixedSize(horizontal: false, vertical: true)
        }
        .font(LunalyrTextStyle.caption.font)
        .foregroundStyle(.secondary)
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}
