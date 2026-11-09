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
}

/// What a player says is queued: the current playlist around the song, and
/// whether shuffle is on (when it is, that order is not the play order).
struct PlayerQueueSnapshot: Equatable, Sendable {
    var items: [PlayerQueueItem] = []
    var shuffle = false
}

/// An album by the artist now playing, for the cover-flow.
struct PlayerAlbum: Identifiable, Equatable, Sendable {
    let id: String
    var title: String
    var year: String
    var artworkURL: URL?
    var address: URL?
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
    func albums(by artist: String) async -> [PlayerAlbum]
    func play(_ album: PlayerAlbum) async throws
}

extension PlayerKind {
    var searcher: any PlayerSearching {
        switch self {
        case .appleMusic: return AppleMusicSearch()
        case .spotify: return SpotifySearch()
        }
    }
}

/// Runs one-off scripts on a private serial queue: a search must never wait
/// behind (or hold up) the monitor's polling queue.
final class ScriptDesk: @unchecked Sendable {
    static let shared = ScriptDesk()
    private let queue = DispatchQueue(label: "lunalyr.desktop-player.scripts", qos: .userInitiated)
    private let runner = LiveAppleScriptRunner()

    func run(_ script: PlayerScript, for kind: PlayerKind) async throws -> NSAppleEventDescriptor {
        try await withCheckedThrowingContinuation { continuation in
            queue.async {
                switch self.runner.run(script) {
                case .success(let reply):
                    if reply.scriptItems.first?.scriptText == "notRunning" || reply.stringValue == "notRunning" {
                        continuation.resume(throwing: PlayerSearchError.notRunning(kind))
                    } else {
                        continuation.resume(returning: reply)
                    }
                case .failure(let failure):
                    switch PlayerResponse<Bool>(failureCode: failure.code) {
                    case .blocked: continuation.resume(throwing: PlayerSearchError.blocked(kind))
                    case .notRunning: continuation.resume(throwing: PlayerSearchError.notRunning(kind))
                    default: continuation.resume(throwing: PlayerSearchError.failed)
                    }
                }
            }
        }
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
    private static let bundleID = "com.apple.Music"

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
            PlayerScript(name: "desktop-player.library-search", source: source, cacheable: false), for: .appleMusic)
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
        guard var components = URLComponents(string: web), components.host?.hasSuffix("apple.com") == true else { return nil }
        components.scheme = "music"
        return components.url
    }

    /// Plays a song inside the playlist that is playing, so the playlist
    /// carries on after it; a song from elsewhere plays from the library.
    static func playInContext(persistentID id: String) -> String {
        """
        set pl to current playlist
        try
        set theTrack to first track of pl whose persistent ID is \(ScriptText.quoted(id))
        on error
        set theTrack to first track of library playlist 1 whose persistent ID is \(ScriptText.quoted(id))
        end try
        play theTrack
        """
    }

    func play(_ result: PlayerSearchResult) async throws {
        switch result.target {
        case .libraryTrack(let id):
            try await run("play", Self.playInContext(persistentID: id))
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
            PlayerScript(name: "desktop-player.queue", source: source, cacheable: false), for: .appleMusic)
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

    func playQueueItem(_ item: PlayerQueueItem) async throws {
        if let id = item.persistentID {
            try await run("queue-play", Self.playInContext(persistentID: id))
        } else {
            try await run("queue-play", """
            set pl to current playlist
            play track \(item.index) of pl
            """)
        }
    }

    func albums(by artist: String) async -> [PlayerAlbum] {
        await Self.artistAlbums(artist)
    }

    static func artistAlbums(_ artist: String) async -> [PlayerAlbum] {
        let name = artist.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !name.isEmpty else { return [] }
        var components = URLComponents(string: "https://itunes.apple.com/search")
        components?.queryItems = [
            URLQueryItem(name: "term", value: name), URLQueryItem(name: "media", value: "music"),
            URLQueryItem(name: "entity", value: "album"), URLQueryItem(name: "attribute", value: "artistTerm"),
            URLQueryItem(name: "limit", value: "25"),
        ]
        guard let url = components?.url, let (data, _) = try? await URLSession.shared.data(from: url) else { return [] }
        return parseAlbums(data, artist: name)
    }

    static func parseAlbums(_ data: Data, artist: String) -> [PlayerAlbum] {
        guard let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let rows = root["results"] as? [[String: Any]] else { return [] }
        let wanted = artist.lowercased()
        var seen = Set<String>()
        return rows.compactMap { row -> PlayerAlbum? in
            guard let title = row["collectionName"] as? String,
                  let owner = (row["artistName"] as? String)?.lowercased(),
                  owner == wanted || owner.contains(wanted) || wanted.contains(owner),
                  seen.insert(title.lowercased()).inserted else { return nil }
            let art = (row["artworkUrl100"] as? String).flatMap { URL(string: $0.replacingOccurrences(of: "100x100", with: "300x300")) }
            let year = (row["releaseDate"] as? String).map { String($0.prefix(4)) } ?? ""
            let id = (row["collectionId"] as? NSNumber)?.stringValue ?? title
            return PlayerAlbum(id: id, title: title, year: year, artworkURL: art,
                               address: (row["collectionViewUrl"] as? String).flatMap(musicAddress))
        }
    }

    func play(_ album: PlayerAlbum) async throws {
        guard let address = album.address else { throw PlayerSearchError.failed }
        try await open(address)
    }

    private func run(_ name: String, _ body: String) async throws {
        let script = PlayerScript(name: "desktop-player.\(name)", source: PlayerScriptSource.command(Self.bundleID, body),
                                  cacheable: false)
        _ = try await ScriptDesk.shared.run(script, for: .appleMusic)
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
            _ = try await ScriptDesk.shared.run(script, for: .spotify)
        default:
            throw PlayerSearchError.failed
        }
    }

    func queue() async throws -> PlayerQueueSnapshot { PlayerQueueSnapshot() }
    func playQueueItem(_ item: PlayerQueueItem) async throws {}
    func albums(by artist: String) async -> [PlayerAlbum] { [] }
    func play(_ album: PlayerAlbum) async throws {}
}
