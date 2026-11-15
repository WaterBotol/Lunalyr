import WebKit
import XCTest
@testable import VersoCustom

// Showing YouTube videos sooner: the player shown at once with YouTube's
// controls covered when the video is shown whole, and the next video
// switched to inside a page whose player is already up. No network, no
// player, no sound: the parked pages here are blank web views.

@MainActor
final class MusicVideoReuseTests: XCTestCase {
    // MARK: Covering YouTube's controls

    func testOnlyAWholeVideoThatStartsShowingIsCovered() {
        XCTAssertTrue(MusicVideoOverlayMask.covers(framing: .whole, wasShowing: false))
        XCTAssertTrue(MusicVideoOverlayMask.covers(framing: .ambient, wasShowing: false))
        XCTAssertFalse(MusicVideoOverlayMask.covers(framing: .fill, wasShowing: false), "filled: off-screen")
        XCTAssertFalse(MusicVideoOverlayMask.covers(framing: .whole, wasShowing: true), "already up: no new controls")
    }

    func testTheCoverOutlastsYouTubesControls() {
        // Measured: hidden 3.8–4.4 s after playing starts.
        XCTAssertGreaterThanOrEqual(MusicVideoOverlayMask.hold, 4.4)
        XCTAssertLessThanOrEqual(MusicVideoOverlayMask.hold + MusicVideoOverlayMask.fadeOut, 5.5)
        // Measured sizes (pt): top strip to 60, middle 124×56, bottom bar 48 tall ending 12 above the bottom.
        XCTAssertGreaterThanOrEqual(MusicVideoOverlayMask.topBand, 60 + MusicVideoOverlayMask.feather / 2)
        XCTAssertGreaterThanOrEqual(MusicVideoOverlayMask.bottomBand, 60 + MusicVideoOverlayMask.feather / 2)
        XCTAssertGreaterThanOrEqual(MusicVideoOverlayMask.middle.width, 124 + MusicVideoOverlayMask.feather)
        XCTAssertGreaterThanOrEqual(MusicVideoOverlayMask.middle.height, 56 + MusicVideoOverlayMask.feather)
    }

    func testTheRegionsSitOverTheControlsAtTheWholeLayoutsPlayerSizes() {
        // A 16:9 video on a 1512×982 laptop screen: the whole picture shows.
        let layout = MusicVideoFraming.wholeLayout(contentAspect: 16.0 / 9.0, in: CGSize(width: 1512, height: 982))
        let regions = MusicVideoOverlayMask.regions(player: layout.player, visible: layout.visible)
        XCTAssertEqual(regions.count, 3)
        let top = regions[0], middle = regions[1], bottom = regions[2]
        XCTAssertEqual(top.minY, 0, accuracy: 0.5)
        XCTAssertEqual(top.height, MusicVideoOverlayMask.topBand, accuracy: 0.5)
        XCTAssertEqual(top.width, layout.visible.width, accuracy: 0.5, "the title strip runs the player's width")
        XCTAssertEqual(middle.midX, layout.visible.width / 2, accuracy: 0.5)
        XCTAssertEqual(middle.midY, layout.visible.height / 2, accuracy: 0.5)
        XCTAssertEqual(bottom.maxY, layout.visible.height, accuracy: 0.5)
        // A 4:3 video: the 16:9 player is larger than the picture and its
        // bars fall outside the view, so the controls sit beyond the sides
        // but the strips still reach the visible top and bottom.
        let square = MusicVideoFraming.wholeLayout(contentAspect: 4.0 / 3.0, in: CGSize(width: 1512, height: 982))
        let clipped = MusicVideoOverlayMask.regions(player: square.player, visible: square.visible)
        for region in clipped {
            XCTAssertTrue(CGRect(origin: .zero, size: square.visible).contains(region), "never past the view")
        }
        XCTAssertTrue(MusicVideoOverlayMask.regions(player: .zero, visible: layout.visible).isEmpty)
    }

    func testTheWholePlayerIsNoLongerHeldHidden() {
        // The hold that kept a whole player hidden for 5 s is gone: a player
        // that starts playing is shown, and only buffering hides it.
        XCTAssertTrue(YouTubeEmbed.showsVideo(state: 1))
        XCTAssertEqual(YouTubeEmbed.hideDelay(buffering: true, hold: .live, hasFrame: false, framing: .whole), 0)
    }

    // MARK: Switching videos in the same player

    func testTheNextVideoSwitchesInsideAReadyPlayerOnly() {
        XCTAssertEqual(YouTubePlayerReuse.load(hasView: true, pageReady: true), .switchVideo)
        XCTAssertEqual(YouTubePlayerReuse.load(hasView: true, pageReady: false), .newPage, "loading, failed or crashed")
        XCTAssertEqual(YouTubePlayerReuse.load(hasView: false, pageReady: true), .newPage)
        XCTAssertTrue(YouTubePlayerReuse.mayPark(pageReady: true, failed: false))
        XCTAssertFalse(YouTubePlayerReuse.mayPark(pageReady: true, failed: true), "a failed player is rebuilt")
        XCTAssertFalse(YouTubePlayerReuse.mayPark(pageReady: false, failed: false))
        XCTAssertTrue(YouTubePlayerReuse.mayReuse(parkedFor: 5))
        XCTAssertFalse(YouTubePlayerReuse.mayReuse(parkedFor: YouTubePlayerReuse.parkedLimit))
        XCTAssertFalse(YouTubePlayerReuse.mayReuse(parkedFor: .nan))
    }

    func testTheSwitchScriptCuesTheVideoMutedWithItsCaptions() {
        let script = YouTubeEmbed.switchScript(videoID: "wIgmyE5Juzw", start: 20.7, page: 3, captions: "en-GB")
        XCTAssertEqual(script, "return !!(window.lunalyrLoad && window.lunalyrLoad('wIgmyE5Juzw', 20, 3, 'en'));")
        XCTAssertTrue(YouTubeEmbed.switchScript(videoID: "wIgmyE5Juzw", start: .nan, page: 1, captions: nil)
            .hasSuffix("('wIgmyE5Juzw', 0, 1, null));"))
        XCTAssertTrue(YouTubeEmbed.switchScript(videoID: "wIgmyE5Juzw", start: 0, page: 1, captions: "x'); alert(1")
            .hasSuffix("null));"), "only a language code reaches the page")
        let html = YouTubeEmbed.html(videoID: "wIgmyE5Juzw", start: 0, captions: "en")
        XCTAssertTrue(html.contains("window.lunalyrLoad = function (id, start, pageNo, captions)"))
        XCTAssertTrue(html.contains("player.cueVideoById({videoId: id"), "cued, not played: the sync loop plays it")
        XCTAssertTrue(html.contains("currentVideo = id; currentPage = pageNo;"), "its messages carry the new video")
        XCTAssertTrue(html.contains("if (state === 5 && awaitingCue)"), "ready once cued")
        XCTAssertTrue(html.contains("player.setOption('captions', 'track', {languageCode: lang})"),
                      "captions asked for again for the new video")
        XCTAssertTrue(html.contains("show(-1);\n  player.mute();") || html.contains("player.mute();"), "muted")
    }

    func testAParkedPageIsTakenOnceAndOnlyWhileFresh() {
        var now: TimeInterval = 100
        let park = YouTubePlayerPark(uptime: { now })
        XCTAssertNil(park.take())
        let first = WKWebView(frame: .zero), second = WKWebView(frame: .zero)
        park.park(first)
        park.park(second)
        XCTAssertTrue(park.take() === second, "the latest page; the one before is closed")
        XCTAssertNil(park.take(), "taken once")
        park.park(first)
        now += YouTubePlayerReuse.parkedLimit + 1
        XCTAssertNil(park.take(), "too old: closed, a new page loads")
        park.park(second)
        park.drop()
        XCTAssertFalse(park.hasPage)
    }
}
