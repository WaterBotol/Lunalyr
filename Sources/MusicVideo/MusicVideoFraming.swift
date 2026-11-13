import AVFoundation
import AppKit
import CoreImage
import SwiftUI

/// How a music video sits on the screen. Set separately for YouTube videos
/// and videos from the Music library (Settings › Wallpaper › Music video).
enum MusicVideoFraming: String, CaseIterable, Identifiable, Sendable {
    /// Aspect-filled, cropping the edges. A YouTube video is zoomed a
    /// further `fillOverscan` so YouTube's title bar and logo fall off-screen.
    case fill
    /// The picture's width matched to the screen's (grown by `wholeBleed`):
    /// nothing is cut from its sides. A picture wider than the screen leaves
    /// space above and below, which shows a blurred, darkened copy of the
    /// album cover or the video's own still, never black bars; a taller one
    /// (4:3 on a laptop) loses a little of its top and bottom. A YouTube player stays hidden
    /// for its first `titleOverlayHold` of play, behind the still, while
    /// YouTube's title overlay fades.
    case whole

    var id: String { rawValue }

    var title: String {
        switch self {
        case .fill: return "Fill the screen"
        case .whole: return "Show the whole video"
        }
    }

    static let youTubeKey = "custom.musicVideoFraming"
    static let libraryKey = "custom.libraryVideoFraming"

    /// A stored value, or `.fill` (the look before the choice existed).
    init(stored: Any?) {
        self = (stored as? String).flatMap(Self.init(rawValue:)) ?? .fill
    }

    /// YouTube's player is 16:9; other shapes are letterboxed inside it.
    static let youTubeAspect: CGFloat = 16.0 / 9.0
    /// How much larger than the screen a filled YouTube player is drawn.
    static let fillOverscan: Double = 1.25
    /// A fitted picture is grown this much, so no hairline shows at its edge.
    static let wholeBleed: CGFloat = 1.01
    /// YouTube shows the video's title, channel, a pause button, "More
    /// videos" and its logo over the first seconds of play, and again after
    /// a pause or a seek. Filled, they are off-screen; fitted, the player is
    /// revealed only once they have faded. Measured on four official videos
    /// (13 Nov 2026): gone 4.0–4.4 s after playing starts, after a start, a
    /// resume and a seek alike.
    static let titleOverlayHold: TimeInterval = 5

    /// The YouTube page's zoom of its player.
    var youTubeOverscan: Double { self == .fill ? Self.fillOverscan : 1 }

    /// How long a YouTube player that starts playing stays hidden.
    var revealHold: TimeInterval { self == .whole ? Self.titleOverlayHold : 0 }

    /// How a library video's AVPlayerLayer draws a picture of
    /// `contentAspect` (nil until known) in `container`: whole matches the
    /// width, which is AVFoundation's fit for a wider picture and its fill
    /// for a taller one.
    func videoGravity(contentAspect: CGFloat?, in container: CGSize) -> AVLayerVideoGravity {
        guard self == .whole else { return .resizeAspectFill }
        guard let contentAspect, contentAspect > 0, contentAspect.isFinite,
              container.width > 0, container.height > 0 else { return .resizeAspect }
        return contentAspect >= container.width / container.height ? .resizeAspect : .resizeAspectFill
    }

    /// The extra scale of a fitted picture (none when filled).
    var bleedScale: CGFloat { self == .whole ? Self.wholeBleed : 1 }

    /// Whether a blurred backdrop shows around the picture.
    var showsBackdrop: Bool { self == .whole }

    /// Where a video of `aspect` (width / height) is drawn in `container`
    /// (centred; may run past the container's edges, which clip it).
    func rect(aspect: CGFloat, in container: CGSize) -> CGRect {
        guard aspect > 0, aspect.isFinite, container.width > 0, container.height > 0 else {
            return CGRect(origin: .zero, size: container)
        }
        let byWidth = CGSize(width: container.width, height: container.width / aspect)
        let byHeight = CGSize(width: container.height * aspect, height: container.height)
        let fits = byWidth.height <= container.height
        var size: CGSize
        switch self {
        case .fill: size = fits ? byHeight : byWidth
        case .whole: size = byWidth
        }
        let grow = self == .whole ? Self.wholeBleed : 1
        size = CGSize(width: size.width * grow, height: size.height * grow)
        return CGRect(x: (container.width - size.width) / 2, y: (container.height - size.height) / 2,
                      width: size.width, height: size.height)
    }

    /// The 16:9 player's size for a picture of `contentAspect` shown in
    /// `frame` (sized to that aspect): YouTube letterboxes or pillarboxes a
    /// picture of another shape inside its player, so the player is drawn
    /// larger and its own bars fall outside the frame (clipped).
    static func playerSize(contentAspect: CGFloat, frame: CGSize) -> CGSize {
        guard contentAspect > 0, contentAspect.isFinite, frame.width > 0, frame.height > 0 else { return frame }
        let width = contentAspect < youTubeAspect ? frame.height * youTubeAspect : frame.width
        return CGSize(width: width, height: width / youTubeAspect)
    }

    /// The share of the picture's area that falls outside `container`,
    /// 0...1. A YouTube video's overscan is included.
    func croppedShare(aspect: CGFloat, in container: CGSize, overscan: Double = 1) -> Double {
        let drawn = rect(aspect: aspect, in: container)
        let width = Double(drawn.width) * overscan, height = Double(drawn.height) * overscan
        guard width > 0, height > 0 else { return 0 }
        let seenWidth = min(width, Double(container.width))
        let seenHeight = min(height, Double(container.height))
        return 1 - (seenWidth * seenHeight) / (width * height)
    }

    /// The share of `container` the picture leaves for the backdrop, 0...1.
    func backdropShare(aspect: CGFloat, in container: CGSize) -> Double {
        let drawn = rect(aspect: aspect, in: container)
        let area = Double(container.width * container.height)
        guard area > 0 else { return 0 }
        let covered = min(Double(drawn.width), Double(container.width)) * min(Double(drawn.height), Double(container.height))
        return max(0, 1 - covered / area)
    }
}

/// The blurred, darkened copy around a fitted video: blurred once, small,
/// off the main thread, then drawn stretched, so the wallpaper never blurs
/// a full-screen image on every frame.
enum MusicVideoBackdrop {
    /// The width the picture is reduced to before blurring.
    static let workingWidth: CGFloat = 256
    /// The blur's radius at that width (about 48 pt on a 1600 pt screen).
    static let blurRadius: Double = 8
    static let darkening: Double = 0.45

    /// A small, blurred copy of `image`, its edges kept (no dark fringe).
    nonisolated static func blurred(_ image: CGImage) -> CGImage? {
        guard image.width > 0, image.height > 0 else { return nil }
        let scale = min(1, workingWidth / CGFloat(image.width))
        let input = CIImage(cgImage: image).transformed(by: CGAffineTransform(scaleX: scale, y: scale))
        let extent = input.extent
        guard let filter = CIFilter(name: "CIGaussianBlur") else { return nil }
        filter.setValue(input.clampedToExtent(), forKey: kCIInputImageKey)
        filter.setValue(blurRadius, forKey: kCIInputRadiusKey)
        guard let output = filter.outputImage?.cropped(to: extent) else { return nil }
        return CIContext(options: [.useSoftwareRenderer: false]).createCGImage(output, from: extent)
    }
}

/// The backdrop around a video shown whole: the album cover, or the YouTube
/// video's own still once it has loaded, blurred and darkened. Clear until
/// one is ready, so the stand-in under it shows.
struct MusicVideoBackdropView: View {
    let artwork: NSImage?
    let videoID: String?
    var large = true
    @State private var cover: CGImage?
    @State private var still: CGImage?

    private struct Key: Hashable {
        let artwork: ObjectIdentifier?
        let videoID: String?
    }

    var body: some View {
        ZStack {
            if let picture = still ?? cover {
                Image(decorative: picture, scale: 1).resizable().interpolation(.high)
                    .aspectRatio(contentMode: .fill)
                    .transition(.opacity)
                Color.black.opacity(MusicVideoBackdrop.darkening)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .clipped()
        .ignoresSafeArea()
        .animation(.easeOut(duration: YouTubeThumbnail.fadeIn), value: still != nil)
        .task(id: Key(artwork: artwork.map(ObjectIdentifier.init), videoID: videoID)) {
            let source = artwork?.cgImage(forProposedRect: nil, context: nil, hints: nil)
            cover = await Self.blur(source)
            still = nil
            guard let videoID, !AppRuntime.isUnitTesting else { return }
            let image = await YouTubeThumbnail.load(videoID, large: large)
            guard !Task.isCancelled else { return }
            let picture = image?.cgImage(forProposedRect: nil, context: nil, hints: nil)
            still = await Self.blur(picture)
        }
    }

    private static func blur(_ image: CGImage?) async -> CGImage? {
        guard let image else { return nil }
        return await Task.detached(priority: .utility) { MusicVideoBackdrop.blurred(image) }.value
    }
}

/// The shape of the picture inside a 16:9 YouTube still: an older or
/// cinema-shaped video is letterboxed or pillarboxed in black by YouTube.
/// Read from the video's own still (maxresdefault.jpg, 16:9), so a video
/// shown whole is cropped to its picture rather than showing those bars.
enum MusicVideoContentShape {
    /// Darker than this (0...255 grey) counts as a bar.
    static let barLevel = 22
    /// A bar must be this share of the still's width or height, each side.
    static let minimumBar = 0.04
    /// The two bars may differ by this share.
    static let symmetry = 0.025
    /// Shapes outside this are not believed (a dark scene, not bars).
    static let believable: ClosedRange<CGFloat> = 1.0...2.6

    /// The picture's aspect (width / height), or nil for a full 16:9 still
    /// (or one whose edges are not clearly bars).
    nonisolated static func aspect(of image: CGImage) -> CGFloat? {
        let width = 160, height = 90
        var pixels = [UInt8](repeating: 0, count: width * height)
        let drawn: Bool = pixels.withUnsafeMutableBytes { buffer in
            guard let context = CGContext(data: buffer.baseAddress, width: width, height: height, bitsPerComponent: 8,
                                          bytesPerRow: width, space: CGColorSpaceCreateDeviceGray(),
                                          bitmapInfo: CGImageAlphaInfo.none.rawValue) else { return false }
            context.interpolationQuality = .medium
            context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
            return true
        }
        guard drawn else { return nil }
        func columnDark(_ x: Int) -> Bool {
            for y in 0..<height where Int(pixels[y * width + x]) > barLevel { return false }
            return true
        }
        func rowDark(_ y: Int) -> Bool {
            for x in 0..<width where Int(pixels[y * width + x]) > barLevel { return false }
            return true
        }
        let left = (0..<width).first { !columnDark($0) } ?? width
        let right = (0..<width).reversed().first { !columnDark($0) }.map { width - 1 - $0 } ?? width
        let top = (0..<height).first { !rowDark($0) } ?? height
        let bottom = (0..<height).reversed().first { !rowDark($0) }.map { height - 1 - $0 } ?? height
        guard left < width / 2, top < height / 2 else { return nil }  // all dark
        let sideBars = bars(left, right, of: width)
        let endBars = bars(top, bottom, of: height)
        guard sideBars || endBars else { return nil }
        // One step inside the bars: a column half bar, half picture is read
        // as picture, and must not leave a sliver of black at the edge.
        let pictureWidth = CGFloat(sideBars ? width - left - right - 2 : width)
        let pictureHeight = CGFloat(endBars ? height - top - bottom - 2 : height)
        guard pictureHeight > 0 else { return nil }
        // Back to the still's own shape (16:9), from the 160×90 grid.
        let aspect = pictureWidth / pictureHeight
        return believable.contains(aspect) ? aspect : nil
    }

    private nonisolated static func bars(_ a: Int, _ b: Int, of length: Int) -> Bool {
        let first = Double(a) / Double(length), second = Double(b) / Double(length)
        return first >= minimumBar && second >= minimumBar && abs(first - second) <= symmetry
    }
}
