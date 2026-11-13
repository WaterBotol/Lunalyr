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
/// from Apple's public catalogue:
/// 1. this Mac's storefront is searched for the artist and title. The entry
///    under the listed title, of the playing length, is read from the
///    Japanese storefront, and its name there is the answer (the same name:
///    there is no other). Apple's search also finds a song by its English
///    title where the storefront lists it under the native one, so the
///    artist's first entry for the playing version, when its name is native
///    and its length fits, is the answer too.
/// 2. Without either, the Japanese storefront is searched for the title,
///    and its first entry by the artist for the playing version is taken in
///    the same way. A storefront in Japan starts here.
/// Lengths fit within 3 s, or 2% for a long song, as recordings are matched.
///
/// Asked only when a lyric service found nothing usable under the listed
/// title, or only Latin-script lyrics for an East Asian artist
/// (`LyricsFetcher.withOriginalTitle`). Only the title and artist are
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

    /// v2: the Japanese storefront is searched too, and lengths fit within
    /// 2% (answers stored before, "none" among them, are asked again).
    static func key(_ query: LyricQuery) -> String {
        ["v2", LyricQuery.normalize(query.title), LyricQuery.normalize(query.artist), String(Int(query.duration.rounded()))]
            .joined(separator: "|")
    }

    /// How far a catalogue entry's length may be from the playing one's.
    static func tolerance(_ duration: Double) -> Double { max(3, duration * 0.02) }

    /// The original title (and album) of the playing song, nil when it has
    /// none other than the one listed, or it could not be found.
    func original(for query: LyricQuery) async -> Found? {
        guard query.hasArtist, query.duration > 0, !LyricLanguage.hasNative(query.title) else {
            LyricTrace.note("original title not looked up: nothing to look up")
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
        let window = Int(Self.tolerance(query.duration).rounded())
        var failed = false
        if storefront != Self.original.country {
            let term = ArtworkProvider.primaryArtist(query.artist) + " " + query.title
            if let listed = await fetch("search", ["term": term, "entity": "song", "country": storefront, "limit": "15"]) {
                LyricTrace.results("iTunes \(storefront) search", returned: listed.count, read: listed.count)
                if let match = Self.match(listed, query: query), let id = match.trackId {
                    guard let local = await fetch("lookup", ["id": String(id), "country": Self.original.country,
                                                              "lang": Self.original.language]) else { return nil }
                    let name = Self.originalName(local.first { $0.trackId == id }, query: query)
                    var found = "the same, or not native script"
                    if let name { found = "“\(name.title)”" }
                    LyricTrace.note("iTunes JP name for “\(match.trackName ?? "")”: \(found)")
                    return .some(name)
                }
                if let found = Self.nativeMatch(listed, query: query) {
                    LyricTrace.note("iTunes \(storefront): the song is listed as “\(found.title)”")
                    return .some(found)
                }
                LyricTrace.note("iTunes \(storefront): no catalogue entry with this title, artist and length (±\(window) s)")
            } else {
                failed = true
            }
            guard !Task.isCancelled else { return nil }
        }
        // Japan's own storefront knows a Japanese song by its English title too.
        guard let local = await fetch("search", ["term": query.title, "entity": "song", "country": Self.original.country,
                                                 "lang": Self.original.language, "limit": "25"]) else { return nil }
        LyricTrace.results("iTunes \(Self.original.country) search", returned: local.count, read: local.count)
        if let found = Self.nativeMatch(local, query: query) {
            LyricTrace.note("iTunes JP: the song is listed as “\(found.title)”")
            return .some(found)
        }
        LyricTrace.note("iTunes JP: no entry by this artist under another name, of this length (±\(window) s)")
        // A failed first search is asked again next time rather than remembered as "none".
        return failed ? nil : .some(nil)
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
    /// and the length nearest the playing one, within `tolerance`.
    static func match(_ results: [Song], query: LyricQuery) -> Song? {
        results.filter { song in
            guard let name = song.trackName, song.trackId != nil,
                  LyricQuery.similarity(name, query.title) >= 0.95,
                  VersionTags.tags(name) == VersionTags.tags(query.title),
                  ArtworkProvider.artistMatches(song.artistName, wanted: query.artist) else { return false }
            guard let millis = song.trackTimeMillis, millis > 0 else { return true }
            return abs(Double(millis) / 1000 - query.duration) <= tolerance(query.duration)
        }.min { distance($0, query) < distance($1, query) }
    }

    private static func distance(_ song: Song, _ query: LyricQuery) -> Double {
        song.trackTimeMillis.map { abs(Double($0) / 1000 - query.duration) } ?? tolerance(query.duration)
    }

    /// The song a search found by its listed title though the catalogue
    /// names it in native script: the first entry by the artist that names
    /// the playing version (other versions, "English Version" among them,
    /// are passed over), when its name is native and its length fits. Only
    /// the first counts, as the search ranks the song it matched by name
    /// first; a later entry may be another song of the artist's.
    static func nativeMatch(_ results: [Song], query: LyricQuery) -> Found? {
        let wanted = VersionTags.tags(query.title)
        guard let first = results.first(where: { song in
                  guard let name = song.trackName, song.trackId != nil else { return false }
                  return ArtworkProvider.artistMatches(song.artistName, wanted: query.artist) && VersionTags.tags(name) == wanted
              }),
              let millis = first.trackTimeMillis, millis > 0,
              abs(Double(millis) / 1000 - query.duration) <= tolerance(query.duration) else { return nil }
        return originalName(first, query: query)
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
    /// (`OriginalTitles`) when the listed title found nothing usable: a
    /// language version ("English Version") the title does not name, or,
    /// for an East Asian genre, lyrics in Latin script only. For an artist
    /// who sings in Chinese, Japanese or Korean (`eastAsianArtist`) it is
    /// searched again too when the listed title found nothing, or only
    /// lyrics in Latin script (an English version under the English title
    /// many storefronts list the song by), unless they are Apple's own,
    /// relayed by Lyrics+, for the listed entry. Other artists' songs are
    /// not searched again for want of a match. A match found that way, in
    /// the song's own script, is used instead: it is recorded under the
    /// listed title (it is the same song) and marked `viaOriginalTitle`, so
    /// the other sources' Latin-only lyrics rank as another language's
    /// (`LyricLanguage.expected`). A title that names a language version, or
    /// is already in native script, is not searched again.
    static func withOriginalTitle(_ id: LyricSourceID, query: LyricQuery, titles: OriginalTitles = .shared,
                                  aliases: ArtistAliases = .shared, enabled: Bool = OriginalTitles.enabled,
                                  search: (LyricQuery) async throws -> LyricCandidate?) async throws -> LyricCandidate? {
        LyricTrace.emit(LyricTraceEvent(kind: .variant, message: "listed title", query: LyricTraceQuery(query)))
        let first = try await search(query)
        func keep(_ why: @autoclosure () -> String) -> LyricCandidate? {
            if LyricTrace.isOn {
                LyricTrace.candidate(first, query: query, kind: .variantResult, message: "kept; no original-title search: " + why())
            }
            return first
        }
        guard enabled, originalTitleSources.contains(id), query.hasArtist, query.duration > 0,
              !LyricLanguage.hasNative(query.title),
              VersionTags.tags(query.title).isDisjoint(with: VersionTags.languages) else {
            return keep(!enabled ? "original-title search is off"
                : !originalTitleSources.contains(id) ? "this source is not searched under original titles"
                : !query.hasArtist ? "no artist" : query.duration <= 0 ? "no duration"
                : LyricLanguage.hasNative(query.title) ? "title already in native script"
                : "title names a language version")
        }
        let reason: String
        if usable(first, query: query) {
            guard let first, LyricLanguage.script(of: first) == .latin, !isAppleRelay(first) else {
                return keep("listed-title answer is usable")
            }
            switch await eastAsianArtist(query, aliases: aliases) {
            case true?: break
            case false?: return keep("listed-title answer is usable (no sign the artist sings in Chinese, Japanese or Korean)")
            case nil:
                // Not known yet: shown at once, not saved, and MusicBrainz is asked in the background.
                var kept = first
                kept.languageUnchecked = true
                if LyricTrace.isOn {
                    LyricTrace.candidate(kept, query: query, kind: .variantResult, message: "kept; no original-title search: the artist's country is not known yet (asked in the background); not saved as the song's choice")
                }
                return kept
            }
            reason = "Latin-script lyrics, for an East Asian artist: looking up the original title"
        } else if first == nil {
            guard await eastAsianArtist(query, aliases: aliases) == true else {
                return keep("nothing under the listed title, and no sign (yet) the artist sings in Chinese, Japanese or Korean")
            }
            reason = "nothing under the listed title: looking up the original title"
        } else {
            reason = "not usable (language version or Latin-only lyrics): looking up the original title"
        }
        LyricTrace.candidate(first, query: query, kind: .variantResult, message: reason)
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

    /// Whether the artist sings in Chinese, Japanese or Korean, as far as
    /// the song or MusicBrainz says: an East Asian genre, an artist or album
    /// name in native script, or an artist MusicBrainz places in Japan,
    /// Korea, China, Taiwan or Hong Kong. MusicBrainz counts only when its
    /// answer is already stored (`ArtistAliases.knownEastAsian`): nil when
    /// it is not, and it is then asked in the background for the next song,
    /// so no lookup waits for MusicBrainz (one request per artist, shared
    /// with the alias search and kept 30 days).
    static func eastAsianArtist(_ query: LyricQuery, aliases: ArtistAliases) async -> Bool? {
        if LyricLanguage.eastAsianGenre(query.genre) || LyricLanguage.hasNative(query.artist)
            || LyricLanguage.hasNative(query.album ?? "") { return true }
        let found = await aliases.knownEastAsian(query.artist)
        LyricTrace.note("MusicBrainz: “\(query.artist)” " + (found == true ? "is an East Asian artist"
            : found == false ? "is not known as an East Asian artist" : "not known yet; asked in the background"))
        return found
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
