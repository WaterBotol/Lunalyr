import AppKit
import AVFoundation
import Foundation

/// Music videos in the Music library, through its AppleScript dictionary:
/// `file track`s whose `video kind` is `music video`, with the file's
/// `location`. Cloud-only and streamed videos have no location and are
/// skipped; purchased videos that are copy-protected are filtered out by
/// `AVMusicVideoPlayability`.
///
/// The library-wide `whose` is slow on a big library and holds up the
/// now-playing poll on the shared script queue, so it runs once per Music
/// session (Music's process, refreshed after `listLifetime`), at the
/// lowest priority, and each song change is matched against that list here.
///
/// Only read, and only while Music is already running and macOS has already
/// let Lunalyr read it (`mayRead`): it never launches Music, never raises
/// the permission prompt, and never controls playback.
actor AppleMusicVideoLibrary: MusicVideoLibrary {
    /// Music's process for this session, nil while it is not running.
    typealias Session = @Sendable () async -> String?
    /// Every music video in the library with a file, nil when the read failed.
    typealias Reader = @Sendable () async -> [Hit]?

    static let listLifetime: TimeInterval = 3600
    /// Behind every other script on the shared queue, covers included.
    static let priority: ScriptDesk.Priority = .artwork

    private let mayRead: @Sendable () async -> Bool
    private let session: Session
    private let reader: Reader
    private let uptime: @Sendable () -> TimeInterval
    private var list: (session: String, at: TimeInterval, hits: [Hit])?
    private var reading: (session: String, task: Task<[Hit]?, Never>)?

    init(mayRead: @escaping @Sendable () async -> Bool,
         session: @escaping Session = { AppleMusicVideoLibrary.musicSession() },
         reader: @escaping Reader = { await AppleMusicVideoLibrary.readLibrary() },
         uptime: @escaping @Sendable () -> TimeInterval = { ProcessInfo.processInfo.systemUptime }) {
        self.mayRead = mayRead
        self.session = session
        self.reader = reader
        self.uptime = uptime
    }

    func localVideo(for song: MusicVideoSong) async -> URL? {
        guard let hits = await videos() else { return nil }
        return Self.pick(hits, for: song)
    }

    /// The session's list, read once (one read at a time, shared by every
    /// caller). A failed read is not kept, so the next song tries again.
    private func videos() async -> [Hit]? {
        guard await mayRead(), let current = await session() else { return nil }
        if let list, list.session == current, uptime() - list.at < Self.listLifetime { return list.hits }
        let task: Task<[Hit]?, Never>
        if let reading, reading.session == current {
            task = reading.task
        } else {
            let reader = self.reader
            task = Task { await reader() }
            reading = (current, task)
        }
        let hits = await task.value
        if reading?.session == current { reading = nil }
        if let hits { list = (current, uptime(), hits) }
        return hits
    }

    /// Music's process identifier and launch time: a new value after Music
    /// quits and opens again.
    static func musicSession() -> String? {
        guard let app = NSRunningApplication.runningApplications(withBundleIdentifier: AppleMusicSearch.bundleID)
            .first(where: { !$0.isTerminated }) else { return nil }
        return "\(app.processIdentifier)-\(app.launchDate?.timeIntervalSince1970 ?? 0)"
    }

    static func readLibrary() async -> [Hit]? {
        guard !AppRuntime.isUnitTesting else { return nil }
        do {
            let reply = try await ScriptDesk.shared.run(
                PlayerScript(name: "music-video.library", source: script, cacheable: false),
                for: .appleMusic, priority: priority)
            return parse(reply)
        } catch {
            return nil
        }
    }

    /// Up to `limit` videos, read in three bulk events (names, artists,
    /// locations) rather than one per track.
    static let limit = 5000
    static var script: String {
        PlayerScriptSource.query(AppleMusicSearch.bundleID, """
        set hits to {}
        try
        set namesList to name of (every file track of library playlist 1 whose video kind is music video)
        set artistsList to artist of (every file track of library playlist 1 whose video kind is music video)
        set locationsList to location of (every file track of library playlist 1 whose video kind is music video)
        on error number errorNumber
        if errorNumber is not in \(PlayerScriptSource.tolerated) then error number errorNumber
        return {"ok", hits}
        end try
        set limitCount to count of namesList
        if (count of artistsList) is not limitCount or (count of locationsList) is not limitCount then error number -1700
        if limitCount > \(limit) then set limitCount to \(limit)
        repeat with i from 1 to limitCount
        set f to item i of locationsList
        try
        if f is not missing value then set end of hits to {item i of namesList, item i of artistsList, POSIX path of f}
        end try
        end repeat
        return {"ok", hits}
        """)
    }

    struct Hit: Equatable, Sendable {
        var title: String
        var artist: String
        var path: String
    }

    /// The hits, or nil when the reply is not the script's own answer.
    static func parse(_ reply: NSAppleEventDescriptor) -> [Hit]? {
        let items = reply.scriptItems
        guard items.first?.scriptText == "ok", items.count >= 2 else { return nil }
        return items[1].scriptItems.compactMap { row in
            let fields = row.scriptItems
            guard fields.count >= 3, let title = fields[0].scriptText, let path = fields[2].scriptText,
                  !path.isEmpty else { return nil }
            return Hit(title: title, artist: fields[1].scriptText ?? "", path: path)
        }
    }

    /// The hit that is this song by this artist. The title must be the
    /// same song (`MusicVideoTitleMatch`, the rule the YouTube pick uses),
    /// never just contain it, and not a live recording unless the song is.
    static func pick(_ hits: [Hit], for song: MusicVideoSong) -> URL? {
        let ranked = hits.compactMap { hit -> (Hit, Double)? in
            guard MusicVideoTitleMatch.matches(videoTitle: hit.title, songTitle: song.title, artist: song.artist),
                  !MusicVideoTitleMatch.isLiveOrFanUpload(hit.title, songTitle: song.title) else { return nil }
            let artist = song.artist.isEmpty ? 1 : LyricQuery.similarity(hit.artist, song.artist)
            guard artist >= 0.5 else { return nil }
            return (hit, artist + (CoverLookup.normal(hit.title) == CoverLookup.normal(song.title) ? 1 : 0))
        }
        guard let best = ranked.max(by: { $0.1 < $1.1 }) else { return nil }
        return URL(fileURLWithPath: best.0.path)
    }
}

/// AVFoundation's own answer: playable, and not FairPlay-protected.
struct AVMusicVideoPlayability: MusicVideoPlayability {
    func check(_ url: URL) async -> MusicVideoPlayabilityResult {
        guard FileManager.default.isReadableFile(atPath: url.path) else { return .unplayable }
        let asset = AVURLAsset(url: url)
        guard let (playable, protected) = try? await asset.load(.isPlayable, .hasProtectedContent) else {
            return .unplayable
        }
        if protected { return .protected }
        return playable ? .playable : .unplayable
    }
}

/// Plain GETs on an ephemeral session: no cookies kept, nothing cached on
/// disk. Refuses to run inside the test host.
struct URLSessionMusicVideoHTTP: MusicVideoHTTP {
    static let session: URLSession = {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 8
        configuration.httpCookieAcceptPolicy = .never
        configuration.httpShouldSetCookies = false
        return URLSession(configuration: configuration)
    }()

    struct Refused: Error {}
    static let webKitAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15"

    func get(_ url: URL) async throws -> (Data, HTTPURLResponse) {
        guard !AppRuntime.isUnitTesting else { throw Refused() }
        var request = URLRequest(url: url)
        // YouTube serves its results data only to a browser; it is WebKit
        // asking, after all. Everyone else is told it is Lunalyr.
        let youTube = url.host?.hasSuffix("youtube.com") == true
        request.setValue(youTube ? Self.webKitAgent : "Lunalyr (macOS)", forHTTPHeaderField: "User-Agent")
        request.setValue("en", forHTTPHeaderField: "Accept-Language")
        let (data, response) = try await Self.session.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw Refused() }
        return (data, http)
    }
}
