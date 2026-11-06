import SwiftUI

// MARK: - The setting

/// Where highlight tiles take their colour from: Cutout's labels, the tile
/// "Cutout · Highlight" presses behind each sung word, and the held words
/// Cascade picks out. It is a setting of its own, apart from the text
/// colour, so the highlight can set the lyrics off rather than repeat them.
enum LyricHighlightMode: String, CaseIterable, Identifiable {
    /// Opposite the lyric colour on the colour wheel, or beside its
    /// opposite, following the text colour as it changes. White lyrics have
    /// no hue to oppose, so they take the album accent.
    case complement
    /// The cover's richest colour, made vivid.
    case album
    /// A colour of the user's own.
    case custom

    var id: String { rawValue }

    var title: String {
        switch self {
        case .complement: return "Complement lyrics"
        case .album: return "Album accent"
        case .custom: return "Custom"
        }
    }

    var detail: String {
        switch self {
        case .complement:
            return "Follows the text colour and picks a complementary accent clear of the cover’s main hue. White text takes an album accent."
        case .album:
            return "A vivid cover accent that stands apart from the album’s main colour."
        case .custom:
            return "Your chosen colour, adjusted only where the text needs more contrast."
        }
    }
}

/// The highlight colour preference. Kept in its own store, with injectable
/// defaults, so tests never touch the real preferences.
@MainActor
final class LyricHighlightOptions: ObservableObject {
    static let shared = LyricHighlightOptions()
    /// A pastel coral that still stands apart from the lyric text.
    static let defaultCustom = "#FF92A3"

    enum Keys {
        static let mode = "custom.lyricHighlightMode"
        static let custom = "custom.lyricHighlightCustom"
    }

    @Published var mode: LyricHighlightMode { didSet { defaults.set(mode.rawValue, forKey: Keys.mode) } }
    /// The custom colour, as `#RRGGBB`.
    @Published private(set) var customHex: String {
        didSet {
            customColour = Self.oklab(hex: customHex) ?? Self.fallback
            defaults.set(customHex, forKey: Keys.custom)
        }
    }
    /// Parsed once per change, not per frame.
    private(set) var customColour: OKLab
    private let defaults: UserDefaults

    private static let fallback = oklab(hex: defaultCustom) ?? LyricHighlight.coral

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        mode = LyricHighlightMode(rawValue: defaults.string(forKey: Keys.mode) ?? "") ?? .complement
        let saved = defaults.string(forKey: Keys.custom).flatMap { Self.oklab(hex: $0) == nil ? nil : $0.uppercased() }
        customHex = saved ?? Self.defaultCustom
        customColour = Self.oklab(hex: saved ?? Self.defaultCustom) ?? Self.fallback
    }

    func setCustom(_ colour: Color) {
        guard let hex = Self.hex(colour) else { return }
        customHex = hex
    }

    /// The custom colour as picked, for a colour well.
    var customSwatch: Color { Perceptual.color(customColour) }

    nonisolated static func oklab(hex: String) -> OKLab? {
        var text = hex.trimmingCharacters(in: .whitespaces)
        if text.hasPrefix("#") { text.removeFirst() }
        guard text.count == 6, let value = UInt32(text, radix: 16) else { return nil }
        let rgb = SIMD3(Double((value >> 16) & 0xFF), Double((value >> 8) & 0xFF), Double(value & 0xFF)) / 255
        return Perceptual.oklab(sRGB: rgb)
    }

    nonisolated static func hex(_ colour: Color) -> String? {
        guard let rgb = NSColor(colour).usingColorSpace(.sRGB) else { return nil }
        func byte(_ value: CGFloat) -> Int { Int((min(1, max(0, value)) * 255).rounded()) }
        return String(format: "#%02X%02X%02X", byte(rgb.redComponent), byte(rgb.greenComponent), byte(rgb.blueComponent))
    }
}

// MARK: - Tiles

/// One highlight as the stage draws it: the tile, the text set on it, and
/// the same hue as type of its own for held words.
struct LyricHighlightTone: Equatable {
    var tile: OKLab
    var text: OKLab
    var held: OKLab
}

/// Highlight colours, worked out in OKLab/OKLCH.
///
/// A tile is a flat, opaque piece of coloured paper with the word set on it.
/// Every tile here is:
/// - vivid: chroma is lifted from its source, up to a little short of what
///   sRGB can show at that lightness, and capped short of neon;
/// - at a pleasing lightness: a deep tile (L 0.38-0.62) with near-white
///   type, or a light one (L 0.70-0.92) with deep ink of its own hue, as
///   suits the hue -- blues and violets are richest deep, yellows, greens
///   and cyans light;
/// - readable: the text on it reaches `textContrast` (WCAG AA for normal
///   text; AAA with Increase Contrast);
/// - clear of the wallpaper: the backdrop luminance the stage is given is
///   the brightest it gets where the lyrics sit, and darker anywhere else,
///   so a tile is lighter than that by `separation` (or, over a backdrop
///   bright enough to need it, darker);
/// - never muddy: yellow-green that is not light enough to be lemon reads
///   as olive or khaki, gold that is too dark as mustard, orange as brown
///   or rust, red-orange as brick, deep green as olive unless it is vivid,
///   anything greyish as dull -- those hues are turned to the nearest clean
///   one, or replaced by a clean colour of the same cover (and lime, clean
///   but acid, gives way to gold or mint where either will do);
/// - deterministic: a pure function of its inputs.
enum LyricHighlight {
    /// The custom colour until one is picked (`LyricHighlightOptions.defaultCustom`).
    static let coral = Perceptual.oklab(sRGB: SIMD3(1, 0x6F / 255.0, 0x61 / 255.0))
    static let textContrast = 4.5
    static let increasedTextContrast = 7.0
    /// How much lighter than the brightest backdrop a tile is, in OKLab L.
    static let separation = 0.15
    /// No tile is closer to the backdrop than this.
    static let minimumSeparation = 0.06

    static let deepBand = 0.38...0.62
    static let lightBand = 0.70...0.92

    static func degrees(_ radians: Double) -> Double {
        let value = (radians * 180 / .pi).truncatingRemainder(dividingBy: 360)
        return value < 0 ? value + 360 : value
    }

    /// The angle between two hues, 0...pi.
    static func hueDistance(_ first: Double, _ second: Double) -> Double {
        let turn = 2 * Double.pi
        var difference = (first - second).truncatingRemainder(dividingBy: turn)
        if difference < 0 { difference += turn }
        return min(difference, turn - difference)
    }

    /// Olive, khaki, citron, mustard, brown, rust, brick, tan -- or
    /// anything greyish.
    static func isMuddy(_ colour: OKLab) -> Bool {
        let hue = degrees(colour.hue), l = colour.l, c = colour.chroma
        switch hue {
        // Yellow-green is lemon or lime only when light -- lightest where
        // it is most olive, about 110-125 degrees; otherwise citron, olive,
        // khaki.
        case 95...140: return l < oliveFloor(hue) || c < 0.12
        // Green, deep only when vivid (emerald); a dull deep one is olive.
        case 140..<165: return l < 0.62 && c < 0.15
        // Gold, unless dark (goldenrod, mustard) or dull (khaki).
        case 70..<95: return l < 0.74 || c < 0.11
        // Orange, unless deep (brown, rust) or dull (tan).
        case 30..<70: return l < 0.62 || c < 0.09
        // Red-orange: vermilion when vivid, brick when deep and dull.
        case 20..<30: return l < 0.62 && c < 0.15
        default: return c < (l < 0.8 ? 0.07 : 0.045)
        }
    }

    /// Lemon, lime and chartreuse read acid beside golden yellow or mint:
    /// a nudge towards either side -- for violet lyrics, a
    /// split-complement rather than lime.
    static func acidity(_ hue: Double) -> Double {
        0.13 * max(0, 1 - abs(degrees(hue) - 118) / 24)
    }

    /// How light yellow-green must be not to read as olive: 0.8 for pure
    /// yellow, 0.88 through lime, easing to 0.7 as it reaches green.
    static func oliveFloor(_ degrees: Double) -> Double {
        if degrees < 110 { return 0.8 + 0.08 * max(0, degrees - 95) / 15 }
        if degrees <= 125 { return 0.88 }
        return 0.88 - 0.18 * min(1, (degrees - 125) / 15)
    }

    /// The lightness a light tile looks best at: yellows are light by
    /// nature and dull to mustard below it, so they sit lighter.
    static func lightSweet(_ hue: Double) -> Double {
        0.8 + 0.06 * max(0, 1 - abs(degrees(hue) - 95) / 35)
    }

    /// The most chroma sRGB can show at this lightness and hue.
    static func maxChroma(l: Double, hue: Double) -> Double {
        var low = 0.0, high = 0.37
        for _ in 0..<14 {
            let middle = (low + high) / 2
            if Perceptual.inGamut(OKLab(l: l, chroma: middle, hue: hue)) { low = middle } else { high = middle }
        }
        return low
    }

    /// Light paper keeps a little short of neon, greens and cyans most.
    static func lightCap(_ hue: Double) -> Double {
        switch degrees(hue) {
        case 165...230: return 0.115
        case 140..<165: return 0.13
        case 75..<140: return 0.155
        default: return 0.16
        }
    }

    static let deepCap = 0.19

    static func lightInk(_ tile: OKLab) -> OKLab { OKLab(l: 0.985, chroma: 0.01, hue: tile.hue) }
    static func deepInk(_ tile: OKLab) -> OKLab { OKLab(l: 0.25, chroma: min(0.05, tile.chroma * 0.3), hue: tile.hue) }

    /// The text for a tile: near-white when that reads better and well
    /// enough, otherwise deep ink of the tile's own hue, deepened as far as
    /// it must be to reach `ratio`, or plain white. At any lightness a tile
    /// reaches 4.5:1 against white or against black, so this always does --
    /// even part way through a change from a deep tile to a light one.
    static func text(on tile: OKLab, ratio: Double = textContrast) -> OKLab {
        let background = Perceptual.luminance(tile)
        func contrast(_ ink: OKLab) -> Double { Perceptual.contrast(Perceptual.luminance(ink), background) }
        let light = lightInk(tile)
        var deep = deepInk(tile)
        if contrast(light) >= ratio, contrast(light) >= contrast(deep) { return light }
        while contrast(deep) < ratio, deep.l > 0.03 {
            deep = OKLab(l: deep.l - 0.03, a: deep.a * 0.9, b: deep.b * 0.9)
        }
        if contrast(deep) >= ratio { return deep }
        return [light, .white, deep].max { contrast($0) < contrast($1) } ?? deep
    }

    /// Readings on a tile are its text at this opacity.
    static let readingOpacity = 0.85

    /// `colour` drawn at `opacity` over `background`, blended as the screen
    /// blends them: in gamma-encoded sRGB.
    static func blend(_ colour: OKLab, over background: OKLab, opacity: Double) -> OKLab {
        let alpha = min(1, max(0, opacity))
        return Perceptual.oklab(sRGB: Perceptual.sRGB(colour) * alpha + Perceptual.sRGB(background) * (1 - alpha))
    }

    /// The backdrop's lightness, and how clear of it a tile at `l` is: a
    /// tile lighter than the brightest backdrop is clear of all of it; over
    /// a backdrop so bright that nothing light would show, a darker one.
    static func clearance(_ l: Double, backdrop: Double) -> Double {
        let floor = cbrt(min(1, max(0, backdrop)))
        return floor >= 0.74 ? floor - l : l - floor
    }

    /// The best tile for one hue: lightness searched over both bands,
    /// chroma as high as asked for and the gamut and cap allow. Nil when
    /// no tile of this hue can carry legible text clear of the backdrop.
    static func tile(hue: Double, chroma want: Double, backdrop: Double,
                     ratio: Double = textContrast) -> (tile: OKLab, score: Double)? {
        var best: (tile: OKLab, score: Double)?
        for light in [false, true] {
            let band = light ? lightBand : deepBand
            let sweet = light ? lightSweet(hue) : 0.50
            let cap = light ? lightCap(hue) : deepCap
            for step in 0...24 {
                let l = band.lowerBound + (band.upperBound - band.lowerBound) * Double(step) / 24
                let c = min(want, cap, maxChroma(l: l, hue: hue) * 0.96)
                let tile = OKLab(l: l, chroma: max(0, c), hue: hue)
                let ink = light ? deepInk(tile) : lightInk(tile)
                guard Perceptual.contrast(Perceptual.luminance(ink), Perceptual.luminance(tile)) >= ratio else { continue }
                let clear = clearance(l, backdrop: backdrop)
                guard clear >= minimumSeparation else { continue }
                var score = c * 3 - abs(l - sweet) * 0.8 - max(0, separation - clear) * 4 - acidity(hue)
                if light { score += 0.10 }
                if isMuddy(tile) { score -= 1 }
                if best.map({ score > $0.score }) ?? true { best = (tile, score) }
            }
        }
        return best
    }

    /// A hue to try, how vivid to make it, and what straying to it costs.
    struct Candidate {
        var hue: Double
        var chroma: Double
        var penalty: Double
    }

    /// The best-scoring tile among `candidates`.
    static func best(_ candidates: [Candidate], backdrop: Double, ratio: Double) -> (tile: OKLab, score: Double)? {
        var best: (tile: OKLab, score: Double)?
        for candidate in candidates {
            guard let found = tile(hue: candidate.hue, chroma: candidate.chroma, backdrop: backdrop, ratio: ratio)
            else { continue }
            let score = found.score - candidate.penalty
            if best.map({ score > $0.score }) ?? true { best = (found.tile, score) }
        }
        return best
    }

    /// Small turns, so a muddy hue can step to the nearest clean one.
    static let turns: [Double] = [0, -15, 15, -30, 30, -45, 45]
    /// The complement's turns: finer, so as the lyric colour drifts its
    /// complement moves in small steps rather than jumping between two.
    static let fineTurns: [Double] = stride(from: -45.0, through: 45, by: 5).sorted { abs($0) < abs($1) }

    /// Opposite `lyric` on the wheel, or up to 45 degrees either side
    /// (split-complementary), keeping a little clear of `stops` -- the
    /// other colours the lyrics are drawn in. Nil for a colour with no hue
    /// to oppose (white, grey). `previous`, the tile chosen a moment ago
    /// for a drifting lyric colour, is kept a little longer when two hues
    /// score alike, so the tile does not flicker between them.
    static func complement(of lyric: OKLab, stops: [OKLab] = [], previous: OKLab? = nil, backgroundHue: Double? = nil,
                           backdrop: Double,
                           ratio: Double = textContrast) -> OKLab? {
        guard lyric.chroma >= 0.035 else { return nil }
        let others = stops.filter { $0.chroma >= 0.04 }
        let candidates = fineTurns.map { turn -> Candidate in
            let hue = lyric.hue + .pi + turn * .pi / 180
            // A vivid lyric colour of the same hue would hide the tile; a
            // pale one, much less.
            let crowding = others.map { max(0, 30 - degrees(hueDistance(hue, $0.hue))) * min(1, $0.chroma / 0.14) }.max() ?? 0
            let staying = previous.map { degrees(hueDistance(hue, $0.hue)) <= 8 ? 0.025 : 0 } ?? 0
            let background = backgroundPenalty(hue, beside: backgroundHue)
            return Candidate(hue: hue, chroma: 0.19, penalty: abs(turn) * 0.003 + crowding * 0.004 + background - staying)
        }
        return best(candidates, backdrop: backdrop, ratio: ratio)?.tile
    }

    /// The fallback hue for a cover with no colour at all: vermilion, the
    /// classic accent for black and white.
    static let monochromeHue = 25 * Double.pi / 180

    /// The cover's richest colour as a tile. The most vivid cover colours
    /// are preferred, the accent the layouts used before breaks ties, and a
    /// muddy hue is turned to the nearest clean one. A cover of one hue
    /// that gives nothing clean fans out to its neighbours and its
    /// complement; a cover with no colour takes vermilion.
    static func album(_ colours: [OKLab], accent: OKLab? = nil, backgroundHue: Double? = nil, backdrop: Double,
                      ratio: Double = textContrast) -> OKLab {
        let chromatic = colours.filter { $0.chroma >= 0.04 }
        guard let richest = chromatic.max(by: { $0.chroma < $1.chroma }) else {
            let vermilion = [0.0, -20, 20].map {
                Candidate(hue: monochromeHue + $0 * .pi / 180, chroma: 0.19, penalty: abs($0) * 0.004)
            }
            return best(vermilion, backdrop: backdrop, ratio: ratio)?.tile ?? OKLab(l: 0.5, chroma: 0.19, hue: monochromeHue)
        }
        var candidates: [Candidate] = []
        for colour in chromatic {
            let want = min(0.19, max(0.15, colour.chroma * 1.5 + 0.04))
            var preference = (richest.chroma - colour.chroma) * 1.2
            if let accent, accent.chroma >= 0.04, hueDistance(colour.hue, accent.hue) < 20 * .pi / 180 { preference -= 0.03 }
            for turn in turns {
                let hue = colour.hue + turn * .pi / 180
                candidates.append(Candidate(hue: hue, chroma: want,
                                            penalty: preference + abs(turn) * 0.004 + backgroundPenalty(hue, beside: backgroundHue)))
            }
        }
        let own = best(candidates, backdrop: backdrop, ratio: ratio)
        let spread = chromatic.flatMap { first in chromatic.map { hueDistance(first.hue, $0.hue) } }.max() ?? 0
        if spread < 40 * .pi / 180, own.map({ isMuddy($0.tile) || $0.tile.chroma < 0.09 }) ?? true {
            // One hue, and nothing clean in it: a coordinated fan.
            for turn in [60.0, -60, 180] {
                candidates.append(Candidate(hue: richest.hue + turn * .pi / 180, chroma: 0.17,
                                            penalty: 0.12 + abs(turn) * 0.0008))
            }
            if let fanned = best(candidates, backdrop: backdrop, ratio: ratio) { return fanned.tile }
        }
        return own?.tile ?? OKLab(l: 0.5, chroma: 0.15, hue: richest.hue)
    }

    private static func backgroundPenalty(_ hue: Double, beside backgroundHue: Double?) -> Double {
        guard let backgroundHue else { return 0 }
        return 0.50 * max(0, 1 - hueDistance(hue, backgroundHue) / (.pi / 3))
    }

    /// The user's own colour as a tile: its hue and chroma kept, its
    /// lightness moved only as far as legibility and the backdrop need.
    static func custom(_ colour: OKLab, backdrop: Double, ratio: Double = textContrast) -> OKLab {
        var best: (tile: OKLab, cost: Double)?
        for step in 0...60 {
            let l = 0.06 + 0.9 * Double(step) / 60
            let tile = OKLab(l: l, chroma: min(colour.chroma, maxChroma(l: l, hue: colour.hue)), hue: colour.hue)
            let ink = text(on: tile, ratio: ratio)
            guard Perceptual.contrast(Perceptual.luminance(ink), Perceptual.luminance(tile)) >= ratio else { continue }
            let clear = clearance(l, backdrop: backdrop)
            guard clear >= minimumSeparation else { continue }
            let cost = abs(l - colour.l) + max(0, separation - clear) * 2 + (colour.chroma - tile.chroma) * 2
            if best.map({ cost < $0.cost }) ?? true { best = (tile, cost) }
        }
        return best?.tile ?? Perceptual.clipped(colour)
    }

    /// The tile's hue as type of its own, for held words: light and clear,
    /// and readable against the backdrop at the lyrics' own floor.
    static func held(_ tile: OKLab, backdrop: Double, ratio: Double) -> OKLab {
        let tone = OKLab(l: max(0.8, tile.l), chroma: min(0.15, tile.chroma), hue: tile.hue)
        return Perceptual.readable(tone, background: backdrop, ratio: ratio)
    }

    /// A highlight for ink no director made (previews, cards): vermilion.
    static let fallback = tone(album([], backdrop: 0.08), backdrop: 0.08, textRatio: textContrast,
                               lyricRatio: LyricInk.minimumContrast)
    static let fallbackTile = Perceptual.color(fallback.tile)
    static let fallbackText = Perceptual.color(fallback.text)

    /// How opaque a fading (or arriving) tile is when the word on it
    /// should change between the colour set on the tile and its own lyric
    /// colour (a luminance). Behind a word the wallpaper may be anything
    /// from black to `backdrop`, the brightest it gets, so this is where the
    /// lyric colour over the tile on the brightest backdrop reads as well as
    /// the tile's ink over the tile on black: the worst either can do
    /// through the fade is as good as it gets, rather than both sinking into
    /// the same half-faded grey. Only ink darker than the lyrics crosses over.
    static func textSwitch(tile: OKLab, text: OKLab, backdrop: Double, lyric: Double) -> Double {
        let ink = Perceptual.luminance(text)
        guard ink < lyric else { return 0.5 }
        let paper = LyricLegibility.encoded(Perceptual.luminance(tile)), bright = LyricLegibility.encoded(backdrop)
        func light(_ opacity: Double) -> Double {
            (lyric + 0.05) / (LyricLegibility.linear(opacity * paper + (1 - opacity) * bright) + 0.05)
        }
        func deep(_ opacity: Double) -> Double { (LyricLegibility.linear(opacity * paper) + 0.05) / (ink + 0.05) }
        var low = 0.0, high = 1.0
        for _ in 0..<24 {
            let middle = (low + high) / 2
            if deep(middle) < light(middle) { low = middle } else { high = middle }
        }
        return (low + high) / 2
    }

    /// The whole highlight for a tile.
    static func tone(_ tile: OKLab, backdrop: Double, textRatio: Double, lyricRatio: Double) -> LyricHighlightTone {
        LyricHighlightTone(tile: tile, text: text(on: tile, ratio: textRatio),
                           held: held(tile, backdrop: backdrop, ratio: lyricRatio))
    }
}
