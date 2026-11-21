import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// An action word's moving letters keep the size its layout fitted it to.
/// SwiftUI lays a word out again in its frame plus the renderer's display
/// padding to draw it, so a word set to shrink to fit (Hinge's pages) was
/// drawn at full size, far past its page, while its action ran.
@MainActor
final class LivelyGlyphFitTests: XCTestCase {
    func testOnlyAOneLineWordWiderThanItsFrameIsScaled() throws {
        guard #available(macOS 15.0, *) else { throw XCTSkip("Letters move on macOS 15 and later") }
        XCTAssertEqual(LivelyGlyphRenderer.fitScale(lineWidth: 800, lines: 1, frame: 200), 0.25)
        XCTAssertEqual(LivelyGlyphRenderer.fitScale(lineWidth: 200.3, lines: 1, frame: 200), 1, "rounding")
        XCTAssertEqual(LivelyGlyphRenderer.fitScale(lineWidth: 150, lines: 1, frame: 200), 1)
        XCTAssertEqual(LivelyGlyphRenderer.fitScale(lineWidth: 800, lines: 2, frame: 200), 1, "wrapped text is left alone")
        XCTAssertEqual(LivelyGlyphRenderer.fitScale(lineWidth: 800, lines: 1, frame: nil), 1)
    }

    /// Where a word shrunk to its frame is inked, with and without an action
    /// moving its letters (at its onset, before anything moves): the same
    /// place and size, in each lyric face and in one whose line box the
    /// layout sets well below its origin (Georgia), on a Hinge page.
    func testAShrunkWordIsDrawnInItsOwnPlaceWhileItsLettersMove() throws {
        guard #available(macOS 15.0, *) else { throw XCTSkip("Letters move on macOS 15 and later") }
        let moving = LivelyGlyphs(action: .dip, t: 0.001, duration: 1.7, intensity: 1, hold: 1.2, size: 200, glow: .white,
                                  seed: 1, lift: 0)
        let faces = [LyricFace.sans, .serif, .slab, .soft].map { $0.font(size: 200, hero: true) } + [.custom("Georgia", size: 200)]
        for font in faces {
            for width: CGFloat in [200, 2000] {
                func word(_ glyphs: LivelyGlyphs?) -> some View {
                    HingeCap(limit: width) {
                        Text("FALLING").font(font).foregroundStyle(.white)
                            .livelyLetters(particles: false)
                            .lineLimit(1).minimumScaleFactor(0.2)
                    }
                    .environment(\.lyricLivelyGlyphs, glyphs)
                }
                let still = try XCTUnwrap(Self.ink(word(nil)))
                let live = try XCTUnwrap(Self.ink(word(moving)))
                if width == 200 { XCTAssertLessThanOrEqual(still.width, 201) }
                XCTAssertEqual(live.minX, still.minX, accuracy: 3, "\(font) \(width)")
                XCTAssertEqual(live.maxX, still.maxX, accuracy: 4, "\(font) \(width)")
                XCTAssertEqual(live.minY, still.minY, accuracy: 3, "\(font) \(width)")
                XCTAssertEqual(live.maxY, still.maxY, accuracy: 3, "\(font) \(width)")
            }
        }
    }

    /// Hinge moves an action word by the size it is drawn at, not its set size.
    func testHingeActionTravelsInTheWordsDrawnSize() {
        XCTAssertEqual(StageLyricView.hingeFitted(size: 200, width: 800, room: 400, shrink: 0.3), 100)
        XCTAssertEqual(StageLyricView.hingeFitted(size: 200, width: 800, room: 100, shrink: 0.3), 60, "no smaller than the shrink")
        XCTAssertEqual(StageLyricView.hingeFitted(size: 200, width: 300, room: 400, shrink: 0.3), 200, "a word that fits")
    }

    /// The box of lit pixels, drawn on a 2400 × 900 black canvas.
    private static func ink(_ view: some View) -> CGRect? {
        let renderer = ImageRenderer(content: view.frame(width: 2400, height: 900).background(Color.black))
        renderer.scale = 1
        guard let image = renderer.cgImage, let data = image.dataProvider?.data as Data? else { return nil }
        var minX = Int.max, maxX = -1, minY = Int.max, maxY = -1
        for y in 0..<image.height {
            for x in 0..<image.width where data[y * image.bytesPerRow + x * 4 + 1] > 128 {
                minX = min(minX, x); maxX = max(maxX, x); minY = min(minY, y); maxY = max(maxY, y)
            }
        }
        return maxX >= minX ? CGRect(x: minX, y: minY, width: maxX - minX, height: maxY - minY) : nil
    }
}
