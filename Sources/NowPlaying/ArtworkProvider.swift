import Foundation
import ImageIO

/// Apple Music's AppleScript bridge reports `count of artworks == 0` for
/// streamed catalog tracks -- cover art simply is not exposed there (it is
/// DRM'd content, not a file tag). Apple's public iTunes Search API does
/// serve the same cover, unauthenticated, so we look the track up by
/// artist + title and pull the image from there.
actor ArtworkProvider {
    static let shared = ArtworkProvider()

    /// A cover that has not arrived by now is not worth waiting for: the
    /// wallpaper keeps the previous colours meanwhile, and URLSession's
    /// default would leave that going for a minute per request.
    static let timeout: TimeInterval = 7
    /// Larger than any real cover; anything bigger is not one.
    static let maxImageBytes = 20_000_000

    /// Covers kept for songs played again this session. Bounded: the app
    /// runs all day, and a 600px cover is around 100 KB.
    static let cacheCapacity = 64
    static let cacheBytes = 16_000_000

    private let session: URLSession
    private var cache = BoundedCache<String, Data>(capacity: ArtworkProvider.cacheCapacity,
                                                   maxCost: ArtworkProvider.cacheBytes, cost: { $0.count })

    init(session: URLSession = .shared) {
        self.session = session
    }

    func artworkData(title: String, artist: String, album: String?, duration: Double) async -> Data? {
        let key = "\(title)|\(artist)|\(album ?? "")"
        if let hit = cache.value(for: key) { return hit }
        // A catalogue cover is only right if it is this artist's. Without
        // an artist to check against, a title search is a guess.
        guard !Self.normalize(artist).isEmpty, !Self.normalize(title).isEmpty else { return nil }

        // Apple Music artist fields often chain collaborators ("A & B, C");
        // the search index matches far better on the primary artist alone.
        let primaryArtist = Self.primaryArtist(artist)
        // The user's own storefront first: it names artists the way their
        // Music app does ("宇多田ヒカル" rather than "Hikaru Utada").
        for country in Self.storefronts() {
            for term in ["\(primaryArtist) \(title)", title] {
                guard !Task.isCancelled else { return nil }
                if let image = await search(term: term, country: country, title: title, artist: artist,
                                            album: album, duration: duration) {
                    cache.insert(image, for: key)
                    return image
                }
            }
        }
        return nil
    }

    /// A cover a player published by address, such as Spotify's artwork URL.
    func imageData(at url: URL) async -> Data? {
        guard url.scheme?.lowercased() == "https", url.host != nil else { return nil }
        return await download(url)
    }

    private func search(term: String, country: String, title: String, artist: String, album: String?,
                        duration: Double) async -> Data? {
        var components = URLComponents(string: "https://itunes.apple.com/search")
        components?.queryItems = [
            URLQueryItem(name: "term", value: term),
            URLQueryItem(name: "entity", value: "song"),
            URLQueryItem(name: "country", value: country),
            URLQueryItem(name: "limit", value: "12"),
        ]
        guard let url = components?.url,
              let (data, response) = try? await session.data(for: URLRequest(url: url, timeoutInterval: Self.timeout)),
              (response as? HTTPURLResponse)?.statusCode == 200,
              let payload = try? JSONDecoder().decode(SearchResponse.self, from: data),
              let best = Self.bestMatch(in: payload.results, title: title, artist: artist, album: album, duration: duration),
              let thumb = best.artworkUrl100
        else { return nil }

        // The same path serves larger renditions; 600px is plenty for
        // both the gradient sampling and the on-screen capsule.
        let large = thumb.replacingOccurrences(of: "100x100bb", with: "600x600bb")
        guard let imageURL = URL(string: large), imageURL.scheme == "https" else { return nil }
        return await download(imageURL)
    }

    private func download(_ url: URL) async -> Data? {
        guard let (data, response) = try? await session.data(for: URLRequest(url: url, timeoutInterval: Self.timeout)),
              (response as? HTTPURLResponse)?.statusCode == 200,
              data.count <= Self.maxImageBytes,
              let source = CGImageSourceCreateWithData(data as CFData, nil),
              CGImageSourceGetCount(source) > 0
        else { return nil }
        return data
    }

    /// Taking the first hit picks the wrong cover constantly: a catalogue
    /// carries the single, the album, remasters and deluxe reissues under the
    /// same title, each with different art. Album name and runtime are what
    /// actually identify the edition -- among this artist's songs only: a
    /// same-titled song by someone else is never this track's cover.
    static func bestMatch(
        in results: [Result], title: String, artist: String, album: String?, duration: Double
    ) -> Result? {
        let wantedTitle = normalize(title)
        let wantedAlbum = album.map(normalize) ?? ""

        var best: (result: Result, score: Int, delta: Double)?
        for candidate in results {
            guard matches(normalize(candidate.trackName ?? ""), wantedTitle),
                  artistMatches(candidate.artistName, wanted: artist) else { continue }

            var score = 0
            if !wantedAlbum.isEmpty, matches(normalize(candidate.collectionName ?? ""), wantedAlbum) {
                score += 4
            }
            let delta = duration > 0 && (candidate.trackTimeMillis ?? 0) > 0
                ? abs(Double(candidate.trackTimeMillis ?? 0) / 1000 - duration)
                : Double.greatestFiniteMagnitude
            if delta < 2 { score += 4 } else if delta < 5 { score += 2 }

            if let current = best {
                if score > current.score || (score == current.score && delta < current.delta) {
                    best = (candidate, score, delta)
                }
            } else {
                best = (candidate, score, delta)
            }
        }
        return best?.result
    }

    /// A shared credit ("A" for "A & B", "A, B & C" for "A"), compared as
    /// whole names: "Max" is not "Ava Max".
    static func artistMatches(_ candidate: String?, wanted: String) -> Bool {
        let have = credits(candidate ?? "")
        let want = credits(wanted)
        guard !have.isEmpty, !want.isEmpty else { return false }
        return !have.isDisjoint(with: want)
    }

    /// The individual names in an artist credit, folded for comparison.
    static func credits(_ artist: String) -> Set<String> {
        let folded = artist.folding(options: [.caseInsensitive, .diacriticInsensitive],
                                    locale: Locale(identifier: "en_US_POSIX"))
        let separated = folded.replacingOccurrences(
            of: #"\s*(?:,|&|\+|/|;|×|\bfeaturing\b|\bfeat\b\.?|\bft\b\.?|\bwith\b|\bvs\b\.?|\bx\b|\band\b)\s*"#,
            with: "|", options: .regularExpression
        )
        return Set(separated.split(separator: "|").map { name in
            name.components(separatedBy: CharacterSet.alphanumerics.inverted).joined()
        }.filter { !$0.isEmpty })
    }

    static func primaryArtist(_ artist: String) -> String {
        let first = artist
            .components(separatedBy: CharacterSet(charactersIn: "&,"))
            .first?
            .trimmingCharacters(in: .whitespaces) ?? artist
        return first.isEmpty ? artist : first
    }

    private static func matches(_ a: String, _ b: String) -> Bool {
        guard !a.isEmpty, !b.isEmpty else { return false }
        return a == b || a.contains(b) || b.contains(a)
    }

    static func normalize(_ value: String) -> String {
        var text = value.lowercased()
        for pattern in [#"\([^)]*\)"#, #"\[[^\]]*\]"#, #"feat\..*"#, #"ft\..*"#] {
            text = text.replacingOccurrences(of: pattern, with: "", options: .regularExpression)
        }
        return text.components(separatedBy: CharacterSet.alphanumerics.inverted).joined()
    }

    private static func storefronts() -> [String] {
        let storefront = Locale.current.region?.identifier.lowercased() ?? "us"
        return storefront == "us" ? ["us"] : [storefront, "us"]
    }

    private struct SearchResponse: Decodable {
        var results: [Result]
    }

    struct Result: Decodable {
        var artworkUrl100: String?
        var trackName: String?
        var artistName: String?
        var collectionName: String?
        var trackTimeMillis: Int?
    }
}
