import SwiftUI
import Foundation

// MARK: - The picture's objects
//
// The three Look pages (Wallpaper, Lyric style, Animated covers) are one
// sidebar row, "Look", which opens the picture. The page stored under
// `custom.settingsPage` still says which of the three is selected, so a page
// remembered by an earlier version, and every destination another surface
// opens, lands on the right object.

/// What is selected on the picture: each is one of the Look pages.
enum LookObject: String, CaseIterable, Identifiable {
    case wallpaper, lyrics, covers

    var id: String { rawValue }

    /// The object switch's names (spec §3).
    var title: String {
        switch self {
        case .wallpaper: return "Wallpaper"
        case .lyrics: return "Lyric style"
        case .covers: return "Animated covers"
        }
    }

    /// The page stored for this object.
    var page: SettingsPage {
        switch self {
        case .wallpaper: return .wallpaper
        case .lyrics: return .typography
        case .covers: return .covers
        }
    }

    /// The object a page shows, or nil for a page without a picture.
    init?(page: SettingsPage) {
        switch page {
        case .wallpaper: self = .wallpaper
        case .typography: self = .lyrics
        case .covers: self = .covers
        default: return nil
        }
    }
}

/// One row of the Settings sidebar: Look (the three Look pages), or a page.
enum SettingsSidebarRow: Hashable, Identifiable {
    case look
    case page(SettingsPage)

    var id: String {
        switch self {
        case .look: return "look"
        case .page(let page): return page.rawValue
        }
    }

    var title: String {
        switch self {
        case .look: return "Look"
        case .page(let page): return page.title
        }
    }

    /// The row a page is shown under.
    init(_ page: SettingsPage) {
        self = LookObject(page: page) == nil ? .page(page) : .look
    }

    /// The sidebar's groups below Look: a title and its rows.
    static let groups: [(title: String, rows: [SettingsSidebarRow])] = [
        (SettingsPage.Group.words.title, SettingsPage.Group.words.pages.map { .page($0) }),
        (SettingsPage.Group.app.title, SettingsPage.Group.app.pages.map { .page($0) }),
    ]

    /// Every row, in sidebar order.
    static var all: [SettingsSidebarRow] { [.look] + groups.flatMap(\.rows) }

    /// The pages a row stands for.
    var pages: [SettingsPage] {
        switch self {
        case .look: return LookObject.allCases.map(\.page)
        case .page(let page): return [page]
        }
    }

    /// Whether `query` finds this row: Look when any Look page matches.
    func matches(_ query: String) -> Bool {
        pages.contains { $0.matches(query) }
    }

    /// The page choosing this row opens. Look opens the object last shown
    /// (`lastLook`), unless a search names another one: then the first Look
    /// page the search finds, so the search lands on the right object.
    func destination(lastLook: SettingsPage, query: String = "") -> SettingsPage {
        switch self {
        case .page(let page):
            return page
        case .look:
            let trimmed = query.trimmingCharacters(in: .whitespaces)
            let fallback = LookObject(page: lastLook) == nil ? SettingsPage.wallpaper : lastLook
            guard !trimmed.isEmpty, !fallback.matches(trimmed) else { return fallback }
            return pages.first { $0.matches(trimmed) } ?? fallback
        }
    }
}

// MARK: - Lyric size by its handles

/// Dragging a corner of the lyric line's box sets Lyric size
/// (`Settings.lyricScale`): the box grows from its centre with the pointer,
/// so the scale follows the pointer's distance from the centre.
enum LyricSizeDrag {
    /// Lyric size's real range (the slider's).
    static let range: ClosedRange<Double> = 0.7...1.5
    /// The share of the lyric stage a line fills at size 1
    /// (`LyricStageFit.presentationScale`'s neutral share).
    static let neutralShare: CGFloat = 0.75

    /// The scale after a corner that started at `start` (from the box's
    /// centre) has been dragged by `translation`, from `startScale`.
    static func scale(startScale: Double, start: CGPoint, translation: CGSize) -> Double {
        let before = hypot(start.x, start.y)
        guard before > 1, startScale.isFinite else { return clamp(startScale) }
        let now = CGPoint(x: start.x + translation.width, y: start.y + translation.height)
        // Only the part along the corner's own diagonal counts, so dragging
        // past the centre doesn't flip the box.
        let along = (now.x * start.x + now.y * start.y) / before
        return clamp(startScale * Double(max(0, along) / before))
    }

    static func clamp(_ value: Double) -> Double {
        guard value.isFinite else { return 1 }
        return min(range.upperBound, max(range.lowerBound, value))
    }

    /// The line's box on a picture of `size`: the lyric stage
    /// (`LyricViewportMetrics.stageSize`, centred), filled to the neutral
    /// share times `scale`, never past the stage.
    static func box(in size: CGSize, scale: Double) -> CGRect {
        let stage = CGSize(width: size.width * 0.80, height: size.height * 0.72)
        let share = min(1, neutralShare * CGFloat(clamp(scale)))
        let width = stage.width * share
        let height = stage.height * share * 0.62
        return CGRect(x: (size.width - width) / 2, y: (size.height - height) / 2, width: width, height: height)
    }
}

// MARK: - Choice grids

/// How the tiles under the picture are laid out: `count` per row across
/// `width`, each `ratio` tall for its width.
enum ChoiceGridMetrics {
    static let gap: CGFloat = 9

    static func tile(count: Int, ratio: CGFloat, width: CGFloat, gap: CGFloat = gap) -> CGSize {
        let columns = CGFloat(max(1, count))
        let tileWidth = max(1, ((width - gap * (columns - 1)) / columns).rounded(.down))
        return CGSize(width: tileWidth, height: (tileWidth * ratio).rounded())
    }

    /// `items` in rows of `count`.
    static func rows<T>(_ items: [T], count: Int) -> [[T]] {
        let step = max(1, count)
        return stride(from: 0, to: items.count, by: step).map { Array(items[$0..<min($0 + step, items.count)]) }
    }
}

/// The wallpaper types in the order the canvas shows them (spec §3: the
/// five Lunalyr draws, then the three that start from something else).
enum LookChoices {
    static var wallpaperTypes: [Settings.BackgroundMode] {
        Settings.BackgroundMode.generated + Settings.BackgroundMode.own
    }

    /// Match the music (nil), then every layout, in the Layout menu's order
    /// (Calm, Bold, Playful).
    static var layouts: [LyricComposition?] {
        let grouped = LyricCompositionGroup.allCases.flatMap(\.layouts)
        return [nil] + grouped.map { Optional($0) }
    }

    static func layoutTitle(_ layout: LyricComposition?) -> String {
        layout?.title ?? "Match the music"
    }

    /// The typefaces, in the order the tiles show them.
    static let typefaces: [Settings.FontChoice] = [.auto, .manrope, .appleMusic, .sans, .serif, .slab, .soft, .google]

    /// The short name under a typeface tile.
    static func shortName(_ face: Settings.FontChoice) -> String {
        switch face {
        case .auto: return "Match"
        case .manrope: return "Manrope"
        case .appleMusic: return "SF Pro"
        case .sans: return "Avenir"
        case .serif: return "Charter"
        case .slab: return "Rockwell"
        case .soft: return "Rounded"
        case .google: return "Other…"
        }
    }
}

// MARK: - Floating options for the picture pages

private struct LookFloatingOptionsKey: EnvironmentKey {
    static var defaultValue: FloatingLyricsOptions? { nil }
}

extension EnvironmentValues {
    /// The floating lyrics' options the picture pages read: the app's own
    /// (`FloatingLyricsOptions.shared`) unless set, as UI snapshots set an
    /// isolated one so drawing the Floating view never writes the app's.
    var lookFloatingOptions: FloatingLyricsOptions? {
        get { self[LookFloatingOptionsKey.self] }
        set { self[LookFloatingOptionsKey.self] = newValue }
    }
}
