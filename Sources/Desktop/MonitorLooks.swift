import Foundation

// MARK: - Each monitor's look
//
// Every connected display has a look of its own: its wallpaper (type,
// scene, adjustments, animated covers) and two lyric styles, Desktop for the
// lyrics on its wallpaper and Floating for its floating window. A slot is
// one display's style for one surface; the wallpaper is kept with the
// display's Desktop slot.
//
// Settings edits one slot at a time, in the app's own stores (the
// top-level preferences: `Settings.shared`, the colour, highlight, reading,
// visualiser and desktop stores). Every other slot is kept under
// `storeKey(_:)` and drawn from a read-only copy (`LyricStyles`). Picking
// another monitor or style stores the edited slot and loads the picked one
// into the app's stores (`MonitorLookSlots.switchEditing`).

/// One display's lyric style for one surface.
struct LookSlot: Hashable, CustomStringConvertible {
    var display: String
    var surface: LyricsSurface

    var description: String { "\(display).\(surface.rawValue)" }
}

/// Which preferences belong to a monitor's look.
@MainActor
enum MonitorLookKeys {
    /// Kept per slot: the lyric style (layout, typeface, colours,
    /// highlights, readings and translations shown, entrances, the orb,
    /// word symbols and action words, the title card...) and the Look
    /// last applied to it.
    static let style: [String] = LyricStyleProfiles.keys + [LookStore.Keys.current]

    /// Kept per display, with its Desktop slot: the wallpaper and its
    /// options, the animated covers, and how the desktop lyrics stay
    /// readable over it.
    static let wallpaper: [String] = [
        "backgroundMode", "custom.musicVideoFallback", "shaderStyle", "patternStyle", "custom.albumGradientWallpaper",
        "custom.visualizerStyle", "custom.wavefieldPreset", "custom.wavefieldProjection", "custom.light3DColours",
        "custom.wallpaperPath", "custom.wallpaperName", "custom.wallpaperPlacement",
        MusicVideoFraming.youTubeKey, MusicVideoFraming.libraryKey, "custom.musicVideoDim", "custom.musicVideoBlur",
        "effectSpeed", "vividness", "scrimStrength", "patternOpacity", "motionBlur",
        "useMotionArtwork", "motionPlacement",
    ] + VisualizerPreferences.lookKeys + DesktopOptions.lookKeys

    /// Every key a slot can hold.
    static let all: [String] = style + wallpaper

    /// The keys `surface`'s slot holds.
    static func keys(_ surface: LyricsSurface) -> [String] { surface == .desktop ? all : style }

    /// Settings on the Look pages that every monitor shares, and why.
    static let shared: [String: String] = [
        FloatingLyricsOptions.Keys.surface: "Which style Settings edits, not part of a style.",
        MonitorLookSlots.editingKey: "Which monitor Settings edits, not part of a look.",
        MonitorLookSlots.sameEverywhereKey: "Whether every monitor shows one look, so not one monitor's.",
        "custom.look.": "Where every monitor's looks are kept.",
        FloatingLyricsOptions.Keys.displays: "What each display shows is already kept per display.",
        FloatingLyricsOptions.Keys.textSize: "The floating window's own size, with its frames per display.",
        FloatingLyricsOptions.Keys.placement: "The floating window's place, already kept per display.",
        FloatingLyricsOptions.Keys.placements: "The floating window's place, already kept per display.",
        FloatingLyricsOptions.Keys.frames: "The floating window's frames, already kept per display.",
        FloatingLyricsOptions.Keys.sizes: "The floating window's sizes, already kept per display.",
        FloatingLyricsOptions.Keys.backdrop: "The floating window's own shade.",
        FloatingLyricsOptions.Keys.fades: "The floating window's own pointer behaviour.",
        FloatingLyricsOptions.Keys.titleCard: "The floating title card's window, placed per display.",
        FloatingLyricsOptions.Keys.titleFrames: "The floating title card's frames, already kept per display.",
        FloatingLyricsOptions.Keys.titleStart: "Where the floating title card starts.",
        OpeningTitle.alwaysKey: "The title card's place and strength are kept app-wide.",
        OpeningTitle.strengthKey: "The title card's place and strength are kept app-wide.",
        "custom.openingTitleLeft": "The title card's place is kept app-wide.",
        "custom.openingTitleLift": "The title card's place is kept app-wide.",
        "custom.openingTitleScale": "The title card's place is kept app-wide.",
        "custom.rotateLayoutCounts": "The rotation plays through the songs, whichever monitor shows them.",
        "custom.styleMemory.enabled": "Styles remembered per song and artist apply wherever the song shows.",
        "custom.styleMemory.songs": "Styles remembered per song and artist apply wherever the song shows.",
        "custom.styleMemory.artists": "Styles remembered per song and artist apply wherever the song shows.",
        Settings.wordSymbolListKey: "The word lists are shared; each style switches them on or off.",
        Settings.actionWordListKey: "The word lists are shared; each style switches them on or off.",
        "motionExclusions": "Albums left out of animated covers, whichever monitor plays them.",
        "custom.musicVideoOnline": "Finding videos is done once for every monitor.",
        "custom.musicVideoLinks": "A song's video is the same on every monitor.",
        "custom.musicVideoOffsets": "A video's timing is the same on every monitor.",
        "custom.musicVideoOffsetVideos": "A video's timing is the same on every monitor.",
        MusicVideoDisplayChoice.key: "The video plays on the displays chosen here.",
        VisualizerPreferences.Key.heavyScenesOnMainDisplay: "Chooses between displays, so not one monitor's.",
        VisualizerPreferences.Key.punch: "Every display draws the scenes from one shared simulation.",
        VisualizerPreferences.Key.starTrailsColours: "Every display draws Star Trails from one shared simulation.",
        "custom.audioReactiveEnabled": "The music is listened to once for every monitor.",
        "custom.audioSensitivity": "The music is listened to once for every monitor.",
        "custom.audioCaptureMethod": "The music is listened to once for every monitor.",
    ]
}

/// Where each monitor's look is kept, and the swap that moves Settings
/// from one slot to another. Pure preferences, so tests use a throwaway
/// suite.
@MainActor
struct MonitorLookSlots {
    /// A slot's look while Settings is not editing it.
    static func storeKey(_ slot: LookSlot) -> String { "custom.look.\(slot.display).\(slot.surface.rawValue)" }
    /// The monitor Settings edits (its display key).
    static let editingKey = "custom.lookMonitor"
    /// Every monitor draws the edited monitor's look.
    static let sameEverywhereKey = "custom.lookSameEverywhere"
    /// A display key while no display is known at all (a headless start).
    static let fallbackDisplay = "main"

    let defaults: UserDefaults

    var editingDisplay: String? { defaults.string(forKey: Self.editingKey) }
    var editingSurface: LyricsSurface {
        LyricsSurface(rawValue: defaults.string(forKey: FloatingLyricsOptions.Keys.surface) ?? "") ?? .desktop
    }
    var sameEverywhere: Bool { defaults.bool(forKey: Self.sameEverywhereKey) }

    /// The slot Settings edits; `main` when no monitor was picked yet.
    func editing(main: String?) -> LookSlot {
        LookSlot(display: editingDisplay ?? main ?? Self.fallbackDisplay, surface: editingSurface)
    }

    /// `keys` as the app's stores hold them now.
    func top(_ keys: [String]) -> [String: Any] {
        var values: [String: Any] = [:]
        for key in keys { if let value = defaults.object(forKey: key) { values[key] = value } }
        return values
    }

    func saved(_ slot: LookSlot) -> [String: Any]? { defaults.dictionary(forKey: Self.storeKey(slot)) }

    func store(_ values: [String: Any], for slot: LookSlot) {
        let keys = Set(MonitorLookKeys.keys(slot.surface))
        defaults.set(values.filter { keys.contains($0.key) }, forKey: Self.storeKey(slot))
    }

    private static func only(_ keys: [String], of values: [String: Any]) -> [String: Any] {
        let wanted = Set(keys)
        return values.filter { wanted.contains($0.key) }
    }

    /// A slot's lyric style. The edited slot's is the app's stores; a kept
    /// slot's is its own; a display never seen before copies the main
    /// display's; the main display, before monitors had looks, has today's
    /// styles (`custom.lyricStyle.<surface>`, read for one release). A
    /// Floating style never kept starts from its display's Desktop style,
    /// less its size, and is kept only once floating lyrics are used there.
    func style(_ slot: LookSlot, editing: LookSlot?, main: String?) -> [String: Any] {
        let keys = MonitorLookKeys.style
        if slot == editing { return top(keys) }
        if let saved = saved(slot) { return Self.only(keys, of: saved) }
        let main = main ?? slot.display
        let mainSlot = LookSlot(display: main, surface: slot.surface)
        switch slot.surface {
        case .floating:
            if slot.display != main, mainSlot == editing || saved(mainSlot) != nil {
                return style(mainSlot, editing: editing, main: main)
            }
            if slot.display == main, let legacy = LyricStyleProfiles(defaults: defaults).saved(.floating) {
                return Self.only(keys, of: legacy)
            }
            return style(LookSlot(display: slot.display, surface: .desktop), editing: editing, main: main)
                .merging(LyricStyleProfiles.floatingSeed) { _, seed in seed }
        case .desktop:
            if slot.display != main { return style(mainSlot, editing: editing, main: main) }
            if let legacy = LyricStyleProfiles(defaults: defaults).saved(.desktop) { return Self.only(keys, of: legacy) }
            return top(keys)
        }
    }

    /// A display's wallpaper: the app's stores for the edited monitor,
    /// else kept with its Desktop slot; a display never seen before copies
    /// the main display's, and before monitors had looks every display has
    /// today's.
    func wallpaper(_ display: String, editing: LookSlot?, main: String?) -> [String: Any] {
        let keys = MonitorLookKeys.wallpaper
        if display == editing?.display { return top(keys) }
        if let saved = saved(LookSlot(display: display, surface: .desktop)) { return Self.only(keys, of: saved) }
        if let main, display != main { return wallpaper(main, editing: editing, main: main) }
        return top(keys)
    }

    /// Everything `slot` holds.
    func values(_ slot: LookSlot, editing: LookSlot?, main: String?) -> [String: Any] {
        var values = style(slot, editing: editing, main: main)
        if slot.surface == .desktop {
            values.merge(wallpaper(slot.display, editing: editing, main: main)) { _, wallpaper in wallpaper }
        }
        return values
    }

    /// Everything a read-only copy of `slot` draws with: its style and its
    /// display's wallpaper.
    func drawn(_ slot: LookSlot, editing: LookSlot?, main: String?) -> [String: Any] {
        style(slot, editing: editing, main: main)
            .merging(wallpaper(slot.display, editing: editing, main: main)) { _, wallpaper in wallpaper }
    }

    /// Keeps the edited slot (and, while its Floating style is edited, the
    /// wallpaper with its Desktop slot), then loads `new` into the app's
    /// stores and remembers it as edited. Returns true when `new` had never
    /// been kept and was worked out (`style(_:editing:main:)`) and kept now.
    @discardableResult
    func switchEditing(from old: LookSlot, to new: LookSlot, main: String?) -> Bool {
        guard old != new else { return false }
        keepEdited(old, main: main)
        let seeded = saved(new) == nil
        let incomingStyle = style(new, editing: nil, main: main)
        let incomingWallpaper = wallpaper(new.display, editing: nil, main: main)
        load(incomingStyle, keys: MonitorLookKeys.style)
        load(incomingWallpaper, keys: MonitorLookKeys.wallpaper)
        if seeded { store(incomingStyle.merging(incomingWallpaper) { _, wallpaper in wallpaper }, for: new) }
        defaults.set(new.display, forKey: Self.editingKey)
        return seeded
    }

    /// Stores the edited slot as it is now, under its own key, and the
    /// edited monitor's wallpaper with its Desktop slot.
    func keepEdited(_ edited: LookSlot, main: String?) {
        store(values(edited, editing: edited, main: main), for: edited)
        if edited.surface == .floating {
            let desktop = LookSlot(display: edited.display, surface: .desktop)
            store(values(desktop, editing: edited, main: main), for: desktop)
        }
    }

    /// Puts `values` in the app's stores, removing those of `keys` it lacks.
    private func load(_ values: [String: Any], keys: [String]) {
        for key in keys {
            if let value = values[key] { defaults.set(value, forKey: key) } else { defaults.removeObject(forKey: key) }
        }
    }

    /// The first time monitors are known, and whenever a display is first
    /// seen: each connected display's Desktop slot is kept as worked out
    /// (today's look, or the main display's), and so is its Floating slot
    /// where there is one to copy. The edited slot stays in the app's stores.
    func keepConnected(_ connected: [String], editing: LookSlot) {
        let main = connected.first
        if editingDisplay == nil, main != nil { defaults.set(editing.display, forKey: Self.editingKey) }
        for display in connected {
            let desktop = LookSlot(display: display, surface: .desktop)
            if desktop != editing, saved(desktop) == nil {
                store(values(desktop, editing: editing, main: main), for: desktop)
            }
            let floating = LookSlot(display: display, surface: .floating)
            guard floating != editing, saved(floating) == nil else { continue }
            let mainFloating = main.map { LookSlot(display: $0, surface: .floating) }
            let hasSource = display == main
                ? LyricStyleProfiles(defaults: defaults).saved(.floating) != nil
                : (mainFloating == editing || mainFloating.flatMap(saved) != nil)
            if hasSource { store(values(floating, editing: editing, main: main), for: floating) }
        }
    }

    /// Gives every display in `displays` the edited monitor's look: its
    /// Desktop slot and wallpaper, and its Floating style once that is its
    /// own (kept or being edited).
    func copyEdited(_ edited: LookSlot, to displays: [String], main: String?) {
        let desktop = values(LookSlot(display: edited.display, surface: .desktop), editing: edited, main: main)
        let floatingSlot = LookSlot(display: edited.display, surface: .floating)
        let floating = floatingSlot == edited || saved(floatingSlot) != nil
            ? style(floatingSlot, editing: edited, main: main) : nil
        for display in displays where display != edited.display {
            store(desktop, for: LookSlot(display: display, surface: .desktop))
            if let floating { store(floating, for: LookSlot(display: display, surface: .floating)) }
        }
    }
}
