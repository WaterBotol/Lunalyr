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

/// What has been looked up for a song.
struct LookAheadParts: Equatable, Sendable {
    var lyrics = false
    var video = false
    /// The catalogue cover.
    var cover = false
}

/// One upcoming song and what is still to look up for it.
struct LookAheadItem: Equatable, Sendable {
    var track: QueuedTrack
    var lyrics = true
    var video = false
    var cover = false
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
    /// covers), skipping the playing song and repeats; the first
    /// `videoSongs` with their video. Of each, only what `done` lacks:
    /// a song moving up the queue gets the parts its new place adds.
    static func plan(upcoming: [QueuedTrack], current: NowPlayingMonitor.Track?, conditions: LookAheadConditions,
                     done: [String: LookAheadParts]) -> [LookAheadItem] {
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
            let had = done[track.recordingKey] ?? LookAheadParts()
            let item = LookAheadItem(track: track, lyrics: !had.lyrics,
                                     video: conditions.videos && index < videoSongs && !had.video,
                                     cover: !conditions.onBattery && !had.cover)
            return item.lyrics || item.video || item.cover ? item : nil
        }
    }
}

/// Looks the next songs in Apple Music's Up Next up before they play, so
/// their lyrics, video and cover are in the usual caches when they start:
/// the same lookups the playing song makes (`LyricsFetcher.lookup`,
/// `MusicVideoController.lookAhead`, `ArtworkProvider`), one song at a time
/// at background priority, after the playing song's own lookups have had
/// `settleDelay` to go first. A change of queue plans again: work for songs
/// no longer planned is cancelled. What was done is not looked up again; a
/// video the rate limit had no room for is tried again on a later plan.
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
        /// True once the video's answer is known; false: try again later.
        var video: @MainActor (QueuedTrack) async -> Bool
        var cover: @MainActor (QueuedTrack) async -> Void

        static var live: Work {
            Work(lyrics: { track in _ = await LyricsFetcher.shared.lookup(track.query) },
                 video: { track in await MusicVideoController.shared.lookAhead(track.videoSong) },
                 cover: { track in
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
    /// Songs with something looked up, oldest first, and what was.
    private(set) var songs: [QueuedTrack] = []
    private(set) var done: [String: LookAheadParts] = [:]
    static let remembered = 50
    private var running: Task<Void, Never>?
    /// Which run is current: a cancelled one never touches state after.
    private var runToken = 0
    private(set) var plan: [LookAheadItem] = []
    /// The song being looked up now.
    private(set) var working: QueuedTrack?
    private var cancellables: Set<AnyCancellable> = []

    init(work: Work, uptime: @escaping () -> TimeInterval = { ProcessInfo.processInfo.systemUptime }) {
        self.work = work
        self.uptime = uptime
    }

    /// The song looked up ahead (or being looked up) that `track` is, if any.
    func lookedUp(_ track: NowPlayingMonitor.Track) -> QueuedTrack? {
        if let working, working.matches(track) { return working }
        return songs.last { $0.matches(track) }
    }

    /// New facts: plans again, cancelling work no longer wanted.
    func update(upcoming: [QueuedTrack], current: NowPlayingMonitor.Track?, conditions: LookAheadConditions) {
        if let current, !(self.current.map { $0.isSameTrack(as: current) } ?? false) {
            songChangedAt = uptime()
            let ahead = lookedUp(current).flatMap { done[$0.recordingKey] }
            LookAheadLog.note("song started: " + (ahead.map { "looked up ahead (lyrics \($0.lyrics), video \($0.video))" }
                                                   ?? "not looked up ahead"))
        }
        self.upcoming = upcoming
        self.current = current
        self.conditions = conditions
        replan()
    }

    private func replan() {
        let next = LookAheadPlanner.plan(upcoming: upcoming, current: current, conditions: conditions, done: done)
        // A run under way carries on while its song is still planned (or
        // while it is still waiting out `settleDelay`): it reads `plan`
        // afresh for each song.
        let keep = running != nil && !next.isEmpty
            && (working.map { song in next.contains { $0.track == song } } ?? true)
        plan = next
        if keep { return }
        running?.cancel()
        running = nil
        working = nil
        guard !next.isEmpty else { return }
        LookAheadLog.note("planned \(next.count) songs (video \(next.filter(\.video).count), covers \(next.filter(\.cover).count))")
        runToken += 1
        let token = runToken
        let wait = max(0, settleDelay - (uptime() - songChangedAt))
        running = Task(priority: .background) { [weak self] in
            if wait > 0 { try? await Task.sleep(for: .seconds(wait)) }
            await self?.run(token)
        }
    }

    /// The first planned song with something still to do, and what;
    /// videos already tried in this run (`tried`) wait for a later run.
    private func nextItem(tried: Set<String>) -> LookAheadItem? {
        for item in plan {
            let key = item.track.recordingKey
            let had = done[key] ?? LookAheadParts()
            let left = LookAheadItem(track: item.track, lyrics: item.lyrics && !had.lyrics,
                                     video: item.video && !had.video && !tried.contains(key), cover: item.cover && !had.cover)
            if left.lyrics || left.video || left.cover { return left }
        }
        return nil
    }

    /// Works through `plan`, one song at a time, while it is the current run.
    private func run(_ token: Int) async {
        var tried: Set<String> = []
        while !Task.isCancelled, token == runToken, let item = nextItem(tried: tried) {
            working = item.track
            let began = uptime()
            var parts = done[item.track.recordingKey] ?? LookAheadParts()
            if item.lyrics { await work.lyrics(item.track); parts.lyrics = !Task.isCancelled }
            if item.video, !Task.isCancelled {
                tried.insert(item.track.recordingKey)
                parts.video = await work.video(item.track) && !Task.isCancelled
            }
            if item.cover, !Task.isCancelled { await work.cover(item.track); parts.cover = !Task.isCancelled }
            record(parts, for: item.track)
            guard !Task.isCancelled, token == runToken else { return }
            LookAheadLog.note("looked up a song ahead in \(String(format: "%.1f", uptime() - began)) s "
                              + "(lyrics \(parts.lyrics), video \(parts.video), cover \(parts.cover))")
        }
        guard token == runToken else { return }
        working = nil
        running = nil
    }

    private func record(_ parts: LookAheadParts, for track: QueuedTrack) {
        let key = track.recordingKey
        guard parts != LookAheadParts() || done[key] != nil else { return }
        done[key] = parts
        songs.removeAll { $0.recordingKey == key }
        songs.append(track)
        if songs.count > Self.remembered {
            for old in songs.prefix(songs.count - Self.remembered) { done[old.recordingKey] = nil }
            songs.removeFirst(songs.count - Self.remembered)
        }
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
            LyricStyles.shared.objectWillChange.map { _ in () }.eraseToAnyPublisher(),
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
        let lowPowerMode = ProcessInfo.processInfo.isLowPowerModeEnabled
        reader.setActive(enabled && followsMusic && !lowPowerMode)
        let conditions = LookAheadConditions(
            enabled: enabled, followsMusic: followsMusic, online: LyricConnectivity.shared.isOnline,
            lowPowerMode: lowPowerMode, onBattery: PowerSource.isOnBattery,
            videos: LyricStyles.shared.anyWallpaper { $0.backgroundMode == .musicVideo } && settings.musicVideoOnline)
        update(upcoming: reader.upcoming, current: monitor.current, conditions: conditions)
    }
}
