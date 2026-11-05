import Accelerate
import CoreGraphics
import Foundation
@testable import VersoCustom

/// The shade NowPlayingView lays over every visualiser (WallpaperShade): the
/// Darkening scrim down the screen and, while lyrics show, the text backing
/// behind the lyric stage. Both are black gradients, which SwiftUI
/// interpolates linearly in opacity and the desktop composites over
/// gamma-encoded values, so each pixel keeps (1 − scrim) × (1 − backing) of
/// its encoded value. testOverlayMatchesSwiftUIsOwnGradients checks that
/// against SwiftUI drawing the same views.
///
/// Built once per run at the drawable's size. The gradients are smooth, so
/// shading the drawable before it is scaled to the display matches shading
/// the display.
final class WallpaperOverlay {
    let width: Int, height: Int
    /// The scrim's strength (Darkening) and the text backing drawn.
    let scrim: Double, backing: Double
    /// What each pixel keeps, top row first; empty when nothing is drawn.
    let keep: [Float]
    private var scratch: [Float] = []

    convenience init(width: Int, height: Int, settings: VisualiserRenderSettings) {
        self.init(width: width, height: height, scrim: settings.scrim, backing: settings.textBackingAmount)
    }

    init(width: Int, height: Int, scrim: Double, backing: Double) {
        let width = max(1, width), height = max(1, height)
        let strength = max(0, scrim)
        // NowPlayingView leaves out a backing too faint to matter.
        let amount = backing > WallpaperShade.minimumBacking ? min(1, backing) : 0
        self.width = width; self.height = height
        self.scrim = strength; self.backing = amount
        guard strength > 0 || amount > 0 else { keep = []; return }
        // The ellipse's frame in screen fractions: the lyric stage (a share
        // of the screen at any size) times the backing's span, centred.
        let stage = LyricViewportMetrics(size: CGSize(width: 1, height: 1)).stageSize
        let spanX = Double(stage.width * WallpaperShade.backingSpan.width)
        let spanY = Double(stage.height * WallpaperShade.backingSpan.height)
        let radius = Double(WallpaperShade.backingRadius)
        let locations = WallpaperShade.backingStops.map(\.location)
        let shades = WallpaperShade.backingStops.map { $0.share * amount }
        var map = [Float](repeating: 1, count: width * height)
        map.withUnsafeMutableBufferPointer { map in
            for row in 0..<height {
                let y = (Double(row) + 0.5) / Double(height)
                let rowKeep = 1 - Self.scrimOpacity(at: y, strength: strength)
                let dy = (y - 0.5) / spanY
                for column in 0..<width {
                    var shade = 0.0
                    let dx = ((Double(column) + 0.5) / Double(width) - 0.5) / spanX
                    // Outside its frame the backing draws nothing.
                    if amount > 0, abs(dx) <= 0.5, abs(dy) <= 0.5 {
                        shade = Self.interpolate(locations, shades, at: (dx * dx + dy * dy).squareRoot() / radius)
                    }
                    map[row * width + column] = Float(rowKeep * (1 - shade))
                }
            }
        }
        keep = map
    }

    /// Whether the frames are shown as drawn.
    var isClear: Bool { keep.isEmpty }

    /// The scrim's opacity `y` of the way down the screen (0 at the top):
    /// its evenly spaced stops, interpolated linearly.
    static func scrimOpacity(at y: Double, strength: Double) -> Double {
        let stops = WallpaperShade.scrim.map { min(1, max(0, $0 * strength)) }
        let locations = stops.indices.map { Double($0) / Double(max(1, stops.count - 1)) }
        return interpolate(locations, stops, at: y)
    }

    /// A gradient's value at `location`: linear between stops, held at the ends.
    static func interpolate(_ locations: [Double], _ values: [Double], at location: Double) -> Double {
        guard let first = locations.first, let last = locations.last else { return 0 }
        if location <= first { return values[0] }
        if location >= last { return values[values.count - 1] }
        var index = 1
        while index < locations.count - 1, location > locations[index] { index += 1 }
        let start = locations[index - 1], span = max(1e-12, locations[index] - start)
        return values[index - 1] + (values[index] - values[index - 1]) * (location - start) / span
    }

    /// `image` as the desktop shows it: blue, green and red scaled by what
    /// each pixel keeps.
    func apply(to image: BGRAImage) -> BGRAImage {
        guard !keep.isEmpty else { return image }
        precondition(image.width == width && image.height == height, "The overlay was built for another size")
        let count = image.pixels.count
        if scratch.count != count { scratch = [Float](repeating: 0, count: count) }
        vDSP.convertElements(of: image.pixels, to: &scratch)
        let pixels = vDSP_Length(width * height)
        scratch.withUnsafeMutableBufferPointer { values in
            keep.withUnsafeBufferPointer { keep in
                for channel in 0..<3 {
                    let start = values.baseAddress! + channel
                    vDSP_vmul(start, 4, keep.baseAddress!, 1, start, 4, pixels)
                }
            }
        }
        var shaded = [UInt8](repeating: 0, count: count)
        vDSP.convertElements(of: scratch, to: &shaded, rounding: .towardNearestInteger)
        return BGRAImage(width: width, height: height, pixels: shaded)
    }
}
