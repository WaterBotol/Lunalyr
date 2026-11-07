import AVFoundation
import XCTest
@testable import VersoCustom

@MainActor
final class LyricCardFormatsTests: XCTestCase {
    private func content(aspect: CardAspect, long: Bool = false) -> LyricCard.Content {
        let text = long ? "I have been waiting all of my life for a night like this one under the city lights" : "But I ignored your call"
        let words = text.split(separator: " ").enumerated().map { i, w in
            LyricsFetcher.Word(text: String(w), start: 10 + Double(i) * 0.3, end: 10.28 + Double(i) * 0.3)
        }
        let line = LyricsFetcher.Line(start: 10, end: 10.3 * Double(words.count) + 0.4, text: text, words: words,
                                      isInstrumental: false, wordTimed: true)
        let animation = LyricCard.Animation(line: line, composition: .editorial, mood: .neutral, seed: 0)
        let art = NSImage(size: NSSize(width: 400, height: 400), flipped: false) { rect in
            NSGradient(colors: [.systemOrange, .systemPurple])?.draw(in: rect, angle: 60); return true
        }
        var content = LyricCard.Content(pages: [.init(words: words.map(\.text), animation: animation)], title: "A Song",
                                        artist: "An Artist", artwork: art, palette: LyricCard.palette(for: art), reduceMotion: true)
        content.aspect = aspect
        return content
    }

    func testEveryShapeIsDrawnAtItsOwnSize() throws {
        for aspect in CardAspect.allCases {
            let png = try XCTUnwrap(LyricCard.stillPNG(content(aspect: aspect), scale: 1), "\(aspect)")
            let rep = try XCTUnwrap(NSBitmapImageRep(data: png))
            XCTAssertEqual(CGFloat(rep.pixelsWide), aspect.size.width)
            XCTAssertEqual(CGFloat(rep.pixelsHigh), aspect.size.height)
        }
    }

    func testTheStillIsTwiceTheCardsSizeByDefault() throws {
        let png = try XCTUnwrap(LyricCard.stillPNG(content(aspect: .portrait)))
        let rep = try XCTUnwrap(NSBitmapImageRep(data: png))
        XCTAssertEqual(rep.pixelsWide, 2160)
        XCTAssertEqual(rep.pixelsHigh, 2700)
    }

    func testTheLyricStageStaysInsideEveryShape() {
        for aspect in CardAspect.allCases {
            let stage = content(aspect: aspect).lyricStage
            XCTAssertGreaterThan(stage.width, 300, "\(aspect)")
            XCTAssertGreaterThan(stage.height, 300, "\(aspect)")
            XCTAssertTrue(CGRect(origin: .zero, size: aspect.size).contains(stage), "\(aspect)")
        }
    }

    func testTheVideoIsH264AtTheCardsSize() async throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("card-\(UUID().uuidString).mp4")
        defer { try? FileManager.default.removeItem(at: url) }
        let made = await LyricCard.video(content(aspect: .square), to: url)
        XCTAssertTrue(made)
        let asset = AVURLAsset(url: url)
        let tracks = try await asset.loadTracks(withMediaType: .video)
        let track = try XCTUnwrap(tracks.first)
        let size = try await track.load(.naturalSize)
        XCTAssertEqual(size, CGSize(width: 1080, height: 1080))
        let duration = try await asset.load(.duration).seconds
        XCTAssertGreaterThan(duration, 1.5)
        let rate = try await track.load(.nominalFrameRate)
        XCTAssertGreaterThan(rate, 20)
    }

    func testExportMakesEachChosenFormatAndPublishesThemTogether() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("cards-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: directory) }
        let files = await LyricCard.export(content(aspect: .portrait), formats: [.png, .gif, .mp4], to: directory)
        XCTAssertEqual(files.map(\.0), [.mp4, .gif, .png])
        for (_, url) in files { XCTAssertGreaterThan(try Data(contentsOf: url).count, 1000) }
        let board = NSPasteboard(name: NSPasteboard.Name("LyricCardFormatsTests.\(UUID().uuidString)"))
        XCTAssertTrue(LyricCard.publish(files, to: board))
        XCTAssertEqual(board.pasteboardItems?.count, 3)
        XCTAssertNotNil(board.pasteboardItems?.last?.data(forType: .png), "the PNG also travels as image data")
    }

    /// Writes one PNG per shape (set LUNALYR_CARD_PREVIEW_DIR) to look at.
    func testWritePreviews() throws {
        guard let path = ProcessInfo.processInfo.environment["LUNALYR_CARD_PREVIEW_DIR"]
            ?? ProcessInfo.processInfo.environment["LUNALYR_UI_SNAPSHOT_CARD_DIR"] else { throw XCTSkip("no preview folder") }
        for aspect in CardAspect.allCases {
            for long in [false, true] {
                let png = try XCTUnwrap(LyricCard.stillPNG(content(aspect: aspect, long: long), scale: 0.5))
                try png.write(to: URL(fileURLWithPath: path).appendingPathComponent("\(aspect.rawValue)-\(long ? "long" : "short").png"))
            }
        }
    }
}
