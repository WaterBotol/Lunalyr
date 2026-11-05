import AppKit
import AVFoundation
import Combine
import ImageIO
import SwiftUI

/// Keeps white lyrics readable over whatever sits behind the lyric stage.
///
/// The lyrics are white. On a pale cover, a light imported picture or the
/// user's own desktop picture, a fixed scrim of about 12% at the stage left
/// white type at under 2:1. This estimates the backdrop's luminance where the
/// lyrics sit and returns just enough extra darkening, applied as a soft
/// shade behind the stage, for WCAG contrast: 4.5:1, or 3:1 when the type is
/// very large, with readings and translations (white at 70%) at 3:1 or
/// better. The shade holds that strength across the whole stage, where a
/// long line or a block of plain lyrics ends, not only at its centre, and
/// eases out to nothing at the screen's edges. It is capped so the wallpaper
/// always stays visible.
enum LyricLegibility {
    /// Never darken the middle of the wallpaper further than this.
    static let maximumBacking = 0.6
    /// Used when nothing is known about what is behind the lyrics.
    static let unknownBackdropLuminance = 0.35

    // MARK: Shape

    /// Half the lyric stage's width and height, as shares of the view.
    static let stageHalfExtent: (x: Double, y: Double) = {
        let stage = LyricViewportMetrics(size: CGSize(width: 1, height: 1)).stageSize
        return (Double(stage.width) / 2, Double(stage.height) / 2)
    }()

    /// The share of the backing at a point of the view, given from its
    /// centre in view sizes: (0.5, 0) is the middle of its right edge. Full
    /// across the lyric stage, corners included, then easing out to nothing
    /// at the view's edges -- the stage's shape rather than an ellipse, so
    /// the shade covers what the lyrics can reach and no more, and never
    /// runs off the screen's sides while leaving its corners bright.
    static func backingStrength(x: Double, y: Double) -> Double {
        backingProfile(abs(x), plateau: stageHalfExtent.x) * backingProfile(abs(y), plateau: stageHalfExtent.y)
    }

    /// Along one axis: 1 out to `plateau`, easing to 0 at the view's edge.
    static func backingProfile(_ offset: Double, plateau: Double) -> Double {
        guard offset.isFinite else { return 0 }
        let edge = 0.5
        guard offset > plateau else { return 1 }
        guard offset < edge, plateau < edge else { return 0 }
        let t = (offset - plateau) / (edge - plateau)
        return 1 - t * t * (3 - 2 * t)
    }

    /// The shape as an image -- black, as opaque as the backing is strong --
    /// that the wallpaper stretches over itself and fades to the amount
    /// needed: one small texture worked out once, as the shape is a share of
    /// the view whatever its size. Nil only if it could not be made.
    static let backingShade: CGImage? = shadeImage(columns: 256, rows: 144)

    static func shadeImage(columns: Int, rows: Int) -> CGImage? {
        guard columns > 0, rows > 0 else { return nil }
        // Premultiplied black: every colour channel stays 0.
        var pixels = [UInt8](repeating: 0, count: columns * rows * 4)
        for row in 0..<rows {
            let y = (Double(row) + 0.5) / Double(rows) - 0.5
            for column in 0..<columns {
                let x = (Double(column) + 0.5) / Double(columns) - 0.5
                pixels[(row * columns + column) * 4 + 3] = UInt8((backingStrength(x: x, y: y) * 255).rounded())
            }
        }
        guard let provider = CGDataProvider(data: Data(pixels) as CFData),
              let space = CGColorSpace(name: CGColorSpace.sRGB) else { return nil }
        return CGImage(width: columns, height: rows, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: columns * 4,
                       space: space, bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedLast.rawValue),
                       provider: provider, decode: nil, shouldInterpolate: true, intent: .defaultIntent)
    }

    /// A Text backing amount the user set keeps the soft ellipse it has
    /// always been drawn with: it is their own choice of shade, not one
    /// worked out for a contrast target. A multiple of the stage on each
    /// axis, and its strength from the centre (0) to the rim (1).
    static let manualBackingScale = (width: 1.3, height: 1.4)
    static let manualBackingStops: [(location: Double, strength: Double)] =
        [(0, 1), (0.55, 0.9), (0.8, 0.45), (1, 0)]

    // MARK: Contrast

    static func linear(_ channel: Double) -> Double {
        let c = min(1, max(0, channel))
        return c <= 0.04045 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4)
    }

    /// The gamma-encoded grey with this relative luminance.
    static func encoded(_ luminance: Double) -> Double {
        let l = min(1, max(0, luminance))
        return l <= 0.0031308 ? l * 12.92 : 1.055 * pow(l, 1 / 2.4) - 0.055
    }

    /// WCAG relative luminance of a gamma-encoded sRGB colour.
    static func luminance(_ rgb: SIMD3<Double>) -> Double {
        0.2126 * linear(rgb.x) + 0.7152 * linear(rgb.y) + 0.0722 * linear(rgb.z)
    }

    static func whiteContrast(over luminance: Double) -> Double {
        1.05 / (min(1, max(0, luminance)) + 0.05)
    }

    /// 4.5:1 for ordinary lyric sizes; 3:1 once the type is very large.
    static func targetContrast(baseSize: CGFloat) -> Double { baseSize >= 96 ? 3 : 4.5 }

    /// A white-based backing target with headroom for lower-luminance
    /// pastels to keep their hue instead of being lifted nearly to white.
    static func pastelBackingTarget(increaseContrast: Bool) -> Double { increaseContrast ? 8 : 6.5 }

    /// Luminance after a black layer of `darkening` opacity. Desktop
    /// compositing blends gamma-encoded values, so the backdrop is treated as
    /// the grey of the same luminance and scaled in that space.
    static func darkened(_ luminance: Double, by darkening: Double) -> Double {
        linear(encoded(luminance) * (1 - min(1, max(0, darkening))))
    }

    /// Extra darkening, on top of `existing`, for white text to reach
    /// `target` over a backdrop of `luminance`. 0 when it already does.
    static func backing(forLuminance luminance: Double, existingDarkening existing: Double,
                        target: Double, cap: Double = maximumBacking) -> Double {
        guard target.isFinite, target > 1 else { return 0 }
        return backing(forLuminance: luminance, existingDarkening: existing, ceiling: 1.05 / target - 0.05, cap: cap)
    }

    // MARK: Readings and translations

    /// Readings and translations are white at this opacity.
    static let secondaryTextOpacity = 0.7
    /// They are held to 3:1, the floor for large text: they are set at
    /// about a third of the lyric's size.
    static let secondaryTextTarget = 3.0

    /// White text at `opacity` over `luminance`. The text is blended with
    /// what is behind it, in the gamma-encoded space the desktop composites in.
    static func contrast(ofWhiteAt opacity: Double, over luminance: Double) -> Double {
        let alpha = min(1, max(0, opacity))
        let text = linear(alpha + (1 - alpha) * encoded(luminance))
        return (text + 0.05) / (min(1, max(0, luminance)) + 0.05)
    }

    /// The brightest backdrop over which white text at `opacity` still
    /// reaches `target`; negative if none does.
    static func brightestBackdrop(forWhiteAt opacity: Double, target: Double) -> Double {
        guard contrast(ofWhiteAt: opacity, over: 0) >= target else { return -1 }
        var dark = 0.0, light = 1.0
        for _ in 0..<40 {
            let middle = (dark + light) / 2
            if contrast(ofWhiteAt: opacity, over: middle) >= target { dark = middle } else { light = middle }
        }
        return dark
    }

    /// Worked out once: it depends on neither the cover nor the scrim.
    static let secondaryTextCeiling = brightestBackdrop(forWhiteAt: secondaryTextOpacity, target: secondaryTextTarget)

    /// What the stage needs: white lyrics at `target`, and the readings and
    /// translations beside them at 3:1. Where the lyric's own target is
    /// 4.5:1 that already holds; for very large type, whose target is 3:1,
    /// the dimmer readings decide.
    static func stageBacking(forLuminance luminance: Double, existingDarkening existing: Double,
                             target: Double, cap: Double = maximumBacking) -> Double {
        max(backing(forLuminance: luminance, existingDarkening: existing, target: target, cap: cap),
            backing(forLuminance: luminance, existingDarkening: existing, ceiling: secondaryTextCeiling, cap: cap))
    }

    /// Extra darkening, on top of `existing`, to bring a backdrop of
    /// `luminance` down to `ceiling`. 0 when it is no brighter already.
    static func backing(forLuminance luminance: Double, existingDarkening existing: Double,
                        ceiling: Double, cap: Double = maximumBacking) -> Double {
        guard luminance.isFinite, existing.isFinite, ceiling.isFinite else { return 0 }
        let backdrop = encoded(luminance)
        guard backdrop > 0 else { return 0 }
        // Beyond 21:1 nothing is enough; darken as far as allowed.
        guard ceiling > 0 else { return min(max(0, cap), 1) }
        let allowed = encoded(ceiling) / backdrop
        let remaining = 1 - min(0.999, max(0, existing))
        guard allowed < remaining else { return 0 }
        return min(max(0, cap), max(0, 1 - allowed / remaining))
    }

    // MARK: Backdrop estimates

    /// The colour field: large soft blobs of each album stop at 85% over a
    /// darkened average. Any blob can drift under the lyrics, so the brightest
    /// one decides; the pattern's faint white lines add a little.
    static func colourFieldLuminance(_ palette: AlbumWallpaperPalette, patternOpacity: Double?) -> Double {
        let stops = palette.stops.map { SIMD3<Double>(Double($0.x), Double($0.y), Double($0.z)) }
        guard let brightest = stops.max(by: { luminance($0) < luminance($1) }) else { return unknownBackdropLuminance }
        let base = stops.reduce(.zero, +) / Double(stops.count) * 0.45
        var colour = brightest * 0.85 + base * 0.15
        if let patternOpacity { colour += SIMD3(repeating: 0.03 * min(1, max(0, patternOpacity))) }
        return luminance(colour.clamped(lowerBound: .zero, upperBound: SIMD3(repeating: 1)))
    }

    /// GPU effects take their light from the album's first and third stops
    /// and can glow slightly past them.
    static func shaderLuminance(_ palette: AlbumWallpaperPalette) -> Double {
        let tints = [0, 2].compactMap { palette.stops.indices.contains($0) ? palette.stops[$0] : nil }
            .map { SIMD3<Double>(Double($0.x), Double($0.y), Double($0.z)) }
        guard let brightest = tints.max(by: { luminance($0) < luminance($1) }) else { return unknownBackdropLuminance }
        return min(1, luminance(brightest) * 1.1)
    }

    /// Visualisers are mostly dark between their lit ribbons.
    static func visualizerLuminance(_ palette: AlbumWallpaperPalette) -> Double {
        let stops = palette.stops.map { SIMD3<Double>(Double($0.x), Double($0.y), Double($0.z)) }
        return (stops.map(luminance).max() ?? unknownBackdropLuminance) * 0.85
    }

    /// An animated cover shows the cover itself; the middle row of the
    /// sampled 3x3 grid is what fills the screen behind the lyrics.
    static func coverLuminance(hsbGrid grid: [SIMD3<Double>]) -> Double {
        let middle = [3, 4, 5].filter(grid.indices.contains).map { grid[$0] }
        let cells = middle.isEmpty ? grid : middle
        return cells.map { luminance(rgb(hsb: $0)) }.max() ?? unknownBackdropLuminance
    }

    static func rgb(hsb: SIMD3<Double>) -> SIMD3<Double> {
        let hue = (hsb.x.truncatingRemainder(dividingBy: 1) + 1).truncatingRemainder(dividingBy: 1) * 6
        func channel(_ offset: Double) -> Double {
            let k = (hue + offset).truncatingRemainder(dividingBy: 6)
            return hsb.z * (1 - hsb.y * max(0, min(k, 4 - k, 1)))
        }
        return SIMD3(channel(5), channel(3), channel(1))
    }

    // MARK: Images

    /// A high percentile of luminance over the part of `image` that lands
    /// behind the lyric stage when placed on a `screen`-sized desktop.
    static func stageLuminance(of image: CGImage, screen: CGSize, placement: Settings.MotionPlacement,
                               percentile: Double = 0.85) -> Double? {
        guard screen.width > 0, screen.height > 0, image.width > 0, image.height > 0 else { return nil }
        let longest = 72.0
        let ratio = Double(image.width) / Double(image.height)
        let width = max(1, Int(ratio >= 1 ? longest : longest * ratio))
        let height = max(1, Int(ratio >= 1 ? longest / ratio : longest))
        var pixels = [UInt8](repeating: 0, count: width * height * 4)
        let drawn = pixels.withUnsafeMutableBytes { buffer -> Bool in
            guard let space = CGColorSpace(name: CGColorSpace.sRGB),
                  let context = CGContext(data: buffer.baseAddress, width: width, height: height,
                                          bitsPerComponent: 8, bytesPerRow: width * 4, space: space,
                                          bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return false }
            context.setFillColor(CGColor(gray: 0, alpha: 1))
            context.fill(CGRect(x: 0, y: 0, width: width, height: height))
            context.interpolationQuality = .medium
            context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
            return true
        }
        guard drawn else { return nil }
        // Where the image sits on the desktop, top-left origin.
        let imageSize = CGSize(width: image.width, height: image.height)
        let scale = placement == .fit
            ? min(screen.width / imageSize.width, screen.height / imageSize.height)
            : max(screen.width / imageSize.width, screen.height / imageSize.height)
        let shown = CGSize(width: imageSize.width * scale, height: imageSize.height * scale)
        let originX = (screen.width - shown.width) / 2
        let originY: CGFloat
        switch placement {
        case .fillTop: originY = 0
        case .fillBottom: originY = screen.height - shown.height
        case .fill, .fit: originY = (screen.height - shown.height) / 2
        }
        let stage = LyricViewportMetrics(size: screen).stageSize
        let stageRect = CGRect(x: (screen.width - stage.width) / 2, y: (screen.height - stage.height) / 2,
                               width: stage.width, height: stage.height)
        var samples: [Double] = []
        samples.reserveCapacity(width * height)
        for row in 0..<height {
            let y = originY + (CGFloat(row) + 0.5) / CGFloat(height) * shown.height
            for column in 0..<width {
                let x = originX + (CGFloat(column) + 0.5) / CGFloat(width) * shown.width
                guard stageRect.contains(CGPoint(x: x, y: y)) else { continue }
                let offset = (row * width + column) * 4
                samples.append(luminance(SIMD3(Double(pixels[offset]) / 255, Double(pixels[offset + 1]) / 255,
                                               Double(pixels[offset + 2]) / 255)))
            }
        }
        // A fitted image leaves black bars inside the stage.
        if placement == .fit, !shown.equalTo(.zero) {
            let covered = stageRect.intersection(CGRect(x: originX, y: originY, width: shown.width, height: shown.height))
            let barShare = 1 - (covered.isNull ? 0 : covered.width * covered.height / (stageRect.width * stageRect.height))
            let bars = Int((Double(samples.count) * barShare / max(0.01, 1 - barShare)).rounded())
            if bars > 0 { samples.append(contentsOf: repeatElement(0, count: min(bars, 20_000))) }
        }
        guard !samples.isEmpty else { return nil }
        samples.sort()
        let index = min(samples.count - 1, max(0, Int((Double(samples.count - 1) * percentile).rounded())))
        return samples[index]
    }

    /// A small decoded copy, never the full-resolution picture.
    static func thumbnail(at url: URL, maxPixels: Int = 256) -> CGImage? {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil), CGImageSourceGetCount(source) > 0 else { return nil }
        let options: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceThumbnailMaxPixelSize: maxPixels,
        ]
        return CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary)
    }

    static func videoFrame(at url: URL) async -> CGImage? {
        let generator = AVAssetImageGenerator(asset: AVURLAsset(url: url))
        generator.appliesPreferredTrackTransform = true
        generator.maximumSize = CGSize(width: 256, height: 256)
        return try? await generator.image(at: CMTime(seconds: 0.5, preferredTimescale: 600)).image
    }
}

/// The text backing over a view of `size`, `amount` at its strongest: the
/// stage-shaped shade for an amount worked out for contrast, or the user's
/// own amount (`manual`) as the ellipse it has always been.
struct TextBackingShade: View {
    let amount: Double
    var manual = false
    let size: CGSize

    var body: some View {
        if !manual, let shade = LyricLegibility.backingShade {
            Image(decorative: shade, scale: 1)
                .resizable()
                .interpolation(.high)
                .opacity(amount)
                .frame(width: size.width, height: size.height)
        } else {
            let stage = LyricViewportMetrics(size: size).stageSize
            EllipticalGradient(
                stops: LyricLegibility.manualBackingStops.map {
                    .init(color: .black.opacity(amount * $0.strength), location: $0.location)
                },
                center: .center, startRadiusFraction: 0, endRadiusFraction: 0.5
            )
            .frame(width: stage.width * LyricLegibility.manualBackingScale.width,
                   height: stage.height * LyricLegibility.manualBackingScale.height)
            .frame(width: size.width, height: size.height)
        }
    }
}

/// How much of the lyric stage is in use, mirroring what the wallpaper's
/// lyric section shows, so the text backing comes and goes with what it
/// backs. 1 while lyrics, plain lines, a track name, the loading spinner or
/// a notice can show; the notices fade out on their own schedule; 0 once the
/// stage is empty, and 0 while the lyrics are hidden. A paused or stopped
/// player (Music reports both as nothing playing) therefore leaves the
/// desktop picture undarkened.
enum LyricStagePresence {
    struct Inputs: Equatable {
        var hasTrack: Bool
        /// When playback stopped, for the "Nothing playing" notice.
        var idleSince: Date?
        var isLoading: Bool
        var hasTimedLines: Bool
        var hasPlainLines: Bool
        var notFound: Bool
        /// When the "Lyrics unavailable" notice went up.
        var notFoundAt: Date?
        var noLyrics: Settings.NoLyricsBehaviour
        /// False while the lyrics are hidden -- by the user or while a
        /// display is mirrored -- which empties the whole stage.
        var lyricsShown: Bool = true
    }

    struct Timing: Equatable {
        var idleHold: TimeInterval
        var noticeHold: TimeInterval
        var noticeFade: TimeInterval
    }

    static func presence(_ inputs: Inputs, at date: Date, timing: Timing) -> Double {
        func notice(since: Date?, hold: TimeInterval) -> Double {
            guard let since else { return 0 }
            let fade = max(0.001, timing.noticeFade)
            let t = min(max((date.timeIntervalSince(since) - hold) / fade, 0), 1)
            return 1 - t * t * (3 - 2 * t)
        }
        // Hidden lyrics take every notice with them: nothing is left to back.
        guard inputs.lyricsShown else { return 0 }
        guard inputs.hasTrack else { return notice(since: inputs.idleSince, hold: timing.idleHold) }
        if inputs.hasPlainLines || (inputs.isLoading && !inputs.hasTimedLines) { return 1 }
        guard inputs.notFound || !inputs.hasTimedLines else { return 1 }
        switch inputs.noLyrics {
        case .nothing: return 0
        case .trackName: return 1
        case .notice: return notice(since: inputs.notFoundAt, hold: timing.noticeHold)
        }
    }
}

/// What is behind one surface's lyrics when that is a picture rather than
/// the album palette: an imported image or video, or the desktop picture
/// under "Your wallpaper". Measured once per picture, off the main thread.
@MainActor
final class BackdropLuminance: ObservableObject {
    struct Source: Hashable {
        enum Kind: Hashable { case imported(URL), desktop(URL?) }
        let kind: Kind
        let width: Double
        let height: Double
        let placement: Settings.MotionPlacement

        init(kind: Kind, screen: CGSize, placement: Settings.MotionPlacement) {
            self.kind = kind
            width = Double(screen.width)
            height = Double(screen.height)
            self.placement = placement
        }
        var screen: CGSize { CGSize(width: width, height: height) }
    }

    @Published private(set) var luminance: Double?
    private var source: Source?
    private var task: Task<Void, Never>?
    private var desktopPoll: Timer?
    /// Removed with this object too: a surface torn down while following the
    /// desktop picture (a display unplugged, Settings closed) must not leave
    /// its observer registered.
    private var spaceObserver: NotificationObservation?
    private var displayID: CGDirectDisplayID = 0
    private var screen: CGSize = .zero
    private static var cache: [Source: Double] = [:]

    /// Follows the imported file or the desktop picture for this display.
    func track(mode: Settings.BackgroundMode, customPath: String, placement: Settings.MotionPlacement,
               displayID: CGDirectDisplayID, screen: CGSize) {
        self.displayID = displayID
        self.screen = screen
        switch mode {
        case .custom where !customPath.isEmpty:
            stopDesktopTracking()
            measure(Source(kind: .imported(URL(fileURLWithPath: customPath)), screen: screen, placement: placement))
        case .desktop:
            startDesktopTracking()
            measure(desktopSource())
        default:
            stopDesktopTracking()
            measure(nil)
        }
    }

    private func desktopSource() -> Source {
        let screen = NSScreen.screens.first { $0.versoDisplayID == displayID } ?? NSScreen.main
        let url = screen.flatMap { NSWorkspace.shared.desktopImageURL(for: $0) }
        let options = screen.flatMap { NSWorkspace.shared.desktopImageOptions(for: $0) }
        let clips = (options?[.allowClipping] as? NSNumber)?.boolValue ?? true
        return Source(kind: .desktop(url), screen: self.screen, placement: clips ? .fill : .fit)
    }

    private func measure(_ next: Source?) {
        guard next != source else { return }
        source = next
        task?.cancel()
        guard let next else { luminance = nil; return }
        if let cached = Self.cache[next] { luminance = cached; return }
        luminance = nil
        task = Task { [weak self] in
            // Decoding and sampling happen off the main thread.
            let value = await Task.detached(priority: .utility) { await Self.evaluate(next) }.value
            guard let self, !Task.isCancelled, self.source == next else { return }
            if let value { Self.cache[next] = value }
            self.luminance = value
        }
    }

    nonisolated private static func evaluate(_ source: Source) async -> Double? {
        let image: CGImage?
        switch source.kind {
        case .imported(let url):
            image = CustomWallpaperStore.isVideo(url) ? await LyricLegibility.videoFrame(at: url)
                : LyricLegibility.thumbnail(at: url)
        case .desktop(let url):
            // Directories (rotating sets) and dynamic or aerial wallpapers do
            // not decode to one picture; those fall back to a cautious default.
            guard let url, !url.hasDirectoryPath else { return nil }
            image = LyricLegibility.thumbnail(at: url)
        }
        guard !Task.isCancelled, let image else { return nil }
        return LyricLegibility.stageLuminance(of: image, screen: source.screen, placement: source.placement)
    }

    /// macOS announces no desktop-picture change, so check cheaply when the
    /// Space changes and once every half minute.
    private func startDesktopTracking() {
        guard desktopPoll == nil else { return }
        let timer = Timer(timeInterval: 30, repeats: true) { [weak self] timer in
            if self == nil { timer.invalidate(); return }
            Task { @MainActor [weak self] in self?.refreshDesktop() }
        }
        timer.tolerance = 10
        RunLoop.main.add(timer, forMode: .common)
        desktopPoll = timer
        spaceObserver = NotificationObservation(NSWorkspace.shared.notificationCenter,
                                                NSWorkspace.activeSpaceDidChangeNotification) { [weak self] in
            Task { @MainActor [weak self] in self?.refreshDesktop() }
        }
    }

    private func refreshDesktop() {
        guard case .desktop = source?.kind else { return }
        measure(desktopSource())
    }

    private func stopDesktopTracking() {
        desktopPoll?.invalidate()
        desktopPoll = nil
        spaceObserver = nil
    }
}

/// One notification observer, removed when this is released.
final class NotificationObservation {
    private let center: NotificationCenter
    private let token: NSObjectProtocol

    init(_ center: NotificationCenter, _ name: Notification.Name, perform action: @escaping () -> Void) {
        self.center = center
        token = center.addObserver(forName: name, object: nil, queue: .main) { _ in action() }
    }

    deinit { center.removeObserver(token) }
}
