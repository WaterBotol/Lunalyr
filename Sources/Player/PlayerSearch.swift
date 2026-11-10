import AppKit

/// One thing the desktop player's search can find.
struct PlayerSearchResult: Identifiable, Equatable, Sendable {
    enum Kind: String, Sendable { case song, album }

    /// What playing it takes: a track already in the Music library, an
    /// address Music or Spotify opens, or nothing more than a search.
    enum Target: Equatable, Sendable {
        case libraryTrack(persistentID: String)
        case address(URL)
        case spotifySearch(String)
    }

    let id: String
    var kind: Kind
    var title: String
    var artist: String
    var album: String
    var artworkURL: URL?
    var target: Target
    var inLibrary: Bool
}

/// A track that plays after the current one.
struct PlayerQueueItem: Identifiable, Equatable, Sendable {
    let id: String
    var title: String
    var artist: String
    /// Position in the current playlist, one-based.
    var index: Int
    /// The song that is playing now.
    var isCurrent = false
    /// The Music library's id for the song, when it has one.
    var persistentID: String?

    /// Whether clicking it does anything: the song playing pauses or plays,
    /// any other needs its library ID to be found again. A song that played
    /// from a stream or the radio has none.
    var isPlayable: Bool { isCurrent || persistentID != nil }
}

/// What a player says is queued: the current playlist around the song, and
/// whether shuffle is on (when it is, that order is not the play order).
struct PlayerQueueSnapshot: Equatable, Sendable {
    var items: [PlayerQueueItem] = []
    var shuffle = false
}

enum PlayerSearchError: Error, Equatable, Sendable {
    case notRunning(PlayerKind)
    case blocked(PlayerKind)
    /// The player offers nothing to search (a local-file player).
    case unsupported(PlayerKind)
    case failed
    /// The script stopped on this AppleScript error number.
    case scriptFailed(Int)

    /// For the queue log: the error number, or what went wrong.
    static func logText(_ error: Error) -> String {
        switch error {
        case PlayerSearchError.scriptFailed(let code): return "error \(code)"
        case PlayerSearchError.notRunning: return "Music is not running"
        case PlayerSearchError.blocked: return "Automation access is off"
        case PlayerSearchError.failed: return "unexpected reply"
        case is CancellationError: return "cancelled"
        default: return "\(type(of: error))"
        }
    }
}

/// Searching and playing, one implementation per player. Everything runs
/// off the main thread; callers hop back to publish.
protocol PlayerSearching: Sendable {
    var kind: PlayerKind { get }
    func search(_ query: String) async throws -> [PlayerSearchResult]
    func play(_ result: PlayerSearchResult) async throws
    func queue() async throws -> PlayerQueueSnapshot
    func playQueueItem(_ item: PlayerQueueItem) async throws
    /// `play`, and anything the user should know about how it plays (a
    /// song that plays alone, and why); nil when there is nothing to say.
    func playNoting(_ result: PlayerSearchResult) async throws -> String?
    func playQueueItemNoting(_ item: PlayerQueueItem) async throws -> String?
}

extension PlayerSearching {
    func playNoting(_ result: PlayerSearchResult) async throws -> String? {
        try await play(result)
        return nil
    }

    func playQueueItemNoting(_ item: PlayerQueueItem) async throws -> String? {
        try await playQueueItem(item)
        return nil
    }
}

extension PlayerKind {
    var searcher: any PlayerSearching {
        switch self {
        case .appleMusic: return AppleMusicSearch()
        case .spotify: return SpotifySearch()
        case .vlc, .swinsian, .vox: return NoSearch(kind: self)
        case .safari, .chrome, .arc, .brave, .edge: return WebPlayerSearch(kind: self)
        }
    }
}

/// Sends one script to a player and hands back its reply. Live, this is the
/// monitor's own serial script queue (`NowPlayingMonitor.scriptLane`).
struct ScriptLane: Sendable {
    let send: @Sendable (PlayerScript, PlayerKind, Bool,
                         @escaping @Sendable (Result<NSAppleEventDescriptor, ScriptFailure>) -> Void) -> Void

    init(_ send: @escaping @Sendable (PlayerScript, PlayerKind, Bool,
                                      @escaping @Sendable (Result<NSAppleEventDescriptor, ScriptFailure>) -> Void) -> Void) {
        self.send = send
    }
}

/// The desktop player's scripts, waiting their turn. They run on the
/// monitor's script queue (there is no second AppleScript thread), one at a
/// time, so whatever waits here can still be reordered: a play goes ahead of
/// a search, a search ahead of a queue read, and covers come last. A job
/// whose task was cancelled while it waited (a cover scrolled away) is
/// dropped without being sent.
final class ScriptDesk: @unchecked Sendable {
    enum Priority: Int, Comparable, Sendable {
        case artwork, read, search, command
        static func < (a: Priority, b: Priority) -> Bool { a.rawValue < b.rawValue }
    }

    static let shared = ScriptDesk { await NowPlayingMonitor.shared.scriptLane }

    private struct Job {
        let id: Int
        let kind: PlayerKind
        let priority: Priority
        let polling: Bool
        /// Builds the script as it is about to go; nil when there is nothing left to send.
        let make: @Sendable () -> PlayerScript?
        let finish: @Sendable (Result<NSAppleEventDescriptor, Error>) -> Void
    }

    private let resolveLane: @Sendable () async -> ScriptLane
    private let lock = NSLock()
    private var lane: ScriptLane?
    private var waiting: [Job] = []
    private var busy = false
    private var nextID = 0
    /// Jobs cancelled before they were queued, or while they ran.
    private var cancelled: Set<Int> = []

    init(lane: @escaping @Sendable () async -> ScriptLane) {
        resolveLane = lane
    }

    /// Jobs not yet sent, for tests.
    var waitingCount: Int {
        lock.lock(); defer { lock.unlock() }
        return waiting.count
    }

    func run(_ script: PlayerScript, for kind: PlayerKind, priority: Priority,
             polling: Bool = false) async throws -> NSAppleEventDescriptor {
        try await run(for: kind, priority: priority, polling: polling) { script }
    }

    /// `make` is called only when the job reaches the front of the queue, so
    /// it can gather work that piled up meanwhile (several covers at once).
    func run(for kind: PlayerKind, priority: Priority, polling: Bool = false,
             make: @escaping @Sendable () -> PlayerScript?) async throws -> NSAppleEventDescriptor {
        let lane = await currentLane()
        let id: Int = {
            lock.lock(); defer { lock.unlock() }
            nextID &+= 1
            return nextID
        }()
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<NSAppleEventDescriptor, Error>) in
                enqueue(Job(id: id, kind: kind, priority: priority, polling: polling, make: make,
                            finish: { continuation.resume(with: $0) }), lane: lane)
            }
        } onCancel: {
            cancel(id)
        }
    }

    private func currentLane() async -> ScriptLane {
        if let known = (lock.withLock { lane }) { return known }
        let resolved = await resolveLane()
        lock.withLock { if lane == nil { lane = resolved } }
        return resolved
    }

    private func enqueue(_ job: Job, lane: ScriptLane) {
        lock.lock()
        if job.priority != .command, cancelled.remove(job.id) != nil {
            lock.unlock()
            job.finish(.failure(CancellationError()))
            return
        }
        waiting.append(job)
        lock.unlock()
        pump(lane)
    }

    /// A command the user asked for still goes; anything else waiting is dropped.
    private func cancel(_ id: Int) {
        lock.lock()
        var dropped: Job?
        if let index = waiting.firstIndex(where: { $0.id == id }) {
            if waiting[index].priority != .command { dropped = waiting.remove(at: index) }
        } else {
            cancelled.insert(id)
        }
        lock.unlock()
        dropped?.finish(.failure(CancellationError()))
    }

    private func pump(_ lane: ScriptLane) {
        while true {
            lock.lock()
            guard !busy, !waiting.isEmpty else { lock.unlock(); return }
            // Highest priority first; first come, first served within one.
            var best = 0
            for index in waiting.indices.dropFirst() where waiting[index].priority > waiting[best].priority {
                best = index
            }
            let job = waiting.remove(at: best)
            busy = true
            lock.unlock()
            guard let script = job.make() else {
                lock.withLock { busy = false; cancelled.remove(job.id) }
                job.finish(.failure(CancellationError()))
                continue
            }
            lane.send(script, job.kind, job.polling) { [self] outcome in
                lock.withLock { busy = false; cancelled.remove(job.id) }
                job.finish(Self.reply(outcome, for: job.kind))
                pump(lane)
            }
            return
        }
    }

    static func reply(_ outcome: Result<NSAppleEventDescriptor, ScriptFailure>,
                      for kind: PlayerKind) -> Result<NSAppleEventDescriptor, Error> {
        switch outcome {
        case .success(let reply):
            if reply.scriptItems.first?.scriptText == "notRunning" || reply.stringValue == "notRunning" {
                return .failure(PlayerSearchError.notRunning(kind))
            }
            return .success(reply)
        case .failure(let failure):
            switch PlayerResponse<Bool>(failureCode: failure.code) {
            case .blocked: return .failure(PlayerSearchError.blocked(kind))
            case .notRunning: return .failure(PlayerSearchError.notRunning(kind))
            default: return .failure(PlayerSearchError.scriptFailed(failure.code))
            }
        }
    }
}

/// The persistent IDs of the playlists Lunalyr made in Music: the queue it
/// plays from, and any half-built one a failure may have left. These are the
/// only playlists it ever deletes.
struct QueuePlaylistStore: @unchecked Sendable {
    static let queueKey = "custom.desktopPlayer.queuePlaylistID"
    static let pendingKey = "custom.desktopPlayer.pendingQueuePlaylistIDs"

    var defaults: UserDefaults = .standard

    var queueID: String? {
        get { defaults.string(forKey: Self.queueKey).flatMap(Self.valid) }
        nonmutating set { defaults.set(newValue, forKey: Self.queueKey) }
    }

    var pendingIDs: [String] {
        get { (defaults.stringArray(forKey: Self.pendingKey) ?? []).compactMap(Self.valid) }
        nonmutating set { defaults.set(newValue, forKey: Self.pendingKey) }
    }

    /// Music's persistent IDs are hexadecimal.
    static func valid(_ id: String) -> String? {
        !id.isEmpty && id.count <= 32 && id.allSatisfy(\.isHexDigit) ? id : nil
    }

    /// Builds that have not finished, kept on record until one does.
    static let pendingLimit = 10

    /// A build made `id`: on record, after the earlier unfinished ones, until
    /// its finish reports back. Only the latest few are kept.
    func record(_ id: String, after leftovers: [String]) {
        pendingIDs = Array((leftovers.filter { $0 != id } + [id]).suffix(Self.pendingLimit))
    }
}

/// How a click on a song went, for the strip's note and the log.
enum QueueOutcome: Equatable, Sendable {
    /// "Lunalyr Queue" plays: `songs` of the `wanted` ones were copied.
    case queued(songs: Int, wanted: Int, fromAlbum: Bool)
    /// The song plays on its own.
    case alone(QueueSolo)
    /// A newer click came first; this one played nothing.
    case superseded

    /// In plain words, when there is something to say.
    var note: String? {
        switch self {
        case .queued(let songs, let wanted, let fromAlbum):
            if songs < wanted { return "Some songs after it couldn\u{2019}t be added, so they\u{2019}re left out." }
            return fromAlbum ? "Then the rest of its album." : nil
        case .alone(let why): return why.note
        case .superseded: return nil
        }
    }
}

/// Why a song plays on its own.
enum QueueSolo: String, Sendable {
    /// What is playing can't be copied: radio, a CD, a folder, a stream.
    case source
    /// The song isn't in the library, so nothing can be copied after it.
    case notInLibrary
    /// A library song with no album name.
    case single
    /// Nothing comes after it on its album in the library.
    case album
    /// The queue could not be built, so the song plays by itself.
    case rescued

    var note: String {
        switch self {
        case .source: return "Plays this song only: Lunalyr can keep only your library and your own playlists going."
        case .notInLibrary: return "Plays this song only: it isn\u{2019}t in your library, so the playlist can\u{2019}t carry on from it."
        case .single: return "Plays this song only."
        case .album: return "Plays this song only: nothing after it on its album is in your library."
        case .rescued: return "Couldn\u{2019}t line up what comes next, so this song plays on its own."
        }
    }
}

/// A library song from the clicked song's album.
struct AlbumTrack: Equatable, Sendable {
    var id: String
    var disc = 0
    var number = 0
}

/// The first step's reply (`AppleMusicSearch.prepareQueue`).
enum QueuePrepared: Equatable, Sendable {
    /// Played alone, why; and the playing playlist's kind and size.
    case played(QueueSolo, source: String, songs: Int)
    /// The new queue playlist's persistent ID.
    case made(String, source: String, songs: Int)
    /// The clicked library song's album, in library order.
    case album([AlbumTrack], source: String, songs: Int)
    case unreadable

    init(_ items: [NSAppleEventDescriptor]) {
        func text(_ i: Int) -> String { items.indices.contains(i) ? items[i].scriptText ?? "" : "" }
        func number(_ i: Int) -> Int { items.indices.contains(i) ? Int(scriptValue: items[i].scriptNumber) ?? 0 : 0 }
        func numbers(_ i: Int) -> [Int] {
            items.indices.contains(i) ? items[i].scriptItems.map { Int(scriptValue: $0.scriptNumber) ?? 0 } : []
        }
        switch text(1) {
        case "played":
            self = .played(QueueSolo(rawValue: text(2)) ?? .source, source: text(3), songs: number(4))
        case "made":
            guard let id = QueuePlaylistStore.valid(text(4)) else { self = .unreadable; return }
            self = .made(id, source: text(2), songs: number(3))
        case "album":
            let ids = items.indices.contains(4) ? items[4].scriptItems.map { $0.scriptText ?? "" } : []
            let discs = numbers(5), tracks = numbers(6)
            let matched = discs.count == ids.count && tracks.count == ids.count
            self = .album(ids.indices.map { AlbumTrack(id: ids[$0], disc: matched ? discs[$0] : 0,
                                                        number: matched ? tracks[$0] : 0) },
                          source: text(2), songs: number(3))
        default:
            self = .unreadable
        }
    }

    /// For the queue log; no song titles.
    var logText: String {
        switch self {
        case .played(let why, let source, let songs): return "source \(source) (\(songs) songs): plays alone (\(why.rawValue))"
        case .made(_, let source, let songs): return "source \(source) (\(songs) songs): building"
        case .album(let tracks, let source, _): return "not in the playing playlist (source \(source)): \(tracks.count) songs on its album in the library"
        case .unreadable: return "unreadable reply"
        }
    }
}

/// The second step's reply (`AppleMusicSearch.collectQueue`).
enum QueueCollected: Equatable, Sendable {
    case ids([String])
    /// Positions in the library playlist.
    case ranges([ClosedRange<Int>])
    case unreadable

    init(_ items: [NSAppleEventDescriptor]) {
        func number(_ i: Int) -> Int? { items.indices.contains(i) ? Int(scriptValue: items[i].scriptNumber) : nil }
        switch items.indices.contains(1) ? items[1].scriptText : nil {
        case "ids":
            self = .ids(items.indices.contains(2) ? items[2].scriptItems.compactMap { $0.scriptText.flatMap(QueuePlaylistStore.valid) } : [])
        case "range":
            guard let n = number(2), let end = number(3), n >= 1, end >= n else { self = .unreadable; return }
            let upTo = number(4) ?? 0
            self = .ranges([n...end] + (upTo >= 1 && upTo < n ? [1...upTo] : []))
        default:
            self = .unreadable
        }
    }

    /// How many songs a fill step copied.
    static func copied(_ items: [NSAppleEventDescriptor]) -> Int {
        items.indices.contains(1) ? max(0, Int(scriptValue: items[1].scriptNumber) ?? 0) : 0
    }
}

/// Numbers the clicks on songs, so a build can tell a newer click came.
final class ClickCounter: @unchecked Sendable {
    private let lock = NSLock()
    private var latest = 0

    func begin() -> Int { lock.withLock { latest += 1; return latest } }
    func isLatest(_ click: Int) -> Bool { lock.withLock { latest == click } }
}

/// Runs async jobs one after another, in the order they were started.
final class SerialFlow: @unchecked Sendable {
    private let lock = NSLock()
    private var tail: Task<Void, Never>?

    func run<Value: Sendable>(_ work: @escaping @Sendable () async throws -> Value) async throws -> Value {
        let task: Task<Value, Error> = lock.withLock {
            let previous = tail
            let task = Task { _ = await previous?.value; return try await work() }
            tail = Task { _ = try? await task.value }
            return task
        }
        return try await task.value
    }
}

/// Escapes text for an AppleScript string literal. Inside the quotes every
/// word is plain text, so a song or search named "back", "last", "stop" or
/// "end tell" is never read as AppleScript. Line breaks of every kind and
/// other control characters (a NUL ends the source early) become spaces; tab
/// stays. Everything else, accents and emoji included, is kept as it is.
enum ScriptText {
    static func quoted(_ text: String) -> String {
        var cleaned = ""
        cleaned.unicodeScalars.reserveCapacity(text.unicodeScalars.count + 2)
        for scalar in text.unicodeScalars {
            switch scalar {
            case "\\": cleaned += "\\\\"
            case "\"": cleaned += "\\\""
            case "\t": cleaned.unicodeScalars.append(scalar)
            case "\u{2028}", "\u{2029}", "\u{85}": cleaned += " "
            default:
                if scalar.properties.generalCategory == .control {
                    cleaned += " "
                } else {
                    cleaned.unicodeScalars.append(scalar)
                }
            }
        }
        return "\"\(cleaned)\""
    }
}

// MARK: - Apple Music

struct AppleMusicSearch: PlayerSearching {
    var kind: PlayerKind { .appleMusic }
    static let bundleID = "com.apple.Music"

    func search(_ query: String) async throws -> [PlayerSearchResult] {
        let trimmed = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed.count >= 2 else { return [] }
        async let library = librarySearch(trimmed)
        async let catalogue = Self.catalogueSearch(trimmed)
        var libraryError: Error?
        var inLibrary: [PlayerSearchResult] = []
        do { inLibrary = try await library } catch { libraryError = error }
        let online = await catalogue
        // Library first, then catalogue results that are not already there.
        var seen = Set(inLibrary.map { Self.key($0.title, $0.artist) })
        var merged = inLibrary
        for result in online where seen.insert(Self.key(result.title, result.artist, result.kind)).inserted {
            merged.append(result)
        }
        // Nothing found and the library could not be asked: say why.
        if merged.isEmpty, let libraryError { throw libraryError }
        return merged
    }

    private static func key(_ title: String, _ artist: String, _ kind: PlayerSearchResult.Kind = .song) -> String {
        "\(kind.rawValue)|\(title.lowercased())|\(artist.lowercased())"
    }

    /// Tracks already in the library. Only those can be played by their
    /// persistent ID; everything else goes through an address.
    func librarySearch(_ query: String) async throws -> [PlayerSearchResult] {
        let source = PlayerScriptSource.query(Self.bundleID, """
        set hits to {}
        try
        set found to search library playlist 1 for \(ScriptText.quoted(query)) only all
        on error number errorNumber
        if errorNumber is not in \(PlayerScriptSource.tolerated) then error number errorNumber
        return {"ok"}
        end try
        set limitCount to count of found
        if limitCount > 6 then set limitCount to 6
        repeat with i from 1 to limitCount
        set t to item i of found
        set end of hits to {persistent ID of t, name of t, artist of t, album of t}
        end repeat
        return {"ok", hits}
        """)
        let reply = try await ScriptDesk.shared.run(
            PlayerScript(name: "desktop-player.library-search", source: source, cacheable: false), for: .appleMusic,
            priority: .search)
        return Self.parseLibrary(reply)
    }

    static func parseLibrary(_ reply: NSAppleEventDescriptor) -> [PlayerSearchResult] {
        let items = reply.scriptItems
        guard items.first?.scriptText == "ok", items.count >= 2 else { return [] }
        let rows = items[1].scriptItems
        return rows.compactMap { row -> PlayerSearchResult? in
            let fields = row.scriptItems
            guard fields.count >= 4, let id = fields[0].scriptText, let title = fields[1].scriptText else { return nil }
            return PlayerSearchResult(
                id: "lib-\(id)", kind: .song, title: title, artist: fields[2].scriptText ?? "",
                album: fields[3].scriptText ?? "", artworkURL: nil,
                target: .libraryTrack(persistentID: id), inLibrary: true)
        }
    }

    /// Apple's public catalogue search, for everything the library does not
    /// hold. The query is the user's own typing; nothing else is sent.
    static func catalogueSearch(_ query: String) async -> [PlayerSearchResult] {
        async let songs = fetch(query, entity: "song", limit: 8)
        async let albums = fetch(query, entity: "album", limit: 4)
        return (await songs) + (await albums)
    }

    static func searchURL(_ query: String, entity: String, limit: Int) -> URL? {
        var components = URLComponents(string: "https://itunes.apple.com/search")
        components?.queryItems = [
            URLQueryItem(name: "term", value: query), URLQueryItem(name: "media", value: "music"),
            URLQueryItem(name: "entity", value: entity), URLQueryItem(name: "limit", value: String(limit)),
        ]
        return components?.url
    }

    private static func fetch(_ query: String, entity: String, limit: Int) async -> [PlayerSearchResult] {
        guard let url = searchURL(query, entity: entity, limit: limit),
              let (data, _) = try? await URLSession.shared.data(from: url) else { return [] }
        return parseCatalogue(data)
    }

    static func parseCatalogue(_ data: Data) -> [PlayerSearchResult] {
        guard let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let rows = root["results"] as? [[String: Any]] else { return [] }
        return rows.compactMap { row -> PlayerSearchResult? in
            let wrapper = row["wrapperType"] as? String
            let art = (row["artworkUrl100"] as? String).flatMap { URL(string: $0.replacingOccurrences(of: "100x100", with: "200x200")) }
            if wrapper == "track", let title = row["trackName"] as? String,
               let address = (row["trackViewUrl"] as? String).flatMap(musicAddress) {
                let id = (row["trackId"] as? NSNumber)?.stringValue ?? title
                return PlayerSearchResult(
                    id: "cat-t-\(id)", kind: .song, title: title, artist: row["artistName"] as? String ?? "",
                    album: row["collectionName"] as? String ?? "", artworkURL: art,
                    target: .address(address), inLibrary: false)
            }
            if wrapper == "collection", let title = row["collectionName"] as? String,
               let address = (row["collectionViewUrl"] as? String).flatMap(musicAddress) {
                let id = (row["collectionId"] as? NSNumber)?.stringValue ?? title
                return PlayerSearchResult(
                    id: "cat-a-\(id)", kind: .album, title: title, artist: row["artistName"] as? String ?? "",
                    album: title, artworkURL: art, target: .address(address), inLibrary: false)
            }
            return nil
        }
    }

    /// A catalogue page address in the form the Music app itself opens.
    static func musicAddress(_ web: String) -> URL? {
        guard var components = URLComponents(string: web), let host = components.host?.lowercased(),
              host == "apple.com" || host.hasSuffix(".apple.com") else { return nil }
        components.scheme = "music"
        return components.url
    }

    /// Music plays one song alone when told to play it out of a playlist, and
    /// has no way to start a playlist part-way. So the song and the ones after
    /// it are copied, in order, into a playlist of Lunalyr's own, which is
    /// played from its start: the playlist carries on, and shuffle still works.
    /// A library song from outside the playing playlist (a search result)
    /// carries on through the rest of its album instead.
    ///
    /// Only playlists Lunalyr made are ever deleted: their persistent IDs are
    /// kept (`QueuePlaylistStore`), and one is deleted only while it is still
    /// a plain user playlist (not a folder, not smart) with exactly the name
    /// Lunalyr gave it, case included.
    static let queuePlaylistName = "Lunalyr Queue"
    static let buildingPlaylistName = queuePlaylistName + " (new)"
    /// Songs copied at most.
    static let queueLength = 150
    /// Seconds each Apple event of a build step may take. Copying songs into
    /// a playlist can be slow while Music syncs the library; a step that runs
    /// out of time fails the build (and the clicked song still plays).
    static let queueStepTimeout = 12

    var store = QueuePlaylistStore()
    /// Where the queue's scripts wait their turn; tests give their own.
    var desk: ScriptDesk = .shared

    /// Errors one song can raise without stopping the copy: it is missing, or
    /// Music refuses to add it to a playlist (-10000 handler failed, -10006
    /// write refused, as for a song no longer available). Anything else --
    /// Music quitting, access off, a timeout -- ends the step.
    static let skippable = "{-1728, -1700, -1708, -1719, -10000, -10006}"

    /// Deletes the playlist with this persistent ID, but only if it is still
    /// one Lunalyr made: a plain user playlist named exactly `name`.
    static func deleteOwnPlaylist(_ id: String, named name: String) -> String {
        """
        try
        set ownList to (first user playlist whose persistent ID is \(ScriptText.quoted(id)))
        if class of ownList is user playlist then
        if smart of ownList is false then
        set ownName to name of ownList
        considering case
        if ownName is \(ScriptText.quoted(name)) then delete ownList
        end considering
        end if
        end if
        on error number errorNumber
        if errorNumber is not in \(PlayerScriptSource.tolerated) then error number errorNumber
        end try
        """
    }

    /// Finds the clicked song in the playing playlist as `srcTrack`: at its
    /// place in the strip when that is still the song (a playlist can hold the
    /// same song twice), else its first copy.
    private static func locate(_ id: String, index: Int?) -> String {
        let quotedID = ScriptText.quoted(id)
        var lines = ["set src to current playlist"]
        if let index, index > 0 {
            lines += [
                "try",
                "set candidate to track \(index) of src",
                "if persistent ID of candidate is \(quotedID) then",
                "set srcTrack to candidate",
                "set inSource to true",
                "end if",
                "on error number errorNumber",
                "if errorNumber is not in \(PlayerScriptSource.tolerated) then error number errorNumber",
                "end try",
            ]
        }
        lines += [
            "if not inSource then",
            "set srcTrack to first track of src whose persistent ID is \(quotedID)",
            "set inSource to true",
            "end if",
        ]
        return lines.joined(separator: "\n")
    }

    /// Step one. Clears up any half-built queue Lunalyr left behind, then
    /// decides, from the scripting dictionary's own classes:
    /// - the song is in the playing playlist, that playlist is the library,
    ///   a user playlist or an Apple Music playlist, and the song is in the
    ///   library: makes the new queue playlist, `{"ok", "made", kind, count, id}`;
    /// - it is in the playing playlist but that cannot be copied (radio, a
    ///   CD, a folder, a stream) or the song is not in the library: plays
    ///   it alone, `{"ok", "played", why, kind, count}`;
    /// - it is only in the library (a search result, a song from history):
    ///   replies with its album's songs, `{"ok", "album", kind, count, ids,
    ///   discs, numbers}`, or plays it alone when it has no album name.
    ///
    /// Only library songs are ever copied (`fillQueue`, `fillRange`): adding
    /// any other song to a user playlist would add it to the library.
    /// `kind` is the playing playlist's class, for the log. Not verified
    /// live: the hard rules forbid driving Music.
    static func prepareQueue(persistentID id: String, index: Int?, leftovers: [String]) -> String {
        let cleanUp = leftovers.map { deleteOwnPlaylist($0, named: buildingPlaylistName) }.joined(separator: "\n")
        let quotedID = ScriptText.quoted(id)
        return """
        \(cleanUp)
        set inSource to false
        try
        \(locate(id, index: index))
        on error number errorNumber
        if errorNumber is not in \(PlayerScriptSource.tolerated) then error number errorNumber
        end try
        set inLibrary to false
        try
        set libTrack to first track of library playlist 1 whose persistent ID is \(quotedID)
        set inLibrary to true
        on error number errorNumber
        if errorNumber is not in \(PlayerScriptSource.tolerated) then error number errorNumber
        end try
        set sourceKind to "none"
        set total to 0
        if inSource then
        set sourceKind to "other"
        try
        set total to count of tracks of src
        set sourceClass to class of src
        if sourceClass is library playlist then
        set sourceKind to "library"
        else if sourceClass is user playlist then
        set sourceKind to "playlist"
        else if sourceClass is subscription playlist then
        set sourceKind to "apple-music-playlist"
        else if sourceClass is radio tuner playlist then
        set sourceKind to "radio"
        else if sourceClass is audio CD playlist then
        set sourceKind to "cd"
        else if sourceClass is folder playlist then
        set sourceKind to "folder"
        end if
        on error number errorNumber
        if errorNumber is not in \(PlayerScriptSource.tolerated) then error number errorNumber
        end try
        end if
        if inSource and inLibrary and sourceKind is in {"library", "playlist", "apple-music-playlist"} then
        set newList to make new user playlist with properties {name:\(ScriptText.quoted(buildingPlaylistName))}
        return {"ok", "made", sourceKind, total, persistent ID of newList}
        end if
        if inSource then
        play srcTrack
        if inLibrary then return {"ok", "played", "source", sourceKind, total}
        return {"ok", "played", "notInLibrary", sourceKind, total}
        end if
        if not inLibrary then error number -1728
        set albumName to ""
        set albumArtist to ""
        set trackArtist to ""
        try
        set albumName to album of libTrack
        set albumArtist to album artist of libTrack
        set trackArtist to artist of libTrack
        on error number errorNumber
        if errorNumber is not in \(PlayerScriptSource.tolerated) then error number errorNumber
        end try
        if albumName is "" then
        play libTrack
        return {"ok", "played", "single", sourceKind, total}
        end if
        if albumArtist is not "" then
        set albumIDs to persistent ID of (every track of library playlist 1 whose album is albumName and album artist is albumArtist)
        set albumDiscs to disc number of (every track of library playlist 1 whose album is albumName and album artist is albumArtist)
        set albumNumbers to track number of (every track of library playlist 1 whose album is albumName and album artist is albumArtist)
        else
        set albumIDs to persistent ID of (every track of library playlist 1 whose album is albumName and artist is trackArtist)
        set albumDiscs to disc number of (every track of library playlist 1 whose album is albumName and artist is trackArtist)
        set albumNumbers to track number of (every track of library playlist 1 whose album is albumName and artist is trackArtist)
        end if
        return {"ok", "album", sourceKind, total, albumIDs, albumDiscs, albumNumbers}
        """
    }

    /// Makes the new queue playlist for an album (step one only reads it).
    static func makeQueue() -> String {
        """
        set newList to make new user playlist with properties {name:\(ScriptText.quoted(buildingPlaylistName))}
        return {"ok", "made", persistent ID of newList}
        """
    }

    /// Plays a library song on its own: one with nothing to carry on to.
    static func playAlone(persistentID id: String) -> String {
        """
        play (first track of library playlist 1 whose persistent ID is \(ScriptText.quoted(id)))
        return {"ok", "played"}
        """
    }

    /// Step two. Finds the song in the playing playlist and says which songs
    /// follow it (wrapping round, up to `queueLength`). From the library:
    /// `{"ok", "range", n, endAt, upTo}`, the positions `fillRange` copies,
    /// a short run at a time. From any other playlist, which may hold songs
    /// that aren't in the library: `{"ok", "ids", {…}}`, for `fillQueue`.
    /// Copies nothing itself. A failure deletes the half-built playlist and
    /// then raises the error.
    static func collectQueue(persistentID id: String, index: Int?, into newID: String) -> String {
        """
        try
        set inSource to false
        \(locate(id, index: index))
        set n to index of srcTrack
        set total to count of tracks of src
        set endAt to n + \(queueLength - 1)
        if endAt > total then set endAt to total
        set room to \(queueLength) - (endAt - n + 1)
        set upTo to 0
        if n > 1 and room > 0 then
        set upTo to n - 1
        if upTo > room then set upTo to room
        end if
        if class of src is library playlist then return {"ok", "range", n, endAt, upTo}
        set wanted to (persistent ID of tracks n thru endAt of src) as list
        if upTo > 0 then set wanted to wanted & ((persistent ID of tracks 1 thru upTo of src) as list)
        return {"ok", "ids", wanted}
        \(cleanUpOnFailure(newID))
        """
    }

    /// Songs `fillQueue` copies in one script, and positions `fillRange`
    /// copies in one: short enough that a poll never waits long behind it.
    static let queueChunk = 20
    static let rangeChunk = 25

    /// Copies these songs, by their library copies, into the new playlist.
    /// A song that is gone or that Music won't add is skipped (`skippable`).
    /// Replies `{"ok", copied}`.
    static func fillQueue(_ ids: [String], into newID: String) -> String {
        let list = "{" + ids.compactMap(QueuePlaylistStore.valid).map(ScriptText.quoted).joined(separator: ", ") + "}"
        return """
        set newList to first user playlist whose persistent ID is \(ScriptText.quoted(newID))
        try
        set countBefore to count of tracks of newList
        repeat with idItem in \(list)
        set wantedID to contents of idItem
        try
        duplicate (first track of library playlist 1 whose persistent ID is wantedID) to newList
        on error number errorNumber
        if errorNumber is not in \(skippable) then error number errorNumber
        end try
        end repeat
        set copied to (count of tracks of newList) - countBefore
        \(cleanUpOnFailure(newID))
        return {"ok", copied}
        """
    }

    /// Copies library songs `from` to `to` (positions in the library
    /// playlist) into the new playlist in one ranged `duplicate`. If that
    /// is refused and added nothing, they go one at a time and any Music
    /// won't add is skipped, so one bad song no longer sinks the whole
    /// build. Replies `{"ok", copied}`.
    static func fillRange(from start: Int, to end: Int, into newID: String) -> String {
        """
        set newList to first user playlist whose persistent ID is \(ScriptText.quoted(newID))
        try
        set src to library playlist 1
        set countBefore to count of tracks of newList
        try
        duplicate tracks \(start) thru \(end) of src to newList
        on error number errorNumber
        if errorNumber is not in \(skippable) then error number errorNumber
        if (count of tracks of newList) is countBefore then
        repeat with i from \(start) to \(end)
        try
        duplicate track i of src to newList
        on error number errorNumber
        if errorNumber is not in \(skippable) then error number errorNumber
        end try
        end repeat
        end if
        end try
        set copied to (count of tracks of newList) - countBefore
        \(cleanUpOnFailure(newID))
        return {"ok", copied}
        """
    }

    /// Closes a `try` opened at the top of a step: on any error the
    /// half-built playlist is deleted, by its ID and exact name, and the
    /// error raised again unchanged.
    private static func cleanUpOnFailure(_ newID: String) -> String {
        """
        on error buildText number buildNumber
        try
        \(deleteOwnPlaylist(newID, named: buildingPlaylistName))
        end try
        error buildText number buildNumber
        end try
        """
    }

    /// The last step. The previous queue is deleted, the new one takes its
    /// name and plays from its start with shuffle off for the moment, then
    /// shuffle is put back. Replies `{"ok", n}`, where n is a playback error
    /// number, or 0.
    static func finishQueue(into newID: String, replacing oldID: String?) -> String {
        finishQueue(into: newID, replacing: oldID.map { [$0] } ?? [])
    }

    /// `oldIDs`: the previous queue, and any earlier build whose finish
    /// renamed it but never reported back (Music quit, Lunalyr quit). Each
    /// goes only by its own ID and only while it is still named exactly
    /// "Lunalyr Queue"; without this such a playlist was left behind for
    /// good, a second "Lunalyr Queue" beside the real one.
    static func finishQueue(into newID: String, replacing oldIDs: [String]) -> String {
        var seen: Set<String> = [newID]
        let replace = oldIDs.compactMap(QueuePlaylistStore.valid).filter { seen.insert($0).inserted }
            .map { deleteOwnPlaylist($0, named: queuePlaylistName) }.joined(separator: "\n")
        return """
        set newList to first user playlist whose persistent ID is \(ScriptText.quoted(newID))
        set wasShuffle to shuffle enabled
        \(replace)
        set name of newList to \(ScriptText.quoted(queuePlaylistName))
        set playError to 0
        try
        set shuffle enabled to false
        play newList
        delay 0.6
        set shuffle enabled to wasShuffle
        delay 0.4
        if shuffle enabled is not wasShuffle then set shuffle enabled to wasShuffle
        on error number errorNumber
        set playError to errorNumber
        try
        set shuffle enabled to wasShuffle
        end try
        end try
        return {"ok", playError}
        """
    }

    /// After a build failed: the half-built playlist goes (by ID and exact
    /// name), and the clicked song plays on its own -- unless it is already
    /// playing (a finish that started it but whose reply was lost). Never
    /// leaves silence where a song was asked for. Replies `{"ok", "played"}`
    /// or `{"ok", "playing"}`.
    static func rescueQueue(persistentID id: String, index: Int?, newID: String?) -> String {
        let quotedID = ScriptText.quoted(id)
        let cleanUp = newID.flatMap(QueuePlaylistStore.valid).map {
            "try\n\(deleteOwnPlaylist($0, named: buildingPlaylistName))\nend try"
        } ?? ""
        return """
        \(cleanUp)
        try
        if player state is playing then
        if persistent ID of current track is \(quotedID) then return {"ok", "playing"}
        end if
        on error number errorNumber
        if errorNumber is not in \(PlayerScriptSource.tolerated) then error number errorNumber
        end try
        set inSource to false
        try
        \(locate(id, index: index))
        on error number errorNumber
        if errorNumber is not in \(PlayerScriptSource.tolerated) then error number errorNumber
        end try
        if inSource then
        play srcTrack
        else
        play (first track of library playlist 1 whose persistent ID is \(quotedID))
        end if
        return {"ok", "played"}
        """
    }

    /// One step of building the queue, as a command: its reply's items once
    /// it says "ok". Every step is logged with its AppleScript error number.
    private static func sendQueueStep(_ name: String, _ body: String, click: Int,
                                      desk: ScriptDesk) async throws -> [NSAppleEventDescriptor] {
        do {
            let reply = try await desk.run(for: .appleMusic, priority: .command) {
                PlayerScript(name: "desktop-player.queue-\(name)",
                             source: PlayerScriptSource.query(bundleID, timeout: queueStepTimeout, body), cacheable: false)
            }
            let items = reply.scriptItems
            guard items.first?.scriptText == "ok" else { throw PlayerSearchError.failed }
            QueueLog.step(click, name, detail: "ok")
            return items
        } catch {
            QueueLog.step(click, name, failed: PlayerSearchError.logText(error))
            throw error
        }
    }

    /// `ids` in runs of `queueChunk`, in order.
    static func chunks(_ ids: [String]) -> [[String]] {
        stride(from: 0, to: ids.count, by: queueChunk).map { Array(ids[$0..<min($0 + queueChunk, ids.count)]) }
    }

    /// Library positions in runs of `rangeChunk`, in order.
    static func rangeChunks(_ ranges: [ClosedRange<Int>]) -> [ClosedRange<Int>] {
        ranges.filter { $0.lowerBound >= 1 }.flatMap { range in
            stride(from: range.lowerBound, through: range.upperBound, by: rangeChunk).map {
                $0...min($0 + rangeChunk - 1, range.upperBound)
            }
        }
    }

    /// One click at a time: a second click's clean-up must not delete the
    /// first click's playlist while that one is still being filled.
    private static let queueFlow = SerialFlow()
    /// Which click is the latest: one overtaken by a newer click stops
    /// before it plays anything, so a quick run of clicks plays only the last.
    private static let clicks = ClickCounter()

    /// Plays the song, carrying on with what follows it where Music allows
    /// (see `prepareQueue`), and says how it went. `origin` is for the log.
    @discardableResult
    func playInContext(persistentID id: String, index: Int?, origin: String = "strip") async throws -> QueueOutcome {
        let store = store
        let click = Self.clicks.begin()
        let send = { @Sendable [desk] (name: String, body: String) async throws -> [NSAppleEventDescriptor] in
            try await Self.sendQueueStep(name, body, click: click, desk: desk)
        }
        return try await Self.queueFlow.run {
            guard Self.clicks.isLatest(click) else {
                QueueLog.finished(click, "skipped: a newer click came first")
                return .superseded
            }
            QueueLog.started(click, from: origin, index: index)
            return try await Self.build(persistentID: id, index: index, click: click, store: store, send: send)
        }
    }

    private static func build(persistentID id: String, index: Int?, click: Int, store: QueuePlaylistStore,
                              send: @escaping @Sendable (String, String) async throws -> [NSAppleEventDescriptor])
        async throws -> QueueOutcome {
        var newID: String?
        do {
            let leftovers = store.pendingIDs
            let prepared = QueuePrepared(try await send("prepare", prepareQueue(persistentID: id, index: index,
                                                                                    leftovers: leftovers)))
            QueueLog.prepared(click, prepared)
            let wanted: Int
            var copied = 0
            var fromAlbum = false
            switch prepared {
            case .unreadable:
                throw PlayerSearchError.failed
            case .played(let why, _, _):
                QueueLog.finished(click, "played alone (\(why.rawValue))")
                return .alone(why)
            case .made(let made, _, _):
                newID = made
                store.record(made, after: leftovers)
                switch QueueCollected(try await send("collect", collectQueue(persistentID: id, index: index, into: made))) {
                case .unreadable:
                    throw PlayerSearchError.failed
                case .ids(let ids):
                    wanted = ids.count
                    QueueLog.step(click, "collect", detail: "\(ids.count) songs by ID")
                    for chunk in chunks(ids) {
                        copied += QueueCollected.copied(try await send("fill", fillQueue(chunk, into: made)))
                    }
                case .ranges(let ranges):
                    wanted = ranges.reduce(0) { $0 + $1.count }
                    QueueLog.step(click, "collect", detail: "\(wanted) library songs")
                    for range in rangeChunks(ranges) {
                        copied += QueueCollected.copied(try await send(
                            "fill", fillRange(from: range.lowerBound, to: range.upperBound, into: made)))
                    }
                }
            case .album(let tracks, _, _):
                let order = albumQueue(startingAt: id, tracks: tracks)
                guard order.count > 1 else {
                    _ = try await send("play-alone", playAlone(persistentID: id))
                    QueueLog.finished(click, "played alone (nothing after it on its album)")
                    return .alone(.album)
                }
                fromAlbum = true
                wanted = order.count
                let madeReply = try await send("make", makeQueue())
                guard madeReply.count >= 3, let made = madeReply[2].scriptText.flatMap(QueuePlaylistStore.valid) else {
                    throw PlayerSearchError.failed
                }
                newID = made
                store.record(made, after: leftovers)
                for chunk in chunks(order) {
                    copied += QueueCollected.copied(try await send("fill", fillQueue(chunk, into: made)))
                }
            }
            QueueLog.step(click, "fill", detail: "\(copied) of \(wanted) songs copied")
            guard let made = newID, copied > 0 else { throw PlayerSearchError.failed }
            guard clicks.isLatest(click) else {
                // Left on record: the newer click's first step deletes it.
                QueueLog.finished(click, "stopped: a newer click came first")
                return .superseded
            }
            let finished = try await send("finish", finishQueue(
                into: made, replacing: (store.queueID.map { [$0] } ?? []) + leftovers))
            store.queueID = made
            store.pendingIDs = []
            newID = nil
            if finished.count >= 2, let playError = Int(scriptValue: finished[1].scriptNumber), playError != 0 {
                QueueLog.step(click, "finish", failed: "play error \(playError)")
                throw PlayerSearchError.scriptFailed(playError)
            }
            QueueLog.finished(click, "playing \(queuePlaylistName), \(copied) songs\(fromAlbum ? " from its album" : "")")
            return .queued(songs: copied, wanted: wanted, fromAlbum: fromAlbum)
        } catch {
            guard !(error is CancellationError) else { throw error }
            do {
                _ = try await send("rescue", rescueQueue(persistentID: id, index: index, newID: newID))
                QueueLog.finished(click, "build failed; played the song alone")
                return .alone(.rescued)
            } catch {
                QueueLog.finished(click, "build failed and the song could not be played")
            }
            throw error
        }
    }

    /// An album's library songs from the clicked one to the end, in album
    /// order (disc, then track number; unnumbered songs after the numbered
    /// ones, in library order). Nothing before it, nothing after the album.
    static func albumQueue(startingAt id: String, tracks: [AlbumTrack]) -> [String] {
        let ordered = tracks.enumerated().sorted { a, b in
            (max(a.element.disc, 1), a.element.number > 0 ? a.element.number : Int.max, a.offset)
                < (max(b.element.disc, 1), b.element.number > 0 ? b.element.number : Int.max, b.offset)
        }.map(\.element.id)
        guard let start = ordered.firstIndex(of: id) else { return QueuePlaylistStore.valid(id).map { [$0] } ?? [] }
        var seen: Set<String> = []
        return Array(ordered[start...].filter { QueuePlaylistStore.valid($0) != nil && seen.insert($0).inserted }
            .prefix(queueLength))
    }

    func play(_ result: PlayerSearchResult) async throws {
        _ = try await playNoting(result)
    }

    /// A library song carries on through the playlist it is in, else its
    /// album (`playInContext`); the note says when it plays alone.
    func playNoting(_ result: PlayerSearchResult) async throws -> String? {
        switch result.target {
        case .libraryTrack(let id):
            return try await playInContext(persistentID: id, index: nil, origin: "search").note
        case .address(let url):
            try await open(url)
            return nil
        case .spotifySearch:
            throw PlayerSearchError.failed
        }
    }

    /// Opening a `music://` address plays a song, or opens an album. Music is
    /// then told to play, so an album starts at once rather than waiting.
    private func open(_ url: URL) async throws {
        try await run("open", """
        open location \(ScriptText.quoted(url.absoluteString))
        delay 0.6
        play
        """)
    }

    func queue() async throws -> PlayerQueueSnapshot {
        let source = PlayerScriptSource.query(Self.bundleID, """
        if player state is stopped then return {"ok", {}, false}
        set rows to {}
        set shuffling to false
        try
        set shuffling to shuffle enabled
        end try
        try
        set here to index of current track
        set listing to current playlist
        set total to count of tracks of listing
        set upTo to here + 24
        if upTo > total then set upTo to total
        set fromHere to here - 24
        if fromHere < 1 then set fromHere to 1
        repeat with i from fromHere to upTo
        set t to track i of listing
        set end of rows to {persistent ID of t, name of t, artist of t, i, here}
        end repeat
        on error number errorNumber
        if errorNumber is not in \(PlayerScriptSource.tolerated) then error number errorNumber
        end try
        return {"ok", rows, shuffling}
        """)
        let reply = try await ScriptDesk.shared.run(
            PlayerScript(name: "desktop-player.queue", source: source, cacheable: false), for: .appleMusic,
            priority: .read, polling: true)
        let items = reply.scriptItems
        let shuffle = items.count >= 3 && items[2].booleanValue
        return PlayerQueueSnapshot(items: Self.parseQueue(reply), shuffle: shuffle)
    }

    static func parseQueue(_ reply: NSAppleEventDescriptor) -> [PlayerQueueItem] {
        let items = reply.scriptItems
        guard items.first?.scriptText == "ok", items.count >= 2 else { return [] }
        return items[1].scriptItems.compactMap { row -> PlayerQueueItem? in
            let fields = row.scriptItems
            guard fields.count >= 4, let id = fields[0].scriptText, let title = fields[1].scriptText,
                  let index = Int(scriptValue: fields[3].scriptNumber) else { return nil }
            let here = fields.count >= 5 ? Int(scriptValue: fields[4].scriptNumber) : nil
            return PlayerQueueItem(id: "q-\(id)-\(index)", title: title, artist: fields[2].scriptText ?? "",
                                   index: index, isCurrent: here == index, persistentID: id)
        }
    }

    /// A song without a library ID (a stream, a radio track) cannot be found
    /// again; the strip does not offer it (`PlayerQueueItem.isPlayable`).
    func playQueueItem(_ item: PlayerQueueItem) async throws {
        _ = try await playQueueItemNoting(item)
    }

    func playQueueItemNoting(_ item: PlayerQueueItem) async throws -> String? {
        guard let id = item.persistentID else { throw PlayerSearchError.failed }
        return try await playInContext(persistentID: id, index: item.index > 0 ? item.index : nil, origin: "strip").note
    }

    private func run(_ name: String, _ body: String) async throws {
        let script = PlayerScript(name: "desktop-player.\(name)", source: PlayerScriptSource.command(Self.bundleID, body),
                                  cacheable: false)
        _ = try await ScriptDesk.shared.run(script, for: .appleMusic, priority: .command)
    }
}

// MARK: - Spotify

/// Spotify offers no search or queue to other apps without an account
/// sign-in, so a search opens Spotify's own search page for the words typed.
/// Playing a track by its `spotify:` address works the same way.
struct SpotifySearch: PlayerSearching {
    var kind: PlayerKind { .spotify }

    func search(_ query: String) async throws -> [PlayerSearchResult] {
        let trimmed = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed.count >= 2 else { return [] }
        return [PlayerSearchResult(
            id: "spotify-search", kind: .song, title: "Search Spotify for \u{201C}\(trimmed)\u{201D}", artist: "Opens in the Spotify app",
            album: "", artworkURL: nil, target: .spotifySearch(trimmed), inLibrary: false)]
    }

    static func searchAddress(_ query: String) -> URL? {
        let allowed = CharacterSet.urlPathAllowed.subtracting(CharacterSet(charactersIn: "/:"))
        guard let escaped = query.addingPercentEncoding(withAllowedCharacters: allowed) else { return nil }
        return URL(string: "spotify:search:\(escaped)")
    }

    func play(_ result: PlayerSearchResult) async throws {
        switch result.target {
        case .spotifySearch(let query):
            guard let url = Self.searchAddress(query),
                  let app = NSWorkspace.shared.urlForApplication(withBundleIdentifier: kind.bundleID) else { throw PlayerSearchError.failed }
            let configuration = NSWorkspace.OpenConfiguration()
            configuration.activates = true
            _ = try await NSWorkspace.shared.open([url], withApplicationAt: app, configuration: configuration)
        case .address(let url):
            let script = PlayerScript(name: "desktop-player.spotify-open", source: PlayerScriptSource.command(kind.bundleID,
                "play track \(ScriptText.quoted(url.absoluteString))"), cacheable: false)
            _ = try await ScriptDesk.shared.run(script, for: .spotify, priority: .command)
        default:
            throw PlayerSearchError.failed
        }
    }

    func queue() async throws -> PlayerQueueSnapshot { PlayerQueueSnapshot() }
    func playQueueItem(_ item: PlayerQueueItem) async throws {}
}

// MARK: - Local-file players

/// VLC, Swinsian and VOX share no library search or queue through their
/// dictionaries: the strip shows just the song playing.
struct NoSearch: PlayerSearching {
    let kind: PlayerKind

    func search(_ query: String) async throws -> [PlayerSearchResult] { throw PlayerSearchError.unsupported(kind) }
    func play(_ result: PlayerSearchResult) async throws { throw PlayerSearchError.unsupported(kind) }
    func queue() async throws -> PlayerQueueSnapshot { PlayerQueueSnapshot() }
    func playQueueItem(_ item: PlayerQueueItem) async throws {}
}

// MARK: - Web players

/// A web player has no search or queue Lunalyr can read, so, as for
/// Spotify, a search opens the service's own search page -- in the browser
/// it plays in, and only when the user picks the result.
struct WebPlayerSearch: PlayerSearching {
    let kind: PlayerKind
    var memory: WebTabMemory = .shared

    /// The service last followed in this browser; YouTube Music before any.
    var service: WebService { memory.entry(for: kind)?.service ?? .youTubeMusic }

    func search(_ query: String) async throws -> [PlayerSearchResult] {
        let trimmed = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard trimmed.count >= 2, let url = service.searchURL(trimmed) else { return [] }
        return [PlayerSearchResult(
            id: "web-search", kind: .song, title: "Search \(service.displayName) for \u{201C}\(trimmed)\u{201D}",
            artist: "Opens in \(kind.shortBrowserName)", album: "", artworkURL: nil, target: .address(url), inLibrary: false)]
    }

    func play(_ result: PlayerSearchResult) async throws {
        // The test host never opens anything in a real browser.
        guard !AppRuntime.isUnitTesting else { throw PlayerSearchError.failed }
        guard case .address(let url) = result.target, url.scheme == "https",
              let app = NSWorkspace.shared.urlForApplication(withBundleIdentifier: kind.bundleID) else { throw PlayerSearchError.failed }
        let configuration = NSWorkspace.OpenConfiguration()
        configuration.activates = true
        _ = try await NSWorkspace.shared.open([url], withApplicationAt: app, configuration: configuration)
    }

    func queue() async throws -> PlayerQueueSnapshot { PlayerQueueSnapshot() }
    func playQueueItem(_ item: PlayerQueueItem) async throws {}
}
