import Foundation

/// An artist's names in other scripts ("米津玄師" for "Kenshi Yonezu"), from
/// MusicBrainz, so the Chinese catalogues (NetEase, QQ, Kugou, Kuwo), which
/// often index only the native name, can be searched again under it.
///
/// Asked only when such a source found nothing, or only another artist, under
/// the library's name, or when a song's original title may be worth looking
/// up (`origin`). One request per artist, at most one a second (as
/// MusicBrainz asks), and every answer, empty ones too, is kept on disk for
/// 30 days, so a song played again asks nothing.
///
/// The same answer gives the artist's country, so a Japanese, Korean or
/// Chinese artist listed under a Latin name ("YOASOBI") is known as one.
actor ArtistAliases {
    static let shared = ArtistAliases()
    /// Whether the lookup may run ("Search under other names").
    static var enabled: Bool { UserDefaults.standard.object(forKey: "custom.artistAliases") as? Bool ?? true }
    /// The sources searched again under another name.
    static let catalogues: Set<LyricSourceID> = [.netEase, .qqMusic, .kugou, .kuwo]

    private struct Entry: Codable {
        var names: [String]
        var storedAt: Date
        /// The artist's country code ("JP"); nil when MusicBrainz gave none.
        var country: String? = nil
        /// False in entries stored before the country was kept, which are
        /// asked again when the country is wanted.
        var hasCountry: Bool? = nil
    }
    private let file: URL
    private let session: URLSession
    private let clock: @Sendable () -> Date
    private var entries: [String: Entry]?
    private var inFlight: [String: Task<Entry?, Never>] = [:]
    private var lastRequest = Date.distantPast
    static let lifetime: TimeInterval = 30 * 86400

    init(folder: URL = SavedLyrics.defaultFolder.deletingLastPathComponent(), session: URLSession = .shared,
         clock: @escaping @Sendable () -> Date = { Date() }) {
        file = folder.appendingPathComponent("ArtistAliases.json")
        self.session = session
        self.clock = clock
    }

    /// Up to two other names for `artist`, native scripts first. Empty when
    /// MusicBrainz has none, or cannot tell which artist this is.
    func names(for artist: String) async -> [String] {
        await entry(for: artist, needsCountry: false)?.names ?? []
    }

    /// The countries whose artists mostly sing in Chinese, Japanese or Korean.
    static let eastAsianCountries: Set<String> = ["JP", "KR", "CN", "TW", "HK", "MO"]
    private static let eastAsianAreas: Set<String> = ["japan", "south korea", "korea", "china", "taiwan", "hong kong", "macao", "macau"]

    /// Whether MusicBrainz places the artist in Japan, Korea, China, Taiwan
    /// or Hong Kong. False when it cannot tell which artist this is, or the
    /// request failed. Shares the request and the stored answer with `names`.
    func isEastAsian(_ artist: String) async -> Bool {
        guard let country = await entry(for: artist, needsCountry: true)?.country else { return false }
        return Self.eastAsianCountries.contains(country.uppercased())
    }

    private func entry(for artist: String, needsCountry: Bool) async -> Entry? {
        let key = LyricQuery.normalize(artist)
        guard !key.isEmpty else { return nil }
        if let entry = load()[key], clock().timeIntervalSince(entry.storedAt) < Self.lifetime,
           !needsCountry || entry.hasCountry == true {
            LyricTrace.note("MusicBrainz answer for “\(artist)” from its cache: \(entry.names), country \(entry.country ?? "unknown")")
            return entry
        }
        if let running = inFlight[key] { return await running.value }
        let task = Task { await self.request(artist) }
        inFlight[key] = task
        let found = await task.value
        inFlight[key] = nil
        // A failed request (nil) is not remembered, so it is asked again next time.
        guard let found else { return nil }
        store(key, found)
        return found
    }

    private func request(_ artist: String) async -> Entry? {
        // MusicBrainz allows one request a second from each client.
        // Each request takes the next free slot before waiting, so two artists asked at once still queue.
        let now = clock(), slot = max(now, lastRequest.addingTimeInterval(1.1))
        lastRequest = slot
        let wait = slot.timeIntervalSince(now)
        if wait > 0 { try? await Task.sleep(nanoseconds: UInt64(wait * 1_000_000_000)) }
        var parts = URLComponents(string: "https://musicbrainz.org/ws/2/artist")!
        let quoted = "\"" + artist.replacingOccurrences(of: "\\", with: "").replacingOccurrences(of: "\"", with: "") + "\""
        parts.queryItems = [.init(name: "query", value: quoted), .init(name: "limit", value: "5"), .init(name: "fmt", value: "json")]
        guard let url = parts.url else { return nil }
        var call = URLRequest(url: url, timeoutInterval: 4)
        call.setValue("Lunalyr/1.0 (desktop lyrics app)", forHTTPHeaderField: "User-Agent")
        guard let (data, response) = try? await LyricTrace.data(session, for: call),
              (response as? HTTPURLResponse)?.statusCode == 200, data.count <= 1_000_000,
              let reply = try? JSONDecoder().decode(Reply.self, from: data) else { return nil }
        let names = Self.pick(reply, for: artist), country = Self.country(reply, for: artist)
        LyricTrace.results("MusicBrainz artists", returned: reply.artists.count, read: reply.artists.count)
        LyricTrace.note("MusicBrainz aliases for “\(artist)”: \(names), country \(country ?? "unknown")")
        return Entry(names: names, storedAt: clock(), country: country, hasCountry: true)
    }

    struct Reply: Decodable {
        struct Artist: Decodable {
            struct Alias: Decodable { var name: String; var locale: String?; var primary: Bool? }
            struct Area: Decodable { var name: String? }
            var name: String
            var score: Int?
            var aliases: [Alias]?
            var country: String?
            var area: Area?
        }
        var artists: [Artist]
    }

    /// The other names of the one artist that `artist` names. Two strong
    /// matches mean two artists share the name, and nothing is guessed.
    static func pick(_ reply: Reply, for artist: String) -> [String] {
        guard let found = only(reply, for: artist) else { return [] }
        let native: Set<String> = ["ja", "zh", "zh_Hans", "zh_Hant", "zh_TW", "zh_CN", "zh_HK", "ko"]
        var ranked: [(name: String, rank: Int)] = [(found.name, 1)]
        for alias in found.aliases ?? [] {
            let locale = alias.locale ?? ""
            ranked.append((alias.name, native.contains(locale) ? (alias.primary == true ? 0 : 1) : 2))
        }
        var names: [String] = []
        for entry in ranked.sorted(by: { $0.rank < $1.rank }) {
            let name = entry.name.trimmingCharacters(in: .whitespacesAndNewlines)
            // Only names the library's spelling could not already find: another script.
            guard !name.isEmpty, LyricQuery.similarity(name, artist) < 0.65, isOtherScript(name),
                  !names.contains(where: { LyricQuery.normalize($0) == LyricQuery.normalize(name) }) else { continue }
            names.append(name)
            if names.count == 2 { break }
        }
        return names
    }

    /// The one artist that `artist` names, as `pick` finds it.
    private static func only(_ reply: Reply, for artist: String) -> Reply.Artist? {
        func named(_ found: Reply.Artist) -> Bool {
            ([found.name] + (found.aliases ?? []).map(\.name)).contains { LyricQuery.similarity($0, artist) >= 0.95 }
        }
        let matches = reply.artists.filter { ($0.score ?? 0) >= 90 && named($0) }
        return matches.count == 1 ? matches.first : nil
    }

    /// The country code of the one artist that `artist` names, or its area
    /// when that is a country in East Asia. Nil when MusicBrainz cannot tell.
    static func country(_ reply: Reply, for artist: String) -> String? {
        guard let found = only(reply, for: artist) else { return nil }
        if let code = found.country?.trimmingCharacters(in: .whitespaces), !code.isEmpty { return code.uppercased() }
        let area = found.area?.name?.lowercased() ?? ""
        guard eastAsianAreas.contains(area) else { return nil }
        switch area {
        case "japan": return "JP"
        case "china": return "CN"
        case "taiwan": return "TW"
        case "hong kong": return "HK"
        case "macao", "macau": return "MO"
        default: return "KR"
        }
    }

    /// Written in Chinese, Japanese or Korean script.
    static func isOtherScript(_ name: String) -> Bool {
        name.unicodeScalars.contains { (0x3040...0x30FF).contains($0.value) || (0x3400...0x9FFF).contains($0.value)
            || (0xAC00...0xD7AF).contains($0.value) || (0xF900...0xFAFF).contains($0.value) }
    }

    private func load() -> [String: Entry] {
        if let entries { return entries }
        let read = (try? Data(contentsOf: file)).flatMap { try? JSONDecoder().decode([String: Entry].self, from: $0) } ?? [:]
        entries = read
        return read
    }

    private func store(_ key: String, _ entry: Entry) {
        var all = load().filter { clock().timeIntervalSince($0.value.storedAt) < Self.lifetime }
        all[key] = entry
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
    /// A catalogue source's answer, searched again under the artist's other
    /// names when the library's name found nothing, or only another artist.
    /// A match found that way is recorded under the library's artist, since
    /// it matched the same artist's other name.
    static func withAliases(_ id: LyricSourceID, query: LyricQuery, aliases: ArtistAliases = .shared, enabled: Bool = ArtistAliases.enabled,
                            search: (LyricQuery) async throws -> LyricCandidate?) async throws -> LyricCandidate? {
        let first = try await search(query)
        guard enabled, ArtistAliases.catalogues.contains(id), query.hasArtist, !ArtistAliases.isOtherScript(query.artist) else {
            if LyricTrace.isOn, enabled, ArtistAliases.catalogues.contains(id) {
                LyricTrace.note(query.hasArtist ? "no alias search: artist already in native script" : "no alias search: no artist")
            }
            return first
        }
        if let first, !first.metadataReported || query.artistScore(first.artist) >= 0.65 { return first }
        LyricTrace.candidate(first, query: query, kind: .variantResult,
                             message: first == nil ? "nothing under the library artist: trying aliases" : "only another artist: trying aliases")
        let names = await aliases.names(for: query.artist)
        if names.isEmpty { LyricTrace.note("no artist aliases to try") }
        for name in names {
            try Task.checkCancellation()
            var other = query
            other.artist = name
            let result = try await LyricTrace.within(variant: "artist alias “\(name)”") { () -> LyricCandidate? in
                LyricTrace.emit(LyricTraceEvent(kind: .variant, message: "artist alias", query: LyricTraceQuery(other)))
                let found = try await search(other)
                let fits = found.map { LyricQuery.similarity($0.artist, name) >= 0.65 } ?? false
                LyricTrace.candidate(found, query: other, kind: .variantResult,
                                     message: fits ? "used; recorded under the library artist"
                                        : found == nil ? "nothing under this alias" : "discarded: another artist")
                return fits ? found : nil
            }
            guard var found = result else { continue }
            found.artist = query.artist
            return found
        }
        return first
    }
}
