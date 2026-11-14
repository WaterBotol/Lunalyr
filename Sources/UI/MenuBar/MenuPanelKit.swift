import AppKit
import SwiftUI

// MARK: - The menu-bar panel's language (SPEC.md section 7)
//
// A short menu: rows 24 high, 14 in from the edges, 13 pt; secondary text a
// little darker than the window's ink2 (it sits on frosted glass); 1-pt
// separators 14 in, 5 above and below. No cards, no icons per row.
//
//     MenuPanelRow("Tap to sync…") { tapSync.toggle() }
//     MenuPanelSubmenuRow("Wallpaper", value: "Match the music", items: items)
//     QuickToggle("Pause", isOn: paused, …) { extras.toggleWallpaperPause() }
//     MenuPanelSeparator()

enum MenuPanel {
    static let rowHeight: CGFloat = 24
    static let inset: CGFloat = 14
    static let verticalPadding: CGFloat = 6
    static let cornerRadius: CGFloat = 12
    static let rowFont = Font.system(size: 13)
    static let labelFont = Font.system(size: 12, weight: .semibold)
    static let miniHeight: CGFloat = 20

    enum Role {
        /// Secondary text: values, artist, the next line.
        case secondary
        /// Over the material: white 62 % / black 48 %.
        case tint
        /// Opaque ground under Reduce Transparency.
        case solid
        /// Mini buttons and the empty cover.
        case fill
        /// Hover and press under a row.
        case highlight
    }

    static func color(_ role: Role, dark: Bool, increaseContrast: Bool = false) -> NSColor {
        func rgb(_ value: UInt32) -> NSColor {
            NSColor(srgbRed: CGFloat((value >> 16) & 0xFF) / 255, green: CGFloat((value >> 8) & 0xFF) / 255,
                    blue: CGFloat(value & 0xFF) / 255, alpha: 1)
        }
        switch role {
        case .secondary:
            if increaseContrast { return dark ? rgb(0xDADADF) : rgb(0x2E2E32) }
            return dark ? rgb(0xB8B8BF) : rgb(0x4A4A4F)
        case .tint: return dark ? NSColor(white: 0, alpha: 0.48) : NSColor(white: 1, alpha: 0.62)
        case .solid: return dark ? rgb(0x262628) : rgb(0xF2F2F4)
        case .fill: return dark ? NSColor(white: 1, alpha: 0.10) : NSColor(white: 0, alpha: 0.07)
        case .highlight: return dark ? NSColor(white: 1, alpha: 0.10) : NSColor(white: 0, alpha: 0.07)
        }
    }
}

extension Color {
    /// A menu-bar panel colour, resolved for the current appearance and contrast.
    static func menuPanel(_ role: MenuPanel.Role) -> Color {
        Color(nsColor: NSColor(name: "LunalyrMenuPanel.\(role)") { appearance in
            let look = LunalyrAppearance(appearance)
            return MenuPanel.color(role, dark: look.dark, increaseContrast: look.increaseContrast)
        })
    }
}

// MARK: Ground

/// Frosted material with the spec's tint; opaque under Reduce Transparency.
struct MenuPanelGround: View {
    @Environment(\.accessibilityReduceTransparency) private var reduceTransparency

    var body: some View {
        if reduceTransparency {
            Rectangle().fill(Color.menuPanel(.solid))
        } else {
            ZStack {
                Rectangle().fill(.regularMaterial)
                Rectangle().fill(Color.menuPanel(.tint))
            }
        }
    }
}

// MARK: Separator and label

struct MenuPanelSeparator: View {
    var body: some View {
        Rectangle()
            .fill(Color.studio(.separator))
            .frame(height: 1)
            .padding(.horizontal, MenuPanel.inset)
            .padding(.vertical, 5)
            .accessibilityHidden(true)
    }
}

/// A group's name ("Look", "Listen along"): 12 semibold, secondary.
struct MenuPanelLabel<Accessory: View>: View {
    let title: String
    let accessory: Accessory

    init(_ title: String, @ViewBuilder accessory: () -> Accessory) {
        self.title = title
        self.accessory = accessory()
    }

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 6) {
            Text(title)
                .font(MenuPanel.labelFont)
                .foregroundStyle(Color.menuPanel(.secondary))
                .accessibilityAddTraits(.isHeader)
            Spacer(minLength: 6)
            accessory
        }
    }
}

extension MenuPanelLabel where Accessory == EmptyView {
    init(_ title: String) { self.init(title) { EmptyView() } }
}

/// A caption under a row: 11.5, secondary, wrapping.
struct MenuPanelCaption: View {
    let text: String
    /// Inside a section that has its own inset: no padding of its own.
    var inline = false

    init(_ text: String) { self.text = text }
    init(inline text: String) {
        self.text = text
        inline = true
    }

    var body: some View {
        Text(text)
            .font(LunalyrDesign.Studio.TypeScale.caption)
            .foregroundStyle(Color.menuPanel(.secondary))
            .fixedSize(horizontal: false, vertical: true)
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.horizontal, inline ? 0 : MenuPanel.inset)
            .padding(.bottom, inline ? 0 : 3)
    }
}

// MARK: Rows

/// A row like a menu item: its title, an optional value or shortcut at the
/// trailing edge, highlighted under the pointer. Disabled rows are ink at 40 %.
struct MenuPanelRowStyle<Trailing: View>: ButtonStyle {
    let trailing: Trailing

    init(@ViewBuilder trailing: () -> Trailing) {
        self.trailing = trailing()
    }

    func makeBody(configuration: Configuration) -> some View {
        MenuPanelRowBody(pressed: configuration.isPressed) {
            configuration.label
        } trailing: {
            trailing
        }
    }
}

extension MenuPanelRowStyle where Trailing == EmptyView {
    init() { self.init { EmptyView() } }
}

/// The look shared by every row: the hover highlight reaches 5 points into
/// the panel's margin, as a menu's does, while the words line up at 14.
struct MenuPanelRowBody<Label: View, Trailing: View>: View {
    var pressed = false
    let label: Label
    let trailing: Trailing
    @State private var hovering = false
    @Environment(\.isEnabled) private var enabled

    init(pressed: Bool = false, @ViewBuilder label: () -> Label, @ViewBuilder trailing: () -> Trailing) {
        self.pressed = pressed
        self.label = label()
        self.trailing = trailing()
    }

    var body: some View {
        HStack(spacing: 6) {
            label
                .font(MenuPanel.rowFont)
                .foregroundStyle(Color.studio(.ink).opacity(enabled ? 1 : 0.4))
                .lineLimit(1)
            Spacer(minLength: 8)
            trailing
                .font(MenuPanel.rowFont)
                .foregroundStyle(enabled ? Color.menuPanel(.secondary) : Color.studio(.ink).opacity(0.4))
        }
        .padding(.horizontal, MenuPanel.inset - 5)
        .frame(minHeight: MenuPanel.rowHeight)
        .frame(maxWidth: .infinity, alignment: .leading)
        .contentShape(Rectangle())
        .background(
            RoundedRectangle(cornerRadius: 5, style: .continuous)
                .fill(Color.menuPanel(.highlight).opacity(hovering && enabled ? (pressed ? 1.6 : 1) : 0))
        )
        .padding(.horizontal, 5)
        .onHover { hovering = $0 }
        // A panel that closes under the pointer never hears it leave.
        .onDisappear { hovering = false }
        .onReceive(NotificationCenter.default.publisher(for: NSWindow.didResignKeyNotification)) { _ in
            hovering = false
        }
    }
}

/// A plain action row: "Tap to sync…", "Open Lyric Card…  ⌃⌥⌘C".
struct MenuPanelRow: View {
    let title: String
    var value: String?
    let action: () -> Void

    init(_ title: String, value: String? = nil, action: @escaping () -> Void) {
        self.title = title
        self.value = value
        self.action = action
    }

    var body: some View {
        Button(action: action) {
            Text(title)
        }
        .buttonStyle(MenuPanelRowStyle {
            if let value { Text(value) }
        })
    }
}

// MARK: Mini buttons

/// The panel's small buttons (Earlier, Later, a banner's fix): 12 pt, 20
/// high, radius 5, a faint fill.
struct MenuPanelMiniButtonStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        MiniBody(configuration: configuration)
    }

    private struct MiniBody: View {
        let configuration: ButtonStyleConfiguration
        @Environment(\.isEnabled) private var enabled
        @Environment(\.colorSchemeContrast) private var contrast

        var body: some View {
            configuration.label
                .font(.system(size: 12))
                .foregroundStyle(Color.studio(.ink).opacity(enabled ? 1 : 0.4))
                .lineLimit(1)
                .padding(.horizontal, 8)
                .frame(minHeight: MenuPanel.miniHeight)
                .background(RoundedRectangle(cornerRadius: 5, style: .continuous)
                    .fill(Color.menuPanel(.fill).opacity(configuration.isPressed ? 2 : 1)))
                .overlay {
                    if contrast == .increased {
                        RoundedRectangle(cornerRadius: 5, style: .continuous)
                            .strokeBorder(Color.studio(.ink).opacity(0.25), lineWidth: 1)
                    }
                }
                .contentShape(RoundedRectangle(cornerRadius: 5, style: .continuous))
        }
    }
}

extension ButtonStyle where Self == MenuPanelMiniButtonStyle {
    static var menuPanelMini: MenuPanelMiniButtonStyle { MenuPanelMiniButtonStyle() }
}

// MARK: Switch

/// The switch a toggle row draws at its trailing edge (29 x 17, accent when on).
struct MenuPanelSwitch: View {
    let isOn: Bool
    @Environment(\.isEnabled) private var enabled
    @Environment(\.colorSchemeContrast) private var contrast
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    var body: some View {
        Capsule()
            .fill(isOn ? AnyShapeStyle(Color.studio(.accent)) : AnyShapeStyle(Color.studio(.track).opacity(1.7)))
            .frame(width: 29, height: 17)
            .overlay(alignment: isOn ? .trailing : .leading) {
                Circle().fill(.white).padding(2).shadow(color: .black.opacity(0.2), radius: 1, y: 0.5)
            }
            .overlay {
                if contrast == .increased {
                    Capsule().strokeBorder(Color.studio(.ink).opacity(0.25), lineWidth: 1)
                }
            }
            .opacity(enabled ? 1 : 0.4)
            .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion), value: isOn)
            .accessibilityHidden(true)
    }
}

// MARK: Submenus

/// One entry of a row's submenu.
struct MenuPanelMenuItem: Identifiable {
    let id: String
    let title: String
    var checked = false
    var enabled = true
    /// The item's tooltip.
    var help: String?
    let action: () -> Void

    /// A line between groups of items.
    static func separator(_ id: String = "separator") -> MenuPanelMenuItem {
        MenuPanelMenuItem(id: "\(separatorPrefix)\(id)", title: "", enabled: false) {}
    }

    static let separatorPrefix = "menu.separator."
    var isSeparator: Bool { id.hasPrefix(Self.separatorPrefix) }
}

/// A row that opens a real menu beside it, like a menu's submenu:
/// "Wallpaper   Match the music ›". The menu lists `items`, the chosen one
/// checked. Reachable with the keyboard (Space or Return opens it).
struct MenuPanelSubmenuRow: View {
    let title: String
    var value: String?
    let items: () -> [MenuPanelMenuItem]
    @State private var anchor = MenuPanelMenuAnchor.Handle()

    init(_ title: String, value: String? = nil, items: @escaping () -> [MenuPanelMenuItem]) {
        self.title = title
        self.value = value
        self.items = items
    }

    var body: some View {
        Button {
            anchor.popUp(items())
        } label: {
            Text(title)
        }
        .buttonStyle(MenuPanelRowStyle {
            HStack(spacing: 6) {
                if let value { Text(value) }
                Image(systemName: "chevron.right")
                    .font(.system(size: 10, weight: .semibold))
            }
        })
        .background(MenuPanelMenuAnchor(handle: anchor))
        .accessibilityLabel(title)
        .accessibilityValue(value ?? "")
        .accessibilityHint("Opens a menu of choices")
    }
}

/// An invisible AppKit view under a submenu row, for the menu to pop up from.
struct MenuPanelMenuAnchor: NSViewRepresentable {
    final class Handle {
        weak var view: NSView?

        @MainActor func popUp(_ items: [MenuPanelMenuItem]) {
            guard let view else { return }
            let menu = NSMenu()
            menu.autoenablesItems = false
            for item in items {
                if item.isSeparator {
                    menu.addItem(.separator())
                    continue
                }
                let target = MenuPanelMenuTarget(item.action)
                let entry = NSMenuItem(title: item.title, action: #selector(MenuPanelMenuTarget.run), keyEquivalent: "")
                entry.target = target
                entry.representedObject = target
                entry.state = item.checked ? .on : .off
                entry.isEnabled = item.enabled
                entry.toolTip = item.help
                menu.addItem(entry)
            }
            // Beside the row's trailing edge, like a submenu.
            let bounds = view.bounds
            let origin = NSPoint(x: bounds.maxX - 8, y: view.isFlipped ? bounds.minY : bounds.maxY)
            menu.popUp(positioning: menu.items.first { $0.state == .on }, at: origin, in: view)
        }
    }

    let handle: Handle

    func makeNSView(context: Context) -> NSView {
        let view = NSView()
        handle.view = view
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {
        handle.view = nsView
    }
}

/// Runs a submenu item's closure (NSMenuItem needs an Objective-C target).
final class MenuPanelMenuTarget: NSObject {
    private let action: () -> Void

    init(_ action: @escaping () -> Void) {
        self.action = action
    }

    @objc func run() { action() }
}
