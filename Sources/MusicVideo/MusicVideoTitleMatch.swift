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

    /// The song's titles to look for, best first: as the player names it,
    /// then its plain title without the version part ("Feel So Close -
    /// Radio Edit" → "Feel So Close"), whose official video is usually the
    /// one an edit or an extended cut shares. Not for a version with a
    /// recording of its own (live, acoustic, remix, sped up, a language
    /// version: `VersionTags`): its video, if any, names it.
    static func titleVersions(_ title: String) -> [String] {
        let plain = plainTitle(title)
        guard !plain.isEmpty, songKey(plain) != songKey(title),
              VersionTags.tags(title).isSubset(of: sharedVideoTags) else { return [title] }
        return [title, plain]
    }

    /// Version tags whose song shares the plain title's video.
    static let sharedVideoTags: Set<String> = ["edit", "extended"]

    /// The title without brackets or anything after its first " - ".
    static func plainTitle(_ title: String) -> String {
        var plain = stripBrackets(title)
        if let dash = plain.range(of: #"\s[-–—]\s"#, options: .regularExpression) {
            plain = String(plain[..<dash.lowerBound])
        }
        return plain.trimmingCharacters(in: .whitespaces)
    }

    /// Which of the song's titles (`titleVersions`) a video names: 0 for
    /// the title as given, 1 for its plain title, nil for neither.
    static func matchedVersion(videoTitle: String, songTitle: String, artist: String) -> Int? {
        titleVersions(songTitle).firstIndex { matches(videoTitle: videoTitle, songTitle: $0, artist: artist) }
    }

    /// The song title as compared: brackets, "feat." credits and qualifier
    /// segments removed, then `CoverLookup.normal`.
    static func songKey(_ title: String) -> String {
        core(fold(title), artists: [])
    }

    /// The video title's song part, compared the same way, with the
    /// artist's own segments (and an artist prefix or suffix) removed.
    static func videoKey(_ title: String, artist: String) -> String {
        core(fold(title), artists: artistParts(artist))
    }

    /// Traditional Chinese written as simplified ("周杰倫" → "周杰伦",
    /// "告白氣球" → "告白气球"), so a video in one script is the song in the
    /// other. Titles, artists and channels are all compared this way; text
    /// without Han characters is returned as it is.
    static func fold(_ text: String) -> String {
        guard text.unicodeScalars.contains(where: \.properties.isIdeographic) else { return text }
        return text.applyingTransform(StringTransform("Traditional-Simplified"), reverse: false) ?? text
    }

    /// The song and the video name the same song. Also tried with segments
    /// that are the artist beside qualifiers or the artist's name in its
    /// own script dropped ("米津玄師 MV「Lemon」", "IVE 아이브 'I AM' MV",
    /// "周杰倫 Jay Chou"); what is left must still equal the song title.
    /// Or the song is a quoted part with nothing but the artist and
    /// qualifiers before it: "YOASOBI「咲き誇れ」Official Music Video /
    /// NHK連続テレビ小説『ブラッサム』主題歌" quotes its song, then goes on about
    /// the tie-in, whose own quoted name is not it. "Dayglow - How I Made
    /// "Hot Rod"" quotes the song too, but is a video about it.
    /// 【】 and 《》 quote as well. Inside them the song may sit beside its
    /// name wholly in another script ("【告白氣球 Love Confession】" is 告白气球
    /// and Love Confession), and after 【】 only qualifiers may follow:
    /// "【告白氣球 Love Confession】MV花絮 Making of" is about the song.
    /// Names paired across scripts ("[MV] IU(아이유) _ Through the
    /// Night(밤편지)") are read each way (`pairReadings`).
    static func matches(videoTitle: String, songTitle: String, artist: String) -> Bool {
        let song = songKey(songTitle)
        guard !song.isEmpty else { return false }
        let artists = artistParts(artist)
        let title = fold(videoTitle)
        return ([title] + pairReadings(title, artists: artists)).contains {
            reads($0, as: song, songTitle: songTitle, artists: artists)
        }
    }

    private static func reads(_ videoTitle: String, as song: String, songTitle: String, artists: [String]) -> Bool {
        if core(videoTitle, artists: artists) == song || core(videoTitle, artists: artists, loose: true) == song { return true }
        for quote in quotes(videoTitle) {
            guard quote.before.isEmpty || core(quote.before, artists: artists, loose: true).isEmpty else { continue }
            let named = core(quote.text, artists: artists) == song
                || (quote.mark != .quote && namesBeside(quote.text, song: song, songTitle: songTitle, artists: artists))
            guard named else { continue }
            if quote.mark == .lenticular,
               !segments(stripBrackets(quote.after)).allSatisfy({ isQualifier(stripCredits($0)) }) { continue }
            return true
        }
        return false
    }

    /// The title with each name paired across scripts read as one of its
    /// names: a Latin name with its name in another script in brackets
    /// right after it, or the reverse ("IU(아이유)", "Through the
    /// Night(밤편지)", "밤편지(Through the Night)"). One reading keeps the
    /// names outside the brackets, the other those inside; a pair naming
    /// the artist reads as the artist's own form in both. Brackets holding
    /// both scripts, or no letters, are left alone.
    static func pairReadings(_ title: String, artists: [String]) -> [String] {
        guard let regex = try? NSRegularExpression(pattern: #"[(（]([^()（）]+)[)）]"#) else { return [] }
        let ns = title as NSString
        var pairs: [(range: NSRange, outside: String, inside: String)] = []
        for match in regex.matches(in: title, range: NSRange(location: 0, length: ns.length)) {
            let inside = ns.substring(with: match.range(at: 1)).trimmingCharacters(in: .whitespaces)
            guard let insideScript = script(of: inside) else { continue }
            var start = match.range.location
            while start > 0, ns.substring(with: NSRange(location: start - 1, length: 1)) == " " { start -= 1 }
            var nameStart = start
            while nameStart > 0 {
                let character = ns.substring(with: NSRange(location: nameStart - 1, length: 1))
                guard let kind = nameCharacter(character), kind == nil || kind == insideScript.other else { break }
                nameStart -= 1
            }
            let outside = ns.substring(with: NSRange(location: nameStart, length: start - nameStart))
                .trimmingCharacters(in: .whitespaces.union(CharacterSet(charactersIn: "'’.&!?,")))
            guard !outside.isEmpty, script(of: outside) == insideScript.other else { continue }
            let from = ns.range(of: outside, options: [], range: NSRange(location: nameStart, length: start - nameStart))
            pairs.append((NSRange(location: from.location, length: match.range.location + match.range.length - from.location),
                          outside, inside))
        }
        guard !pairs.isEmpty else { return [] }
        let readings = [true, false].map { keepOutside in
            pairs.reversed().reduce(title) { text, pair in
                let artist = [pair.outside, pair.inside].first { artists.contains(CoverLookup.normal($0)) }
                let name = artist ?? (keepOutside ? pair.outside : pair.inside)
                return (text as NSString).replacingCharacters(in: pair.range, with: name)
            }
        }
        return Array(NSOrderedSet(array: readings.filter { $0 != title })) as? [String] ?? []
    }

    private enum Script {
        case latin, other
        var other: Script { self == .latin ? .other : .latin }
    }

    /// Latin when every letter is a Latin letter, other when none is; nil
    /// for both, or no letters.
    private static func script(of text: String) -> Script? {
        var latin = false, other = false
        for scalar in text.unicodeScalars where scalar.properties.isAlphabetic {
            if scalar.isASCII { latin = true } else { other = true }
        }
        return latin == other ? nil : latin ? .latin : .other
    }

    /// A character that can be part of a name before a bracket: a letter
    /// of a script (`.some(script)`), or a digit, space or name punctuation
    /// that belongs to either (`.some(nil)`). nil ends the name.
    private static func nameCharacter(_ character: String) -> Script?? {
        guard let scalar = character.unicodeScalars.first else { return nil }
        if scalar.properties.isAlphabetic { return .some(scalar.isASCII ? .latin : .other) }
        if CharacterSet.decimalDigits.contains(scalar) || " '’.&!?,".unicodeScalars.contains(scalar) { return .some(nil) }
        return nil
    }

    /// "告白气球 Love Confession" names 告白气球 and Love Confession: the song,
    /// then (or after) a name wholly in another script, Latin beside a
    /// title without Latin letters or the other way round. Not a version:
    /// "Love Confession Piano Version" is not Love Confession.
    private static func namesBeside(_ text: String, song: String, songTitle: String, artists: [String]) -> Bool {
        guard VersionTags.tags("(\(text))").isSubset(of: VersionTags.tags(songTitle)) else { return false }
        let key = core(text, artists: artists)
        var rest = key
        if key.hasPrefix(song) { rest.removeFirst(song.count) } else if key.hasSuffix(song) {
            rest.removeLast(song.count)
        } else { return false }
        guard !rest.isEmpty, rest.count < key.count else { return false }
        let latin = { (text: String) in text.unicodeScalars.contains { $0.isASCII && $0.properties.isAlphabetic } }
        return latin(song)
            ? !rest.unicodeScalars.contains(where: \.isASCII)
            : rest.unicodeScalars.allSatisfy(\.isASCII) && latin(rest)
    }

    /// The parts of a title in quotation marks: 「」『』“” "" 【】 and 《》.
    static func quoted(_ title: String) -> [String] { quotes(fold(title)).map(\.text) }

    private enum Mark { case quote, lenticular, angle }

    /// Each quoted part, with everything in the title before and after it.
    private static func quotes(_ title: String) -> [(text: String, before: String, after: String, mark: Mark)] {
        let pattern = #"「([^「」]+)」|『([^『』]+)』|“([^“”]+)”|"([^"]+)"|【([^【】]+)】|《([^《》]+)》"#
        guard let regex = try? NSRegularExpression(pattern: pattern) else { return [] }
        let ns = title as NSString
        return regex.matches(in: title, range: NSRange(location: 0, length: ns.length)).compactMap { match in
            guard let group = (1..<match.numberOfRanges).first(where: { match.range(at: $0).location != NSNotFound })
            else { return nil }
            let end = match.range.location + match.range.length
            return (ns.substring(with: match.range(at: group)), ns.substring(to: match.range.location),
                    ns.substring(from: end), group == 5 ? .lenticular : group == 6 ? .angle : .quote)
        }
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
        let artist = fold(artist)
        let whole = CoverLookup.normal(artist)
        var parts = [whole]
        let pattern = #"\s*(?:,|&|\+|/|\bx\b|\band\b|\bfeat\.?|\bft\.?|\bfeaturing\b|\bwith\b)\s*"#
        let split = artist.replacingOccurrences(of: pattern, with: "\u{1}", options: [.regularExpression, .caseInsensitive])
        parts += split.split(separator: "\u{1}").map { CoverLookup.normal(String($0)) }
        return Array(Set(parts.filter { $0.count >= 2 }))
    }

    // MARK: Pieces

    private static func core(_ title: String, artists: [String], loose: Bool = false) -> String {
        var kept: [String] = []
        // "M/V", as K-pop titles write it, is the qualifier "MV".
        let title = title.replacingOccurrences(of: #"(?<![A-Za-z0-9])[Mm]/[Vv](?![A-Za-z0-9])"#, with: "MV",
                                               options: .regularExpression)
        for segment in segments(stripBrackets(title)) {
            let cleaned = stripCredits(segment)
            let normal = CoverLookup.normal(cleaned)
            if normal.isEmpty || artists.contains(normal) || isQualifier(cleaned) { continue }
            if loose && isLooseArtistSegment(cleaned, artists: artists) { continue }
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

    /// A segment that is the artist plus qualifier words ("米津玄師 MV"), or
    /// the artist plus their name in another script ("IVE 아이브", and
    /// "周杰伦 Jay Chou": a Latin name beside an artist written without one).
    private static func isLooseArtistSegment(_ segment: String, artists: [String]) -> Bool {
        let words = LyricQuery.normalize(segment).split(separator: " ").map(String.init)
        let rest = words.filter { !qualifierWords.contains($0) }.joined()
        guard !rest.isEmpty else { return false }
        for artist in artists {
            if rest == artist { return true }
            var other = rest
            if other.hasPrefix(artist) { other.removeFirst(artist.count) } else if other.hasSuffix(artist) {
                other.removeLast(artist.count)
            } else { continue }
            if !other.isEmpty, !other.unicodeScalars.contains(where: \.isASCII) { return true }
            if !other.isEmpty, other.unicodeScalars.allSatisfy(\.isASCII),
               !artist.unicodeScalars.contains(where: { $0.isASCII && $0.properties.isAlphabetic }) { return true }
        }
        return false
    }

    /// Cut at separators: dashes, bars, colons and underscores with spaces, double
    /// quotes, Japanese corner brackets 「」『』, and single quotes that open
    /// or close a quotation ("BLACKPINK - 'Shut Down' MV"), not apostrophes.
    private static func segments(_ text: String) -> [String] {
        let pattern = #"\s+[-–—|:_]\s+|\s*[|“”"«»「」『』]\s*|\s+[-–—]|[-–—]\s+|:\s+"#
            + #"|(?:^|\s)['‘](?=\S)|(?<=\S)['’](?:\s|$)"#
        let marked = text.replacingOccurrences(of: pattern, with: "\u{1}", options: .regularExpression)
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
