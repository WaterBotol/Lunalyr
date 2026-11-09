import Foundation

/// What the host is playing, as the coordinator reads it.
struct ListenHostSnapshot: Equatable {
    var track: ListenProtocol.Track?
    var playing: Bool
    var position: Double
    /// Line timing for `track`, already shifted by the user's lyric offsets.
    var lyrics: [ListenProtocol.LyricLine] = []
    var hostName: String?
}

/// The sharing side: makes a room on the relay, keeps the host socket
/// open (reconnecting with back-off) and sends what plays -- on every
/// change, with a heartbeat every ten seconds, and the lyrics once per song.
@MainActor
final class ListenHost: ObservableObject {
    @Published private(set) var room: String?
    @Published private(set) var listeners = 0
    @Published private(set) var connected = false

    /// The most messages are kept under the relay's 64 KB.
    static let maximumMessageBytes = 60_000

    private let base: URL
    private let http: any ListenHTTP
    private let sockets: any ListenSocketOpening
    private let clock: () -> TimeInterval
    private var hostKey: String?
    private var socket: (any ListenSocket)?
    private var schedule = ListenHostSchedule()
    private var latest: ListenHostSnapshot?
    private var creating = false
    private var failures = 0
    private var reconnectTask: Task<Void, Never>?
    private var receiveTask: Task<Void, Never>?
    private var ended = false
    /// States sent so far; tests read it.
    private(set) var sentCount = 0

    init(base: URL, http: any ListenHTTP = LiveListenHTTP(), sockets: any ListenSocketOpening = LiveListenSockets(),
         clock: @escaping () -> TimeInterval = { ProcessInfo.processInfo.systemUptime }) {
        self.base = base
        self.http = http
        self.sockets = sockets
        self.clock = clock
    }

    /// Called whenever playback may have changed, and once a second.
    func update(_ snapshot: ListenHostSnapshot) {
        guard !ended else { return }
        latest = snapshot
        if room == nil {
            if snapshot.track != nil { createRoom() }
            return
        }
        sendIfDue()
    }

    private func createRoom() {
        guard !creating, reconnectTask == nil else { return }
        creating = true
        let http = http, url = ListenProtocol.roomsURL(base)
        Task { [weak self] in
            let reply = try? await http.post(url)
            guard let self else { return }
            creating = false
            guard !ended else { return }
            guard let reply, (200..<300).contains(reply.1), let created = ListenProtocol.roomCreated(from: reply.0) else {
                // Rate limited: wait as long as the relay asks, not the
                // usual back-off that starts at a second.
                let wait = reply?.1 == 429 ? ListenProtocol.roomCreationRetryAfter : nil
                retryLater(after: wait) { $0.createRoom() }
                return
            }
            room = created.room
            hostKey = created.hostKey
            connect()
        }
    }

    private func connect() {
        guard let room, let hostKey, let url = ListenProtocol.hostSocketURL(base, room: room, key: hostKey) else { return }
        socket?.close()
        let socket = sockets.open(url)
        self.socket = socket
        schedule.reset()
        connected = true
        receiveTask?.cancel()
        receiveTask = Task { [weak self] in
            while !Task.isCancelled {
                do {
                    let text = try await socket.receive()
                    guard let self else { return }
                    self.failures = 0
                    if case .listeners(let count) = ListenProtocol.decode(text) { self.listeners = count }
                } catch {
                    self?.socketFailed(socket)
                    return
                }
            }
        }
        sendIfDue()
    }

    private func socketFailed(_ failed: any ListenSocket) {
        guard socket === failed, !ended else { return }
        socket = nil
        connected = false
        listeners = 0
        if failed.isUnknownRoom || failed.closeCode == ListenProtocol.badKeyCloseCode {
            // The room expired, or no longer takes this key: a new one,
            // with a new code, rather than the same key every minute.
            room = nil
            hostKey = nil
            retryLater { host in
                if host.latest?.track != nil { host.createRoom() }
            }
        } else {
            retryLater { $0.connect() }
        }
    }

    /// Seconds before the pending retry; tests read it.
    private(set) var retryDelay: TimeInterval?

    private func retryLater(after wait: TimeInterval? = nil, _ work: @escaping @MainActor (ListenHost) -> Void) {
        failures += 1
        let delay = max(wait ?? 0, ListenBackoff.delay(afterFailures: failures))
        retryDelay = delay
        reconnectTask?.cancel()
        reconnectTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: UInt64(delay * 1e9))
            guard let self, !Task.isCancelled, !self.ended else { return }
            self.reconnectTask = nil
            work(self)
        }
    }

    private func sendIfDue() {
        guard let socket, let snapshot = latest else { return }
        let now = clock()
        let sample = ListenHostSchedule.Sample(track: snapshot.track, playing: snapshot.playing, position: snapshot.position)
        let lyricsDue = schedule.needsLyrics(for: snapshot.track) && !snapshot.lyrics.isEmpty
        guard schedule.reason(for: sample, now: now) != nil || lyricsDue else { return }
        var state = ListenProtocol.State(seq: schedule.seq + 1, track: snapshot.track, playing: snapshot.playing,
                                         position: snapshot.position, lyrics: nil,
                                         host: snapshot.hostName.map { ListenProtocol.Host(name: $0) })
        var data = ListenProtocol.encode(state)
        var withLyrics = false
        if lyricsDue {
            state.lyrics = snapshot.lyrics
            let full = ListenProtocol.encode(state)
            if full.count <= Self.maximumMessageBytes { data = full; withLyrics = true }
        }
        _ = schedule.sent(sample, now: now, withLyrics: withLyrics)
        sentCount += 1
        Task { [weak self] in
            do {
                try await socket.send(data)
                self?.failures = 0
            } catch {
                self?.socketFailed(socket)
            }
        }
    }

    /// Ends the room: listeners are told at once.
    func end() {
        guard !ended else { return }
        ended = true
        reconnectTask?.cancel()
        receiveTask?.cancel()
        connected = false
        guard let socket else { return }
        self.socket = nil
        Task {
            try? await socket.send(ListenProtocol.endMessage)
            socket.close()
        }
    }

    /// The same, while the app quits and nothing can be awaited.
    func endBeforeQuitting() {
        guard !ended else { return }
        ended = true
        reconnectTask?.cancel()
        receiveTask?.cancel()
        if let live = socket as? LiveListenSocket { live.sendBlocking(ListenProtocol.endMessage, timeout: 1) }
        socket?.close()
        socket = nil
    }
}
