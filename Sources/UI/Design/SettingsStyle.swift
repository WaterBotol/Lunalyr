import AppKit
import SwiftUI

// MARK: - Window style: Standard and Bold
//
// Standard (stored as "calm", the value earlier versions wrote) is the
// redesign itself: `LunalyrDesign.Studio`'s fills, no edges and no shadows,
// edges only under Increase Contrast. Bold keeps its toned-down
// neubrutalism over the same structure: a 1.5pt ink outline on every
// control and a hard offset shadow with no blur. Both are static and
// opaque, so Reduce Transparency has nothing to take away.
//
//     SettingsStyleStore.shared.style = .bold
//     Button("Hi") { }.styledSurface(radius: 14, selected: false)

enum SettingsVisualStyle: String, CaseIterable, Identifiable {
    case calm, bold

    var id: String { rawValue }

    var title: String {
        switch self {
        case .calm: return "Standard"
        case .bold: return "Bold"
        }
    }

    var summary: String {
        switch self {
        case .calm: return "Flat fills that follow the system appearance."
        case .bold: return "Thick outlines and hard shadows on every control."
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
        case (.calm, _):
            // Standard is the Studio tokens (SPEC.md §2): the window fill,
            // the control fill, ink and ink2; edges only under Increase Contrast.
            func studio(_ role: LunalyrDesign.Studio.Role) -> NSColor {
                LunalyrDesign.Studio.color(role, dark: dark, increaseContrast: increaseContrast)
            }
            let edge = studio(.ink).withAlphaComponent(0.25)
            return Tokens(canvas: studio(.window), face: studio(.control), ink: studio(.ink), secondary: studio(.ink2),
                          controlEdge: edge, cardEdge: increaseContrast ? edge : .clear,
                          shadow: nil, outline: 1, shadowOffset: 0, cardRadius: 8, controlRadius: 6)
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
    func styledSurface(radius: CGFloat = LunalyrDesign.Radius.card, selected: Bool = false,
                       recessed: Bool = false) -> some View {
        modifier(ThemedSurface(radius: radius, selected: selected, state: nil, isCard: true, recessed: recessed))
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

/// The Window style row on General: Standard | Bold, and what Bold does.
/// (Earlier builds showed both as miniatures; the setting is unchanged.)
struct SettingsStylePicker: View {
    @ObservedObject private var store = SettingsStyleStore.shared

    var body: some View {
        VStack(alignment: .leading, spacing: LunalyrDesign.Studio.Layout.captionGap) {
            ChoiceSegments("Window style", selection: Binding(get: { store.style }, set: { store.choose($0) }),
                           cases: SettingsVisualStyle.allCases, label: \.title)
            InspectorCaption(Self.caption)
        }
    }

    static let caption = "Bold adds thick outlines and hard shadows to Lunalyr\u{2019}s own windows. It doesn\u{2019}t change your wallpaper."
}
