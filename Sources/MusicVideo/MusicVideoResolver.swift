import Foundation

// MARK: - Seams

/// Music videos in the user's Music library.
protocol MusicVideoLibrary: Sendable {
    /// A library music video for the song with a file on this Mac, if any.
    func localVideo(for song: MusicVideoSong) async -> URL?
}

/// Whether AVFoundation can play a local file (not copy-protected).
protocol MusicVideoPlayability: Sendable {
    func check(_ url: URL) async -> MusicVideoPlayabilityResult
}

enum MusicVideoPlayabilityResult: Equatable, Sendable {
    case playable, protected, unplayable
}

/// HTTP GET. The live one is URLSession; tests pass a fake, so no test
/// ever reaches the network.
protocol MusicVideoHTTP: Sendable {
    func get(_ url: URL) async throws -> (Data, HTTPURLResponse)
}

// MARK: - Cache

/// Online answers per song, so each song is looked up once: found videos
/// for 60 days, "nothing official" for a week (videos do get uploaded).
/// Local files are not cached; the library is asked on every song change.
@MainActor
final class MusicVideoCache {
    static let key = "custom.musicVideoCache"
    static let foundLifetime: TimeInterval = 60 * 86_400
    static let missingLifetime: TimeInterval = 7 * 86_400
    static let capacity = 500

    private let defaults: UserDefaults
    private var entries: [String: [String: Any]]

    init(defaults: UserDefaults) {
        self.defaults = defaults
        entries = defaults.dictionary(forKey: Self.key) as? [String: [String: Any]] ?? [:]
    }

    var count: Int { entries.count }

    /// The stored answer for a song, unless it has expired.
    func lookup(_ songKey: String, now: Date) -> MusicVideoResolution? {
        guard let entry = entries[songKey], let at = entry["at"] as? Double else { return nil }
        let id = entry["video"] as? String ?? ""
        let age = now.timeIntervalSince1970 - at
        if !id.isEmpty, YouTubeVideoID.isValid(id) {
            return age < Self.foundLifetime ? .found(.youTube(id)) : nil
        }
        guard age < Self.missingLifetime else { return nil }
        return .missing(entry["miss"] as? String == "embed" ? .notEmbeddable : .notFound)
    }

    func store(_ resolution: MusicVideoResolution, for songKey: String, now: Date) {
        guard resolution.isCacheable else { return }
        var entry: [String: Any] = ["at": now.timeIntervalSince1970]
        switch resolution {
        case .found(.youTube(let id)): entry["video"] = id
        case .missing(.notEmbeddable): entry["miss"] = "embed"
        default: entry["miss"] = "none"
        }
        entries[songKey] = entry
        if entries.count > Self.capacity {
            let oldest = entries.sorted { ($0.value["at"] as? Double ?? 0) < ($1.value["at"] as? Double ?? 0) }
            for (key, _) in oldest.prefix(entries.count - Self.capacity) { entries.removeValue(forKey: key) }
        }
        defaults.set(entries, forKey: Self.key)
    }

    func remove(_ songKey: String) {
        guard entries.removeValue(forKey: songKey) != nil else { return }
        defaults.set(entries, forKey: Self.key)
    }

    func removeAll() {
        entries = [:]
        defaults.removeObject(forKey: Self.key)
    }
}

/// How far into each song's video the song starts, per recording.
@MainActor
final class MusicVideoOffsets: ObservableObject {
    static let key = "custom.musicVideoOffsets"
    @Published private(set) var values: [String: Double]
    private let defaults: UserDefaults

    init(defaults: UserDefaults) {
        self.defaults = defaults
        values = defaults.dictionary(forKey: Self.key) as? [String: Double] ?? [:]
    }

    func offset(for songKey: String?) -> Double {
        guard let songKey else { return 0 }
        return MusicVideoTiming.clampedOffset(values[songKey] ?? 0)
    }

    func set(_ value: Double, for songKey: String) {
        guard value.isFinite else { return }
        let bounded = (MusicVideoTiming.clampedOffset(value) * 10).rounded() / 10
        if bounded == 0 { values.removeValue(forKey: songKey) } else { values[songKey] = bounded }
        defaults.set(values, forKey: Self.key)
    }

    func nudge(by delta: Double, for songKey: String) {
        set(offset(for: songKey) + delta, for: songKey)
    }
}

// MARK: - Resolver

/// A YouTube link the user pasted for a song, per recording. Played even
/// with online search off: the user chose that video.
@MainActor
final class MusicVideoLinks: ObservableObject {
    static let key = "custom.musicVideoLinks"
    @Published private(set) var values: [String: String]
    private let defaults: UserDefaults

    init(defaults: UserDefaults) {
        self.defaults = defaults
        let stored = defaults.dictionary(forKey: Self.key) as? [String: String] ?? [:]
        values = stored.filter { YouTubeVideoID.isValid($0.value) }
    }

    func videoID(for songKey: String?) -> String? { songKey.flatMap { values[$0] } }

    /// Stores the link's video; false when the text is not a YouTube link.
    @discardableResult
    func set(_ text: String, for songKey: String) -> Bool {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let id = YouTubeVideoID.fromURL(trimmed) ?? (YouTubeVideoID.isValid(trimmed) ? trimmed : nil) else {
            return false
        }
        values[songKey] = id
        defaults.set(values, forKey: Self.key)
        return true
    }

    func remove(_ songKey: String) {
        guard values.removeValue(forKey: songKey) != nil else { return }
        defaults.set(values, forKey: Self.key)
    }
}

/// Finds a song's video, in order: the Music library's own file (when it
/// can be played here), then a YouTube link the user pasted for the song,
/// then, only with online search on, the cached answer, then YouTube's own
/// search results page.
///
/// song.link was the first plan for the YouTube link, but its keyless API
/// now answers 401 (PUBLIC_API_ACCESS_DEPRECATED) and no longer issues
/// keys, so the search page is the one keyless route left.
@MainActor
final class MusicVideoResolver {
    private let library: MusicVideoLibrary
    private let playability: MusicVideoPlayability
    private let http: MusicVideoHTTP
    let cache: MusicVideoCache
    let links: MusicVideoLinks
    private let now: () -> Date
    private let uptime: () -> TimeInterval
    private let sleep: (TimeInterval) async -> Void
    private let parse: @Sendable (Data) async -> [YouTubeSearchPage.Result]
    private(set) var rateLimit: MusicVideoRateLimit
    /// Waits longer than this are not sat out: the lookup reports
    /// `.rateLimited` and is tried again later.
    var longestWait: TimeInterval = 8

    init(library: MusicVideoLibrary, playability: MusicVideoPlayability, http: MusicVideoHTTP, cache: MusicVideoCache,
         links: MusicVideoLinks, rateLimit: MusicVideoRateLimit = MusicVideoRateLimit(),
         now: @escaping () -> Date = Date.init,
         uptime: @escaping () -> TimeInterval = { ProcessInfo.processInfo.systemUptime },
         sleep: @escaping (TimeInterval) async -> Void = { try? await Task.sleep(for: .seconds($0)) },
         parse: @escaping @Sendable (Data) async -> [YouTubeSearchPage.Result] = { await YouTubeSearchPage.resultsOffMainActor($0) }) {
        self.library = library
        self.playability = playability
        self.http = http
        self.cache = cache
        self.links = links
        self.rateLimit = rateLimit
        self.now = now
        self.uptime = uptime
        self.sleep = sleep
        self.parse = parse
    }

    func resolve(_ song: MusicVideoSong, online: Bool) async -> MusicVideoResolution {
        guard !song.title.trimmingCharacters(in: .whitespaces).isEmpty else { return .missing(.notASong) }
        var libraryMiss = MusicVideoMiss.notInLibrary
        if let url = await library.localVideo(for: song) {
            switch await playability.check(url) {
            case .playable: return .found(.local(url))
            case .protected: libraryMiss = .protectedInLibrary
            case .unplayable: break
            }
        }
        let key = song.key
        if let pasted = links.videoID(for: key) { return .found(.youTube(pasted)) }
        guard online else { return .missing(libraryMiss) }
        if let cached = cache.lookup(key, now: now()) { return cached }
        // Skipped past while the library was asked: nothing is sent.
        guard !Task.isCancelled else { return .missing(.lookupFailed) }
        let result = await searchYouTube(song)
        cache.store(result, for: key, now: now())
        return result
    }

    /// The player found the video cannot be embedded: remembered, so the
    /// song is not looked up again for a while. A pasted link is kept: the
    /// user may want to fix it.
    func markNotEmbeddable(_ song: MusicVideoSong) {
        cache.store(.missing(.notEmbeddable), for: song.key, now: now())
    }

    private enum FetchError: Error { case rateLimited, failed }

    /// YouTube's results for "<artist> <title> official music video". A
    /// page with no results at all is taken as YouTube having changed its
    /// format (or asking for consent), not as "no video": it is not cached.
    private func searchYouTube(_ song: MusicVideoSong) async -> MusicVideoResolution {
        do {
            guard let url = YouTubeSearchPage.requestURL(title: song.title, artist: song.artist) else {
                return .missing(.lookupFailed)
            }
            let data = try await fetch(url)
            // About a megabyte of HTML and JSON: read off the main actor.
            let results = await parse(data)
            guard !results.isEmpty else { return .missing(.lookupFailed) }
            if let video = YouTubeSearchPage.officialVideo(in: results, title: song.title, artist: song.artist) {
                return .found(.youTube(video.videoID))
            }
            return .missing(.notFound)
        } catch FetchError.rateLimited {
            return .missing(.rateLimited)
        } catch {
            return .missing(.lookupFailed)
        }
    }

    /// One rate-limited GET of a 200 reply. A lookup cancelled (the song
    /// skipped) before or while it waited its turn sends nothing and uses
    /// no slot, so rapid skipping cannot use up the allowance.
    private func fetch(_ url: URL) async throws -> Data {
        try Task.checkCancellation()
        let wait = rateLimit.delay(at: uptime())
        if wait > longestWait { throw FetchError.rateLimited }
        if wait > 0 { await sleep(wait) }
        try Task.checkCancellation()
        rateLimit.record(at: uptime())
        let (data, response) = try await http.get(url)
        switch response.statusCode {
        case 200: return data
        case 429, 503:
            let retry = (response.value(forHTTPHeaderField: "Retry-After")).flatMap(Double.init)
            rateLimit.backOff(at: uptime(), retryAfter: retry)
            throw FetchError.rateLimited
        default: throw FetchError.failed
        }
    }
}
