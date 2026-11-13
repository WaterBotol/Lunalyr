import AVFoundation
import AppKit

/// The first Settings window of a launch takes about half a second to build
/// (measured 14 Nov 2026: ~460 ms, ~200 ms after that). Rather than a blank
/// window, it opens at once on Lunalyr's liquid-glass logo (`WelcomeGlass.mp4`,
/// silent, at double speed, the logo filling the window), builds Settings
/// behind it, and fades the logo away as soon as Settings is ready and the
/// glass has formed. Never a wait of its own making: a fast Mac sees it for
/// `minimumShown` at most. Once per launch; Reduce Motion, the test host or a
/// missing video skip it; a click skips it.
@MainActor
enum SettingsLoader {
    /// The video plays this much faster than in the welcome.
    static let rate: Float = 2
    /// The least time on screen, so the glass has formed (about 1.5 s of the
    /// video at double speed) rather than flashing past.
    static let minimumShown: TimeInterval = 0.75
    /// The fade into Settings.
    static let fade: TimeInterval = 0.25
    /// The video's logo sits in the middle of its square frame: this zoom
    /// makes the glass tile large in the window.
    static let zoom: CGFloat = 1.6

    static var shown = false

    static func shouldShow(reduceMotion: Bool = NSWorkspace.shared.accessibilityDisplayShouldReduceMotion,
                           alreadyShown: Bool,
                           isUnitTesting: Bool = AppRuntime.isUnitTesting,
                           hasVideo: Bool = WelcomeGlass.url != nil) -> Bool {
        !reduceMotion && !alreadyShown && !isUnitTesting && hasVideo
    }

    /// How long to keep the logo up once Settings is ready, `elapsed` after
    /// the window opened.
    static func remaining(after elapsed: TimeInterval) -> TimeInterval {
        max(0, minimumShown - max(0, elapsed))
    }
}

/// The window's content while Settings builds: black, the logo playing big in
/// the middle, Settings slotted in underneath once it exists.
@MainActor
final class SettingsLoaderView: NSView {
    private let player = AVPlayer()
    private let playerLayer = AVPlayerLayer()
    private let cover = CALayer()
    private var finished = false
    var onSkip: (() -> Void)?

    init(frame: NSRect, url: URL) {
        super.init(frame: frame)
        wantsLayer = true
        layer?.backgroundColor = NSColor.black.cgColor
        cover.backgroundColor = NSColor.black.cgColor
        cover.masksToBounds = true
        layer?.addSublayer(cover)
        player.isMuted = true
        player.volume = 0
        player.actionAtItemEnd = .pause
        player.allowsExternalPlayback = false
        player.preventsDisplaySleepDuringVideoPlayback = false
        playerLayer.player = player
        playerLayer.videoGravity = .resizeAspect
        cover.addSublayer(playerLayer)
        player.replaceCurrentItem(with: AVPlayerItem(url: url))
        player.isMuted = true
        player.playImmediately(atRate: SettingsLoader.rate)
    }

    required init?(coder: NSCoder) { nil }

    override func layout() {
        super.layout()
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        cover.frame = bounds
        let side = min(bounds.width, bounds.height) * SettingsLoader.zoom
        playerLayer.frame = CGRect(x: bounds.midX - side / 2, y: bounds.midY - side / 2, width: side, height: side)
        CATransaction.commit()
    }

    override func mouseDown(with event: NSEvent) { onSkip?() }

    /// Fades out and stops; `done` once gone.
    func dismiss(after delay: TimeInterval, done: @escaping () -> Void) {
        guard !finished else { return }
        finished = true
        DispatchQueue.main.asyncAfter(deadline: .now() + delay) { [weak self] in
            guard let self else { return }
            NSAnimationContext.runAnimationGroup({ context in
                context.duration = SettingsLoader.fade
                context.timingFunction = LunalyrDesign.Motion.CA.easeIn
                self.animator().alphaValue = 0
            }, completionHandler: {
                MainActor.assumeIsolated {
                    self.player.pause()
                    self.player.replaceCurrentItem(with: nil)
                    self.removeFromSuperview()
                    done()
                }
            })
        }
    }

    /// Skipped: gone at once.
    func dismissNow() {
        finished = true
        player.pause()
        player.replaceCurrentItem(with: nil)
        removeFromSuperview()
    }
}
