import AppKit
import SwiftUI

/// The colours the desktop player's card is washed in, taken from the
/// album's own artwork: the cover's strongest colour (`accent`, for the
/// record's label, the drag fill and the capsule rims) and a deep shade of
/// it (`deep`, the bottom of the wash). Both pass a contrast guard: a pale
/// or near-white cover is darkened until white type stays legible on it.
/// With no artwork, a neutral grey-blue.
struct ArtworkPalette: Equatable, Sendable {
    struct RGB: Equatable, Sendable {
        var red: Double
        var green: Double
        var blue: Double

        init(_ red: Double, _ green: Double, _ blue: Double) {
            self.red = red
            self.green = green
            self.blue = blue
        }

        var color: Color { Color(.sRGB, red: red, green: green, blue: blue) }

        /// WCAG relative luminance (sRGB).
        var luminance: Double {
            func channel(_ value: Double) -> Double {
                let c = min(1, max(0, value))
                return c <= 0.03928 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4)
            }
            return 0.2126 * channel(red) + 0.7152 * channel(green) + 0.0722 * channel(blue)
        }

        /// WCAG contrast ratio of white type on this colour.
        var contrastWithWhite: Double { 1.05 / (luminance + 0.05) }

        var hsb: (hue: Double, saturation: Double, brightness: Double) {
            let color = NSColor(srgbRed: red, green: green, blue: blue, alpha: 1)
            return (Double(color.hueComponent), Double(color.saturationComponent), Double(color.brightnessComponent))
        }

        static func hsb(_ hue: Double, _ saturation: Double, _ brightness: Double) -> RGB {
            let color = NSColor(hue: hue, saturation: min(1, max(0, saturation)), brightness: min(1, max(0, brightness)),
                                alpha: 1).usingColorSpace(.sRGB) ?? .gray
            return RGB(Double(color.redComponent), Double(color.greenComponent), Double(color.blueComponent))
        }

        func scaled(_ factor: Double) -> RGB { RGB(red * factor, green * factor, blue * factor) }

        func mixed(with other: RGB, _ amount: Double) -> RGB {
            RGB(red + (other.red - red) * amount, green + (other.green - green) * amount, blue + (other.blue - blue) * amount)
        }
    }

    /// The cover's strongest colour, bright enough to read as colour on the card.
    var accent: RGB
    /// A deep shade under the type.
    var deep: RGB
    /// False for the neutral fallback (no artwork, or a grey cover).
    var fromArtwork: Bool

    var accentColor: Color { accent.color }
    /// The desktop player card's flat tint: the deep shade with a little of the accent.
    var cardTint: RGB { deep.mixed(with: accent, fromArtwork ? 0.08 : 0.04) }
    var deepColor: Color { deep.color }

    static let neutral = ArtworkPalette(accent: RGB(0.50, 0.52, 0.62), deep: RGB(0.13, 0.13, 0.17), fromArtwork: false)

    /// White type on `deep` reaches at least this, so the card's quieter ink
    /// levels (docs/DESIGN.md) still read 4.5:1 over a bright wallpaper.
    static let deepContrast = 9.0
    /// The accent carries the big title at the wash's top: AA for large type.
    static let accentContrast = 3.0

    // MARK: From artwork

    /// The palette of a cover, or `neutral` without one.
    static func of(_ image: NSImage?) -> ArtworkPalette {
        guard let image else { return .neutral }
        return cache.palette(for: image) { of(pixels: samples(of: image)) }
    }

    /// The palette from a cover's pixels (sRGB, 0...1).
    static func of(pixels: [RGB]) -> ArtworkPalette {
        guard !pixels.isEmpty else { return .neutral }
        var total = RGB(0, 0, 0)
        for pixel in pixels {
            total.red += pixel.red
            total.green += pixel.green
            total.blue += pixel.blue
        }
        let count = Double(pixels.count)
        let average = RGB(total.red / count, total.green / count, total.blue / count)
        // The strongest hue: pixels binned by hue, each weighted by how vivid
        // it is, so a few saturated pixels beat a wide grey ground.
        let bins = 12
        var weight = [Double](repeating: 0, count: bins)
        var sums = [RGB](repeating: RGB(0, 0, 0), count: bins)
        for pixel in pixels {
            let hsb = pixel.hsb
            guard hsb.saturation > 0.2, hsb.brightness > 0.18 else { continue }
            let bin = min(bins - 1, Int(hsb.hue * Double(bins)))
            let w = hsb.saturation * hsb.saturation * (0.4 + hsb.brightness)
            weight[bin] += w
            sums[bin].red += pixel.red * w
            sums[bin].green += pixel.green * w
            sums[bin].blue += pixel.blue * w
        }
        let strongest = weight.indices.max { weight[$0] < weight[$1] } ?? 0
        // Colour only when enough of the cover has it (2% of a fully vivid cover).
        let vivid = weight[strongest] > count * 0.02
        let dominant = vivid
            ? RGB(sums[strongest].red / weight[strongest], sums[strongest].green / weight[strongest],
                  sums[strongest].blue / weight[strongest])
            : average
        let hsb = dominant.hsb
        guard vivid || average.hsb.saturation > 0.12 else {
            // A grey, black or white cover: the neutral card.
            return .neutral
        }
        // The accent: the dominant hue, lifted to read as colour, then guarded.
        let accent = guarded(.hsb(hsb.hue, max(0.45, min(0.85, hsb.saturation)), max(0.62, min(0.95, hsb.brightness))),
                             contrast: accentContrast)
        // The deep shade: the dominant hue, dark, a touch of the cover's average.
        let deepBase = RGB.hsb(hsb.hue, max(0.35, min(0.8, hsb.saturation * 0.9)), 0.32).mixed(with: average.scaled(0.4), 0.2)
        return ArtworkPalette(accent: accent, deep: guarded(deepBase, contrast: deepContrast), fromArtwork: true)
    }

    /// `colour` darkened, keeping its hue, until white type on it reaches `contrast`.
    static func guarded(_ colour: RGB, contrast: Double) -> RGB {
        var result = colour
        var steps = 0
        while result.contrastWithWhite < contrast, steps < 60 {
            result = result.scaled(0.94)
            steps += 1
        }
        return result
    }

    /// The cover drawn small (24 × 24) and read back as sRGB pixels.
    static func samples(of image: NSImage, side: Int = 24) -> [RGB] {
        guard let cgImage = image.cgImage(forProposedRect: nil, context: nil, hints: nil),
              let space = CGColorSpace(name: CGColorSpace.sRGB),
              let context = CGContext(data: nil, width: side, height: side, bitsPerComponent: 8, bytesPerRow: side * 4,
                                      space: space, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)
        else { return [] }
        context.interpolationQuality = .medium
        context.draw(cgImage, in: CGRect(x: 0, y: 0, width: side, height: side))
        guard let data = context.data else { return [] }
        let bytes = data.bindMemory(to: UInt8.self, capacity: side * side * 4)
        var pixels: [RGB] = []
        pixels.reserveCapacity(side * side)
        for index in 0..<(side * side) {
            let alpha = Double(bytes[index * 4 + 3]) / 255
            guard alpha > 0.5 else { continue }
            pixels.append(RGB(Double(bytes[index * 4]) / 255 / alpha, Double(bytes[index * 4 + 1]) / 255 / alpha,
                              Double(bytes[index * 4 + 2]) / 255 / alpha))
        }
        return pixels
    }

    /// The last cover's palette: worked out once per song, not per redraw.
    private static let cache = PaletteCache()
}

/// Holds the last cover (strongly, so its identity can't be reused) and its palette.
private final class PaletteCache: @unchecked Sendable {
    private let lock = NSLock()
    private var image: NSImage?
    private var palette: ArtworkPalette?

    func palette(for image: NSImage, make: () -> ArtworkPalette) -> ArtworkPalette {
        lock.lock()
        if self.image === image, let palette { lock.unlock(); return palette }
        lock.unlock()
        let made = make()
        lock.lock()
        self.image = image
        palette = made
        lock.unlock()
        return made
    }
}

private struct PlayerPaletteKey: EnvironmentKey { static let defaultValue: ArtworkPalette? = nil }
extension EnvironmentValues {
    /// The playing album's colours, for the card and its capsules (nil: the
    /// player's plain frosted look).
    var playerPalette: ArtworkPalette? {
        get { self[PlayerPaletteKey.self] }
        set { self[PlayerPaletteKey.self] = newValue }
    }
}
