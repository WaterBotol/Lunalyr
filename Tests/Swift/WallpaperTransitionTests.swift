import XCTest
@testable import VersoCustom

/// Song-change transitions of the wallpaper (`WallpaperTransition`): what
/// holds the outgoing layer, when the hold lets go, and Reduce Motion.
final class WallpaperTransitionTests: XCTestCase {
    private typealias Layer = NowPlayingView.BackgroundLayer
    private typealias Fade = BackgroundCrossfade<Layer>
    private let start = Date(timeIntervalSinceReferenceDate: 50_000)
    private let duration = WallpaperTransition.fadeDuration(reduceMotion: false)

    private let videoA = Layer.musicVideo(.youTube("aaaaaaaaaaa"))
    private let videoB = Layer.musicVideo(.youTube("bbbbbbbbbbb"))
    private let library = Layer.musicVideo(.local(URL(fileURLWithPath: "/tmp/clip.m4v")))
    private let visualiser = Layer.visualizer(.silk)

    private func at(_ seconds: Double) -> Date { start.addingTimeInterval(seconds) }

    private func drawn(_ fade: Fade, _ seconds: Double) -> [Fade.Drawn] {
        fade.layers(at: at(seconds), duration: duration, isOpaque: \.isOpaque)
    }

    /// What NowPlayingView decides for a layer with no first frame yet and
    /// nothing overdue, over something already on screen.
    private func readyAtOnce(_ layer: Layer) -> Bool {
        WallpaperTransition.canDraw(layer.transitionGate, firstFrameDrawn: false, overdue: false)
    }

    // MARK: Gates

    func testVideosAndMovingLayersWaitForTheirFirstFrame() {
        XCTAssertEqual(videoA.transitionGate, .video)
        XCTAssertEqual(library.transitionGate, .video)
        XCTAssertEqual(Layer.musicVideoPending.transitionGate, .videoLookup)
        XCTAssertEqual(visualiser.transitionGate, .firstFrame)
        XCTAssertEqual(Layer.custom(URL(fileURLWithPath: "/tmp/p.png")).transitionGate, .firstFrame)
        XCTAssertEqual(Layer.motion(URL(fileURLWithPath: "/tmp/m.mp4"), still: false).transitionGate, .firstFrame)
        XCTAssertEqual(Layer.shader(.aurora, epoch: 1).transitionGate, .compile)
        for layer in [videoA, library, .musicVideoPending, visualiser] {
            XCTAssertFalse(readyAtOnce(layer), "\(layer.traceName) would fade in before it has a picture")
        }
    }

    func testStillLayersFadeAtOnce() {
        for layer: Layer in [.desktop, .albumGradient, .generated(pattern: nil, seed: 3),
                             .motion(URL(fileURLWithPath: "/tmp/m.mp4"), still: true)] {
            XCTAssertEqual(layer.transitionGate, .none)
            XCTAssertTrue(readyAtOnce(layer), layer.traceName)
        }
    }

    func testAVideoIsReleasedByItsFirstFrameOrItsWait() {
        XCTAssertTrue(WallpaperTransition.canDraw(.video, firstFrameDrawn: true, overdue: false))
        XCTAssertTrue(WallpaperTransition.canDraw(.video, firstFrameDrawn: false, overdue: true),
                      "A player that never shows a picture is faded to anyway, onto its stand-in")
        XCTAssertEqual(WallpaperTransition.firstFrameWait(for: .video), WallpaperTransition.videoFirstFrameWait)
    }

    /// The stand-in is never faded to over a picture on screen; as a
    /// surface's first layer it shows at once rather than leaving black.
    func testTheStandInOnlyShowsWithNothingElseToHold() {
        XCTAssertFalse(WallpaperTransition.canDraw(.videoLookup, firstFrameDrawn: true, overdue: false))
        XCTAssertTrue(WallpaperTransition.canDraw(.videoLookup, firstFrameDrawn: false, overdue: false, firstLayer: true))
        // The look-up's own timeout chooses the fallback first: the stand-in's
        // backstop never fires before it, so a slow look-up does not go
        // video -> stand-in -> fallback.
        XCTAssertGreaterThan(WallpaperTransition.firstFrameWait(for: .videoLookup), WallpaperTransition.videoLookupWait)
    }

    func testAnEffectWaitsOnlyForItsCompile() {
        XCTAssertFalse(WallpaperTransition.canDraw(.compile, firstFrameDrawn: true, overdue: true, compiled: false))
        XCTAssertTrue(WallpaperTransition.canDraw(.compile, firstFrameDrawn: false, overdue: false, compiled: true))
    }

    // MARK: Song changes

    /// Video to video, through the look-up: the old video's held frame stays
    /// fully drawn (never the stand-in) until the new player has a picture,
    /// then one crossfade with full coverage throughout.
    func testVideoToVideoHoldsTheOldFrameUntilTheNewOneShows() {
        var fade = Fade(videoA)
        fade.change(to: .musicVideoPending, ready: readyAtOnce(.musicVideoPending), at: start, duration: duration)
        XCTAssertTrue(fade.isHeld)
        XCTAssertEqual(drawn(fade, 2), [.init(layer: videoA, opacity: 1), .init(layer: .musicVideoPending, opacity: 0)])

        // Found: the stand-in that never showed gives way to the new video,
        // itself held for its first picture.
        fade.change(to: videoB, ready: readyAtOnce(videoB), at: at(2.5), duration: duration)
        XCTAssertEqual(fade.entries.map(\.layer), [videoA, videoB])
        XCTAssertEqual(drawn(fade, 4), [.init(layer: videoA, opacity: 1), .init(layer: videoB, opacity: 0)])
        XCTAssertTrue(WallpaperTransition.revealsAtOnce(isCurrent: fade.current == videoB, held: fade.isHeld),
                      "The held player's first picture shows at once, under the crossfade")
        XCTAssertFalse(WallpaperTransition.revealsAtOnce(isCurrent: fade.current == videoA, held: fade.isHeld))

        // First frame: the crossfade runs from then.
        fade.becameReady(at: at(4))
        XCTAssertFalse(WallpaperTransition.revealsAtOnce(isCurrent: true, held: fade.isHeld))
        let halfway = drawn(fade, 4 + duration / 2)
        XCTAssertEqual(halfway.map(\.layer), [videoA, videoB])
        XCTAssertEqual(halfway[0].opacity, 1, "The outgoing frame stays fully drawn beneath: no dip to black")
        XCTAssertEqual(halfway[1].opacity, 0.5, accuracy: 1e-9)
        XCTAssertEqual(drawn(fade, 4 + duration), [.init(layer: videoB, opacity: 1)])
    }

    /// A video found at once (cached) goes straight to it, held the same way.
    func testAVideoFoundAtOnceIsOneCrossfade() {
        var fade = Fade(library)
        fade.change(to: videoB, ready: readyAtOnce(videoB), at: start, duration: duration)
        XCTAssertEqual(drawn(fade, 1), [.init(layer: library, opacity: 1), .init(layer: videoB, opacity: 0)])
        fade.becameReady(at: at(1))
        XCTAssertEqual(fade.fadesEnd(duration: duration), at(1 + duration))
    }

    /// No video for the next song: the old frame fades straight into the
    /// fallback visualiser once that has drawn, never via the stand-in.
    func testVideoToFallbackSkipsTheStandIn() {
        var fade = Fade(videoA)
        fade.change(to: .musicVideoPending, ready: readyAtOnce(.musicVideoPending), at: start, duration: duration)
        fade.change(to: visualiser, ready: readyAtOnce(visualiser), at: at(WallpaperTransition.videoLookupWait),
                    duration: duration)
        XCTAssertEqual(fade.entries.map(\.layer), [videoA, visualiser])
        fade.becameReady(at: at(3.2))
        XCTAssertEqual(drawn(fade, 3.2 + duration / 2).map(\.layer), [videoA, visualiser])
    }

    /// The fallback visualiser to the next song's video: the visualiser stays
    /// (not the stand-in) until the video shows.
    func testFallbackToVideoHoldsTheVisualiser() {
        var fade = Fade(visualiser)
        fade.change(to: .musicVideoPending, ready: readyAtOnce(.musicVideoPending), at: start, duration: duration)
        fade.change(to: videoB, ready: readyAtOnce(videoB), at: at(1), duration: duration)
        XCTAssertEqual(drawn(fade, 2), [.init(layer: visualiser, opacity: 1), .init(layer: videoB, opacity: 0)])
    }

    /// A slow player: after its wait the fade runs anyway, onto its stand-in.
    func testASlowVideoIsShownAfterItsWait() {
        var fade = Fade(videoA)
        fade.change(to: videoB, ready: readyAtOnce(videoB), at: start, duration: duration)
        let wait = WallpaperTransition.firstFrameWait(for: videoB.transitionGate)
        let overdue = WallpaperTransition.canDraw(videoB.transitionGate, firstFrameDrawn: false, overdue: true)
        XCTAssertTrue(overdue)
        fade.becameReady(at: at(wait))
        XCTAssertEqual(drawn(fade, wait + duration), [.init(layer: videoB, opacity: 1)])
    }

    /// The outgoing wallpaper is never held for long: at most the look-up
    /// and then the new player's first picture.
    func testTheHoldIsBounded() {
        XCTAssertEqual(WallpaperTransition.longestHold,
                       WallpaperTransition.videoLookupWait + WallpaperTransition.videoFirstFrameWait)
        XCTAssertLessThanOrEqual(WallpaperTransition.longestHold, 8)
    }

    /// Two songs sharing a video: the stand-in held over it gives way to the
    /// same video, which carries on with nothing to fade (one layer, one
    /// player).
    func testReturningToTheVideoOnScreenChangesNothing() {
        var fade = Fade(videoA)
        fade.change(to: .musicVideoPending, ready: false, at: start, duration: duration)
        fade.change(to: videoA, ready: false, at: at(1), duration: duration)
        XCTAssertEqual(fade.entries.map(\.layer), [videoA])
        XCTAssertFalse(fade.isHeld)
        XCTAssertNil(fade.fadesEnd(duration: duration))
    }

    /// A new cover (each still cover is its own layer) crossfades at once.
    func testCoverToCoverCrossfades() {
        let first = Layer.motion(URL(fileURLWithPath: "/tmp/a.mp4"), still: true)
        let second = Layer.motion(URL(fileURLWithPath: "/tmp/b.mp4"), still: true)
        var fade = Fade(first)
        fade.change(to: second, ready: readyAtOnce(second), at: start, duration: duration)
        let halfway = drawn(fade, duration / 2)
        XCTAssertEqual(halfway.map(\.layer), [first, second])
        XCTAssertEqual(halfway[0].opacity, 1)
        XCTAssertEqual(halfway[1].opacity, 0.5, accuracy: 1e-9)
    }

    // MARK: Reduce Motion

    func testReduceMotionIsAShortPlainDissolve() {
        XCTAssertEqual(WallpaperTransition.fadeDuration(reduceMotion: false), WallpaperMotion.surfaceFade)
        XCTAssertEqual(WallpaperTransition.fadeDuration(reduceMotion: true), WallpaperMotion.reducedDissolve)
        XCTAssertLessThan(WallpaperTransition.fadeDuration(reduceMotion: true),
                          WallpaperTransition.fadeDuration(reduceMotion: false))
        XCTAssertTrue((0.8...1.2).contains(WallpaperTransition.fadeDuration(reduceMotion: false)))
    }

    /// Reduce Motion keeps the holds: the fade is shorter, never earlier.
    func testReduceMotionKeepsTheHold() {
        let short = WallpaperTransition.fadeDuration(reduceMotion: true)
        var fade = Fade(videoA)
        fade.change(to: videoB, ready: readyAtOnce(videoB), at: start, duration: short)
        XCTAssertEqual(fade.layers(at: at(2), duration: short, isOpaque: \.isOpaque).last?.opacity, 0)
        fade.becameReady(at: at(2))
        XCTAssertEqual(fade.layers(at: at(2 + short), duration: short, isOpaque: \.isOpaque),
                       [.init(layer: videoB, opacity: 1)])
    }
}
