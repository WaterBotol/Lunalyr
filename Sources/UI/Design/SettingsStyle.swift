import AppKit
import SwiftUI

// MARK: - Two looks for Lunalyr's own windows: Calm and Bold
//
// Calm is flat and opaque: quiet surfaces, hairline edges, no shadows.
// Bold is a toned-down neubrutalism: flat fills, a 1.5pt ink outline on
// everything and a hard offset shadow with no blur. Both are static and
// opaque, so Reduce Transparency has nothing to take away, and selected
// controls are always the violet accent. The values are specified, with
// their contrast ratios, in design/spec.md.
//
//     SettingsStyleStore.shared.style = .bold
//     Button("Hi") { }.styledSurface(radius: 14, selected: false)

enum SettingsVisualStyle: String, CaseIterable, Identifiable {
    case calm, bold

    var id: String { rawValue }

    var title: String {
        switch self {
        case .calm: return "Calm"
        case .bold: return "Bold"
        }
    }

    var summary: String {
        switch self {
        case .calm: return "Flat and quiet, with thin edges."
        case .bold: return "Thick outlines and hard shadows."
        }
    }

    /// Earlier builds stored "soft" and "clay".
    init?(stored: String) {
        switch stored {
        case "soft": self = .calm
        case "clay": self = .bold
        default: self.init(rawValue: stored)
        }
    }

    /// The window's own colour.
    func canvas(dark: Bool) -> NSColor { tokens(dark: dark).canvas }

    /// The face of a card or control.
    func face(dark: Bool) -> NSColor { tokens(dark: dark).face }

    var cornerScale: CGFloat { self == .bold ? 0.7 : 1 }

    /// Every colour and measurement of the look, for one appearance.
    struct Tokens {
        let canvas: NSColor
        let face: NSColor
        let ink: NSColor
        let secondary: NSColor
        let controlEdge: NSColor
        let cardEdge: NSColor
        let shadow: NSColor?
        let outline: CGFloat
        let shadowOffset: CGFloat
        let cardRadius: CGFloat
        let controlRadius: CGFloat
    }

    func tokens(dark: Bool, increaseContrast: Bool = false) -> Tokens {
        func c(_ hex: UInt32) -> NSColor {
            LunalyrPalette.rgb(Int((hex >> 16) & 0xFF), Int((hex >> 8) & 0xFF), Int(hex & 0xFF))
        }
        switch (self, dark) {
        case (.calm, false):
            return Tokens(canvas: c(0xF2F2F4), face: c(0xFFFFFF), ink: c(0x1C1C1E), secondary: c(0x5A5A60),
                          controlEdge: c(0x8A8A92), cardEdge: increaseContrast ? c(0x8A8A92) : c(0xD6D6DB),
                          shadow: nil, outline: 1, shadowOffset: 0, cardRadius: 12, controlRadius: 8)
        case (.calm, true):
            return Tokens(canvas: c(0x18181B), face: c(0x242428), ink: c(0xF2F2F5), secondary: c(0xA6A6AE),
                          controlEdge: c(0x8E8E96), cardEdge: increaseContrast ? c(0x8E8E96) : c(0x34343A),
                          shadow: nil, outline: 1, shadowOffset: 0, cardRadius: 12, controlRadius: 8)
        case (.bold, false):
            return Tokens(canvas: c(0xF5EFE0), face: c(0xFFFFFF), ink: c(0x111111), secondary: c(0x4A4A4F),
                          controlEdge: c(0x111111), cardEdge: c(0x111111),
                          shadow: c(0x111111), outline: 1.5, shadowOffset: 3, cardRadius: 8, controlRadius: 6)
        case (.bold, true):
            return Tokens(canvas: c(0x111113), face: c(0x1E1E22), ink: c(0xF4F4F6), secondary: c(0xB4B4BC),
                          controlEdge: c(0xEDEDF0), cardEdge: c(0xEDEDF0),
                          shadow: c(0xA89BFF), outline: 1.5, shadowOffset: 3, cardRadius: 8, controlRadius: 6)
        }
    }
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
        // LUNALYR_SETTINGS_STYLE=bold|calm forces a look for snapshots and demos.
        let env = ProcessInfo.processInfo.environment
        let forced = env["LUNALYR_SETTINGS_STYLE"] ?? env["LUNALYR_UI_SNAPSHOT_SETTINGS_STYLE"]
        style = (forced ?? defaults.string(forKey: SettingsVisualStyle.key)).flatMap(SettingsVisualStyle.init(stored:)) ?? .calm
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

extension View {
    /// Raises this view as a control or card in the chosen look. Selected
    /// surfaces are the violet accent in both looks.
    func styledSurface(radius: CGFloat = LunalyrDesign.Radius.card, selected: Bool = false) -> some View {
        modifier(ThemedSurface(radius: radius, selected: selected, state: nil, isCard: true))
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

/// Calm and Bold side by side, each a live miniature of itself, to choose
/// from in the welcome and in Settings › General.
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
                        ThemeMiniPreview(style: option)
                        Text(option.title).font(.body.weight(.semibold))
                        Text(option.summary)
                            .lunalyrText(.caption)
                            .multilineTextAlignment(.center)
                    }
                    .padding(LunalyrDesign.Spacing.medium)
                    .frame(maxWidth: .infinity)
                    .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous)
                        .strokeBorder(selected ? Color.lunalyrAccentText : Color.primary.opacity(0.14), lineWidth: selected ? 2.5 : 1))
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
