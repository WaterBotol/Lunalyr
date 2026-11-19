import SwiftUI

/// A page of the Settings window: its sidebar row, its title (the window's
/// title while it shows) and where other surfaces' destinations land.
///
/// Raw values are what `custom.settingsPage` stores. They are the names the
/// pages had before the reworks, so a page remembered by an earlier version
/// still opens: Text keeps Lyric style's `typography`, Timing & sources
/// `lyrics`, Discord & sharing `share`. Pages folded into others open where
/// they went (`init?(rawValue:)`: Music players, Menu bar & shortcuts and
/// About open General).
enum SettingsPage: CaseIterable, Identifiable, RawRepresentable {
    case wallpaper, typography, animation, covers, lyrics, readings, general, share, performance

    init?(rawValue: String) {
        switch rawValue {
        case "wallpaper": self = .wallpaper
        case "typography": self = .typography
        case "animation": self = .animation
        case "covers": self = .covers
        case "lyrics": self = .lyrics
        case "readings": self = .readings
        case "general": self = .general
        case "share": self = .share
        case "performance": self = .performance
        // Pages folded into General.
        case "players", "extras", "about": self = .general
        default: return nil
        }
    }

    var rawValue: String {
        switch self {
        case .wallpaper: return "wallpaper"
        case .typography: return "typography"
        case .animation: return "animation"
        case .covers: return "covers"
        case .lyrics: return "lyrics"
        case .readings: return "readings"
        case .general: return "general"
        case .share: return "share"
        case .performance: return "performance"
        }
    }

    /// Every value `custom.settingsPage` has held, today's and earlier
    /// versions'.
    static let storedNames = ["wallpaper", "typography", "animation", "covers", "lyrics", "readings", "general",
                              "share", "performance", "players", "extras", "about"]

    var id: String { rawValue }

    var title: String {
        switch self {
        case .wallpaper: return "Wallpaper"
        case .typography: return "Text"
        case .animation: return "Animation"
        case .covers: return "Animated covers"
        case .lyrics: return "Timing & sources"
        case .readings: return "Romanisation & translation"
        case .general: return "General"
        case .share: return "Discord & sharing"
        case .performance: return "Performance"
        }
    }

    /// The page's SF Symbol.
    var symbol: String {
        switch self {
        case .wallpaper: return "photo.artframe"
        case .typography: return "textformat"
        case .animation: return "sparkles"
        case .covers: return "square.stack.fill"
        case .lyrics: return "clock.fill"
        case .readings: return "character.book.closed.fill"
        case .general: return "gearshape.fill"
        case .share: return "square.and.arrow.up.fill"
        case .performance: return "leaf.fill"
        }
    }

    /// Every page changes or explains what the desktop shows, so it keeps
    /// the live preview in view beside its controls.
    var showsStage: Bool { true }

    /// The sidebar's three sections.
    enum Group: String, CaseIterable, Identifiable {
        case look, words, app

        var id: String { rawValue }

        var title: String {
            switch self {
            case .look: return "Look"
            case .words: return "Words"
            case .app: return "App"
            }
        }

        var pages: [SettingsPage] {
            switch self {
            case .look: return [.wallpaper, .typography, .animation, .covers]
            case .words: return [.lyrics, .readings]
            case .app: return [.general, .share, .performance]
            }
        }
    }

    /// What a person might type to find this page: setting names and the
    /// words they use for them.
    var searchTerms: [String] {
        switch self {
        case .wallpaper: return ["background", "visualiser", "visualizer", "music response", "desktop picture", "image", "video", "gradient", "darkening", "effects", "audio", "sensitivity", "adjustments"]
        case .typography: return ["layout", "font", "typeface", "size", "colour", "color", "highlight", "text", "style", "editorial", "align", "lyric style", "readability", "legibility", "floating", "place"]
        case .animation: return ["animation", "entrance", "reveal", "word by word", "preview", "orb", "instrumental", "interlude", "echo", "constellation", "symbols", "action words", "motion"]
        case .covers: return ["animated cover", "album art", "motion artwork", "apple music"]
        case .lyrics: return ["timing", "offset", "late", "early", "sync", "sources", "lyrics", "search", "lrclib", "musixmatch", "word by word"]
        case .readings: return ["romanisation", "romanization", "pinyin", "translation", "translate", "japanese", "korean", "readings"]
        case .general: return ["appearance", "look", "theme", "calm", "bold", "launch at login", "startup", "updates", "welcome", "version",
                               "player", "spotify", "music app", "automation", "permission", "access", "tidal",
                               "menu bar", "shortcut", "hotkey", "keyboard", "hide lyrics", "pause wallpaper", "mirroring", "line",
                               "about", "credits", "licence", "license", "website", "on screen"]
        case .share: return ["discord", "rich presence", "lyric card", "gif", "mp4", "png", "share", "copy", "save", "status", "listen along", "sharing"]
        case .performance: return ["performance", "battery", "power", "frame rate", "gpu", "low power", "energy", "fps", "troubleshooting", "diagnostics"]
        }
    }

    /// Whether `query` (several words allowed) finds this page: by its
    /// title and search terms, or by any one setting on it
    /// (`SettingsCatalogue`), by that setting's label, section or keywords.
    func matches(_ query: String) -> Bool {
        let words = query.lowercased().split(separator: " ").map(String.init)
        guard !words.isEmpty else { return true }
        let haystack = ([title] + searchTerms).joined(separator: " ").lowercased()
        if words.allSatisfy({ haystack.contains($0) }) { return true }
        let folded = SettingsCatalogue.searchWords(query)
        guard !folded.isEmpty else { return false }
        return SettingsCatalogue.entries(on: self).contains { $0.matches(words: folded) }
    }

    /// The page another surface's destination opens.
    init(_ destination: SettingsDestination) {
        switch destination {
        case .wallpaper, .musicResponse: self = .wallpaper
        case .lyricStyle: self = .typography
        case .animatedCovers: self = .covers
        case .timing: self = .lyrics
        case .readings: self = .readings
        case .performance: self = .performance
        case .menuBarAndShortcuts, .players, .general, .about: self = .general
        }
    }
}

/// A section inside a page that a destination scrolls to.
enum SettingsAnchor: String, CaseIterable {
    /// Wallpaper › Music response (Visualisers).
    case musicResponse
    /// General › Music players.
    case players
    /// General › On screen (hiding, pausing, the menu-bar line).
    case onScreen
    /// General › App (startup, updates, About).
    case app

    init?(_ destination: SettingsDestination) {
        switch destination {
        case .musicResponse: self = .musicResponse
        case .players: self = .players
        case .menuBarAndShortcuts: self = .onScreen
        case .about: self = .app
        default: return nil
        }
    }
}

/// A scroll request for the page showing: numbered, so asking for the same
/// section twice scrolls there again.
struct SettingsScrollRequest: Equatable {
    let anchor: SettingsAnchor
    let serial: Int
}

extension View {
    /// Scrolls a page's `Form` to `request` when it is made, and again
    /// whenever a new one arrives. The anchored section carries
    /// `.id(anchor)`.
    func scrollsToSettingsAnchor(_ request: SettingsScrollRequest?) -> some View {
        modifier(SettingsAnchorScroller(request: request))
    }
}

private struct SettingsAnchorScroller: ViewModifier {
    let request: SettingsScrollRequest?
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    func body(content: Content) -> some View {
        ScrollViewReader { proxy in
            content
                .onAppear {
                    guard let request else { return }
                    // After the form has laid out its sections once.
                    DispatchQueue.main.async { proxy.scrollTo(request.anchor, anchor: .top) }
                }
                .onChange(of: request) { _, request in
                    guard let request else { return }
                    withAnimation(LunalyrDesign.Motion.animation(LunalyrDesign.Motion.standard, reduceMotion: reduceMotion)) {
                        proxy.scrollTo(request.anchor, anchor: .top)
                    }
                }
        }
    }
}
