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
