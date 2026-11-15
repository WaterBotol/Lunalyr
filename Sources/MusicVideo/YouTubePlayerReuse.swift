import AppKit
import WebKit

// Showing the next YouTube video sooner: a page whose IFrame player is up
// is kept and told to switch videos (`lunalyrLoad`, cueVideoById), instead
// of a new page loading YouTube's script and player again. Measured with a
// hidden, muted web view (16 Nov 2026, three official videos): a new page
// showed its first frame 2.0–2.5 s after it was asked for, a switch in the
// same player 0.8–1.2 s, captions still captured for the new video.
//
// Each song's video is its own wallpaper layer (`WallpaperTransition` holds
// the old one on its last frame until the new one shows), so the player is
// handed from layer to layer: the outgoing layer's page is parked here when
// its layer goes (after the next song's video has faded in), and the video
// after that takes it. So the first change of video still loads a new page,
// and each one after reuses the page from two songs before. One page is
// parked: with several displays, one of their players gets it.

enum YouTubePlayerReuse {
    /// How the page shows a video.
    enum Load: Equatable {
        /// A new page (HTML, YouTube's script, a new player).
        case newPage
        /// The video switched inside the page's ready player.
        case switchVideo
    }

    /// Switch inside the page only when its player is up and well: never a
    /// page still loading, one whose player failed, or one whose web
    /// content quit (each of those loads a new page).
    static func load(hasView: Bool, pageReady: Bool) -> Load {
        hasView && pageReady ? .switchVideo : .newPage
    }

    /// A page is parked for the next video only when its player is ready
    /// and nothing failed. A page let go after a long pause is closed
    /// instead (`releasePlayer`), as before: it frees its memory.
    static func mayPark(pageReady: Bool, failed: Bool) -> Bool { pageReady && !failed }

    /// A parked page is closed after this long unused. The outgoing
    /// layer's page is parked once the next song's video has faded in, so
    /// it waits out that whole song: longer than nearly any song.
    static let parkedLimit: TimeInterval = 15 * 60

    static func mayReuse(parkedFor: TimeInterval) -> Bool {
        parkedFor.isFinite && parkedFor >= 0 && parkedFor < parkedLimit
    }
}

/// The one page kept for the next video, paused and hidden.
@MainActor
final class YouTubePlayerPark {
    static let shared = YouTubePlayerPark()

    private var parked: (view: WKWebView, at: TimeInterval)?
    private var expiry: DispatchWorkItem?
    private let uptime: () -> TimeInterval

    init(uptime: @escaping () -> TimeInterval = { ProcessInfo.processInfo.systemUptime }) {
        self.uptime = uptime
    }

    var hasPage: Bool { parked != nil }

    /// Keeps `view` (already paused, out of its window and without
    /// handlers) for the next video; a page parked before is closed.
    func park(_ view: WKWebView) {
        if let old = parked?.view, old !== view { Self.close(old) }
        parked = (view, uptime())
        expiry?.cancel()
        let work = DispatchWorkItem { [weak self] in
            MainActor.assumeIsolated { self?.drop() }
        }
        expiry = work
        DispatchQueue.main.asyncAfter(deadline: .now() + YouTubePlayerReuse.parkedLimit, execute: work)
    }

    /// The parked page, if it is fresh enough; it is no longer parked.
    func take() -> WKWebView? {
        guard let parked else { return nil }
        self.parked = nil
        expiry?.cancel()
        expiry = nil
        guard YouTubePlayerReuse.mayReuse(parkedFor: uptime() - parked.at) else {
            Self.close(parked.view)
            return nil
        }
        return parked.view
    }

    func drop() {
        expiry?.cancel()
        expiry = nil
        if let view = parked?.view { Self.close(view) }
        parked = nil
    }

    private static func close(_ view: WKWebView) {
        view.evaluateJavaScript("window.lunalyrPause && window.lunalyrPause()", completionHandler: nil)
        view.stopLoading()
        view.loadHTMLString("", baseURL: nil)
    }
}

/// YouTube draws its player controls over the video for the first seconds
/// of play, and again after a pause or a seek: the title and channel along
/// the top, a pause button in the middle, and "Copy link", "More videos"
/// and "Watch on YouTube" along the bottom (with a dark scrim from the top).
/// Filled, they fall off-screen. Shown whole, the player is revealed at
/// once and those regions are covered with a blurred copy of the video's
/// still, feathered into the picture, until the controls have gone.
///
/// Measured in the embed (16 Nov 2026, players 960×540 and 1527×859 pt):
/// the controls are the same size in points at any player size: the top
/// strip 0–60 pt, the middle button 124×56 pt at the centre, the bottom
/// bar 48 pt tall ending 12 pt above the bottom; all hidden 3.8–4.0 s
/// after playing starts (earlier measurements: 4.0–4.4 s).
enum MusicVideoOverlayMask {
    /// How long the regions stay covered after the player starts showing.
    static let hold: TimeInterval = 4.5
    /// Then the cover fades away over this.
    static let fadeOut: TimeInterval = 0.5
    /// The covered regions, a little larger than the controls, in points.
    static let topBand: CGFloat = 76
    static let bottomBand: CGFloat = 76
    static let middle = CGSize(width: 180, height: 100)
    /// How far the cover's edges are feathered, in points.
    static let feather: CGFloat = 14

    /// Whether the cover shows when the player starts showing: shown whole,
    /// whenever it was not already showing (a start, a resume, a seek).
    static func covers(framing: MusicVideoFraming, wasShowing: Bool) -> Bool {
        framing.isWhole && !wasShowing
    }

    /// Whether the picture may be taken (a frozen frame, the ambient glow's
    /// sample): not while the controls are drawn, covered only on screen.
    static func capturable(now: TimeInterval, coveredAt: TimeInterval?) -> Bool {
        guard let coveredAt else { return true }
        return now - coveredAt >= hold || now < coveredAt
    }

    /// The covered regions in the visible part of a whole video, whose 16:9
    /// player (`player`) is centred on it (`visible`); parts off-screen are
    /// left out.
    static func regions(player: CGSize, visible: CGSize) -> [CGRect] {
        guard player.width > 0, player.height > 0, visible.width > 0, visible.height > 0 else { return [] }
        let origin = CGPoint(x: (visible.width - player.width) / 2, y: (visible.height - player.height) / 2)
        let bounds = CGRect(origin: .zero, size: visible)
        let rects = [
            CGRect(x: origin.x, y: origin.y, width: player.width, height: topBand),
            CGRect(x: origin.x + (player.width - middle.width) / 2, y: origin.y + (player.height - middle.height) / 2,
                   width: middle.width, height: middle.height),
            CGRect(x: origin.x, y: origin.y + player.height - bottomBand, width: player.width, height: bottomBand),
        ]
        return rects.map { $0.intersection(bounds) }.filter { !$0.isNull && $0.width > 1 && $0.height > 1 }
    }
}
