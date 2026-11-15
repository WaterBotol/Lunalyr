import Accelerate
import Compression
import CoreGraphics
import Foundation

// MARK: - Lyric card GIFs, encoded by hand

/// A GIF89a encoder made for lyric cards: a still, smooth backdrop with
/// moving lyrics over it.
///
/// - Each frame gets its own palette of up to 255 colours, picked from the
///   pixels it changes (median cut in OKLab, then a k-means pass), plus one
///   transparent index. The previous palette is kept while it still fits the
///   changed pixels, so colours do not flicker.
/// - Dithering is ordered (an 8 × 8 Bayer matrix fixed to pixel position), so
///   a pixel whose colour does not change never changes, and it only mixes the
///   two palette colours either side of a pixel's colour: gradients come out
///   smooth while text edges, where those two are far apart, stay crisp.
/// - Every frame after the first stores only the rectangle that changed, with
///   untouched pixels transparent over the previous frame ("do not dispose"),
///   so mostly the lyrics are stored. A frame identical to the last one only
///   lengthens it.
/// - Delays are whole centiseconds, never below 2 (browsers turn 0 and 1 into 10).
///
/// It works on packed sRGB pixels (`r | g << 8 | b << 16`, the top byte
/// ignored) and must be fed from one thread at a time; each frame's rows are
/// worked on in parallel. The per-pixel loops use raw pointers and plain
/// arithmetic so that Debug builds (the tests) stay usable too.
final class GIFEncoder {
    struct Rect: Equatable {
        var x: Int, y: Int, width: Int, height: Int
        var isEmpty: Bool { width <= 0 || height <= 0 }
        static let empty = Rect(x: 0, y: 0, width: 0, height: 0)
    }

    /// One encoded frame, kept apart until `data` so a later identical frame
    /// can lengthen its delay instead of being stored.
    private struct Frame {
        var rect: Rect
        var delay: Int
        var transparent: Int?
        var palette: [UInt32]
        var tableBits: Int
        var lzw: [UInt8]
        var byteCount: Int { 8 + 10 + 3 * (1 << tableBits) + lzw.count }
    }

    let width: Int
    let height: Int
    let loopCount: Int
    /// Changes of at most this much per channel from the value last stored
    /// for a pixel are left out (0 keeps every change). Measured against the
    /// stored value, so slow drifts are stored in small steps, never lost.
    var tolerance: Int = 1
    /// How far apart (in sRGB levels, Euclidean) two palette colours may be
    /// and still be dithered together. Further apart, the nearest one is used
    /// alone, which keeps text and its antialiasing crisp.
    var ditherSpread: Int = 56
    /// Checked between rows; true stops the frame being added.
    var isCancelled: () -> Bool = { false }

    private var frames: [Frame] = []
    /// The source value last stored for each pixel, to find what changed.
    private let reference: UnsafeMutablePointer<UInt32>
    /// The colour a decoder shows at each pixel.
    private let shown: UnsafeMutablePointer<UInt32>
    /// The frame's palette: `base` then `extra`.
    private var palette: [UInt32] = []
    /// Most of the palette, made from a whole frame and kept while the
    /// backdrop stays: whatever a word moves off is drawn again exactly as
    /// it was, so nothing is left behind where it was.
    private var base: [UInt32] = []
    /// The rest, for colours the base does not hold (mostly the lyrics' own).
    private var extra: [UInt32] = []
    private let lookup = GIFPaletteLookup()
    private let baseLookup = GIFPaletteLookup()
    /// Counts per 24-bit colour while a palette is being made; kept zeroed.
    private var tally: UnsafeMutablePointer<UInt16>?
    private(set) var byteCount = 6 + 7 + 19 + 1
    private(set) var palettesBuilt = 0

    init(width: Int, height: Int, loopCount: Int = 0) {
        precondition(width > 0 && height > 0 && width <= 65535 && height <= 65535)
        self.width = width
        self.height = height
        self.loopCount = loopCount
        reference = .allocate(capacity: width * height)
        reference.initialize(repeating: 0, count: width * height)
        shown = .allocate(capacity: width * height)
        shown.initialize(repeating: 0, count: width * height)
    }

    deinit {
        reference.deallocate()
        shown.deallocate()
        tally?.deallocate()
    }

    var frameCount: Int { frames.count }
    var delays: [Int] { frames.map(\.delay) }
    /// Each frame's stored rectangle and colour count.
    var rects: [Rect] { frames.map(\.rect) }
    var paletteSizes: [Int] { frames.map(\.palette.count) }

    /// A delay in seconds as GIF centiseconds: rounded, at least 2.
    static func centiseconds(_ seconds: Double) -> Int {
        guard seconds.isFinite else { return 2 }
        return min(65535, max(2, Int((seconds * 100).rounded())))
    }

    /// Lengthens the last frame, as if the next one were identical to it.
    func extendLastFrame(byCentiseconds delay: Int) {
        guard !frames.isEmpty else { return }
        frames[frames.count - 1].delay = min(65535, frames[frames.count - 1].delay + max(0, delay))
    }

    /// Adds a frame of `width × height` packed pixels shown for `delay`
    /// centiseconds. Returns the changed rectangle with each changed pixel's
    /// source (alpha byte set) and the rest 0, or nil when cancelled.
    @discardableResult
    func add(_ pixels: UnsafeBufferPointer<UInt32>, delay: Int) -> (rect: Rect, delta: [UInt32])? {
        precondition(pixels.count >= width * height)
        guard let source = pixels.baseAddress else { return nil }
        let delay = max(2, min(65535, delay))
        let first = frames.isEmpty
        guard let (rect, delta) = difference(source, first: first) else { return nil }
        if rect.isEmpty {
            extendLastFrame(byCentiseconds: delay)
            return (rect, [])
        }
        choosePalette(delta, rect: rect, first: first)
        guard let indices = index(delta, rect: rect, first: first) else { return nil }
        // palette.count colours plus the transparent index.
        var tableBits = 1
        while (1 << tableBits) < palette.count + 1 { tableBits += 1 }
        let lzw = indices.withUnsafeBufferPointer { GIFLZW.encode($0, minCodeSize: max(2, tableBits)) }
        if isCancelled() { return nil }
        let frame = Frame(rect: rect, delay: delay, transparent: first ? nil : palette.count, palette: palette,
                          tableBits: tableBits, lzw: lzw)
        frames.append(frame)
        byteCount += frame.byteCount
        return (rect, delta)
    }

    /// The whole file.
    func data() -> Data {
        var out = [UInt8]()
        out.reserveCapacity(byteCount)
        func le16(_ value: Int) { out.append(UInt8(value & 0xFF)); out.append(UInt8((value >> 8) & 0xFF)) }
        out += Array("GIF89a".utf8)
        le16(width); le16(height)
        // No global colour table; 8 bits of colour resolution.
        out += [0x70, 0, 0]
        // NETSCAPE2.0 looping.
        out += [0x21, 0xFF, 0x0B] + Array("NETSCAPE2.0".utf8) + [0x03, 0x01]
        le16(loopCount)
        out.append(0)
        for frame in frames {
            // Graphic control: disposal 1 (do not dispose), transparency flag.
            out += [0x21, 0xF9, 0x04, (1 << 2) | (frame.transparent == nil ? 0 : 1)]
            le16(frame.delay)
            out += [UInt8(frame.transparent ?? 0), 0]
            out.append(0x2C)
            le16(frame.rect.x); le16(frame.rect.y); le16(frame.rect.width); le16(frame.rect.height)
            out.append(0x80 | UInt8(frame.tableBits - 1))
            for index in 0..<(1 << frame.tableBits) {
                let colour = index < frame.palette.count ? frame.palette[index] : 0
                out += [UInt8(colour & 0xFF), UInt8((colour >> 8) & 0xFF), UInt8((colour >> 16) & 0xFF)]
            }
            out += frame.lzw
        }
        out.append(0x3B)
        return Data(out)
    }

    // MARK: Differencing

    @inline(__always)
    private static func differs(_ new: UInt32, _ old: UInt32, _ tolerance: Int32) -> Bool {
        let value = new & 0xFF_FFFF
        if value == old { return false }
        if tolerance == 0 { return true }
        var d = Int32(value & 0xFF) - Int32(old & 0xFF)
        if d > tolerance || d < -tolerance { return true }
        d = Int32((value >> 8) & 0xFF) - Int32((old >> 8) & 0xFF)
        if d > tolerance || d < -tolerance { return true }
        d = Int32((value >> 16) & 0xFF) - Int32((old >> 16) & 0xFF)
        return d > tolerance || d < -tolerance
    }

    private func difference(_ pixels: UnsafePointer<UInt32>, first: Bool) -> (Rect, [UInt32])? {
        let width = width, height = height, reference = reference
        if first {
            let delta = [UInt32](unsafeUninitializedCapacity: width * height) { buffer, count in
                let out = buffer.baseAddress!
                var i = 0
                while i < width * height {
                    let value = pixels[i] & 0xFF_FFFF
                    reference[i] = value
                    out[i] = value | 0xFF00_0000
                    i += 1
                }
                count = width * height
            }
            return (Rect(x: 0, y: 0, width: width, height: height), delta)
        }
        let tolerance = Int32(tolerance)
        // Each row's first and last changed pixel, rows in parallel.
        let minX = UnsafeMutablePointer<Int>.allocate(capacity: height)
        let maxX = UnsafeMutablePointer<Int>.allocate(capacity: height)
        defer { minX.deallocate(); maxX.deallocate() }
        let chunks = GIFRows.chunks(height)
        DispatchQueue.concurrentPerform(iterations: chunks) { chunk in
            var y = height * chunk / chunks
            let end = height * (chunk + 1) / chunks
            while y < end {
                let row = y * width
                var x = 0
                while x < width, !Self.differs(pixels[row + x], reference[row + x], tolerance) { x += 1 }
                if x < width {
                    var last = width - 1
                    while last > x, !Self.differs(pixels[row + last], reference[row + last], tolerance) { last -= 1 }
                    minX[y] = x
                    maxX[y] = last
                } else {
                    minX[y] = -1
                    maxX[y] = -1
                }
                y += 1
            }
        }
        if isCancelled() { return nil }
        var top = -1, bottom = -1, left = Int.max, right = -1
        for y in 0..<height where maxX[y] >= 0 {
            if top < 0 { top = y }
            bottom = y
            if minX[y] < left { left = minX[y] }
            if maxX[y] > right { right = maxX[y] }
        }
        guard top >= 0 else { return (.empty, []) }
        let rect = Rect(x: left, y: top, width: right - left + 1, height: bottom - top + 1)
        var delta = [UInt32](repeating: 0, count: rect.width * rect.height)
        delta.withUnsafeMutableBufferPointer { buffer in
            let out = buffer.baseAddress!
            let chunks = GIFRows.chunks(rect.height)
            DispatchQueue.concurrentPerform(iterations: chunks) { chunk in
                var row = rect.height * chunk / chunks
                let end = rect.height * (chunk + 1) / chunks
                while row < end {
                    let y = rect.y + row
                    if maxX[y] >= 0 {
                        var x = minX[y]
                        let line = y * width, target = row * rect.width - rect.x
                        while x <= maxX[y] {
                            let value = pixels[line + x]
                            if Self.differs(value, reference[line + x], tolerance) {
                                reference[line + x] = value & 0xFF_FFFF
                                out[target + x] = (value & 0xFF_FFFF) | 0xFF00_0000
                            }
                            x += 1
                        }
                    }
                    row += 1
                }
            }
        }
        return (rect, delta)
    }

    // MARK: Palette

    /// Counts the colours of `count` values into `tally`, `weight` each,
    /// noting each new colour. Changed-pixel buffers skip their zeros.
    private func count(_ values: UnsafePointer<UInt32>, _ count: Int, weight: Int, changesOnly: Bool, into colours: inout [UInt32]) {
        if tally == nil {
            tally = .allocate(capacity: 1 << 24)
            tally!.initialize(repeating: 0, count: 1 << 24)
        }
        let tally = tally!
        var i = 0
        while i < count {
            let value = values[i]
            i += 1
            if changesOnly, value == 0 { continue }
            let key = Int(value & 0xFF_FFFF)
            let old = Int(tally[key])
            if old == 0 { colours.append(UInt32(key)) }
            tally[key] = UInt16(min(Int(UInt16.max), old + weight))
        }
    }

    /// The counts of `colours`, leaving the tally zeroed.
    private func collect(_ colours: [UInt32]) -> [Int] {
        guard let tally else { return [] }
        var counts = [Int](repeating: 0, count: colours.count)
        for index in colours.indices {
            let key = Int(colours[index])
            counts[index] = Int(tally[key])
            tally[key] = 0
        }
        return counts
    }

    /// The changed pixels' colours and how often each appears.
    private func histogram(_ delta: [UInt32]) -> (colours: [UInt32], counts: [Int]) {
        var colours: [UInt32] = []
        delta.withUnsafeBufferPointer { count($0.baseAddress!, $0.count, weight: 1, changesOnly: true, into: &colours) }
        return (colours, collect(colours))
    }

    /// Colours this far (OKLab) from every palette colour are "not held".
    static let heldDistance: Float = 0.02
    static let baseSize = 208

    /// Sets the palette for a frame. The base is made from the whole frame
    /// for the first frame and whenever most of the frame changes (a moving
    /// background); otherwise it stays. The extra colours are made from the
    /// changed colours the base does not hold, and kept while the palette
    /// still holds them, so colours do not flicker from frame to frame.
    private func choosePalette(_ delta: [UInt32], rect: Rect, first: Bool) {
        let (colours, counts) = histogram(delta)
        var changedPalette = false
        if first || rect.width * rect.height * 10 >= width * height * 6 {
            var all: [UInt32] = []
            count(reference, width * height, weight: 1, changesOnly: false, into: &all)
            // A frame of 255 colours or fewer keeps them all, exactly.
            base = GIFQuantizer.palette(colours: all, counts: collect(all), maximum: all.count <= 255 ? 255 : Self.baseSize)
            if base.isEmpty { base = [0] }
            baseLookup.set(base)
            extra = []
            changedPalette = true
        }
        // The changed colours the base does not hold.
        let nearestBase = baseLookup.nearest
        var missing: [UInt32] = [], missingCounts: [Int] = []
        for index in colours.indices {
            let colour = Int(colours[index])
            if nearestBase.distance(colour, to: nearestBase.index(colour)) > Self.heldDistance {
                missing.append(colours[index])
                missingCounts.append(counts[index])
            }
        }
        if !missing.isEmpty, !changedPalette {
            // Keep the extra colours if, with them, the palette holds these
            // too: all but a few pixels, and those not far off.
            let nearest = lookup.nearest
            var unheld = 0, worst: Float = 0
            for index in missing.indices {
                let distance = nearest.distance(Int(missing[index]), to: nearest.index(Int(missing[index])))
                if distance > Self.heldDistance { unheld += missingCounts[index] }
                worst = max(worst, distance)
            }
            let changed = counts.reduce(0, +)
            if unheld * 200 > changed || worst > 2 * Self.heldDistance {
                extra = GIFQuantizer.palette(colours: missing, counts: missingCounts, maximum: 255 - base.count)
                changedPalette = true
            }
        } else if !missing.isEmpty {
            extra = GIFQuantizer.palette(colours: missing, counts: missingCounts, maximum: 255 - base.count)
        }
        if changedPalette || palette.isEmpty {
            palette = base + extra
            lookup.set(palette)
            palettesBuilt += 1
        }
    }

    /// Frees the lookup tables (about 64 MB) once no more frames will come.
    func releaseTables() {
        lookup.release()
        baseLookup.release()
        tally?.deallocate()
        tally = nil
    }

    // MARK: Indexing and dithering

    /// 8 × 8 Bayer thresholds, 0...63, indexed `(y & 7) * 8 + (x & 7)`.
    static let bayer: [Int32] = [
        0, 32, 8, 40, 2, 34, 10, 42,
        48, 16, 56, 24, 50, 18, 58, 26,
        12, 44, 4, 36, 14, 46, 6, 38,
        60, 28, 52, 20, 62, 30, 54, 22,
        3, 35, 11, 43, 1, 33, 9, 41,
        51, 19, 59, 27, 49, 17, 57, 25,
        15, 47, 7, 39, 13, 45, 5, 37,
        63, 31, 55, 23, 61, 29, 53, 21,
    ]

    /// Each changed pixel's palette index: the nearest colour, or, by the
    /// Bayer threshold, the one on the far side of the pixel's colour, in
    /// proportion to how far between the two it lies. Pixels that would show
    /// what is already shown, and unchanged ones, are transparent.
    private func index(_ delta: [UInt32], rect: Rect, first: Bool) -> [UInt8]? {
        let transparent = UInt8(truncatingIfNeeded: palette.count)
        let spread = Int32(ditherSpread * ditherSpread)
        let width = width, shown = shown, nearest = lookup.nearest
        let colours = lookup.rgb
        let cancelled = UnsafeMutablePointer<Bool>.allocate(capacity: 1)
        cancelled.initialize(to: false)
        defer { cancelled.deallocate() }
        let bayer = UnsafeMutablePointer<Int32>.allocate(capacity: 64)
        defer { bayer.deallocate() }
        for i in 0..<64 { bayer[i] = Self.bayer[i] }
        let isCancelled = isCancelled
        var indices = [UInt8](repeating: transparent, count: rect.width * rect.height)
        delta.withUnsafeBufferPointer { deltaBuffer in
            indices.withUnsafeMutableBufferPointer { outBuffer in
                let changes = deltaBuffer.baseAddress!, out = outBuffer.baseAddress!
                let chunks = GIFRows.chunks(rect.height)
                DispatchQueue.concurrentPerform(iterations: chunks) { chunk in
                    if isCancelled() { cancelled.pointee = true; return }
                    var row = rect.height * chunk / chunks
                    let end = rect.height * (chunk + 1) / chunks
                    while row < end {
                        let y = rect.y + row
                        var column = 0
                        while column < rect.width {
                            let at = row * rect.width + column
                            let value = changes[at]
                            column += 1
                            if value == 0 { continue }
                            let colour = Int32(bitPattern: value & 0xFF_FFFF)
                            let r = colour & 0xFF, g = (colour >> 8) & 0xFF, b = (colour >> 16) & 0xFF
                            var chosen = nearest.index(Int(colour))
                            let near = colours[chosen]
                            let er = r - (near & 0xFF), eg = g - ((near >> 8) & 0xFF), eb = b - ((near >> 16) & 0xFF)
                            if er > 1 || er < -1 || eg > 1 || eg < -1 || eb > 1 || eb < -1 {
                                // The colour on the far side of `colour` from the nearest.
                                var other = chosen
                                var reach: Int32 = 1
                                while reach <= 2, other == chosen {
                                    let pr = GIFRows.clamp(r + reach * er), pg = GIFRows.clamp(g + reach * eg)
                                    let pb = GIFRows.clamp(b + reach * eb)
                                    other = nearest.index(Int(pr | (pg << 8) | (pb << 16)))
                                    reach += 1
                                }
                                if other != chosen {
                                    let far = colours[other]
                                    let dr = (far & 0xFF) - (near & 0xFF), dg = ((far >> 8) & 0xFF) - ((near >> 8) & 0xFF)
                                    let db = ((far >> 16) & 0xFF) - ((near >> 16) & 0xFF)
                                    let length = dr * dr + dg * dg + db * db
                                    if length > 0, length <= spread {
                                        // Far when the threshold, (t + ½) / 64, is below how far
                                        // along near → far the colour lies.
                                        let along = (er * dr + eg * dg + eb * db) * 128
                                        let x = rect.x + column - 1
                                        if (2 * bayer[(y & 7) * 8 + (x & 7)] + 1) * length < along { chosen = other }
                                    }
                                }
                            }
                            let i = y * width + rect.x + column - 1
                            let output = UInt32(bitPattern: colours[chosen])
                            if first || shown[i] != output {
                                shown[i] = output
                                out[at] = UInt8(truncatingIfNeeded: chosen)
                            }
                        }
                        row += 1
                    }
                }
            }
        }
        return cancelled.pointee ? nil : indices
    }
}

/// Splits rows into chunks for `concurrentPerform`.
enum GIFRows {
    static func chunks(_ count: Int) -> Int {
        max(1, min(count, ProcessInfo.processInfo.activeProcessorCount * 4, (count + 7) / 8))
    }

    @inline(__always)
    static func clamp(_ value: Int32) -> Int32 { value < 0 ? 0 : (value > 255 ? 255 : value) }
}

// MARK: - Colour

/// sRGB → OKLab in Float, for the per-pixel work (`Perceptual` works in
/// Double on whole colours).
enum GIFColour {
    /// sRGB level → linear light.
    static let linear: UnsafeMutablePointer<Float> = {
        let table = UnsafeMutablePointer<Float>.allocate(capacity: 256)
        for value in 0..<256 { table[value] = linear(Float(value)) }
        return table
    }()

    static func linear(_ level: Float) -> Float {
        let c = level / 255
        return c <= 0.04045 ? c / 12.92 : powf((c + 0.055) / 1.055, 2.4)
    }

    @inline(__always)
    static func oklab(_ colour: Int) -> (Float, Float, Float) {
        let table = linear
        return oklab(table[colour & 0xFF], table[(colour >> 8) & 0xFF], table[(colour >> 16) & 0xFF])
    }

    static func oklab(_ r: Float, _ g: Float, _ b: Float) -> (Float, Float, Float) {
        let l = cbrtf(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b)
        let m = cbrtf(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b)
        let s = cbrtf(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b)
        return (0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
                1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
                0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s)
    }
}

/// The current palette, and the nearest entry for each 24-bit colour,
/// looked up once per colour.
final class GIFPaletteLookup {
    /// Copyable into the parallel row loops.
    struct Nearest {
        let count: Int
        let lab: UnsafeMutablePointer<Float>
        let cache: UnsafeMutablePointer<UInt8>

        /// The palette entry nearest `colour` in OKLab.
        @inline(__always)
        func index(_ colour: Int) -> Int {
            let cached = cache[colour]
            if cached != 0xFF { return Int(cached) }
            let (l, a, b) = GIFColour.oklab(colour)
            var best = 0
            var bestDistance = Float.greatestFiniteMagnitude
            var entry = 0
            while entry < count {
                let dl = lab[3 * entry] - l, da = lab[3 * entry + 1] - a, db = lab[3 * entry + 2] - b
                let distance = dl * dl + da * da + db * db
                if distance < bestDistance { bestDistance = distance; best = entry }
                entry += 1
            }
            // Rows run in parallel; a colour two rows meet at once gets the
            // same answer written twice.
            cache[colour] = UInt8(best)
            return best
        }

        func distance(_ colour: Int, to entry: Int) -> Float {
            let (l, a, b) = GIFColour.oklab(colour)
            let dl = lab[3 * entry] - l, da = lab[3 * entry + 1] - a, db = lab[3 * entry + 2] - b
            return (dl * dl + da * da + db * db).squareRoot()
        }
    }

    /// Packed colours, as Int32 for the dithering arithmetic.
    let rgb = UnsafeMutablePointer<Int32>.allocate(capacity: 256)
    private let lab = UnsafeMutablePointer<Float>.allocate(capacity: 3 * 256)
    private var cache: UnsafeMutablePointer<UInt8>?
    private var count = 0

    deinit {
        rgb.deallocate()
        lab.deallocate()
        cache?.deallocate()
    }

    var nearest: Nearest {
        if cache == nil {
            cache = .allocate(capacity: 1 << 24)
            cache!.initialize(repeating: 0xFF, count: 1 << 24)
        }
        return Nearest(count: count, lab: lab, cache: cache!)
    }

    func release() {
        cache?.deallocate()
        cache = nil
    }

    func set(_ palette: [UInt32]) {
        precondition(palette.count <= 255)
        count = palette.count
        for (index, colour) in palette.enumerated() {
            rgb[index] = Int32(bitPattern: colour & 0xFF_FFFF)
            let (l, a, b) = GIFColour.oklab(Int(colour & 0xFF_FFFF))
            lab[3 * index] = l; lab[3 * index + 1] = a; lab[3 * index + 2] = b
        }
        if cache == nil { cache = .allocate(capacity: 1 << 24) }
        cache!.initialize(repeating: 0xFF, count: 1 << 24)
    }
}

// MARK: - Palette

enum GIFQuantizer {
    /// Up to `maximum` colours for the given colours and counts: median cut
    /// in OKLab, always splitting the box with the largest squared error
    /// where the split removes most of it, then a k-means pass. The output
    /// colours are the sRGB means of their members, so a dark gradient keeps
    /// its exact levels.
    static func palette(colours: [UInt32], counts: [Int], maximum: Int) -> [UInt32] {
        guard !colours.isEmpty, maximum > 0 else { return [] }
        if colours.count <= maximum { return colours }
        let points = Points(colours: colours, counts: counts, limit: 24_000)
        defer { points.deallocate() }
        var centres = medianCut(points, maximum: maximum)
        centres = refine(points, centres)
        var seen = Set<UInt32>()
        var result: [UInt32] = []
        for centre in centres {
            let r = UInt32(GIFRows.clamp(Int32(centre.0.rounded()))), g = UInt32(GIFRows.clamp(Int32(centre.1.rounded())))
            let b = UInt32(GIFRows.clamp(Int32(centre.2.rounded())))
            let colour = r | g << 8 | b << 16
            if seen.insert(colour).inserted { result.append(colour) }
        }
        return result
    }

    /// Weighted colours in flat buffers: OKLab (3 per point), sRGB (3), weight.
    private struct Points {
        var count: Int
        let lab: UnsafeMutablePointer<Float>
        let rgb: UnsafeMutablePointer<Float>
        let weight: UnsafeMutablePointer<Float>

        /// Above `limit` distinct colours, they are merged into 6-bit (then
        /// 5-bit) cells at their weighted mean, which bounds the work.
        init(colours: [UInt32], counts: [Int], limit: Int) {
            var merged: (rgb: [Float], weight: [Float])?
            for bits in [6, 5] where (merged?.weight.count ?? colours.count) > limit {
                merged = Self.merge(colours, counts, bits: bits)
            }
            count = merged?.weight.count ?? colours.count
            lab = .allocate(capacity: 3 * count)
            rgb = .allocate(capacity: 3 * count)
            weight = .allocate(capacity: count)
            for index in 0..<count {
                if let merged {
                    rgb[3 * index] = merged.rgb[3 * index]
                    rgb[3 * index + 1] = merged.rgb[3 * index + 1]
                    rgb[3 * index + 2] = merged.rgb[3 * index + 2]
                    weight[index] = merged.weight[index]
                } else {
                    let colour = colours[index]
                    rgb[3 * index] = Float(colour & 0xFF)
                    rgb[3 * index + 1] = Float((colour >> 8) & 0xFF)
                    rgb[3 * index + 2] = Float((colour >> 16) & 0xFF)
                    weight[index] = Float(counts[index])
                }
                let (l, a, b) = GIFColour.oklab(GIFColour.linear(rgb[3 * index]), GIFColour.linear(rgb[3 * index + 1]),
                                                GIFColour.linear(rgb[3 * index + 2]))
                lab[3 * index] = l; lab[3 * index + 1] = a; lab[3 * index + 2] = b
            }
        }

        private static func merge(_ colours: [UInt32], _ counts: [Int], bits: Int) -> (rgb: [Float], weight: [Float]) {
            let shift = UInt32(8 - bits), mask = UInt32((1 << bits) - 1)
            let cells = 1 << (3 * bits)
            let slot = UnsafeMutablePointer<Int32>.allocate(capacity: cells)
            slot.initialize(repeating: -1, count: cells)
            defer { slot.deallocate() }
            var rgb: [Float] = [], weight: [Float] = []
            for index in colours.indices {
                let colour = colours[index]
                let key = Int((colour >> shift) & mask) | Int((colour >> (8 + shift)) & mask) << bits
                    | Int((colour >> (16 + shift)) & mask) << (2 * bits)
                var at = Int(slot[key])
                if at < 0 {
                    at = weight.count
                    slot[key] = Int32(at)
                    weight.append(0)
                    rgb += [0, 0, 0]
                }
                let w = Float(counts[index])
                weight[at] += w
                rgb[3 * at] += Float(colour & 0xFF) * w
                rgb[3 * at + 1] += Float((colour >> 8) & 0xFF) * w
                rgb[3 * at + 2] += Float((colour >> 16) & 0xFF) * w
            }
            for at in weight.indices {
                rgb[3 * at] /= weight[at]; rgb[3 * at + 1] /= weight[at]; rgb[3 * at + 2] /= weight[at]
            }
            return (rgb, weight)
        }

        func deallocate() {
            lab.deallocate(); rgb.deallocate(); weight.deallocate()
        }
    }

    private struct Box {
        var start: Int
        var end: Int
        var error: Float
    }

    /// The squared error of points `order[start..<end]` around their mean, and
    /// the axis along which they vary most.
    private static func spread(_ points: Points, _ order: UnsafeMutablePointer<Int32>, _ start: Int, _ end: Int) -> (error: Float, axis: Int) {
        var w: Float = 0
        var s0: Float = 0, s1: Float = 0, s2: Float = 0, q0: Float = 0, q1: Float = 0, q2: Float = 0
        var i = start
        while i < end {
            let p = Int(order[i])
            let pw = points.weight[p], l = points.lab[3 * p], a = points.lab[3 * p + 1], b = points.lab[3 * p + 2]
            w += pw
            s0 += l * pw; s1 += a * pw; s2 += b * pw
            q0 += l * l * pw; q1 += a * a * pw; q2 += b * b * pw
            i += 1
        }
        guard w > 0 else { return (0, 0) }
        let v0 = max(0, q0 - s0 * s0 / w), v1 = max(0, q1 - s1 * s1 / w), v2 = max(0, q2 - s2 * s2 / w)
        return (v0 + v1 + v2, v0 >= v1 && v0 >= v2 ? 0 : (v1 >= v2 ? 1 : 2))
    }

    private static func medianCut(_ points: Points, maximum: Int) -> [(Float, Float, Float)] {
        let order = UnsafeMutablePointer<Int32>.allocate(capacity: points.count)
        defer { order.deallocate() }
        for i in 0..<points.count { order[i] = Int32(i) }
        var boxes = [Box(start: 0, end: points.count, error: spread(points, order, 0, points.count).error)]
        let buckets = 128
        var bucketWeight = [Float](repeating: 0, count: buckets)
        var bucketSum = [Float](repeating: 0, count: buckets)
        var bucketSquares = [Float](repeating: 0, count: buckets)
        while boxes.count < maximum {
            var pick = -1
            var largest: Float = 0
            for (index, box) in boxes.enumerated() where box.end - box.start > 1 && box.error > largest {
                largest = box.error
                pick = index
            }
            guard pick >= 0 else { break }
            let box = boxes[pick]
            let axis = spread(points, order, box.start, box.end).axis
            // Bucket the points along the axis and split between buckets
            // where the two sides' error along it is least.
            var low = Float.greatestFiniteMagnitude, high = -Float.greatestFiniteMagnitude
            var i = box.start
            while i < box.end {
                let v = points.lab[3 * Int(order[i]) + axis]
                if v < low { low = v }
                if v > high { high = v }
                i += 1
            }
            guard high > low else { boxes[pick].error = 0; continue }
            let scale = Float(buckets) / (high - low) * 0.999_99
            for b in 0..<buckets { bucketWeight[b] = 0; bucketSum[b] = 0; bucketSquares[b] = 0 }
            i = box.start
            while i < box.end {
                let p = Int(order[i])
                let v = points.lab[3 * p + axis], w = points.weight[p]
                let b = min(buckets - 1, Int((v - low) * scale))
                bucketWeight[b] += w; bucketSum[b] += v * w; bucketSquares[b] += v * v * w
                i += 1
            }
            var totalWeight: Float = 0, totalSum: Float = 0, totalSquares: Float = 0
            for b in 0..<buckets { totalWeight += bucketWeight[b]; totalSum += bucketSum[b]; totalSquares += bucketSquares[b] }
            var leftWeight: Float = 0, leftSum: Float = 0, leftSquares: Float = 0
            var cut = -1
            var bestError = Float.greatestFiniteMagnitude
            for b in 0..<(buckets - 1) {
                leftWeight += bucketWeight[b]; leftSum += bucketSum[b]; leftSquares += bucketSquares[b]
                let rightWeight = totalWeight - leftWeight
                guard leftWeight > 0, rightWeight > 0, bucketWeight[b] > 0 else { continue }
                let rightSum = totalSum - leftSum, rightSquares = totalSquares - leftSquares
                let error = (leftSquares - leftSum * leftSum / leftWeight) + (rightSquares - rightSum * rightSum / rightWeight)
                if error < bestError { bestError = error; cut = b }
            }
            guard cut >= 0 else { boxes[pick].error = 0; continue }
            // Partition in place: buckets up to `cut` to the left.
            var front = box.start, back = box.end - 1
            while front <= back {
                let v = points.lab[3 * Int(order[front]) + axis]
                if min(buckets - 1, Int((v - low) * scale)) <= cut {
                    front += 1
                } else {
                    let swap = order[front]; order[front] = order[back]; order[back] = swap
                    back -= 1
                }
            }
            guard front > box.start, front < box.end else { boxes[pick].error = 0; continue }
            boxes[pick] = Box(start: box.start, end: front, error: spread(points, order, box.start, front).error)
            boxes.append(Box(start: front, end: box.end, error: spread(points, order, front, box.end).error))
        }
        return boxes.map { box in
            var w: Float = 0, r: Float = 0, g: Float = 0, b: Float = 0
            var i = box.start
            while i < box.end {
                let p = Int(order[i]), pw = points.weight[p]
                w += pw
                r += points.rgb[3 * p] * pw; g += points.rgb[3 * p + 1] * pw; b += points.rgb[3 * p + 2] * pw
                i += 1
            }
            w = max(w, 1e-6)
            return (r / w, g / w, b / w)
        }
    }

    /// One k-means pass: each point to its nearest centre (in OKLab), each
    /// centre to its members' mean. A centre left without members stays put.
    private static func refine(_ points: Points, _ centres: [(Float, Float, Float)]) -> [(Float, Float, Float)] {
        let k = centres.count
        let lab = UnsafeMutablePointer<Float>.allocate(capacity: 3 * k)
        let sums = UnsafeMutablePointer<Float>.allocate(capacity: 4 * k)
        defer { lab.deallocate(); sums.deallocate() }
        sums.initialize(repeating: 0, count: 4 * k)
        for (index, centre) in centres.enumerated() {
            let (l, a, b) = GIFColour.oklab(GIFColour.linear(centre.0), GIFColour.linear(centre.1), GIFColour.linear(centre.2))
            lab[3 * index] = l; lab[3 * index + 1] = a; lab[3 * index + 2] = b
        }
        var p = 0
        while p < points.count {
            let l = points.lab[3 * p], a = points.lab[3 * p + 1], b = points.lab[3 * p + 2]
            var best = 0
            var bestDistance = Float.greatestFiniteMagnitude
            var c = 0
            while c < k {
                let dl = lab[3 * c] - l, da = lab[3 * c + 1] - a, db = lab[3 * c + 2] - b
                let distance = dl * dl + da * da + db * db
                if distance < bestDistance { bestDistance = distance; best = c }
                c += 1
            }
            let w = points.weight[p]
            sums[4 * best] += points.rgb[3 * p] * w
            sums[4 * best + 1] += points.rgb[3 * p + 1] * w
            sums[4 * best + 2] += points.rgb[3 * p + 2] * w
            sums[4 * best + 3] += w
            p += 1
        }
        return centres.enumerated().map { index, centre in
            let w = sums[4 * index + 3]
            return w > 0 ? (sums[4 * index] / w, sums[4 * index + 1] / w, sums[4 * index + 2] / w) : centre
        }
    }
}

// MARK: - LZW

enum GIFLZW {
    /// Packs codes LSB-first into sub-blocks of at most 255 bytes.
    private struct BitSink {
        let out: UnsafeMutablePointer<UInt8>
        var length = 0
        /// Where the current sub-block's length byte is.
        var blockStart = 0
        var bits: UInt64 = 0
        var bitCount = 0

        init(_ out: UnsafeMutablePointer<UInt8>) {
            self.out = out
            out[0] = 0
            length = 1
        }

        @inline(__always)
        mutating func emit(_ code: Int, width: Int) {
            bits |= UInt64(code) << UInt64(bitCount)
            bitCount += width
            while bitCount >= 8 {
                byte(UInt8(truncatingIfNeeded: bits))
                bits >>= 8
                bitCount -= 8
            }
        }

        @inline(__always)
        private mutating func byte(_ value: UInt8) {
            out[length] = value
            length += 1
            if length - blockStart - 1 == 255 {
                out[blockStart] = 255
                blockStart = length
                out[length] = 0
                length += 1
            }
        }

        /// Flushes the last bits, closes the sub-block and adds the terminator.
        mutating func finish() {
            if bitCount > 0 {
                byte(UInt8(truncatingIfNeeded: bits))
                bits = 0
                bitCount = 0
            }
            let pending = length - blockStart - 1
            if pending > 0 {
                out[blockStart] = UInt8(pending)
                out[length] = 0
                length += 1
            } else {
                // The empty block just opened is the terminator.
                out[blockStart] = 0
            }
        }
    }

    /// `minCodeSize`, then the LZW stream of `indices` in sub-blocks, then the
    /// block terminator. Codes grow from `minCodeSize + 1` bits to 12; a clear
    /// code restarts the table when it fills.
    static func encode(_ indices: UnsafeBufferPointer<UInt8>, minCodeSize: Int) -> [UInt8] {
        precondition((2...8).contains(minCodeSize))
        // At most 12 bits a pixel, plus clear codes, sub-block lengths and slack.
        let capacity = indices.count * 2 + 64
        let buffer = UnsafeMutablePointer<UInt8>.allocate(capacity: capacity + 1)
        defer { buffer.deallocate() }
        buffer[0] = UInt8(minCodeSize)
        var sink = BitSink(buffer + 1)
        let clear = 1 << minCodeSize, end = clear + 1
        var width = minCodeSize + 1
        var next = end + 1
        // Open-addressed table of (prefix << 8 | byte) → code.
        let tableSize = 1 << 14, tableMask = tableSize - 1
        let keys = UnsafeMutablePointer<Int32>.allocate(capacity: tableSize)
        let codes = UnsafeMutablePointer<UInt16>.allocate(capacity: tableSize)
        defer { keys.deallocate(); codes.deallocate() }
        keys.initialize(repeating: -1, count: tableSize)
        sink.emit(clear, width: width)
        if let source = indices.baseAddress, indices.count > 0 {
            var prefix = Int(source[0])
            var position = 1
            while position < indices.count {
                let byte = Int(source[position])
                position += 1
                let key = Int32(prefix << 8 | byte)
                var slot = ((Int(key) &* 0x9E37_79B1) >> 7) & tableMask
                var found = -1
                while keys[slot] >= 0 {
                    if keys[slot] == key { found = Int(codes[slot]); break }
                    slot = (slot + 1) & tableMask
                }
                if found >= 0 {
                    prefix = found
                    continue
                }
                sink.emit(prefix, width: width)
                if next < 4096 {
                    keys[slot] = key
                    codes[slot] = UInt16(next)
                    next += 1
                    // The decoder adds this entry one code later, so it widens
                    // once `next` passes the current width.
                    if next > (1 << width), width < 12 { width += 1 }
                } else {
                    sink.emit(clear, width: width)
                    keys.update(repeating: -1, count: tableSize)
                    width = minCodeSize + 1
                    next = end + 1
                }
                prefix = byte
            }
            sink.emit(prefix, width: width)
            // The decoder adds an entry for that last code, and may widen.
            if next < 4096, next == (1 << width), width < 12 { width += 1 }
        }
        sink.emit(end, width: width)
        sink.finish()
        return Array(UnsafeBufferPointer(start: buffer, count: sink.length + 1))
    }
}

// MARK: - A size-budgeted GIF from rendered frames

/// Takes rendered frames one at a time (from one thread at a time), encodes
/// them as they come and, when a byte budget is set, keeps each frame's
/// changes (compressed) so that a GIF over budget can be made again with
/// fewer frames per second first, then smaller, without drawing anything again.
final class GIFWriter: @unchecked Sendable {
    struct Summary: Equatable {
        var bytes: Int
        var frames: Int
        /// The scale of the written GIF against the frames it was given.
        var scale: Double
        /// The shortest gap kept between frames, in centiseconds.
        var gap: Int
        /// How many times the frames were encoded.
        var passes: Int
        /// How many palettes were made (the rest of the frames reused one).
        var palettes: Int
    }

    private struct Change {
        var rect: GIFEncoder.Rect
        var delay: Int
        var packed: Data
        var count: Int
    }

    let byteBudget: Int?
    private let lock = NSLock()
    private var cancelled = false
    private var encoder: GIFEncoder?
    private var firstFrameBytes = 0
    private var changes: [Change] = []
    private var pixels: [UInt32] = []
    private(set) var summary: Summary?

    init(byteBudget: Int?) {
        self.byteBudget = byteBudget
    }

    func cancel() {
        lock.lock(); cancelled = true; lock.unlock()
    }

    var isCancelled: Bool {
        lock.lock(); defer { lock.unlock() }
        return cancelled
    }

    /// Bytes encoded so far at full rate and size.
    var byteCount: Int { encoder?.byteCount ?? 0 }

    /// Encodes `image`, shown for `delay` seconds. False once cancelled.
    func add(_ image: CGImage, delay: Double) -> Bool {
        guard !isCancelled else { return false }
        if encoder == nil {
            let encoder = GIFEncoder(width: image.width, height: image.height)
            encoder.isCancelled = { [unowned self] in isCancelled }
            self.encoder = encoder
            pixels = [UInt32](repeating: 0, count: image.width * image.height)
        }
        guard let encoder else { return false }
        Self.draw(image, into: &pixels, width: encoder.width, height: encoder.height)
        let centiseconds = GIFEncoder.centiseconds(delay)
        guard let (rect, delta) = pixels.withUnsafeBufferPointer({ encoder.add($0, delay: centiseconds) }) else { return false }
        if encoder.frameCount == 1, firstFrameBytes == 0 { firstFrameBytes = encoder.byteCount }
        if byteBudget != nil {
            changes.append(Change(rect: rect, delay: centiseconds, packed: Self.pack(delta), count: delta.count))
        }
        return !isCancelled
    }

    /// The GIF, made again smaller if it is over budget. `progress` runs 0 to 1
    /// over any extra passes. Nil when cancelled or empty.
    func finish(progress: ((Double) -> Void)? = nil) -> Data? {
        guard let encoder, encoder.frameCount > 0, !isCancelled else { return nil }
        encoder.releaseTables()
        guard let budget = byteBudget, encoder.byteCount > budget else {
            summary = Summary(bytes: encoder.byteCount, frames: encoder.frameCount, scale: 1, gap: 0, passes: 1,
                              palettes: encoder.palettesBuilt)
            progress?(1)
            return encoder.data()
        }
        let delays = changes.filter { !$0.rect.isEmpty }.map(\.delay).sorted()
        let cadence = max(2, delays.isEmpty ? 4 : delays[delays.count / 2])
        let full = Double(encoder.byteCount), first = Double(firstFrameBytes)
        // Fewer frames per second first (down to about 10), then smaller.
        let plans: [(rate: Double, scale: Double)] = [
            (1.25, 1), (1.5, 1), (2, 1), (2.5, 1), (2.5, 0.85), (2.5, 0.72), (3, 0.6), (3, 0.5), (4, 0.4), (5, 0.3),
        ]
        func estimate(_ plan: (rate: Double, scale: Double)) -> Double {
            (first + (full - first) / pow(plan.rate, 0.85)) * plan.scale * plan.scale
        }
        let candidates = plans.filter { estimate($0) <= Double(budget) * 0.95 }
        let tries = Array((candidates.isEmpty ? [plans.last!] : candidates + [plans.last!]).prefix(4))
        var passes = 1
        var best: GIFEncoder?
        var bestPlan = tries[0]
        for (number, plan) in tries.enumerated() {
            guard let made = replay(rate: plan.rate, scale: plan.scale, cadence: cadence, progress: { fraction in
                progress?((Double(number) + fraction) / Double(tries.count))
            }) else { return nil }
            passes += 1
            if best == nil || made.byteCount < best!.byteCount { best = made; bestPlan = plan }
            if made.byteCount <= budget { break }
        }
        guard let best, !isCancelled else { return nil }
        summary = Summary(bytes: best.byteCount, frames: best.frameCount, scale: bestPlan.scale,
                          gap: Int((Double(cadence) * bestPlan.rate).rounded()), passes: passes, palettes: best.palettesBuilt)
        progress?(1)
        return best.data()
    }

    /// The kept changes played back onto a canvas, every frame at least
    /// `rate` × `cadence` after the last kept one, scaled by `scale`.
    private func replay(rate: Double, scale: Double, cadence: Int, progress: (Double) -> Void) -> GIFEncoder? {
        guard let source = encoder else { return nil }
        let width = source.width, height = source.height
        let outWidth = max(1, Int((Double(width) * scale).rounded())), outHeight = max(1, Int((Double(height) * scale).rounded()))
        let made = GIFEncoder(width: outWidth, height: outHeight)
        made.isCancelled = { [unowned self] in isCancelled }
        var canvas = [UInt32](repeating: 0, count: width * height)
        var scaled = [UInt32](repeating: 0, count: outWidth * outHeight)
        // Frames go out on an even schedule `gap` apart, each showing the
        // latest frame by then, so the motion stays even at the lower rate.
        let gap = Double(cadence) * rate
        let total = changes.reduce(0) { $0 + $1.delay }
        func due(_ step: Int) -> Int { min(total, Int((Double(step) * gap).rounded())) }
        var time = 0, step = 0
        for (index, change) in changes.enumerated() {
            guard !isCancelled else { return nil }
            if !change.rect.isEmpty {
                let delta = Self.unpack(change.packed, count: change.count)
                canvas.withUnsafeMutableBufferPointer { canvasBuffer in
                    delta.withUnsafeBufferPointer { deltaBuffer in
                        let canvas = canvasBuffer.baseAddress!, values = deltaBuffer.baseAddress!
                        var row = 0
                        while row < change.rect.height {
                            let line = (change.rect.y + row) * width + change.rect.x, from = row * change.rect.width
                            var column = 0
                            while column < change.rect.width {
                                let value = values[from + column]
                                if value != 0 { canvas[line + column] = value & 0xFF_FFFF }
                                column += 1
                            }
                            row += 1
                        }
                    }
                }
            }
            time += change.delay
            // Every scheduled moment before the next change shows this canvas.
            while due(step) < time {
                let delay = due(step + 1) - due(step)
                step += 1
                if delay < 2, made.frameCount > 0 {
                    made.extendLastFrame(byCentiseconds: delay)
                    continue
                }
                let frame: [UInt32]
                if scale < 1 {
                    Self.resize(canvas, width: width, height: height, into: &scaled, width: outWidth, height: outHeight)
                    frame = scaled
                } else {
                    frame = canvas
                }
                guard frame.withUnsafeBufferPointer({ made.add($0, delay: delay) }) != nil else { return nil }
            }
            progress(Double(index + 1) / Double(changes.count))
        }
        made.releaseTables()
        return made
    }

    // MARK: Pixels

    /// `image` as packed sRGB (`r | g << 8 | b << 16`), drawn to fill `width × height`.
    static func draw(_ image: CGImage, into pixels: inout [UInt32], width: Int, height: Int) {
        pixels.withUnsafeMutableBytes { raw in
            guard let space = CGColorSpace(name: CGColorSpace.sRGB),
                  let context = CGContext(data: raw.baseAddress, width: width, height: height, bitsPerComponent: 8,
                                          bytesPerRow: width * 4, space: space,
                                          bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue) else { return }
            context.interpolationQuality = .high
            context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
        }
    }

    private static func resize(_ source: [UInt32], width: Int, height: Int, into target: inout [UInt32], width outWidth: Int, height outHeight: Int) {
        var source = source
        source.withUnsafeMutableBytes { input in
            target.withUnsafeMutableBytes { output in
                var from = vImage_Buffer(data: input.baseAddress, height: vImagePixelCount(height), width: vImagePixelCount(width),
                                         rowBytes: width * 4)
                var to = vImage_Buffer(data: output.baseAddress, height: vImagePixelCount(outHeight),
                                       width: vImagePixelCount(outWidth), rowBytes: outWidth * 4)
                vImageScale_ARGB8888(&from, &to, nil, vImage_Flags(kvImageHighQualityResampling))
            }
        }
    }

    private static func pack(_ delta: [UInt32]) -> Data {
        guard !delta.isEmpty else { return Data() }
        return delta.withUnsafeBytes { raw in
            let capacity = raw.count + 4096
            let buffer = UnsafeMutablePointer<UInt8>.allocate(capacity: capacity)
            defer { buffer.deallocate() }
            let size = compression_encode_buffer(buffer, capacity, raw.bindMemory(to: UInt8.self).baseAddress!, raw.count,
                                                 nil, COMPRESSION_LZ4)
            return size > 0 ? Data(bytes: buffer, count: size) : Data(raw)
        }
    }

    private static func unpack(_ packed: Data, count: Int) -> [UInt32] {
        var delta = [UInt32](repeating: 0, count: count)
        guard count > 0 else { return delta }
        if packed.count == count * 4 {
            _ = delta.withUnsafeMutableBytes { packed.copyBytes(to: $0) }
            return delta
        }
        delta.withUnsafeMutableBytes { raw in
            packed.withUnsafeBytes { source in
                _ = compression_decode_buffer(raw.bindMemory(to: UInt8.self).baseAddress!, raw.count,
                                              source.bindMemory(to: UInt8.self).baseAddress!, source.count, nil, COMPRESSION_LZ4)
            }
        }
        return delta
    }
}
