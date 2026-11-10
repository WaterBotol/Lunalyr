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
