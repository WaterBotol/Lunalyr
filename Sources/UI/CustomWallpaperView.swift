import AppKit
import ImageIO
import SwiftUI

@MainActor
struct CustomWallpaperView: View {
    let url: URL
    let placement: Settings.MotionPlacement
    let paused: Bool
    /// Called once the picture (or the video's first frame) can be shown, so
    /// the crossfade into it waits for it instead of fading into black and
    /// then popping the picture in. Also called when the picture cannot be
    /// read, so the fade never waits for nothing.
    var onFirstFrame: (() -> Void)? = nil
    @State private var image: NSImage?
    private var alignment: Alignment { placement == .fillTop ? .top : placement == .fillBottom ? .bottom : .center }
    var body: some View {
        Group {
            if CustomWallpaperStore.isVideo(url) {
                MotionArtworkView(url: url, placement: placement, isPaused: paused, onFirstFrame: onFirstFrame)
            } else {
                GeometryReader { geometry in
                    if let image {
                        Image(nsImage: image).resizable()
                            .aspectRatio(contentMode: placement == .fit ? .fit : .fill)
                            .frame(width: geometry.size.width, height: geometry.size.height, alignment: alignment)
                            .clipped()
                    }
                }.task(id: url) {
                    // Decoded off the main thread: a large photo used to
                    // stall the wallpaper (and every other display) while it
                    // was read on the main actor.
                    let decoded = await Self.decode(url)
                    guard !Task.isCancelled else { return }
                    image = decoded
                    onFirstFrame?()
                }
            }
        }.background(.black).ignoresSafeArea()
    }

    /// The largest side a wallpaper picture is decoded at.
    nonisolated static let maxPixelSize = 8192

    /// Reads and fully decodes the picture at `url` on a background thread,
    /// upright (EXIF orientation applied), so drawing it never decodes on
    /// the main thread. Nil when it cannot be read.
    nonisolated static func decode(_ url: URL) async -> NSImage? {
        await Task.detached(priority: .userInitiated) { () -> NSImage? in
            let options: [CFString: Any] = [
                kCGImageSourceCreateThumbnailFromImageAlways: true,
                kCGImageSourceCreateThumbnailWithTransform: true,
                kCGImageSourceShouldCacheImmediately: true,
                kCGImageSourceThumbnailMaxPixelSize: maxPixelSize,
            ]
            if let source = CGImageSourceCreateWithURL(url as CFURL, nil), CGImageSourceGetCount(source) > 0,
               let image = CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary) {
                return NSImage(cgImage: image, size: CGSize(width: image.width, height: image.height))
            }
            // Formats ImageIO cannot open (a PDF, say) still load as before.
            return NSImage(contentsOf: url)
        }.value
    }
}
