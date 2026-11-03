import AppKit
import CoreImage
import SwiftUI

/// Samples a 3x3 grid of average colors from artwork for the mesh gradient.
/// Raw averages come out muddy -- averaging a whole cell pulls everything
/// toward grey -- so each sample gets its saturation pushed and its
/// brightness spread, and near-monochrome covers get a gentle hue fan so the
/// gradient still reads as a gradient.
enum ColorExtractor {
    /// Palette as hue/saturation/brightness triples, so the gradient can
    /// modulate colour every frame without re-deriving HSB from `Color`.
    static func extractPalette(from image: NSImage, columns: Int = 3, rows: Int = 3) -> [SIMD3<Double>] {
        extractGrid(from: image, columns: columns, rows: rows).map { color in
            guard let sRGB = NSColor(color).usingColorSpace(.sRGB) else { return SIMD3(0, 0, 0.3) }
            var h: CGFloat = 0, s: CGFloat = 0, b: CGFloat = 0, a: CGFloat = 0
            sRGB.getHue(&h, saturation: &s, brightness: &b, alpha: &a)
            return SIMD3(Double(h), Double(s), Double(b))
        }
    }

    static let fallbackHSBPalette: [SIMD3<Double>] = fallbackPalette.map { color in
        guard let sRGB = NSColor(color).usingColorSpace(.sRGB) else { return SIMD3(0.7, 0.5, 0.3) }
        var h: CGFloat = 0, s: CGFloat = 0, b: CGFloat = 0, a: CGFloat = 0
        sRGB.getHue(&h, saturation: &s, brightness: &b, alpha: &a)
        return SIMD3(Double(h), Double(s), Double(b))
    }

    static func extractGrid(from image: NSImage, columns: Int = 3, rows: Int = 3) -> [Color] {
        guard let cgImage = image.cgImage(forProposedRect: nil, context: nil, hints: nil) else {
            return fallbackPalette
        }
        let ciImage = CIImage(cgImage: cgImage)
        let context = CIContext()
        let extent = ciImage.extent
        guard extent.width > 1, extent.height > 1 else { return fallbackPalette }

        let cellWidth = extent.width / CGFloat(columns)
        let cellHeight = extent.height / CGFloat(rows)

        var samples: [NSColor] = []
        for row in 0..<rows {
            for column in 0..<columns {
                let rect = CGRect(
                    x: extent.minX + CGFloat(column) * cellWidth,
                    y: extent.minY + CGFloat(rows - 1 - row) * cellHeight,
                    width: cellWidth, height: cellHeight
                )
                samples.append(averageColor(of: ciImage, in: rect, context: context))
            }
        }
        return vivify(samples)
    }

    private static func averageColor(of image: CIImage, in rect: CGRect, context: CIContext) -> NSColor {
        let parameters: [String: Any] = [
            kCIInputImageKey: image,
            kCIInputExtentKey: CIVector(cgRect: rect),
        ]
        guard let filter = CIFilter(name: "CIAreaAverage", parameters: parameters),
              let output = filter.outputImage else {
            return .gray
        }
        var bitmap = [UInt8](repeating: 0, count: 4)
        context.render(
            output, toBitmap: &bitmap, rowBytes: 4,
            bounds: CGRect(x: 0, y: 0, width: 1, height: 1),
            format: .RGBA8, colorSpace: CGColorSpaceCreateDeviceRGB()
        )
        return NSColor(
            srgbRed: CGFloat(bitmap[0]) / 255.0,
            green: CGFloat(bitmap[1]) / 255.0,
            blue: CGFloat(bitmap[2]) / 255.0,
            alpha: 1
        )
    }

    private static func vivify(_ samples: [NSColor]) -> [Color] {
        var hues: [CGFloat] = []
        var saturations: [CGFloat] = []
        var brightnesses: [CGFloat] = []

        for sample in samples {
            let color = sample.usingColorSpace(.sRGB) ?? sample
            var h: CGFloat = 0, s: CGFloat = 0, b: CGFloat = 0, a: CGFloat = 0
            color.getHue(&h, saturation: &s, brightness: &b, alpha: &a)
            hues.append(h)
            saturations.append(s)
            brightnesses.append(b)
        }

        // Detect clustered hues as well as greyscale. A vividly blue cover
        // can be just as monochromatic as a grey one. Use a deliberate hue
        // fan around its anchor so all wallpaper families get distinct ink.
        let count = CGFloat(max(hues.count, 1))
        let circularX = hues.reduce(CGFloat(0)) { $0 + cos($1 * 2 * .pi) } / count
        let circularY = hues.reduce(CGFloat(0)) { $0 + sin($1 * 2 * .pi) } / count
        let neutral = saturations.reduce(0, +) / count < 0.12
        let needsHueFan = neutral || hypot(circularX, circularY) > 0.94
        let anchor = neutral ? CGFloat(0.68) : hues[saturations.firstIndex(of: saturations.max() ?? 0) ?? 0]
        let fan: [CGFloat] = [-0.12, -0.04, 0.10, 0.23, 0.18, -0.18, 0.28, 0.06, -0.02]

        return samples.indices.map { index in
            var hue = hues[index]
            if needsHueFan {
                hue = (anchor + fan[index % fan.count] + 1).truncatingRemainder(dividingBy: 1.0)
            }
            let saturation = min(1.0, max(0.42, saturations[index] * 1.55 + (needsHueFan ? 0.28 : 0.14)))
            // Spread brightness a little around its original value so
            // neighbouring cells keep visible separation.
            let wobble = CGFloat((index % 3) - 1) * 0.05
            let brightness = min(0.95, max(0.16, brightnesses[index] * 0.95 + 0.10 + wobble))
            return Color(nsColor: NSColor(hue: hue, saturation: saturation, brightness: brightness, alpha: 1))
        }
    }

    /// Used before artwork resolves, and when a cover cannot be decoded.
    static let fallbackPalette: [Color] = [
        Color(red: 0.23, green: 0.16, blue: 0.58), Color(red: 0.12, green: 0.40, blue: 0.44), Color(red: 0.65, green: 0.28, blue: 0.43),
        Color(red: 0.15, green: 0.30, blue: 0.56), Color(red: 0.54, green: 0.25, blue: 0.16), Color(red: 0.43, green: 0.19, blue: 0.56),
        Color(red: 0.10, green: 0.46, blue: 0.40), Color(red: 0.55, green: 0.34, blue: 0.16), Color(red: 0.30, green: 0.19, blue: 0.52),
    ]
}
