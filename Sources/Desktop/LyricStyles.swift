import Combine
import SwiftUI

// MARK: - Both styles at once

/// The two lyric styles, live together: the desktop lyrics draw in the
/// Desktop style and the floating lyrics in the Floating style, on the same
/// display or on different ones, at the same time.
///
/// The style Settings is editing (`FloatingLyricsOptions.surface`) is the
/// app's own stores (`Settings.shared`, the colour and highlight stores, the
/// reading and translation switches), as before: every page, Look and
/// shortcut keeps changing it in place. The other style is a second set of
/// stores, `parked`, that reads its values from where that style is kept
/// (`LyricStyleProfiles.storeKey`) and never writes the preferences.
/// Switching the style being edited swaps the two in `LyricStyleProfiles`
/// and reloads both, so `style(for:)` may hand a surface the other set.
///
/// Only the style is kept twice. The parked set's other settings are not
/// followed, so the lyric views read those (scrim, music video, lyric
/// offset, No lyrics) from `Settings.shared`; the word lists and the song's
/// own typeface, which a style's reading of them needs, are copied across.
@MainActor
final class LyricStyles: ObservableObject {
    static let shared = LyricStyles(defaults: .standard, edited: LyricStyle(
        settings: .shared, colours: .shared, highlights: .shared, defaults: .standard,
        readings: .shared, extras: .shared))

    /// The style being edited: the app's own stores.
    let edited: LyricStyle
    /// The other style.
    let parked: LyricStyle
    /// Which style is being edited.
    @Published private(set) var editing: LyricsSurface

    private let defaults: UserDefaults
    private let parkedDefaults: LyricStyleDefaults
    private var mirrors: Set<AnyCancellable> = []

    /// `defaults`: where the styles are kept; `edited`: the stores on those
    /// defaults that Settings changes.
    init(defaults: UserDefaults, edited: LyricStyle, notifications: NotificationCenter = .default) {
        self.defaults = defaults
        self.edited = edited
        let editing = LyricsSurface(rawValue: defaults.string(forKey: FloatingLyricsOptions.Keys.surface) ?? "") ?? .desktop
        self.editing = editing
        parkedDefaults = LyricStyleDefaults(base: defaults, styleKeys: Set(LyricStyleProfiles.keys))
        parkedDefaults.load(LyricStyleProfiles(defaults: defaults).ensureSaved(editing.other))
        parked = LyricStyle(settings: Settings(defaults: parkedDefaults, notifications: notifications),
                            colours: LyricColourOptions(defaults: parkedDefaults),
                            highlights: LyricHighlightOptions(defaults: parkedDefaults), defaults: parkedDefaults)
        mirror()
    }

    /// The stores a surface draws with.
    func style(for surface: LyricsSurface) -> LyricStyle { surface == editing ? edited : parked }

    /// After Settings swapped the style it edits (`LyricStyleProfiles.switchSurface`):
    /// the parked stores take up the style that was being edited.
    func followEditing() {
        let now = LyricsSurface(rawValue: defaults.string(forKey: FloatingLyricsOptions.Keys.surface) ?? "") ?? .desktop
        objectWillChange.send()
        editing = now
        reloadParked()
    }

    /// Rereads the parked style from where it is kept.
    func reloadParked() {
        parkedDefaults.load(LyricStyleProfiles(defaults: defaults).ensureSaved(editing.other))
        parked.settings.reloadLyricStyle()
        parked.colours.reload()
        parked.highlights.reload()
        objectWillChange.send()
    }

    /// What a style's look depends on that is not part of it: the word
    /// lists (the switches are per style, the lists shared) and the
    /// typeface remembered for the playing song.
    private func mirror() {
        let source = edited.settings, copy = parked.settings
        copy.wordSymbolList = source.wordSymbolList
        copy.actionWordList = source.actionWordList
        copy.applySongFont(source.songFont)
        // Published before the change: read the new value from the event.
        source.$wordSymbolList.removeDuplicates().sink { [weak copy] in copy?.wordSymbolList = $0 }.store(in: &mirrors)
        source.$actionWordList.removeDuplicates().sink { [weak copy] in copy?.actionWordList = $0 }.store(in: &mirrors)
        source.$songFont.removeDuplicates().sink { [weak copy] in copy?.applySongFont($0) }.store(in: &mirrors)
    }
}

/// One lyric style's stores, and its switches that live outside them.
@MainActor
final class LyricStyle {
    let settings: Settings
    let colours: LyricColourOptions
    let highlights: LyricHighlightOptions
    /// Where this style's values are read: the app's preferences for the
    /// style being edited, else `LyricStyleDefaults`.
    let defaults: UserDefaults
    /// The app's reading and translation stores, for the style being
    /// edited; nil reads those switches from `defaults`.
    private let readings: RomanizationOptions?
    private let extras: LunalyrExtras?

    init(settings: Settings, colours: LyricColourOptions, highlights: LyricHighlightOptions, defaults: UserDefaults,
         readings: RomanizationOptions? = nil, extras: LunalyrExtras? = nil) {
        self.settings = settings
        self.colours = colours
        self.highlights = highlights
        self.defaults = defaults
        self.readings = readings
        self.extras = extras
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
    static let defaultValue: LyricsSurface = .desktop
}

private struct LyricStylesKey: EnvironmentKey {
    /// nil reads as `LyricStyles.shared`.
    static let defaultValue: LyricStyles? = nil
}

extension EnvironmentValues {
    /// Which style the lyrics below draw in: the wallpaper's Desktop, the
    /// floating windows' Floating, the Settings preview the one being edited.
    var lyricSurface: LyricsSurface {
        get { self[LyricSurfaceKey.self] }
        set { self[LyricSurfaceKey.self] = newValue }
    }

    /// The styles the lyrics below read; unset in the app (`shared`).
    var lyricStyles: LyricStyles? {
        get { self[LyricStylesKey.self] }
        set { self[LyricStylesKey.self] = newValue }
    }
}

/// The lyric style this view draws in (`lyricSurface`), observed: a change
/// to it, or a swap of the style being edited, draws the view again.
/// `@SurfaceStyle private var style`, then `style.settings`, `style.colours`.
@propertyWrapper
struct SurfaceStyle: DynamicProperty {
    @Environment(\.lyricSurface) private var surface
    @Environment(\.lyricStyles) private var styles
    @StateObject private var relay = LyricStyleRelay()

    @MainActor init() {}

    @MainActor var wrappedValue: LyricStyle { (styles ?? .shared).style(for: surface) }

    func update() {
        MainActor.assumeIsolated { relay.follow(styles ?? .shared, surface: surface) }
    }
}

/// Forwards the followed style's changes to the view that owns it.
@MainActor
final class LyricStyleRelay: ObservableObject {
    private var followed: (styles: ObjectIdentifier, style: ObjectIdentifier)?
    private var subscriptions: [AnyCancellable] = []

    func follow(_ styles: LyricStyles, surface: LyricsSurface) {
        let style = styles.style(for: surface)
        let ids = (ObjectIdentifier(styles), ObjectIdentifier(style))
        guard followed?.styles != ids.0 || followed?.style != ids.1 else { return }
        followed = ids
        let send: () -> Void = { [weak self] in self?.objectWillChange.send() }
        subscriptions = [
            styles.objectWillChange.sink { _ in send() },
            style.settings.objectWillChange.sink { _ in send() },
            style.colours.objectWillChange.sink { _ in send() },
            style.highlights.objectWillChange.sink { _ in send() },
        ]
    }
}
