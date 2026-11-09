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
    /// Still-image verdicts per YouTube video, kept as long as found videos.
    static let stillKey = "custom.musicVideoStill"
    /// Found videos stored by an older pick are looked up again: 2 added
    /// the still-image check, 3 the exact song-title match (older picks
    /// included live and fan uploads and other songs by the artist).
    static let version = 3

    private let defaults: UserDefaults
    private var entries: [String: [String: Any]]
    private var stills: [String: [String: Any]]

    init(defaults: UserDefaults) {
        self.defaults = defaults
        entries = defaults.dictionary(forKey: Self.key) as? [String: [String: Any]] ?? [:]
        stills = defaults.dictionary(forKey: Self.stillKey) as? [String: [String: Any]] ?? [:]
    }

    /// Whether a video was found to be a still image, if it was checked.
    func still(_ videoID: String, now: Date) -> Bool? {
        guard let entry = stills[videoID], let at = entry["at"] as? Double, let still = entry["still"] as? Bool,
              now.timeIntervalSince1970 - at < Self.foundLifetime else { return nil }
        return still
    }

    func storeStill(_ still: Bool, for videoID: String, now: Date) {
        stills[videoID] = ["at": now.timeIntervalSince1970, "still": still]
        if stills.count > Self.capacity {
            let oldest = stills.sorted { ($0.value["at"] as? Double ?? 0) < ($1.value["at"] as? Double ?? 0) }
            for (key, _) in oldest.prefix(stills.count - Self.capacity) { stills.removeValue(forKey: key) }
        }
        defaults.set(stills, forKey: Self.stillKey)
    }

    var count: Int { entries.count }

    /// The stored answer for a song, unless it has expired.
    func lookup(_ songKey: String, now: Date) -> MusicVideoResolution? {
        guard let entry = entries[songKey], let at = entry["at"] as? Double else { return nil }
        let id = entry["video"] as? String ?? ""
        let age = now.timeIntervalSince1970 - at
        if !id.isEmpty, YouTubeVideoID.isValid(id) {
            let checked = (entry["v"] as? Int ?? 1) >= Self.version
            return checked && age < Self.foundLifetime ? .found(.youTube(id)) : nil
        }
        guard age < Self.missingLifetime else { return nil }
        return .missing(entry["miss"] as? String == "embed" ? .notEmbeddable : .notFound)
    }

    func store(_ resolution: MusicVideoResolution, for songKey: String, now: Date) {
        guard resolution.isCacheable else { return }
        var entry: [String: Any] = ["at": now.timeIntervalSince1970]
        switch resolution {
        case .found(.youTube(let id)): entry["video"] = id; entry["v"] = Self.version
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
        stills = [:]
        defaults.removeObject(forKey: Self.key)
        defaults.removeObject(forKey: Self.stillKey)
    }
}

/// Where the offset on screen came from.
enum MusicVideoOffsetOrigin: Equatable, Sendable {
    /// No offset: the video plays from where the song is.
    case none
    /// The user's own nudge; always wins.
    case manual
    /// Found from the video's captions against the timed lyrics.
    case captions
    /// Found from the video's soundtrack against the song as it plays.
    case audio
    /// Guessed from how much longer the video is than the song.
    case estimated

    var isAutomatic: Bool { self == .captions || self == .audio }
}

/// How far into each song's video the song starts, per recording.
///
/// Three layers, the first present wins: the user's nudge (`values`, kept
/// even at 0 once set), the automatic offset found for this song and THIS
/// video (captions or audio, cached on this Mac), then a guess from the
/// video's length (kept in memory only).
@MainActor
final class MusicVideoOffsets: ObservableObject {
    static let key = "custom.musicVideoOffsets"
    static let automaticKey = "custom.musicVideoAutoOffsets"
    /// Found offsets are kept this long…
    static let automaticLifetime: TimeInterval = 180 * 86_400
    /// …and "checked, nothing usable" this long, so a video without
    /// captions is not asked about on every play.
    static let unusableLifetime: TimeInterval = 7 * 86_400
    static let capacity = 500

    /// The user's offsets.
    @Published private(set) var values: [String: Double]
    @Published private(set) var automatic: [String: Automatic]
    @Published private(set) var estimates: [String: Double] = [:]
    private let defaults: UserDefaults

    struct Automatic: Equatable, Sendable {
        /// The video it was found for (`videoKey`).
        var video: String
        /// Nil: checked, and nothing usable was found.
        var offset: Double?
        var origin: MusicVideoOffsetOrigin
        var at: Double
    }

    init(defaults: UserDefaults) {
        self.defaults = defaults
        values = defaults.dictionary(forKey: Self.key) as? [String: Double] ?? [:]
        let stored = defaults.dictionary(forKey: Self.automaticKey) as? [String: [String: Any]] ?? [:]
        automatic = stored.compactMapValues { entry in
            guard let video = entry["video"] as? String, let at = entry["at"] as? Double else { return nil }
            let origin: MusicVideoOffsetOrigin = entry["kind"] as? String == "audio" ? .audio : .captions
            return Automatic(video: video, offset: entry["offset"] as? Double, origin: origin, at: at)
        }
    }

    /// The cache key for a video: its YouTube ID or its file.
    nonisolated static func videoKey(_ source: MusicVideoSource?) -> String? {
        switch source {
        case .youTube(let id): return "yt:\(id)"
        case .local(let url): return "file:\(url.path)"
        case nil: return nil
        }
    }

    // MARK: Reading

    /// The offset in force for the song and video.
    func offset(for songKey: String?, video: String? = nil, now: Date = Date()) -> Double {
        resolved(songKey, video: video, now: now).offset
    }

    func origin(for songKey: String?, video: String? = nil, now: Date = Date()) -> MusicVideoOffsetOrigin {
        resolved(songKey, video: video, now: now).origin
    }

    private func resolved(_ songKey: String?, video: String?, now: Date) -> (offset: Double, origin: MusicVideoOffsetOrigin) {
        guard let songKey else { return (0, .none) }
        if let manual = values[songKey] { return (MusicVideoTiming.clampedOffset(manual), .manual) }
        guard let video else { return (0, .none) }
        if let found = automaticEntry(for: songKey, video: video, now: now), let offset = found.offset {
            return (MusicVideoTiming.clampedOffset(offset), found.origin)
        }
        if let estimate = estimates[Self.estimateKey(songKey, video)] { return (estimate, .estimated) }
        return (0, .none)
    }

    func hasManual(_ songKey: String?) -> Bool { songKey.map { values[$0] != nil } ?? false }

    /// What was found for the song and this video, unless it has expired.
    func automaticEntry(for songKey: String, video: String, now: Date) -> Automatic? {
        guard let entry = automatic[songKey], entry.video == video else { return nil }
        let lifetime = entry.offset == nil ? Self.unusableLifetime : Self.automaticLifetime
        return now.timeIntervalSince1970 - entry.at < lifetime ? entry : nil
    }

    // MARK: The user's offset

    /// The user's offset for the song, kept even at 0.
    func set(_ value: Double, for songKey: String) {
        guard value.isFinite else { return }
        values[songKey] = (MusicVideoTiming.clampedOffset(value) * 10).rounded() / 10
        defaults.set(values, forKey: Self.key)
    }

    /// Moves the offset in force (automatic or not) by `delta`; from then
    /// on it is the user's.
    func nudge(by delta: Double, for songKey: String, video: String? = nil, now: Date = Date()) {
        set(offset(for: songKey, video: video, now: now) + delta, for: songKey)
    }

    /// Back to the automatic offset (or none).
    func clearManual(_ songKey: String) {
        guard values.removeValue(forKey: songKey) != nil else { return }
        defaults.set(values, forKey: Self.key)
    }

    // MARK: Automatic offsets

    /// Stores what an alignment found (`offset` nil: nothing usable).
    func storeAutomatic(_ offset: Double?, origin: MusicVideoOffsetOrigin, for songKey: String, video: String, now: Date) {
        let bounded = offset.flatMap { $0.isFinite ? (MusicVideoTiming.clampedOffset($0) * 10).rounded() / 10 : nil }
        automatic[songKey] = Automatic(video: video, offset: bounded, origin: origin, at: now.timeIntervalSince1970)
        if automatic.count > Self.capacity {
            let oldest = automatic.sorted { $0.value.at < $1.value.at }
            for (key, _) in oldest.prefix(automatic.count - Self.capacity) { automatic.removeValue(forKey: key) }
        }
        saveAutomatic()
    }

    func forgetAutomatic(_ songKey: String) {
        guard automatic.removeValue(forKey: songKey) != nil else { return }
        saveAutomatic()
    }

    private func saveAutomatic() {
        let plist = automatic.mapValues { entry -> [String: Any] in
            var row: [String: Any] = ["video": entry.video, "at": entry.at,
                                      "kind": entry.origin == .audio ? "audio" : "captions"]
            if let offset = entry.offset { row["offset"] = offset }
            return row
        }
        defaults.set(plist, forKey: Self.automaticKey)
    }

    /// A starting guess from the video's length, for this session only.
    func setEstimate(_ value: Double?, for songKey: String, video: String) {
        let key = Self.estimateKey(songKey, video)
        if estimates[key] == value { return }
        estimates[key] = value
    }

    private static func estimateKey(_ songKey: String, _ video: String) -> String { songKey + "\u{1}" + video }
}

// MARK: - Resolver

/// What a video's captions gave.
enum MusicVideoCaptionFetch: Equatable, Sendable {
    /// Each usable track's cues, best first; empty when the video has
    /// none that could be read.
    case tracks([[MusicVideoCaptionCue]], duration: Double?)
    /// Offline, rate limited, or the page could not be read: try later.
    case unavailable
}

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

    /// Candidates checked for being a still image, per song.
    static let candidatesChecked = 3

    /// YouTube's results for "<artist> <title> official music video". A
    /// page with no results at all is taken as YouTube having changed its
    /// format (or asking for consent), not as "no video": it is not cached.
    /// The best candidates are checked in turn for being a still image (the
    /// cover over the audio); when the first three are all stills, the song
    /// has no video, remembered like "nothing official".
    private func searchYouTube(_ song: MusicVideoSong) async -> MusicVideoResolution {
        do {
            guard let url = YouTubeSearchPage.requestURL(title: song.title, artist: song.artist) else {
                return .missing(.lookupFailed)
            }
            let data = try await fetch(url)
            // About a megabyte of HTML and JSON: read off the main actor.
            let results = await parse(data)
            guard !results.isEmpty else { return .missing(.lookupFailed) }
            let candidates = YouTubeSearchPage.officialVideos(in: results, title: song.title, artist: song.artist)
            for video in candidates.prefix(Self.candidatesChecked) {
                if try await stillVerdict(video.videoID) != true { return .found(.youTube(video.videoID)) }
            }
            return .missing(.notFound)
        } catch FetchError.rateLimited {
            return .missing(.rateLimited)
        } catch {
            return .missing(.lookupFailed)
        }
    }

    /// Whether a video is a still image: remembered per video, else read
    /// from its three frames. The three small frames together take one
    /// rate-limit slot. Nil when the frames could not be read (not judged,
    /// not remembered: the video is given the benefit of the doubt).
    /// Rate limits and cancellation are passed on.
    private func stillVerdict(_ videoID: String) async throws -> Bool? {
        if let known = cache.still(videoID, now: now()) { return known }
        var fingerprints: [[UInt8]] = []
        for (index, url) in MusicVideoStillFrames.frameURLs(for: videoID).enumerated() {
            let data: Data
            do {
                data = try await fetch(url, usesSlot: index == 0)
            } catch FetchError.failed {
                return nil
            }
            guard let fingerprint = MusicVideoStillFrames.fingerprint(data) else { return nil }
            fingerprints.append(fingerprint)
        }
        guard let still = MusicVideoStillFrames.isStill(fingerprints) else { return nil }
        cache.storeStill(still, for: videoID, now: now())
        return still
    }

    /// Whether a pasted video looks like a still image, for a note in
    /// Settings; it plays either way. Nil when unknown.
    func pastedLooksStill(_ videoID: String) async -> Bool? {
        try? await stillVerdict(videoID)
    }

    // MARK: Captions

    /// The caption tracks worth trying for a video, each as cues, and the
    /// video's length from its watch page. Every request (the page, then
    /// at most two tracks) goes through the same rate limit and ephemeral
    /// session as the search; only the video ID is sent, to YouTube.
    /// `.unavailable` for a passing failure (offline, rate limited, a page
    /// that could not be read), not to be remembered.
    func captionCues(videoID: String, lyricsLanguage: String?) async -> MusicVideoCaptionFetch {
        guard let url = YouTubeCaptions.watchURL(videoID: videoID) else { return .unavailable }
        do {
            let data = try await fetch(url)
            let page = await Task.detached(priority: .utility) {
                YouTubeCaptions.page(fromHTML: String(decoding: data, as: UTF8.self))
            }.value
            guard let page else { return .unavailable }
            var tracks: [[MusicVideoCaptionCue]] = []
            for track in YouTubeCaptions.preferred(page.tracks, lyricsLanguage: lyricsLanguage) {
                guard let textURL = YouTubeCaptions.textURL(for: track) else { continue }
                // An empty body (no proof-of-origin token) gives no cues.
                let cues = YouTubeCaptions.cues(from: try await fetch(textURL))
                if !cues.isEmpty { tracks.append(cues) }
            }
            return .tracks(tracks, duration: page.duration)
        } catch {
            return .unavailable
        }
    }

    /// One rate-limited GET of a 200 reply. A lookup cancelled (the song
    /// skipped) before or while it waited its turn sends nothing and uses
    /// no slot, so rapid skipping cannot use up the allowance.
    private func fetch(_ url: URL, usesSlot: Bool = true) async throws -> Data {
        try Task.checkCancellation()
        if usesSlot {
            let wait = rateLimit.delay(at: uptime())
            if wait > longestWait { throw FetchError.rateLimited }
            if wait > 0 { await sleep(wait) }
            try Task.checkCancellation()
            rateLimit.record(at: uptime())
        } else if rateLimit.blockedUntil > uptime() {
            // Backing off after a 429: the rest of a frame set waits too.
            throw FetchError.rateLimited
        }
        let data: Data, response: HTTPURLResponse
        do {
            (data, response) = try await http.get(url)
        } catch {
            try Task.checkCancellation()
            throw FetchError.failed
        }
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
