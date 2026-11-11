import AppKit
import Combine
import SwiftUI

// MARK: - Where the lyrics are drawn

/// Where the lyrics are drawn: on the desktop wallpaper (under every app) or
/// in a floating window over every app. Never both: in Floating the
/// wallpaper keeps its background, video or visualiser but draws no lyrics.
enum LyricsSurface: String, CaseIterable, Identifiable {
    case desktop, floating
    var id: String { rawValue }
    var title: String { self == .desktop ? "On the desktop" : "Floating over apps" }
    /// Which style the lyric settings are editing, for the Settings label.
    var styleName: String { self == .desktop ? "Desktop" : "Floating" }
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
/// renderer and the Settings pages read, and always hold the style of the
/// surface in use. Switching surface stores the outgoing style under its
/// own key and loads the incoming one. The first switch to Floating starts
/// it from the desktop's style (with the size reset, since Floating has a
/// size of its own); from then on the two never meet. Only one surface
/// draws lyrics at a time, so nothing ever needs both at once.
@MainActor
struct LyricStyleProfiles {
    /// Every preference that belongs to a lyric style.
    static let keys: [String] = [
        // Settings
        "fontChoice", "googleFontFamily", "lyricComposition", "constellationSpread", "constellationLinkOpacity",
        "custom.wordRevealByStyle", "custom.wordArrivalByStyle", "custom.nextWordPreviewByStyle",
        "custom.nextWordPreviewLead", "custom.wordEntranceMix", "echoStrength", "lyricScale", "custom.showInterludes",
        "custom.editorialTypography", "custom.editorialAmount", "custom.randomizeLyricFonts",
        // Layout rotation, backing vocals, credits, the opening title
        LyricLayoutRotation.enabledKey, BackingVocals.settingKey, BackingVocals.styleKey, LyricCreditCues.settingKey,
        OpeningTitle.settingKey, OpeningTitle.coverKey,
        // Readings and translations shown
        RomanizationOptions.displayKey, LunalyrExtras.Keys.translations,
        // Text and highlight colours
        LyricColourOptions.Keys.mode, LyricColourOptions.Keys.speed, LyricColourOptions.Keys.strength,
        LyricColourOptions.Keys.custom, LyricHighlightOptions.Keys.mode, LyricHighlightOptions.Keys.custom,
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

/// The floating lyrics' own settings, apart from the lyric style: which
/// surface is in use, the floating size, backdrop, placement and display,
/// and frames remembered per display. Injectable defaults, so tests never
/// touch the real preferences.
@MainActor
final class FloatingLyricsOptions: ObservableObject {
    static let shared = FloatingLyricsOptions(defaults: .standard, reloadStyle: FloatingLyricsOptions.reloadSharedStyle)

    enum Keys {
        static let surface = "custom.lyricsSurface"
        static let textSize = "custom.floating.textSize"
        static let backdrop = "custom.floating.backdrop"
        static let fades = "custom.floating.fadeUnderPointer"
        static let placement = "custom.floating.placement"
        static let display = "custom.floating.display"
        static let frames = "custom.floating.frames"
        static let sizes = "custom.floating.sizes"
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

    /// Desktop by default, so nothing changes until someone picks Floating.
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
    @Published var placement: FloatingPlacement {
        didSet { defaults.set(placement.rawValue, forKey: Keys.placement) }
    }
    /// The display's key (`FloatingFrame.displayKey`), or empty for the main display.
    @Published var display: String { didSet { defaults.set(display, forKey: Keys.display) } }
    /// Custom frames per display key, relative to that display's frame.
    @Published private(set) var frames: [String: [Double]] { didSet { defaults.set(frames, forKey: Keys.frames) } }
    /// Window sizes per display key, used by the presets.
    @Published private(set) var sizes: [String: [Double]] { didSet { defaults.set(sizes, forKey: Keys.sizes) } }

    private let defaults: UserDefaults
    private let reloadStyle: () -> Void

    /// `reloadStyle` makes the app's lyric settings read the style just
    /// swapped in; tests pass their own.
    init(defaults: UserDefaults, reloadStyle: @escaping () -> Void = {}) {
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
    }

    static func clampedTextSize(_ value: Double) -> Double {
        value.isFinite ? min(textSizeRange.upperBound, max(textSizeRange.lowerBound, value)) : defaultTextSize
    }

    var isFloating: Bool { surface == .floating }

    func toggleSurface() { surface = surface == .floating ? .desktop : .floating }

    /// A frame placed by hand, relative to the display's frame.
    func customFrame(for display: String) -> CGRect? { frames[display].flatMap(FloatingFrame.decode) }
    func savedSize(for display: String) -> CGSize? { sizes[display].flatMap(FloatingFrame.decode)?.size }

    /// Keeps a frame dragged into place on `display` and switches to Custom.
    func storeCustomFrame(_ relative: CGRect, for display: String) {
        frames[display] = FloatingFrame.encode(relative)
        sizes[display] = FloatingFrame.encode(CGRect(origin: .zero, size: relative.size))
        if placement != .custom { placement = .custom }
    }

    /// Forgets the sizes and frames set by hand; the presets go back to the
    /// size that follows the text size.
    func resetFrames() {
        frames = [:]
        sizes = [:]
        if placement == .custom { placement = .bottom }
    }

    /// The shared stores reread the lyric style just swapped in.
    static func reloadSharedStyle() {
        Settings.shared.reloadLyricStyle()
        RomanizationOptions.shared.reloadDisplay()
        LyricColourOptions.shared.reload()
        LyricHighlightOptions.shared.reload()
        LunalyrExtras.shared.reloadTranslations()
    }
}

// MARK: - Frame maths

/// Where the floating window goes on a display. Pure geometry, in AppKit's
/// global coordinates (origin bottom left).
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
    struct Input: Equatable {
        var surface: LyricsSurface
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
        guard input.surface == .floating else { return false }
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
/// colours: an unknown app window shaded by the floating backdrop.
enum FloatingBackdrop {
    /// Most an app behind is darkened, at full strength.
    static let maximumShade = 0.72

    static func luminance(strength: Double, reduceTransparency: Bool) -> Double {
        let shade = reduceTransparency ? 0.86 : maximumShade * min(1, max(0, strength))
        return LyricLegibility.darkened(LyricLegibility.unknownBackdropLuminance, by: shade)
    }
}
