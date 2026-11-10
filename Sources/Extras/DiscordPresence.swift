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
    /// While sharing listen-along: a Join party instead of the buttons.
    /// Discord refuses an activity with both, so it is opt-in.
    var offersJoin = false
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
/// - Discord allows five updates per twenty seconds: lines are spaced out
///   (`DiscordUpdateBudget`), so a fast song skips some lines rather than
///   being refused, and a new song, a pause or a clear always has a slot.
/// - Two buttons, both for others: "Play this song" (a song.link page that
///   lists the song on Spotify, Apple Music, YouTube Music and more) and
///   "Get Lunalyr". Discord never shows a person their own buttons.
@MainActor
final class DiscordPresence {
    nonisolated static let applicationID = "1568919234367717506"
    static let getLunalyrURL = "https://github.com/WaterBotol/Lunalyr/releases/latest"
    static let iconURL = "https://raw.githubusercontent.com/WaterBotol/Lunalyr/main/Sources/Assets.xcassets/AppIcon.appiconset/icon_256.png"
    /// How often the line is checked while a song plays.
    static let tickInterval: TimeInterval = 0.5
    /// How often it is checked while paused or with nothing playing: play,
    /// pause and song changes arrive on their own, so this is only a backstop.
    static let idleTickInterval: TimeInterval = 5

    /// Fast while a song plays (the line moves on) or an update is held back
    /// for Discord's rate limit; slow otherwise.
    nonisolated static func tickInterval(playing: Bool, heldBack: Bool) -> TimeInterval {
        playing || heldBack ? tickInterval : idleTickInterval
    }
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

    /// Discord wants 2...128 characters in each text field and refuses the
    /// whole activity otherwise. It counts UTF-16 units, so an accented, Thai
    /// or emoji line is measured that way, and cut between whole characters.
    static func field(_ text: String) -> String {
        var clipped = text.trimmingCharacters(in: .whitespacesAndNewlines)
        if clipped.utf16.count > 128 {
            while clipped.utf16.count > 127 { clipped.removeLast() }
            clipped += "…"
        }
        return clipped.utf16.count < 2 ? clipped + " ♪" : clipped
    }

    /// Discord refuses the whole activity over one bad button: the label must
    /// be 1...32 characters and the link http(s), well formed and at most 512.
    nonisolated static func isValidButton(label: String, url: String) -> Bool {
        guard (1...32).contains(label.utf16.count), url.utf16.count <= 512,
              let parsed = URL(string: url), let scheme = parsed.scheme?.lowercased(),
              scheme == "https" || scheme == "http", let host = parsed.host, !host.isEmpty else { return false }
        return true
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
        /// The listen-along room being shared, if any.
        var party: ListenParty?
    }

    /// A shared listen-along room, as Discord shows it.
    struct ListenParty: Equatable {
        var room: String
        var listeners: Int
        /// The relay's web listen-along page for the room.
        var webURL: String?
        /// The user's Discord display name, carried in the join secret so a
        /// friend is asked "Listen along with <name>?".
        var hostName: String? = nil
    }

    /// Party size: the host plus up to 50 listeners (the relay's limit).
    static let partyCapacity = 51
    static let listenAlongLabel = "Listen along"

    static let playSongLabel = "Play this song"

    /// song.link for a song: from Spotify's own ID when Spotify is playing,
    /// else from Apple's catalogue ID. Nil for local files and unknown songs.
    nonisolated static func songLink(spotifyURI: String?, appleTrackID: Int?) -> String? {
        if let uri = spotifyURI, uri.hasPrefix("spotify:track:") {
            let id = uri.dropFirst("spotify:track:".count)
            // Spotify IDs are base62; anything else would make a link Discord refuses.
            if !id.isEmpty, id.allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber) }) { return "https://song.link/s/\(id)" }
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
        if let party = playing.party, options.offersJoin {
            // Discord refuses secrets together with buttons ("secrets cannot
            // currently be sent with buttons"): the Join replaces them.
            activity["party"] = ["id": party.room,
                                 "size": [min(partyCapacity, max(1, party.listeners + 1)), partyCapacity]] as [String: Any]
            activity["secrets"] = ["join": ListenProtocol.joinSecret(room: party.room, hostName: party.hostName)]
        } else {
            var buttons: [[String: String]] = []
            if let web = playing.party?.webURL {
                buttons.append(["label": listenAlongLabel, "url": web])
            } else if options.showsSongButton, let song = playing.songURL {
                buttons.append(["label": playSongLabel, "url": song])
            }
            if options.showsButton {
                buttons.append(["label": "Get Lunalyr", "url": getLunalyrURL])
            }
            buttons.removeAll { !isValidButton(label: $0["label"] ?? "", url: $0["url"] ?? "") }
            if !buttons.isEmpty { activity["buttons"] = buttons }
        }
        if options.showsProgress && !playing.paused {
            var timestamps: [String: Any] = ["start": playing.start]
            if let end = playing.end { timestamps["end"] = end }
            activity["timestamps"] = timestamps
        }
        return activity
    }

    // MARK: - Joining (pure, tested)

    /// Discord events Lunalyr acts on.
    enum Event: Equatable {
        /// The handshake's reply, with the user's display name.
        case ready(userName: String?)
        /// The user clicked Join on a friend's profile: the room to follow.
        case join(secret: String)
        /// Someone asked to join the user's party.
        case joinRequest(userID: String, name: String?)
        case error(code: Int, message: String)
    }

    nonisolated static let joinEvents = ["ACTIVITY_JOIN", "ACTIVITY_JOIN_REQUEST"]

    nonisolated static func event(fromFrame body: Data) -> Event? {
        guard let object = try? JSONSerialization.jsonObject(with: body) as? [String: Any] else { return nil }
        let data = object["data"] as? [String: Any] ?? [:]
        func name(_ user: [String: Any]?) -> String? {
            guard let user else { return nil }
            for key in ["global_name", "username"] {
                if let text = user[key] as? String, !text.trimmingCharacters(in: .whitespaces).isEmpty { return text }
            }
            return nil
        }
        switch object["evt"] as? String {
        case "READY":
            return .ready(userName: name(data["user"] as? [String: Any]))
        case "ACTIVITY_JOIN":
            guard let secret = data["secret"] as? String, !secret.isEmpty else { return nil }
            return .join(secret: secret)
        case "ACTIVITY_JOIN_REQUEST":
            let user = data["user"] as? [String: Any]
            guard let id = user?["id"] as? String, !id.isEmpty else { return nil }
            return .joinRequest(userID: id, name: name(user))
        case "ERROR":
            return .error(code: (data["code"] as? NSNumber)?.intValue ?? 0, message: data["message"] as? String ?? "")
        default:
            return nil
        }
    }

    nonisolated static func subscribePayload(_ event: String, nonce: String) -> [String: Any] {
        ["cmd": "SUBSCRIBE", "evt": event, "args": [String: Any](), "nonce": nonce]
    }

    /// Lets a friend who asked to join in: the room is open to anyone with
    /// its code anyway.
    nonisolated static func acceptJoinPayload(userID: String, nonce: String) -> [String: Any] {
        ["cmd": "SEND_ACTIVITY_JOIN_INVITE", "args": ["user_id": userID], "nonce": nonce]
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
    /// A loose match is close enough for the cover; the ID becomes a public
    /// "Play this song" link, so it is taken only from a result whose title
    /// and primary artist are the song's own, once normalised.
    nonisolated static func searchMatch(fromSearch data: Data, title: String, artist: String) -> SearchMatch? {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let results = object["results"] as? [[String: Any]] else { return nil }
        let wantTitle = title.lowercased()
        let wantArtist = artist.lowercased()
        let exactTitle = CoverLookup.normal(title), exactArtist = primaryArtist(artist)
        var cover: String?
        var trackID: Int?
        for result in results {
            let rawName = result["trackName"] as? String ?? "", rawBy = result["artistName"] as? String ?? ""
            let name = rawName.lowercased(), by = rawBy.lowercased()
            guard !name.isEmpty else { continue }
            let exact = !exactTitle.isEmpty && !exactArtist.isEmpty
                && CoverLookup.normal(rawName) == exactTitle && primaryArtist(rawBy) == exactArtist
            let loose = (name.contains(wantTitle) || wantTitle.contains(name))
                && (wantArtist.isEmpty || by.contains(wantArtist) || wantArtist.contains(by))
            guard exact || loose else { continue }
            if cover == nil {
                cover = (result["artworkUrl100"] as? String)?.replacingOccurrences(of: "100x100bb", with: "512x512bb")
            }
            if trackID == nil, exact { trackID = (result["trackId"] as? NSNumber)?.intValue }
            if cover != nil, trackID != nil { break }
        }
        return cover == nil && trackID == nil ? nil : SearchMatch(cover: cover, trackID: trackID)
    }

    /// The first-named artist, normalised: "Calvin Harris feat. Rihanna",
    /// "Calvin Harris & Rihanna" and "calvin harris" all give "calvinharris".
    nonisolated static func primaryArtist(_ artist: String) -> String {
        var name = " \(artist.lowercased()) "
        for separator in [",", ";", "/", " & ", " + ", " x ", " × ", " and ", " with ", " feat.", " feat ", " ft.", " ft ", " featuring "] {
            if let range = name.range(of: separator) { name = String(name[..<range.lowerBound]) }
        }
        return CoverLookup.normal(name)
    }

    // MARK: - State

    private weak var extras: LunalyrExtras?
    private let transport = DiscordTransport()
    private var changes: Set<AnyCancellable> = []
    private var timer: Timer?
    private var timerInterval: TimeInterval = 0
    private var lastSent: Playing??
    private var budget = DiscordUpdateBudget()
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
        var sources: [AnyPublisher<Void, Never>] = [
            monitor.$current.map { _ in () }.eraseToAnyPublisher(),
            monitor.$isPlaying.map { _ in () }.eraseToAnyPublisher(),
            LyricsViewModel.shared.$lines.map { _ in () }.eraseToAnyPublisher(),
        ]
        if let extras { sources.append(extras.objectWillChange.eraseToAnyPublisher()) }
        sources.append(ListenAlong.shared.objectWillChange.eraseToAnyPublisher())
        sources.append(CatalogueMatches.shared.$revision.map { _ in () }.eraseToAnyPublisher())
        transport.onEvent = { [weak self] event in
            Task { @MainActor in self?.handle(event) }
        }
        transport.startListening(for: Self.joinEvents)
        Publishers.MergeMany(sources)
            .receive(on: DispatchQueue.main)
            .sink { [weak self] in self?.update() }
            .store(in: &changes)
        update()
    }

    func stop() {
        // The name goes with the presence: sharing doesn't send it after.
        ListenAlong.shared.discordUserName = nil
        guard running else { return }
        running = false
        timer?.invalidate()
        timer = nil
        timerInterval = 0
        changes.removeAll()
        transport.stopListening()
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
        let needsSearch = Self.needsAppleSearch(options, spotifyURI: spotifyURI)
        let found = needsSearch ? CatalogueMatches.shared.match(title: track.title, artist: track.artist, album: track.album) : nil
        let songURL = options.showsSongButton ? Self.songLink(spotifyURI: spotifyURI, appleTrackID: found?.trackID) : nil
        return Playing(title: track.title, artist: track.artist, album: track.album, line: text,
                       start: start, end: end, paused: !monitor.isPlaying,
                       coverURL: options.showsCover ? found?.cover : nil, songURL: songURL, options: options,
                       party: ListenAlong.shared.discordParty)
    }

    /// Whether Apple's search is asked at all: for the cover, or for the
    /// link of a song Spotify has not given one for. With the cover off the
    /// song button is off too unless the user turned it on themselves
    /// (`LunalyrExtras.discordShowsSongButton`).
    nonisolated static func needsAppleSearch(_ options: DiscordOptions, spotifyURI: String?) -> Bool {
        options.showsCover || (options.showsSongButton && spotifyURI == nil)
    }

    private func handle(_ event: Event) {
        guard running else { return }
        switch event {
        case .ready(let name):
            ListenAlong.shared.discordUserName = name
        case .join(let secret):
            let listen = ListenAlong.shared
            if !listen.isAvailable {
                extras?.post("To listen along, add a listen-along relay in Settings › Share", symbol: "person.2",
                             origin: .elsewhere)
            } else if !listen.requestJoin(discordSecret: secret) {
                extras?.post("That Discord invite isn\u{2019}t a listen-along room", symbol: "person.2.slash",
                             origin: .elsewhere)
            }
        case .joinRequest(let userID, _):
            guard ListenAlong.shared.room != nil else { return }
            transport.send(Self.acceptJoinPayload(userID: userID, nonce: UUID().uuidString))
        case .error(let code, let message):
            NSLog("Lunalyr: Discord refused a request (%d): %@", code, message)
        }
    }

    private func update() {
        guard running else { return }
        let heldBack = !sendIfChanged()
        scheduleTimer(Self.tickInterval(playing: NowPlayingMonitor.shared.isPlaying, heldBack: heldBack))
    }

    /// (Re)starts the tick at `interval` when it differs from the running one.
    private func scheduleTimer(_ interval: TimeInterval) {
        guard running, interval != timerInterval else { return }
        timer?.invalidate()
        timerInterval = interval
        let timer = Timer(timeInterval: interval, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.update() }
        }
        timer.tolerance = interval * 0.4
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    /// Sends the activity if it changed; false when a change is held back.
    private func sendIfChanged() -> Bool {
        let playing = currentPlaying()
        if let lastSent, lastSent == playing { return true }
        // Within Discord's limit; a new song, a pause or a clear goes first.
        // Held back, the latest state goes on a later tick.
        let now = Date()
        guard budget.allows(urgent: Self.isUrgent(from: lastSent, to: playing), at: now) else { return false }
        budget.record(now)
        lastSent = .some(playing)
        if playing == nil { anchor = nil }
        transport.send(Self.setActivityPayload(playing.map(Self.activity(for:)),
                                               pid: ProcessInfo.processInfo.processIdentifier,
                                               nonce: UUID().uuidString))
        return true
    }
}

extension DiscordPresence {
    /// A change that should not wait behind lyric lines: the first update, a
    /// new song, play or pause, or the presence being cleared.
    static func isUrgent(from last: Playing??, to next: Playing?) -> Bool {
        guard let last else { return true }
        guard let last, let next else { return true }
        return last.title != next.title || last.artist != next.artist || last.paused != next.paused
    }
}

/// Discord takes five activity updates in twenty seconds and silently drops
/// the rest. Lines are spaced at least `lineGap` apart and use four of the
/// five; the fifth is kept for an urgent change (a new song, a pause, a clear).
struct DiscordUpdateBudget {
    static let window: TimeInterval = 20
    static let limit = 5
    static let lineGap: TimeInterval = 5
    private(set) var sent: [Date] = []

    func allows(urgent: Bool, at now: Date) -> Bool {
        let recent = sent.filter { now.timeIntervalSince($0) < Self.window }
        if urgent { return recent.count < Self.limit }
        if let last = recent.last, now.timeIntervalSince(last) < Self.lineGap { return false }
        return recent.count < Self.limit - 1
    }

    mutating func record(_ now: Date) {
        sent.removeAll { now.timeIntervalSince($0) >= Self.window }
        sent.append(now)
    }
}

/// Apple catalogue matches (cover and track ID) by song, shared by
/// Discord and listen-along so a song is searched for once.
@MainActor
final class CatalogueMatches: ObservableObject {
    static let shared = CatalogueMatches()

    /// Search results by song, the most recently used kept.
    private var matches = RecentCache<DiscordPresence.SearchMatch>(capacity: CatalogueMatches.capacity)
    /// Songs whose lookup failed or found nothing, and when: asked again
    /// after `lookupRetry`, so a network blip does not last the session.
    private var misses = RecentCache<Date>(capacity: CatalogueMatches.capacity)
    static let capacity = 256
    private var lookingUp: Set<String> = []
    static let lookupRetry: TimeInterval = 600
    /// Bumped when a lookup finishes, so whoever asked can look again.
    @Published private(set) var revision = 0

    static func key(title: String, artist: String, album: String?) -> String {
        "\(title)|\(artist)|\(album ?? "")"
    }

    /// The cached match, starting a lookup the first time a song is seen
    /// unless `lookUpIfMissing` is false.
    func match(title: String, artist: String, album: String?, lookUpIfMissing: Bool = true) -> DiscordPresence.SearchMatch? {
        let key = Self.key(title: title, artist: artist, album: album)
        if let known = matches.value(for: key) { return known }
        if let missed = misses.value(for: key), Date().timeIntervalSince(missed) < Self.lookupRetry { return nil }
        guard lookUpIfMissing, !lookingUp.contains(key), !AppRuntime.isUnitTesting else { return nil }
        lookingUp.insert(key)
        Task { [weak self] in
            let found = await Self.lookUp(title: title, artist: artist)
            guard let self else { return }
            if let found { matches.set(found, for: key); misses.remove(key) } else { misses.set(Date(), for: key) }
            lookingUp.remove(key)
            revision += 1
        }
        return nil
    }

    nonisolated private static func lookUp(title: String, artist: String) async -> DiscordPresence.SearchMatch? {
        var components = URLComponents(string: "https://itunes.apple.com/search")
        components?.queryItems = [URLQueryItem(name: "term", value: "\(artist) \(title)"),
                                  URLQueryItem(name: "media", value: "music"),
                                  URLQueryItem(name: "entity", value: "song"),
                                  URLQueryItem(name: "limit", value: "8")]
        guard let url = components?.url else { return nil }
        var request = URLRequest(url: url)
        request.timeoutInterval = 6
        guard let (data, _) = try? await URLSession.shared.data(for: request) else { return nil }
        return DiscordPresence.searchMatch(fromSearch: data, title: title, artist: artist)
    }
}

/// A small map that keeps the `capacity` most recently used entries.
struct RecentCache<Value> {
    let capacity: Int
    private var values: [String: Value] = [:]
    /// Keys from least to most recently used.
    private var order: [String] = []

    init(capacity: Int) { self.capacity = max(1, capacity) }

    var count: Int { values.count }

    mutating func value(for key: String) -> Value? {
        guard let value = values[key] else { return nil }
        touch(key)
        return value
    }

    mutating func set(_ value: Value, for key: String) {
        values[key] = value
        touch(key)
        while order.count > capacity { values[order.removeFirst()] = nil }
    }

    mutating func remove(_ key: String) {
        values[key] = nil
        order.removeAll { $0 == key }
    }

    private mutating func touch(_ key: String) {
        order.removeAll { $0 == key }
        order.append(key)
    }
}

/// The socket to Discord, on its own serial queue. Connects on demand,
/// remembers the last activity and re-sends it after Discord starts or restarts.
final class DiscordTransport: @unchecked Sendable {
    private let queue = DispatchQueue(label: "lunalyr.discord-ipc", qos: .utility)
    private var fd: Int32 = -1
    private var lastFailure = Date.distantPast
    /// Events subscribed to after every handshake, so a Discord restart
    /// keeps them; while there are any, the socket is read continuously.
    private var subscriptions: [String] = []
    private var listening = false
    /// Bumped on every start, so a stop and start inside one read interval
    /// leaves one loop, not two.
    private var generation = 0
    /// Read loops running; tests read it.
    private var loops = 0
    /// Off in tests: the loop never touches Discord's socket.
    private let connects: Bool
    /// The last activity shown, framed, so a Discord that starts or restarts
    /// gets it again; nil once it was cleared.
    private var lastActivity: Data?
    private var retryScheduled = false
    /// How long after a failed connection Discord is looked for again.
    private let retryInterval: TimeInterval
    /// Tests hand over one end of a socket pair instead of Discord's socket.
    private let dial: (@Sendable () -> Int32?)?

    init(connects: Bool = !AppRuntime.isUnitTesting, retryInterval: TimeInterval = DiscordPresence.reconnectInterval,
         dial: (@Sendable () -> Int32?)? = nil) {
        self.connects = connects
        self.retryInterval = retryInterval
        self.dial = dial
    }

    /// How many read loops are running (one at most).
    var runningLoops: Int { queue.sync { loops } }
    /// Called on the transport's queue for each event Discord sends.
    var onEvent: (@Sendable (DiscordPresence.Event) -> Void)?

    /// How often the socket is read for events while listening.
    static let readInterval: TimeInterval = 1

    /// Keeps the socket open and read, subscribed to `events`.
    func startListening(for events: [String]) {
        queue.async { [self] in
            subscriptions = events
            guard !listening else { return }
            listening = true
            generation += 1
            if fd >= 0 { subscribe() }
            loops += 1
            readLoop(generation)
        }
    }

    func stopListening() {
        queue.async { [self] in
            listening = false
            subscriptions = []
        }
    }

    private var mayConnect: Bool { connects || dial != nil }

    private func readLoop(_ token: Int) {
        guard listening, token == generation else { loops -= 1; return }
        if fd < 0 {
            if mayConnect, Date().timeIntervalSince(lastFailure) >= retryInterval, !connect(replay: true) { lastFailure = Date() }
        } else {
            drain()
        }
        queue.asyncAfter(deadline: .now() + Self.readInterval) { [self] in readLoop(token) }
    }

    private func subscribe() {
        for event in subscriptions {
            if let data = DiscordPresence.frame(.frame, DiscordPresence.subscribePayload(event, nonce: UUID().uuidString)) {
                _ = write(data)
            }
        }
    }

    /// Sends a payload. If Discord is not running, an activity is kept and
    /// sent when Discord is found, looked for every `retryInterval`; if
    /// Discord went away since the last write, it is looked for at once.
    func send(_ payload: [String: Any], thenDisconnect: Bool = false) {
        nonisolated(unsafe) let payload = payload
        queue.async { [self] in
            guard let data = DiscordPresence.frame(.frame, payload) else { return }
            if payload["cmd"] as? String == "SET_ACTIVITY" {
                let shows = (payload["args"] as? [String: Any])?["activity"] != nil
                lastActivity = shows && !thenDisconnect ? data : nil
            }
            if fd < 0 {
                guard mayConnect, Date().timeIntervalSince(lastFailure) >= retryInterval else { scheduleRetry(); return }
                guard connect(replay: false) else { lastFailure = Date(); scheduleRetry(); return }
            }
            if !write(data) {
                // Discord quit or restarted since the last write: find it again now.
                disconnect()
                guard connect(replay: false), write(data) else {
                    disconnect(); lastFailure = Date(); scheduleRetry(); return
                }
            }
            drain()
            if thenDisconnect { disconnect() }
        }
    }

    /// Looks for Discord again later while there is an activity it hasn't got.
    private func scheduleRetry() {
        guard mayConnect, !retryScheduled, lastActivity != nil else { return }
        retryScheduled = true
        queue.asyncAfter(deadline: .now() + retryInterval) { [self] in
            retryScheduled = false
            guard fd < 0, lastActivity != nil else { return }
            if connect(replay: true) { drain() } else { lastFailure = Date(); scheduleRetry() }
        }
    }

    private func connect(replay: Bool) -> Bool {
        if let dial {
            guard let sock = dial() else { return false }
            return handshake(sock, replay: replay)
        }
        guard connects else { return false }
        let base = NSTemporaryDirectory().hasSuffix("/") ? String(NSTemporaryDirectory().dropLast()) : NSTemporaryDirectory()
        for index in 0..<10 {
            let path = "\(base)/discord-ipc-\(index)"
            let sock = socket(AF_UNIX, SOCK_STREAM, 0)
            guard sock >= 0 else { continue }
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
            if handshake(sock, replay: replay) { return true }
        }
        return false
    }

    /// Says hello on a connected socket and waits for READY; then subscribes
    /// and, with `replay`, shows the last activity again.
    private func handshake(_ sock: Int32, replay: Bool) -> Bool {
        fd = sock
        var noSigPipe: Int32 = 1
        setsockopt(sock, SOL_SOCKET, SO_NOSIGPIPE, &noSigPipe, socklen_t(MemoryLayout<Int32>.size))
        // A Discord that hangs without reading can't block the queue for good.
        var timeout = timeval(tv_sec: 2, tv_usec: 0)
        setsockopt(sock, SOL_SOCKET, SO_SNDTIMEO, &timeout, socklen_t(MemoryLayout<timeval>.size))
        let hello: [String: Any] = ["v": 1, "client_id": DiscordPresence.applicationID]
        if let data = DiscordPresence.frame(.handshake, hello), write(data), awaitReady() {
            subscribe()
            if replay, let lastActivity { _ = write(lastActivity) }
            return true
        }
        disconnect()
        return false
    }

    /// Waits (briefly) for Discord's READY reply to the handshake.
    private func awaitReady() -> Bool {
        guard let reply = readPacket(timeout: 3), reply.opcode == DiscordPresence.Opcode.frame.rawValue,
              let event = DiscordPresence.event(fromFrame: reply.body), case .ready = event else { return false }
        onEvent?(event)
        return true
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

    /// Reads Discord's replies so the socket never fills, passing on the
    /// events Lunalyr acts on (joins, errors).
    private func drain() {
        while let packet = readPacket(timeout: 0.05) {
            if packet.opcode == DiscordPresence.Opcode.close.rawValue { disconnect(); return }
            if packet.opcode == DiscordPresence.Opcode.frame.rawValue, let event = DiscordPresence.event(fromFrame: packet.body) {
                onEvent?(event)
            }
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
        // A header without its body leaves the stream mid-packet: start again.
        guard length < 1 << 20, let body = length == 0 ? Data() : readExactly(length, timeout: 1) else {
            disconnect()
            return nil
        }
        return (opcode, body)
    }

    private func readExactly(_ count: Int, timeout: TimeInterval) -> Data? {
        var data = Data()
        while data.count < count {
            var descriptor = pollfd(fd: fd, events: Int16(POLLIN), revents: 0)
            let ready = poll(&descriptor, 1, Int32(timeout * 1000))
            if ready <= 0 {
                // Part of a packet read and the rest late: the framing is lost.
                if !data.isEmpty { disconnect() }
                return nil
            }
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
