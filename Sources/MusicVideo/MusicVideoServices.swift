import AVFoundation
import Foundation

/// Music videos in the Music library, through its AppleScript dictionary:
/// `file track`s whose `video kind` is `music video`, with the file's
/// `location`. Cloud-only and streamed videos have no location and are
/// skipped; purchased videos that are copy-protected are filtered out by
/// `AVMusicVideoPlayability`.
///
/// Only read, and only while Music is already running and macOS has already
/// let Lunalyr read it (`mayRead`): it never launches Music, never raises
/// the permission prompt, and never controls playback.
struct AppleMusicVideoLibrary: MusicVideoLibrary {
    let mayRead: @Sendable () async -> Bool

    func localVideo(for song: MusicVideoSong) async -> URL? {
        guard !AppRuntime.isUnitTesting, await mayRead() else { return nil }
        let reply: NSAppleEventDescriptor
        do {
            reply = try await ScriptDesk.shared.run(
                PlayerScript(name: "music-video.library", source: Self.script(title: song.title), cacheable: false),
                for: .appleMusic, priority: .read)
        } catch {
            return nil
        }
        return Self.pick(Self.parse(reply), for: song)
    }

    static func script(title: String) -> String {
        PlayerScriptSource.query(AppleMusicSearch.bundleID, """
        set hits to {}
        try
        set found to (every file track of library playlist 1 whose video kind is music video and name contains \(ScriptText.quoted(title)))
        on error number errorNumber
        if errorNumber is not in \(PlayerScriptSource.tolerated) then error number errorNumber
        return {"ok"}
        end try
        set limitCount to count of found
        if limitCount > 12 then set limitCount to 12
        repeat with i from 1 to limitCount
        set t to item i of found
        try
        set f to location of t
        if f is not missing value then set end of hits to {name of t, artist of t, POSIX path of f}
        end try
        end repeat
        return {"ok", hits}
        """)
    }

    struct Hit: Equatable {
        var title: String
        var artist: String
        var path: String
    }

    static func parse(_ reply: NSAppleEventDescriptor) -> [Hit] {
        let items = reply.scriptItems
        guard items.first?.scriptText == "ok", items.count >= 2 else { return [] }
        return items[1].scriptItems.compactMap { row in
            let fields = row.scriptItems
            guard fields.count >= 3, let title = fields[0].scriptText, let path = fields[2].scriptText,
                  !path.isEmpty else { return nil }
            return Hit(title: title, artist: fields[1].scriptText ?? "", path: path)
        }
    }

    /// The hit that is this song by this artist, the closest title first.
    static func pick(_ hits: [Hit], for song: MusicVideoSong) -> URL? {
        let ranked = hits.compactMap { hit -> (Hit, Double)? in
            let title = LyricQuery.similarity(hit.title, song.title)
            let artist = song.artist.isEmpty ? 1 : LyricQuery.similarity(hit.artist, song.artist)
            guard title >= 0.85, artist >= 0.5 else { return nil }
            return (hit, title + artist)
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
