import Foundation

/// Version qualifiers in a song name ("Live", "Acoustic", "Remix", "Sped Up"…)
/// that name a different recording, whose timing will not fit the playing one.
///
/// Only qualifier segments count: text in brackets, or after " - " as Music
/// writes "Song - Live". Remasters, mono/stereo, editions and credits name the
/// same recording and are not tags, and a bare "Version" says nothing. Names
/// that only translate the qualifier ("现场" for "Live") fold to the same tag.
enum VersionTags {
    /// The version tags `name` declares.
    static func tags(_ name: String) -> Set<String> {
        var found = Set<String>()
        for segment in segments(name) { found.formUnion(segmentTags(segment)) }
        return found
    }

    /// The tags on which `title` (a service's name for its match) and the
    /// playing song disagree, empty when they name the same version. A tag
    /// the playing song's album carries ("Song (Live)" on "Live at Wembley")
    /// is not held against the match.
    static func mismatch(_ query: LyricQuery, title: String) -> Set<String> {
        let playing = tags(query.title), offered = tags(title)
        let album = tags(query.album ?? "")
        return offered.subtracting(playing).subtracting(album).union(playing.subtracting(offered))
    }

    /// "Live", or "Live / Remix", for the source list.
    static func describe(_ tags: Set<String>) -> String {
        tags.sorted().map { $0.prefix(1).uppercased() + $0.dropFirst() }.joined(separator: " / ")
    }

    // MARK: - Reading the name

    private static let bracket = try! NSRegularExpression(pattern: #"[(\[{（【〔「『<＜]([^)\]}）】〕」』>＞]*)[)\]}）】〕」』>＞]"#)
    private static let dash = try! NSRegularExpression(pattern: #"\s[-–—]\s"#)
    private static let tilde = try! NSRegularExpression(pattern: #"[~～〜]([^~～〜]+)[~～〜]?\s*$"#)

    private static func segments(_ name: String) -> [String] {
        let ns = name as NSString, whole = NSRange(location: 0, length: ns.length)
        var parts = bracket.matches(in: name, range: whole).map { ns.substring(with: $0.range(at: 1)) }
        // Everything after the first " - " ("Song - Live", "Song - Radio Edit").
        if let match = dash.firstMatch(in: name, range: whole) {
            parts.append(ns.substring(from: NSMaxRange(match.range)))
        }
        // Japanese subtitles: "曲名 ~Live ver.~".
        if let match = tilde.firstMatch(in: name, range: whole) { parts.append(ns.substring(with: match.range(at: 1))) }
        return parts
    }

    /// Words of a segment, folded. Latin, CJK and digit runs split apart, so
    /// "Live版" and "2011Live" still give "live".
    static func words(_ segment: String) -> [String] {
        let folded = segment.folding(options: [.caseInsensitive, .diacriticInsensitive, .widthInsensitive], locale: nil).lowercased()
        var words: [String] = [], current = "", kind = 0
        for scalar in folded.unicodeScalars {
            let next: Int
            if CharacterSet.decimalDigits.contains(scalar) { next = 1 }
            else if CharacterSet.letters.contains(scalar) { next = scalar.value < 0x2E80 ? 2 : 3 }
            else { next = 0 }
            if next != kind || next == 0, !current.isEmpty { words.append(current); current = "" }
            if next != 0 { current.unicodeScalars.append(scalar) }
            kind = next
        }
        if !current.isEmpty { words.append(current) }
        return words
    }

    private static let wordTags: [String: String] = [
        "live": "live", "unplugged": "unplugged", "acoustic": "acoustic", "acustico": "acoustic",
        "remix": "remix", "remixes": "remix", "remixed": "remix", "rmx": "remix",
        "demo": "demo", "demos": "demo",
        "instrumental": "instrumental", "inst": "instrumental", "karaoke": "instrumental", "offvocal": "instrumental",
        "acapella": "a cappella", "acappella": "a cappella",
        "spedup": "sped up", "nightcore": "sped up", "slowed": "slowed",
        "edit": "edit", "extended": "extended", "orchestral": "orchestral", "symphonic": "orchestral",
        "rehearsal": "rehearsal",
    ]
    private static let pairTags: [String: String] = [
        "off vocal": "instrumental", "a cappella": "a cappella", "a capella": "a cappella",
        "sped up": "sped up", "speed up": "sped up", "piano version": "piano", "piano ver": "piano",
        "dj mix": "dj mix", "continuous mix": "dj mix",
    ]
    /// Matched inside the segment, for scripts written without spaces.
    private static let spanTags: [String: String] = [
        "现场": "live", "現場": "live", "ライブ": "live", "라이브": "live",
        "不插电": "unplugged", "不插電": "unplugged",
        "伴奏": "instrumental", "纯音乐": "instrumental", "純音樂": "instrumental",
        "オフボーカル": "instrumental", "インスト": "instrumental",
        "混音": "remix", "リミックス": "remix", "리믹스": "remix",
        "清唱": "a cappella", "아카펠라": "a cappella",
        "加速": "sped up", "降速": "slowed", "慢速": "slowed",
        "アコースティック": "acoustic", "어쿠스틱": "acoustic",
    ]

    private static func segmentTags(_ segment: String) -> Set<String> {
        let list = words(segment)
        var found = Set<String>()
        // Music's DJ Mix albums mark each track "[Mixed]"; "Mixed by X" is a credit.
        if list == ["mixed"] { found.insert("dj mix") }
        for (index, word) in list.enumerated() {
            if let tag = wordTags[word] { found.insert(tag) }
            if index + 1 < list.count, let tag = pairTags[word + " " + list[index + 1]] { found.insert(tag) }
            // "Club Mix", "Extended Mix": another mix, but "Original Mix" is the original.
            if word == "mix", index > 0, !["original", "dj", "continuous", "album"].contains(list[index - 1]) {
                found.insert(list[index - 1] == "extended" ? "extended" : "remix")
            }
        }
        let joined = list.joined(separator: " ")
        for (span, tag) in spanTags where joined.contains(span) { found.insert(tag) }
        return found
    }
}
