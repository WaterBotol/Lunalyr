import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// The opening title card on its own, settled, for judging its layout:
/// full and compact, with and without a cover, a long title and a title
/// with a romanised reading. Preview only. Skipped unless
/// LUNALYR_TITLE_CARD_PREVIEWS names a folder (pass it to `xcodebuild test`
/// as TEST_RUNNER_LUNALYR_TITLE_CARD_PREVIEWS=<dir>); files are named
/// `<prefix>-<case>.png`, the prefix from LUNALYR_TITLE_CARD_PREFIX ("after").
@MainActor
final class OpeningTitleCardLayoutPreviewTests: XCTestCase {
    struct Case {
        let name: String
        let title: String
        var artist = "Yot Club"
        var album: String? = "Rare Hymns"
        var reading: String? = nil
        var size: OpeningTitle.Size = .full
        var cover = true
    }

    static let cases: [Case] = [
        Case(name: "full-cover", title: "Human Nature"),
        Case(name: "full-no-cover", title: "Human Nature", cover: false),
        Case(name: "compact-cover", title: "Human Nature", size: .compact),
        Case(name: "compact-no-cover", title: "Human Nature", size: .compact, cover: false),
        Case(name: "long-cover", title: "Everything You Said Before the Summer Ended (Midnight Version)",
             artist: "Clara Vey & The Lanterns", album: "Paper Moons"),
        Case(name: "long-no-cover", title: "Everything You Said Before the Summer Ended (Midnight Version)",
             artist: "Clara Vey & The Lanterns", album: "Paper Moons", cover: false),
        Case(name: "long-compact-cover", title: "Everything You Said Before the Summer Ended (Midnight Version)",
             artist: "Clara Vey & The Lanterns", album: "Paper Moons", size: .compact),
        Case(name: "reading-cover", title: "夜に駆ける", artist: "YOASOBI", album: "THE BOOK", reading: "Yoru ni Kakeru"),
        Case(name: "reading-no-cover", title: "夜に駆ける", artist: "YOASOBI", album: "THE BOOK", reading: "Yoru ni Kakeru",
             cover: false),
    ]

    static let cover: NSImage = {
        let data = OpeningTitlePreviewTests.coverPNG((Color(red: 0.95, green: 0.5, blue: 0.35),
                                                      Color(red: 0.25, green: 0.15, blue: 0.45)))
        return data.flatMap(NSImage.init(data:)) ?? NSImage()
    }()

    func testRenderTitleCardLayouts() throws {
        let environment = ProcessInfo.processInfo.environment
        guard let path = environment["LUNALYR_TITLE_CARD_PREVIEWS"] ?? environment["TEST_RUNNER_LUNALYR_TITLE_CARD_PREVIEWS"],
              !path.isEmpty else {
            throw XCTSkip("Set TEST_RUNNER_LUNALYR_TITLE_CARD_PREVIEWS=<dir> to render the title card layouts")
        }
        let prefix = environment["LUNALYR_TITLE_CARD_PREFIX"] ?? environment["TEST_RUNNER_LUNALYR_TITLE_CARD_PREFIX"] ?? "after"
        let folder = URL(fileURLWithPath: path)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let screen = OpeningTitlePreviewTests.screen
        let baseSize = LyricViewportMetrics(size: screen).baseSize
        for item in Self.cases {
            let phase = OpeningTitle.Phase(age: 10, exit: 0, size: item.size, moving: false)
            let view = ZStack {
                OpeningTitlePreviewTests.NightStreet()
                Color.black.opacity(0.35)
                OpeningTitleCard(phase: phase, title: item.title, artist: item.artist, album: item.album,
                                 reading: item.reading, cover: item.cover ? Self.cover : nil, baseSize: baseSize,
                                 accent: Color(.sRGB, red: 0.95, green: 0.55, blue: 0.42), mood: .neutral)
                // Guides: the card's left edge and the bottom inset.
                Path { path in
                    let left = max(40, screen.width * 0.058)
                    path.move(to: CGPoint(x: left, y: 0))
                    path.addLine(to: CGPoint(x: left, y: screen.height))
                }
                .stroke(Color.cyan.opacity(0.35), lineWidth: 1)
            }
            .frame(width: screen.width, height: screen.height)
            let renderer = ImageRenderer(content: view)
            renderer.scale = 1
            let full = try XCTUnwrap(renderer.cgImage, item.name)
            // The lower-left of the desktop, where the card sits.
            let crop = try XCTUnwrap(full.cropping(to: CGRect(x: 0, y: screen.height * 0.45,
                                                              width: screen.width * 0.62, height: screen.height * 0.55)))
            try DesktopPlayerPreviewTests.write(crop, to: folder.appendingPathComponent("\(prefix)-\(item.name).png"))
        }
    }
}
