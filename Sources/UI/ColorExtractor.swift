import AppKit

/// A 3×3 artwork palette. Each region keeps its strongest supported hue;
/// representative accents also get a place among the five gradient stops.
enum ColorExtractor {
    private static let side = 72
    private static let gridSide = 3
    private static let hueBins = 24

    static func extractPalette(from image: NSImage) -> [SIMD3<Double>] {
        guard let artwork = image.cgImage(forProposedRect: nil, context: nil, hints: nil),
              let space = CGColorSpace(name: CGColorSpace.sRGB),
              let context = CGContext(data: nil, width: side, height: side, bitsPerComponent: 8,
                  bytesPerRow: side * 4, space: space,
                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue | CGBitmapInfo.byteOrder32Big.rawValue),
              let bytes = context.data?.assumingMemoryBound(to: UInt8.self) else { return fallbackHSBPalette }
        // One small CPU draw. Nearest sampling keeps a cover's colour fields
        // separate even when bright, contrasting shapes share one region.
        context.interpolationQuality = .none
        context.draw(artwork, in: CGRect(x: 0, y: 0, width: side, height: side))
        var regions = [Region](repeating: Region(), count: gridSide * gridSide)
        var artworkColours = Region()
        let cell = side / gridSide
        for y in 0..<side {
            for x in 0..<side {
                let i = y * context.bytesPerRow + x * 4
                let alpha = Double(bytes[i + 3])
                guard alpha >= 128 else { continue }
                let rgb = SIMD3(Double(bytes[i]), Double(bytes[i + 1]), Double(bytes[i + 2])) / alpha
                let colour = hsb(rgb)
                regions[(y / cell) * gridSide + x / cell].add(rgb, colour: colour)
                artworkColours.add(rgb, colour: colour)
            }
        }
        guard artworkColours.count > 0 else { return fallbackHSBPalette }

        var palette = regions.map { region in
            region.ranked(minimum: max(4, region.count / 20)).first.map { region.bins[$0].colour }
                ?? (region.count > 0 ? region.average : artworkColours.average)
        }
        var used: [Int] = []
        var accents: [Double] = []
        // The colour field uses the corners and centre. Keep the most
        // prominent accents there, then reserve the remaining cells for
        // further hues in the full wallpaper gradient.
        for bin in artworkColours.ranked(minimum: max(6, artworkColours.count / 65)) {
            let accent = artworkColours.bins[bin].colour
            guard !accents.contains(where: { hueDistance($0, accent.x) < 0.08 }) else { continue }
            let primary = AlbumWallpaperPalette.sampleIndices
            let slots = used.count < 4 ? primary : regions.indices.filter { !primary.contains($0) }
            let available = slots.filter { !used.contains($0) }
            guard let slot = available.max(by: { first, second in
                func affinity(_ index: Int) -> Double {
                    let local = regions[index].bins[bin].weight
                    let sharesHue = palette[index].y * palette[index].z > 0.09
                        && hueDistance(palette[index].x, accent.x) < 0.08
                    return local + (sharesHue ? Double(cell * cell) : 0)
                }
                return affinity(first) < affinity(second)
            }) else { break }
            if palette[slot].y * palette[slot].z <= 0.09 || hueDistance(palette[slot].x, accent.x) >= 0.08 {
                palette[slot] = accent
            }
            accents.append(accent.x)
            used.append(slot)
            if used.count == 8 { break }
        }
        let depth: [Double] = [-0.12, -0.04, 0.06, 0.03, 0.12, -0.08, -0.04, 0.08, 0.02]
        return palette.enumerated().map { index, colour in
            SIMD3(colour.x, min(1, colour.y * 1.14),
                  min(0.96, max(0.08, colour.z * 0.84 + 0.12 + depth[index])))
        }
    }

    static func accent(in palette: [SIMD3<Double>]) -> SIMD3<Double> {
        let stops = AlbumWallpaperPalette.sampleIndices.compactMap { palette.indices.contains($0) ? palette[$0] : nil }
        return stops.max { $0.y * $0.z < $1.y * $1.z } ?? fallbackHSBPalette[2]
    }

    private struct Region {
        var count = 0
        var sum = SIMD3<Double>.zero
        var bins = [HueBin](repeating: HueBin(), count: hueBins)

        var average: SIMD3<Double> { hsb(sum / Double(max(1, count))) }

        mutating func add(_ rgb: SIMD3<Double>, colour: SIMD3<Double>) {
            count += 1
            sum += rgb
            let chroma = colour.y * colour.z
            // Near-grey highlights and dark shadows inform the background
            // brightness, but carry little reliable hue information.
            guard colour.y >= 0.20, chroma >= 0.09 else { return }
            let bin = Int((colour.x * Double(hueBins)).rounded()) % hueBins
            let weight = 0.45 + 0.55 * min(1, chroma / 0.5)
            bins[bin].count += 1
            bins[bin].weight += weight
            bins[bin].sum += rgb * weight
        }

        func ranked(minimum: Int) -> [Int] {
            bins.indices.filter { bins[$0].count >= minimum }.sorted { bins[$0].weight > bins[$1].weight }
        }
    }

    private struct HueBin {
        var count = 0
        var weight = 0.0
        var sum = SIMD3<Double>.zero

        var colour: SIMD3<Double> { hsb(sum / weight) }
    }

    private static func hueDistance(_ a: Double, _ b: Double) -> Double {
        let difference = abs(a - b)
        return min(difference, 1 - difference)
    }

    private static func hsb(_ rgb: SIMD3<Double>) -> SIMD3<Double> {
        let peak = max(rgb.x, rgb.y, rgb.z)
        let spread = peak - min(rgb.x, rgb.y, rgb.z)
        guard spread > 0 else { return SIMD3(0, 0, peak) }
        let angle: Double
        if peak == rgb.x { angle = (rgb.y - rgb.z) / spread }
        else if peak == rgb.y { angle = 2 + (rgb.z - rgb.x) / spread }
        else { angle = 4 + (rgb.x - rgb.y) / spread }
        return SIMD3((angle / 6 + 1).truncatingRemainder(dividingBy: 1), spread / peak, peak)
    }

    /// Used before artwork resolves or when a cover cannot be decoded.
    static let fallbackHSBPalette = [
        SIMD3(0.23, 0.16, 0.58), SIMD3(0.12, 0.40, 0.44), SIMD3(0.65, 0.28, 0.43),
        SIMD3(0.15, 0.30, 0.56), SIMD3(0.54, 0.25, 0.16), SIMD3(0.43, 0.19, 0.56),
        SIMD3(0.10, 0.46, 0.40), SIMD3(0.55, 0.34, 0.16), SIMD3(0.30, 0.19, 0.52),
    ].map(hsb)
}
