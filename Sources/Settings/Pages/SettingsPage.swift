import SwiftUI

/// A page of the Settings window: its sidebar row, its title (the window's
/// title while it shows) and where other surfaces' destinations land.
///
/// Raw values are what `custom.settingsPage` stores. They are the names the
/// pages had before the rework, so a page remembered by an earlier version
/// still opens; General is the only new one.
enum SettingsPage: String, CaseIterable, Identifiable {
    case wallpaper, typography, covers, lyrics, readings, players, general, extras, share, performance, about

    var id: String { rawValue }

    var title: String {
        switch self {
        case .wallpaper: return "Wallpaper"
        case .typography: return "Lyric style"
        case .covers: return "Animated covers"
        case .lyrics: return "Timing & sources"
        case .readings: return "Romanisation"
        case .players: return "Music players"
        case .general: return "General"
        case .extras: return "Menu bar & shortcuts"
        case .share: return "Discord & lyric card"
        case .performance: return "Performance"
        case .about: return "About"
        }
    }

    /// The white symbol on the row's badge (`SettingsIcon`).
    var symbol: String {
        switch self {
        case .wallpaper: return "photo.artframe"
        case .typography: return "textformat"
        case .covers: return "square.stack.fill"
        case .lyrics: return "clock.fill"
        case .readings: return "character.book.closed.fill"
        case .general: return "gearshape.fill"
        case .players: return "music.note"
        case .extras: return "command"
        case .share: return "square.and.arrow.up.fill"
        case .performance: return "leaf.fill"
        case .about: return "info.circle.fill"
        }
    }

    /// The badge's colour, as System Settings colours its own.
    var tint: Color {
        switch self {
        case .wallpaper: return .indigo
        case .typography: return .pink
        case .covers: return .orange
        case .lyrics: return .blue
        case .readings: return .teal
        case .general, .extras, .about: return .gray
        case .share: return .purple
        case .players: return .red
        case .performance: return .green
        }
    }

    /// Every page but About changes or explains what the desktop shows, so
    /// it keeps the live preview in view beside its controls.
    var showsStage: Bool { self != .about }

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
            case .look: return [.wallpaper, .typography, .covers]
            case .words: return [.lyrics, .readings]
            case .app: return [.players, .general, .extras, .share, .performance, .about]
            }
        }
    }

    /// What a person might type to find this page: setting names and the
    /// words they use for them.
    var searchTerms: [String] {
        switch self {
        case .wallpaper: return ["background", "visualiser", "visualizer", "music response", "desktop picture", "image", "video", "gradient", "darkening", "effects", "audio", "sensitivity"]
        case .typography: return ["layout", "font", "typeface", "size", "colour", "color", "highlight", "text", "style", "echo", "cascade", "editorial", "animation", "entrance", "align"]
        case .covers: return ["animated cover", "album art", "motion artwork", "apple music"]
        case .lyrics: return ["timing", "offset", "late", "early", "sync", "sources", "lyrics", "search", "lrclib", "musixmatch", "word by word"]
        case .readings: return ["romanisation", "romanization", "pinyin", "translation", "japanese", "korean", "readings"]
        case .players: return ["player", "spotify", "music app", "automation", "permission", "access", "apple music", "tidal"]
        case .general: return ["appearance", "look", "theme", "calm", "bold", "launch at login", "startup", "updates", "welcome", "version"]
        case .extras: return ["menu bar", "shortcut", "hotkey", "keyboard", "hide lyrics", "pause wallpaper", "mirroring", "line", "lyric card"]
        case .share: return ["discord", "rich presence", "lyric card", "gif", "mp4", "png", "share", "copy", "save", "status"]
        case .performance: return ["performance", "battery", "power", "frame rate", "gpu", "low power", "energy", "fps"]
        case .about: return ["about", "credits", "licence", "license", "version", "website"]
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
        case .menuBarAndShortcuts: self = .extras
        case .players: self = .players
        case .general: self = .general
        case .about: self = .about
        }
    }
}

/// A section inside a page that a destination scrolls to.
enum SettingsAnchor: String, CaseIterable {
    /// Wallpaper › Music response (Visualisers).
    case musicResponse
    /// General › Music players.
    case players

    init?(_ destination: SettingsDestination) {
        switch destination {
        case .musicResponse: self = .musicResponse
        case .players: self = .players
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
