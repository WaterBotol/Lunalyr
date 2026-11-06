import SwiftUI

/// Five stops for the colour field and effects, plus the full nine-stop wallpaper gradient.
struct AlbumWallpaperPalette: Equatable {
    static let sampleIndices = [0, 2, 4, 6, 8]
    private static let diagonal = [0, 1, 3, 2, 4, 6, 5, 7, 8]
    let stops: [SIMD3<Float>]
    let artworkStops: [SIMD3<Float>]
    var wallpaperStops: [SIMD3<Float>] { Self.diagonal.map { artworkStops[$0] } }

    init(grid: [SIMD3<Double>]) {
        let rgb = (0..<9).map { index in
            let hsb = grid.indices.contains(index) ? grid[index] : ColorExtractor.fallbackHSBPalette[index]
            let hue = hsb.x * 6
            func channel(_ offset: Double) -> Float {
                let k = (hue + offset).truncatingRemainder(dividingBy: 6)
                return Float(hsb.z * (1 - hsb.y * max(0, min(k, 4 - k, 1))))
            }
            return SIMD3(channel(5), channel(3), channel(1))
        }
        stops = Self.sampleIndices.map { rgb[$0] }
        artworkStops = rgb
    }

    private init(stops: [SIMD3<Float>], artworkStops: [SIMD3<Float>]) {
        self.stops = stops
        self.artworkStops = artworkStops
    }

    func interpolated(to destination: Self, fraction: Double) -> Self {
        let amount = Float(min(1, max(0, fraction)))
        func blend(_ from: [SIMD3<Float>], _ to: [SIMD3<Float>]) -> [SIMD3<Float>] {
            zip(from, to).map { start, end in start + (end - start) * amount }
        }
        return Self(stops: blend(stops, destination.stops),
                    artworkStops: blend(artworkStops, destination.artworkStops))
    }

    var average: SIMD3<Float> { stops.reduce(.zero, +) / Float(stops.count) }
    var cssGradient: String {
        let colors = stops.map { rgb in
            "rgb(\(Int((rgb.x * 255).rounded())),\(Int((rgb.y * 255).rounded())),\(Int((rgb.z * 255).rounded())))"
        }
        return "linear-gradient(120deg, \(colors.joined(separator: ", ")))"
    }

    static func color(_ rgb: SIMD3<Float>) -> Color {
        Color(.sRGB, red: Double(rgb.x), green: Double(rgb.y), blue: Double(rgb.z))
    }

    var gradient: LinearGradient {
        LinearGradient(colors: stops.map(Self.color), startPoint: .topLeading, endPoint: .bottomTrailing)
    }

    func gradientStops(vividness: Double) -> [SIMD3<Float>] {
        guard vividness != 1 else { return wallpaperStops }
        return wallpaperStops.map { rgb in
            let grey = SIMD3(repeating: rgb.x * 0.2126 + rgb.y * 0.7152 + rgb.z * 0.0722)
            return (grey + (rgb - grey) * Float(vividness)).clamped(lowerBound: .zero, upperBound: SIMD3(repeating: 1))
        }
    }

    func gradientLuminance(vividness: Double) -> Double {
        gradientStops(vividness: vividness).map { Perceptual.luminance(sRGB: SIMD3<Double>($0)) }.max() ?? 0
    }
}

/// The hue that fills the most of the cover, and colours to set against it.
/// A small vivid detail can supply an accent without becoming the background.
struct AlbumColourHarmony {
    let backgroundHue: Double?

    init(_ colours: [OKLab]) {
        let chromatic = colours.filter { $0.chroma >= 0.035 }
        func support(_ colour: OKLab) -> Double {
            chromatic.reduce(0) { sum, sample in
                let distance = LyricHighlight.hueDistance(colour.hue, sample.hue)
                return sum + max(0, 1 - distance / (.pi / 4)) * (0.7 + min(0.3, sample.chroma))
            }
        }
        backgroundHue = chromatic.max { support($0) < support($1) }?.hue
    }
}
