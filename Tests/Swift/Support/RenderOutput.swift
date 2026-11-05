import Accelerate
import CoreGraphics
import CoreText
import Foundation
import ImageIO
import UniformTypeIdentifiers
import XCTest
@testable import VersoCustom

/// Rendered pixels as the drawable holds them: BGRA8, top row first.
struct BGRAImage {
    let width: Int, height: Int
    let pixels: [UInt8]

    static let colourSpace = CGColorSpace(name: CGColorSpace.sRGB)!
    static let bitmapInfo = CGBitmapInfo(rawValue: CGImageAlphaInfo.noneSkipFirst.rawValue
        | CGBitmapInfo.byteOrder32Little.rawValue)

    var cgImage: CGImage? {
        guard let provider = CGDataProvider(data: Data(pixels) as CFData) else { return nil }
        return CGImage(width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: width * 4,
                       space: Self.colourSpace, bitmapInfo: Self.bitmapInfo, provider: provider, decode: nil,
                       shouldInterpolate: true, intent: .defaultIntent)
    }

    /// Whether every byte matches `other`.
    func identical(to other: BGRAImage) -> Bool {
        guard other.width == width, other.height == height else { return false }
        return pixels.withUnsafeBytes { a in
            other.pixels.withUnsafeBytes { b in memcmp(a.baseAddress!, b.baseAddress!, a.count) == 0 }
        }
    }

    /// `image` resampled to `width` × `height` with bilinear filtering, as a
    /// layer magnifies a smaller drawable.
    static func scale(_ image: CGImage, width: Int, height: Int) -> CGImage? {
        if image.width == width, image.height == height { return image }
        guard let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                                      space: colourSpace, bitmapInfo: bitmapInfo.rawValue) else { return nil }
        context.interpolationQuality = .medium
        context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
        return context.makeImage()
    }

    static func writePNG(_ image: CGImage, to url: URL) throws {
        guard let destination = CGImageDestinationCreateWithURL(url as CFURL, UTType.png.identifier as CFString, 1, nil) else {
            throw VisualiserRenderRequest.Invalid(description: "Cannot write \(url.path)")
        }
        CGImageDestinationAddImage(destination, image, nil)
        guard CGImageDestinationFinalize(destination) else {
            throw VisualiserRenderRequest.Invalid(description: "Cannot encode \(url.path)")
        }
    }
}

/// Brightness, lit share and change of successive frames, over every pixel.
/// vDSP keeps it quick in the Debug build the tests run.
final class FrameMetrics {
    struct Reading {
        /// Mean relative luminance, 0...1 (what the photosensitivity limiter caps).
        let luminance: Double
        /// Share of pixels with a channel above 16 / 255.
        let lit: Double
        /// Mean absolute colour change from the previous frame, 0...1.
        let change: Double
    }

    private static let linear: [Float] = (0..<256).map { code in
        let value = Double(code) / 255
        return Float(value <= 0.04045 ? value / 12.92 : pow((value + 0.055) / 1.055, 2.4))
    }
    private var current: [Float] = [], previous: [Float] = [], scratch: [Float] = [], brightest: [Float] = []
    private var hasPrevious = false

    func measure(_ image: BGRAImage) -> Reading {
        let count = image.pixels.count, pixels = count / 4
        if scratch.count != count {
            current = [Float](repeating: 0, count: count)
            previous = current
            scratch = current
            brightest = [Float](repeating: 0, count: pixels)
            hasPrevious = false
        }
        vDSP.convertElements(of: image.pixels, to: &current)
        // Alpha is always opaque, so the change over B, G, R is 4/3 of the mean over all four bytes.
        var change: Float = 0
        if hasPrevious {
            vDSP.subtract(current, previous, result: &scratch)
            change = vDSP.meanMagnitude(scratch) * 4 / 3 / 255
        }
        var blue: Float = 0, green: Float = 0, red: Float = 0, share: Float = 0
        current.withUnsafeBufferPointer { bytes in
            scratch.withUnsafeMutableBufferPointer { linear in
                brightest.withUnsafeMutableBufferPointer { brightest in
                    let b = bytes.baseAddress!, l = linear.baseAddress!, m = brightest.baseAddress!
                    let n = vDSP_Length(pixels)
                    var scale: Float = 1, offset: Float = 0
                    Self.linear.withUnsafeBufferPointer {
                        vDSP_vtabi(b, 1, &scale, &offset, $0.baseAddress!, vDSP_Length(256), l, 1, vDSP_Length(count))
                    }
                    vDSP_meanv(l, 4, &blue, n)
                    vDSP_meanv(l + 1, 4, &green, n)
                    vDSP_meanv(l + 2, 4, &red, n)
                    vDSP_vmax(b, 4, b + 1, 4, m, 1, n)
                    vDSP_vmax(m, 1, b + 2, 4, m, 1, n)
                    var threshold: Float = 16.5, one: Float = 1
                    vDSP_vthrsc(m, 1, &threshold, &one, m, 1, n)
                    vDSP_meanv(m, 1, &share, n)
                }
            }
        }
        swap(&current, &previous)
        hasPrevious = true
        return Reading(luminance: Double(0.0722 * blue + 0.7152 * green + 0.2126 * red),
                       lit: Double((share + 1) / 2), change: Double(change))
    }
}

/// A JSON value with stable key order and short numbers.
indirect enum JSONValue {
    case number(Double), string(String), bool(Bool), null
    case array([JSONValue]), object([(String, JSONValue)])

    /// Any value through reflection: every stored property, so fields added
    /// to VizUniforms or AudioSnapshot appear without changing the harness.
    /// Every integer and floating-point type is a number, a SIMD vector of
    /// either is an array, and arrays over 64 entries (the snapshots'
    /// 256-point waveforms) are summarised; frames.json logs the bound
    /// waveform in full under "bound".
    static func reflecting(_ value: Any) -> JSONValue {
        if let value = value as? JSONValue { return value }
        if let value = value as? Bool { return .bool(value) }
        if let number = number(value) { return .number(number) }
        if let value = value as? String { return .string(value) }
        if let elements = elements(value) { return .array(elements.map(reflecting)) }
        switch value {
        case let value as [Float] where value.count > 64:
            let finite = value.filter(\.isFinite)
            let rms = (finite.reduce(0) { $0 + Double($1) * Double($1) } / Double(max(1, finite.count))).squareRoot()
            return .object([("count", .number(Double(value.count))), ("min", .number(Double(finite.min() ?? 0))),
                            ("max", .number(Double(finite.max() ?? 0))), ("rms", .number(rms))])
        default:
            let mirror = Mirror(reflecting: value)
            switch mirror.displayStyle {
            case .optional?: return mirror.children.first.map { reflecting($0.value) } ?? .null
            case .collection?, .set?: return .array(mirror.children.map { reflecting($0.value) })
            case .enum?: return .string(String(describing: value))
            default:
                let fields = mirror.children.compactMap { child in child.label.map { ($0, reflecting(child.value)) } }
                return fields.isEmpty ? .string(String(describing: value)) : .object(fields)
            }
        }
    }

    /// Any integer or floating-point value (UInt32 counters, Float16, CGFloat…).
    static func number(_ value: Any) -> Double? {
        if let value = value as? any BinaryFloatingPoint { return Double(value) }
        if let value = value as? any BinaryInteger { return Double(value) }
        return nil
    }

    /// The elements of a SIMD vector of any scalar type.
    static func elements(_ value: Any) -> [Any]? {
        func elements<Vector: SIMD>(of vector: Vector) -> [Any] { vector.indices.map { vector[$0] } }
        guard let vector = value as? any SIMD else { return nil }
        return elements(of: vector)
    }

    /// Every value of `values`, in full.
    static func numbers(_ values: [Float]) -> JSONValue { .array(values.map { .number(Double($0)) }) }

    subscript(key: String) -> JSONValue? {
        guard case .object(let fields) = self else { return nil }
        return fields.first { $0.0 == key }?.1
    }

    var number: Double? {
        if case .number(let value) = self { return value }
        return nil
    }

    /// Containers less than `pretty` levels deep get a line per entry; deeper
    /// ones, and arrays of plain values, stay on one line.
    func text(pretty: Int = 0) -> String {
        var out = ""
        write(into: &out, depth: 0, pretty: pretty)
        return out
    }

    private func write(into out: inout String, depth: Int, pretty: Int) {
        switch self {
        case .null: out += "null"
        case .bool(let value): out += value ? "true" : "false"
        case .number(let value): out += Self.format(value)
        case .string(let value): Self.quote(value, into: &out)
        case .array(let items):
            let flat = depth >= pretty || items.allSatisfy {
                switch $0 {
                case .array, .object: return false
                default: return true
                }
            }
            let pad = String(repeating: "  ", count: depth + 1)
            out += "["
            for (index, item) in items.enumerated() {
                if index > 0 { out += flat ? ", " : "," }
                if !flat { out += "\n" + pad }
                item.write(into: &out, depth: depth + 1, pretty: pretty)
            }
            if !flat, !items.isEmpty { out += "\n" + String(repeating: "  ", count: depth) }
            out += "]"
        case .object(let fields):
            let flat = depth >= pretty
            let pad = String(repeating: "  ", count: depth + 1)
            out += "{"
            for (index, field) in fields.enumerated() {
                if index > 0 { out += flat ? ", " : "," }
                if !flat { out += "\n" + pad }
                Self.quote(field.0, into: &out)
                out += ": "
                field.1.write(into: &out, depth: depth + 1, pretty: pretty)
            }
            if !flat, !fields.isEmpty { out += "\n" + String(repeating: "  ", count: depth) }
            out += "}"
        }
    }

    /// Six decimals at most, integers without a fraction; NaN and infinity are null.
    static func format(_ value: Double) -> String {
        guard value.isFinite else { return "null" }
        let rounded = (value * 1_000_000).rounded() / 1_000_000
        if rounded == rounded.rounded(), abs(rounded) < 1e15 { return String(Int(rounded)) }
        return "\(rounded)"
    }

    private static func quote(_ text: String, into out: inout String) {
        out += "\""
        for scalar in text.unicodeScalars {
            switch scalar {
            case "\"": out += "\\\""
            case "\\": out += "\\\\"
            case "\n": out += "\\n"
            case "\t": out += "\\t"
            case _ where scalar.value < 0x20: out += String(format: "\\u%04x", scalar.value)
            default: out.unicodeScalars.append(scalar)
            }
        }
        out += "\""
    }
}

/// The fields VizUniforms and AudioSnapshot had when the harness was written.
/// Anything else (a new sound-type feature, say) is plotted on the contact
/// sheet as well as logged.
enum KnownFields {
    static let uniforms: Set<String> = Set(["resolution", "time", "dt", "bass", "mid", "treble", "level", "beat",
        "sensitivity", "speed", "palette", "subBass", "lowMid", "highMid", "presence", "air", "trebleBeat", "flux",
        "centroid", "beatHit", "trebleHit", "beatCount", "frame", "reduceMotion", "intro"]
        + (0..<16).map { "p\($0)" })
    static let sound: Set<String> = ["bands", "waveform", "level", "bass", "mid", "treble", "components", "beat",
        "trebleBeat", "flux", "centroid", "wavefield"]

    /// Scalar signals in fields outside `known`: every integer, floating-point
    /// and Bool value and SIMD element, two levels into structs and the
    /// first 8 entries of collections ("sound.kick", "sound.kickOnsets",
    /// "stars[2]"), at most `limit` of them. A value that is not finite (an
    /// onset time of −∞ before the first onset) reads as 0.
    static func newSignals(in value: Any, known: Set<String>, prefix: String, limit: Int = 40) -> [(String, Double)] {
        var signals: [(String, Double)] = []
        func add(_ name: String, _ value: Any, depth: Int) {
            guard signals.count < limit else { return }
            if let flag = value as? Bool {
                signals.append((name, flag ? 1 : 0))
            } else if let number = JSONValue.number(value) {
                signals.append((name, number.isFinite ? number : 0))
            } else if let elements = JSONValue.elements(value) {
                for (index, element) in elements.enumerated() { add("\(name)[\(index)]", element, depth: depth + 1) }
            } else if depth < 2 {
                let mirror = Mirror(reflecting: value)
                let entries = mirror.displayStyle == .collection || mirror.displayStyle == .set ? 8 : Int.max
                for (index, child) in mirror.children.prefix(entries).enumerated() {
                    add(child.label.map { "\(name).\($0)" } ?? "\(name)[\(index)]", child.value, depth: depth + 1)
                }
            }
        }
        for child in Mirror(reflecting: value).children {
            guard let label = child.label, !known.contains(label) else { continue }
            add(prefix + label, child.value, depth: 0)
        }
        return signals
    }
}

/// Tiles of a run's frames over plots of what drove them.
struct ContactSheet {
    struct Tile {
        let index: Int
        let time: Double
        let image: CGImage
        /// Smoothed bass, mid, treble, beat, treble beat, flux and level.
        let bars: [Double]
    }

    struct Series {
        var name: String
        let colour: CGColor
        var values: [Double]
    }

    static let barColours: [CGColor] = [colour(1, 0.45, 0.25), colour(0.45, 0.85, 0.35), colour(0.35, 0.65, 1),
                                        colour(1, 1, 1), colour(1, 0.85, 0.2), colour(0.85, 0.4, 0.95),
                                        colour(0.62, 0.62, 0.62)]
    static let barNames = ["bass", "mid", "treble", "beat", "trebleBeat", "flux", "level"]
    static let extraColours: [CGColor] = [colour(1, 0.6, 0.3), colour(0.5, 1, 0.7), colour(0.75, 0.75, 1),
                                          colour(1, 0.4, 0.45), colour(0.65, 0.95, 0.2), colour(0.95, 0.9, 0.5),
                                          colour(0.4, 0.85, 1), colour(1, 0.55, 0.85)]

    static func colour(_ red: CGFloat, _ green: CGFloat, _ blue: CGFloat, _ alpha: CGFloat = 1) -> CGColor {
        CGColor(colorSpace: BGRAImage.colourSpace, components: [red, green, blue, alpha])!
    }

    /// Up to `tiles` captured frame indices, evenly spread, first and last included.
    static func tileIndices(frames: Int, tiles: Int) -> [Int] {
        guard frames > 0 else { return [] }
        let count = min(frames, max(1, tiles))
        guard count > 1 else { return [0] }
        return Array(Set((0..<count).map { Int((Double($0) * Double(frames - 1) / Double(count - 1)).rounded()) })).sorted()
    }

    /// About 90 000 pixels a tile, at the display's aspect ratio.
    static func tileSize(width: Int, height: Int) -> (width: Int, height: Int) {
        let aspect = Double(width) / Double(max(1, height))
        let tileWidth = max(16, Int((90_000 * aspect).squareRoot().rounded()))
        return (tileWidth, max(9, Int((Double(tileWidth) / aspect).rounded())))
    }

    /// Series sharing a time axis, each drawn on a 0...1 scale.
    struct Plot {
        let label: String
        let series: [Series]
    }

    let title: String
    let subtitle: [String]
    let tiles: [Tile]
    let tileSize: (width: Int, height: Int)
    let times: [Double]
    let plots: [Plot]

    private static let characterWidth = 7, legendLine = 15

    /// The legend's lines when `width` points are available for it.
    private static func legend(_ plot: Plot, width: Int) -> [[Series]] {
        var lines: [[Series]] = [[]], used = characterWidth * (plot.label.count + 2)
        for series in plot.series {
            let needed = characterWidth * (series.name.count + 2)
            if used + needed > width, !(lines.last ?? []).isEmpty {
                lines.append([])
                used = characterWidth * (plot.label.count + 2)
            }
            lines[lines.count - 1].append(series)
            used += needed
        }
        return lines
    }

    func image() -> CGImage? {
        let margin = 18, gap = 10, caption = 30, plotHeight = 150, axis = 22
        let columns = max(1, min(4, tiles.count)), rows = (tiles.count + columns - 1) / columns
        let header = 26 + 18 * subtitle.count + 10
        let width = max(960, 2 * margin + columns * tileSize.width + (columns - 1) * gap)
        let legends = plots.map { Self.legend($0, width: width - 2 * margin) }
        let plotsHeight = legends.reduce(0) { $0 + Self.legendLine * $1.count + 4 + plotHeight + axis }
        let height = margin + header + rows * (tileSize.height + caption + gap) + plotsHeight + 20 + margin
        guard let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                                      space: BGRAImage.colourSpace, bitmapInfo: BGRAImage.bitmapInfo.rawValue) else { return nil }
        // Layout runs top-down; CoreGraphics' origin is the bottom left.
        func box(_ x: Int, _ top: Int, _ w: Int, _ h: Int) -> CGRect {
            CGRect(x: x, y: height - top - h, width: w, height: h)
        }
        func text(_ string: String, _ x: Int, _ top: Int, size: CGFloat = 11, bold: Bool = false,
                  colour: CGColor = Self.colour(0.86, 0.86, 0.86)) {
            let font = CTFontCreateWithName((bold ? "Menlo-Bold" : "Menlo-Regular") as CFString, size, nil)
            let attributes: [NSAttributedString.Key: Any] = [
                NSAttributedString.Key(kCTFontAttributeName as String): font,
                NSAttributedString.Key(kCTForegroundColorAttributeName as String): colour,
            ]
            let line = CTLineCreateWithAttributedString(NSAttributedString(string: string, attributes: attributes) as CFAttributedString)
            context.textMatrix = .identity
            context.textPosition = CGPoint(x: CGFloat(x), y: CGFloat(height - top) - size)
            CTLineDraw(line, context)
        }
        context.setFillColor(Self.colour(0.07, 0.07, 0.08))
        context.fill(CGRect(x: 0, y: 0, width: width, height: height))

        var top = margin
        text(title, margin, top, size: 15, bold: true)
        top += 26
        for line in subtitle {
            text(line, margin, top)
            top += 18
        }
        top += 10

        for (number, tile) in tiles.enumerated() {
            let x = margin + (number % columns) * (tileSize.width + gap)
            let y = top + (number / columns) * (tileSize.height + caption + gap)
            context.draw(tile.image, in: box(x, y, tileSize.width, tileSize.height))
            context.setStrokeColor(Self.colour(0.3, 0.3, 0.32))
            context.stroke(box(x, y, tileSize.width, tileSize.height), width: 1)
            text(String(format: "#%ld  %.2f s", tile.index, tile.time), x, y + tileSize.height + 5)
            // The sound behind this frame, one bar per signal.
            for (bar, value) in tile.bars.enumerated() {
                let barHeight = Int((min(1, max(0, value)) * 20).rounded())
                context.setFillColor(Self.colour(0.2, 0.2, 0.22))
                context.fill(box(x + tileSize.width - 7 * 9 + bar * 9, y + tileSize.height + 5, 7, 20))
                context.setFillColor(Self.barColours[bar % Self.barColours.count])
                context.fill(box(x + tileSize.width - 7 * 9 + bar * 9, y + tileSize.height + 25 - barHeight, 7, barHeight))
            }
        }
        top += rows * (tileSize.height + caption + gap)

        let tileTimes = tiles.map(\.time)
        for (plot, legend) in zip(plots, legends) {
            for (number, line) in legend.enumerated() {
                var x = margin
                if number == 0 { text(plot.label + ":", x, top, colour: Self.colour(0.7, 0.7, 0.7)) }
                x += Self.characterWidth * (plot.label.count + 2)
                for series in line {
                    text(series.name, x, top, colour: series.colour)
                    x += Self.characterWidth * (series.name.count + 2)
                }
                top += Self.legendLine
            }
            top += 4
            draw(plot.series, in: box(margin, top, width - 2 * margin, plotHeight), context: context,
                 tileTimes: tileTimes, text: { text($0, $1, $2, colour: $3) }, height: height)
            top += plotHeight + axis
        }
        text("Tile bars: " + Self.barNames.joined(separator: " "), margin, top, colour: Self.colour(0.6, 0.6, 0.6))
        return context.makeImage()
    }

    private func draw(_ series: [Series], in frame: CGRect, context: CGContext, tileTimes: [Double],
                      text: (String, Int, Int, CGColor) -> Void, height: Int) {
        context.setFillColor(Self.colour(0.11, 0.11, 0.13))
        context.fill(frame)
        guard let first = times.first, let last = times.last else { return }
        let span = max(last - first, 1e-6)
        func x(_ time: Double) -> CGFloat { frame.minX + CGFloat((time - first) / span) * frame.width }
        func y(_ value: Double) -> CGFloat { frame.minY + CGFloat(min(1, max(0, value))) * frame.height }
        context.setLineWidth(1)
        context.setStrokeColor(Self.colour(0.22, 0.22, 0.25))
        for quarter in 1..<4 {
            context.strokeLineSegments(between: [CGPoint(x: frame.minX, y: y(Double(quarter) / 4)),
                                                 CGPoint(x: frame.maxX, y: y(Double(quarter) / 4))])
        }
        // Half-second ticks, labelled every second.
        var tick = (first * 2).rounded(.up) / 2
        while tick <= last + 1e-9 {
            context.setStrokeColor(Self.colour(0.3, 0.3, 0.33))
            context.strokeLineSegments(between: [CGPoint(x: x(tick), y: frame.minY), CGPoint(x: x(tick), y: frame.maxY)])
            if abs(tick - tick.rounded()) < 1e-9 {
                text(String(format: "%.0f s", tick), Int(x(tick)) + 3, height - Int(frame.minY) + 2, Self.colour(0.55, 0.55, 0.58))
            }
            tick += 0.5
        }
        context.setStrokeColor(Self.colour(1, 1, 1, 0.5))
        for time in tileTimes {
            context.strokeLineSegments(between: [CGPoint(x: x(time), y: frame.maxY - 6), CGPoint(x: x(time), y: frame.maxY)])
        }
        context.setLineWidth(1.4)
        for line in series where line.values.count == times.count {
            context.setStrokeColor(line.colour)
            context.beginPath()
            for (index, value) in line.values.enumerated() {
                let point = CGPoint(x: x(times[index]), y: y(value))
                if index == 0 { context.move(to: point) } else { context.addLine(to: point) }
            }
            context.strokePath()
        }
    }
}

/// One scene × fixture rendered to disk: PNG frames, a contact sheet and a log.
@MainActor
enum RenderRun {
    struct Result {
        let entry: JSONValue
        let summary: String
        /// Some frame has visible content.
        let visible: Bool
        /// Some frame differs from the first.
        let animated: Bool
    }

    static func render(_ scene: AudioVisualizerStyle, _ fixture: SyntheticMusic.Fixture, music: SyntheticMusic.Stereo,
                       request: VisualiserRenderRequest, renderer: VisualiserRenderer) async throws -> Result {
        let settings = request.settings
        let relative = "\(scene.rawValue)/\(fixture.rawValue)"
        let directory = request.output.appendingPathComponent(relative, isDirectory: true)
        // A fresh directory, so frames from an earlier, longer render never mix in.
        try? FileManager.default.removeItem(at: directory)
        try FileManager.default.createDirectory(at: directory.appendingPathComponent("frames"), withIntermediateDirectories: true)
        let drawable = settings.drawableSize(for: scene)
        let tileIndices = Set(ContactSheet.tileIndices(frames: settings.capturedFrames, tiles: request.tiles))
        let tileSize = ContactSheet.tileSize(width: settings.width, height: settings.height)
        // The scrim and text backing NowPlayingView lays over the scene.
        let overlay = WallpaperOverlay(width: Int(drawable.width), height: Int(drawable.height), settings: settings)

        var frames: [JSONValue] = [], tiles: [ContactSheet.Tile] = [], times: [Double] = []
        var sound = zip(ContactSheet.barNames, ContactSheet.barColours).map { ContactSheet.Series(name: $0, colour: $1, values: []) }
        var extras: [String: [Double]] = [:], extraOrder: [String] = []
        // New AudioSnapshot fields before smoothing, to spot any the smoother drops.
        var heardExtras: [String: Double] = [:]
        // The picture as shown (under the overlay) and of the scene alone.
        var luminance: [Double] = [], change: [Double] = [], lit: [Double] = []
        var sceneLuminance: [Double] = [], sceneChange: [Double] = [], sceneLit: [Double] = []
        var gpu: [Double] = [], sceneTimes: [Double] = []
        var first: BGRAImage?, animated = false
        let metrics = FrameMetrics(), sceneMetrics = FrameMetrics()
        var uniformFields: [String] = [], colours: [SIMD4<Float>] = []

        try await renderer.render(scene, music: music, settings: settings) { frame in
            let shownFrame = overlay.apply(to: frame.image)
            let drawn = try XCTUnwrap(shownFrame.cgImage)
            let shown = try XCTUnwrap(BGRAImage.scale(drawn, width: settings.width, height: settings.height))
            var file: JSONValue = .null
            if frame.index % request.every == 0 {
                let name = String(format: "frames/frame_%04d.png", frame.index)
                try BGRAImage.writePNG(shown, to: directory.appendingPathComponent(name))
                file = .string(name)
            }
            let reading = metrics.measure(shownFrame)
            let alone = overlay.isClear ? reading : sceneMetrics.measure(frame.image)
            if let first, !animated, !frame.image.identical(to: first) { animated = true }
            if first == nil { first = frame.image }
            times.append(frame.time); gpu.append(frame.gpuMilliseconds)
            luminance.append(reading.luminance); change.append(reading.change); lit.append(reading.lit)
            sceneLuminance.append(alone.luminance); sceneChange.append(alone.change); sceneLit.append(alone.lit)

            let s = frame.sound
            let bars = [s.bass, s.mid, s.treble, s.beat, s.trebleBeat, s.flux, s.level].map(Double.init)
            for (index, value) in bars.enumerated() { sound[index].values.append(value) }
            let uniforms = frame.inputs.uniforms
            sceneTimes.append(Double(uniforms.time))
            let signals = KnownFields.newSignals(in: uniforms, known: KnownFields.uniforms, prefix: "u.")
                + KnownFields.newSignals(in: s, known: KnownFields.sound, prefix: "sound.")
            for (name, value) in signals {
                if extras[name] == nil { extraOrder.append(name) }
                extras[name, default: []].append(value)
            }
            for (name, value) in KnownFields.newSignals(in: frame.analyser, known: KnownFields.sound, prefix: "sound.") {
                heardExtras[name] = max(heardExtras[name] ?? 0, abs(value))
            }
            if tileIndices.contains(frame.index), let tile = BGRAImage.scale(shown, width: tileSize.width, height: tileSize.height) {
                tiles.append(.init(index: frame.index, time: frame.time, image: tile, bars: bars))
            }
            if uniformFields.isEmpty {
                uniformFields = Mirror(reflecting: uniforms).children.compactMap(\.label)
                colours = frame.inputs.colours
            }
            var entry: [(String, JSONValue)] = [
                ("index", .number(Double(frame.index))), ("time", .number(frame.time)), ("file", file),
                ("gpuMilliseconds", .number(frame.gpuMilliseconds)),
                ("meanLuminance", .number(reading.luminance)), ("litFraction", .number(reading.lit)),
                ("change", .number(reading.change)),
                ("sceneLuminance", .number(alone.luminance)), ("sceneLitFraction", .number(alone.lit)),
                ("sceneChange", .number(alone.change)),
                ("uniforms", .reflecting(uniforms)),
                ("sound", .reflecting(s)),
                ("analyser", .reflecting(frame.analyser)),
            ]
            if let field = frame.inputs.field {
                entry.append(("wavefield", .reflecting(field)))
            } else {
                // Exactly what the fragment reads at buffer(1) and buffer(2).
                entry.append(("bound", .object([("bands", .numbers(frame.inputs.bands)),
                                                ("waveform", .numbers(frame.inputs.waveform))])))
            }
            frames.append(.object(entry))
        }

        // Signals over 1 are drawn divided by their maximum.
        func normalised(_ values: [Double]) -> [Double] {
            let peak = values.map(abs).max() ?? 0
            return peak > 1 ? values.map { $0 / peak } : values
        }
        // New signals, each drawn once: one that never moves is only named, a
        // repeat (a uniform passing a snapshot field straight through) joins
        // the first one's name, and counters and clocks (values that only
        // climb) get their own plot, so the levels and envelopes stay readable.
        var levels: [ContactSheet.Series] = [], counters: [ContactSheet.Series] = [], flat: [String] = []
        var drawn: [(values: [Double], counter: Bool, index: Int)] = []
        for name in extraOrder {
            let values = extras[name] ?? []
            if let low = values.min(), let high = values.max(), high - low < 1e-9 {
                flat.append(name + String(format: " %g", low))
                continue
            }
            if let earlier = drawn.first(where: { $0.values == values }) {
                if earlier.counter { counters[earlier.index].name += " = " + name } else { levels[earlier.index].name += " = " + name }
                continue
            }
            let counter = Self.isClockLike(values)
            let suffix = (values.map(abs).max() ?? 0) > 1 ? " (/max)" : ""
            let index = counter ? counters.count : levels.count
            let series = ContactSheet.Series(name: name + suffix,
                colour: ContactSheet.extraColours[index % ContactSheet.extraColours.count], values: normalised(values))
            if counter { counters.append(series) } else { levels.append(series) }
            drawn.append((values, counter, index))
        }
        // A field the analyser fills but the smoothed sound never shows has
        // not been carried through VisualizerSmoother.
        let lostInSmoothing = heardExtras.filter { name, peak in
            peak > 0.01 && (extras[name] ?? []).allSatisfy { abs($0) < 0.001 }
        }.keys.sorted()
        // Each picture series is drawn against its own peak, but never against
        // less than a small floor, so a nearly still scene stays a flat line.
        // The luminance as shown shares the scene's scale, so the shade shows.
        func picture(_ name: String, _ values: [Double], scale: Double, _ colour: CGColor) -> ContactSheet.Series {
            .init(name: name + String(format: " (/%.4g)", scale), colour: colour, values: values.map { $0 / scale })
        }
        let luminanceScale = max(0.005, sceneLuminance.max() ?? 0)
        let pictureSeries = [picture("luminance", luminance, scale: luminanceScale, ContactSheet.colour(0.3, 0.95, 0.95))]
            + (overlay.isClear ? [] : [picture("scene alone", sceneLuminance, scale: luminanceScale, ContactSheet.colour(0.2, 0.5, 0.55))])
            + [picture("lit share", lit, scale: max(0.01, lit.max() ?? 0), ContactSheet.colour(0.95, 0.95, 0.5)),
               picture("change/frame", change, scale: max(0.002, change.max() ?? 0), ContactSheet.colour(1, 0.55, 0.75))]
        func mean(_ values: ArraySlice<Double>) -> Double { values.reduce(0, +) / Double(max(1, values.count)) }
        let gpuMean = mean(gpu[...]), gpuMax = gpu.max() ?? 0
        let changeMean = mean(change.dropFirst())
        let drawableText = "\(Int(drawable.width))x\(Int(drawable.height))"
        // Which sounds the picture follows, whatever its scale: the correlation of
        // its brightness and change with each sound signal over the run. Counters
        // and clocks follow nothing and are left out.
        let followed = (sound + levels).map { ($0.name, $0.values) }
        let follows = [("luminance", luminance), ("change", change)].map { name, values in
            (name, RenderRun.follows(values, followed, skipFirst: name == "change"))
        }
        let followsText = follows.map { name, links -> String in
            let strong = links.filter { abs($0.r) >= 0.3 }.prefix(2)
            return name + (strong.isEmpty ? " follows no signal clearly" : " follows "
                + strong.map { String(format: "%@ (r %.2f)", $0.signal, $0.r) }.joined(separator: ", "))
        }.joined(separator: "; ")
        let luminanceText = String(format: "luminance %.3f-%.3f", luminance.min() ?? 0, luminance.max() ?? 0)
            + (overlay.isClear ? "" : String(format: " as shown, %.3f-%.3f scene alone", sceneLuminance.min() ?? 0,
                                             sceneLuminance.max() ?? 0))
        let sheet = ContactSheet(
            title: "\(scene.title) (\(scene.rawValue)) - \(fixture.rawValue): \(fixture.summary)",
            subtitle: [
                "\(settings.width)x\(settings.height) display, drawn at \(drawableText)"
                    + (settings.fullResolution ? " (full resolution)" : " (the wallpaper's pixel budget) and scaled up")
                    + String(format: " - %g fps - %g s after %g s of pre-roll - %ld frames", settings.fps, settings.seconds,
                             settings.preroll, settings.capturedFrames),
                String(format: "sensitivity %g, punch %g, colour richness %g, speed %g, palette %@%@%@ - scene clock %.2f-%.2f s",
                       settings.sensitivity, settings.punch, settings.vividness, settings.speed, settings.palette.rawValue,
                       settings.reduceMotion ? ", Reduce Motion" : "", settings.dimFlashingLights ? ", Dim Flashing Lights" : "",
                       sceneTimes.first ?? 0, sceneTimes.last ?? 0),
                settings.overlayDescription,
                String(format: "GPU %.2f ms mean, %.2f ms max - %@ - change %.4f per frame at %g fps (motion and "
                    + "smoothing depend on the frame rate)", gpuMean, gpuMax, luminanceText, changeMean, settings.fps),
                followsText,
            ] + (lostInSmoothing.isEmpty ? [] : ["Not carried through VisualizerSmoother: " + lostInSmoothing.joined(separator: ", ")])
              + (flat.isEmpty ? [] : [Self.clipped("New signals flat throughout: " + flat.joined(separator: ", "), to: 150)]),
            tiles: tiles, tileSize: tileSize, times: times,
            plots: [ContactSheet.Plot(label: "sound (0-1)", series: sound)]
                + (levels.isEmpty ? [] : [ContactSheet.Plot(label: "new signals", series: levels)])
                + (counters.isEmpty ? [] : [ContactSheet.Plot(label: "new counters and clocks", series: counters)])
                + [ContactSheet.Plot(label: "picture", series: pictureSeries)])
        let sheetURL = directory.appendingPathComponent("contact-sheet.png")
        try BGRAImage.writePNG(try XCTUnwrap(sheet.image(), "contact sheet"), to: sheetURL)

        func stats(_ values: [Double]) -> JSONValue {
            .object([("min", .number(values.min() ?? 0)), ("mean", .number(mean(values[...]))), ("max", .number(values.max() ?? 0))])
        }
        let summary: JSONValue = .object([
            ("frames", .number(Double(frames.count))), ("gpuMilliseconds", stats(gpu)),
            ("meanLuminance", stats(luminance)), ("change", stats(Array(change.dropFirst()))), ("litFraction", stats(lit)),
            ("sceneLuminance", stats(sceneLuminance)), ("sceneChange", stats(Array(sceneChange.dropFirst()))),
            ("sceneLitFraction", stats(sceneLit)), ("sceneTime", stats(sceneTimes)), ("animated", .bool(animated)),
            ("follows", .object(follows.map { name, links in
                (name, .array(links.map { .object([("signal", .string($0.signal)), ("r", .number($0.r))]) }))
            })),
            ("newSignals", .array(extraOrder.map { .string($0) })),
            ("flatSignals", .array(flat.map { .string($0) })),
            ("lostInSmoothing", .array(lostInSmoothing.map { .string($0) })),
        ])
        let log: JSONValue = .object([
            ("scene", .string(scene.rawValue)), ("title", .string(scene.title)),
            ("fragment", .string(scene.fragment ?? "")),
            ("fixture", .string(fixture.rawValue)), ("fixtureSummary", .string(fixture.summary)),
            ("displaySize", .array([.number(Double(settings.width)), .number(Double(settings.height))])),
            ("drawableSize", .array([.number(drawable.width), .number(drawable.height)])),
            ("fps", .number(settings.fps)), ("seconds", .number(settings.seconds)), ("preroll", .number(settings.preroll)),
            ("start", .number(settings.start)), ("sampleRate", .number(SyntheticMusic.sampleRate)),
            ("settings", .reflecting(settings)),
            ("overlay", .object([
                ("scrim", .number(overlay.scrim)), ("textBacking", .number(overlay.backing)),
                ("textBackingMode", .string(settings.textBacking == nil ? "automatic" : "manual")),
                ("description", .string(settings.overlayDescription)),
            ])),
            ("albumColours", .reflecting(colours)),
            ("device", .string(renderer.gpu.device.name)),
            ("renderer", .string("AudioVisualizerView.Coordinator.prepare(sound:at:timelines:) and encodeFrame, "
                + "fed by VisualizerAudio.append(hostTime:) in AudioPCMProcessor's hop-sized pieces, under "
                + "NowPlayingView's scrim and text backing (WallpaperShade); the lyrics are not drawn")),
            ("timeBase", .string("simulated seconds since the music started; captured frames begin after the pre-roll")),
            ("notes", .array([
                .string("meanLuminance, litFraction and change are of the frame as shown (PNGs and tiles); "
                    + "the scene* fields are of the scene alone, before the overlay"),
                .string("change is measured from one frame to the next, and the smoothed sound and Wavefield's modes "
                    + "step once a frame: compare runs at one frame rate"),
                .string("bound holds the bands and waveform the fragment reads at buffer(1) and buffer(2), in full"),
            ])),
            ("uniformFields", .array(uniformFields.map { .string($0) })),
            ("summary", summary),
            ("frames", .array(frames)),
        ])
        try Data(log.text(pretty: 2).utf8).write(to: directory.appendingPathComponent("frames.json"))

        let line = String(format: "%@: %ld frames %ldx%ld (drawn %@), %@, change %.4f/frame, "
            + "GPU %.2f ms mean %.2f max; %@%@ -> %@", relative, frames.count, settings.width, settings.height, drawableText,
            luminanceText, changeMean, gpuMean, gpuMax, followsText,
            lostInSmoothing.isEmpty ? "" : "; NOT carried through VisualizerSmoother: " + lostInSmoothing.joined(separator: ", "),
            sheetURL.path)
        let entry: JSONValue = .object([
            ("scene", .string(scene.rawValue)), ("fixture", .string(fixture.rawValue)),
            ("directory", .string(relative)), ("contactSheet", .string(relative + "/contact-sheet.png")),
            ("log", .string(relative + "/frames.json")), ("summary", summary),
        ])
        return Result(entry: entry, summary: line, visible: (sceneLit.max() ?? 0) > 0.001, animated: animated)
    }

    /// `text`, cut to `limit` characters with an ellipsis; frames.json has the rest.
    nonisolated static func clipped(_ text: String, to limit: Int) -> String {
        text.count <= limit ? text : String(text.prefix(limit - 1)) + "…"
    }

    /// Whether a signal only climbs and is a count or a clock rather than a
    /// level: integers throughout, or rising by more than 1 over the run.
    nonisolated static func isClockLike(_ values: [Double]) -> Bool {
        guard let first = values.first, let last = values.last, last > first,
              zip(values, values.dropFirst()).allSatisfy({ $1 >= $0 }) else { return false }
        return last - first > 1 || values.allSatisfy { $0 == $0.rounded() }
    }

    /// Pearson correlations of `values` with each signal, strongest first;
    /// signals that never vary are left out.
    nonisolated static func follows(_ values: [Double], _ signals: [(String, [Double])],
                                    skipFirst: Bool) -> [(signal: String, r: Double)] {
        let start = skipFirst ? 1 : 0
        func correlation(_ a: ArraySlice<Double>, _ b: ArraySlice<Double>) -> Double? {
            guard a.count == b.count, a.count > 2 else { return nil }
            let meanA = a.reduce(0, +) / Double(a.count), meanB = b.reduce(0, +) / Double(b.count)
            var ab = 0.0, aa = 0.0, bb = 0.0
            for (x, y) in zip(a, b) {
                ab += (x - meanA) * (y - meanB); aa += (x - meanA) * (x - meanA); bb += (y - meanB) * (y - meanB)
            }
            guard aa > 1e-18, bb > 1e-18 else { return nil }
            return ab / (aa * bb).squareRoot()
        }
        guard values.count > start else { return [] }
        return signals.compactMap { name, signal in
            guard signal.count == values.count, let r = correlation(values[start...], signal[start...]) else { return nil }
            return (name, r)
        }.sorted { abs($0.r) > abs($1.r) }
    }

    /// 16-bit stereo WAV, for listening to what a run responded to.
    nonisolated static func writeWAV(_ music: SyntheticMusic.Stereo, to url: URL) throws {
        var data = Data()
        func append<T: FixedWidthInteger>(_ value: T) { withUnsafeBytes(of: value.littleEndian) { data.append(contentsOf: $0) } }
        let rate = UInt32(SyntheticMusic.sampleRate), bytes = UInt32(music.count * 4)
        data.append(contentsOf: Array("RIFF".utf8)); append(36 + bytes)
        data.append(contentsOf: Array("WAVEfmt ".utf8)); append(UInt32(16)); append(UInt16(1)); append(UInt16(2))
        append(rate); append(rate * 4); append(UInt16(4)); append(UInt16(16))
        data.append(contentsOf: Array("data".utf8)); append(bytes)
        data.reserveCapacity(data.count + Int(bytes))
        for i in 0..<music.count {
            append(Int16((min(1, max(-1, music.left[i])) * 32_767).rounded()))
            append(Int16((min(1, max(-1, music.right[i])) * 32_767).rounded()))
        }
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try data.write(to: url)
    }
}
