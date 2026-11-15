import AppKit
import Combine
import Foundation

/// What allows looking ahead right now.
struct LookAheadConditions: Equatable, Sendable {
    /// General › Music players › Look ahead in the queue.
    var enabled = true
    /// Apple Music is the player on screen: its queue is the one read.
    var followsMusic = true
    var online = true
    var lowPowerMode = false
    var onBattery = false
    /// "Music video" is the wallpaper, with online search on.
    var videos = false
}

/// One upcoming song and what to look up for it.
struct LookAheadItem: Equatable, Sendable {
    var track: QueuedTrack
    var lyrics = true
    var video = false
    /// The catalogue cover, and the video's still.
    var pictures = false
}

/// Which upcoming songs to look up ahead, and what for each. Pure, so the
/// rules are tested on their own.
enum LookAheadPlanner {
    /// Songs looked up ahead on mains power, and on battery.
    static let songs = 3
    static let songsOnBattery = 1
    /// Of those, the ones whose video is found too: each search can take
    /// several of the YouTube rate limit's slots.
    static let videoSongs = 2

    /// Nothing in Low Power Mode, offline, with the setting off or while
    /// another player is followed. Otherwise the first `songs` lookable
    /// songs after the playing one (`songsOnBattery` on battery, without
    /// pictures), skipping the playing song, repeats, and songs already
    /// done.
    static func plan(upcoming: [QueuedTrack], current: NowPlayingMonitor.Track?, conditions: LookAheadConditions,
                     done: Set<String>) -> [LookAheadItem] {
        guard conditions.enabled, conditions.followsMusic, conditions.online, !conditions.lowPowerMode,
              current != nil else { return [] }
        let limit = conditions.onBattery ? songsOnBattery : songs
        var seen: Set<String> = []
        var chosen: [QueuedTrack] = []
        for track in upcoming where chosen.count < limit {
            guard track.isLookable, !(current.map(track.matches) ?? false), seen.insert(track.recordingKey).inserted else {
                continue
            }
            chosen.append(track)
        }
        return chosen.enumerated().compactMap { index, track in
            guard !done.contains(track.recordingKey) else { return nil }
            return LookAheadItem(track: track, lyrics: true, video: conditions.videos && index < videoSongs,
                                 pictures: !conditions.onBattery)
        }
    }
}

/// Looks the next songs in Apple Music's Up Next up before they play, so
/// their lyrics, video and cover are in the usual caches when they start:
/// the same lookups the playing song makes (`LyricsFetcher.lookup`,
/// `MusicVideoController.lookAhead`, `ArtworkProvider`), one song at a time
/// at background priority, after the playing song's own lookups have had
/// `settleDelay` to go first. A change of queue plans again: work for songs
/// no longer next is cancelled. Songs done are not looked up again.
///
/// MediaRemote's details for a queued song can differ slightly from what
/// the player reports once it plays (its duration by a second or two), so
/// the song's lookups ask `lookedUp` and carry what was found across.
@MainActor
final class QueueLookAhead {
    static let shared = QueueLookAhead(work: .live)

    /// The lookups made for one song. Tests pass their own.
    struct Work {
        var lyrics: @MainActor (QueuedTrack) async -> Void
        var video: @MainActor (QueuedTrack, _ pictures: Bool) async -> Void
        var artwork: @MainActor (QueuedTrack) async -> Void

        static var live: Work {
            Work(lyrics: { track in _ = await LyricsFetcher.shared.lookup(track.query) },
                 video: { track, pictures in await MusicVideoController.shared.lookAhead(track.videoSong, pictures: pictures) },
                 artwork: { track in
                     _ = await ArtworkProvider.shared.artworkData(title: track.title, artist: track.artist,
                                                                  album: track.album.isEmpty ? nil : track.album,
                                                                  duration: track.duration)
                 })
        }
    }

    /// The playing song's own lookups go first for this long after it starts.
    var settleDelay: TimeInterval = 8
    private let work: Work
    private let uptime: () -> TimeInterval

    private var upcoming: [QueuedTrack] = []
    private var current: NowPlayingMonitor.Track?
    private var conditions = LookAheadConditions()
    private var songChangedAt: TimeInterval = -.infinity
    /// Songs whose lookups finished, oldest first.
    private(set) var done: [QueuedTrack] = []
    static let remembered = 50
    private var running: Task<Void, Never>?
    private(set) var plan: [LookAheadItem] = []
    /// The song being looked up now, for tests and the log.
    private(set) var working: QueuedTrack?
    private var cancellables: Set<AnyCancellable> = []

    init(work: Work, uptime: @escaping () -> TimeInterval = { ProcessInfo.processInfo.systemUptime }) {
        self.work = work
        self.uptime = uptime
    }

    /// The song looked up ahead (or being looked up) that `track` is, if any.
    func lookedUp(_ track: NowPlayingMonitor.Track) -> QueuedTrack? {
        if let working, working.matches(track) { return working }
        return done.last { $0.matches(track) }
    }

    /// New facts: plans again, cancelling work no longer wanted.
    func update(upcoming: [QueuedTrack], current: NowPlayingMonitor.Track?, conditions: LookAheadConditions) {
        if let current, !(self.current.map { $0.isSameTrack(as: current) } ?? false) {
            songChangedAt = uptime()
            let ahead = lookedUp(current) != nil
            LookAheadLog.note("song started: \(ahead ? "looked up ahead" : "not looked up ahead")")
        }
        self.upcoming = upcoming
        self.current = current
        self.conditions = conditions
        replan()
    }

    private func replan() {
        let next = LookAheadPlanner.plan(upcoming: upcoming, current: current, conditions: conditions,
                                         done: Set(done.map(\.recordingKey)))
        guard next != plan || (running == nil && !next.isEmpty) else { return }
        // The song under way stays under way if it is still planned first.
        if let working, next.first?.track == working, plan.first?.track == working, running != nil {
            plan = next
            return
        }
        running?.cancel()
        running = nil
        working = nil
        plan = next
        guard !next.isEmpty else { return }
        LookAheadLog.note("planned \(next.count) songs (video \(next.filter(\.video).count), pictures \(next.first?.pictures == true))")
        let wait = max(0, settleDelay - (uptime() - songChangedAt))
        running = Task(priority: .background) { [weak self] in
            if wait > 0 { try? await Task.sleep(for: .seconds(wait)) }
            await self?.run()
        }
    }

    /// Works through `plan`, one song at a time, while not cancelled.
    private func run() async {
        while !Task.isCancelled, let item = plan.first(where: { item in !done.contains { $0 == item.track } }) {
            working = item.track
            let began = uptime()
            if item.lyrics { await work.lyrics(item.track) }
            if item.video, !Task.isCancelled { await work.video(item.track, item.pictures) }
            if item.pictures, !Task.isCancelled { await work.artwork(item.track) }
            guard !Task.isCancelled else { return }
            done.append(item.track)
            if done.count > Self.remembered { done.removeFirst(done.count - Self.remembered) }
            LookAheadLog.note("looked up a song ahead in \(String(format: "%.1f", uptime() - began)) s")
        }
        working = nil
        running = nil
    }

    // MARK: Live

    /// Follows the playing song, Music's queue and the settings. Started by
    /// the app delegate, never in the test host.
    func start() {
        guard cancellables.isEmpty, !AppRuntime.isUnitTesting else { return }
        let monitor = NowPlayingMonitor.shared, reader = MusicQueueReader.shared
        monitor.$current
            .removeDuplicates { a, b in
                switch (a, b) {
                case (nil, nil): return true
                case let (a?, b?): return a.isSameTrack(as: b)
                default: return false
                }
            }
            .sink { track in MainActor.assumeIsolated { reader.songChanged(to: track) } }
            .store(in: &cancellables)
        let triggers: [AnyPublisher<Void, Never>] = [
            monitor.$current.map { _ in () }.eraseToAnyPublisher(),
            monitor.$activePlayer.map { _ in () }.eraseToAnyPublisher(),
            reader.$upcoming.map { _ in () }.eraseToAnyPublisher(),
            PlayerOptions.shared.$looksAhead.map { _ in () }.eraseToAnyPublisher(),
            Settings.shared.$backgroundMode.map { _ in () }.eraseToAnyPublisher(),
            Settings.shared.$musicVideoOnline.map { _ in () }.eraseToAnyPublisher(),
            NotificationCenter.default.publisher(for: PowerSource.didChange).map { _ in () }.eraseToAnyPublisher(),
            NotificationCenter.default.publisher(for: .NSProcessInfoPowerStateDidChange).map { _ in () }.eraseToAnyPublisher(),
            // Coming back online plans again.
            Timer.publish(every: 60, on: .main, in: .common).autoconnect().map { _ in () }.eraseToAnyPublisher(),
        ]
        Publishers.MergeMany(triggers)
            .debounce(for: .milliseconds(300), scheduler: RunLoop.main)
            .sink { [weak self] in MainActor.assumeIsolated { self?.refresh() } }
            .store(in: &cancellables)
    }

    private func refresh() {
        let monitor = NowPlayingMonitor.shared, reader = MusicQueueReader.shared
        let settings = Settings.shared
        let followsMusic = monitor.activePlayer == .appleMusic
        let enabled = PlayerOptions.shared.looksAhead
        reader.setActive(enabled && followsMusic)
        let conditions = LookAheadConditions(
            enabled: enabled, followsMusic: followsMusic, online: LyricConnectivity.shared.isOnline,
            lowPowerMode: ProcessInfo.processInfo.isLowPowerModeEnabled, onBattery: PowerSource.isOnBattery,
            videos: settings.backgroundMode == .musicVideo && settings.musicVideoOnline)
        update(upcoming: reader.upcoming, current: monitor.current, conditions: conditions)
    }
}
