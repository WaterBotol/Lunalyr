import Foundation

/// NetEase Cloud Music publishes a word-level karaoke format ("yrc") that is
/// reachable with no API key. Each line carries per-word start times and
/// durations, e.g.
///
///   [32820,4800](32820,540,0)Maybe (33360,120,0)you (33480,180,0)can ...
///
/// i.e. `[lineStartMs,lineDurationMs]` followed by `(wordStartMs,wordDurationMs,0)word`
/// repeated. This is genuine syllable-accurate timing rather than the
/// derived-from-line-length approximation LRCLIB forces.
///
/// Availability is best for tracks in NetEase's catalogue; when a match is
/// not confident, callers fall back to LRCLIB's line-level data.
enum NetEaseLyricsProvider {
    /// Longest a single sung word is allowed to hold. Real sustains top out
    /// around 3s; anything beyond is bad data, not singing.
    private static let maxWordDuration: Double = 5.0

    static func timedLines(title: String, artist: String, duration: Double) async -> [LyricsFetcher.Line]? {
        guard !Task.isCancelled,
              let songID = await songID(title: title, artist: artist, duration: duration),
              let payload = await lyricPayload(id: songID) else {return nil}
        if let yrc=payload.yrc?.lyric {
            let parsed=parseYRC(yrc)
            if !parsed.isEmpty {return attachingRomanization(payload.romalrc?.lyric,to:parsed,duration:duration)}
        }
        if let lrc=payload.lrc?.lyric {
            let parsed=LyricsFetcher.parseLRC(lrc,trackDuration:duration)
            if !parsed.isEmpty {return attachingRomanization(payload.romalrc?.lyric,to:parsed,duration:duration)}
        }
        return nil
    }

    // MARK: - Search

    /// The catalogue entry a search settled on. The fetcher ranks and labels
    /// NetEase by it, so a different edit cannot pass as the playing song.
    struct MatchedSong: Equatable, Sendable {
        var id: Int
        var title: String
        var artist: String
        var duration: Double?
    }

    /// Receives the entry `songID` picks while `timedLines` runs, and why a
    /// request failed, so a lookup can tell an outage from "no match". Scoped
    /// to a lookup with `$recorder.withValue`, so concurrent lookups never mix.
    final class MatchRecorder: @unchecked Sendable {
        /// The session the lookup's requests use.
        let session: URLSession
        private let lock = NSLock()
        private var stored: MatchedSong?
        private var error: Error?
        init(session: URLSession = .shared) { self.session = session }
        var song: MatchedSong? {
            get { lock.withLock { stored } }
            set { lock.withLock { stored = newValue } }
        }
        var failure: Error? {
            get { lock.withLock { error } }
            set { lock.withLock { error = newValue } }
        }
    }
    @TaskLocal static var recorder: MatchRecorder?

    /// One search result, as `bestSong` compares it.
    struct SearchHit {
        var id: Int
        var name: String
        var artists: [String]
        /// Milliseconds, as NetEase reports it.
        var duration: Int?
    }

    private static func songID(title: String, artist: String, duration: Double) async -> Int? {
        var components = URLComponents(string: "https://music.163.com/api/search/get")
        components?.queryItems = [
            URLQueryItem(name: "s", value: "\(title) \(artist)"),
            URLQueryItem(name: "type", value: "1"),
            URLQueryItem(name: "limit", value: "8"),
        ]
        guard let url = components?.url, let data = await get(url) else { return nil }
        // A reply without a result is an error ("code": -460 and the like);
        // a result without songs is a real "no match".
        guard let result = (try? JSONDecoder().decode(SearchResponse.self, from: data))?.result else {
            recorder?.failure = LyricHTTP.Failure(message: "NetEase: unexpected search response")
            return nil
        }
        let hits = (result.songs ?? []).map {
            SearchHit(id: $0.id, name: $0.name, artists: ($0.artists ?? []).map(\.name), duration: $0.duration)
        }
        guard let match = bestSong(hits, title: title, artist: artist, duration: duration) else { return nil }
        recorder?.song = match
        return match.id
    }

    /// Search returns many editions of a popular track -- radio edits, live
    /// cuts, remixes -- all with similar titles and artists. A wrong edit
    /// means every word timestamp drifts, so every hit is scored and the best
    /// kept: an edition whose runtime matches first, then the closest title
    /// and artist, then the smallest runtime difference.
    static func bestSong(_ songs: [SearchHit], title: String, artist: String, duration: Double) -> MatchedSong? {
        let query = LyricQuery(title: title, artist: artist, album: nil, duration: duration)
        var best: (song: MatchedSong, key: [Double])?
        for song in songs {
            let names = song.artists.filter { !$0.isEmpty }
            let credit = names.joined(separator: ", ")
            let titleMatch = LyricQuery.similarity(title, song.name)
            let artistMatch = query.hasArtist
                ? names.map { LyricQuery.similarity(artist, $0) }.reduce(LyricQuery.similarity(artist, credit), max) : 1
            guard titleMatch >= 0.75, artistMatch >= 0.65 else { continue }
            // An entry without a length cannot be told apart from other edits.
            let length = song.duration.map { Double($0) / 1000 }.flatMap { $0 > 0 ? $0 : nil }
            guard duration <= 0 || length != nil else { continue }
            let delta = duration > 0 ? length.map { abs($0 - duration) } ?? 0 : 0
            guard duration <= 0 || delta < 8 else { continue }
            let fits = duration <= 0 || delta <= max(3, duration * 0.02)
            let key: [Double] = [fits ? 1 : 0, titleMatch, artistMatch, -delta]
            if let current = best, !current.key.lexicographicallyPrecedes(key) { continue }
            best = (MatchedSong(id: song.id, title: song.name, artist: credit, duration: length), key)
        }
        return best?.song
    }

    // MARK: - Lyric fetch

    private static func lyricPayload(id: Int) async -> LyricResponse? {
        // rv=-1 adds `romalrc`, NetEase's own romanised lines (romaji, Korean).
        guard let url=URL(string:"https://music.163.com/api/song/lyric?id=\(id)&lv=1&kv=1&tv=-1&yv=1&rv=-1"),
              let data=await get(url) else {return nil}
        return try? JSONDecoder().decode(LyricResponse.self,from:data)
    }

    private static func get(_ url: URL) async -> Data? {
        guard !Task.isCancelled else {return nil}
        var request = URLRequest(url: url)
        request.setValue("Mozilla/5.0 (Macintosh; Intel Mac OS X 14_0)", forHTTPHeaderField: "User-Agent")
        request.setValue("https://music.163.com/", forHTTPHeaderField: "Referer")
        request.timeoutInterval = 6
        do {
            let (data, response) = try await (recorder?.session ?? .shared).data(for: request)
            guard let http = response as? HTTPURLResponse, http.statusCode == 200 else {
                let status = (response as? HTTPURLResponse)?.statusCode ?? 0
                recorder?.failure = LyricHTTP.Failure(message: "NetEase: HTTP \(status). The service may be temporarily unavailable.")
                return nil
            }
            return data
        } catch {
            recorder?.failure = error
            return nil
        }
    }

    // MARK: - yrc parsing

    static func parseYRC(_ yrc: String) -> [LyricsFetcher.Line] {
        guard yrc.utf8.count <= 2_000_000,
              let headerRegex = try? NSRegularExpression(pattern: #"^\[(\d+),(\d+)\]"#),
              let timingRegex = try? NSRegularExpression(pattern: #"\((\d+),(\d+),-?\d+\)"#)
        else { return [] }

        var lines: [LyricsFetcher.Line] = []
        var inCredits = true
        for rawLine in yrc.split(whereSeparator: \.isNewline) {
            let s = String(rawLine)
            let ns = s as NSString
            guard let header = headerRegex.firstMatch(in: s, range: NSRange(location: 0, length: ns.length)),
                  let startMs = Double(ns.substring(with: header.range(at: 1))),
                  let durationMs = Double(ns.substring(with: header.range(at: 2)))
            else { continue }

            // A word's text runs up to the next timing tuple, so brackets in
            // the lyric itself ("Oh (yeah) baby") stay in place.
            let body = NSMaxRange(header.range)
            let timings = timingRegex.matches(in: s, range: NSRange(location: body, length: ns.length - body))
            var pieces: [LyricScript.TimedToken] = []
            var fullText = "", spaced = true
            var vocalEnd = 0.0
            for (index, timing) in timings.enumerated() {
                let from = NSMaxRange(timing.range)
                let to = index + 1 < timings.count ? timings[index + 1].range.location : ns.length
                let piece = ns.substring(with: NSRange(location: from, length: to - from))
                let wordStart = Double(ns.substring(with: timing.range(at: 1))) ?? 0
                let wordDuration = Double(ns.substring(with: timing.range(at: 2))) ?? 0
                fullText += piece
                vocalEnd = max(vocalEnd, (wordStart + wordDuration) / 1000)
                let glued = !spaced && !(piece.first?.isWhitespace ?? true)
                spaced = piece.last?.isWhitespace ?? spaced
                let trimmed = piece.trimmingCharacters(in: .whitespaces)
                guard !trimmed.isEmpty else { continue }

                // yrc occasionally parks a huge duration on a trailing token
                // (a lone "!" carrying 30+ seconds across an instrumental).
                // Left alone that pins the word at full emphasis and glow, so
                // cap what any single word can hold.
                let start = wordStart / 1000
                pieces.append(.init(text: trimmed, start: start, end: start + min(max(wordDuration / 1000, 0.06), maxWordDuration), glued: glued))
            }

            let cleaned = fullText.trimmingCharacters(in: .whitespaces)
            guard !cleaned.isEmpty else { continue }
            if LyricCredits.isCredit(cleaned, inCredits: inCredits) { inCredits = true; continue }
            // Zero-length rows ("[0,0](0,0,0) 作词 : …") are headers, not lyrics.
            // A zero-length row that does carry sung words ends with them.
            let start = startMs / 1000
            let end = durationMs > 0 ? (startMs + durationMs) / 1000 : vocalEnd
            guard end > start else { continue }
            // Glued Latin syllables ("beau" "ti" "ful ") join into one word;
            // Han characters stay separate karaoke tokens.
            let words = LyricScript.hasLetters(cleaned) ? LyricScript.words(from: attachingPunctuation(pieces, lineEnd: end)) : []
            if !words.isEmpty { inCredits = false }
            lines.append(LyricsFetcher.Line(
                start: start,
                end: end,
                text: words.isEmpty ? "" : cleaned,
                words: words,
                isInstrumental: words.isEmpty, wordTimed: !words.isEmpty
            ))
        }
        return lines
    }

    /// Bare punctuation is its own piece in yrc. A mark touching a word
    /// belongs to it: before the word when it opens one ("(" "yeah" ")",
    /// "\"" "Hello" "\""), otherwise after the word it follows ("day" "!").
    /// A mark with spaces on both sides ("Je t'aime !", "Oh - yeah") stays a
    /// token of its own, so every word reads exactly as it does in the line.
    /// A mark's own timing means little, so a word keeps its own.
    private static func attachingPunctuation(_ pieces: [LyricScript.TimedToken], lineEnd: Double) -> [LyricScript.TimedToken] {
        func opens(_ text: String) -> Bool {
            text.unicodeScalars.allSatisfy { [.openPunctuation, .initialPunctuation].contains($0.properties.generalCategory) }
        }
        var tokens: [LyricScript.TimedToken] = []
        var index = 0
        while index < pieces.count {
            // A run of pieces with no whitespace between them: one source word.
            var next = index + 1
            while next < pieces.count, pieces[next].glued { next += 1 }
            let run = Array(pieces[index..<next])
            index = next
            guard run.contains(where: { LyricScript.hasLetters($0.text) }) else {
                let start = run[0].start
                tokens.append(.init(text: run.map(\.text).joined(), start: start,
                                    end: lineEnd > start ? min(run[run.count - 1].end, lineEnd) : run[run.count - 1].end, glued: false))
                continue
            }
            var opening = "", hasWord = false
            for (offset, piece) in run.enumerated() {
                if LyricScript.hasLetters(piece.text) {
                    tokens.append(.init(text: opening + piece.text, start: piece.start, end: piece.end, glued: hasWord))
                    opening = ""; hasWord = true
                } else if hasWord, !(opens(piece.text) && run[(offset + 1)...].contains(where: { LyricScript.hasLetters($0.text) })) {
                    tokens[tokens.count - 1].text += opening + piece.text
                    opening = ""
                } else {
                    opening += piece.text
                }
            }
            if !opening.isEmpty { tokens[tokens.count - 1].text += opening }
        }
        return tokens
    }

    /// Attaches NetEase's romanised lines (`romalrc`, timed like the LRC) to
    /// the lines they read. A yrc line can start a little after its LRC stamp,
    /// so a reading goes to the nearest line within 0.6 s, and only when that
    /// line has no reading nearer to it.
    static func attachingRomanization(_ romalrc: String?, to lines: [LyricsFetcher.Line], duration: Double) -> [LyricsFetcher.Line] {
        guard let romalrc, !romalrc.isEmpty else { return lines }
        let readings = LyricsFetcher.parseLRC(romalrc, trackDuration: duration).filter { !$0.isInstrumental }
        let order = lines.indices.filter { !lines[$0].isInstrumental }.sorted { lines[$0].start < lines[$1].start }
        guard !readings.isEmpty, !order.isEmpty else { return lines }
        let lineStarts = order.map { lines[$0].start }, readingStarts = readings.map(\.start)
        var result = lines
        for (index, reading) in readings.enumerated() {
            guard let nearestLine = nearest(reading.start, in: lineStarts),
                  abs(lineStarts[nearestLine] - reading.start) <= 0.6,
                  nearest(lineStarts[nearestLine], in: readingStarts) == index else { continue }
            let target = order[nearestLine]
            guard result[target].romanization == nil,
                  reading.text.lowercased() != result[target].text.lowercased() else { continue }
            result[target].romanization = reading.text
        }
        return result
    }

    /// Index of the value closest to `value` in ascending `sorted`.
    private static func nearest(_ value: Double, in sorted: [Double]) -> Int? {
        guard !sorted.isEmpty else { return nil }
        var low = 0, high = sorted.count
        while low < high {
            let middle = (low + high) / 2
            if sorted[middle] < value { low = middle + 1 } else { high = middle }
        }
        if low == sorted.count { return low - 1 }
        return low > 0 && value - sorted[low - 1] <= sorted[low] - value ? low - 1 : low
    }

    // MARK: - Payloads

    private struct SearchResponse: Decodable {
        struct Result: Decodable { var songs: [Song]? }
        var result: Result?
    }

    private struct Song: Decodable {
        struct Artist: Decodable { var name: String }
        var id: Int
        var name: String
        var artists: [Artist]?
        var duration: Int?
    }

    private struct LyricResponse: Decodable {
        struct Lyric: Decodable { var lyric: String? }
        var yrc: Lyric?
        var lrc: Lyric?
        var romalrc: Lyric?
    }
}
