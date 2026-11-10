import AppKit
import Combine
import CoreGraphics
import Foundation

/// Finds the playing song's video while "Music video" is the wallpaper,
/// and says what it found for Settings. Looks nothing up while another
/// wallpaper type is chosen.
@MainActor
final class MusicVideoController: ObservableObject {
    static let shared: MusicVideoController = {
        let resolver = MusicVideoResolver(
            library: AppleMusicVideoLibrary(mayRead: {
                await MainActor.run {
                    let monitor = NowPlayingMonitor.shared
                    return monitor.runningPlayers.contains(.appleMusic) && monitor.automation[.appleMusic] == .granted
                }
            }),
            playability: AVMusicVideoPlayability(), http: URLSessionMusicVideoHTTP(),
            cache: MusicVideoCache(defaults: .standard), links: MusicVideoLinks(defaults: .standard))
        return MusicVideoController(resolver: resolver, offsets: MusicVideoOffsets(defaults: .standard),
                                    autoSync: AppRuntime.isUnitTesting ? nil : LiveMusicVideoAutoSync(resolver: resolver),
                                    listener: AppRuntime.isUnitTesting ? nil : LiveMusicVideoListener(),
                                    cutSync: !AppRuntime.isUnitTesting)
    }()

    enum Status: Equatable {
        /// Another wallpaper type is chosen.
        case off
        /// Nothing is playing.
        case idle
        case searching(title: String)
        case showing(MusicVideoSource, title: String)
        case missing(MusicVideoMiss, title: String)
    }

    @Published private(set) var status: Status = .off
    /// The pasted link for the playing song looks like a still image (the
    /// cover over the audio). It plays anyway: the user chose it.
    @Published private(set) var pastedLooksStill = false
    static let pastedStillNote = "This video looks like a still image, probably the cover over the audio. It plays because you chose it."
    let offsets: MusicVideoOffsets
    private let resolver: MusicVideoResolver
    private var song: MusicVideoSong?
    /// The song's key, or a marker for radio and adverts, so a change
    /// between those and nothing playing is noticed too.
    private var playingKey: String?
    private var enabled = false
    private var online = false
    private var lookup: Task<Void, Never>?
    private var stillCheck: Task<Void, Never>?
    private var retry: Task<Void, Never>?
    /// Each song is retried once after a passing failure, not every minute.
    private var retried: String?
    private var cancellables: Set<AnyCancellable> = []
    /// Seconds before a lookup that failed for a passing reason is retried.
    var retryDelay: TimeInterval = 45

    /// An automatic offset is being looked for.
    @Published private(set) var syncing = false
    private let autoSync: MusicVideoAutoSyncing?
    private let now: () -> Date
    private let sleep: (TimeInterval) async -> Void
    private var syncTask: Task<Void, Never>?
    /// An audio offset is checked once more, this much later in the song.
    var refineDelay: TimeInterval = 60

    /// Capture runs briefly for a library video's alignment.
    @Published private(set) var listening = false
    private let listener: MusicVideoBriefListening?
    /// Song and video pairs listened to already: once each.
    private var listened: Set<String> = []
    private var listenToken: UUID?
    /// A library video's brief listen that could not start (paused, say):
    /// tried once more when playback resumes.
    private var missedListen: String?
    private var resumeRetried: Set<String> = []
    /// The longest a brief listen runs.
    static let listenLimit: TimeInterval = 25

    /// Tap to sync.
    @Published private(set) var tapState: MusicVideoTapState = .idle
    /// This song and video's taps, as offsets, averaged into the user's.
    private var taps: [Double] = []
    private let songClock: @MainActor () -> (time: Double, playing: Bool)
    private let lyrics: @MainActor (MusicVideoSong) -> [MusicVideoLyricLine]?
    private let uptime: () -> TimeInterval
    /// The video's own position, as its player last reported it.
    private var videoSample: (video: String, time: Double, playing: Bool, at: TimeInterval)?
    private var keyMonitor: Any?
    private var tapExpiry: Task<Void, Never>?

    /// The video's cuts are being lined up with the beat (YouTube).
    @Published private(set) var cutSyncing = false
    private let cutSyncEnabled: Bool
    private let captureActive: @MainActor () -> Bool
    private let audioFrames: () -> [AudioEnvelopeRecorder.Frame]
    /// Song and video pairs whose cuts were tried this session: once each.
    private var cutTried: Set<String> = []
    /// The pair being tried, and whether a brief listen was started for it.
    private var cutPair: String?
    private var cutListening = false

    init(resolver: MusicVideoResolver, offsets: MusicVideoOffsets, autoSync: MusicVideoAutoSyncing? = nil,
         listener: MusicVideoBriefListening? = nil,
         cutSync: Bool = false,
         captureActive: @escaping @MainActor () -> Bool = LiveMusicVideoAutoSync.captureIsActive,
         audioFrames: @escaping () -> [AudioEnvelopeRecorder.Frame] = { AudioEnvelopeRecorder.shared.snapshot() },
         songClock: @escaping @MainActor () -> (time: Double, playing: Bool) = {
             (NowPlayingMonitor.shared.playbackPosition(), NowPlayingMonitor.shared.isPlaying)
         },
         lyrics: @escaping @MainActor (MusicVideoSong) -> [MusicVideoLyricLine]? = LiveMusicVideoAutoSync.currentLyrics,
         uptime: @escaping () -> TimeInterval = { ProcessInfo.processInfo.systemUptime },
         now: @escaping () -> Date = Date.init,
         sleep: @escaping (TimeInterval) async -> Void = { try? await Task.sleep(for: .seconds($0)) }) {
        self.resolver = resolver
        self.offsets = offsets
        self.autoSync = autoSync
        self.listener = listener
        self.cutSyncEnabled = cutSync
        self.captureActive = captureActive
        self.audioFrames = audioFrames
        self.songClock = songClock
        self.lyrics = lyrics
        self.uptime = uptime
        self.now = now
        self.sleep = sleep
    }

    /// Follows the playing song and the settings. Started by the app
    /// delegate, never in the test host.
    func start() { start(monitor: .shared, settings: .shared) }

    func start(monitor: NowPlayingMonitor, settings: Settings) {
        guard cancellables.isEmpty else { return }
        monitor.$isPlaying.removeDuplicates()
            .receive(on: DispatchQueue.main)
            .sink { [weak self] playing in
                MainActor.assumeIsolated { self?.playbackChanged(playing: playing) }
            }
            .store(in: &cancellables)
        Publishers.CombineLatest3(monitor.$current.removeDuplicates(by: Self.sameSong),
                                  settings.$backgroundMode.removeDuplicates(),
                                  settings.$musicVideoOnline.removeDuplicates())
            .receive(on: DispatchQueue.main)
            .sink { [weak self] track, mode, online in
                MainActor.assumeIsolated {
                    self?.update(track: track, enabled: mode == .musicVideo, online: online)
                }
            }
            .store(in: &cancellables)
    }

    nonisolated private static func sameSong(_ a: NowPlayingMonitor.Track?, _ b: NowPlayingMonitor.Track?) -> Bool {
        switch (a, b) {
        case (nil, nil): return true
        case let (a?, b?): return a.isSameTrack(as: b) && a.isStream == b.isStream && a.isAdvert == b.isAdvert
        default: return false
        }
    }

    /// Whether `source` is the video found for the playing song. A surface
    /// still fading out after a song change is not.
    func isShowing(_ source: MusicVideoSource) -> Bool {
        if case .showing(let showing, _) = status { return showing == source }
        return false
    }

    /// The video to show for `track`, when one was found for it.
    func source(for track: NowPlayingMonitor.Track?) -> MusicVideoSource? {
        guard let track, case .showing(let source, _) = status, let song,
              MusicVideoSong(track).key == song.key else { return nil }
        return source
    }

    func update(track: NowPlayingMonitor.Track?, enabled: Bool, online: Bool) {
        let next = track.flatMap { $0.isStream || $0.isAdvert ? nil : MusicVideoSong($0) }
        let isAdvertOrStream = track.map { $0.isStream || $0.isAdvert } ?? false
        let playing = next?.key ?? (isAdvertOrStream ? "\u{0}not-a-song" : nil)
        let changed = playing != playingKey || enabled != self.enabled || online != self.online
        self.enabled = enabled
        self.online = online
        guard changed else { return }
        playingKey = playing
        song = next
        stillCheck?.cancel()
        pastedLooksStill = false
        lookup?.cancel()
        retry?.cancel()
        stopAutoSync()
        endCutSync()
        resetTaps()
        guard enabled else { status = .off; return }
        guard let next else {
            status = isAdvertOrStream ? .missing(.notASong, title: track?.title ?? "") : .idle
            return
        }
        status = .searching(title: next.title)
        start(next)
    }

    private func start(_ song: MusicVideoSong) {
        let online = self.online
        lookup = Task { [weak self] in
            guard let self else { return }
            let result = await self.resolver.resolve(song, online: online)
            guard !Task.isCancelled, self.song?.key == song.key else { return }
            self.apply(result, for: song)
        }
    }

    private func apply(_ result: MusicVideoResolution, for song: MusicVideoSong) {
        switch result {
        case .found(let source):
            status = .showing(source, title: song.title)
            if case .youTube(let id) = source, resolver.links.videoID(for: song.key) == id { checkPastedStill(id, song) }
            startAutoSync(song, source, force: false)
        case .missing(let miss):
            status = .missing(miss, title: song.title)
            if (miss == .rateLimited || miss == .lookupFailed) && retried != song.key { scheduleRetry(song) }
        }
    }

    private func scheduleRetry(_ song: MusicVideoSong) {
        retry?.cancel()
        let delay = retryDelay
        retry = Task { [weak self] in
            try? await Task.sleep(for: .seconds(delay))
            guard let self, !Task.isCancelled, self.enabled, self.song?.key == song.key else { return }
            self.retried = song.key
            self.status = .searching(title: song.title)
            self.start(song)
        }
    }

    /// The note only; the pasted video is never replaced.
    private func checkPastedStill(_ id: String, _ song: MusicVideoSong) {
        stillCheck?.cancel()
        stillCheck = Task { [weak self] in
            guard let self else { return }
            let still = await self.resolver.pastedLooksStill(id)
            guard !Task.isCancelled, self.song?.key == song.key, self.resolver.links.videoID(for: song.key) == id else { return }
            self.pastedLooksStill = still == true
        }
    }

    /// Waits for the lookup in flight (and a pasted link's still check,
    /// and the automatic offset), for tests.
    func settle() async {
        await lookup?.value
        await stillCheck?.value
        await syncTask?.value
    }

    /// The player could not show the video, and the wallpaper falls back
    /// for this song. Only a report about the video on screen counts.
    /// - YouTube refused it for good: the song's next official video is
    ///   tried (a few at most), then "can't be embedded" is remembered;
    ///   never for the user's own pasted link (kept, so they can fix it).
    /// - Something passing (offline, no answer in time, player error 5):
    ///   nothing is remembered, and the song is tried once more later.
    /// - The library's own file would not play: said as such.
    func reportPlaybackFailure(of source: MusicVideoSource, _ failure: MusicVideoPlaybackFailure = .refused) {
        guard let song, case .showing(let showing, _) = status, showing == source else { return }
        switch (source, failure) {
        case (.local, _):
            status = .missing(.localFileFailed, title: song.title)
        case (.youTube, .transient):
            status = .missing(.playerUnreachable, title: song.title)
            if retried != song.key { scheduleRetry(song) }
        case (.youTube(let id), .refused):
            status = .missing(.notEmbeddable, title: song.title)
            // The user's own link is kept as it is, so they can fix it.
            guard resolver.links.videoID(for: song.key) != id else { return }
            // A found video: the song's next official video, if it has one.
            if resolver.markNotEmbeddable(song, videoID: online ? id : nil) { relook(song) }
        }
    }

    /// Whether a surface shows `source` or falls back. The Settings preview
    /// shows it (a still for YouTube). On the desktop, "Show the video on"
    /// decides; until the user picks, it follows "Heavy scenes on main
    /// display only", as the web scenes do. A picked display that is
    /// unplugged hands the video to the main display.
    nonisolated static func placement(source: MusicVideoSource?, preview: Bool, heavyScenesOnMainDisplayOnly: Bool,
                                      choice: MusicVideoDisplayChoice = .followHeavyScenes,
                                      displays: [MusicVideoDisplay] = [],
                                      display: CGDirectDisplayID?, mainDisplay: CGDirectDisplayID) -> MusicVideoSource? {
        guard let source else { return nil }
        if preview { return source }
        guard let display else { return source }
        return MusicVideoDisplays.shows(choice, heavyScenesOnMainDisplayOnly: heavyScenesOnMainDisplayOnly,
                                        display: display, main: mainDisplay, connected: displays) ? source : nil
    }

    // MARK: Open on YouTube

    /// The YouTube video on screen for the playing song, nil for a library
    /// video or none.
    var showingYouTubeID: String? {
        if case .showing(.youTube(let id), _) = status { return id }
        return nil
    }

    /// A watch link for a video at the song's position, offset included:
    /// `https://www.youtube.com/watch?v=<id>&t=<seconds>s` (no `t` at 0).
    nonisolated static func watchURL(videoID: String, songTime: Double, offset: Double) -> URL? {
        guard YouTubeVideoID.isValid(videoID) else { return nil }
        let at = MusicVideoTiming.videoTime(songTime: songTime, offset: offset, videoDuration: nil) ?? 0
        let seconds = Int(max(0, at.rounded(.down)))
        var components = URLComponents(string: "https://www.youtube.com/watch")!
        components.queryItems = [URLQueryItem(name: "v", value: videoID)]
            + (seconds > 0 ? [URLQueryItem(name: "t", value: "\(seconds)s")] : [])
        return components.url
    }

    /// The video on screen, where the song is now.
    var watchURL: URL? {
        showingYouTubeID.flatMap {
            Self.watchURL(videoID: $0, songTime: NowPlayingMonitor.shared.playbackPosition(), offset: currentOffset)
        }
    }

    /// Opens the video on screen in the browser. Only ever called from the
    /// user's click on "Open video on YouTube"; never inside the test host.
    func openOnYouTube(open: (URL) -> Void = { NSWorkspace.shared.open($0) }) {
        guard !AppRuntime.isUnitTesting, let url = watchURL else { return }
        open(url)
    }

    // MARK: Pasted links

    /// The YouTube link pasted for the playing song, as a watch link.
    var pastedLink: String? {
        resolver.links.videoID(for: song?.key).map { "https://www.youtube.com/watch?v=\($0)" }
    }

    /// Uses a pasted YouTube link (or bare video ID) for the playing song.
    /// False when it is not one.
    @discardableResult
    func usePastedLink(_ text: String) -> Bool {
        guard let song, resolver.links.set(text, for: song.key) else { return false }
        resolver.cache.remove(song.key)
        relook(song)
        return true
    }

    /// Forgets the pasted link and whatever was remembered for the song
    /// (a "can't be embedded" left by that link would otherwise block the
    /// search for a week).
    func clearPastedLink() {
        guard let song else { return }
        resolver.links.remove(song.key)
        resolver.cache.remove(song.key)
        relook(song)
    }

    private func relook(_ song: MusicVideoSong) {
        stillCheck?.cancel()
        pastedLooksStill = false
        lookup?.cancel()
        retry?.cancel()
        stopAutoSync()
        endCutSync()
        resetTaps()
        objectWillChange.send()
        guard enabled else { return }
        status = .searching(title: song.title)
        start(song)
    }

    // MARK: Offsets

    /// The video on screen for the playing song, as an offset cache key.
    private var showingVideoKey: String? {
        guard case .showing(let source, _) = status else { return nil }
        return MusicVideoOffsets.videoKey(source)
    }

    /// How far into the video the song starts, for the playing song: the
    /// user's nudge, else the automatic offset for this video, else the
    /// length estimate, else 0.
    var currentOffset: Double { offsets.offset(for: song?.key, video: showingVideoKey, now: now()) }

    /// Where `currentOffset` came from.
    var offsetOrigin: MusicVideoOffsetOrigin { offsets.origin(for: song?.key, video: showingVideoKey, now: now()) }

    func offset(for track: NowPlayingMonitor.Track?) -> Double {
        let key = track.map { MusicVideoSong($0).key }
        return offsets.offset(for: key, video: key == song?.key ? showingVideoKey : nil, now: now())
    }

    /// The user's nudge, from the offset in force; it wins from then on,
    /// for the video on screen.
    func nudgeOffset(by delta: Double) {
        guard let song else { return }
        offsets.nudge(by: delta, for: song.key, video: showingVideoKey, now: now())
        endCutSync()
        resetTaps()
        objectWillChange.send()
    }

    /// The menu's fine step: "Video earlier" / "Video later".
    nonisolated static let fineNudge = 0.25

    /// The video's moments come `fineNudge` sooner (it lagged the music).
    func videoEarlier() { nudgeOffset(by: Self.fineNudge) }
    /// The video's moments come `fineNudge` later (it ran ahead).
    func videoLater() { nudgeOffset(by: -Self.fineNudge) }

    /// Drops the user's nudge: back to the automatic offset, looked for
    /// again if there is none yet.
    func resetOffset() {
        guard let song else { return }
        offsets.clearManual(song.key)
        resetTaps()
        objectWillChange.send()
        if case .showing(let source, _) = status { startAutoSync(song, source, force: false) }
    }

    /// "Re-sync": forgets the user's nudge and what was found, and looks
    /// for the offset again.
    func resync() {
        guard let song, case .showing(let source, _) = status else { return }
        offsets.clearManual(song.key)
        offsets.forgetAutomatic(song.key)
        endCutSync()
        if let video = MusicVideoOffsets.videoKey(source) { cutTried.remove(song.key + "\u{0}" + video) }
        resetTaps()
        objectWillChange.send()
        startAutoSync(song, source, force: true)
    }

    var hasSong: Bool { song != nil }
    var canResync: Bool { showingVideoKey != nil && autoSync != nil }

    /// A video's length, from its player: the starting guess for its intro
    /// when nothing better is known.
    func noteVideoDuration(_ duration: Double, of source: MusicVideoSource) {
        guard let song, case .showing(let showing, _) = status, showing == source,
              let video = MusicVideoOffsets.videoKey(source) else { return }
        let estimate = MusicVideoDurationEstimate.intro(videoDuration: duration, songDuration: song.duration)
        let before = currentOffset
        offsets.setEstimate(estimate, for: song.key, video: video)
        if currentOffset != before { objectWillChange.send() }
    }

    /// Playback resumed: a library video whose brief listen could not start
    /// gets it now, once.
    func playbackChanged(playing: Bool) {
        guard playing, let pair = missedListen, let song, case .showing(let source, _) = status,
              let video = MusicVideoOffsets.videoKey(source), pair == song.key + "\u{0}" + video,
              !offsets.hasManual(song.key, video: video), offsets.automaticEntry(for: song.key, video: video, now: now()) == nil
        else { return }
        missedListen = nil
        resumeRetried.insert(pair)
        startAutoSync(song, source, force: false)
    }

    private func stopAutoSync() {
        syncTask?.cancel()
        syncTask = nil
        syncing = false
    }

    /// Whether to listen briefly for a library video: a listener, not done
    /// for this song and video yet (unless `force`, Re-sync), and allowed
    /// just now (music playing, permission granted, power not spared).
    private func shouldListen(_ pair: String, force: Bool) -> Bool {
        guard let listener, force || !listened.contains(pair) else { return false }
        return listener.mayListen
    }

    /// Runs capture for at most `listenLimit` while the soundtrack is lined
    /// up with the song, then stops it, whatever the outcome.
    private func listenBriefly(url: URL, song: MusicVideoSong, pair: String,
                               autoSync: MusicVideoAutoSyncing) async -> MusicVideoAutoResult {
        guard let listener, listener.start(for: Self.listenLimit) else { return .unavailable }
        listened.insert(pair)
        let token = UUID()
        listenToken = token
        listening = true
        let attempt = Task { await autoSync.audioOffset(url: url, song: song) }
        let limit = Self.listenLimit, sleep = self.sleep
        let watchdog = Task {
            await sleep(limit)
            attempt.cancel()
        }
        let result = await withTaskCancellationHandler { await attempt.value } onCancel: { attempt.cancel() }
        watchdog.cancel()
        // Unless a newer listen (Re-sync) has taken over meanwhile.
        if listenToken == token {
            listener.stop()
            listening = false
            listenToken = nil
        }
        return result
    }

    /// Looks for the video's offset once: captions for YouTube (only with
    /// online lookups on), the soundtrack for a library video. Skipped when
    /// the user set the offset or one is remembered for this video, unless
    /// `force` (Re-sync).
    private func startAutoSync(_ song: MusicVideoSong, _ source: MusicVideoSource, force: Bool) {
        stopAutoSync()
        guard let autoSync, let video = MusicVideoOffsets.videoKey(source) else { return }
        if !force && (offsets.hasManual(song.key, video: video) || offsets.automaticEntry(for: song.key, video: video, now: now()) != nil) {
            return
        }
        if case .youTube = source, !online { return }
        syncing = true
        let pair = song.key + "\u{0}" + video
        var briefly = false
        if case .local = source {
            briefly = shouldListen(pair, force: force)
            missedListen = !briefly && listener != nil && !listened.contains(pair) && !resumeRetried.contains(pair) ? pair : nil
        } else {
            missedListen = nil
        }
        syncTask = Task { [weak self] in
            let result: MusicVideoAutoResult
            switch source {
            case .youTube(let id): result = await autoSync.captionOffset(videoID: id, song: song)
            case .local(let url):
                if briefly, let self {
                    result = await self.listenBriefly(url: url, song: song, pair: pair, autoSync: autoSync)
                } else {
                    result = await autoSync.audioOffset(url: url, song: song)
                }
            }
            guard let self, !Task.isCancelled, self.song?.key == song.key, self.showingVideoKey == video else { return }
            self.syncing = false
            self.store(result, song: song, video: video)
            // A brief listen is not repeated: capture is off again by now.
            if !briefly, case .found(let first, .audio) = result, case .local(let url) = source {
                await self.refine(first, url: url, song: song, video: video, autoSync: autoSync)
            }
        }
    }

    private func store(_ result: MusicVideoAutoResult, song: MusicVideoSong, video: String) {
        switch result {
        case .found(let offset, let origin):
            offsets.storeAutomatic(offset, origin: origin, for: song.key, video: video, now: now())
        case .unusable(let origin):
            offsets.storeAutomatic(nil, origin: origin, for: song.key, video: video, now: now())
        case .unavailable:
            break
        }
        objectWillChange.send()
    }

    /// Once more, later in the song, with a fresh stretch of audio. Kept
    /// when it agrees roughly (a refinement), not when it is somewhere else
    /// entirely (a repeated chorus fooled one of them).
    static let refineAgreement = 2.0

    private func refine(_ first: Double, url: URL, song: MusicVideoSong, video: String,
                        autoSync: MusicVideoAutoSyncing) async {
        await sleep(refineDelay)
        guard !Task.isCancelled, self.song?.key == song.key, showingVideoKey == video,
              !offsets.hasManual(song.key, video: video) else { return }
        let second = await autoSync.audioOffset(url: url, song: song)
        guard !Task.isCancelled, self.song?.key == song.key, showingVideoKey == video,
              !offsets.hasManual(song.key, video: video),
              case .found(let offset, _) = second, abs(offset - first) <= Self.refineAgreement else { return }
        store(second, song: song, video: video)
    }

    /// The words beside the nudge buttons.
    nonisolated static func timingLabel(origin: MusicVideoOffsetOrigin, syncing: Bool, listening: Bool = false,
                                        watchingCuts: Bool = false) -> String? {
        if watchingCuts && origin != .manual { return Self.watchingCutsLabel }
        if listening && origin != .manual { return Self.listeningLabel }
        switch origin {
        case .manual: return "Offset set by you"
        case .captions, .audio, .cuts: return "Synced automatically"
        case .estimated: return syncing ? "Estimated; syncing…" : "Estimated"
        case .none: return syncing ? "Syncing…" : nil
        }
    }

    nonisolated static let listeningLabel = "Listening to line up the video…"
    nonisolated static let watchingCutsLabel = "Lining up the video’s cuts with the beat…"

    // MARK: Tap to sync

    /// Tap to sync is offered while a video shows for the playing song.
    var canTapSync: Bool { song != nil && showingVideoKey != nil }

    /// The video's own position, from its player's sync loop, read at
    /// uptime `at` (by default now).
    func noteVideoSample(time: Double, playing: Bool, of source: MusicVideoSource, at: TimeInterval? = nil) {
        guard time.isFinite, let video = MusicVideoOffsets.videoKey(source), video == showingVideoKey else { return }
        videoSample = (video, time, playing, at ?? uptime())
    }

    /// Where the video is now: its player's last report, moved on while it
    /// plays; without a recent one, where the sync loop keeps it.
    private func videoTimeNow(songTime: Double) -> Double {
        if let sample = videoSample, sample.video == showingVideoKey {
            let age = uptime() - sample.at
            if age >= 0, age < 1.5 { return sample.time + (sample.playing ? age : 0) }
        }
        return songTime + currentOffset
    }

    /// The button: arms, takes the tap, or arms again to refine.
    func tapSyncButton() {
        if case .armed = tapState { tap() } else { beginTapSync() }
    }

    /// Waits for the tap: a key, or the button again.
    func beginTapSync() {
        guard let song, canTapSync else { return }
        guard let lines = lyrics(song), !lines.isEmpty else { tapState = .failed(.noLyrics); return }
        let clock = songClock()
        guard clock.playing else { tapState = .failed(.notPlaying); return }
        tapState = .armed(firstLine: MusicVideoTapSync.armedBeforeFirstLine(songTime: clock.time, lines: lines))
        watchKeys()
    }

    /// The tap: the singing starts (or the next line is sung) in the video
    /// now. The offset becomes the user's, averaged with this song and
    /// video's earlier taps.
    func tap() {
        guard case .armed(let firstLine) = tapState else { return }
        stopWatchingKeys()
        guard let song, canTapSync else { tapState = .idle; return }
        guard let lines = lyrics(song), !lines.isEmpty else { tapState = .failed(.noLyrics); return }
        let clock = songClock()
        guard clock.playing else { tapState = .failed(.notPlaying); return }
        let marked = videoTimeNow(songTime: clock.time) - MusicVideoTapSync.reactionTime
        // The song time the video shows, by the offset in force.
        let reference = marked - currentOffset
        guard let line = MusicVideoTapSync.line(in: lines, reference: reference, firstLine: firstLine) else {
            tapState = .failed(.noLyrics)
            return
        }
        taps.append(MusicVideoTapSync.offset(videoTimeAtTap: marked, lineStart: line.start, reaction: 0))
        let value = MusicVideoTapSync.averaged(taps) ?? 0
        offsets.set(value, for: song.key, video: showingVideoKey)
        endCutSync()
        tapState = .done(offset: currentOffset, taps: taps.count, firstLine: firstLine)
        objectWillChange.send()
    }

    func cancelTapSync() {
        stopWatchingKeys()
        if case .armed = tapState { tapState = taps.isEmpty ? .idle : .done(offset: currentOffset, taps: taps.count, firstLine: false) }
    }

    private func resetTaps() {
        stopWatchingKeys()
        taps = []
        if tapState != .idle { tapState = .idle }
    }

    /// While armed, any key in Lunalyr is the tap and Esc cancels. Local to
    /// the app's own windows: no new permission. Gives up after a while.
    private func watchKeys() {
        stopWatchingKeys()
        guard !AppRuntime.isUnitTesting else { return }
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            MainActor.assumeIsolated {
                guard let self, case .armed = self.tapState else { return event }
                let typing = (event.window ?? NSApp.keyWindow)?.firstResponder is NSText
                switch MusicVideoTapSync.keyAction(keyCode: event.keyCode, modifiers: event.modifierFlags,
                                                   isRepeat: event.isARepeat, textInputFocused: typing) {
                case .pass: return event
                case .cancel: self.cancelTapSync(); return nil
                case .tap: self.tap(); return nil
                }
            }
        }
        tapExpiry = Task { [weak self] in
            try? await Task.sleep(for: .seconds(MusicVideoTapSync.armedLimit))
            guard !Task.isCancelled else { return }
            self?.cancelTapSync()
        }
    }

    private func stopWatchingKeys() {
        if let keyMonitor { NSEvent.removeMonitor(keyMonitor) }
        keyMonitor = nil
        tapExpiry?.cancel()
        tapExpiry = nil
    }

    // MARK: Cut-to-beat alignment

    /// A brief listen for the cut alignment runs at most this long.
    static let cutListenLimit: TimeInterval = MusicVideoCutCollector.limit

    private func cutPairKey(_ source: MusicVideoSource) -> String? {
        guard let song, isShowing(source), let video = MusicVideoOffsets.videoKey(source) else { return nil }
        return song.key + "\u{0}" + video
    }

    /// Whether the YouTube video on screen should have its cuts lined up
    /// with the beat: once per song and video, never over the user's own
    /// offset or one already found for it, and only with the music heard
    /// (System audio's capture running, or allowed to run briefly).
    func cutSyncWanted(_ source: MusicVideoSource) -> Bool {
        guard cutSyncEnabled, !cutSyncing, case .youTube = source, let song, let pair = cutPairKey(source),
              let video = MusicVideoOffsets.videoKey(source), !cutTried.contains(pair),
              !offsets.hasManual(song.key, video: video) else { return false }
        if let entry = offsets.automaticEntry(for: song.key, video: video, now: now()),
           entry.offset != nil || entry.origin == .cuts {
            return false
        }
        return captureActive() || (listener?.mayListen ?? false)
    }

    /// Starts it: from now on the player collects frames. Starts a brief
    /// listen when capture is not running already. False when it cannot.
    func beginCutSync(_ source: MusicVideoSource) -> Bool {
        guard cutSyncWanted(source), let pair = cutPairKey(source) else { return false }
        if !captureActive() {
            guard let listener, listener.start(for: Self.cutListenLimit) else { return false }
            cutListening = true
        }
        cutTried.insert(pair)
        cutPair = pair
        cutSyncing = true
        return true
    }

    /// Whether the alignment under way is still for `source` and still
    /// wanted (no song change, no nudge or tap since).
    func cutSyncContinues(_ source: MusicVideoSource) -> Bool {
        guard cutSyncing, let pair = cutPairKey(source), pair == cutPair, let song,
              let video = MusicVideoOffsets.videoKey(source) else { return false }
        return !offsets.hasManual(song.key, video: video)
    }

    /// Asks for a verdict on the frames so far. True when it is over: an
    /// offset was found and stored, or `final` and nothing more will come.
    /// The offset stored is the one the frames were taken under, plus how
    /// far the player was from it, plus how late the cuts fell.
    @discardableResult
    func checkCutSync(_ source: MusicVideoSource, frames: [MusicVideoCutAlign.Frame], offsetUsed: Double,
                      drift: Double, final: Bool) -> Bool {
        guard cutSyncContinues(source), let song, let video = MusicVideoOffsets.videoKey(source) else {
            endCutSync()
            return true
        }
        switch MusicVideoCutAlign.align(frames: frames, audio: audioFrames()) {
        case .found(let result):
            // The frames are from an offset no longer in force: wait for new ones.
            guard abs(currentOffset - offsetUsed) < 0.001 else { return false }
            offsets.storeAutomatic(offsetUsed + drift + result.lag, origin: .cuts, for: song.key, video: video, now: now())
            endCutSync()
            objectWillChange.send()
            return true
        case .unclear:
            guard final else { return false }
            offsets.storeAutomatic(nil, origin: .cuts, for: song.key, video: video, now: now())
            endCutSync()
            return true
        case .tooLittle:
            if final { endCutSync() }
            return final
        }
    }

    /// A player stops collecting for `source`: ends the alignment if it is
    /// still the one under way (not a newer song's).
    func endCutSync(of source: MusicVideoSource) {
        guard let pair = cutPairKey(source), pair == cutPair else { return }
        endCutSync()
    }

    /// Stops the alignment (and its brief listen) wherever it is.
    func endCutSync() {
        if cutListening { listener?.stop() }
        cutListening = false
        cutPair = nil
        if cutSyncing { cutSyncing = false }
    }

    // MARK: Settings copy

    /// One line for Settings about what the wallpaper is showing.
    static func note(for status: Status, online: Bool, fallback: String) -> String? {
        switch status {
        case .off: return nil
        case .idle: return "Play a song to see its music video."
        case .searching(let title): return "Looking for the video for “\(title)”…"
        case .showing(.local, let title): return "Showing the video for “\(title)” from your Music library."
        case .showing(.youTube, let title): return "Showing the official video for “\(title)” from YouTube."
        case .missing(let miss, let title):
            let instead = "Showing \(fallback) instead."
            switch miss {
            case .notInLibrary:
                return "Your Music library has no video for “\(title)”. \(instead)"
                    + (online ? "" : " Turn on Search YouTube, or paste a link below.")
            case .protectedInLibrary:
                return "The library’s video for “\(title)” is copy-protected, so only Music can play it. \(instead)"
            case .notFound: return "No official video found for “\(title)”. \(instead) You can paste a YouTube link for it below."
            case .notEmbeddable: return "The video for “\(title)” can’t be shown outside YouTube. \(instead)"
            case .rateLimited: return "Too many lookups just now; trying again shortly. \(instead)"
            case .lookupFailed: return "Couldn’t search YouTube just now. \(instead) You can paste a YouTube link for it below."
            case .notASong: return "Radio and adverts have no video. \(instead)"
            case .playerUnreachable:
                return "Couldn’t load YouTube’s player for “\(title)” just now; trying again shortly. \(instead)"
            case .localFileFailed:
                return "The library’s video for “\(title)” wouldn’t play here. \(instead)"
            }
        }
    }
}
