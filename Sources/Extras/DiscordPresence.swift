import AppKit
import Combine
import Darwin

/// Shows what is playing on the user's Discord profile (Rich Presence).
///
/// Discord's desktop app listens on a local Unix socket
/// (`$TMPDIR/discord-ipc-N`); this speaks its small framed-JSON protocol
/// directly, so there is no library, no login and no network traffic from
/// Lunalyr. Off by default. Nothing is sent unless the user turns it on, and
/// the activity is cleared when playback stops or the feature is switched off.
///
/// - The small status under the user's name shows the line being sung
///   (`status_display_type` = state); the full profile card shows the song
///   and artist, the line, the album on hover and a progress bar.
/// - Discord allows five updates per twenty seconds: updates are spaced out,
///   so a very fast song skips some lines rather than being refused.
@MainActor
final class DiscordPresence {
    nonisolated static let applicationID = "1568919234367717506"
    static let getLunalyrURL = "https://github.com/WaterBotol/Lunalyr/releases/latest"
    static let iconURL = "https://raw.githubusercontent.com/WaterBotol/Lunalyr/main/Sources/Assets.xcassets/AppIcon.appiconset/icon_256.png"
    /// Shortest gap between two activity updates.
    static let minimumUpdateGap: TimeInterval = 4
    /// How often the line is checked while a song plays.
    static let tickInterval: TimeInterval = 0.5
    /// How often a missing Discord is looked for again.
    nonisolated static let reconnectInterval: TimeInterval = 20

    // MARK: - Protocol (pure, tested)

    enum Opcode: UInt32 { case handshake = 0, frame = 1, close = 2, ping = 3, pong = 4 }

    /// One IPC packet: opcode and payload length (little endian), then JSON.
    nonisolated static func frame(_ opcode: Opcode, _ payload: [String: Any]) -> Data? {
        guard let json = try? JSONSerialization.data(withJSONObject: payload) else { return nil }
        var data = Data()
        var code = opcode.rawValue.littleEndian
        var length = UInt32(json.count).littleEndian
        withUnsafeBytes(of: &code) { data.append(contentsOf: $0) }
        withUnsafeBytes(of: &length) { data.append(contentsOf: $0) }
        data.append(json)
        return data
    }

    /// Discord wants 2...128 characters in each text field.
    static func field(_ text: String) -> String {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        let clipped = trimmed.count > 128 ? String(trimmed.prefix(127)) + "…" : trimmed
        return clipped.count < 2 ? clipped + " ♪" : clipped
    }

    struct Playing: Equatable {
        var title: String
        var artist: String
        var album: String?
        /// The line being sung, or nil between lines and without timed lyrics.
        var line: String?
        /// When the song would have started and ends, in Unix seconds.
        var start: Int
        var end: Int?
    }

    /// The activity object for SET_ACTIVITY.
    static func activity(for playing: Playing) -> [String: Any] {
        var hover = playing.artist
        if let album = playing.album, !album.isEmpty { hover += " — \(album)" }
        var activity: [String: Any] = [
            "type": 2,                       // Listening
            // 1 = the small status shows `state`, the lyric line.
            "status_display_type": 1,
            "details": field(playing.artist.isEmpty ? playing.title : "\(playing.title) · \(playing.artist)"),
            "state": field(playing.line ?? (playing.artist.isEmpty ? playing.title : playing.artist)),
            "assets": [
                "large_image": iconURL,
                "large_text": field(hover),
            ],
            "buttons": [["label": "Get Lunalyr", "url": getLunalyrURL]],
        ]
        var timestamps: [String: Any] = ["start": playing.start]
        if let end = playing.end { timestamps["end"] = end }
        activity["timestamps"] = timestamps
        return activity
    }

    static func setActivityPayload(_ activity: [String: Any]?, pid: Int32, nonce: String) -> [String: Any] {
        var args: [String: Any] = ["pid": Int(pid)]
        if let activity { args["activity"] = activity }
        return ["cmd": "SET_ACTIVITY", "args": args, "nonce": nonce]
    }

    // MARK: - State

    private weak var extras: LunalyrExtras?
    private let transport = DiscordTransport()
    private var changes: Set<AnyCancellable> = []
    private var timer: Timer?
    private var lastSent: Playing??
    private var lastSentAt = Date.distantPast
    /// The timestamps already shown, so a line change does not move the bar.
    private var anchor: (start: Int, trackKey: String)?
    private var running = false

    init(extras: LunalyrExtras) {
        self.extras = extras
    }

    func start() {
        guard !running else { return }
        running = true
        let monitor = NowPlayingMonitor.shared
        let sources: [AnyPublisher<Void, Never>] = [
            monitor.$current.map { _ in () }.eraseToAnyPublisher(),
            monitor.$isPlaying.map { _ in () }.eraseToAnyPublisher(),
            LyricsViewModel.shared.$lines.map { _ in () }.eraseToAnyPublisher(),
        ]
        Publishers.MergeMany(sources)
            .receive(on: DispatchQueue.main)
            .sink { [weak self] in self?.update() }
            .store(in: &changes)
        let timer = Timer(timeInterval: Self.tickInterval, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.update() }
        }
        timer.tolerance = 0.2
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
        update()
    }

    func stop() {
        guard running else { return }
        running = false
        timer?.invalidate()
        timer = nil
        changes.removeAll()
        transport.send(Self.setActivityPayload(nil, pid: ProcessInfo.processInfo.processIdentifier, nonce: UUID().uuidString),
                       thenDisconnect: true)
        lastSent = nil
        anchor = nil
    }

    private func currentPlaying() -> Playing? {
        let monitor = NowPlayingMonitor.shared
        guard monitor.isPlaying, let track = monitor.current, !track.isAdvert,
              !track.title.isEmpty else { return nil }
        let position = monitor.playbackTime()
        let now = Int(Date().timeIntervalSince1970)
        let key = "\(track.title)|\(track.artist)|\(track.album ?? "")"
        var start = now - Int(position)
        // Keep the bar still unless the song was scrubbed or restarted.
        if let anchor, anchor.trackKey == key, abs(anchor.start - start) <= 3 { start = anchor.start }
        anchor = (start, key)
        let end = track.duration > 0 ? start + Int(track.duration) : nil
        var line: String?
        if let extras, extras.lyricsVisible, !extras.wallpaperPaused {
            let text = MenuBarLyricTicker.currentText(limit: 128, extras: extras)
            if let text, text != "♪", text != MenuBarLyricTicker.pausedText, text != MenuBarLyricTicker.hiddenText {
                line = text
            }
        }
        return Playing(title: track.title, artist: track.artist, album: track.album,
                       line: line, start: start, end: end)
    }

    private func update() {
        guard running else { return }
        let playing = currentPlaying()
        if let lastSent, lastSent == playing { return }
        // Spaced out; a clear (nil) is sent at once so a pause shows.
        if playing != nil, Date().timeIntervalSince(lastSentAt) < Self.minimumUpdateGap { return }
        lastSent = .some(playing)
        lastSentAt = Date()
        if playing == nil { anchor = nil }
        transport.send(Self.setActivityPayload(playing.map(Self.activity(for:)),
                                               pid: ProcessInfo.processInfo.processIdentifier,
                                               nonce: UUID().uuidString))
    }
}

/// The socket to Discord, on its own serial queue. Connects on demand,
/// remembers the last activity and re-sends it after Discord restarts.
final class DiscordTransport: @unchecked Sendable {
    private let queue = DispatchQueue(label: "lunalyr.discord-ipc", qos: .utility)
    private var fd: Int32 = -1
    private var lastFailure = Date.distantPast
    private var pending: [String: Any]?

    /// Sends a SET_ACTIVITY payload. If Discord is not running it is dropped
    /// and the next update tries again, at most every `reconnectInterval`.
    func send(_ payload: [String: Any], thenDisconnect: Bool = false) {
        nonisolated(unsafe) let payload = payload
        queue.async { [self] in
            if fd < 0 {
                guard Date().timeIntervalSince(lastFailure) >= DiscordPresence.reconnectInterval else { return }
                guard connect() else { lastFailure = Date(); return }
            }
            guard let data = DiscordPresence.frame(.frame, payload), write(data) else {
                disconnect(); lastFailure = Date(); return
            }
            drain()
            if thenDisconnect { disconnect() }
        }
    }

    private func connect() -> Bool {
        let base = NSTemporaryDirectory().hasSuffix("/") ? String(NSTemporaryDirectory().dropLast()) : NSTemporaryDirectory()
        for index in 0..<10 {
            let path = "\(base)/discord-ipc-\(index)"
            let sock = socket(AF_UNIX, SOCK_STREAM, 0)
            guard sock >= 0 else { continue }
            var noSigPipe: Int32 = 1
            setsockopt(sock, SOL_SOCKET, SO_NOSIGPIPE, &noSigPipe, socklen_t(MemoryLayout<Int32>.size))
            var address = sockaddr_un()
            address.sun_family = sa_family_t(AF_UNIX)
            let bytes = Array(path.utf8CString)
            guard bytes.count <= MemoryLayout.size(ofValue: address.sun_path) else { close(sock); continue }
            withUnsafeMutableBytes(of: &address.sun_path) { buffer in
                for (offset, byte) in bytes.enumerated() { buffer[offset] = UInt8(bitPattern: byte) }
            }
            let result = withUnsafePointer(to: &address) {
                $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                    Darwin.connect(sock, $0, socklen_t(MemoryLayout<sockaddr_un>.size))
                }
            }
            guard result == 0 else { close(sock); continue }
            fd = sock
            let hello: [String: Any] = ["v": 1, "client_id": DiscordPresence.applicationID]
            if let data = DiscordPresence.frame(.handshake, hello), write(data), awaitReady() { return true }
            disconnect()
        }
        return false
    }

    /// Waits (briefly) for Discord's READY reply to the handshake.
    private func awaitReady() -> Bool {
        guard let reply = readPacket(timeout: 3) else { return false }
        return reply.opcode == DiscordPresence.Opcode.frame.rawValue
            && (String(data: reply.body, encoding: .utf8) ?? "").contains("READY")
    }

    private func write(_ data: Data) -> Bool {
        data.withUnsafeBytes { raw in
            var sent = 0
            while sent < data.count {
                let n = Darwin.write(fd, raw.baseAddress!.advanced(by: sent), data.count - sent)
                if n <= 0 { return false }
                sent += n
            }
            return true
        }
    }

    /// Reads and discards Discord's replies so the socket never fills.
    private func drain() {
        while let packet = readPacket(timeout: 0.05) {
            if packet.opcode == DiscordPresence.Opcode.close.rawValue { disconnect(); return }
            if packet.opcode == DiscordPresence.Opcode.ping.rawValue,
               let pong = DiscordPresence.frame(.pong, (try? JSONSerialization.jsonObject(with: packet.body)) as? [String: Any] ?? [:]) {
                _ = write(pong)
            }
        }
    }

    private func readPacket(timeout: TimeInterval) -> (opcode: UInt32, body: Data)? {
        guard fd >= 0, let header = readExactly(8, timeout: timeout) else { return nil }
        let opcode = header.withUnsafeBytes { $0.loadUnaligned(fromByteOffset: 0, as: UInt32.self) }.littleEndian
        let length = Int(header.withUnsafeBytes { $0.loadUnaligned(fromByteOffset: 4, as: UInt32.self) }.littleEndian)
        guard length < 1 << 20, let body = length == 0 ? Data() : readExactly(length, timeout: 1) else { return nil }
        return (opcode, body)
    }

    private func readExactly(_ count: Int, timeout: TimeInterval) -> Data? {
        var data = Data()
        while data.count < count {
            var descriptor = pollfd(fd: fd, events: Int16(POLLIN), revents: 0)
            let ready = poll(&descriptor, 1, Int32(timeout * 1000))
            if ready <= 0 { return nil }
            var buffer = [UInt8](repeating: 0, count: count - data.count)
            let n = read(fd, &buffer, buffer.count)
            if n <= 0 { disconnect(); return nil }
            data.append(contentsOf: buffer[..<n])
        }
        return data
    }

    private func disconnect() {
        if fd >= 0 { close(fd) }
        fd = -1
    }
}
