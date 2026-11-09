import CoreGraphics
import Foundation
import ImageIO

/// Tells a real video from a still-image upload (the album cover over the
/// audio). YouTube keeps three small frames of every video, taken about a
/// quarter, half and three quarters of the way through (`1.jpg`, `2.jpg`,
/// `3.jpg`). Each is reduced to a 16×9 greyscale fingerprint of its 16:9
/// picture (the letterbox bars cropped off); when all three are nearly the
/// same, nothing moves in the video.
enum MusicVideoStillFrames {
    static let width = 16
    static let height = 9
    /// Mean absolute difference, in 0...255 grey levels, below which two
    /// fingerprints count as the same picture. JPEG noise between frames of
    /// one still stays around 1; different shots of a real video differ by
    /// tens.
    static let threshold = 4.0

    /// The three frame URLs for a video.
    static func frameURLs(for videoID: String) -> [URL] {
        guard YouTubeVideoID.isValid(videoID) else { return [] }
        return (1...3).compactMap { URL(string: "https://i.ytimg.com/vi/\(videoID)/\($0).jpg") }
    }

    /// A 16×9 greyscale fingerprint of the image's central 16:9 picture.
    static func fingerprint(_ data: Data) -> [UInt8]? {
        guard let source = CGImageSourceCreateWithData(data as CFData, nil),
              let image = CGImageSourceCreateImageAtIndex(source, 0, nil) else { return nil }
        return fingerprint(image)
    }

    static func fingerprint(_ image: CGImage) -> [UInt8]? {
        guard image.width > 0, image.height > 0 else { return nil }
        var pixels = [UInt8](repeating: 0, count: width * height)
        let drawn: Bool = pixels.withUnsafeMutableBytes { buffer in
            guard let context = CGContext(data: buffer.baseAddress, width: width, height: height, bitsPerComponent: 8,
                                          bytesPerRow: width, space: CGColorSpaceCreateDeviceGray(),
                                          bitmapInfo: CGImageAlphaInfo.none.rawValue) else { return false }
            context.interpolationQuality = .medium
            // Aspect-fill into 16:9: a 4:3 frame loses its letterbox bars.
            let scale = max(Double(width) / Double(image.width), Double(height) / Double(image.height))
            let drawWidth = Double(image.width) * scale, drawHeight = Double(image.height) * scale
            context.draw(image, in: CGRect(x: (Double(width) - drawWidth) / 2, y: (Double(height) - drawHeight) / 2,
                                           width: drawWidth, height: drawHeight))
            return true
        }
        return drawn ? pixels : nil
    }

    /// Mean absolute difference between two fingerprints, 0...255.
    static func difference(_ a: [UInt8], _ b: [UInt8]) -> Double {
        guard a.count == b.count, !a.isEmpty else { return .infinity }
        let total = zip(a, b).reduce(0) { $0 + abs(Int($1.0) - Int($1.1)) }
        return Double(total) / Double(a.count)
    }

    /// Whether the frames are all the same picture. Nil when fewer than
    /// three could be read: not enough to judge.
    static func isStill(_ fingerprints: [[UInt8]]) -> Bool? {
        guard fingerprints.count >= 3 else { return nil }
        for i in fingerprints.indices {
            for j in fingerprints.indices where j > i {
                if difference(fingerprints[i], fingerprints[j]) >= threshold { return false }
            }
        }
        return true
    }
}
