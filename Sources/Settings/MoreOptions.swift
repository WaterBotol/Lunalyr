import SwiftUI

/// The rarer controls of a section, at its foot, behind a closed "More
/// options" disclosure: a section shows its everyday controls, and the
/// rest are one click away. The same disclosure as "Layouts in the
/// rotation"; it opens closed each time the page is shown.
struct MoreOptions<Content: View>: View {
    static var title: String { "More options" }

    let content: Content
    @State private var expanded = false
    @Environment(\.formDensity) private var density

    init(@ViewBuilder content: () -> Content) {
        self.content = content()
    }

    var body: some View {
        DisclosureGroup(isExpanded: $expanded) {
            VStack(alignment: .leading, spacing: density.rowSpacing) {
                content
            }
            .padding(.top, 4)
            .frame(maxWidth: .infinity, alignment: .leading)
        } label: {
            Text(Self.title)
        }
    }
}
