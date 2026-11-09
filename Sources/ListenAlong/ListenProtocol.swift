import Foundation

/// The listen-along relay protocol, v1 (`listen-protocol.md`, shared with the
/// relay and its web page). Pure: encoding, decoding, addresses and the
/// timing rules, so every part is tested without a socket.
enum ListenProtocol {
    static let version = 1

    struct Track: Codable, Equatable, Sendable {
        var title: String
        var artist: String
        var album: String?
        var duration: Double
        var spotifyId: String?
        var appleId: Int?
        var isrc: String?

        /// The recording, for "is this still the same song": title, artist
        /// and a duration within a second and a half.
        func isSameSong(as other: Track) -> Bool {
            if let a = spotifyId, let b = other.spotifyId { return a == b }
            if let a = appleId, let b = other.appleId { return a == b }
            return title == other.title && artist == other.artist && abs(duration - other.duration) < 1.5
        }
    }

    /// Line timing only: the word timing stays on the host.
    struct LyricLine: Codable, Equatable, Sendable {
        var start: Double
        var end: Double
        var text: String
    }

    struct Host: Codable, Equatable, Sendable {
        var name: String?
    }

    struct State: Equatable, Sendable {
        var seq: Int
        var track: Track?
        var playing: Bool
        /// Seconds into the song when the host sent it.
        var position: Double
        var lyrics: [LyricLine]?
        var host: Host?
        /// Milliseconds the relay held this state before forwarding it;
        /// set by the relay only.
        var age: Double?
        var v: Int = ListenProtocol.version
    }

    struct RoomCreated: Decodable, Equatable, Sendable {
        var room: String
        var hostKey: String
    }

    /// Everything a socket can bring in.
    enum Incoming: Equatable, Sendable {
        case state(State)
        case listeners(Int)
        case ended
        /// Another type, for a newer relay: ignored, never fatal.
        case other(String)
    }

    // MARK: - Encoding

    /// A state message. `track` is written as `null` when nothing plays.
    static func encode(_ state: State) -> Data {
        var object: [String: Any] = [
            "type": "state", "v": state.v, "seq": state.seq,
            "playing": state.playing, "position": rounded(state.position),
            "track": NSNull(),
        ]
        if let track = state.track {
            var t: [String: Any] = ["title": track.title, "artist": track.artist, "duration": rounded(track.duration)]
            if let album = track.album, !album.isEmpty { t["album"] = album }
            if let id = track.spotifyId { t["spotifyId"] = id }
            if let id = track.appleId { t["appleId"] = id }
            if let isrc = track.isrc { t["isrc"] = isrc }
            object["track"] = t
        }
        if let lyrics = state.lyrics {
            object["lyrics"] = lyrics.map { ["start": rounded($0.start), "end": rounded($0.end), "text": $0.text] as [String: Any] }
        }
        if let name = state.host?.name, !name.isEmpty { object["host"] = ["name": name] }
        if let age = state.age { object["age"] = age }
        return (try? JSONSerialization.data(withJSONObject: object, options: [.sortedKeys])) ?? Data()
    }

    static let endMessage = Data(#"{"type":"end"}"#.utf8)

    private static func rounded(_ value: Double) -> Double {
        guard value.isFinite else { return 0 }
        return (value * 1000).rounded() / 1000
    }

    // MARK: - Decoding

    static func decode(_ data: Data) -> Incoming? {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let type = object["type"] as? String else { return nil }
        switch type {
        case "state": return state(from: object).map(Incoming.state)
        case "listeners": return .listeners(max(0, (object["count"] as? NSNumber)?.intValue ?? 0))
        case "ended": return .ended
        default: return .other(type)
        }
    }

    static func decode(_ text: String) -> Incoming? { decode(Data(text.utf8)) }

    private static func state(from object: [String: Any]) -> State? {
        let number = { (key: String) in (object[key] as? NSNumber)?.doubleValue }
        guard let position = number("position"), position.isFinite else { return nil }
        var track: Track?
        if let t = object["track"] as? [String: Any], let title = t["title"] as? String {
            let duration = (t["duration"] as? NSNumber)?.doubleValue ?? 0
            track = Track(title: title, artist: t["artist"] as? String ?? "", album: t["album"] as? String,
                          duration: duration.isFinite ? duration : 0,
                          spotifyId: (t["spotifyId"] as? String).flatMap(validSpotifyID),
                          appleId: (t["appleId"] as? NSNumber)?.intValue ?? (t["appleId"] as? String).flatMap { Int($0) },
                          isrc: t["isrc"] as? String)
        }
        let lyrics = (object["lyrics"] as? [[String: Any]])?.compactMap { line -> LyricLine? in
            guard let start = (line["start"] as? NSNumber)?.doubleValue, let text = line["text"] as? String else { return nil }
            return LyricLine(start: start, end: (line["end"] as? NSNumber)?.doubleValue ?? start, text: text)
        }
        let host = (object["host"] as? [String: Any]).map { Host(name: $0["name"] as? String) }
        return State(seq: (object["seq"] as? NSNumber)?.intValue ?? 0, track: track,
                     playing: (object["playing"] as? Bool) ?? false, position: max(0, position),
                     lyrics: lyrics, host: host, age: number("age").flatMap { $0.isFinite && $0 >= 0 ? $0 : nil },
                     v: (object["v"] as? NSNumber)?.intValue ?? version)
    }

    static func roomCreated(from data: Data) -> RoomCreated? {
        guard let created = try? JSONDecoder().decode(RoomCreated.self, from: data),
              isValidRoom(created.room), !created.hostKey.isEmpty else { return nil }
        return created
    }

    // MARK: - Identifiers

    /// Ten lower-case letters or digits.
    static func isValidRoom(_ room: String) -> Bool {
        room.count == 10 && room.unicodeScalars.allSatisfy { ("a"..."z").contains($0) || ("0"..."9").contains($0) }
    }

    /// A room from what someone pasted: the code, a listen page address or
    /// a `/rooms/<room>/…` address.
    static func room(fromInput input: String) -> String? {
        let text = input.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        if isValidRoom(text) { return text }
        let parts = text.split(separator: "/").map(String.init)
        for (index, part) in parts.enumerated() where (part == "listen" || part == "rooms") && index + 1 < parts.count {
            let candidate = String(parts[index + 1].prefix { $0 != "?" && $0 != "#" })
            if isValidRoom(candidate) { return candidate }
        }
        return nil
    }

    static func validSpotifyID(_ id: String) -> String? {
        !id.isEmpty && id.count <= 40 && id.allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber) }) ? id : nil
    }

    /// The track ID in Spotify's `spotify:track:<id>` URI; nil for local
    /// files, episodes and adverts.
    static func spotifyID(fromURI uri: String?) -> String? {
        guard let uri, uri.hasPrefix("spotify:track:") else { return nil }
        return validSpotifyID(String(uri.dropFirst("spotify:track:".count)))
    }

    // MARK: - Addresses

    /// The relay's base address, or nil for an empty or unusable setting.
    static func base(_ setting: String) -> URL? {
        var text = setting.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return nil }
        if !text.contains("://") { text = "https://" + text }
        while text.hasSuffix("/") { text.removeLast() }
        guard let url = URL(string: text), let scheme = url.scheme?.lowercased(),
              scheme == "https" || scheme == "http", url.host?.isEmpty == false else { return nil }
        return url
    }

    static func roomsURL(_ base: URL) -> URL { base.appendingPathComponent("rooms") }

    static func hostSocketURL(_ base: URL, room: String, key: String) -> URL? {
        var components = URLComponents(url: socketBase(base).appendingPathComponent("rooms/\(room)/host"), resolvingAgainstBaseURL: false)
        components?.queryItems = [URLQueryItem(name: "key", value: key)]
        return components?.url
    }

    static func listenSocketURL(_ base: URL, room: String) -> URL {
        socketBase(base).appendingPathComponent("rooms/\(room)/listen")
    }

    /// The web listen-along page, for people without Lunalyr.
    static func webPageURL(_ base: URL, room: String) -> URL {
        base.appendingPathComponent("listen/\(room)")
    }

    private static func socketBase(_ base: URL) -> URL {
        guard var components = URLComponents(url: base, resolvingAgainstBaseURL: false) else { return base }
        components.scheme = components.scheme?.lowercased() == "http" ? "ws" : "wss"
        return components.url ?? base
    }

    /// The close code the relay uses for a room it does not know.
    static let unknownRoomCloseCode = 4404
}

/// Where the host is in the song now, from a state and when it arrived.
enum ListenClock {
    /// `position + age/1000 + seconds since it arrived` while playing; the
    /// position as sent while paused. Never past the song's end.
    static func position(of state: ListenProtocol.State, receivedAt: TimeInterval, now: TimeInterval) -> Double {
        var position = state.position
        if state.playing {
            position += (state.age ?? 0) / 1000 + max(0, now - receivedAt)
        }
        if let duration = state.track?.duration, duration > 0 { position = min(position, duration) }
        return max(0, position)
    }
}

/// What a follower does about the gap between its player and the host.
enum ListenDrift: Equatable {
    case none
    case seek(to: Double)

    /// Above this many seconds apart, the follower seeks.
    static let threshold: Double = 1.5

    static func decide(expected: Double, actual: Double, threshold: Double = threshold) -> ListenDrift {
        guard expected.isFinite, actual.isFinite else { return .none }
        return abs(expected - actual) > threshold ? .seek(to: max(0, expected)) : .none
    }
}

/// When the host sends: on every change, and a heartbeat in between.
struct ListenHostSchedule {
    static let heartbeat: TimeInterval = 10
    /// A jump bigger than this is a seek, and sent at once.
    static let seekThreshold: Double = 1.5

    enum Reason: Equatable { case first, track, playPause, seek, heartbeat }

    struct Sample: Equatable {
        var track: ListenProtocol.Track?
        var playing: Bool
        var position: Double
    }

    private(set) var last: Sample?
    private(set) var lastSentAt: TimeInterval = -.infinity
    /// The track whose lyrics were already sent.
    private(set) var lyricsSentFor: ListenProtocol.Track?
    private(set) var seq = 0

    /// Why `sample` should be sent at `now`, or nil to stay quiet.
    func reason(for sample: Sample, now: TimeInterval) -> Reason? {
        guard let last else { return .first }
        switch (last.track, sample.track) {
        case (nil, nil): break
        case let (a?, b?) where a.isSameSong(as: b) && a.spotifyId == b.spotifyId && a.appleId == b.appleId: break
        default: return .track
        }
        if last.playing != sample.playing { return .playPause }
        let expected = last.playing ? last.position + (now - lastSentAt) : last.position
        if sample.track != nil, abs(expected - sample.position) > Self.seekThreshold { return .seek }
        if now - lastSentAt >= Self.heartbeat { return .heartbeat }
        return nil
    }

    /// Whether this state should carry the lyrics: the first for each song.
    func needsLyrics(for track: ListenProtocol.Track?) -> Bool {
        guard let track else { return false }
        return lyricsSentFor.map { !$0.isSameSong(as: track) } ?? true
    }

    /// Records a state as sent and returns its sequence number.
    mutating func sent(_ sample: Sample, now: TimeInterval, withLyrics: Bool) -> Int {
        last = sample
        lastSentAt = now
        if withLyrics { lyricsSentFor = sample.track }
        seq += 1
        return seq
    }

    /// After a reconnect: the next state goes at once, with its lyrics.
    mutating func reset() {
        last = nil
        lyricsSentFor = nil
    }
}

/// Seconds before reconnecting after the n-th failure in a row: 1, 2, 4 …
/// up to a minute.
enum ListenBackoff {
    static func delay(afterFailures count: Int) -> TimeInterval {
        min(60, pow(2, Double(max(0, min(count, 10)) - 1)).rounded(.up))
    }
}
