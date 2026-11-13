import Foundation

/// Which language a song is sung in, as far as the lyrics found for it say,
/// so lyrics in another language rank as another version.
///
/// Apple Music lists a Japanese song that also has an English recording
/// under its English title in many storefronts ("Racing into the Night" for
/// 夜に駆ける), while the English recording carries "English Version" or
/// similar (`VersionTags.languages`). A title without such a tag is taken to
/// be the original, so lyrics in English only, for a song whose own lyrics
/// are Japanese, are the English version's.
///
/// Only Chinese, Japanese and Korean script is told apart from Latin script:
/// romaji is Latin, and lyrics mixing both (as K-pop often does) are never
/// held against a song.
enum LyricLanguage {
    enum Script: Equatable {
        /// Latin script only (English, or a romanisation).
        case latin
        /// Mostly Chinese, Japanese or Korean script.
        case native
        /// Both, or too little to tell.
        case mixed
    }

    /// Kana, Han or Hangul.
    static func isNative(_ scalar: Unicode.Scalar) -> Bool {
        switch scalar.value {
        case 0x3040...0x30FF, 0x31F0...0x31FF, 0x3400...0x4DBF, 0x4E00...0x9FFF, 0xF900...0xFAFF,
             0xAC00...0xD7AF, 0x1100...0x11FF, 0x3130...0x318F, 0x20000...0x2FA1F: return true
        default: return false
        }
    }

    static func hasNative(_ text: String) -> Bool { text.unicodeScalars.contains(where: isNative) }

    /// The script of sung lines: native when at least four lines and 30% of
    /// them carry native script, Latin when at least four lines have letters
    /// and at most one in twenty carries native script, else mixed. Nil when
    /// there are fewer than four lines to judge.
    static func script(of lines: [String]) -> Script? {
        let sung = lines.filter { $0.unicodeScalars.contains { CharacterSet.letters.contains($0) } }
        guard sung.count >= 4 else { return nil }
        let native = sung.filter(hasNative).count
        if native >= 4, native * 10 >= sung.count * 3 { return .native }
        if native * 20 <= sung.count { return .latin }
        return .mixed
    }

    static func script(of candidate: LyricCandidate) -> Script? {
        switch candidate.result {
        case .synced(let lines, _): return script(of: lines.filter { !$0.isInstrumental && $0.credit != true }.map(\.text))
        case .plain(let lines, _): return script(of: lines)
        }
    }

    /// Genres whose songs are mostly sung in Chinese, Japanese or Korean.
    /// Only a hint: it decides a tie between the lyrics found, and whether
    /// the original title is worth looking up, never on its own.
    static func eastAsianGenre(_ genre: String?) -> Bool {
        guard let genre = genre?.lowercased(), !genre.isEmpty else { return false }
        let words = ["j-pop", "jpop", "j-rock", "jrock", "anime", "japan", "kayokyoku", "enka", "vocaloid",
                     "k-pop", "kpop", "korea", "c-pop", "cpop", "mandopop", "cantopop", "chinese", "visual kei"]
        return words.contains { genre.contains($0) } || hasNative(genre)
    }

    /// The script the playing song's lyrics are written in, when the song
    /// or the lyrics found say so:
    /// - a language tag on the title, or the album ("English Version");
    /// - Music's own lyrics, which belong to the playing track;
    /// - a title or album in native script;
    /// - lyrics found under the song's original title (`OriginalTitles`);
    /// - otherwise the other lyrics found: more native than Latin, or as many
    ///   with an East Asian genre (`eastAsianGenre`). Lyrics whose title names
    ///   a language version do not count: their tag explains them.
    /// Nil when nothing says.
    static func expected(_ query: LyricQuery, candidates: [LyricCandidate]) -> Script? {
        let declared = VersionTags.tags(query.title).intersection(VersionTags.languages)
        let fromAlbum = VersionTags.tags(query.album ?? "").intersection(VersionTags.languages)
        let tags = declared.isEmpty ? fromAlbum : declared
        if !tags.isEmpty { return tags == ["english"] ? .latin : tags.contains("english") ? nil : .native }
        if let music = candidates.first(where: { $0.source == .appleMusicCache }), let script = script(of: music) {
            return script == .mixed ? nil : script
        }
        if hasNative(query.title) || hasNative(query.album ?? "") { return .native }
        var native = 0, latin = 0
        for candidate in candidates where VersionTags.tags(candidate.title).isDisjoint(with: VersionTags.languages) {
            switch script(of: candidate) {
            case .native?:
                if candidate.viaOriginalTitle { return .native }
                native += 1
            case .latin?: latin += 1
            default: break
            }
        }
        if native > latin || (native > 0 && native == latin && eastAsianGenre(query.genre)) { return .native }
        return nil
    }

    /// Sets `languageMismatch` on each candidate: its lyrics are Latin only
    /// where the song's are native, or native where they are Latin. Music's
    /// own lyrics and local files (chosen for the track) are never marked.
    static func weigh(_ candidates: [LyricCandidate], query: LyricQuery) -> [LyricCandidate] {
        let expected = expected(query, candidates: candidates)
        return candidates.map { candidate in
            var marked = candidate
            marked.languageMismatch = false
            guard let expected, !candidate.source.isLocal, let script = script(of: candidate) else { return marked }
            marked.languageMismatch = expected == .native ? script == .latin : script == .native
            return marked
        }
    }
}

/// A song's title in its original language ("夜に駆ける"), for a song Apple
/// Music lists under an English or romanised one ("Racing into the Night"),
/// from Apple's public catalogue: the song is found in this Mac's storefront
/// by title, artist and length, then the same catalogue entry is read from
/// the Japanese storefront.
///
/// Asked only when a lyric service found nothing usable under the listed
/// title (`LyricsFetcher.withOriginalTitle`). Only the title and artist are
/// sent, as for cover art. Requests are spaced a second apart, every
/// answer, "none" too, is kept on disk for 30 days, and sources asking at
/// once share one request.
actor OriginalTitles {
    static let shared = OriginalTitles()
    /// Shares the "Search under other names" setting.
    static var enabled: Bool { ArtistAliases.enabled }

    struct Found: Codable, Equatable {
        var title: String
        var album: String?
    }
    private struct Entry: Codable { var found: Found?; var storedAt: Date }
    private let file: URL
    private let session: URLSession
    private let clock: @Sendable () -> Date
    private let storefront: String
    private let spacing: TimeInterval
    private var entries: [String: Entry]?
    private var inFlight: [String: Task<Found??, Never>] = [:]
    private var lastRequest = Date.distantPast
    static let lifetime: TimeInterval = 30 * 86400
    /// Where original titles are read: Japan, in Japanese.
    static let original = (country: "jp", language: "ja_jp")

    init(folder: URL = SavedLyrics.defaultFolder.deletingLastPathComponent(), session: URLSession = .shared,
         storefront: String = Locale.current.region?.identifier.lowercased() ?? "us", spacing: TimeInterval = 1,
         clock: @escaping @Sendable () -> Date = { Date() }) {
        file = folder.appendingPathComponent("OriginalTitles.json")
        self.session = session
        self.storefront = storefront
        self.spacing = spacing
        self.clock = clock
    }

    static func key(_ query: LyricQuery) -> String {
        [LyricQuery.normalize(query.title), LyricQuery.normalize(query.artist), String(Int(query.duration.rounded()))]
            .joined(separator: "|")
    }

    /// The original title (and album) of the playing song, nil when it has
    /// none other than the one listed, or it could not be found.
    func original(for query: LyricQuery) async -> Found? {
        guard query.hasArtist, query.duration > 0, !LyricLanguage.hasNative(query.title),
              storefront != Self.original.country else {
            LyricTrace.note("original title not looked up (storefront \(storefront), or nothing to look up)")
            return nil
        }
        let key = Self.key(query)
        if let entry = load()[key], clock().timeIntervalSince(entry.storedAt) < Self.lifetime {
            LyricTrace.note("original title from its cache: " + (entry.found.map { "“\($0.title)”" } ?? "none"))
            return entry.found
        }
        if let running = inFlight[key] { return await running.value ?? nil }
        let task = Task { await self.request(query) }
        inFlight[key] = task
        let found = await task.value
        inFlight[key] = nil
        // A failed request is not remembered, so it is asked again next time.
        guard let found else { return nil }
        store(key, found)
        return found
    }

    /// Nil when a request failed; `.some(nil)` when the song has no other title.
    private func request(_ query: LyricQuery) async -> Found?? {
        let term = ArtworkProvider.primaryArtist(query.artist) + " " + query.title
        guard let listed = await fetch("search", ["term": term, "entity": "song", "country": storefront, "limit": "15"]) else { return nil }
        LyricTrace.results("iTunes \(storefront) search", returned: listed.count, read: listed.count)
        guard let match = Self.match(listed, query: query), let id = match.trackId else {
            LyricTrace.note("iTunes: no catalogue entry with this title, artist and length (±3 s)")
            return .some(nil)
        }
        guard let local = await fetch("lookup", ["id": String(id), "country": Self.original.country,
                                                  "lang": Self.original.language]) else { return nil }
        let name = Self.originalName(local.first { $0.trackId == id }, query: query)
        LyricTrace.note("iTunes JP name for “\(match.trackName ?? "")”: " + (name.map { "“\($0.title)”" } ?? "the same, or not native script"))
        return .some(name)
    }

    struct Reply: Decodable { var results: [Song] }
    struct Song: Decodable {
        var trackId: Int?
        var trackName: String?
        var artistName: String?
        var collectionName: String?
        var trackTimeMillis: Int?
    }

    /// The catalogue entry for the playing song: the same title and artist,
    /// and the length nearest the playing one, within 3 s.
    static func match(_ results: [Song], query: LyricQuery) -> Song? {
        results.filter { song in
            guard let name = song.trackName, song.trackId != nil,
                  LyricQuery.similarity(name, query.title) >= 0.95,
                  VersionTags.tags(name) == VersionTags.tags(query.title),
                  ArtworkProvider.artistMatches(song.artistName, wanted: query.artist) else { return false }
            guard let millis = song.trackTimeMillis, millis > 0 else { return true }
            return abs(Double(millis) / 1000 - query.duration) <= 3
        }.min { distance($0, query) < distance($1, query) }
    }

    private static func distance(_ song: Song, _ query: LyricQuery) -> Double {
        song.trackTimeMillis.map { abs(Double($0) / 1000 - query.duration) } ?? 3
    }

    /// The entry's name in the original storefront, when it is another name
    /// written in native script.
    static func originalName(_ song: Song?, query: LyricQuery) -> Found? {
        guard let name = song?.trackName?.trimmingCharacters(in: .whitespacesAndNewlines), !name.isEmpty,
              LyricLanguage.hasNative(name), LyricQuery.similarity(name, query.title) < 0.95 else { return nil }
        let album = song?.collectionName?.trimmingCharacters(in: .whitespacesAndNewlines)
        return Found(title: name, album: album?.isEmpty == false ? album : nil)
    }

    private func fetch(_ path: String, _ parameters: [String: String]) async -> [Song]? {
        // Spaced out: Apple's catalogue limits how often one client may ask.
        let now = clock(), slot = max(now, lastRequest.addingTimeInterval(spacing))
        lastRequest = slot
        let wait = slot.timeIntervalSince(now)
        if wait > 0 { try? await Task.sleep(nanoseconds: UInt64(wait * 1_000_000_000)) }
        guard !Task.isCancelled else { return nil }
        var parts = URLComponents(string: "https://itunes.apple.com/" + path)!
        parts.queryItems = parameters.sorted { $0.key < $1.key }.map { URLQueryItem(name: $0.key, value: $0.value) }
        guard let url = parts.url,
              let (data, response) = try? await LyricTrace.data(session, for: URLRequest(url: url, timeoutInterval: 4)),
              (response as? HTTPURLResponse)?.statusCode == 200, data.count <= 2_000_000,
              let reply = try? JSONDecoder().decode(Reply.self, from: data) else { return nil }
        return reply.results
    }

    private func load() -> [String: Entry] {
        if let entries { return entries }
        let read = (try? Data(contentsOf: file)).flatMap { try? JSONDecoder().decode([String: Entry].self, from: $0) } ?? [:]
        entries = read
        return read
    }

    private func store(_ key: String, _ found: Found?) {
        var all = load().filter { clock().timeIntervalSince($0.value.storedAt) < Self.lifetime }
        all[key] = Entry(found: found, storedAt: clock())
        if all.count > 2000 {
            for old in all.sorted(by: { $0.value.storedAt < $1.value.storedAt }).prefix(all.count - 2000) { all[old.key] = nil }
        }
        entries = all
        guard let data = try? JSONEncoder().encode(all) else { return }
        try? FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? data.write(to: file, options: .atomic)
    }
}

extension LyricsFetcher {
    /// The sources searched again under a song's original title.
    static let originalTitleSources: Set<LyricSourceID> = [.lrclib, .netEase, .qqMusic, .kugou, .kuwo, .lyricsPlus, .amll]

    /// A source's answer, searched again under the song's original title
    /// (`OriginalTitles`) when the listed title found nothing usable: no
    /// match, a language version ("English Version") the title does not
    /// name, or, for an East Asian genre, lyrics in Latin script only. A
    /// match found that way is recorded under the listed title (it is the
    /// same song) and marked `viaOriginalTitle`. A title that names a
    /// language version, or is already in native script, is not searched again.
    static func withOriginalTitle(_ id: LyricSourceID, query: LyricQuery, titles: OriginalTitles = .shared,
                                  enabled: Bool = OriginalTitles.enabled,
                                  search: (LyricQuery) async throws -> LyricCandidate?) async throws -> LyricCandidate? {
        LyricTrace.emit(LyricTraceEvent(kind: .variant, message: "listed title", query: LyricTraceQuery(query)))
        let first = try await search(query)
        guard enabled, originalTitleSources.contains(id), query.hasArtist, query.duration > 0,
              !LyricLanguage.hasNative(query.title),
              VersionTags.tags(query.title).isDisjoint(with: VersionTags.languages),
              !usable(first, query: query) else {
            if LyricTrace.isOn {
                let why = !enabled ? "original-title search is off"
                    : !originalTitleSources.contains(id) ? "this source is not searched under original titles"
                    : !query.hasArtist ? "no artist" : query.duration <= 0 ? "no duration"
                    : LyricLanguage.hasNative(query.title) ? "title already in native script"
                    : !VersionTags.tags(query.title).isDisjoint(with: VersionTags.languages) ? "title names a language version"
                    : "listed-title answer is usable"
                LyricTrace.candidate(first, query: query, kind: .variantResult, message: "kept; no original-title search: " + why)
            }
            return first
        }
        LyricTrace.candidate(first, query: query, kind: .variantResult,
                             message: first == nil ? "nothing under the listed title: looking up the original title"
                                : "not usable (language version or Latin-only lyrics): looking up the original title")
        guard let original = await titles.original(for: query) else {
            LyricTrace.note("no original title found (iTunes JP): listed-title answer kept")
            return first
        }
        try Task.checkCancellation()
        var other = query
        other.title = original.title
        other.album = original.album ?? query.album
        return try await LyricTrace.within(variant: "original title “\(original.title)”") {
            LyricTrace.emit(LyricTraceEvent(kind: .variant, message: "original title", query: LyricTraceQuery(other)))
            let second = try await search(other)
            guard var found = second,
                  VersionTags.mismatch(other, title: found.title).isEmpty,
                  !found.metadataReported || LyricQuery.similarity(found.title, original.title) >= 0.65,
                  LyricLanguage.script(of: found) != .latin else {
                if LyricTrace.isOn {
                    let why = second.map { found in
                        !VersionTags.mismatch(other, title: found.title).isEmpty ? "another version under the original title"
                            : found.metadataReported && LyricQuery.similarity(found.title, original.title) < 0.65 ? "title too far from the original"
                            : "lyrics in Latin script only"
                    } ?? "nothing under the original title"
                    LyricTrace.candidate(second, query: other, kind: .variantResult, message: "discarded: \(why); listed-title answer kept")
                }
                return first
            }
            LyricTrace.candidate(found, query: other, kind: .variantResult, message: "used; recorded under the listed title")
            found.title = query.title
            found.viaOriginalTitle = true
            return found
        }
    }

    /// Whether a source's answer under the listed title can stand: it names
    /// no language version the song does not, and, for an East Asian genre,
    /// its lyrics are not in Latin script only.
    private static func usable(_ candidate: LyricCandidate?, query: LyricQuery) -> Bool {
        guard let candidate else { return false }
        if !VersionTags.mismatch(query, title: candidate.title).isDisjoint(with: VersionTags.languages) { return false }
        if LyricLanguage.eastAsianGenre(query.genre), LyricLanguage.script(of: candidate) == .latin { return false }
        return true
    }
}
