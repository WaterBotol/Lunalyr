import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// The card's panes and its hit mask.
@MainActor
final class PlayerSleeveTests: XCTestCase {
    private static let suite = "lunalyr.tests.player-sleeve-model"

    override func tearDown() {
        UserDefaults().removePersistentDomain(forName: Self.suite)
        UserDefaults().removePersistentDomain(forName: "lunalyr.tests.player-sleeve")
        super.tearDown()
    }

    private func model() -> DesktopPlayerModel {
        DesktopPlayerModel(defaults: UserDefaults(suiteName: Self.suite)!, preview: true)
    }

    func testSearchAndQueueButtonsOpenAndClose() {
        let model = model()
        XCTAssertEqual(model.pane, .song)
        model.toggle(.search)
        XCTAssertTrue(model.showsSearch)
        model.toggle(.search)
        XCTAssertEqual(model.pane, .song)
        model.toggle(.queue)
        XCTAssertTrue(model.showsQueue)
        model.toggle(.search)
        XCTAssertTrue(model.showsSearch, "the search replaces the queue")
        XCTAssertFalse(model.showsQueue)
    }

    func testATypedSearchStaysOpen() {
        let model = model()
        model.loadPreview(queue: [], shuffle: false, query: "evening", pane: .song)
        XCTAssertTrue(model.showsSearch, "something typed shows the search")
        model.closePane()
        XCTAssertTrue(model.showsSearch, "the player going away keeps a typed search")
        model.loadPreview(queue: [], shuffle: false, pane: .queue)
        XCTAssertTrue(model.showsQueue)
        model.closePane()
        XCTAssertEqual(model.pane, .song, "the queue closes with the player")
    }

    func testAPreviewModelNeverSearchesOrPlays() {
        let model = model()
        model.loadPreview(queue: PlayerPreviewScene.queue, shuffle: true, results: PlayerPreviewScene.results,
                          query: "evening", pane: .search)
        model.runSearch()
        XCTAssertEqual(model.status, .idle)
        model.player = .spotify
        XCTAssertEqual(model.status, .idle, "switching player starts no search")
        model.play(PlayerPreviewScene.queue[0])
        XCTAssertEqual(model.queue.first(where: \.isCurrent)?.id, "q3", "nothing played")
    }

    func testCardSitsUnderTheMiddleAndFitsThePanel() {
        XCTAssertEqual(DesktopPlayerView.cardTop(panelHeight: 780), 398)
        for height in [780.0, 700, 500, 300] as [CGFloat] {
            let top = DesktopPlayerView.cardTop(panelHeight: height)
            XCTAssertGreaterThanOrEqual(top, 0)
            if height >= DesktopPlayerView.tallestHeight {
                XCTAssertLessThanOrEqual(top + DesktopPlayerView.tallestHeight, height, "the open queue fits, \(height)")
            }
        }
        XCTAssertEqual(CGFloat(ScrubMath.barWidth), DesktopPlayerView.column, "the bar spans the column")
        XCTAssertEqual(DesktopPlayerView.artWidth + DesktopPlayerView.column + 3 * DesktopPlayerView.padding,
                       DesktopPlayerView.width)
    }

    /// The hit mask is drawn from the same view: every part of the card
    /// takes clicks (the cover, the transport, the icons, the bar, the
    /// search, the queue rows), and nothing outside it does.
    func testHitMaskCoversTheWholeCard() throws {
        let panel = SleeveScene.panel
        let left = (panel.width - DesktopPlayerView.width) / 2
        let top = DesktopPlayerView.cardTop(panelHeight: panel.height)
        func point(x: CGFloat, fromTop y: CGFloat) -> CGPoint { CGPoint(x: x, y: panel.height - y) }

        for state in [SleeveState.rest, .search, .queue, .nothing, .align, .spotify] {
            let fixture = SleeveFixture(state: state)
            let mask = try XCTUnwrap(ControlHitMask.render(fixture.player.environment(\.playerMaskRender, true),
                                                           size: panel), "\(state)")
            let height = DesktopPlayerView.columnHeight(fixture.model.showsSearch ? .search : fixture.model.showsQueue ? .queue : .song,
                                                         queueRows: fixture.model.queue.count)
                + 2 * DesktopPlayerView.padding
            let inside = [
                point(x: left + 108, fromTop: top + 108),                                // the cover
                point(x: left + DesktopPlayerView.width - 30, fromTop: top + 30),          // top right
                point(x: left + 250, fromTop: top + height - 30),                          // the column, low
                point(x: left + DesktopPlayerView.width - 40, fromTop: top + height - 26), // the row's icons / rows
                point(x: left + 6, fromTop: top + height / 2),                             // the left edge
            ]
            for (index, inside) in inside.enumerated() {
                XCTAssertTrue(mask.contains(inside), "\(state): point \(index) \(inside) should take clicks")
            }
            let outside = [
                point(x: 20, fromTop: 20),
                point(x: panel.width / 2, fromTop: top - 30),                  // above the card: the lyric
                point(x: left - 20, fromTop: top + 100),
                point(x: left + DesktopPlayerView.width + 20, fromTop: top + 100),
            ] + (top + height + 14 < panel.height ? [point(x: panel.width / 2, fromTop: top + height + 14)] : [])
            for (index, outside) in outside.enumerated() {
                XCTAssertFalse(mask.contains(outside), "\(state): point \(index) \(outside) should reach Finder")
            }
        }
    }

    func testEveryStateDraws() throws {
        for state in SleeveState.allCases {
            for backdrop in SleeveBackdrop.allCases {
                let renderer = ImageRenderer(content: SleeveScene(state: state, backdrop: backdrop))
                renderer.scale = 0.25
                XCTAssertNotNil(renderer.cgImage, "\(state) \(backdrop)")
            }
        }
    }
}

/// Renders the real player (Sleeve) in every state, on a dark and a light
/// wallpaper, to PNGs for the user to look at. Preview only: nothing plays,
/// nothing is fetched. Skipped unless LUNALYR_PLAYER_LOOK names a folder
/// (pass it to `xcodebuild test` as TEST_RUNNER_LUNALYR_PLAYER_LOOK=<dir>).
@MainActor
final class PlayerSleevePreviewTests: XCTestCase {
    func testRenderPlayerSleeve() throws {
        let environment = ProcessInfo.processInfo.environment
        guard let path = environment["LUNALYR_PLAYER_LOOK"] ?? environment["TEST_RUNNER_LUNALYR_PLAYER_LOOK"],
              !path.isEmpty else {
            throw XCTSkip("Set TEST_RUNNER_LUNALYR_PLAYER_LOOK=<dir> to render the player's previews")
        }
        let directory = URL(fileURLWithPath: path)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { PlayerInteraction.shared.dragging = false; PlayerInteraction.shared.lastClick = nil }
        for (index, state) in SleeveState.allCases.enumerated() {
            for backdrop in SleeveBackdrop.allCases {
                let caption = "Sleeve \u{2014} \(state.title) (\(backdrop.rawValue) wallpaper)"
                let renderer = ImageRenderer(content: SleeveScene(state: state, backdrop: backdrop, caption: caption))
                renderer.scale = 1.5
                let image = try XCTUnwrap(renderer.cgImage, "\(state)")
                try DesktopPlayerPreviewTests.write(image, to: directory.appendingPathComponent(
                    String(format: "%02d-%@-%@.png", index + 1, state.rawValue, backdrop.rawValue)))
            }
        }
    }
}
