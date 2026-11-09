import AppKit

// Players of files on this Mac, each through its own public AppleScript
// dictionary. None posts a notification Lunalyr can use, so changes are
// noticed by polling (`PlayerKind.pollsForChanges`). Covers: none of them
// publishes one by address and their files are not sent to Apple's
// catalogue, so the wallpaper keeps its default colours.
//
// Of the other local players looked at: IINA and Doppler have no AppleScript
// dictionary, and Cog's does not report the position.

/// VLC (3.x). Its dictionary describes the application rather than a track:
/// `name of current item` is VLC's title line ("Artist - Title" when the file
/// is tagged, else the file name), `duration of current item` and `current
/// time` are whole seconds, and `play` toggles playback.
struct VLCBackend: PlayerBackend {
    var kind: PlayerKind { .vlc }
    var usesAppleCatalogue: Bool { false }
    var readScript: PlayerScript { Self.read }
    var positionScript: PlayerScript { Self.position }
    var artworkScripts: [PlayerScript] { [] }

    private static let bundleID = PlayerKind.vlc.bundleID

    private static let fields = """
    set isPlaying to playing
    \(PlayerScriptSource.optional("itemName", "name of current item", tolerating: PlayerScriptSource.toleratedUnknownTerms))
    \(PlayerScriptSource.optional("itemPath", "path of current item", tolerating: PlayerScriptSource.toleratedUnknownTerms))
    if (itemName is missing value or itemName is "") and (itemPath is missing value or itemPath is "") then return {"stopped"}
    set stateName to "paused"
    if isPlaying then set stateName to "playing"
    set itemID to itemPath
    if itemID is missing value or itemID is "" then set itemID to itemName
    """

    static let read = PlayerScript(name: "vlc.read", source: PlayerScriptSource.query(bundleID, """
    \(fields)
    \(PlayerScriptSource.optional("itemDuration", "duration of current item", tolerating: PlayerScriptSource.toleratedUnknownTerms))
    return {stateName, itemID, itemName, itemPath, itemDuration}
    """))

    static let position = PlayerScript(name: "vlc.position", source: PlayerScriptSource.query(bundleID, """
    \(fields)
    \(PlayerScriptSource.optional("itemTime", "current time", tolerating: PlayerScriptSource.toleratedUnknownTerms))
    return {stateName, itemID, itemTime}
    """))

    func commandScript(_ command: TransportCommand) -> PlayerScript {
        switch command {
        case .playPause: return Self.playPause
        case .next: return Self.next
        case .previous: return Self.previous
        }
    }

    private static let playPause = PlayerScript(name: "vlc.playpause", source: PlayerScriptSource.command(bundleID, "play"))
    private static let next = PlayerScript(name: "vlc.next", source: PlayerScriptSource.command(bundleID, "next"))
    private static let previous = PlayerScript(name: "vlc.previous", source: PlayerScriptSource.command(bundleID, "previous"))

    /// Whole seconds only.
    func seekScript(to seconds: TimeInterval) -> PlayerScript {
        let whole = Int((seconds.isFinite ? max(0, seconds) : 0).rounded())
        return PlayerScript(name: "vlc.seek", source: PlayerScriptSource.command(Self.bundleID, "set current time to \(whole)"),
                            cacheable: false)
    }

    func parseRead(_ reply: NSAppleEventDescriptor) -> PlayerResponse<PlayerSnapshot> {
        let (items, word) = status(of: reply)
        switch word {
        case "notRunning": return .notRunning
        case "stopped": return .value(PlayerSnapshot(state: .stopped))
        default: break
        }
        guard let word, let state = PlayerSnapshot.State(rawValue: word), items.count >= 5 else { return .failed(-1) }
        let (title, artist) = Self.titleAndArtist(name: items[2].scriptText, path: items[3].scriptText)
        guard !title.isEmpty else { return .value(PlayerSnapshot(state: .stopped)) }
        let duration = items[4].scriptNumber.flatMap { $0.isFinite && $0 > 0 ? $0 : nil } ?? 0
        let track = NowPlayingMonitor.Track(title: title, artist: artist, duration: duration, genre: nil, album: nil,
                                            persistentID: items[1].scriptText.flatMap { $0.isEmpty ? nil : $0 },
                                            isStream: duration <= 0)
        return .value(PlayerSnapshot(state: state, track: track))
    }

    func parsePosition(_ reply: NSAppleEventDescriptor, at time: TimeInterval) -> PlayerResponse<PositionSample> {
        switch parsePositionReply(reply, at: time) {
        case .value(var sample):
            sample.position = sample.position.map { Self.midpoint($0, playing: sample.state == .playing) }
            return .value(sample)
        case let other:
            return other
        }
    }

    func parseArtwork(_ reply: NSAppleEventDescriptor) -> PlayerResponse<ArtworkReply> {
        .value(ArtworkReply(trackID: nil, reading: .none))
    }

    func snapshot(fromNotification userInfo: [AnyHashable: Any]?) -> PlayerSnapshot? { nil }

    /// VLC reports whole seconds, cut down: while it plays, the true
    /// position is anywhere in the second after, half a second on average.
    static func midpoint(_ seconds: TimeInterval, playing: Bool) -> TimeInterval {
        guard playing, seconds > 0, seconds.rounded() == seconds else { return seconds }
        return seconds + 0.5
    }

    /// The song's title and artist from VLC's title line, or from the file
    /// name when the line is empty: "Artist - Title" is split on its first
    /// " - ", a file extension and a leading track number are dropped.
    static func titleAndArtist(name: String?, path: String?) -> (title: String, artist: String) {
        var line = name?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        let fileName = path.flatMap(Self.fileName)
        if line.isEmpty, let fileName { line = fileName }
        line = stripExtension(line)
        if line == fileName.map(stripExtension) || name?.trimmingCharacters(in: .whitespaces).isEmpty != false {
            line = stripTrackNumber(line)
        }
        if let range = line.range(of: " - ") {
            let artist = line[..<range.lowerBound].trimmingCharacters(in: .whitespaces)
            let title = line[range.upperBound...].trimmingCharacters(in: .whitespaces)
            if !artist.isEmpty, !title.isEmpty { return (title, artist) }
        }
        return (line, "")
    }

    /// The last path component of a path or a file: address, decoded.
    static func fileName(_ path: String) -> String? {
        let trimmed = path.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return nil }
        if let url = URL(string: trimmed), url.scheme != nil {
            return url.lastPathComponent.removingPercentEncoding ?? url.lastPathComponent
        }
        return (trimmed as NSString).lastPathComponent
    }

    private static let audioExtensions: Set<String> = [
        "mp3", "m4a", "aac", "flac", "alac", "wav", "aif", "aiff", "ogg", "oga", "opus", "wma", "ape", "wv", "mka", "mp4",
        "m4v", "mkv", "webm", "mov", "dsf", "dff",
    ]

    static func stripExtension(_ name: String) -> String {
        let ext = (name as NSString).pathExtension.lowercased()
        guard audioExtensions.contains(ext) else { return name }
        return (name as NSString).deletingPathExtension
    }

    /// "01 Song", "01. Song", "1-03 Song", "01 - Song" -> "Song".
    static func stripTrackNumber(_ name: String) -> String {
        let pattern = #"^\s*(\d{1,2}-)?\d{1,3}\s*([.\-–]\s*|\s+)"#
        guard let range = name.range(of: pattern, options: .regularExpression), range.upperBound < name.endIndex else { return name }
        return String(name[range.upperBound...])
    }
}

/// Swinsian, whose dictionary follows iTunes': `player state`, `player
/// position` in seconds, and a `current track` with name, artist, album and
/// duration in seconds.
struct SwinsianBackend: PlayerBackend {
    var kind: PlayerKind { .swinsian }
    var usesAppleCatalogue: Bool { false }
    var readScript: PlayerScript { Self.read }
    var positionScript: PlayerScript { Self.position }
    var artworkScripts: [PlayerScript] { [] }

    private static let bundleID = PlayerKind.swinsian.bundleID

    static let read = PlayerScript(name: "swinsian.read", source: PlayerScriptSource.query(bundleID, """
    \(PlayerScriptSource.state)
    try
    set theTrack to current track
    on error number errorNumber
    if errorNumber is -1728 then return {"stopped"}
    error number errorNumber
    end try
    if theTrack is missing value then return {"stopped"}
    set trackName to name of theTrack
    set trackArtist to artist of theTrack
    set trackAlbum to album of theTrack
    \(PlayerScriptSource.optional("trackDuration", "duration of theTrack", tolerating: PlayerScriptSource.toleratedUnknownTerms))
    \(PlayerScriptSource.optional("trackID", "path of theTrack", tolerating: PlayerScriptSource.toleratedUnknownTerms))
    return {stateName, trackID, trackName, trackArtist, trackAlbum, trackDuration}
    """))

    static let position = PlayerScript(name: "swinsian.position", source: PlayerScriptSource.query(bundleID, """
    \(PlayerScriptSource.state)
    set playerPosition to player position
    \(PlayerScriptSource.optional("trackID", "path of current track", tolerating: PlayerScriptSource.toleratedUnknownTerms))
    return {stateName, trackID, playerPosition}
    """))

    func commandScript(_ command: TransportCommand) -> PlayerScript {
        switch command {
        case .playPause: return Self.playPause
        case .next: return Self.next
        case .previous: return Self.previous
        }
    }

    private static let playPause = PlayerScript(name: "swinsian.playpause", source: PlayerScriptSource.command(bundleID, "playpause"))
    private static let next = PlayerScript(name: "swinsian.next", source: PlayerScriptSource.command(bundleID, "next track"))
    private static let previous = PlayerScript(name: "swinsian.previous", source: PlayerScriptSource.command(bundleID, "previous track"))

    func seekScript(to seconds: TimeInterval) -> PlayerScript {
        PlayerScript(name: "swinsian.seek", source: PlayerScriptSource.command(Self.bundleID,
            "set player position to \(PlayerScriptSource.number(seconds))"), cacheable: false)
    }

    func parseRead(_ reply: NSAppleEventDescriptor) -> PlayerResponse<PlayerSnapshot> {
        let (items, word) = status(of: reply)
        switch word {
        case "notRunning": return .notRunning
        case "stopped": return .value(PlayerSnapshot(state: .stopped))
        default: break
        }
        guard let word, let state = PlayerSnapshot.State(rawValue: word), items.count >= 6 else { return .failed(-1) }
        let title = items[2].scriptText?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        guard !title.isEmpty else { return .value(PlayerSnapshot(state: .stopped)) }
        let duration = items[5].scriptNumber.flatMap { $0.isFinite && $0 > 0 ? $0 : nil } ?? 0
        let album = items[4].scriptText ?? ""
        let track = NowPlayingMonitor.Track(title: title, artist: items[3].scriptText ?? "", duration: duration, genre: nil,
                                            album: album.isEmpty ? nil : album,
                                            persistentID: items[1].scriptText.flatMap { $0.isEmpty ? nil : $0 },
                                            isStream: duration <= 0)
        return .value(PlayerSnapshot(state: state, track: track))
    }

    func parsePosition(_ reply: NSAppleEventDescriptor, at time: TimeInterval) -> PlayerResponse<PositionSample> {
        parsePositionReply(reply, at: time)
    }

    func parseArtwork(_ reply: NSAppleEventDescriptor) -> PlayerResponse<ArtworkReply> {
        .value(ArtworkReply(trackID: nil, reading: .none))
    }

    func snapshot(fromNotification userInfo: [AnyHashable: Any]?) -> PlayerSnapshot? { nil }
}

/// VOX. Its dictionary has application properties for the track playing:
/// `track` (the title), `artist`, `album`, `total time` and `current time` in
/// seconds, `unique id`, and `player state` as a number (1 while playing).
struct VoxBackend: PlayerBackend {
    var kind: PlayerKind { .vox }
    var usesAppleCatalogue: Bool { false }
    var readScript: PlayerScript { Self.read }
    var positionScript: PlayerScript { Self.position }
    var artworkScripts: [PlayerScript] { [] }

    private static let bundleID = PlayerKind.vox.bundleID

    static let read = PlayerScript(name: "vox.read", source: PlayerScriptSource.query(bundleID, """
    \(PlayerScriptSource.optional("stateValue", "player state", tolerating: PlayerScriptSource.toleratedUnknownTerms))
    \(PlayerScriptSource.optional("trackName", "track", tolerating: PlayerScriptSource.toleratedUnknownTerms))
    \(PlayerScriptSource.optional("trackArtist", "artist", tolerating: PlayerScriptSource.toleratedUnknownTerms))
    \(PlayerScriptSource.optional("trackAlbum", "album", tolerating: PlayerScriptSource.toleratedUnknownTerms))
    \(PlayerScriptSource.optional("trackDuration", "total time", tolerating: PlayerScriptSource.toleratedUnknownTerms))
    \(PlayerScriptSource.optional("trackID", "unique id", tolerating: PlayerScriptSource.toleratedUnknownTerms))
    return {"vox", stateValue, trackID, trackName, trackArtist, trackAlbum, trackDuration}
    """))

    static let position = PlayerScript(name: "vox.position", source: PlayerScriptSource.query(bundleID, """
    \(PlayerScriptSource.optional("stateValue", "player state", tolerating: PlayerScriptSource.toleratedUnknownTerms))
    \(PlayerScriptSource.optional("trackName", "track", tolerating: PlayerScriptSource.toleratedUnknownTerms))
    \(PlayerScriptSource.optional("trackID", "unique id", tolerating: PlayerScriptSource.toleratedUnknownTerms))
    \(PlayerScriptSource.optional("playerPosition", "current time", tolerating: PlayerScriptSource.toleratedUnknownTerms))
    return {"vox", stateValue, trackID, trackName, playerPosition}
    """))

    func commandScript(_ command: TransportCommand) -> PlayerScript {
        switch command {
        case .playPause: return Self.playPause
        case .next: return Self.next
        case .previous: return Self.previous
        }
    }

    private static let playPause = PlayerScript(name: "vox.playpause", source: PlayerScriptSource.command(bundleID, "playpause"))
    private static let next = PlayerScript(name: "vox.next", source: PlayerScriptSource.command(bundleID, "next"))
    private static let previous = PlayerScript(name: "vox.previous", source: PlayerScriptSource.command(bundleID, "previous"))

    func seekScript(to seconds: TimeInterval) -> PlayerScript {
        PlayerScript(name: "vox.seek", source: PlayerScriptSource.command(Self.bundleID,
            "set current time to \(PlayerScriptSource.number(seconds))"), cacheable: false)
    }

    /// Playing, paused, or stopped when no track is loaded.
    static func state(_ value: NSAppleEventDescriptor, title: String) -> PlayerSnapshot.State {
        guard !title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return .stopped }
        if let number = value.scriptNumber { return number == 1 ? .playing : .paused }
        return value.scriptText?.lowercased() == "playing" ? .playing : .paused
    }

    func parseRead(_ reply: NSAppleEventDescriptor) -> PlayerResponse<PlayerSnapshot> {
        let (items, word) = status(of: reply)
        if word == "notRunning" { return .notRunning }
        guard word == "vox", items.count >= 7 else { return .failed(-1) }
        let title = items[3].scriptText?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        let state = Self.state(items[1], title: title)
        guard state != .stopped else { return .value(PlayerSnapshot(state: .stopped)) }
        let duration = items[6].scriptNumber.flatMap { $0.isFinite && $0 > 0 ? $0 : nil } ?? 0
        let album = items[5].scriptText ?? ""
        let track = NowPlayingMonitor.Track(title: title, artist: items[4].scriptText ?? "", duration: duration, genre: nil,
                                            album: album.isEmpty ? nil : album,
                                            persistentID: items[2].scriptText.flatMap { $0.isEmpty ? nil : $0 },
                                            isStream: duration <= 0)
        return .value(PlayerSnapshot(state: state, track: track))
    }

    func parsePosition(_ reply: NSAppleEventDescriptor, at time: TimeInterval) -> PlayerResponse<PositionSample> {
        let (items, word) = status(of: reply)
        if word == "notRunning" { return .notRunning }
        guard word == "vox", items.count >= 5 else { return .failed(-1) }
        let state = Self.state(items[1], title: items[3].scriptText ?? "")
        guard state != .stopped else { return .value(PositionSample(state: .stopped, trackID: nil, position: nil, time: time)) }
        let position = items[4].scriptNumber.flatMap { $0.isFinite && $0 >= 0 ? $0 : nil }
        return .value(PositionSample(state: state, trackID: items[2].scriptText.flatMap { $0.isEmpty ? nil : $0 },
                                     position: position, time: time))
    }

    func parseArtwork(_ reply: NSAppleEventDescriptor) -> PlayerResponse<ArtworkReply> {
        .value(ArtworkReply(trackID: nil, reading: .none))
    }

    func snapshot(fromNotification userInfo: [AnyHashable: Any]?) -> PlayerSnapshot? { nil }
}
