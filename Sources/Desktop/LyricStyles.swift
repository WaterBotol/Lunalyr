import Combine
import SwiftUI

// MARK: - Every monitor's styles at once

/// Every display's look, live together: each display's wallpaper and its
/// desktop lyrics draw in its Desktop slot, its floating lyrics in its
/// Floating slot (`MonitorLooks.swift`), all at the same time.
///
/// The slot Settings is editing (`editingDisplay`, `editing`) is the app's
/// own stores (`Settings.shared`, the colour and highlight stores, the
/// reading and translation switches, the visualiser and desktop options),
/// as before: every page, Look and shortcut keeps changing it in place.
/// Every other slot is a read-only copy (`LyricStyleDefaults`), made the
/// first time a window on a connected display asks for it, that reads its
/// values from where the slot is kept and never writes the preferences.
/// Picking another monitor or style keeps the edited slot, loads the picked
/// one into the app's stores and rereads the copies in place.
///
/// A copy's settings that no look keeps (power, frame rate, timing, the
/// word lists and the song's own typeface...) follow the app's, so a
/// monitor's stores answer every question the app's would.
@MainActor
final class LyricStyles: ObservableObject {
    static let shared = LyricStyles(defaults: .standard, edited: LyricStyle(
        settings: .shared, colours: .shared, highlights: .shared, defaults: .standard,
        readings: .shared, extras: .shared, visualizer: .shared, desktop: .shared),
        connected: LyricStyles.connectedDisplays, shows: { FloatingLyricsOptions.shared.choice(for: $0) })

    /// The slot being edited: the app's own stores.
    let edited: LyricStyle
    /// Which style is being edited.
    @Published private(set) var editing: LyricsSurface
    /// Which monitor is being edited (its display key).
    @Published private(set) var editingDisplay: String
    /// Every monitor draws the edited monitor's look.
    @Published private(set) var sameEverywhere: Bool

    private let defaults: UserDefaults
    private let notifications: NotificationCenter
    private let connected: () -> [String]
    private let shows: (String) -> DisplayLyrics
    private var parkedStyles: [LookSlot: (style: LyricStyle, defaults: LyricStyleDefaults)] = [:]
    private var follows: [LookSlot: AnyCancellable] = [:]

    private var slots: MonitorLookSlots { MonitorLookSlots(defaults: defaults) }
    var editingSlot: LookSlot { LookSlot(display: editingDisplay, surface: editing) }
    /// The main display (with the menu bar), when one is known.
    var mainDisplay: String? { connected().first }

    /// The displays connected now, the main one first; one stand-in display
    /// inside the test host, as Settings draws there.
    static func connectedDisplays() -> [String] {
        AppRuntime.isUnitTesting ? ["test-display"] : FloatingFrame.connectedKeys()
    }

    /// `defaults`: where the looks are kept; `edited`: the stores on those
    /// defaults that Settings changes; `connected`: the displays connected
    /// now, main first; `shows`: what a display shows.
    init(defaults: UserDefaults, edited: LyricStyle, notifications: NotificationCenter = .default,
         connected: @escaping () -> [String] = { [] }, shows: @escaping (String) -> DisplayLyrics = { _ in .fallback }) {
        self.defaults = defaults
        self.edited = edited
        self.notifications = notifications
        self.connected = connected
        self.shows = shows
        let slots = MonitorLookSlots(defaults: defaults)
        let displays = connected()
        let editingSlot = slots.editing(main: displays.first)
        editing = editingSlot.surface
        editingDisplay = editingSlot.display
        sameEverywhere = slots.sameEverywhere
        // The first time monitors have looks, every connected display's
        // starts as today's: nothing changes on screen.
        slots.keepConnected(displays, editing: editingSlot)
    }

    // MARK: Reading

    /// The stores `surface`'s lyrics draw with on `display`; nil surface,
    /// the style being edited; nil display, the monitor being edited.
    func style(for surface: LyricsSurface?, display: String? = nil) -> LyricStyle {
        let slot = LookSlot(display: sameEverywhere ? editingDisplay : display ?? editingDisplay,
                            surface: surface ?? editing)
        if slot == editingSlot { return edited }
        return parkedStyles[slot]?.style ?? park(slot)
    }

    /// The stores `display`'s wallpaper draws with: the app's on the
    /// monitor being edited, whichever of its styles is.
    func wallpaperStyle(for display: String?) -> LyricStyle {
        let display = sameEverywhere ? editingDisplay : display ?? editingDisplay
        return display == editingDisplay ? edited : style(for: .desktop, display: display)
    }

    /// The other style of the monitor being edited.
    var parked: LyricStyle { style(for: editing.other) }

    /// Each connected display's wallpaper settings, each once (the edited
    /// monitor's are the app's); the app's alone with no display known.
    var wallpaperSettings: [Settings] {
        var seen = Set<ObjectIdentifier>()
        let all = connected().map { wallpaperStyle(for: $0).settings }
        let settings = all.isEmpty ? [edited.settings] : all
        return settings.filter { seen.insert(ObjectIdentifier($0)).inserted }
    }

    /// Whether any connected display's wallpaper passes `test`.
    func anyWallpaper(_ test: (Settings) -> Bool) -> Bool { wallpaperSettings.contains(where: test) }

    /// The styles lyrics are drawn in now: each connected display's, as it
    /// shows them, and the edited one (Settings' picture); each once.
    var liveStyles: [LyricStyle] {
        var styles: [LyricStyle] = [edited]
        for display in connected() {
            let choice = shows(display)
            if choice.onDesktop { styles.append(style(for: .desktop, display: display)) }
            if choice.floats { styles.append(style(for: .floating, display: display)) }
        }
        var seen = Set<ObjectIdentifier>()
        return styles.filter { seen.insert(ObjectIdentifier($0)).inserted }
    }

    /// A change to any monitor's wallpaper or style, delivered once it has
    /// happened.
    var changes: AnyPublisher<Void, Never> {
        Publishers.Merge3(objectWillChange.map { _ in () },
                          edited.settings.objectWillChange.map { _ in () },
                          edited.visualizer.objectWillChange.map { _ in () })
            .receive(on: RunLoop.main)
            .eraseToAnyPublisher()
    }

    // MARK: Editing

    /// After Settings swapped the style it edits (`FloatingLyricsOptions.surface`)
    /// or the monitor: the copies take up their slots again.
    func followEditing() {
        let now = slots.editing(main: mainDisplay)
        objectWillChange.send()
        editing = now.surface
        editingDisplay = now.display
        reloadParked()
    }

    /// Settings edits `display`'s look from now on, the same style
    /// (Desktop or Floating) as before. `reloadEdited` makes the app's
    /// stores read what was loaded into them.
    func edit(display: String, reloadEdited: (() -> Void)? = nil) {
        let from = editingSlot
        let to = LookSlot(display: display, surface: editing)
        guard from != to else { return }
        slots.switchEditing(from: from, to: to, main: mainDisplay)
        (reloadEdited ?? reloadEditedStores)()
        followEditing()
    }

    /// The app's stores reread every key a look keeps.
    func reloadEditedStores() {
        edited.reload()
        // The app's Looks: which one is current is kept per style.
        if edited.settings === Settings.shared { LookStore.shared.reloadCurrent() }
    }

    /// Floating lyrics are on for `displays` (nil: the monitor being
    /// edited): from now on each one's Floating style is its own, kept
    /// apart from its Desktop style.
    func keepFloatingStyle(displays: [String]? = nil) {
        var kept = false
        for display in displays ?? [editingDisplay] {
            let slot = LookSlot(display: display, surface: .floating)
            guard slot != editingSlot, slots.saved(slot) == nil else { continue }
            slots.store(slots.style(slot, editing: editingSlot, main: mainDisplay), for: slot)
            kept = true
        }
        if kept { reloadParked() }
    }

    /// Every connected monitor takes the edited monitor's look: its
    /// wallpaper, its Desktop style and its Floating style.
    func copyToAllMonitors() {
        slots.copyEdited(editingSlot, to: connected(), main: mainDisplay)
        reloadParked()
    }

    /// Turns "Same on every monitor" on or off. Off, every monitor keeps
    /// the look it was showing, the edited one's.
    func setSameEverywhere(_ on: Bool) {
        guard on != sameEverywhere else { return }
        if !on { slots.copyEdited(editingSlot, to: connected(), main: mainDisplay) }
        defaults.set(on, forKey: MonitorLookSlots.sameEverywhereKey)
        objectWillChange.send()
        sameEverywhere = on
        reloadParked()
    }

    /// After displays came or went: a display seen for the first time
    /// starts as a copy of the main display's look, copies for displays
    /// gone are let go, and when the edited monitor is gone Settings edits
    /// the main display's look.
    func displaysChanged(reloadEdited: (() -> Void)? = nil) {
        let displays = connected()
        guard let main = displays.first else { return }
        slots.keepConnected(displays, editing: editingSlot)
        let gone = parkedStyles.keys.filter { !displays.contains($0.display) }
        for slot in gone {
            parkedStyles[slot] = nil
            follows[slot] = nil
        }
        if !displays.contains(editingDisplay) {
            edit(display: main, reloadEdited: reloadEdited)
        } else if !gone.isEmpty {
            objectWillChange.send()
        }
    }

    /// Rereads every copy from where its slot is kept.
    func reloadParked() {
        for (slot, parked) in parkedStyles {
            guard slot != editingSlot else {
                parkedStyles[slot] = nil
                follows[slot] = nil
                continue
            }
            parked.defaults.load(slots.drawn(slot, editing: editingSlot, main: mainDisplay))
            parked.style.reload()
        }
        objectWillChange.send()
    }

    // MARK: Copies

    /// A read-only copy of `slot`, made on first use. Called while views
    /// draw, so it publishes nothing.
    private func park(_ slot: LookSlot) -> LyricStyle {
        let parkedDefaults = LyricStyleDefaults(base: defaults, styleKeys: Set(MonitorLookKeys.all))
        parkedDefaults.load(slots.drawn(slot, editing: editingSlot, main: mainDisplay))
        let style = LyricStyle(settings: Settings(defaults: parkedDefaults, notifications: notifications),
                               colours: LyricColourOptions(defaults: parkedDefaults),
                               highlights: LyricHighlightOptions(defaults: parkedDefaults), defaults: parkedDefaults)
        parkedStyles[slot] = (style, parkedDefaults)
        follow(slot, style)
        return style
    }

    /// A copy takes on the app's settings that no look keeps, now and
    /// whenever they change.
    private func follow(_ slot: LookSlot, _ copy: LyricStyle) {
        let source = edited
        copy.follow(appSettingsOf: source)
        follows[slot] = Publishers.Merge3(source.settings.objectWillChange.map { _ in () },
                                          source.visualizer.objectWillChange.map { _ in () },
                                          source.desktop.objectWillChange.map { _ in () })
            .receive(on: RunLoop.main)
            .sink { [weak copy, weak source] in
                guard let copy, let source else { return }
                copy.follow(appSettingsOf: source)
            }
    }
}

/// One lyric style's stores, and its switches that live outside them.
@MainActor
final class LyricStyle {
    let settings: Settings
    let colours: LyricColourOptions
    let highlights: LyricHighlightOptions
    /// The visualiser scenes' choices and the desktop's readability, kept
    /// with the display's wallpaper.
    let visualizer: VisualizerPreferences
    let desktop: DesktopOptions
    /// Where this style's values are read: the app's preferences for the
    /// style being edited, else `LyricStyleDefaults`.
    let defaults: UserDefaults
    /// The app's reading and translation stores, for the style being
    /// edited; nil reads those switches from `defaults`.
    private let readings: RomanizationOptions?
    private let extras: LunalyrExtras?

    init(settings: Settings, colours: LyricColourOptions, highlights: LyricHighlightOptions, defaults: UserDefaults,
         readings: RomanizationOptions? = nil, extras: LunalyrExtras? = nil,
         visualizer: VisualizerPreferences? = nil, desktop: DesktopOptions? = nil) {
        self.settings = settings
        self.colours = colours
        self.highlights = highlights
        self.defaults = defaults
        self.readings = readings
        self.extras = extras
        self.visualizer = visualizer ?? VisualizerPreferences(defaults: defaults)
        self.desktop = desktop ?? DesktopOptions(defaults: defaults)
    }

    /// Rereads every key a look keeps, after its preferences changed under it.
    func reload() {
        settings.reloadLyricStyle()
        settings.reloadWallpaper()
        colours.reload()
        highlights.reload()
        readings?.reloadDisplay()
        extras?.reloadTranslations()
        visualizer.reload()
        desktop.reload()
    }

    /// Takes on `source`'s settings that no look keeps.
    func follow(appSettingsOf source: LyricStyle) {
        settings.follow(appSettingsOf: source.settings)
        visualizer.follow(appPreferencesOf: source.visualizer)
        desktop.follow(appOptionsOf: source.desktop)
    }

    /// Readings: off, below the lyrics or in their place.
    var readingDisplay: RomanizationOptions.Display {
        readings?.display ?? RomanizationOptions.Display(rawValue: defaults.string(forKey: RomanizationOptions.displayKey) ?? "")
            ?? .below
    }
    /// The reading options as the lyrics are read with this style.
    func readingSnapshot(_ base: RomanizationOptions.Snapshot) -> RomanizationOptions.Snapshot {
        var snapshot = base
        snapshot.display = readingDisplay
        return snapshot
    }
    var showsTranslations: Bool {
        extras?.showsTranslations ?? (defaults.object(forKey: LunalyrExtras.Keys.translations) as? Bool ?? true)
    }
    var backingVocals: Bool { BackingVocals.isOn(in: defaults) }
    var backingStyle: LyricComposition? { BackingVocals.chosenStyle(in: defaults) }
    var creditCues: Bool { LyricCreditCues.isOn(in: defaults) }
    var openingTitle: Bool { OpeningTitle.isOn(in: defaults) }
    var openingTitleCover: Bool { OpeningTitle.showsCover(in: defaults) }
}

/// A lyric style that is not being edited, as preferences: its values in
/// memory, loaded from where the style is kept; every other key read from
/// `base` and never written. `Settings` and the colour stores run on it as
/// on any preferences.
final class LyricStyleDefaults: UserDefaults, @unchecked Sendable {
    private let base: UserDefaults
    private let styleKeys: Set<String>
    private var values: [String: Any] = [:]

    init(base: UserDefaults, styleKeys: Set<String>) {
        self.base = base
        self.styleKeys = styleKeys
        super.init(suiteName: nil)!
    }

    func load(_ style: [String: Any]) {
        values = style.filter { styleKeys.contains($0.key) }
    }

    override func object(forKey key: String) -> Any? {
        styleKeys.contains(key) ? values[key] : base.object(forKey: key)
    }

    override func set(_ value: Any?, forKey key: String) {
        guard styleKeys.contains(key) else { return }
        values[key] = value
    }

    override func removeObject(forKey key: String) {
        guard styleKeys.contains(key) else { return }
        values[key] = nil
    }
}

// MARK: - In the views

private struct LyricSurfaceKey: EnvironmentKey {
    /// nil: the style being edited, as every lyric view read before both
    /// styles were live (lyric cards, onboarding, Settings' samples).
    static let defaultValue: LyricsSurface? = nil
}

private struct LyricDisplayKey: EnvironmentKey {
    /// nil: the monitor being edited (Settings' picture, lyric cards).
    static let defaultValue: String? = nil
}

private struct LyricStylesKey: EnvironmentKey {
    /// nil reads as `LyricStyles.shared`.
    static let defaultValue: LyricStyles? = nil
}

extension EnvironmentValues {
    /// Which style the lyrics below draw in: the wallpaper's Desktop, the
    /// floating windows' Floating; unset, the one being edited.
    var lyricSurface: LyricsSurface? {
        get { self[LyricSurfaceKey.self] }
        set { self[LyricSurfaceKey.self] = newValue }
    }

    /// Which display the views below are on (`FloatingFrame.displayKey`),
    /// so they draw in that monitor's look; unset, the monitor being edited.
    var lyricDisplay: String? {
        get { self[LyricDisplayKey.self] }
        set { self[LyricDisplayKey.self] = newValue }
    }

    /// The styles the lyrics below read; unset in the app (`shared`).
    var lyricStyles: LyricStyles? {
        get { self[LyricStylesKey.self] }
        set { self[LyricStylesKey.self] = newValue }
    }
}

/// The stores this view draws with, observed: a change to them, or a swap
/// of what Settings edits, draws the view again.
/// `@SurfaceStyle private var style`: the lyric style of `lyricSurface` on
/// `lyricDisplay`, then `style.settings`, `style.colours`.
/// `@SurfaceStyle(.wallpaper) private var wallpaper`: that display's
/// wallpaper, whichever lyric style is drawn.
@propertyWrapper
struct SurfaceStyle: DynamicProperty {
    enum Role { case lyrics, wallpaper }

    @Environment(\.lyricSurface) private var surface
    @Environment(\.lyricDisplay) private var display
    @Environment(\.lyricStyles) private var styles
    @StateObject private var relay = LyricStyleRelay()
    private let role: Role

    @MainActor init(_ role: Role = .lyrics) { self.role = role }

    @MainActor var wrappedValue: LyricStyle { Self.resolve(styles ?? .shared, role: role, surface: surface, display: display) }

    @MainActor static func resolve(_ styles: LyricStyles, role: Role, surface: LyricsSurface?, display: String?) -> LyricStyle {
        role == .wallpaper ? styles.wallpaperStyle(for: display) : styles.style(for: surface, display: display)
    }

    func update() {
        MainActor.assumeIsolated {
            let styles = styles ?? .shared
            relay.follow(styles, style: Self.resolve(styles, role: role, surface: surface, display: display))
        }
    }
}

/// Forwards the followed style's changes to the view that owns it.
@MainActor
final class LyricStyleRelay: ObservableObject {
    private var followed: (styles: ObjectIdentifier, style: ObjectIdentifier)?
    private var subscriptions: [AnyCancellable] = []

    func follow(_ styles: LyricStyles, style: LyricStyle) {
        let ids = (ObjectIdentifier(styles), ObjectIdentifier(style))
        guard followed?.styles != ids.0 || followed?.style != ids.1 else { return }
        followed = ids
        let send: () -> Void = { [weak self] in self?.objectWillChange.send() }
        subscriptions = [
            styles.objectWillChange.sink { _ in send() },
            style.settings.objectWillChange.sink { _ in send() },
            style.colours.objectWillChange.sink { _ in send() },
            style.highlights.objectWillChange.sink { _ in send() },
            style.visualizer.objectWillChange.sink { _ in send() },
            style.desktop.objectWillChange.sink { _ in send() },
        ]
    }
}
