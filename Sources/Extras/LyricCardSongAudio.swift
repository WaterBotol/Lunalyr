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

    /// Whether the file is Lunalyr's own recording (a temporary file it may
    /// delete), not the song's own file in the library.
    var isRecording: Bool { SongAudioSource.isRecording(url) }
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

    /// The song's sound window by window, laid end to end. Where the clip
    /// doesn't cover a window the sound is silent; it fades out over
    /// `LyricCardMix.songCutFade` where it stops, whether the video ends first or carries on.
    nonisolated static func songLayer(_ clip: SongAudioClip, windows: [(start: Double, duration: Double)],
                                      volume: Float = 1) async -> SoundLayer? {
        let source = AVURLAsset(url: clip.url)
        guard !windows.isEmpty, (try? await source.loadTracks(withMediaType: .audio).first) != nil,
              let sourceLength = try? await source.load(.duration) else { return nil }
        let available = CMTimeGetSeconds(sourceLength)
        return SoundLayer(volume: volume, role: .song) { length in
            let rate = LyricCardMix.sampleRate, channels = LyricCardMix.channels
            let frames = Int((length * rate).rounded())
            var output = [Float](repeating: 0, count: frames * channels)
            var cursor = 0.0
            var lastSound = 0
            for window in windows where cursor < length {
                // Any part of the window before the clip starts stays silent.
                let missing = max(0, clip.songStart - window.start)
                let from = max(0, window.start - clip.songStart)
                let want = min(window.duration - missing, length - cursor - missing)
                let seconds = min(want, available - from)
                if seconds > 0.02, let samples = await LyricCardMix.decode(clip.url, from: from, seconds: seconds) {
                    let at = Int(((cursor + missing) * rate).rounded()) * channels
                    let count = min(samples.count, output.count - at)
                    if count > 0 {
                        output.replaceSubrange(at..<(at + count), with: samples[0..<count])
                        lastSound = (at + count) / channels
                    }
                }
                cursor += window.duration
            }
            // Ending with the video, the MP4's own end fade is enough.
            let cut = windows.reduce(0) { $0 + $1.duration } > length + 0.01
            guard lastSound > 0, cut || lastSound < frames - Int(rate * 0.01) else { return Rendered(output) }
            return Rendered(output, fadeOut: (lastSound, LyricCardMix.songCutFade))
        }
    }

    /// Lays the song's sound under the finished video at `videoURL`, window by
    /// window, and replaces the file. False, with the file untouched, if it can't.
    @discardableResult
    static func addSongAudio(_ clip: SongAudioClip, windows: [(start: Double, duration: Double)], to videoURL: URL, volume: Float = 1) async -> Bool {
        guard let layer = await songLayer(clip, windows: windows, volume: volume) else { return false }
        return await mux(soundOnto: videoURL, layers: [layer])
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

    /// Recordings are written to the temporary folder with this prefix.
    nonisolated static let recordingPrefix = "Lunalyr song "

    nonisolated static func recordingURL(in folder: URL = FileManager.default.temporaryDirectory) -> URL {
        folder.appendingPathComponent("\(recordingPrefix)\(UUID().uuidString).caf")
    }

    /// Only Lunalyr's own recordings: never a song file from the library.
    nonisolated static func isRecording(_ url: URL, in folder: URL = FileManager.default.temporaryDirectory) -> Bool {
        url.isFileURL && url.pathExtension == "caf" && url.lastPathComponent.hasPrefix(recordingPrefix)
            && url.deletingLastPathComponent().standardizedFileURL.path == folder.standardizedFileURL.path
    }

    /// Deletes a recording that is no longer used (each is up to two minutes of
    /// uncompressed sound). The song's own file is never touched.
    nonisolated static func discard(_ clip: SongAudioClip?, in folder: URL = FileManager.default.temporaryDirectory) {
        guard let clip, isRecording(clip.url, in: folder) else { return }
        try? FileManager.default.removeItem(at: clip.url)
    }

    /// Deletes recordings left by an earlier run (quit or crash mid-card).
    nonisolated static func discardLeftoverRecordings(in folder: URL = FileManager.default.temporaryDirectory) {
        let files = (try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: nil)) ?? []
        for file in files where isRecording(file, in: folder) { try? FileManager.default.removeItem(at: file) }
    }

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

    /// How a recording is getting on.
    enum RecordingUpdate: Equatable {
        /// Seeking and waiting for the song to play (up to 8 seconds).
        case starting
        /// `elapsed` of the `needed` seconds are recorded; `secondsLeft` is
        /// the countdown, which changes once a second.
        case recording(elapsed: Double, needed: Double, secondsLeft: Int)
    }

    /// The update to publish now, or nil when the countdown shows the same
    /// second as `last`: the window redraws once a second, not every poll.
    nonisolated static func recordingUpdate(elapsed: Double, needed: Double, last: RecordingUpdate?) -> RecordingUpdate? {
        let left = max(0, Int((needed - elapsed).rounded(.up)))
        if case .recording(_, _, let shown) = last, shown == left { return nil }
        return .recording(elapsed: min(max(0, elapsed), needed), needed: needed, secondsLeft: left)
    }

    /// Plays `range` of the song once and records it. The player is put back
    /// where it was, playing or paused, afterwards.
    static func record(range: ClosedRange<Double>, update: @escaping (RecordingUpdate) -> Void) async throws -> SongAudioClip {
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
        update(.starting)
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
        var shown: RecordingUpdate?
        while monitor.playbackTime() < range.upperBound + 0.5 {
            try Task.checkCancellation()
            guard monitor.isPlaying else { throw Failure.notRecordable("The song stopped while recording.") }
            if let next = recordingUpdate(elapsed: monitor.playbackTime() - songStart, needed: needed, last: shown) {
                shown = next
                update(next)
            }
            try await Task.sleep(nanoseconds: 150_000_000)
        }
        let file = recordingURL()
        guard let written = try recorder.finish(to: file) else {
            throw Failure.notRecordable("Nothing was heard. Allow Lunalyr to record system audio, and turn the volume up.")
        }
        recorded = true
        return SongAudioClip(url: written, songStart: songStart, songEnd: songStart + recorder.seconds)
    }
}
