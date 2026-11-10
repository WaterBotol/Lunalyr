import AppKit
import ImageIO

/// Turns cover bytes into an image ready to draw, off the main thread.
/// `NSImage(data:)` decodes lazily -- on the main thread, at full size, the
/// first time a wallpaper draws it -- and a library cover can be a 3000px
/// PNG (36 MB decoded). Decoding once here, no larger than any surface
/// shows it, keeps that work and memory off the wallpaper.
enum ArtworkImage {
    /// The longest side kept: a still cover fills a 5K display softened,
    /// so more than this is never seen.
    static let maxPixelSize = 2048

    /// The decoded cover, or nil when the bytes are not an image.
    static func decode(_ data: Data, maxPixelSize: Int = ArtworkImage.maxPixelSize) -> CGImage? {
        guard let source = CGImageSourceCreateWithData(data as CFData, [kCGImageSourceShouldCache: false] as CFDictionary),
              CGImageSourceGetCount(source) > 0 else { return nil }
        let options: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceShouldCacheImmediately: true,
            kCGImageSourceThumbnailMaxPixelSize: maxPixelSize,
        ]
        return CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary)
    }

    static func image(_ cgImage: CGImage) -> NSImage {
        NSImage(cgImage: cgImage, size: NSSize(width: cgImage.width, height: cgImage.height))
    }
}

extension ArtworkImage {
    /// `decode` on a background thread, for callers on the main actor.
    static func decodeDetached(_ data: Data?, maxPixelSize: Int = ArtworkImage.maxPixelSize) async -> CGImage? {
        guard let data else { return nil }
        return await Task.detached(priority: .userInitiated) { decode(data, maxPixelSize: maxPixelSize) }.value
    }
}
