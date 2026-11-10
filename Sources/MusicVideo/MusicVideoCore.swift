import Foundation

// The music-video wallpaper's decisions, free of AppKit, AVFoundation and
// WebKit so every rule here can be tested with plain values.

/// Where a song's video comes from.
enum MusicVideoSource: Hashable, Sendable {
    /// A video file in the Music library that AVFoundation can play.
    case local(URL)
    /// A YouTube video, played in the IFrame Player.
    case youTube(String)

    var origin: String {
        switch self {
        case .local: return "your Music library"
        case .youTube: return "YouTube"
        }
    }
}

/// The song a video is looked up for.
struct MusicVideoSong: Hashable, Sendable {
    var title: String
    var artist: String
    var album: String?
    var duration: Double
    /// Music's persistent ID, or Spotify's `spotify:track:` URI.
    var persistentID: String?

    init(title: String, artist: String, album: String? = nil, duration: Double, persistentID: String? = nil) {
        self.title = title
        self.artist = artist
        self.album = album
        self.duration = duration
        self.persistentID = persistentID
    }

    init(_ track: NowPlayingMonitor.Track) {
        self.init(title: track.title, artist: track.artist, album: track.album, duration: track.duration,
                  persistentID: track.persistentID)
    }

    /// Caches and offsets are keyed by the recording, as lyric offsets are.
    var key: String {
        LyricQuery(title: title, artist: artist, album: album, duration: duration).recordingKey
    }

}

// MARK: - YouTube IDs

enum YouTubeVideoID {
    /// YouTube's IDs are eleven characters of URL-safe base64.
    static func isValid(_ id: String) -> Bool {
        id.count == 11 && id.unicodeScalars.allSatisfy {
            ("a"..."z").contains($0) || ("A"..."Z").contains($0) || ("0"..."9").contains($0) || $0 == "-" || $0 == "_"
        }
    }

    /// The video ID in a watch, short, embed, shorts or YouTube Music link.
    static func fromURL(_ string: String) -> String? {
        guard let components = URLComponents(string: string), let host = components.host?.lowercased() else { return nil }
        let path = components.path.split(separator: "/").map(String.init)
        var candidate: String?
        if host == "youtu.be" || host == "www.youtu.be" {
            candidate = path.first
        } else if host == "youtube.com" || host.hasSuffix(".youtube.com") || host == "youtube-nocookie.com"
                    || host.hasSuffix(".youtube-nocookie.com") {
            if path.first == "watch" {
                candidate = components.queryItems?.first { $0.name == "v" }?.value
            } else if let first = path.first, ["embed", "shorts", "v", "live"].contains(first), path.count >= 2 {
                candidate = path[1]
            }
        }
        guard let candidate, isValid(candidate) else { return nil }
        return candidate
    }
}

// MARK: - YouTube search page

/// Reads YouTube's own search results page (no API key): the videos in its
/// `ytInitialData`. If YouTube changes the page, `results` comes back empty
/// and the lookup is reported as failed, never as "no video".
enum YouTubeSearchPage {
    struct Result: Equatable, Sendable {
        var videoID: String
        var title: String
        var channel: String
        /// The channel carries YouTube's verified-artist badge (the music
        /// note), from the result's `ownerBadges`.
        var verifiedArtist = false

        init(videoID: String, title: String, channel: String, verifiedArtist: Bool = false) {
            self.videoID = videoID
            self.title = title
            self.channel = channel
            self.verifiedArtist = verifiedArtist
        }
    }

    static func requestURL(title: String, artist: String) -> URL? {
        var components = URLComponents(string: "https://www.youtube.com/results")
        components?.queryItems = [URLQueryItem(name: "search_query", value: "\(artist) \(title) official music video")]
        return components?.url
    }

    /// `results(fromHTML:)` on a background thread, never the main actor:
    /// the page is about a megabyte. `parser` is a seam for tests.
    static func resultsOffMainActor(_ data: Data,
                                    parser: @escaping @Sendable (String) -> [Result] = { results(fromHTML: $0) })
        async -> [Result] {
        await Task.detached(priority: .utility) { parser(String(decoding: data, as: UTF8.self)) }.value
    }

    /// Video results in the page's `ytInitialData`, in page order.
    static func results(fromHTML html: String) -> [Result] {
        guard let json = initialData(in: html),
              let object = try? JSONSerialization.jsonObject(with: Data(json.utf8)) else { return [] }
        var found: [Result] = []
        collect(object, into: &found)
        return found
    }

    /// The official video among `results`: the first of `officialVideos`.
    static func officialVideo(in results: [Result], title: String, artist: String) -> Result? {
        officialVideos(in: results, title: title, artist: artist).first
    }

    /// Words in a title that mark an upload as something other than the
    /// video (whole words, any case): audio and art tracks, lyric and
    /// visualiser uploads, which are mostly a still cover, and live,
    /// cover and edited versions. Allowed when the song's own title has them.
    static let excludedWords = ["lyric", "lyrics", "audio", "official audio", "audio only", "art track",
                                "visualizer", "visualiser", "live", "cover", "karaoke", "instrumental", "reaction",
                                "remix", "sped up", "slowed", "8d", "nightcore", "tutorial"]

    /// Candidate official videos among `results`, best first. Each must:
    /// - be this song: its title, artist and qualifiers taken off, equals
    ///   the song title (`MusicVideoTitleMatch`), not merely contains it;
    /// - come from the artist: the channel is the artist's own (its name
    ///   is or contains the artist's), a VEVO channel, or carries YouTube's
    ///   verified-artist badge; never an auto-generated "- Topic" channel;
    /// - not be a live, concert or fan upload, nor any of `excludedWords`.
    /// "Official music video" ranks above "official video", which ranks
    /// above "official" or "music video"; ties keep YouTube's order.
    static func officialVideos(in results: [Result], title: String, artist: String) -> [Result] {
        let song = LyricQuery.normalize(title)
        let singers = MusicVideoTitleMatch.artistParts(artist)
        guard !song.isEmpty, !singers.isEmpty else { return [] }
        var ranked: [(result: Result, score: Int, index: Int)] = []
        for (index, result) in results.prefix(12).enumerated() {
            let name = LyricQuery.normalize(result.title)
            let channel = LyricQuery.normalize(result.channel)
            let channelKey = CoverLookup.normal(result.channel)
            guard MusicVideoTitleMatch.matches(videoTitle: result.title, songTitle: title, artist: artist) else { continue }
            guard !channel.hasSuffix(" topic") else { continue }
            let ownChannel = singers.contains { Self.channel(channelKey, isOf: $0) }
            let vevo = channelKey.hasSuffix("vevo")
            guard ownChannel || vevo || result.verifiedArtist else { continue }
            if MusicVideoTitleMatch.isLiveOrFanUpload(result.title, songTitle: title) { continue }
            if excludedWords.contains(where: { containsWords(name, $0) && !containsWords(song, $0) }) { continue }
            var score = 1
            if containsWords(name, "official music video") { score += 4 }
            else if containsWords(name, "official video") { score += 3 }
            else if containsWords(name, "official") || containsWords(name, "music video") || containsWords(name, "mv") { score += 1 }
            if vevo || ownChannel { score += 1 }
            ranked.append((result, score, index))
        }
        return ranked.sorted { $0.score != $1.score ? $0.score > $1.score : $0.index < $1.index }.map(\.result)
    }

    /// A channel named after the artist: the name itself, or starting or
    /// ending with it ("WallowsOfficial", "Ayase / YOASOBI"). A short name
    /// inside another ("IU" in "Liquid Music") is not the artist's.
    static func channel(_ channelKey: String, isOf artist: String) -> Bool {
        guard !artist.isEmpty else { return false }
        return channelKey == artist || channelKey.hasPrefix(artist) || channelKey.hasSuffix(artist)
            || (artist.count >= 5 && channelKey.contains(artist))
    }

    /// Whole-word containment on normalised text.
    private static func containsWords(_ text: String, _ words: String) -> Bool {
        guard !words.isEmpty else { return false }
        return " \(text) ".contains(" \(words) ")
    }

    private static func initialData(in html: String) -> String? {
        for marker in ["var ytInitialData = ", "window[\"ytInitialData\"] = ", "ytInitialData = "] {
            guard let start = html.range(of: marker) else { continue }
            let rest = html[start.upperBound...]
            guard let end = rest.range(of: ";</script>") else { continue }
            return String(rest[..<end.lowerBound])
        }
        return nil
    }

    private static func collect(_ node: Any, into found: inout [Result]) {
        if let dictionary = node as? [String: Any] {
            if let renderer = dictionary["videoRenderer"] as? [String: Any],
               let id = renderer["videoId"] as? String, YouTubeVideoID.isValid(id) {
                let title = text(renderer["title"]) ?? ""
                let channel = text(renderer["ownerText"]) ?? text(renderer["longBylineText"]) ?? ""
                found.append(Result(videoID: id, title: title, channel: channel,
                                    verifiedArtist: hasVerifiedArtistBadge(renderer["ownerBadges"])))
            }
            for (key, value) in dictionary where key != "videoRenderer" { collect(value, into: &found) }
        } else if let array = node as? [Any] {
            for value in array { collect(value, into: &found) }
        }
    }

    /// `ownerBadges: [{metadataBadgeRenderer: {style: "BADGE_STYLE_TYPE_VERIFIED_ARTIST"}}]`.
    static func hasVerifiedArtistBadge(_ node: Any?) -> Bool {
        guard let badges = node as? [Any] else { return false }
        return badges.contains { badge in
            guard let renderer = (badge as? [String: Any])?["metadataBadgeRenderer"] as? [String: Any] else { return false }
            return renderer["style"] as? String == "BADGE_STYLE_TYPE_VERIFIED_ARTIST"
        }
    }

    private static func text(_ node: Any?) -> String? {
        guard let node = node as? [String: Any] else { return nil }
        if let simple = node["simpleText"] as? String { return simple }
        if let runs = node["runs"] as? [[String: Any]] {
            let joined = runs.compactMap { $0["text"] as? String }.joined()
            return joined.isEmpty ? nil : joined
        }
        return nil
    }
}

// MARK: - Position mapping

/// Song position to video position. Official videos often open with a
/// scene before the music starts; the per-song offset says how far into
/// the video the song begins (positive: the video runs ahead).
enum MusicVideoTiming {
    /// The offsets the nudge can reach, in seconds.
    static let offsetRange: ClosedRange<Double> = -300...300

    static func clampedOffset(_ value: Double) -> Double {
        value.isFinite ? min(offsetRange.upperBound, max(offsetRange.lowerBound, value)) : 0
    }

    /// Where the video should be when the song is at `songTime`, or nil
    /// when that is past the video's end (or before its start): the video
    /// holds still there instead of looping or jumping.
    static func videoTime(songTime: Double, offset: Double, videoDuration: Double?) -> Double? {
        guard songTime.isFinite else { return nil }
        let time = songTime + clampedOffset(offset)
        if time < 0 { return nil }
        if let videoDuration, videoDuration.isFinite, videoDuration > 0, time >= videoDuration - 0.05 { return nil }
        return time
    }

    /// "+12.0 s", "−0.5 s", "0.0 s".
    static func format(_ seconds: Double) -> String {
        abs(seconds) < 0.05 ? "0.0 s" : String(format: "%+.1f s", seconds).replacingOccurrences(of: "-", with: "−")
    }
}

/// What the sync loop does to the video on each check.
enum MusicVideoDrift {
    struct Sample: Equatable {
        /// The video's own position and whether it is playing.
        var videoTime: Double
        var videoPlaying: Bool
        /// Still loading or buffering: seeking again would only restart that.
        var buffering = false
    }

    enum Action: Equatable {
        case none
        case play
        case pause
        case seek(to: Double, thenPlay: Bool)
    }

    /// Drift beyond this is corrected with a seek; the goal is under 0.5 s.
    static let seekThreshold = 0.35
    /// A seek needs time to land; until then the old position is expected.
    static let seekSettle = 1.2
    /// A held video (paused, or before its start) is only moved for a real jump.
    static let pausedThreshold = 1.0

    /// - Parameters:
    ///   - target: where the video should be (`MusicVideoTiming.videoTime`),
    ///     nil when the song is past the video's end or before its start.
    ///   - shouldPlay: the song is playing and the surface is visible.
    ///   - sinceLastSeek: seconds since the last seek this loop issued.
    ///   - seekLead: added to a seek target, for players that take a moment
    ///     to resume after seeking.
    ///   - threshold: drift sought away while playing (`seekThreshold`).
    static func decide(target: Double?, sample: Sample, shouldPlay: Bool, sinceLastSeek: Double,
                       seekLead: Double = 0, threshold: Double = seekThreshold) -> Action {
        guard let target else {
            return sample.videoPlaying ? .pause : .none
        }
        let drift = sample.videoTime - target
        let settled = sinceLastSeek >= seekSettle
        if !shouldPlay {
            if sample.videoPlaying { return .pause }
            if settled && abs(drift) > pausedThreshold { return .seek(to: target, thenPlay: false) }
            return .none
        }
        if settled && !sample.buffering && abs(drift) > threshold {
            return .seek(to: target + seekLead, thenPlay: true)
        }
        return sample.videoPlaying || sample.buffering ? .none : .play
    }
}

// MARK: - Rate limiting

/// Spaces the online lookups so YouTube's search page is never hammered:
/// two seconds apart, eight a minute at most. A 429 backs off for as long
/// as the reply asks, or a minute.
struct MusicVideoRateLimit: Equatable {
    var minimumGap: TimeInterval = 2
    var window: TimeInterval = 60
    var maximumPerWindow = 8
    var defaultBackOff: TimeInterval = 60
    private(set) var sent: [TimeInterval] = []
    private(set) var blockedUntil: TimeInterval = 0

    init(minimumGap: TimeInterval = 2, window: TimeInterval = 60, maximumPerWindow: Int = 8,
         defaultBackOff: TimeInterval = 60) {
        self.minimumGap = minimumGap
        self.window = window
        self.maximumPerWindow = maximumPerWindow
        self.defaultBackOff = defaultBackOff
    }

    /// Seconds to wait before the next request may go, 0 for now.
    func delay(at now: TimeInterval) -> TimeInterval {
        var wait = max(0, blockedUntil - now)
        if let last = sent.last { wait = max(wait, last + minimumGap - now) }
        let recent = sent.filter { $0 > now - window }
        if recent.count >= maximumPerWindow, let oldest = recent.first {
            wait = max(wait, oldest + window - now)
        }
        return max(0, wait)
    }

    mutating func record(at now: TimeInterval) {
        sent = sent.filter { $0 > now - window } + [now]
    }

    /// A 429 or 503: no request until `retryAfter` (or the default) has passed.
    mutating func backOff(at now: TimeInterval, retryAfter: TimeInterval?) {
        let seconds = retryAfter.flatMap { $0.isFinite && $0 > 0 ? min($0, 3600) : nil } ?? defaultBackOff
        blockedUntil = max(blockedUntil, now + seconds)
    }
}

// MARK: - Resolution

/// Why a song has no video on screen.
enum MusicVideoMiss: Equatable, Sendable {
    /// Not in the library, and online lookups are off.
    case notInLibrary
    /// The library's video is copy-protected, and online lookups are off.
    case protectedInLibrary
    /// Looked up online and nothing official was found.
    case notFound
    /// YouTube refused to play the video outside its own site.
    case notEmbeddable
    /// Too many lookups for now; tried again later.
    case rateLimited
    /// The lookup failed (offline, or a service did not answer).
    case lookupFailed
    /// YouTube's player did not load or stopped (offline, blocked, a
    /// passing player error). Passing: never cached, tried again later.
    case playerUnreachable
    /// The library's own file would not play here.
    case localFileFailed
    /// Live radio, adverts and streams have no video.
    case notASong
}

/// How a player that could not show a video failed.
enum MusicVideoPlaybackFailure: Equatable, Sendable {
    /// YouTube refused the video for good here (not embeddable, removed,
    /// a bad ID): remembered for the song.
    case refused
    /// Something passing (offline, a player error, no answer in time):
    /// not remembered, tried again later.
    case transient
}

enum MusicVideoResolution: Equatable, Sendable {
    case found(MusicVideoSource)
    case missing(MusicVideoMiss)

    var source: MusicVideoSource? {
        if case .found(let source) = self { return source }
        return nil
    }

    /// Whether the answer may be stored: transient failures are retried.
    var isCacheable: Bool {
        switch self {
        case .found(.youTube): return true
        case .found(.local): return false
        case .missing(let miss): return miss == .notFound || miss == .notEmbeddable
        }
    }
}
