import SwiftUI

/// The rarer controls of a section, at its foot, behind a closed "More
/// options" disclosure: a section shows its everyday controls, and the
/// rest are one click away. It opens closed each time the page is shown.
///
/// Not a `DisclosureGroup`: that indents its content and leaves only the
/// chevron and title clickable. Here the whole row is the button, and the
/// content keeps the section's full width, so wide rows (a link field with
/// buttons, the video timing nudges) never squeeze their labels.
struct MoreOptions<Content: View>: View {
    static var title: String { "More options" }
    static var hideTitle: String { "Fewer options" }
    /// The row's height: a comfortable target for the whole row.
    static var rowHeight: CGFloat { 28 }

    let content: Content
    @State private var expanded = false
    @State private var hovering = false
    @Environment(\.formDensity) private var density
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    init(@ViewBuilder content: () -> Content) {
        self.content = content()
    }

    var body: some View {
        VStack(alignment: .leading, spacing: density.rowSpacing) {
            toggleRow
            if expanded {
                VStack(alignment: .leading, spacing: density.rowSpacing) {
                    content
                }
                .labeledContentStyle(FittingLabeledContentStyle())
                .frame(maxWidth: .infinity, alignment: .leading)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var toggleRow: some View {
        Button {
            withAnimation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion)) {
                expanded.toggle()
            }
        } label: {
            HStack(spacing: 6) {
                Image(systemName: "chevron.right")
                    .font(.system(size: 10, weight: .semibold))
                    .rotationEffect(.degrees(expanded ? 90 : 0))
                    .accessibilityHidden(true)
                Text(expanded ? Self.hideTitle : Self.title)
                Spacer(minLength: 0)
            }
            .foregroundStyle(.secondary)
            .padding(.horizontal, 6)
            .frame(maxWidth: .infinity, minHeight: Self.rowHeight, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: 6, style: .continuous)
                .fill(Color.primary.opacity(hovering ? 0.06 : 0)))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .padding(.horizontal, -6)
        .onHover { hovering = $0 }
        .accessibilityLabel(Self.title)
        .accessibilityValue(expanded ? "Shown" : "Hidden")
        .accessibilityAddTraits(.isButton)
    }
}

/// A label beside its control when both fit the width, otherwise the label
/// above the control at full width: rows inside More options sit outside a
/// `Form`, where `LabeledContent` would otherwise squeeze the label.
struct FittingLabeledContentStyle: LabeledContentStyle {
    func makeBody(configuration: Configuration) -> some View {
        ViewThatFits(in: .horizontal) {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                configuration.label.fixedSize()
                Spacer(minLength: 8)
                configuration.content
            }
            VStack(alignment: .leading, spacing: 4) {
                configuration.label
                configuration.content
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
        }
    }
}
