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
                                    autoSync: AppRuntime.isUnitTesting ? nil : LiveMusicVideoAutoSync(resolver: resolver))
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

    init(resolver: MusicVideoResolver, offsets: MusicVideoOffsets, autoSync: MusicVideoAutoSyncing? = nil,
         now: @escaping () -> Date = Date.init,
         sleep: @escaping (TimeInterval) async -> Void = { try? await Task.sleep(for: .seconds($0)) }) {
        self.resolver = resolver
        self.offsets = offsets
        self.autoSync = autoSync
        self.now = now
        self.sleep = sleep
    }

    /// Follows the playing song and the settings. Started by the app
    /// delegate, never in the test host.
    func start() { start(monitor: .shared, settings: .shared) }

    func start(monitor: NowPlayingMonitor, settings: Settings) {
        guard cancellables.isEmpty else { return }
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
    /// - YouTube refused it for good: remembered, unless it is the user's
    ///   own pasted link (kept, so they can fix it).
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
            if resolver.links.videoID(for: song.key) != id { resolver.markNotEmbeddable(song) }
            status = .missing(.notEmbeddable, title: song.title)
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
            Self.watchURL(videoID: $0, songTime: NowPlayingMonitor.shared.playbackTime(), offset: currentOffset)
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

    /// The user's nudge, from the offset in force; it wins from then on.
    func nudgeOffset(by delta: Double) {
        guard let song else { return }
        offsets.nudge(by: delta, for: song.key, video: showingVideoKey, now: now())
        objectWillChange.send()
    }

    /// Drops the user's nudge: back to the automatic offset, looked for
    /// again if there is none yet.
    func resetOffset() {
        guard let song else { return }
        offsets.clearManual(song.key)
        objectWillChange.send()
        if case .showing(let source, _) = status { startAutoSync(song, source, force: false) }
    }

    /// "Re-sync": forgets the user's nudge and what was found, and looks
    /// for the offset again.
    func resync() {
        guard let song, case .showing(let source, _) = status else { return }
        offsets.clearManual(song.key)
        offsets.forgetAutomatic(song.key)
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

    private func stopAutoSync() {
        syncTask?.cancel()
        syncTask = nil
        syncing = false
    }

    /// Looks for the video's offset once: captions for YouTube (only with
    /// online lookups on), the soundtrack for a library video. Skipped when
    /// the user set the offset or one is remembered for this video, unless
    /// `force` (Re-sync).
    private func startAutoSync(_ song: MusicVideoSong, _ source: MusicVideoSource, force: Bool) {
        stopAutoSync()
        guard let autoSync, let video = MusicVideoOffsets.videoKey(source) else { return }
        if !force && (offsets.hasManual(song.key) || offsets.automaticEntry(for: song.key, video: video, now: now()) != nil) {
            return
        }
        if case .youTube = source, !online { return }
        syncing = true
        syncTask = Task { [weak self] in
            let result: MusicVideoAutoResult
            switch source {
            case .youTube(let id): result = await autoSync.captionOffset(videoID: id, song: song)
            case .local(let url): result = await autoSync.audioOffset(url: url, song: song)
            }
            guard let self, !Task.isCancelled, self.song?.key == song.key, self.showingVideoKey == video else { return }
            self.syncing = false
            self.store(result, song: song, video: video)
            if case .found(let first, .audio) = result, case .local(let url) = source {
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
              !offsets.hasManual(song.key) else { return }
        let second = await autoSync.audioOffset(url: url, song: song)
        guard !Task.isCancelled, self.song?.key == song.key, showingVideoKey == video, !offsets.hasManual(song.key),
              case .found(let offset, _) = second, abs(offset - first) <= Self.refineAgreement else { return }
        store(second, song: song, video: video)
    }

    /// The words beside the nudge buttons.
    nonisolated static func timingLabel(origin: MusicVideoOffsetOrigin, syncing: Bool) -> String? {
        switch origin {
        case .manual: return "Offset set by you"
        case .captions, .audio: return "Synced automatically"
        case .estimated: return syncing ? "Estimated; syncing…" : "Estimated"
        case .none: return syncing ? "Syncing…" : nil
        }
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
