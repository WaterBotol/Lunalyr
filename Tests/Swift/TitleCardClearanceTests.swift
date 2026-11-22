import XCTest
@testable import VersoCustom

/// Translations at the foot of the lyric stage keep clear of the song's
/// kept title card (`OpeningTitle.stageFootInset`, `LyricLineStack.footLift`),
/// and the translation slot at the screen's edge shows one translation per
/// line, the latest taking over (`EdgeTranslations`).
@MainActor
final class TitleCardClearanceTests: XCTestCase {
    /// A 1280 x 720 desktop as `LyricViewportMetrics` lays it out.
    private let layer = CGSize(width: 1280, height: 720)
    private var metrics: LyricViewportMetrics { LyricViewportMetrics(size: layer) }
    private var stageBox: CGRect {
        let stage = metrics.stageSize
        return CGRect(x: (layer.width - stage.width) / 2, y: (layer.height - stage.height) / 2,
                      width: stage.width, height: stage.height)
    }

    private func inset(_ placement: OpeningTitle.Placement) -> CGFloat {
        OpeningTitle.stageFootInset(layer: layer, stage: metrics.stageSize, baseSize: metrics.baseSize,
                                    placement: placement, span: LyricLayer.translationWidth)
    }

    // MARK: The card's claim on the stage's foot

    /// In its shipped place, low on the left, the card reaches up into the
    /// stage: a translation there keeps above the card's box and its glow.
    func testTheStandardPlaceClaimsTheStagesFoot() {
        let placement = OpeningTitle.Placement.standard
        let box = OpeningTitle.box(in: layer, baseSize: metrics.baseSize, placement: placement)
        let gap = metrics.baseSize * CGFloat(placement.scale) * OpeningTitle.glowClearance
        XCTAssertLessThan(box.minY, stageBox.maxY, "the shipped place reaches into the stage")
        let claimed = inset(placement)
        XCTAssertEqual(claimed, stageBox.maxY - box.minY + gap, accuracy: 1e-6)
        XCTAssertGreaterThan(claimed, metrics.baseSize * 0.5, "more than the edge translation's own margin")
        // A translation's foot at the inset sits above the card's top and its glow.
        XCTAssertLessThanOrEqual(stageBox.maxY - claimed, box.minY - gap + 1e-6)
        // The place computed from the screen (installs from before the shipped place) does too.
        XCTAssertGreaterThan(inset(OpeningTitle.Placement(corner: nil, scale: 1)), 0)
    }

    /// Dragged to the top of the screen, the card leaves the foot alone.
    func testACardAtTheTopClaimsNothing() {
        XCTAssertEqual(inset(OpeningTitle.Placement(corner: CGPoint(x: 0.03, y: 0.75), scale: 1)), 0)
        XCTAssertEqual(inset(OpeningTitle.Placement(corner: CGPoint(x: 0.5, y: 0.7), scale: 0.6)), 0)
    }

    /// Off to the side of where translations run, or below the stage by
    /// more than the glow, the card claims nothing; dragged up into the
    /// middle, it never claims more than its share of the stage.
    func testTheCardsBoxDecides() {
        let stage = metrics.stageSize
        let span = stage.width * LyricLayer.translationWidth
        func claim(_ card: CGRect) -> CGFloat {
            OpeningTitle.stageFootInset(card: card, layer: layer, stage: stage, span: span, gap: 10)
        }
        let low = stageBox.maxY - 60
        // Beside the translations' span, left and right.
        XCTAssertEqual(claim(CGRect(x: 0, y: low, width: stageBox.midX - span / 2 - 1, height: 100)), 0)
        XCTAssertEqual(claim(CGRect(x: stageBox.midX + span / 2 + 1, y: low, width: 100, height: 100)), 0)
        // Just inside it.
        XCTAssertEqual(claim(CGRect(x: 0, y: low, width: stageBox.midX - span / 2 + 1, height: 100)), 70, accuracy: 1e-6)
        // Below the stage's foot: within the glow's reach, a little; beyond it, nothing.
        XCTAssertEqual(claim(CGRect(x: 100, y: stageBox.maxY + 4, width: 300, height: 80)), 6, accuracy: 1e-6)
        XCTAssertEqual(claim(CGRect(x: 100, y: stageBox.maxY + 20, width: 300, height: 80)), 0)
        // In the middle of the stage's lower half: capped.
        XCTAssertEqual(claim(CGRect(x: 100, y: stageBox.midY + 1, width: 300, height: stageBox.height / 2)),
                       stage.height * OpeningTitle.footShare, accuracy: 1e-6)
        // No card.
        XCTAssertEqual(claim(.zero), 0)
    }

    // MARK: A line with its translation under it

    func testALineRisesOnlyAsFarAsItsFootReachesTheCard() {
        // Clear of it already, or nothing to keep clear of: where it was.
        XCTAssertEqual(LyricLineStack.footLift(extent: 200, clearance: 80, stageHeight: 500), 0)
        XCTAssertEqual(LyricLineStack.footLift(extent: 480, clearance: 0, stageHeight: 500), 0)
        // Reaching 30 into it: up by 30.
        XCTAssertEqual(LyricLineStack.footLift(extent: 400, clearance: 80, stageHeight: 500), 30, accuracy: 1e-9)
        // Fitted to the stage less the clearance: up by half the clearance, top at the stage's top.
        XCTAssertEqual(LyricLineStack.footLift(extent: 420, clearance: 80, stageHeight: 500), 40, accuracy: 1e-9)
        // Too tall to rise without leaving the stage: never past its top.
        XCTAssertEqual(LyricLineStack.footLift(extent: 500, clearance: 80, stageHeight: 500), 0)
    }

    func testTheStackLiftsTheLineAndAHeldLineStaysAboveIt() {
        let fit = LyricLineStack.Fit(available: CGSize(width: 1200, height: 420), userScale: 1, neutral: 1)
        let clear = LyricLineStack.Item(size: CGSize(width: 1000, height: 400),
                                        line: .init(fit: fit, presence: 1, footClearance: 80), held: nil)
        let held = LyricLineStack.Item(size: CGSize(width: 1000, height: 50),
                                       line: .init(fit: fit, presence: 1), held: 1)
        let placed = LyricLineStack.offsets([held, clear], stageHeight: 500, gap: 10)
        XCTAssertEqual(placed[1], -30, accuracy: 1e-9)
        // The held line keeps to the top of the stage, above the lifted line.
        XCTAssertEqual(placed[0], -250 + 50 * (1 - LyricLineStack.heldShrink) / 2, accuracy: 1e-9)
        // Without the clearance, the same line stays centred.
        let plain = LyricLineStack.Item(size: CGSize(width: 1000, height: 400), line: .init(fit: fit, presence: 1),
                                        held: nil)
        XCTAssertEqual(LyricLineStack.offsets([plain], stageHeight: 500, gap: 10), [0])
    }

    /// A lifted line that becomes held (a note ringing on into the next
    /// line) moves aside from where it was, not from the middle.
    func testAHeldLineSetsOffFromWhereItWasLifted() {
        let fit = LyricLineStack.Fit(available: CGSize(width: 1200, height: 420), userScale: 1, neutral: 1)
        func lifted(held: Double?) -> LyricLineStack.Item {
            .init(size: CGSize(width: 1000, height: 400), line: .init(fit: fit, presence: 1, footClearance: 80), held: held)
        }
        let next = LyricLineStack.Item(size: CGSize(width: 1000, height: 100), line: .init(fit: fit, presence: 1),
                                       held: nil)
        let current = LyricLineStack.offsets([lifted(held: nil)], stageHeight: 500, gap: 10)[0]
        XCTAssertEqual(current, -30, accuracy: 1e-9)
        // Held, before it has moved (and its Reduce Motion twin left in place).
        XCTAssertEqual(LyricLineStack.offsets([lifted(held: 0), next], stageHeight: 500, gap: 10)[0], current,
                       accuracy: 1e-9)
        // Part-way, between there and where a held line goes.
        let there = LyricLineStack.offsets([lifted(held: 1), next], stageHeight: 500, gap: 10)[0]
        let half = LyricLineStack.offsets([lifted(held: 0.5), next], stageHeight: 500, gap: 10)[0]
        XCTAssertLessThan(there, current)
        XCTAssertLessThan(half, current)
        XCTAssertGreaterThan(half, -250)
    }

    // MARK: The translation slot at the screen's edge

    private func item(_ id: Int, _ opacity: Double, top: Bool = false) -> EdgeTranslations.Item {
        EdgeTranslations.Item(id: id, text: "line \(id)", top: top, opacity: opacity, size: 20)
    }

    /// A held line drawn twice under Reduce Motion hands up its translation
    /// twice: the slot shows it once.
    func testOneTranslationPerLine() {
        let shown = EdgeTranslations.slot([item(3, 0.6), item(3, 0.6), item(3, 0.2)])
        XCTAssertEqual(shown.map(\.id), [3])
        XCTAssertEqual(shown[0].opacity, 0.6, accuracy: 1e-9)
    }

    /// The latest line's translation at its own strength; an earlier one only
    /// as far as the later ones leave room, gone once a later one is in.
    func testTheLatestLineTakesTheSlotOver() {
        let arriving = EdgeTranslations.slot([item(2, 1), item(3, 0.3)])
        XCTAssertEqual(arriving.map(\.id), [2, 3])
        XCTAssertEqual(arriving[0].opacity, 0.7, accuracy: 1e-9)
        XCTAssertEqual(arriving[1].opacity, 0.3, accuracy: 1e-9)
        XCTAssertEqual(EdgeTranslations.slot([item(2, 1), item(3, 1)]).map(\.id), [3])
        // Three lines: the oldest gives way to either later one.
        let three = EdgeTranslations.slot([item(1, 1), item(2, 0.9), item(3, 0.2)])
        XCTAssertEqual(three.map(\.id), [2, 3])
        XCTAssertEqual(three[0].opacity, 0.8, accuracy: 1e-9)
        // A later line's translation still on its way in does not lift an earlier one.
        XCTAssertEqual(EdgeTranslations.slot([item(2, 0.4), item(3, 0.1)]).first?.opacity ?? 0, 0.4, accuracy: 1e-9)
    }

    /// Top and bottom are slots of their own.
    func testEachEdgeIsItsOwnSlot() {
        let shown = EdgeTranslations.slot([item(2, 1, top: true), item(3, 1)])
        XCTAssertEqual(shown.map(\.id), [2, 3])
        XCTAssertEqual(shown.map(\.opacity), [1, 1])
    }

    func testATranslationsStrengthFollowsItsLineAndTheHandOver() {
        XCTAssertEqual(EdgeTranslations.presence(own: 0.8, handoff: 1, handedOn: 0), 0.8, accuracy: 1e-9)
        XCTAssertEqual(EdgeTranslations.presence(own: 1, handoff: 0.5, handedOn: 0), 0.5, accuracy: 1e-9)
        XCTAssertEqual(EdgeTranslations.presence(own: 1, handoff: 1, handedOn: 0.25), 0.75, accuracy: 1e-9)
        XCTAssertEqual(EdgeTranslations.presence(own: 1, handoff: 1, handedOn: 1), 0)
    }

    /// A held line's translation gives the slot up as the next line arrives,
    /// whether or not that line has a translation of its own.
    func testAHeldLineHandsTheSlotOn() {
        let lines = [
            LyricHighlightRenderTests.spaced("Hold me close until the morning light", start: 30, step: 0.4,
                                             held: ["light": 2.2]),
            LyricHighlightRenderTests.spaced("Never let me go", start: 33.8, step: 0.45),
        ]
        XCTAssertTrue(LyricPhraseTiming.isHeld(0, in: lines), "the fixture's last note rings on into the next line")
        func handover(_ time: Double) -> Double {
            LyricLayer.edgeHandover(after: 0, in: lines, time: time) { LyricLayer.handOver(after: $0, in: lines) { _ in false } }
        }
        XCTAssertEqual(handover(33.7), 0)
        XCTAssertGreaterThan(handover(34.0), 0)
        XCTAssertEqual(handover(35.5), 1, accuracy: 1e-3)
        // Nothing after the last line, and an instrumental break takes nothing over.
        XCTAssertEqual(LyricLayer.edgeHandover(after: 1, in: lines, time: 40) { _ in 50 }, 0)
        var withBreak = lines
        withBreak[1].isInstrumental = true
        XCTAssertEqual(LyricLayer.edgeHandover(after: 0, in: withBreak, time: 35) { _ in 50 }, 0)
    }
}
