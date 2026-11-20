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
    /// "Nothing official" stored before 5 is looked up again: 4 also
    /// searches a version's plain title ("Feel So Close" for "Feel So
    /// Close - Radio Edit", `MusicVideoTitleMatch.titleVersions`), 5 the
    /// song's original title ("咲き誇れ" for "The Bloom") and a quoted title,
    /// 6 "<artist> <title>" alone, as Search YouTube… sends it, 7 titles
    /// in 【】 or 《》 and traditional Chinese for simplified ("周杰倫
    /// Jay Chou【告白氣球 Love Confession】Official MV" for 告白气球).
    static let missVersion = 7

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
        guard age < Self.missingLifetime, (entry["v"] as? Int ?? 1) >= Self.missVersion else { return nil }
        return .missing(entry["miss"] as? String == "embed" ? .notEmbeddable : .notFound)
    }

    func store(_ resolution: MusicVideoResolution, for songKey: String, now: Date) {
        guard resolution.isCacheable else { return }
        var entry: [String: Any] = ["at": now.timeIntervalSince1970]
        switch resolution {
        case .found(.youTube(let id)): entry["video"] = id; entry["v"] = Self.version
        case .missing(.notEmbeddable): entry["miss"] = "embed"; entry["v"] = Self.missVersion
        default: entry["miss"] = "none"; entry["v"] = Self.missVersion
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
    /// The user's own nudge, tap or drag for this video.
    case manual
    /// Measured from the video's captions against the song's timed lyrics
    /// (`MusicVideoCaptionSync`).
    case captions
    /// Guessed from how much longer the video is than the song.
    case estimated
}

/// How far into each song's video the song starts, per recording.
///
/// Three layers, the first present wins: the user's nudge, tap or drag
/// (`values`, saved, kept even at 0 once set, for the video it was set
/// on), then the offset measured from the video's captions, then a guess
/// from the video's length (both kept in memory only, per song and video).
/// Captions set the starting timing; any timing the user saved for the
/// video wins over them, and captions measured again later never undo it.
/// Reset forgets the user's timing, so captions (or the estimate) apply.
///
/// Offsets the earlier automatic timing stored (`retiredAutomaticKey`) are
/// not used: they are dropped from the store when it loads.
@MainActor
final class MusicVideoOffsets: ObservableObject {
    static let key = "custom.musicVideoOffsets"
    /// Where automatic offsets used to be kept; cleared on load.
    static let retiredAutomaticKey = "custom.musicVideoAutoOffsets"
    /// The video each of the user's offsets was set on.
    static let manualVideoKey = "custom.musicVideoOffsetVideos"

    /// The user's offsets.
    @Published private(set) var values: [String: Double]
    /// The video (`videoKey`) each of `values` was set on. A song without
    /// one was set before videos were told apart: it applies to any video.
    @Published private(set) var manualVideos: [String: String]
    @Published private(set) var estimates: [String: Double] = [:]
    /// Measured from captions, per song and video (`estimateKey`).
    @Published private(set) var captioned: [String: Double] = [:]
    private let defaults: UserDefaults

    init(defaults: UserDefaults) {
        self.defaults = defaults
        let manual = defaults.dictionary(forKey: Self.key) as? [String: Double] ?? [:]
        values = manual
        manualVideos = (defaults.dictionary(forKey: Self.manualVideoKey) as? [String: String] ?? [:])
            .filter { manual[$0.key] != nil }
        // The real preferences are left alone inside the test host.
        if defaults.object(forKey: Self.retiredAutomaticKey) != nil,
           defaults !== UserDefaults.standard || !AppRuntime.isUnitTesting {
            defaults.removeObject(forKey: Self.retiredAutomaticKey)
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
    func offset(for songKey: String?, video: String? = nil) -> Double {
        resolved(songKey, video: video).offset
    }

    func origin(for songKey: String?, video: String? = nil) -> MusicVideoOffsetOrigin {
        resolved(songKey, video: video).origin
    }

    private func resolved(_ songKey: String?, video: String?) -> (offset: Double, origin: MusicVideoOffsetOrigin) {
        guard let songKey else { return (0, .none) }
        if let manual = values[songKey], manualApplies(songKey, video: video) {
            return (MusicVideoTiming.clampedOffset(manual), .manual)
        }
        if let video, let measured = captioned[Self.estimateKey(songKey, video)] {
            return (MusicVideoTiming.clampedOffset(measured), .captions)
        }
        guard let video else { return (0, .none) }
        if let estimate = estimates[Self.estimateKey(songKey, video)] { return (estimate, .estimated) }
        return (0, .none)
    }

    /// The user set the offset for the song (and this video, when given).
    func hasManual(_ songKey: String?, video: String? = nil) -> Bool {
        songKey.map { values[$0] != nil && manualApplies($0, video: video) } ?? false
    }

    /// A tap or nudge is kept for the video it was made on: another upload
    /// of the song (the next candidate after a refusal, a pasted link, a
    /// later search) has its own intro and does not inherit it.
    private func manualApplies(_ songKey: String, video: String?) -> Bool {
        guard let video, let owner = manualVideos[songKey] else { return true }
        return owner == video
    }

    // MARK: The user's offset

    /// The user's offset for the song, kept even at 0, for `video` (the
    /// one on screen) when given, and saved. To a hundredth of a second, so
    /// fine nudges add up exactly. It wins over timing from captions.
    func set(_ value: Double, for songKey: String, video: String? = nil) {
        guard value.isFinite else { return }
        values[songKey] = (MusicVideoTiming.clampedOffset(value) * 100).rounded() / 100
        if let video { manualVideos[songKey] = video } else { manualVideos.removeValue(forKey: songKey) }
        save()
    }

    /// Moves the offset in force (from captions, estimated or not) by
    /// `delta`; from then on it is the user's, for this video.
    func nudge(by delta: Double, for songKey: String, video: String? = nil) {
        set(offset(for: songKey, video: video) + delta, for: songKey, video: video)
    }

    /// The user's own offset for a song, as stored: its value, the video
    /// it was set on (nil: set before videos were told apart).
    struct ManualEntry: Equatable, Sendable {
        var value: Double
        var video: String?
    }

    func manualEntry(_ songKey: String) -> ManualEntry? {
        values[songKey].map { ManualEntry(value: $0, video: manualVideos[songKey]) }
    }

    /// Puts back what `manualEntry` read (nil: there was none).
    func restoreManual(_ entry: ManualEntry?, for songKey: String) {
        if let entry {
            set(entry.value, for: songKey, video: entry.video)
        } else {
            clearManual(songKey)
        }
    }

    /// Forgets the user's offset: back to the timing from captions, else
    /// the estimate (or none). With `video`, only when it applies to that
    /// video (timing set on another upload of the song stays).
    func clearManual(_ songKey: String, video: String? = nil) {
        if let video, origin(for: songKey, video: video) != .manual { return }
        guard values.removeValue(forKey: songKey) != nil else { return }
        manualVideos.removeValue(forKey: songKey)
        save()
    }

    private func save() {
        defaults.set(values, forKey: Self.key)
        defaults.set(manualVideos, forKey: Self.manualVideoKey)
    }

    // MARK: Estimates

    /// A starting guess from the video's length, for this session only.
    func setEstimate(_ value: Double?, for songKey: String, video: String) {
        let key = Self.estimateKey(songKey, video)
        if estimates[key] == value { return }
        estimates[key] = value
    }

    // MARK: From captions

    /// The offset the video's captions gave, for this session; nil drops
    /// it. The user's own timing for the video, set over captions, stays
    /// in force.
    func setCaptioned(_ value: Double?, for songKey: String, video: String) {
        let key = Self.estimateKey(songKey, video)
        let value = value.flatMap { $0.isFinite ? $0 : nil }
        if captioned[key] == value { return }
        captioned[key] = value
    }

    /// The offset from captions for the song and video, if any.
    func captionedOffset(_ songKey: String, video: String) -> Double? { captioned[Self.estimateKey(songKey, video)] }

    private static func estimateKey(_ songKey: String, _ video: String) -> String { songKey + "\u{1}" + video }
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
    private let originalTitle: @Sendable (MusicVideoSong) async -> String?
    private(set) var rateLimit: MusicVideoRateLimit
    /// Waits longer than this are not sat out: the lookup reports
    /// `.rateLimited` and is tried again later.
    var longestWait: TimeInterval = 8

    init(library: MusicVideoLibrary, playability: MusicVideoPlayability, http: MusicVideoHTTP, cache: MusicVideoCache,
         links: MusicVideoLinks, rateLimit: MusicVideoRateLimit = MusicVideoRateLimit(),
         now: @escaping () -> Date = Date.init,
         uptime: @escaping () -> TimeInterval = { ProcessInfo.processInfo.systemUptime },
         sleep: @escaping (TimeInterval) async -> Void = { try? await Task.sleep(for: .seconds($0)) },
         parse: @escaping @Sendable (Data) async -> [YouTubeSearchPage.Result] = { await YouTubeSearchPage.resultsOffMainActor($0) },
         originalTitle: @escaping @Sendable (MusicVideoSong) async -> String? = { _ in nil }) {
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
        self.originalTitle = originalTitle
    }

    /// The song's title in its own language when the player lists it under
    /// another ("The Bloom" for YOASOBI's "咲き誇れ"): the lyric lookup's
    /// `OriginalTitles`, usually already known from finding its lyrics.
    /// Only with "Search under other names" on.
    nonisolated static func originalTitle(of song: MusicVideoSong) async -> String? {
        guard OriginalTitles.enabled else { return nil }
        let query = LyricQuery(title: song.title, artist: song.artist, album: song.album, duration: song.duration)
        return await OriginalTitles.shared.original(for: query)?.title
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

    // MARK: Looking ahead

    /// Slots of the rate limit a look-ahead leaves free in its window, so
    /// the song that starts next never waits on one.
    static let lookAheadReserve = 4
    /// The most slots one lookup can take: the search and each candidate's still check.
    static var lookupSlots: Int { 1 + candidatesChecked }

    /// Whether a look-ahead may search now: no wait, and room in the window
    /// for its own worst case with `lookAheadReserve` slots still free.
    var hasLookAheadHeadroom: Bool {
        let now = uptime()
        let recent = rateLimit.sent.filter { $0 > now - rateLimit.window }.count
        return rateLimit.delay(at: now) == 0
            && recent + Self.lookupSlots + Self.lookAheadReserve <= rateLimit.maximumPerWindow
    }

    /// Finds an upcoming song's video ahead of time (`QueueLookAhead`) and
    /// remembers it as `resolve` would, so the song's own lookup is a cache
    /// hit. Online search only (with it on): a pasted link or a remembered
    /// answer is enough, a song with a video in the library needs nothing
    /// (that video is the answer, not remembered: the library is asked
    /// again as the song starts), and nothing is searched without
    /// `hasLookAheadHeadroom`. Nil when nothing was looked up: try later.
    func lookAhead(_ song: MusicVideoSong) async -> MusicVideoResolution? {
        guard !song.title.trimmingCharacters(in: .whitespaces).isEmpty else { return nil }
        let key = song.key
        if let pasted = links.videoID(for: key) { return .found(.youTube(pasted)) }
        if let cached = cache.lookup(key, now: now()) { return cached }
        // Without room, nothing is asked, not even the library: a later plan tries again.
        guard hasLookAheadHeadroom else { return nil }
        if let local = await library.localVideo(for: song) { return .found(.local(local)) }
        guard !Task.isCancelled, hasLookAheadHeadroom else { return nil }
        let result = await searchYouTube(song)
        guard !Task.isCancelled || result.isCacheable else { return nil }
        cache.store(result, for: key, now: now())
        return result
    }

    /// Carries the answer found for a song looked up ahead under slightly
    /// different details (MediaRemote's duration) over to the playing
    /// song's key, unless that has its own.
    func adopt(from prefetched: MusicVideoSong, to playing: MusicVideoSong) {
        let from = prefetched.key, to = playing.key
        guard from != to, cache.lookup(to, now: now()) == nil, let answer = cache.lookup(from, now: now()) else { return }
        cache.store(answer, for: to, now: now())
    }

    /// Videos YouTube's player refused for each song this session; the
    /// song's next search skips them.
    private var refused: [String: Set<String>] = [:]
    /// Refused videos per song before "can't be embedded" is remembered.
    static let refusalsTried = 3

    /// The player found the video cannot be embedded. With `videoID` (a
    /// found video, online lookups on), the song's next candidate is tried:
    /// true means look the song up again. Otherwise, or once
    /// `refusalsTried` videos were refused, "can't be embedded" is
    /// remembered, so the song is not looked up again for a while. A pasted
    /// link never comes here: the user may want to fix it.
    @discardableResult
    func markNotEmbeddable(_ song: MusicVideoSong, videoID: String? = nil) -> Bool {
        if let videoID {
            if refused.count > 200 { refused.removeAll() }
            refused[song.key, default: []].insert(videoID)
            if refused[song.key, default: []].count < Self.refusalsTried {
                cache.remove(song.key)
                return true
            }
        }
        cache.store(.missing(.notEmbeddable), for: song.key, now: now())
        return false
    }

    private enum FetchError: Error { case rateLimited, failed }

    /// Candidates checked for being a still image, per song.
    static let candidatesChecked = 3

    /// YouTube's results for "<artist> <title> official music video", then,
    /// when nothing official is among them, for the song's plain title
    /// (`MusicVideoTitleMatch.titleVersions`: "Feel So Close" for "Feel So
    /// Close - Radio Edit"), then for its original title when the player
    /// lists it under another ("咲き誇れ" for YOASOBI's "The Bloom"), then for
    /// "<artist> <title>" alone, as Search YouTube… sends it. A page
    /// with no results at all is taken as YouTube having changed its
    /// format (or asking for consent), not as "no video": it is not cached.
    /// The best candidates are checked in turn for being a still image (the
    /// cover over the audio); when the first three are all stills, the song
    /// has no video, remembered like "nothing official".
    private func searchYouTube(_ song: MusicVideoSong) async -> MusicVideoResolution {
        do {
            var found: [YouTubeSearchPage.Result] = []
            // Each search: the results page to read, and the song title a video must name.
            func search(_ url: URL?, matching title: String) async throws -> Bool {
                guard let url else { return false }
                let data = try await fetch(url)
                // About a megabyte of HTML and JSON: read off the main actor.
                let results = await parse(data)
                guard !results.isEmpty else { return false }
                found = YouTubeSearchPage.officialVideos(in: results, title: title, artist: song.artist,
                                                         songDuration: song.duration)
                return true
            }
            func search(_ query: String, matching title: String) async throws -> Bool {
                try await search(YouTubeSearchPage.requestURL(title: query, artist: song.artist), matching: title)
            }
            for title in MusicVideoTitleMatch.titleVersions(song.title) where found.isEmpty {
                guard try await search(title, matching: song.title) else { return .missing(.lookupFailed) }
            }
            if found.isEmpty, let original = await originalTitle(song),
               MusicVideoTitleMatch.songKey(original) != MusicVideoTitleMatch.songKey(song.title) {
                // A page with no results is YouTube's format or a consent page,
                // not "no video": not remembered as a miss (as the first search).
                guard try await search(original, matching: original) else { return .missing(.lookupFailed) }
            }
            // Last, what Search YouTube… sends: "<artist> <title>" alone. The
            // owner found that search picked videos the automatic one missed
            // (20 Nov 2026): "official music video" can push a label's or an
            // anime's upload of the song out of the results read. The same
            // checks pick from its results, so a lyric video or cover is still
            // never played.
            if found.isEmpty {
                guard try await search(YouTubeSearchPage.requestURL(query: MusicVideoSearchModel.prefill(for: song)),
                                       matching: song.title) else { return .missing(.lookupFailed) }
            }
            let skipped = refused[song.key] ?? []
            let candidates = found.filter { !skipped.contains($0.videoID) }
            // Every official video was refused by the player already.
            if candidates.isEmpty && !found.isEmpty { return .missing(.notEmbeddable) }
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

    // MARK: Search YouTube… (the picker)

    /// YouTube's results for what the user typed in Search YouTube…, in
    /// YouTube's order: the same rate limit and ephemeral session as the
    /// automatic lookup, nothing cached. A page with no results data at
    /// all (YouTube changed it, or asks for consent) is `.failed`, not
    /// "no videos".
    func searchResults(for query: String) async -> MusicVideoSearchOutcome {
        guard let url = YouTubeSearchPage.requestURL(query: query) else { return .failed }
        do {
            let data = try await fetch(url)
            let results = await parse(data)
            if results.isEmpty && data.range(of: Data("ytInitialData".utf8)) == nil { return .failed }
            return .results(results)
        } catch FetchError.rateLimited {
            return .rateLimited
        } catch {
            return Task.isCancelled ? .cancelled : .failed
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
