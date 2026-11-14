import Foundation

/// One user-facing setting or action in the Settings window: where it lives,
/// what it is called there, what stores it and the words that find it.
///
/// The catalogue is the redesign's safety net: `SettingsReachabilityTests`
/// checks that every stored `custom.*` key is either here or deliberately
/// left out (`SettingsCatalogue.excludedKeys`), that every entry's page is in
/// the sidebar, and that searching an entry's label finds it.
struct SettingsCatalogueEntry: Hashable, Identifiable {
    enum Kind: String, CaseIterable {
        case toggle, slider, choice, action, textField
    }

    /// The page it is on.
    let page: SettingsPage
    /// The section (or card) it sits in, as titled on the page.
    let section: String
    /// Its label as shown (or, for a control without visible text, as
    /// VoiceOver reads it).
    let label: String
    let kind: Kind
    /// The `UserDefaults` keys it reads or writes; empty for an action that
    /// stores nothing.
    let keys: [String]
    /// The store property, when the value isn't a plain key
    /// (`"Settings.launchAtLogin"`, kept by macOS).
    let store: String?
    /// Other words a person might type for it.
    let keywords: [String]
    /// Also reachable from the menu-bar panel.
    let inMenuBar: Bool
    /// The label is a name (a lyric source, an entrance), not a sentence.
    let labelIsName: Bool

    var id: String { page.rawValue + "|" + label }

    init(_ page: SettingsPage, _ section: String, _ label: String, _ kind: Kind,
         keys: [String] = [], store: String? = nil, keywords: [String] = [],
         menuBar: Bool = false, name: Bool = false) {
        self.page = page
        self.section = section
        self.label = label
        self.kind = kind
        self.keys = keys
        self.store = store
        self.keywords = keywords
        self.inMenuBar = menuBar
        self.labelIsName = name
        let parts: [String] = [label, section] + keywords
        self.searchText = SettingsCatalogue.fold(parts.joined(separator: " "))
    }

    /// Label, section and keywords, folded for search.
    let searchText: String

    /// Whether every one of `words` (already folded) is in this entry.
    func matches(words: [String]) -> Bool {
        guard !words.isEmpty else { return true }
        let text = searchText
        return words.allSatisfy { text.contains($0) }
    }
}

/// Every user-facing setting and action in the Settings window, page by
/// page in the order the pages show them.
///
/// Deliberately left out:
/// - Status banners' one-off fixes that only appear while something is
///   wrong (power saving's override and "Performance Settings…", "Open Login
///   Items…" while launch at login waits, "Use Match the music" on Animated
///   covers): each repeats a setting that is catalogued.
/// - Read-only rows (Lyrics from, Timing, Connection, Sharing status, the
///   audio meters, version text) and captions.
/// - The Settings window's own chrome: the toolbar's Larger/Smaller Preview
///   and Show/Hide Preview (`custom.settingsLargePreview`,
///   `custom.settingsShowsPreview`), the sidebar search field, the page
///   remembered (`custom.settingsPage`) and the window frame.
/// - The lyric card window's own controls (shape, formats, background, text
///   size), reached through "Open Lyric Card…".
/// - Menu-bar-panel-only actions: Quit Lunalyr, Join with code, Align… and
///   Video earlier / later for a music video, the listen-along banners.
/// - Keys that store state rather than a choice; see `excludedKeys`.
enum SettingsCatalogue {
    static let all: [SettingsCatalogueEntry] = {
        let pages: [[SettingsCatalogueEntry]] = [
            wallpaper, lyricStyle, covers, timing, readings, players, general, extras, share, performance, about,
        ]
        return pages.flatMap { $0 }
    }()

    /// The entries on `page`.
    static func entries(on page: SettingsPage) -> [SettingsCatalogueEntry] {
        byPage[page] ?? []
    }

    private static let byPage: [SettingsPage: [SettingsCatalogueEntry]] = Dictionary(grouping: all, by: \.page)

    /// Every key some entry stores.
    static var keys: Set<String> { Set(all.flatMap(\.keys)) }

    /// The entries `query` (several words allowed) finds, those whose label
    /// matches first.
    static func search(_ query: String) -> [SettingsCatalogueEntry] {
        let words = searchWords(query)
        guard !words.isEmpty else { return [] }
        let found = all.filter { $0.matches(words: words) }
        let labelled = found.filter { entry in
            let label = fold(entry.label)
            return words.allSatisfy { label.contains($0) }
        }
        let others = found.filter { entry in !labelled.contains(entry) }
        return labelled + others
    }

    /// A query's words, folded as entries are.
    static func searchWords(_ query: String) -> [String] {
        fold(query).split(whereSeparator: { $0.isWhitespace }).map(String.init)
    }

    /// Lowercased, accents and curly quotes evened out, so "colour" finds
    /// "Colour" and "im" or "i'm" finds "I’m".
    static func fold(_ text: String) -> String {
        let folded = text.folding(options: [.caseInsensitive, .diacriticInsensitive], locale: nil)
        return folded.replacingOccurrences(of: "\u{2019}", with: "'")
    }

    /// Keys the app stores that no setting in the window shows, and why. A
    /// prefix ending in "." stands for every key under it.
    static let excludedKeys: [String: String] = [
        "hasOnboarded": "Whether the welcome was seen; Show Welcome Again… replays it.",
        "custom.audioCaptureConfirmed": "Remembers that system audio capture worked once.",
        "custom.screenAudioPermissionDeclined": "Remembers a declined Screen Recording prompt.",
        "custom.desktopPlayer.pendingQueuePlaylistIDs": "Desktop player's queue state.",
        "custom.desktopPlayer.player": "Desktop player's last-used player.",
        "custom.desktopPlayer.queuePlaylistID": "Desktop player's queue state.",
        "custom.desktopPlayer.timeMode": "Desktop player's elapsed/remaining toggle, set on the player itself.",
        "custom.lyricCardAspect": "Set in the lyric card window.",
        "custom.lyricCardBackgroundPath": "Set in the lyric card window.",
        "custom.lyricCardFormats": "Set in the lyric card window.",
        "custom.lyricCardTextScale": "Set in the lyric card window.",
        "custom.lyricCardUseSong": "Set in the lyric card window.",
        "custom.lyricCardLinesBefore": "Stored by LunalyrExtras; no control sets it.",
        "custom.lyricCardLinesAfter": "Stored by LunalyrExtras; no control sets it.",
        "custom.lyricSourcesSeenAMLL": "One-time migration that offered AMLL.",
        "custom.lyricStyle.": "The Desktop and Floating styles, swapped by Show lyrics.",
        "custom.musicVideoAutoOffsets": "Retired; read once to migrate.",
        "custom.musicVideoCache": "Cache of each song's video.",
        "custom.musicVideoStill": "Cache of videos found to be still pictures.",
        "custom.preferWordTimedLRCLIB": "Retired; removed at launch.",
        "custom.preferWordTiming": "Retired; removed at launch.",
        "custom.rotateLayoutsCursor": "Where the layout rotation has got to.",
        "custom.settingsLargePreview": "Settings toolbar: Larger Preview.",
        "custom.settingsShowsPreview": "Settings toolbar: Show/Hide Preview.",
        "custom.settingsPage": "The page Settings last showed.",
        "custom.settingsStyleChosen": "Whether a look was picked in the welcome.",
        "custom.settingsWindow": "The Settings window's frame.",
        "custom.updates.adoptedDailyChecks": "One-time migration to daily update checks.",
    ]

    // MARK: Look

    private static let wallpaper: [SettingsCatalogueEntry] = [
        .init(.wallpaper, "Picture", "Wallpaper type", .choice, keys: ["backgroundMode"],
              keywords: ["background", "mode", "match the music", "gpu effects", "line patterns", "visualiser",
                         "visualizer", "colour only", "your wallpaper", "image or video", "music video"],
              menuBar: true),
        .init(.wallpaper, "Match the music", "GPU effect", .choice, keys: ["shaderStyle"],
              keywords: ["shader", "gpu effects", "plasma", "aurora", "nebula"]),
        .init(.wallpaper, "Match the music", "Pattern", .choice, keys: ["patternStyle"],
              keywords: ["line patterns", "lines"]),
        .init(.wallpaper, "Colour only", "Album gradient", .toggle, keys: ["custom.albumGradientWallpaper"],
              keywords: ["colour only", "color", "cover colours"]),
        .init(.wallpaper, "Visualiser", "Scene", .choice, keys: ["custom.visualizerStyle"],
              keywords: ["visualiser", "visualizer", "silk", "wavefield", "wavecore", "star trails", "light 3d"],
              menuBar: true),
        .init(.wallpaper, "Visualiser", "Wavefield preset", .choice, keys: ["custom.wavefieldPreset"],
              keywords: ["visualiser", "visualizer"]),
        .init(.wallpaper, "Visualiser", "Projection", .choice, keys: ["custom.wavefieldProjection"],
              keywords: ["wavefield", "visualiser"]),
        .init(.wallpaper, "Visualiser", "Colours", .choice,
              keys: ["custom.light3DColours", "custom.starTrailsColours", "custom.wavefieldColours",
                     "custom.wavecoreColours"],
              keywords: ["colors", "album cover", "original", "visualiser", "scene colours"]),
        .init(.wallpaper, "Visualiser", "Beat punch", .slider, keys: ["custom.visualizerPunch"],
              keywords: ["flash", "beat", "visualiser"]),
        .init(.wallpaper, "Visualiser", "Heavy scenes on main display only", .toggle,
              keys: ["custom.visualizerHeavyScenesOnMainDisplay"],
              keywords: ["displays", "monitors", "visualiser", "performance"]),
        .init(.wallpaper, "Image or video", "File", .action, keys: ["custom.wallpaperPath", "custom.wallpaperName"],
              keywords: ["choose", "import", "picture", "photo", "image", "video", "movie", "custom wallpaper"]),
        .init(.wallpaper, "Image or video", "Placement", .choice, keys: ["custom.wallpaperPlacement"],
              keywords: ["fill", "fit", "image", "video"]),
        .init(.wallpaper, "Music video", "Search YouTube for videos", .toggle, keys: ["custom.musicVideoOnline"],
              keywords: ["music video", "online", "youtube"]),
        .init(.wallpaper, "Music video", "Without a video", .choice, keys: ["custom.musicVideoFallback"],
              keywords: ["music video", "fallback"]),
        .init(.wallpaper, "Music video", "YouTube link", .textField, keys: ["custom.musicVideoLinks"],
              keywords: ["music video", "paste", "url", "use", "clear"]),
        .init(.wallpaper, "Music video", "Search YouTube…", .action,
              keywords: ["music video", "find video", "pick video"], menuBar: true),
        .init(.wallpaper, "Music video", "Open video on YouTube", .action,
              keywords: ["music video", "browser"], menuBar: true),
        .init(.wallpaper, "Music video", "Video timing", .action,
              keys: ["custom.musicVideoOffsets", "custom.musicVideoOffsetVideos"],
              keywords: ["music video", "offset", "nudge", "earlier", "later", "intro", "reset", "sync"]),
        .init(.wallpaper, "Music video", "Tap to sync", .action,
              keys: ["custom.musicVideoOffsets", "custom.musicVideoOffsetVideos"],
              keywords: ["music video", "video timing", "tap now", "tap again"], menuBar: true),
        .init(.wallpaper, "Music video", "Show the video on", .choice, keys: ["custom.musicVideoDisplays"],
              keywords: ["music video", "displays", "monitors", "screens"]),
        .init(.wallpaper, "Music video", "YouTube videos", .choice, keys: ["custom.musicVideoFraming"],
              keywords: ["music video", "framing", "fill the screen", "show the whole video", "crop"]),
        .init(.wallpaper, "Music video", "Library videos", .choice, keys: ["custom.libraryVideoFraming"],
              keywords: ["music video", "framing", "fill the screen", "show the whole video", "crop"]),
        .init(.wallpaper, "Music video", "Video dimming", .slider, keys: ["custom.musicVideoDim"],
              keywords: ["music video", "darken", "dim"]),
        .init(.wallpaper, "Music video", "Video blur", .slider, keys: ["custom.musicVideoBlur"],
              keywords: ["music video", "soften"]),
        .init(.wallpaper, "Music response", "System audio", .toggle, keys: ["custom.audioReactiveEnabled"],
              keywords: ["music response", "audio reactive", "visualiser", "listen"]),
        .init(.wallpaper, "Music response", "Sensitivity", .slider, keys: ["custom.audioSensitivity"],
              keywords: ["music response", "audio", "visualiser"]),
        .init(.wallpaper, "Music response", "Capture method", .choice, keys: ["custom.audioCaptureMethod"],
              keywords: ["audio setup", "diagnostics", "system audio", "screen recording", "tap"]),
        .init(.wallpaper, "Music response", "Open Privacy Settings…", .action,
              keywords: ["audio", "permission", "system audio recording"]),
        .init(.wallpaper, "Music response", "Copy Audio Diagnostics", .action,
              keywords: ["audio setup", "bug report", "diagnostics"]),
        .init(.wallpaper, "Music response", "Reconnect", .action,
              keywords: ["system audio", "needs attention"]),
        .init(.wallpaper, "Music response", "Try Compatibility Capture", .action,
              keywords: ["system audio", "screen recording", "needs attention"]),
        .init(.wallpaper, "Legibility", "Automatic text backing", .toggle, keys: ["custom.textBackingAutomatic"],
              keywords: ["shadow", "readable", "legibility", "contrast"]),
        .init(.wallpaper, "Legibility", "Text backing", .slider, keys: ["custom.textBacking"],
              keywords: ["shadow", "readable", "legibility", "contrast"]),
        .init(.wallpaper, "Legibility", "Shade under the menu bar", .toggle, keys: ["custom.menuBarShade"],
              keywords: ["menu bar", "bright", "washed out"]),
        .init(.wallpaper, "Adjustments", "Motion speed", .slider, keys: ["effectSpeed"],
              keywords: ["effect speed", "animation speed", "adjustments"]),
        .init(.wallpaper, "Adjustments", "Colour richness", .slider, keys: ["vividness"],
              keywords: ["color", "vividness", "saturation", "adjustments"]),
        .init(.wallpaper, "Adjustments", "Darkening", .slider, keys: ["scrimStrength"],
              keywords: ["scrim", "dim", "darken", "adjustments"]),
        .init(.wallpaper, "Adjustments", "Pattern visibility", .slider, keys: ["patternOpacity"],
              keywords: ["opacity", "line patterns", "adjustments"]),
        .init(.wallpaper, "Adjustments", "Reset Adjustments", .action,
              keys: ["effectSpeed", "vividness", "scrimStrength", "patternOpacity", "motionBlur"],
              keywords: ["defaults", "restore"]),
    ]

    private static let lyricStyle: [SettingsCatalogueEntry] = {
        let mix: [SettingsCatalogueEntry] = WordArrival.mixChoices.map { arrival in
            SettingsCatalogueEntry(.typography, "Word entrances", arrival.title, .toggle, keys: ["custom.wordEntranceMix"],
                                   keywords: ["custom mix", "entrance", "animation"], name: true)
        }
        let before: [SettingsCatalogueEntry] = [
            .init(.typography, "Picture", "Show lyrics", .choice, keys: ["custom.lyricsSurface"],
                  keywords: ["floating", "desktop", "over apps", "surface", "window"], menuBar: true),
            .init(.typography, "Size", "Size", .choice, keys: ["custom.floating.textSize"],
                  keywords: ["floating", "small", "medium", "large"]),
            .init(.typography, "Size", "Text size", .slider, keys: ["custom.floating.textSize"],
                  keywords: ["floating", "points", "font size"]),
            .init(.typography, "Over apps", "Shade behind lyrics", .slider, keys: ["custom.floating.backdrop"],
                  keywords: ["floating", "backdrop", "shadow", "readable"]),
            .init(.typography, "Over apps", "Fade when the pointer is over them", .toggle,
                  keys: ["custom.floating.fadeUnderPointer"], keywords: ["floating", "mouse", "cursor", "hover"]),
            .init(.typography, "Place", "Place", .choice, keys: ["custom.floating.placement"],
                  keywords: ["floating", "position", "placement", "corner", "custom"]),
            .init(.typography, "Place", "Display", .choice, keys: ["custom.floating.display"],
                  keywords: ["floating", "monitor", "screen"]),
            .init(.typography, "Place", "Edit position…", .action, keys: ["custom.floating.frames"],
                  keywords: ["floating", "move", "drag", "resize", "done"], menuBar: true),
            .init(.typography, "Place", "Reset size", .action,
                  keys: ["custom.floating.frames", "custom.floating.sizes"], keywords: ["floating", "forget size"]),
            .init(.typography, "Picture", "Layout", .choice, keys: ["lyricComposition"],
                  keywords: ["composition", "match the music", "scattered", "echo", "cascade", "editorial",
                             "constellation", "spoken", "cutout"]),
            .init(.typography, "Picture", "Change layout every song", .toggle, keys: ["custom.rotateLayouts"],
                  keywords: ["rotate", "rotation", "next layout"]),
            .init(.typography, "Layout", "Layouts in the rotation", .slider, keys: ["custom.rotateLayoutCounts"],
                  keywords: ["rotate", "songs in a row", "never"]),
            .init(.typography, "Shown with the lyrics", "Show the song title at the start", .toggle, keys: ["custom.openingTitle"],
                  keywords: ["opening title", "artist", "intro"]),
            .init(.typography, "Shown with the lyrics", "Show the album cover", .toggle, keys: ["custom.openingTitleCover"],
                  keywords: ["opening title", "artwork", "cover"]),
            .init(.typography, "Shown with the lyrics", "Show credits in the background", .toggle, keys: ["custom.creditsLayer"],
                  keywords: ["credits", "produced by", "writers"]),
            .init(.typography, "Shown with the lyrics", "Backing vocals in the background", .toggle, keys: ["custom.backingVocalsLayer"],
                  keywords: ["backing vocals", "brackets", "harmony"]),
            .init(.typography, "Shown with the lyrics", "Backing vocals animate like", .choice, keys: ["custom.backingVocalStyle"],
                  keywords: ["backing vocals", "lead's layout"]),
            .init(.typography, "Shown with the lyrics", "Add Editorial typography", .toggle, keys: ["custom.editorialTypography"],
                  keywords: ["editorial", "magazine"]),
            .init(.typography, "Shown with the lyrics", "Editorial amount", .slider, keys: ["custom.editorialAmount"],
                  keywords: ["editorial"]),
            .init(.typography, "Lyric style", "Lyric size", .slider, keys: ["lyricScale"],
                  keywords: ["text size", "scale", "bigger", "smaller"]),
            .init(.typography, "Typeface", "Randomise lyric fonts", .toggle, keys: ["custom.randomizeLyricFonts"],
                  keywords: ["randomize", "mix", "typefaces", "font"]),
            .init(.typography, "Typeface", "Typeface", .choice, keys: ["fontChoice"],
                  keywords: ["font", "manrope", "google fonts"]),
            .init(.typography, "Typeface", "Google Fonts family", .choice, keys: ["googleFontFamily"],
                  keywords: ["font", "typeface", "family"]),
            .init(.typography, "Typeface", "Any family", .textField, keys: ["googleFontFamily"],
                  keywords: ["font", "installed", "google fonts", "use"]),
            .init(.typography, "Colours", "Colour", .choice, keys: ["custom.lyricColourMode"],
                  keywords: ["color", "text colour", "pastel", "album", "gradient", "music reactive"]),
            .init(.typography, "Colours", "Colour strength", .slider, keys: ["custom.lyricColourStrength"],
                  keywords: ["color", "vivid", "neutral"]),
            .init(.typography, "Colours", "Colour speed", .slider, keys: ["custom.lyricColourSpeed"],
                  keywords: ["color", "drift", "lively", "slow"]),
            .init(.typography, "Colours", "Colour 1", .choice, keys: ["custom.lyricColourCustom"],
                  keywords: ["color", "custom gradient", "colour picker", "remove colour"]),
            .init(.typography, "Colours", "Add Colour", .action, keys: ["custom.lyricColourCustom"],
                  keywords: ["color", "custom gradient", "fourth colour"]),
            .init(.typography, "Colours", "Highlight colour", .choice, keys: ["custom.lyricHighlightMode"],
                  keywords: ["color", "cutout", "tiles", "cascade", "accent"]),
            .init(.typography, "Colours", "Your highlight colour", .choice, keys: ["custom.lyricHighlightCustom"],
                  keywords: ["color", "custom", "highlight"]),
            .init(.typography, "Word entrances", "Reveal sung words", .toggle, keys: ["custom.wordRevealByStyle"],
                  keywords: ["word by word", "animation", "reveal"]),
            .init(.typography, "Word entrances", "Entrance", .choice, keys: ["custom.wordArrivalByStyle"],
                  keywords: ["word arrival", "animation", "custom mix", "fade", "pop"]),
        ]
        let after: [SettingsCatalogueEntry] = [
            .init(.typography, "Word entrances", "Faint next-word preview", .toggle, keys: ["custom.nextWordPreviewByStyle"],
                  keywords: ["preview", "upcoming word", "ghost"]),
            .init(.typography, "Word entrances", "Preview lead", .slider, keys: ["custom.nextWordPreviewLead"],
                  keywords: ["preview", "seconds", "before the next word"]),
            .init(.typography, "Constellation", "Spread", .slider, keys: ["constellationSpread"],
                  keywords: ["constellation"]),
            .init(.typography, "Constellation", "Links", .slider, keys: ["constellationLinkOpacity"],
                  keywords: ["constellation", "lines", "connections"]),
            .init(.typography, "Echo", "Repeats", .slider, keys: ["echoStrength"],
                  keywords: ["echo", "strength"]),
            .init(.typography, "Instrumental breaks", "Show three dots during instrumental breaks", .toggle,
                  keys: ["custom.showInterludes"], keywords: ["interlude", "dots", "instrumental"]),
            .init(.typography, "Remember a style", "Use remembered styles", .toggle, keys: ["custom.styleMemory.enabled"],
                  keywords: ["style memory", "per song", "per artist"]),
            .init(.typography, "Remember a style", "Remember for This Song", .action, keys: ["custom.styleMemory.songs"],
                  keywords: ["style memory", "save style"]),
            .init(.typography, "Remember a style", "Remember for Artist", .action, keys: ["custom.styleMemory.artists"],
                  keywords: ["style memory", "save style"]),
            .init(.typography, "Remember a style", "Update with the Choices Above", .action,
                  keys: ["custom.styleMemory.songs", "custom.styleMemory.artists"], keywords: ["style memory"]),
            .init(.typography, "Remember a style", "Update", .action,
                  keys: ["custom.styleMemory.songs", "custom.styleMemory.artists"],
                  keywords: ["style memory", "remembered layout", "remembered colour"]),
            .init(.typography, "Remember a style", "Forget", .action,
                  keys: ["custom.styleMemory.songs", "custom.styleMemory.artists"],
                  keywords: ["style memory", "remembered style"]),
            .init(.typography, "Remember a style", "Forget All", .action,
                  keys: ["custom.styleMemory.songs", "custom.styleMemory.artists"],
                  keywords: ["style memory", "remembered styles", "clear"]),
        ]
        return before + mix + after
    }()

    private static let covers: [SettingsCatalogueEntry] = [
        .init(.covers, "Animated covers", "Use animated covers", .toggle, keys: ["useMotionArtwork"],
              keywords: ["motion artwork", "album art", "apple music"]),
        .init(.covers, "Picture", "Placement", .choice, keys: ["motionPlacement"],
              keywords: ["framing", "fill", "fit"]),
        .init(.covers, "Framing", "Softening", .slider, keys: ["motionBlur"],
              keywords: ["blur", "soften"]),
        .init(.covers, "Framing", "Exclude this album", .toggle, keys: ["motionExclusions"],
              keywords: ["exclude", "skip", "album"]),
        .init(.covers, "Excluded albums", "Allow", .action, keys: ["motionExclusions"],
              keywords: ["excluded albums", "include again"]),
    ]

    // MARK: Words

    private static let timing: [SettingsCatalogueEntry] = {
        let sourceRows: [SettingsCatalogueEntry] = LyricSourceID.allCases.map { source in
            SettingsCatalogueEntry(.lyrics, "Sources", source.title, .toggle, keys: ["custom.lyricSources"],
                                   keywords: ["lyric source", "service", "provider"], name: true)
        }
        let thisSong: [SettingsCatalogueEntry] = [
            .init(.lyrics, "Lyric timing", "Lyric timing", .action, keys: ["custom.songOffsets"],
                  keywords: ["earlier", "later", "nudge", "reset song offset"]),
            // `LyricTapSyncController.menuTitle`, main-actor isolated; the tests
            // check the two agree.
            .init(.lyrics, "Lyric timing", "Tap to Sync Lyrics…", .action, keys: ["custom.songOffsets"],
                  keywords: ["tap to sync", "timing", "keys"], menuBar: true),
            .init(.lyrics, "Lyric timing", "Undo Tap to Sync", .action, keys: ["custom.songOffsets"],
                  keywords: ["tap to sync", "undo"], menuBar: true),
            .init(.lyrics, "Lyric timing", "Song offset", .slider, keys: ["custom.songOffsets"],
                  keywords: ["timing", "offset", "earlier", "later", "this song"]),
            .init(.lyrics, "Lyrics", "Return to Automatic", .action,
                  keywords: ["saved choice", "pinned", "automatic selection"]),
            .init(.lyrics, "Find lyrics", "Song title", .textField, keywords: ["search", "title"]),
            .init(.lyrics, "Find lyrics", "Artist", .textField, keywords: ["search"]),
            .init(.lyrics, "Find lyrics", "Search", .action, keywords: ["find lyrics", "every source"]),
            .init(.lyrics, "Find lyrics", "Use Playing Song", .action, keywords: ["find lyrics", "fill"]),
            .init(.lyrics, "Find lyrics", "Import LRC or TTML…", .action, keywords: ["file", "own lyrics", "import"]),
            .init(.lyrics, "Find lyrics", "Use for This Song", .action,
                  keywords: ["candidate", "match", "close matches", "choose lyrics"]),
            .init(.lyrics, "Without synced lyrics", "When none are found", .choice, keys: ["noLyrics"],
                  keywords: ["no lyrics", "missing lyrics", "notice"]),
            .init(.lyrics, "Sources", "Choose by", .choice, keys: ["custom.lyricMatchMode"],
                  keywords: ["match mode", "smart", "priority", "selection"]),
            .init(.lyrics, "Sources", "Restore Recommended Order", .action,
                  keys: ["custom.lyricSourceOrder", "custom.lyricMatchMode"],
                  keywords: ["source order", "reset", "defaults"]),
        ]
        let rest: [SettingsCatalogueEntry] = [
            .init(.lyrics, "Sources", "Priority order", .action, keys: ["custom.lyricSourceOrder"],
                  keywords: ["raise", "lower", "move up", "move down", "source order"]),
            .init(.lyrics, "Sources", "Search under other names", .toggle, keys: ["custom.artistAliases"],
                  keywords: ["artist aliases", "musicbrainz", "japanese", "other names"]),
            .init(.lyrics, "Local exports", "Lyrimuse lyrics folder", .action, keys: ["custom.lyrimuseFolder"],
                  keywords: ["local files", "choose folder", "exports"]),
            .init(.lyrics, "Local exports", "Use Default Lyrimuse Folder", .action, keys: ["custom.lyrimuseFolder"],
                  keywords: ["local files", "exports"]),
            .init(.lyrics, "Advanced timing", "Global offset", .slider, keys: ["custom.lyricOffset"],
                  keywords: ["every song", "timing", "offset", "earlier", "later"]),
            .init(.lyrics, "Advanced timing", "Treat long pauses as instrumental", .toggle, keys: ["custom.repairQQGaps"],
                  keywords: ["qq music", "gaps", "instrumental", "pauses"]),
        ]
        return thisSong + sourceRows + rest
    }()

    private static let readings: [SettingsCatalogueEntry] = [
        .init(.readings, "Romanisation", "Display", .choice, keys: ["custom.romanizationDisplay"],
              keywords: ["romanization", "readings", "below", "above", "furigana"]),
        .init(.readings, "Romanisation", "Language", .choice, keys: ["custom.romanizationLanguage"],
              keywords: ["romanization", "japanese", "korean", "chinese", "cantonese", "mandarin"]),
        .init(.readings, "Romanisation", "Keep tone marks", .toggle, keys: ["custom.romanizationTones"],
              keywords: ["pinyin", "tones", "accents"]),
        .init(.readings, "Romanisation", "Prefer saved readings", .toggle, keys: ["custom.romanizationSaved"],
              keywords: ["saved readings", "source readings"]),
        .init(.readings, "Saved readings", "Import Reading LRC…", .action,
              keywords: ["readings", "file", "pronunciation"]),
        .init(.readings, "Translations", "Show translations under lyrics", .toggle, keys: ["custom.showTranslations"],
              keywords: ["translation", "bilingual"]),
        .init(.readings, "Translations", MachineTranslationCopy.toggle, .toggle, keys: ["custom.machineTranslation"],
              keywords: ["machine translation", "apple translation", "translate"]),
        .init(.readings, "Translations", "Translate into", .choice, keys: ["custom.machineTranslationTarget"],
              keywords: ["target language", "translation", "system language"]),
        .init(.readings, "Translations", "Download Languages…", .action,
              keywords: ["translation", "languages"]),
        .init(.readings, "Translations", "Try Again", .action,
              keywords: ["translation", "retry", "failed"]),
    ]

    // MARK: App

    /// "Follow VLC, Swinsian and VOX", as Music players names them all.
    private static var localPlayersTitle: String {
        let names = PlayerKind.localPlayers.map(\.displayName)
        guard names.count > 1, let last = names.last else { return "Follow " + (names.first ?? "") }
        let head = names.dropLast().joined(separator: ", ")
        return "Follow " + head + " and " + last
    }

    private static let players: [SettingsCatalogueEntry] = [
        .init(.players, "Follow", "Player", .choice, keys: ["custom.player"],
              keywords: ["automatic", "spotify", "apple music", "music app", "follow"], menuBar: true),
        .init(.players, "Follow", localPlayersTitle, .toggle, keys: ["custom.player.localPlayers"],
              keywords: ["local players", "local files", "vlc", "swinsian", "vox"]),
        .init(.players, "Follow", "Follow web players in browsers", .toggle, keys: ["custom.player.webPlayers"],
              keywords: ["browser", "youtube music", "soundcloud", "amazon music", "safari", "chrome", "web"]),
        .init(.players, "Access", "Allow Access…", .action,
              keywords: ["automation", "permission", "access"]),
        .init(.players, "Access", "Open Privacy Settings", .action,
              keywords: ["automation", "permission", "privacy & security"]),
    ]

    private static let general: [SettingsCatalogueEntry] = [
        .init(.general, "Windows", "Window style", .choice, keys: ["custom.settingsStyle"],
              keywords: ["calm", "bold", "standard", "look", "theme", "appearance", "style"]),
        .init(.general, "Startup", "Launch at login", .toggle, store: "Settings.launchAtLogin",
              keywords: ["startup", "login items", "open at login"]),
        .init(.general, "Updates", "Check for Updates…", .action,
              keywords: ["updates", "update", "version"], menuBar: true),
        .init(.general, "Updates", "Check for updates automatically", .toggle, store: "AppUpdates.automaticallyChecks",
              keywords: ["updates", "daily", "automatic"]),
        .init(.general, "Welcome", "Show Welcome Again…", .action,
              keywords: ["welcome", "onboarding", "setup"]),
    ]

    private static let extras: [SettingsCatalogueEntry] = {
        // One row per shortcut (`GlobalHotkeys.shortcuts`, main-actor
        // isolated; the tests check the two agree).
        let shortcuts: [SettingsCatalogueEntry] = GlobalHotkeys.Action.allCases.map { action in
            SettingsCatalogueEntry(.extras, "Keyboard shortcuts", action.title, .toggle,
                                   keys: ["custom.globalShortcutsOff", "custom.globalShortcutsOn"],
                                   keywords: ["shortcut", "hotkey", "keyboard"])
        }
        let before: [SettingsCatalogueEntry] = [
            .init(.extras, "Lyrics on screen", "Hide lyrics", .toggle, keys: ["custom.lyricsHidden"],
                  keywords: ["show lyrics", "hidden"], menuBar: true),
            .init(.extras, "Lyrics on screen", "Hide lyrics while a display is mirrored", .toggle,
                  keys: ["custom.hideLyricsWhileMirroring"], keywords: ["mirroring", "presenting", "projector", "tv"]),
            .init(.extras, "Lyrics on screen", LunalyrExtras.Titles.showLyricsAnyway, .action,
                  keywords: ["mirroring", "show anyway"], menuBar: true),
            .init(.extras, "Lyrics on screen", "Pause wallpaper", .toggle, keys: ["custom.wallpaperPaused"],
                  keywords: ["freeze", "resume", "pause"], menuBar: true),
            .init(.extras, "Menu bar", "Show the current line in the menu bar", .toggle, keys: ["custom.menuBarLyric"],
                  keywords: ["menu bar lyric", "status bar"]),
            .init(.extras, "Menu bar", "Longest line", .slider, keys: ["custom.menuBarLyricLength"],
                  keywords: ["menu bar", "characters", "length"]),
            .init(.extras, "Keyboard shortcuts", "Use global shortcuts", .toggle, keys: ["custom.globalShortcuts"],
                  keywords: ["hotkeys", "keyboard"]),
            .init(.extras, "Keyboard shortcuts", "Keys", .choice, keys: ["custom.globalShortcutModifiers"],
                  keywords: ["modifiers", "control option command", "control shift command", "shortcut"]),
        ]
        return before + shortcuts
    }()

    private static let share: [SettingsCatalogueEntry] = [
        .init(.share, "Discord", "Show what I'm listening to on Discord", .toggle, keys: ["custom.discordPresence"],
              keywords: ["rich presence", "status"]),
        .init(.share, "Discord", "Status shows", .choice, keys: ["custom.discordStatusText"],
              keywords: ["discord status", "song", "artist", "lyric"]),
        .init(.share, "Discord", "Show the current lyric", .toggle, keys: ["custom.discordLyric"],
              keywords: ["discord", "line"]),
        .init(.share, "Discord", "Show the album cover", .toggle, keys: ["custom.discordCover"],
              keywords: ["discord", "artwork"]),
        .init(.share, "Discord", "Show the progress bar", .toggle, keys: ["custom.discordProgress"],
              keywords: ["discord", "time"]),
        .init(.share, "Discord", "Show a Play this song button", .toggle, keys: ["custom.discordSongButton"],
              keywords: ["discord", "song.link"]),
        .init(.share, "Discord", "Show a Get Lunalyr button", .toggle, keys: ["custom.discordButton"],
              keywords: ["discord", "download"]),
        .init(.share, "Discord", "Keep showing while paused", .toggle, keys: ["custom.discordPaused"],
              keywords: ["discord", "pause"]),
        .init(.share, "Discord", "Lyric lead", .slider, keys: ["custom.discordLyricLead"],
              keywords: ["discord", "earlier", "ahead"]),
        .init(.share, "Discord", "Let friends listen along", .toggle, keys: ["custom.listenSharing"],
              keywords: ["listen along", "share", "friends", "relay"]),
        .init(.share, "Discord", "Copy Link", .action,
              keywords: ["listen along", "share link"]),
        .init(.share, "Discord", "Share lyrics with listeners", .toggle, keys: ["custom.listenSharesLyrics"],
              keywords: ["listen along", "lyrics"]),
        .init(.share, "Discord", "Show a Join button on Discord", .toggle, keys: ["custom.discordJoinParty"],
              keywords: ["listen along", "join"]),
        .init(.share, "Listen along", "Relay", .textField, keys: ["custom.listenRelayURL"],
              keywords: ["listen along", "listen-along relay", "relay", "url", "cloudflare worker", "server"]),
        .init(.share, "Lyric card", "Card layout", .choice, keys: ["custom.lyricCardComposition"],
              keywords: ["lyric card", "follow wallpaper", "layout"]),
        .init(.share, "Lyric card", "Open Lyric Card…", .action,
              keywords: ["lyric card", "gif", "mp4", "png", "share", "copy"], menuBar: true),
    ]

    private static let performance: [SettingsCatalogueEntry] = [
        .init(.performance, "Rendering and power", "Frame rate", .choice, keys: ["custom.animationRate"],
              keywords: ["fps", "animation rate", "smooth"]),
        .init(.performance, "Rendering and power", "Low power visuals", .toggle, keys: ["custom.lowPowerVisuals"],
              keywords: ["battery", "energy", "30 fps"]),
        .init(.performance, "Rendering and power", "Follow macOS Low Power Mode", .toggle, keys: ["respectLowPower"],
              keywords: ["battery", "energy", "low power"]),
        .init(.performance, "Rendering and power", "On battery", .choice, keys: ["onBattery"],
              keywords: ["power", "battery", "light effects"]),
        .init(.performance, "Rendering and power", "Pause fully covered wallpapers", .toggle,
              keys: ["custom.pauseWhenCovered"], keywords: ["covered", "windows", "full screen", "occluded"]),
        .init(.performance, "Desktop", "Show player on hover", .toggle, keys: ["custom.showControlsOnHover"],
              keywords: ["desktop player", "controls", "hover", "pointer"]),
    ]

    private static let about: [SettingsCatalogueEntry] = [
        .init(.about, "About", "Project & Downloads", .action,
              keywords: ["website", "github", "download"]),
        .init(.about, "Built on open source", "View Bundled Licences", .action,
              keywords: ["licenses", "credits", "open source"]),
    ]
}
