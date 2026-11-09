import Foundation

/// What the follower's own player is doing.
struct ListenPlayerReading: Equatable, Sendable {
    var kind: PlayerKind
    var title: String
    var artist: String
    /// Spotify's `spotify:track:` URI or Music's persistent ID.
    var trackID: String?
    var position: Double
    var playing: Bool
}

/// The song to play on the follower's own service.
enum ListenPlayTarget: Equatable, Sendable {
    case spotify(id: String)
    case library(persistentID: String)

    /// Whether the player is on this song now.
    func matches(_ reading: ListenPlayerReading) -> Bool {
        switch self {
        case .spotify(let id):
            return reading.kind == .spotify && ListenProtocol.spotifyID(fromURI: reading.trackID) == id
        case .library(let persistentID):
            return reading.kind == .appleMusic
                && MusicBackend.canonicalID(reading.trackID) == MusicBackend.canonicalID(persistentID)
        }
    }
}

/// A song in the follower's Music library, from a search.
struct LibraryCandidate: Equatable, Sendable {
    var persistentID: String
    var title: String
    var artist: String
    var duration: Double
}

/// The follower's player. Injected: the live one drives Music or Spotify by
/// AppleScript, the tests' one is plain state.
@MainActor
protocol ListenPlayerControl: AnyObject {
    /// The service the follower listens on: the one playing, or the one open.
    var kind: PlayerKind? { get }
    func reading() -> ListenPlayerReading?
    func play(_ target: ListenPlayTarget, at position: Double, playing: Bool) async -> Bool
    func seek(to position: Double) async
    func setPlaying(_ playing: Bool) async
    func shuffle(_ kind: PlayerKind) async -> Bool?
    func setShuffle(_ on: Bool, _ kind: PlayerKind) async
    func searchLibrary(title: String) async -> [LibraryCandidate]
}

/// Finding the host's song in a library by name.
enum ListenTrackMatching {
    /// Lower case, accents folded, and without what services add to a
    /// title: "(feat. …)", "[Live]", " - Remastered 2011".
    static func normalized(_ text: String) -> String {
        var value = text.folding(options: [.caseInsensitive, .diacriticInsensitive], locale: Locale(identifier: "en_US_POSIX"))
        value = value.replacingOccurrences(of: #"\s*[\(\[][^\)\]]*[\)\]]"#, with: "", options: .regularExpression)
        if let dash = value.range(of: " - ") { value = String(value[..<dash.lowerBound]) }
        value = value.replacingOccurrences(of: "&", with: "and")
        return value.unicodeScalars.filter { CharacterSet.alphanumerics.contains($0) }.map(String.init).joined()
    }

    /// What the library search is sent: the title without its decorations.
    static func searchTerm(_ title: String) -> String {
        var value = title.replacingOccurrences(of: #"\s*[\(\[][^\)\]]*[\)\]]"#, with: "", options: .regularExpression)
        if let dash = value.range(of: " - ") { value = String(value[..<dash.lowerBound]) }
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? title : trimmed
    }

    /// The candidate that is the song: same title, an artist that matches
    /// (either way round, for "A & B" against "A"), and the nearest length.
    static func best(_ candidates: [LibraryCandidate], title: String, artist: String, duration: Double) -> LibraryCandidate? {
        let wantTitle = normalized(title)
        let wantArtist = normalized(artist)
        let fits = candidates.filter { candidate in
            let name = normalized(candidate.title), by = normalized(candidate.artist)
            guard !name.isEmpty, name == wantTitle else { return false }
            guard !wantArtist.isEmpty, !by.isEmpty else { return true }
            return by.contains(wantArtist) || wantArtist.contains(by)
        }
        let timed = fits.filter { duration <= 0 || $0.duration <= 0 || abs($0.duration - duration) <= 10 }
        return timed.min { abs($0.duration - duration) < abs($1.duration - duration) } ?? (timed.isEmpty ? nil : timed[0])
    }
}

/// The listening side: follows one room's host on the follower's own
/// service -- the same song, at the same moment, paused when they pause.
@MainActor
final class ListenFollower: ObservableObject {
    enum Phase: Equatable {
        case connecting
        case following
        case ended
        /// The room is unknown or gone.
        case notFound
    }

    @Published private(set) var phase: Phase = .connecting
    @Published private(set) var hostName: String?
    /// A short note: the song can't be played here, no player is open.
    @Published private(set) var note: String?
    @Published private(set) var lyrics: [ListenProtocol.LyricLine] = []
    @Published private(set) var track: ListenProtocol.Track?
    /// The song's song.link page, offered with a note when the song can't
    /// be played here.
    @Published private(set) var songPageURL: URL?

    let room: String
    /// Seconds between drift checks.
    static let tickInterval: TimeInterval = 2
    /// Seconds after a command before the player's own reading is trusted.
    static let settleTime: TimeInterval = 3

    private let socketURL: URL?
    private let sockets: any ListenSocketOpening
    private let player: any ListenPlayerControl
    private let clock: () -> TimeInterval
    private var socket: (any ListenSocket)?
    private var receiveTask: Task<Void, Never>?
    private var reconnectTask: Task<Void, Never>?
    private var failures = 0
    private var latest: ListenProtocol.State?
    private var receivedAt: TimeInterval = 0
    private(set) var target: ListenPlayTarget?
    /// The song being matched, so a heartbeat during the lookup doesn't
    /// start another.
    private var matching: ListenProtocol.Track?
    private var lastCommandAt: TimeInterval = -.infinity
    /// No player was open for the song: tried again once one is.
    private var waitingForPlayer = false
    private var savedShuffle: (kind: PlayerKind, on: Bool)?
    private var stopped = false
    var onEnd: ((Phase) -> Void)?

    init(room: String, base: URL?, sockets: any ListenSocketOpening = LiveListenSockets(),
         player: any ListenPlayerControl,
         clock: @escaping () -> TimeInterval = { ProcessInfo.processInfo.systemUptime }) {
        self.room = room
        self.socketURL = base.map { ListenProtocol.listenSocketURL($0, room: room) }
        self.sockets = sockets
        self.player = player
        self.clock = clock
    }

    var displayName: String {
        let name = hostName?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return name.isEmpty ? "a friend" : name
    }

    func start() {
        connect()
    }

    private func connect() {
        guard !stopped, let socketURL else { return }
        let socket = sockets.open(socketURL)
        self.socket = socket
        receiveTask?.cancel()
        receiveTask = Task { [weak self] in
            while !Task.isCancelled {
                do {
                    let text = try await socket.receive()
                    guard let self, let incoming = ListenProtocol.decode(text) else { continue }
                    self.failures = 0
                    self.receive(incoming)
                } catch {
                    self?.socketFailed(socket)
                    return
                }
            }
        }
    }

    private func socketFailed(_ failed: any ListenSocket) {
        guard socket === failed, !stopped else { return }
        socket = nil
        if failed.isUnknownRoom {
            finish(.notFound)
            return
        }
        failures += 1
        guard failures <= 8 else { finish(.notFound); return }
        let delay = ListenBackoff.delay(afterFailures: failures)
        reconnectTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: UInt64(delay * 1e9))
            guard let self, !Task.isCancelled else { return }
            self.connect()
        }
    }

    /// One message from the relay.
    func receive(_ incoming: ListenProtocol.Incoming) {
        guard !stopped else { return }
        switch incoming {
        case .state(let state): apply(state)
        case .ended: finish(.ended)
        case .listeners, .other: break
        }
    }

    private func apply(_ state: ListenProtocol.State) {
        let wasConnecting = phase == .connecting
        phase = .following
        if let name = state.host?.name { hostName = name }
        let previous = latest
        latest = state
        receivedAt = clock()
        if wasConnecting { Task { await self.saveShuffle() } }
        guard let incoming = state.track else {
            track = nil
            lyrics = []
            target = nil
            if previous?.playing == true { command { await $0.player.setPlaying(false) } }
            return
        }
        if let lines = state.lyrics { lyrics = lines }
        if let current = track, current.isSameSong(as: incoming) {
            track = incoming
            sync()
            return
        }
        track = incoming
        if state.lyrics == nil { lyrics = [] }
        target = nil
        note = nil
        songPageURL = nil
        waitingForPlayer = false
        startTrack(incoming)
    }

    /// Finds the host's song on the follower's service and plays it.
    private func startTrack(_ song: ListenProtocol.Track) {
        guard matching.map({ !$0.isSameSong(as: song) }) ?? true else { return }
        matching = song
        Task { [weak self] in
            guard let self else { return }
            let found = await self.findTarget(for: song)
            guard !self.stopped, let current = self.track, current.isSameSong(as: song) else { self.matching = nil; return }
            self.matching = nil
            guard let found else {
                self.waitingForPlayer = self.player.kind == nil
                self.note = Self.unavailableNote(for: song, on: self.player.kind)
                self.songPageURL = self.player.kind == nil ? nil : Self.songPage(for: song)
                return
            }
            self.target = found
            guard let state = self.latest else { return }
            let position = ListenClock.position(of: state, receivedAt: self.receivedAt, now: self.clock())
            self.lastCommandAt = self.clock()
            if await !self.player.play(found, at: position, playing: state.playing) {
                self.note = "Couldn\u{2019}t play \u{201C}\(song.title)\u{201D} \u{2014} still following"
            }
            self.lastCommandAt = self.clock()
        }
    }

    /// The host's song on the follower's service, with no network: Spotify
    /// plays by the host's Spotify ID (its AppleScript can't search); Music
    /// finds the song in the library by title and artist.
    func findTarget(for song: ListenProtocol.Track) async -> ListenPlayTarget? {
        switch player.kind {
        case .spotify:
            return song.spotifyId.map { .spotify(id: $0) }
        case .appleMusic:
            return await libraryHit(title: song.title, artist: song.artist, duration: song.duration)
        case nil:
            return nil
        }
    }

    static func unavailableNote(for song: ListenProtocol.Track, on kind: PlayerKind?) -> String {
        switch kind {
        case nil: return "Open Music or Spotify to listen along"
        case .spotify: return "\u{201C}\(song.title)\u{201D} isn\u{2019}t on your Spotify \u{2014} still following"
        case .appleMusic: return "\u{201C}\(song.title)\u{201D} isn\u{2019}t in your Music library \u{2014} still following"
        }
    }

    /// song.link's page for the song (a web page, not its API), so the
    /// listener can open it on another service themselves.
    static func songPage(for song: ListenProtocol.Track) -> URL? {
        DiscordPresence.songLink(spotifyURI: song.spotifyId.map { "spotify:track:\($0)" }, appleTrackID: song.appleId)
            .flatMap(URL.init(string:))
    }

    private func libraryHit(title: String, artist: String, duration: Double) async -> ListenPlayTarget? {
        let candidates = await player.searchLibrary(title: ListenTrackMatching.searchTerm(title))
        return ListenTrackMatching.best(candidates, title: title, artist: artist, duration: duration)
            .map { .library(persistentID: $0.persistentID) }
    }

    /// Brings the player back in line: the song, play or pause, and the
    /// position when it is more than `ListenDrift.threshold` out.
    func sync() {
        if !stopped, waitingForPlayer, target == nil, matching == nil, player.kind != nil, let track {
            waitingForPlayer = false
            note = nil
            Task { await self.saveShuffle() }
            startTrack(track)
            return
        }
        guard !stopped, let state = latest, let target, matching == nil else { return }
        let now = clock()
        guard now - lastCommandAt >= Self.settleTime else { return }
        let expected = ListenClock.position(of: state, receivedAt: receivedAt, now: now)
        guard let reading = player.reading() else { return }
        guard target.matches(reading) else {
            // The player moved on (the song ended early, or a skip): back.
            command { _ = await $0.player.play(target, at: expected, playing: state.playing) }
            return
        }
        if reading.playing != state.playing {
            command { await $0.player.setPlaying(state.playing) }
            return
        }
        if state.playing, case .seek(let to) = ListenDrift.decide(expected: expected, actual: reading.position) {
            command { await $0.player.seek(to: to) }
        }
    }

    private func command(_ work: @escaping @MainActor (ListenFollower) async -> Void) {
        lastCommandAt = clock()
        Task { [weak self] in
            guard let self else { return }
            await work(self)
            self.lastCommandAt = self.clock()
        }
    }

    private func saveShuffle() async {
        guard savedShuffle == nil, let kind = player.kind, let on = await player.shuffle(kind) else { return }
        guard !stopped else { return }
        savedShuffle = (kind, on)
        // Off while following, so a song that ends before the host's next
        // state doesn't jump somewhere random.
        if on { await player.setShuffle(false, kind) }
    }

    private func finish(_ phase: Phase) {
        self.phase = phase
        stop()
        onEnd?(phase)
    }

    /// Stops following: closes the socket and gives back shuffle. The music
    /// itself carries on.
    func stop() {
        guard !stopped else { return }
        stopped = true
        receiveTask?.cancel()
        reconnectTask?.cancel()
        socket?.close()
        socket = nil
        if let saved = savedShuffle, saved.on {
            let player = player
            Task { await player.setShuffle(true, saved.kind) }
        }
    }

    /// Whose shuffle to restore while the app quits, when nothing can be awaited.
    var shuffleToRestore: PlayerKind? {
        savedShuffle.flatMap { $0.on ? $0.kind : nil }
    }

    func markShuffleRestored() { savedShuffle = nil }
}
