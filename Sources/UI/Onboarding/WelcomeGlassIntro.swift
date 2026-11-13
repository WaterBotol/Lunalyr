import AVFoundation
import SwiftUI

/// The welcome's opening: Lunalyr's logo forming as a drop of liquid glass
/// (`WelcomeGlass.mp4`, made with ReactDrops' Liquid Glass Logo from our own
/// logo; the video has no sound track and is played muted regardless). It
/// plays once per launch, then the welcome's wallpaper preview takes over.
/// Reduce Motion, and the unit-test host, skip it.
enum WelcomeGlass {
    static let resource = "WelcomeGlass"
    /// After the video ends, its last frame (the plain logo) holds this long
    /// before the preview fades in over it.
    static let hold: TimeInterval = 0.35
    /// The cross-fade into the preview.
    static let fade: TimeInterval = 0.6

    static var url: URL? { Bundle.main.url(forResource: resource, withExtension: "mp4") }

    /// Whether the intro plays now: once per launch, with motion allowed,
    /// outside tests, and only when the video is in the bundle.
    @MainActor static func shouldPlay(reduceMotion: Bool, alreadyPlayed: Bool, isUnitTesting: Bool = AppRuntime.isUnitTesting,
                                      hasVideo: Bool = WelcomeGlass.url != nil) -> Bool {
        !reduceMotion && !alreadyPlayed && !isUnitTesting && hasVideo
    }

    /// Set once the intro has played in this launch (Back to the welcome
    /// shows the preview straight away).
    @MainActor static var played = false
}

/// The video, muted, played once from the start; tells `onFinish` when it
/// has ended (or failed to load, so the welcome never sticks on black).
struct WelcomeGlassVideo: NSViewRepresentable {
    let url: URL
    let onFinish: () -> Void

    func makeNSView(context: Context) -> WelcomeGlassVideoView {
        let view = WelcomeGlassVideoView()
        view.play(url, onFinish: onFinish)
        return view
    }

    func updateNSView(_ view: WelcomeGlassVideoView, context: Context) {}

    static func dismantleNSView(_ view: WelcomeGlassVideoView, coordinator: ()) {
        view.stop()
    }
}

@MainActor
final class WelcomeGlassVideoView: NSView {
    private let player = AVPlayer()
    private let playerLayer = AVPlayerLayer()
    private var endObserver: NSObjectProtocol?
    private var statusObservation: NSKeyValueObservation?
    private var finished = false
    private var onFinish: (() -> Void)?

    override init(frame: NSRect) {
        super.init(frame: frame)
        player.isMuted = true
        player.volume = 0
        player.actionAtItemEnd = .pause
        player.preventsDisplaySleepDuringVideoPlayback = false
        player.allowsExternalPlayback = false
        playerLayer.player = player
        playerLayer.videoGravity = .resizeAspect
        let host = CALayer()
        host.backgroundColor = NSColor.black.cgColor
        layer = host
        wantsLayer = true
        host.addSublayer(playerLayer)
    }

    required init?(coder: NSCoder) { nil }

    override func hitTest(_ point: NSPoint) -> NSView? { nil }

    override func layout() {
        super.layout()
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        playerLayer.frame = bounds
        CATransaction.commit()
    }

    func play(_ url: URL, onFinish: @escaping () -> Void) {
        self.onFinish = onFinish
        let item = AVPlayerItem(url: url)
        endObserver = NotificationCenter.default.addObserver(forName: .AVPlayerItemDidPlayToEndTime, object: item,
                                                             queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.finish() }
        }
        statusObservation = item.observe(\.status, options: [.new]) { [weak self] item, _ in
            guard item.status == .failed else { return }
            DispatchQueue.main.async { self?.finish() }
        }
        player.replaceCurrentItem(with: item)
        // Muted by construction, checked again before playing.
        player.isMuted = true
        player.volume = 0
        player.play()
    }

    private func finish() {
        guard !finished else { return }
        finished = true
        onFinish?()
    }

    func stop() {
        player.pause()
        if let endObserver { NotificationCenter.default.removeObserver(endObserver) }
        endObserver = nil
        statusObservation = nil
        player.replaceCurrentItem(with: nil)
    }
}
