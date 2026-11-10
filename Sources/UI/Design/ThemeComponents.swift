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
    @Environment(\.isFocused) private var focused
    @State private var hovering = false

    var body: some View {
        ThemeReader { _, t in
            Button {
                configuration.isOn.toggle()
            } label: {
                HStack {
                    configuration.label
                        .foregroundStyle(Color(nsColor: t.ink))
                    Spacer(minLength: LunalyrDesign.Spacing.medium)
                    ThemedSwitchTrack(on: configuration.isOn, focused: focused, hovering: hovering)
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(PressReportingButtonStyle())
            .accessibilityValue(configuration.isOn ? "On" : "Off")
            .accessibilityAddTraits(.isToggle)
        }
        .onHover { hovering = $0 }
    }
}

private struct ThemePressedKey: EnvironmentKey { static let defaultValue = false }

extension EnvironmentValues {
    /// Whether the button around this view is held down (`PressReportingButtonStyle`).
    fileprivate var themeButtonPressed: Bool {
        get { self[ThemePressedKey.self] }
        set { self[ThemePressedKey.self] = newValue }
    }
}

/// Draws the label as it is, and tells it whether the button is held down,
/// so a control drawn inside (the switch's track) can show its pressed face.
struct PressReportingButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .environment(\.themeButtonPressed, configuration.isPressed)
    }
}

/// The switch itself: the track, and a knob that slides to the on side.
struct ThemedSwitchTrack: View {
    let on: Bool
    var focused = false
    var hovering = false
    @Environment(\.isEnabled) private var enabled
    @Environment(\.themeForcedState) private var forced
    @Environment(\.themeButtonPressed) private var pressed
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    /// Track 42 wide, knob 16 inset by 4: the knob travels 42 - 24 points.
    static let travel: CGFloat = 18

    var body: some View {
        let state = resolvedState(forced: forced, enabled: enabled, pressed: pressed, focused: focused, hovering: hovering)
        ThemeReader { style, t in
            let knob = RoundedRectangle(cornerRadius: style == .bold ? 3 : 9, style: .continuous)
            Color.clear
                .frame(width: 42, height: 24)
                // One alignment and an offset, so the knob slides rather than jumps.
                .overlay(alignment: .leading) {
                    knob
                        .fill(on ? Color.white : Color(nsColor: t.ink))
                        .overlay(knob.strokeBorder(Color(nsColor: t.controlEdge), lineWidth: on && style == .bold ? 1 : 0))
                        .frame(width: 16, height: 16)
                        .padding(4)
                        .offset(x: on ? Self.travel : 0)
                }
                .themedControl(radius: 12, selected: on, state: state)
                // Scoped to the switch: the change that flips it never animates the preview.
                .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion), value: on)
        }
    }
}

// MARK: Segmented picker

struct ThemedSegments<Value: Hashable>: View {
    let title: String
    @Binding var selection: Value
    let options: [(value: Value, title: String)]
    @Environment(\.isEnabled) private var enabled
    @Environment(\.themeForcedState) private var forced
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

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
            // The accent moves to the new choice with the click, not a cut.
            .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion),
                       value: selection)
            .accessibilityElement(children: .contain)
            .accessibilityLabel(title)
        }
    }
}

// MARK: Slider

/// The track and thumb alone: drag or click to set, arrow keys and VoiceOver
/// to step. `step` snaps the value. A drag follows the pointer 1:1, keeping
/// where the thumb was grabbed; keyboard steps and outside changes (Reset)
/// glide. Dims as a whole when disabled.
struct ThemedSliderTrack: View {
    @Binding var value: Double
    let range: ClosedRange<Double>
    var step: Double?
    @Environment(\.isEnabled) private var enabled
    @Environment(\.themeForcedState) private var forced
    @Environment(\.layoutDirection) private var layoutDirection
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @GestureState private var dragging = false
    /// Pointer minus thumb centre when the drag began on the thumb; 0 when it
    /// began on the track (the thumb comes to the pointer).
    @State private var grabOffset: CGFloat?
    @FocusState private var focused: Bool

    static let thumb: CGFloat = 22
    /// What a disabled slider is drawn at: track, fill and thumb together.
    static let disabledOpacity = 0.45

    private var fraction: Double {
        ThemedSliderMath.fraction(of: value, in: range)
    }

    private var increment: Double { step ?? (range.upperBound - range.lowerBound) / 20 }

    private func set(_ proposed: Double) {
        let next = ThemedSliderMath.snapped(proposed, in: range, step: step)
        if next != value { value = next }
    }

    private var thumbState: ThemeState {
        // Disabled dims the whole slider instead (below).
        if let forced { return forced == .disabled ? .normal : forced }
        if dragging { return .pressed }
        return focused && enabled ? .focused : .normal
    }

    var body: some View {
        ThemeReader { _, t in
            GeometryReader { proxy in
                let thumb = Self.thumb
                let travel = max(1, proxy.size.width - thumb)
                ZStack(alignment: .leading) {
                    Capsule().fill(Color(nsColor: t.face))
                        .overlay(Capsule().strokeBorder(Color(nsColor: t.controlEdge), lineWidth: t.outline))
                        .frame(height: 8)
                    Group {
                        Capsule().fill(Color.lunalyrAccent)
                            .frame(width: thumb / 2 + travel * fraction, height: 8)
                        Color.clear
                            .frame(width: thumb, height: thumb)
                            .themedControl(radius: 11, state: thumbState)
                            .offset(x: travel * fraction)
                    }
                    // A drag stays 1:1 with the pointer; steps and Reset glide.
                    .animation(dragging || reduceMotion ? nil : LunalyrDesign.Motion.quick, value: fraction)
                }
                .frame(height: proxy.size.height)
                .contentShape(Rectangle())
                .gesture(DragGesture(minimumDistance: 0)
                    .updating($dragging) { _, state, _ in state = enabled }
                    .onChanged { drag in
                        guard enabled else { return }
                        let offset = grabOffset ?? ThemedSliderMath.grabOffset(
                            start: drag.startLocation.x, fraction: fraction, travel: travel, thumb: thumb)
                        grabOffset = offset
                        let f = ThemedSliderMath.fraction(pointer: drag.location.x, grabOffset: offset,
                                                          travel: travel, thumb: thumb)
                        set(range.lowerBound + f * (range.upperBound - range.lowerBound))
                    }
                    .onEnded { _ in grabOffset = nil })
            }
            .frame(height: 26)
            // Flattened first, so the track never shows through a dimmed thumb.
            .compositingGroup()
            .opacity(enabled && forced != .disabled ? 1 : Self.disabledOpacity)
            .focusable(enabled)
            .focused($focused)
            // The thumb draws the look's own focus ring instead of a box round the track.
            .focusEffectDisabled()
            .onChange(of: dragging) { _, now in if !now { grabOffset = nil } }
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

/// The slider's arithmetic, apart from the view so it can be tested.
enum ThemedSliderMath {
    static func fraction(of value: Double, in range: ClosedRange<Double>) -> Double {
        let span = range.upperBound - range.lowerBound
        return span > 0 ? min(1, max(0, (value - range.lowerBound) / span)) : 0
    }

    static func snapped(_ proposed: Double, in range: ClosedRange<Double>, step: Double?) -> Double {
        var next = proposed
        if let step, step > 0 { next = (next / step).rounded() * step }
        return min(range.upperBound, max(range.lowerBound, next))
    }

    /// Where on the thumb a drag starting at `start` took hold: the pointer
    /// minus the thumb's centre if it began on the thumb, else 0, so a click
    /// on the track brings the thumb's centre to the pointer.
    static func grabOffset(start: CGFloat, fraction: Double, travel: CGFloat, thumb: CGFloat) -> CGFloat {
        let centre = thumb / 2 + travel * CGFloat(fraction)
        let offset = start - centre
        return abs(offset) <= thumb / 2 ? offset : 0
    }

    /// The fraction a pointer at `pointer` asks for, holding the thumb where it was grabbed.
    static func fraction(pointer: CGFloat, grabOffset: CGFloat, travel: CGFloat, thumb: CGFloat) -> Double {
        Double(min(1, max(0, (pointer - grabOffset - thumb / 2) / travel)))
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

    @Environment(\.isEnabled) private var enabled
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        ThemeReader { _, t in
            VStack(alignment: .leading, spacing: 6) {
                HStack {
                    Text(title).foregroundStyle(Color(nsColor: t.ink))
                    Spacer()
                    Text(format(value)).monospacedDigit().foregroundStyle(Color(nsColor: t.secondary))
                        .contentTransition(.numericText(value: value))
                        .animation(reduceMotion ? nil : LunalyrDesign.Motion.quick, value: value)
                }
                .opacity(enabled ? 1 : ThemedSliderTrack.disabledOpacity)
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
                HStack(spacing: 18) {
                    Toggle("Pressed on", isOn: $on).toggleStyle(ThemedToggleStyle()).fixedSize()
                        .environment(\.themeForcedState, .pressed)
                    Toggle("Pressed off", isOn: $off).toggleStyle(ThemedToggleStyle()).fixedSize()
                        .environment(\.themeForcedState, .pressed)
                }
                ThemedSegments("Pick", selection: $pick, options: [(0, "Automatic"), (1, "Music"), (2, "Spotify")])
                ThemedSlider("Lyric size", value: $level, in: 0...1, format: { "\(Int($0 * 100))%" })
                HStack(spacing: 14) {
                    ForEach([ThemeState.pressed, .focused, .disabled], id: \.self) { state in
                        VStack(alignment: .leading, spacing: 2) {
                            Text(state.rawValue.capitalized).lunalyrText(.caption)
                            ThemedSliderTrack(value: $level, range: 0...1)
                                .environment(\.themeForcedState, state)
                        }
                    }
                }
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

    @Environment(\.formScrollMemory) private var memory
    @Environment(\.formScrollKey) private var key
    @Environment(\.formScrollRestores) private var restores

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                content
            }
            .padding(20)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background {
                if let memory, let key {
                    FormScrollAnchor(memory: memory, key: key, restores: restores)
                        .accessibilityHidden(true)
                }
            }
        }
        .scrollContentBackground(.hidden)
        .toggleStyle(ThemedToggleStyle())
        .buttonStyle(ThemedButtonStyle())
        .labeledContentStyle(ThemedLabeledContentStyle())
        .environment(\.compactCaptions, true)
    }
}

/// Inside a `ThemedForm`'s scrolling content: puts the scroll view back where
/// `memory` says this page was left, then records where it is scrolled to.
/// It watches the clip view's bounds, so scrolling redraws nothing.
private struct FormScrollAnchor: NSViewRepresentable {
    let memory: FormScrollMemory
    let key: String
    let restores: Bool

    func makeNSView(context: Context) -> FormScrollAnchorView {
        FormScrollAnchorView(memory: memory, key: key, restores: restores)
    }

    func updateNSView(_ view: FormScrollAnchorView, context: Context) {}
}

final class FormScrollAnchorView: NSView {
    private let memory: FormScrollMemory
    private let key: String
    private let restores: Bool
    private var observer: NSObjectProtocol?
    /// Nothing is recorded until any restore is done, so the first layout's
    /// top can't overwrite the remembered offset.
    private var ready = false

    init(memory: FormScrollMemory, key: String, restores: Bool) {
        self.memory = memory
        self.key = key
        self.restores = restores
        super.init(frame: .zero)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) is not used") }

    override func hitTest(_ point: NSPoint) -> NSView? { nil }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        guard window != nil else {
            stopWatching()
            return
        }
        guard observer == nil, let clip = enclosingScrollView?.contentView else { return }
        clip.postsBoundsChangedNotifications = true
        observer = NotificationCenter.default.addObserver(forName: NSView.boundsDidChangeNotification, object: clip,
                                                          queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.record() }
        }
        // After SwiftUI has sized the content.
        DispatchQueue.main.async { [weak self] in self?.restore() }
    }

    private func restore() {
        defer { ready = true }
        guard restores, let y = memory.offsets[key], let scroll = enclosingScrollView else { return }
        let clip = scroll.contentView
        let bottom = (scroll.documentView?.frame.height ?? 0) - clip.bounds.height + clip.contentInsets.bottom
        clip.scroll(to: NSPoint(x: clip.bounds.minX, y: max(-clip.contentInsets.top, min(y, bottom))))
        scroll.reflectScrolledClipView(clip)
    }

    private func record() {
        guard ready, let clip = enclosingScrollView?.contentView else { return }
        memory.offsets[key] = clip.bounds.minY
    }

    private func stopWatching() {
        if let observer { NotificationCenter.default.removeObserver(observer) }
        observer = nil
    }

    deinit {
        if let observer { NotificationCenter.default.removeObserver(observer) }
    }
}

/// Where each settings page was scrolled to (the clip view's bounds origin),
/// by page, for the life of the window. A plain reference: writing it while
/// scrolling redraws nothing.
final class FormScrollMemory {
    var offsets: [String: CGFloat] = [:]
}

private struct FormScrollMemoryKey: EnvironmentKey { static let defaultValue: FormScrollMemory? = nil }
private struct FormScrollKeyKey: EnvironmentKey { static let defaultValue: String? = nil }
private struct FormScrollRestoresKey: EnvironmentKey { static let defaultValue = true }

extension EnvironmentValues {
    /// Set by the Settings window: `ThemedForm`s below remember their scroll offset here.
    var formScrollMemory: FormScrollMemory? {
        get { self[FormScrollMemoryKey.self] }
        set { self[FormScrollMemoryKey.self] = newValue }
    }

    /// Which page a `ThemedForm` is, for `formScrollMemory`.
    var formScrollKey: String? {
        get { self[FormScrollKeyKey.self] }
        set { self[FormScrollKeyKey.self] = newValue }
    }

    /// False while another surface's request scrolls the page to a section:
    /// that visit opens there instead of where the page was left.
    var formScrollRestores: Bool {
        get { self[FormScrollRestoresKey.self] }
        set { self[FormScrollRestoresKey.self] = newValue }
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
