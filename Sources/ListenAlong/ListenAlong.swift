import AppKit
import Combine

/// Listen-along: friends play what the user plays, in time, on their own
/// Music or Spotify. Both sides go through a small relay whose address is a
/// setting (`listenRelayURL`); with none set, nothing here runs or shows.
///
/// - Sharing ("Let friends listen along", off by default) makes a room on
///   the relay and sends the song, play/pause and position, the Discord
///   display name, and (with "Share lyrics with listeners", on by default)
///   the lyric lines with their timing once per song. Discord gets a Join
///   party or a Listen along button for it (`DiscordPresence`).
/// - Following starts from Discord's Join or a code typed in the menu, only
///   once the user accepts "Listen along with <name>?", and ends on Stop,
///   when the host stops sharing, or on quit.
@MainActor
final class ListenAlong: ObservableObject {
    static let shared = ListenAlong()

    @Published private(set) var base: URL?
    @Published private(set) var sharing = false
    @Published private(set) var host: ListenHost?
    @Published private(set) var follower: ListenFollower?
    /// The Discord user's display name, from Discord's READY, sent as the
    /// host's name so friends see who they follow. Cleared when Discord
    /// presence is turned off.
    var discordUserName: String?
    /// "Share lyrics with listeners": the lyric lines go to the relay.
    private(set) var sharesLyrics = true
    /// The invitation being asked about, so a second one waits its turn.
    @Published private(set) var asking: ListenInvite?

    private let sockets: any ListenSocketOpening
    private let makePlayer: @MainActor () -> any ListenPlayerControl
    private let confirmer: any ListenJoinConfirming
    /// Off in a test host: the shared instance never reaches a relay.
    private let connects: Bool
    /// Off for tests' own instances: no monitor, timer or quit observer.
    private let observesPlayback: Bool

    private var timer: Timer?
    private var changes: Set<AnyCancellable> = []
    private var hostChanges: AnyCancellable?
    private var followerChanges: AnyCancellable?
    private var lyricsTrack: NowPlayingMonitor.Track?
    private var started = false
    private var quitObserver: NSObjectProtocol?

    private convenience init() {
        let live = !AppRuntime.isUnitTesting
        self.init(sockets: LiveListenSockets(), makePlayer: { LiveListenPlayer() }, confirmer: AlertListenJoinConfirmer(),
                  connects: live, observesPlayback: live)
    }

    /// Tests make their own, over fake sockets, player and confirmation.
    init(sockets: any ListenSocketOpening, makePlayer: @escaping @MainActor () -> any ListenPlayerControl,
         confirmer: any ListenJoinConfirming, connects: Bool, observesPlayback: Bool) {
        self.sockets = sockets
        self.makePlayer = makePlayer
        self.confirmer = confirmer
        self.connects = connects
        self.observesPlayback = observesPlayback
    }

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
        return DiscordPresence.ListenParty(room: room, listeners: listeners, webURL: webPageURL?.absoluteString,
                                           hostName: discordUserName)
    }

    /// Applies the settings. Called by `LunalyrExtras` once it has started.
    func configure(relay: String, sharing: Bool, sharesLyrics: Bool = true) {
        // A test host never reaches a relay or the user's players.
        guard connects else { return }
        self.sharesLyrics = sharesLyrics
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
        guard observesPlayback else { return }
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
        // Apple's catalogue ID, only when Discord's cover or song button
        // already looked the song up: sharing never sends a title to Apple.
        let appleId = CatalogueMatches.shared.match(title: current.title, artist: current.artist, album: current.album,
                                                    lookUpIfMissing: false)?.trackID
        let track = ListenProtocol.Track(title: current.title, artist: current.artist, album: current.album,
                                         duration: current.duration, spotifyId: spotifyId, appleId: appleId, isrc: nil)
        return ListenHostSnapshot(track: track, playing: monitor.isPlaying, position: monitor.playbackTime(),
                                  lyrics: sharesLyrics && lyricsTrack == current ? lyrics(for: current) : [],
                                  hostName: ListenProtocol.cleanHostName(discordUserName))
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

    /// Asks "Listen along with <name>?" for a Discord Join secret, and
    /// follows only if the user accepts. False when it isn't a room.
    @discardableResult
    func requestJoin(discordSecret secret: String) -> Bool {
        guard let invite = ListenProtocol.joinInvite(fromSecret: secret) else { return false }
        return requestJoin(ListenInvite(room: invite.room, hostName: invite.hostName))
    }

    /// The same for a code or link typed in the menu.
    @discardableResult
    func requestJoin(code: String) -> Bool {
        guard let room = ListenProtocol.room(fromInput: code) else { return false }
        return requestJoin(ListenInvite(room: room, hostName: nil))
    }

    private func requestJoin(_ invite: ListenInvite) -> Bool {
        guard base != nil else { return false }
        if invite.room == host?.room { return false }   // one's own room
        if follower?.room == invite.room { return true }
        guard asking == nil else { return true }
        asking = invite
        Task { [weak self] in
            guard let self else { return }
            let accepted = await self.confirmer.confirm(invite)
            self.asking = nil
            if accepted { self.join(room: invite.room) }
        }
        return true
    }

    /// Follows a room the user has agreed to join. Nothing connects or
    /// plays before this.
    private func join(room: String) {
        guard let base, room != host?.room else { return }
        if follower?.room == room, follower?.phase == .following { return }
        stopFollowing()
        let follower = ListenFollower(room: room, base: base, sockets: sockets, player: makePlayer())
        follower.onEnd = { [weak self, weak follower] phase in
            guard let self, let follower, let current = self.follower, current === follower else { return }
            let name = current.displayName
            let connected = current.everConnected
            self.follower = nil
            self.followerChanges = nil
            self.startIfNeeded()
            LunalyrExtras.shared.post(Self.endNote(phase, name: name, connected: connected), symbol: "person.2.slash")
        }
        self.follower = follower
        followerChanges = follower.objectWillChange.sink { [weak self] _ in self?.objectWillChange.send() }
        follower.start()
        startIfNeeded()
    }

    /// What the wallpaper says when following ends without a Stop.
    static func endNote(_ phase: ListenFollower.Phase, name: String, connected: Bool) -> String {
        switch phase {
        case .ended: return "\(name.prefix(1).uppercased() + name.dropFirst()) stopped sharing"
        case .full: return "That listen-along is full"
        case .notFound where !connected:
            return "Couldn\u{2019}t find that listen-along \u{2014} it may have ended, or uses another relay"
        default: return "That listen-along has ended"
        }
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

/// An invitation to follow a room, before the user has said yes.
struct ListenInvite: Equatable, Sendable {
    var room: String
    /// The host's display name, from a Discord join secret; nil for a code.
    var hostName: String?

    var question: String {
        if let hostName { return "Listen along with \(hostName)?" }
        return "Listen along with room \(room)?"
    }

    static let explanation = "Lunalyr will play what they play on your Music or Spotify, in time with them. Pausing or changing the song yourself pauses listening along until you resume it from the menu bar."
    static let acceptTitle = "Listen Along"
    static let declineTitle = "Not Now"
}

/// Asks the user about an invitation. Injected: tests answer it themselves.
@MainActor
protocol ListenJoinConfirming {
    func confirm(_ invite: ListenInvite) async -> Bool
}

/// An alert in front of everything; only "Listen Along" accepts.
struct AlertListenJoinConfirmer: ListenJoinConfirming {
    func confirm(_ invite: ListenInvite) async -> Bool {
        guard !AppRuntime.isUnitTesting else { return false }
        let alert = NSAlert()
        alert.messageText = invite.question
        alert.informativeText = ListenInvite.explanation
        alert.addButton(withTitle: ListenInvite.acceptTitle)
        alert.addButton(withTitle: ListenInvite.declineTitle).keyEquivalent = "\u{1b}"
        NSApp.activate(ignoringOtherApps: true)
        return alert.runModal() == .alertFirstButtonReturn
    }
}
