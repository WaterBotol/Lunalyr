import Foundation
import simd

/// The reference nebula provides the light and shape. Album mode adds a
/// separate field of nine cover tints, so colour can vary across the sky
/// independently of the cloud density.
struct StarTrailsSky: Equatable {
    static let originalColours: [SIMD3<Double>] = [
        [0.031, 0.008, 0.075], // Deep sky
        [0.055, 0.035, 0.112], [0.082, 0.043, 0.145], [0.118, 0.059, 0.188], [0.180, 0.078, 0.259],
        [0.235, 0.110, 0.310], [0.298, 0.141, 0.353], [0.360, 0.165, 0.392], // Cloud ramp
        [0.647, 0.282, 0.565], [0.831, 0.369, 0.706], // Wisps
        [1.0, 0.97, 1.0], // Dust stars
    ]
    static let count = originalColours.count
    static let tintCount = 9
    private static let originalTrails: [SIMD4<Double>] = [[0.992, 0.478, 0.992, 0], [0.882, 0.996, 0.478, 0]]
    static let trailCount = originalTrails.count

    /// Linear RGB, scaled to at most unit luminance. W is the strength of
    /// the album effect. The final two slots hold trail cores in sRGB.
    let colours: [SIMD4<Double>]
    var tints: ArraySlice<SIMD4<Double>> { colours.prefix(Self.tintCount) }
    var trails: ArraySlice<SIMD4<Double>> { colours.suffix(Self.trailCount) }
    static let original = StarTrailsSky(colours: Array(repeating: SIMD4(1, 1, 1, 0), count: tintCount) + originalTrails)

    static func album(_ palette: AlbumWallpaperPalette) -> StarTrailsSky {
        let tints = palette.artworkStops.map { rgb -> SIMD4<Double> in
            let colour = SIMD3<Double>(rgb)
            let luminance = Perceptual.luminance(sRGB: colour)
            let linear = SIMD3(Perceptual.linear(colour.x), Perceptual.linear(colour.y), Perceptual.linear(colour.z))
            // Keep a little of the cover's light and dark structure. The
            // shader multiplies this by the original cloud luminance.
            let gain = 0.68 + 0.28 * sqrt(luminance)
            let tint = luminance > 0.0005 ? linear * (gain / luminance) : SIMD3(repeating: gain)
            return SIMD4(tint.x, tint.y, tint.z, 1)
        }
        let harmony = AlbumColourHarmony(palette.artworkStops.map { Perceptual.oklab(sRGB: SIMD3<Double>($0)) })
        let trails = [Double.pi * 0.69, -Double.pi * 0.69].enumerated().map { index, turn in
            guard let hue = harmony.accentHue(turn: turn) else { return originalTrails[index] }
            let tone = OKLab(l: 0.87, chroma: 0.17, hue: hue)
            let core = Perceptual.sRGB(Perceptual.readable(tone, background: 0.13))
            return SIMD4(core.x, core.y, core.z, 1)
        }
        return StarTrailsSky(colours: tints + trails)
    }

    static let packedOriginal = originalColours.map { colour in
        SIMD4<Float>(Float(colour.x), Float(colour.y), Float(colour.z), 0)
    }
}

/// Three equal lags blend the album's tints and effect strength in linear
/// light. A new cover arrives over about 2.5 seconds without a pop.
struct StarTrailsSkyFade {
    static let tau = 0.42
    private(set) var target: StarTrailsSky
    private var a: [SIMD4<Double>], b: [SIMD4<Double>], c: [SIMD4<Double>]

    init(_ sky: StarTrailsSky) {
        target = sky
        a = sky.colours; b = sky.colours; c = sky.colours
    }

    mutating func aim(at sky: StarTrailsSky) {
        target = sky
    }

    mutating func step(_ dt: Double) {
        guard dt > 0 else { return }
        let k = 1 - exp(-dt / Self.tau)
        for i in target.colours.indices {
            a[i] += (target.colours[i] - a[i]) * k
            b[i] += (a[i] - b[i]) * k
            c[i] += (b[i] - c[i]) * k
        }
    }

    func write(into out: UnsafeMutablePointer<SIMD4<Float>>) {
        for (i, colour) in StarTrailsSky.packedOriginal.enumerated() { out[i] = colour }
        for (i, colour) in c.enumerated() {
            let goal = target.colours[i]
            let value = simd_distance(colour, goal) < 1e-7 ? goal : colour
            out[StarTrailsSky.count + i] = SIMD4<Float>(value)
        }
    }
}
