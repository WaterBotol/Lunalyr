import AppKit
import AVFoundation
import XCTest
@testable import VersoCustom

// "Show the whole video": the setting, the layout maths, the embed page's
// zoom and the reveal hold. Preferences are a UUID suite; nothing loads.

@MainActor
final class MusicVideoFramingTests: XCTestCase {
    private var defaults: UserDefaults!

    override func setUp() async throws {
        let name = "LunalyrTests.MusicVideoFraming.\(UUID().uuidString)"
        defaults = UserDefaults(suiteName: name)!
        addTeardownBlock { UserDefaults(suiteName: name)?.removePersistentDomain(forName: name) }
    }

    private let laptop = CGSize(width: 1512, height: 982)   // 16:10-ish built-in screen
    private let wide = CGSize(width: 2560, height: 1440)    // 16:9 external
    private let tall = CGSize(width: 1080, height: 1920)    // portrait
    private let hd: CGFloat = 16.0 / 9.0

    // MARK: Setting

    func testDefaultsToFillSoNothingChanges() {
        let settings = VersoCustom.Settings(defaults: defaults, notifications: NotificationCenter())
        XCTAssertEqual(settings.musicVideoFraming, .fill)
        XCTAssertEqual(settings.libraryVideoFraming, .fill)
        XCTAssertNil(defaults.object(forKey: MusicVideoFraming.youTubeKey))
        XCTAssertNil(defaults.object(forKey: MusicVideoFraming.libraryKey))
    }

    func testEachChoiceIsRememberedOnItsOwn() {
        let settings = VersoCustom.Settings(defaults: defaults, notifications: NotificationCenter())
        settings.musicVideoFraming = .whole
        var reloaded = VersoCustom.Settings(defaults: defaults, notifications: NotificationCenter())
        XCTAssertEqual(reloaded.musicVideoFraming, .whole)
        XCTAssertEqual(reloaded.libraryVideoFraming, .fill, "library videos keep their own choice")
        settings.libraryVideoFraming = .whole
        settings.musicVideoFraming = .fill
        reloaded = VersoCustom.Settings(defaults: defaults, notifications: NotificationCenter())
        XCTAssertEqual(reloaded.musicVideoFraming, .fill)
        XCTAssertEqual(reloaded.libraryVideoFraming, .whole)
    }

    func testUnknownStoredValuesFallBackToFill() {
        XCTAssertEqual(MusicVideoFraming(stored: nil), .fill)
        XCTAssertEqual(MusicVideoFraming(stored: "zoom"), .fill)
        XCTAssertEqual(MusicVideoFraming(stored: 3), .fill)
        XCTAssertEqual(MusicVideoFraming(stored: "whole"), .whole)
        defaults.set("bogus", forKey: MusicVideoFraming.youTubeKey)
        XCTAssertEqual(VersoCustom.Settings(defaults: defaults, notifications: NotificationCenter()).musicVideoFraming, .fill)
    }

    func testTitles() {
        XCTAssertEqual(MusicVideoFraming.fill.title, "Fill the screen")
        XCTAssertEqual(MusicVideoFraming.whole.title, "Show the whole video")
        XCTAssertEqual(MusicVideoFraming.allCases, [.fill, .whole])
    }

    // MARK: Layout maths

    func testWholeFitsTheVideoWithAtMostATinyBleed() {
        for screen in [laptop, wide, tall] {
            let rect = MusicVideoFraming.whole.rect(aspect: hd, in: screen)
            XCTAssertEqual(rect.width / rect.height, hd, accuracy: 0.001, "keeps its shape")
            XCTAssertEqual(rect.midX, screen.width / 2, accuracy: 0.001)
            XCTAssertEqual(rect.midY, screen.height / 2, accuracy: 0.001)
            let crop = MusicVideoFraming.whole.croppedShare(aspect: hd, in: screen)
            XCTAssertLessThanOrEqual(crop, 0.021, "a 1% bleed each way loses about 2% of the area, \(screen)")
            XCTAssertLessThanOrEqual(rect.width, screen.width * 1.0101)
            XCTAssertLessThanOrEqual(rect.height, screen.height * 1.0101)
        }
        XCTAssertEqual(MusicVideoFraming.whole.youTubeOverscan, 1, "no overscan")
        XCTAssertLessThanOrEqual(MusicVideoFraming.wholeBleed, 1.02)
        XCTAssertGreaterThanOrEqual(MusicVideoFraming.wholeBleed, 1)
    }

    func testWholeOnALaptopLeavesBandsForTheBackdrop() {
        let rect = MusicVideoFraming.whole.rect(aspect: hd, in: laptop)
        XCTAssertEqual(rect.width, laptop.width * MusicVideoFraming.wholeBleed, accuracy: 0.01, "fits the width")
        XCTAssertLessThan(rect.height, laptop.height)
        let backdrop = MusicVideoFraming.whole.backdropShare(aspect: hd, in: laptop)
        XCTAssertGreaterThan(backdrop, 0.05)
        XCTAssertLessThan(backdrop, 0.15)
        XCTAssertEqual(MusicVideoFraming.whole.backdropShare(aspect: hd, in: wide), 0, accuracy: 0.0001,
                       "a 16:9 screen is filled exactly")
        let portrait = MusicVideoFraming.whole.rect(aspect: hd, in: tall)
        XCTAssertEqual(portrait.width, tall.width * MusicVideoFraming.wholeBleed, accuracy: 0.01)
        XCTAssertGreaterThan(MusicVideoFraming.whole.backdropShare(aspect: hd, in: tall), 0.6)
    }

    func testWholeMatchesTheWidthEvenWhenTheVideoIsNarrower() {
        for screen in [laptop, wide, tall] {
            let rect = MusicVideoFraming.whole.rect(aspect: 4.0 / 3.0, in: screen)
            XCTAssertEqual(rect.width, screen.width * MusicVideoFraming.wholeBleed, accuracy: 0.01,
                           "the picture's width is the screen's, \(screen)")
            XCTAssertEqual(rect.width / rect.height, 4.0 / 3.0, accuracy: 0.001)
            XCTAssertEqual(rect.midY, screen.height / 2, accuracy: 0.001, "centred; top and bottom lose alike")
        }
        let onLaptop = MusicVideoFraming.whole.rect(aspect: 4.0 / 3.0, in: laptop)
        XCTAssertGreaterThan(onLaptop.height, laptop.height, "4:3 on a 16:10 screen runs past the top and bottom")
        XCTAssertEqual(MusicVideoFraming.whole.backdropShare(aspect: 4.0 / 3.0, in: laptop), 0, accuracy: 0.0001,
                       "no side bands")
        let cinema = MusicVideoFraming.whole.rect(aspect: 2.39, in: laptop)
        XCTAssertLessThan(cinema.height, laptop.height, "a wider picture leaves bands above and below")
    }

    func testLibraryGravityMatchesTheWidth() {
        let whole = MusicVideoFraming.whole
        XCTAssertEqual(whole.videoGravity(contentAspect: hd, in: laptop), .resizeAspect, "wider: bands above and below")
        XCTAssertEqual(whole.videoGravity(contentAspect: 4.0 / 3.0, in: laptop), .resizeAspectFill,
                       "taller: width matched, top and bottom cut")
        XCTAssertEqual(whole.videoGravity(contentAspect: nil, in: laptop), .resizeAspect, "unknown yet")
        XCTAssertEqual(whole.videoGravity(contentAspect: hd, in: tall), .resizeAspect)
        XCTAssertEqual(MusicVideoFraming.fill.videoGravity(contentAspect: hd, in: laptop), .resizeAspectFill)
        XCTAssertEqual(MusicVideoFraming.fill.videoGravity(contentAspect: nil, in: laptop), .resizeAspectFill)
    }

    func testFillCoversTheScreenAndYouTubeCropsMore() {
        for screen in [laptop, wide, tall] {
            let rect = MusicVideoFraming.fill.rect(aspect: hd, in: screen)
            XCTAssertGreaterThanOrEqual(rect.width + 0.001, screen.width)
            XCTAssertGreaterThanOrEqual(rect.height + 0.001, screen.height)
            XCTAssertEqual(MusicVideoFraming.fill.backdropShare(aspect: hd, in: screen), 0, accuracy: 0.0001)
        }
        XCTAssertEqual(MusicVideoFraming.fill.youTubeOverscan, YouTubeEmbed.overscan)
        let library = MusicVideoFraming.fill.croppedShare(aspect: hd, in: wide)
        let youTube = MusicVideoFraming.fill.croppedShare(aspect: hd, in: wide, overscan: YouTubeEmbed.overscan)
        XCTAssertEqual(library, 0, accuracy: 0.0001)
        XCTAssertEqual(youTube, 1 - 1 / (1.25 * 1.25), accuracy: 0.0001, "36% of the picture is lost to the overscan")
        XCTAssertLessThan(MusicVideoFraming.whole.croppedShare(aspect: hd, in: laptop),
                          MusicVideoFraming.fill.croppedShare(aspect: hd, in: laptop, overscan: YouTubeEmbed.overscan))
    }

    func testDegenerateSizesDoNotCrash() {
        for framing in MusicVideoFraming.allCases {
            XCTAssertEqual(framing.rect(aspect: hd, in: .zero), .zero)
            XCTAssertEqual(framing.rect(aspect: 0, in: wide), CGRect(origin: .zero, size: wide))
            XCTAssertEqual(framing.rect(aspect: .nan, in: wide), CGRect(origin: .zero, size: wide))
            XCTAssertEqual(framing.croppedShare(aspect: hd, in: .zero), 0)
            XCTAssertEqual(framing.backdropShare(aspect: hd, in: .zero), 0)
        }
    }

    func testLibraryGravity() {
        XCTAssertEqual(MusicVideoFraming.fill.bleedScale, 1)
        XCTAssertTrue(MusicVideoFraming.whole.showsBackdrop)
        XCTAssertFalse(MusicVideoFraming.fill.showsBackdrop)
    }

    // MARK: YouTube page and reveal

    func testEmbedPageZoomFollowsTheFraming() {
        let filled = YouTubeEmbed.html(videoID: "4NRXx6U8ABQ", start: 0)
        XCTAssertTrue(filled.contains("scale(1.25)"))
        XCTAssertTrue(filled.contains("overscan = 1.25,"))
        let whole = YouTubeEmbed.html(videoID: "4NRXx6U8ABQ", start: 0, overscan: MusicVideoFraming.whole.youTubeOverscan)
        XCTAssertTrue(whole.contains("scale(1.0)"))
        XCTAssertTrue(whole.contains("overscan = 1.0,"))
        XCTAssertTrue(whole.contains("window.lunalyrOverscan = function (scale)"), "changed live without a reload")
        XCTAssertTrue(whole.contains("scale(' + (overscan * (blurPx > 0 ? 1.04 : 1))"), "the blur keeps its own margin")
        XCTAssertTrue(YouTubeEmbed.html(videoID: "4NRXx6U8ABQ", start: 0, overscan: .nan).contains("overscan = 1.25,"))
        XCTAssertTrue(YouTubeEmbed.html(videoID: "4NRXx6U8ABQ", start: 0, overscan: 9).contains("overscan = 2.0,"))
        XCTAssertTrue(YouTubeEmbed.html(videoID: "4NRXx6U8ABQ", start: 0, overscan: 0.5).contains("overscan = 1.0,"))
    }

    func testWholeHoldsTheRevealUntilYouTubesTitleHasFaded() {
        XCTAssertEqual(YouTubeEmbed.revealDelay(framing: .fill, showing: false), 0, "filled: the title is off-screen")
        XCTAssertEqual(YouTubeEmbed.revealDelay(framing: .whole, showing: false), MusicVideoFraming.titleOverlayHold)
        XCTAssertEqual(YouTubeEmbed.revealDelay(framing: .whole, showing: true), 0, "already up: not hidden again")
        XCTAssertGreaterThanOrEqual(MusicVideoFraming.titleOverlayHold, 4.5, "measured: overlays gone 4.0–4.4 s in")
        XCTAssertLessThanOrEqual(MusicVideoFraming.titleOverlayHold, 6)
    }

    func testWholeHidesABufferingPlayerAtOnce() {
        XCTAssertEqual(YouTubeEmbed.hideDelay(buffering: true, hold: .live, hasFrame: false, framing: .fill),
                       YouTubeEmbed.bufferingGrace, "filled: a drift seek does not blink")
        XCTAssertEqual(YouTubeEmbed.hideDelay(buffering: true, hold: .live, hasFrame: false, framing: .whole), 0,
                       "whole: YouTube's overlays come back after a seek, so the player hides and is held again")
        XCTAssertEqual(YouTubeEmbed.hideDelay(buffering: true, hold: .live, hasFrame: false),
                       YouTubeEmbed.bufferingGrace, "fill by default")
    }

    // MARK: The picture's own shape

    func testPlayerIsDrawnLargerSoYouTubesBarsFallOutsideTheFrame() {
        let frame = CGSize(width: 1200, height: 900)          // a 4:3 picture's frame
        let pillar = MusicVideoFraming.playerSize(contentAspect: 4.0 / 3.0, frame: frame)
        XCTAssertEqual(pillar.height, 900, accuracy: 0.01)
        XCTAssertEqual(pillar.width, 1600, accuracy: 0.01, "16:9 at the picture's height")
        let cinema = CGSize(width: 1512, height: 1512 / 2.39)
        let letter = MusicVideoFraming.playerSize(contentAspect: 2.39, frame: cinema)
        XCTAssertEqual(letter.width, 1512, accuracy: 0.01)
        XCTAssertEqual(letter.height, 1512 * 9 / 16, accuracy: 0.01, "16:9 at the picture's width")
        let hdFrame = CGSize(width: 1600, height: 900)
        XCTAssertEqual(MusicVideoFraming.playerSize(contentAspect: hd, frame: hdFrame).width, 1600, accuracy: 0.01)
        XCTAssertEqual(MusicVideoFraming.playerSize(contentAspect: hd, frame: hdFrame).height, 900, accuracy: 0.01)
        XCTAssertEqual(MusicVideoFraming.playerSize(contentAspect: .nan, frame: hdFrame), hdFrame)
    }

    func testTheVideosShapeComesFromOEmbed() async throws {
        XCTAssertEqual(YouTubeVideoShape.aspect(fromOEmbed: Data(#"{"width":200,"height":150,"title":"x"}"#.utf8)) ?? 0,
                       4.0 / 3.0, accuracy: 0.001)
        XCTAssertEqual(YouTubeVideoShape.aspect(fromOEmbed: Data(#"{"width":200,"height":113}"#.utf8)) ?? 0,
                       200.0 / 113.0, accuracy: 0.001)
        XCTAssertNil(YouTubeVideoShape.aspect(fromOEmbed: Data(#"{"width":0,"height":113}"#.utf8)))
        XCTAssertNil(YouTubeVideoShape.aspect(fromOEmbed: Data(#"{"width":2000,"height":10}"#.utf8)), "not believable")
        XCTAssertNil(YouTubeVideoShape.aspect(fromOEmbed: Data("nonsense".utf8)))
        let url = try XCTUnwrap(YouTubeVideoShape.url(for: "9fdQtU5CEAw"))
        XCTAssertEqual(url.host, "www.youtube.com")
        XCTAssertTrue(url.absoluteString.contains("oembed"))
        XCTAssertTrue(url.absoluteString.contains("9fdQtU5CEAw"), "only the video's ID is sent")
        XCTAssertNil(YouTubeVideoShape.url(for: "bad id'"))

        struct Fake: MusicVideoHTTP {
            func get(_ url: URL) async throws -> (Data, HTTPURLResponse) {
                (Data(#"{"width":200,"height":150}"#.utf8),
                 HTTPURLResponse(url: url, statusCode: 200, httpVersion: nil, headerFields: nil)!)
            }
        }
        let found = await YouTubeVideoShape.aspect(of: "AAAAAAAAAA1", http: Fake())
        XCTAssertEqual(found ?? 0, 4.0 / 3.0, accuracy: 0.001)
        XCTAssertEqual(YouTubeVideoShape.cached("AAAAAAAAAA1") ?? 0, 4.0 / 3.0, accuracy: 0.001, "remembered")
    }

    func testTheWholeLayoutNeverDrawsTheViewLargerThanTheScreen() throws {
        for screen in [laptop, wide, tall] {
            for aspect in [hd, 4.0 / 3.0, 2.39, 1.0] {
                let layout = MusicVideoFraming.wholeLayout(contentAspect: aspect, in: screen)
                XCTAssertLessThanOrEqual(layout.visible.width, screen.width + 0.001, "\(screen) \(aspect)")
                XCTAssertLessThanOrEqual(layout.visible.height, screen.height + 0.001, "\(screen) \(aspect)")
                XCTAssertEqual(layout.frame.width, screen.width * MusicVideoFraming.wholeBleed, accuracy: 0.01,
                               "the width is matched")
                let box = try XCTUnwrap(layout.box)
                XCTAssertGreaterThanOrEqual(box.width, 99.9, "the player covers the view across")
                XCTAssertGreaterThanOrEqual(box.height, 99.9, "and down")
            }
        }
        // A 16:9 video on a 16:10 laptop: the view is the picture, the player fills it.
        let hdOnLaptop = MusicVideoFraming.wholeLayout(contentAspect: hd, in: laptop)
        XCTAssertLessThan(hdOnLaptop.visible.height, laptop.height)
        XCTAssertEqual(try XCTUnwrap(hdOnLaptop.box).height, 100, accuracy: 0.1)
        // A 4:3 video: the player is wider than the view, so its own side bars fall outside.
        let square = MusicVideoFraming.wholeLayout(contentAspect: 4.0 / 3.0, in: laptop)
        XCTAssertEqual(square.visible.height, laptop.height, accuracy: 0.01, "top and bottom cut")
        XCTAssertGreaterThan(try XCTUnwrap(square.box).width, 130)
    }

    func testThePageDrawsThePlayerAtTheBox() throws {
        let box = try XCTUnwrap(YouTubeEmbed.Box(width: 133.4, height: 100))
        let html = YouTubeEmbed.html(videoID: "4NRXx6U8ABQ", start: 0, overscan: 1, box: box)
        XCTAssertTrue(html.contains("box = [133.400,100.000]"))
        XCTAssertTrue(html.contains("window.lunalyrBox = function (w, h)"))
        XCTAssertTrue(html.contains("frame.style.width = box ? box[0] + '%' : '';"))
        XCTAssertTrue(YouTubeEmbed.html(videoID: "4NRXx6U8ABQ", start: 0).contains("box = null"), "fill: no box")
        XCTAssertNil(YouTubeEmbed.Box(width: .nan, height: 100))
        XCTAssertNil(YouTubeEmbed.Box(width: 0, height: 100))
        XCTAssertEqual(YouTubeEmbed.Box(width: 9999, height: 100)?.width, 400, "capped")
    }

    func testBackdropIsSmallAndBlurred() throws {
        let side = 1024
        let context = try XCTUnwrap(CGContext(data: nil, width: side, height: side / 2, bitsPerComponent: 8,
                                              bytesPerRow: 0, space: CGColorSpaceCreateDeviceRGB(),
                                              bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue))
        context.setFillColor(NSColor.white.cgColor)
        context.fill(CGRect(x: 0, y: 0, width: side / 2, height: side / 2))
        context.setFillColor(NSColor.black.cgColor)
        context.fill(CGRect(x: side / 2, y: 0, width: side / 2, height: side / 2))
        let image = try XCTUnwrap(context.makeImage())
        let blurred = try XCTUnwrap(MusicVideoBackdrop.blurred(image))
        XCTAssertEqual(CGFloat(blurred.width), MusicVideoBackdrop.workingWidth, accuracy: 1)
        XCTAssertEqual(blurred.height, blurred.width / 2, accuracy: 1, "keeps its shape")
        // The hard edge in the middle is softened; the outer edges keep their colour.
        let grey = greys(blurred)
        let middle = grey[grey.count / 2]
        XCTAssertGreaterThan(middle, 40)
        XCTAssertLessThan(middle, 215)
        XCTAssertGreaterThan(grey.first!, 230, "no dark fringe at the edge")
        XCTAssertLessThan(grey.last!, 25)
    }

    /// One row's grey levels, across the middle of the image.
    private func greys(_ image: CGImage) -> [UInt8] {
        let width = image.width
        var row = [UInt8](repeating: 0, count: width)
        row.withUnsafeMutableBytes { buffer in
            guard let context = CGContext(data: buffer.baseAddress, width: width, height: 1, bitsPerComponent: 8,
                                          bytesPerRow: width, space: CGColorSpaceCreateDeviceGray(),
                                          bitmapInfo: CGImageAlphaInfo.none.rawValue) else { return }
            context.draw(image, in: CGRect(x: 0, y: -CGFloat(image.height) / 2, width: CGFloat(width),
                                           height: CGFloat(image.height)))
        }
        return row
    }
}
