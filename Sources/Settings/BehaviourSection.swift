import SwiftUI

/// Shared section header styling, used by the menu bar.
@MainActor
func sectionTitle(_ text: String) -> some View {
    // docs/DESIGN.md: sentence case, nothing under 10 pt (was 9.5 pt capitals).
    Text(text)
        .font(.subheadline.weight(.semibold))
        .foregroundStyle(.secondary)
}
