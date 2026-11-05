import SwiftUI

// MARK: - Clock

/// Playback position as the lyrics see it.
///
/// Music only reports where it is every couple of seconds, and each report
/// carries tens of milliseconds of AppleScript round-trip error. Used raw,
/// every re-anchor jumped every word mid-entrance. This follows the reported
/// position by bending the rate a few per cent instead, and adopts a real
/// jump (a seek, a new song, a large offset change) at once -- reporting it,
/// so the lyric layer can cross-fade rather than cut.
@MainActor
final class LyricClock {
    static let shared = LyricClock()
    struct Jump: Equatable {
        let from: Double
        let to: Double
        let wall: Double
    }
    private var state = VCLyricClock()
    private(set) var lastJump: Jump?

    /// Every display and the settings preview evaluate this; a repeated or
    /// slightly older wall time returns the current value unchanged.
    func time(wall: Double, raw: Double, playing: Bool) -> Double {
        let before = state.valid != 0 ? state.time : raw
        if vc_lyric_clock_advance(&state, wall, raw, playing ? 1 : 0) != 0 {
            lastJump = Jump(from: before, to: state.time, wall: wall)
        }
        return state.time
    }
}

// MARK: - Scene memory

/// What the lyric layer is showing. Compared frame to frame, so a change of
/// song or lyric source, or a seek, cross-fades instead of cutting.
struct LyricSceneKey: Equatable {
    enum Kind: Equatable { case idle, loading, plain, missing, synced }
    var kind: Kind
    /// Hash of title and artist.
    var track: Int
    var fingerprint: Int

    static func fingerprint(_ lines: [LyricsFetcher.Line], plain: [String]) -> Int {
        var hasher = Hasher()
        hasher.combine(lines.count)
        hasher.combine(plain.count)
        if let first = lines.first { hasher.combine(first.start); hasher.combine(first.text) }
        if let last = lines.last { hasher.combine(last.end); hasher.combine(last.text) }
        hasher.combine(plain.first)
        return hasher.finalize()
    }
}

/// The data one lyric scene is drawn from, captured by value so an outgoing
/// scene can finish fading after the model has moved on.
struct LyricSceneContent {
    var lines: [LyricsFetcher.Line] = []
    var romanized: [Int: [LyricsFetcher.Word]] = [:]
    var plainLines: [String] = []
    var title = ""
    var artist = ""
    var notFoundAt: Date?
    var idleSince: Date?
    var duration: Double = 0
}

/// Holds the scene being replaced while it fades. Never publishes: it is
/// consulted while the frame is being built, like `RenderMemo`.
@MainActor
final class LyricSceneMemory: ObservableObject {
    struct Outgoing {
        let key: LyricSceneKey
        let content: LyricSceneContent
        /// Playback time of the outgoing scene when it was replaced.
        let time: Double
        /// Wall time the cross-fade began.
        let wall: Double
        private let nextLineStart: Double

        init(key: LyricSceneKey, content: LyricSceneContent, time: Double, wall: Double) {
            self.key = key
            self.content = content
            self.time = time
            self.wall = wall
            let row = LyricLineIndex(lines: content.lines).line(at: time) ?? -1
            nextLineStart = content.lines.indices.contains(row + 1) ? content.lines[row + 1].start : .infinity
        }

        /// Let the line that was on screen keep moving while it fades, up
        /// to the next cue. The incoming scene owns all new lyric entries.
        func renderTime(at wall: Double) -> Double {
            min(time + max(0, wall - self.wall), nextLineStart.nextDown)
        }
    }
    static let fadeOut = 0.45
    static let fadeIn = 0.55
    private(set) var outgoing: Outgoing?
    private var last: (key: LyricSceneKey, content: LyricSceneContent, time: Double)?
    private var lastJumpWall = -Double.infinity
    private var changedAt = -Double.infinity

    struct Transition {
        var outgoing: Outgoing?
        /// 0...1 presence of the outgoing scene.
        var outgoingPresence: Double
        /// 0...1 presence of the incoming scene.
        var incomingPresence: Double
    }

    func observe(key: LyricSceneKey, content: @autoclosure () -> LyricSceneContent, time: Double,
                 wall: Double, jump: LyricClock.Jump?) -> Transition {
        // The system clock stepped back during a cross-fade: it is finished
        // rather than held for as long as the clock went back.
        if changedAt > wall + 1 { changedAt = -.infinity }
        if let outgoing, outgoing.wall > wall + 1 { self.outgoing = nil }
        // The scene being replaced only becomes the outgoing one if it was
        // actually on screen. A song change often arrives in two steps (new
        // title, then cleared lines); the half-faded-in middle step must not
        // displace the old lyrics that are still visibly leaving.
        let shown = changedAt.isFinite ? vc_settle(wall - changedAt, Self.fadeIn) : 1
        if let last, last.key != key {
            let visible = last.key.kind == .synced || last.key.kind == .plain || last.key.kind == .missing
            if last.key.track == key.track && last.key.kind == .synced && key.kind == .synced {
                // A better timing source for the current recording takes
                // over the stage atomically. Both sheets can contain this line.
                outgoing = nil
                changedAt = -.infinity
            } else {
                if shown >= 0.5 || outgoing == nil {
                    outgoing = visible ? Outgoing(key: last.key, content: last.content, time: last.time, wall: wall) : nil
                }
                changedAt = wall
            }
        } else if let jump, jump.wall > lastJumpWall, key.kind == .synced, let last {
            if shown >= 0.5 || outgoing == nil {
                outgoing = Outgoing(key: key, content: last.content, time: jump.from, wall: jump.wall)
            }
            changedAt = jump.wall
        }
        if let jump { lastJumpWall = max(lastJumpWall, jump.wall) }
        if last?.key != key || last == nil {
            last = (key, content(), time)
        } else {
            last?.time = time
            // Readings arrive after the lines; keep the newest copy.
            if key.kind == .synced { last?.content = content() }
        }
        if let outgoing, wall - outgoing.wall > Self.fadeOut + 0.05 { self.outgoing = nil }
        return Transition(outgoing: outgoing,
                          outgoingPresence: outgoing.map { 1 - vc_settle(wall - $0.wall, Self.fadeOut) } ?? 0,
                          incomingPresence: changedAt.isFinite ? vc_settle(wall - changedAt, Self.fadeIn) : 1)
    }
}

// MARK: - Script direction

/// Right-to-left and cursive scripts must be shaped as whole words: drawing
/// one `Text` per character both reverses their order and breaks joining.
/// Rendering rules for scripts; the parser rules live in Sources/Lyrics/LyricScript.swift.
extension LyricScript {
    static func isRightToLeft(_ text: String) -> Bool {
        for scalar in text.unicodeScalars where scalar.properties.isAlphabetic {
            return rightToLeft(scalar.value)
        }
        return false
    }

    /// True when glyphs may be transformed one at a time without breaking
    /// the word: no cursive joining, no combining marks, no complex shaping.
    static func allowsGlyphMotion(_ text: String) -> Bool {
        for scalar in text.unicodeScalars {
            switch scalar.properties.generalCategory {
            case .nonspacingMark, .spacingMark, .enclosingMark: return false
            default: break
            }
            let value = scalar.value
            if value < 0x0250 { continue }                       // Latin and IPA
            if (0x0370...0x052F).contains(value) { continue }   // Greek, Cyrillic
            if (0x0530...0x058F).contains(value) { continue }   // Armenian
            if (0x0590...0x05FF).contains(value) { continue }   // Hebrew (not joined)
            if (0x10A0...0x10FF).contains(value) { continue }   // Georgian
            if (0x1E00...0x1FFF).contains(value) { continue }   // Latin/Greek extended
            if (0x2000...0x2BFF).contains(value) { continue }   // punctuation, symbols
            if (0x3000...0x30FF).contains(value) { continue }   // CJK punctuation, kana
            if (0x3400...0x9FFF).contains(value) { continue }   // CJK ideographs
            if (0xAC00...0xD7AF).contains(value) { continue }   // Hangul syllables
            if (0xF900...0xFAFF).contains(value) { continue }   // CJK compatibility
            if (0xFF00...0xFFEF).contains(value) { continue }   // full width forms
            return false
        }
        return true
    }

    /// A line reads right to left when most of its letters do.
    static func lineIsRightToLeft(_ texts: [String]) -> Bool {
        var rtl = 0, ltr = 0
        for text in texts {
            for scalar in text.unicodeScalars where scalar.properties.isAlphabetic {
                if rightToLeft(scalar.value) { rtl += 1 } else { ltr += 1 }
            }
        }
        return rtl > ltr
    }

    private static func rightToLeft(_ value: UInt32) -> Bool {
        (0x0590...0x08FF).contains(value) || (0xFB1D...0xFDFF).contains(value)
            || (0xFE70...0xFEFF).contains(value) || (0x10800...0x10FFF).contains(value)
            || (0x1E800...0x1EFFF).contains(value)
    }
}

// MARK: - Word motion

enum OrganicMotion {
    /// Time from one onset to the next, which sets how briskly a word moves.
    static func cadence(of words: [LyricsFetcher.Word], at index: Int) -> Double {
        let word = words[index]
        if index + 1 < words.count, words[index + 1].start > word.start {
            return words[index + 1].start - word.start
        }
        return max(word.end - word.start, 0.12)
    }

    /// How much the voice is carrying right now, from the timing alone:
    /// dense, quick delivery and long held notes both read as intensity.
    /// A pure function of time, so a seek lands on the same value.
    static func vocalIntensity(_ words: [LyricsFetcher.Word], at time: Double) -> Double {
        var onsets = 0.0, held = 0.0
        for word in words {
            let distance = abs(word.start - time)
            if distance < 2.5 { onsets += exp(-distance * distance / 1.2) }
            if time >= word.start - 0.1, time <= word.end + 0.3 {
                held = max(held, Easing.smoothstep((word.end - word.start - 0.4) / 1.2))
            }
        }
        return min(1, max(0, (onsets - 0.8) / 3.2) * 0.7 + held * 0.45)
    }

    /// The slow float settled words keep, so a held line is never frozen.
    static func idleAmplitude(composition: LyricComposition, mood: TrackMood, reduceMotion: Bool) -> Double {
        guard !reduceMotion else { return 0 }
        let base = composition.hasConnections ? 1.0 : composition == .echo ? 0.8 : 0.6
        return base * (0.55 + 0.45 * mood.intensity)
    }
}

/// The parts of a word's motion that change every frame -- the idle float
/// and its exit -- applied outside the glyph tree so SwiftUI can keep the
/// rendered ink and move it as a layer.
struct WordFloat: ViewModifier, Equatable {
    var drift: VCWordPose
    var leave: Double
    var size: CGFloat
    var seed: UInt64
    /// Reduce Motion: the exit is a plain fade, with no drift or blur.
    var reduceMotion = false

    static func == (lhs: Self, rhs: Self) -> Bool {
        lhs.drift.x == rhs.drift.x && lhs.drift.y == rhs.drift.y && lhs.drift.angle == rhs.drift.angle
            && lhs.drift.scale == rhs.drift.scale && lhs.leave == rhs.leave && lhs.size == rhs.size
            && lhs.reduceMotion == rhs.reduceMotion
    }

    func body(content: Content) -> some View {
        // Each word leaves along its own slight diagonal, not as a block.
        let lateral = vc_jitter(seed, 12) * 0.10
        let travel = reduceMotion ? 0 : leave
        content
            .scaleEffect(drift.scale * (1 - 0.04 * travel))
            .rotationEffect(.degrees(drift.angle + vc_jitter(seed, 13) * 3 * travel))
            .offset(x: (CGFloat(drift.x) + CGFloat(lateral * travel)) * size,
                    y: (CGFloat(drift.y) - 0.26 * CGFloat(travel)) * size)
            .blur(radius: 2.4 * travel)
            .opacity(1 - leave)
    }
}
