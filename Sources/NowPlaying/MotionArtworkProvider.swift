import Foundation

/// Uses the album's own artwork in the public Apple Music page payload.
actor MotionArtworkProvider {
    static let shared = MotionArtworkProvider()

    struct Motion: Equatable {
        let square: URL
        let tall: URL?
    }

    /// One HTTP exchange: the body and response, or nil if it failed.
    typealias Fetch = @Sendable (URLRequest) async -> (Data, URLResponse)?

    private let fetch: Fetch
    private var cache: [String: Motion] = [:]
    /// Albums Apple was reached for and has no animated cover. Session-only:
    /// a cover added later appears after a relaunch.
    private var misses: Set<String> = []

    init(session: URLSession = .shared) {
        fetch = { request in try? await session.data(for: request) }
    }

    /// Every request answered by `fetch` rather than a URL session, for
    /// tests that need to see when requests arrive.
    init(fetch: @escaping Fetch) {
        self.fetch = fetch
    }

    /// Motion artwork belongs to a release, so every track of an album
    /// shares one key (and one lookup).
    static func cacheKey(album: String?, artist: String, title: String) -> String {
        let release = album.flatMap { $0.isEmpty ? nil : $0 } ?? title
        return "\(release)|\(artist)"
    }

    func motion(album: String?, artist: String, title: String) async -> Motion? {
        let key = Self.cacheKey(album: album, artist: artist, title: title)
        if let cached = cache[key] { return cached }
        if misses.contains(key) { return nil }

        // Only an answer from Apple is a miss. A failure to reach it must
        // not become a negative cache, even for the session.
        var reachedApple = true
        let storefront = Locale.current.region?.identifier.lowercased() ?? "us"
        for country in storefront == "us" ? ["us"] : [storefront, "us"] {
            var queries = [("song", title)]
            if let album, !album.isEmpty { queries.insert(("album", album), at: 0) }
            for (entity, name) in queries {
                guard !Task.isCancelled else { return nil }
                guard let pages = await albumPages(country: country, entity: entity, name: name,
                    album: album, artist: artist) else {
                    reachedApple = false
                    continue
                }
                for page in pages {
                    guard let html = await fetchText(page) else {
                        reachedApple = false
                        continue
                    }
                    if let artwork = Self.parse(html: html) {
                        let motion = await progressive(artwork)
                        if !Task.isCancelled { cache[key] = motion }
                        return motion
                    }
                }
            }
        }
        if reachedApple, !Task.isCancelled { misses.insert(key) }
        return nil
    }

    static func parse(html: String) -> Motion? {
        guard let script = html.range(of: #"<script\b[^>]*\bid=["']serialized-server-data["'][^>]*>"#,
                                      options: .regularExpression),
              let end = html[script.upperBound...].range(of: "</script>"),
              let payload = try? JSONDecoder().decode(AlbumPage.self,
                from: Data(html[script.upperBound..<end.lowerBound].utf8)) else { return nil }
        // The first section contains the current album header. Recommendations
        // can also carry motion art, so never search across the whole document.
        let artwork = payload.data.lazy.compactMap { $0.data?.sections?.first?.items }.first
            ?? []
        for item in artwork {
            let dictionary = item.videoArtwork?.dictionary
            let square = dictionary?.motionDetailSquare?.video.flatMap(Self.mediaURL)
            let tall = dictionary?.motionDetailTall?.video.flatMap(Self.mediaURL)
            if let primary = square ?? tall { return Motion(square: primary, tall: tall) }
        }
        return nil
    }

    private static func mediaURL(_ raw: String) -> URL? {
        guard let url = URL(string: raw), url.scheme == "https", url.pathExtension.lowercased() == "m3u8",
              let host = url.host, host == "mvod.itunes.apple.com" || host.hasSuffix(".mzstatic.com")
        else { return nil }
        return url
    }

    /// The cover with each rendition resolved to one progressive MP4 where
    /// Apple publishes one. Portrait displays play the tall rendition only
    /// as such a file (`Motion.rendition(portrait:)`), so it is resolved as
    /// the square one is -- at the same time, so a Mac without a portrait
    /// display doesn't wait for it before the square cover can start. An
    /// album with a single rendition uses it for both and fetches it once.
    /// The answer is cached per album, so this costs a few playlist
    /// requests once per album. Known cost: with portrait and landscape
    /// displays together the two renditions are two files, and
    /// `SharedVideoPlayers` decodes each file once, so two decoders run
    /// where one did.
    private func progressive(_ artwork: Motion) async -> Motion {
        guard let master = artwork.tall, master != artwork.square else {
            let square = await progressiveRendition(for: artwork.square) ?? artwork.square
            return Motion(square: square, tall: artwork.tall == nil ? nil : square)
        }
        async let square = progressiveRendition(for: artwork.square)
        async let tall = progressiveRendition(for: master)
        let (squareFile, tallFile) = await (square, tall)
        return Motion(square: squareFile ?? artwork.square, tall: tallFile ?? master)
    }

    /// Apple's byte-range playlists often point to one complete fragmented
    /// MP4. Prefer that rendition for reliable startup and local-style looping.
    private func progressiveRendition(for master: URL) async -> URL? {
        guard let playlist = await fetchText(master) else { return nil }
        if let direct = Self.singleMP4(in: playlist, relativeTo: master) { return direct }
        let lines = playlist.split(whereSeparator: \.isNewline).map(String.init)
        var variants: [(url: URL, distance: Int)] = []
        for index in lines.indices.dropLast() {
            let tag = lines[index]
            guard tag.hasPrefix("#EXT-X-STREAM-INF:"), tag.contains("avc1."),
                  let size = tag.range(of: #"RESOLUTION=[0-9]+"#, options: .regularExpression),
                  let width = Int(tag[size].dropFirst("RESOLUTION=".count)),
                  let url = URL(string: lines[index + 1], relativeTo: master)?.absoluteURL,
                  Self.mediaURL(url.absoluteString) != nil else { continue }
            variants.append((url, abs(width - 768)))
        }
        for variant in variants.sorted(by: { $0.distance < $1.distance }).prefix(3) {
            if let text = await fetchText(variant.url), let direct = Self.singleMP4(in: text, relativeTo: variant.url) {
                return direct
            }
        }
        return nil
    }

    static func singleMP4(in playlist: String, relativeTo url: URL) -> URL? {
        let lines = playlist.split(whereSeparator: \.isNewline).map { $0.trimmingCharacters(in: .whitespaces) }
        let segments = Set(lines.filter { !$0.isEmpty && !$0.hasPrefix("#") })
        guard lines.contains("#EXT-X-ENDLIST"),
              lines.contains(where: { $0.hasPrefix("#EXT-X-BYTERANGE:") }),
              !lines.contains(where: { $0.hasPrefix("#EXT-X-KEY:") }),
              segments.count == 1, let segment = segments.first,
              lines.contains(where: { $0.hasPrefix("#EXT-X-MAP:") && $0.contains("URI=\"\(segment)\"") }),
              let video = URL(string: segment, relativeTo: url)?.absoluteURL,
              video.scheme == "https", video.host == url.host, video.pathExtension.lowercased() == "mp4"
        else { return nil }
        return video
    }

    /// Nil when the search could not be completed, as opposed to an empty answer.
    private func albumPages(country: String, entity: String, name: String, album: String?,
                            artist: String) async -> [URL]? {
        var components = URLComponents(string: "https://itunes.apple.com/search")
        components?.queryItems = [
            URLQueryItem(name: "term", value: "\(artist) \(name)"),
            URLQueryItem(name: "entity", value: entity),
            URLQueryItem(name: "country", value: country),
            URLQueryItem(name: "limit", value: "20"),
        ]
        guard let url = components?.url, let data = await fetchData(url),
              let payload = try? JSONDecoder().decode(SearchResponse.self, from: data) else { return nil }
        return Self.matchingPages(payload.results, album: album, artist: artist, song: entity == "song" ? name : nil)
    }

    static func matchingPages(_ candidates: [SearchResponse.Result], album: String?, artist: String, song: String?) -> [URL] {
        func normalized(_ text: String) -> String {
            text.folding(options: [.caseInsensitive, .diacriticInsensitive], locale: Locale(identifier: "en_US_POSIX"))
                .components(separatedBy: CharacterSet.alphanumerics.inverted).joined()
        }
        let wantedAlbum = album.map(normalized) ?? ""
        let wantedArtist = normalized(artist)
        var seen = Set<String>()
        return candidates.compactMap { candidate in
            guard let raw = candidate.collectionViewUrl, let url = URL(string: raw), url.scheme == "https",
                  url.host == "music.apple.com", url.pathComponents.contains("album"),
                  wantedAlbum.isEmpty || normalized(candidate.collectionName ?? "") == wantedAlbum,
                  wantedArtist.isEmpty || normalized(candidate.artistName ?? "") == wantedArtist,
                  song == nil || normalized(candidate.trackName ?? "") == normalized(song ?? ""),
                  seen.insert(url.path).inserted else { return nil }
            return url
        }
        .prefix(3).map { $0 }
    }

    private func fetchText(_ url: URL) async -> String? {
        guard let data = await fetchData(url) else { return nil }
        return String(data: data, encoding: .utf8)
    }

    private func fetchData(_ url: URL) async -> Data? {
        var request = URLRequest(url: url)
        request.setValue("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15", forHTTPHeaderField: "User-Agent")
        request.timeoutInterval = 10
        guard let (data, response) = await fetch(request),
              (response as? HTTPURLResponse)?.statusCode == 200 else { return nil }
        return data
    }

    struct SearchResponse: Decodable {
        struct Result: Decodable {
            var collectionViewUrl: String?
            var collectionName: String?
            var artistName: String?
            var trackName: String?
        }
        var results: [Result]
    }

    private struct AlbumPage: Decodable {
        struct Payload: Decodable {
            struct Data: Decodable {
                struct Section: Decodable {
                    struct Item: Decodable {
                        struct Artwork: Decodable {
                            struct Dictionary: Decodable {
                                struct Asset: Decodable { let video: String? }
                                let motionDetailSquare: Asset?
                                let motionDetailTall: Asset?
                            }
                            let dictionary: Dictionary?
                        }
                        let videoArtwork: Artwork?
                    }
                    let items: [Item]?
                }
                let sections: [Section]?
            }
            let data: Data?
        }
        let data: [Payload]
    }
}
