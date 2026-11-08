import AppKit
import SwiftUI

// MARK: - Theme components (design/spec.md)
//
// A small, native style system for Calm and Bold: tokens resolved from the
// chosen look, one control surface shared by every component, and the
// components themselves. Everything is static and opaque.
//
//     Button("Save") { }.buttonStyle(ThemedButtonStyle(prominent: true))
//     Toggle("Launch at login", isOn: $on).toggleStyle(ThemedToggleStyle())
//     ThemedSegments("Player", selection: $choice, options: [(.auto, "Automatic"), (.music, "Music")])
//     ThemedCard("Startup", info: "Longer explanation") { … }

/// What a component looks like at a moment.
enum ThemeState: String, CaseIterable {
    case normal, hover, pressed, disabled, focused
}

private struct ThemeStyleOverrideKey: EnvironmentKey { static let defaultValue: SettingsVisualStyle? = nil }
private struct ThemeForcedStateKey: EnvironmentKey { static let defaultValue: ThemeState? = nil }

extension EnvironmentValues {
    /// Draws components in this look instead of the chosen one (the picker's
    /// miniatures, the Style Gallery).
    var themeStyleOverride: SettingsVisualStyle? {
        get { self[ThemeStyleOverrideKey.self] }
        set { self[ThemeStyleOverrideKey.self] = newValue }
    }

    /// Holds every component at one state (the Style Gallery).
    var themeForcedState: ThemeState? {
        get { self[ThemeForcedStateKey.self] }
        set { self[ThemeForcedStateKey.self] = newValue }
    }
}

/// Resolves the look and its tokens for the current appearance.
struct ThemeReader<Content: View>: View {
    @ObservedObject private var store = SettingsStyleStore.shared
    @Environment(\.themeStyleOverride) private var override
    @Environment(\.colorScheme) private var scheme
    @Environment(\.colorSchemeContrast) private var contrast
    let content: (SettingsVisualStyle, SettingsVisualStyle.Tokens) -> Content

    init(@ViewBuilder content: @escaping (SettingsVisualStyle, SettingsVisualStyle.Tokens) -> Content) {
        self.content = content
    }

    var body: some View {
        let style = override ?? store.style
        content(style, style.tokens(dark: scheme == .dark, increaseContrast: contrast == .increased))
    }
}

// MARK: Surface

/// The one surface every control and card is drawn on: fill, edge, Bold's
/// hard shadow, the pressed shift and the focus ring.
struct ThemedSurface: ViewModifier {
    let radius: CGFloat
    let selected: Bool
    /// nil for a card, which has no states.
    let state: ThemeState?
    let isCard: Bool

    func body(content: Content) -> some View {
        ThemeReader { style, t in
            // Circular corners: a continuous corner at a pill's full radius overshoots its stroke.
            let shape = RoundedRectangle(cornerRadius: radius * style.cornerScale, style: .circular)
            let current = state ?? .normal
            let pressed = current == .pressed
            let shadowed = style == .bold && !pressed && current != .disabled
            let shift: CGFloat = style == .bold && pressed ? t.shadowOffset * 0.66 : 0
            let edge = isCard ? Color(nsColor: t.cardEdge) : Color(nsColor: t.controlEdge)
            content
                .background(fill(style: style, t: t, state: current).clipShape(shape))
                .overlay(shape.strokeBorder(selected ? Color.white.opacity(style == .bold ? 0 : 0.25) : edge, lineWidth: selected && style == .bold ? t.outline : (isCard && style == .calm ? 1 : t.outline)))
                .background(alignment: .topLeading) {
                    if shadowed, let shadow = t.shadow {
                        shape.fill(Color(nsColor: shadow)).offset(x: t.shadowOffset, y: t.shadowOffset)
                    }
                }
                .overlay {
                    if current == .focused {
                        shape.inset(by: -3).strokeBorder(Color.lunalyrAccentText, lineWidth: 2)
                    }
                }
                .offset(x: shift, y: shift)
                .opacity(current == .disabled ? 0.4 : 1)
        }
    }

    @ViewBuilder private func fill(style: SettingsVisualStyle, t: SettingsVisualStyle.Tokens, state: ThemeState) -> some View {
        if selected {
            ZStack {
                Color.lunalyrAccent
                if state == .pressed { Color.black.opacity(0.18) }
                if state == .hover { Color.white.opacity(0.14) }
            }
        } else {
            ZStack {
                Color(nsColor: state == .pressed && style == .calm ? t.canvas : t.face)
                if state == .hover {
                    (style == .bold ? Color.lunalyrAccent.opacity(0.12) : Color.primary.opacity(0.05))
                }
            }
        }
    }
}

extension View {
    /// A control face in the chosen look.
    func themedControl(radius: CGFloat = 8, selected: Bool = false, state: ThemeState = .normal) -> some View {
        modifier(ThemedSurface(radius: radius, selected: selected, state: state, isCard: false))
    }
}

/// The state a control is in: forced by the gallery, else what it is doing.
private func resolvedState(forced: ThemeState?, enabled: Bool, pressed: Bool, focused: Bool, hovering: Bool) -> ThemeState {
    if let forced { return forced }
    if !enabled { return .disabled }
    if pressed { return .pressed }
    if focused { return .focused }
    return hovering ? .hover : .normal
}

// MARK: Button

struct ThemedButtonStyle: ButtonStyle {
    var prominent = false

    func makeBody(configuration: Configuration) -> some View {
        ThemedButtonBody(configuration: configuration, prominent: prominent)
    }
}

private struct ThemedButtonBody: View {
    let configuration: ButtonStyleConfiguration
    let prominent: Bool
    @Environment(\.isEnabled) private var enabled
    @Environment(\.isFocused) private var focused
    @Environment(\.themeForcedState) private var forced
    @State private var hovering = false

    var body: some View {
        let state = resolvedState(forced: forced, enabled: enabled, pressed: configuration.isPressed,
                                  focused: focused, hovering: hovering)
        ThemeReader { style, t in
            configuration.label
                .font(.body.weight(style == .bold ? .semibold : .regular))
                .foregroundStyle(prominent ? Color.white : Color(nsColor: t.ink))
                .padding(.horizontal, 14)
                .frame(minHeight: LunalyrDesign.Size.minimumHitTarget)
                .themedControl(radius: 8, selected: prominent, state: state)
                .contentShape(Rectangle())
        }
        .onHover { hovering = $0 }
    }
}

// MARK: Toggle

struct ThemedToggleStyle: ToggleStyle {
    func makeBody(configuration: Configuration) -> some View {
        ThemedToggleBody(configuration: configuration)
    }
}

private struct ThemedToggleBody: View {
    let configuration: ToggleStyleConfiguration
    @Environment(\.isEnabled) private var enabled
    @Environment(\.isFocused) private var focused
    @Environment(\.themeForcedState) private var forced
    @State private var hovering = false

    var body: some View {
        let state = resolvedState(forced: forced, enabled: enabled, pressed: false, focused: focused, hovering: hovering)
        ThemeReader { style, t in
            Button {
                configuration.isOn.toggle()
            } label: {
                HStack {
                    configuration.label
                        .foregroundStyle(Color(nsColor: t.ink))
                    Spacer(minLength: LunalyrDesign.Spacing.medium)
                    track(style: style, t: t, state: state)
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityValue(configuration.isOn ? "On" : "Off")
            .accessibilityAddTraits(.isToggle)
        }
        .onHover { hovering = $0 }
    }

    private func track(style: SettingsVisualStyle, t: SettingsVisualStyle.Tokens, state: ThemeState) -> some View {
        let on = configuration.isOn
        let knob = RoundedRectangle(cornerRadius: style == .bold ? 3 : 9, style: .continuous)
        return Color.clear
            .frame(width: 42, height: 24)
            .overlay(alignment: on ? .trailing : .leading) {
                knob
                    .fill(on ? Color.white : Color(nsColor: t.ink))
                    .overlay(knob.strokeBorder(Color(nsColor: t.controlEdge), lineWidth: on && style == .bold ? 1 : 0))
                    .frame(width: 16, height: 16)
                    .padding(4)
            }
            .themedControl(radius: 12, selected: on, state: state)
    }
}

// MARK: Segmented picker

struct ThemedSegments<Value: Hashable>: View {
    let title: String
    @Binding var selection: Value
    let options: [(value: Value, title: String)]
    @Environment(\.isEnabled) private var enabled
    @Environment(\.themeForcedState) private var forced

    init(_ title: String, selection: Binding<Value>, options: [(Value, String)]) {
        self.title = title
        _selection = selection
        self.options = options.map { (value: $0.0, title: $0.1) }
    }

    var body: some View {
        ThemeReader { style, t in
            HStack(spacing: style == .bold ? 8 : 4) {
                ForEach(options.indices, id: \.self) { index in
                    let option = options[index]
                    let on = option.value == selection
                    Button {
                        selection = option.value
                    } label: {
                        Text(option.title)
                            .font(.body.weight(style == .bold || on ? .semibold : .regular))
                            .foregroundStyle(on ? Color.white : Color(nsColor: t.ink))
                            .lineLimit(1)
                            .frame(maxWidth: .infinity, minHeight: LunalyrDesign.Size.minimumHitTarget)
                            .themedControl(radius: 8, selected: on, state: forced ?? (enabled ? .normal : .disabled))
                            .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .accessibilityAddTraits(on ? .isSelected : [])
                }
            }
            .accessibilityElement(children: .contain)
            .accessibilityLabel(title)
        }
    }
}

// MARK: Slider

/// The track and thumb alone: drag or click to set, arrow keys and VoiceOver
/// to step. `step` snaps the value.
struct ThemedSliderTrack: View {
    @Binding var value: Double
    let range: ClosedRange<Double>
    var step: Double?
    @Environment(\.isEnabled) private var enabled
    @Environment(\.themeForcedState) private var forced
    @Environment(\.layoutDirection) private var layoutDirection

    private var fraction: Double {
        let span = range.upperBound - range.lowerBound
        return span > 0 ? min(1, max(0, (value - range.lowerBound) / span)) : 0
    }

    private var increment: Double { step ?? (range.upperBound - range.lowerBound) / 20 }

    private func set(_ proposed: Double) {
        var next = proposed
        if let step, step > 0 { next = (next / step).rounded() * step }
        next = min(range.upperBound, max(range.lowerBound, next))
        if next != value { value = next }
    }

    var body: some View {
        ThemeReader { _, t in
            GeometryReader { proxy in
                let thumb: CGFloat = 22
                let travel = max(1, proxy.size.width - thumb)
                ZStack(alignment: .leading) {
                    Capsule().fill(Color(nsColor: t.face))
                        .overlay(Capsule().strokeBorder(Color(nsColor: t.controlEdge), lineWidth: t.outline))
                        .frame(height: 8)
                    Capsule().fill(Color.lunalyrAccent)
                        .frame(width: thumb / 2 + travel * fraction, height: 8)
                    Color.clear
                        .frame(width: thumb, height: thumb)
                        .themedControl(radius: 11, state: forced ?? (enabled ? .normal : .disabled))
                        .offset(x: travel * fraction)
                }
                .frame(height: proxy.size.height)
                .contentShape(Rectangle())
                .gesture(DragGesture(minimumDistance: 0).onChanged { drag in
                    guard enabled else { return }
                    let f = min(1, max(0, (drag.location.x - thumb / 2) / travel))
                    set(range.lowerBound + f * (range.upperBound - range.lowerBound))
                })
            }
            .frame(height: 26)
            .focusable(enabled)
            .onKeyPress(keys: [.leftArrow, .rightArrow, .upArrow, .downArrow]) { press in
                guard enabled, press.modifiers.isEmpty else { return .ignored }
                let forward = layoutDirection == .leftToRight ? KeyEquivalent.rightArrow : .leftArrow
                set(value + (press.key == forward || press.key == .upArrow ? increment : -increment))
                return .handled
            }
            .accessibilityAdjustableAction { direction in
                guard enabled else { return }
                switch direction {
                case .increment: set(value + increment)
                case .decrement: set(value - increment)
                @unknown default: break
                }
            }
        }
    }
}

struct ThemedSlider: View {
    let title: String
    @Binding var value: Double
    let range: ClosedRange<Double>
    let format: (Double) -> String

    init(_ title: String, value: Binding<Double>, in range: ClosedRange<Double>,
         format: @escaping (Double) -> String) {
        self.title = title
        _value = value
        self.range = range
        self.format = format
    }

    var body: some View {
        ThemeReader { _, t in
            VStack(alignment: .leading, spacing: 6) {
                HStack {
                    Text(title).foregroundStyle(Color(nsColor: t.ink))
                    Spacer()
                    Text(format(value)).monospacedDigit().foregroundStyle(Color(nsColor: t.secondary))
                }
                ThemedSliderTrack(value: $value, range: range)
                    .accessibilityLabel(title)
                    .accessibilityValue(format(value))
            }
        }
    }
}

// MARK: Section header, info button, card

struct ThemedSectionHeader: View {
    let title: String
    let info: String?

    init(_ title: String, info: String? = nil) {
        self.title = title
        self.info = info
    }

    var body: some View {
        ThemeReader { style, t in
            HStack(spacing: 6) {
                Text(style == .bold ? title.uppercased() : title)
                    .font(.system(size: style == .bold ? 11 : 12, weight: style == .bold ? .bold : .semibold))
                    .tracking(style == .bold ? 0.6 : 0)
                    .foregroundStyle(Color(nsColor: style == .bold ? t.ink : t.secondary))
                    .accessibilityAddTraits(.isHeader)
                if let info { ThemedInfoButton(text: info, label: title) }
                Spacer(minLength: 0)
            }
        }
    }
}

/// An ⓘ that opens a longer explanation, so the page itself stays short.
struct ThemedInfoButton: View {
    let text: String
    let label: String
    @State private var shown = false

    var body: some View {
        Button {
            shown.toggle()
        } label: {
            Image(systemName: "info.circle")
                .font(.system(size: 13, weight: .regular))
                .foregroundStyle(Color.lunalyrAccentText)
                .frame(width: LunalyrDesign.Size.minimumHitTarget - 8, height: LunalyrDesign.Size.minimumHitTarget - 8)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help("More about \(label)")
        .accessibilityLabel("More about \(label)")
        .popover(isPresented: $shown, arrowEdge: .bottom) {
            Text(text)
                .lunalyrText(.body)
                .frame(width: 280, alignment: .leading)
                .padding(LunalyrDesign.Spacing.large)
        }
    }
}

/// A titled group of controls on a card.
struct ThemedCard<Content: View>: View {
    let title: String
    let info: String?
    let content: Content

    init(_ title: String, info: String? = nil, @ViewBuilder content: () -> Content) {
        self.title = title
        self.info = info
        self.content = content()
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            ThemedSectionHeader(title, info: info)
            content
        }
        .padding(16)
        .frame(maxWidth: .infinity, alignment: .leading)
        .modifier(ThemedSurface(radius: 12, selected: false, state: nil, isCard: true))
    }
}

// MARK: Miniature

/// A small, live drawing of one look: a card with a header, a switch, a
/// button and two segments, for choosing between looks.
struct ThemeMiniPreview: View {
    let style: SettingsVisualStyle
    @State private var on = true
    @State private var pick = 0

    var body: some View {
        ThemeReader { _, t in
            VStack(alignment: .leading, spacing: 8) {
                ThemedSectionHeader("Section")
                Toggle("Switch", isOn: $on).toggleStyle(ThemedToggleStyle())
                HStack(spacing: 8) {
                    Button("Go") {}.buttonStyle(ThemedButtonStyle(prominent: true))
                    ThemedSegments("Pick", selection: $pick, options: [(0, "A"), (1, "B")])
                }
            }
            .font(.callout)
            .padding(12)
            .modifier(ThemedSurface(radius: 12, selected: false, state: nil, isCard: true))
            .padding(10)
            .frame(maxWidth: .infinity)
            .background(Color(nsColor: t.canvas))
        }
        .environment(\.themeStyleOverride, style)
        .allowsHitTesting(false)
        .frame(height: 150)
        .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
        .accessibilityHidden(true)
    }
}

// MARK: Style Gallery

/// Every component in both looks and every state, side by side:
/// the preview for judging the system (`#Preview "Style Gallery"`) and the
/// source of the gallery screenshots (UI snapshot tests).
struct StyleGallery: View {
    @State private var on = true
    @State private var off = false
    @State private var pick = 1
    @State private var level = 0.6

    var body: some View {
        HStack(alignment: .top, spacing: 0) {
            ForEach(SettingsVisualStyle.allCases) { style in
                column(style)
            }
        }
        .frame(width: 1080)
    }

    private func column(_ style: SettingsVisualStyle) -> some View {
        ThemeReader { _, t in
            VStack(alignment: .leading, spacing: 18) {
                Text(style.title).font(.title2.weight(.bold)).foregroundStyle(Color(nsColor: t.ink))
                ThemedSectionHeader("Buttons")
                stateGrid { state in
                    Button(state.rawValue.capitalized) {}
                        .buttonStyle(ThemedButtonStyle())
                        .environment(\.themeForcedState, state)
                }
                ThemedSectionHeader("Prominent")
                stateGrid { state in
                    Button(state.rawValue.capitalized) {}
                        .buttonStyle(ThemedButtonStyle(prominent: true))
                        .environment(\.themeForcedState, state)
                }
                ThemedSectionHeader("Toggles", info: "Switches are the accent when on.")
                HStack(spacing: 18) {
                    Toggle("On", isOn: $on).toggleStyle(ThemedToggleStyle()).fixedSize()
                    Toggle("Off", isOn: $off).toggleStyle(ThemedToggleStyle()).fixedSize()
                    Toggle("Disabled", isOn: $on).toggleStyle(ThemedToggleStyle()).fixedSize()
                        .environment(\.themeForcedState, .disabled)
                    Toggle("Focused", isOn: $off).toggleStyle(ThemedToggleStyle()).fixedSize()
                        .environment(\.themeForcedState, .focused)
                }
                ThemedSegments("Pick", selection: $pick, options: [(0, "Automatic"), (1, "Music"), (2, "Spotify")])
                ThemedSlider("Lyric size", value: $level, in: 0...1, format: { "\(Int($0 * 100))%" })
                ThemedCard("Card", info: "A titled group of controls.") {
                    Text("Body text on the card face.").foregroundStyle(Color(nsColor: t.ink))
                    Text("Secondary text uses the secondary ink.").lunalyrText(.caption)
                }
            }
            .padding(.horizontal, 30)
            .padding(.vertical, 24)
            .frame(width: 540, alignment: .topLeading)
            .background(Color(nsColor: t.canvas))
        }
        .environment(\.themeStyleOverride, style)
        .tint(.lunalyrAccentText)
    }

    private func stateGrid<Item: View>(@ViewBuilder _ item: @escaping (ThemeState) -> Item) -> some View {
        HStack(spacing: 14) {
            ForEach(ThemeState.allCases, id: \.self) { state in
                item(state)
            }
        }
    }
}

#Preview("Style Gallery") {
    StyleGallery()
}

// MARK: Pages

/// A settings page: cards in a scrolling column, with the look's switches and
/// buttons applied to every control inside.
struct ThemedForm<Content: View>: View {
    let content: Content

    init(@ViewBuilder content: () -> Content) {
        self.content = content()
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                content
            }
            .padding(20)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .scrollContentBackground(.hidden)
        .toggleStyle(ThemedToggleStyle())
        .buttonStyle(ThemedButtonStyle())
        .labeledContentStyle(ThemedLabeledContentStyle())
        .environment(\.compactCaptions, true)
    }
}

/// Label on the left, value or control on the right, as a form row.
struct ThemedLabeledContentStyle: LabeledContentStyle {
    func makeBody(configuration: Configuration) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: LunalyrDesign.Spacing.medium) {
            configuration.label
            Spacer(minLength: LunalyrDesign.Spacing.small)
            configuration.content
        }
    }
}

/// A titled card inside a `ThemedForm`; the stand-in for a form `Section`.
struct ThemedSection<Content: View>: View {
    let title: String
    let content: Content

    init(_ title: String, @ViewBuilder content: () -> Content) {
        self.title = title
        self.content = content()
    }

    var body: some View {
        ThemedCard(title) { content }
    }
}
