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

    /// The only player a command for this song may go to.
    var kind: PlayerKind {
        switch self {
        case .spotify: return .spotify
        case .library: return .appleMusic
        }
    }

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
    /// Starts `target` at `position`, playing. Never called while the host
    /// is paused: a song is never started just to be paused again.
    func play(_ target: ListenPlayTarget, at position: Double) async -> Bool
    func seek(to position: Double) async
    /// Plays or pauses `kind` only -- the player the followed song was
    /// started on -- and never whichever player happens to be active.
    func setPlaying(_ playing: Bool, on kind: PlayerKind) async
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
///
/// The follower's player stays the user's. Lunalyr remembers what it last
/// told the player to do; any play-state or song change it didn't make (a
/// pause, a media key, headphones unplugged, sleep, another song, another
/// player) pauses following (`hold == .paused`, "Paused · Resume" in the
/// menu) and nothing is asserted again until the user clicks Resume.
@MainActor
final class ListenFollower: ObservableObject {
    enum Phase: Equatable {
        case connecting
        case following
        case ended
        /// The room is unknown or gone.
        case notFound
        /// The room has the most listeners it takes.
        case full
    }

    /// Why following is on hold: nothing is sent to the player until the
    /// user clicks.
    enum Hold: Equatable {
        /// A player opened after the join had to wait for one: Play starts it.
        case ready
        /// The user paused or changed the music themselves: Resume.
        case paused
    }

    @Published private(set) var phase: Phase = .connecting
    @Published private(set) var hostName: String?
    @Published private(set) var hold: Hold?
    /// A short note: the song can't be played here, no player is open.
    @Published private(set) var note: String?
    @Published private(set) var lyrics: [ListenProtocol.LyricLine] = []
    @Published private(set) var track: ListenProtocol.Track?
    /// The song's song.link page, offered with a note when the song can't
    /// be played here.
    @Published private(set) var songPageURL: URL?
    /// The connection dropped and is being tried again; the menu says so
    /// rather than still claiming to listen along.
    @Published private(set) var isReconnecting = false

    let room: String
    /// Seconds between drift checks.
    static let tickInterval: TimeInterval = 2
    /// Seconds after a command before the player's own reading is trusted.
    static let settleTime: TimeInterval = 3
    /// A song that stops matching this close to the host's end ran out on
    /// its own (a library copy can be up to 10 s shorter), not a skip.
    static let naturalEndMargin: Double = 12
    /// Seconds after the host's song ran out (or restarted) during which
    /// the follower's own song running out is not taken for a skip.
    static let naturalEndGrace: TimeInterval = 20

    /// What Lunalyr last told the player: the song, and playing or paused.
    struct Expectation: Equatable {
        var target: ListenPlayTarget
        var playing: Bool
    }

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
    /// The host's song on this Mac's service, once found.
    private(set) var target: ListenPlayTarget?
    /// What the player was last told; nil until Lunalyr has started a song.
    private(set) var expected: Expectation?
    /// The song being matched, so a heartbeat during the lookup doesn't
    /// start another.
    private var matching: ListenProtocol.Track?
    private var lastCommandAt: TimeInterval = -.infinity
    /// When the host's song last ran out on its own or restarted (repeat),
    /// so the follower's copy ending around then is not a skip.
    private var hostSongEndedAt: TimeInterval?
    /// No player was open for the song: once one is, following waits for
    /// a Play click (`Hold.ready`) rather than starting by itself.
    private var waitingForPlayer = false
    private var savedShuffle: (kind: PlayerKind, on: Bool)?
    private var stopped = false
    var onEnd: ((Phase) -> Void)?
    /// The first state arrived: following has begun.
    var onFollowing: (() -> Void)?

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
        ListenProtocol.cleanHostName(hostName) ?? "a friend"
    }

    /// Connects. Called once the user has accepted the invitation, which is
    /// their Play click for the first song.
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
                    guard let self else { return }
                    guard let incoming = ListenProtocol.decode(text) else { continue }
                    self.failures = 0
                    if self.isReconnecting { self.isReconnecting = false }
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
        if failed.closeCode == ListenProtocol.roomFullCloseCode {
            finish(.full)
            return
        }
        if failed.isUnknownRoom {
            finish(.notFound)
            return
        }
        failures += 1
        guard failures <= 8 else { finish(.notFound); return }
        isReconnecting = true
        let delay = ListenBackoff.delay(afterFailures: failures)
        reconnectTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: UInt64(delay * 1e9))
            guard let self, !Task.isCancelled else { return }
            self.connect()
        }
    }

    /// Whether a state ever arrived: an unknown room then means it ended.
    var everConnected: Bool { latest != nil }

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
        if let name = ListenProtocol.cleanHostName(state.host?.name) { hostName = name }
        if wasConnecting { onFollowing?() }
        let restarted = noteHostSongEnd(previous: latest, previousAt: receivedAt, next: state)
        latest = state
        receivedAt = clock()
        if wasConnecting, hold == nil { Task { await self.saveShuffle() } }
        guard let incoming = state.track else {
            track = nil
            lyrics = []
            target = nil
            note = nil
            songPageURL = nil
            sync()
            return
        }
        if let lines = state.lyrics { lyrics = lines }
        if let current = track, current.isSameSong(as: incoming) {
            track = incoming
            // The host's song came round again (repeat) after the follower's
            // copy ran out and was let go: look it up again.
            if restarted, target == nil, expected == nil, hold == nil, !waitingForPlayer {
                startTrack(incoming)
                return
            }
            sync()
            return
        }
        track = incoming
        if state.lyrics == nil { lyrics = [] }
        target = nil
        note = nil
        songPageURL = nil
        waitingForPlayer = false
        // On hold, the song is looked for when the user resumes.
        if hold == nil { startTrack(incoming) }
    }

    /// Notes when the host's previous state was its song running out (it
    /// was near the end, playing) and `next` is another song or the same
    /// one from the top. Returns whether it was the same one restarting.
    private func noteHostSongEnd(previous: ListenProtocol.State?, previousAt: TimeInterval,
                                 next: ListenProtocol.State) -> Bool {
        guard let previous, previous.playing, let old = previous.track, old.duration > 0 else { return false }
        let now = clock()
        let wasAt = ListenClock.position(of: previous, receivedAt: previousAt, now: now)
        guard wasAt >= old.duration - Self.naturalEndMargin else { return false }
        let restarted = next.track.map { $0.isSameSong(as: old) && next.position < Self.naturalEndMargin } ?? false
        let changed = next.track.map { !$0.isSameSong(as: old) } ?? false
        guard restarted || changed else { return false }
        hostSongEndedAt = now
        return restarted
    }

    /// Finds the host's song on the follower's service; `sync` plays it
    /// once the host is playing.
    private func startTrack(_ song: ListenProtocol.Track) {
        guard matching.map({ !$0.isSameSong(as: song) }) ?? true else { return }
        matching = song
        Task { [weak self] in
            guard let self else { return }
            let found = await self.findTarget(for: song)
            // Only this lookup's own mark is cleared: a newer song's lookup
            // may be running.
            if self.matching.map({ $0.isSameSong(as: song) }) ?? false { self.matching = nil }
            guard !self.stopped, let current = self.track, current.isSameSong(as: song) else { return }
            guard self.hold == nil else { return }
            guard let found else {
                self.waitingForPlayer = self.player.kind == nil
                self.note = Self.unavailableNote(for: song, on: self.player.kind)
                self.songPageURL = self.player.kind == nil ? nil : Self.songPage(for: song)
                // A song still playing from before is paused, not left on.
                self.sync()
                return
            }
            self.target = found
            self.sync()
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
        default:
            // Browsers and local-file players can't be told to play a song.
            return nil
        }
    }

    static func unavailableNote(for song: ListenProtocol.Track, on kind: PlayerKind?) -> String {
        switch kind {
        case nil: return "Open Music or Spotify to listen along"
        case .spotify: return "\u{201C}\(song.title)\u{201D} can\u{2019}t be found on Spotify from here \u{2014} still following"
        case .appleMusic: return "\u{201C}\(song.title)\u{201D} isn\u{2019}t in your Music library \u{2014} still following"

        default: return "Listening along needs Music or Spotify \u{2014} still following"
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

    /// Brings the player in line with the host -- the song, play or pause,
    /// and the position when it is more than `ListenDrift.threshold` out --
    /// after first checking that the player is still where Lunalyr left it.
    func sync() {
        guard !stopped, hold == nil, matching == nil, let state = latest else { return }
        if waitingForPlayer {
            // A player opened: wait for the user's Play, never start by itself.
            if player.kind != nil {
                waitingForPlayer = false
                note = nil
                hold = .ready
            }
            return
        }
        let now = clock()
        guard now - lastCommandAt >= Self.settleTime else { return }
        let hostPosition = ListenClock.position(of: state, receivedAt: receivedAt, now: now)
        if let expected {
            let reading = player.reading()
            if let reading, expected.target.matches(reading), reading.playing == expected.playing {
                // Still where Lunalyr left it.
            } else if expected.playing, let endedAt = hostSongEndedAt, now - endedAt < Self.naturalEndGrace {
                // The host's song ran out and then the follower's did too
                // (the host's next state came first): not a skip. Their
                // next song (or the same one again) is started below.
                hostSongEndedAt = nil
                self.expected = nil
            } else if expected.playing, state.playing, ranOut(hostPosition: hostPosition) {
                // The song ended a little before the host's: the host's next
                // one is started when it comes, not this one again.
                self.expected = nil
                self.target = nil
                return
            } else {
                pauseFollowing()
                return
            }
        }
        if let target, expected?.target != target {
            // Never started while the host is paused (that would be heard
            // before the pause): it starts when they play.
            if state.playing {
                hostSongEndedAt = nil
                expected = Expectation(target: target, playing: true)
                command { await $0.player.play(target, at: hostPosition) } failed: { follower in
                    // Not tried again until the host's next song.
                    follower.expected = nil
                    follower.target = nil
                    follower.note = "Couldn\u{2019}t play \u{201C}\(follower.track?.title ?? "the song")\u{201D} \u{2014} still following"
                }
                return
            }
        }
        guard let current = expected else { return }
        let shouldPlay = state.playing && current.target == target
        if current.playing != shouldPlay {
            expected?.playing = shouldPlay
            let kind = current.target.kind
            command { await $0.player.setPlaying(shouldPlay, on: kind) }
            return
        }
        if shouldPlay, let reading = player.reading(),
           case .seek(let to) = ListenDrift.decide(expected: hostPosition, actual: reading.position) {
            command { await $0.player.seek(to: to) }
        }
    }

    /// Whether the followed song is at (or past) its end on the host.
    private func ranOut(hostPosition: Double) -> Bool {
        guard let duration = track?.duration, duration > 0 else { return false }
        return hostPosition >= duration - Self.naturalEndMargin
    }

    /// The user took the player back: stop sending it anything until they
    /// click Resume, and give their shuffle back.
    private func pauseFollowing() {
        hold = .paused
        expected = nil
        note = nil
        restoreShuffle()
    }

    /// The user's Play or Resume click: following starts again from the
    /// host's current song and moment.
    func resume() {
        guard !stopped, hold != nil else { return }
        hold = nil
        note = nil
        songPageURL = nil
        expected = nil
        target = nil
        waitingForPlayer = false
        lastCommandAt = -.infinity
        Task { await self.saveShuffle() }
        if let track { startTrack(track) } else { sync() }
    }

    private func command(_ work: @escaping @MainActor (ListenFollower) async -> Void) {
        lastCommandAt = clock()
        Task { [weak self] in
            guard let self else { return }
            await work(self)
            self.lastCommandAt = self.clock()
        }
    }

    private func command(_ work: @escaping @MainActor (ListenFollower) async -> Bool,
                         failed: @escaping @MainActor (ListenFollower) -> Void) {
        lastCommandAt = clock()
        Task { [weak self] in
            guard let self else { return }
            let ok = await work(self)
            self.lastCommandAt = self.clock()
            if !ok, !self.stopped { failed(self) }
        }
    }

    private func saveShuffle() async {
        guard savedShuffle == nil, let kind = player.kind, let on = await player.shuffle(kind) else { return }
        guard !stopped, hold != .paused, savedShuffle == nil else { return }
        savedShuffle = (kind, on)
        // Off while following, so a song that ends before the host's next
        // state doesn't jump somewhere random.
        if on { await player.setShuffle(false, kind) }
    }

    private func restoreShuffle() {
        guard let saved = savedShuffle else { return }
        savedShuffle = nil
        if saved.on {
            let player = player
            Task { await player.setShuffle(true, saved.kind) }
        }
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
        restoreShuffle()
    }

    /// Whose shuffle to restore while the app quits, when nothing can be awaited.
    var shuffleToRestore: PlayerKind? {
        savedShuffle.flatMap { $0.on ? $0.kind : nil }
    }

    func markShuffleRestored() { savedShuffle = nil }
}
