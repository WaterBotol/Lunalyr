import AppKit

/// Spotify through its public AppleScript dictionary and its
/// `com.spotify.client.PlaybackStateChanged` notification.
///
/// Spotify reports durations in milliseconds and positions in seconds,
/// identifies tracks by URI ("spotify:track:…") and publishes covers by
/// address rather than as image data.
struct SpotifyBackend: PlayerBackend {
    var kind: PlayerKind { .spotify }
    /// Covers come from Spotify itself. A Spotify listener's songs are not
    /// sent to Apple's catalogue: a local file without a cover keeps the
    /// default colours, and there are no Apple Music animated covers.
    var usesAppleCatalogue: Bool { false }
    var readScript: PlayerScript { Self.read }
    var positionScript: PlayerScript { Self.position }
    var artworkScripts: [PlayerScript] { [Self.artwork] }

    private static let bundleID = "com.spotify.client"

    static let read = PlayerScript(name: "spotify.read", source: PlayerScriptSource.query(bundleID, """
    \(PlayerScriptSource.state)
    try
    set theTrack to current track
    on error number errorNumber
    if errorNumber is -1728 then return {"stopped"}
    error number errorNumber
    end try
    set trackID to id of theTrack
    set trackName to name of theTrack
    set trackArtist to artist of theTrack
    set trackAlbum to album of theTrack
    \(PlayerScriptSource.optional("trackDuration", "duration of theTrack"))
    \(PlayerScriptSource.optional("artworkURL", "artwork url of theTrack"))
    return {stateName, trackID, trackName, trackArtist, trackAlbum, trackDuration, artworkURL}
    """))

    /// The position is the middle of three events, as for Music.
    static let position = PlayerScript(name: "spotify.position", source: PlayerScriptSource.query(bundleID, """
    \(PlayerScriptSource.state)
    set playerPosition to player position
    \(PlayerScriptSource.optional("trackID", "id of current track"))
    return {stateName, trackID, playerPosition}
    """))

    static let artwork = PlayerScript(name: "spotify.artwork", source: PlayerScriptSource.query(bundleID, """
    try
    set theTrack to current track
    on error number errorNumber
    if errorNumber is -1728 then return {"none", missing value}
    error number errorNumber
    end try
    \(PlayerScriptSource.optional("trackID", "id of theTrack"))
    \(PlayerScriptSource.optional("artworkURL", "artwork url of theTrack"))
    if artworkURL is missing value or artworkURL is "" then return {"none", trackID}
    return {"url", trackID, artworkURL}
    """))

    func commandScript(_ command: TransportCommand) -> PlayerScript {
        switch command {
        case .playPause: return Self.playPause
        case .next: return Self.next
        case .previous: return Self.previous
        }
    }

    private static let playPause = PlayerScript(name: "spotify.playpause", source: PlayerScriptSource.command(bundleID, "playpause"))
    private static let next = PlayerScript(name: "spotify.next", source: PlayerScriptSource.command(bundleID, "next track"))
    private static let previous = PlayerScript(name: "spotify.previous", source: PlayerScriptSource.command(bundleID, "previous track"))

    func seekScript(to seconds: TimeInterval) -> PlayerScript {
        PlayerScript(name: "spotify.seek", source: PlayerScriptSource.command(Self.bundleID,
            "set player position to \(PlayerScriptSource.number(seconds))"), cacheable: false)
    }

    func parseRead(_ reply: NSAppleEventDescriptor) -> PlayerResponse<PlayerSnapshot> {
        let (items, word) = status(of: reply)
        switch word {
        case "notRunning": return .notRunning
        case "stopped": return .value(PlayerSnapshot(state: .stopped))
        default: break
        }
        guard let word, let state = PlayerSnapshot.State(rawValue: word), items.count >= 7 else { return .failed(-1) }
        let track = Self.track(id: items[1].scriptText, name: items[2].scriptText ?? "",
                               artist: items[3].scriptText ?? "", album: items[4].scriptText ?? "",
                               milliseconds: items[5].scriptNumber ?? 0)
        return .value(PlayerSnapshot(state: state, track: track, artworkURL: Self.coverURL(items[6].scriptText)))
    }

    func parsePosition(_ reply: NSAppleEventDescriptor, at time: TimeInterval) -> PlayerResponse<PositionSample> {
        parsePositionReply(reply, at: time)
    }

    func parseArtwork(_ reply: NSAppleEventDescriptor) -> PlayerResponse<ArtworkReply> {
        let (items, word) = status(of: reply)
        let id = items.count > 1 ? items[1].scriptText.flatMap { $0.isEmpty ? nil : $0 } : nil
        switch word {
        case "notRunning": return .notRunning
        case "none": return .value(ArtworkReply(trackID: id, reading: .none))
        case "url":
            guard items.count >= 3, let url = Self.coverURL(items[2].scriptText) else {
                return .value(ArtworkReply(trackID: id, reading: .none))
            }
            return .value(ArtworkReply(trackID: id, reading: .url(url)))
        default: return .failed(-1)
        }
    }

    /// Spotify's notification carries the track, its duration and the
    /// playback position, so a change needs no Apple event at all.
    func snapshot(fromNotification userInfo: [AnyHashable: Any]?) -> PlayerSnapshot? {
        guard let info = userInfo, let stateText = info["Player State"] as? String else { return nil }
        let state: PlayerSnapshot.State
        switch stateText.lowercased() {
        case "playing": state = .playing
        case "paused": state = .paused
        case "stopped": return PlayerSnapshot(state: .stopped)
        default: return nil
        }
        guard let id = info["Track ID"] as? String, !id.isEmpty, let name = info["Name"] as? String,
              let milliseconds = (info["Duration"] as? NSNumber)?.doubleValue,
              let track = Self.track(id: id, name: name, artist: info["Artist"] as? String ?? "",
                                     album: info["Album"] as? String ?? "", milliseconds: milliseconds),
              track.duration > 0 || Self.isAdvert(id)
        else { return nil }
        let position = ((info["Playback Position"] as? NSNumber)?.doubleValue).flatMap { $0.isFinite && $0 >= 0 ? $0 : nil }
        return PlayerSnapshot(state: state, track: track, position: track.isStream ? nil : position)
    }

    /// Adverts are published like streams -- no duration, no scrubber, no
    /// lyric or catalogue lookups -- rather than as songs called "Advertisement".
    static func track(id: String?, name: String, artist: String, album: String,
                      milliseconds: Double) -> NowPlayingMonitor.Track? {
        guard !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return nil }
        let identifier = id.flatMap { $0.isEmpty ? nil : $0 }
        let seconds = milliseconds.isFinite ? milliseconds / 1000 : 0
        let advert = isAdvert(identifier ?? "")
        let isStream = advert || !(seconds > 0)
        return NowPlayingMonitor.Track(title: name, artist: artist, duration: isStream ? 0 : seconds,
                                       genre: nil, album: isStream ? nil : album,
                                       persistentID: identifier, isStream: isStream, isAdvert: advert)
    }

    static func isAdvert(_ id: String) -> Bool {
        id.hasPrefix("spotify:ad:")
    }

    /// Covers come from Spotify's CDN; older versions report them over
    /// plain HTTP, which is upgraded. Anything else must already be HTTPS.
    static func coverURL(_ text: String?) -> URL? {
        guard let text = text?.trimmingCharacters(in: .whitespacesAndNewlines), !text.isEmpty,
              var components = URLComponents(string: text), let host = components.host?.lowercased() else { return nil }
        if components.scheme?.lowercased() == "http", host == "scdn.co" || host.hasSuffix(".scdn.co") {
            components.scheme = "https"
        }
        guard components.scheme?.lowercased() == "https" else { return nil }
        return components.url
    }
}
