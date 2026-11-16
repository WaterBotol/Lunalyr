import Foundation

/// Lines sung close together, grouped into runs, so a layout's stage motion
/// can carry on from one line into the next instead of starting again at
/// every line.
///
/// Two neighbouring lines share a run when the next one starts singing less
/// than `breakGap` after the singing so far has stopped. An instrumental
/// line, a line with nothing to sing, a switch between word timing and line
/// timing, or a long pause starts a new run, and the motion settles and
/// starts again there, as it did at every line before.
///
/// Worked out once per song from the lyrics alone (`LyricLayerCache`), never
/// from what was drawn before: a seek lands on exactly the picture playing
/// through would have shown.
struct LyricPhraseRuns: Equatable {
    /// The shortest pause, in seconds, that breaks a run. A breath between
    /// lines, a held beat or a short fill is under it. It is the pause that
    /// brings up the instrumental-break cue (`LyricLayer.instrumentalGap`),
    /// which dims every line for its dots: a hand-over across a longer pause
    /// would play half-hidden behind them, so the two always agree.
    static let breakGap: Double = 2.4

    /// Where one line hands the stage on to the next inside a run.
    struct Boundary: Equatable {
        /// The onset of the last word sung on the earlier line.
        var lastOnset: Double
        /// The onset of the first word sung on the later line.
        var nextOnset: Double
    }

    /// One line's place in its run.
    struct Line: Equatable {
        /// The run's number in the song, from 0.
        var run: Int
        /// The line's place in its run, from 0.
        var ordinal: Int
        /// When the run's first word is sung.
        var runStart: Double
        /// The words (that are sung, not punctuation) on the run's earlier lines.
        var wordsBefore: Int
        /// The hand-over from the line before, when it is in the same run.
        var incoming: Boundary?
        /// The hand-over to the line after, when it is in the same run.
        var outgoing: Boundary?

        /// A steady seed for the whole run: the same for every line in it,
        /// different for the next run.
        var seed: Int { Int(truncatingIfNeeded: (LyricPhraseTiming.timeSeed(runStart) &* 0x9E37_79B9_7F4A_7C15) >> 1) }
    }

    /// One entry per line; nil for a line that is in no run (nothing sung).
    let lines: [Line?]

    init(lines source: [LyricsFetcher.Line]) {
        var result: [Line?] = Array(repeating: nil, count: source.count)
        var run = -1
        var current: Line?
        var sungUntil = -Double.infinity
        var previousRow: Int?
        for (row, line) in source.enumerated() {
            guard let onset = Self.firstOnset(of: line), !line.isInstrumental else {
                current = nil
                previousRow = nil
                continue
            }
            if let before = previousRow, var last = current,
               Self.continues(from: source[before], to: line, sungUntil: sungUntil, nextOnset: onset) {
                // Inside the run: carried across unless the earlier line is
                // still being sung over this one (a duet part or an ad-lib
                // keeps its own exit).
                let carried = !LyricPhraseTiming.isHeld(before, in: source) && before + 1 == row
                let boundary = carried ? Boundary(lastOnset: Self.lastOnset(of: source[before]), nextOnset: onset) : nil
                if carried { result[before]?.outgoing = boundary }
                last.wordsBefore += Self.sungWords(in: source[before])
                last.ordinal += 1
                last.incoming = boundary
                last.outgoing = nil
                current = last
                sungUntil = max(sungUntil, Self.sungEnd(of: line))
            } else {
                run += 1
                current = Line(run: run, ordinal: 0, runStart: onset, wordsBefore: 0)
                sungUntil = Self.sungEnd(of: line)
            }
            result[row] = current
            previousRow = row
        }
        lines = result
    }

    /// The run record for `row`, if it is in one.
    subscript(row: Int) -> Line? {
        lines.indices.contains(row) ? lines[row] : nil
    }

    /// Whether `next` carries on the run that `previous` is in.
    static func continues(from previous: LyricsFetcher.Line, to next: LyricsFetcher.Line, sungUntil: Double,
                          nextOnset: Double) -> Bool {
        guard previous.wordTimed == next.wordTimed else { return false }
        return nextOnset - sungUntil < breakGap
    }

    /// When the first word is sung (`LyricPhraseTiming.firstWordOnset`).
    static func firstOnset(of line: LyricsFetcher.Line) -> Double? {
        LyricPhraseTiming.firstWordOnset(of: line)
    }

    /// The onset of the last word sung on the line (its start, line-timed).
    static func lastOnset(of line: LyricsFetcher.Line) -> Double {
        guard line.wordTimed else { return line.start }
        return line.words.last { LyricRuns.isVisible($0.text) }?.start ?? line.start
    }

    /// When the singing on a line stops: its last word's end when word-timed;
    /// for a line-timed line, the end of the time its estimated words were
    /// given, never a line end that only marks where the next line starts.
    static func sungEnd(of line: LyricsFetcher.Line) -> Double {
        if line.wordTimed { return line.words.map(\.end).max() ?? line.end }
        return LyricsFetcher.estimatedSweepEnd(of: line)
    }

    static func sungWords(in line: LyricsFetcher.Line) -> Int {
        line.words.filter { LyricRuns.isVisible($0.text) }.count
    }
}

extension LyricComposition {
    /// Layouts whose stage hands one line straight on to the next inside a
    /// run (`StageCarry`): Torque's column turns on to the next line as to a
    /// new face, Hinge's last page turns over onto the next line's, and
    /// Portal's trail climbs away as the next line's first pill takes its
    /// place. The line is held on the stage until that hand-over.
    var carriesStage: Bool { self == .torque || self == .hinge || self == .portal }
}

/// What one line's stage carries over from the line before it and on into
/// the line after it, inside a run (`LyricPhraseRuns`). Nil -- the first
/// line after a break, a preview, Reduce Motion -- draws the line as it
/// always was: arriving from rest and leaving on its own.
struct StageCarry: Equatable {
    /// The run's steady seed (`LyricPhraseRuns.Line.seed`).
    var seed: Int
    /// The words sung on the run's earlier lines.
    var wordsBefore: Int
    /// The hand-over from the line before, and its start (`LyricsFetcher.Line.start`).
    var incoming: Hand?
    /// The hand-over to the line after.
    var outgoing: Hand?

    struct Hand: Equatable {
        var boundary: LyricPhraseRuns.Boundary
        /// When the later line's view is first drawn (its line start).
        var laterStart: Double
    }

    /// The carry for `row`, given which neighbours share its layout.
    static func make(_ row: Int, runs: LyricPhraseRuns, lines: [LyricsFetcher.Line],
                     sameLayout: (Int) -> Bool) -> StageCarry? {
        guard let record = runs[row] else { return nil }
        let incoming = record.incoming.flatMap { boundary in
            sameLayout(row - 1) ? Hand(boundary: boundary, laterStart: lines[row].start) : nil
        }
        let outgoing = record.outgoing.flatMap { boundary in
            sameLayout(row + 1) && lines.indices.contains(row + 1)
                ? Hand(boundary: boundary, laterStart: lines[row + 1].start) : nil
        }
        return StageCarry(seed: record.seed, wordsBefore: record.wordsBefore, incoming: incoming, outgoing: outgoing)
    }

    /// When a carrying layout's line that hands on to the next starts its
    /// own exit: once the hand-over has played. It stays on the stage until
    /// then (`LyricPhraseTiming.exitStart(of:nextStart:heldUntil:)`).
    /// When a carrying layout's hand-over to the next line starts; text
    /// that is not part of the stage (a translation, a whole-line reading)
    /// fades from here, so it never sits over the next line's.
    static func handoverStart(_ carry: StageCarry?, composition: LyricComposition) -> Double? {
        guard composition.carriesStage, let hand = carry?.outgoing else { return nil }
        switch composition {
        case .torque: return StageMotion.torqueHandover(hand)
        case .hinge: return StageMotion.hingeHandover(hand)
        default: return StageMotion.portalHandover(hand)
        }
    }

    static func heldUntil(_ carry: StageCarry?, composition: LyricComposition) -> Double? {
        guard composition.carriesStage, let hand = carry?.outgoing else { return nil }
        switch composition {
        case .torque: return StageMotion.torqueHandover(hand) + StageMotion.torqueHandoverSpan
        case .hinge: return StageMotion.hingeHandover(hand) + StageMotion.hingeTurn
        default: return StageMotion.portalHandover(hand)
        }
    }
}

extension StageMotion {
    /// Torque: when the column starts its turn from one line's last face to
    /// the next line's first, as between two faces of one line: facing the
    /// new line as its first word is sung, never before the last word has
    /// landed, and never before the new line is on the stage.
    static func torqueHandover(_ hand: StageCarry.Hand) -> Double {
        let start = hand.boundary.nextOnset
        let early = start - lead - torqueTurnLead
        let turn = min(start - lead, max(early, hand.boundary.lastOnset + torqueUnit))
        return max(turn, hand.laterStart)
    }

    /// Torque: the hand-over turn's length, with its overshoot settled.
    static let torqueHandoverSpan = 3 * torqueUnit * (1 + torqueLaunch)

    /// Hinge: when the last page of a line starts to turn over onto the next
    /// line's first page, as a page turns within a line. The next line need
    /// not be on the stage yet: its page lies beneath, uncovered as the
    /// turning page lifts off it (`hingeUncovered`).
    static func hingeHandover(_ hand: StageCarry.Hand) -> Double {
        hand.boundary.nextOnset - lead - hingeTurnLead
    }

    /// Hinge: how far the hand-over page has turned, 0...1, at `time`: as a
    /// page turns within a line at an ordinary pace. Both lines work it out
    /// from the same hand-over, so it never depends on either line's pace.
    static func hingeHandoverTurn(_ hand: StageCarry.Hand, at time: Double) -> Double {
        StageLyricView.hingeEaseInOut((time - hingeHandover(hand)) / hingeTurn)
    }

    /// Hinge: how much of the next line's page the turning page has
    /// uncovered, 0...1: none while the page still lies flat over it, all
    /// of it once the page stands edge-on -- as the turning page fades on
    /// its way there (`hingeInstances`).
    static func hingeUncovered(_ hand: StageCarry.Hand, at time: Double) -> Double {
        let turn = hingeHandoverTurn(hand, at: time)
        guard turn < 0.5 else { return 1 }
        let edgeOn = abs(cos(turn * .pi))
        return 1 - Easing.smoothstep((edgeOn - 0.1) / 0.35)
    }

    /// Hinge: a turned-over last page fading once the hand-over has landed.
    static let hingeCarryFade = 0.4

    /// Portal: when the trail starts to climb away, a moment ahead of the
    /// next line's first pill, so the live pill is lifting clear as that
    /// pill arrives in its place.
    static func portalHandover(_ hand: StageCarry.Hand) -> Double {
        hand.boundary.nextOnset - lead - portalHandoverLead
    }

    /// Portal: how far ahead of the next line's first pill the trail starts to climb.
    static let portalHandoverLead = 0.12
}
