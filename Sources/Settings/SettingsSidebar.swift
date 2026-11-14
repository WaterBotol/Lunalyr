import SwiftUI

/// The pages in a sidebar, always visible, in three groups, with a search
/// field that filters them by what is on them ("late", "discord", "battery").
/// ↑ and ↓ move between the pages shown (↓ from the search field picks the
/// first), ⌘F goes to the search field, and the selection is kept in view.
@MainActor
struct SettingsSidebar: View {
    @Binding var selection: SettingsPage
    @State private var query = ""
    @ObservedObject private var store = SettingsStyleStore.shared
    @Environment(\.colorScheme) private var scheme
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @FocusState private var focus: Focus?

    private enum Focus { case search, list }

    /// The pages the search leaves, in sidebar order.
    static func visiblePages(matching query: String) -> [SettingsPage] {
        SettingsPage.Group.allCases.flatMap { $0.pages.filter { $0.matches(query) } }
    }

    /// The page `step` rows from `current` among `pages`, held at either end;
    /// the first (or last) when `current` isn't among them.
    static func page(from current: SettingsPage, by step: Int, in pages: [SettingsPage]) -> SettingsPage? {
        guard !pages.isEmpty else { return nil }
        guard let index = pages.firstIndex(of: current) else { return step < 0 ? pages.last : pages.first }
        return pages[min(pages.count - 1, max(0, index + step))]
    }

    static let width: CGFloat = 232

    var body: some View {
        let t = store.style.tokens(dark: scheme == .dark)
        VStack(alignment: .leading, spacing: 0) {
            searchField(t)
                .padding(.horizontal, 12)
                .padding(.top, 12)
                .padding(.bottom, 16)
            ScrollViewReader { proxy in
                ScrollView {
                    VStack(alignment: .leading, spacing: 14) {
                        ForEach(SettingsPage.Group.allCases) { group in
                            let pages = group.pages.filter { $0.matches(query) }
                            if !pages.isEmpty {
                                VStack(alignment: .leading, spacing: 2) {
                                    Text(group.title)
                                        .font(.subheadline.weight(.semibold))
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
                    // Filtering eases rows away; the selection itself stays instant.
                    .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion),
                               value: query)
                }
                .focusable()
                .focused($focus, equals: .list)
                .focusEffectDisabled()
                .onKeyPress(keys: [.upArrow, .downArrow]) { press in
                    guard press.modifiers.isEmpty else { return .ignored }
                    move(by: press.key == .upArrow ? -1 : 1)
                    return .handled
                }
                .onChange(of: selection) { _, page in
                    // Only as far as it takes to show the row.
                    withAnimation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.standard, reduceMotion: reduceMotion)) {
                        proxy.scrollTo(page)
                    }
                }
            }
        }
        .background {
            // ⌘F: to the search field.
            Button("Search Settings") { focus = .search }
                .keyboardShortcut("f", modifiers: .command)
                .frame(width: 0, height: 0)
                .opacity(0)
                .accessibilityHidden(true)
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
                .focused($focus, equals: .search)
                .onSubmit {
                    if let first = SettingsPage.allCases.first(where: { $0.matches(query) }) { selection = first }
                }
                .onKeyPress(.downArrow) {
                    guard let first = Self.visiblePages(matching: query).first else { return .ignored }
                    selection = first
                    focus = .list
                    return .handled
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
            focus = .list
        } label: {
            HStack(spacing: 8) {
                // The symbol alone, white on the selected row (no badge behind it).
                SettingsIcon(symbol: page.symbol, tint: page.tint, size: 22)
                    .foregroundStyle(on ? Color.white : Color.lunalyrAccentText)
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
        .id(page)
    }

    private func move(by step: Int) {
        if let next = Self.page(from: selection, by: step, in: Self.visiblePages(matching: query)) {
            selection = next
        }
    }
}
