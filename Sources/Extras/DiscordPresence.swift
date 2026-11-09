import AppKit
import Combine
import Darwin

/// What the small status under the user's name shows.
enum DiscordStatusText: String, CaseIterable, Identifiable {
    case lyric, song, artist

    var id: String { rawValue }

    var title: String {
        switch self {
        case .lyric: return "Lyric line"
        case .song: return "Song"
        case .artist: return "Artist"
        }
    }
}

/// What the user chose to share. Part of the activity, so a change sends it again.
struct DiscordOptions: Equatable {
    var statusText: DiscordStatusText = .lyric
    var showsLyric = true
    var showsCover = true
    var showsButton = true
    /// A "Play this song" button: a song.link page with the song on every service.
    var showsSongButton = true
    var showsProgress = true
    var showsWhenPaused = false
    /// How far ahead of the music the line is read, so it lands as it is sung.
    var lyricLead: Double = 2.5
}

/// Shows what is playing on the user's Discord profile (Rich Presence).
///
/// Discord's desktop app listens on a local Unix socket
/// (`$TMPDIR/discord-ipc-N`); this speaks its small framed-JSON protocol
/// directly, so there is no library and no login. Off by default. Nothing is
/// sent unless the user turns it on, and the activity is cleared when
/// playback stops or the feature is switched off.
///
/// - The small status under the user's name shows the line being sung (or the
///   song or artist, by choice); the full profile card shows the song and
///   artist, the line, the album on hover, the cover and a progress bar.
/// - The album cover is found on Apple's public search (title and artist
///   only) and cached; without one, the Lunalyr icon is shown.
/// - Discord allows five updates per twenty seconds: updates are spaced out,
///   so a very fast song skips some lines rather than being refused.
/// - Two buttons, both for others: "Play this song" (a song.link page that
///   lists the song on Spotify, Apple Music, YouTube Music and more) and
///   "Get Lunalyr". Discord never shows a person their own buttons.
@MainActor
final class DiscordPresence {
    nonisolated static let applicationID = "1568919234367717506"
    static let getLunalyrURL = "https://github.com/WaterBotol/Lunalyr/releases/latest"
    static let iconURL = "https://raw.githubusercontent.com/WaterBotol/Lunalyr/main/Sources/Assets.xcassets/AppIcon.appiconset/icon_256.png"
    /// Shortest gap between two activity updates.
    static let minimumUpdateGap: TimeInterval = 3
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
        var paused = false
        var coverURL: String?
        /// A page that opens the song on any service, when it was found.
        var songURL: String?
        var options = DiscordOptions()
    }

    static let playSongLabel = "Play this song"

    /// song.link for a song: from Spotify's own ID when Spotify is playing,
    /// else from Apple's catalogue ID. Nil for local files and unknown songs.
    nonisolated static func songLink(spotifyURI: String?, appleTrackID: Int?) -> String? {
        if let uri = spotifyURI, uri.hasPrefix("spotify:track:") {
            let id = uri.dropFirst("spotify:track:".count)
            if !id.isEmpty, id.allSatisfy({ $0.isLetter || $0.isNumber }) { return "https://song.link/s/\(id)" }
        }
        if let appleTrackID, appleTrackID > 0 { return "https://song.link/i/\(appleTrackID)" }
        return nil
    }

    /// The activity object for SET_ACTIVITY.
    static func activity(for playing: Playing) -> [String: Any] {
        let options = playing.options
        var hover = playing.artist
        if let album = playing.album, !album.isEmpty { hover += " — \(album)" }
        let song = playing.artist.isEmpty ? playing.title : "\(playing.title) · \(playing.artist)"
        let fallback = playing.artist.isEmpty ? playing.title : playing.artist
        let lyric = options.showsLyric ? playing.line : nil

        var state = lyric ?? fallback
        var displayType = 1   // 0 name, 1 state, 2 details
        switch options.statusText {
        case .lyric: break
        case .song: displayType = 2
        case .artist:
            state = fallback
        }
        var assets: [String: Any] = ["large_text": field(hover)]
        if options.showsCover, let cover = playing.coverURL {
            assets["large_image"] = cover
            assets["small_image"] = iconURL
            assets["small_text"] = "Lunalyr"
        } else {
            assets["large_image"] = iconURL
        }
        var activity: [String: Any] = [
            "type": 2,                       // Listening
            "status_display_type": displayType,
            "details": field(playing.paused ? "Paused · \(song)" : song),
            "state": field(playing.paused ? fallback : state),
            "assets": assets,
        ]
        var buttons: [[String: String]] = []
        if options.showsSongButton, let song = playing.songURL {
            buttons.append(["label": playSongLabel, "url": song])
        }
        if options.showsButton {
            buttons.append(["label": "Get Lunalyr", "url": getLunalyrURL])
        }
        if !buttons.isEmpty { activity["buttons"] = buttons }
        if options.showsProgress && !playing.paused {
            var timestamps: [String: Any] = ["start": playing.start]
            if let end = playing.end { timestamps["end"] = end }
            activity["timestamps"] = timestamps
        }
        return activity
    }

    static func setActivityPayload(_ activity: [String: Any]?, pid: Int32, nonce: String) -> [String: Any] {
        var args: [String: Any] = ["pid": Int(pid)]
        if let activity { args["activity"] = activity }
        return ["cmd": "SET_ACTIVITY", "args": args, "nonce": nonce]
    }

    /// The cover of the best match in an Apple search reply (iTunes Search
    /// API), at a size Discord shows well, or nil if no result is the song.
    nonisolated static func artworkURL(fromSearch data: Data, title: String, artist: String) -> String? {
        searchMatch(fromSearch: data, title: title, artist: artist)?.cover
    }

    struct SearchMatch: Equatable, Sendable {
        var cover: String?
        var trackID: Int?
    }

    /// The best match's cover and Apple catalogue ID, or nil if no result is the song.
    nonisolated static func searchMatch(fromSearch data: Data, title: String, artist: String) -> SearchMatch? {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let results = object["results"] as? [[String: Any]] else { return nil }
        let wantTitle = title.lowercased()
        let wantArtist = artist.lowercased()
        for result in results {
            let name = (result["trackName"] as? String ?? "").lowercased()
            let by = (result["artistName"] as? String ?? "").lowercased()
            guard !name.isEmpty, name.contains(wantTitle) || wantTitle.contains(name),
                  wantArtist.isEmpty || by.contains(wantArtist) || wantArtist.contains(by) else { continue }
            let cover = (result["artworkUrl100"] as? String)?.replacingOccurrences(of: "100x100bb", with: "512x512bb")
            let id = (result["trackId"] as? NSNumber)?.intValue
            guard cover != nil || id != nil else { continue }
            return SearchMatch(cover: cover, trackID: id)
        }
        return nil
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
    /// Search results by song; nil inside means looked up and not found.
    private var matches: [String: SearchMatch?] = [:]
    private var lookingUp: Set<String> = []

    init(extras: LunalyrExtras) {
        self.extras = extras
    }

    func start() {
        guard !running else { return }
        running = true
        let monitor = NowPlayingMonitor.shared
        var sources: [AnyPublisher<Void, Never>] = [
            monitor.$current.map { _ in () }.eraseToAnyPublisher(),
            monitor.$isPlaying.map { _ in () }.eraseToAnyPublisher(),
            LyricsViewModel.shared.$lines.map { _ in () }.eraseToAnyPublisher(),
        ]
        if let extras { sources.append(extras.objectWillChange.eraseToAnyPublisher()) }
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

    private var options: DiscordOptions { extras?.discordOptions ?? DiscordOptions() }

    /// What is shown for a break in the singing.
    static let instrumentalText = "♪ Instrumental"

    /// The line being sung `lead` seconds from now, `instrumentalText` in an
    /// intro or a break, or nil without timed lyrics.
    private func line(lead: Double, extras: LunalyrExtras) -> String? {
        guard extras.lyricsVisible, !extras.wallpaperPaused else { return nil }
        let model = LyricsViewModel.shared
        guard let first = model.lines.first else { return nil }
        let time = NowPlayingMonitor.shared.playbackTime() + Settings.shared.lyricOffset
            + SongOffsets.shared.offset(for: model.currentQuery) + lead
        guard let index = model.activeLineIndex(at: time), model.lines.indices.contains(index) else {
            return time < first.start ? Self.instrumentalText : nil
        }
        let line = model.lines[index]
        let words = RomanizationOptions.shared.display == .replace ? model.romanized[index] : nil
        let text = (words.map { $0.map(\.text).joined(separator: " ") } ?? line.text)
            .trimmingCharacters(in: .whitespacesAndNewlines)
        let finished = time > line.end + 1.5 && index + 1 < model.lines.count
        return line.isInstrumental || finished || text.isEmpty ? Self.instrumentalText : text
    }

    private func currentPlaying() -> Playing? {
        let monitor = NowPlayingMonitor.shared
        let options = options
        guard let track = monitor.current, !track.isAdvert, !track.title.isEmpty,
              monitor.isPlaying || options.showsWhenPaused else { return nil }
        let position = monitor.playbackTime()
        let now = Int(Date().timeIntervalSince1970)
        let key = "\(track.title)|\(track.artist)|\(track.album ?? "")"
        var start = now - Int(position)
        // Keep the bar still unless the song was scrubbed or restarted.
        if let anchor, anchor.trackKey == key, abs(anchor.start - start) <= 3 { start = anchor.start }
        anchor = (start, key)
        let end = track.duration > 0 ? start + Int(track.duration) : nil
        let text = monitor.isPlaying ? extras.flatMap { line(lead: options.lyricLead, extras: $0) } : nil
        let spotifyURI = track.persistentID.flatMap { $0.hasPrefix("spotify:track:") ? $0 : nil }
        let needsSearch = options.showsCover || (options.showsSongButton && spotifyURI == nil)
        let found = needsSearch ? match(for: track, key: key) : nil
        let songURL = options.showsSongButton ? Self.songLink(spotifyURI: spotifyURI, appleTrackID: found?.trackID) : nil
        return Playing(title: track.title, artist: track.artist, album: track.album, line: text,
                       start: start, end: end, paused: !monitor.isPlaying,
                       coverURL: options.showsCover ? found?.cover : nil, songURL: songURL, options: options)
    }

    /// The cached search result, starting a lookup the first time a song is seen.
    private func match(for track: NowPlayingMonitor.Track, key: String) -> SearchMatch? {
        if let known = matches[key] { return known }
        guard !lookingUp.contains(key) else { return nil }
        lookingUp.insert(key)
        let title = track.title, artist = track.artist
        Task { [weak self] in
            let found = await Self.lookUp(title: title, artist: artist)
            guard let self else { return }
            matches[key] = .some(found)
            lookingUp.remove(key)
            update()
        }
        return nil
    }

    nonisolated private static func lookUp(title: String, artist: String) async -> SearchMatch? {
        var components = URLComponents(string: "https://itunes.apple.com/search")
        components?.queryItems = [URLQueryItem(name: "term", value: "\(artist) \(title)"),
                                  URLQueryItem(name: "media", value: "music"),
                                  URLQueryItem(name: "entity", value: "song"),
                                  URLQueryItem(name: "limit", value: "8")]
        guard let url = components?.url else { return nil }
        var request = URLRequest(url: url)
        request.timeoutInterval = 6
        guard let (data, _) = try? await URLSession.shared.data(for: request) else { return nil }
        return searchMatch(fromSearch: data, title: title, artist: artist)
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
