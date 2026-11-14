import Combine
import SwiftUI

/// The Looks: the built-ins, the user's own, and which one is in use.
///
/// Until a Look is chosen `current` is nil and nothing here writes a
/// setting, so the app behaves exactly as without Looks. A Look is applied
/// through the same setters the Settings window uses, so fonts load and
/// Music video remembers its fallback as usual. Whether the current Look has
/// been edited is worked out from the live settings each time it is read, so
/// a change made anywhere -- Settings, shortcuts, a floating/desktop style
/// swap -- shows at once.
@MainActor
final class LookStore: ObservableObject {
    static let shared = LookStore(defaults: .standard, settings: .shared, highlights: .shared)

    enum Keys {
        static let saved = "custom.looks.saved"
        static let current = "custom.looks.current"
    }

    /// Built-ins first, then the user's Looks in the order saved.
    var looks: [Look] { Look.builtIns + userLooks }
    @Published private(set) var userLooks: [Look]
    /// The id of the Look last chosen or saved, or nil.
    @Published private(set) var current: Look.ID?

    private let defaults: UserDefaults
    private let settings: Settings
    private let highlights: LyricHighlightOptions
    private var observers: Set<AnyCancellable> = []

    init(defaults: UserDefaults = .standard, settings: Settings, highlights: LyricHighlightOptions) {
        self.defaults = defaults
        self.settings = settings
        self.highlights = highlights
        let stored = defaults.data(forKey: Keys.saved).flatMap { try? JSONDecoder().decode([StoredLook].self, from: $0) }
        userLooks = (stored ?? []).compactMap(\.look)
        // A current id whose Look is gone means no Look.
        let known = Set(Look.builtIns.map(\.id)).union(userLooks.map(\.id))
        let savedCurrent = defaults.string(forKey: Keys.current)
        current = savedCurrent.flatMap { known.contains($0) ? $0 : nil }
        observeCoveredSettings()
    }

    // MARK: State

    var currentLook: Look? { current.flatMap(look(id:)) }

    func look(id: Look.ID) -> Look? { looks.first { $0.id == id } }

    /// Whether anything the current Look covers has changed since it was applied.
    var isEdited: Bool {
        guard let look = currentLook else { return false }
        return liveValues != look.values
    }

    /// "Matched", "Matched, edited", or "Custom" with no Look chosen.
    var displayName: String { Self.displayName(for: currentLook, isEdited: isEdited) }

    static func displayName(for look: Look?, isEdited: Bool) -> String {
        guard let look else { return "Custom" }
        return isEdited ? "\(look.name), edited" : look.name
    }

    /// The covered settings as they are now.
    var liveValues: LookValues {
        let layout = settings.lyricComposition
        let key = layout?.rawValue ?? "automatic"
        var entrance = settings.wordArrivalByStyle[key].flatMap(WordArrival.init(rawValue:))
        // A chosen layout with no entry of its own uses Match the style.
        if entrance == nil, layout != nil { entrance = .automatic }
        return LookValues(wallpaper: settings.backgroundMode, layout: layout, typeface: settings.fontChoice,
                          fontFamily: settings.googleFontFamily, entrance: entrance,
                          entranceMix: settings.wordEntranceMix, highlight: highlights.mode,
                          highlightCustomHex: highlights.customHex)
    }

    // MARK: Actions

    /// Applies `look` and makes it current. With an undo manager the whole
    /// change, and the previous choice of Look, undoes as one step.
    func apply(_ look: Look, undoManager: UndoManager? = nil) {
        let before = Snapshot(self)
        write(look.values)
        setCurrent(look.id)
        guard let undoManager else { return }
        let after = Snapshot(self)
        undoManager.registerSettingsChange("Choose Look", undo: { [weak self] in self?.restore(before) },
                                           redo: { [weak self] in self?.restore(after) })
    }

    /// Stores the current settings as a new user Look, which becomes current.
    /// A blank name becomes "My Look"; a name in use gets a number.
    @discardableResult
    func saveCurrent(as name: String) -> Look {
        let look = Look(id: UUID().uuidString, name: uniqueName(name), isBuiltIn: false, values: liveValues)
        userLooks.append(look)
        persistLooks()
        setCurrent(look.id)
        return look
    }

    /// Renames a user Look. Built-ins and blank names are refused.
    @discardableResult
    func rename(_ id: Look.ID, to name: String) -> Bool {
        guard let index = userLooks.firstIndex(where: { $0.id == id }) else { return false }
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return false }
        if trimmed != userLooks[index].name { userLooks[index].name = uniqueName(trimmed, excluding: id) }
        persistLooks()
        return true
    }

    /// Deletes a user Look; built-ins can't be deleted. Settings are left as
    /// they are -- when it was current, no Look is current any more.
    @discardableResult
    func delete(_ id: Look.ID) -> Bool {
        guard let index = userLooks.firstIndex(where: { $0.id == id }) else { return false }
        userLooks.remove(at: index)
        persistLooks()
        if current == id { setCurrent(nil) }
        return true
    }

    // MARK: Writing

    /// Sets each covered setting that differs, through the UI's setters.
    private func write(_ values: LookValues) {
        if settings.backgroundMode != values.wallpaper { settings.backgroundMode = values.wallpaper }
        if settings.lyricComposition != values.layout { settings.lyricComposition = values.layout }
        if values.typeface == .google {
            let family = values.fontFamily ?? ""
            if settings.fontChoice != .google || settings.googleFontFamily != family {
                if family.isEmpty { settings.selectFontChoice(.google) } else { settings.applyGoogleFont(family: family) }
            }
        } else if settings.fontChoice != values.typeface {
            settings.selectFontChoice(values.typeface)
        }
        let key = values.entranceKey
        if let entrance = values.entrance {
            if settings.wordArrivalByStyle[key] != entrance.rawValue {
                settings.wordArrivalByStyle[key] = entrance.rawValue
            }
            if entrance == .mix, let mix = values.entranceMix, settings.wordEntranceMix != mix {
                settings.wordEntranceMix = mix
            }
        } else if settings.wordArrivalByStyle[key] != nil {
            settings.wordArrivalByStyle[key] = nil
        }
        if highlights.mode != values.highlight { highlights.mode = values.highlight }
        if values.highlight == .custom, let hex = values.highlightCustomHex, highlights.customHex != hex,
           let colour = Self.colour(hex: hex) {
            highlights.setCustom(colour)
        }
    }

    private static func colour(hex: String) -> Color? {
        var text = hex
        if text.hasPrefix("#") { text.removeFirst() }
        guard text.count == 6, let value = UInt32(text, radix: 16) else { return nil }
        let red = Double((value >> 16) & 0xFF) / 255
        let green = Double((value >> 8) & 0xFF) / 255
        let blue = Double(value & 0xFF) / 255
        return Color(.sRGB, red: red, green: green, blue: blue)
    }

    private func setCurrent(_ id: Look.ID?) {
        current = id
        if let id { defaults.set(id, forKey: Keys.current) } else { defaults.removeObject(forKey: Keys.current) }
    }

    private func persistLooks() {
        guard let data = try? JSONEncoder().encode(userLooks.map(StoredLook.init)) else { return }
        defaults.set(data, forKey: Keys.saved)
    }

    private func uniqueName(_ raw: String, excluding id: Look.ID? = nil) -> String {
        let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        let base = trimmed.isEmpty ? "My Look" : trimmed
        let taken = Set(looks.filter { $0.id != id }.map { $0.name.lowercased() })
        guard taken.contains(base.lowercased()) else { return base }
        var number = 2
        while taken.contains("\(base) \(number)".lowercased()) { number += 1 }
        return "\(base) \(number)"
    }

    // MARK: Undo

    /// Everything `apply` can change, whole, so undo puts back entries for
    /// other layouts and Music video's fallback too.
    private struct Snapshot {
        let backgroundMode: Settings.BackgroundMode
        let musicVideoFallback: Settings.BackgroundMode
        let layout: LyricComposition?
        let fontChoice: Settings.FontChoice
        let fontFamily: String
        let arrivals: [String: String]
        let mix: [String]
        let highlight: LyricHighlightMode
        let highlightHex: String
        let current: Look.ID?

        @MainActor init(_ store: LookStore) {
            let s = store.settings
            backgroundMode = s.backgroundMode
            musicVideoFallback = s.musicVideoFallback
            layout = s.lyricComposition
            fontChoice = s.fontChoice
            fontFamily = s.googleFontFamily
            arrivals = s.wordArrivalByStyle
            mix = s.wordEntranceMix
            highlight = store.highlights.mode
            highlightHex = store.highlights.customHex
            current = store.current
        }
    }

    private func restore(_ snapshot: Snapshot) {
        if settings.backgroundMode != snapshot.backgroundMode { settings.backgroundMode = snapshot.backgroundMode }
        if settings.musicVideoFallback != snapshot.musicVideoFallback {
            settings.musicVideoFallback = snapshot.musicVideoFallback
        }
        if settings.lyricComposition != snapshot.layout { settings.lyricComposition = snapshot.layout }
        if snapshot.fontChoice == .google {
            if settings.fontChoice != .google || settings.googleFontFamily != snapshot.fontFamily {
                settings.applyGoogleFont(family: snapshot.fontFamily)
            }
        } else {
            if settings.googleFontFamily != snapshot.fontFamily { settings.googleFontFamily = snapshot.fontFamily }
            if settings.fontChoice != snapshot.fontChoice { settings.selectFontChoice(snapshot.fontChoice) }
        }
        if settings.wordArrivalByStyle != snapshot.arrivals { settings.wordArrivalByStyle = snapshot.arrivals }
        if settings.wordEntranceMix != snapshot.mix { settings.wordEntranceMix = snapshot.mix }
        if highlights.mode != snapshot.highlight { highlights.mode = snapshot.highlight }
        if highlights.customHex != snapshot.highlightHex, let colour = Self.colour(hex: snapshot.highlightHex) {
            highlights.setCustom(colour)
        }
        setCurrent(snapshot.current)
    }

    // MARK: Observation

    /// Republishes when a covered setting changes, so views showing
    /// `isEdited` or `displayName` redraw; uncovered settings don't.
    private func observeCoveredSettings() {
        let changes: [AnyPublisher<Void, Never>] = [
            settings.$backgroundMode.map { _ in () }.eraseToAnyPublisher(),
            settings.$lyricComposition.map { _ in () }.eraseToAnyPublisher(),
            settings.$fontChoice.map { _ in () }.eraseToAnyPublisher(),
            settings.$googleFontFamily.map { _ in () }.eraseToAnyPublisher(),
            settings.$wordArrivalByStyle.map { _ in () }.eraseToAnyPublisher(),
            settings.$wordEntranceMix.map { _ in () }.eraseToAnyPublisher(),
            highlights.$mode.map { _ in () }.eraseToAnyPublisher(),
            highlights.$customHex.map { _ in () }.eraseToAnyPublisher(),
        ]
        Publishers.MergeMany(changes)
            .dropFirst(changes.count)
            .sink { [weak self] in self?.objectWillChange.send() }
            .store(in: &observers)
    }
}
