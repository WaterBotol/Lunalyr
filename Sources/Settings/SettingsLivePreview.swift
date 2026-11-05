import AppKit
import Combine
import ImageIO
import SwiftUI

@MainActor
final class SettingsPreviewDisplay: ObservableObject {
    @Published private(set) var size: CGSize
    /// The macOS desktop picture of the followed screen, put behind the
    /// lyrics while Your wallpaper is chosen -- white lyrics over a light
    /// window background could not be judged at all. `nil` until loaded,
    /// when another mode is chosen, or when macOS has no picture to give.
    @Published private(set) var desktopPicture: NSImage?
    private var displayID: CGDirectDisplayID?
    private var wantsDesktopPicture = false
    private var pictureURL: URL?
    private var modeSubscription: AnyCancellable?

    /// `size` is the followed screen's once `follow` runs; snapshots pass
    /// the display they draw for and never follow one.
    init(size: CGSize = CGSize(width: 1280, height: 720)) {
        self.size = size
    }

    func follow(_ screen: NSScreen?) {
        displayID = (screen ?? NSScreen.main)?.versoDisplayID
        refresh()
    }

    func refresh() {
        guard let screen = NSScreen.screens.first(where: { $0.versoDisplayID == displayID }) ?? NSScreen.main else { return }
        if size != screen.frame.size { size = screen.frame.size }
        updatePicture(for: screen)
    }

    /// Follows the wallpaper mode while Settings is open, loading the desktop
    /// picture only when it is needed.
    func activate() {
        guard modeSubscription == nil else { return }
        // Published delivers the incoming value before the property changes.
        modeSubscription = Settings.shared.$backgroundMode.removeDuplicates().sink { [weak self] mode in
            MainActor.assumeIsolated {
                self?.wantsDesktopPicture = mode == .desktop
                self?.refresh()
            }
        }
    }

    /// Lets the picture go once Settings closes.
    func deactivate() {
        modeSubscription = nil
        wantsDesktopPicture = false
        pictureURL = nil
        desktopPicture = nil
    }

    private func updatePicture(for screen: NSScreen) {
        let url = wantsDesktopPicture ? NSWorkspace.shared.desktopImageURL(for: screen) : nil
        guard url != pictureURL else { return }
        pictureURL = url
        guard let url else {
            desktopPicture = nil
            return
        }
        // Desktop pictures run to 6K and HEIC; decode a preview-sized copy
        // away from the main thread.
        Task.detached(priority: .utility) { [weak self] in
            let image = SettingsPreviewDisplay.thumbnail(of: url, maxPixelSize: 1600)
            await MainActor.run { [weak self] in
                guard let self, self.pictureURL == url else { return }
                self.desktopPicture = image.map { NSImage(cgImage: $0, size: .zero) }
            }
        }
    }

    nonisolated static func thumbnail(of url: URL, maxPixelSize: Int) -> CGImage? {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil), CGImageSourceGetCount(source) > 0 else { return nil }
        let options: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceThumbnailMaxPixelSize: maxPixelSize,
        ]
        return CGImageSourceCreateThumbnailAtIndex(source, CGImageSourceGetPrimaryImageIndex(source), options as CFDictionary)
    }
}

/// Lays out the same full-size desktop canvas, then scales the entire image.
///
/// The preview follows its own window's visibility: minimised, fully
/// covered or on another Space, it stops rendering entirely -- whatever the
/// wallpaper's own "Pause fully covered wallpapers" choice -- and resumes
/// when the window is seen again.
@MainActor
struct SettingsLivePreview: View {
    let desktopSize: CGSize
    var desktopPicture: NSImage?
    @StateObject private var activity = WindowRenderActivity()
    @Model(\.settings) private var settings
    @Model(\.nowPlaying) private var playing
    /// Off in snapshots: the wallpaper draws through Metal and WebKit.
    @Environment(\.lunalyrRendersLiveContent) private var rendersLiveContent
    /// Set once the window is known, so nothing is built for a window that
    /// has not been shown yet.
    @State private var tracking = false

    var body: some View {
        GeometryReader { geometry in
            ZStack {
                if settings.backgroundMode == .desktop {
                    DesktopPictureBackdrop(picture: desktopPicture)
                        .frame(width: geometry.size.width, height: geometry.size.height)
                }
                if !rendersLiveContent {
                    LiveContentPlaceholder(kind: .wallpaper, overDesktop: settings.backgroundMode == .desktop)
                } else if tracking && activity.shouldRender(pauseWhenCovered: true) {
                    NowPlayingView(displayID: 0, renderActivity: activity, isSettingsPreview: true)
                        .environmentObject(playing)
                        .frame(width: desktopSize.width, height: desktopSize.height)
                        .scaleEffect(LyricViewportMetrics.previewScale(desktop: desktopSize, inside: geometry.size))
                        .frame(width: geometry.size.width, height: geometry.size.height)
                }
            }
            .clipped()
            .allowsHitTesting(false)
            .accessibilityElement(children: .ignore)
            .accessibilityLabel("Live wallpaper preview")
        }
        .background(HostWindowReader { window in
            if let window, rendersLiveContent {
                activity.follow(window: window)
                tracking = true
            } else {
                activity.stop()
                tracking = false
            }
        })
        .onDisappear {
            activity.stop()
            tracking = false
        }
    }
}

/// What sits behind the lyrics in the Your wallpaper preview: the desktop
/// picture, or a dark neutral that white lyrics can be judged against.
private struct DesktopPictureBackdrop: View {
    let picture: NSImage?

    var body: some View {
        if let picture {
            // macOS fills the screen with the picture by default.
            Image(nsImage: picture).resizable().scaledToFill()
        } else {
            Color(white: 0.16)
        }
    }
}
