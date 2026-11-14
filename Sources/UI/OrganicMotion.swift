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
    /// Lines translated on this Mac, for lines the source left untranslated.
    var machineTranslations: [Int: String] = [:]
    var plainLines: [String] = []
    var title = ""
    var artist = ""
    var album = ""
    /// The song's cover as the wallpaper has it, for the opening title.
    var artwork: NSImage?
    /// The song time Lunalyr first saw this song at (0 from its start), for
    /// the opening title (`OpeningTitle`): an outgoing scene keeps its own.
    var joined = 0.0
    var notFoundAt: Date?
    var idleSince: Date?
    var duration: Double = 0
    /// The song's seed, for its credits: an outgoing scene keeps its own.
    var seed = 0
    /// Where the voice rests, for the interlude dots: an outgoing scene
    /// keeps its own, so a skip during an intro fades them out.
    var vocalWindows: [VCVocalWindow] = []
    /// Wall time the lyrics started loading, for the loading notice.
    var loadingSince: Double?
    /// No lyrics were found while the Mac was offline.
    var offline = false
}

/// Holds the scenes being replaced while they fade. Never publishes: it is
/// consulted while the frame is being built, like `RenderMemo`.
///
/// Every fade is interruptible: a scene that is replaced part-way through
/// its own entrance leaves from the presence it had reached, never from
/// full strength, and the incoming scene is never pushed back to nothing.
/// Up to two scenes can be leaving at once, so scrubbing through a song
/// cross-fades frame to frame instead of dropping what was on screen.
@MainActor
final class LyricSceneMemory: ObservableObject {
    /// How a scene leaves and the next arrives.
    enum Fade: Equatable {
        /// A new song or a new kind of scene: a soft dissolve with a little
        /// blur (`sceneOut` / `sceneIn`).
        case song
        /// A jump within the same lyrics. Both scenes draw mostly the same
        /// pixels, so the incoming one comes up first and the outgoing one
        /// goes after it: the line never dims or blurs (`seekIn` / `seekOut`).
        case seek
        /// A better lyric source for the same song showing a different line.
        case upgrade

        /// A source upgrade's cross-fade, both ways.
        static let upgradeDuration = 0.25

        /// The outgoing scene's share, 0...1, at `age` seconds.
        func outgoing(_ age: Double) -> Double {
            switch self {
            case .song: return 1 - vc_settle(age, WallpaperMotion.sceneOut)
            case .seek:
                return 1 - Easing.smoothstep((age - WallpaperMotion.seekOutDelay) / WallpaperMotion.seekOut)
            case .upgrade: return 1 - Easing.smoothstep(age / Self.upgradeDuration)
            }
        }

        /// The incoming scene's presence, 0...1, at `age` seconds.
        func incoming(_ age: Double) -> Double {
            switch self {
            case .song: return vc_settle(age, WallpaperMotion.sceneIn)
            case .seek: return Easing.smoothstep(age / WallpaperMotion.seekIn)
            case .upgrade: return Easing.smoothstep(age / Self.upgradeDuration)
            }
        }
    }

    struct Outgoing {
        let key: LyricSceneKey
        let content: LyricSceneContent
        /// Playback time of the outgoing scene when it was replaced.
        let time: Double
        /// Wall time the cross-fade began.
        let wall: Double
        let fade: Fade
        /// How present the scene was when it was replaced: it leaves from there.
        let startPresence: Double
        /// Tells two leaving scenes apart.
        let id: Int
        private let nextLineStart: Double

        init(key: LyricSceneKey, content: LyricSceneContent, time: Double, wall: Double,
             fade: Fade = .song, startPresence: Double = 1, id: Int = 0) {
            self.key = key
            self.content = content
            self.time = time
            self.wall = wall
            self.fade = fade
            self.startPresence = min(1, max(0, startPresence))
            self.id = id
            let row = LyricLineIndex(lines: content.lines).line(at: time) ?? -1
            nextLineStart = content.lines.indices.contains(row + 1) ? content.lines[row + 1].start : .infinity
        }

        /// Let the line that was on screen keep moving while it fades, up
        /// to the next cue. The incoming scene owns all new lyric entries.
        func renderTime(at wall: Double) -> Double {
            min(time + max(0, wall - self.wall), nextLineStart.nextDown)
        }

        /// 0...1 presence at `wall`.
        func presence(at wall: Double) -> Double {
            startPresence * min(1, max(0, fade.outgoing(wall - self.wall)))
        }

        /// A song change dissolves softly; a seek or a source upgrade, which
        /// redraw much the same lines in place, must not blur them.
        var blurs: Bool { fade == .song }
    }

    /// A scene below this presence is dropped rather than kept leaving.
    static let dropBelow = 0.05
    /// At most this many scenes leave at once.
    static let maxLeaving = 2
    /// A source upgrade that changes the line on screen.
    static let upgradeFade = Fade.upgradeDuration

    private(set) var leaving: [Outgoing] = []
    private var last: (key: LyricSceneKey, content: LyricSceneContent, time: Double)?
    private var lastJumpWall = -Double.infinity
    /// When the incoming scene began to arrive, and how; nil once it is
    /// fully present (or never faded).
    private var arriving: (wall: Double, fade: Fade)?
    private var nextID = 0

    struct Transition {
        struct Leaving {
            let scene: Outgoing
            /// 0...1 presence of this leaving scene.
            let presence: Double
        }
        /// The scenes on their way out, oldest first.
        var leaving: [Leaving] = []
        /// 0...1 presence of the incoming scene.
        var incomingPresence: Double

        /// The most visible leaving scene.
        var outgoing: Outgoing? { leaving.max { $0.presence < $1.presence }?.scene }
        /// 0...1 presence of `outgoing`.
        var outgoingPresence: Double { leaving.map(\.presence).max() ?? 0 }
    }

    private func incomingPresence(at wall: Double) -> Double {
        guard let arriving else { return 1 }
        return min(1, max(0, arriving.fade.incoming(wall - arriving.wall)))
    }

    private func push(_ key: LyricSceneKey, _ content: LyricSceneContent, time: Double, wall: Double,
                      fade: Fade, presence: Double, now: Double) {
        leaving.removeAll { $0.presence(at: now) <= Self.dropBelow }
        leaving.append(Outgoing(key: key, content: content, time: time, wall: wall, fade: fade,
                                startPresence: presence, id: nextID))
        nextID &+= 1
        while leaving.count > Self.maxLeaving,
              let faintest = leaving.indices.min(by: { leaving[$0].presence(at: now) < leaving[$1].presence(at: now) }) {
            leaving.remove(at: faintest)
        }
    }

    /// Whether two sheets of the same song show different lines at `time`:
    /// a different text, or the line starting somewhere else.
    static func activeLineDiffers(_ old: LyricSceneContent, _ new: LyricSceneContent, at time: Double) -> Bool {
        let before = LyricLineIndex(lines: old.lines).line(at: time).flatMap { old.lines.indices.contains($0) ? old.lines[$0] : nil }
        let after = LyricLineIndex(lines: new.lines).line(at: time).flatMap { new.lines.indices.contains($0) ? new.lines[$0] : nil }
        guard let before, let after else { return (before == nil) != (after == nil) }
        let text = { (line: LyricsFetcher.Line) in
            line.text.trimmingCharacters(in: .whitespacesAndNewlines).folding(options: [.caseInsensitive], locale: nil)
        }
        return text(before) != text(after) || abs(before.start - after.start) > 0.3
    }

    /// What can be on screen and so must fade rather than vanish. A
    /// loading or idle stage draws its own notice, which fades too.
    static func isVisible(_ kind: LyricSceneKey.Kind) -> Bool {
        switch kind {
        case .synced, .plain, .missing, .loading, .idle: return true
        }
    }

    func observe(key: LyricSceneKey, content: @autoclosure () -> LyricSceneContent, time: Double,
                 wall: Double, jump: LyricClock.Jump?) -> Transition {
        // The system clock stepped back during a cross-fade: it is finished
        // rather than held for as long as the clock went back.
        if let arriving, arriving.wall > wall + 1 { self.arriving = nil }
        leaving.removeAll { $0.wall > wall + 1 }
        // How present the scene being replaced is: it leaves from there. A
        // song change often arrives in two steps (new title, then cleared
        // lines); the barely-arrived middle step is dropped rather than
        // displacing the old lyrics that are still visibly leaving.
        let shown = incomingPresence(at: wall)
        if let last, last.key != key {
            if last.key.track == key.track && last.key.kind == .synced && key.kind == .synced {
                // A better timing source for the current recording takes
                // over the stage in place: both sheets usually show this
                // line, and two copies would double it. Anything still
                // leaving from the song before, and the new song's own
                // entrance, carry on. Only when the new sheet shows another
                // line does it cross-fade, briefly and without blur.
                let next = content()
                if shown >= 0.999, Self.activeLineDiffers(last.content, next, at: time) {
                    push(last.key, last.content, time: last.time, wall: wall, fade: .upgrade, presence: 1, now: wall)
                    arriving = (wall, .upgrade)
                }
            } else {
                if Self.isVisible(last.key.kind), shown > Self.dropBelow {
                    push(last.key, last.content, time: last.time, wall: wall, fade: .song, presence: shown, now: wall)
                }
                arriving = (wall, .song)
            }
        } else if let jump, jump.wall > lastJumpWall, key.kind == .synced, let last {
            // A seek, or the clock adopting a correction: the scene on
            // screen leaves from the presence it reached. Jumps that come
            // while that cross-fade is still in flight (scrubbing) join it:
            // the arriving scene shows the new place and keeps arriving,
            // rather than piling up scenes or restarting from nothing.
            let inFlight = arriving != nil && !leaving.isEmpty
            if !inFlight {
                if shown > Self.dropBelow {
                    push(key, last.content, time: jump.from, wall: jump.wall, fade: .seek, presence: shown, now: wall)
                }
                arriving = (jump.wall, .seek)
            }
        }
        if let jump { lastJumpWall = max(lastJumpWall, jump.wall) }
        if last?.key != key || last == nil {
            last = (key, content(), time)
        } else {
            last?.time = time
            // Readings arrive after the lines; keep the newest copy.
            if key.kind == .synced { last?.content = content() }
        }
        leaving.removeAll { $0.presence(at: wall) <= 0.001 }
        let incoming = incomingPresence(at: wall)
        if incoming >= 1 { arriving = nil }
        return Transition(leaving: leaving.map { .init(scene: $0, presence: $0.presence(at: wall)) },
                          incomingPresence: incoming)
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
    ///
    /// Continuous in time: it springs every word's swell and gesture and
    /// paces the rolling stages, so a step in it jolted the words on stage.
    /// A held note used to count in full from 0.1 s before its onset to
    /// 0.3 s after its end and not at all outside that, which snapped the
    /// word being sung (up to 3% in scale, its place with it) just as it
    /// handed over to a held note, and again as that note stepped back.
    /// Each held note now eases in over the 0.2 s before that window and
    /// out over 0.3 s from 0.15 s after its end; the onsets' sum is taken
    /// relative to its value at the 2.5 s cut-off, so it reaches 0 there
    /// instead of dropping by a sliver.
    ///
    /// `horizon`: how far ahead `words` is sure to hold every word. A
    /// rolling stage's window takes a word in only `LyricLayerCache`'s lead
    /// (at least 1.2 s) before it is sung, so the window's intensity rose by
    /// a step as each one joined; given the horizon, a word yet to come
    /// counts in full up to 0.4 s short of it and eases out to nothing at it.
    static func vocalIntensity(_ words: [LyricsFetcher.Word], at time: Double,
                               horizon: Double = .infinity) -> Double {
        var onsets = 0.0, held = 0.0
        let cutoff = exp(-2.5 * 2.5 / 1.2)
        for word in words {
            let distance = abs(word.start - time)
            if distance < 2.5 {
                let ahead = word.start > time && horizon.isFinite
                    ? 1 - Easing.smoothstep((distance - (horizon - 0.4)) / 0.4) : 1
                onsets += (exp(-distance * distance / 1.2) - cutoff) * ahead
            }
            let gathers = Easing.smoothstep((time - (word.start - 0.3)) / 0.2)
            let releases = 1 - Easing.smoothstep((time - (word.end + 0.15)) / 0.3)
            let presence = gathers * releases
            if presence > 0 {
                held = max(held, presence * Easing.smoothstep((word.end - word.start - 0.4) / 1.2))
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
