import AppKit
import Combine

/// Listen-along: friends play what the user plays, in time, on their own
/// Music or Spotify. Both sides go through a small relay whose address is a
/// setting (`listenRelayURL`); with none set, nothing here runs or shows.
///
/// - Sharing ("Let friends listen along", off by default) makes a room on
///   the relay and sends the song, play/pause and position, and the lyrics'
///   line timing once per song. Discord gets a Join party or a Listen along
///   button for it (`DiscordPresence`).
/// - Following starts from Discord's Join or a code typed in the menu, and
///   ends on Stop, when the host stops sharing, or on quit.
@MainActor
final class ListenAlong: ObservableObject {
    static let shared = ListenAlong()

    @Published private(set) var base: URL?
    @Published private(set) var sharing = false
    @Published private(set) var host: ListenHost?
    @Published private(set) var follower: ListenFollower?
    /// The Discord user's display name, from Discord's READY, sent as the
    /// host's name so friends see who they follow.
    var discordUserName: String?

    private var timer: Timer?
    private var changes: Set<AnyCancellable> = []
    private var hostChanges: AnyCancellable?
    private var followerChanges: AnyCancellable?
    private var lyricsTrack: NowPlayingMonitor.Track?
    private var started = false
    private var quitObserver: NSObjectProtocol?

    private init() {}

    var isAvailable: Bool { base != nil }
    var room: String? { sharing ? host?.room : nil }
    var listeners: Int { host?.listeners ?? 0 }

    /// The web listen-along page for the shared room.
    var webPageURL: URL? {
        guard let base, let room else { return nil }
        return ListenProtocol.webPageURL(base, room: room)
    }

    /// What Discord shows for the room, while sharing.
    var discordParty: DiscordPresence.ListenParty? {
        guard let room else { return nil }
        return DiscordPresence.ListenParty(room: room, listeners: listeners, webURL: webPageURL?.absoluteString)
    }

    /// Applies the settings. Called by `LunalyrExtras` once it has started.
    func configure(relay: String, sharing: Bool) {
        // A test host never reaches a relay or the user's players.
        guard !AppRuntime.isUnitTesting else { return }
        let base = ListenProtocol.base(relay)
        if base != self.base {
            host?.end()
            host = nil
            stopFollowing()
            self.base = base
        }
        let share = sharing && base != nil
        if !share, let host {
            host.end()
            self.host = nil
        }
        self.sharing = share
        if share, host == nil, let base {
            let host = ListenHost(base: base)
            self.host = host
            hostChanges = host.objectWillChange.sink { [weak self] _ in self?.objectWillChange.send() }
        }
        startIfNeeded()
    }

    private func startIfNeeded() {
        let needed = base != nil && (sharing || follower != nil)
        if needed, !started {
            started = true
            let monitor = NowPlayingMonitor.shared
            monitor.$current.removeDuplicates().sink { [weak self] _ in self?.lyricsTrack = nil }.store(in: &changes)
            LyricsViewModel.shared.$lines.sink { [weak self] lines in
                guard let self else { return }
                self.lyricsTrack = lines.isEmpty ? nil : NowPlayingMonitor.shared.current
            }.store(in: &changes)
            Publishers.Merge(monitor.$current.map { _ in () }, monitor.$isPlaying.map { _ in () })
                .receive(on: DispatchQueue.main)
                .sink { [weak self] in self?.tick() }
                .store(in: &changes)
            let timer = Timer(timeInterval: 1, repeats: true) { [weak self] _ in
                MainActor.assumeIsolated { self?.tick() }
            }
            timer.tolerance = 0.2
            RunLoop.main.add(timer, forMode: .common)
            self.timer = timer
            if quitObserver == nil {
                quitObserver = NotificationCenter.default.addObserver(
                    forName: NSApplication.willTerminateNotification, object: nil, queue: .main
                ) { _ in
                    MainActor.assumeIsolated { ListenAlong.shared.quit() }
                }
            }
        } else if !needed, started {
            started = false
            timer?.invalidate()
            timer = nil
            changes.removeAll()
        }
    }

    private var tickCount = 0

    private func tick() {
        if sharing, let host { host.update(snapshot()) }
        tickCount += 1
        if let follower, tickCount % Int(ListenFollower.tickInterval) == 0 { follower.sync() }
    }

    // MARK: Hosting

    private func snapshot() -> ListenHostSnapshot {
        let monitor = NowPlayingMonitor.shared
        guard let current = monitor.current, !current.isAdvert, !current.isStream, !current.title.isEmpty else {
            return ListenHostSnapshot(track: nil, playing: false, position: 0, hostName: discordUserName)
        }
        let spotifyId = ListenProtocol.spotifyID(fromURI: current.persistentID)
        // Apple's catalogue ID, from the search Discord already made; a
        // Music song is looked up here too. A Spotify song is never sent to
        // Apple just for this.
        let lookUp = monitor.activePlayer == .appleMusic
        let appleId = CatalogueMatches.shared.match(title: current.title, artist: current.artist, album: current.album,
                                                    lookUpIfMissing: lookUp)?.trackID
        let track = ListenProtocol.Track(title: current.title, artist: current.artist, album: current.album,
                                         duration: current.duration, spotifyId: spotifyId, appleId: appleId, isrc: nil)
        return ListenHostSnapshot(track: track, playing: monitor.isPlaying, position: monitor.playbackTime(),
                                  lyrics: lyricsTrack == current ? lyrics(for: current) : [], hostName: discordUserName)
    }

    /// Line timing as the user sees it: shifted by their offsets.
    private func lyrics(for track: NowPlayingMonitor.Track) -> [ListenProtocol.LyricLine] {
        let model = LyricsViewModel.shared
        let shift = Settings.shared.lyricOffset + SongOffsets.shared.offset(for: model.currentQuery)
        return Self.lines(model.lines, shift: shift)
    }

    nonisolated static func lines(_ lines: [LyricsFetcher.Line], shift: Double) -> [ListenProtocol.LyricLine] {
        lines.compactMap { line in
            let text = line.text.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !line.isInstrumental, !text.isEmpty else { return nil }
            return ListenProtocol.LyricLine(start: max(0, line.start - shift), end: max(0, line.end - shift), text: text)
        }
    }

    // MARK: Following

    /// Follows a room: from Discord's Join secret or a typed code or link.
    @discardableResult
    func join(_ input: String) -> Bool {
        guard let base, let room = ListenProtocol.room(fromInput: input) else { return false }
        if room == host?.room { return false }   // one's own room
        if follower?.room == room, follower?.phase == .following { return true }
        stopFollowing()
        let follower = ListenFollower(room: room, base: base, player: LiveListenPlayer())
        follower.onEnd = { [weak self] phase in
            guard let self, let current = self.follower, current === follower else { return }
            let name = current.displayName
            self.follower = nil
            self.followerChanges = nil
            self.startIfNeeded()
            LunalyrExtras.shared.post(phase == .ended ? "\(name.prefix(1).uppercased() + name.dropFirst()) stopped sharing"
                                                       : "That listen-along has ended", symbol: "person.2.slash")
        }
        self.follower = follower
        followerChanges = follower.objectWillChange.sink { [weak self] _ in self?.objectWillChange.send() }
        follower.start()
        startIfNeeded()
        return true
    }

    func stopFollowing() {
        guard let follower else { return }
        follower.stop()
        self.follower = nil
        followerChanges = nil
        startIfNeeded()
    }

    /// Ends the room and gives shuffle back before the app goes.
    private func quit() {
        host?.endBeforeQuitting()
        if let follower {
            if let kind = follower.shuffleToRestore {
                LiveListenPlayer.restoreShuffleNow(kind)
                follower.markShuffleRestored()
            }
            follower.stop()
        }
    }
}
