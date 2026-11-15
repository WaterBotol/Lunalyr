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
///   Items…" while launch at login waits, "Use Custom Mix" on Animated
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
            wallpaper, text, animation, covers, timing, readings, general, share, performance,
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
        "custom.looks.saved": "The user's saved Looks, kept by LookStore; each Look's settings are catalogued on their own.",
        "custom.looks.current": "Which Look was last applied, kept by LookStore.",
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
        "custom.lyricSourcesSeen": "One-time migration that offers lyric sources added later.",
        "custom.lyricSourcesSeenAMLL": "One-time migration that offered AMLL.",
        "custom.lyricSourcesSeenMusixmatch": "One-time migration that offered Musixmatch.",
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
              keywords: ["background", "mode", "custom mix", "match the music", "gpu effects", "line patterns", "visualiser",
                         "visualizer", "colour only", "your wallpaper", "image or video", "music video"],
              menuBar: true),
        .init(.wallpaper, "Custom Mix", "GPU effect", .choice, keys: ["shaderStyle"],
              keywords: ["shader", "gpu effects", "plasma", "aurora", "nebula"]),
        .init(.wallpaper, "Custom Mix", "Pattern", .choice, keys: ["patternStyle"],
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
        // The adjustments sit in the section of the type showing (the
        // visualiser's, Custom Mix's, Line patterns'…).
        .init(.wallpaper, "Visualiser", "Motion speed", .slider, keys: ["effectSpeed"],
              keywords: ["effect speed", "animation speed", "adjustments"]),
        .init(.wallpaper, "Visualiser", "Colour richness", .slider, keys: ["vividness"],
              keywords: ["color", "vividness", "saturation", "adjustments"]),
        .init(.wallpaper, "Visualiser", "Darkening", .slider, keys: ["scrimStrength"],
              keywords: ["scrim", "dim", "darken", "adjustments"]),
        .init(.wallpaper, "Line patterns", "Pattern visibility", .slider, keys: ["patternOpacity"],
              keywords: ["opacity", "line patterns", "adjustments"]),
        .init(.wallpaper, "Visualiser", "Reset Adjustments", .action,
              keys: ["effectSpeed", "vividness", "scrimStrength", "patternOpacity", "motionBlur"],
              keywords: ["defaults", "restore", "adjustments"]),
    ]

    private static let text: [SettingsCatalogueEntry] = [
        .init(.typography, "Picture", "Show lyrics", .choice, keys: ["custom.lyricsSurface"],
              keywords: ["floating", "desktop", "over apps", "surface", "window"], menuBar: true),
        .init(.typography, "Picture", "Layout", .choice, keys: ["lyricComposition"],
              keywords: ["composition", "custom mix", "match the music", "scattered", "echo", "cascade", "editorial",
                         "constellation", "spoken", "cutout"]),
        .init(.typography, "Picture", "Change layout every song", .toggle, keys: ["custom.rotateLayouts"],
              keywords: ["rotate", "rotation", "next layout"]),
        .init(.typography, "Style", "Layouts in the rotation", .slider, keys: ["custom.rotateLayoutCounts"],
              keywords: ["rotate", "songs in a row", "never"]),
        .init(.typography, "Style", "Use remembered styles", .toggle, keys: ["custom.styleMemory.enabled"],
              keywords: ["style memory", "per song", "per artist", "remembered styles"]),
        .init(.typography, "Style", "Remember for This Song", .action, keys: ["custom.styleMemory.songs"],
              keywords: ["style memory", "save style", "remembered styles"]),
        .init(.typography, "Style", "Remember for Artist", .action, keys: ["custom.styleMemory.artists"],
              keywords: ["style memory", "save style", "remembered styles"]),
        .init(.typography, "Style", "Update with the Choices Above", .action,
              keys: ["custom.styleMemory.songs", "custom.styleMemory.artists"], keywords: ["style memory"]),
        .init(.typography, "Style", "Update", .action,
              keys: ["custom.styleMemory.songs", "custom.styleMemory.artists"],
              keywords: ["style memory", "remembered layout", "remembered colour"]),
        .init(.typography, "Style", "Forget", .action,
              keys: ["custom.styleMemory.songs", "custom.styleMemory.artists"],
              keywords: ["style memory", "remembered style"]),
        .init(.typography, "Style", "Forget All", .action,
              keys: ["custom.styleMemory.songs", "custom.styleMemory.artists"],
              keywords: ["style memory", "remembered styles", "clear"]),
        .init(.typography, "Size and place", "Lyric size", .slider, keys: ["lyricScale"],
              keywords: ["text size", "scale", "bigger", "smaller"]),
        .init(.typography, "Size and place", "Size", .choice, keys: ["custom.floating.textSize"],
              keywords: ["floating", "small", "medium", "large"]),
        .init(.typography, "Size and place", "Text size", .slider, keys: ["custom.floating.textSize"],
              keywords: ["floating", "points", "font size"]),
        .init(.typography, "Size and place", "Place", .choice, keys: ["custom.floating.placement"],
              keywords: ["floating", "position", "placement", "corner", "custom"]),
        .init(.typography, "Size and place", "Display", .choice, keys: ["custom.floating.display"],
              keywords: ["floating", "monitor", "screen"]),
        .init(.typography, "Size and place", "Edit position…", .action, keys: ["custom.floating.frames"],
              keywords: ["floating", "move", "drag", "resize", "done"], menuBar: true),
        .init(.typography, "Size and place", "Reset size", .action,
              keys: ["custom.floating.frames", "custom.floating.sizes"], keywords: ["floating", "forget size"]),
        .init(.typography, "Typeface", "Typeface", .choice, keys: ["fontChoice"],
              keywords: ["font", "manrope", "google fonts", "installed font"]),
        .init(.typography, "Typeface", "Randomise lyric fonts", .toggle, keys: ["custom.randomizeLyricFonts"],
              keywords: ["randomize", "mix", "typefaces", "font"]),
        .init(.typography, "Typeface", "Google Fonts family", .choice, keys: ["googleFontFamily"],
              keywords: ["font", "typeface", "family"]),
        .init(.typography, "Typeface", "Any family", .textField, keys: ["googleFontFamily"],
              keywords: ["font", "installed", "google fonts", "use"]),
        .init(.typography, "Typeface", "Installed family", .choice, keys: ["custom.installedFontFamily"],
              keywords: ["font", "installed", "font book", "this mac", "search fonts"]),
        .init(.typography, "Typeface", "Find new fonts", .action, keys: ["custom.installedFontFamily"],
              keywords: ["refresh fonts", "newly installed", "font book"]),
        .init(.typography, "Colour", "Colour", .choice, keys: ["custom.lyricColourMode"],
              keywords: ["color", "text colour", "pastel", "album", "gradient", "music reactive"]),
        .init(.typography, "Colour", "Highlight colour", .choice, keys: ["custom.lyricHighlightMode"],
              keywords: ["color", "cutout", "tiles", "cascade", "accent"]),
        .init(.typography, "Colour", "Colour strength", .slider, keys: ["custom.lyricColourStrength"],
              keywords: ["color", "vivid", "neutral"]),
        .init(.typography, "Colour", "Colour speed", .slider, keys: ["custom.lyricColourSpeed"],
              keywords: ["color", "drift", "lively", "slow"]),
        .init(.typography, "Colour", "Colour 1", .choice, keys: ["custom.lyricColourCustom"],
              keywords: ["color", "custom gradient", "colour picker", "remove colour"]),
        .init(.typography, "Colour", "Add Colour", .action, keys: ["custom.lyricColourCustom"],
              keywords: ["color", "custom gradient", "fourth colour"]),
        .init(.typography, "Colour", "Make the colours pastel", .toggle, keys: ["custom.lyricColourPastel"],
              keywords: ["color", "pastel", "custom gradient", "exact colours", "saturated", "vivid"]),
        .init(.typography, "Colour", "Your highlight colour", .choice, keys: ["custom.lyricHighlightCustom"],
              keywords: ["color", "custom", "highlight"]),
        .init(.typography, "Readability", "Automatic text backing", .toggle, keys: ["custom.textBackingAutomatic"],
              keywords: ["shadow", "readable", "legibility", "contrast"]),
        .init(.typography, "Readability", "Shade under the menu bar", .toggle, keys: ["custom.menuBarShade"],
              keywords: ["menu bar", "bright", "washed out"]),
        .init(.typography, "Readability", "Text backing", .slider, keys: ["custom.textBacking"],
              keywords: ["shadow", "readable", "legibility", "contrast"]),
        .init(.typography, "Readability", "Shade behind lyrics", .slider, keys: ["custom.floating.backdrop"],
              keywords: ["floating", "backdrop", "shadow", "readable", "over apps"]),
        .init(.typography, "Readability", "Fade when the pointer is over them", .toggle,
              keys: ["custom.floating.fadeUnderPointer"], keywords: ["floating", "mouse", "cursor", "hover"]),
        .init(.typography, "Shown with the lyrics", "Show the song title at the start", .toggle, keys: ["custom.openingTitle"],
              keywords: ["opening title", "artist", "intro"]),
        .init(.typography, "Shown with the lyrics", "Keep the song title on screen", .toggle, keys: ["custom.openingTitleAlways"],
              keywords: ["opening title", "song title", "now playing", "whole song", "always", "artist"]),
        .init(.typography, "Shown with the lyrics", "Title strength", .slider, keys: ["custom.openingTitleStrength"],
              keywords: ["opening title", "song title", "opacity", "fade", "dim", "solid", "kept"]),
        .init(.typography, "Shown with the lyrics", "Reset Title Position", .action,
              keys: ["custom.openingTitleLeft", "custom.openingTitleLift", "custom.openingTitleScale",
                     "custom.floatingTitleCentre", "custom.floatingTitleEdge", "custom.floatingTitleScale"],
              keywords: ["opening title", "move", "resize", "position", "size", "drag", "title card"]),
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
    ]

    private static let animation: [SettingsCatalogueEntry] = {
        let mix: [SettingsCatalogueEntry] = WordArrival.mixChoices.map { arrival in
            SettingsCatalogueEntry(.animation, "Word entrances", arrival.title, .toggle, keys: ["custom.wordEntranceMix"],
                                   keywords: ["mixed entrances", "custom mix", "mix", "entrance", "animation"], name: true)
        }
        let entrances: [SettingsCatalogueEntry] = [
            .init(.animation, "Word entrances", "Entrance", .choice, keys: ["custom.wordArrivalByStyle"],
                  keywords: ["word arrival", "animation", "mixed entrances", "custom mix", "mix", "fade", "pop"]),
            .init(.animation, "Word entrances", "Reveal sung words", .toggle, keys: ["custom.wordRevealByStyle"],
                  keywords: ["word by word", "animation", "reveal"]),
        ]
        let rest: [SettingsCatalogueEntry] = [
            .init(.animation, "Word entrances", "Faint next-word preview", .toggle, keys: ["custom.nextWordPreviewByStyle"],
                  keywords: ["preview", "upcoming word", "ghost"]),
            .init(.animation, "Word entrances", "Preview lead", .slider, keys: ["custom.nextWordPreviewLead"],
                  keywords: ["preview", "seconds", "before the next word"]),
            .init(.animation, "Instrumental breaks", "Show a glowing orb during instrumental breaks", .toggle,
                  keys: ["custom.showInterludes"], keywords: ["interlude", "orb", "dots", "instrumental"]),
            .init(.animation, "Instrumental breaks", "Orb style", .choice,
                  keys: [ThinkingOrb.styleKey], keywords: ["orb", "instrumental", "style", "chaotic"]),
            .init(.animation, "Word symbols and actions", "Show symbols beside listed words", .toggle,
                  keys: ["custom.wordSymbols.enabled"], keywords: ["emoji", "symbol", "icon", "word symbols"]),
            .init(.animation, "Word symbols and actions", "Animate listed action words", .toggle,
                  keys: ["custom.actionWords.enabled"], keywords: ["action", "animation", "spin", "motion"]),
            .init(.animation, "Word symbols and actions", "Word symbol list", .textField,
                  keys: ["custom.wordSymbols.words"], keywords: ["emoji", "symbol", "icon", "word list"]),
            .init(.animation, "Word symbols and actions", "Action word list", .textField,
                  keys: ["custom.actionWords.words"], keywords: ["action", "animation", "spin", "word list"]),
            .init(.animation, "Constellation", "Spread", .slider, keys: ["constellationSpread"],
                  keywords: ["constellation"]),
            .init(.animation, "Constellation", "Links", .slider, keys: ["constellationLinkOpacity"],
                  keywords: ["constellation", "lines", "connections"]),
            .init(.animation, "Echo", "Repeats", .slider, keys: ["echoStrength"],
                  keywords: ["echo", "strength"]),
        ]
        return entrances + mix + rest
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
        .init(.covers, "Framing", "Allow", .action, keys: ["motionExclusions"],
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
            .init(.lyrics, "Sources", "When none are found", .choice, keys: ["noLyrics"],
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
            .init(.lyrics, "Sources", "Lyrimuse lyrics folder", .action, keys: ["custom.lyrimuseFolder"],
                  keywords: ["local files", "choose folder", "exports", "local exports"]),
            .init(.lyrics, "Sources", "Use Default Lyrimuse Folder", .action, keys: ["custom.lyrimuseFolder"],
                  keywords: ["local files", "exports", "local exports"]),
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
        .init(.readings, "Romanisation", "Import Reading LRC…", .action,
              keywords: ["readings", "file", "pronunciation"]),
        .init(.readings, "Translation", "Show translations under lyrics", .toggle, keys: ["custom.showTranslations"],
              keywords: ["translation", "bilingual"]),
        .init(.readings, "Translation", MachineTranslationCopy.toggle, .toggle, keys: ["custom.machineTranslation"],
              keywords: ["machine translation", "apple translation", "translate"]),
        .init(.readings, "Translation", "Translate into", .choice, keys: ["custom.machineTranslationTarget"],
              keywords: ["target language", "translation", "system language"]),
        .init(.readings, "Translation", "Download Languages…", .action,
              keywords: ["translation", "languages"]),
        .init(.readings, "Translation", "Try Again", .action,
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

    private static let general: [SettingsCatalogueEntry] = {
        // One row per shortcut (`GlobalHotkeys.shortcuts`, main-actor
        // isolated; the tests check the two agree).
        let shortcuts: [SettingsCatalogueEntry] = GlobalHotkeys.Action.allCases.map { action in
            SettingsCatalogueEntry(.general, "Shortcuts", action.title, .toggle,
                                   keys: ["custom.globalShortcutsOff", "custom.globalShortcutsOn"],
                                   keywords: ["shortcut", "hotkey", "keyboard"])
        }
        let players: [SettingsCatalogueEntry] = [
            .init(.general, "Music players", "Player", .choice, keys: ["custom.player"],
                  keywords: ["automatic", "spotify", "apple music", "music app", "follow"], menuBar: true),
            .init(.general, "Music players", "Follow web players in browsers", .toggle, keys: ["custom.player.webPlayers"],
                  keywords: ["browser", "youtube music", "soundcloud", "amazon music", "safari", "chrome", "web"]),
            .init(.general, "Music players", localPlayersTitle, .toggle, keys: ["custom.player.localPlayers"],
                  keywords: ["local players", "local files", "vlc", "swinsian", "vox"]),
            .init(.general, "Music players", "Allow Access…", .action,
                  keywords: ["automation", "permission", "access"]),
            .init(.general, "Music players", "Open Privacy Settings", .action,
                  keywords: ["automation", "permission", "privacy & security"]),
        ]
        let onScreen: [SettingsCatalogueEntry] = [
            .init(.general, "On screen", "Hide lyrics", .toggle, keys: ["custom.lyricsHidden"],
                  keywords: ["show lyrics", "hidden"], menuBar: true),
            .init(.general, "On screen", "Pause wallpaper", .toggle, keys: ["custom.wallpaperPaused"],
                  keywords: ["freeze", "resume", "pause"], menuBar: true),
            .init(.general, "On screen", "Show the current line in the menu bar", .toggle, keys: ["custom.menuBarLyric"],
                  keywords: ["menu bar lyric", "status bar"]),
            .init(.general, "On screen", "Hide lyrics while a display is mirrored", .toggle,
                  keys: ["custom.hideLyricsWhileMirroring"], keywords: ["mirroring", "presenting", "projector", "tv"]),
            .init(.general, "On screen", LunalyrExtras.Titles.showLyricsAnyway, .action,
                  keywords: ["mirroring", "show anyway"], menuBar: true),
            .init(.general, "On screen", "Longest line", .slider, keys: ["custom.menuBarLyricLength"],
                  keywords: ["menu bar", "characters", "length"]),
            .init(.general, "On screen", "Show player on hover", .toggle, keys: ["custom.showControlsOnHover"],
                  keywords: ["desktop player", "controls", "hover", "pointer", "desktop"]),
            .init(.general, "Shortcuts", "Use global shortcuts", .toggle, keys: ["custom.globalShortcuts"],
                  keywords: ["hotkeys", "keyboard"]),
            .init(.general, "Shortcuts", "Keys", .choice, keys: ["custom.globalShortcutModifiers"],
                  keywords: ["modifiers", "control option command", "control shift command", "shortcut"]),
        ]
        let app: [SettingsCatalogueEntry] = [
            .init(.general, "App", "Launch at login", .toggle, store: "Settings.launchAtLogin",
                  keywords: ["startup", "login items", "open at login"]),
            .init(.general, "App", "Check for updates automatically", .toggle, store: "AppUpdates.automaticallyChecks",
                  keywords: ["updates", "daily", "automatic"]),
            .init(.general, "App", "Window style", .choice, keys: ["custom.settingsStyle"],
                  keywords: ["calm", "bold", "standard", "look", "theme", "appearance", "style", "windows"]),
            .init(.general, "App", "Check for Updates…", .action,
                  keywords: ["updates", "update", "version"], menuBar: true),
            .init(.general, "App", "Show Welcome Again…", .action,
                  keywords: ["welcome", "onboarding", "setup"]),
            .init(.general, "App", "About Lunalyr…", .action,
                  keywords: ["about", "version", "credits"]),
            .init(.general, "App", "Project & Downloads", .action,
                  keywords: ["website", "github", "download", "about"]),
            .init(.general, "App", "View Bundled Licences", .action,
                  keywords: ["licenses", "credits", "open source", "about"]),
        ]
        return players + onScreen + shortcuts + app
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
        .init(.share, "Listen along", "Let friends listen along", .toggle, keys: ["custom.listenSharing"],
              keywords: ["listen along", "share", "friends", "relay"]),
        .init(.share, "Listen along", "Copy Link", .action,
              keywords: ["listen along", "share link"]),
        .init(.share, "Listen along", "Share lyrics with listeners", .toggle, keys: ["custom.listenSharesLyrics"],
              keywords: ["listen along", "lyrics"]),
        .init(.share, "Listen along", "Show a Join button on Discord", .toggle, keys: ["custom.discordJoinParty"],
              keywords: ["listen along", "join"]),
        .init(.share, "Listen along", "Relay", .textField, keys: ["custom.listenRelayURL"],
              keywords: ["listen along", "listen-along relay", "relay", "url", "cloudflare worker", "server"]),
        .init(.share, "Lyric card", "Card layout", .choice, keys: ["custom.lyricCardComposition"],
              keywords: ["lyric card", "follow wallpaper", "layout"]),
        .init(.share, "Lyric card", "Open Lyric Card…", .action,
              keywords: ["lyric card", "gif", "mp4", "png", "share", "copy"], menuBar: true),
    ]

    private static let performance: [SettingsCatalogueEntry] = [
        .init(.performance, "Power", "Frame rate", .choice, keys: ["custom.animationRate"],
              keywords: ["fps", "animation rate", "smooth", "rendering"]),
        .init(.performance, "Power", "On battery", .choice, keys: ["onBattery"],
              keywords: ["power", "battery", "light effects"]),
        .init(.performance, "Power", "Follow macOS Low Power Mode", .toggle, keys: ["respectLowPower"],
              keywords: ["battery", "energy", "low power"]),
        .init(.performance, "Power", "Low power visuals", .toggle, keys: ["custom.lowPowerVisuals"],
              keywords: ["battery", "energy", "30 fps"]),
        .init(.performance, "Power", "Pause fully covered wallpapers", .toggle,
              keys: ["custom.pauseWhenCovered"], keywords: ["covered", "windows", "full screen", "occluded"]),
        .init(.performance, "Troubleshooting", "Open Privacy Settings…", .action,
              keywords: ["audio", "permission", "system audio recording"]),
        .init(.performance, "Troubleshooting", "Copy Audio Diagnostics", .action,
              keywords: ["audio setup", "bug report", "diagnostics"]),
        .init(.performance, "Troubleshooting", "Reconnect", .action,
              keywords: ["system audio", "needs attention"]),
        .init(.performance, "Troubleshooting", "Try Compatibility Capture", .action,
              keywords: ["system audio", "screen recording", "needs attention"]),
    ]
}
