import SwiftUI

/// The sidebar: a search field, then plain-text rows (no icons): Look, which
/// opens the picture, and the Words and App groups. The search filters the
/// rows by what is on their pages ("late", "discord", "battery"); ↑ and ↓
/// move between the rows shown (↓ from the search field picks the first),
/// ⌘F goes to the search field, and the selection is kept in view.
@MainActor
struct SettingsSidebar: View {
    @Binding var selection: SettingsPage
    /// The Look page last shown, which the Look row opens again.
    var lastLook: SettingsPage = .wallpaper
    @State private var query = ""
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.colorScheme) private var scheme
    @FocusState private var focus: Focus?

    private enum Focus { case search, list }

    /// The pages the search leaves, in sidebar order.
    static func visiblePages(matching query: String) -> [SettingsPage] {
        SettingsPage.Group.allCases.flatMap { $0.pages.filter { $0.matches(query) } }
    }

    /// The rows the search leaves, in sidebar order.
    static func visibleRows(matching query: String) -> [SettingsSidebarRow] {
        SettingsSidebarRow.all.filter { $0.matches(query) }
    }

    /// The item `step` rows from `current` among `items`, held at either
    /// end; the first (or last) when `current` isn't among them.
    static func page<Item: Equatable>(from current: Item, by step: Int, in items: [Item]) -> Item? {
        guard !items.isEmpty else { return nil }
        guard let index = items.firstIndex(of: current) else { return step < 0 ? items.last : items.first }
        return items[min(items.count - 1, max(0, index + step))]
    }

    static let width: CGFloat = LunalyrDesign.Studio.Layout.sidebarWidth
    /// Room for the window's buttons above the search field.
    static let topInset: CGFloat = 46

    private var selectedRow: SettingsSidebarRow { SettingsSidebarRow(selection) }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            searchField
                .padding(.horizontal, 12)
                .padding(.top, Self.topInset)
                .padding(.bottom, 16)
            ScrollViewReader { proxy in
                ScrollView {
                    VStack(alignment: .leading, spacing: 2) {
                        let rows = Self.visibleRows(matching: query)
                        if rows.contains(.look) { row(.look) }
                        ForEach(SettingsSidebarRow.groups, id: \.title) { group in
                            let shown = group.rows.filter(rows.contains)
                            if !shown.isEmpty {
                                Text(group.title)
                                    .font(LunalyrDesign.Studio.TypeScale.sidebarGroup)
                                    .foregroundStyle(Color.studio(.ink2))
                                    .padding(.leading, 10)
                                    .padding(.top, 16)
                                    .padding(.bottom, 4)
                                    .accessibilityAddTraits(.isHeader)
                                ForEach(shown) { row($0) }
                            }
                        }
                        if rows.isEmpty {
                            Text("No page matches “\(query)”.")
                                .font(LunalyrDesign.Studio.TypeScale.caption)
                                .foregroundStyle(Color.studio(.ink2))
                                .padding(.horizontal, 10)
                        }
                    }
                    .padding(.horizontal, LunalyrDesign.Studio.Layout.sidebarInset)
                    .padding(.bottom, 12)
                    // Filtering eases rows away; the selection itself stays instant.
                    .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion),
                               value: query)
                }
                .scrollContentBackground(.hidden)
                .focusable()
                .focused($focus, equals: .list)
                .focusEffectDisabled()
                .onKeyPress(keys: [.upArrow, .downArrow]) { press in
                    guard press.modifiers.isEmpty else { return .ignored }
                    move(by: press.key == .upArrow ? -1 : 1)
                    return .handled
                }
                .onChange(of: selectedRow) { _, row in
                    // Only as far as it takes to show the row.
                    withAnimation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.standard, reduceMotion: reduceMotion)) {
                        proxy.scrollTo(row.id)
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
        .frame(maxHeight: .infinity, alignment: .top)
        .background(Color.studio(.sidebar))
    }

    private var searchField: some View {
        HStack(spacing: 6) {
            Image(systemName: "magnifyingglass")
                .font(.system(size: 12))
                .foregroundStyle(Color.studio(.ink2))
                .accessibilityHidden(true)
            TextField("Search settings", text: $query, prompt: Text("Search settings").foregroundStyle(Color.studio(.ink2)))
                .textFieldStyle(.plain)
                .font(.system(size: 13))
                .focused($focus, equals: .search)
                .onSubmit {
                    if let first = Self.visibleRows(matching: query).first { choose(first) }
                }
                .onKeyPress(.downArrow) {
                    guard let first = Self.visibleRows(matching: query).first else { return .ignored }
                    choose(first)
                    focus = .list
                    return .handled
                }
            if !query.isEmpty {
                Button { query = "" } label: { Image(systemName: "xmark.circle.fill") }
                    .buttonStyle(.plain)
                    .foregroundStyle(Color.studio(.ink2))
                    .accessibilityLabel("Clear search")
            }
        }
        .padding(.horizontal, 8)
        .frame(height: LunalyrDesign.Studio.Layout.searchHeight)
        .background(RoundedRectangle(cornerRadius: LunalyrDesign.Studio.Layout.searchRadius, style: .continuous)
            .fill(Color.studio(.searchFill)))
        .overlay(RoundedRectangle(cornerRadius: LunalyrDesign.Studio.Layout.searchRadius, style: .continuous)
            .strokeBorder(Color.lunalyrContrastBorder, lineWidth: 1))
    }

    private func row(_ row: SettingsSidebarRow) -> some View {
        let on = row == selectedRow
        return Button {
            choose(row)
            focus = .list
        } label: {
            Text(row.title)
                .font(.system(size: 13, weight: on ? .semibold : .regular))
                .foregroundStyle(Color.studio(.ink).opacity(on ? 1 : (scheme == .dark ? 0.82 : 0.78)))
                .lineLimit(1)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.horizontal, 10)
                .frame(height: LunalyrDesign.Studio.Layout.sidebarRowHeight)
                .background(RoundedRectangle(cornerRadius: LunalyrDesign.Studio.Layout.sidebarRowRadius, style: .continuous)
                    .fill(on ? Color.studio(.sidebarSelected) : Color.clear))
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(on ? .isSelected : [])
        .id(row.id)
    }

    private func choose(_ row: SettingsSidebarRow) {
        selection = row.destination(lastLook: lastLook, query: query)
    }

    private func move(by step: Int) {
        if let next = Self.page(from: selectedRow, by: step, in: Self.visibleRows(matching: query)) {
            choose(next)
        }
    }
}
