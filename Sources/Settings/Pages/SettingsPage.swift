import SwiftUI

/// A page of the Settings window: its sidebar row, its title (the window's
/// title while it shows) and where other surfaces' destinations land.
///
/// Raw values are what `custom.settingsPage` stores. They are the names the
/// pages had before the rework, so a page remembered by an earlier version
/// still opens; General is the only new one.
enum SettingsPage: String, CaseIterable, Identifiable {
    case wallpaper, typography, covers, lyrics, readings, general, extras, performance, about

    var id: String { rawValue }

    var title: String {
        switch self {
        case .wallpaper: return "Wallpaper"
        case .typography: return "Lyric style"
        case .covers: return "Animated covers"
        case .lyrics: return "Timing & sources"
        case .readings: return "Romanisation"
        case .general: return "General"
        case .extras: return "Menu bar & shortcuts"
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
        case .extras: return "command"
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
        case .performance: return .green
        }
    }

    /// Every page but About changes or explains what the desktop shows, so
    /// it keeps the live preview in view beside its controls.
    var showsStage: Bool { self != .about }

    /// The sidebar's three sections.
    enum Group: String, CaseIterable, Identifiable {
        case look, lyrics, lunalyr

        var id: String { rawValue }

        var title: String {
            switch self {
            case .look: return "Look"
            case .lyrics: return "Lyrics"
            case .lunalyr: return "App"
            }
        }

        var pages: [SettingsPage] {
            switch self {
            case .look: return [.wallpaper, .typography, .covers]
            case .lyrics: return [.lyrics, .readings]
            case .lunalyr: return [.general, .extras, .performance, .about]
            }
        }
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
        case .general, .players: self = .general
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
