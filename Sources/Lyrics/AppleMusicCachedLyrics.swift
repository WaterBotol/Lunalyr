import Foundation
import SQLite3

/// Reads only response bodies for Music's song requests that included lyrics.
enum AppleMusicCachedLyrics {
    static let sourceName = "Apple Music · local cache"
    static let maxBodyBytes = 4_000_000

    private static let reader = Reader()

    static func lookup(_ query: LyricQuery) async -> LyricCandidate? {
        await reader.lookup(query)
    }

    private actor Reader {
        private var reading = false

        func lookup(_ query: LyricQuery) async -> LyricCandidate? {
            // Files belonging to another app can wait on macOS privacy UI.
            // Let the regular providers proceed, with at most one such read.
            guard !reading else { return nil }
            reading = true
            let (values, result) = AsyncStream<LyricCandidate?>.makeStream()
            DispatchQueue.global(qos: .utility).async {
                result.yield(AppleMusicCachedLyrics.candidate(query))
                result.finish()
                Task { await self.finished() }
            }
            let deadline = Task {
                try? await Task.sleep(for: .milliseconds(650))
                result.finish()
            }
            defer { deadline.cancel() }
            var iterator = values.makeAsyncIterator()
            return await iterator.next() ?? nil
        }

        private func finished() { reading = false }
    }

    struct Envelope: Decodable {
        let data: [Song]
    }

    struct Song: Decodable {
        let id: String
        let type: String
        let attributes: Metadata
        let relationships: [String: Relationship]?

        struct Metadata: Decodable {
            let name: String
            let artistName: String
            let albumName: String
            let durationInMillis: Int
        }

        struct Relationship: Decodable {
            let data: [Resource]?
        }

        struct Resource: Decodable {
            let type: String
            let attributes: Lyrics?
        }

        struct Lyrics: Decodable {
            let ttmlLocalizations: String?
        }
    }

    static func candidate(_ query: LyricQuery, directory: URL = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Caches/com.apple.Music", isDirectory: true)) -> LyricCandidate? {
        var db: OpaquePointer?
        let path = directory.appendingPathComponent("Cache.db").path
        guard sqlite3_open_v2(path, &db, SQLITE_OPEN_READONLY, nil) == SQLITE_OK, let db else {
            if let db { sqlite3_close(db) }
            return nil
        }
        defer { sqlite3_close(db) }
        sqlite3_busy_timeout(db, 150)

        let sql = """
            SELECT d.isDataOnFS, d.receiver_data
            FROM cfurl_cache_response r
            JOIN cfurl_cache_receiver_data d USING(entry_ID)
            WHERE r.request_key LIKE 'https://amp-api.music.apple.com/v1/catalog/%/songs%'
              AND r.request_key LIKE '%syllable-lyrics%'
              AND length(d.receiver_data) BETWEEN 1 AND 4000000
            ORDER BY r.time_stamp DESC, r.entry_ID DESC
            LIMIT 180
            """
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &statement, nil) == SQLITE_OK, let statement else { return nil }
        defer { sqlite3_finalize(statement) }

        let bodyDirectory = directory.appendingPathComponent("fsCachedData", isDirectory: true)
        var matches: [Song] = []
        var state = sqlite3_step(statement)
        while state == SQLITE_ROW {
            guard !Task.isCancelled else { return nil }
            if let body = responseBody(statement, directory: bodyDirectory),
               let payload = try? JSONDecoder().decode(Envelope.self, from: body) {
                matches.append(contentsOf: payload.data.filter { matchesRecording($0, query: query) })
            }
            state = sqlite3_step(statement)
        }
        guard state == SQLITE_DONE else { return nil }
        return candidate(query, songs: matches)
    }

    private static func responseBody(_ statement: OpaquePointer, directory: URL) -> Data? {
        if sqlite3_column_int(statement, 0) == 0 {
            let length = Int(sqlite3_column_bytes(statement, 1))
            guard (1...maxBodyBytes).contains(length), let bytes = sqlite3_column_blob(statement, 1) else { return nil }
            return Data(bytes: bytes, count: length)
        }
        guard sqlite3_column_bytes(statement, 1) == 36, let raw = sqlite3_column_text(statement, 1) else { return nil }
        let filename = String(cString: raw)
        guard UUID(uuidString: filename) != nil else { return nil }
        let file = directory.appendingPathComponent(filename)
        guard let metadata = try? file.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey, .fileSizeKey]),
              metadata.isRegularFile == true, metadata.isSymbolicLink != true,
              let size = metadata.fileSize, (1...maxBodyBytes).contains(size),
              let stream = try? FileHandle(forReadingFrom: file) else { return nil }
        defer { try? stream.close() }
        guard let data = try? stream.read(upToCount: maxBodyBytes + 1), data.count <= maxBodyBytes else { return nil }
        return data
    }

    /// How surely a cached song is the playing recording: 2 for the same
    /// title, artist and album, 1 when the names differ only in ways that
    /// keep the recording (no album reported, " - Single" or a deluxe or
    /// remastered album, a feat. credit), 0 otherwise. Length must agree
    /// within 1.5 s either way, and a version tag (Live, Remix…) never may differ.
    static func matchLevel(_ song: Song, query: LyricQuery) -> Int {
        let metadata = song.attributes
        let duration = Double(metadata.durationInMillis) / 1000
        guard song.type == "songs", !song.id.isEmpty, query.duration > 0, duration > 0,
              abs(duration - query.duration) <= 1.5 else { return 0 }
        let album = query.album.map(LyricQuery.normalize) ?? ""
        if exact(metadata.name, query.title), exact(metadata.artistName, query.artist),
           !album.isEmpty, exact(metadata.albumName, query.album ?? "") { return 2 }
        guard VersionTags.tags(metadata.name) == VersionTags.tags(query.title),
              LyricQuery.similarity(metadata.name, query.title) >= 0.95,
              LyricQuery.similarity(metadata.artistName, query.artist) >= 0.95 else { return 0 }
        if album.isEmpty { return 1 }
        let mine = albumCore(query.album ?? ""), theirs = albumCore(metadata.albumName)
        return !mine.isEmpty && mine == theirs && VersionTags.tags(metadata.albumName) == VersionTags.tags(query.album ?? "") ? 1 : 0
    }

    private static func matchesRecording(_ song: Song, query: LyricQuery) -> Bool { matchLevel(song, query: query) > 0 }

    private static func exact(_ a: String, _ b: String) -> Bool {
        let left = LyricQuery.normalize(a)
        return !left.isEmpty && left == LyricQuery.normalize(b)
    }

    private static let editionWords: Set<String> = ["deluxe", "edition", "remaster", "remastered", "expanded",
                                                    "anniversary", "bonus", "version", "special", "single", "ep"]
    private static let albumBracket = try! NSRegularExpression(pattern: #"\s*[(\[]([^)\]]*)[)\]]"#)
    private static let albumSuffix = try! NSRegularExpression(pattern: #"\s+-\s+(single|ep)\s*$"#, options: .caseInsensitive)

    /// The album name without its release kind and edition (" - Single",
    /// "(Deluxe Edition)", "[2011 Remaster]"), normalised.
    static func albumCore(_ name: String) -> String {
        let value = NSMutableString(string: name)
        albumSuffix.replaceMatches(in: value, range: NSRange(location: 0, length: value.length), withTemplate: "")
        for match in albumBracket.matches(in: value as String, range: NSRange(location: 0, length: value.length)).reversed() {
            let words = LyricQuery.normalize(value.substring(with: match.range(at: 1))).split(separator: " ").map(String.init)
            if words.contains(where: editionWords.contains) { value.deleteCharacters(in: match.range) }
        }
        return LyricQuery.normalize(value as String)
    }

    static func candidate(_ query: LyricQuery, songs: [Song]) -> LyricCandidate? {
        // The surest level that names exactly one song wins; two songs at the
        // surest level found are ambiguous, and nothing is guessed.
        let levels = songs.map { matchLevel($0, query: query) }
        guard let top = levels.max(), top > 0 else { return nil }
        let matches = zip(songs, levels).filter { $0.1 == top }.map(\.0)
        guard Set(matches.map(\.id)).count == 1 else { return nil }
        for song in matches {
            for resource in song.relationships?["syllable-lyrics"]?.data ?? [] where resource.type == "syllable-lyrics" {
                guard let ttml = resource.attributes?.ttmlLocalizations else { continue }
                let lines = TTMLLyricParser.parse(ttml, duration: query.duration)
                guard lines.contains(where: { !$0.isInstrumental && !$0.words.isEmpty }),
                      let end = lines.map(\.end).max(), end <= query.duration + 3 else { continue }
                let provenance = LyricsFetcher.Provenance(precision: LyricsFetcher.precision(lines), source: sourceName)
                let metadata = song.attributes
                return LyricCandidate(source: .appleMusicCache, result: .synced(lines, provenance),
                    title: metadata.name, artist: metadata.artistName,
                    duration: Double(metadata.durationInMillis) / 1000, score: 120)
            }
        }
        return nil
    }
}
