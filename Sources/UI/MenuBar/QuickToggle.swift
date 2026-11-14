import SwiftUI

/// A state the panel flips -- Pause, Hide lyrics, Floating -- as a menu row
/// with a switch at its trailing edge (SPEC.md section 7, macOS's own menu
/// toggles). The whole row is the control. Its shortcut, while one is
/// registered, sits before the switch; VoiceOver hears the action named for
/// what it acts on ("Pause Wallpaper") and the state as its value
/// ("Wallpaper paused"), as the panel's earlier tiles said them.
///
///     QuickToggle("Pause", isOn: extras.wallpaperPaused, detail: "⌃⌥⌘P",
///                 accessibilityTitle: extras.pauseActionTitle,
///                 state: "Wallpaper paused") { extras.toggleWallpaperPause() }
struct QuickToggle: View {
    let title: String
    var isOn: Bool
    /// The shortcut while one is registered.
    var detail: String?
    /// Spoken for the action, when the title alone is ambiguous
    /// ("Resume Wallpaper", not "Pause").
    var accessibilityTitle: String?
    /// Spoken as the value while on ("Wallpaper paused"); "On" if nil.
    var state: String?
    let action: () -> Void

    init(_ title: String, isOn: Bool = false, detail: String? = nil, accessibilityTitle: String? = nil,
         state: String? = nil, action: @escaping () -> Void) {
        self.title = title
        self.isOn = isOn
        self.detail = detail
        self.accessibilityTitle = accessibilityTitle
        self.state = state
        self.action = action
    }

    var body: some View {
        Button(action: action) {
            Text(title)
        }
        .buttonStyle(MenuPanelRowStyle {
            HStack(spacing: 8) {
                if let detail { Text(detail) }
                MenuPanelSwitch(isOn: isOn)
            }
        })
        .help(Self.help(title: accessibilityTitle ?? title, shortcut: detail))
        .accessibilityLabel(accessibilityTitle ?? title)
        .accessibilityValue(Self.spokenValue(isOn: isOn, state: state))
        .accessibilityAddTraits(.isToggle)
    }

    /// "Pause Wallpaper (⌃⌥⌘P)".
    static func help(title: String, shortcut: String?) -> String {
        shortcut.map { "\(title) (\($0))" } ?? title
    }

    /// The state while on, nothing while off (as the tiles said it).
    static func spokenValue(isOn: Bool, state: String?) -> String {
        isOn ? (state ?? "On") : ""
    }
}
