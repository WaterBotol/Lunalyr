import CoreGraphics
import XCTest
@testable import VersoCustom

/// A window server for `DesktopCoverage.check`: windows where they are drawn,
/// front to back. A click reaches the first that holds the point, else nothing.
private final class FakeWindowServer {
    var drawn: [(number: CGWindowID, frame: CGRect)]
    let primaryHeight: CGFloat
    /// What was hit-tested, in Quartz coordinates.
    var asked: [CGPoint] = []

    init(_ drawn: [(number: CGWindowID, frame: CGRect)], primaryHeight: CGFloat = 982) {
        self.drawn = drawn
        self.primaryHeight = primaryHeight
    }

    func hit(_ appKit: CGPoint) -> Int {
        let point = DesktopCoverage.quartzPoint(appKit, primaryHeight: primaryHeight)
        asked.append(point)
        return Int(drawn.first { $0.frame.contains(point) }?.number ?? 0)
    }

    /// A check against this server, with the pointer at a Quartz point.
    func check(_ listing: DesktopCoverage.Listing, pointer: CGPoint? = nil,
               distrusted: [CGDirectDisplayID: [DesktopWindowRecord]]?, rotation: Int = 0) -> DesktopCoverage.Evaluation {
        let height = primaryHeight
        return DesktopCoverage.check(listing, distrusted: distrusted, primaryHeight: height, rotation: rotation,
                                     pointer: { pointer.map { DesktopCoverage.appKitPoint($0, primaryHeight: height) } },
                                     hitTest: hit)
    }
}

/// Coverage is decided from the window list alone; these lists are synthetic.
final class DesktopCoverageTests: XCTestCase {
    private let normal = Int(CGWindowLevelForKey(.normalWindow))
    private let desktopLevel = Int(CGWindowLevelForKey(.desktopIconWindow))
    // A 1512x982 laptop with a 33 pt menu bar, and a 2560x1440 display to its right.
    private let laptop = DesktopDisplayRecord(displayID: 1, frame: CGRect(x: 0, y: 0, width: 1512, height: 982),
        visibleFrame: CGRect(x: 0, y: 33, width: 1512, height: 949), wallpaperWindow: 10)
    private let external = DesktopDisplayRecord(displayID: 2, frame: CGRect(x: 1512, y: 0, width: 2560, height: 1440),
        visibleFrame: CGRect(x: 1512, y: 25, width: 2560, height: 1415), wallpaperWindow: 20)
    private let own: Set<CGWindowID> = [10, 20, 11, 21]

    private func window(_ number: CGWindowID, _ bounds: CGRect, layer: Int? = nil, alpha: Double = 1) -> DesktopWindowRecord {
        DesktopWindowRecord(number: number, layer: layer ?? normal, alpha: alpha, bounds: bounds)
    }
    /// Our own wallpaper windows, listed at their desktop level.
    private var ownSurfaces: [DesktopWindowRecord] {
        [window(10, laptop.frame, layer: desktopLevel - 1), window(20, external.frame, layer: desktopLevel - 1)]
    }

    func testParsesOnlyGeometryLayerAndAlpha() throws {
        let bounds = CGRect(x: 4, y: 30, width: 800, height: 600)
        let list: [[String: Any]] = [
            [kCGWindowNumber as String: NSNumber(value: 77), kCGWindowLayer as String: NSNumber(value: 0),
             kCGWindowAlpha as String: NSNumber(value: 0.5),
             kCGWindowBounds as String: bounds.dictionaryRepresentation as NSDictionary],
            // Alpha is optional: absent means opaque.
            [kCGWindowNumber as String: NSNumber(value: 78), kCGWindowLayer as String: NSNumber(value: 3),
             kCGWindowBounds as String: bounds.dictionaryRepresentation as NSDictionary],
            [:],
            [kCGWindowNumber as String: NSNumber(value: 79), kCGWindowLayer as String: NSNumber(value: 0)],
            [kCGWindowNumber as String: NSNumber(value: 0), kCGWindowLayer as String: NSNumber(value: 0),
             kCGWindowBounds as String: bounds.dictionaryRepresentation as NSDictionary],
        ]
        let records = DesktopCoverage.records(from: list)
        XCTAssertEqual(records.map(\.number), [77, 78])
        XCTAssertEqual(records[0].alpha, 0.5)
        XCTAssertEqual(records[1].alpha, 1)
        XCTAssertEqual(records[1].layer, 3)
        XCTAssertEqual(records[0].bounds, bounds)
    }

    func testMaximisedOpaqueWindowHidesOnlyItsDisplay() {
        let browser = window(500, laptop.visibleFrame)
        let hidden = DesktopCoverage.hiddenDisplays([laptop, external], windows: ownSurfaces + [browser], ownWindows: own)
        XCTAssertEqual(hidden, [1])
    }

    func testEmptyOrFailedWindowListHidesNothing() {
        XCTAssertEqual(DesktopCoverage.hiddenDisplays([laptop, external], windows: [], ownWindows: own), [])
    }

    func testVisibleDesktopIsNotHidden() {
        let half = window(500, CGRect(x: 0, y: 33, width: 756, height: 949))
        let small = window(501, CGRect(x: 1700, y: 200, width: 900, height: 700))
        XCTAssertEqual(DesktopCoverage.hiddenDisplays([laptop, external], windows: ownSurfaces + [half, small], ownWindows: own), [])
    }

    func testTranslucentFloatingAndDesktopLevelWindowsNeverCover() {
        let fading = window(500, laptop.frame, alpha: 0.5)
        let floating = window(501, laptop.frame, layer: Int(CGWindowLevelForKey(.floatingWindow)))
        let overlay = window(502, laptop.frame, layer: Int(CGWindowLevelForKey(.screenSaverWindow)))
        let finderIcons = window(503, laptop.frame, layer: desktopLevel)
        let widgets = window(504, laptop.frame, layer: desktopLevel - 2)
        let windows = ownSurfaces + [fading, floating, overlay, finderIcons, widgets]
        XCTAssertEqual(DesktopCoverage.hiddenDisplays([laptop], windows: windows, ownWindows: own), [])
    }

    func testTiledWindowsWithMarginsCountAsCovered() {
        let left = window(500, CGRect(x: 8, y: 41, width: 744, height: 933))
        let right = window(501, CGRect(x: 760, y: 41, width: 744, height: 933))
        XCTAssertEqual(DesktopCoverage.hiddenDisplays([laptop], windows: ownSurfaces + [left, right], ownWindows: own), [1])
    }

    func testAnUncoveredLyricStageKeepsRendering() {
        // Four windows framing a 300 pt hole where the lyrics sit.
        let frame = [
            window(500, CGRect(x: 0, y: 33, width: 1512, height: 300)),
            window(501, CGRect(x: 0, y: 633, width: 1512, height: 349)),
            window(502, CGRect(x: 0, y: 333, width: 606, height: 300)),
            window(503, CGRect(x: 906, y: 333, width: 606, height: 300)),
        ]
        XCTAssertEqual(DesktopCoverage.hiddenDisplays([laptop], windows: ownSurfaces + frame, ownWindows: own), [])
    }

    func testOwnWallpaperMissingFromScreenMeansAnotherSpaceOwnsTheDisplay() {
        // A full-screen Space on the laptop: our window there is not on screen,
        // while the external display still lists its own.
        let windows = [window(20, external.frame, layer: desktopLevel - 1)]
        XCTAssertEqual(DesktopCoverage.hiddenDisplays([laptop, external], windows: windows, ownWindows: own), [1])
    }

    func testUnlistedOwnSurfacesAreNotTakenAsHidden() {
        // If the list shows none of our windows, it says nothing about them.
        let unrelated = window(500, CGRect(x: 100, y: 100, width: 300, height: 200))
        XCTAssertEqual(DesktopCoverage.hiddenDisplays([laptop, external], windows: [unrelated], ownWindows: own), [])
    }

    func testListedControlsAloneDoNotMakeMissingWallpapersHidden() {
        // Controls panels listed, wallpapers not: nothing is inferred.
        let panels = [window(11, CGRect(x: 556, y: 326, width: 400, height: 330), layer: desktopLevel + 1),
                      window(21, CGRect(x: 2592, y: 555, width: 400, height: 330), layer: desktopLevel + 1)]
        XCTAssertEqual(DesktopCoverage.hiddenDisplays([laptop, external], windows: panels, ownWindows: own), [])
    }

    func testFullScreenAppWindowCoversWholeDisplay() {
        let fullScreen = window(500, external.frame)
        XCTAssertEqual(DesktopCoverage.hiddenDisplays([laptop, external], windows: ownSurfaces + [fullScreen], ownWindows: own), [2])
    }

    func testOwnWindowsNeverCoverTheirWallpaper() {
        // Our controls panel or wallpaper at the normal layer must not count.
        let ownPanel = window(11, laptop.frame)
        XCTAssertEqual(DesktopCoverage.hiddenDisplays([laptop], windows: ownSurfaces + [ownPanel], ownWindows: own), [])
    }

    func testManyWindowsAreBoundedAndOnlyUnderReport() {
        var windows = ownSurfaces
        // 200 tiny windows, then one that covers everything: the large one
        // must still be considered.
        for index in 0..<200 {
            windows.append(window(CGWindowID(1000 + index), CGRect(x: index, y: 40, width: 2, height: 2)))
        }
        windows.append(window(5000, laptop.visibleFrame))
        XCTAssertEqual(DesktopCoverage.hiddenDisplays([laptop], windows: windows, ownWindows: own), [1])
    }

    // MARK: Live shapes

    /// This Mac's own layout (read-only probe of the window list): a 1728x1117
    /// laptop on the left and a 2560x1440 display above and to its right,
    /// holding the Dock, with four maximised windows. AppKit's usable area for
    /// the external display can still include its 30 pt menu bar, which
    /// maximised windows never cover.
    private let liveLaptop = DesktopDisplayRecord(displayID: 1, frame: CGRect(x: 0, y: 0, width: 1728, height: 1117),
        visibleFrame: CGRect(x: 0, y: 33, width: 1728, height: 1084), wallpaperWindow: 34676)
    private let liveExternal = DesktopDisplayRecord(displayID: 4, frame: CGRect(x: 1728, y: -771, width: 2560, height: 1440),
        visibleFrame: CGRect(x: 1728, y: -771, width: 2560, height: 1332), wallpaperWindow: 34677)
    private var liveWindows: [DesktopWindowRecord] {
        let menuBar = Int(CGWindowLevelForKey(.mainMenuWindow)), dock = Int(CGWindowLevelForKey(.dockWindow))
        return [
            window(23439, CGRect(x: 0, y: 0, width: 1728, height: 33), layer: menuBar),
            window(34557, CGRect(x: 1728, y: -771, width: 2560, height: 30), layer: menuBar),
            // The Dock's window spans its whole display and must never count.
            window(35, CGRect(x: 1728, y: -771, width: 2560, height: 1440), layer: dock),
            window(30912, CGRect(x: 1728, y: -741, width: 2560, height: 1302)),
            window(96, CGRect(x: 1728, y: -741, width: 2560, height: 1301)),
            window(100, CGRect(x: 1728, y: -741, width: 2560, height: 1301)),
            window(31516, CGRect(x: 1728, y: -741, width: 2560, height: 1301)),
            window(34453, CGRect(x: 2408, y: -590, width: 1728, height: 1084)),
            // Finder's icon windows and our wallpapers, at desktop level.
            window(35284, liveLaptop.frame, layer: desktopLevel),
            window(35068, liveExternal.frame, layer: desktopLevel),
            window(34676, liveLaptop.frame, layer: desktopLevel - 1),
            window(34677, liveExternal.frame, layer: desktopLevel - 1),
        ]
    }
    private let liveOwn: Set<CGWindowID> = [34676, 34677, 34953, 34954]

    func testSecondaryDisplayMenuBarIsNotLeftShowingDesktop() {
        // The external display's maximised windows stop at its menu bar; the
        // laptop beside it is uncovered.
        XCTAssertEqual(DesktopCoverage.hiddenDisplays([liveLaptop, liveExternal], windows: liveWindows, ownWindows: liveOwn), [4])
        let region = DesktopCoverage.usableRegion(liveExternal, windows: liveWindows,
                                                  menuBarLayer: Int(CGWindowLevelForKey(.mainMenuWindow)))
        XCTAssertEqual(region, CGRect(x: 1728, y: -741, width: 2560, height: 1302))
        // Where AppKit already leaves the menu bar out, nothing more is trimmed.
        XCTAssertEqual(DesktopCoverage.usableRegion(liveLaptop, windows: liveWindows,
                                                    menuBarLayer: Int(CGWindowLevelForKey(.mainMenuWindow))),
                       liveLaptop.visibleFrame)
        // Without the menu bar in the list, the strip above the windows would
        // keep the wallpaper rendering.
        let withoutMenuBars = liveWindows.filter { $0.layer != Int(CGWindowLevelForKey(.mainMenuWindow)) }
        XCTAssertEqual(DesktopCoverage.hiddenDisplays([liveLaptop, liveExternal], windows: withoutMenuBars, ownWindows: liveOwn), [])
    }

    func testOnlyAFullWidthStripAtTheTopIsTakenForAMenuBar() {
        let menuBar = Int(CGWindowLevelForKey(.mainMenuWindow))
        let display = DesktopDisplayRecord(displayID: 7, frame: CGRect(x: 0, y: 0, width: 1600, height: 1000),
                                           visibleFrame: CGRect(x: 0, y: 0, width: 1600, height: 1000), wallpaperWindow: nil)
        for impostor in [
            window(1, CGRect(x: 0, y: 0, width: 400, height: 30), layer: menuBar),        // a menu bar extra
            window(2, CGRect(x: 0, y: 0, width: 1600, height: 400), layer: menuBar),      // too tall
            window(3, CGRect(x: 0, y: 200, width: 1600, height: 30), layer: menuBar),     // not at the top
            window(4, CGRect(x: 0, y: -30, width: 1600, height: 30), layer: menuBar),     // auto-hidden above
            window(5, CGRect(x: 0, y: 0, width: 1600, height: 30), layer: normal),        // an app window
        ] {
            XCTAssertEqual(DesktopCoverage.usableRegion(display, windows: [impostor], menuBarLayer: menuBar), display.frame,
                           "window \(impostor.number)")
        }
    }

    // MARK: Erring towards rendering

    func testSeeThroughTerminalsNeverCover() {
        let terminal = DesktopWindowRecord(number: 500, layer: normal, alpha: 1, bounds: laptop.visibleFrame, ownerPID: 4242)
        let browser = DesktopWindowRecord(number: 501, layer: normal, alpha: 1, bounds: laptop.visibleFrame, ownerPID: 777)
        let owners = DesktopCoverage.seeThroughOwners(among: [("com.googlecode.iterm2", 4242), ("com.apple.Safari", 777), (nil, 9)])
        XCTAssertEqual(owners, [4242])
        let behindTerminal = DesktopCoverage.evaluate([laptop], windows: ownSurfaces + [terminal], ownWindows: own,
                                                      seeThroughOwners: owners)
        XCTAssertEqual(behindTerminal.hidden, [])
        let behindBrowser = DesktopCoverage.evaluate([laptop], windows: ownSurfaces + [browser], ownWindows: own,
                                                     seeThroughOwners: owners)
        XCTAssertEqual(behindBrowser.hidden, [1])
        XCTAssertTrue(DesktopCoverage.seeThroughApps.contains("com.apple.Terminal"))
    }

    func testStageManagerTurnsTheWindowRuleOffButNotFullScreenSpaces() {
        let browser = window(500, laptop.visibleFrame)
        XCTAssertEqual(DesktopCoverage.evaluate([laptop, external], windows: ownSurfaces + [browser], ownWindows: own,
                                                appWindowsCover: false).hidden, [])
        let fullScreenOnLaptop = [window(20, external.frame, layer: desktopLevel - 1)]
        XCTAssertEqual(DesktopCoverage.evaluate([laptop, external], windows: fullScreenOnLaptop, ownWindows: own,
                                                appWindowsCover: false).hidden, [1])
    }

    func testDesktopFoundUnderTheWindowsKeepsADisplayVisibleUntilTheyChange() {
        // Show Desktop slides windows aside without changing their frames:
        // the list still says "covered", but the pointer is over Finder's icons.
        let finder = window(503, laptop.frame, layer: desktopLevel)
        let browser = window(500, laptop.visibleFrame)
        let windows = ownSurfaces + [finder, browser]
        let onDesktop = DesktopHitSample(location: CGPoint(x: 700, y: 500), windowNumber: 503)
        let seen = DesktopCoverage.evaluate([laptop, external], windows: windows, ownWindows: own, pointer: onDesktop)
        XCTAssertEqual(seen.hidden, [])
        XCTAssertNotNil(seen.distrustedCovers)

        // The pointer moves onto a window's sliver at the edge, while the
        // rest of the display still finds the desktop: it stays visible.
        let onSliver = DesktopHitSample(location: CGPoint(x: 20, y: 500), windowNumber: 500)
        let later = DesktopCoverage.evaluate([laptop, external], windows: windows, ownWindows: own,
                                             samples: hits(on: laptop, 503), pointer: onSliver,
                                             distrusted: seen.distrustedCovers)
        XCTAssertEqual(later.hidden, [])
        XCTAssertEqual(later.distrustedCovers, seen.distrustedCovers)

        // No hit test can tell either way and nothing has changed: still visible.
        let unsure = DesktopCoverage.evaluate([laptop, external], windows: windows, ownWindows: own,
                                              samples: hits(on: laptop, 0), distrusted: later.distrustedCovers)
        XCTAssertEqual(unsure.hidden, [])
        XCTAssertEqual(unsure.distrustedCovers, seen.distrustedCovers)

        // Once the windows change, the list is believed again.
        let moved = window(500, CGRect(x: 0, y: 33, width: 1512, height: 948))
        let changed = DesktopCoverage.evaluate([laptop, external], windows: ownSurfaces + [finder, moved], ownWindows: own,
                                               samples: hits(on: laptop, 0), distrusted: unsure.distrustedCovers)
        XCTAssertEqual(changed.hidden, [1])
        XCTAssertNil(changed.distrustedCovers)
    }

    func testOnlyTheDesktopFoundUnderAnOpaqueWindowDiscreditsTheList() {
        let finder = window(503, laptop.frame, layer: desktopLevel)
        let browser = window(500, laptop.visibleFrame)
        let windows = ownSurfaces + finderIcons + [browser]
        for sample in [
            DesktopHitSample(location: CGPoint(x: 700, y: 500), windowNumber: 0),     // AppKit could not say
            DesktopHitSample(location: CGPoint(x: 700, y: 500), windowNumber: 500),   // over the window
            DesktopHitSample(location: CGPoint(x: 700, y: 500), windowNumber: 999),   // not in the list
            DesktopHitSample(location: CGPoint(x: 700, y: 10), windowNumber: 503),    // menu bar strip
            DesktopHitSample(location: CGPoint(x: 2000, y: 500), windowNumber: 504),  // uncovered display
        ] {
            let evaluation = DesktopCoverage.evaluate([laptop, external], windows: windows, ownWindows: own,
                                                      samples: [sample])
            XCTAssertEqual(evaluation.hidden, [1], "\(sample)")
            XCTAssertNil(evaluation.distrustedCovers, "\(sample)")
        }
        // Our own controls panel sits on the desktop, so it discredits the list too.
        let overControls = DesktopHitSample(location: CGPoint(x: 700, y: 500), windowNumber: 11)
        XCTAssertEqual(DesktopCoverage.evaluate([laptop], windows: windows, ownWindows: own, samples: [overControls]).hidden, [])

        // The desktop in a tiling gap no window claims is just the gap.
        let left = window(500, CGRect(x: 8, y: 41, width: 744, height: 933))
        let right = window(501, CGRect(x: 760, y: 41, width: 744, height: 933))
        let inGap = DesktopHitSample(location: CGPoint(x: 756, y: 491), windowNumber: 503)
        let tiled = DesktopCoverage.evaluate([laptop], windows: ownSurfaces + [finder, left, right], ownWindows: own,
                                             samples: [inGap])
        XCTAssertEqual(tiled.hidden, [1])
        XCTAssertNil(tiled.distrustedCovers)
    }

    // MARK: Show Desktop on several displays

    /// Finder's icon windows and a maximised window on each display.
    private var finderIcons: [DesktopWindowRecord] {
        [window(503, laptop.frame, layer: desktopLevel), window(504, external.frame, layer: desktopLevel)]
    }
    private var maximised: [DesktopWindowRecord] {
        [window(500, laptop.visibleFrame), window(501, external.visibleFrame)]
    }

    /// What a check finds at every one of a display's sample points.
    private func hits(on display: DesktopDisplayRecord, _ number: CGWindowID) -> [DesktopHitSample] {
        DesktopCoverage.samplePoints(for: display).map { DesktopHitSample(location: $0, windowNumber: Int(number)) }
    }

    func testShowDesktopResumesEveryDisplayNotOnlyThePointers() {
        let windows = ownSurfaces + finderIcons + maximised
        // Before: the windows are found where the list puts them.
        let pointerOnBrowser = DesktopHitSample(location: CGPoint(x: 700, y: 500), windowNumber: 500)
        let before = DesktopCoverage.evaluate([laptop, external], windows: windows, ownWindows: own,
                                              samples: hits(on: laptop, 500) + hits(on: external, 501),
                                              pointer: pointerOnBrowser)
        XCTAssertEqual(before.hidden, [1, 2])
        XCTAssertNil(before.distrustedCovers)

        // Show Desktop: every frame is unchanged, the pointer rests on the
        // laptop's desktop, and the external display -- which the pointer
        // never visits -- finds its desktop too. Both resume.
        let pointerOnDesktop = DesktopHitSample(location: CGPoint(x: 700, y: 500), windowNumber: 503)
        let shown = DesktopCoverage.evaluate([laptop, external], windows: windows, ownWindows: own,
                                             samples: hits(on: laptop, 503) + hits(on: external, 504),
                                             pointer: pointerOnDesktop, distrusted: before.distrustedCovers)
        XCTAssertEqual(shown.hidden, [])
        XCTAssertEqual(shown.distrustedCovers.map { Set($0.keys) }, [1, 2])

        // The external display alone, with the pointer elsewhere.
        let externalOnly = DesktopCoverage.evaluate([laptop, external], windows: windows, ownWindows: own,
                                                    samples: hits(on: external, 504))
        XCTAssertEqual(externalOnly.hidden, [])
    }

    func testOneDisplaysContradictionDiscreditsTheListOnEveryDisplay() {
        let windows = ownSurfaces + finderIcons + maximised
        // Show Desktop is global: the laptop finds its desktop, and the
        // external display, whose hit tests cannot tell or still find one of
        // its windows (one that stays put), is not trusted either.
        for externalHits in [hits(on: external, 0), hits(on: external, 501), []] {
            let evaluation = DesktopCoverage.evaluate([laptop, external], windows: windows, ownWindows: own,
                                                      samples: hits(on: laptop, 503) + externalHits)
            XCTAssertEqual(evaluation.hidden, [], "\(externalHits)")
            XCTAssertNotNil(evaluation.distrustedCovers)
        }
    }

    func testAContradictionWithNothingToPauseIsNotKept() {
        // A click-through window over half the laptop: the desktop is found
        // under it, but no display was going to pause, so nothing is kept
        // (and the poll does not speed up for it).
        let overlay = window(500, CGRect(x: 0, y: 33, width: 756, height: 949))
        let evaluation = DesktopCoverage.evaluate([laptop, external], windows: ownSurfaces + finderIcons + [overlay],
                                                  ownWindows: own, samples: hits(on: laptop, 503))
        XCTAssertEqual(evaluation.hidden, [])
        XCTAssertNil(evaluation.distrustedCovers)
    }

    func testShowDesktopEndingByKeyboardPausesCoveredDisplaysAtTheNextCheck() {
        let windows = ownSurfaces + finderIcons + maximised
        let shown = DesktopCoverage.evaluate([laptop, external], windows: windows, ownWindows: own,
                                             samples: hits(on: laptop, 503) + hits(on: external, 504))
        XCTAssertEqual(shown.hidden, [])
        // The windows slide back. Not one frame changed, no app activated and
        // the Space is the same, but the hit tests find the windows again.
        let ended = DesktopCoverage.evaluate([laptop, external], windows: windows, ownWindows: own,
                                             samples: hits(on: laptop, 500) + hits(on: external, 501),
                                             distrusted: shown.distrustedCovers)
        XCTAssertEqual(ended.hidden, [1, 2])
        XCTAssertNil(ended.distrustedCovers)
        // One window found back in place is enough, wherever the pointer is.
        let oneBack = DesktopCoverage.evaluate([laptop, external], windows: windows, ownWindows: own,
                                               samples: hits(on: laptop, 0) + Array(hits(on: external, 501).prefix(1)),
                                               distrusted: shown.distrustedCovers)
        XCTAssertEqual(oneBack.hidden, [1, 2])
        XCTAssertNil(oneBack.distrustedCovers)
        // And on again.
        let again = DesktopCoverage.evaluate([laptop, external], windows: windows, ownWindows: own,
                                             samples: hits(on: laptop, 0) + hits(on: external, 504),
                                             distrusted: ended.distrustedCovers)
        XCTAssertEqual(again.hidden, [])
    }

    func testOnlyAWindowFoundWhereTheListPutsItShowsTheWindowsAreBack() {
        // A small window listed in the middle of the laptop, over a maximised
        // browser. Show Desktop slides it aside, onto a quarter's sample.
        let small = window(505, CGRect(x: 600, y: 400, width: 300, height: 200))
        let windows = ownSurfaces + finderIcons + [small, window(500, laptop.visibleFrame)]
        let shown = DesktopCoverage.evaluate([laptop], windows: windows, ownWindows: own, samples: hits(on: laptop, 503))
        XCTAssertEqual(shown.hidden, [])
        let slidAside = DesktopHitSample(location: CGPoint(x: 378, y: 270.25), windowNumber: 505)
        let aside = DesktopCoverage.evaluate([laptop], windows: windows, ownWindows: own, samples: [slidAside],
                                             distrusted: shown.distrustedCovers)
        XCTAssertEqual(aside.hidden, [])
        XCTAssertEqual(aside.distrustedCovers, shown.distrustedCovers)
        // Found back in its frame, at the stage's centre, it does.
        let inPlace = DesktopHitSample(location: CGPoint(x: 756, y: 491), windowNumber: 505)
        let back = DesktopCoverage.evaluate([laptop], windows: windows, ownWindows: own, samples: [inPlace],
                                            distrusted: aside.distrustedCovers)
        XCTAssertEqual(back.hidden, [1])
    }

    func testFullScreenSpaceStaysPausedThroughShowDesktopOnTheOtherDisplay() {
        // A full-screen app owns the laptop: our wallpaper there is not on
        // screen, and the app's window is not moved by Show Desktop.
        let fullScreenApp = window(600, laptop.frame)
        let windows = [window(20, external.frame, layer: desktopLevel - 1), window(504, external.frame, layer: desktopLevel),
                       fullScreenApp, window(501, external.visibleFrame)]
        let covered = DesktopCoverage.evaluate([laptop, external], windows: windows, ownWindows: own,
                                               samples: hits(on: laptop, 600) + hits(on: external, 501))
        XCTAssertEqual(covered.hidden, [1, 2])

        let shown = DesktopCoverage.evaluate([laptop, external], windows: windows, ownWindows: own,
                                             samples: hits(on: laptop, 600) + hits(on: external, 504))
        XCTAssertEqual(shown.hidden, [1])
        XCTAssertEqual(shown.distrustedCovers.map { Set($0.keys) }, [2])

        // The full-screen app's window, found where it always is, says
        // nothing about Show Desktop on the external display.
        let unsure = DesktopCoverage.evaluate([laptop, external], windows: windows, ownWindows: own,
                                              samples: hits(on: laptop, 600) + hits(on: external, 0),
                                              distrusted: shown.distrustedCovers)
        XCTAssertEqual(unsure.hidden, [1])
        XCTAssertNotNil(unsure.distrustedCovers)

        let ended = DesktopCoverage.evaluate([laptop, external], windows: windows, ownWindows: own,
                                             samples: hits(on: laptop, 600) + hits(on: external, 501),
                                             distrusted: unsure.distrustedCovers)
        XCTAssertEqual(ended.hidden, [1, 2])
    }

    func testShowDesktopOnThisMacsLayoutResumesTheExternalDisplay() {
        // The pointer rests on the uncovered laptop; the external display's
        // four maximised windows have slid aside, frames unchanged.
        let pointer = DesktopHitSample(location: CGPoint(x: 400, y: 600), windowNumber: 35284)
        let covered = DesktopCoverage.evaluate([liveLaptop, liveExternal], windows: liveWindows, ownWindows: liveOwn,
                                               samples: hits(on: liveLaptop, 35284) + hits(on: liveExternal, 30912),
                                               pointer: pointer)
        XCTAssertEqual(covered.hidden, [4])
        let shown = DesktopCoverage.evaluate([liveLaptop, liveExternal], windows: liveWindows, ownWindows: liveOwn,
                                             samples: hits(on: liveLaptop, 35284) + hits(on: liveExternal, 35068),
                                             pointer: pointer, distrusted: covered.distrustedCovers)
        XCTAssertEqual(shown.hidden, [])
        let ended = DesktopCoverage.evaluate([liveLaptop, liveExternal], windows: liveWindows, ownWindows: liveOwn,
                                             samples: hits(on: liveLaptop, 35284) + hits(on: liveExternal, 30912),
                                             pointer: pointer, distrusted: shown.distrustedCovers)
        XCTAssertEqual(ended.hidden, [4])
    }

    func testStageManagerAsksNothingOfTheHitTests() {
        let windows = ownSurfaces + finderIcons + maximised
        let evaluation = DesktopCoverage.evaluate([laptop, external], windows: windows, ownWindows: own,
                                                  samples: hits(on: laptop, 503), appWindowsCover: false)
        XCTAssertEqual(evaluation.hidden, [])
        XCTAssertNil(evaluation.distrustedCovers)
    }

    func testSamplesSpreadOverEachDisplayAwayFromItsEdges() {
        // The lyric stage's centre, then the middle of each quarter of the
        // usable area.
        XCTAssertEqual(DesktopCoverage.samplePoints(for: laptop), [
            CGPoint(x: 756, y: 491),
            CGPoint(x: 378, y: 270.25), CGPoint(x: 1134, y: 270.25),
            CGPoint(x: 378, y: 744.75), CGPoint(x: 1134, y: 744.75),
        ])
        XCTAssertEqual(DesktopCoverage.samplePoints(for: external), [
            CGPoint(x: 2792, y: 720),
            CGPoint(x: 2152, y: 378.75), CGPoint(x: 3432, y: 378.75),
            CGPoint(x: 2152, y: 1086.25), CGPoint(x: 3432, y: 1086.25),
        ])
        // Each lies in its own display's usable region, under that display's
        // maximised window, and well clear of the edges Show Desktop piles
        // windows against.
        let menuBar = Int(CGWindowLevelForKey(.mainMenuWindow))
        for display in [laptop, external, liveLaptop, liveExternal] {
            let region = DesktopCoverage.usableRegion(display, windows: liveWindows, menuBarLayer: menuBar)
            let points = DesktopCoverage.samplePoints(for: display)
            XCTAssertEqual(points.count, 5)
            for point in points {
                XCTAssertTrue(region.contains(point), "\(point) on display \(display.displayID)")
                XCTAssertGreaterThanOrEqual(min(point.x - display.frame.minX, display.frame.maxX - point.x),
                                            display.frame.width / 5)
                XCTAssertGreaterThanOrEqual(min(point.y - display.frame.minY, display.frame.maxY - point.y),
                                            display.frame.height / 5)
            }
        }
        // A display AppKit reports no usable area for is sampled over its frame.
        let bare = DesktopDisplayRecord(displayID: 9, frame: CGRect(x: 0, y: 0, width: 800, height: 600),
                                        visibleFrame: .zero, wallpaperWindow: nil)
        XCTAssertEqual(DesktopCoverage.samplePoints(for: bare).first, CGPoint(x: 400, y: 300))
        XCTAssertEqual(DesktopCoverage.samplePoints(for: bare).count, 5)
        let empty = DesktopDisplayRecord(displayID: 9, frame: .zero, visibleFrame: .zero, wallpaperWindow: nil)
        XCTAssertEqual(DesktopCoverage.samplePoints(for: empty), [])
    }

    // MARK: Telling Show Desktop from everything else

    func testHitTestsNearAWindowsEdgeSayNothing() {
        let windows = ownSurfaces + finderIcons + maximised
        let inPlace = hits(on: laptop, 500) + hits(on: external, 501)
        // The pointer parked in a maximised window's rounded corner reaches
        // Finder's icons through it. Neither display has moved anything.
        let corner = DesktopHitSample(location: CGPoint(x: 1, y: 980), windowNumber: 503)
        for parked in [
            DesktopCoverage.evaluate([laptop, external], windows: windows, ownWindows: own, samples: inPlace, pointer: corner),
            DesktopCoverage.evaluate([laptop, external], windows: windows, ownWindows: own, samples: [corner] + inPlace),
        ] {
            XCTAssertEqual(parked.hidden, [1, 2])
            XCTAssertNil(parked.distrustedCovers)
        }
        // Only the desktop found at least noticeableGap inside a window counts.
        let nearTheEdge = DesktopHitSample(location: CGPoint(x: 23, y: 500), windowNumber: 503)
        let wellInside = DesktopHitSample(location: CGPoint(x: 25, y: 500), windowNumber: 503)
        XCTAssertEqual(DesktopCoverage.evaluate([laptop, external], windows: windows, ownWindows: own,
                                                samples: [nearTheEdge]).hidden, [1, 2])
        XCTAssertEqual(DesktopCoverage.evaluate([laptop, external], windows: windows, ownWindows: own,
                                                samples: [wellInside]).hidden, [])

        // Two tiled laptop windows with a gap between them, each inside a
        // click-through border window 6 pt larger, as border utilities draw
        // them (listed at the normal layer, fully opaque). The stage's centre
        // lies in the gap, inside both borders, and reaches the desktop.
        let left = window(500, CGRect(x: 8, y: 41, width: 744, height: 933))
        let right = window(502, CGRect(x: 760, y: 41, width: 744, height: 933))
        let borders = [window(900, left.bounds.insetBy(dx: -6, dy: -6)), window(901, right.bounds.insetBy(dx: -6, dy: -6))]
        let tiled = ownSurfaces + finderIcons + [left, right, maximised[1]] + borders
        let laptopHits = DesktopCoverage.samplePoints(for: laptop).map { point in
            DesktopHitSample(location: point,
                             windowNumber: left.bounds.contains(point) ? 500 : right.bounds.contains(point) ? 502 : 503)
        }
        XCTAssertEqual(laptopHits.first?.windowNumber, 503)
        let bordered = DesktopCoverage.evaluate([laptop, external], windows: tiled, ownWindows: own,
                                                samples: laptopHits + hits(on: external, 501))
        XCTAssertEqual(bordered.hidden, [1, 2])
        XCTAssertNil(bordered.distrustedCovers)
        // Nor is the point in the gap ever asked about: it moves into the
        // border beside it, and so well inside the left window too.
        XCTAssertEqual(DesktopCoverage.listing([laptop], windows: tiled, ownWindows: own).covered.first?.probes,
                       [CGPoint(x: 710, y: 491)] + DesktopCoverage.samplePoints(for: laptop).dropFirst())
        XCTAssertTrue(DesktopCoverage.deepInside(left.bounds, CGPoint(x: 710, y: 491)))
    }

    // MARK: Tiled displays

    /// A 3440x1440 display with a 25 pt menu bar.
    private let ultrawide = DesktopDisplayRecord(displayID: 3, frame: CGRect(x: 0, y: 0, width: 3440, height: 1440),
        visibleFrame: CGRect(x: 0, y: 25, width: 3440, height: 1415), wallpaperWindow: 30)

    /// Four windows tiled side by side across its usable area, `gap` apart
    /// and from its edges, as tiling tools lay out "fourths".
    private func fourths(gap: CGFloat) -> [DesktopWindowRecord] {
        let area = ultrawide.visibleFrame, width = (area.width - gap * 5) / 4
        return (0..<4).map { column in
            window(CGWindowID(600 + column), CGRect(x: area.minX + gap + CGFloat(column) * (width + gap),
                                                    y: area.minY + gap, width: width, height: area.height - 2 * gap))
        }
    }

    /// A display's own wallpaper window, listed at its desktop level.
    private func surface(of display: DesktopDisplayRecord) -> DesktopWindowRecord {
        window(display.wallpaperWindow ?? 0, display.frame, layer: desktopLevel - 1)
    }

    func testTiledWindowsAreProbedInsideThemNotWhereTheyMeet() {
        // Four columns meet right under the stage's centre and the quarters'
        // middles, so each of those points moves into the column beside it.
        let own = own.union([30, 50])
        let gapless = DesktopCoverage.listing([ultrawide], windows: fourths(gap: 0) + [surface(of: ultrawide)],
                                              ownWindows: own)
        XCTAssertEqual(gapless.covered.first?.probes, [
            CGPoint(x: 1672, y: 720),
            CGPoint(x: 812, y: 378.75), CGPoint(x: 2532, y: 378.75),
            CGPoint(x: 812, y: 1086.25), CGPoint(x: 2532, y: 1086.25),
        ])
        // Whatever the tiling, every probe lies well inside a window, in its
        // display's usable region and a fifth of the display in from its edges.
        let portrait = DesktopDisplayRecord(displayID: 5, frame: CGRect(x: 0, y: 0, width: 1080, height: 1920),
                                            visibleFrame: CGRect(x: 0, y: 25, width: 1080, height: 1895), wallpaperWindow: 50)
        let rows = (0..<4).map { row in
            window(CGWindowID(700 + row), CGRect(x: 0, y: 25 + CGFloat(row) * 473.75, width: 1080, height: 473.75))
        }
        let laptopColumns = (0..<4).map { column in
            window(CGWindowID(710 + column), CGRect(x: CGFloat(column) * 378, y: 33, width: 378, height: 949))
        }
        let sidebar = [window(720, CGRect(x: 0, y: 33, width: 378, height: 949)),
                       window(721, CGRect(x: 378, y: 33, width: 1134, height: 949))]
        for (display, windows) in [(ultrawide, fourths(gap: 0)), (ultrawide, fourths(gap: 8)), (portrait, rows),
                                   (laptop, laptopColumns), (laptop, sidebar)] {
            let listing = DesktopCoverage.listing([display], windows: windows + [surface(of: display)], ownWindows: own)
            guard let covered = listing.covered.first else { return XCTFail("display \(display.displayID) not covered") }
            XCTAssertEqual(covered.probes.count, 5, "\(windows.map(\.number))")
            for probe in covered.probes {
                XCTAssertTrue(windows.contains { DesktopCoverage.deepInside($0.bounds, probe) }, "\(probe)")
                XCTAssertTrue(covered.region.contains(probe), "\(probe)")
                XCTAssertTrue(DesktopCoverage.middle(of: display.frame).contains(probe), "\(probe)")
            }
        }
    }

    func testShowDesktopOverTiledColumnsResumesAndEndsWithThePointerAnywhere() {
        for gap in [CGFloat(0), 8] {
            let columns = fourths(gap: gap)
            let desktop = [window(530, ultrawide.frame, layer: desktopLevel), surface(of: ultrawide)]
            let listing = DesktopCoverage.listing([ultrawide], windows: columns + desktop, ownWindows: own.union([30]))
            let inPlace = columns.map { (number: $0.number, frame: $0.bounds) } + desktop.map { ($0.number, $0.bounds) }
            let server = FakeWindowServer(inPlace, primaryHeight: 1440)
            // The pointer rests in the hot corner, or on the menu bar or Dock:
            // never well inside a window, so only the probes can tell.
            for pointer in [CGPoint(x: 3438, y: 1438), CGPoint(x: 1700, y: 10)] {
                server.drawn = inPlace
                let paused = server.check(listing, pointer: pointer, distrusted: nil)
                XCTAssertEqual(paused.hidden, [3], "gap \(gap)")
                // Show Desktop slides each column off its nearer side edge.
                server.drawn = columns.map { column in
                    (column.number, CGRect(x: column.bounds.midX < 1720 ? 0 : 3420, y: column.bounds.minY,
                                           width: 20, height: column.bounds.height))
                } + desktop.map { ($0.number, $0.bounds) }
                var evaluation = server.check(listing, pointer: pointer, distrusted: paused.distrustedCovers)
                for _ in 0..<3 {
                    XCTAssertEqual(evaluation.hidden, [], "gap \(gap), pointer \(pointer)")
                    XCTAssertEqual(evaluation.distrustedCovers?[3]?.count, 4)
                    evaluation = server.check(listing, pointer: pointer, distrusted: evaluation.distrustedCovers)
                }
                // It ends by keyboard: no frame changed, nothing activated.
                server.drawn = inPlace
                let ended = server.check(listing, pointer: pointer, distrusted: evaluation.distrustedCovers)
                XCTAssertEqual(ended.hidden, [3], "gap \(gap), pointer \(pointer)")
                XCTAssertNil(ended.distrustedCovers)
            }
        }
    }

    func testADisplayNoHitTestCanCheckKeepsRendering() {
        // Twenty columns, too narrow to hold a point well inside any, meet
        // under every sample point: nothing could notice Show Desktop there.
        let slats = (0..<20).map { column in
            window(CGWindowID(800 + column), CGRect(x: CGFloat(column) * 75.6, y: 33, width: 75.6, height: 949))
        }
        let listing = DesktopCoverage.listing([laptop], windows: ownSurfaces + slats, ownWindows: own)
        XCTAssertEqual(listing.covered, [])
        XCTAssertEqual(DesktopCoverage.hiddenDisplays([laptop], windows: ownSurfaces + slats, ownWindows: own), [])
    }

    func testThePointerKeepsOnlyItsOwnDisplayRendering() {
        let windows = ownSurfaces + finderIcons + maximised
        let inPlace = hits(on: laptop, 500) + hits(on: external, 501)
        // The pointer finds the desktop well inside the laptop's browser (a
        // hole in it, or a click-through overlay), while every fixed point
        // finds the windows where the list puts them.
        let throughAHole = DesktopHitSample(location: CGPoint(x: 700, y: 500), windowNumber: 503)
        let resting = DesktopCoverage.evaluate([laptop, external], windows: windows, ownWindows: own,
                                               samples: inPlace, pointer: throughAHole)
        XCTAssertEqual(resting.hidden, [2])
        XCTAssertEqual(resting.distrustedCovers.map { Set($0.keys) }, [1])
        // Once it moves on, the laptop's own points find its window in place.
        let onTheBrowser = DesktopHitSample(location: CGPoint(x: 700, y: 500), windowNumber: 500)
        let movedOn = DesktopCoverage.evaluate([laptop, external], windows: windows, ownWindows: own, samples: inPlace,
                                               pointer: onTheBrowser, distrusted: resting.distrustedCovers)
        XCTAssertEqual(movedOn.hidden, [1, 2])
        XCTAssertNil(movedOn.distrustedCovers)

        // Nor can the pointer end Show Desktop: it may rest on a window's
        // sliver, well inside the window's listed frame.
        let shown = DesktopCoverage.evaluate([laptop, external], windows: windows, ownWindows: own,
                                             samples: hits(on: laptop, 503) + hits(on: external, 504))
        let onASliver = DesktopHitSample(location: CGPoint(x: 60, y: 500), windowNumber: 500)
        let unsure = DesktopCoverage.evaluate([laptop, external], windows: windows, ownWindows: own,
                                              samples: hits(on: laptop, 0) + hits(on: external, 0), pointer: onASliver,
                                              distrusted: shown.distrustedCovers)
        XCTAssertEqual(unsure.hidden, [])
        XCTAssertEqual(unsure.distrustedCovers, shown.distrustedCovers)
    }

    func testAWindowOpenedDuringShowDesktopCannotEndIt() {
        // One display: Show Desktop has moved its maximised browser aside.
        let base = [ownSurfaces[0], finderIcons[0], maximised[0]]
        let shown = DesktopCoverage.evaluate([laptop], windows: base, ownWindows: own, samples: hits(on: laptop, 503))
        XCTAssertEqual(shown.hidden, [])
        XCTAssertEqual(shown.distrustedCovers?[1]?.map(\.number), [500])
        // A file opened from the desktop brings up a 1200x800 window over
        // every fixed point, leaving a third of the desktop around it in
        // view. Wherever the pointer is, the browser is still believed moved.
        let document = window(600, CGRect(x: 156, y: 91, width: 1200, height: 800))
        for pointer in [DesktopHitSample(location: CGPoint(x: 700, y: 500), windowNumber: 600),
                        DesktopHitSample(location: CGPoint(x: 60, y: 500), windowNumber: 503), nil] {
            let opened = DesktopCoverage.evaluate([laptop], windows: base + [document], ownWindows: own,
                                                  samples: hits(on: laptop, 600), pointer: pointer,
                                                  distrusted: shown.distrustedCovers)
            XCTAssertEqual(opened.hidden, [], "\(String(describing: pointer))")
            XCTAssertEqual(opened.distrustedCovers?[1]?.map(\.number), [500])
        }
        // A new window that fills the display by itself pauses it, though
        // the browser behind it is still believed moved aside...
        let filling = window(600, laptop.visibleFrame)
        let filled = DesktopCoverage.evaluate([laptop], windows: base + [filling], ownWindows: own,
                                              samples: hits(on: laptop, 600), distrusted: shown.distrustedCovers)
        XCTAssertEqual(filled.hidden, [1])
        XCTAssertEqual(filled.distrustedCovers?[1]?.map(\.number), [500])
        // ...until it closes and the browser is found back in place.
        let closed = DesktopCoverage.evaluate([laptop], windows: base, ownWindows: own, samples: hits(on: laptop, 500),
                                              distrusted: filled.distrustedCovers)
        XCTAssertEqual(closed.hidden, [1])
        XCTAssertNil(closed.distrustedCovers)
    }

    func testAWindowFoundAwayFromItsFrameIsBelievedMovedToo() {
        let base = [ownSurfaces[0], finderIcons[0], maximised[0]]
        let document = window(600, CGRect(x: 156, y: 91, width: 1200, height: 800))
        let shown = DesktopCoverage.evaluate([laptop], windows: base, ownWindows: own, samples: hits(on: laptop, 503))
        let opened = DesktopCoverage.evaluate([laptop], windows: base + [document], ownWindows: own,
                                              samples: hits(on: laptop, 600), distrusted: shown.distrustedCovers)
        // Show Desktop ends and starts again, moving the document aside as
        // well: the desktop is found inside its frame.
        let again = DesktopCoverage.evaluate([laptop], windows: base + [document], ownWindows: own,
                                             samples: hits(on: laptop, 503), distrusted: opened.distrustedCovers)
        XCTAssertEqual(again.hidden, [])
        XCTAssertEqual(again.distrustedCovers?[1]?.map(\.number), [500, 600])
        // So the document, found back in place, shows Show Desktop has ended.
        let ended = DesktopCoverage.evaluate([laptop], windows: base + [document], ownWindows: own,
                                             samples: hits(on: laptop, 600), distrusted: again.distrustedCovers)
        XCTAssertEqual(ended.hidden, [1])
        XCTAssertNil(ended.distrustedCovers)
    }

    func testWindowsBehindAFileOpenedFromTheDesktopAreCheckedWhereTheyShow() {
        let browser = window(500, laptop.visibleFrame)
        let document = window(600, CGRect(x: 156, y: 91, width: 1200, height: 800))
        let desktop = [finderIcons[0], ownSurfaces[0]]
        let drawnDesktop = desktop.map { (number: $0.number, frame: $0.bounds) }
        // Show Desktop slides the maximised browser off the left edge.
        let alone = DesktopCoverage.listing([laptop], windows: [browser] + desktop, ownWindows: own)
        let shown = FakeWindowServer([(500, CGRect(x: 0, y: 33, width: 20, height: 949))] + drawnDesktop)
            .check(alone, distrusted: nil)
        XCTAssertEqual(shown.hidden, [])
        XCTAssertEqual(shown.distrustedCovers?[1]?.map(\.number), [500])

        // A file opened from the desktop brings up a document in front, over
        // every probe, with the pointer on it: no probe can see the browser.
        let opened = DesktopCoverage.listing([laptop], windows: [document, browser] + desktop, ownWindows: own)
        XCTAssertTrue(opened.covered[0].probes.allSatisfy { document.bounds.contains($0) })
        let pointer = CGPoint(x: 700, y: 500)
        // While the browser stays aside, the desktop found beside the
        // document keeps the wallpaper rendering, however the browser was
        // pushed and however wide its sliver.
        for sliver in [CGFloat(20), 300] {
            for aside in [CGRect(x: 0, y: 33, width: sliver, height: 949), CGRect(x: 1512 - sliver, y: 33, width: sliver, height: 949),
                          CGRect(x: 0, y: 33, width: 1512, height: sliver), CGRect(x: 0, y: 982 - sliver, width: 1512, height: sliver)] {
                let server = FakeWindowServer([(600, document.bounds), (500, aside)] + drawnDesktop)
                let stillAside = server.check(opened, pointer: pointer, distrusted: shown.distrustedCovers)
                XCTAssertEqual(stillAside.hidden, [], "\(aside)")
                XCTAssertEqual(stillAside.distrustedCovers?[1]?.map(\.number), [500], "\(aside)")
                // The pointer and every probe, then two points on the browser
                // clear of the document, in opposite corners of the display.
                XCTAssertEqual(server.asked.count, 1 + 5 + 2)
                let beside = Array(server.asked.suffix(2))
                for point in beside {
                    XCTAssertTrue(browser.bounds.insetBy(dx: 48, dy: 48).contains(point), "\(point)")
                    XCTAssertFalse(document.bounds.insetBy(dx: -24, dy: -24).contains(point), "\(point)")
                }
                XCTAssertNotEqual(beside[0].x < 756, beside[1].x < 756)
                XCTAssertNotEqual(beside[0].y < 491, beside[1].y < 491)
            }
        }
        // Once Show Desktop ends, the browser found there pauses it.
        let back = FakeWindowServer([(600, document.bounds), (500, browser.bounds)] + drawnDesktop)
        let ended = back.check(opened, pointer: pointer, distrusted: shown.distrustedCovers)
        XCTAssertEqual(ended.hidden, [1])
        XCTAssertNil(ended.distrustedCovers)

        // Where some of the browser shows in the middle of the display, one
        // point there is enough: here between two documents over the probes.
        let left = window(600, CGRect(x: 156, y: 91, width: 700, height: 800))
        let right = window(601, CGRect(x: 1000, y: 91, width: 400, height: 800))
        let split = DesktopCoverage.listing([laptop], windows: [left, right, browser] + desktop, ownWindows: own)
        let between = FakeWindowServer([(600, left.bounds), (601, right.bounds), (500, browser.bounds)] + drawnDesktop)
        let splitEnded = between.check(split, distrusted: shown.distrustedCovers)
        XCTAssertEqual(between.asked.count, 5 + 1)
        XCTAssertTrue((880..<976).contains(between.asked[5].x), "\(between.asked[5])")
        XCTAssertEqual(splitEnded.hidden, [1])
        XCTAssertNil(splitEnded.distrustedCovers)

        // A document over all but a thin margin of the browser leaves
        // nowhere to look: the wallpaper keeps rendering, as before.
        let wide = window(600, laptop.visibleFrame.insetBy(dx: 30, dy: 30))
        let covered = DesktopCoverage.listing([laptop], windows: [wide, browser] + desktop, ownWindows: own)
        let blind = FakeWindowServer([(600, wide.bounds), (500, browser.bounds)] + drawnDesktop)
        let unsure = blind.check(covered, distrusted: shown.distrustedCovers)
        XCTAssertEqual(blind.asked.count, 5)
        XCTAssertEqual(unsure.hidden, [])
        XCTAssertEqual(unsure.distrustedCovers?[1]?.map(\.number), [500])
    }

    func testTheDesktopFoundOnADisplayKeepsItRenderingWhateverOpenedSince() {
        let base = [ownSurfaces[0], finderIcons[0], maximised[0]]
        let shown = DesktopCoverage.evaluate([laptop], windows: base, ownWindows: own, samples: hits(on: laptop, 503))
        // Two windows opened since tile the display, meeting under the
        // stage's centre. In place, they cover it by themselves.
        let halves = [window(600, CGRect(x: 0, y: 33, width: 756, height: 949)),
                      window(601, CGRect(x: 756, y: 33, width: 756, height: 949))]
        let windows = base + halves
        let tiled = DesktopCoverage.evaluate([laptop], windows: windows, ownWindows: own,
                                             samples: DesktopCoverage.samplePoints(for: laptop).map {
                                                 DesktopHitSample(location: $0, windowNumber: $0.x < 756 ? 600 : 601)
                                             }, distrusted: shown.distrustedCovers)
        XCTAssertEqual(tiled.hidden, [1])
        XCTAssertEqual(tiled.distrustedCovers?[1]?.map(\.number), [500])
        // Show Desktop again: the first point, in the seam, finds the
        // desktop, and the check asks no further. Nothing showed the halves
        // moved, but the desktop found there keeps the display rendering.
        var asked: [CGPoint] = []
        let again = DesktopCoverage.check(DesktopCoverage.listing([laptop], windows: windows, ownWindows: own),
                                          distrusted: tiled.distrustedCovers, primaryHeight: 982,
                                          pointer: { nil }) { point in
            asked.append(point)
            return 503
        }
        XCTAssertEqual(asked, [CGPoint(x: 756, y: 491)])
        XCTAssertEqual(again.hidden, [])
        XCTAssertEqual(again.distrustedCovers?[1]?.map(\.number), [500])
    }

    func testAChangedWindowListIsBelievedAgainOnlyOnItsOwnDisplay() {
        let windows = ownSurfaces + finderIcons + maximised
        let shown = DesktopCoverage.evaluate([laptop, external], windows: windows, ownWindows: own,
                                             samples: hits(on: laptop, 503) + hits(on: external, 504))
        // The external display's window is resized while no hit test can tell.
        let resized = window(501, CGRect(x: 1512, y: 25, width: 2560, height: 1414))
        let changed = DesktopCoverage.evaluate([laptop, external], windows: ownSurfaces + finderIcons + [maximised[0], resized],
                                               ownWindows: own, samples: hits(on: laptop, 0) + hits(on: external, 0),
                                               distrusted: shown.distrustedCovers)
        XCTAssertEqual(changed.hidden, [2])
        XCTAssertEqual(changed.distrustedCovers.map { Set($0.keys) }, [1])
    }

    func testTheDesktopFoundOnADisplayThatWouldNotPauseSaysNothing() {
        // A click-through overlay over the middle of the uncovered external
        // display lets its hit tests, and the pointer's, reach the desktop.
        // The laptop's browser is in place.
        let overlay = window(700, CGRect(x: 2492, y: 420, width: 600, height: 600))
        let windows = ownSurfaces + finderIcons + [maximised[0], overlay]
        let evaluation = DesktopCoverage.evaluate(
            [laptop, external], windows: windows, ownWindows: own, samples: hits(on: laptop, 500) + hits(on: external, 504),
            pointer: DesktopHitSample(location: CGPoint(x: 2792, y: 720), windowNumber: 504))
        XCTAssertEqual(evaluation.hidden, [1])
        XCTAssertNil(evaluation.distrustedCovers)
    }

    // MARK: Full-screen Spaces

    func testFullScreenSpacesPauseWhenNoWallpaperIsListed() {
        // One display in a full-screen Space: nothing below the normal layer
        // is listed, not even our wallpaper, only the app's window, the menu
        // bar and the Dock.
        let app = window(600, laptop.frame)
        let chrome = [window(23439, CGRect(x: 0, y: 0, width: 1512, height: 33), layer: Int(CGWindowLevelForKey(.mainMenuWindow))),
                      window(35, laptop.frame, layer: Int(CGWindowLevelForKey(.dockWindow)))]
        for stageManager in [false, true] {
            XCTAssertEqual(DesktopCoverage.evaluate([laptop], windows: [app] + chrome, ownWindows: own,
                                                    appWindowsCover: !stageManager).hidden, [1], "Stage Manager \(stageManager)")
        }
        // Nothing of the desktop is drawn behind a see-through terminal there.
        let terminal = DesktopWindowRecord(number: 601, layer: normal, alpha: 1, bounds: laptop.frame, ownerPID: 4242)
        XCTAssertEqual(DesktopCoverage.evaluate([laptop], windows: [terminal], ownWindows: own,
                                                seeThroughOwners: [4242]).hidden, [1])
        // A notched display: the window stops below the camera housing, the
        // menu bar is hidden and AppKit's usable area is the whole display.
        let notched = DesktopDisplayRecord(displayID: 3, frame: CGRect(x: 0, y: 0, width: 1728, height: 1117),
                                           visibleFrame: CGRect(x: 0, y: 0, width: 1728, height: 1117), wallpaperWindow: 30)
        let belowTheNotch = window(602, CGRect(x: 0, y: 38, width: 1728, height: 1079))
        XCTAssertEqual(DesktopCoverage.evaluate([notched], windows: [belowTheNotch], ownWindows: [30],
                                                appWindowsCover: false).hidden, [3])
        // Every display full screen, so neither lists a wallpaper.
        XCTAssertEqual(DesktopCoverage.evaluate([laptop, external], windows: [app, window(603, external.frame)],
                                                ownWindows: own, appWindowsCover: false).hidden, [1, 2])

        // The window server lists a backstop below the desktop picture on
        // every display, once for all its Spaces (this Mac's list puts it
        // three levels down), so neither it nor anything else that far down
        // says the desktop is showing.
        let desktopPictureLevel = Int(CGWindowLevelForKey(.desktopWindow))
        let backstop = window(800, laptop.frame, layer: desktopPictureLevel - 3)
        let belowThePicture = window(801, laptop.frame, layer: desktopPictureLevel - 1)
        for stageManager in [false, true] {
            XCTAssertEqual(DesktopCoverage.evaluate([laptop], windows: [app] + chrome + [belowThePicture, backstop],
                                                    ownWindows: own, appWindowsCover: !stageManager).hidden, [1])
        }

        // On the desktop Space something from the desktop picture's level up
        // is always listed: our wallpaper, or if not ours then Finder's icons,
        // the window manager's desktop window or the desktop picture. Any of
        // our windows counts too, at whatever level.
        let desktopPicture = window(700, laptop.frame, layer: desktopPictureLevel)
        let windowManager = window(701, laptop.frame, layer: desktopLevel)
        let ownPanel = window(11, CGRect(x: 556, y: 326, width: 400, height: 330))
        for desktop in [ownSurfaces[0], finderIcons[0], windowManager, desktopPicture, ownPanel] {
            XCTAssertEqual(DesktopCoverage.evaluate([laptop], windows: [desktop, app, backstop], ownWindows: own,
                                                    appWindowsCover: false).hidden, [], "window \(desktop.number)")
        }
        // And with no desktop listed, windows that leave some of the display
        // showing pause nothing.
        let small = window(604, CGRect(x: 100, y: 100, width: 800, height: 600))
        XCTAssertEqual(DesktopCoverage.evaluate([laptop], windows: [small], ownWindows: own, appWindowsCover: false).hidden, [])
    }

    // MARK: What a check asks the window server

    func testChecksAskOnlyWhereTheAnswerCouldMatter() {
        let windows = ownSurfaces + finderIcons + maximised
        let listing = DesktopCoverage.listing([laptop, external], windows: windows, ownWindows: own)
        XCTAssertEqual(listing.covered.map(\.displayID), [1, 2])
        let appKit = { (point: CGPoint) in DesktopCoverage.appKitPoint(point, primaryHeight: 982) }
        let laptopPoints = DesktopCoverage.samplePoints(for: laptop).map(appKit)
        let externalPoints = DesktopCoverage.samplePoints(for: external).map(appKit)
        // In AppKit's coordinates: Quartz (700, 500), on the laptop's browser.
        let pointer = CGPoint(x: 700, y: 482)
        var asked: [CGPoint] = []
        let inPlace = { (point: CGPoint) -> Int in
            asked.append(point)
            return point.x < 1512 ? 500 : 501
        }

        // The windows in place: the pointer first, then each display that
        // would pause until one of its windows is found where the list puts
        // it, which Show Desktop would have moved.
        let paused = DesktopCoverage.check(listing, distrusted: nil, primaryHeight: 982, pointer: { pointer },
                                           hitTest: inPlace)
        XCTAssertEqual(asked, [pointer, laptopPoints[0], externalPoints[0]])
        XCTAssertEqual(paused.hidden, [1, 2])
        XCTAssertNil(paused.distrustedCovers)

        // Show Desktop: each display is asked only until its desktop is found.
        asked = []
        let shown = DesktopCoverage.check(listing, distrusted: nil, primaryHeight: 982, pointer: { pointer }) { point in
            asked.append(point)
            return point.x < 1512 ? 503 : 504
        }
        XCTAssertEqual(asked, [pointer, laptopPoints[0], externalPoints[0]])
        XCTAssertEqual(shown.hidden, [])
        XCTAssertEqual(shown.distrustedCovers.map { Set($0.keys) }, [1, 2])

        // And when it ends, every point is asked again, looking for a window
        // back in place and for the desktop, and the windows are back.
        asked = []
        let ended = DesktopCoverage.check(listing, distrusted: shown.distrustedCovers, primaryHeight: 982,
                                          pointer: { pointer }, hitTest: inPlace)
        XCTAssertEqual(asked, [pointer] + laptopPoints + externalPoints)
        XCTAssertEqual(ended.hidden, [1, 2])
        XCTAssertNil(ended.distrustedCovers)

        // With nothing to pause, or under Stage Manager, nothing is asked,
        // not even where the pointer is.
        asked = []
        var pointerRead = false
        for quiet in [DesktopCoverage.listing([laptop, external], windows: ownSurfaces + finderIcons, ownWindows: own),
                      DesktopCoverage.listing([laptop, external], windows: windows, ownWindows: own, appWindowsCover: false)] {
            let evaluation = DesktopCoverage.check(quiet, distrusted: shown.distrustedCovers, primaryHeight: 982,
                                                   pointer: { pointerRead = true; return pointer }, hitTest: inPlace)
            XCTAssertEqual(evaluation.hidden, [])
            XCTAssertNil(evaluation.distrustedCovers)
        }
        XCTAssertEqual(asked, [])
        XCTAssertFalse(pointerRead)

        // Only the displays that would pause are asked about, and the
        // pointer only where it can count: not over the uncovered external
        // display, near a window's edge, or on the menu bar.
        let laptopOnly = DesktopCoverage.listing([laptop, external], windows: ownSurfaces + finderIcons + [maximised[0]],
                                                 ownWindows: own)
        for elsewhere in [CGPoint(x: 2000, y: 500), CGPoint(x: 10, y: 482), CGPoint(x: 700, y: 975)] {
            asked = []
            _ = DesktopCoverage.check(laptopOnly, distrusted: nil, primaryHeight: 982, pointer: { elsewhere }, hitTest: inPlace)
            XCTAssertEqual(asked, [laptopPoints[0]], "\(elsewhere)")
        }

        // Each point is asked once, even where the pointer rests on one.
        asked = []
        _ = DesktopCoverage.check(listing, distrusted: nil, primaryHeight: 982, pointer: { laptopPoints[0] },
                                  hitTest: inPlace)
        XCTAssertEqual(asked, [laptopPoints[0], externalPoints[0]])
    }

    func testPausedDisplaysCostOneHitTestEachAndTakeTurns() {
        let listing = DesktopCoverage.listing([laptop, external], windows: ownSurfaces + finderIcons + maximised, ownWindows: own)
        let inPlace = FakeWindowServer([(500, laptop.visibleFrame), (501, external.visibleFrame),
                                        (503, laptop.frame), (504, external.frame)])
        let laptopProbes = DesktopCoverage.samplePoints(for: laptop), externalProbes = DesktopCoverage.samplePoints(for: external)
        // Each check starts one probe on, so over five checks every probe of
        // both displays is asked once.
        for rotation in 0..<7 {
            inPlace.asked = []
            let paused = inPlace.check(listing, pointer: CGPoint(x: 700, y: 500), distrusted: nil, rotation: rotation)
            XCTAssertEqual(paused.hidden, [1, 2])
            XCTAssertEqual(inPlace.asked, [CGPoint(x: 700, y: 500), laptopProbes[rotation % 5], externalProbes[rotation % 5]])
        }
        // A panel floating over a probe says nothing either way, so the next
        // probe is asked.
        let panel = window(900, CGRect(x: 700, y: 440, width: 120, height: 100), layer: Int(CGWindowLevelForKey(.floatingWindow)))
        let withPanel = DesktopCoverage.listing([laptop], windows: [panel] + ownSurfaces + finderIcons + maximised, ownWindows: own)
        let underPanel = FakeWindowServer([(900, panel.bounds), (500, laptop.visibleFrame), (503, laptop.frame)])
        XCTAssertEqual(underPanel.check(withPanel, distrusted: nil).hidden, [1])
        XCTAssertEqual(underPanel.asked, Array(laptopProbes.prefix(2)))

        // A window that stays put through Show Desktop -- one it leaves
        // alone, or one opened since it began -- under the probe a check
        // starts from keeps that check paused. The next starts one probe on
        // and finds the desktop.
        let stays = window(700, CGRect(x: 1000, y: 650, width: 300, height: 200))
        let windows = [stays] + ownSurfaces + finderIcons + [maximised[0]]
        let showing = DesktopCoverage.listing([laptop], windows: windows, ownWindows: own)
        XCTAssertEqual(showing.covered.first?.probes, laptopProbes)
        XCTAssertTrue(stays.bounds.contains(laptopProbes[4]))
        let shown = FakeWindowServer([(700, stays.bounds), (500, CGRect(x: 0, y: 33, width: 20, height: 949)),
                                      (503, laptop.frame)])
        let missed = shown.check(showing, distrusted: nil, rotation: 4)
        XCTAssertEqual(missed.hidden, [1])
        XCTAssertNil(missed.distrustedCovers)
        let seen = shown.check(showing, distrusted: missed.distrustedCovers, rotation: 5)
        XCTAssertEqual(seen.hidden, [])
        XCTAssertNotNil(seen.distrustedCovers)
        // From then on every probe is asked, so it stays found.
        shown.asked = []
        let still = shown.check(showing, distrusted: seen.distrustedCovers, rotation: 4)
        XCTAssertEqual(still.hidden, [])
        XCTAssertEqual(shown.asked, [laptopProbes[4], laptopProbes[0]])
    }

    func testHitTestsAskAppKitInItsOwnCoordinates() {
        // This Mac's layout: the external display is stacked above a 1117 pt
        // primary, so Quartz y is negative at its stage's centre, and AppKit's
        // above the primary's height. Only the external display would pause.
        let listing = DesktopCoverage.listing([liveLaptop, liveExternal], windows: liveWindows, ownWindows: liveOwn)
        XCTAssertEqual(listing.covered.map(\.displayID), [4])
        var asked: [CGPoint] = []
        let hitTest = { (point: CGPoint) -> Int in
            asked.append(point)
            return point.x >= 1728 ? 30912 : 35284
        }
        let evaluation = DesktopCoverage.check(listing, distrusted: nil, primaryHeight: 1117, pointer: { nil },
                                               hitTest: hitTest)
        XCTAssertEqual(asked, [CGPoint(x: 3008, y: 1168)])
        XCTAssertEqual(evaluation.hidden, [4])
        // While the list is discredited every probe is asked, each flipped.
        asked = []
        let ended = DesktopCoverage.check(listing, distrusted: [4: listing.covered[0].covers], primaryHeight: 1117,
                                          pointer: { nil }, hitTest: hitTest)
        XCTAssertEqual(asked, DesktopCoverage.samplePoints(for: liveExternal).map {
            DesktopCoverage.appKitPoint($0, primaryHeight: 1117)
        })
        XCTAssertEqual(ended.hidden, [4])
        XCTAssertNil(ended.distrustedCovers)
        // And the flip is undone exactly.
        let point = CGPoint(x: 10, y: 82)
        XCTAssertEqual(DesktopCoverage.quartzPoint(DesktopCoverage.appKitPoint(point, primaryHeight: 982), primaryHeight: 982), point)
    }

    func testDisplayRecordsComeFromAppKitScreens() {
        // This Mac's screens as AppKit reports them, primary first.
        let records = DesktopCoverage.displayRecords([
            (id: 1, frame: CGRect(x: 0, y: 0, width: 1728, height: 1117),
             visibleFrame: CGRect(x: 0, y: 0, width: 1728, height: 1084)),
            (id: 4, frame: CGRect(x: 1728, y: 448, width: 2560, height: 1440),
             visibleFrame: CGRect(x: 1728, y: 556, width: 2560, height: 1332)),
        ], primaryHeight: 1117, wallpapers: [1: 34676, 4: 34677])
        XCTAssertEqual(records, [liveLaptop, liveExternal])
        // A display whose wallpaper is not ordered in yet has none.
        let bare = DesktopCoverage.displayRecords([(id: 9, frame: CGRect(x: 0, y: 0, width: 800, height: 600),
                                                    visibleFrame: .zero)], primaryHeight: 600, wallpapers: [1: 34676])
        XCTAssertEqual(bare.map(\.wallpaperWindow), [nil])
    }

    func testAppKitToQuartzConversionFlipsAboutThePrimaryDisplay() {
        // A display stacked above a 982 pt primary: AppKit y 982...2422.
        let above = DesktopCoverage.quartzRect(CGRect(x: 0, y: 982, width: 2560, height: 1440), primaryHeight: 982)
        XCTAssertEqual(above, CGRect(x: 0, y: -1440, width: 2560, height: 1440))
        let menuBarFree = DesktopCoverage.quartzRect(CGRect(x: 0, y: 0, width: 1512, height: 949), primaryHeight: 982)
        XCTAssertEqual(menuBarFree, CGRect(x: 0, y: 33, width: 1512, height: 949))
        XCTAssertEqual(DesktopCoverage.quartzPoint(CGPoint(x: 10, y: 900), primaryHeight: 982), CGPoint(x: 10, y: 82))
    }

    @MainActor
    func testRenderActivityPausesOnlyWhenAllowed() {
        // An activity that has never been attached renders, whatever the setting.
        let activity = WindowRenderActivity()
        XCTAssertTrue(activity.shouldRender(pauseWhenCovered: true))
        XCTAssertTrue(activity.shouldRender(pauseWhenCovered: false))
        // An ordinary window that has gone away is never rendered.
        activity.follow(window: nil)
        XCTAssertFalse(activity.shouldRender(pauseWhenCovered: false))
        XCTAssertFalse(activity.shouldRender(pauseWhenCovered: true))
    }
}
