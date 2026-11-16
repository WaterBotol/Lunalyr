import Foundation

// Syncing a YouTube video from its own captions: the caption track (the
// uploader's lyric captions, or YouTube's speech recognition, which times
// each word) is matched with the song's timed lyric lines, and where they
// agree, how far into the video the song starts is measured rather than
// guessed. Plain values only (Foundation), so it can be tested and run by
// Scripts/mv-caption-harness.swift outside the app.
//
// The text comes from inside YouTube's own player (`YouTubeEmbed`): its
// /api/timedtext replies are read as the player loads them, since YouTube
// refuses that endpoint to requests from anywhere else.

/// A timed lyric line in the song's own time.
struct MusicVideoLyricLine: Equatable, Sendable {
    var start: Double
    var text: String
}

/// A caption in the video's own time. `words`: when each word starts,
/// for tracks that say (YouTube's automatic captions do).
struct MusicVideoCaptionCue: Equatable, Sendable {
    struct Word: Equatable, Sendable {
        var start: Double
        var text: String
    }

    var start: Double
    var end: Double
    var text: String
    var words: [Word]?

    init(start: Double, end: Double, text: String, words: [Word]? = nil) {
        self.start = start
        self.end = end
        self.text = text
        self.words = words
    }
}

enum MusicVideoCaptionSync {
    // MARK: Parsing

    /// Cues from a timed-text reply in either of YouTube's formats (json3,
    /// srv3 or the older XML). Empty when it is neither.
    static func cues(from data: Data) -> [MusicVideoCaptionCue] {
        guard let first = data.first(where: { !($0 == 0x20 || $0 == 0x0A || $0 == 0x0D || $0 == 0x09 || $0 == 0xEF || $0 == 0xBB || $0 == 0xBF) }) else { return [] }
        if first == UInt8(ascii: "{") { return cues(json3: data) }
        if first == UInt8(ascii: "<") { return cues(xml: data) }
        return []
    }

    /// `{"events":[{"tStartMs":1200,"dDurationMs":2000,"segs":[{"utf8":"what's"},
    /// {"utf8":" wrong","tOffsetMs":1000}]}]}`. Events that only carry a
    /// newline (`aAppend`) or a window style are skipped.
    static func cues(json3 data: Data) -> [MusicVideoCaptionCue] {
        guard let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let events = object["events"] as? [[String: Any]] else { return [] }
        var cues: [MusicVideoCaptionCue] = []
        for event in events {
            guard let startMs = (event["tStartMs"] as? NSNumber)?.doubleValue, startMs.isFinite,
                  let segments = event["segs"] as? [[String: Any]] else { continue }
            let start = startMs / 1000
            let duration = ((event["dDurationMs"] as? NSNumber)?.doubleValue ?? 0) / 1000
            var words: [MusicVideoCaptionCue.Word] = []
            var timed = false
            for segment in segments {
                guard let text = segment["utf8"] as? String else { continue }
                let offset = (segment["tOffsetMs"] as? NSNumber)?.doubleValue
                if offset != nil { timed = true }
                let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
                guard !trimmed.isEmpty else { continue }
                words.append(.init(start: start + (offset ?? 0) / 1000, text: trimmed))
            }
            let text = segments.compactMap { $0["utf8"] as? String }.joined()
                .replacingOccurrences(of: "\n", with: " ").trimmingCharacters(in: .whitespacesAndNewlines)
            guard !text.isEmpty else { continue }
            cues.append(MusicVideoCaptionCue(start: start, end: start + max(0, duration), text: text,
                                             words: timed && words.count > 1 ? words : nil))
        }
        return cues
    }

    /// srv3 (`<p t="1200" d="2000"><s t="0">what's</s><s t="1000"> wrong</s></p>`,
    /// milliseconds) or the older format (`<text start="1.2" dur="2">`, seconds).
    static func cues(xml data: Data) -> [MusicVideoCaptionCue] {
        let reader = TimedTextXMLReader()
        let parser = XMLParser(data: data)
        parser.delegate = reader
        guard parser.parse() else { return [] }
        return reader.cues
    }

    // MARK: Words

    /// Lower case, without accents, punctuation, bracketed asides ("[Music]",
    /// "(Chorus)", backing vocals) or note symbols; spaces collapsed.
    static func normalize(_ text: String) -> String {
        var s = text.folding(options: [.caseInsensitive, .diacriticInsensitive, .widthInsensitive], locale: nil)
        s = s.replacingOccurrences(of: "\\[[^\\]]*\\]|\\([^)]*\\)", with: " ", options: .regularExpression)
        s = s.replacingOccurrences(of: "\u{2019}", with: "'").replacingOccurrences(of: "'", with: "")
        let kept = s.unicodeScalars.map { CharacterSet.alphanumerics.contains($0) ? Character($0) : " " }
        return String(kept).split(separator: " ").joined(separator: " ")
    }

    /// Words, or single characters for scripts written without spaces
    /// (Japanese, Chinese), so they can be matched either way.
    static func tokens(_ text: String) -> [String] {
        let normalized = normalize(text)
        if isUnspaced(normalized) { return normalized.replacingOccurrences(of: " ", with: "").map { String($0) } }
        return normalized.split(separator: " ").map(String.init)
    }

    private static func isUnspaced(_ text: String) -> Bool {
        text.unicodeScalars.contains { (0x3040...0x9FFF).contains($0.value) || (0xF900...0xFAFF).contains($0.value) }
    }

    /// What speech recognition writes for music, applause or singing it
    /// can't read: not lyrics.
    static let noiseCues: Set<String> = ["foreign", "music", "applause", "laughter", "instrumental", "singing"]

    /// The same word, allowing for how lyrics and recognition spell it:
    /// "askin" and "asking", one letter off in a longer word.
    static func sameWord(_ a: String, _ b: String) -> Bool {
        if a == b { return true }
        if a + "g" == b || b + "g" == a { return true }
        guard min(a.count, b.count) >= 4, abs(a.count - b.count) <= 1 else { return false }
        return editDistance(Array(a), Array(b), limit: 1) <= 1
    }

    private static func editDistance(_ a: [Character], _ b: [Character], limit: Int) -> Int {
        guard !a.isEmpty, !b.isEmpty else { return max(a.count, b.count) }
        var previous = Array(0...b.count)
        for i in 1...a.count {
            var current = [i] + Array(repeating: 0, count: b.count)
            for j in 1...b.count {
                current[j] = min(previous[j] + 1, current[j - 1] + 1, previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1))
            }
            if (current.min() ?? 0) > limit { return limit + 1 }
            previous = current
        }
        return previous[b.count]
    }

    /// One caption word in the video's time. `exact`: its start was given
    /// (a timed word, or the first word of a cue); otherwise spread over
    /// its cue.
    struct CaptionWord: Equatable {
        var token: String
        var time: Double
        var exact: Bool
    }

    /// The caption track as one stream of words, noise left out.
    ///
    /// Speech recognition times most words, but a word it could not place
    /// comes exactly 1.000 s after the one before: a placeholder, not
    /// counted as exact. Untimed cues (an uploader's) time only their first
    /// word; the rest are spread over the cue.
    static func captionWords(_ cues: [MusicVideoCaptionCue]) -> [CaptionWord] {
        var stream: [CaptionWord] = []
        for cue in cues.sorted(by: { $0.start < $1.start }) where cue.start.isFinite {
            let all = tokens(cue.text)
            guard !all.isEmpty, !(all.count == 1 && noiseCues.contains(all[0])) else { continue }
            if let words = cue.words, !words.isEmpty {
                var previous: Double?
                for word in words where word.start.isFinite {
                    let placeholder = previous.map { abs(word.start - $0 - placeholderGap) < 0.015 } ?? false
                    for (index, token) in tokens(word.text).enumerated() {
                        stream.append(CaptionWord(token: token, time: word.start, exact: index == 0 && !placeholder))
                    }
                    previous = word.start
                }
            } else {
                let span = cue.end > cue.start ? cue.end - cue.start : Double(all.count) * typicalWord
                let step = span / Double(all.count)
                for (index, token) in all.enumerated() {
                    stream.append(CaptionWord(token: token, time: cue.start + Double(index) * step, exact: index == 0))
                }
            }
        }
        return stream
    }

    /// A word's typical length when sung, seconds.
    static let typicalWord = 0.3
    /// Recognition's gap for a word it could not time.
    static let placeholderGap = 1.0

    // MARK: Matching

    /// One lyric line found in the captions.
    struct Match: Equatable {
        var line: Int
        var songTime: Double
        var videoTime: Double
        /// Share of the line's words found, 0...1.
        var score: Double
        var offset: Double { videoTime - songTime }
    }

    /// A stretch of the song with its own offset (`Result.segments`).
    struct Segment: Equatable {
        var songStart: Double
        var songEnd: Double
        var offset: Double
        var lines: Int
    }

    struct Result: Equatable {
        /// The offset (video time − song time) when the captions agree on
        /// one well enough to trust (at least `minimumMatches` lines within
        /// `tolerance` of the median, and `minimumAgreement` of all matched
        /// lines, with no rival offset nearly as well supported); nil
        /// otherwise.
        var offset: Double?
        /// Each matched line's match, the one nearest the consensus when a
        /// line matched in several places (a chorus).
        var matches: [Match]
        /// Matches within `tolerance` of the median.
        var agreeing: Int
        /// The agreeing matches' median absolute deviation, seconds.
        var spread: Double
        /// Two stretches with offsets a second or more apart, one after the
        /// other: a video with an extra scene mid-song. Empty otherwise.
        var segments: [Segment]
        /// Why `offset` is there or not.
        var verdict: Verdict = .noMatches

        static let none = Result(offset: nil, matches: [], agreeing: 0, spread: 0, segments: [])
    }

    /// Whether the captions' offset is trusted, and if not, why.
    enum Verdict: Equatable {
        case trusted
        /// No lyric line was found in the captions.
        case noMatches
        /// Fewer than `minimumMatches` lines agree.
        case tooFew
        /// Enough lines agree, but under `minimumAgreement` of those matched.
        case thinAgreement
        /// Another offset (a chorus one repeat off) has `rival` lines
        /// against the winner's `support`.
        case ambiguous(rival: Int, support: Int)

        /// For the log: a few words, no lyric text.
        var summary: String {
            switch self {
            case .trusted: return "trusted"
            case .noMatches: return "refused: no lyric line found in the captions"
            case .tooFew: return "refused: too few agreeing lines (need \(MusicVideoCaptionSync.minimumMatches))"
            case .thinAgreement:
                return "refused: thin agreement (under \(Int(MusicVideoCaptionSync.minimumAgreement * 100))% of matched lines)"
            case .ambiguous(let rival, let support):
                return "refused: ambiguous, another offset has \(rival) lines against \(support) (a chorus shift)"
            }
        }
    }

    static let minimumMatches = 5
    static let minimumAgreement = 0.6
    static let tolerance = 0.5
    /// A lyric line needs this many words to be matched (characters, for
    /// scripts without spaces: `minimumCharacters`).
    static let minimumWords = 3
    static let minimumCharacters = 4
    /// Share of a line's words that must be found, in order.
    static let minimumScore = 0.6
    static let defaultRange: ClosedRange<Double> = -300...300
    /// The latest word of a line its start may be worked back from.
    static let maximumAnchorWord = 2

    /// Where each lyric line could be in the captions, as offsets.
    static func candidates(cues: [MusicVideoCaptionCue], lines: [MusicVideoLyricLine],
                           range: ClosedRange<Double> = defaultRange) -> [[Match]] {
        let stream = captionWords(cues)
        let sorted = lines.enumerated().filter { $0.element.start.isFinite }.sorted { $0.element.start < $1.element.start }
        var result: [[Match]] = []
        for (position, entry) in sorted.enumerated() {
            let line = entry.element
            let words = tokens(line.text)
            let unspaced = isUnspaced(words.joined())
            guard words.count >= (unspaced ? minimumCharacters : minimumWords) else { continue }
            let next = position + 1 < sorted.count ? sorted[position + 1].element.start : line.start + 4
            let pace = min(0.4, max(0.12, (next - line.start) / Double(words.count)))
            var found: [Match] = []
            var p = 0
            while p < stream.count {
                defer { p += 1 }
                // A start: one of the line's first two words.
                guard let first = (0..<min(2, words.count)).first(where: { sameWord(words[$0], stream[p].token) }) else { continue }
                var pairs = [(first, p)]
                var j = p + 1
                for k in (first + 1)..<words.count {
                    guard let hit = (j..<min(stream.count, j + 3)).first(where: { sameWord(words[k], stream[$0].token) }) else { continue }
                    pairs.append((k, hit))
                    j = hit + 1
                }
                let score = Double(pairs.count) / Double(words.count)
                guard pairs.count >= (unspaced ? minimumCharacters : minimumWords), score >= minimumScore else { continue }
                // Not spread wider than the line could be sung.
                guard let last = pairs.last, stream[last.1].time - stream[p].time <= Double(words.count) * 0.8 + 2 else { continue }
                // The line's start in the video: its first word if exactly
                // timed, else back from the first exact word (one of its
                // first three) at the line's pace.
                guard let anchor = pairs.first(where: { stream[$0.1].exact }), anchor.0 <= maximumAnchorWord else { continue }
                let videoTime = stream[anchor.1].time - Double(anchor.0) * pace
                let match = Match(line: entry.offset, songTime: line.start, videoTime: videoTime, score: score)
                guard range.contains(match.offset) else { continue }
                if let previous = found.last, abs(previous.videoTime - videoTime) < 1 {
                    if score > previous.score { found[found.count - 1] = match }
                    continue
                }
                found.append(match)
            }
            if !found.isEmpty { result.append(found) }
        }
        return result
    }

    /// The offset the captions agree on, if they do (`Result`).
    static func measure(cues: [MusicVideoCaptionCue], lines: [MusicVideoLyricLine],
                        range: ClosedRange<Double> = defaultRange) -> Result {
        consensus(candidates(cues: cues, lines: lines, range: range))
    }

    /// Each inner list: the places one lyric line matched.
    static func consensus(_ lines: [[Match]]) -> Result {
        guard let center = vote(lines) else { return .none }
        let chosen = lines.map { nearest($0, to: center) }
        let median = median(chosen.map(\.offset))
        let agreeing = chosen.map(\.offset).filter { abs($0 - median) <= tolerance }
        let refined = Self.median(agreeing)
        let spread = Self.median(agreeing.map { abs($0 - refined) })
        // Another offset with nearly as many lines (a chorus heard once
        // less, one chorus off) leaves it ambiguous.
        let support = self.support(lines, center: center)
        let rival = lines.joined().map(\.offset).filter { abs($0 - center) > 1 }
            .map { self.support(lines, center: $0) }.max() ?? 0
        let verdict: Verdict
        if agreeing.count < minimumMatches {
            verdict = .tooFew
        } else if Double(agreeing.count) < minimumAgreement * Double(chosen.count) {
            verdict = .thinAgreement
        } else if Double(rival) >= rivalShare * Double(support) {
            verdict = .ambiguous(rival: rival, support: support)
        } else {
            verdict = .trusted
        }
        return Result(offset: verdict == .trusted ? (refined * 20).rounded() / 20 : nil,
                      matches: chosen.sorted { $0.songTime < $1.songTime }, agreeing: agreeing.count,
                      spread: spread, segments: segments(lines, first: center), verdict: verdict)
    }

    /// A rival offset with this share of the winner's lines makes it ambiguous.
    static let rivalShare = 0.75

    /// How many lines have a match within `tolerance` of `center`.
    private static func support(_ lines: [[Match]], center: Double) -> Int {
        lines.filter { $0.contains { abs($0.offset - center) <= tolerance } }.count
    }

    /// The offset with the most lines having a match near it (ties: the
    /// better-matched).
    private static func vote(_ lines: [[Match]], within: Double = tolerance) -> Double? {
        var best: (center: Double, count: Int, score: Double)?
        for center in lines.joined().map(\.offset) {
            var count = 0, score = 0.0
            for line in lines {
                if let hit = line.filter({ abs($0.offset - center) <= within }).max(by: { $0.score < $1.score }) {
                    count += 1
                    score += hit.score
                }
            }
            if best == nil || count > best!.count || (count == best!.count && score > best!.score) {
                best = (center, count, score)
            }
        }
        return best?.center
    }

    private static func nearest(_ matches: [Match], to center: Double) -> Match {
        matches.min { abs($0.offset - center) < abs($1.offset - center) }!
    }

    /// Two offsets a second or more apart, each held by at least
    /// `segmentLines` lines, the one's lines all before the other's (one
    /// stray allowed): the video's edit differs from the song partway.
    static func segments(_ lines: [[Match]], first: Double) -> [Segment] {
        let others = lines.filter { line in !line.contains { abs($0.offset - first) <= 1 } }
            .map { $0.filter { abs($0.offset - first) > 1 } }
        guard let second = vote(others) else { return [] }
        var assigned: [(time: Double, which: Int, offset: Double)] = []
        for line in lines {
            let a = nearest(line, to: first), b = nearest(line, to: second)
            if abs(a.offset - first) <= tolerance { assigned.append((a.songTime, 0, a.offset)) }
            else if abs(b.offset - second) <= tolerance { assigned.append((b.songTime, 1, b.offset)) }
        }
        assigned.sort { $0.time < $1.time }
        let counts = [assigned.filter { $0.which == 0 }.count, assigned.filter { $0.which == 1 }.count]
        guard counts.allSatisfy({ $0 >= segmentLines }) else { return [] }
        // The best split: fewest lines on the wrong side.
        var best: (index: Int, strays: Int, firstEarly: Bool)?
        for split in 1..<assigned.count {
            for firstEarly in [true, false] {
                let early = firstEarly ? 0 : 1
                let strays = assigned[..<split].filter { $0.which != early }.count
                    + assigned[split...].filter { $0.which == early }.count
                if best == nil || strays < best!.strays { best = (split, strays, firstEarly) }
            }
        }
        guard let best, best.strays <= 1 else { return [] }
        let early = Array(assigned[..<best.index]), late = Array(assigned[best.index...])
        let earlyWhich = best.firstEarly ? 0 : 1
        func segment(_ part: [(time: Double, which: Int, offset: Double)], _ which: Int) -> Segment {
            let own = part.filter { $0.which == which }
            return Segment(songStart: part.first!.time, songEnd: part.last!.time,
                           offset: (median(own.map(\.offset)) * 20).rounded() / 20, lines: own.count)
        }
        return [segment(early, earlyWhich), segment(late, 1 - earlyWhich)]
    }

    static let segmentLines = 3

    static func median(_ values: [Double]) -> Double {
        let sorted = values.sorted()
        guard !sorted.isEmpty else { return 0 }
        let middle = sorted.count / 2
        return sorted.count % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle]
    }
}

// MARK: - Capture

extension MusicVideoCaptionSync {
    /// The script message the capture script posts to.
    static let messageName = "lunalyrCaptions"
    /// Replies larger than this are not read (a song's track is ~10-80 kB).
    static let maximumReply = 2_000_000

    /// Runs in every frame at document start (a WKUserScript with
    /// `forMainFrameOnly: false`). Inside YouTube's player frame only, it
    /// hides the player's caption text and passes on the text of each
    /// /api/timedtext reply the player loads (fetch or XHR) to
    /// `messageName`. It changes no request and sends nothing anywhere.
    static let captureScript = """
    (function () {
      var host = location.hostname;
      if (host !== 'www.youtube-nocookie.com' && host !== 'www.youtube.com') return;
      if (window.__lunalyrCaptions) return;
      window.__lunalyrCaptions = true;
      function hide() {
        var style = document.createElement('style');
        style.textContent = '.ytp-caption-window-container,.caption-window,.ytp-caption-segment{display:none!important;opacity:0!important}';
        (document.head || document.documentElement).appendChild(style);
      }
      if (document.documentElement) hide(); else document.addEventListener('DOMContentLoaded', hide);
      function pass(url, text, status) {
        try {
          if (typeof text !== 'string' || text.length > \(maximumReply)) return;
          window.webkit.messageHandlers.\(messageName).postMessage({url: String(url), text: text, status: status | 0});
        } catch (e) {}
      }
      function wanted(url) { return String(url || '').indexOf('/api/timedtext') >= 0; }
      var originalFetch = window.fetch;
      if (originalFetch) {
        window.fetch = function (input) {
          var url = typeof input === 'string' ? input : (input && input.url) || '';
          var reply = originalFetch.apply(this, arguments);
          if (wanted(url)) {
            reply.then(function (r) { r.clone().text().then(function (t) { pass(r.url || url, t, r.status); }); }).catch(function () {});
          }
          return reply;
        };
      }
      var open = XMLHttpRequest.prototype.open, send = XMLHttpRequest.prototype.send;
      XMLHttpRequest.prototype.open = function (method, url) { this.__lunalyrURL = url; return open.apply(this, arguments); };
      XMLHttpRequest.prototype.send = function () {
        var request = this;
        if (wanted(request.__lunalyrURL)) {
          request.addEventListener('load', function () {
            var text = '';
            try {
              if (request.responseType === '' || request.responseType === 'text') text = request.responseText;
              else if (typeof request.response === 'string') text = request.response;
              else if (request.responseType === 'json') text = JSON.stringify(request.response);
            } catch (e) {}
            pass(request.responseURL || request.__lunalyrURL, text, request.status);
          });
        }
        return send.apply(this, arguments);
      };
    })();
    """

    /// What a captured reply is, if it is to be believed: from YouTube's
    /// player frame (https, its own host), YouTube's timed-text endpoint on
    /// that host, for `videoID`, not a machine translation (`tlang`).
    struct Capture: Equatable {
        var automatic: Bool
        var language: String?
    }

    static func capture(frameHost: String, frameScheme: String, url: String, videoID: String) -> Capture? {
        let allowed: Set<String> = ["www.youtube-nocookie.com", "www.youtube.com"]
        guard frameScheme.lowercased() == "https", allowed.contains(frameHost.lowercased()),
              let components = URLComponents(string: url, encodingInvalidCharacters: false),
              components.scheme?.lowercased() == "https", let host = components.host?.lowercased(),
              allowed.contains(host), components.path == "/api/timedtext" else { return nil }
        let items = components.queryItems ?? []
        func value(_ name: String) -> String? { items.first { $0.name == name }?.value }
        guard value("v") == videoID, value("tlang") == nil else { return nil }
        return Capture(automatic: value("kind") == "asr", language: value("lang"))
    }
}

// MARK: - XML timed text

private final class TimedTextXMLReader: NSObject, XMLParserDelegate {
    var cues: [MusicVideoCaptionCue] = []
    private var cueStart: Double?
    private var cueEnd: Double?
    private var text = ""
    private var words: [MusicVideoCaptionCue.Word] = []
    private var wordStart: Double?
    private var wordText = ""
    private var timedWords = false

    func parser(_ parser: XMLParser, didStartElement name: String, namespaceURI: String?, qualifiedName: String?,
                attributes: [String: String] = [:]) {
        switch name {
        case "p":
            guard let t = attributes["t"].flatMap(Double.init) else { return }
            begin(start: t / 1000, duration: (attributes["d"].flatMap(Double.init) ?? 0) / 1000)
        case "text":
            guard let t = attributes["start"].flatMap(Double.init) else { return }
            begin(start: t, duration: attributes["dur"].flatMap(Double.init) ?? 0)
        case "s":
            guard let start = cueStart else { return }
            let offset = attributes["t"].flatMap(Double.init)
            if offset != nil { timedWords = true }
            wordStart = start + (offset ?? 0) / 1000
            wordText = ""
        case "br":
            text += " "
        default: break
        }
    }

    private func begin(start: Double, duration: Double) {
        cueStart = start
        cueEnd = start + max(0, duration)
        text = ""
        words = []
        timedWords = false
    }

    func parser(_ parser: XMLParser, foundCharacters string: String) {
        guard cueStart != nil else { return }
        text += string
        if wordStart != nil { wordText += string }
    }

    func parser(_ parser: XMLParser, didEndElement name: String, namespaceURI: String?, qualifiedName: String?) {
        switch name {
        case "s":
            if let wordStart {
                let trimmed = wordText.trimmingCharacters(in: .whitespacesAndNewlines)
                if !trimmed.isEmpty { words.append(.init(start: wordStart, text: trimmed)) }
            }
            wordStart = nil
        case "p", "text":
            guard let start = cueStart, let end = cueEnd else { return }
            let clean = text.replacingOccurrences(of: "\n", with: " ").trimmingCharacters(in: .whitespacesAndNewlines)
            if !clean.isEmpty {
                cues.append(MusicVideoCaptionCue(start: start, end: end, text: clean,
                                                 words: timedWords && words.count > 1 ? words : nil))
            }
            cueStart = nil
        default: break
        }
    }
}
