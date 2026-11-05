import AppKit

/// Apple Music through its public AppleScript dictionary and its
/// `com.apple.Music.playerInfo` notification.
struct MusicBackend: PlayerBackend {
    var kind: PlayerKind { .appleMusic }
    var usesAppleCatalogue: Bool { true }
    var readScript: PlayerScript { Self.read }
    var positionScript: PlayerScript { Self.position }
    var artworkScripts: [PlayerScript] { Self.artwork }

    private static let bundleID = "com.apple.Music"

    /// Every field comes from one resolved track reference, so a skip
    /// halfway through cannot pair one song's title with the next one's
    /// artist. Streams report no duration; only then is the stream title read.
    static let read = PlayerScript(name: "music.read", source: PlayerScriptSource.query(bundleID, """
    \(PlayerScriptSource.state)
    try
    set theTrack to current track
    on error number errorNumber
    if errorNumber is -1728 then return {"stopped"}
    error number errorNumber
    end try
    set trackName to name of theTrack
    set trackArtist to artist of theTrack
    set trackAlbum to album of theTrack
    set trackGenre to genre of theTrack
    \(PlayerScriptSource.optional("trackDuration", "duration of theTrack"))
    \(PlayerScriptSource.optional("trackID", "persistent ID of theTrack"))
    set streamTitle to missing value
    if trackDuration is missing value or trackDuration is 0 then
    \(PlayerScriptSource.optional("streamTitle", "current stream title"))
    end if
    return {stateName, trackID, trackName, trackArtist, trackAlbum, trackGenre, trackDuration, streamTitle}
    """))

    /// Three events with the position in the middle, so the midpoint of the
    /// round trip is when Music most likely sampled it.
    static let position = PlayerScript(name: "music.position", source: PlayerScriptSource.query(bundleID, """
    \(PlayerScriptSource.state)
    set playerPosition to player position
    \(PlayerScriptSource.optional("trackID", "persistent ID of current track"))
    return {stateName, trackID, playerPosition}
    """))

    /// Music's own artwork is the exact cover for the exact edition playing.
    /// Availability varies -- a track streamed moments ago may report none
    /// until it caches -- so the count is checked before asking for bytes.
    /// `data` is tried first, then the original-format `raw data`.
    static let artwork = ["data", "raw data"].map { property in
        PlayerScript(name: "music.artwork.\(property)", source: PlayerScriptSource.query(bundleID, timeout: 10, """
        try
        set theTrack to current track
        on error number errorNumber
        if errorNumber is -1728 then return {"none", missing value}
        error number errorNumber
        end try
        \(PlayerScriptSource.optional("trackID", "persistent ID of theTrack"))
        if (count of artworks of theTrack) is 0 then return {"none", trackID}
        \(PlayerScriptSource.optional("artworkData", "\(property) of artwork 1 of theTrack"))
        if artworkData is missing value then return {"none", trackID}
        return {"artwork", trackID, artworkData}
        """))
    }

    func commandScript(_ command: TransportCommand) -> PlayerScript {
        switch command {
        case .playPause: return Self.playPause
        case .next: return Self.next
        case .previous: return Self.previous
        }
    }

    private static let playPause = PlayerScript(name: "music.playpause", source: PlayerScriptSource.command(bundleID, "playpause"))
    private static let next = PlayerScript(name: "music.next", source: PlayerScriptSource.command(bundleID, "next track"))
    private static let previous = PlayerScript(name: "music.previous", source: PlayerScriptSource.command(bundleID, "previous track"))

    func seekScript(to seconds: TimeInterval) -> PlayerScript {
        PlayerScript(name: "music.seek", source: PlayerScriptSource.command(Self.bundleID,
            "set player position to \(PlayerScriptSource.number(seconds))"), cacheable: false)
    }

    func parseRead(_ reply: NSAppleEventDescriptor) -> PlayerResponse<PlayerSnapshot> {
        let (items, word) = status(of: reply)
        switch word {
        case "notRunning": return .notRunning
        case "stopped": return .value(PlayerSnapshot(state: .stopped))
        default: break
        }
        guard let word, let state = PlayerSnapshot.State(rawValue: word), items.count >= 8 else { return .failed(-1) }
        let track = Self.track(
            name: items[2].scriptText ?? "", artist: items[3].scriptText ?? "",
            album: items[4].scriptText ?? "", genre: items[5].scriptText ?? "",
            duration: items[6].scriptNumber ?? 0, streamTitle: items[7].scriptText,
            persistentID: Self.canonicalID(items[1].scriptText)
        )
        return .value(PlayerSnapshot(state: state, track: track))
    }

    func parsePosition(_ reply: NSAppleEventDescriptor, at time: TimeInterval) -> PlayerResponse<PositionSample> {
        switch parsePositionReply(reply, at: time) {
        case .value(var sample):
            sample.trackID = Self.canonicalID(sample.trackID)
            return .value(sample)
        case let other:
            return other
        }
    }

    func parseArtwork(_ reply: NSAppleEventDescriptor) -> PlayerResponse<ArtworkReply> {
        let (items, word) = status(of: reply)
        let id = items.count > 1 ? Self.canonicalID(items[1].scriptText) : nil
        switch word {
        case "notRunning": return .notRunning
        case "none": return .value(ArtworkReply(trackID: id, reading: .none))
        case "artwork":
            guard items.count >= 3, !items[2].isMissingValue, !items[2].data.isEmpty else {
                return .value(ArtworkReply(trackID: id, reading: .none))
            }
            return .value(ArtworkReply(trackID: id, reading: .image(items[2].data)))
        default: return .failed(-1)
        }
    }

    /// Music's notification carries the same metadata as the script, so a
    /// track change or a pause needs no Apple event beyond the position.
    /// It carries the track's persistent ID too, so a reply about the
    /// previous track (its cover, say) can be told apart from the moment
    /// the change is announced. Anything that looks like a stream (no
    /// duration) is confirmed with the script instead: misreading a song as
    /// a stream would hide its lyrics.
    func snapshot(fromNotification userInfo: [AnyHashable: Any]?) -> PlayerSnapshot? {
        guard let info = userInfo, let stateText = info["Player State"] as? String else { return nil }
        let state: PlayerSnapshot.State
        switch stateText.lowercased() {
        case "playing": state = .playing
        case "paused": state = .paused
        case "stopped": return PlayerSnapshot(state: .stopped)
        default: return nil
        }
        guard let name = info["Name"] as? String,
              let milliseconds = (info["Total Time"] as? NSNumber)?.doubleValue, milliseconds > 0,
              let track = Self.track(
                name: name, artist: info["Artist"] as? String ?? "", album: info["Album"] as? String ?? "",
                genre: info["Genre"] as? String ?? "", duration: milliseconds / 1000,
                streamTitle: nil, persistentID: Self.persistentID(fromNotification: info["PersistentID"])
              )
        else { return nil }
        return PlayerSnapshot(state: state, track: track)
    }

    /// The notification states the persistent ID as a signed 64-bit number;
    /// AppleScript states the same 64 bits as hexadecimal text.
    static func persistentID(fromNotification value: Any?) -> String? {
        let bits: UInt64
        switch value {
        case let number as NSNumber:
            bits = UInt64(bitPattern: number.int64Value)
        case let text as String:
            let digits = text.trimmingCharacters(in: .whitespaces)
            if let signed = Int64(digits) {
                bits = UInt64(bitPattern: signed)
            } else if let unsigned = UInt64(digits) {
                bits = unsigned
            } else {
                return nil
            }
        default:
            return nil
        }
        return bits == 0 ? nil : hex(bits)
    }

    /// One spelling for an identifier from either source: sixteen
    /// upper-case hex digits, the way AppleScript writes it.
    static func canonicalID(_ text: String?) -> String? {
        guard let text = text?.trimmingCharacters(in: .whitespacesAndNewlines), !text.isEmpty else { return nil }
        guard let bits = UInt64(text, radix: 16) else { return text }
        return hex(bits)
    }

    private static func hex(_ bits: UInt64) -> String {
        String(format: "%016llX", bits)
    }

    /// Live radio has no duration. It is published with duration 0 and
    /// `isStream`, titled by what the station is playing when it says so,
    /// so lyric and catalogue lookups never search for a station name.
    static func track(name: String, artist: String, album: String, genre: String, duration: Double,
                      streamTitle: String?, persistentID: String?) -> NowPlayingMonitor.Track? {
        let isStream = !(duration.isFinite && duration > 0)
        let nowPlaying = streamTitle?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        let title = isStream && !nowPlaying.isEmpty ? nowPlaying : name
        guard !title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return nil }
        return NowPlayingMonitor.Track(
            title: title,
            artist: isStream && !nowPlaying.isEmpty ? name : artist,
            duration: isStream ? 0 : duration,
            genre: genre,
            album: isStream ? nil : album,
            persistentID: persistentID.flatMap { $0.isEmpty ? nil : $0 },
            isStream: isStream
        )
    }
}
