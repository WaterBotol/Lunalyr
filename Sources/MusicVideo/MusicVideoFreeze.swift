import AppKit
import CoreGraphics

/// Why the video is (or is not) moving, as the sync loop sees it on each
/// check.
enum MusicVideoHold: Equatable {
    /// The song plays inside the video's span, on a live wallpaper.
    case live
    /// The user paused the song, the wallpaper is live, and the song is
    /// inside the video's span: the video holds its frame.
    case userPaused
    /// Anything else: the wallpaper covered or paused, Reduce Motion,
    /// before the video starts or past its end.
    case other

    /// `target` is where the video should be (`MusicVideoTiming.videoTime`),
    /// nil before its start or past its end.
    static func of(surfacePaused: Bool, songPlaying: Bool, target: Double?) -> MusicVideoHold {
        guard !surfacePaused, target != nil else { return .other }
        return songPlaying ? .live : .userPaused
    }
}

/// What the frozen frame needs from the YouTube player around it.
@MainActor
protocol MusicVideoFreezeHost: AnyObject {
    /// A picture of the player as drawn now (crop, scale and blur
    /// included); nil when none could be taken.
    func captureFrame(_ done: @escaping @MainActor (CGImage?) -> Void)
    /// Shows the picture over the (hidden) player, at once.
    func showFrozenFrame(_ image: CGImage)
    /// Hides it. `handOver`: the player is playing again; fade only once
    /// it has faded in, so nothing shows between them.
    func hideFrozenFrame(handOver: Bool)
    /// Pauses the player itself.
    func sendPause()
    /// A long pause: the player is closed to save power.
    func releasePlayer()
    /// Playback resumed after `releasePlayer`: a fresh player.
    func recreatePlayer()
}

/// A paused YouTube video's last frame. YouTube draws a pause button and
/// its title bar over a paused player, so the player is hidden whenever it
/// is not playing. When the user pauses the song, a picture of the player
/// is taken just before it is paused and shown in its place, so the
/// wallpaper keeps the paused video rather than falling back. Other pauses
/// (covered, wallpaper paused, Reduce Motion, outside the video, buffering)
/// fall back as before. After a long pause of any kind (the song paused,
/// or the wallpaper covered, hidden or paused) the player is closed, so a
/// hidden YouTube page uses nothing; the picture stays until a new one
/// plays, and the player opens again once the video should play.
@MainActor
final class MusicVideoFrozenFrame {
    /// After this long not playing, the player is closed (about 10 minutes).
    nonisolated static let longPause: TimeInterval = 600

    weak var host: MusicVideoFreezeHost?
    private(set) var hold: MusicVideoHold = .live
    /// The player is visible: playing, not buffering.
    private(set) var playerShowing = false
    private(set) var image: CGImage?
    private(set) var frameShown = false
    private(set) var capturing = false
    /// The player was closed for a long pause.
    private(set) var released = false
    private var generation = 0
    private var cancelLongPause: (() -> Void)?
    private let longPause: TimeInterval
    private let schedule: (TimeInterval, @escaping @MainActor () -> Void) -> () -> Void

    /// `schedule` runs the work after a delay and returns a cancel; a seam
    /// for tests.
    init(longPause: TimeInterval = MusicVideoFrozenFrame.longPause,
         schedule: @escaping (TimeInterval, @escaping @MainActor () -> Void) -> () -> Void = MusicVideoFrozenFrame.after) {
        self.longPause = longPause
        self.schedule = schedule
    }

    nonisolated static func after(_ delay: TimeInterval, _ work: @escaping @MainActor () -> Void) -> () -> Void {
        let task = Task { @MainActor in
            try? await Task.sleep(for: .seconds(delay))
            guard !Task.isCancelled else { return }
            work()
        }
        return { task.cancel() }
    }

    /// The visibility rule. The frozen frame shows while the user has the
    /// song paused, and on resume until the player plays again; never for
    /// other pauses.
    nonisolated static func showsFrame(hold: MusicVideoHold, playerShowing: Bool, hasImage: Bool) -> Bool {
        guard hasImage else { return false }
        switch hold {
        case .userPaused: return true
        case .live: return !playerShowing
        case .other: return false
        }
    }

    /// From each check of the sync loop.
    func noteHold(_ hold: MusicVideoHold) {
        self.hold = hold
        if hold == .live {
            stopLongPause()
            if released {
                released = false
                host?.recreatePlayer()
            }
        } else if cancelLongPause == nil, !released {
            startLongPause()
        }
        refresh()
    }

    /// The player's state: visible (playing) or hidden.
    func notePlayer(showing: Bool) {
        playerShowing = showing
        refresh()
    }

    /// The sync loop pauses the video. A user pause of a playing video takes
    /// its picture first and shows it, then pauses; anything else pauses.
    func pause() {
        guard !capturing else { return }
        // Once taken, the picture is kept: a later one could catch YouTube's
        // pause button as the pause lands.
        guard hold == .userPaused, playerShowing, !released, image == nil, let host else {
            host?.sendPause()
            return
        }
        capturing = true
        let mine = generation
        host.captureFrame { [weak self] picture in
            guard let self, self.generation == mine else { return }
            self.capturing = false
            switch self.hold {
            case .live:
                // Resumed meanwhile: nothing to pause.
                return
            case .userPaused:
                if let picture { self.image = picture }
                // Shown before the pause, so YouTube's pause button is never seen.
                self.refresh()
            case .other:
                break
            }
            self.host?.sendPause()
        }
    }

    /// A new video, a failure, or the player gone: no frame.
    func reset() {
        generation += 1
        capturing = false
        stopLongPause()
        released = false
        image = nil
        playerShowing = false
        if frameShown {
            frameShown = false
            host?.hideFrozenFrame(handOver: false)
        }
    }

    private func refresh() {
        let show = Self.showsFrame(hold: hold, playerShowing: playerShowing, hasImage: image != nil)
        if show, !frameShown, let image {
            frameShown = true
            host?.showFrozenFrame(image)
        } else if !show, frameShown {
            frameShown = false
            host?.hideFrozenFrame(handOver: hold == .live && playerShowing)
        }
        // Playing again: the picture is out of date.
        if hold == .live && playerShowing && image != nil {
            image = nil
            stopLongPause()
        }
    }

    private func startLongPause() {
        stopLongPause()
        cancelLongPause = schedule(longPause) { [weak self] in
            guard let self else { return }
            self.cancelLongPause = nil
            guard self.hold != .live, !self.released else { return }
            self.released = true
            self.playerShowing = false
            self.host?.releasePlayer()
        }
    }

    private func stopLongPause() {
        cancelLongPause?()
        cancelLongPause = nil
    }

    /// Whether a picture is all but black: WebKit may leave out a video it
    /// draws on the GPU. Such a picture counts as none.
    nonisolated static func looksBlank(_ image: CGImage, threshold: UInt8 = 6) -> Bool {
        let side = 8
        var pixels = [UInt8](repeating: 0, count: side * side * 4)
        let drawn: Bool = pixels.withUnsafeMutableBytes { buffer in
            guard let context = CGContext(data: buffer.baseAddress, width: side, height: side, bitsPerComponent: 8,
                                          bytesPerRow: side * 4, space: CGColorSpaceCreateDeviceRGB(),
                                          bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return false }
            context.interpolationQuality = .medium
            context.draw(image, in: CGRect(x: 0, y: 0, width: side, height: side))
            return true
        }
        guard drawn else { return false }
        for index in stride(from: 0, to: pixels.count, by: 4)
            where max(pixels[index], pixels[index + 1], pixels[index + 2]) > threshold {
            return false
        }
        return true
    }
}

/// The frozen frame, over the hidden player. Layer-hosting, so its fades
/// are its own.
@MainActor
final class MusicVideoFrozenFrameView: NSView {
    /// The player fades in over 0.5 s; the picture leaves after that.
    static let handOverDelay: CFTimeInterval = 0.55
    private let imageLayer = CALayer()
    private var token = 0

    override init(frame: NSRect) {
        super.init(frame: frame)
        let root = CALayer()
        layer = root
        wantsLayer = true
        imageLayer.contentsGravity = .resizeAspectFill
        imageLayer.masksToBounds = true
        imageLayer.opacity = 0
        root.addSublayer(imageLayer)
    }

    required init?(coder: NSCoder) { nil }

    override func hitTest(_ point: NSPoint) -> NSView? { nil }

    override func layout() {
        super.layout()
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        imageLayer.frame = bounds
        CATransaction.commit()
    }

    func show(_ image: CGImage) {
        token += 1
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        imageLayer.removeAllAnimations()
        imageLayer.contents = image
        imageLayer.opacity = 1
        CATransaction.commit()
    }

    func hide(handOver: Bool) {
        token += 1
        let mine = token
        let fade = CABasicAnimation(keyPath: "opacity")
        fade.fromValue = imageLayer.presentation()?.opacity ?? imageLayer.opacity
        fade.toValue = 0
        fade.duration = handOver ? 0.35 : 0.15
        if handOver {
            fade.beginTime = CACurrentMediaTime() + Self.handOverDelay
            fade.fillMode = .backwards
        }
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        CATransaction.setCompletionBlock { [weak self] in
            MainActor.assumeIsolated {
                guard let self, self.token == mine else { return }
                self.imageLayer.contents = nil
            }
        }
        imageLayer.removeAllAnimations()
        imageLayer.opacity = 0
        imageLayer.add(fade, forKey: "fade")
        CATransaction.commit()
    }
}
