import SwiftUI

/// The pages in a sidebar, always visible, in three groups, with a search
/// field that filters them by what is on them ("late", "discord", "battery").
@MainActor
struct SettingsSidebar: View {
    @Binding var selection: SettingsPage
    @State private var query = ""
    @ObservedObject private var store = SettingsStyleStore.shared
    @Environment(\.colorScheme) private var scheme

    static let width: CGFloat = 232

    var body: some View {
        let t = store.style.tokens(dark: scheme == .dark)
        VStack(alignment: .leading, spacing: 0) {
            searchField(t)
                .padding(.horizontal, 12)
                .padding(.top, 12)
                .padding(.bottom, 16)
            ScrollView {
                VStack(alignment: .leading, spacing: 14) {
                    ForEach(SettingsPage.Group.allCases) { group in
                        let pages = group.pages.filter { $0.matches(query) }
                        if !pages.isEmpty {
                            VStack(alignment: .leading, spacing: 2) {
                                Text(group.title.uppercased())
                                    .font(.system(size: 11, weight: .bold))
                                    .tracking(0.6)
                                    .foregroundStyle(Color(nsColor: t.secondary))
                                    .padding(.horizontal, 8)
                                    .padding(.bottom, 2)
                                    .accessibilityAddTraits(.isHeader)
                                ForEach(pages) { page in row(page, t) }
                            }
                        }
                    }
                    if SettingsPage.allCases.allSatisfy({ !$0.matches(query) }) {
                        Text("No page matches “\(query)”.")
                            .lunalyrText(.caption)
                            .padding(.horizontal, 8)
                    }
                }
                .padding(.horizontal, 12)
                .padding(.bottom, 12)
            }
        }
        .frame(width: Self.width)
        .frame(maxHeight: .infinity)
        .background(Color(nsColor: t.face).opacity(store.style == .bold ? 1 : 0.55))
    }

    private func searchField(_ t: SettingsVisualStyle.Tokens) -> some View {
        HStack(spacing: 6) {
            Image(systemName: "magnifyingglass").foregroundStyle(Color(nsColor: t.secondary))
            TextField("Search settings", text: $query)
                .textFieldStyle(.plain)
                .onSubmit {
                    if let first = SettingsPage.allCases.first(where: { $0.matches(query) }) { selection = first }
                }
            if !query.isEmpty {
                Button { query = "" } label: { Image(systemName: "xmark.circle.fill") }
                    .buttonStyle(.plain)
                    .foregroundStyle(Color(nsColor: t.secondary))
                    .accessibilityLabel("Clear search")
            }
        }
        .padding(.horizontal, 8)
        .frame(height: 28)
        .themedControl(radius: 8)
    }

    private func row(_ page: SettingsPage, _ t: SettingsVisualStyle.Tokens) -> some View {
        let on = page == selection
        return Button {
            selection = page
        } label: {
            HStack(spacing: 8) {
                if on {
                    Image(systemName: page.symbol)
                        .font(.system(size: 12, weight: .semibold))
                        .foregroundStyle(Color.white)
                        .frame(width: 22, height: 22)
                        .background(RoundedRectangle(cornerRadius: 6, style: .continuous).fill(Color.white.opacity(0.22)))
                        .accessibilityHidden(true)
                } else {
                    SettingsIcon(symbol: page.symbol, tint: page.tint, size: 22)
                }
                Text(page.title)
                    .font(.body.weight(on ? .semibold : .regular))
                    .foregroundStyle(on ? Color.white : Color(nsColor: t.ink))
                    .lineLimit(1)
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 8)
            .frame(height: 32)
            .background(RoundedRectangle(cornerRadius: t.controlRadius, style: .continuous)
                .fill(on ? Color.lunalyrAccent : Color.clear))
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(on ? .isSelected : [])
    }
}
