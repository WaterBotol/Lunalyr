import AppKit
import SwiftUI

// MARK: - Theme components (SPEC.md §2, §4 and §8)
//
// The form language of Lunalyr's own windows: plain sections on the window
// fill (no cards, no borders, no dividers), 13 pt rows, 11.5 pt captions,
// and one set of controls (switch, checkbox, pop-up, button, segmented,
// slider) drawn from `LunalyrDesign.Studio`. Two window styles share it:
// Standard (the design itself) and Bold, which keeps its 1.5 pt outlines and
// hard shadows on every control (`ThemedSurface`) over the same structure.
//
//     ThemedForm {                          // the window draws the page title
//         ThemedSection("Startup") {
//             Toggle("Launch at login", isOn: $on)            // a switch
//         }
//         ThemedSection("Discord") {
//             CheckboxList {                                  // checkboxes
//                 Toggle("Show the current lyric", isOn: $lyric)
//                 Toggle("Show the album cover", isOn: $cover)
//             }
//         }
//     }
//
// Switch or checkbox (SPEC §4, the mocks): a switch for a setting that
// stands alone and turns a feature on or off; checkboxes for a list of
// related options under one heading (Discord's six, the lyric sources,
// the shortcut rows, Keep tone marks / Prefer saved readings). Every
// `Toggle` in a `ThemedForm` is a switch; wrap a list in `CheckboxList` (or
// apply `ThemedCheckboxStyle`) to make its rows checkboxes. Both keep
// SwiftUI `Toggle` semantics (VoiceOver hears a switch or checkbox, Space
// flips it).

/// What a component looks like at a moment.
enum ThemeState: String, CaseIterable {
    case normal, hover, pressed, disabled, focused
}

/// How tightly a form packs its sections and rows.
enum FormDensity: Equatable {
    /// A page without a picture: sections 28 apart, rows 8.
    case page
    /// The Look inspector beside the picture: sections 22 apart, rows 9.
    case inspector

    var sectionSpacing: CGFloat {
        self == .page ? LunalyrDesign.Studio.Layout.pageSectionSpacing : LunalyrDesign.Studio.Layout.inspectorSectionSpacing
    }

    var rowSpacing: CGFloat {
        self == .page ? LunalyrDesign.Studio.Layout.pageRowSpacing : LunalyrDesign.Studio.Layout.inspectorRowSpacing
    }
}

private struct ThemeStyleOverrideKey: EnvironmentKey { static let defaultValue: SettingsVisualStyle? = nil }
private struct ThemeForcedStateKey: EnvironmentKey { static let defaultValue: ThemeState? = nil }
private struct FormDensityKey: EnvironmentKey { static let defaultValue = FormDensity.page }
private struct ThemeOnCanvasKey: EnvironmentKey { static let defaultValue = false }

extension EnvironmentValues {
    /// Draws components in this style instead of the chosen one (the Style Gallery).
    var themeStyleOverride: SettingsVisualStyle? {
        get { self[ThemeStyleOverrideKey.self] }
        set { self[ThemeStyleOverrideKey.self] = newValue }
    }

    /// Holds every component at one state (the Style Gallery).
    var themeForcedState: ThemeState? {
        get { self[ThemeForcedStateKey.self] }
        set { self[ThemeForcedStateKey.self] = newValue }
    }

    /// `.page` for a page without a picture (the default), `.inspector` for
    /// the Look inspector: set it on the inspector and every `ThemedForm` and
    /// `ThemedSection` inside packs to that density.
    var formDensity: FormDensity {
        get { self[FormDensityKey.self] }
        set { self[FormDensityKey.self] = newValue }
    }

    /// Controls sit on the canvas grey (Timing & sources' This song, the
    /// picture's strip): their fill is `controlOnCanvas`, fields are white.
    var themeOnCanvas: Bool {
        get { self[ThemeOnCanvasKey.self] }
        set { self[ThemeOnCanvasKey.self] = newValue }
    }
}

/// Resolves the window style and its tokens for the current appearance.
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

/// The 1 pt edge Increase Contrast adds to controls and tiles (ink at 25 %),
/// where fills alone separate them otherwise; clear without it. Resolved
/// from the appearance, so it follows Increase Contrast wherever AppKit
/// draws it (and in the UI snapshots' contrast looks).
private struct ContrastEdgeColor {
    static let color = Color(nsColor: NSColor(name: "LunalyrStudio.contrastEdge") { appearance in
        let look = LunalyrAppearance(appearance)
        guard look.increaseContrast else { return .clear }
        return LunalyrDesign.Studio.color(.ink, dark: look.dark).withAlphaComponent(0.25)
    })
}

extension Color {
    /// Ink at 25 %: the edge controls get under Increase Contrast.
    static var studioContrastEdge: Color { ContrastEdgeColor.color }
}

// MARK: Surface

/// The one surface every control is drawn on: its fill, the pressed and hover
/// tints and the focus ring; Bold adds its outline and hard shadow.
struct ThemedSurface: ViewModifier {
    let radius: CGFloat
    let selected: Bool
    /// nil for a card, which has no states.
    let state: ThemeState?
    let isCard: Bool
    /// A tile: a step darker than a control in Standard, so it reads as a
    /// button wherever it sits.
    var recessed = false
    @Environment(\.themeOnCanvas) private var onCanvas

    func body(content: Content) -> some View {
        ThemeReader { style, t in
            let shape = RoundedRectangle(cornerRadius: radius * style.cornerScale, style: .circular)
            let current = state ?? .normal
            let pressed = current == .pressed
            let shadowed = style == .bold && !pressed && current != .disabled
            let shift: CGFloat = style == .bold && pressed ? t.shadowOffset * 0.66 : 0
            content
                .background(fill(style: style, t: t, state: current).clipShape(shape))
                .overlay { edge(style: style, t: t, shape: shape) }
                .background(alignment: .topLeading) {
                    if shadowed, let shadow = t.shadow {
                        shape.fill(Color(nsColor: shadow)).offset(x: t.shadowOffset, y: t.shadowOffset)
                    }
                }
                .overlay {
                    if current == .focused {
                        shape.inset(by: -3).strokeBorder(Color.studio(.accent), lineWidth: 2)
                    }
                }
                .offset(x: shift, y: shift)
                .opacity(current == .disabled ? 0.4 : 1)
        }
    }

    /// Standard has no edges (fills separate), except under Increase
    /// Contrast; Bold outlines everything that isn't the accent.
    @ViewBuilder private func edge(style: SettingsVisualStyle, t: SettingsVisualStyle.Tokens,
                                   shape: RoundedRectangle) -> some View {
        if style == .bold {
            if !selected {
                shape.strokeBorder(Color(nsColor: isCard ? t.cardEdge : t.controlEdge), lineWidth: t.outline)
            }
        } else {
            shape.strokeBorder(Color.studioContrastEdge, lineWidth: 1)
        }
    }

    @ViewBuilder private func fill(style: SettingsVisualStyle, t: SettingsVisualStyle.Tokens, state: ThemeState) -> some View {
        if selected {
            ZStack {
                Color.studio(.accent)
                if state == .pressed { Color.black.opacity(0.18) }
                if state == .hover { Color.white.opacity(0.12) }
            }
        } else if style == .bold {
            ZStack {
                Color(nsColor: t.face)
                if state == .hover { Color.lunalyrAccent.opacity(0.12) }
            }
        } else {
            ZStack {
                Color.studio(onCanvas ? .controlOnCanvas : .control)
                if recessed { Color.primary.opacity(0.04) }
                if state == .hover { Color.primary.opacity(0.04) }
                if state == .pressed { Color.primary.opacity(0.1) }
            }
        }
    }
}

extension View {
    /// A control face in the chosen window style.
    func themedControl(radius: CGFloat = 6, selected: Bool = false, state: ThemeState = .normal) -> some View {
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

/// Push buttons: 24 high, radius 6, 12.5 pt on the control fill; padding 10,
/// or 7 at `.controlSize(.small)`. `prominent` is the accent.
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
    @Environment(\.controlSize) private var controlSize
    @State private var hovering = false

    var body: some View {
        let state = resolvedState(forced: forced, enabled: enabled, pressed: configuration.isPressed,
                                  focused: focused, hovering: hovering)
        let compact = controlSize == .small || controlSize == .mini
        ThemeReader { style, _ in
            configuration.label
                .font(.system(size: compact ? 12 : 12.5, weight: style == .bold ? .semibold : .regular))
                .foregroundStyle(prominent ? Color.white : Color.studio(.ink))
                .lineLimit(1)
                // A button's title is never cut short ("E…" for Earlier).
                .fixedSize()
                .padding(.horizontal, compact ? 7 : 10)
                .frame(minHeight: 24)
                .themedControl(radius: 6, selected: prominent, state: state)
                .contentShape(Rectangle())
        }
        .onHover { hovering = $0 }
    }
}

// MARK: Switch

/// A switch: for a setting that stands alone (see the top of this file).
/// Label left, 13 pt; the switch at the row's end.
struct ThemedToggleStyle: ToggleStyle {
    func makeBody(configuration: Configuration) -> some View {
        ThemedToggleBody(configuration: configuration)
    }
}

private struct ThemedToggleBody: View {
    let configuration: ToggleStyleConfiguration
    @Environment(\.isFocused) private var focused
    @Environment(\.isEnabled) private var enabled
    @State private var hovering = false

    var body: some View {
        Button {
            configuration.isOn.toggle()
        } label: {
            HStack(alignment: .center, spacing: 10) {
                configuration.label
                    .font(LunalyrDesign.Studio.TypeScale.row)
                    .foregroundStyle(Color.studio(.ink))
                    .opacity(enabled ? 1 : 0.4)
                    .fixedSize(horizontal: false, vertical: true)
                    .frame(maxWidth: .infinity, alignment: .leading)
                ThemedSwitchTrack(on: configuration.isOn, focused: focused, hovering: hovering)
            }
            .frame(minHeight: 24)
            .contentShape(Rectangle())
        }
        .buttonStyle(PressReportingButtonStyle())
        .accessibilityValue(configuration.isOn ? "On" : "Off")
        .accessibilityAddTraits(.isToggle)
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

/// The switch itself: 29 × 17, accent when on, the track grey when off, a
/// white knob that slides. Bold: its 42 × 24 outlined switch.
struct ThemedSwitchTrack: View {
    let on: Bool
    var focused = false
    var hovering = false
    @Environment(\.isEnabled) private var enabled
    @Environment(\.themeForcedState) private var forced
    @Environment(\.themeButtonPressed) private var pressed
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.colorScheme) private var scheme

    static let size = CGSize(width: 29, height: 17)

    var body: some View {
        let state = resolvedState(forced: forced, enabled: enabled, pressed: pressed, focused: focused, hovering: hovering)
        ThemeReader { style, t in
            Group {
                if style == .bold { bold(t: t, state: state) } else { standard(state: state) }
            }
            // Scoped to the switch: the change that flips it never animates the preview.
            .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion), value: on)
        }
    }

    private func standard(state: ThemeState) -> some View {
        let dark = scheme == .dark
        // Off is the slider's track, 1.8× (light) / 1.6× (dark) as strong.
        let off = dark ? Color.white.opacity(0.16 * 1.6) : Color.black.opacity(0.10 * 1.8)
        return Capsule()
            .fill(on ? Color.studio(.accent) : off)
            .overlay { Capsule().strokeBorder(Color.studioContrastEdge, lineWidth: 1) }
            .frame(width: Self.size.width, height: Self.size.height)
            .overlay(alignment: .leading) {
                Circle().fill(Color.white)
                    .shadow(color: .black.opacity(0.2), radius: 1, y: 0.5)
                    .padding(2)
                    .frame(width: Self.size.height, height: Self.size.height)
                    .offset(x: on ? Self.size.width - Self.size.height : 0)
            }
            .overlay {
                if state == .focused {
                    Capsule().inset(by: -3).strokeBorder(Color.studio(.accent), lineWidth: 2)
                }
            }
            .overlay { if state == .pressed { Capsule().fill(Color.black.opacity(0.08)) } }
            .opacity(state == .disabled ? 0.4 : 1)
    }

    private func bold(t: SettingsVisualStyle.Tokens, state: ThemeState) -> some View {
        let knob = RoundedRectangle(cornerRadius: 3, style: .continuous)
        return Color.clear
            .frame(width: 42, height: 24)
            .overlay(alignment: .leading) {
                knob
                    .fill(on ? Color.white : Color(nsColor: t.ink))
                    .overlay(knob.strokeBorder(Color(nsColor: t.controlEdge), lineWidth: on ? 1 : 0))
                    .frame(width: 16, height: 16)
                    .padding(4)
                    .offset(x: on ? 18 : 0)
            }
            .themedControl(radius: 12, selected: on, state: state)
    }
}

// MARK: Checkbox

/// A checkbox: for a list of related options (see the top of this file).
/// 15 × 15, radius 3.5, 8 to its label; the label may carry a caption under it.
struct ThemedCheckboxStyle: ToggleStyle {
    func makeBody(configuration: Configuration) -> some View {
        ThemedCheckboxBody(configuration: configuration)
    }
}

private struct ThemedCheckboxBody: View {
    let configuration: ToggleStyleConfiguration
    @Environment(\.isFocused) private var focused
    @Environment(\.isEnabled) private var enabled
    @Environment(\.themeForcedState) private var forced

    var body: some View {
        Button {
            configuration.isOn.toggle()
        } label: {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                ThemedCheckboxBox(on: configuration.isOn,
                                  state: resolvedState(forced: forced, enabled: enabled, pressed: false,
                                                       focused: focused, hovering: false))
                    .alignmentGuide(.firstTextBaseline) { $0[VerticalAlignment.center] + 4.5 }
                configuration.label
                    .font(LunalyrDesign.Studio.TypeScale.row)
                    .foregroundStyle(Color.studio(.ink))
                    .opacity(enabled ? 1 : 0.4)
                    .fixedSize(horizontal: false, vertical: true)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityValue(configuration.isOn ? "On" : "Off")
        .accessibilityAddTraits(.isToggle)
    }
}

/// The box alone: accent with a white tick when on; white (dark: a grey)
/// with a hairline edge when off. Bold: square, outlined, hard shadow.
struct ThemedCheckboxBox: View {
    let on: Bool
    var state: ThemeState = .normal

    static let size: CGFloat = 15

    var body: some View {
        ThemeReader { style, _ in
            let shape = RoundedRectangle(cornerRadius: style == .bold ? 2.5 : 3.5, style: .continuous)
            ZStack {
                if style == .bold {
                    Color.clear.themedControl(radius: 2.5, selected: on, state: state == .disabled ? .disabled : .normal)
                } else {
                    shape.fill(on ? Color.studio(.accent) : Color.studio(.checkboxOff))
                    if !on { shape.strokeBorder(Color.studio(.checkboxEdge), lineWidth: 1) }
                }
                if on {
                    Image(systemName: "checkmark")
                        .font(.system(size: 9, weight: .bold))
                        .foregroundStyle(.white)
                }
            }
            .frame(width: Self.size, height: Self.size)
            .overlay {
                if state == .focused {
                    shape.inset(by: -3).strokeBorder(Color.studio(.accent), lineWidth: 2)
                }
            }
            .opacity(style != .bold && state == .disabled ? 0.45 : 1)
            .accessibilityHidden(true)
        }
    }
}

/// A list of related options: every `Toggle` inside is a checkbox, 7 apart.
struct CheckboxList<Content: View>: View {
    let content: Content

    init(@ViewBuilder content: () -> Content) {
        self.content = content()
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 7) {
            content
        }
        .toggleStyle(ThemedCheckboxStyle())
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

// MARK: Segmented picker

/// Segments on a control-filled track (radius 8, 2 inset); the chosen one is
/// raised white (dark: grey) with a soft shadow, semibold ink; the others ink2.
struct ThemedSegments<Value: Hashable>: View {
    let title: String
    @Binding var selection: Value
    let options: [(value: Value, title: String)]
    var small = false
    @Environment(\.isEnabled) private var enabled
    @Environment(\.themeForcedState) private var forced
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.themeOnCanvas) private var onCanvas
    @Environment(\.colorScheme) private var scheme
    @Namespace private var chosen

    init(_ title: String, selection: Binding<Value>, options: [(Value, String)], small: Bool = false) {
        self.title = title
        _selection = selection
        self.options = options.map { (value: $0.0, title: $0.1) }
        self.small = small
    }

    var body: some View {
        ThemeReader { style, t in
            Group {
                if style == .bold { bold(t: t) } else { standard }
            }
            // The chosen face moves to the new choice with the click, not a cut.
            .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion),
                       value: selection)
            .accessibilityElement(children: .contain)
            .accessibilityLabel(title)
        }
    }

    private var standard: some View {
        let track = RoundedRectangle(cornerRadius: 8, style: .continuous)
        return HStack(spacing: 2) {
            ForEach(options.indices, id: \.self) { index in
                let option = options[index]
                let on = option.value == selection
                Button {
                    selection = option.value
                } label: {
                    Text(option.title)
                        .font(.system(size: small ? 12 : 13, weight: on ? .semibold : .regular))
                        .foregroundStyle(on ? Color.studio(.ink) : Color.studio(.ink2))
                        .lineLimit(1)
                        .fixedSize()
                        .padding(.horizontal, small ? 10 : 14)
                        .frame(height: small ? 22 : 26)
                        .background {
                            if on {
                                RoundedRectangle(cornerRadius: 6, style: .continuous)
                                    .fill(Color.studio(.segmentSelected))
                                    .shadow(color: .black.opacity(scheme == .dark ? 0.3 : 0.12), radius: 1.5, y: 0.5)
                                    .matchedGeometryEffect(id: "chosen", in: chosen)
                            }
                        }
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityAddTraits(on ? .isSelected : [])
            }
        }
        .padding(2)
        .background(track.fill(Color.studio(onCanvas ? .controlOnCanvas : .control)))
        .overlay { track.strokeBorder(Color.studioContrastEdge, lineWidth: 1) }
        .opacity(enabled && forced != .disabled ? 1 : 0.4)
        .fixedSize()
    }

    private func bold(t: SettingsVisualStyle.Tokens) -> some View {
        HStack(spacing: 8) {
            ForEach(options.indices, id: \.self) { index in
                let option = options[index]
                let on = option.value == selection
                Button {
                    selection = option.value
                } label: {
                    Text(option.title)
                        .font(.system(size: small ? 12 : 13, weight: .semibold))
                        .foregroundStyle(on ? Color.white : Color(nsColor: t.ink))
                        .lineLimit(1)
                        .fixedSize()
                        .padding(.horizontal, small ? 9 : 12)
                        .frame(height: small ? 22 : 26)
                        .themedControl(radius: 6, selected: on, state: forced ?? (enabled ? .normal : .disabled))
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityAddTraits(on ? .isSelected : [])
            }
        }
        .fixedSize()
    }
}

// MARK: Slider

/// The track and knob alone: a 4 pt track with the accent fill and a 15 pt
/// white knob (Bold: outlined). Drag or click to set, arrow keys and
/// VoiceOver to step. `step` snaps the value. A drag follows the pointer
/// 1:1, keeping where the knob was grabbed; keyboard steps and outside
/// changes (Reset) glide. Dims as a whole when disabled.
struct ThemedSliderTrack: View {
    @Binding var value: Double
    let range: ClosedRange<Double>
    var step: Double?
    @Environment(\.isEnabled) private var enabled
    @Environment(\.themeForcedState) private var forced
    @Environment(\.layoutDirection) private var layoutDirection
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.colorSchemeContrast) private var contrast
    @GestureState private var dragging = false
    /// Pointer minus knob centre when the drag began on the knob; 0 when it
    /// began on the track (the knob comes to the pointer).
    @State private var grabOffset: CGFloat?
    @FocusState private var focused: Bool

    static let thumb: CGFloat = 15
    /// What a disabled slider is drawn at: track, fill and knob together.
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
        ThemeReader { style, t in
            GeometryReader { proxy in
                let thumb = Self.thumb
                let travel = max(1, proxy.size.width - thumb)
                let bold = style == .bold
                let trackHeight: CGFloat = bold ? 6 : 4
                ZStack(alignment: .leading) {
                    Capsule().fill(bold ? Color(nsColor: t.face) : Color.studio(.track))
                        .overlay {
                            if bold {
                                Capsule().strokeBorder(Color(nsColor: t.controlEdge), lineWidth: t.outline)
                            } else {
                                Capsule().strokeBorder(Color.studioContrastEdge, lineWidth: 1)
                            }
                        }
                        .frame(height: trackHeight)
                    Group {
                        Capsule().fill(Color.studio(.accent))
                            .frame(width: thumb / 2 + travel * fraction, height: trackHeight)
                        knob(bold: bold)
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
            .frame(height: 20)
            // Flattened first, so the track never shows through a dimmed knob.
            .compositingGroup()
            .opacity(enabled && forced != .disabled ? 1 : Self.disabledOpacity)
            .focusable(enabled)
            .focused($focused)
            // The knob draws the accent focus ring instead of a box round the track.
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

    @ViewBuilder private func knob(bold: Bool) -> some View {
        let thumb = Self.thumb
        if bold {
            Color.clear
                .frame(width: thumb, height: thumb)
                .themedControl(radius: thumb / 2, state: thumbState)
        } else {
            Circle().fill(Color.white)
                .shadow(color: .black.opacity(0.28), radius: 1.5, y: 0.5)
                .overlay { Circle().strokeBorder(Color.black.opacity(contrast == .increased ? 0.4 : 0.06), lineWidth: 0.5) }
                .frame(width: thumb, height: thumb)
                .overlay {
                    if thumbState == .focused {
                        Circle().inset(by: -3).strokeBorder(Color.studio(.accent), lineWidth: 2)
                    }
                }
                .scaleEffect(thumbState == .pressed ? 1.08 : 1)
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

    /// Where on the knob a drag starting at `start` took hold: the pointer
    /// minus the knob's centre if it began on the knob, else 0, so a click
    /// on the track brings the knob's centre to the pointer.
    static func grabOffset(start: CGFloat, fraction: Double, travel: CGFloat, thumb: CGFloat) -> CGFloat {
        let centre = thumb / 2 + travel * CGFloat(fraction)
        let offset = start - centre
        return abs(offset) <= thumb / 2 ? offset : 0
    }

    /// The fraction a pointer at `pointer` asks for, holding the knob where it was grabbed.
    static func fraction(pointer: CGFloat, grabOffset: CGFloat, travel: CGFloat, thumb: CGFloat) -> Double {
        Double(min(1, max(0, (pointer - grabOffset - thumb / 2) / travel)))
    }
}

/// A titled slider with its value at the right (12 pt, monospaced digits).
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
        ValueSlider(title, value: $value, in: range, format: format)
    }
}

// MARK: Section header, "More", section

/// A section's title: 13 pt semibold ink, a header to VoiceOver. `info`, a
/// longer explanation, opens from a "More" link beside it.
struct ThemedSectionHeader: View {
    let title: String
    let info: String?
    var value: String?

    init(_ title: String, info: String? = nil, value: String? = nil) {
        self.title = title
        self.info = info
        self.value = value
    }

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            Text(title)
                .font(LunalyrDesign.Studio.TypeScale.section)
                .foregroundStyle(Color.studio(.ink))
                .accessibilityAddTraits(.isHeader)
            if let info { ThemedInfoButton(text: info, label: title) }
            Spacer(minLength: 0)
            if let value {
                Text(value)
                    .font(.system(size: 12))
                    .foregroundStyle(Color.studio(.ink2))
            }
        }
    }
}

/// "More" in the link colour: opens a longer explanation in a popover. The
/// spec's replacement for the ⓘ button (the name is kept for callers).
struct ThemedInfoButton: View {
    let text: String
    let label: String
    @State private var shown = false

    var body: some View {
        Button {
            shown.toggle()
        } label: {
            Text(InspectorCaption.moreTitle)
                .font(LunalyrDesign.Studio.TypeScale.caption)
                .foregroundStyle(Color.studio(.accentText))
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .help("More about \(label)")
        .accessibilityLabel("More about \(label)")
        .popover(isPresented: $shown, arrowEdge: .bottom) {
            ExplanationPopover(text: text)
        }
    }
}

/// The longer explanation a "More" opens.
struct ExplanationPopover: View {
    let text: String

    var body: some View {
        Text(text)
            .font(LunalyrDesign.Studio.TypeScale.row)
            .foregroundStyle(Color.studio(.ink))
            .fixedSize(horizontal: false, vertical: true)
            .frame(width: 300, alignment: .leading)
            .padding(16)
    }
}

/// A titled group of rows: a plain section on the window fill, no card.
/// (The name is kept for callers; there are no cards in this design.)
struct ThemedCard<Content: View>: View {
    let title: String
    let info: String?
    let content: Content
    @Environment(\.formDensity) private var density

    init(_ title: String, info: String? = nil, @ViewBuilder content: () -> Content) {
        self.title = title
        self.info = info
        self.content = content()
    }

    var body: some View {
        VStack(alignment: .leading, spacing: density.rowSpacing) {
            ThemedSectionHeader(title, info: info)
            content
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        // The form's controls even outside a `ThemedForm` (the Look inspector).
        .themedFormControls()
    }
}

// MARK: Style Gallery

/// Every control in both window styles and its states, side by side: the
/// preview for judging the system (`#Preview "Style Gallery"`) and the
/// source of the gallery screenshots (UI snapshot tests).
struct StyleGallery: View {
    @State private var on = true
    @State private var off = false
    @State private var pick = 1
    @State private var level = 0.6
    @State private var menu = 0

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
            VStack(alignment: .leading, spacing: 28) {
                Text(style.title)
                    .font(LunalyrDesign.Studio.TypeScale.title)
                    .foregroundStyle(Color.studio(.ink))
                ThemedCard("Buttons") {
                    stateGrid { state in
                        Button(state.rawValue.capitalized) {}
                            .buttonStyle(ThemedButtonStyle())
                            .environment(\.themeForcedState, state)
                    }
                    stateGrid { state in
                        Button(state.rawValue.capitalized) {}
                            .buttonStyle(ThemedButtonStyle(prominent: true))
                            .environment(\.themeForcedState, state)
                    }
                }
                ThemedCard("Switches") {
                    Toggle("On", isOn: $on)
                    Toggle("Off", isOn: $off)
                    Toggle("Disabled", isOn: $on).disabled(true)
                    InspectorCaption("A caption sits 3 points under its control.", more: "The longer explanation.")
                }
                ThemedCard("Checkboxes") {
                    CheckboxList {
                        Toggle("On", isOn: $on)
                        Toggle("Off", isOn: $off)
                        Toggle(isOn: $on) {
                            VStack(alignment: .leading, spacing: 2) {
                                Text("With a caption")
                                InspectorCaption("Captions under a checkbox line up with its label.")
                            }
                        }
                    }
                }
                ThemedCard("Choices") {
                    ChoiceSegments("Segments", selection: $pick, cases: [0, 1, 2], label: { ["Automatic", "Music", "Spotify"][$0] })
                    ChoiceMenu("Pop-up", selection: $menu, selectedTitle: { ["Say so briefly", "Stay quiet"][$0] }) {
                        Text("Say so briefly").tag(0)
                        Text("Stay quiet").tag(1)
                    }
                    ValueSlider("Lyric size", value: $level, in: 0...1, minimumLabel: "Small", maximumLabel: "Large",
                                format: { "\(Int($0 * 100))%" })
                    HStack(spacing: 14) {
                        ForEach([ThemeState.pressed, .focused, .disabled], id: \.self) { state in
                            VStack(alignment: .leading, spacing: 2) {
                                Text(state.rawValue.capitalized).lunalyrText(.caption)
                                ThemedSliderTrack(value: $level, range: 0...1)
                                    .environment(\.themeForcedState, state)
                            }
                        }
                    }
                }
                StatusBanner(.warning, title: "Default folder unavailable",
                             message: "Export lyrics in Lyrimuse first, or choose the folder where you already save them.")
            }
            .toggleStyle(ThemedToggleStyle())
            .buttonStyle(ThemedButtonStyle())
            .padding(.horizontal, 30)
            .padding(.vertical, 24)
            .frame(width: 540, alignment: .topLeading)
            .background(Color(nsColor: t.canvas))
        }
        .environment(\.themeStyleOverride, style)
    }

    private func stateGrid<Item: View>(@ViewBuilder _ item: @escaping (ThemeState) -> Item) -> some View {
        HStack(spacing: 10) {
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

/// A settings page or inspector: plain sections in a scrolling column on the
/// window fill, with the form's switches, buttons and rows applied to every
/// control inside.
///
/// `.page` density (the default): a 600 pt column 40 from the sidebar,
/// sections 28 apart. The window draws the page's title in its 56 pt band,
/// so pages pass no `title` (it stays for a form shown outside the window).
/// `.inspector` density: no title unless given, the inspector's 20 / 16
/// padding, sections 22 apart.
struct ThemedForm<Content: View>: View {
    let title: String?
    let content: Content

    init(title: String? = nil, @ViewBuilder content: () -> Content) {
        self.title = title
        self.content = content()
    }

    @Environment(\.formScrollMemory) private var memory
    @Environment(\.formScrollKey) private var key
    @Environment(\.formScrollRestores) private var restores
    @Environment(\.formDensity) private var density

    var body: some View {
        ScrollView {
            column
                .frame(maxWidth: .infinity, alignment: .leading)
                .background {
                    if let memory, let key {
                        FormScrollAnchor(memory: memory, key: key, restores: restores)
                            .accessibilityHidden(true)
                    }
                }
        }
        .scrollContentBackground(.hidden)
        .themedFormControls()
    }

    @ViewBuilder private var column: some View {
        switch density {
        case .page:
            VStack(alignment: .leading, spacing: 0) {
                if let title { ThemedPageTitle(title) }
                VStack(alignment: .leading, spacing: density.sectionSpacing) {
                    content
                }
                .frame(maxWidth: ThemedPageMetrics.column, alignment: .leading)
            }
            .padding(.leading, ThemedPageMetrics.inset)
            .padding(.trailing, ThemedPageMetrics.inset)
            .padding(.top, 8)
            .padding(.bottom, 32)
        case .inspector:
            VStack(alignment: .leading, spacing: density.sectionSpacing) {
                if let title { ThemedPageTitle(title, band: false) }
                content
            }
            .padding(LunalyrDesign.Studio.Layout.inspectorPadding)
        }
    }
}

/// The measurements of a page without a picture (SPEC §8).
enum ThemedPageMetrics {
    /// The one column's width.
    static let column: CGFloat = 600
    /// From the sidebar to the column.
    static let inset: CGFloat = 40
    /// The title's band.
    static let titleBand: CGFloat = 56
}

/// A page's or object's title: 17 pt semibold ink, in its 56 pt band.
struct ThemedPageTitle: View {
    let text: String
    var band = true

    init(_ text: String, band: Bool = true) {
        self.text = text
        self.band = band
    }

    var body: some View {
        Text(text)
            .font(LunalyrDesign.Studio.TypeScale.title)
            .foregroundStyle(Color.studio(.ink))
            .accessibilityAddTraits(.isHeader)
            .frame(height: band ? ThemedPageMetrics.titleBand : nil, alignment: .center)
    }
}

extension View {
    /// The form's control language for everything inside: switches, buttons,
    /// label-and-control rows and short captions.
    func themedFormControls() -> some View {
        toggleStyle(ThemedToggleStyle())
            .buttonStyle(ThemedButtonStyle())
            .labeledContentStyle(ThemedLabeledContentStyle())
            .textFieldStyle(ThemedTextFieldStyle())
            .environment(\.compactCaptions, true)
            .font(LunalyrDesign.Studio.TypeScale.row)
            .foregroundStyle(Color.studio(.ink))
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

/// Label on the left (13 pt ink), value or control on the right; a plain
/// value reads in ink2.
struct ThemedLabeledContentStyle: LabeledContentStyle {
    func makeBody(configuration: Configuration) -> some View {
        HStack(alignment: .center, spacing: 10) {
            configuration.label
                .font(LunalyrDesign.Studio.TypeScale.row)
                .foregroundStyle(Color.studio(.ink))
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 8)
            configuration.content
                .font(LunalyrDesign.Studio.TypeScale.row)
                .foregroundStyle(Color.studio(.ink2))
                .multilineTextAlignment(.trailing)
        }
        .frame(minHeight: 24)
    }
}

/// A text field on the control fill (white on the canvas grey), 26 high,
/// radius 6, 13 pt.
struct ThemedTextFieldStyle: TextFieldStyle {
    func _body(configuration: TextField<Self._Label>) -> some View {
        ThemedTextFieldBody(field: configuration)
    }
}

private struct ThemedTextFieldBody<Field: View>: View {
    let field: Field
    @Environment(\.themeOnCanvas) private var onCanvas
    @FocusState private var focused: Bool

    var body: some View {
        ThemeReader { style, t in
            let shape = RoundedRectangle(cornerRadius: 6, style: .continuous)
            field
                .textFieldStyle(.plain)
                .font(LunalyrDesign.Studio.TypeScale.row)
                .foregroundStyle(Color.studio(.ink))
                .focused($focused)
                .padding(.horizontal, 8)
                .frame(maxWidth: .infinity, minHeight: 26, alignment: .leading)
                .background(shape.fill(onCanvas ? Color.studio(.window) : Color.studio(.control)))
                .overlay {
                    if style == .bold {
                        shape.strokeBorder(Color(nsColor: t.controlEdge), lineWidth: t.outline)
                    } else {
                        shape.strokeBorder(Color.studioContrastEdge, lineWidth: 1)
                    }
                }
                .overlay {
                    if focused { shape.inset(by: -3).strokeBorder(Color.studio(.accent), lineWidth: 2) }
                }
        }
    }
}

/// A row (or, on a `Group`, each row) that appears and disappears with the
/// form's `reveal` transition: a fade with a 4 pt settle, a plain fade under
/// Reduce Motion. Pair it with `lunalyrReveal(_:)` on the page, keyed on the
/// flag that shows it, so the rows below slide rather than jump.
private struct RevealRowModifier: ViewModifier {
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    func body(content: Content) -> some View {
        content.transition(LunalyrDesign.Motion.reveal(reduceMotion: reduceMotion))
    }
}

extension View {
    func revealRow() -> some View {
        modifier(RevealRowModifier())
    }
}

/// A titled section inside a `ThemedForm`: 13 pt semibold title, then its
/// rows 8 (inspector 9) apart. No card, border or divider.
struct ThemedSection<Content: View>: View {
    let title: String
    var info: String?
    var value: String?
    let content: Content

    init(_ title: String, info: String? = nil, value: String? = nil, @ViewBuilder content: () -> Content) {
        self.title = title
        self.info = info
        self.value = value
        self.content = content()
    }

    @Environment(\.formDensity) private var density

    var body: some View {
        VStack(alignment: .leading, spacing: density.rowSpacing) {
            ThemedSectionHeader(title, info: info, value: value)
            content
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        // The form's controls even outside a `ThemedForm` (the Look inspector).
        .themedFormControls()
    }
}

/// A page split by scope (Timing & sources): the leading column on the
/// canvas grey (what applies now, `themeOnCanvas`), the trailing column on
/// the window fill, side by side; stacked in a window too narrow for both.
/// Scrolls as one, and remembers where it was left like `ThemedForm`.
struct ThemedSplitForm<Leading: View, Trailing: View>: View {
    let leading: Leading
    let trailing: Trailing

    /// The leading column's width, and the trailing one's widest.
    static var leadingWidth: CGFloat { 360 }
    static var trailingWidth: CGFloat { 480 }

    init(@ViewBuilder leading: () -> Leading, @ViewBuilder trailing: () -> Trailing) {
        self.leading = leading()
        self.trailing = trailing()
    }

    @Environment(\.formScrollMemory) private var memory
    @Environment(\.formScrollKey) private var key
    @Environment(\.formScrollRestores) private var restores

    var body: some View {
        GeometryReader { proxy in
            ScrollView {
                let split = proxy.size.width >= Self.leadingWidth + ThemedPageMetrics.inset + 300
                let layout = split ? AnyLayout(HStackLayout(alignment: .top, spacing: 0))
                    : AnyLayout(VStackLayout(alignment: .leading, spacing: 0))
                layout {
                    leadingColumn
                        .frame(width: split ? Self.leadingWidth : nil, alignment: .topLeading)
                        .frame(maxWidth: split ? nil : .infinity, alignment: .topLeading)
                        .background(split ? Color.clear : Color.studio(.canvas))
                    trailingColumn
                        .frame(maxWidth: Self.trailingWidth, alignment: .topLeading)
                        .padding(.leading, ThemedPageMetrics.inset)
                        .padding(.trailing, split ? 24 : ThemedPageMetrics.inset)
                        .frame(maxWidth: .infinity, alignment: .topLeading)
                }
                .frame(minHeight: proxy.size.height, alignment: .top)
                // The canvas runs the window's full height beside the page.
                .background(alignment: .leading) {
                    if split { Color.studio(.canvas).frame(width: Self.leadingWidth) }
                }
                .background {
                    if let memory, let key {
                        FormScrollAnchor(memory: memory, key: key, restores: restores)
                            .accessibilityHidden(true)
                    }
                }
            }
            .scrollContentBackground(.hidden)
        }
        .themedFormControls()
    }

    private var leadingColumn: some View {
        VStack(alignment: .leading, spacing: 24) { leading }
            .padding(.horizontal, 24)
            .padding(.top, 8)
            .padding(.bottom, 24)
            .environment(\.themeOnCanvas, true)
            .accessibilityElement(children: .contain)
    }

    private var trailingColumn: some View {
        VStack(alignment: .leading, spacing: 26) { trailing }
            .padding(.top, 8)
            .padding(.bottom, 32)
            .accessibilityElement(children: .contain)
    }
}
