import Foundation

// How the sync loop corrects drift, per player:
// - AVPlayer: small drift is eased away by playing a little faster or
//   slower (`MusicVideoRateNudge`); only a real jump is sought.
// - YouTube: its rates are too coarse to ease with, so it seeks, but
//   rarely (`MusicVideoSeekHysteresis`), and each seek is led by the time
//   this session's player takes to land one (`MusicVideoSeekLatency`).

/// Small drift corrected with the playback rate instead of a seek.
struct MusicVideoRateNudge: Equatable {
    /// Drift beyond this is a jump: sought.
    static let seekThreshold = 1.5
    /// Start easing past this drift…
    static let engage = 0.12
    /// …and stop once within this.
    static let release = 0.04
    /// The furthest from 1 the rate goes.
    static let maximumNudge = 0.03
    /// Drift is eased away over about this many seconds (until the nudge caps).
    static let horizon = 6.0

    private(set) var easing = false

    /// The rate for a playing video `drift` seconds ahead (+) or behind (−).
    mutating func rate(forDrift drift: Double) -> Double {
        if easing && abs(drift) < Self.release { easing = false }
        else if !easing && abs(drift) > Self.engage { easing = true }
        guard easing else { return 1 }
        let nudge = min(Self.maximumNudge, max(-Self.maximumNudge, drift / Self.horizon))
        // In steps of 0.005, so the rate is not reset on every check.
        return 1 - (nudge * 200).rounded() / 200
    }

    mutating func reset() { easing = false }
}

/// How long this session's YouTube player takes from `seekTo` until it
/// plays at the target: the lead added to the next seek.
///
/// Measured from the drift once the video plays after a seek: a seek led
/// by `lead` that lands after `latency` leaves the video `lead − latency`
/// ahead, so `latency = lead − residual`, whatever the sampling cadence.
struct MusicVideoSeekLatency: Equatable {
    static let initial = 0.25
    static let bounds: ClosedRange<Double> = 0...2.5
    static let kept = 5

    private(set) var samples: [Double] = []

    /// The lead for the next seek: the median of the last few landings.
    var lead: Double {
        guard !samples.isEmpty else { return Self.initial }
        let sorted = samples.sorted(), middle = sorted.count / 2
        return sorted.count % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle]
    }

    mutating func record(lead used: Double, residual: Double) {
        guard used.isFinite, residual.isFinite else { return }
        let latency = used - residual
        // Wild values are a scrub or a stall during the seek, not latency.
        guard latency > -0.5, latency < Self.bounds.upperBound + 1 else { return }
        samples = Array((samples + [min(Self.bounds.upperBound, max(Self.bounds.lowerBound, latency))]).suffix(Self.kept))
    }
}

/// When a YouTube video is sought: only for drift that is large or lasts,
/// and once correcting, until it is well within step (hysteresis).
///
/// Drift between `release` and `engage` that simply stays (a seek that
/// landed off, with the retries used up) is sought too once it has lasted
/// `lingerChecks`; each such seek doubles the wait for the next, so a
/// player that never lands closer is not sought over and over.
struct MusicVideoSeekHysteresis: Equatable {
    /// Drift past this, on `persistence` checks in a row, is sought.
    static let engage = 1.0
    /// Drift past this is sought at once (a scrub, a skipped intro).
    static let jump = 2.5
    /// After a seek, another one is tried until drift is within this…
    static let release = 0.3
    /// …or this, after the offset itself was changed (a quarter-second
    /// nudge must show).
    static let retargetRelease = 0.12
    /// …at most this many times.
    static let retries = 2
    static let persistence = 2
    /// Checks (a quarter second each) a lingering drift lasts before a seek.
    static let lingerChecks = 12

    private var over = 0
    private var correcting = false
    private var tries = 0
    private var lingering = 0
    private var lingerWait = lingerChecks
    private var releaseNow = release

    /// Whether to seek now, for a playing, settled video `drift` off.
    mutating func shouldSeek(drift: Double) -> Bool {
        let size = abs(drift)
        if correcting {
            if size <= releaseNow || tries >= Self.retries {
                correcting = false
                tries = 0
                over = 0
                releaseNow = Self.release
                return false
            }
            tries += 1
            return true
        }
        if size > Self.jump { return startCorrecting() }
        if size > Self.engage {
            over += 1
            return over >= Self.persistence ? startCorrecting() : false
        }
        over = 0
        guard size > Self.release else {
            lingering = 0
            lingerWait = Self.lingerChecks
            return false
        }
        lingering += 1
        guard lingering >= lingerWait else { return false }
        lingerWait *= 2
        return startCorrecting()
    }

    /// The target itself moved (the user's nudge or tap, a found offset):
    /// what is off now is meant to be corrected at once, not waited out.
    mutating func retarget() {
        correcting = true
        releaseNow = Self.retargetRelease
        tries = 0
        over = 0
        lingering = 0
        lingerWait = Self.lingerChecks
    }

    private mutating func startCorrecting() -> Bool {
        correcting = true
        tries = 0
        over = 0
        lingering = 0
        return true
    }

    mutating func reset() { self = MusicVideoSeekHysteresis() }
}

/// How a surface's drift is corrected.
enum MusicVideoCorrectionStyle: Equatable {
    /// Seek past `MusicVideoDrift.seekThreshold` (the original rule).
    case seek
    /// Ease small drift with the rate; seek only jumps (AVPlayer).
    case rate
    /// Seek rarely, led by the measured seek latency (YouTube).
    case ledSeek
}

/// The sync loop's decisions, with the state each style keeps.
struct MusicVideoCorrector {
    enum Action: Equatable {
        case none
        case play
        case pause
        case seek(to: Double, thenPlay: Bool)
        case rate(Double)
    }

    let style: MusicVideoCorrectionStyle
    private(set) var rate = 1.0
    private(set) var latency = MusicVideoSeekLatency()
    private var nudge = MusicVideoRateNudge()
    private var hysteresis = MusicVideoSeekHysteresis()
    private(set) var lastSeek: TimeInterval = -.infinity
    /// The lead of the seek whose landing is still to be measured.
    private var pendingLead: Double?
    /// The offset was changed on purpose; not yet acted on (AVPlayer).
    private var retargeted = false
    /// A changed offset seeks AVPlayer past this; less is eased.
    static let retargetSeek = 0.3

    init(style: MusicVideoCorrectionStyle) {
        self.style = style
    }

    /// The seek threshold when the video is not playing yet.
    private var threshold: Double {
        switch style {
        case .seek: return MusicVideoDrift.seekThreshold
        case .rate: return MusicVideoRateNudge.seekThreshold
        case .ledSeek: return MusicVideoSeekHysteresis.engage
        }
    }

    /// - Parameter fixedLead: the surface's own lead, for `.seek` and `.rate`.
    mutating func decide(target: Double?, sample: MusicVideoDrift.Sample, shouldPlay: Bool, now: TimeInterval,
                         fixedLead: Double) -> Action {
        let sinceLastSeek = now - lastSeek
        let settled = sinceLastSeek >= MusicVideoDrift.seekSettle
        let lead = style == .ledSeek ? latency.lead : fixedLead
        let steady = shouldPlay && sample.videoPlaying && !sample.buffering
        if let target, steady {
            let drift = sample.videoTime - target
            // The first steady check after a seek tells how long it took.
            if let used = pendingLead {
                pendingLead = nil
                if style == .ledSeek { latency.record(lead: used, residual: drift) }
            }
            switch style {
            case .seek:
                break
            case .rate:
                guard settled else { return .none }
                let deliberate = retargeted
                retargeted = false
                if abs(drift) > MusicVideoRateNudge.seekThreshold || (deliberate && abs(drift) > Self.retargetSeek) {
                    return seek(target + lead, thenPlay: true, now: now)
                }
                let next = nudge.rate(forDrift: drift)
                if next != rate {
                    rate = next
                    return .rate(next)
                }
                return .none
            case .ledSeek:
                guard settled else { return .none }
                return hysteresis.shouldSeek(drift: drift) ? seek(target + latency.lead, thenPlay: true, now: now) : .none
            }
        }
        let base = MusicVideoDrift.decide(target: target, sample: sample, shouldPlay: shouldPlay,
                                          sinceLastSeek: sinceLastSeek, seekLead: lead, threshold: threshold)
        switch base {
        case .none: return .none
        case .play:
            resetRate()
            return .play
        case .pause:
            resetRate()
            hysteresis.reset()
            pendingLead = nil
            return .pause
        case .seek(let to, let thenPlay):
            return seek(to, thenPlay: thenPlay, now: now, led: thenPlay ? lead : nil)
        }
    }

    private mutating func seek(_ to: Double, thenPlay: Bool, now: TimeInterval, led: Double? = nil) -> Action {
        lastSeek = now
        pendingLead = thenPlay ? (led ?? (style == .ledSeek ? latency.lead : nil)) : nil
        resetRate()
        return .seek(to: to, thenPlay: thenPlay)
    }

    /// The offset in force changed (a nudge, a tap, a found offset): the
    /// change is applied at the next steady check instead of waiting for
    /// the drift to grow past the usual thresholds, which a small change
    /// never would on YouTube.
    mutating func targetMoved() {
        switch style {
        case .seek: break
        case .rate: retargeted = true
        case .ledSeek: hysteresis.retarget()
        }
    }

    /// Someone else sought the video at `now` (a drag on the video's bar):
    /// it is given time to land like one of the corrector's own seeks.
    mutating func noteSeek(at now: TimeInterval) {
        guard now.isFinite else { return }
        lastSeek = max(lastSeek, now)
        pendingLead = nil
        resetRate()
    }

    /// AVPlayer's `play()` and seeks leave the rate at 1.
    private mutating func resetRate() {
        rate = 1
        nudge.reset()
    }
}
