import CoreGraphics
import Foundation
import Metal

/// How bright a generated wallpaper (a GPU effect or a visualiser) really is
/// behind the lyrics, measured from the frame it has just drawn.
///
/// The lyric colours used to take it from the cover's colours alone: a
/// pale cover was taken as a pale wallpaper (about 0.8), but most effects
/// draw dark scenes whatever the cover (0.02 to 0.2 behind the lyrics), so
/// the lyrics turned dark -- muddy olive and mauve with a pale halo -- over
/// a dark wallpaper. Measured here twice a second, the lyrics follow what
/// is on the screen.
///
/// Every `interval` the frame being drawn is copied on the GPU, reduced
/// through its mipmaps (each level the average of the one above), and the
/// level about `smallest` pixels wide is read back once the GPU has
/// finished, so the main thread never waits. The reading is the mean
/// relative luminance of `region`, before the scrim and the text backing
/// laid over the surface.
@MainActor
final class StageMeter {
    /// Seconds between readings.
    static let interval = 0.5
    /// Seconds before trying again after a reading could not be taken.
    static let retry = 5.0
    /// The part of the surface the lyrics sit over: the middle 70% of its
    /// width and 50% of its height.
    static let region = CGRect(x: 0.15, y: 0.25, width: 0.7, height: 0.5)
    /// The narrowest mip level read back.
    static let smallest = 48
    /// Readings are rounded to this step, so a scene's flicker does not
    /// redraw the wallpaper twice a second.
    static let step = 0.02

    /// Called on the main actor with each new reading (0...1).
    var onReading: ((Double) -> Void)?

    private var last = -Double.infinity
    private var pending = false
    private var copy: MTLTexture?
    private var readback: MTLBuffer?

    /// Whether a reading is due at `now` (`CACurrentMediaTime`).
    func isDue(at now: Double) -> Bool {
        onReading != nil && !pending && now - last >= Self.interval
    }

    /// The mip level read back for a frame `width` pixels wide: the
    /// smallest at least `smallest` wide (or the full frame, when it is
    /// narrower than that).
    nonisolated static func level(forWidth width: Int) -> Int {
        var level = 0
        var size = max(1, width)
        while size / 2 >= smallest { size /= 2; level += 1 }
        return level
    }

    /// Encodes the reading of `frame`, the texture the frame was just drawn
    /// into (a drawable whose view is not framebuffer-only), on `command`,
    /// before it is presented. False when nothing was encoded.
    @discardableResult
    func measure(_ frame: MTLTexture, on command: MTLCommandBuffer, at now: Double) -> Bool {
        guard isDue(at: now), frame.width > 0, frame.height > 0,
              frame.pixelFormat == .bgra8Unorm || frame.pixelFormat == .bgra8Unorm_srgb else { return false }
        last = now
        let device = frame.device
        if copy == nil || copy?.device !== device || copy?.width != frame.width || copy?.height != frame.height
            || copy?.pixelFormat != frame.pixelFormat {
            let descriptor = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: frame.pixelFormat,
                width: frame.width, height: frame.height, mipmapped: true)
            descriptor.usage = [.shaderRead, .renderTarget]
            descriptor.storageMode = .private
            copy = device.makeTexture(descriptor: descriptor)
        }
        let level = min(Self.level(forWidth: frame.width), max(0, (copy?.mipmapLevelCount ?? 1) - 1))
        let width = max(1, frame.width >> level), height = max(1, frame.height >> level)
        let bytesPerRow = width * 4
        if readback == nil || readback?.device !== device || (readback?.length ?? 0) < bytesPerRow * height {
            readback = device.makeBuffer(length: bytesPerRow * height, options: .storageModeShared)
        }
        guard let copy, let readback, let blit = command.makeBlitCommandEncoder() else {
            last = now - Self.interval + Self.retry
            return false
        }
        let size = MTLSize(width: frame.width, height: frame.height, depth: 1)
        blit.copy(from: frame, sourceSlice: 0, sourceLevel: 0, sourceOrigin: MTLOrigin(x: 0, y: 0, z: 0), sourceSize: size,
                  to: copy, destinationSlice: 0, destinationLevel: 0, destinationOrigin: MTLOrigin(x: 0, y: 0, z: 0))
        if level > 0 { blit.generateMipmaps(for: copy) }
        blit.copy(from: copy, sourceSlice: 0, sourceLevel: level, sourceOrigin: MTLOrigin(x: 0, y: 0, z: 0),
                  sourceSize: MTLSize(width: width, height: height, depth: 1), to: readback, destinationOffset: 0,
                  destinationBytesPerRow: bytesPerRow, destinationBytesPerImage: bytesPerRow * height)
        blit.endEncoding()
        pending = true
        let bgra = frame.pixelFormat
        command.addCompletedHandler { [weak self] buffer in
            let ok = buffer.status == .completed
            let value: Double? = ok ? Self.stageLuminance(bgra: UnsafeRawBufferPointer(start: readback.contents(),
                count: bytesPerRow * height), width: width, height: height, bytesPerRow: bytesPerRow,
                format: bgra) : nil
            DispatchQueue.main.async {
                MainActor.assumeIsolated { self?.finish(value, at: now) }
            }
        }
        return true
    }

    /// Takes a reading measured another way (a web scene's snapshot).
    func record(_ value: Double?, at now: Double) {
        last = now
        finish(value, at: now)
    }

    /// Marks a snapshot reading as under way, so no other is started.
    func begin(at now: Double) {
        last = now
        pending = true
    }

    private func finish(_ value: Double?, at time: Double) {
        pending = false
        guard let value, value.isFinite else {
            last = time - Self.interval + Self.retry
            return
        }
        onReading?(Self.rounded(value))
    }

    nonisolated static func rounded(_ value: Double) -> Double {
        let clamped: Double = min(1, max(0, value))
        return (clamped / step).rounded() * step
    }

    /// The mean relative luminance of `region` in a BGRA (or RGBA) image,
    /// each pixel's sRGB decoded first.
    nonisolated static func stageLuminance(bgra bytes: UnsafeRawBufferPointer, width: Int, height: Int,
                                           bytesPerRow: Int, format: MTLPixelFormat = .bgra8Unorm,
                                           redFirst: Bool = false) -> Double? {
        guard width > 0, height > 0, bytes.count >= bytesPerRow * (height - 1) + width * 4 else { return nil }
        let x0 = min(width - 1, Int((Double(width) * region.minX).rounded(.down)))
        let x1 = max(x0 + 1, min(width, Int((Double(width) * region.maxX).rounded(.up))))
        let y0 = min(height - 1, Int((Double(height) * region.minY).rounded(.down)))
        let y1 = max(y0 + 1, min(height, Int((Double(height) * region.maxY).rounded(.up))))
        let red = redFirst ? 0 : 2, blue = redFirst ? 2 : 0
        var total = 0.0
        var count = 0
        for y in y0..<y1 {
            let row = y * bytesPerRow
            for x in x0..<x1 {
                let i = row + x * 4
                let r: Double = decoded(bytes[i + red])
                let g: Double = decoded(bytes[i + 1])
                let b: Double = decoded(bytes[i + blue])
                var luminance: Double = 0.2126 * r
                luminance += 0.7152 * g
                luminance += 0.0722 * b
                total += luminance
                count += 1
            }
        }
        return count > 0 ? total / Double(count) : nil
    }

    /// sRGB-encoded byte to linear light.
    private nonisolated static let table: [Double] = (0...255).map { index in
        let c: Double = Double(index) / 255
        return c <= 0.04045 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4)
    }

    private nonisolated static func decoded(_ byte: UInt8) -> Double { table[Int(byte)] }
}

extension StageMeter {
    /// The reading of a picture (a web scene's snapshot): drawn small into
    /// an RGBA bitmap, then measured as a frame is.
    nonisolated static func stageLuminance(of image: CGImage, width: Int = 64) -> Double? {
        let height = max(1, Int((Double(width) * Double(image.height) / Double(max(1, image.width))).rounded()))
        let bytesPerRow = width * 4
        var pixels = [UInt8](repeating: 0, count: bytesPerRow * height)
        let drawn: Bool = pixels.withUnsafeMutableBytes { buffer -> Bool in
            guard let space = CGColorSpace(name: CGColorSpace.sRGB),
                  let context = CGContext(data: buffer.baseAddress, width: width, height: height, bitsPerComponent: 8,
                                          bytesPerRow: bytesPerRow, space: space,
                                          bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue) else { return false }
            context.interpolationQuality = .medium
            context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
            return true
        }
        guard drawn else { return nil }
        return pixels.withUnsafeBytes {
            stageLuminance(bgra: $0, width: width, height: height, bytesPerRow: bytesPerRow, redFirst: true)
        }
    }
}
