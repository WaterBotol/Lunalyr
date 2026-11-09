import Foundation
import NaturalLanguage

// Caption alignment for YouTube videos: the video's own caption track
// (often the lyrics, timed to the video) is matched against Lunalyr's
// timed lyric lines for the song, which gives how far into the video the
// song starts. Only text is fetched; nothing is ever played.
//
// Research, November 2026 (curl, no key): the watch page's
// `ytInitialPlayerResponse` lists `captions.playerCaptionsTracklistRenderer
// .captionTracks`, each with a signed `baseUrl` on `/api/timedtext`. Those
// URLs now carry `exp=xpe` and answer 200 with an EMPTY body unless a
// proof-of-origin token (`pot`) is added, which only YouTube's own player
// mints. Dropping `exp=xpe` breaks the signature (404). So today the track
// list is readable but the text mostly is not: an empty body is taken as
// "no captions" for the video, and the duration estimate is used instead.
// The parsers below handle `fmt=json3` and WebVTT for when a body does come.

/// A timed caption line in the video's own time.
struct MusicVideoCaptionCue: Equatable, Sendable {
    var start: Double
    var text: String
}

/// A timed lyric line in the song's own time.
struct MusicVideoLyricLine: Equatable, Sendable {
    var start: Double
    var text: String
}

enum YouTubeCaptions {
    struct Track: Equatable, Sendable {
        var baseURL: URL
        var languageCode: String
        /// YouTube's speech recognition ("asr"), rather than captions
        /// someone wrote.
        var automatic: Bool
        var name: String
    }

    /// The watch page for a video, whose player response lists its tracks.
    static func watchURL(videoID: String) -> URL? {
        guard YouTubeVideoID.isValid(videoID) else { return nil }
        var components = URLComponents(string: "https://www.youtube.com/watch")
        components?.queryItems = [URLQueryItem(name: "v", value: videoID), URLQueryItem(name: "hl", value: "en")]
        return components?.url
    }

    /// What the watch page says: the caption tracks and the video's length.
    struct Page: Equatable, Sendable {
        var tracks: [Track]
        var duration: Double?
    }

    /// Reads `ytInitialPlayerResponse` from a watch page. Nil when the page
    /// has no player response (YouTube changed it, or a consent wall).
    static func page(fromHTML html: String) -> Page? {
        guard let json = playerResponseJSON(in: html),
              let object = try? JSONSerialization.jsonObject(with: Data(json.utf8)) as? [String: Any] else { return nil }
        let duration = ((object["videoDetails"] as? [String: Any])?["lengthSeconds"] as? String).flatMap(Double.init)
        let renderer = (object["captions"] as? [String: Any])?["playerCaptionsTracklistRenderer"] as? [String: Any]
        let raw = renderer?["captionTracks"] as? [[String: Any]] ?? []
        let tracks = raw.compactMap { entry -> Track? in
            guard let base = entry["baseUrl"] as? String, let url = trustedTimedTextURL(base) else { return nil }
            let name = (entry["name"] as? [String: Any]).flatMap { ($0["simpleText"] as? String) ?? runsText($0) } ?? ""
            return Track(baseURL: url, languageCode: entry["languageCode"] as? String ?? "",
                         automatic: entry["kind"] as? String == "asr", name: name)
        }
        return Page(tracks: tracks, duration: duration.flatMap { $0 > 0 ? $0 : nil })
    }

    /// Only YouTube's own timed-text endpoint is ever fetched from a page.
    static func trustedTimedTextURL(_ string: String) -> URL? {
        guard let url = URL(string: string), url.scheme == "https", let host = url.host?.lowercased(),
              host == "www.youtube.com" || host == "youtube.com", url.path == "/api/timedtext" else { return nil }
        return url
    }

    /// The track's text as YouTube's JSON (`fmt=json3`).
    static func textURL(for track: Track) -> URL? {
        guard var components = URLComponents(url: track.baseURL, resolvingAgainstBaseURL: false) else { return nil }
        var items = (components.queryItems ?? []).filter { $0.name != "fmt" }
        items.append(URLQueryItem(name: "fmt", value: "json3"))
        components.queryItems = items
        return components.url
    }

    /// The tracks worth fetching, best first, at most `limit`: written
    /// captions in the lyrics' language, then the automatic track (it is in
    /// the language sung), then other written captions (often translations).
    static func preferred(_ tracks: [Track], lyricsLanguage: String?, limit: Int = 2) -> [Track] {
        func base(_ code: String) -> String { code.split(separator: "-").first.map { String($0).lowercased() } ?? "" }
        let language = lyricsLanguage.map(base)
        let written = tracks.filter { !$0.automatic }
        let automatic = tracks.filter(\.automatic)
        var ordered: [Track] = []
        if let language {
            ordered += written.filter { base($0.languageCode) == language }
            ordered += automatic.filter { base($0.languageCode) == language }
            ordered += automatic.filter { base($0.languageCode) != language }
            ordered += written.filter { base($0.languageCode) != language }
        } else {
            ordered = written + automatic
        }
        return Array(ordered.prefix(limit))
    }

    /// The lyrics' language, from their text.
    static func language(of lines: [MusicVideoLyricLine]) -> String? {
        let text = lines.prefix(40).map(\.text).joined(separator: "\n")
        guard !text.isEmpty else { return nil }
        let recognizer = NLLanguageRecognizer()
        recognizer.processString(text)
        return recognizer.dominantLanguage?.rawValue
    }

    // MARK: Parsing

    /// Cues from either format. Empty when the body is empty (the usual
    /// reply today without a proof-of-origin token) or unreadable.
    static func cues(from data: Data) -> [MusicVideoCaptionCue] {
        let text = String(decoding: data, as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
        if text.isEmpty { return [] }
        if text.hasPrefix("WEBVTT") { return cues(fromVTT: text) }
        return cues(fromJSON3: data)
    }

    /// `{"events":[{"tStartMs":1200,"dDurationMs":2000,"segs":[{"utf8":"…"}]}]}`.
    /// Automatic tracks split a line into word segments (`tOffsetMs`) and
    /// add `aAppend` events that only carry a newline; both are handled.
    static func cues(fromJSON3 data: Data) -> [MusicVideoCaptionCue] {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let events = object["events"] as? [[String: Any]] else { return [] }
        var cues: [MusicVideoCaptionCue] = []
        for event in events {
            guard let start = (event["tStartMs"] as? NSNumber)?.doubleValue,
                  let segments = event["segs"] as? [[String: Any]] else { continue }
            let text = segments.compactMap { $0["utf8"] as? String }.joined()
                .replacingOccurrences(of: "\n", with: " ").trimmingCharacters(in: .whitespacesAndNewlines)
            guard !text.isEmpty else { continue }
            cues.append(MusicVideoCaptionCue(start: start / 1000, text: text))
        }
        return cues
    }

    /// WebVTT cues. Inline word timings (`<00:00:01.500><c> word</c>`) are
    /// dropped, and the rolling repeats of automatic tracks (each cue
    /// repeating the line before it) are kept once, at their first time.
    static func cues(fromVTT text: String) -> [MusicVideoCaptionCue] {
        var cues: [MusicVideoCaptionCue] = []
        var recent: [String] = []
        var start: Double?
        let lines = text.replacingOccurrences(of: "\r\n", with: "\n").components(separatedBy: "\n")
        for (index, line) in lines.enumerated() {
            if line.contains("-->") {
                start = vttTime(line.components(separatedBy: "-->")[0])
                continue
            }
            // A cue identifier: the line just before a timing line.
            if index + 1 < lines.count, lines[index + 1].contains("-->") { continue }
            guard let start else { continue }
            let plain = stripTags(line).trimmingCharacters(in: .whitespacesAndNewlines)
            guard !plain.isEmpty else { continue }
            let key = MusicVideoCaptionAlign.normalize(plain)
            // An automatic track repeats the line before, untimed, above
            // the new one: not a new cue.
            let wordTimed = line.range(of: "<\\d", options: .regularExpression) != nil
            if !wordTimed && recent.contains(key) { continue }
            recent = Array((recent + [key]).suffix(2))
            cues.append(MusicVideoCaptionCue(start: start, text: plain))
        }
        return cues
    }

    /// "00:01:02.500", "01:02.500" or "1:02.5" to seconds.
    static func vttTime(_ raw: String) -> Double? {
        let token = raw.trimmingCharacters(in: .whitespaces).split(separator: " ").first.map(String.init) ?? ""
        let parts = token.replacingOccurrences(of: ",", with: ".").split(separator: ":").map(String.init)
        guard (2...3).contains(parts.count) else { return nil }
        var seconds = 0.0
        for part in parts {
            guard let value = Double(part), value >= 0 else { return nil }
            seconds = seconds * 60 + value
        }
        return seconds
    }

    private static func stripTags(_ line: String) -> String {
        line.replacingOccurrences(of: "<[^>]*>", with: "", options: .regularExpression)
    }

    // MARK: Page JSON

    private static func playerResponseJSON(in html: String) -> String? {
        for marker in ["var ytInitialPlayerResponse = ", "ytInitialPlayerResponse = ", "ytInitialPlayerResponse\":"] {
            guard let start = html.range(of: marker) else { continue }
            if let object = balancedObject(html[start.upperBound...]) { return object }
        }
        return nil
    }

    /// The JSON object at the start of `text` (after spaces), braces counted
    /// outside strings.
    static func balancedObject(_ text: Substring) -> String? {
        guard let open = text.firstIndex(where: { !$0.isWhitespace }), text[open] == "{" else { return nil }
        var depth = 0
        var inString = false
        var escaped = false
        let scalars = text.unicodeScalars
        var scalarIndex = open
        while scalarIndex < scalars.endIndex {
            let c = scalars[scalarIndex]
            if inString {
                if escaped { escaped = false }
                else if c == "\\" { escaped = true }
                else if c == "\"" { inString = false }
            } else if c == "\"" {
                inString = true
            } else if c == "{" {
                depth += 1
            } else if c == "}" {
                depth -= 1
                if depth == 0 { return String(text[open..<scalars.index(after: scalarIndex)]) }
            }
            scalarIndex = scalars.index(after: scalarIndex)
        }
        return nil
    }

    private static func runsText(_ node: [String: Any]) -> String? {
        (node["runs"] as? [[String: Any]])?.compactMap { $0["text"] as? String }.joined()
    }
}

// MARK: - Alignment

/// offset = video time − song time, from caption cues and lyric lines.
enum MusicVideoCaptionAlign {
    struct Result: Equatable, Sendable {
        var offset: Double
        /// Cues that agreed with the offset.
        var matched: Int
        /// Median absolute deviation of those cues' differences, seconds.
        var spread: Double
    }

    /// Cues whose text matches a lyric line at least this well count.
    static let similarityThreshold = 0.6
    /// At least this many agreeing cues.
    static let minimumMatches = 5
    /// Agreeing differences may scatter this much (median absolute deviation).
    static let maximumSpread = 0.6
    /// Cues within this of the voted offset are counted as agreeing.
    static let agreement = 1.0

    /// Lower case, accents, punctuation and bracketed asides ("[Music]",
    /// "(Chorus)") and note symbols removed, spaces collapsed.
    static func normalize(_ text: String) -> String {
        var s = text.folding(options: [.caseInsensitive, .diacriticInsensitive, .widthInsensitive], locale: nil)
        s = s.replacingOccurrences(of: "\\[[^\\]]*\\]|\\([^)]*\\)", with: " ", options: .regularExpression)
        s = s.replacingOccurrences(of: "’", with: "'")
        s = s.replacingOccurrences(of: "'", with: "")
        let kept = s.unicodeScalars.map { scalar -> Character in
            CharacterSet.alphanumerics.contains(scalar) ? Character(scalar) : " "
        }
        return String(kept).split(separator: " ").joined(separator: " ")
    }

    /// Words, or single characters for scripts written without spaces
    /// (Japanese, Chinese), so overlap can be counted either way.
    static func tokens(_ normalized: String) -> [String] {
        let unspaced = normalized.unicodeScalars.contains {
            (0x3040...0x9FFF).contains($0.value) || (0xF900...0xFAFF).contains($0.value)
        }
        if unspaced { return normalized.replacingOccurrences(of: " ", with: "").map { String($0) } }
        return normalized.split(separator: " ").map(String.init)
    }

    /// Dice overlap of the two token multisets, 0...1.
    static func similarity(_ a: [String], _ b: [String]) -> Double {
        guard !a.isEmpty, !b.isEmpty else { return 0 }
        var counts: [String: Int] = [:]
        for token in a { counts[token, default: 0] += 1 }
        var common = 0
        for token in b where (counts[token] ?? 0) > 0 {
            counts[token]! -= 1
            common += 1
        }
        return 2 * Double(common) / Double(a.count + b.count)
    }

    /// The offset, or nil when too few cues match or they disagree.
    /// Choruses repeat, so each cue may match several lines: every match
    /// is a candidate difference, the difference most cues agree on wins
    /// (a vote), and the median of the agreeing differences is the answer.
    static func offset(cues: [MusicVideoCaptionCue], lines: [MusicVideoLyricLine],
                       minimumMatches: Int = minimumMatches, maximumSpread: Double = maximumSpread) -> Result? {
        let lyricTokens = lines.map { (start: $0.start, tokens: tokens(normalize($0.text))) }
            .filter { $0.tokens.count >= 2 && $0.start.isFinite }
        guard !lyricTokens.isEmpty else { return nil }
        var candidates: [[Double]] = []
        for cue in cues where cue.start.isFinite {
            let cueTokens = tokens(normalize(cue.text))
            guard cueTokens.count >= 2 else { continue }
            let differences = lyricTokens.filter { similarity(cueTokens, $0.tokens) >= similarityThreshold }
                .map { cue.start - $0.start }
            if !differences.isEmpty { candidates.append(differences) }
        }
        guard candidates.count >= minimumMatches else { return nil }
        // The vote: the candidate difference with the most cues near it.
        var best: (center: Double, count: Int)?
        for center in candidates.joined() {
            let count = candidates.filter { $0.contains { abs($0 - center) <= agreement } }.count
            if best == nil || count > best!.count { best = (center, count) }
        }
        guard let best else { return nil }
        let agreeing = candidates.compactMap { differences in
            differences.filter { abs($0 - best.center) <= agreement }.min { abs($0 - best.center) < abs($1 - best.center) }
        }
        guard agreeing.count >= minimumMatches else { return nil }
        let center = median(agreeing)
        let spread = median(agreeing.map { abs($0 - center) })
        guard spread <= maximumSpread, MusicVideoTiming.offsetRange.contains(center) else { return nil }
        return Result(offset: (center * 10).rounded() / 10, matched: agreeing.count, spread: spread)
    }

    static func median(_ values: [Double]) -> Double {
        let sorted = values.sorted()
        guard !sorted.isEmpty else { return 0 }
        let middle = sorted.count / 2
        return sorted.count % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle]
    }
}

// MARK: - Duration estimate

enum MusicVideoDurationEstimate {
    /// A video this much longer than the song (seconds) probably opens with
    /// that much intro. Outside the range the edits differ some other way.
    static let range: ClosedRange<Double> = 2...40

    /// The guessed intro, or nil when the lengths say nothing useful.
    static func intro(videoDuration: Double?, songDuration: Double) -> Double? {
        guard let videoDuration, videoDuration.isFinite, songDuration.isFinite, songDuration > 0 else { return nil }
        let difference = videoDuration - songDuration
        guard range.contains(difference) else { return nil }
        return (difference * 10).rounded() / 10
    }
}
