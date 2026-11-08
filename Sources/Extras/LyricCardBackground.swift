import AppKit
import AVFoundation
import ImageIO
import UniformTypeIdentifiers

// MARK: - A picture, GIF or video behind the lyric card
//
// The card normally sits on the album's colours and a blurred cover. A custom
// background replaces that: any image, an animated GIF, or a video (MP4, MOV,
// M4V…). A moving one plays under the lyrics for as long as the card runs and
// loops if it is shorter. Frames are decoded as they are needed, so a long
// video costs no more memory than a picture.

final class LyricCardBackground: @unchecked Sendable {
    enum Kind { case image, animatedImage, video }

    let url: URL
    let kind: Kind
    /// Seconds one pass lasts; 0 for a picture.
    let duration: Double
    /// The first frame, used for stills and the preview.
    let first: CGImage
    /// Whether a video background carries sound.
    let hasAudio: Bool

    private let source: CGImageSource?
    private let delays: [Double]
    private let generator: AVAssetImageGenerator?

    static let maximumPixels = 1920

    var isAnimated: Bool { kind != .image }
    var name: String { url.lastPathComponent }

    private init(url: URL, kind: Kind, duration: Double, first: CGImage, hasAudio: Bool = false, source: CGImageSource? = nil,
                 delays: [Double] = [], generator: AVAssetImageGenerator? = nil) {
        self.url = url; self.kind = kind; self.duration = duration; self.first = first; self.hasAudio = hasAudio
        self.source = source; self.delays = delays; self.generator = generator
    }

    /// Types the picker offers.
    static var contentTypes: [UTType] { [.image, .movie] }

    /// Opens an image, GIF or video; nil when it cannot be read.
    static func load(_ url: URL) async -> LyricCardBackground? {
        let type = UTType(filenameExtension: url.pathExtension.lowercased())
        if let type, type.conforms(to: .movie) || type.conforms(to: .video) {
            return await loadVideo(url)
        }
        return loadImage(url)
    }

    private static func thumbnail(_ source: CGImageSource, at index: Int) -> CGImage? {
        CGImageSourceCreateThumbnailAtIndex(source, index, [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceThumbnailMaxPixelSize: maximumPixels,
        ] as CFDictionary)
    }

    private static func loadImage(_ url: URL) -> LyricCardBackground? {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil), CGImageSourceGetCount(source) > 0,
              let first = thumbnail(source, at: 0) else { return nil }
        let count = CGImageSourceGetCount(source)
        guard count > 1 else { return LyricCardBackground(url: url, kind: .image, duration: 0, first: first) }
        let delays = (0..<count).map { index -> Double in
            let properties = CGImageSourceCopyPropertiesAtIndex(source, index, nil) as? [CFString: Any]
            let gif = properties?[kCGImagePropertyGIFDictionary] as? [CFString: Any]
            let value = (gif?[kCGImagePropertyGIFUnclampedDelayTime] as? Double).flatMap { $0 > 0 ? $0 : nil }
                ?? (gif?[kCGImagePropertyGIFDelayTime] as? Double) ?? 0.1
            return max(0.02, value)
        }
        return LyricCardBackground(url: url, kind: .animatedImage, duration: delays.reduce(0, +), first: first,
                                   source: source, delays: delays)
    }

    private static func loadVideo(_ url: URL) async -> LyricCardBackground? {
        let asset = AVURLAsset(url: url)
        guard let length = try? await asset.load(.duration), length.seconds.isFinite, length.seconds > 0 else { return nil }
        let generator = AVAssetImageGenerator(asset: asset)
        generator.appliesPreferredTrackTransform = true
        generator.maximumSize = CGSize(width: maximumPixels, height: maximumPixels)
        let tolerance = CMTime(seconds: 1.0 / 60, preferredTimescale: 600)
        generator.requestedTimeToleranceBefore = tolerance
        generator.requestedTimeToleranceAfter = tolerance
        guard let first = try? await generator.image(at: .zero).image else { return nil }
        let sound = ((try? await asset.loadTracks(withMediaType: .audio)) ?? []).isEmpty == false
        return LyricCardBackground(url: url, kind: .video, duration: length.seconds, first: first, hasAudio: sound,
                                   generator: generator)
    }

    /// The frame showing `time` seconds in, looping when the clip is shorter.
    func frame(at time: Double) async -> CGImage {
        switch kind {
        case .image:
            return first
        case .animatedImage:
            guard let source, duration > 0 else { return first }
            let position = time.truncatingRemainder(dividingBy: duration)
            var elapsed = 0.0
            for (index, delay) in delays.enumerated() {
                elapsed += delay
                if position < elapsed { return Self.thumbnail(source, at: index) ?? first }
            }
            return first
        case .video:
            guard let generator, duration > 0 else { return first }
            let position = min(max(0, time.truncatingRemainder(dividingBy: duration)), max(0, duration - 0.05))
            let cm = CMTime(seconds: position, preferredTimescale: 600)
            return (try? await generator.image(at: cm).image) ?? first
        }
    }
}
