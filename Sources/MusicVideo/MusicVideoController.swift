import Combine
import CoreGraphics
import Foundation

/// Finds the playing song's video while "Music video" is the wallpaper,
/// and says what it found for Settings. Looks nothing up while another
/// wallpaper type is chosen.
@MainActor
final class MusicVideoController: ObservableObject {
    static let shared = MusicVideoController(
        resolver: MusicVideoResolver(
            library: AppleMusicVideoLibrary(mayRead: {
                await MainActor.run {
                    let monitor = NowPlayingMonitor.shared
                    return monitor.runningPlayers.contains(.appleMusic) && monitor.automation[.appleMusic] == .granted
                }
            }),
            playability: AVMusicVideoPlayability(), http: URLSessionMusicVideoHTTP(),
            cache: MusicVideoCache(defaults: .standard), links: MusicVideoLinks(defaults: .standard)),
        offsets: MusicVideoOffsets(defaults: .standard))

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
    let offsets: MusicVideoOffsets
    private let resolver: MusicVideoResolver
    private var song: MusicVideoSong?
    /// The song's key, or a marker for radio and adverts, so a change
    /// between those and nothing playing is noticed too.
    private var playingKey: String?
    private var enabled = false
    private var online = false
    private var lookup: Task<Void, Never>?
    private var retry: Task<Void, Never>?
    /// Each song is retried once after a passing failure, not every minute.
    private var retried: String?
    private var cancellables: Set<AnyCancellable> = []
    /// Seconds before a lookup that failed for a passing reason is retried.
    var retryDelay: TimeInterval = 45

    init(resolver: MusicVideoResolver, offsets: MusicVideoOffsets) {
        self.resolver = resolver
        self.offsets = offsets
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
        lookup?.cancel()
        retry?.cancel()
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
        case .found(let source): status = .showing(source, title: song.title)
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

    /// Waits for the lookup in flight, for tests.
    func settle() async {
        await lookup?.value
    }

    /// The player could not show the video: YouTube refused to embed it,
    /// or the file would not play. The wallpaper falls back for this song.
    func reportPlaybackFailure(of source: MusicVideoSource) {
        guard let song, case .showing(let showing, _) = status, showing == source else { return }
        if case .youTube = source { resolver.markNotEmbeddable(song) }
        status = .missing(.notEmbeddable, title: song.title)
    }

    /// Whether a surface shows `source` or falls back: the Settings preview
    /// shows it (a still for YouTube), and with "Heavy scenes on main display
    /// only" on, only the main display plays it, as with the web scenes.
    nonisolated static func placement(source: MusicVideoSource?, preview: Bool, heavyScenesOnMainDisplayOnly: Bool,
                                      display: CGDirectDisplayID?, mainDisplay: CGDirectDisplayID) -> MusicVideoSource? {
        guard let source else { return nil }
        switch WebVisualizerView.placement(preview: preview, heavyScenesOnMainDisplayOnly: heavyScenesOnMainDisplayOnly,
                                           display: display, mainDisplay: mainDisplay) {
        case .preview, .live: return source
        case .standIn: return nil
        }
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

    func clearPastedLink() {
        guard let song else { return }
        resolver.links.remove(song.key)
        relook(song)
    }

    private func relook(_ song: MusicVideoSong) {
        lookup?.cancel()
        retry?.cancel()
        objectWillChange.send()
        guard enabled else { return }
        status = .searching(title: song.title)
        start(song)
    }

    // MARK: Offsets

    /// How far into the video the song starts, for the playing song.
    var currentOffset: Double { offsets.offset(for: song?.key) }

    func offset(for track: NowPlayingMonitor.Track?) -> Double {
        offsets.offset(for: track.map { MusicVideoSong($0).key })
    }

    func nudgeOffset(by delta: Double) {
        guard let song else { return }
        offsets.nudge(by: delta, for: song.key)
        objectWillChange.send()
    }

    func resetOffset() {
        guard let song else { return }
        offsets.set(0, for: song.key)
        objectWillChange.send()
    }

    var hasSong: Bool { song != nil }

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
            }
        }
    }
}
