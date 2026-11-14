import AppKit
import SwiftUI

/// The foot of the menu-bar panel, like the end of a menu: Settings… ⌘,,
/// Check for Updates… and Quit Lunalyr ⌘Q as rows that highlight under the
/// pointer, their keys at the trailing edge. ⌘, and ⌘Q work while the panel
/// is open.
struct MenuBarFooter: View {
    @Model(\.updates) private var updates
    @Environment(\.lunalyrModels) private var models

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            MenuPanelRow(LunalyrExtras.Titles.settings, value: "⌘,") { models.resolved.navigation.openSettings() }
                .keyboardShortcut(",", modifiers: .command)
            MenuPanelRow("Check for Updates…") { updates.check() }
                .disabled(updates.buttonDisabled)
                .help(updates.buttonDisabled ? "Available once the update service has started" : "")
            MenuPanelRow("Quit Lunalyr", value: "⌘Q") { NSApplication.shared.terminate(nil) }
                .keyboardShortcut("q", modifiers: .command)
        }
    }
}
