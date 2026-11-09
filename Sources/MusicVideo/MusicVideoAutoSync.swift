import Foundation

/// What an automatic alignment found.
enum MusicVideoAutoResult: Equatable, Sendable {
    /// The offset (video time − song time), and how it was found.
    case found(Double, MusicVideoOffsetOrigin)
    /// Checked, and nothing usable: remembered for a while.
    case unusable(MusicVideoOffsetOrigin)
    /// Not possible just now (no lyrics yet, offline, capture off): not
    /// remembered.
    case unavailable
}

/// Finds a video's offset by itself. A seam: the live one reads the
/// network and the audio capture; tests pass a fake.
@MainActor
protocol MusicVideoAutoSyncing: AnyObject {
    /// From the YouTube video's captions against the song's timed lyrics.
    func captionOffset(videoID: String, song: MusicVideoSong) async -> MusicVideoAutoResult
    /// From the library video's soundtrack against the song as it plays.
    func audioOffset(url: URL, song: MusicVideoSong) async -> MusicVideoAutoResult
}

@MainActor
final class LiveMusicVideoAutoSync: MusicVideoAutoSyncing {
    private let resolver: MusicVideoResolver
    private let lyrics: @MainActor (MusicVideoSong) -> [MusicVideoLyricLine]?
    private let captureReady: @MainActor () -> Bool
    private let captureActive: @MainActor () -> Bool
    private let songClock: @MainActor () -> (time: Double, playing: Bool)
    private let uptime: () -> TimeInterval
    private let sleep: (TimeInterval) async -> Void
    /// The last video soundtrack read, so a refinement does not decode it again.
    private var soundtrack: (url: URL, onsets: [Float])?

    /// How long to wait for the song's timed lyrics.
    static let lyricsWait: TimeInterval = 30
    /// How long to wait for enough of the song to have been heard.
    static let listenWait: TimeInterval = 60

    init(resolver: MusicVideoResolver,
         lyrics: @escaping @MainActor (MusicVideoSong) -> [MusicVideoLyricLine]? = LiveMusicVideoAutoSync.currentLyrics,
         captureReady: @escaping @MainActor () -> Bool = LiveMusicVideoAutoSync.captureIsRunning,
         captureActive: @escaping @MainActor () -> Bool = LiveMusicVideoAutoSync.captureIsActive,
         songClock: @escaping @MainActor () -> (time: Double, playing: Bool) = {
             (NowPlayingMonitor.shared.playbackTime(), NowPlayingMonitor.shared.isPlaying)
         },
         uptime: @escaping () -> TimeInterval = { ProcessInfo.processInfo.systemUptime },
         sleep: @escaping (TimeInterval) async -> Void = { try? await Task.sleep(for: .seconds($0)) }) {
        self.resolver = resolver
        self.lyrics = lyrics
        self.captureReady = captureReady
        self.captureActive = captureActive
        self.songClock = songClock
        self.uptime = uptime
        self.sleep = sleep
    }

    func captionOffset(videoID: String, song: MusicVideoSong) async -> MusicVideoAutoResult {
        // The lyrics usually arrive with the video; wait for them a little.
        var lines = lyrics(song)
        var waited: TimeInterval = 0
        while lines == nil, waited < Self.lyricsWait {
            await sleep(1)
            waited += 1
            if Task.isCancelled { return .unavailable }
            lines = lyrics(song)
        }
        guard let lines, lines.count >= MusicVideoCaptionAlign.minimumMatches else { return .unavailable }
        switch await resolver.captionCues(videoID: videoID, lyricsLanguage: YouTubeCaptions.language(of: lines)) {
        case .unavailable:
            return .unavailable
        case .tracks(let tracks, _):
            for cues in tracks {
                if let result = MusicVideoCaptionAlign.offset(cues: cues, lines: lines) {
                    return .found(result.offset, .captions)
                }
            }
            return .unusable(.captions)
        }
    }

    func audioOffset(url: URL, song: MusicVideoSong) async -> MusicVideoAutoResult {
        // Only while capture runs, for the visualisers or a brief listen the
        // controller started: nothing is started here, and no permission is
        // asked for.
        guard captureActive() else { return .unavailable }
        let video: [Float]
        if let soundtrack, soundtrack.url == url {
            video = soundtrack.onsets
        } else {
            guard let energies = await MusicVideoSoundtrack.energies(of: url), !Task.isCancelled else { return .unavailable }
            video = MusicVideoAudioAlign.onsets(energies)
            soundtrack = (url, video)
        }
        var waited: TimeInterval = 0
        while waited <= Self.listenWait {
            if Task.isCancelled || !captureActive() { return .unavailable }
            let clock = songClock()
            let now = uptime()
            // A brief listen may still be connecting, or between loud parts.
            if captureReady(), clock.playing,
               let window = MusicVideoAudioAlign.liveWindow(AudioEnvelopeRecorder.shared.snapshot(), now: now,
                                                            songTimeNow: clock.time, songStartedAt: now - clock.time) {
                let result = await Task.detached(priority: .utility) {
                    MusicVideoAudioAlign.align(live: window.onsets, liveStart: window.start, video: video)
                }.value
                return result.map { .found($0.offset, .audio) } ?? .unavailable
            }
            await sleep(1)
            waited += 1
        }
        return .unavailable
    }

    // MARK: Live sources

    /// The song's timed lyric lines in song time (the lyric offsets taken
    /// back off), or nil while they are not loaded for it.
    static func currentLyrics(for song: MusicVideoSong) -> [MusicVideoLyricLine]? {
        let model = LyricsViewModel.shared
        guard let query = model.currentQuery, query.recordingKey == song.key, !model.lines.isEmpty else { return nil }
        let shift = Settings.shared.lyricOffset + SongOffsets.shared.offset(for: query)
        let lines = model.lines.filter { !$0.isInstrumental && $0.credit != true }
            .map { MusicVideoLyricLine(start: $0.start - shift, text: $0.text) }
            .filter { !$0.text.trimmingCharacters(in: .whitespaces).isEmpty }
        return lines.isEmpty ? nil : lines
    }

    /// Capture runs or is starting, or a brief listen is under way.
    static func captureIsActive() -> Bool {
        let capture = AudioReactiveController.shared
        return capture.running || capture.starting || capture.listeningBriefly
    }

    static func captureIsRunning() -> Bool {
        let capture = AudioReactiveController.shared
        return capture.running && capture.state == .receiving
    }
}
