import AppKit
import SwiftUI

/// The foot of the menu-bar panel, like the end of a menu: which player
/// Lunalyr follows, then Settings…, Check for Updates… and Quit Lunalyr as
/// rows that highlight under the pointer, with their keys at the trailing
/// edge. ⌘, and ⌘Q work while the panel is open.
struct MenuBarFooter: View {
    @Model(\.updates) private var updates
    @Environment(\.lunalyrModels) private var models

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            MenuBarPickerRow("Player") {
                LunalyrPlayerPicker()
            }
            .padding(.horizontal, 8)
            .padding(.bottom, LunalyrDesign.Spacing.hairline)
            Button(LunalyrExtras.Titles.settings) { models.resolved.navigation.openSettings() }
                .buttonStyle(MenuRowButtonStyle(shortcut: "⌘,"))
                .keyboardShortcut(",", modifiers: .command)
            Button("Check for Updates…") { updates.check() }
                .buttonStyle(MenuRowButtonStyle())
                .disabled(updates.buttonDisabled)
                .help(updates.buttonDisabled ? "Available once the update service has started" : "")
            Button("Quit Lunalyr") { NSApplication.shared.terminate(nil) }
                .buttonStyle(MenuRowButtonStyle(shortcut: "⌘Q"))
                .keyboardShortcut("q", modifiers: .command)
        }
        // The rows' highlight reaches into the panel's margins, as a menu's
        // does, while their words line up with everything above.
        .padding(.horizontal, -8)
    }
}
