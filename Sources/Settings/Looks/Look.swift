import Foundation

/// The settings a Look stores, read and written through the same stores the
/// Settings window uses.
///
/// A Look covers exactly these preferences, and nothing else:
///
/// | Field | Store | UserDefaults key |
/// |---|---|---|
/// | `wallpaper` | `Settings.backgroundMode` | `backgroundMode` |
/// | `layout` | `Settings.lyricComposition` (nil = Match the music) | `lyricComposition` |
/// | `typeface` | `Settings.fontChoice`, via `selectFontChoice(_:)` / `applyGoogleFont(family:)` | `fontChoice` |
/// | `fontFamily` | `Settings.googleFontFamily`, only when `typeface == .google` | `googleFontFamily` |
/// | `entrance` | `Settings.wordArrivalByStyle[layout key]` -- the entry `arrival(in:)` reads for the Look's layout (`"automatic"` for Match the music) | `custom.wordArrivalByStyle` |
/// | `entranceMix` | `Settings.wordEntranceMix`, only when `entrance == .mix` | `custom.wordEntranceMix` |
/// | `highlight` | `LyricHighlightOptions.mode` (the colour treatment) | `custom.lyricHighlightMode` |
/// | `highlightCustomHex` | `LyricHighlightOptions.customHex`, only when `highlight == .custom` | `custom.lyricHighlightCustom` |
///
/// Deliberately left out:
/// - Lyric size (`lyricScale`) and colour strength (`vividness`,
///   `scrimStrength`, `LyricColourOptions.strength`): they are readability
///   and per-display choices, so picking a Look never makes text smaller or
///   fainter than the user set it.
/// - The text colour mode (`LyricColourOptions.mode`): the designer's
///   "Colour" column names highlight treatments ("Album accent",
///   "Complement lyrics" are `LyricHighlightMode` titles), and the text
///   colour stays the user's.
/// - Pinned shader / pattern / visualiser styles, the imported wallpaper
///   file, effect speed, editorial typography: the Look's wallpaper type is
///   chosen, and within it the track (or the user's pin) still picks.
///
/// Every lyric-style key above belongs to `LyricStyleProfiles`, so with
/// floating lyrics in use a Look styles the floating lyrics (the style in
/// use), and the wallpaper type applies to the desktop as always.
struct LookValues: Equatable, Hashable {
    var wallpaper: Settings.BackgroundMode
    /// nil is Match the music.
    var layout: LyricComposition?
    var typeface: Settings.FontChoice
    /// The Google or installed family, when `typeface` is `.google`.
    var fontFamily: String?
    /// The entrance for the Look's layout. nil only in a user Look saved
    /// while Match the music had no entrance of its own (each layout used
    /// its own pick); applying it clears that entry again.
    var entrance: WordArrival?
    /// The custom mix's entrances (raw values), when `entrance` is `.mix`.
    var entranceMix: [String]?
    var highlight: LyricHighlightMode
    /// `#RRGGBB`, when `highlight` is `.custom`.
    var highlightCustomHex: String?

    init(wallpaper: Settings.BackgroundMode, layout: LyricComposition?, typeface: Settings.FontChoice,
         fontFamily: String? = nil, entrance: WordArrival?, entranceMix: [String]? = nil,
         highlight: LyricHighlightMode, highlightCustomHex: String? = nil) {
        self.wallpaper = wallpaper
        self.layout = layout
        self.typeface = typeface
        self.fontFamily = typeface == .google ? fontFamily : nil
        self.entrance = entrance
        self.entranceMix = entrance == .mix ? entranceMix : nil
        self.highlight = highlight
        self.highlightCustomHex = highlight == .custom ? highlightCustomHex?.uppercased() : nil
    }

    /// The `wordArrivalByStyle` entry the entrance lives in -- the one
    /// `Settings.arrival(in:)` reads once `layout` is chosen.
    var entranceKey: String { layout?.rawValue ?? "automatic" }

    /// The typeface as a short name: "SF Pro Display", "Avenir Next".
    var typefaceName: String { Self.typefaceName(typeface, family: fontFamily) }

    static func typefaceName(_ choice: Settings.FontChoice, family: String?) -> String {
        switch choice {
        case .manrope: return "Manrope"
        case .appleMusic: return "SF Pro Display"
        case .auto: return "Match the music"
        case .sans: return "Avenir Next"
        case .serif: return "Charter"
        case .slab: return "Rockwell"
        case .soft: return "SF Rounded"
        case .google:
            let name = family?.trimmingCharacters(in: .whitespaces) ?? ""
            return name.isEmpty ? "Custom font" : name
        }
    }

    var layoutTitle: String { layout?.title ?? "Match the music" }
    var entranceTitle: String { entrance?.title ?? "Each layout’s own" }

    /// "Visualisers · Cascade · Avenir Next". Repeats collapse, so
    /// Match the music for both wallpaper and layout reads once.
    var summary: String {
        var parts: [String] = []
        for part in [wallpaper.title, layoutTitle, typefaceName] where parts.last != part {
            parts.append(part)
        }
        return parts.joined(separator: " · ")
    }
}

/// A named bundle of `LookValues`, chosen together.
struct Look: Identifiable, Equatable, Hashable {
    let id: String
    var name: String
    let isBuiltIn: Bool
    var values: LookValues

    var summary: String { values.summary }
    // For drawing a picture of the Look.
    var wallpaper: Settings.BackgroundMode { values.wallpaper }
    var layout: LyricComposition? { values.layout }
    var typeface: Settings.FontChoice { values.typeface }
    var entrance: WordArrival? { values.entrance }
    /// The palette treatment: where highlight tiles take their colour.
    var palette: LyricHighlightMode { values.highlight }
}

// MARK: - Built-ins

extension Look {
    /// The designer's six, in order. Typefaces that are not built-in
    /// choices take the nearest one: Futura → Manrope (geometric sans),
    /// New York → Charter (Serif). SF Pro Display is the Apple Music
    /// choice; Avenir Next is Sans.
    static let builtIns: [Look] = [
        Look(id: "builtin.matched", name: "Matched", isBuiltIn: true,
             values: LookValues(wallpaper: .auto, layout: nil, typeface: .appleMusic,
                                entrance: .automatic, highlight: .album)),
        Look(id: "builtin.quiet", name: "Quiet", isBuiltIn: true,
             values: LookValues(wallpaper: .desktop, layout: .spoken, typeface: .appleMusic,
                                entrance: .fade, highlight: .complement)),
        Look(id: "builtin.nightDrive", name: "Night drive", isBuiltIn: true,
             values: LookValues(wallpaper: .visualizer, layout: .cascade, typeface: .sans,
                                entrance: .glide, highlight: .album)),
        Look(id: "builtin.poster", name: "Poster", isBuiltIn: true,
             values: LookValues(wallpaper: .colourOnly, layout: .broadside, typeface: .manrope,
                                entrance: .pop, highlight: .complement)),
        Look(id: "builtin.starChart", name: "Star chart", isBuiltIn: true,
             values: LookValues(wallpaper: .pattern, layout: .constellation, typeface: .serif,
                                entrance: .lift, highlight: .complement)),
        Look(id: "builtin.collage", name: "Collage", isBuiltIn: true,
             values: LookValues(wallpaper: .shader, layout: .mosaic, typeface: .manrope,
                                entrance: .cutoutHighlight, highlight: .album)),
    ]
}

// MARK: - Storage

/// A user Look as kept under `custom.looks.saved`: raw values, so an option
/// a later version drops loses only that Look, never the list.
struct StoredLook: Codable, Equatable {
    var id: String
    var name: String
    var wallpaper: String
    var layout: String?
    var typeface: String
    var fontFamily: String?
    var entrance: String?
    var entranceMix: [String]?
    var highlight: String
    var highlightCustomHex: String?

    init(_ look: Look) {
        id = look.id
        name = look.name
        wallpaper = look.values.wallpaper.rawValue
        layout = look.values.layout?.rawValue
        typeface = look.values.typeface.rawValue
        fontFamily = look.values.fontFamily
        entrance = look.values.entrance?.rawValue
        entranceMix = look.values.entranceMix
        highlight = look.values.highlight.rawValue
        highlightCustomHex = look.values.highlightCustomHex
    }

    /// nil when any stored option is no longer a real one.
    var look: Look? {
        guard let wallpaper = Settings.BackgroundMode(rawValue: wallpaper),
              let typeface = Settings.FontChoice(rawValue: typeface),
              let highlight = LyricHighlightMode(rawValue: highlight) else { return nil }
        var chosenLayout: LyricComposition?
        if let layout {
            guard let parsed = LyricComposition(rawValue: layout) else { return nil }
            chosenLayout = parsed
        }
        var chosenEntrance: WordArrival?
        if let entrance {
            guard let parsed = WordArrival(rawValue: entrance) else { return nil }
            chosenEntrance = parsed
        }
        let values = LookValues(wallpaper: wallpaper, layout: chosenLayout, typeface: typeface,
                                fontFamily: fontFamily, entrance: chosenEntrance, entranceMix: entranceMix,
                                highlight: highlight, highlightCustomHex: highlightCustomHex)
        return Look(id: id, name: name, isBuiltIn: false, values: values)
    }
}
