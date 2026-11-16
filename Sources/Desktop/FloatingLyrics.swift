import AppKit
import Combine
import SwiftUI

// MARK: - Where the lyrics are drawn

/// A lyric style: the desktop's or the floating lyrics'. Each display picks
/// where its lyrics go (`DisplayLyrics`), and both can be on screen at once,
/// each drawn in its own style (`LyricStyles`). `FloatingLyricsOptions.surface`
/// is the style Settings is editing.
enum LyricsSurface: String, CaseIterable, Identifiable {
    case desktop, floating
    var id: String { rawValue }
    var title: String { self == .desktop ? "Desktop style" : "Floating style" }
    /// Which style the lyric settings are editing, for the Settings label.
    var styleName: String { self == .desktop ? "Desktop" : "Floating" }
    var other: LyricsSurface { self == .desktop ? .floating : .desktop }
}

/// What one display shows: lyrics on its wallpaper, floating over its apps,
/// both, or the wallpaper alone.
enum DisplayLyrics: String, CaseIterable, Identifiable {
    case desktop, floating, both, none
    var id: String { rawValue }

    var title: String {
        switch self {
        case .desktop: return "Desktop lyrics"
        case .floating: return "Floating lyrics"
        case .both: return "Both"
        case .none: return "None (wallpaper only)"
        }
    }

    /// The wallpaper on this display draws lyrics.
    var onDesktop: Bool { self == .desktop || self == .both }
    /// A floating window goes on this display.
    var floats: Bool { self == .floating || self == .both }

    /// A display with no choice of its own: one plugged in for the first
    /// time shows the lyrics on its desktop, as every display did before.
    static let fallback: DisplayLyrics = .desktop

    /// The same, with the floating lyrics turned on or off.
    func floating(_ on: Bool) -> DisplayLyrics {
        switch (self, on) {
        case (.desktop, true), (.none, true): return .floating
        case (.floating, false), (.both, false): return .desktop
        default: return self
        }
    }

    /// The choices that keep someone's look from before displays had their
    /// own. Desktop: every display had the lyrics on its wallpaper. Floating:
    /// one window on the chosen display (the main one, first in `connected`,
    /// when that display is not connected or none was chosen) and no lyrics
    /// on any wallpaper. Nothing while no display is known.
    static func migrated(surface: String?, floatingDisplay: String, connected: [String]) -> [String: DisplayLyrics] {
        guard let main = connected.first else { return [:] }
        guard surface == LyricsSurface.floating.rawValue else {
            return Dictionary(connected.map { ($0, DisplayLyrics.desktop) }, uniquingKeysWith: { first, _ in first })
        }
        let floater = connected.contains(floatingDisplay) ? floatingDisplay : main
        return Dictionary(connected.map { ($0, $0 == floater ? DisplayLyrics.floating : .none) },
                          uniquingKeysWith: { first, _ in first })
    }
}

/// Where the floating window sits on its display.
enum FloatingPlacement: String, CaseIterable, Identifiable {
    case topLeft, top, topRight, centre, bottomLeft, bottom, bottomRight, custom
    var id: String { rawValue }

    var title: String {
        switch self {
        case .topLeft: return "Top left"
        case .top: return "Top, below the menu bar"
        case .topRight: return "Top right"
        case .centre: return "Centre"
        case .bottomLeft: return "Bottom left"
        case .bottom: return "Bottom, above the Dock"
        case .bottomRight: return "Bottom right"
        case .custom: return "Custom (drag to place)"
        }
    }

    static let presets: [FloatingPlacement] = allCases.filter { $0 != .custom }

    /// Column and row in the picker's 3 by 3 grid (0 left/top); nil for Custom.
    var cell: (column: Int, row: Int)? {
        switch self {
        case .topLeft: return (0, 0)
        case .top: return (1, 0)
        case .topRight: return (2, 0)
        case .centre: return (1, 1)
        case .bottomLeft: return (0, 2)
        case .bottom: return (1, 2)
        case .bottomRight: return (2, 2)
        case .custom: return nil
        }
    }
}

// MARK: - Separate styles for the two surfaces

/// The lyric style is kept twice, once per surface, so changing the floating
/// lyrics never changes the desktop's and the reverse.
///
/// The app's lyric settings (Settings, the colour, highlight and reading
/// stores, the backing-vocal and credit switches) stay the one place the
/// Settings pages change, and hold the style being edited. Switching the
/// style being edited stores the outgoing style under its own key and
/// loads the incoming one. The Floating style starts from the desktop's
/// (with the size reset, since Floating has a size of its own); from then
/// on the two never meet. Both draw at once: the style not being edited is
/// read from its key by `LyricStyles`.
@MainActor
struct LyricStyleProfiles {
    /// Every preference that belongs to a lyric style.
    static let keys: [String] = [
        // Settings
        "fontChoice", "googleFontFamily", Settings.installedFontFamilyKey, "lyricComposition", "constellationSpread", "constellationLinkOpacity",
        "custom.wordRevealByStyle", "custom.wordArrivalByStyle", "custom.nextWordPreviewByStyle",
        "custom.nextWordPreviewLead", "custom.wordEntranceMix", "echoStrength", "lyricScale", "custom.showInterludes", ThinkingOrb.styleKey,
        "custom.editorialTypography", "custom.editorialAmount", "custom.randomizeLyricFonts",
        // The two switches; their word lists are shared by both surfaces.
        Settings.wordSymbolsKey, Settings.actionWordsKey, Settings.actionWordStrengthKey,
        // Layout rotation, backing vocals, credits, the opening title
        LyricLayoutRotation.enabledKey, BackingVocals.settingKey, BackingVocals.styleKey, LyricCreditCues.settingKey,
        OpeningTitle.settingKey, OpeningTitle.coverKey,
        // Readings and translations shown
        RomanizationOptions.displayKey, LunalyrExtras.Keys.translations,
        // Text and highlight colours
        LyricColourOptions.Keys.mode, LyricColourOptions.Keys.speed, LyricColourOptions.Keys.strength,
        LyricColourOptions.Keys.custom, LyricColourOptions.Keys.pastel, LyricHighlightOptions.Keys.mode, LyricHighlightOptions.Keys.custom,
    ]

    /// Where each surface's style is kept while the other is in use.
    static func storeKey(_ surface: LyricsSurface) -> String { "custom.lyricStyle.\(surface.rawValue)" }

    /// What the floating style starts with that is not the desktop's: its
    /// size is `FloatingLyricsOptions.textSize`, so the wallpaper's Lyric
    /// size multiplier starts neutral.
    static let floatingSeed: [String: Any] = ["lyricScale": 1.0]

    let defaults: UserDefaults

    /// The style in use, as stored.
    func current() -> [String: Any] {
        var values: [String: Any] = [:]
        for key in Self.keys { if let value = defaults.object(forKey: key) { values[key] = value } }
        return values
    }

    func saved(_ surface: LyricsSurface) -> [String: Any]? {
        defaults.dictionary(forKey: Self.storeKey(surface))
    }

    /// `surface`'s style as kept, keeping it first when it never was: the
    /// Floating style starts from the style in use, less its size.
    @discardableResult
    func ensureSaved(_ surface: LyricsSurface) -> [String: Any] {
        if let saved = saved(surface) { return saved }
        var values = current()
        if surface == .floating { values.merge(Self.floatingSeed) { _, seed in seed } }
        defaults.set(values, forKey: Self.storeKey(surface))
        return values
    }

    /// Stores the style in use as `old`'s and puts `new`'s in its place.
    /// Returns true when `new` had no style yet and was seeded from `old`'s.
    @discardableResult
    func switchSurface(from old: LyricsSurface, to new: LyricsSurface) -> Bool {
        guard old != new else { return false }
        defaults.set(current(), forKey: Self.storeKey(old))
        if let incoming = saved(new) {
            for key in Self.keys {
                if let value = incoming[key] { defaults.set(value, forKey: key) } else { defaults.removeObject(forKey: key) }
            }
            return false
        }
        // The first time: start from the style just stored.
        if new == .floating {
            for (key, value) in Self.floatingSeed { defaults.set(value, forKey: key) }
        }
        defaults.set(current(), forKey: Self.storeKey(new))
        return true
    }
}

// MARK: - Floating options

/// The floating lyrics' own settings, apart from the lyric style: what each
/// display shows, which style Settings edits, the floating size, backdrop
/// and placement, and frames remembered per display. Injectable defaults
/// and displays, so tests never touch the real preferences or screens.
@MainActor
final class FloatingLyricsOptions: ObservableObject {
    static let shared = FloatingLyricsOptions(defaults: .standard, connected: FloatingFrame.connectedKeys(),
                                              reloadStyle: FloatingLyricsOptions.reloadSharedStyle)

    enum Keys {
        static let surface = "custom.lyricsSurface"
        static let textSize = "custom.floating.textSize"
        static let backdrop = "custom.floating.backdrop"
        static let fades = "custom.floating.fadeUnderPointer"
        static let placement = "custom.floating.placement"
        static let display = "custom.floating.display"
        static let frames = "custom.floating.frames"
        static let sizes = "custom.floating.sizes"
        /// Placements per display key, where one differs from `placement`.
        static let placements = "custom.floating.placements"
        /// What each display shows (`DisplayLyrics`), by display key.
        static let displays = "custom.displayLyrics"
    }

    /// Small, Medium, Large, as text sizes in points.
    enum SizePreset: Double, CaseIterable, Identifiable {
        case small = 30, medium = 44, large = 64
        var id: Double { rawValue }
        var title: String { self == .small ? "Small" : self == .medium ? "Medium" : "Large" }
    }

    static let textSizeRange: ClosedRange<Double> = 22...110
    static let defaultTextSize = SizePreset.medium.rawValue
    static let defaultBackdrop = 0.6

    /// The style Settings is editing: its values are the app's lyric
    /// settings, and the other style waits under its own key
    /// (`LyricStyleProfiles`). Before displays had their own choice this was
    /// where the lyrics went, which `DisplayLyrics.migrated` still reads.
    @Published var surface: LyricsSurface {
        didSet {
            guard surface != oldValue else { return }
            defaults.set(surface.rawValue, forKey: Keys.surface)
            LyricStyleProfiles(defaults: defaults).switchSurface(from: oldValue, to: surface)
            reloadStyle()
        }
    }
    /// The floating lyrics' text size in points.
    @Published var textSize: Double {
        didSet { defaults.set(textSize, forKey: Keys.textSize) }
    }
    /// How strong the soft shade behind the floating lyrics is, 0...1.
    @Published var backdrop: Double { didSet { defaults.set(backdrop, forKey: Keys.backdrop) } }
    /// Fade the window down while the pointer is over it.
    @Published var fadesUnderPointer: Bool { didSet { defaults.set(fadesUnderPointer, forKey: Keys.fades) } }
    /// The place on every display; picking one also forgets the places set
    /// per display by dragging.
    @Published var placement: FloatingPlacement {
        didSet {
            defaults.set(placement.rawValue, forKey: Keys.placement)
            if !placements.isEmpty { placements = [:] }
        }
    }
    /// Places set on one display (Custom, by dragging there).
    @Published private(set) var placements: [String: FloatingPlacement] {
        didSet { defaults.set(placements.mapValues(\.rawValue), forKey: Keys.placements) }
    }
    /// The display the one floating window used to go on: the key
    /// (`FloatingFrame.displayKey`), or empty for the main display. Read
    /// only to carry it into `displays`.
    @Published var display: String { didSet { defaults.set(display, forKey: Keys.display) } }
    /// What each display shows, by display key; a display not listed shows
    /// `DisplayLyrics.fallback`.
    @Published private(set) var displays: [String: DisplayLyrics] {
        didSet { defaults.set(displays.mapValues(\.rawValue), forKey: Keys.displays) }
    }
    /// Custom frames per display key, relative to that display's frame.
    @Published private(set) var frames: [String: [Double]] { didSet { defaults.set(frames, forKey: Keys.frames) } }
    /// Window sizes per display key, used by the presets.
    @Published private(set) var sizes: [String: [Double]] { didSet { defaults.set(sizes, forKey: Keys.sizes) } }

    private let defaults: UserDefaults
    private let reloadStyle: () -> Void

    /// `reloadStyle` makes the app's lyric settings read the style just
    /// swapped in; tests pass their own.
    /// `connected`: the keys of the displays connected now, main first, for
    /// the first launch with a choice per display (`DisplayLyrics.migrated`).
    init(defaults: UserDefaults, connected: [String] = [], reloadStyle: @escaping () -> Void = {}) {
        self.defaults = defaults
        self.reloadStyle = reloadStyle
        surface = LyricsSurface(rawValue: defaults.string(forKey: Keys.surface) ?? "") ?? .desktop
        textSize = Self.clampedTextSize(defaults.object(forKey: Keys.textSize) as? Double ?? Self.defaultTextSize)
        let backdrop = defaults.object(forKey: Keys.backdrop) as? Double ?? Self.defaultBackdrop
        self.backdrop = backdrop.isFinite ? min(1, max(0, backdrop)) : Self.defaultBackdrop
        fadesUnderPointer = defaults.object(forKey: Keys.fades) as? Bool ?? true
        placement = FloatingPlacement(rawValue: defaults.string(forKey: Keys.placement) ?? "") ?? .bottom
        display = defaults.string(forKey: Keys.display) ?? ""
        frames = defaults.dictionary(forKey: Keys.frames) as? [String: [Double]] ?? [:]
        sizes = defaults.dictionary(forKey: Keys.sizes) as? [String: [Double]] ?? [:]
        placements = (defaults.dictionary(forKey: Keys.placements) as? [String: String] ?? [:])
            .compactMapValues(FloatingPlacement.init(rawValue:))
        displays = (defaults.dictionary(forKey: Keys.displays) as? [String: String] ?? [:])
            .compactMapValues(DisplayLyrics.init(rawValue:))
        migrateDisplaysIfNeeded(connected: connected)
    }

    /// The first time displays are known: gives each connected display the
    /// choice that keeps the look it had (`DisplayLyrics.migrated`). Once.
    func migrateDisplaysIfNeeded(connected: [String]) {
        guard defaults.object(forKey: Keys.displays) == nil, !connected.isEmpty else { return }
        displays = DisplayLyrics.migrated(surface: defaults.string(forKey: Keys.surface), floatingDisplay: display,
                                          connected: connected)
    }

    // MARK: Per display

    func choice(for display: String) -> DisplayLyrics { displays[display] ?? .fallback }

    func setChoice(_ choice: DisplayLyrics, for display: String) {
        guard self.choice(for: display) != choice || displays[display] == nil else { return }
        displays[display] = choice
    }

    /// The displays among `connected` that get a floating window.
    func floatingDisplays(among connected: [String]) -> [String] {
        connected.filter { choice(for: $0).floats }
    }

    /// Floating lyrics are on somewhere among `connected`.
    func floats(among connected: [String]) -> Bool { !floatingDisplays(among: connected).isEmpty }

    /// The Floating quick toggle: off everywhere when any display floats
    /// (their lyrics go back on the desktop), else on for `target`, the
    /// display with the menu bar, its desktop lyrics moving into the window.
    func toggleFloating(connected: [String], target: String?) {
        if floats(among: connected) {
            for key in connected where choice(for: key).floats { setChoice(choice(for: key).floating(false), for: key) }
        } else if let key = target ?? connected.first {
            setChoice(choice(for: key).floating(true), for: key)
        }
    }

    /// Where the window goes on `display`.
    func placement(for display: String) -> FloatingPlacement { placements[display] ?? placement }

    static func clampedTextSize(_ value: Double) -> Double {
        value.isFinite ? min(textSizeRange.upperBound, max(textSizeRange.lowerBound, value)) : defaultTextSize
    }

    /// Settings is editing the Floating style.
    var isFloating: Bool { surface == .floating }

    func toggleSurface() { surface = surface.other }

    /// A frame placed by hand, relative to the display's frame.
    func customFrame(for display: String) -> CGRect? { frames[display].flatMap(FloatingFrame.decode) }
    func savedSize(for display: String) -> CGSize? { sizes[display].flatMap(FloatingFrame.decode)?.size }

    /// Keeps a frame dragged into place on `display` and makes that
    /// display's place Custom; the others keep theirs.
    func storeCustomFrame(_ relative: CGRect, for display: String) {
        frames[display] = FloatingFrame.encode(relative)
        sizes[display] = FloatingFrame.encode(CGRect(origin: .zero, size: relative.size))
        if placement(for: display) != .custom { placements[display] = .custom }
    }

    /// Forgets the sizes and frames set by hand; the presets go back to the
    /// size that follows the text size.
    func resetFrames() {
        frames = [:]
        sizes = [:]
        placements = [:]
        if placement == .custom { placement = .bottom }
    }

    /// The shared stores reread the lyric style just swapped in.
    static func reloadSharedStyle() {
        Settings.shared.reloadLyricStyle()
        RomanizationOptions.shared.reloadDisplay()
        LyricColourOptions.shared.reload()
        LyricHighlightOptions.shared.reload()
        LunalyrExtras.shared.reloadTranslations()
        LyricStyles.shared.followEditing()
    }
}

// MARK: - Frame maths

/// Where the floating lyrics' box goes on a display. Pure geometry, in
/// AppKit's global coordinates (origin bottom left). The window is the box
/// with a margin round it (`window(forBox:margin:)`).
enum FloatingFrame {
    /// Kept clear of the menu bar, the Dock, the notch and the screen edges.
    static let margin: CGFloat = 24
    static let minimumSize = CGSize(width: 220, height: 64)

    /// The area a floating window may use: the visible frame (no menu bar,
    /// no Dock), and below the notch even while the menu bar hides itself.
    static func usableArea(screenFrame: CGRect, visibleFrame: CGRect, safeTopInset: CGFloat) -> CGRect {
        var area = visibleFrame.intersection(screenFrame)
        if area.isNull || area.isEmpty { area = screenFrame }
        let top = min(area.maxY, screenFrame.maxY - max(0, safeTopInset))
        area.size.height = max(0, top - area.minY)
        return area
    }

    /// The window size that suits a text size: room for a line and its
    /// neighbours, a reading or a translation.
    static func defaultSize(textSize: CGFloat, in area: CGRect) -> CGSize {
        let width = min(max(textSize * 19, 420), max(minimumSize.width, area.width - 2 * margin))
        let height = min(max(textSize * 4.6, 150), max(minimumSize.height, area.height * 0.6))
        return CGSize(width: width.rounded(), height: height.rounded())
    }

    /// A preset's frame for a window of `size` in `area`.
    static func preset(_ placement: FloatingPlacement, size: CGSize, in area: CGRect) -> CGRect {
        let size = fitted(size, in: area)
        let cell = placement.cell ?? (1, 2)
        let inner = area.insetBy(dx: min(margin, max(0, (area.width - size.width) / 2)),
                                 dy: min(margin, max(0, (area.height - size.height) / 2)))
        let x: CGFloat
        switch cell.column {
        case 0: x = inner.minX
        case 2: x = inner.maxX - size.width
        default: x = area.midX - size.width / 2
        }
        let y: CGFloat
        switch cell.row {
        case 0: y = inner.maxY - size.height
        case 2: y = inner.minY
        default: y = area.midY - size.height / 2
        }
        return CGRect(x: x.rounded(), y: y.rounded(), width: size.width, height: size.height)
    }

    /// A size no smaller than the minimum and no larger than the area.
    static func fitted(_ size: CGSize, in area: CGRect) -> CGSize {
        let width = min(max(size.width, minimumSize.width), max(minimumSize.width, area.width))
        let height = min(max(size.height, minimumSize.height), max(minimumSize.height, area.height))
        return CGSize(width: width, height: height)
    }

    /// Keeps a frame on the area: shrunk to fit, then moved inside it.
    static func clamped(_ frame: CGRect, to area: CGRect) -> CGRect {
        let size = fitted(frame.size, in: area)
        let x = min(max(frame.minX, area.minX), area.maxX - size.width)
        let y = min(max(frame.minY, area.minY), area.maxY - size.height)
        return CGRect(x: x, y: y, width: size.width, height: size.height)
    }

    /// The window's frame for a placement on one display.
    /// - Parameters:
    ///   - custom: the frame placed by hand, relative to `screenFrame`'s origin.
    ///   - savedSize: the size last given on this display, for the presets.
    static func resolve(_ placement: FloatingPlacement, custom: CGRect?, savedSize: CGSize?, textSize: CGFloat,
                        screenFrame: CGRect, area: CGRect) -> CGRect {
        if placement == .custom, let custom {
            return clamped(custom.offsetBy(dx: screenFrame.minX, dy: screenFrame.minY), to: area)
        }
        let size = savedSize ?? defaultSize(textSize: textSize, in: area)
        return preset(placement == .custom ? .bottom : placement, size: size, in: area)
    }

    /// A frame relative to a display's frame, to keep across rearrangements.
    static func relative(_ frame: CGRect, to screenFrame: CGRect) -> CGRect {
        frame.offsetBy(dx: -screenFrame.minX, dy: -screenFrame.minY)
    }

    /// A move or resize by hand: `delta` is how far the pointer went since
    /// the drag began, in AppKit's coordinates (y up). Resizing pins the top
    /// left corner, as dragging a window's bottom-right corner does.
    static func dragged(_ start: CGRect, by delta: CGSize, resizing: Bool, in area: CGRect) -> CGRect {
        if resizing {
            let width = max(minimumSize.width, start.width + delta.width)
            let height = max(minimumSize.height, start.height - delta.height)
            let maxWidth = area.maxX - start.minX, maxHeight = start.maxY - area.minY
            let size = CGSize(width: min(width, max(minimumSize.width, maxWidth)),
                              height: min(height, max(minimumSize.height, maxHeight)))
            return CGRect(x: start.minX, y: start.maxY - size.height, width: size.width, height: size.height)
        }
        return clamped(start.offsetBy(dx: delta.width, dy: delta.height), to: area)
    }

    // MARK: The box and the window round it

    /// Every frame above, saved or placed, is the box: where the lyrics are
    /// laid out, the shade, and what a drag, a resize or the pointer acts
    /// on. The window itself is bigger by this see-through margin on every
    /// side, so what is drawn (pills, glows, letters that spin, hop or drip,
    /// dust) can spill past the box instead of being cut off at its edge.
    /// The margin takes no clicks and may run off the screen.
    static func spillMargin(textSize: CGFloat) -> CGFloat {
        let size = textSize.isFinite ? max(0, textSize) : 0
        return max(160, size * 1.5).rounded(.up)
    }

    /// The window's frame for a box: the box grown by `margin` on every side.
    static func window(forBox box: CGRect, margin: CGFloat) -> CGRect {
        box.insetBy(dx: -margin, dy: -margin)
    }

    /// The box inside a window made by `window(forBox:margin:)`.
    static func box(forWindow window: CGRect, margin: CGFloat) -> CGRect {
        window.insetBy(dx: margin, dy: margin)
    }

    static func encode(_ rect: CGRect) -> [Double] {
        [rect.minX, rect.minY, rect.width, rect.height].map(Double.init)
    }

    static func decode(_ values: [Double]) -> CGRect? {
        guard values.count == 4, values.allSatisfy(\.isFinite), values[2] > 0, values[3] > 0 else { return nil }
        return CGRect(x: values[0], y: values[1], width: values[2], height: values[3])
    }

    /// A display's lasting name: its UUID, which survives restarts and
    /// reconnections, else its number.
    static func displayKey(_ id: CGDirectDisplayID) -> String {
        if let uuid = CGDisplayCreateUUIDFromDisplayID(id)?.takeRetainedValue(),
           let text = CFUUIDCreateString(nil, uuid) as String? {
            return text
        }
        return "display-\(id)"
    }

    /// The keys of the displays connected now, the main one (with the menu
    /// bar) first.
    @MainActor static func connectedKeys() -> [String] {
        NSScreen.screens.compactMap(\.versoDisplayID).map(cachedKey)
    }

    @MainActor private static var keys: [CGDirectDisplayID: String] = [:]

    /// After displays change: a number may now be another display's.
    @MainActor static func forgetCachedKeys() { keys = [:] }

    /// `displayKey`, worked out once per display for the wallpaper's
    /// frequent asking.
    @MainActor static func cachedKey(_ id: CGDirectDisplayID) -> String {
        if let key = keys[id] { return key }
        let key = displayKey(id)
        keys[id] = key
        return key
    }

    /// The display the window goes on: the chosen one when connected, else
    /// the main display (the first, with the menu bar).
    static func chosenIndex(_ choice: String, among keys: [String]) -> Int? {
        guard !keys.isEmpty else { return nil }
        if !choice.isEmpty, let index = keys.firstIndex(of: choice) { return index }
        return 0
    }
}

// MARK: - When the window shows

enum FloatingLyricsVisibility {
    /// For one display's window.
    struct Input: Equatable {
        /// The display is set to Floating or Both.
        var floats: Bool
        /// Edit position is on: shown even with nothing playing, to place it.
        var editing = false
        /// Not hidden by Hide lyrics or by mirroring.
        var lyricsVisible = true
        var hasTrack = false
        var isPlaying = false
        /// How long the song has been paused, when it is.
        var pausedFor: TimeInterval?
        var hasTimedLines = false
        var hasPlainLines = false
        /// Every timed line is an instrumental marker.
        var allInstrumental = false
        var notFound = false
    }

    /// A pause shorter than this keeps the lyrics up.
    static let pauseGrace: TimeInterval = 10

    static func shows(_ input: Input) -> Bool {
        guard input.floats else { return false }
        if input.editing { return true }
        guard input.lyricsVisible, input.hasTrack, !input.notFound else { return false }
        let hasWords = (input.hasTimedLines && !input.allInstrumental) || input.hasPlainLines
        guard hasWords else { return false }
        if input.isPlaying { return true }
        return (input.pausedFor ?? .infinity) < pauseGrace
    }

    /// When a pause will hide the window, if it has not yet.
    static func hidesAfter(_ input: Input) -> TimeInterval? {
        guard shows(input), !input.isPlaying, !input.editing, let paused = input.pausedFor else { return nil }
        return max(0, pauseGrace - paused)
    }
}

/// How dark the backdrop behind the floating lyrics reads, for the text
/// colours: an unknown app window shaded by the floating backdrop, or, with
/// a music video as the wallpaper, that video as the wallpaper's own lyrics
/// take it.
enum FloatingBackdrop {
    /// Most an app behind is darkened, at full strength.
    static let maximumShade = 0.72

    /// `behind`: what lies under the shade (`behind(mode:scrim:videoDim:)`).
    static func luminance(strength: Double, reduceTransparency: Bool,
                          behind: Double = LyricLegibility.unknownBackdropLuminance) -> Double {
        let shade = reduceTransparency ? 0.86 : maximumShade * min(1, max(0, strength))
        return LyricLegibility.darkened(min(1, max(0, behind)), by: shade)
    }

    /// What the floating lyrics are taken to float over. An app window is
    /// unknown (`unknownBackdropLuminance`, on the dark-text line with no
    /// shade). Over a music video wallpaper they take the video as the
    /// desktop lyrics do (`LyricBackdrop`: unmeasured footage, its dimming
    /// and the scrim, never guessed light enough for dark text): with
    /// the shade turned down, the words turned dark and muddy with a pale
    /// glow over the video.
    static func behind(mode: Settings.BackgroundMode, scrim: Double, videoDim: Double) -> Double {
        guard mode == .musicVideo else { return LyricLegibility.unknownBackdropLuminance }
        return LyricBackdrop.beforeBacking(mode: .musicVideo, motion: false, palette: [], scrim: scrim,
                                           measured: nil, videoDim: videoDim)
    }
}
