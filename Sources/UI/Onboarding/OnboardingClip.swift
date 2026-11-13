import AppKit
import AVFoundation
import SwiftUI

/// A short screen recording of Lunalyr itself, shown in onboarding: the
/// desktop with lyrics, Japanese readings, floating lyrics. Each is an
/// H.264 file with no audio track (`Sources/Resources/Onboarding`), with its
/// first frame beside it as a JPEG poster.
///
/// It plays muted and looping only while its step is on screen and the
/// window is active; under Reduce Motion, in snapshots and in the unit-test
/// host only the poster shows and no player is made.
enum OnboardingClip: String, CaseIterable, Identifiable {
    case hero = "OnboardingHero"
    case readings = "OnboardingReadings"
    case floating = "OnboardingFloating"

    var id: String { rawValue }

    /// The recordings are 960 x 620.
    static let aspect: CGFloat = 960.0 / 620.0

    /// Where in the frame to look, and how far in, when the picture is
    /// smaller than the recording's detail: the lyrics, not the margins.
    var focus: UnitPoint {
        switch self {
        case .hero: return .center
        case .readings: return UnitPoint(x: 0.5, y: 0.53)
        case .floating: return UnitPoint(x: 0.47, y: 0.62)
        }
    }

    var zoom: CGFloat {
        switch self {
        case .hero, .floating: return 1
        case .readings: return 1.6
        }
    }

    var videoURL: URL? { Self.url(rawValue, "mp4") }
    var posterURL: URL? { Self.url(rawValue, "jpg") }

    @MainActor var poster: NSImage? {
        if let cached = Self.posters[self] { return cached }
        let image = posterURL.flatMap(NSImage.init(contentsOf:))
        Self.posters[self] = image
        return image
    }

    @MainActor private static var posters: [OnboardingClip: NSImage?] = [:]

    /// The bundle flattens the Onboarding folder; look in both places.
    private static func url(_ name: String, _ type: String) -> URL? {
        Bundle.main.url(forResource: name, withExtension: type)
            ?? Bundle.main.url(forResource: name, withExtension: type, subdirectory: "Onboarding")
    }

    /// Whether a clip plays now rather than holding its poster.
    static func plays(live: Bool, reduceMotion: Bool, windowActive: Bool,
                      isUnitTesting: Bool = AppRuntime.isUnitTesting) -> Bool {
        live && !reduceMotion && windowActive && !isUnitTesting
    }

    /// The recording's rectangle in a box of `size`: filling it, `zoom`
    /// times larger, moved so `focus` sits as near the centre as the edges
    /// allow. Top-left origin.
    func frame(in size: CGSize) -> CGRect {
        let fill = max(size.width / Self.aspect, size.height) * zoom
        let content = CGSize(width: fill * Self.aspect, height: fill)
        let x = Self.clamp(size.width / 2 - content.width * focus.x, lower: size.width - content.width)
        let y = Self.clamp(size.height / 2 - content.height * focus.y, lower: size.height - content.height)
        return CGRect(origin: CGPoint(x: x, y: y), size: content)
    }

    private static func clamp(_ value: CGFloat, lower: CGFloat) -> CGFloat {
        min(0, max(lower, value))
    }
}

/// A clip in a rounded frame: the poster, and over it the playing video
/// while it may play. No border; the corners take a radius token.
struct OnboardingClipView: View {
    let clip: OnboardingClip
    var cornerRadius: CGFloat = LunalyrDesign.Radius.card
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @Environment(\.lunalyrRendersLiveContent) private var live
    @Environment(\.controlActiveState) private var activeState

    var body: some View {
        let plays = OnboardingClip.plays(live: live, reduceMotion: reduceMotion, windowActive: activeState != .inactive)
        GeometryReader { proxy in
            let frame = clip.frame(in: proxy.size)
            ZStack(alignment: .topLeading) {
                if let poster = clip.poster {
                    Image(nsImage: poster)
                        .resizable()
                        .interpolation(.high)
                        .frame(width: frame.width, height: frame.height)
                        .offset(x: frame.minX, y: frame.minY)
                }
                if let url = clip.videoURL, plays {
                    LoopingClipVideo(url: url, clip: clip)
                        .frame(width: proxy.size.width, height: proxy.size.height)
                }
            }
            .frame(width: proxy.size.width, height: proxy.size.height, alignment: .topLeading)
        }
        .background(Color.black)
        .clipShape(RoundedRectangle(cornerRadius: cornerRadius, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
            .strokeBorder(Color.lunalyrContrastBorder, lineWidth: 1))
        .accessibilityHidden(true)
    }
}

/// The video itself, muted and looping, framed as `OnboardingClip.frame(in:)`
/// says. Made only while it may play, so leaving the step (or the window
/// going inactive) tears the player down.
struct LoopingClipVideo: NSViewRepresentable {
    let url: URL
    let clip: OnboardingClip

    func makeNSView(context: Context) -> LoopingClipVideoView {
        let view = LoopingClipVideoView(clip: clip)
        view.play(url)
        return view
    }

    func updateNSView(_ view: LoopingClipVideoView, context: Context) {}

    static func dismantleNSView(_ view: LoopingClipVideoView, coordinator: ()) {
        view.stop()
    }
}

@MainActor
final class LoopingClipVideoView: NSView {
    private let clip: OnboardingClip
    private let player = AVQueuePlayer()
    private let playerLayer = AVPlayerLayer()
    private var looper: AVPlayerLooper?

    init(clip: OnboardingClip) {
        self.clip = clip
        super.init(frame: .zero)
        player.isMuted = true
        player.volume = 0
        player.preventsDisplaySleepDuringVideoPlayback = false
        player.allowsExternalPlayback = false
        playerLayer.player = player
        playerLayer.videoGravity = .resizeAspectFill
        let host = CALayer()
        host.masksToBounds = true
        layer = host
        wantsLayer = true
        host.addSublayer(playerLayer)
    }

    required init?(coder: NSCoder) { nil }

    override func hitTest(_ point: NSPoint) -> NSView? { nil }

    override func layout() {
        super.layout()
        let frame = clip.frame(in: bounds.size)
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        // Layers count from the bottom; the frame from the top.
        playerLayer.frame = CGRect(x: frame.minX, y: bounds.height - frame.maxY, width: frame.width, height: frame.height)
        CATransaction.commit()
    }

    func play(_ url: URL) {
        let item = AVPlayerItem(asset: AVURLAsset(url: url))
        looper = AVPlayerLooper(player: player, templateItem: item)
        // Muted by construction (and the files have no audio track), checked again before playing.
        player.isMuted = true
        player.volume = 0
        player.play()
    }

    func stop() {
        player.pause()
        looper?.disableLooping()
        looper = nil
        player.removeAllItems()
    }
}
