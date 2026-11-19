import Foundation
import os

/// A short record of what moves the lyrics, kept in the system log on this
/// Mac only (Console, subsystem `local.verso.custom`, category `sync`), so
/// a lyric that races or jumps can be traced afterwards: clock jumps from a
/// player reading, and lyrics swapped while a song plays. Nothing leaves
/// the Mac, and nothing is logged in tests.
enum SyncLog {
    private static let logger = Logger(subsystem: "local.verso.custom", category: "sync")

    /// A player reading moved the clock by at least this much while the
    /// song kept playing: anything smaller is eased out unnoticed.
    static let reportedJump: TimeInterval = 0.25

    static func clockMoved(from shown: TimeInterval?, to position: TimeInterval, player: String, track: String,
                           continuous: Bool, estimate: Bool) {
        guard !AppRuntime.isUnitTesting, let shown, abs(position - shown) >= reportedJump else { return }
        logger.notice("clock \(String(format: "%+.2f", position - shown), privacy: .public) s: \(String(format: "%.2f", shown), privacy: .public) → \(String(format: "%.2f", position), privacy: .public) [\(player, privacy: .public), continuous \(continuous, privacy: .public), estimate \(estimate, privacy: .public)] \(track, privacy: .public)")
    }

    static func seek(to position: TimeInterval, track: String) {
        guard !AppRuntime.isUnitTesting else { return }
        logger.notice("seek → \(String(format: "%.2f", position), privacy: .public) \(track, privacy: .public)")
    }

    static func lyrics(_ what: String, source: String, lines: Int, at time: TimeInterval, track: String) {
        guard !AppRuntime.isUnitTesting else { return }
        logger.notice("lyrics \(what, privacy: .public): \(source, privacy: .public), \(lines, privacy: .public) lines, at \(String(format: "%.2f", time), privacy: .public) \(track, privacy: .public)")
    }
}

/// What syncing a YouTube video from its captions decided
/// (`MusicVideoCaptionSync`), one line per decision, in the same `sync`
/// category: whether captions were asked for and arrived (and if not, what
/// came back), how many lyric lines matched, the offset, and whether it was
/// applied, refused, or outranked by the user's own timing. Counts, seconds
/// and HTTP statuses only: no lyric or caption text, no video IDs. The
/// lines are built by the `…Line` functions so tests can read them; nothing
/// is logged in tests.
enum CaptionSyncLog {
    private static let logger = Logger(subsystem: "local.verso.custom", category: "sync")

    static func note(_ line: String) {
        guard !AppRuntime.isUnitTesting else { return }
        logger.notice("\(line, privacy: .public)")
    }

    static func seconds(_ value: Double) -> String { String(format: "%+.2f s", value) }

    static func askedLine(language: String?) -> String {
        language.map { "captions: asked the player for them (language \($0))" }
            ?? "captions: not asked for (not the video showing)"
    }

    static func ignoredLine(_ why: String) -> String { "captions: reply ignored (\(why))" }

    static func otherLanguageLine(got: String?, asking: String) -> String {
        "captions: the player sent a \(got ?? "?") track, not the lyrics' \(asking): asked for \(asking), keeping this one if none comes"
    }

    static let fallbackLine = "captions: no track in the lyrics' language came; using the other one"


    /// `type`: how the player read the reply ("fetch", or an XHR's
    /// response type); one the capture script can't read arrives empty.
    static func emptyLine(status: Int, bytes: Int, type: String = "text") -> String {
        let readable = ["fetch", "text", "json", ""].contains(type)
        let why = !readable ? "unreadable response type \(type)"
            : status >= 400 ? "blocked or refused"
            : bytes == 0 ? "empty (no track, or refused)" : "not timed text"
        return "captions: reply with no captions (HTTP \(status), \(bytes) bytes): \(why)"
    }

    static func fetchedLine(automatic: Bool, language: String?, cues: Int, wordTimed: Bool) -> String {
        "captions: fetched \(automatic ? "YouTube's automatic (auto-only)" : "the uploader's") track"
            + ", language \(language ?? "?"), \(cues) cues, word timing \(wordTimed ? "yes" : "no")"
    }

    static func noneLine(after seconds: Double) -> String {
        "captions: none arrived within \(Int(seconds)) s (the video has none, or the player never loaded them)"
    }

    static let waitingLine = "captions: waiting for the song's timed lyrics to measure"

    /// The measurement and what became of it. `userOffset`: the user's own
    /// timing for the video, which won over a trusted offset.
    static func measuredLine(_ result: MusicVideoCaptionSync.Result, userOffset: Double?) -> String {
        var line = "captions: \(result.matches.count) lines matched, \(result.agreeing) agree"
        if result.agreeing > 0 { line += String(format: " (spread %.2f s)", result.spread) }
        if !result.segments.isEmpty { line += ", \(result.segments.count) segments" }
        guard let offset = result.offset else {
            if !result.matches.isEmpty {
                line += ", best \(seconds(MusicVideoCaptionSync.median(result.matches.map(\.offset))))"
            }
            return line + ", \(result.verdict.summary)"
        }
        line += ", offset \(seconds(offset))"
        if let userOffset { return line + ", skipped: the user's timing \(seconds(userOffset)) wins for this video" }
        return line + ", applied"
    }

    static func userLine(offset: Double, captions: Double) -> String {
        "captions: the user set \(seconds(offset)) over the captions' \(seconds(captions)), kept for this video"
    }
}

/// The desktop player's song clicks in Music, step by step, in the same
/// system log (subsystem `local.verso.custom`, category `queue`): where the
/// clicked song was found, each script of the "Lunalyr Queue" build with its
/// AppleScript error number when it fails, and what played in the end.
/// Each click has a number, so its lines can be told apart. No song titles.
/// Nothing is logged in tests.
enum QueueLog {
    private static let logger = Logger(subsystem: "local.verso.custom", category: "queue")

    static func started(_ click: Int, from origin: String, index: Int?) {
        guard !AppRuntime.isUnitTesting else { return }
        let place = index.map { "playlist position \($0)" } ?? "no playlist position"
        logger.notice("queue #\(click, privacy: .public) started from \(origin, privacy: .public), \(place, privacy: .public)")
    }

    static func prepared(_ click: Int, _ prepared: QueuePrepared) {
        guard !AppRuntime.isUnitTesting else { return }
        logger.notice("queue #\(click, privacy: .public) \(prepared.logText, privacy: .public)")
    }

    static func step(_ click: Int, _ name: String, detail: String) {
        guard !AppRuntime.isUnitTesting else { return }
        logger.notice("queue #\(click, privacy: .public) \(name, privacy: .public): \(detail, privacy: .public)")
    }

    static func step(_ click: Int, _ name: String, failed reason: String) {
        guard !AppRuntime.isUnitTesting else { return }
        logger.error("queue #\(click, privacy: .public) \(name, privacy: .public) failed: \(reason, privacy: .public)")
    }

    static func finished(_ click: Int, _ what: String) {
        guard !AppRuntime.isUnitTesting else { return }
        logger.notice("queue #\(click, privacy: .public) finished: \(what, privacy: .public)")
    }
}

/// What builds the wallpaper in the first seconds of each song, in the same
/// system log (subsystem `local.verso.custom`, category `songstart`), to
/// trace a lyric line seen doubled as a song begins: every lyric layer
/// created or let go, each scene its memory starts fading out or in (and
/// how many are leaving), each time jump it takes in place, the cover's
/// colours landing, each background cross-fade starting (with when it
/// ends) or waiting for a first frame, and the wallpaper and floating
/// windows being made, shown or hidden. Clock jumps and lyric swaps are in
/// `sync`. Events only, never a line per frame, and only for `window`
/// seconds after the playing song changes, so it costs nothing the rest of
/// the time. Nothing is logged in tests.
///
/// Read it with
/// `/usr/bin/log show --last 30m --style compact --predicate 'subsystem == "local.verso.custom"'`
/// (both categories together): each line starts `+s.ss` (seconds since the
/// song changed). `layer #n` is one lyric layer (one per display, and one
/// in the Settings preview); `push` is a scene starting to fade out under
/// the one arriving, with `leaving` the count after it.
enum SongStartLog {
    private static let logger = Logger(subsystem: "local.verso.custom", category: "songstart")

    /// How long after a song change events are logged.
    static let window: TimeInterval = 10

    /// System uptime when the playing song last changed; written on the
    /// main thread, read anywhere (a stale read only skips or adds a line).
    nonisolated(unsafe) private static var songChangedAt: TimeInterval = -.infinity
    private static let enabled = !AppRuntime.isUnitTesting

    /// Whether `now` is within `window` of a song change at `changedAt`.
    static func isOpen(changedAt: TimeInterval, now: TimeInterval) -> Bool {
        now >= changedAt && now - changedAt < window
    }

    /// Seconds since the song changed, while the window is open.
    private static var age: TimeInterval? {
        guard enabled else { return nil }
        let now = ProcessInfo.processInfo.systemUptime
        return isOpen(changedAt: songChangedAt, now: now) ? now - songChangedAt : nil
    }

    /// The playing song changed: opens the window.
    static func songChanged(title: String?) {
        guard enabled else { return }
        songChangedAt = ProcessInfo.processInfo.systemUptime
        logger.notice("+0.00 song \(title ?? "(none)", privacy: .public)")
    }

    /// Logs `message` if the window is open.
    static func note(_ message: @autoclosure () -> String) {
        guard let age else { return }
        let text = message()
        logger.notice("\(String(format: "%+.2f", age), privacy: .public) \(text, privacy: .public)")
    }
}

/// The queue look-ahead (`MusicQueueReader`, `QueueLookAhead`), in the same
/// system log (subsystem `local.verso.custom`, category `lookahead`): the
/// helper starting, failing and backing off, how many upcoming songs were
/// read, and what was looked up ahead and whether a starting song had been.
/// Counts and outcomes only, never song titles. Nothing is logged in tests.
enum LookAheadLog {
    private static let logger = Logger(subsystem: "local.verso.custom", category: "lookahead")

    static func note(_ message: @autoclosure () -> String) {
        guard !AppRuntime.isUnitTesting else { return }
        let text = message()
        logger.notice("\(text, privacy: .public)")
    }

    static func failure(_ message: @autoclosure () -> String) {
        guard !AppRuntime.isUnitTesting else { return }
        let text = message()
        logger.error("\(text, privacy: .public)")
    }
}

/// What a generated wallpaper measured behind the lyrics (`StageMeter`),
/// logged when it changes: category `backdrop`.
enum BackdropLog {
    private static let logger = Logger(subsystem: "local.verso.custom", category: "backdrop")
    private static let enabled = !AppRuntime.isUnitTesting

    static func drawn(_ value: Double, layer: String) {
        guard enabled else { return }
        logger.log("drawn \(value, format: .fixed(precision: 2), privacy: .public) behind the stage on \(layer, privacy: .public)")
    }
}
