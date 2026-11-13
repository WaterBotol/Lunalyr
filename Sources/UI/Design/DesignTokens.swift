import AppKit
import SwiftUI

// MARK: - Lunalyr's design tokens
//
// One visual language for the Settings window, the menu-bar panel and
// onboarding: calm system chrome, with colour kept for the wallpaper preview,
// the violet accent and status. Every surface uses these instead of numbers of
// its own (the spec, gui/spec.md, says where each applies):
//
//     VStack(alignment: .leading, spacing: LunalyrDesign.Spacing.small) {
//         Text("Wallpaper").lunalyrText(.groupTitle)
//         Text(detail).lunalyrText(.caption)
//     }
//     .padding(LunalyrDesign.Spacing.panel)
//     .background(RoundedRectangle(cornerRadius: LunalyrDesign.Radius.card, style: .continuous)
//         .fill(.quaternary))
//     .lunalyrTint()                                // at the root of each surface
//     .foregroundStyle(LunalyrStatus.warning.text) // status text, contrast-safe
//
// Colours are dynamic: they follow light and dark mode and Increase Contrast
// (the system setting, or a high-contrast appearance in snapshots). Use system
// colours and materials for everything else: `.primary`/`.secondary` text,
// `.regularMaterial`, `Color(nsColor: .windowBackgroundColor)`. They already
// adapt to Increase Contrast and Reduce Transparency.

enum LunalyrDesign {
    /// The 4-point grid. Between related rows `small`, between groups
    /// `large`, around a panel's content `panel`.
    enum Spacing {
        static let hairline: CGFloat = 2
        static let xSmall: CGFloat = 4
        static let small: CGFloat = 8
        static let medium: CGFloat = 12
        static let large: CGFloat = 16
        static let xLarge: CGFloat = 20
        static let xxLarge: CGFloat = 24
        static let section: CGFloat = 32
        /// Inside the menu-bar panel and other floating panels.
        static let panel: CGFloat = 18
        /// Around a window's content (Settings stage, onboarding).
        static let window: CGFloat = 20
    }

    /// Corner radii, always with the continuous (squircle) style.
    enum Radius {
        /// Key caps, small tags.
        static let tag: CGFloat = 5
        /// Settings sidebar icon badges at 20 points.
        static let badge: CGFloat = 6
        /// Album artwork up to 64 points.
        static let artwork: CGFloat = 8
        /// Search fields, capsules, notes; small pictures of a surface.
        static let control: CGFloat = 10
        /// Banners, cards, tiles.
        static let card: CGFloat = 14
        /// The wallpaper preview: a desktop in miniature.
        static let preview: CGFloat = 18
        /// The menu-bar panel's own corners (drawn by the system).
        static let panel: CGFloat = 12
    }

    /// Fixed sizes. Text never gets a fixed height: it wraps.
    enum Size {
        static let sidebarIcon: CGFloat = 20
        /// Artwork in the menu-bar panel and onboarding.
        static let artworkLarge: CGFloat = 56
        /// Artwork beside the Settings preview.
        static let artworkSmall: CGFloat = 40
        /// The smallest a clickable target gets (HIG: 28 x 28 on macOS for
        /// icon buttons; text buttons are taller than their text anyway).
        static let minimumHitTarget: CGFloat = 28
        /// Quick-control tiles in the menu-bar panel.
        static let tileHeight: CGFloat = 68
    }

    /// Window and panel geometry the three surfaces share.
    enum Layout {
        /// The menu-bar panel's content width.
        static let menuPanelWidth: CGFloat = 364
        /// The Settings inspector column beside the preview stage.
        static let inspectorWidth: CGFloat = 440
        /// Narrower than this, the Settings detail stacks the preview above
        /// the inspector instead of beside it.
        static let stageBesideInspectorMinimum: CGFloat = 760
        /// The largest Settings window the 13-inch Air plan allows.
        static let settingsMaximum = CGSize(width: 1200, height: 780)
        /// Onboarding's fixed window content size.
        static let onboarding = CGSize(width: 640, height: 540)
    }

    /// Onboarding's page geometry and its pictures' sizes (`Sources/UI/Onboarding`).
    enum Onboarding {
        /// From the window's sides to a step's content.
        static let sideInset: CGFloat = 48
        /// From the window's top (under the see-through title bar) to a
        /// step's title, the same on every step.
        static let titleTop: CGFloat = 40
        /// The text column under a title.
        static let messageWidth: CGFloat = 480
        /// The welcome's desktop in miniature.
        static let heroWidth: CGFloat = 392
        /// The look step's live preview, and the choices under it.
        static let previewWidth: CGFloat = 280
        static let choicesWidth: CGFloat = 408
        /// The choices' reserved height, so switching Wallpaper and Lyrics
        /// doesn't move the window's content.
        static let choicesHeight: CGFloat = 148
        /// A lyric layout's thumbnail (16:9-ish) and the stage it is set on
        /// before being scaled down to it.
        static let layoutThumb = CGSize(width: 96, height: 56)
        static let layoutStage = CGSize(width: 360, height: 210)
        /// The music-video step's two framing pictures.
        static let framingThumb = CGSize(width: 112, height: 76)
        /// The extras' pictures, beside their rows.
        static let extraPicture = CGSize(width: 196, height: 52)
        /// The Discord status recording: 254 x 44 pixels, shown at 1.5 times
        /// its 2x point size so its 11-point name and line read as 16.
        static let discordStatus = CGSize(width: 190.5, height: 33)
        /// Discord's dark member list, behind the recording.
        static let discordBacking = Color(.sRGB, red: 0.169, green: 0.176, blue: 0.192, opacity: 1)
    }

    /// Animation, always through `LunalyrDesign.Motion.animation(_:reduceMotion:)`
    /// so Reduce Motion turns movement into a short fade or nothing.
    enum Motion {
        /// Page changes, panels appearing.
        static let standard = Animation.smooth(duration: 0.28)
        /// A control answering a click: a tile lighting up.
        static let quick = Animation.smooth(duration: 0.16)
        /// Onboarding steps sliding in.
        static let step = Animation.spring(response: 0.42, dampingFraction: 0.9)
        /// What replaces movement under Reduce Motion: a brief cross-fade.
        static let reduced = Animation.easeInOut(duration: 0.18)

        static func animation(_ animation: Animation, reduceMotion: Bool) -> Animation {
            reduceMotion ? reduced : animation
        }

        /// A transition that moves, or only fades under Reduce Motion.
        static func transition(_ edge: Edge, reduceMotion: Bool) -> AnyTransition {
            reduceMotion ? .opacity : .asymmetric(insertion: .move(edge: edge).combined(with: .opacity),
                                                  removal: .move(edge: edge.opposite).combined(with: .opacity))
        }
    }
}

private extension Edge {
    var opposite: Edge {
        switch self {
        case .leading: return .trailing
        case .trailing: return .leading
        case .top: return .bottom
        case .bottom: return .top
        }
    }
}

// MARK: - Type

/// The type scale: semantic macOS text styles only, so text follows the
/// system's sizes (and its text-size setting where macOS offers one).
enum LunalyrTextStyle {
    /// A window or onboarding step's title. 22-point semibold.
    case largeTitle
    /// A page's title beside its controls, a step's heading. 17-point semibold.
    case title
    /// A group inside a panel ("Wallpaper" in the menu). 11-point semibold,
    /// secondary; a header to VoiceOver when used with `PanelSectionHeader`.
    case groupTitle
    /// The line being sung, quoted in the menu-bar panel. 15-point semibold.
    case lyric
    /// Ordinary text and control labels. 13-point.
    case body
    /// A track's title. 13-point semibold.
    case emphasis
    /// Explanations under a control: what it does, why it is off. 11-point,
    /// secondary. Long explanations stay at this size, never smaller.
    case caption
    /// Counts, sources, technical detail. 10-point, secondary.
    case footnote
    /// Numbers that change (offsets, percentages): monospaced digits.
    case value

    var font: Font {
        switch self {
        case .largeTitle: return .title.weight(.semibold)
        case .title: return .title2.weight(.semibold)
        case .groupTitle: return .subheadline.weight(.semibold)
        case .lyric: return .title3.weight(.semibold)
        case .body: return .body
        case .emphasis: return .body.weight(.semibold)
        case .caption: return .subheadline
        case .footnote: return .footnote
        case .value: return .body.monospacedDigit()
        }
    }

    var isSecondary: Bool {
        switch self {
        case .groupTitle, .caption, .footnote: return true
        default: return false
        }
    }
}

extension View {
    /// Sets text in one of Lunalyr's styles: font, and the secondary colour
    /// where the style has one. Multi-line styles wrap instead of truncating.
    func lunalyrText(_ style: LunalyrTextStyle) -> some View {
        modifier(LunalyrTextModifier(style: style))
    }
}

private struct LunalyrTextModifier: ViewModifier {
    let style: LunalyrTextStyle

    func body(content: Content) -> some View {
        let styled = content.font(style.font)
        switch style {
        case .caption, .footnote:
            styled.foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        case .groupTitle:
            styled.foregroundStyle(.secondary)
        default:
            styled
        }
    }
}

// MARK: - Accent

extension Color {
    /// Lunalyr's violet as a fill under white -- switches, slider tracks,
    /// selection, prominent buttons. Plain `.mint` gives white about 2:1.
    /// Apply it with `.tint(.lunalyrAccent)` at the root of every surface.
    static let lunalyrAccent = Color(nsColor: NSColor(name: "LunalyrAccent") { appearance in
        let look = LunalyrAppearance(appearance)
        return LunalyrAccent.fill(dark: look.dark, increaseContrast: look.increaseContrast)
    })

    /// Lunalyr's violet for text and symbols drawn on the window itself.
    static let lunalyrAccentText = Color(nsColor: NSColor(name: "LunalyrAccentText") { appearance in
        let look = LunalyrAppearance(appearance)
        return LunalyrAccent.text(dark: look.dark, increaseContrast: look.increaseContrast)
    })

    /// The outline tiles, cards and the preview gain under Increase
    /// Contrast; clear otherwise. Always draw it: it resolves itself.
    static let lunalyrContrastBorder = Color(nsColor: NSColor(name: "LunalyrContrastBorder") { appearance in
        let look = LunalyrAppearance(appearance)
        guard look.increaseContrast else { return .clear }
        return look.dark ? NSColor(white: 1, alpha: 0.55) : NSColor(white: 0, alpha: 0.55)
    })
}

extension View {
    /// Lunalyr's violet for every control of a surface: put it at the root of
    /// the Settings window, the menu-bar panel and onboarding. It is the
    /// text-safe mint, so popup values, bordered buttons and links stay
    /// readable in dark mode; a button that puts white text on the mint
    /// uses `.buttonStyle(.lunalyrProminent)` instead.
    func lunalyrTint() -> some View {
        tint(.lunalyrAccentText)
    }
}

/// The default action's button: white text on the fill mint, 4.5:1 or
/// better in every appearance. `Button("Continue") { … }.buttonStyle(.lunalyrProminent)`.
struct LunalyrProminentButtonStyle: PrimitiveButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        Button(configuration)
            .buttonStyle(.borderedProminent)
            .tint(.lunalyrAccent)
    }
}

extension PrimitiveButtonStyle where Self == LunalyrProminentButtonStyle {
    static var lunalyrProminent: LunalyrProminentButtonStyle { LunalyrProminentButtonStyle() }
}

/// What a dynamic colour needs to know about where it is drawn.
struct LunalyrAppearance {
    var dark: Bool
    /// Increase Contrast, from the system setting or a high-contrast
    /// appearance (which is how AppKit draws while the setting is on).
    var increaseContrast: Bool

    init(dark: Bool, increaseContrast: Bool) {
        self.dark = dark
        self.increaseContrast = increaseContrast
    }

    init(_ appearance: NSAppearance) {
        dark = appearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua
        increaseContrast = Self.simulatedIncreaseContrast
            ?? (Self.isHighContrast(appearance.name) || NSWorkspace.shared.accessibilityDisplayShouldIncreaseContrast)
    }

    /// The high-contrast appearances AppKit draws with while Increase
    /// Contrast is on (vibrant ones included) match plain aqua and darkAqua
    /// too, so they are told apart by name.
    static func isHighContrast(_ name: NSAppearance.Name) -> Bool {
        [NSAppearance.Name.accessibilityHighContrastAqua, .accessibilityHighContrastDarkAqua,
         .accessibilityHighContrastVibrantLight, .accessibilityHighContrastVibrantDark].contains(name)
    }

    /// Set only by the UI snapshot harness while it draws its Increase
    /// Contrast looks: SwiftUI resolves colours with the system's own
    /// contrast setting, which a test can't change. Nil everywhere else.
    nonisolated(unsafe) static var simulatedIncreaseContrast: Bool?
}

/// The accent's values, kept apart from the views so tests can hold them to
/// their contrast. No one mint serves both jobs in dark mode: a fill white
/// can sit on is too dark to read as text on the dark window.
enum LunalyrAccent {
    static func isDark(_ appearance: NSAppearance) -> Bool {
        appearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua
    }

    /// White on it: 5.4:1 in light mode, 7.7:1 with Increase Contrast, 4.5:1
    /// in dark mode. Against the window: 4.5:1 or better in light mode; in
    /// dark mode 3.7:1 on a #1E1E1E window and about 2.8:1 on a #323232 one,
    /// which no mint that keeps white at 4.5:1 can beat -- enough to show a
    /// control's fill, never enough for text. Dark mode keeps it under
    /// Increase Contrast: deeper would sink into the window, lighter would
    /// dim the white on it, and macOS outlines controls then.
    static func fill(dark: Bool, increaseContrast: Bool) -> NSColor {
        switch (dark, increaseContrast) {
        case (true, _): return LunalyrPalette.rgb(0x6A, 0x57, 0xE8)
        case (false, true): return LunalyrPalette.rgb(0x3F, 0x2F, 0xB5)
        case (false, false): return LunalyrPalette.rgb(0x5B, 0x48, 0xD9)
        }
    }

    /// At least 4.5:1 against the window and its grouped rows in every
    /// appearance; lighter in dark mode, and further from the background
    /// again under Increase Contrast.
    static func text(dark: Bool, increaseContrast: Bool) -> NSColor {
        switch (dark, increaseContrast) {
        case (true, true): return LunalyrPalette.rgb(0xCF, 0xC7, 0xFF)
        case (true, false): return LunalyrPalette.rgb(0xA8, 0x9B, 0xFF)
        case (false, true): return LunalyrPalette.rgb(0x3F, 0x2F, 0xB5)
        case (false, false): return LunalyrPalette.rgb(0x5B, 0x48, 0xD9)
        }
    }
}

// MARK: - Status

/// How much a status matters, which decides its symbol and colour. Colour is
/// never the only signal: banners and chips carry the symbol and say the
/// status in words to VoiceOver ("Warning: …").
enum LunalyrStatus: String, CaseIterable {
    /// Something is waiting, harmlessly: an invitation (macOS hasn't asked
    /// about Music yet), or a mode the user chose (wallpaper paused).
    case info
    /// Done, allowed, working.
    case success
    /// Lunalyr is holding back or can't do something, and the user can fix
    /// it: access refused, audio not arriving, power saving.
    case warning
    /// Something failed outright.
    case critical
    /// A plain state with no judgement: lyrics hidden for presenting.
    case neutral

    /// The SF Symbol shown with the status.
    var symbol: String {
        switch self {
        case .info: return "info.circle.fill"
        case .success: return "checkmark.circle.fill"
        case .warning: return "exclamationmark.triangle.fill"
        case .critical: return "xmark.octagon.fill"
        case .neutral: return "circle.fill"
        }
    }

    /// Said before the status's own words.
    var spokenName: String? {
        switch self {
        case .warning: return "Warning"
        case .critical: return "Problem"
        case .info, .success, .neutral: return nil
        }
    }

    /// For the symbol and any status-coloured text: at least 4.5:1 on the
    /// window, grouped rows and the status's own fill, in every appearance.
    var text: Color {
        Color(nsColor: NSColor(name: "LunalyrStatus.\(rawValue).text") { appearance in
            LunalyrPalette.text(for: self, LunalyrAppearance(appearance))
        })
    }

    /// The outline banners and chips gain under Increase Contrast; clear otherwise.
    var border: Color {
        Color(nsColor: NSColor(name: "LunalyrStatus.\(rawValue).border") { appearance in
            let look = LunalyrAppearance(appearance)
            return look.increaseContrast ? LunalyrPalette.text(for: self, look).withAlphaComponent(0.75) : .clear
        })
    }

    /// The banner or chip background: the status hue, faint.
    var fill: Color {
        Color(nsColor: NSColor(name: "LunalyrStatus.\(rawValue).fill") { appearance in
            LunalyrPalette.fill(for: self, LunalyrAppearance(appearance))
        })
    }
}

/// The values behind the dynamic colours, for tests to hold to contrast.
enum LunalyrPalette {
    static func text(for status: LunalyrStatus, _ look: LunalyrAppearance) -> NSColor {
        // Each keeps 4.5:1 on its own fill over the window and grouped rows
        // (DesignSystemTests), a little deeper than the plain accent text.
        switch (status, look.dark, look.increaseContrast) {
        case (.info, false, false): return rgb(0x4A, 0x39, 0xC4)
        case (.info, false, true): return rgb(0x36, 0x27, 0xA0)
        case (.info, true, false): return rgb(0xB0, 0xA5, 0xFF)
        case (.info, true, true): return rgb(0xD2, 0xCB, 0xFF)
        case (.success, false, false): return rgb(0x12, 0x70, 0x2F)
        case (.success, false, true): return rgb(0x0E, 0x5A, 0x25)
        case (.success, true, false): return rgb(0x5E, 0xD4, 0x7A)
        case (.success, true, true): return rgb(0x8B, 0xE8, 0xA0)
        case (.warning, false, false): return rgb(0x9C, 0x46, 0x00)
        case (.warning, false, true): return rgb(0x85, 0x3C, 0x00)
        case (.warning, true, false): return rgb(0xFF, 0xA9, 0x4D)
        case (.warning, true, true): return rgb(0xFF, 0xC2, 0x85)
        case (.critical, false, false): return rgb(0xB4, 0x23, 0x18)
        case (.critical, false, true): return rgb(0x9E, 0x1C, 0x12)
        case (.critical, true, false): return rgb(0xFF, 0x95, 0x8C)
        case (.critical, true, true): return rgb(0xFF, 0xA9, 0xA1)
        case (.neutral, false, false): return rgb(0x5C, 0x5C, 0x5C)
        case (.neutral, false, true): return rgb(0x44, 0x44, 0x44)
        case (.neutral, true, false): return rgb(0xBD, 0xBD, 0xBD)
        case (.neutral, true, true): return rgb(0xD6, 0xD6, 0xD6)
        }
    }

    /// Faint enough that text on it keeps its contrast; under Increase
    /// Contrast banners add an outline instead of a stronger fill.
    static func fill(for status: LunalyrStatus, _ look: LunalyrAppearance) -> NSColor {
        let hue: NSColor
        switch status {
        case .info: hue = LunalyrAccent.fill(dark: look.dark, increaseContrast: false)
        case .success: hue = rgb(0x28, 0xA7, 0x45)
        case .warning: hue = rgb(0xFF, 0x95, 0x00)
        case .critical: hue = rgb(0xFF, 0x3B, 0x30)
        case .neutral: hue = look.dark ? .white : .black
        }
        let alpha: CGFloat = status == .neutral ? (look.dark ? 0.08 : 0.05) : (look.dark ? 0.16 : 0.10)
        return hue.withAlphaComponent(alpha)
    }

    static func rgb(_ red: Int, _ green: Int, _ blue: Int) -> NSColor {
        NSColor(srgbRed: CGFloat(red) / 255, green: CGFloat(green) / 255, blue: CGFloat(blue) / 255, alpha: 1)
    }
}
