import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// Word presence 2.0 (`WordPresence`): p = 1 is the old look bit for bit,
/// p = 2 strengthens the in, hold and out by the stated factors, and
/// Reduce Motion is untouched.
@MainActor
final class WordPresenceTests: XCTestCase {
    private static let styles: [WordArrival] = [.fade, .lift, .pop, .orbit, .glide, .unfold, .cutoutFold,
                                                .cutoutSlide, .satinRibbon, .splitInk]
    private static let progresses: [Double] = [0, 0.1, 0.33, 0.5, 0.77, 0.95, 1]
    private static let arrivals: [Double?] = [nil, 0, 0.4, 0.92, 1.06, 1]

    // MARK: Factors

    func testDefaultIsTwoAndClampedToOneToTwo() {
        XCTAssertEqual(WordPresence.wordPresence, 2)
        XCTAssertEqual(WordPresence.clamped(0.3), 1)
        XCTAssertEqual(WordPresence.clamped(1.5), 1.5)
        XCTAssertEqual(WordPresence.clamped(7), 2)
        XCTAssertEqual(WordPresence.clamped(.nan), 1)
        XCTAssertEqual(WordPresence.clamped(.infinity), 1)
        XCTAssertEqual(WordPresence.effective(2, reduceMotion: true), 1)
        XCTAssertEqual(WordPresence.effective(2, reduceMotion: false), 2)
    }

    func testFactorsAreExactlyOneAtOne() {
        XCTAssertEqual(WordPresence.extra(1), 0)
        XCTAssertEqual(WordPresence.entrance(1), 1)
        XCTAssertEqual(WordPresence.exit(1), 1)
        XCTAssertEqual(WordPresence.hold(1), 1)
        XCTAssertEqual(WordPresence.glow(1), 1)
        for value in [0.7, 1.0, 1.0731, 1.15, -0.2] {
            XCTAssertEqual(WordPresence.about1(value, 1), value)
        }
    }

    func testFactorsAtTwo() {
        XCTAssertEqual(WordPresence.entrance(2), 1.8, accuracy: 1e-12)
        XCTAssertEqual(WordPresence.exit(2), 1.8, accuracy: 1e-12)
        XCTAssertEqual(WordPresence.hold(2), 1.7, accuracy: 1e-12)
        XCTAssertEqual(WordPresence.glow(2), 1.35, accuracy: 1e-12)
        XCTAssertEqual(WordPresence.about1(1.1, 1.7), 1.17, accuracy: 1e-12)
        // Halfway is halfway.
        XCTAssertEqual(WordPresence.entrance(1.5), 1.4, accuracy: 1e-12)
    }

    // MARK: Entrances (flow and rolling)

    func testEntranceAtOneIsTheOldEntranceExactly() {
        for style in Self.styles {
            for progress in Self.progresses {
                for arrival in Self.arrivals {
                    let old = WordEntrance.transform(progress: progress, arrival: arrival, style: style, size: 48,
                                                     direction: -1, reduceMotion: false, maxTurn: 9)
                    let one = WordEntrance.transform(progress: progress, arrival: arrival, style: style, size: 48,
                                                     direction: -1, reduceMotion: false, maxTurn: 9, presence: 1)
                    XCTAssertEqual(old, one, "\(style) \(progress) \(String(describing: arrival))")
                    XCTAssertEqual(old, Self.legacyTransform(progress: progress, arrival: arrival, style: style,
                                                             size: 48, direction: -1, maxTurn: 9),
                                   "\(style) \(progress) \(String(describing: arrival))")
                }
            }
        }
    }

    func testEntranceAtTwoTravelsTurnsAndShrinksFurther() {
        for style in Self.styles where style != .fade {
            for progress in Self.progresses {
                let one = WordEntrance.transform(progress: progress, arrival: nil, style: style, size: 48,
                                                 direction: 1, reduceMotion: false)
                let two = WordEntrance.transform(progress: progress, arrival: nil, style: style, size: 48,
                                                 direction: 1, reduceMotion: false, presence: 2)
                XCTAssertEqual(two.offset.width, one.offset.width * 1.8, accuracy: 1e-9)
                XCTAssertEqual(two.offset.height, one.offset.height * 1.8, accuracy: 1e-9)
                XCTAssertEqual(two.turn, one.turn * 1.8, accuracy: 1e-9)
                XCTAssertEqual(1 - two.scale, (1 - one.scale) * 1.8, accuracy: 1e-9)
                XCTAssertEqual(two.fold, one.fold, "the fold is not part of the rules")
                XCTAssertEqual(two.eased, one.eased)
            }
        }
    }

    func testSoftFocusRisesAndGrowsIntoPlaceAtTwo() {
        for progress in Self.progresses {
            let one = WordEntrance.transform(progress: progress, arrival: nil, style: .fade, size: 50,
                                             direction: 1, reduceMotion: false)
            XCTAssertEqual(one.offset, .zero)
            XCTAssertEqual(one.scale, 1)
            let two = WordEntrance.transform(progress: progress, arrival: nil, style: .fade, size: 50,
                                             direction: 1, reduceMotion: false, presence: 2)
            let remaining = 1 - two.eased
            XCTAssertEqual(two.offset.height, CGFloat(0.32 * remaining) * 50, accuracy: 1e-9)
            XCTAssertEqual(two.offset.width, 0)
            XCTAssertEqual(two.scale, CGFloat(1 - 0.12 * remaining), accuracy: 1e-9)
            // A rolling stage's faint preview does not rise.
            let preview = WordEntrance.transform(progress: progress, arrival: nil, style: .fade, size: 50,
                                                 direction: 1, reduceMotion: false, presence: 2, softRise: false)
            XCTAssertEqual(preview, one)
        }
    }

    func testReduceMotionEntranceIsUnchanged() {
        for style in Self.styles {
            for progress in Self.progresses {
                XCTAssertEqual(WordEntrance.transform(progress: progress, arrival: 0.5, style: style, size: 48,
                                                      direction: 1, reduceMotion: true, presence: 2),
                               WordEntrance.Transform())
            }
        }
    }

    // MARK: Hold (flow)

    func testSwellAtOneIsTheOldSwellExactly() {
        for heat in [0.0, 0.3, 0.8, 1.1] {
            for glow in [0.0, 0.5, 1.0] {
                for gesture in [0.96, 1.0, 1.04, 1.12] {
                    let old = (1 + 0.07 * CGFloat(heat) + 0.03 * CGFloat(glow)) * CGFloat(gesture)
                    XCTAssertEqual(ScatteredRunFrame.swell(heat: heat, glow: glow, gesture: gesture, reduceMotion: false), old)
                    XCTAssertEqual(ScatteredRunFrame.swell(heat: heat, glow: glow, gesture: gesture, reduceMotion: false,
                                                           presence: 1), old)
                    let two = ScatteredRunFrame.swell(heat: heat, glow: glow, gesture: gesture, reduceMotion: false,
                                                      presence: 2)
                    XCTAssertEqual(two - 1, (old - 1) * 1.7, accuracy: 1e-9)
                    XCTAssertEqual(ScatteredRunFrame.swell(heat: heat, glow: glow, gesture: gesture, reduceMotion: true,
                                                           presence: 2), 1)
                }
            }
        }
    }

    func testWordFloatComparesPresence() {
        let drift = vc_idle_drift(7, 1, 0)
        let one = WordFloat(drift: drift, leave: 0.4, size: 40, seed: 7)
        XCTAssertEqual(one.presence, 1)
        XCTAssertNotEqual(one, WordFloat(drift: drift, leave: 0.4, size: 40, seed: 7, presence: 2))
    }

    // MARK: Rolling

    func testRollingAtOneDrawsTheOldPoseAndPresenceNeverMovesTheStack() throws {
        let words = "we keep dreaming under neon rain".split(separator: " ").enumerated().map { index, text in
            LyricsFetcher.Word(text: String(text), start: 1 + Double(index) * 0.6, end: 1.5 + Double(index) * 0.6)
        }
        for composition in [LyricComposition.spoken, .cascade, .cutout] {
            let prepared = SpokenTypography.prepare(.init(words: words, baseSize: 50, mood: .neutral,
                composition: composition, fontIdentity: LyricFontIdentity(settings: .shared)), settings: .shared)
            let token = try XCTUnwrap(prepared.dropFirst(2).first)
            for rank in [-0.6, -0.1, 0, 0.3, 1, 2.5] {
                for time in [1.9, 2.25, 2.6, 3.3] {
                    for arrival in [WordArrival.glide, .fade, .lift] {
                        func state(_ presence: Double, reduceMotion: Bool = false) -> RollingTokenState {
                            RollingTokenState.make(token, next: nil, rank: rank, time: time, composition: composition,
                                baseSize: 50, stageWidth: 1000, intensity: 0.4, reduceMotion: reduceMotion,
                                arrival: arrival, presence: presence)
                        }
                        let one = state(1), two = state(2)
                        XCTAssertEqual(one.drawScale, one.scale)
                        XCTAssertEqual(one.drawAngle, one.angle)
                        // Render-only: the stack's slot and the fit's inputs never change.
                        XCTAssertEqual(two.slot, one.slot)
                        XCTAssertEqual(two.scale, one.scale)
                        XCTAssertEqual(two.angle, one.angle)
                        XCTAssertEqual(two.pose.blur, one.pose.blur, "receding blur is unchanged")
                        let hold = 1 + 0.7 * one.focus
                        XCTAssertEqual(two.drawScale - 1, (one.scale - 1) * hold, accuracy: 1e-9)
                        XCTAssertEqual(two.drawAngle, one.angle * hold, accuracy: 1e-9)
                        let reduced = state(2, reduceMotion: true)
                        XCTAssertEqual(reduced.drawScale, reduced.scale)
                        XCTAssertEqual(reduced.drawAngle, reduced.angle)
                    }
                }
            }
        }
    }

    // MARK: Stage layouts

    func testStagePoseIsTheIdentityAtOneAndUnderReduceMotion() {
        for time in stride(from: 0.5, through: 4, by: 0.05) {
            let arrival = StageMotion.arrival(1, at: time)
            let leave = StageMotion.leave(at: time, exitStart: 3)
            XCTAssertEqual(WordPresence.stagePose(1, arrival: arrival, time: time, start: 1, end: 1.5, leave: leave,
                                                  reduceMotion: false), .identity)
            XCTAssertEqual(WordPresence.stagePose(2, arrival: arrival, time: time, start: 1, end: 1.5, leave: leave,
                                                  reduceMotion: true), .identity)
        }
    }

    func testStagePoseAtTwo() {
        // Arriving: the full arrive layer, before it is sung.
        let arriving = WordPresence.stagePose(2, arrival: 0, time: 0.9, start: 1, end: 1.5, leave: 0, reduceMotion: false)
        XCTAssertEqual(arriving.dy, 0.32, accuracy: 1e-12)
        XCTAssertEqual(arriving.scale, 0.88, accuracy: 1e-12)
        XCTAssertEqual(arriving.blur, 2, accuracy: 1e-12)
        // Sung and landed: the swell.
        let sung = WordPresence.stagePose(2, arrival: 1, time: 1.3, start: 1, end: 1.5, leave: 0, reduceMotion: false)
        XCTAssertEqual(sung.dy, 0, accuracy: 1e-12)
        XCTAssertEqual(sung.scale, 1.06, accuracy: 1e-12)
        XCTAssertEqual(sung.blur, 0, accuracy: 1e-12)
        // A short word still swells for at least 0.15 s.
        let short = WordPresence.stagePose(2, arrival: 1, time: 1.15, start: 1, end: 1.02, leave: 0, reduceMotion: false)
        XCTAssertEqual(short.scale, 1.06, accuracy: 1e-12)
        // Long after: settled.
        XCTAssertEqual(WordPresence.stagePose(2, arrival: 1, time: 3, start: 1, end: 1.5, leave: 0, reduceMotion: false),
                       WordPresence.StagePose(dy: 0, scale: 1, blur: 0))
        // Leaving in full.
        let gone = WordPresence.stagePose(2, arrival: 1, time: 4, start: 1, end: 1.5, leave: 1, reduceMotion: false)
        XCTAssertEqual(gone.dy, -0.26, accuracy: 1e-12)
        XCTAssertEqual(gone.scale, 0.96, accuracy: 1e-12)
        XCTAssertEqual(gone.blur, 2.4, accuracy: 1e-12)
        // The spring's overshoot never blurs negatively.
        let over = WordPresence.stagePose(2, arrival: 1.08, time: 1.1, start: 1, end: 1.5, leave: 0, reduceMotion: false)
        XCTAssertEqual(over.blur, 0)
        // No exit, or a broken spring value: never NaN.
        let none = WordPresence.stagePose(2, arrival: .nan, time: 2, start: 1, end: 1.5,
                                          leave: StageMotion.leave(at: 2, exitStart: .infinity), reduceMotion: false)
        XCTAssertFalse(none.dy.isNaN || none.scale.isNaN || none.blur.isNaN)
        XCTAssertEqual(none.blur, 0)
    }

    // MARK: Helpers

    /// `WordEntrance.transform` as it was before word presence.
    private static func legacyTransform(progress: Double, arrival: Double?, style: WordArrival, size: CGFloat,
                                        direction: Double, maxTurn: Double) -> WordEntrance.Transform {
        let paper = style.isCutout
        let eased = paper || style.isSwipe ? progress : (arrival ?? vc_motion_ease(progress))
        let remaining = 1 - eased
        return WordEntrance.Transform(
            offset: CGSize(width: style == .orbit || style == .glide || style == .cutoutSlide
                               ? CGFloat(remaining * direction) * size * (style == .orbit ? 0.32 : 0.72) : 0,
                           height: style == .lift || style == .glide || style == .orbit
                               ? CGFloat(remaining) * size * (style == .lift ? 0.48 : 0.20) : 0),
            turn: style == .orbit || style == .cutoutSlide ? remaining * direction * min(12, max(0, maxTurn)) : 0,
            scale: style == .pop || paper ? 0.76 + 0.24 * CGFloat(eased) : 1,
            fold: style == .unfold || style == .cutoutFold ? (paper ? -30 : -48) * remaining : 0,
            eased: eased)
    }

    // MARK: Before / after frames

    /// Before (p = 1) and after (p = 2) frames, written only when
    /// LUNALYR_PRESENCE_PREVIEW_DIR is set (pass it as
    /// TEST_RUNNER_LUNALYR_PRESENCE_PREVIEW_DIR). One sheet per layout: the
    /// top row at p = 1, the bottom at p = 2, through the in, hold and out.
    func testWriteBeforeAfterFrames() throws {
        guard let dir = ProcessInfo.processInfo.environment["LUNALYR_PRESENCE_PREVIEW_DIR"] else {
            throw XCTSkip("Set TEST_RUNNER_LUNALYR_PRESENCE_PREVIEW_DIR to write the frames")
        }
        try FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        let words = "we keep dreaming under neon rain".split(separator: " ").enumerated().map { index, text in
            LyricsFetcher.Word(text: String(text), start: 1 + Double(index) * 0.62, end: 1.55 + Double(index) * 0.62)
        }
        let exitStart = 5.2
        // In (mid-entrance of "dreaming" and "under"), hold, preview, out.
        let times: [Double] = [2.28, 2.36, 2.95, 3.6, 5.45, 5.6]
        let stage = CGSize(width: 640, height: 360)
        let layouts: [LyricComposition] = [.scattered, .spoken, .cascade, .hinge, .torque]
        for composition in layouts {
            let sheet = VStack(spacing: 2) {
                ForEach([1.0, 2.0], id: \.self) { presence in
                    HStack(spacing: 2) {
                        ForEach(times, id: \.self) { time in
                            ZStack {
                                LinearGradient(colors: [Color(hue: 0.62, saturation: 0.55, brightness: 0.32),
                                                        Color(hue: 0.62, saturation: 0.6, brightness: 0.08)],
                                               startPoint: .topLeading, endPoint: .bottomTrailing)
                                Group {
                                    if composition.fillsStage {
                                        StageLyricView(words: words, glue: [Bool](repeating: false, count: words.count),
                                                       time: time, stageSize: stage, baseSize: 46, mood: .neutral,
                                                       composition: composition, seed: 3, exitStart: exitStart)
                                    } else {
                                        LyricLineView(words: words, time: time, stageSize: stage, baseSize: 46,
                                                      mood: .neutral, composition: composition, seed: 3,
                                                      lineEnd: words.last.map { $0.end + 0.2 },
                                                      exitStart: composition.arrivalOnly ? .infinity : exitStart)
                                            .modifier(FittedLyricStage(available: stage, userScale: 1, neutral: 0.75))
                                    }
                                }
                                .frame(width: stage.width, height: stage.height)
                                Text(String(format: "%@  p=%.0f  %.2fs", composition.title, presence, time))
                                    .font(.system(size: 11)).foregroundStyle(.white.opacity(0.6))
                                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading).padding(6)
                            }
                            .frame(width: stage.width, height: stage.height)
                            .clipped()
                            .environment(\.lyricWordPresence, presence)
                        }
                    }
                }
            }
            .background(Color.white)
            .environment(\.colorScheme, .dark)
            .environment(\.lyricInk, LyricInk())
            .environment(\.lyricReduceMotion, false)
            let renderer = ImageRenderer(content: sheet)
            renderer.scale = 1
            let image = try XCTUnwrap(renderer.nsImage)
            let rep = try XCTUnwrap(NSBitmapImageRep(data: try XCTUnwrap(image.tiffRepresentation)))
            try XCTUnwrap(rep.representation(using: .png, properties: [:]))
                .write(to: URL(fileURLWithPath: dir + "/\(composition.rawValue).png"))
        }
    }
}
