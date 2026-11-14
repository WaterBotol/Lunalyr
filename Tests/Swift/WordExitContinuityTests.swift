import SwiftUI
import XCTest
@testable import VersoCustom

/// Nothing jumps as a word stops being the one sung.
///
/// Each layout's words are sampled at 120 Hz across three lines -- an even
/// one, a fast one (a word every 0.15 s) and one with a held note -- from
/// the same functions the views draw with: each word's opacity, scale,
/// place and the time-driven inputs feeding them. The change over one 60 Hz
/// frame is compared with the changes around it: a jump is a change far
/// larger than its neighbours', and across every word end and onset the
/// change from t - 1/120 to t + 1/120 must be no larger than the frames
/// either side of it allow.
@MainActor
final class WordExitContinuityTests: XCTestCase {
    typealias Word = LyricsFetcher.Word

    struct Line {
        let name: String
        let words: [Word]
    }

    static var lines: [Line] {
        let normal = StagePhrasesTests.even("we keep dreaming under neon rain")
        let fast = "we run it back and turn it up til the lights go".split(separator: " ").enumerated().map { i, text in
            Word(text: String(text), start: 1 + Double(i) * 0.15, end: 1 + Double(i) * 0.15 + 0.13)
        }
        let held: [Word] = [
            Word(text: "hold", start: 1.00, end: 1.40), Word(text: "on", start: 1.45, end: 1.80),
            Word(text: "to", start: 1.85, end: 2.10), Word(text: "forever", start: 2.15, end: 4.00),
            Word(text: "now", start: 4.30, end: 4.80), Word(text: "love", start: 4.85, end: 5.40),
        ]
        return [Line(name: "normal", words: normal), Line(name: "fast", words: fast), Line(name: "held", words: held)]
    }

    /// One word's drawn values on one frame, by channel name.
    typealias Sample = [String: Double]

    static let rate = 120.0
    static let frame = 1.0 / 60

    // MARK: Rolling stages (Spoken, Cascade, Cutout)

    /// Every rolling word as `SpokenTypographyView.stack` draws it.
    /// Words join the stage's window `SpokenTypographyView.windowLead`
    /// before they are sung, as the wallpaper's window takes the next line's.
    static func rolling(_ all: [Word], composition: LyricComposition, at time: Double,
                        reduceMotion: Bool = false) -> [Int: Sample] {
        let settings = Settings.shared
        let baseSize: CGFloat = 46
        let words = all.filter { time >= $0.start - SpokenTypographyView.windowLead }
        let following = all.first { time < $0.start - SpokenTypographyView.windowLead }?.start
        let prepared = SpokenTypography.prepare(.init(words: words, baseSize: baseSize, mood: .neutral,
            composition: composition, fontIdentity: LyricFontIdentity(settings: settings), following: following),
            settings: settings)
        let intensity = OrganicMotion.vocalIntensity(words, at: time, horizon: SpokenTypographyView.windowLead)
        let cursor = SpokenTypographyView.cursor(prepared, time: time, intensity: intensity, reduceMotion: reduceMotion,
                                                 composition: composition)
        let next = words.firstIndex { $0.start > time }
        let lineEnd = LyricHighlightTiming.lineEnd(nil, words: words)
        var result: [Int: Sample] = [:]
        var placed: [(index: Int, size: CGSize, slot: RollingStack.Slot)] = []
        for word in prepared {
            let rank = cursor - 1 - Double(word.index)
            guard rank >= -2, rank <= 5.2 else { continue }
            let chosen = settings.arrival(in: composition, for: word.source)
            let eligible = word.index > 0 ? prepared[word.index - 1].source.start : -Double.infinity
            let highlight = chosen.highlightsSungWords && !word.tile
                ? SpokenTypographyView.highlight(word, lineEnd: lineEnd, time: time, reduceMotion: reduceMotion) : .hidden
            let state = RollingTokenState.make(word, next: next, rank: rank, time: time, composition: composition,
                baseSize: baseSize, stageWidth: 900, intensity: intensity, reduceMotion: reduceMotion,
                arrival: chosen.motion(in: composition), eligibleSince: eligible, highlightOpacity: highlight.opacity)
            var sample: Sample = [:]
            sample["rank"] = rank
            sample["opacity"] = state.shown
            sample["scale"] = state.scale
            sample["angle"] = state.angle / 10
            sample["blur"] = state.pose.blur
            sample["x"] = Double(state.slot.dx / baseSize)
            sample["lift"] = Double(state.slot.lift / baseSize)
            sample["label"] = word.tile ? RollingUnit.labelHold(rank: rank) : 0
            sample["travel"] = Double(state.slot.entrance.height + state.slot.entrance.width) / Double(baseSize)
            sample["turn"] = state.slot.turn / 10
            result[word.index] = sample
            // The unit's size, estimated from its text as the stack measures it.
            let size = CGSize(width: word.pointSize * 0.6 * CGFloat(word.source.text.count) + 4,
                              height: word.pointSize * 1.3)
            placed.append((word.index, size, state.slot))
        }
        // Where the stack puts each unit (`RollingStack.centres`).
        let centres = RollingStack.centres(sizes: placed.map(\.size), slots: placed.map(\.slot),
                                           anchor: CGPoint(x: 450, y: 300), gap: baseSize * SpokenTypographyView.gap(composition))
        for (unit, centre) in zip(placed, centres) {
            result[unit.index]?["placeX"] = Double(centre.x / baseSize)
            result[unit.index]?["placeY"] = Double(centre.y / baseSize)
        }
        return result
    }

    // MARK: Flow layouts (Scattered, Editorial, Constellation x2, Echo)

    /// Every run as `ScatteredLyricText` draws it: its opacity (reveal and
    /// exit), its swell, its vocal gesture and its glow.
    static func flow(_ words: [Word], composition: LyricComposition, at time: Double,
                     reduceMotion: Bool = false) -> [Int: Sample] {
        let settings = Settings.shared
        let key = ScatteredTypography.Key(words: words, baseSize: 46, style: .forLine(3), mood: .neutral,
            composition: composition, spread: settings.constellationSpread,
            fontIdentity: LyricFontIdentity(settings: settings), lineEnd: nil, glue: [])
        let layout = ScatteredTypography.layout(key, settings: settings)
        let prepared = layout.words
        let next = prepared.firstIndex { $0.source.start > time }
        let intensity = OrganicMotion.vocalIntensity(words, at: time)
        let arrivals = layout.runs.map { settings.arrival(in: composition, for: $0.source) }
        let states: [ScatteredWordState] = prepared.map {
            ScatteredWordFrame.state($0, nextIndex: next, time: time, composition: composition, energy: intensity,
                                     exitStart: .infinity, reduceMotion: reduceMotion, ink: .white,
                                     eligibleSince: $0.index > 0 ? prepared[$0.index - 1].source.start : -.infinity)
        }
        let reveals = settings.revealsWords(in: composition)
        var result: [Int: Sample] = [:]
        for run in layout.runs {
            let frame = ScatteredRunFrame.make(run, states: states, reveals: reveals, ink: .white,
                highlights: arrivals[run.index].highlightsSungWords, time: time, reduceMotion: reduceMotion)
            let leave = states[run.tokens.lowerBound].leave
            var sample: Sample = [:]
            sample["opacity"] = frame.presentation.opacity * (1 - leave)
            sample["scale"] = Double(ScatteredRunFrame.swell(heat: frame.heat, glow: frame.glow, gesture: frame.driftScale,
                                                             reduceMotion: reduceMotion))
            sample["x"] = reduceMotion ? 0 : frame.driftX
            sample["y"] = reduceMotion ? 0 : frame.driftY
            sample["angle"] = reduceMotion ? 0 : frame.driftAngle / 10
            sample["glow"] = frame.glow
            sample["heat"] = frame.heat
            result[run.index] = sample
        }
        return result
    }

    // MARK: Stage layouts (Hinge, Broadside, Portal, Mosaic, Torque, Fable)

    static func runs(_ words: [Word]) -> [StageRun] {
        StageRun.make(words, glue: [Bool](repeating: false, count: words.count))
    }

    /// Torque: each word's face (its turn away and the fade that goes with
    /// it), its own wring, rise and scale, and its liveness.
    static func torque(_ words: [Word], at time: Double) -> [Int: Sample] {
        let runs = runs(words)
        let plan = StageLyricView.torquePlan(runs)
        let shown = StageLyricView.shownRuns(runs, at: time)
        let rho = StageLyricView.torqueRotation(plan, at: time, shownCount: shown.count, reduceMotion: false)
        var result: [Int: Sample] = [:]
        for run in runs where time >= run.start - StageMotion.lead {
            let angle = (Double(plan.faceOfRun[run.id]) - rho) * 90
            guard abs(angle) < 90 else { continue }
            let pose = StageLyricView.torquePose(run, role: plan.roles[run.id], clock: plan.clocks[run.id],
                held: plan.held[run.id], lineTimed: plan.lineTimed, at: time, arrived: run.id < shown.count,
                reduceMotion: false)
            let face = pow(max(0, cos(abs(angle) * .pi / 180)), 1.5)
            result[run.id] = ["face": angle / 90, "opacity": face * pose.opacity, "wring": pose.wring / 90,
                              "rise": pose.rise, "scale": pose.scale, "depth": pose.depth,
                              "live": StageLyricView.torqueLive(run, runs: runs, at: time)]
        }
        return result
    }

    /// Fable: each word's page turning away, its landing and its liveness.
    static func fable(_ words: [Word], at time: Double) -> [Int: Sample] {
        let runs = runs(words)
        let plan = StageLyricView.fablePlan(runs)
        var result: [Int: Sample] = [:]
        for (index, page) in plan.pages.enumerated()
        where StageLyricView.fablePageShown(index, plan: plan, at: time, exitStart: .infinity) {
            let turn = StageLyricView.fablePageTurn(index, plan: plan, at: time, exitStart: .infinity)
            for id in page.runs where time >= runs[id].start - StageMotion.lead {
                let run = runs[id]
                let pose = StageLyricView.fablePose(run, role: plan.roles[id],
                    landing: FableLanding.forPhrase(plan.phraseOfRun[id], seed: 3), lineTimed: plan.lineTimed,
                    clock: plan.clocks[id], at: time)
                result[id] = ["opacity": 1 - turn, "fold": turn, "lift": pose.lift, "scale": pose.scale,
                              "spin": pose.spin / 90, "live": StageLyricView.torqueLive(run, runs: runs, at: time)]
            }
        }
        return result
    }

    /// Mosaic: each word's rank, its path from where it landed into the
    /// stack, the big setting shrinking and the small one taking over.
    static func mosaic(_ words: [Word], at time: Double) -> [Int: Sample] {
        let shown = StageLyricView.shownRuns(runs(words), at: time)
        let speed = PortalLayoutTests.speed(runs(words))
        let ranks = StageLyricView.ranks(shown, at: time, speed: speed, seed: 3, reduceMotion: false)
        let swaps = StageLyricView.mosaicSwaps(shown, at: time, speed: speed, seed: 3, reduceMotion: false)
        var result: [Int: Sample] = [:]
        for run in shown {
            let rank = ranks[run.id]
            let settle = StageLyricView.glide(min(rank, 1.2) / 1.2)
            let morph = StageMotion.mosaicMorph(settle: settle, smallRatio: 0.6 / 2.4, swap: swaps[run.id])
            let slot = min(3, max(rank, 1))
            let arrival = StageMotion.arrival(run.start, at: time, speed: speed)
            let fade = StageLyricView.mosaicStackFade(rank: rank)
            result[run.id] = ["rank": rank, "settle": settle, "bigScale": morph.bigScale,
                              "bigOpacity": min(1, arrival * 1.6) * morph.bigOpacity, "smallOpacity": morph.smallOpacity,
                              "stackY": settle * (slot - 1), "swap": morph.smallOpacity, "small": fade.word * morph.smallOpacity,
                              "smallReading": fade.reading * morph.smallOpacity]
        }
        return result
    }

    /// Portal: each pill as `PortalLayoutTests.poses` lays it out.
    static func portal(_ words: [Word], at time: Double) -> [Int: Sample] {
        let runs = StageLyricView.portalRuns(words, glue: [Bool](repeating: false, count: words.count))
        let poses = PortalLayoutTests.poses(runs, at: time)
        var result: [Int: Sample] = [:]
        for (index, pose) in poses.enumerated() {
            result[index] = ["y": Double(pose.y) / 100, "scale": Double(pose.scale), "opacity": pose.opacity,
                             "live": pose.live]
        }
        return result
    }

    /// Broadside: each word's arrival, its place on the strip (the camera
    /// following the newest big word) and how far it has dropped out.
    static func broadside(_ words: [Word], at time: Double) -> [Int: Sample] {
        let shown = StageLyricView.shownRuns(runs(words), at: time)
        let speed = PortalLayoutTests.speed(runs(words))
        let moves = shown.map { StageLyricView.handoff($0, at: time, speed: speed, seed: 3, reduceMotion: false) }
        let measured = shown.map { CGSize(width: CGFloat($0.text.count) * 46 * 0.9, height: 80) }
        let places = StageLyricView.broadsidePlaces(measured, little: shown.map { _ in false },
            slots: shown.map { _ in 0 }, moves: moves, unit: 46, in: CGRect(x: 0, y: 0, width: 900, height: 360))
        var result: [Int: Sample] = [:]
        for run in shown {
            let after = moves[(run.id + 1)...].reduce(0, +)
            let keep = StageMotion.softUnit(1 - (after - 1.6) * 0.9)
            let arrival = StageMotion.arrival(run.start, at: time, speed: speed)
            result[run.id] = ["x": Double(places[run.id].x) / 46, "opacity": min(1, arrival * 1.6) * keep,
                              "scale": 0.86 + 0.14 * arrival]
        }
        return result
    }

    /// Hinge: each page's turn over and how it shows; its words ride on it.
    static func hinge(_ words: [Word], at time: Double) -> [Int: Sample] {
        let runs = runs(words)
        let phrasing = StagePhrases.phrasing(runs)
        let pages = StageLyricView.hingePages(phrasing)
        let starts: [Double] = pages.map { page in page.map { phrasing.phrases[$0].start }.min() ?? 0 }
        let turns = StageLyricView.hingeTurns(pageStarts: starts, lineTimed: phrasing.lineTimed, exitStart: .infinity,
                                              at: time, speed: PortalLayoutTests.speed(runs))
        let begun = starts.map { phrasing.lineTimed || time >= $0 - StageMotion.lead }
        var result: [Int: Sample] = [:]
        for instance in StageLyricView.hingeInstances(turns: turns, begun: begun, reduceMotion: false) {
            result[instance.id] = ["angle": instance.angle / 90, "opacity": instance.opacity]
        }
        return result
    }

    // MARK: Measuring

    struct Jump: CustomStringConvertible {
        let layout: String
        let line: String
        let word: Int
        let channel: String
        let time: Double
        let step: Double
        let around: Double
        var ratio: Double { step / max(around, 1e-9) }
        var description: String {
            String(format: "%@ %@ word %d %@ at %.3f s: %.4f in a frame, %.4f around (x%.1f)",
                   layout, line, word, channel, time, step, around, ratio)
        }
    }

    /// Every frame-sized change that is far larger than the changes either
    /// side of it, and every change across a word end or onset that is.
    /// The least change over a frame worth looking at, by channel: about
    /// what can just be seen on a word at the gallery's size (46 pt).
    static func floor(_ channel: String) -> Double {
        switch channel {
        case "blur": return 0.15
        case "angle", "wring", "spin", "turn": return 0.02
        case "scale", "bigScale": return 0.004
        default: return 0.01
        }
    }

    static func jumps(layout: String, line: Line, sampler: (Double) -> [Int: Sample],
                      factor: Double = 2.5) -> [Jump] {
        let first = (line.words.map(\.start).min() ?? 0) - 0.3
        let last = (line.words.map(\.end).max() ?? 0) + 1.2
        let count = Int(((last - first) * rate).rounded(.up))
        let samples = (0...count).map { sampler(first + Double($0) / rate) }
        var found: [Jump] = []
        let keys = Set(samples.flatMap { $0.keys })
        for word in keys.sorted() {
            let channels = Set(samples.compactMap { $0[word] }.flatMap { $0.keys })
            for channel in channels.sorted() {
                let floor = Self.floor(channel)
                // D[i]: the change over one 60 Hz frame from sample i.
                var steps = [Double](repeating: .nan, count: samples.count - 2)
                for i in steps.indices {
                    guard let a = samples[i][word], let b = samples[i + 2][word],
                          let from = a[channel], let to = b[channel] else { continue }
                    // A word that cannot be seen cannot be seen to jump.
                    if channel != "opacity", min(a["opacity"] ?? 1, b["opacity"] ?? 1) < 0.05 { continue }
                    steps[i] = abs(to - from)
                }
                var worst: Jump?
                for i in steps.indices where steps[i] > floor {
                    // The frames just before and after, not overlapping this one.
                    func side(_ offsets: [Int]) -> Double? {
                        offsets.filter { steps.indices.contains($0) }.map { steps[$0] }.filter { !$0.isNaN }.max()
                    }
                    // Both sides: a word that starts to move, or leaves, is
                    // not a jump.
                    guard let before = side([i - 3, i - 2]), let after = side([i + 2, i + 3]) else { continue }
                    let around = max(before, after)
                    guard steps[i] > factor * around + floor * 0.5 else { continue }
                    let jump = Jump(layout: layout, line: line.name, word: word, channel: channel,
                                    time: first + Double(i + 1) / rate, step: steps[i], around: around)
                    if worst == nil || jump.step > worst!.step { worst = jump }
                }
                if let worst { found.append(worst) }
                // A move that starts at full speed: still, then in its first
                // frame already as fast as it will ever go.
                for i in steps.indices where steps[i] > floor {
                    guard i >= 4, !steps[i - 2].isNaN, steps[i - 2] < floor * 0.1, !steps[i - 4].isNaN,
                          steps[i - 4] < floor * 0.1 else { continue }
                    let later = (i..<min(steps.count, i + Int(0.4 * rate))).map { steps[$0] }.filter { !$0.isNaN }
                    let peak = later.max() ?? 0
                    if steps[i] >= 0.8 * peak, peak > 2 * floor {
                        found.append(Jump(layout: layout, line: line.name, word: word, channel: channel + " (starts at full speed)",
                                          time: first + Double(i + 1) / rate, step: steps[i], around: steps[i - 2]))
                        break
                    }
                }
            }
        }
        return found
    }

    static let rollingLayouts: [LyricComposition] = [.spoken, .cascade, .cutout]
    static let flowLayouts: [LyricComposition] = [.scattered, .editorial, .constellation, .constellationEditorial, .echo]

    /// Every layout's sampler, by name: all fourteen.
    static var samplers: [(name: String, sample: ([Word], Double) -> [Int: Sample])] {
        var result: [(name: String, sample: ([Word], Double) -> [Int: Sample])] = []
        for composition in rollingLayouts {
            result.append((composition.rawValue, { rolling($0, composition: composition, at: $1) }))
        }
        for composition in flowLayouts {
            result.append((composition.rawValue, { flow($0, composition: composition, at: $1) }))
        }
        result += [("torque", torque), ("fable", fable), ("mosaic", mosaic), ("portal", portal),
                   ("broadside", broadside), ("hinge", hinge)]
        return result
    }

    /// Changes that are not a word stepping back, and are left as they are:
    /// Torque's thick edge growing in behind an arriving word (its entrance),
    /// and Hinge's page passing edge-on as it turns over (it is hidden there,
    /// so as not to be seen as a sliver; on a fast line that takes about
    /// two frames each side of 90 degrees).
    static func exempt(_ jump: Jump) -> Bool {
        (jump.layout == "torque" && jump.channel.hasPrefix("depth"))
            || (jump.layout == "hinge" && jump.channel.hasPrefix("opacity"))
    }

    /// No layout's word jumps as it stops being the one sung, or anywhere
    /// else in the line: nothing changes in one frame far more than in the
    /// frames around it, and nothing still sets off at full speed.
    func testNoWordJumpsAsItStepsBack() {
        var found: [Jump] = []
        for line in Self.lines {
            for sampler in Self.samplers {
                found += Self.jumps(layout: sampler.name, line: line) { sampler.sample(line.words, $0) }
            }
        }
        let jumps = found.filter { !Self.exempt($0) }
        for jump in jumps.sorted(by: { $0.step > $1.step }) { print("EXIT-JUMP \(jump)") }
        XCTAssertTrue(jumps.isEmpty, jumps.prefix(8).map(\.description).joined(separator: "\n"))
    }

    /// Across every word end and onset the change from t - 1/120 to
    /// t + 1/120 is no larger than the frames either side of it allow, for
    /// every word already on the stage (one arriving then is the arrivals'
    /// business: in a fast line the next word lands as this one ends).
    func testNothingStepsAtAWordEndOrOnset() {
        for line in Self.lines {
            let runs = Self.runs(line.words)
            for sampler in Self.samplers {
                for word in line.words {
                    for moment in [word.end, word.start] {
                        // Units are keyed by their word's (or run's) place in the line;
                        // Hinge's by page, whose words are all on it by then.
                        let settled = Set(runs.filter { $0.start < moment - 0.1 }.map(\.id))
                        let before = sampler.sample(line.words, moment - 1.0 / 120)
                        let after = sampler.sample(line.words, moment + 1.0 / 120)
                        let earlier = sampler.sample(line.words, moment - 3.0 / 120)
                        let later = sampler.sample(line.words, moment + 3.0 / 120)
                        for (index, values) in before where sampler.name == "hinge" || settled.contains(index) {
                            guard let next = after[index] else { continue }
                            for (channel, value) in values {
                                guard let now = next[channel] else { continue }
                                if Self.exempt(Jump(layout: sampler.name, line: line.name, word: index, channel: channel,
                                                    time: moment, step: 0, around: 0)) { continue }
                                let across = abs(now - value)
                                let either = max(abs(value - (earlier[index]?[channel] ?? value)),
                                                 abs((later[index]?[channel] ?? now) - now))
                                XCTAssertLessThanOrEqual(across, 2.5 * either + Self.floor(channel),
                                    String(format: "%@ %@ word %d %@ steps %.4f at %.3f s (%.4f either side)",
                                           sampler.name, line.name, index, channel, across, moment, either))
                            }
                        }
                    }
                }
            }
        }
    }

    /// Reduce Motion still cross-fades: the rolling and flow layouts' words
    /// fade and step back without a jump there too.
    func testReduceMotionStillCrossFades() {
        var found: [Jump] = []
        for line in Self.lines {
            for composition in Self.rollingLayouts {
                found += Self.jumps(layout: composition.rawValue + " (Reduce Motion)", line: line) {
                    Self.rolling(line.words, composition: composition, at: $0, reduceMotion: true)
                }
            }
            for composition in Self.flowLayouts {
                found += Self.jumps(layout: composition.rawValue + " (Reduce Motion)", line: line) {
                    Self.flow(line.words, composition: composition, at: $0, reduceMotion: true)
                }
            }
        }
        XCTAssertTrue(found.isEmpty, found.prefix(8).map(\.description).joined(separator: "\n"))
    }

    // MARK: The fixes, one by one

    /// The voice's intensity moves every word's swell and the rolling
    /// stages' pace, so it never steps: not as a held note is reached or
    /// left, not at the edge of the onsets it counts, and not as a word
    /// joins a rolling window 1.2 s before it is sung.
    func testVocalIntensityIsContinuous() {
        for line in Self.lines {
            let words = line.words
            var time = (words.first?.start ?? 0) - 3
            var previous = OrganicMotion.vocalIntensity(words, at: time)
            var previousWindow: Double?
            while time < (words.last?.end ?? 0) + 3 {
                time += 0.001
                let now = OrganicMotion.vocalIntensity(words, at: time)
                XCTAssertLessThan(abs(now - previous), 0.004, String(format: "%@ at %.3f s", line.name, time))
                previous = now
                let window = words.filter { time >= $0.start - SpokenTypographyView.windowLead }
                let rolling = OrganicMotion.vocalIntensity(window, at: time, horizon: SpokenTypographyView.windowLead)
                if let previousWindow {
                    XCTAssertLessThan(abs(rolling - previousWindow), 0.004,
                                      String(format: "%@ window at %.3f s", line.name, time))
                }
                previousWindow = rolling
            }
        }
        // A held note still reads as intense while it is sung, and a dense
        // line as more intense than a sparse one.
        let held = Self.lines[2].words
        XCTAssertGreaterThan(OrganicMotion.vocalIntensity(held, at: 3), OrganicMotion.vocalIntensity(held, at: 6.5) + 0.2)
    }

    /// Torque's column sets off from rest as the next phrase begins and
    /// still lands facing it, with its overshoot.
    func testTorqueTurnSetsOffFromRest() {
        XCTAssertEqual(StageMotion.torqueTurn(0), 0)
        // The first 60 Hz frame of the turn (three units long).
        let frame = 1.0 / 60 / (3 * StageMotion.torqueUnit)
        XCTAssertLessThan(StageMotion.torqueTurn(frame) * 90, 4, "degrees in the first frame")
        XCTAssertGreaterThan(StageMotion.torqueOut(frame) * 90, 12, "the plain out-ease, for comparison")
        let peak = stride(from: 0.0, through: 1.1, by: 0.01).map { StageMotion.torqueTurn($0) }.max() ?? 0
        XCTAssertGreaterThan(peak, 1.05, "keeps its overshoot")
        XCTAssertEqual(StageMotion.torqueTurn(1 + StageMotion.torqueLaunch / 2), 1, accuracy: 1e-9)
    }

    /// Mosaic's cross-fade from a word's big setting to its small one takes
    /// at least about 0.1 s even on a fast line, and the two are still never
    /// seen at different sizes.
    func testMosaicCrossFadeTakesItsTime() {
        for line in Self.lines {
            let runs = Self.runs(line.words)
            let speed = PortalLayoutTests.speed(runs)
            var time = (runs.first?.start ?? 0) - 0.1
            var starts: [Int: Double] = [:], ends: [Int: Double] = [:]
            while time < (runs.last?.start ?? 0) + 2 {
                let ranks = StageLyricView.ranks(runs, at: time, speed: speed, seed: 3, reduceMotion: false)
                let swaps = StageLyricView.mosaicSwaps(runs, at: time, speed: speed, seed: 3, reduceMotion: false)
                for run in runs {
                    let settle = StageLyricView.glide(min(ranks[run.id], 1.2) / 1.2)
                    let morph = StageMotion.mosaicMorph(settle: settle, smallRatio: 0.25, swap: swaps[run.id])
                    if morph.bigOpacity > 0.01, morph.smallOpacity > 0.01 {
                        XCTAssertEqual(morph.bigScale, 0.25, accuracy: 0.005, "\(line.name) \(run.text): two sizes at once")
                    }
                    if swaps[run.id] > 0.05, starts[run.id] == nil { starts[run.id] = time }
                    if swaps[run.id] > 0.95, ends[run.id] == nil { ends[run.id] = time }
                }
                time += 1.0 / 240
            }
            for (id, start) in starts {
                guard let end = ends[id] else { continue }
                XCTAssertGreaterThan(end - start, 0.09, "\(line.name) \(runs[id].text): cross-faded in \(end - start) s")
            }
        }
    }

    /// The rolling stages' two tiers keep their values at whole ranks.
    func testRecedeKeepsItsTiers() {
        for composition in Self.rollingLayouts {
            XCTAssertEqual(RollingTokenState.recede(0, composition: composition), 1, accuracy: 1e-9)
        }
        XCTAssertEqual(RollingTokenState.recede(1, composition: .spoken), 0.74, accuracy: 1e-9)
        XCTAssertEqual(RollingTokenState.recede(2, composition: .spoken), 0.58, accuracy: 1e-9)
        XCTAssertEqual(StageMotion.softUnit(0.5), 0.5)
        XCTAssertEqual(StageMotion.softUnit(-1), 0)
        XCTAssertEqual(StageMotion.softUnit(2), 1)
    }
}
