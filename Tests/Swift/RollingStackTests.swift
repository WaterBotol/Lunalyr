import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// Spoken, Cascade and Cutout with romanisation: the word being sung, the
/// next-word preview and the words fading out must never run into each
/// other, whatever the script, face or size.
final class RollingStackTests: XCTestCase {
    private struct Lyric {
        let name: String
        let words: [(text: String, reading: String?)]
    }
    private let lyrics = [
        Lyric(name: "Japanese", words: [("夢見る", "yumemiru"), ("少女じゃ", "shoujo ja"), ("いられない", "irarenai"),
                                        ("抱きしめて", "dakishimete"), ("永遠に", "eien ni"), ("輝いて", "kagayaite")]),
        Lyric(name: "Korean", words: [("사랑했지만", "saranghaetjiman"), ("그대를", "geudaereul"), ("기억하며", "gieokhamyeo"),
                                      ("잊을게요", "ijeulgeyo"), ("영원히", "yeongwonhi")]),
        Lyric(name: "Latin", words: [("extraordinarily", nil), ("unforgettable", nil), ("everlasting", nil),
                                     ("love", nil), ("tonight", nil)]),
        // Romanisation set to "Replace": long Latin words stand in for the originals.
        Lyric(name: "Replace", words: [("yumemiru", nil), ("shoujoja", nil), ("irarenai", nil), ("dakishimete", nil)]),
    ]

    /// Whole glued phrases with their readings: the tallest, widest units.
    private let phrases = Lyric(name: "Phrases", words: [("夢見る少女じゃいられない", "yumemiru shoujo ja irarenai"),
        ("抱きしめて永遠に輝いて", "dakishimete eien ni kagayaite"), ("涙がこぼれる前に", "namida ga koboreru mae ni")])

    private func timed(_ lyric: Lyric) -> (words: [LyricsFetcher.Word], readings: [TimedWordKey: String]) {
        var words: [LyricsFetcher.Word] = [], readings: [TimedWordKey: String] = [:]
        for (index, item) in lyric.words.enumerated() {
            let start = 10 + Double(index) * 0.75
            let word = LyricsFetcher.Word(text: item.text, start: start, end: start + 0.65)
            words.append(word)
            if let reading = item.reading { readings[word.presentationID] = reading }
        }
        return (words, readings)
    }

    private func assertSeparated(_ placed: [RollingStack.Placed], _ context: String,
                                 file: StaticString = #filePath, line: UInt = #line) {
        let visible = placed.filter { $0.slot.shown > 0.05 }
        for (i, a) in visible.enumerated() {
            for b in visible[(i + 1)...] {
                let overlap = a.box.insetBy(dx: 0.5, dy: 0.5).intersection(b.box.insetBy(dx: 0.5, dy: 0.5))
                XCTAssertTrue(overlap.isNull || overlap.isEmpty,
                    "\(context): ranks \(a.slot.rank) and \(b.slot.rank) overlap by \(overlap.size)",
                    file: file, line: line)
            }
        }
    }

    /// The stage's fit for a unit as a share of what its width alone allows:
    /// below 1, the stage is shrinking it to keep it in vertically.
    private func legibility(size: CGSize, slot: RollingStack.Slot, centre: CGPoint, stage: CGSize,
                            margin: CGFloat) -> CGFloat {
        let fit = RollingStack.fit(size: size, slot: slot, centre: centre, stage: stage, margin: margin)
        let wide = RollingStack.fit(size: size, slot: slot, centre: CGPoint(x: centre.x, y: 1e6),
                                    stage: CGSize(width: stage.width, height: 2e6), margin: margin)
        return wide > 0 ? fit / wide : 0
    }

    /// The real stage, rendered: what the layout placed is what is checked.
    @MainActor
    func testPreviewCurrentAndFadingWordsNeverOverlapOnTheRealStage() throws {
        let stage = CGSize(width: 1210, height: 707)
        var placed: [RollingStack.Placed] = []
        RollingStackLayout.recorder = { placed = $0 }
        defer { RollingStackLayout.recorder = nil }
        var crowded = 0
        for composition in [LyricComposition.spoken, .cascade, .cutout] {
            for userScale in [0.7, 1.0, 1.5] {
                for lyric in lyrics {
                    let (words, readings) = timed(lyric)
                    let baseSize = 70 * 0.75 * CGFloat(userScale)
                    for step in 0...44 {
                        let time = 9.4 + Double(step) * 0.11
                        placed = []
                        let view = SpokenTypographyView(words: words, time: time, baseSize: baseSize, mood: .neutral,
                            composition: composition, seed: 3, accent: .mint, energy: 0, readings: readings,
                            stageHeight: stage.height)
                            .frame(width: stage.width, height: stage.height)
                        let renderer = ImageRenderer(content: view)
                        renderer.scale = 0.25
                        _ = renderer.cgImage
                        assertSeparated(placed, "\(composition.title) \(lyric.name) ×\(userScale) at \(time)")
                        if placed.filter({ $0.slot.shown > 0.05 }).count >= 3 { crowded += 1 }
                    }
                }
            }
        }
        XCTAssertGreaterThan(crowded, 200, "the preview, current and fading words were on stage together")
    }

    @MainActor
    func testReduceMotionDoesNotRenderTwoPositionsOfAWord() throws {
        let words = [
            LyricsFetcher.Word(text: "Hold", start: 10, end: 10.5),
            .init(text: "steady", start: 10.55, end: 11.2),
        ]
        var placed: [RollingStack.Placed] = []
        RollingStackLayout.recorder = { placed = $0 }
        defer { RollingStackLayout.recorder = nil }
        let renderer = ImageRenderer(content: SpokenTypographyView(words: words, time: 10.65, baseSize: 52,
            mood: .neutral, composition: .spoken, seed: 0, accent: .mint, energy: 0, stageHeight: 420)
            .environment(\.lyricReduceMotion, true)
            .frame(width: 600, height: 420))
        _ = try XCTUnwrap(renderer.cgImage)
        XCTAssertEqual(placed.count, words.count)
        XCTAssertEqual(Set(placed.map { $0.slot.rank }).count, words.count)
        XCTAssertTrue(placed.allSatisfy { $0.slot.rank == $0.slot.rank.rounded() })
    }

    /// Other faces: tall CJK, wide serif, heavy system. Sizes are measured
    /// from the same unit view the stage lays out.
    @MainActor
    func testStackingHoldsInEveryFaceAndAtEverySliderValue() throws {
        let faces: [(name: String, word: (CGFloat) -> Font, reading: (CGFloat) -> Font)] = [
            ("system heavy", { .system(size: $0, weight: .heavy) }, { .system(size: $0) }),
            ("Hiragino Mincho", { .custom("HiraMinProN-W6", fixedSize: $0) }, { .custom("HiraMinProN-W3", fixedSize: $0) }),
            ("Apple SD Gothic", { .custom("AppleSDGothicNeo-Heavy", fixedSize: $0) }, { .custom("AppleSDGothicNeo-Regular", fixedSize: $0) }),
            ("Georgia", { .custom("Georgia-Bold", fixedSize: $0) }, { .custom("Georgia", fixedSize: $0) }),
        ]
        for face in faces {
            for composition in [LyricComposition.spoken, .cascade, .cutout] {
                for userScale in [0.7, 1.0, 1.5] {
                    for lyric in lyrics {
                        let (words, readings) = timed(lyric)
                        let baseSize = 70 * 0.75 * CGFloat(userScale)
                        let prepared = SpokenTypography.prepare(.init(words: words, baseSize: baseSize, mood: .neutral,
                            composition: composition, fontIdentity: LyricFontIdentity(settings: .shared), readings: readings),
                            settings: .shared).map { token in
                                SpokenTypography.Word(index: token.index, source: token.source, hash: token.hash,
                                    seed: token.seed, cadence: token.cadence, rightToLeft: token.rightToLeft,
                                    hero: token.hero, pointSize: token.pointSize, tile: token.tile,
                                    font: face.word(token.pointSize),
                                    reading: token.reading.map { LyricReadingInk(text: $0.text, font: face.reading(token.pointSize * 0.56)) })
                            }
                        let sizes = try prepared.map { token -> CGSize in
                            let renderer = ImageRenderer(content: RollingUnit(token: token))
                            renderer.scale = 1
                            return try XCTUnwrap(renderer.nsImage).size
                        }
                        for step in 0...60 {
                          // Reduce Motion places each word once at its settled rank.
                          for reduce in [false, true] {
                            let time = 9.4 + Double(step) * 0.08
                            let cursor = SpokenTypographyView.cursor(prepared, time: time, intensity: 0, reduceMotion: reduce)
                            let position = reduce ? cursor.rounded() : cursor
                            let next = words.firstIndex { $0.start > time }
                            var units: [(size: CGSize, slot: RollingStack.Slot)] = []
                            for token in prepared {
                                let rank = position - 1 - Double(token.index)
                                guard rank >= -2, rank <= 5.2 else { continue }
                                let state = RollingTokenState.make(token, next: next, rank: rank, time: time,
                                    composition: composition, baseSize: baseSize, stageWidth: 1210, intensity: 0,
                                    reduceMotion: reduce, arrival: .automatic.resolved(for: composition),
                                    eligibleSince: token.index > 0 ? prepared[token.index - 1].source.start : -.infinity)
                                units.append((sizes[token.index], state.slot))
                            }
                            let centres = RollingStack.centres(sizes: units.map(\.size), slots: units.map(\.slot),
                                                               anchor: CGPoint(x: 605, y: 380), gap: baseSize * 0.16)
                            let placed = zip(units, centres).map { RollingStack.Placed(size: $0.size, slot: $0.slot, centre: $1) }
                            assertSeparated(placed, "\(face.name) \(composition.title) \(lyric.name) ×\(userScale)\(reduce ? " reduced" : "") at \(time)")
                          }
                        }
                    }
                }
            }
        }
    }

    func testTheStackRollsContinuously() {
        // Two settled words above, the word in focus, and one to come, as
        // the cursor moves through two onsets.
        let sizes = [CGSize(width: 300, height: 140), CGSize(width: 180, height: 90),
                     CGSize(width: 420, height: 160), CGSize(width: 220, height: 120)]
        func centres(_ position: Double) -> [CGPoint] {
            let slots = (0..<4).map { index -> RollingStack.Slot in
                let rank = position - 1 - Double(index)
                return RollingStack.Slot(rank: rank, scale: 0.7 + 0.34 * exp(-rank * rank * 2.4))
            }
            return RollingStack.centres(sizes: sizes, slots: slots, anchor: CGPoint(x: 500, y: 400), gap: 12)
        }
        var previous = centres(0)
        for step in 1...3000 {
            let now = centres(Double(step) / 1000)
            for (a, b) in zip(previous, now) {
                XCTAssertLessThan(abs(a.y - b.y), 1.5, "a word jumped at cursor \(Double(step) / 1000)")
            }
            previous = now
        }
        // The word in focus sits on the anchor.
        XCTAssertEqual(centres(2)[1].y, 400, accuracy: 0.001)
    }

    /// The bug as reported: with the old fixed spacing per rank, a word and
    /// its reading ran into the preview below and the word above.
    @MainActor
    func testTheOldFixedSpacingDidOverlapWithReadings() throws {
        let (words, readings) = timed(lyrics[0])
        let baseSize: CGFloat = 70 * 0.75
        let prepared = SpokenTypography.prepare(.init(words: words, baseSize: baseSize, mood: .neutral, composition: .spoken,
            fontIdentity: LyricFontIdentity(settings: .shared), readings: readings), settings: .shared)
        var overlapped = false
        for step in 0...40 {
            let time = 10.3 + Double(step) * 0.1
            let position = SpokenTypographyView.cursor(prepared, time: time, intensity: 0, reduceMotion: false)
            var boxes: [CGRect] = []
            for token in prepared {
                let rank = position - 1 - Double(token.index)
                guard rank >= -1.2, rank <= 1.5 else { continue }
                let renderer = ImageRenderer(content: RollingUnit(token: token))
                renderer.scale = 1
                let size = try XCTUnwrap(renderer.nsImage).size
                let pose = vc_rolling_pose(0, rank, time - token.source.start, 0.65, token.seed, 0, 1)
                let placed = RollingStack.Placed(size: size, slot: .init(rank: rank, scale: pose.scale),
                                                 centre: CGPoint(x: 0, y: CGFloat(pose.y) * baseSize))
                boxes.append(placed.box)
            }
            for (i, a) in boxes.enumerated() where boxes[(i + 1)...].contains(where: { $0.intersects(a.insetBy(dx: 1, dy: 1)) }) {
                overlapped = true
            }
        }
        XCTAssertTrue(overlapped, "the regression tests must be able to see this bug")
    }

    // MARK: Findings from the adversarial review

    /// Every entrance style -- including Orbit and Cutout-Slide, whose 12°
    /// turn a preview shows in full -- with phrase-length glued units and
    /// Cutout labels. Boxes are as drawn: travel, turn, scale, label tilt
    /// and the stage's fit.
    @MainActor
    func testEveryEntranceStyleStaysClearAsDrawn() throws {
        let runs = Lyric(name: "Replace runs", words: [("dakishimeteeiennikagayaite", nil), ("namidagakoborerumaeni", nil),
            ("yumemirushoujojairarenai", nil)])
        let styles: [WordArrival] = [.fade, .lift, .pop, .orbit, .glide, .unfold, .cutoutFold, .cutoutSlide,
                                     .satinRibbon, .splitInk]
        let stage = CGSize(width: 1210, height: 707)
        var crowded = 0
        defer { XCTAssertGreaterThan(crowded, 1000, "three or more words were on stage together") }
        for style in styles {
            for composition in [LyricComposition.spoken, .cascade, .cutout] {
                for userScale in [0.7, 1.5] {
                    for lyric in [phrases, runs, lyrics[0]] {
                        let (words, readings) = timed(lyric)
                        let baseSize = 70 * 0.75 * CGFloat(userScale)
                        let prepared = SpokenTypography.prepare(.init(words: words, baseSize: baseSize, mood: .neutral,
                            composition: composition, fontIdentity: LyricFontIdentity(settings: .shared), readings: readings),
                            settings: .shared)
                        let sizes = try prepared.map { token -> CGSize in
                            let renderer = ImageRenderer(content: RollingUnit(token: token))
                            renderer.scale = 1
                            return try XCTUnwrap(renderer.nsImage).size
                        }
                        for step in 0...50 {
                            let time = 9.4 + Double(step) * 0.07
                            let position = SpokenTypographyView.cursor(prepared, time: time, intensity: 0, reduceMotion: false)
                            let next = words.firstIndex { $0.start > time }
                            var units: [(size: CGSize, slot: RollingStack.Slot)] = []
                            var drawnSlots: [RollingStack.Slot] = []
                            for token in prepared {
                                let rank = position - 1 - Double(token.index)
                                guard rank >= -2, rank <= 5.2 else { continue }
                                let state = RollingTokenState.make(token, next: next, rank: rank, time: time,
                                    composition: composition, baseSize: baseSize, stageWidth: stage.width, intensity: 0,
                                    reduceMotion: false, arrival: style,
                                    eligibleSince: token.index > 0 ? prepared[token.index - 1].source.start : -.infinity)
                                units.append((sizes[token.index], state.slot))
                                // What WordEntrance will actually draw, worked out on its
                                // own -- not taken from the slot the stack was given.
                                let drawn = WordEntrance.transform(progress: state.presentation.progress,
                                    arrival: state.presentation.arrival, style: style, size: token.pointSize,
                                    direction: token.hash % 2 == 0 ? 1 : -1, reduceMotion: false,
                                    maxTurn: token.turnLimit)
                                var slot = state.slot
                                slot.entrance = drawn.offset; slot.turn = drawn.turn; slot.entranceScale = drawn.scale
                                slot.tilt = token.tile ? 1.2 : 0
                                drawnSlots.append(slot)
                            }
                            let anchor = CGPoint(x: stage.width / 2, y: stage.height * 0.54
                                + CGFloat(vc_rolling_baseline(composition == .spoken ? 0 : composition == .cascade ? 1 : 2, 0)) * baseSize)
                            let centres = RollingStack.centres(sizes: units.map(\.size), slots: units.map(\.slot),
                                                               anchor: anchor, gap: baseSize * 0.16)
                            let placed = zip(zip(units, drawnSlots), centres).map { pair, centre in
                                RollingStack.Placed(size: pair.0.size, slot: pair.1, centre: centre,
                                    fit: RollingStack.fit(size: pair.0.size, slot: pair.1, centre: centre, stage: stage,
                                                          margin: baseSize * 0.22))
                            }
                            if placed.filter({ $0.slot.shown > 0.05 }).count >= 3 { crowded += 1 }
                            assertSeparated(placed, "\(style.title) \(composition.title) \(lyric.name) ×\(userScale) at \(time)")
                            // Kept clear by room, not by shrinking: the word in
                            // focus is never shrunk, and at ordinary sizes neither
                            // is the next word -- its faint preview included. (At
                            // the largest sizes a tall next word still is: see
                            // testATallNextWordIsStillShrunkAtLargeSizes.)
                            let context = "\(style.title) \(composition.title) \(lyric.name) ×\(userScale) at \(time)"
                            for unit in placed {
                                let share = legibility(size: unit.size, slot: unit.slot, centre: unit.centre, stage: stage,
                                                       margin: baseSize * 0.22)
                                if unit.slot.shown > 0.5 && abs(unit.slot.rank) <= 0.25 {
                                    XCTAssertGreaterThanOrEqual(share, 0.95, "\(context): the word in focus shrunk")
                                }
                                if userScale <= 1, unit.slot.shown > 0.02, unit.slot.rank > -1.3, unit.slot.rank < -0.25 {
                                    XCTAssertGreaterThanOrEqual(share, 0.95, "\(context): the next word shrunk at rank \(unit.slot.rank)")
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    /// With translations on, the rolling stage keeps its foot clear: no
    /// word, preview or entrance reaches the translation band.
    @MainActor
    func testRollingWordsKeepClearOfTheTranslationBand() throws {
        let stage = CGSize(width: 1536, height: 777.6)
        var placed: [RollingStack.Placed] = []
        RollingStackLayout.recorder = { placed = $0 }
        defer { RollingStackLayout.recorder = nil }
        for composition in [LyricComposition.spoken, .cascade, .cutout] {
            for userScale in [1.0, 1.25, 1.5] {
              for lyric in [lyrics[0], phrases] {
                let (words, readings) = timed(lyric)
                let base = 88.3 * CGFloat(userScale)
                let baseSize = base * 0.75
                let band = base * 0.30 * 2.7 + 88.3 * 0.3
                for step in 0...44 {
                    let time = 9.4 + Double(step) * 0.11
                    placed = []
                    let view = SpokenTypographyView(words: words, time: time, baseSize: baseSize, mood: .neutral,
                        composition: composition, seed: 3, accent: .mint, energy: 0, readings: readings,
                        stageHeight: stage.height, bottomInset: band)
                        .frame(width: stage.width, height: stage.height)
                    let renderer = ImageRenderer(content: view)
                    renderer.scale = 0.25
                    _ = renderer.cgImage
                    let free = CGSize(width: stage.width, height: stage.height - band)
                    for unit in placed where unit.slot.shown > 0.05 {
                        let drawn = RollingStack.Placed(size: unit.size, slot: unit.slot, centre: unit.centre,
                            fit: RollingStack.fit(size: unit.size, slot: unit.slot, centre: unit.centre, stage: free,
                                                  margin: baseSize * 0.22))
                        let context = "\(composition.title) \(lyric.name) ×\(userScale) at \(time): rank \(unit.slot.rank)"
                        // A unit the stage has shrunk to nothing draws nothing.
                        // (How far a tall next word is shrunk here is pinned by
                        // testATallNextWordIsStillShrunkAtLargeSizes.)
                        guard drawn.fit > 0 else { continue }
                        XCTAssertLessThanOrEqual(drawn.box.maxY, free.height + 0.5, "\(context) reaches the translation")
                    }
                }
              }
            }
        }
    }

    /// The window's last word keeps its pace when the next line's first word
    /// joins the window, so the stack does not lurch back at a phrase end.
    @MainActor
    func testTheStackDoesNotLurchWhenTheNextLineJoins() {
        let first = LyricsFetcher.Line(start: 10, end: 11, text: "hold on", words: [
            .init(text: "hold", start: 10, end: 10.6), .init(text: "on", start: 10.6, end: 10.9),
        ], isInstrumental: false, wordTimed: true)
        let second = LyricsFetcher.Line(start: 12.1, end: 13, text: "tonight", words: [
            .init(text: "tonight", start: 12.1, end: 12.9),
        ], isInstrumental: false, wordTimed: true)
        let lines = [first, second]
        func cursor(_ time: Double) -> Double {
            let window = LyricLayerCache.rollingWindow(around: 0, lines: lines, romanized: [:], time: time, lead: 1.2,
                                                       display: .off)
            let prepared = SpokenTypography.prepare(.init(words: window.words, baseSize: 50, mood: .neutral,
                composition: .cascade, fontIdentity: LyricFontIdentity(settings: .shared), following: window.following),
                settings: .shared)
            return SpokenTypographyView.cursor(prepared, time: time, intensity: 0, reduceMotion: false)
        }
        // "tonight" joins the window at 12.1 - 1.2 = 10.9, while "on" is
        // still handing over.
        var previous = cursor(10.7)
        for step in 1...600 {
            let now = cursor(10.7 + Double(step) / 1000)
            XCTAssertLessThan(abs(now - previous), 0.01, "the stage jumped at \(10.7 + Double(step) / 1000)")
            previous = now
        }
    }

    /// Neighbours ease back as a held word's entrance lands; the room they
    /// left for it is not dropped in one frame when its reveal completes.
    @MainActor
    func testRoomForAnEntranceShrinksSmoothly() throws {
        let words: [LyricsFetcher.Word] = [.init(text: "hold", start: 9, end: 9.6), .init(text: "me", start: 9.6, end: 10),
                                           .init(text: "forever", start: 10, end: 11.8), .init(text: "now", start: 11.8, end: 12.2)]
        let prepared = SpokenTypography.prepare(.init(words: words, baseSize: 60, mood: .neutral, composition: .cascade,
            fontIdentity: LyricFontIdentity(settings: .shared)), settings: .shared)
        let sizes = try prepared.map { token -> CGSize in
            let renderer = ImageRenderer(content: RollingUnit(token: token))
            renderer.scale = 1
            return try XCTUnwrap(renderer.nsImage).size
        }
        var previous: [CGPoint]?
        for step in 0...1200 {
            let time = 10 + Double(step) / 1000
            let position = SpokenTypographyView.cursor(prepared, time: time, intensity: 0, reduceMotion: false)
            let slots = prepared.map { token in
                RollingTokenState.make(token, next: words.firstIndex { $0.start > time }, rank: position - 1 - Double(token.index),
                    time: time, composition: .cascade, baseSize: 60, stageWidth: 1200, intensity: 0, reduceMotion: false,
                    arrival: .lift).slot
            }
            let centres = RollingStack.centres(sizes: sizes, slots: slots, anchor: CGPoint(x: 600, y: 380), gap: 9.6)
            if let previous {
                for (a, b) in zip(previous, centres) { XCTAssertLessThan(abs(a.y - b.y), 1.2, "jump at \(time)") }
            }
            previous = centres
        }
    }

    /// Old words leave the window, but never while the stack is anchored on
    /// them: everything still on stage is exactly where it would be if no
    /// word were ever dropped -- through a long break, while the next word
    /// previews, and as it takes over. (A preview used to jump when the last
    /// word sung expired twelve seconds after it ended.)
    @MainActor
    func testDroppingOldWordsNeverMovesTheWordsOnStage() {
        let first = LyricsFetcher.Line(start: 10, end: 11.6, text: "hold on to me", words: [
            .init(text: "hold", start: 10, end: 10.4), .init(text: "on", start: 10.4, end: 10.8),
            .init(text: "to", start: 10.8, end: 11.1), .init(text: "me", start: 11.1, end: 11.6),
        ], isInstrumental: false, wordTimed: true)
        let second = LyricsFetcher.Line(start: 27.6, end: 29.4, text: "tonight forever", words: [
            .init(text: "tonight", start: 27.6, end: 28.4), .init(text: "forever", start: 28.4, end: 29.4),
        ], isInstrumental: false, wordTimed: true)
        let lines = [first, second]
        let anchor = CGPoint(x: 600, y: 700 * 0.54 + CGFloat(vc_rolling_baseline(1, 0)) * 50)
        // "me" carries a reading, so it is twice as tall as the rest.
        func size(_ word: LyricsFetcher.Word) -> CGSize {
            CGSize(width: 30 * CGFloat(word.text.count) + 20, height: word.text == "me" ? 140 : 70)
        }
        func stage(_ words: [LyricsFetcher.Word], following: Double?,
                   time: Double) -> [TimedWordKey: (centre: CGPoint, shown: Double, rank: Double)] {
            let prepared = SpokenTypography.prepare(.init(words: words, baseSize: 50, mood: .neutral,
                composition: .cascade, fontIdentity: LyricFontIdentity(settings: .shared), following: following),
                settings: .shared)
            let position = SpokenTypographyView.cursor(prepared, time: time, intensity: 0, reduceMotion: false)
            let next = words.firstIndex { $0.start > time }
            var sizes: [CGSize] = [], states: [RollingTokenState] = [], keys: [TimedWordKey] = []
            for token in prepared {
                let rank = position - 1 - Double(token.index)
                guard rank >= -2, rank <= 5.2 else { continue }
                states.append(RollingTokenState.make(token, next: next, rank: rank, time: time, composition: .cascade,
                    baseSize: 50, stageWidth: 1200, intensity: 0, reduceMotion: false, arrival: .lift,
                    eligibleSince: token.index > 0 ? prepared[token.index - 1].source.start : -.infinity))
                sizes.append(size(token.source))
                keys.append(token.source.presentationID)
            }
            let centres = RollingStack.centres(sizes: sizes, slots: states.map(\.slot), anchor: anchor, gap: 8)
            return Dictionary(uniqueKeysWithValues: zip(keys, zip(centres, states)).map {
                ($0, ($1.0, $1.1.shown, $1.1.slot.rank))
            })
        }
        var compared = 0
        for step in 0...(10 * 60) {
            let time = 21.5 + Double(step) / 60
            let index = time < second.start ? 0 : 1
            let window = LyricLayerCache.rollingWindow(around: index, lines: lines, romanized: [:], time: time,
                                                       lead: 1.2, display: .off)
            let drawn = stage(window.words, following: window.following, time: time)
            let ideal = stage(first.words + second.words, following: nil, time: time)
            // Every word in the window -- shown or not, so this does not
            // depend on the preview settings -- is where it would be.
            for (key, unit) in drawn {
                guard let reference = ideal[key] else { XCTFail("an unknown word at \(time)"); continue }
                XCTAssertLessThan(abs(unit.centre.y - reference.centre.y), 0.5, "a word moved at \(time)")
                XCTAssertEqual(unit.shown, reference.shown, accuracy: 1e-9, "a word changed at \(time)")
                if unit.rank > -1.3 && unit.rank < 0.3 { compared += 1 }
            }
            // Nothing that would be on stage is missing.
            for (key, unit) in ideal where unit.shown > 0.01 {
                XCTAssertNotNil(drawn[key], "a word on stage was dropped at \(time)")
            }
        }
        XCTAssertGreaterThan(compared, 300, "the next word and the new line must be on stage")
    }

    /// Rolling words carry over between rolling lines, then leave before
    /// the whole-phrase renderer takes the stage.
    @MainActor
    func testARollingLineHandsItsWordsOnInsteadOfLeavingTwice() {
        let a = LyricsFetcher.Line(start: 10, end: 11, text: "hold on", words: [
            .init(text: "hold", start: 10, end: 10.5), .init(text: "on", start: 10.5, end: 11),
        ], isInstrumental: false, wordTimed: true)
        let b = LyricsFetcher.Line(start: 11, end: 11.4, text: "now", words: [.init(text: "now", start: 11, end: 11.4)],
                                   isInstrumental: false, wordTimed: true)
        let c = LyricsFetcher.Line(start: 11.4, end: 13, text: "we fly tonight", words: [
            .init(text: "we fly tonight", start: 11.4, end: 13),
        ], isInstrumental: false, wordTimed: false)
        let lines = [a, b, c]
        let rolls = { (row: Int) in lines[row].wordTimed }
        for step in 0...120 {
            let time = 11.4 + Double(step) / 100
            for row in 0...1 {
                XCTAssertFalse(LyricLayer.isLeaving(row, current: 2, lines: lines, time: time, rolls: rolls),
                               "the rolling stage reappeared at \(time)")
            }
        }
        XCTAssertEqual(LyricLayer.timingHandoffPresence(after: 0, in: lines, time: 11.1), 1)
        XCTAssertEqual(LyricLayer.timingHandoffPresence(after: 1, in: lines, time: 11.1), 1)
        XCTAssertEqual(LyricLayer.timingHandoffPresence(after: 1, in: lines, time: 11.31), 0.5, accuracy: 0.001)
        XCTAssertEqual(LyricLayer.timingHandoffPresence(after: 1, in: lines, time: 11.4), 0)
        XCTAssertFalse(LyricLayer.isLeaving(0, current: 1, lines: lines, time: 11.1, rolls: rolls))
        // A current rolling stage carries on into the next rolling line; an
        // outgoing one always fades on its own exit.
        XCTAssertEqual(LyricLayer.rollingPresence(nextRolls: true, outgoing: false, exit: 0.4), 1)
        XCTAssertEqual(LyricLayer.rollingPresence(nextRolls: true, outgoing: true, exit: 0.4), 0.6, accuracy: 1e-12)
        XCTAssertEqual(LyricLayer.rollingPresence(nextRolls: false, outgoing: false, exit: 0.4), 0.6, accuracy: 1e-12)
    }

    /// Very short lines: every word still on stage stays in the window as
    /// the active line changes. (The window looked back only two lines, so
    /// words still fading out vanished at the switch.)
    @MainActor
    func testWordsStillOnStageStayInTheWindowAcrossShortLines() {
        let lines = (0..<8).map { k -> LyricsFetcher.Line in
            let start = 10 + Double(k) * 0.3
            return LyricsFetcher.Line(start: start, end: start + 0.28, text: "w\(k)",
                words: [.init(text: "w\(k)", start: start, end: start + 0.25)], isInstrumental: false, wordTimed: true)
        }
        let everything = lines.flatMap(\.words)
        let prepared = SpokenTypography.prepare(.init(words: everything, baseSize: 50, mood: .neutral,
            composition: .cascade, fontIdentity: LyricFontIdentity(settings: .shared)), settings: .shared)
        var checked = 0
        for step in 0...260 {
            let time = 10 + Double(step) / 100
            let index = lines.lastIndex { $0.start <= time } ?? 0
            let window = Set(LyricLayerCache.rollingWindow(around: index, lines: lines, romanized: [:], time: time,
                                                           lead: 1.2, display: .off).words.map(\.presentationID))
            let position = SpokenTypographyView.cursor(prepared, time: time, intensity: 0, reduceMotion: false)
            for token in prepared where token.source.start <= time {
                let state = RollingTokenState.make(token, next: everything.firstIndex { $0.start > time },
                    rank: position - 1 - Double(token.index), time: time, composition: .cascade, baseSize: 50,
                    stageWidth: 1200, intensity: 0, reduceMotion: false, arrival: .lift)
                guard state.shown > 0.02 else { continue }
                XCTAssertTrue(window.contains(token.source.presentationID), "\(token.source.text) vanished at \(time)")
                checked += 1
            }
        }
        XCTAssertGreaterThan(checked, 500)
    }

    /// Before anything is sung, the first word of a run waits one plain step
    /// below the focus: its entrance's turn and travel do not push it
    /// further down (they used to, by its whole turned height, which sent a
    /// long first phrase to the foot of the stage).
    func testTheFirstWordWaitsOnePlainStepBelowTheFocus() {
        var slot = RollingStack.Slot(rank: -1, entrance: CGSize(width: 0, height: 30), turn: 12, entranceScale: 1.1)
        let size = CGSize(width: 400, height: 80)
        let anchor = CGPoint(x: 600, y: 300)
        var centre = RollingStack.centres(sizes: [size], slots: [slot], anchor: anchor, gap: 10)[0]
        XCTAssertEqual(centre.y, anchor.y + 80 + 10, accuracy: 0.001)
        // Halfway there, halfway down; at the focus, on it.
        slot.rank = -0.5
        centre = RollingStack.centres(sizes: [size], slots: [slot], anchor: anchor, gap: 10)[0]
        XCTAssertEqual(centre.y, anchor.y + 45, accuracy: 0.001)
        slot.rank = 0
        centre = RollingStack.centres(sizes: [size], slots: [slot], anchor: anchor, gap: 10)[0]
        XCTAssertEqual(centre.y, anchor.y, accuracy: 0.001)
    }

    /// A wide label turns only a few degrees on its way in -- its ends swing
    /// by about a third of its height -- while a short word keeps the full
    /// turn. Worked out once per word, when the words are prepared.
    @MainActor
    func testWideLabelsTurnLess() throws {
        func token(_ text: String, reading: String? = nil) -> SpokenTypography.Word {
            let word = LyricsFetcher.Word(text: text, start: 10, end: 10.5)
            return SpokenTypography.prepare(.init(words: [word], baseSize: 66, mood: .neutral, composition: .cutout,
                fontIdentity: LyricFontIdentity(settings: .shared),
                readings: reading.map { [word.presentationID: $0] } ?? [:]), settings: .shared)[0]
        }
        XCTAssertEqual(token("love").turnLimit, 12)
        XCTAssertEqual(token("夢見る", reading: "yumemiru").turnLimit, 12)
        XCTAssertEqual(token("tonight").turnLimit, 10, accuracy: 1.5)
        for (text, reading) in [("夢見る少女じゃいられない", "yumemiru shoujo ja irarenai"),
                                ("dakishimeteeiennikagayaite", nil)] {
            let phrase = token(text, reading: reading)
            XCTAssertLessThan(phrase.turnLimit, 8, text)
            let renderer = ImageRenderer(content: RollingUnit(token: phrase))
            renderer.scale = 1
            let size = try XCTUnwrap(renderer.nsImage).size
            let radians = phrase.turnLimit * .pi / 180
            let turned = size.height * CGFloat(cos(radians)) + size.width * CGFloat(sin(radians))
            XCTAssertLessThan(turned, size.height * 1.8, "\(text) swings by more than its own height")
        }
    }

    /// Known limit, handed over: at the largest sizes -- sooner with a
    /// translation band at the foot -- the word in focus and a tall next word
    /// (a glued phrase with its reading, or an entrance that travels, such
    /// as Orbit or Float up) do not both fit below the focus, so the stage
    /// shrinks the next word until its onset and it zooms in as it is sung.
    /// This pins that behaviour; when it is fixed the expected failure is
    /// reported as missing, and this wrapper should go.
    @MainActor
    func testATallNextWordIsStillShrunkAtLargeSizes() throws {
        var worst: CGFloat = 1
        let cases: [(stage: CGSize, band: CGFloat, base: CGFloat, composition: LyricComposition, style: WordArrival)] = [
            (CGSize(width: 1210, height: 707), 0, 70 * 1.5, .cutout, .orbit),
            (CGSize(width: 1536, height: 777.6), 88.3 * 1.25 * 0.30 * 2.7 + 88.3 * 0.3, 88.3 * 1.25, .cascade, .lift),
        ]
        for item in cases {
            let (words, readings) = timed(phrases)
            let baseSize = item.base * 0.75
            let prepared = SpokenTypography.prepare(.init(words: words, baseSize: baseSize, mood: .neutral,
                composition: item.composition, fontIdentity: LyricFontIdentity(settings: .shared), readings: readings),
                settings: .shared)
            let sizes = try prepared.map { token -> CGSize in
                let renderer = ImageRenderer(content: RollingUnit(token: token))
                renderer.scale = 1
                return try XCTUnwrap(renderer.nsImage).size
            }
            let free = CGSize(width: item.stage.width, height: item.stage.height - item.band)
            let anchor = CGPoint(x: free.width / 2, y: free.height * 0.54
                + CGFloat(vc_rolling_baseline(item.composition == .cascade ? 1 : 2, 0)) * baseSize)
            for step in 0...300 {
                let time = 9.2 + Double(step) / 100
                let position = SpokenTypographyView.cursor(prepared, time: time, intensity: 0, reduceMotion: false)
                let next = words.firstIndex { $0.start > time }
                var units: [(size: CGSize, slot: RollingStack.Slot)] = []
                for token in prepared {
                    let rank = position - 1 - Double(token.index)
                    guard rank >= -2, rank <= 5.2 else { continue }
                    let state = RollingTokenState.make(token, next: next, rank: rank, time: time,
                        composition: item.composition, baseSize: baseSize, stageWidth: free.width, intensity: 0,
                        reduceMotion: false, arrival: item.style,
                        eligibleSince: token.index > 0 ? prepared[token.index - 1].source.start : -.infinity)
                    units.append((sizes[token.index], state.slot))
                }
                let centres = RollingStack.centres(sizes: units.map(\.size), slots: units.map(\.slot), anchor: anchor,
                                                   gap: baseSize * 0.16)
                for (unit, centre) in zip(units, centres)
                where unit.slot.shown > 0.02 && unit.slot.rank > -1.3 && unit.slot.rank < -0.25 {
                    worst = min(worst, legibility(size: unit.size, slot: unit.slot, centre: centre, stage: free,
                                                  margin: baseSize * 0.22))
                }
            }
        }
        XCTExpectFailure("Known limit, handed over: a tall next word is shrunk at the largest sizes") {
            XCTAssertGreaterThanOrEqual(worst, 0.9)
        }
    }
}
