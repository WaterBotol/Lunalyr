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
    case failed
}

/// Searching and playing, one implementation per player. Everything runs
/// off the main thread; callers hop back to publish.
protocol PlayerSearching: Sendable {
    var kind: PlayerKind { get }
    func search(_ query: String) async throws -> [PlayerSearchResult]
    func play(_ result: PlayerSearchResult) async throws
    func queue() async throws -> PlayerQueueSnapshot
    func playQueueItem(_ item: PlayerQueueItem) async throws
}

extension PlayerKind {
    var searcher: any PlayerSearching {
        switch self {
        case .appleMusic: return AppleMusicSearch()
        case .spotify: return SpotifySearch()
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
            default: return .failure(PlayerSearchError.failed)
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
}

/// Runs async jobs one after another, in the order they were started.
final class SerialFlow: @unchecked Sendable {
    private let lock = NSLock()
    private var tail: Task<Void, Never>?

    func run(_ work: @escaping @Sendable () async throws -> Void) async throws {
        let task: Task<Void, Error> = lock.withLock {
            let previous = tail
            let task = Task { _ = await previous?.value; try await work() }
            tail = Task { _ = try? await task.value }
            return task
        }
        try await task.value
    }
}

/// Escapes text for an AppleScript string literal.
enum ScriptText {
    static func quoted(_ text: String) -> String {
        let cleaned = text.replacingOccurrences(of: "\\", with: "\\\\")
            .replacingOccurrences(of: "\"", with: "\\\"")
            .replacingOccurrences(of: "\n", with: " ")
            .replacingOccurrences(of: "\r", with: " ")
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
    /// A song that is not in the playing playlist plays from the library.
    ///
    /// Only playlists Lunalyr made are ever deleted: their persistent IDs are
    /// kept (`QueuePlaylistStore`), and one is deleted only while it is still
    /// a plain user playlist (not a folder, not smart) with exactly the name
    /// Lunalyr gave it, case included.
    static let queuePlaylistName = "Lunalyr Queue"
    static let buildingPlaylistName = queuePlaylistName + " (new)"
    /// Songs copied at most.
    static let queueLength = 150

    var store = QueuePlaylistStore()

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

    /// Step one. Plays the song directly when a queue cannot (or should not)
    /// be built; otherwise clears up any half-built queue Lunalyr left behind,
    /// makes a new one and returns its persistent ID.
    ///
    /// Only a source made of library songs is copied. `duplicate` into a user
    /// playlist adds a song that is not in the library to it (with Music's
    /// default "Add songs to Library when adding to playlists"), so an Apple
    /// Music playlist, radio or a catalogue song falls back to a plain `play`.
    /// Not verified live: the hard rules forbid driving Music, so how Music
    /// treats every kind of source here is untested.
    static func prepareQueue(persistentID id: String, index: Int?, leftovers: [String]) -> String {
        let cleanUp = leftovers.map { deleteOwnPlaylist($0, named: buildingPlaylistName) }.joined(separator: "\n")
        return """
        set inSource to false
        try
        \(locate(id, index: index))
        on error number errorNumber
        if errorNumber is not in \(PlayerScriptSource.tolerated) then error number errorNumber
        end try
        set inLibrary to false
        try
        set libTrack to first track of library playlist 1 whose persistent ID is \(ScriptText.quoted(id))
        set inLibrary to true
        on error number errorNumber
        if errorNumber is not in \(PlayerScriptSource.tolerated) then error number errorNumber
        end try
        set queueable to false
        if inSource and inLibrary then
        set sourceClass to class of src
        if sourceClass is library playlist or sourceClass is user playlist then set queueable to true
        end if
        if not queueable then
        if inSource then
        play srcTrack
        else if inLibrary then
        play libTrack
        else
        error number -1728
        end if
        return {"ok", "played"}
        end if
        \(cleanUp)
        set newList to make new user playlist with properties {name:\(ScriptText.quoted(buildingPlaylistName))}
        return {"ok", "made", persistent ID of newList}
        """
    }

    /// Step two. Copies the song and the ones after it (wrapping round, up
    /// to `queueLength`) into the new playlist, each as the library's own
    /// copy, so nothing is added to the library. A failure deletes the
    /// half-built playlist and then raises the error. Then the previous
    /// queue is deleted, the new one takes its name and plays. Replies
    /// `{"ok", n}`, where n is a playback error number, or 0.
    static func buildQueue(persistentID id: String, index: Int?, into newID: String, replacing oldID: String?) -> String {
        let replace = oldID.map { deleteOwnPlaylist($0, named: queuePlaylistName) } ?? ""
        return """
        set newList to first user playlist whose persistent ID is \(ScriptText.quoted(newID))
        set wasShuffle to shuffle enabled
        try
        set inSource to false
        \(locate(id, index: index))
        set n to index of srcTrack
        set total to count of tracks of src
        set endAt to n + \(queueLength - 1)
        if endAt > total then set endAt to total
        set wanted to (persistent ID of tracks n thru endAt of src) as list
        set room to \(queueLength) - (endAt - n + 1)
        if n > 1 and room > 0 then
        set upTo to n - 1
        if upTo > room then set upTo to room
        set wanted to wanted & ((persistent ID of tracks 1 thru upTo of src) as list)
        end if
        repeat with idItem in wanted
        set wantedID to contents of idItem
        try
        duplicate (first track of library playlist 1 whose persistent ID is wantedID) to newList
        on error number errorNumber
        if errorNumber is not in \(PlayerScriptSource.tolerated) then error number errorNumber
        end try
        end repeat
        on error buildText number buildNumber
        try
        \(deleteOwnPlaylist(newID, named: buildingPlaylistName))
        end try
        error buildText number buildNumber
        end try
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

    /// One click at a time: a second click's clean-up must not delete the
    /// first click's playlist while that one is still being filled.
    private static let queueFlow = SerialFlow()

    func playInContext(persistentID id: String, index: Int?) async throws {
        let store = store
        try await Self.queueFlow.run {
            let prepared = try await ScriptDesk.shared.run(for: .appleMusic, priority: .command) {
                PlayerScript(name: "desktop-player.queue-prepare", source: PlayerScriptSource.query(Self.bundleID,
                    Self.prepareQueue(persistentID: id, index: index, leftovers: store.pendingIDs)), cacheable: false)
            }
            let items = prepared.scriptItems
            guard items.first?.scriptText == "ok" else { throw PlayerSearchError.failed }
            guard items.count >= 3, items[1].scriptText == "made", let newID = items[2].scriptText, !newID.isEmpty else { return }
            // The script deleted every earlier leftover it was given.
            store.pendingIDs = [newID]
            let built = try await ScriptDesk.shared.run(for: .appleMusic, priority: .command) {
                PlayerScript(name: "desktop-player.queue-build", source: PlayerScriptSource.query(Self.bundleID,
                    Self.buildQueue(persistentID: id, index: index, into: newID, replacing: store.queueID)), cacheable: false)
            }
            let reply = built.scriptItems
            guard reply.first?.scriptText == "ok" else { throw PlayerSearchError.failed }
            store.queueID = newID
            store.pendingIDs = []
            if reply.count >= 2, let playError = reply[1].scriptNumber, playError != 0 { throw PlayerSearchError.failed }
        }
    }

    func play(_ result: PlayerSearchResult) async throws {
        switch result.target {
        case .libraryTrack(let id):
            try await playInContext(persistentID: id, index: nil)
        case .address(let url):
            try await open(url)
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
                  let index = fields[3].scriptNumber else { return nil }
            let here = fields.count >= 5 ? fields[4].scriptNumber : nil
            return PlayerQueueItem(id: "q-\(id)-\(Int(index))", title: title, artist: fields[2].scriptText ?? "",
                                   index: Int(index), isCurrent: here == index, persistentID: id)
        }
    }

    /// A song without a library ID (a stream, a radio track) cannot be found
    /// again; the strip does not offer it (`PlayerQueueItem.isPlayable`).
    func playQueueItem(_ item: PlayerQueueItem) async throws {
        guard let id = item.persistentID else { throw PlayerSearchError.failed }
        try await playInContext(persistentID: id, index: item.index > 0 ? item.index : nil)
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
