import SwiftUI

/// `ToggleTile` drawn the way Control Center draws its controls: a circle
/// that lights in the accent while on, its name beneath, and its shortcut
/// under that, with no tile around it. A row of three sits lighter than the
/// wallpaper-type tiles above it (those choose one of seven looks; these
/// flip a state), and takes 20 points less height, which keeps the panel
/// under its 700-point budget with a status showing. Same inputs as
/// `ToggleTile` plus a spoken state, so it can fold into QuickControls.swift
/// as a style of it.
///
///     QuickToggle("Pause", symbol: extras.pauseActionSymbol, isOn: extras.wallpaperPaused,
///                 detail: "⌃⌥⌘P", accessibilityTitle: extras.pauseActionTitle,
///                 state: "Wallpaper paused") { extras.toggleWallpaperPause() }
struct QuickToggle: View {
    let title: String
    let symbol: String
    var isOn: Bool
    /// A second line: the shortcut while one is registered.
    var detail: String?
    /// Spoken for the action, when the title alone is ambiguous
    /// ("Resume Wallpaper", not "Resume").
    var accessibilityTitle: String?
    /// Spoken as the value while on ("Wallpaper paused"); "On" if nil.
    var state: String?
    let action: () -> Void
    @Environment(\.isEnabled) private var enabled
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    init(_ title: String, symbol: String, isOn: Bool = false, detail: String? = nil, accessibilityTitle: String? = nil,
         state: String? = nil, action: @escaping () -> Void) {
        self.title = title
        self.symbol = symbol
        self.isOn = isOn
        self.detail = detail
        self.accessibilityTitle = accessibilityTitle
        self.state = state
        self.action = action
    }

    static let diameter: CGFloat = 32

    var body: some View {
        Button(action: action) {
            VStack(spacing: LunalyrDesign.Spacing.xSmall) {
                Image(systemName: symbol)
                    .font(.system(size: 14, weight: .semibold))
                    .contentTransition(reduceMotion ? .opacity : .symbolEffect(.replace.downUp))
                    .foregroundStyle(isOn ? Color.white : Color.primary)
                    .frame(width: Self.diameter, height: Self.diameter)
                    .background(Circle().fill(isOn ? AnyShapeStyle(Color.lunalyrAccent) : AnyShapeStyle(.quaternary)))
                    .overlay(Circle().strokeBorder(Color.lunalyrContrastBorder, lineWidth: 1))
                    .modifier(QuickTogglePress())
                VStack(spacing: 1) {
                    // Short names, never scaled: a scale worked out in an
                    // early sizing pass can stick.
                    Text(title)
                        .font(.subheadline.weight(.medium))
                        .lineLimit(1)
                        .contentTransition(.opacity)
                    if let detail {
                        Text(detail)
                            .lunalyrText(.footnote)
                            .lineLimit(1)
                    }
                }
            }
            .frame(maxWidth: .infinity)
            .opacity(enabled ? 1 : 0.45)
            .contentShape(Rectangle())
        }
        .buttonStyle(QuickToggleButtonStyle())
        // Value-scoped, so a shortcut or link that flips it animates too.
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion), value: isOn)
        .help(detail.map { "\(accessibilityTitle ?? title) (\($0))" } ?? (accessibilityTitle ?? title))
        .accessibilityLabel(accessibilityTitle ?? title)
        .accessibilityValue(isOn ? (state ?? "On") : "")
    }
}

/// Darkens the control while it is pressed, as Control Center's do, and
/// tells the circle (`QuickTogglePress`) to press in.
private struct QuickToggleButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .brightness(configuration.isPressed ? -0.12 : 0)
            .environment(\.quickTogglePressed, configuration.isPressed)
    }
}

/// The circle shrinks a little while pressed, as Control Center's do; only
/// the darkening under Reduce Motion.
private struct QuickTogglePress: ViewModifier {
    @Environment(\.quickTogglePressed) private var pressed
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    func body(content: Content) -> some View {
        content
            .scaleEffect(QuickToggle.pressScale(pressed: pressed, reduceMotion: reduceMotion))
            .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion),
                       value: pressed)
    }
}

extension QuickToggle {
    /// The circle's scale while pressed.
    static func pressScale(pressed: Bool, reduceMotion: Bool) -> CGFloat {
        pressed && !reduceMotion ? 0.92 : 1
    }
}

private struct QuickTogglePressedKey: EnvironmentKey {
    static let defaultValue = false
}

private extension EnvironmentValues {
    var quickTogglePressed: Bool {
        get { self[QuickTogglePressedKey.self] }
        set { self[QuickTogglePressedKey.self] = newValue }
    }
}
