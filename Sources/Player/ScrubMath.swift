import Foundation

/// A lyric line as the seek bar sees it: in the song's own time (the lyric
/// offsets already taken off), so a line start is where a seek must land.
struct ScrubLine: Equatable, Sendable {
    var start: Double
    var end: Double
    var text: String
    var romanized: String?
    var isInstrumental: Bool

    /// The lines of a synced lyric, in song time. Credits are left out: they
    /// are not sung and never a seek target. `offset` is what the lyric layer
    /// adds to the song's time (global plus per-song offset).
    static func lines(from lyric: [LyricsFetcher.Line], offset: Double, romanized: [Int: String] = [:]) -> [ScrubLine] {
        let shift = offset.isFinite ? offset : 0
        return lyric.enumerated().compactMap { index, line in
            guard line.credit != true, line.start.isFinite, line.end.isFinite else { return nil }
            let roman = romanized[index] ?? line.romanization
            return ScrubLine(start: line.start - shift, end: line.end - shift, text: line.text,
                             romanized: roman?.isEmpty == false ? roman : nil, isInstrumental: line.isInstrumental)
        }
    }
}

/// What the lyric capsule over the seek bar says for a moment of the song.
enum ScrubLyric: Equatable {
    case line(Int)
    case instrumental
    case none
}

/// The seek bar's arithmetic, kept apart from the view so every rule is
/// tested without a window: fine-scrub gain by distance from the bar,
/// snapping to lyric lines, the rubber band past the ends, where a seek
/// lands, and what the bar draws for the lyric structure.
enum ScrubMath {
    static let barWidth: Double = 456
    /// Snap radius at full speed; scaled by the gain, so fine modes barely snap.
    static let snapRadius: Double = 7
    /// A sweep faster than this (pt/s) is never sticky.
    static let snapSpeedLimit: Double = 250
    /// At most this share of the distance to the neighbouring snap target.
    static let snapNeighbourShare: Double = 0.4
    /// A seek to a line start lands this much early, so its first word is
    /// not clipped by the seek's latency and the player's buffering.
    static let preRoll: Double = 0.20
    /// Seeking to the very end makes Music skip to the next song.
    static let endGuard: Double = 0.5
    /// How far past an end the fill may stretch, and the iOS constant.
    static let rubberBandDimension: Double = 40
    static let rubberBandCoefficient: Double = 0.55
    /// A silence this long between two lines breaks the bar into sections.
    static let sectionGap: Double = 6
    /// The fewest points between two drawn line ticks.
    static let minimumTickSpacing: Double = 4

    // MARK: Fine scrub

    /// Horizontal gain by vertical distance from the bar's centre line
    /// (OBSlider's steps): 1, ½, ¼, then 0.1. Option held is always fine.
    static func gain(verticalDistance: Double, fine: Bool = false) -> Double {
        if fine { return 0.1 }
        let distance = verticalDistance.isFinite ? abs(verticalDistance) : 0
        switch distance {
        case ..<40: return 1
        case ..<80: return 0.5
        case ..<120: return 0.25
        default: return 0.1
        }
    }

    /// The words under the time while scrubbing slower than the hand.
    static func rateLabel(gain: Double) -> String? {
        switch gain {
        case 0.75...: return nil
        case 0.4...: return "Half speed"
        case 0.2...: return "Quarter speed"
        default: return "Fine"
        }
    }

    // MARK: Position

    static func time(x: Double, width: Double = barWidth, duration: Double) -> Double {
        guard width > 0, duration > 0, x.isFinite else { return 0 }
        return min(max(x / width, 0), 1) * duration
    }

    static func x(time: Double, width: Double = barWidth, duration: Double) -> Double {
        guard width > 0, duration > 0, time.isFinite else { return 0 }
        return min(max(time / duration, 0), 1) * width
    }

    /// iOS's rubber band: `(1 - 1/(x·c/d + 1))·d`, signed. Grows ever slower
    /// and never reaches `dimension`.
    static func rubberBand(overshoot: Double, dimension: Double = rubberBandDimension,
                           coefficient: Double = rubberBandCoefficient) -> Double {
        guard overshoot.isFinite, dimension > 0, overshoot != 0 else { return 0 }
        let magnitude = (1 - 1 / (abs(overshoot) * coefficient / dimension + 1)) * dimension
        return overshoot < 0 ? -magnitude : magnitude
    }

    // MARK: Snapping

    /// Line starts (instrumental lines left out) plus the very start, sorted
    /// and without near-duplicates, inside the song.
    static func snapTargets(lines: [ScrubLine], duration: Double) -> [Double] {
        guard duration > 0 else { return [] }
        var targets = [0.0]
        for line in lines where !line.isInstrumental && line.start.isFinite && line.start > 0 && line.start < duration {
            targets.append(line.start)
        }
        targets.sort()
        var unique: [Double] = []
        for target in targets where unique.last.map({ target - $0 >= 0.05 }) ?? true { unique.append(target) }
        return unique
    }

    /// The snap target a time is pulled to, as an index into `targets`, or
    /// nil. `pointsPerSecond` is the bar's scale; `speed` the pointer's
    /// horizontal speed in pt/s; `disabled` is ⌘ held.
    static func snap(time: Double, targets: [Double], pointsPerSecond: Double, gain: Double = 1,
                     speed: Double = 0, disabled: Bool = false) -> Int? {
        guard !disabled, !targets.isEmpty, pointsPerSecond > 0, time.isFinite,
              speed.isFinite, abs(speed) < snapSpeedLimit else { return nil }
        let nearest = nearestIndex(of: time, in: targets)
        let target = targets[nearest]
        var radius = snapRadius * max(0, min(gain, 1))
        var neighbourGap = Double.infinity
        if nearest > 0 { neighbourGap = min(neighbourGap, target - targets[nearest - 1]) }
        if nearest + 1 < targets.count { neighbourGap = min(neighbourGap, targets[nearest + 1] - target) }
        if neighbourGap.isFinite { radius = min(radius, neighbourGap * pointsPerSecond * snapNeighbourShare) }
        return abs(time - target) * pointsPerSecond <= radius ? nearest : nil
    }

    static func nearestIndex(of time: Double, in sorted: [Double]) -> Int {
        var low = 0, high = sorted.count
        while low < high {
            let middle = (low + high) / 2
            if sorted[middle] < time { low = middle + 1 } else { high = middle }
        }
        if low == 0 { return 0 }
        if low == sorted.count { return sorted.count - 1 }
        return time - sorted[low - 1] <= sorted[low] - time ? low - 1 : low
    }

    /// Where a seek actually goes: a snapped line start lands `preRoll` early;
    /// never past `duration - endGuard`, never before 0.
    static func commitTarget(time: Double, snappedTo lineStart: Double? = nil, duration: Double) -> Double {
        var target = lineStart.map { $0 - preRoll } ?? time
        if !target.isFinite { target = 0 }
        let last = duration > 0 ? max(0, duration - endGuard) : max(0, target)
        return min(max(target, 0), last)
    }

    // MARK: Drawing the lyric structure

    /// x positions of the line-start ticks, at most one per `spacing` points.
    /// The first target (0:00) is not a line and gets no tick.
    static func ticks(targets: [Double], duration: Double, width: Double = barWidth,
                      spacing: Double = minimumTickSpacing) -> [Double] {
        guard duration > 0, width > 0 else { return [] }
        var result: [Double] = []
        for target in targets where target > 0 {
            let x = Self.x(time: target, width: width, duration: duration)
            if let last = result.last, x - last < spacing { continue }
            result.append(x)
        }
        return result
    }

    /// Song times where a new section starts: the first sung line after a
    /// silence of `sectionGap` seconds or more, or after an instrumental line.
    /// Never within a second of either end, and never two within a second.
    static func sectionBreaks(lines: [ScrubLine], duration: Double, gap: Double = sectionGap) -> [Double] {
        guard duration > 2, lines.count > 1 else { return [] }
        var breaks: [Double] = []
        var previousEnd: Double?
        var afterInstrumental = false
        for line in lines.sorted(by: { $0.start < $1.start }) {
            if line.isInstrumental {
                afterInstrumental = previousEnd != nil
                continue
            }
            if let end = previousEnd, afterInstrumental || line.start - end >= gap,
               line.start > 1, line.start < duration - 1,
               breaks.last.map({ line.start - $0 >= 1 }) ?? true {
                breaks.append(line.start)
            }
            previousEnd = max(previousEnd ?? line.end, line.end)
            afterInstrumental = false
        }
        return breaks
    }

    /// Stretches where lines that come back in the song are sung, two or
    /// more such lines in a row: most often the chorus. A guess from the
    /// words alone, so the bar only lightens them and never names them.
    static func repeatedSpans(lines: [ScrubLine], duration: Double, gap: Double = sectionGap) -> [ClosedRange<Double>] {
        let sung = lines.filter { !$0.isInstrumental }.sorted { $0.start < $1.start }
        let keys = sung.map { CoverLookup.normal($0.text) }
        var counts: [String: Int] = [:]
        for key in keys where !key.isEmpty { counts[key, default: 0] += 1 }
        var spans: [ClosedRange<Double>] = []
        var run: [ScrubLine] = []
        func close() {
            if run.count >= 2, let first = run.first, let last = run.last {
                let end = min(max(last.end, first.start), duration > 0 ? duration : last.end)
                if end > first.start { spans.append(first.start...end) }
            }
            run = []
        }
        for (line, key) in zip(sung, keys) {
            let repeated = !key.isEmpty && (counts[key] ?? 0) >= 2
            if repeated, let last = run.last, line.start - last.end < gap {
                run.append(line)
            } else {
                close()
                if repeated { run = [line] }
            }
        }
        close()
        return spans
    }

    /// The line being sung at `time`, an instrumental stretch, or nothing.
    /// A gap of 3 s or more after a line's end reads as instrumental, and so
    /// does an intro of 3 s or more before the first line.
    static func lyric(at time: Double, lines: [ScrubLine]) -> ScrubLyric {
        guard !lines.isEmpty, time.isFinite else { return .none }
        var found: Int?
        var low = 0, high = lines.count
        while low < high {
            let middle = (low + high) / 2
            if lines[middle].start <= time { low = middle + 1 } else { high = middle }
        }
        if low > 0 { found = low - 1 }
        guard let index = found else {
            let firstSung = lines.first { !$0.isInstrumental }?.start ?? lines[0].start
            return firstSung >= 3 ? .instrumental : .none
        }
        let line = lines[index]
        if line.isInstrumental { return .instrumental }
        let nextStart = index + 1 < lines.count ? lines[index + 1].start : Double.infinity
        if time > line.end + 1, nextStart - line.end >= 3 { return .instrumental }
        return .line(index)
    }

    // MARK: Stepping (keyboard and VoiceOver)

    /// The next or previous line start from `time`. A time just before a
    /// start (a pre-rolled seek) counts as at it, so repeated presses walk
    /// line by line. Going back more than 2 s into a line first returns to
    /// its own start, as a previous-track button does.
    static func adjacentLine(from time: Double, targets: [Double], forward: Bool) -> Double? {
        guard !targets.isEmpty, time.isFinite else { return nil }
        let slack = preRoll + 0.05
        let current = targets.lastIndex { $0 <= time + slack }
        if forward {
            let next = current.map { $0 + 1 } ?? 0
            return next < targets.count ? targets[next] : nil
        }
        guard let current else { return nil }
        if time - targets[current] > 2 { return targets[current] }
        return current > 0 ? targets[current - 1] : targets[0]
    }

    /// A seek by a fixed step, inside the song.
    static func stepped(_ time: Double, by step: Double, duration: Double) -> Double {
        commitTarget(time: time + step, duration: duration)
    }

    /// The 0–9 keys: 0 % to 90 % of the song.
    static func percentJump(digit: Int, duration: Double) -> Double? {
        guard (0...9).contains(digit), duration > 0 else { return nil }
        return commitTarget(time: duration * Double(digit) / 10, duration: duration)
    }

    // MARK: After a seek

    /// What the bar shows after a commit: the target, for at most `holdFor`
    /// seconds or until the player's clock is within `tolerance` of it, so a
    /// single stale redraw never pulls the knob back.
    static func displayedTime(playback: Double, held: Double?, heldSince: Double, now: Double,
                              holdFor: Double = 0.6, tolerance: Double = 0.5) -> Double {
        guard let held, now - heldSince < holdFor, abs(playback - held) > tolerance else { return playback }
        return held
    }

    // MARK: Words

    /// 1:05, 12:40, 1:02:03.
    static func timestamp(_ seconds: Double) -> String {
        guard seconds.isFinite, seconds >= 0 else { return "0:00" }
        let total = Int(seconds.rounded(.down))
        let hours = total / 3600, minutes = (total % 3600) / 60, rest = total % 60
        return hours > 0 ? String(format: "%d:%02d:%02d", hours, minutes, rest) : String(format: "%d:%02d", minutes, rest)
    }

    /// The right-hand label: time left (with a minus) or the song's length.
    static func trailingLabel(elapsed: Double, duration: Double, showsRemaining: Bool) -> String {
        guard duration > 0 else { return "" }
        if !showsRemaining { return timestamp(duration) }
        let left = max(0, duration - min(max(elapsed, 0), duration))
        return "-" + timestamp(left.rounded(.up))
    }

    /// VoiceOver's value for the bar.
    static func accessibilityValue(elapsed: Double, duration: Double, line: String?) -> String {
        var value = "\(timestamp(elapsed)) of \(timestamp(duration))"
        if let line, !line.isEmpty { value += ", line: \(line)" }
        return value
    }
}
