import Combine
import CryptoKit
import Foundation

enum LyricSourceID: String, CaseIterable, Identifiable, Codable {
    case appleMusicCache, lyricsPlus, netEase, qqMusic, kugou, lrclib, lyrimuse, kuwo, lyricsOVH, amll
    var id: String { rawValue }
    var title: String {
        switch self {
        case .appleMusicCache: return AppleMusicCachedLyrics.sourceName
        case .lyricsPlus: return "Lyrics+ / KPoe"
        case .netEase: return "NetEase Cloud Music"
        case .qqMusic: return "QQ Music"
        case .kugou: return "Kugou Music"
        case .lrclib: return "LRCLIB"
        case .kuwo: return "Kuwo Music"
        case .lyricsOVH: return "lyrics.ovh"
        case .amll: return "AMLL TTML DB"
        case .lyrimuse: return "Lyrimuse · local files"
        }
    }
    var timing: String {
        switch self {
        case .appleMusicCache: return "Word or line timing from Music on this Mac"
        case .lyricsPlus: return "Word / syllable timing"
        case .netEase: return "Word or line timing"
        case .qqMusic, .kuwo: return "Line timing"
        case .kugou: return "Word or line timing"
        case .lrclib: return "Word or line timing"
        case .lyricsOVH: return "Plain text fallback"
        case .amll: return "Word / syllable timing"
        case .lyrimuse: return "LRC / TTML / YRC + readings, translations"
        }
    }
    /// Read from this Mac rather than a lyric service. Local sources are
    /// evaluated on every lookup and never enter the automatic cache.
    var isLocal: Bool { self == .appleMusicCache || self == .lyrimuse }
    /// Best timing tier the source can deliver (see `LyricsFetcher.timingTier`),
    /// so a lookup can settle without waiting for sources that cannot win.
    var timingCeiling: Int {
        switch self {
        case .lyricsOVH: return 0
        case .kuwo: return 1 // Kuwo's line list is converted to line-level LRC.
        default: return 3
        }
    }
}

@MainActor
final class LyricSourceOptions: ObservableObject {
    static let shared = LyricSourceOptions()
    enum Mode: String, CaseIterable, Identifiable {
        case smart, priority
        var id: String { rawValue }
        var title: String { self == .smart ? "Accuracy first" : "Source order" }
        /// What the mode decides beyond `LyricSourceOptions.ranking`.
        var detail: String {
            self == .smart
                ? "Among your other sources, \(title) prefers the closest title and artist match, then the order of the list below."
                : "Among your other sources, \(title) follows the order of the list below."
        }
    }
    /// How every match is ranked, in the labels Timing & sources shows: the
    /// timing on each match and the names in the source list.
    static var ranking: String {
        let music = LyricSourceID.appleMusicCache.title, lrclib = LyricSourceID.lrclib.title
        return "Word-by-word and Mixed timing lyrics come first: \(music), then Apple Music’s lyrics from Lyrics+, then \(lrclib), then your other sources. "
            + "Line-synced lyrics come next, again with \(music) first, then Apple Music’s from Lyrics+."
            + " Timed lyrics for another version of the song (Live, Remix, Acoustic, English Version…), or in another language than the song’s, come after timed lyrics for the version playing."
    }
    /// What a lookup asks and ranks by. Timing, and the lead of Music's own
    /// lyrics and LRCLIB's, are fixed (`LyricsFetcher.outranks`); the order
    /// of `enabled`, and `mode`, only rank the other sources.
    struct Snapshot {
        let enabled: [LyricSourceID]
        let mode: Mode
        let localFolder: String
        /// Identity of the configured local folder. Local results are read
        /// afresh on every lookup, so this is not part of the cache key.
        let localVersion: String
        /// Bump when matching, ranking or parsing changes, so automatic
        /// choices made under the old rules are looked up again. Saved
        /// explicit choices are keyed separately and are unaffected.
        /// v7: Music's own lyrics and LRCLIB lead word and mixed timing.
        /// v8: Lyrics+ keeps each syllable of an unspaced (Japanese,
        /// Chinese, Thai) line, which it had joined into one word.
        /// v10: production credits are kept (as `Line.credit`) for the credits layer.
        /// v9: AMLL's word-timed database and Kugou's word-timed KRC.
        /// v11: lyrics for another version (Live, Remix…) rank below the playing one's.
        /// v13: a language version (English Version…), or lyrics in another
        /// language than the song's, rank below the playing one's; songs listed
        /// under an English title are searched under their original title too.
        /// v14: a whole line or a few long phrases timed as "words" is line
        /// timing (`LyricsFetcher.isPhraseTimed`); Apple's lyrics relayed by
        /// Lyrics+ rank as Apple Music's.
        /// v15: a Lyrics+ mirror's "qApple" is Apple's lyrics too.
        /// v16: Lyrics+ answers timed only by line are kept (as Apple's line
        /// timing); an East Asian artist's song listed under an English title
        /// is searched under its original title even when Latin-script
        /// lyrics (an English version) were found, and NetEase search works
        /// again (no cookies).
        static let cachePolicy = "v16"
        /// Keys the cached online choice. Only settings that change which
        /// online result wins take part: local sources are always evaluated
        /// afresh.
        var fingerprint: String {
            let online = enabled.filter { !$0.isLocal }.map(\.rawValue).joined(separator: ",")
            return "\(Self.cachePolicy)|\(mode.rawValue)|\(online)"
        }
    }
    @Published var order: [LyricSourceID] {
        didSet { UserDefaults.standard.set(order.map(\.rawValue), forKey: "custom.lyricSourceOrder") }
    }
    @Published var enabled: Set<LyricSourceID> {
        didSet { UserDefaults.standard.set(enabled.map(\.rawValue).sorted(), forKey: "custom.lyricSources") }
    }
    @Published var mode: Mode {
        didSet { UserDefaults.standard.set(mode.rawValue, forKey: "custom.lyricMatchMode") }
    }
    static let recommendedOrder: [LyricSourceID] = [.appleMusicCache, .lyrimuse, .lyricsPlus, .amll, .netEase, .lrclib, .qqMusic, .kugou, .kuwo, .lyricsOVH]
    // "custom.preferWordTiming" and "custom.preferWordTimedLRCLIB" are no
    // longer read: word and mixed timing always rank first, with LRCLIB
    // after Music's own lyrics. Stored values are left as they are.
    func resetOrder() { order = Self.recommendedOrder; mode = .smart }
    @Published var repairQQGaps: Bool {
        didSet { UserDefaults.standard.set(repairQQGaps, forKey: "custom.repairQQGaps") }
    }
    var snapshot: Snapshot {
        Snapshot(enabled: order.filter { enabled.contains($0) }, mode: mode,
                 localFolder: LocalLyricOptions.shared.folder,
                 localVersion: enabled.contains(.lyrimuse) ? LocalLyricOptions.shared.fingerprint : "")
    }
    var fingerprint: String { snapshot.fingerprint }
    private init() {
        let d = UserDefaults.standard
        let saved = (d.stringArray(forKey: "custom.lyricSourceOrder") ?? []).compactMap(LyricSourceID.init(rawValue:))
        var unique: [LyricSourceID] = []
        for item in saved + Self.recommendedOrder where !unique.contains(item) { unique.append(item) }
        order = unique
        var chosen = Set((d.stringArray(forKey: "custom.lyricSources") ?? LyricSourceID.allCases.filter { $0 != .lyrimuse && $0 != .appleMusicCache }.map(\.rawValue))
            .compactMap(LyricSourceID.init(rawValue:)))
        // A source added after someone saved their choices starts switched on, once.
        if !d.bool(forKey: "custom.lyricSourcesSeenAMLL") {
            chosen.insert(.amll)
            d.set(true, forKey: "custom.lyricSourcesSeenAMLL")
        }
        enabled = chosen
        mode = Mode(rawValue: d.string(forKey: "custom.lyricMatchMode") ?? "") ?? .smart
        repairQQGaps = d.object(forKey: "custom.repairQQGaps") as? Bool ?? true
    }
    func move(_ source: LyricSourceID, by amount: Int) {
        guard let index = order.firstIndex(of: source) else { return }
        let next = index + amount
        guard order.indices.contains(next) else { return }
        order.swapAt(index, next)
    }
}

struct LyricQuery {
    var title: String
    var artist: String
    var album: String?
    var duration: Double
    /// The player's genre ("J-Pop"), a hint at the language sung
    /// (`LyricLanguage`). Not part of `recordingKey`.
    var genre: String? = nil
    init(title: String, artist: String, album: String?, duration: Double) {
        self.title=title;self.artist=artist;self.album=album
        self.duration=duration.isFinite ? min(max(duration,0),864000) : 0
    }
    /// Saved choices, cached results and per-song offsets are keyed by this;
    /// `normalize` must stay unchanged so existing entries keep matching.
    var recordingKey: String {
        [Self.normalize(title), Self.normalize(artist), Self.normalize(album ?? ""),
         String(Int(duration.rounded()))].joined(separator: "|")
    }
    /// A local file without an artist tag can still match on title and length.
    var hasArtist: Bool { !artist.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
    /// Artist similarity for a provider's own pre-filter. Without an artist to
    /// compare, candidates pass on title and duration, which ranking enforces.
    func artistScore(_ candidate: String) -> Double { hasArtist ? Self.similarity(artist, candidate) : 0.5 }
    static func normalize(_ text: String) -> String {
        text.folding(options: [.diacriticInsensitive, .caseInsensitive], locale: .current)
            .components(separatedBy: CharacterSet.alphanumerics.inverted).filter { !$0.isEmpty }.joined(separator: " ")
    }
    /// 1 for the same name, 0.95 when only edition or credit qualifiers differ
    /// ("(2011 Remaster)" / "- Remastered 2011", "(feat. X)" / "(with X)"),
    /// 0.85 when one name contains the other as whole words, else word overlap.
    static func similarity(_ lhs: String, _ rhs: String) -> Double {
        let a = normalize(lhs), b = normalize(rhs)
        // Names made only of symbols or emoji ("!!!", "🦋") have nothing
        // left once normalised; compare them as written instead.
        guard !a.isEmpty, !b.isEmpty else { return symbolsMatch(lhs, rhs) ? 1 : 0 }
        if a == b { return 1 }
        let coreA = core(lhs), coreB = core(rhs)
        let x = coreA.isEmpty ? a : coreA, y = coreB.isEmpty ? b : coreB
        if x == y { return 0.95 }
        let aa = Set(x.split(separator: " ").map(String.init))
        let bb = Set(y.split(separator: " ").map(String.init))
        let token = Double(aa.intersection(bb).count) / Double(max(aa.count, bb.count))
        return max(token, contains(x, y) ? 0.85 : 0)
    }
    /// The name without edition and credit qualifiers, normalised. Qualifiers
    /// are bracketed or follow " - ": feat./ft./featuring/with credits,
    /// remaster(ed) with or without a year, live, (radio) edit, version,
    /// mono and stereo. Recordings are still told apart by duration.
    static func core(_ text: String) -> String {
        let folded = text.folding(options: [.diacriticInsensitive, .caseInsensitive], locale: .current)
        let value = NSMutableString(string: folded)
        // Later matches are removed first, so earlier ranges stay valid.
        for match in bracketPattern.matches(in: folded, range: NSRange(location: 0, length: value.length)).reversed()
            where isQualifier(value.substring(with: match.range(at: 1)), allowsWith: true) {
            value.deleteCharacters(in: match.range)
        }
        func truncate(at pattern: NSRegularExpression, when test: (String) -> Bool) {
            guard let match = pattern.firstMatch(in: value as String, range: NSRange(location: 0, length: value.length)),
                  test(value.substring(from: NSMaxRange(match.range))) else { return }
            value.deleteCharacters(in: NSRange(location: match.range.location, length: value.length - match.range.location))
        }
        truncate(at: dashPattern) { isQualifier($0, allowsWith: false) }
        truncate(at: creditPattern) { _ in true }
        return normalize(value as String)
    }
    private static let bracketPattern = try! NSRegularExpression(pattern: #"[(\[{（【]([^)\]}）】]*)[)\]}）】]"#)
    private static let dashPattern = try! NSRegularExpression(pattern: #"\s+[-–—]\s+"#)
    private static let creditPattern = try! NSRegularExpression(pattern: #"\s(feat\.?|ft\.|featuring)\s"#)
    private static let qualifierWords: Set<String> = ["remaster", "remastered", "live", "edit", "version", "mono", "stereo"]
    private static func isQualifier(_ text: String, allowsWith: Bool) -> Bool {
        let words = normalize(text).split(separator: " ").map(String.init)
        guard let first = words.first else { return false }
        if ["feat", "ft", "featuring"].contains(first) || (allowsWith && first == "with") { return true }
        return words.contains { qualifierWords.contains($0) }
    }
    private static func contains(_ a: String, _ b: String) -> Bool {
        let (long, short) = a.count >= b.count ? (a, b) : (b, a)
        if (" " + long + " ").contains(" " + short + " ") { return true }
        // Scripts written without spaces have no word boundaries to respect.
        return short.unicodeScalars.contains(where: isUnspaced) && long.contains(short)
    }
    private static func isUnspaced(_ scalar: Unicode.Scalar) -> Bool {
        switch scalar.value {
        case 0x0E00...0x0EFF, 0x1000...0x109F, 0x1780...0x17FF, 0x3040...0x30FF, 0x31F0...0x31FF,
             0x3400...0x4DBF, 0x4E00...0x9FFF, 0xF900...0xFAFF, 0x20000...0x2FA1F: return true
        default: return false
        }
    }
    private static func symbolsMatch(_ lhs: String, _ rhs: String) -> Bool {
        func fold(_ text: String) -> String {
            text.trimmingCharacters(in: .whitespacesAndNewlines).folding(options: [.diacriticInsensitive, .caseInsensitive], locale: .current)
        }
        let a = fold(lhs)
        return !a.isEmpty && a == fold(rhs)
    }
    /// A name a service reported, when it can be checked against the one
    /// asked for: it matches, or both are written in Latin script, so a
    /// mismatch means a different name. Nil when it may only be the same
    /// name in another script ("米津玄師" for "Kenshi Yonezu").
    static func checkable(_ reported: String?, against asked: String) -> String? {
        guard let reported else { return nil }
        func latin(_ text: String) -> Bool {
            text.unicodeScalars.allSatisfy { !$0.properties.isAlphabetic || $0.value < 0x0250 || (0x1E00...0x1EFF).contains($0.value) }
        }
        return similarity(asked, reported) >= 0.75 || latin(asked) && latin(reported) ? reported : nil
    }
    /// "3:41", or "1:02:03" past an hour. Lengths beyond ten days (from a
    /// broken service reply) show as ten days rather than overflow.
    static func clock(_ seconds: Double) -> String {
        let total = Int(min(864000, max(0, seconds.isFinite ? seconds : 0)).rounded())
        let h = total / 3600, m = total / 60 % 60, s = total % 60
        return h > 0 ? String(format: "%d:%02d:%02d", h, m, s) : String(format: "%d:%02d", m, s)
    }
}

struct LyricCandidate: Identifiable {
    var source: LyricSourceID
    var result: LyricsFetcher.Result
    var title: String
    var artist: String
    var duration: Double?
    var score: Double
    /// False when the service did not name the recording it matched in a
    /// form that can be checked (not at all, or in another script); title
    /// and artist then repeat the search.
    var metadataReported = true
    /// What the other sources in the same lookup say about it (`LyricsFetcher.weighAgreement`).
    var agreement = LyricAgreement()
    /// Its lyrics are in another language than the song's (`LyricLanguage`),
    /// set when candidates are screened: it ranks as another version.
    var languageMismatch = false
    /// Found by searching under the song's original title
    /// (`OriginalTitles`), which shows the song is sung in that language.
    var viaOriginalTitle = false
    /// Latin-script lyrics kept without the original-title search because
    /// whether the artist sings in Chinese, Japanese or Korean was not known
    /// yet (MusicBrainz is being asked in the background). Shown, but not
    /// saved as the song's choice, so the next lookup asks again.
    var languageUnchecked = false
    var id: String { source.rawValue }
    var hasReadings: Bool {
        if case .synced(let lines, _) = result { return lines.contains { $0.romanization?.isEmpty == false } }
        return false
    }
    var provenance: LyricsFetcher.Provenance {
        switch result { case .synced(let lines, let p): return .init(precision: LyricsFetcher.precision(lines), source: p.source)
        case .plain(_, let p): return p }
    }
    var lineCount: Int {
        switch result { case .synced(let lines, _): return lines.count; case .plain(let lines, _): return lines.count }
    }
}

/// A candidate that failed the recording check, kept so the user can see why
/// and still choose it for the song.
struct LyricRejection: Identifiable {
    var candidate: LyricCandidate
    var reason: String
    var id: String { candidate.id }
}

struct LyricLookupReport {
    /// What to show. During a progressive lookup this is the current choice.
    var result: LyricsFetcher.Result?
    var candidates: [LyricCandidate] = []
    var states: [LyricSourceID: String] = [:]
    var pinned = false
    var cached = false
    var rejected: [LyricRejection] = []
    /// Increments each time `result` changes; 0 means nothing to show yet.
    var revision = 0
    /// False for progress updates while sources are still answering.
    var isComplete = true
    /// The network looked unreachable: nothing is cached for the miss and the
    /// lookup is worth repeating when the connection returns.
    var offline = false
    /// The item reports no song length (a radio station or stream), so no
    /// automatic lookup ran.
    var skippedStream = false
    /// Local lyric files that matched the track when the lookup ran.
    var localPrint: String?
}

enum SavedLyrics {
    struct Entry: Codable {
        var result: LyricsFetcher.Result
        var storedAt: Date
        /// Present for automatic online choices, so they can be ranked again
        /// against local sources without repeating the lookup.
        var source: String? = nil
        var title: String? = nil
        var artist: String? = nil
        var duration: Double? = nil
    }
    static var defaultFolder: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Verso Custom/Lyrics", isDirectory: true)
    }
    private static func file(_ key: String, pin: Bool, in folder: URL) -> URL {
        let hash = SHA256.hash(data: Data(key.utf8)).map { String(format: "%02x", $0) }.joined()
        return folder.appendingPathComponent((pin ? "pin-" : "cache-") + hash + ".json")
    }
    private static func entry(_ key: String, pin: Bool, in folder: URL) -> Entry? {
        guard let data = try? Data(contentsOf: file(key, pin: pin, in: folder)), data.count <= 2_000_000,
              let entry = try? JSONDecoder().decode(Entry.self, from: data),
              pin || Date().timeIntervalSince(entry.storedAt) < 14 * 86400 else { return nil }
        return entry
    }
    static func read(_ key: String, pin: Bool, in folder: URL = defaultFolder) -> LyricsFetcher.Result? {
        entry(key, pin: pin, in: folder)?.result
    }
    /// The cached automatic online choice, as a candidate that can be ranked.
    static func readCandidate(_ key: String, in folder: URL = defaultFolder) -> LyricCandidate? {
        guard let entry = entry(key, pin: false, in: folder), let raw = entry.source,
              let source = LyricSourceID(rawValue: raw), let title = entry.title, let artist = entry.artist else { return nil }
        return LyricCandidate(source: source, result: entry.result, title: title, artist: artist, duration: entry.duration, score: 0)
    }
    @discardableResult
    static func save(_ candidate: LyricCandidate, key: String, in folder: URL = defaultFolder) -> Bool {
        write(Entry(result: candidate.result, storedAt: Date(), source: candidate.source.rawValue,
                    title: candidate.title, artist: candidate.artist, duration: candidate.duration), key: key, pin: false, in: folder)
    }
    @discardableResult
    static func save(_ result: LyricsFetcher.Result, key: String, pin: Bool, in folder: URL = defaultFolder) -> Bool {
        write(Entry(result: result, storedAt: Date()), key: key, pin: pin, in: folder)
    }
    private static func write(_ entry: Entry, key: String, pin: Bool, in folder: URL) -> Bool {
        do {
            let data = try JSONEncoder().encode(entry)
            guard data.count <= 2_000_000 else { return false }
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            try data.write(to: file(key, pin: pin, in: folder), options: .atomic)
            // Bound automatic cache growth. Explicit user selections are retained.
            let files = (try? FileManager.default.contentsOfDirectory(at: folder,
                includingPropertiesForKeys: [.contentModificationDateKey])) ?? []
            let automatic = files.filter { $0.lastPathComponent.hasPrefix("cache-") }
            if automatic.count > 150 {
                let oldest = automatic.sorted {
                    let a = (try? $0.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? .distantPast
                    let b = (try? $1.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? .distantPast
                    return a < b
                }
                for url in oldest.prefix(automatic.count - 150) { try? FileManager.default.removeItem(at: url) }
            }
            return true
        } catch { return false }
    }
    static func unpin(_ key: String, in folder: URL = defaultFolder) {
        try? FileManager.default.removeItem(at: file(key, pin: true, in: folder))
    }
}

/// Recent "no lyrics" answers, so replays within a few minutes do not ask
/// every service again. Kept in memory per options fingerprint and per
/// source: a source that failed or timed out is asked again next time, and
/// nothing is recorded while the connection is down. Each source's answer
/// expires on its own clock, so replays never extend an older one.
final class LyricMissCache: @unchecked Sendable {
    /// What recently answered without an acceptable match: the sources, and
    /// the candidates among those answers that failed the recording check,
    /// so they can still be listed (and chosen) without asking again.
    struct Recent {
        var sources: Set<LyricSourceID> = []
        var rejected: [LyricCandidate] = []
    }
    private struct Answer {
        var at: Date
        var rejected: LyricCandidate?
    }
    private let lock = NSLock()
    private var misses: [String: [LyricSourceID: Answer]] = [:]
    private let clock: @Sendable () -> Date
    let lifetime: TimeInterval
    init(lifetime: TimeInterval = 600, clock: @escaping @Sendable () -> Date = { Date() }) {
        self.lifetime = lifetime
        self.clock = clock
    }
    /// Sources that recently answered that they have nothing usable for `key`.
    func answered(_ key: String, now given: Date? = nil) -> Recent {
        let now = given ?? clock()
        return lock.withLock {
            let live = (misses[key] ?? [:]).filter { now.timeIntervalSince($0.value.at) < lifetime }
            misses[key] = live.isEmpty ? nil : live
            return Recent(sources: Set(live.keys), rejected: live.values.compactMap(\.rejected))
        }
    }
    /// Records the sources that just answered without an acceptable match.
    /// `rejected` holds what any of them offered that failed the check.
    /// Sources already recorded and not passed keep their own time.
    func insert(_ key: String, sources: Set<LyricSourceID>, rejected: [LyricCandidate] = [], now given: Date? = nil) {
        let now = given ?? clock()
        lock.withLock {
            misses = misses.compactMapValues { answers in
                let live = answers.filter { now.timeIntervalSince($0.value.at) < lifetime }
                return live.isEmpty ? nil : live
            }
            guard !sources.isEmpty else { return }
            var answers = misses[key] ?? [:]
            for id in sources { answers[id] = Answer(at: now, rejected: rejected.first { $0.source == id }) }
            misses[key] = answers
        }
    }
    func remove(_ key: String) { lock.withLock { misses[key] = nil } }
}

enum LyricHTTP {
    struct Failure: LocalizedError {
        let message: String
        var errorDescription: String? { message }
    }
    /// Errors meaning this Mac has no connection, as opposed to one service
    /// being slow, missing or broken.
    static func isOffline(_ error: Error) -> Bool {
        guard let code = (error as? URLError)?.code else { return false }
        return [.notConnectedToInternet, .networkConnectionLost, .dataNotAllowed, .internationalRoamingOff].contains(code)
    }
    static func json(_ base: String, _ parameters: [String: String], referer: String? = nil, session: URLSession = .shared) async throws -> [String: Any] {
        try Task.checkCancellation()
        var parts = URLComponents(string: base)
        parts?.queryItems = parameters.sorted { $0.key < $1.key }.map { URLQueryItem(name: $0.key, value: $0.value) }
        guard let url = parts?.url else { throw Failure(message: "Invalid service address") }
        var request = URLRequest(url: url, timeoutInterval: 6)
        request.setValue("Lunalyr/0.4.6 (personal desktop lyrics)", forHTTPHeaderField: "User-Agent")
        if let referer { request.setValue(referer, forHTTPHeaderField: "Referer") }
        let (data, response) = try await LyricTrace.data(session, for: request)
        guard let http = response as? HTTPURLResponse, http.statusCode == 200 else {
            throw Failure(message: "Service response: HTTP \((response as? HTTPURLResponse)?.statusCode ?? 0)")
        }
        guard data.count <= 2_000_000 else { throw Failure(message: "Response too large") }
        guard let value = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw Failure(message: "Unexpected response format")
        }
        return value
    }
    /// A plain-text GET (a few lyric files are served as text, not JSON).
    static func fetchText(_ address: String, session: URLSession = .shared) async throws -> String {
        try Task.checkCancellation()
        guard let url = URL(string: address) else { throw Failure(message: "Invalid service address") }
        var request = URLRequest(url: url, timeoutInterval: 8)
        request.setValue("Lunalyr/0.5 (personal desktop lyrics)", forHTTPHeaderField: "User-Agent")
        let (data, response) = try await LyricTrace.data(session, for: request)
        guard let http = response as? HTTPURLResponse, http.statusCode == 200 else {
            throw Failure(message: "Service response: HTTP \((response as? HTTPURLResponse)?.statusCode ?? 0)")
        }
        guard data.count <= 2_000_000, let text = String(data: data, encoding: .utf8) else { throw Failure(message: "Unexpected response format") }
        return text
    }
    static func string(_ value: Any?) -> String? {
        if let string = value as? String { return string }
        if let number = value as? NSNumber { return number.stringValue }
        return nil
    }
    static func text(_ value: String) -> String {
        value.replacingOccurrences(of: "&amp;", with: "&").replacingOccurrences(of: "&apos;", with: "'")
            .replacingOccurrences(of: "&quot;", with: "\"").replacingOccurrences(of: "&#39;", with: "'")
            .replacingOccurrences(of: "&lt;", with: "<").replacingOccurrences(of: "&gt;", with: ">")
    }
}
