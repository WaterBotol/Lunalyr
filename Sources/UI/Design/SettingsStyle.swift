import AppKit
import SwiftUI

// MARK: - Two looks for Lunalyr's own windows: Soft and Clay
//
// Soft is neumorphism: one calm surface colour, with controls that seem
// pressed out of it by a light and a dark shadow. Clay is claymorphism:
// pastel, puffy shapes with a coloured drop shadow and a bright rim.
// Both keep text, symbols and selected controls at full contrast (a plain
// neumorphic edge is only about 1.4:1, so selection is always the violet
// accent and every control keeps a faint outline), and both drop their
// shadows under Increase Contrast.
//
//     SettingsStyleStore.shared.style = .clay
//     Button("Hi") { }.styledSurface(radius: 14, selected: false)

enum SettingsVisualStyle: String, CaseIterable, Identifiable {
    case soft, clay

    var id: String { rawValue }

    var title: String {
        switch self {
        case .soft: return "Soft"
        case .clay: return "Clay"
        }
    }

    var summary: String {
        switch self {
        case .soft: return "Quiet, one-tone surfaces with gently raised controls."
        case .clay: return "Playful pastel shapes that look moulded from clay."
        }
    }

    /// The window's own colour, which controls rise from.
    func canvas(dark: Bool) -> NSColor {
        switch (self, dark) {
        case (.soft, false): return LunalyrPalette.rgb(0xE3, 0xE8, 0xF0)
        case (.soft, true): return LunalyrPalette.rgb(0x2B, 0x2E, 0x37)
        case (.clay, false): return LunalyrPalette.rgb(0xF4, 0xEE, 0xFF)
        case (.clay, true): return LunalyrPalette.rgb(0x25, 0x20, 0x35)
        }
    }

    /// The face of a raised control.
    func face(dark: Bool) -> NSColor {
        switch (self, dark) {
        case (.soft, false): return LunalyrPalette.rgb(0xE9, 0xEE, 0xF5)
        case (.soft, true): return LunalyrPalette.rgb(0x30, 0x34, 0x3E)
        case (.clay, false): return LunalyrPalette.rgb(0xFF, 0xFF, 0xFF)
        case (.clay, true): return LunalyrPalette.rgb(0x36, 0x2F, 0x4D)
        }
    }

    var cornerScale: CGFloat { self == .clay ? 1.5 : 1 }
}

extension SettingsVisualStyle {
    static let key = "custom.settingsStyle"
    static let chosenKey = "custom.settingsStyleChosen"
}

/// The chosen look, shared by Settings, the menu-bar panel and onboarding.
@MainActor
final class SettingsStyleStore: ObservableObject {
    static let shared = SettingsStyleStore()

    private let defaults: UserDefaults

    @Published var style: SettingsVisualStyle {
        didSet { defaults.set(style.rawValue, forKey: SettingsVisualStyle.key) }
    }

    /// False until the person picks one (in the welcome, or in Settings).
    @Published private(set) var hasChosen: Bool

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        // LUNALYR_SETTINGS_STYLE=clay|soft forces a look for snapshots and demos.
        let env = ProcessInfo.processInfo.environment
        let forced = env["LUNALYR_SETTINGS_STYLE"] ?? env["LUNALYR_UI_SNAPSHOT_SETTINGS_STYLE"]
        style = SettingsVisualStyle(rawValue: forced ?? defaults.string(forKey: SettingsVisualStyle.key) ?? "") ?? .soft
        hasChosen = defaults.bool(forKey: SettingsVisualStyle.chosenKey)
    }

    func choose(_ style: SettingsVisualStyle) {
        self.style = style
        hasChosen = true
        defaults.set(true, forKey: SettingsVisualStyle.chosenKey)
    }
}

extension Color {
    static func lunalyrCanvas(_ style: SettingsVisualStyle) -> Color {
        Color(nsColor: NSColor(name: "LunalyrCanvas.\(style.rawValue)") { appearance in
            style.canvas(dark: LunalyrAppearance(appearance).dark)
        })
    }

    static func lunalyrFace(_ style: SettingsVisualStyle) -> Color {
        Color(nsColor: NSColor(name: "LunalyrFace.\(style.rawValue)") { appearance in
            style.face(dark: LunalyrAppearance(appearance).dark)
        })
    }
}

/// A raised surface in the chosen look. Selected surfaces are the violet
/// accent in both looks, so selection never depends on a faint shadow.
private struct StyledSurface: ViewModifier {
    let radius: CGFloat
    let selected: Bool
    @ObservedObject private var store = SettingsStyleStore.shared
    @Environment(\.colorScheme) private var scheme
    @Environment(\.colorSchemeContrast) private var contrast

    init(radius: CGFloat, selected: Bool) {
        self.radius = radius
        self.selected = selected
    }

    func body(content: Content) -> some View {
        let style = store.style
        let r = radius * style.cornerScale
        let dark = scheme == .dark
        let plain = contrast == .increased
        let shape = RoundedRectangle(cornerRadius: r, style: .continuous)
        content
            .background(
                shape.fill(selected ? AnyShapeStyle(Color.lunalyrAccent.gradient) : AnyShapeStyle(Color.lunalyrFace(style)))
            )
            .overlay(
                shape.strokeBorder(selected ? Color.white.opacity(0.25) : Color.primary.opacity(plain ? 0.55 : 0.08), lineWidth: 1)
            )
            .overlay(alignment: .top) {
                // Clay's bright rim along the top edge.
                if style == .clay && !plain {
                    shape.strokeBorder(LinearGradient(colors: [.white.opacity(dark ? 0.28 : 0.9), .clear],
                                                      startPoint: .top, endPoint: .center), lineWidth: 2)
                }
            }
            .shadow(color: shadow(style, dark: dark, light: false, plain: plain), radius: style == .soft ? 7 : 12,
                    x: style == .soft ? -4 : 0, y: style == .soft ? -4 : 0)
            .shadow(color: shadow(style, dark: dark, light: true, plain: plain), radius: style == .soft ? 7 : 12,
                    x: style == .soft ? 4 : 0, y: style == .soft ? 4 : 8)
    }

    /// `light` is the second shadow: Soft's dark one, Clay's coloured drop.
    private func shadow(_ style: SettingsVisualStyle, dark: Bool, light: Bool, plain: Bool) -> Color {
        guard !plain else { return .clear }
        switch (style, light) {
        case (.soft, false): return dark ? Color.white.opacity(0.06) : Color.white.opacity(0.9)
        case (.soft, true): return dark ? Color.black.opacity(0.45) : Color(red: 0.64, green: 0.69, blue: 0.78).opacity(0.65)
        case (.clay, false): return .clear
        case (.clay, true): return Color.lunalyrAccent.opacity(dark ? 0.35 : 0.25)
        }
    }
}

extension View {
    /// Raises this view as a control or card in the chosen look.
    func styledSurface(radius: CGFloat = LunalyrDesign.Radius.card, selected: Bool = false) -> some View {
        modifier(StyledSurface(radius: radius, selected: selected))
    }

    /// Paints the window or panel with the chosen look's canvas.
    func styledCanvas() -> some View {
        modifier(StyledCanvas())
    }
}

private struct StyledCanvas: ViewModifier {
    @ObservedObject private var store = SettingsStyleStore.shared

    func body(content: Content) -> some View {
        content
            .scrollContentBackground(.hidden)
            .background(Color.lunalyrCanvas(store.style))
    }
}

/// Soft and Clay side by side, each a small sample, to choose from in the
/// welcome and in Settings › General.
struct SettingsStylePicker: View {
    @ObservedObject private var store = SettingsStyleStore.shared

    var body: some View {
        HStack(spacing: LunalyrDesign.Spacing.large) {
            ForEach(SettingsVisualStyle.allCases) { option in
                let selected = store.hasChosen && store.style == option
                Button {
                    store.choose(option)
                } label: {
                    VStack(spacing: LunalyrDesign.Spacing.small) {
                        HStack(spacing: 8) {
                            ForEach(0..<3, id: \.self) { index in
                                Circle()
                                    .fill(index == 0 ? AnyShapeStyle(Color.lunalyrAccent.gradient)
                                          : AnyShapeStyle(Color.lunalyrFace(option)))
                                    .frame(width: 26, height: 26)
                                    .overlay(Circle().strokeBorder(Color.primary.opacity(0.1), lineWidth: 1))
                                    .shadow(color: option == .clay ? Color.lunalyrAccent.opacity(0.3) : .black.opacity(0.18),
                                            radius: option == .clay ? 6 : 4, x: option == .soft ? 2 : 0, y: 3)
                            }
                        }
                        Text(option.title).font(.body.weight(.semibold))
                        Text(option.summary)
                            .lunalyrText(.caption)
                            .multilineTextAlignment(.center)
                    }
                    .padding(LunalyrDesign.Spacing.large)
                    .frame(maxWidth: .infinity)
                    .background(RoundedRectangle(cornerRadius: 18 * option.cornerScale, style: .continuous)
                        .fill(Color.lunalyrCanvas(option)))
                    .overlay(RoundedRectangle(cornerRadius: 18 * option.cornerScale, style: .continuous)
                        .strokeBorder(selected ? Color.lunalyrAccentText : Color.primary.opacity(0.12), lineWidth: selected ? 2 : 1))
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityLabel("\(option.title) look")
                .accessibilityHint(option.summary)
                .accessibilityAddTraits(selected ? .isSelected : [])
            }
        }
    }
}
