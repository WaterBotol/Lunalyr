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
        XCTAssertEqual(size, CGSize(width: 720, height: 720), "MP4s are two thirds of the card size")
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

    func testSavingTwiceIntoOneFolderKeepsBothCards() async throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("cards-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: directory) }
        let first = await LyricCard.export(content(aspect: .square), formats: [.png, .mp4], to: directory)
        let second = await LyricCard.export(content(aspect: .square), formats: [.png, .mp4], to: directory)
        XCTAssertEqual(first.map(\.1.lastPathComponent), ["Lunalyr Lyric Card.mp4", "Lunalyr Lyric Card.png"])
        // The MP4 writer refuses a file that exists, so the second MP4 used to fail.
        XCTAssertEqual(second.map(\.1.lastPathComponent), ["Lunalyr Lyric Card 2.mp4", "Lunalyr Lyric Card 2.png"])
        for (_, url) in first + second { XCTAssertGreaterThan(try Data(contentsOf: url).count, 1000) }
    }

    func testAStillHoldsAtMostThreeLinesOneUnderAnother() {
        var many = content(aspect: .portrait)
        let page = many.pages[0]
        many.pages = (0..<6).map { index in LyricCard.Page(words: ["Line", "\(index)"], animation: page.animation) }
        let still = many.stillPage
        XCTAssertEqual(still.words.filter { $0 == LyricCard.lineBreak }.count, 2)
        XCTAssertEqual(still.words.filter { $0 == "Line" }.count, 3)
        XCTAssertFalse(still.words.contains("3"))
        var one = many
        one.pages = [many.pages[0]]
        XCTAssertFalse(one.stillPage.words.contains(LyricCard.lineBreak))
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

@MainActor
final class BackingVocalPreviewTests: XCTestCase {
    /// Writes frames of a line with backing vocals (set LUNALYR_CARD_PREVIEW_DIR) to look at.
    func testWriteBackingFrames() async throws {
        guard let path = ProcessInfo.processInfo.environment["LUNALYR_CARD_PREVIEW_DIR"] else { throw XCTSkip("no folder") }
        let pieces: [(String, Double)] = [("Hold", 10), ("me", 10.4), ("(oh", 10.8), ("oh)", 11.0), ("closer", 11.5), ("tonight", 12.0)]
        let words = pieces.map { LyricsFetcher.Word(text: $0.0, start: $0.1, end: $0.1 + 0.4) }
        let line = LyricsFetcher.Line(start: 10, end: 12.6, text: "Hold me (oh oh) closer tonight", words: words,
                                      isInstrumental: false, wordTimed: true)
        let art = NSImage(size: NSSize(width: 400, height: 400), flipped: false) { rect in
            NSGradient(colors: [.systemTeal, .systemIndigo])?.draw(in: rect, angle: 60); return true
        }
        for layout in [LyricComposition.editorial, .constellation, .cutout, .spoken] {
            var content = LyricCard.Content(pages: [.init(words: pieces.map(\.0),
                animation: LyricCard.Animation(line: line, composition: layout, mood: .neutral, seed: 0))],
                title: "A Song", artist: "An Artist", artwork: art, palette: LyricCard.palette(for: art), reduceMotion: true)
            content.aspect = .portrait
            var elapsed = 0.0, saved = 0
            await LyricCard.renderFrames(content, scale: 0.5, minimumCadence: 0.1, budget: nil) { image, delay in
                elapsed += delay
                if elapsed > 1.0 && saved == 0 || elapsed > 1.9 && saved == 1 {
                    let rep = NSBitmapImageRep(cgImage: image)
                    try? rep.representation(using: .png, properties: [:])?
                        .write(to: URL(fileURLWithPath: path).appendingPathComponent("backing-\(layout.rawValue)-\(saved).png"))
                    saved += 1
                }
                return true
            }
        }
    }
}
