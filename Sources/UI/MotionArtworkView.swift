import AVKit
import SwiftUI

/// Plays Apple Music's animated album art as the backdrop, looping silently.
///
/// Shown only for albums that actually ship motion artwork; everything else
/// falls back to the generated colour field. Every display shows the same
/// shared player (`SharedVideoPlayers`) through its own layer, which fades
/// in once it has a frame instead of popping in over the album gradient.
struct MotionArtworkView: NSViewRepresentable {
    let url: URL
    var placement: Settings.MotionPlacement = .fill
    var isPaused = false
    /// Called once the clip has a frame to show (see `CustomWallpaperView`).
    var onFirstFrame: (() -> Void)? = nil

    func makeNSView(context: Context) -> PlayerView {
        let view = PlayerView()
        view.placement = placement
        view.onFirstFrame = onFirstFrame
        view.play(url: url, paused: isPaused)
        return view
    }

    func updateNSView(_ view: PlayerView, context: Context) {
        view.placement = placement
        view.onFirstFrame = onFirstFrame
        view.play(url: url, paused: isPaused)
        view.setPaused(isPaused)
    }

    static func dismantleNSView(_ view: PlayerView, coordinator: ()) {
        view.stop()
    }

    final class PlayerView: NSView {
        private var lease: SharedVideoPlayers.Lease?
        private var playerLayer: AVPlayerLayer?
        private var readyObservation: NSKeyValueObservation?
        private var currentURL: URL?
        private var paused = false
        /// Told once per clip, when it first has a frame.
        var onFirstFrame: (() -> Void)?
        /// The clip's real dimensions, once the asset reports them.
        ///
        /// Only a correction: motion covers are square by construction -- the
        /// provider asks for the square URL -- so layout assumes 1:1 until this
        /// arrives. The first version treated a nil here as "cannot lay out"
        /// and fell back to filling the bounds, which with `.resizeAspect`
        /// letterboxes; placement did nothing at all until the load returned,
        /// and nothing ever if it failed.
        private var naturalSize: CGSize?

        /// What layout works from. Square is the right guess for album art.
        private var videoSize: CGSize {
            guard let s = naturalSize, s.width > 0, s.height > 0 else {
                return CGSize(width: 1, height: 1)
            }
            return s
        }

        var placement: Settings.MotionPlacement = .fill {
            didSet { if placement != oldValue { needsLayout = true } }
        }

        func play(url: URL, paused: Bool) {
            guard currentURL != url else { return }
            stop()
            currentURL = url
            self.paused = paused

            let players = SharedVideoPlayers.shared
            let lease = players.acquire(url, paused: paused) { [weak self] size in
                guard let self, self.currentURL == url else { return }
                self.naturalSize = size
                self.needsLayout = true
            }
            self.lease = lease
            naturalSize = players.naturalSize(for: lease)

            let layer = AVPlayerLayer(player: players.player(for: lease))
            // The layer's own frame does the fitting, so gravity must not also
            // scale -- otherwise the two fight and the anchor is ignored.
            layer.videoGravity = .resizeAspect
            layer.frame = bounds
            // Hidden until it can show a frame, then faded in: the album
            // gradient underneath carries the wait.
            layer.opacity = 0
            readyObservation = layer.observe(\.isReadyForDisplay, options: [.initial, .new]) { [weak self] observed, _ in
                guard observed.isReadyForDisplay else { return }
                DispatchQueue.main.async { self?.reveal(observed) }
            }

            wantsLayer = true
            let host = CALayer()
            // A filled layer is deliberately larger than the view, so without
            // this the overhang draws outside its bounds and over whatever is
            // next to it.
            host.masksToBounds = true
            self.layer = host
            host.addSublayer(layer)

            playerLayer = layer
            needsLayout = true
        }

        private func reveal(_ layer: AVPlayerLayer) {
            guard layer === playerLayer, layer.opacity < 1 else { return }
            let fade = CABasicAnimation(keyPath: "opacity")
            fade.fromValue = layer.presentation()?.opacity ?? layer.opacity
            fade.toValue = 1
            fade.duration = WallpaperMotion.mediaReveal
            fade.timingFunction = LunalyrDesign.Motion.CA.easeOut
            layer.opacity = 1
            layer.add(fade, forKey: "reveal")
            onFirstFrame?()
        }

        func setPaused(_ value: Bool) {
            guard paused != value else { return }
            paused = value
            if let lease { SharedVideoPlayers.shared.setPaused(value, for: lease) }
        }

        func stop() {
            readyObservation?.invalidate()
            readyObservation = nil
            playerLayer?.removeFromSuperlayer()
            playerLayer?.player = nil
            playerLayer = nil
            if let lease { SharedVideoPlayers.shared.release(lease) }
            lease = nil
            currentURL = nil
            naturalSize = nil
        }

        override func layout() {
            super.layout()
            guard let layer = playerLayer else { return }

            let video = videoSize
            guard bounds.width > 0, bounds.height > 0 else { return }

            let scale: CGFloat
            switch placement {
            case .fit:
                scale = min(bounds.width / video.width, bounds.height / video.height)
            case .fill, .fillTop, .fillBottom:
                scale = max(bounds.width / video.width, bounds.height / video.height)
            }

            let size = CGSize(width: video.width * scale, height: video.height * scale)
            let x = (bounds.width - size.width) / 2
            let y: CGFloat
            switch placement {
            // AppKit's origin is bottom-left, so "keep the top of the cover"
            // means pushing the overhang off the bottom of the view.
            case .fillTop: y = bounds.height - size.height
            case .fillBottom: y = 0
            case .fill, .fit: y = (bounds.height - size.height) / 2
            }
            CATransaction.begin()
            CATransaction.setDisableActions(true)
            layer.frame = CGRect(x: x, y: y, width: size.width, height: size.height)
            CATransaction.commit()
        }
    }
}

extension MotionArtworkProvider.Motion {
    /// The rendition to play on a display. A portrait display prefers the
    /// tall cover, but only as a single progressive file. The provider
    /// resolves both renditions to one MP4 wherever Apple publishes one; a
    /// tall cover left as Apple's raw HLS playlist can be slow to start and
    /// loop with a gap, so the square cover fills the portrait display
    /// instead.
    func rendition(portrait: Bool) -> URL {
        guard portrait, let tall, tall.pathExtension.lowercased() == "mp4" else { return square }
        return tall
    }
}
