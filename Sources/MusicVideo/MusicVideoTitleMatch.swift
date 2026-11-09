import Foundation

/// Whether a video is *this song*, not just this artist. A video title is
/// cut at its separators (- – — | : and quotes); the artist's segments,
/// bracketed and trailing qualifiers ("(Official Music Video)", "[4K]",
/// "Remastered", "ft. …") are dropped, and what is left must equal the song
/// title under the same treatment (`CoverLookup.normal`). "Love" is not
/// "Cherry Flavored Love" or "Lovesick". Live, concert and fan uploads are
/// refused outright: a date, a "Venue, City" segment, or words such as
/// live, concert, tour, 4K, fancam or pro-shot.
enum MusicVideoTitleMatch {
    /// Words (whole, any case) that mark a live or fan upload. Allowed when
    /// the song's own title has them.
    static let liveWords = ["live", "concert", "tour", "4k", "fancam", "fan cam", "pro shot", "proshot", "pro-shot"]

    /// Words a qualifier segment may be made of, alone or together.
    private static let qualifierWords: Set<String> = [
        "official", "music", "video", "videos", "mv", "hd", "hq", "4k", "1080p", "720p", "uhd", "remastered", "remaster",
        "version", "explicit", "clean", "uncensored", "edit", "visual", "visualizer", "visualiser", "audio", "lyric",
        "lyrics", "new", "single", "premiere",
    ]

    /// The song title as compared: brackets, "feat." credits and qualifier
    /// segments removed, then `CoverLookup.normal`.
    static func songKey(_ title: String) -> String {
        core(title, artists: [])
    }

    /// The video title's song part, compared the same way, with the
    /// artist's own segments (and an artist prefix or suffix) removed.
    static func videoKey(_ title: String, artist: String) -> String {
        core(title, artists: artistParts(artist))
    }

    /// The song and the video name the same song.
    static func matches(videoTitle: String, songTitle: String, artist: String) -> Bool {
        let song = songKey(songTitle)
        return !song.isEmpty && videoKey(videoTitle, artist: artist) == song
    }

    /// A live, concert or fan recording, unless the song's own title says so.
    static func isLiveOrFanUpload(_ title: String, songTitle: String) -> Bool {
        let name = " \(LyricQuery.normalize(title)) "
        let song = " \(LyricQuery.normalize(songTitle)) "
        for word in liveWords {
            let normal = " \(LyricQuery.normalize(word)) "
            if name.contains(normal) && !song.contains(normal) { return true }
        }
        if title.range(of: #"\b\d{1,2}[/.\-]\d{1,2}[/.\-]\d{2,4}\b"#, options: .regularExpression) != nil
            || title.range(of: #"\b\d{4}[/.\-]\d{1,2}[/.\-]\d{1,2}\b"#, options: .regularExpression) != nil {
            return true
        }
        // " - Republik, Honolulu": a separate segment naming a place.
        let songCore = songKey(songTitle)
        return segments(stripBrackets(title)).contains { $0.contains(",") && songKey($0) != songCore }
    }

    /// The artist credit split into its names: "A & B", "A, B", "A x B",
    /// "A feat. B". Each is a channel or a title segment that may stand for
    /// the artist.
    static func artistParts(_ artist: String) -> [String] {
        let whole = CoverLookup.normal(artist)
        var parts = [whole]
        let pattern = #"\s*(?:,|&|\+|/|\bx\b|\band\b|\bfeat\.?|\bft\.?|\bfeaturing\b|\bwith\b)\s*"#
        let split = artist.replacingOccurrences(of: pattern, with: "\u{1}", options: [.regularExpression, .caseInsensitive])
        parts += split.split(separator: "\u{1}").map { CoverLookup.normal(String($0)) }
        return Array(Set(parts.filter { $0.count >= 2 }))
    }

    // MARK: Pieces

    private static func core(_ title: String, artists: [String]) -> String {
        var kept: [String] = []
        for segment in segments(stripBrackets(title)) {
            let cleaned = stripCredits(segment)
            let normal = CoverLookup.normal(cleaned)
            if normal.isEmpty || artists.contains(normal) || isQualifier(cleaned) { continue }
            kept.append(normal)
        }
        var key = kept.joined()
        // "The Weeknd Blinding Lights": the artist written without a separator.
        for artist in artists.sorted(by: { $0.count > $1.count }) where key.count > artist.count {
            if key.hasPrefix(artist) { key.removeFirst(artist.count) } else if key.hasSuffix(artist) {
                key.removeLast(artist.count)
            }
        }
        // A title that is only brackets ("(Interlude)") keeps them.
        return key.isEmpty && artists.isEmpty ? CoverLookup.normal(title) : key
    }

    private static func segments(_ text: String) -> [String] {
        let marked = text.replacingOccurrences(of: #"\s+[-–—|:]\s+|\s*[|“”"«»]\s*|\s+[-–—]|[-–—]\s+|:\s+"#, with: "\u{1}",
                                               options: .regularExpression)
        return marked.split(separator: "\u{1}").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
    }

    private static func stripBrackets(_ text: String) -> String {
        var result = text
        for pattern in [#"\([^()]*\)"#, #"\[[^\[\]]*\]"#, #"\{[^{}]*\}"#, #"【[^【】]*】"#] {
            result = result.replacingOccurrences(of: pattern, with: " ", options: .regularExpression)
        }
        return result
    }

    /// "Song ft. Someone" → "Song".
    private static func stripCredits(_ text: String) -> String {
        text.replacingOccurrences(of: #"\s+(?:feat\.?|ft\.?|featuring|prod\.?(?:\s+by)?)\s+.*$"#, with: "",
                                  options: [.regularExpression, .caseInsensitive])
    }

    private static func isQualifier(_ segment: String) -> Bool {
        let words = LyricQuery.normalize(segment).split(separator: " ").map(String.init)
        guard !words.isEmpty else { return true }
        return words.allSatisfy { qualifierWords.contains($0) || ($0.count == 4 && Int($0) != nil) }
    }
}
