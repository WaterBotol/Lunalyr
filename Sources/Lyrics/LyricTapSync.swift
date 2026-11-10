import Foundation

// Tap to sync for lyrics: the user presses a key each time a lyric line
// starts in the song, and the per-song offset (`SongOffsets`) is worked out
// from those taps. The music video has its own Tap to sync
// (`MusicVideoTapSync`); this one times the lyrics against the song.
//
// Pure logic only; `LyricTapSyncController` runs a session.

enum LyricTapSync {
    /// How late a tap lands after the line it marks is heard.
    ///
    /// Shorter than the music video's (`MusicVideoTapSync.reactionTime`, 0.2)
    /// on purpose: here the listener taps along to the song again and again,
    /// and a rhythm is anticipated, so taps land closer to the beat. The
    /// video's is a single tap reacting to a line seen on screen, a plain
    /// visual reaction, which is slower.
    static let reactionTime = 0.15
    /// Taps before the offset is first worked out and applied.
    static let requiredTaps = 4
    /// Taps after which a session that still doesn't line up gives up.
    static let maximumTaps = 10
    /// A tap within this many seconds of a line's start (at the offset
    /// being tried) counts as marking that line.
    static let tolerance = 0.35
    /// Lines further than this from where the offset in force puts a tap
    /// are not considered for it: Tap to sync refines timing, it doesn't
    /// search the whole song.
    static let searchWindow = 8.0
    /// No first tap in this long ends the session.
    static let firstTapLimit: TimeInterval = 30
    /// No further tap in this long ends the session.
    static let nextTapLimit: TimeInterval = 15
    /// `SongOffsets` keeps offsets within ±30 s.
    static let offsetLimit = 30.0

    struct Estimate: Equatable {
        /// The song offset (positive shows lyrics earlier), to a hundredth.
        var offset: Double
        /// Taps that agreed on it.
        var used: Int
        var total: Int
    }

    /// The song offset the taps point to, or nil when they don't agree.
    ///
    /// - Parameters:
    ///   - taps: each tap's lyric time with no song offset: the playback time
    ///     of the tap, less the reaction time, plus the global offset.
    ///   - lineStarts: the start of every sung line.
    ///   - prior: the song offset in force while tapping.
    ///
    /// Robust to stray taps: every pairing of a tap with a nearby line gives
    /// a candidate offset; the one the most taps agree with (each line
    /// counted once, so a double tap is one vote) wins, ties going to the
    /// candidate nearest `prior`. The result is the median of the agreeing
    /// taps' own offsets, so a stray tap moves nothing. At least
    /// `requiredTaps` taps, and at least three in five of them, must agree.
    static func estimate(taps: [Double], lineStarts: [Double], prior: Double) -> Estimate? {
        let starts = lineStarts.filter(\.isFinite).sorted()
        let taps = taps.filter(\.isFinite)
        guard taps.count >= requiredTaps, !starts.isEmpty else { return nil }
        var best: (votes: Int, distance: Double, residual: Double, matches: [Double])?
        for tap in taps {
            for start in starts {
                let candidate = start - tap
                guard abs(candidate - prior) <= searchWindow, abs(candidate) <= offsetLimit else { continue }
                let matches = agreeing(taps: taps, starts: starts, offset: candidate)
                let votes = matches.count
                let distance = abs(candidate - prior)
                let residual = matches.reduce(0) { $0 + abs($1 - candidate) }
                if let current = best {
                    if votes < current.votes { continue }
                    if votes == current.votes {
                        // Clearly nearer the offset in force wins; otherwise the tighter fit.
                        if distance > current.distance + tolerance { continue }
                        if abs(distance - current.distance) <= tolerance, residual >= current.residual { continue }
                    }
                }
                best = (votes, distance, residual, matches)
            }
        }
        guard let best, best.votes >= requiredTaps, best.votes * 5 >= taps.count * 3,
              let middle = median(best.matches) else { return nil }
        let offset = (min(offsetLimit, max(-offsetLimit, middle)) * 100).rounded() / 100
        return Estimate(offset: offset, used: best.votes, total: taps.count)
    }

    /// For each line some tap marks at `offset` (within `tolerance`), that
    /// tap's own offset: the closest tap when several mark the same line.
    static func agreeing(taps: [Double], starts: [Double], offset: Double) -> [Double] {
        var byLine: [Int: Double] = [:]
        for tap in taps {
            let shifted = tap + offset
            guard let index = nearest(starts, to: shifted) else { continue }
            let own = starts[index] - tap
            guard abs(own - offset) <= tolerance else { continue }
            if let kept = byLine[index], abs(kept - offset) <= abs(own - offset) { continue }
            byLine[index] = own
        }
        return byLine.keys.sorted().compactMap { byLine[$0] }
    }

    /// The index of the value in sorted `values` nearest `target`.
    static func nearest(_ values: [Double], to target: Double) -> Int? {
        guard !values.isEmpty else { return nil }
        var low = 0, high = values.count
        while low < high {
            let mid = (low + high) / 2
            if values[mid] < target { low = mid + 1 } else { high = mid }
        }
        if low == 0 { return 0 }
        if low == values.count { return values.count - 1 }
        return target - values[low - 1] <= values[low] - target ? low - 1 : low
    }

    static func median(_ values: [Double]) -> Double? {
        guard !values.isEmpty else { return nil }
        let sorted = values.sorted()
        let mid = sorted.count / 2
        return sorted.count.isMultiple(of: 2) ? (sorted[mid - 1] + sorted[mid]) / 2 : sorted[mid]
    }

    /// A tap's lyric time with no song offset.
    static func tapTime(playbackTime: Double, globalOffset: Double, reaction: Double = reactionTime) -> Double {
        playbackTime - reaction + globalOffset
    }

    /// The starts of the sung lines (no instrumental breaks or empty lines).
    static func lineStarts(_ lines: [LyricsFetcher.Line]) -> [Double] {
        lines.filter { !$0.isInstrumental && !$0.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
            .map(\.start)
    }
}

// MARK: - Session

/// One Tap to sync session, start to finish, without timers or windows: the
/// controller feeds it taps and the clock.
struct LyricTapSession: Equatable {
    enum Phase: Equatable {
        case idle
        case listening
        /// The offset was set; `previous` is what Undo goes back to.
        case applied(offset: Double, previous: Double, used: Int, total: Int)
        case failed(Failure)
    }

    enum Failure: Equatable {
        /// No song, or it is paused.
        case notPlaying
        /// The song has no timed lyrics (or they haven't loaded).
        case noLyrics
        /// The taps never agreed on one offset.
        case noAgreement
        /// No tap came in time.
        case timedOut
        /// Esc, or Lunalyr lost the keyboard.
        case cancelled
        /// Another song started mid-session.
        case songChanged
        /// Other lyrics (another source) replaced the ones being tapped.
        case lyricsChanged
    }

    private(set) var phase: Phase = .idle
    private(set) var taps: [Double] = []
    private(set) var lineStarts: [Double] = []
    private(set) var prior = 0.0
    private(set) var startedAt = 0.0
    private(set) var lastTapAt: Double?

    var isListening: Bool { phase == .listening }

    /// Starts listening, or says why it can't.
    mutating func start(lineStarts: [Double], prior: Double, playing: Bool, now: Double) {
        taps = []
        lastTapAt = nil
        self.lineStarts = []
        guard playing else { phase = .failed(.notPlaying); return }
        guard !lineStarts.isEmpty else { phase = .failed(.noLyrics); return }
        self.lineStarts = lineStarts
        self.prior = prior
        startedAt = now
        phase = .listening
    }

    /// A tap at lyric time `tapTime` (`LyricTapSync.tapTime`), at wall time
    /// `now`. Returns the estimate once the taps agree; the phase is then
    /// `.applied`, the caller saves the offset.
    @discardableResult
    mutating func tap(at tapTime: Double, now: Double) -> LyricTapSync.Estimate? {
        guard isListening else { return nil }
        taps.append(tapTime)
        lastTapAt = now
        if let estimate = LyricTapSync.estimate(taps: taps, lineStarts: lineStarts, prior: prior) {
            phase = .applied(offset: estimate.offset, previous: prior, used: estimate.used, total: estimate.total)
            return estimate
        }
        if taps.count >= LyricTapSync.maximumTaps { phase = .failed(.noAgreement) }
        return nil
    }

    /// Ends a session that has waited too long for a tap. True if it ended.
    @discardableResult
    mutating func expire(now: Double) -> Bool {
        guard isListening, let deadline else { return false }
        guard now >= deadline else { return false }
        phase = .failed(taps.count >= LyricTapSync.requiredTaps ? .noAgreement : .timedOut)
        return true
    }

    /// When the session gives up without another tap.
    var deadline: Double? {
        guard isListening else { return nil }
        if let lastTapAt { return lastTapAt + LyricTapSync.nextTapLimit }
        return startedAt + LyricTapSync.firstTapLimit
    }

    mutating func cancel() {
        stop(.cancelled)
    }

    /// Ends a listening session for `reason` (the song paused or changed,
    /// other lyrics arrived).
    mutating func stop(_ reason: Failure) {
        guard isListening else { return }
        phase = .failed(reason)
    }

    /// Back to idle (the confirmation has been seen).
    mutating func dismiss() {
        guard !isListening else { return }
        phase = .idle
    }

    // MARK: Copy

    /// The line on the on-screen panel.
    var message: String {
        switch phase {
        case .idle:
            return ""
        case .listening:
            let left = max(0, LyricTapSync.requiredTaps - taps.count)
            if taps.isEmpty { return "Press any key each time a lyric line starts. Esc stops." }
            if left > 0 { return "\(taps.count) tapped, \(left) more. Esc stops." }
            let more = max(0, LyricTapSync.maximumTaps - taps.count)
            return "\(taps.count) tapped. Keep going: the taps don’t agree yet (up to \(more) more)."
        case .applied(let offset, _, let used, let total):
            let agreed = used == total ? "" : " (\(used) of \(total) taps agreed)"
            return "Lyrics synced: \(TimingNudge.format(offset)) for this song\(agreed)."
        case .failed(.notPlaying):
            // Refused at the start, or the song paused mid-session.
            if lineStarts.isEmpty { return "Play a song first: the taps are timed against it." }
            return "The song paused, so Tap to sync stopped. The timing is unchanged."
        case .failed(.songChanged):
            return "The song changed, so Tap to sync stopped. The timing is unchanged."
        case .failed(.lyricsChanged):
            return "Other lyrics loaded, so Tap to sync stopped. Start again to time these."
        case .failed(.noLyrics):
            return "Tap to sync needs this song’s timed lyrics, and they haven’t loaded."
        case .failed(.noAgreement):
            return "The taps didn’t line up with the lyrics, so the timing is unchanged. Try again."
        case .failed(.timedOut):
            return "No taps, so the timing is unchanged."
        case .failed(.cancelled):
            return "Tap to sync stopped. The timing is unchanged."
        }
    }
}
