import SwiftUI

/// The Look switcher (SPEC.md section 7, item 3): "Look", then the six
/// built-in Looks as small pictures with their names, the chosen one ringed
/// and its name semibold. A click applies the Look through `LookStore`, the
/// same action as Settings, undoable where the panel has an undo manager.
/// While the chosen Look is edited, or none is chosen, its name sits at the
/// right of the label ("Matched, edited", "Custom"). Saved Looks are a "Saved Looks" submenu
/// row beneath, shown only once any exist.
struct MenuBarLookSwitcher: View {
    @ObservedObject var store: LookStore
    @Environment(\.undoManager) private var undoManager
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    static let tileWidth: CGFloat = 47
    static let tileHeight: CGFloat = 29

    var body: some View {
        let edited = store.isEdited
        VStack(alignment: .leading, spacing: 0) {
            VStack(alignment: .leading, spacing: 7) {
                MenuPanelLabel("Look") {
                    if let value = Self.headerValue(current: store.currentLook, isEdited: edited) {
                        Text(value)
                            .font(.system(size: 12))
                            .foregroundStyle(Color.menuPanel(.secondary))
                            .lineLimit(1)
                    }
                }
                .padding(.horizontal, 2)
                HStack(alignment: .top, spacing: 0) {
                    ForEach(Look.builtIns) { look in
                        tile(look, chosen: store.current == look.id, edited: edited)
                            .frame(maxWidth: .infinity)
                    }
                }
                .accessibilityElement(children: .contain)
                .accessibilityLabel("Look")
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 4)
            if !store.userLooks.isEmpty {
                MenuPanelSubmenuRow("Saved Looks", value: savedValue) {
                    store.userLooks.map { look in
                        MenuPanelMenuItem(id: look.id, title: look.name, checked: store.current == look.id,
                                          help: look.summary) { choose(look) }
                    }
                }
                .padding(.top, 4)
            }
        }
        .animation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.quick, reduceMotion: reduceMotion),
                   value: store.current)
    }

    /// The chosen saved Look's name, when one is chosen.
    private var savedValue: String? {
        guard let look = store.currentLook, !look.isBuiltIn else { return nil }
        return look.name
    }

    private func choose(_ look: Look) {
        Self.apply(look, in: store, undoManager: undoManager)
    }

    /// What a click does: the Look's values written in one undoable change.
    @MainActor
    static func apply(_ look: Look, in store: LookStore, undoManager: UndoManager?) {
        store.apply(look, undoManager: undoManager)
    }

    /// The name at the right of "Look": nil while a Look is chosen and
    /// unedited (a built-in's ringed tile, or the Saved Looks row, says it).
    static func headerValue(current: Look?, isEdited: Bool) -> String? {
        if current != nil, !isEdited { return nil }
        return LookStore.displayName(for: current, isEdited: isEdited)
    }

    /// The tile's name to VoiceOver: "Matched", or "Matched, edited" for
    /// the chosen Look once edited.
    static func spokenName(_ look: Look, chosen: Bool, isEdited: Bool) -> String {
        LookStore.displayName(for: look, isEdited: chosen && isEdited)
    }

    private func tile(_ look: Look, chosen: Bool, edited: Bool) -> some View {
        Button {
            choose(look)
        } label: {
            VStack(spacing: 5) {
                Group {
                    if let art = MenuBarLookArt.art(for: look.id) {
                        MenuBarLookArt(art: art)
                    } else {
                        WallpaperTypePicture(mode: look.wallpaper)
                    }
                }
                .frame(width: Self.tileWidth, height: Self.tileHeight)
                .clipShape(RoundedRectangle(cornerRadius: 5, style: .continuous))
                .modifier(MenuBarChosenRing(chosen: chosen))
                Text(look.name)
                    .font(.system(size: 10.5, weight: chosen ? .semibold : .regular))
                    .foregroundStyle(chosen ? Color.studio(.ink) : Color.menuPanel(.secondary))
                    .lineLimit(1)
                    .fixedSize()
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(MenuBarLookTileStyle())
        .help(look.summary)
        .accessibilityLabel(Self.spokenName(look, chosen: chosen, isEdited: edited))
        .accessibilityHint(look.summary)
        .accessibilityAddTraits(chosen ? [.isSelected] : [])
    }
}

/// The chosen ring: 2.5 pt of accent, 4 outside the picture.
struct MenuBarChosenRing: ViewModifier {
    let chosen: Bool
    @Environment(\.colorSchemeContrast) private var contrast

    func body(content: Content) -> some View {
        content
            .overlay {
                if contrast == .increased {
                    RoundedRectangle(cornerRadius: 5, style: .continuous)
                        .strokeBorder(Color.studio(.ink).opacity(0.25), lineWidth: 1)
                }
            }
            .overlay {
                RoundedRectangle(cornerRadius: 8, style: .continuous)
                    .strokeBorder(chosen ? Color.studio(.accent) : .clear, lineWidth: 2.5)
                    .padding(-4)
            }
    }
}

/// A tile dims a little while pressed.
private struct MenuBarLookTileStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label.brightness(configuration.isPressed ? -0.08 : 0)
    }
}

// MARK: - Which LookStore

private struct LookStoreKey: EnvironmentKey {
    static let defaultValue: LookStore? = nil
}

extension EnvironmentValues {
    /// The Looks the panel shows. Nil: the app's own (`LookStore.shared`)
    /// with the app's models, or one kept for isolated models (previews).
    var lunalyrLookStore: LookStore? {
        get { self[LookStoreKey.self] }
        set { self[LookStoreKey.self] = newValue }
    }
}

@MainActor
enum MenuBarLooks {
    /// The models are kept with their store, so their address is never reused.
    private static var isolated: [ObjectIdentifier: (models: LunalyrModels, store: LookStore)] = [:]

    /// `injected`, else the shared store for the app's models, else a store
    /// over the isolated models' settings that never touches the app's
    /// preferences.
    static func store(injected: LookStore?, models: LunalyrModels) -> LookStore {
        if let injected { return injected }
        if models === LunalyrModels.live { return .shared }
        let key = ObjectIdentifier(models)
        if let entry = isolated[key] { return entry.store }
        // A suite of its own, never the app's domain.
        guard let defaults = UserDefaults(suiteName: "LunalyrLooksPreview") else { return .shared }
        let store = LookStore(defaults: defaults, settings: models.settings, highlights: models.highlightColours)
        isolated[key] = (models, store)
        return store
    }
}
