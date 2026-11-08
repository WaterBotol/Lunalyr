import AVFoundation
import AppKit
import Foundation

// MARK: - The song's own sound under a lyric card
//
// A card plays its selected lines one after another, skipping the gaps
// between them, so the sound is the stretches of the song those lines are
// sung in, laid end to end. The audio comes from the song's file when the
// player has a plain one (a ripped or purchased track), otherwise it is
// recorded from the speakers while the lines play (see SongAudioRecorder).

/// Audio of the song, with where in the song it begins and ends.
struct SongAudioClip: Equatable {
    var url: URL
    /// Song time of the file's first sample.
    var songStart: Double
    /// Song time of its last; infinite for the whole track.
    var songEnd: Double

    func covers(_ range: ClosedRange<Double>) -> Bool {
        songStart <= range.lowerBound + 0.35 && songEnd >= range.upperBound - 0.35
    }
}

extension LyricCard {
    /// The stretch of the song each page of the card plays, in card order.
    /// Mirrors how `renderFrames` times each page.
    static func songWindows(_ content: Content) -> [(start: Double, duration: Double)] {
        content.pages.enumerated().compactMap { index, page in
            guard let animation = page.animation, let firstInk = animation.firstInk else { return nil }
            let start = min(firstInk, max(animation.line.start, firstInk - 0.12))
            let span = animation.end - start
            guard span.isFinite, span > 0 else { return nil }
            return (start, span + (index == content.pages.count - 1 ? 0.32 : 0))
        }
    }

    /// The part of the song that has to be heard: first window's start to the last one's end.
    static func songRange(_ content: Content) -> ClosedRange<Double>? {
        let windows = songWindows(content)
        guard let first = windows.map(\.start).min(), let last = windows.map({ $0.start + $0.duration }).max(), last > first
        else { return nil }
        return first...last
    }

    /// Lays the song's sound under the finished video at `videoURL`, window by
    /// window, and replaces the file. False, with the file untouched, if it can't.
    @discardableResult
    static func addSongAudio(_ clip: SongAudioClip, windows: [(start: Double, duration: Double)], to videoURL: URL) async -> Bool {
        let source = AVURLAsset(url: clip.url)
        guard !windows.isEmpty, let track = try? await source.loadTracks(withMediaType: .audio).first,
              let sourceLength = try? await source.load(.duration) else { return false }
        return await mux(soundOnto: videoURL) { audio, length in
            var cursor = CMTime.zero
            for window in windows where cursor < length {
                let from = max(0, window.start - clip.songStart)
                let want = min(window.duration, CMTimeGetSeconds(CMTimeSubtract(length, cursor)))
                let available = CMTimeGetSeconds(sourceLength) - from
                let seconds = min(want, available)
                if seconds > 0.02 {
                    try audio.insertTimeRange(CMTimeRange(start: CMTime(seconds: from, preferredTimescale: 600),
                                                          duration: CMTime(seconds: seconds, preferredTimescale: 600)),
                                              of: track, at: cursor)
                }
                cursor = CMTimeAdd(cursor, CMTime(seconds: window.duration, preferredTimescale: 600))
            }
            return true
        }
    }
}

@MainActor
enum SongAudioSource {
    enum Failure: LocalizedError {
        case nothingPlaying, notRecordable(String)
        var errorDescription: String? {
            switch self {
            case .nothingPlaying: return "Play the song first."
            case .notRecordable(let reason): return reason
            }
        }
    }

    private static let plainFileTypes: Set<String> = ["m4a", "mp3", "aac", "wav", "aif", "aiff", "caf", "flac"]

    /// The playing track's own file, when Apple Music has a plain one. Downloaded
    /// Apple Music tracks (protected packages) and streams have none.
    static func localFile() async -> URL? {
        guard NowPlayingMonitor.shared.activePlayer == .appleMusic else { return nil }
        let path: String? = await withCheckedContinuation { continuation in
            DispatchQueue.global(qos: .userInitiated).async {
                let script = NSAppleScript(source: "tell application \"Music\" to return POSIX path of (get location of current track)")
                var error: NSDictionary?
                let result = script?.executeAndReturnError(&error)
                continuation.resume(returning: error == nil ? result?.stringValue : nil)
            }
        }
        guard let path, !path.isEmpty else { return nil }
        let url = URL(fileURLWithPath: path)
        guard plainFileTypes.contains(url.pathExtension.lowercased()) else { return nil }
        let asset = AVURLAsset(url: url)
        guard let playable = try? await asset.load(.isPlayable), playable,
              let protected = try? await asset.load(.hasProtectedContent), !protected else { return nil }
        return url
    }

    /// Plays `range` of the song once and records it. The player is put back
    /// where it was, playing or paused, afterwards.
    static func record(range: ClosedRange<Double>, update: @escaping (String) -> Void) async throws -> SongAudioClip {
        let monitor = NowPlayingMonitor.shared
        guard let track = monitor.current else { throw Failure.nothingPlaying }
        guard !track.isStream, #available(macOS 14.2, *) else {
            throw Failure.notRecordable("This can’t be recorded: it needs macOS 14.2 and a song, not a live stream.")
        }
        let wasPlaying = monitor.isPlaying
        let origin = monitor.playbackTime()
        let preRoll = 1.0
        let recorder = try SongAudioRecorder()
        // After a good recording the song carries on from where it ended; if
        // it fails or is cancelled it goes back to where it was.
        var recorded = false
        defer {
            recorder.stop()
            if !recorded { monitor.seek(to: origin) }
            if !wasPlaying && monitor.isPlaying { monitor.playPause() }
        }
        update("Starting the song…")
        monitor.seek(to: max(0, range.lowerBound - preRoll))
        if !monitor.isPlaying { monitor.playPause() }
        // Wait for the song to be playing near the start.
        var waited = 0.0
        while waited < 8 {
            try Task.checkCancellation()
            if monitor.isPlaying, abs(monitor.playbackTime() - (range.lowerBound - preRoll)) < 2.5 { break }
            try await Task.sleep(nanoseconds: 100_000_000)
            waited += 0.1
        }
        guard monitor.isPlaying else { throw Failure.notRecordable("The song didn’t start playing.") }
        try await recorder.start()
        let songStart = monitor.playbackTime()
        let needed = range.upperBound + 0.5 - songStart
        guard needed > 0, needed < SongAudioRecorder.maximumSeconds else {
            throw Failure.notRecordable("That stretch is too long to record (the limit is two minutes).")
        }
        while monitor.playbackTime() < range.upperBound + 0.5 {
            try Task.checkCancellation()
            guard monitor.isPlaying else { throw Failure.notRecordable("The song stopped while recording.") }
            update(String(format: "Recording the song… %.0f s left", max(0, range.upperBound + 0.5 - monitor.playbackTime())))
            try await Task.sleep(nanoseconds: 150_000_000)
        }
        let file = FileManager.default.temporaryDirectory.appendingPathComponent("Lunalyr song \(UUID().uuidString).caf")
        guard let written = try recorder.finish(to: file) else {
            throw Failure.notRecordable("Nothing was heard. Allow Lunalyr to record system audio, and turn the volume up.")
        }
        recorded = true
        return SongAudioClip(url: written, songStart: songStart, songEnd: songStart + recorder.seconds)
    }
}
