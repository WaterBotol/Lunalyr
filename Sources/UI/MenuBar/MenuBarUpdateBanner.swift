import SwiftUI

/// The top of the menu-bar panel while the daily check has found an update
/// (the icon is a violet arrow meanwhile): "Lunalyr 2.1.0 is available", with
/// Update…, which shows Sparkle's update window, and Later, which brings the
/// moon back and takes this row away until the next daily check. Settings › General
/// keeps saying it until the update is installed or skipped.
struct MenuBarUpdateBanner: View {
    @Model(\.updates) private var updates

    var body: some View {
        if updates.badge.showsBadge {
            MenuPanelStatus(.info, title: UpdateBadgeState.headline(for: updates.badge.pendingVersion),
                            message: Self.message, symbol: Self.symbol) {
                Button("Update…") { updates.check() }
                    .help("See what’s new and install the update")
                Button("Later") { updates.dismissBadge() }
                    .help("Hide this until tomorrow’s check. Settings › General still shows the update.")
            }
        }
    }

    static let message = "See what’s new, then install it when you’re ready."
    static let symbol = "arrow.down.circle.fill"
}
