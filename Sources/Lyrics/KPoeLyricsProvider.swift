import Compression
import Foundation

/// Lyrics from the Lyrics+ / KPoe community nodes, which aggregate
/// syllable-level karaoke lyrics -- including Apple Music's own, the same
/// ones Music.app shows. Free, no key.
///
/// Preferred over NetEase because it returns `"type": "Word"` with a
/// `syllabus` array per line: real per-syllable timing sourced from Apple,
/// so the highlight matches what the user hears in their own app.
enum KPoeLyricsProvider {
    /// Mirrors are tried in order; community nodes come and go.
    static let hosts = [
        "https://lyricsplus.prjktla.workers.dev",
        "https://lyricsplus.binimum.org",
        "https://lyricsplus.prjktla.my.id",
    ]

    struct Hit {
        var lines: [LyricsFetcher.Line]
        /// The upstream the node pulled from, e.g. "Apple".
        var source: String
        /// The recording the node matched, when it reports one.
        var title: String?
        var artist: String?
    }

    /// Returns nil when a node answers that it has no word-timed lyrics for
    /// the song and one other healthy mirror, asked while time allows,
    /// agrees or cannot answer. Nodes run the same service with different
    /// upstream access, so one node's "nothing" is checked once; a node that
    /// said nothing for lyrics another node had rests, like a failing one.
    /// Mirrors that fail rest for a while and are tried last, so one dead
    /// node does not cost every lookup its timeout. Throws when no mirror
    /// gave a usable answer.
    static func lookup(
        title: String, artist: String, album: String?, duration: Double,
        hosts: [String] = hosts, session: URLSession = .shared
    ) async throws -> Hit? {
        var failures: [String] = []
        let began = Date()
        var emptyHanded: [String] = [], checked = false
        let ordered = await health.ordered(hosts)
        if LyricTrace.isOn, ordered != hosts { LyricTrace.note("Lyrics+ mirrors in order (resting ones last or skipped): \(ordered)") }
        for host in ordered {
            try Task.checkCancellation()
            if !emptyHanded.isEmpty {
                // One second opinion, from a mirror that is not resting, and
                // only while its timeout still fits in the lookup's time.
                guard !checked, await health.isHealthy(host), Date().timeIntervalSince(began) < secondOpinionWindow else { break }
                checked = true
            }
            let outcome = await attempt(host: host, title: title, artist: artist, album: album, duration: duration, session: session)
            if LyricTrace.isOn {
                switch outcome {
                case .lyrics(let hit): LyricTrace.note("Lyrics+ \(host): \(hit.lines.count) lines from \(hit.source)")
                case .notFound: LyricTrace.note("Lyrics+ \(host): no lyrics")
                case .abort(let error): LyricTrace.note("Lyrics+ \(host): aborted (\(LyricTrace.describe(error)))")
                case .failed(let reason, let rest): LyricTrace.note("Lyrics+ \(host): failed (\(reason)); rests \(Int(rest)) s")
                }
            }
            switch outcome {
            case .lyrics(let hit):
                await health.answered(host)
                for node in emptyHanded { await health.failed(node, rest: 600) }
                return hit
            case .notFound:
                emptyHanded.append(host)
            case .abort(let error):
                throw error
            case .failed(let reason, let rest):
                await health.failed(host, rest: rest)
                failures.append(reason)
            }
        }
        if !emptyHanded.isEmpty { return nil }
        throw LyricHTTP.Failure(message: failures.isEmpty ? "Lyrics+ is unavailable" :
            "Lyrics+ mirrors unavailable (" + failures.joined(separator: ", ") + ")")
    }
    /// A second opinion starts only this soon after the lookup did, so its
    /// own four-second timeout ends before the fetcher stops waiting.
    private static let secondOpinionWindow: TimeInterval = 3.5

    // MARK: - Fetch

    private enum Attempt {
        case lyrics(Hit)
        case notFound
        /// No connection, or the lookup was cancelled: other mirrors cannot help.
        case abort(Error)
        case failed(String, rest: TimeInterval)
    }

    private static let health = MirrorHealth()

    /// Mirrors that recently failed, and until when they rest.
    private actor MirrorHealth {
        private var restingUntil: [String: Date] = [:]
        /// Healthy mirrors in their usual order, then at most one resting
        /// mirror -- the one due back soonest -- in case it has recovered.
        func ordered(_ hosts: [String]) -> [String] {
            let now = Date()
            let healthy = hosts.filter { (restingUntil[$0] ?? .distantPast) <= now }
            let resting = hosts.filter { (restingUntil[$0] ?? .distantPast) > now }
                .sorted { (restingUntil[$0] ?? now) < (restingUntil[$1] ?? now) }
            return healthy + resting.prefix(1)
        }
        func isHealthy(_ host: String) -> Bool { (restingUntil[host] ?? .distantPast) <= Date() }
        func failed(_ host: String, rest: TimeInterval) { restingUntil[host] = Date().addingTimeInterval(rest) }
        func answered(_ host: String) { restingUntil[host] = nil }
    }

    private static func attempt(
        host: String, title: String, artist: String, album: String?, duration: Double, session: URLSession
    ) async -> Attempt {
        var components = URLComponents(string: "\(host)/v2/lyrics/get")
        var query = [
            URLQueryItem(name: "title", value: title),
            URLQueryItem(name: "artist", value: artist),
        ]
        if duration > 0 { query.append(URLQueryItem(name: "duration", value: String(Int(duration)))) }
        if let album, !album.isEmpty { query.append(URLQueryItem(name: "album", value: album)) }
        components?.queryItems = query
        guard let url = components?.url else { return .failed("invalid address", rest: 0) }

        var request = URLRequest(url: url)
        request.setValue("Lunalyr/0.1 (macOS now-playing lyrics)", forHTTPHeaderField: "User-Agent")
        request.timeoutInterval = 4

        do {
            let (data, response) = try await LyricTrace.data(session, for: request)
            let status = (response as? HTTPURLResponse)?.statusCode ?? 0
            // These nodes serve a gzip body under `content-type:
            // application/json` with no `Content-Encoding` header, so
            // URLSession leaves it compressed. Inflate it ourselves.
            let body = gunzipIfNeeded(data)
            switch status {
            case 200:
                guard let payload = try? JSONDecoder().decode(Response.self, from: body) else {
                    return .failed("unreadable reply", rest: 600)
                }
                return hit(from: payload).map(Attempt.lyrics) ?? .notFound
            case 404 where (try? JSONSerialization.jsonObject(with: body)) != nil:
                // The service's own "no lyrics" answer, as opposed to a
                // missing node's error page.
                return .notFound
            case 429:
                // A short cooldown; the next mirror may not be busy.
                return .failed("busy", rest: 60)
            default:
                return .failed("HTTP \(status)", rest: 600)
            }
        } catch {
            if LyricHTTP.isOffline(error) { return .abort(error) }
            if error is CancellationError || (error as? URLError)?.code == .cancelled { return .abort(CancellationError()) }
            return .failed((error as? URLError)?.code == .timedOut ? "timed out" : "no response", rest: 600)
        }
    }

    private static func hit(from payload: Response) -> Hit? {
        let lines = (payload.lyrics ?? []).compactMap { convert($0) }.filter { !$0.words.isEmpty }
        guard !lines.isEmpty else { return nil }
        // The node reports which upstream it pulled from, e.g. "Apple" or
        // "Lyrics+ (via Apple with QQ)"; keep just the leading name.
        return Hit(lines: lines, source: upstreamName(payload.metadata?.source), title: payload.metadata?.title, artist: payload.metadata?.artist)
    }

    /// The upstream a node names, cut to its leading name. Mirrors name
    /// Apple's lyrics differently: lyricsplus.binimum.org says "qApple" for
    /// the same lines another mirror calls "Apple", so both read "Apple".
    static func upstreamName(_ raw: String?) -> String {
        let raw = raw ?? "Lyrics+"
        let name = (raw.components(separatedBy: " (").first ?? raw).trimmingCharacters(in: .whitespaces)
        return name.lowercased() == "qapple" ? "Apple" : name
    }

    // MARK: - Conversion

    private static func convert(_ entry: Entry) -> LyricsFetcher.Line? {
        let start = entry.time / 1000
        let end = (entry.time + max(entry.duration, 1)) / 1000
        let text = entry.text.trimmingCharacters(in: .whitespaces)
        guard !text.isEmpty else { return nil }

        var words: [LyricsFetcher.Word] = []
        if let syllabus = entry.syllabus, !syllabus.isEmpty {
            words = self.words(syllabus, lineEnd: end)
        }
        guard !words.isEmpty else { return nil }

        return LyricsFetcher.Line(
            start: start, end: end, text: text, words: words, isInstrumental: false, wordTimed: true
        )
    }

    /// A syllable is not always a word: "con", "trol", "la" arrive as three
    /// entries, written together, and are joined back into the word they
    /// spell, from the first syllable's start to the last one's end. Han,
    /// kana, Hangul and Thai syllables stay timed tokens of their own, as
    /// the TTML, YRC and LRC parsers keep them (`LyricScript.words`).
    /// Joining every syllable up to the next space made a Japanese, Chinese
    /// or Thai line, which has none, one word timed from its first syllable,
    /// so the whole line appeared at once.
    private static func words(_ syllabus: [Syllable], lineEnd: Double) -> [LyricsFetcher.Word] {
        var tokens: [LyricScript.TimedToken] = []
        var spaced = true
        for syllable in syllabus {
            defer { spaced = syllable.text.last?.isWhitespace ?? spaced }
            let text = syllable.text.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !text.isEmpty else { continue }
            let start = syllable.time / 1000
            let end = (syllable.time + max(syllable.duration, 1)) / 1000
            // A syllable holds at least a moment, though never past its line.
            tokens.append(.init(text: text, start: start, end: max(end, min(start + 0.06, lineEnd)),
                                glued: !spaced && !(syllable.text.first?.isWhitespace ?? true)))
        }
        return LyricScript.words(from: tokens)
    }

    // MARK: - gzip

    private static func gunzipIfNeeded(_ data: Data) -> Data {
        guard data.count > 18, data[data.startIndex] == 0x1f, data[data.startIndex + 1] == 0x8b else {
            return data
        }
        // Step over the gzip header, including any optional fields the
        // flag byte announces, to reach the raw DEFLATE stream.
        var index = data.startIndex + 10
        let flags = data[data.startIndex + 3]
        if flags & 0x04 != 0, index + 1 < data.endIndex {
            let extraLength = Int(data[index]) | Int(data[index + 1]) << 8
            index += 2 + extraLength
        }
        if flags & 0x08 != 0 {
            while index < data.endIndex, data[index] != 0 { index += 1 }
            index += 1
        }
        if flags & 0x10 != 0 {
            while index < data.endIndex, data[index] != 0 { index += 1 }
            index += 1
        }
        if flags & 0x02 != 0 { index += 2 }
        guard index < data.endIndex else { return data }

        return inflate(data.subdata(in: index..<data.endIndex)) ?? data
    }

    /// Apple's COMPRESSION_ZLIB is raw DEFLATE, which is exactly what sits
    /// inside a gzip container once the header is skipped.
    private static func inflate(_ input: Data) -> Data? {
        let bufferSize = 64 * 1024
        let destination = UnsafeMutablePointer<UInt8>.allocate(capacity: bufferSize)
        defer { destination.deallocate() }

        var stream = compression_stream(
            dst_ptr: destination, dst_size: bufferSize,
            src_ptr: destination, src_size: 0, state: nil
        )
        guard compression_stream_init(&stream, COMPRESSION_STREAM_DECODE, COMPRESSION_ZLIB) == COMPRESSION_STATUS_OK
        else { return nil }
        defer { compression_stream_destroy(&stream) }

        return input.withUnsafeBytes { raw -> Data? in
            guard let base = raw.bindMemory(to: UInt8.self).baseAddress else { return nil }
            stream.src_ptr = base
            stream.src_size = input.count

            var output = Data()
            var status = COMPRESSION_STATUS_OK
            repeat {
                stream.dst_ptr = destination
                stream.dst_size = bufferSize
                status = compression_stream_process(&stream, Int32(COMPRESSION_STREAM_FINALIZE.rawValue))
                switch status {
                case COMPRESSION_STATUS_OK, COMPRESSION_STATUS_END:
                    output.append(destination, count: bufferSize - stream.dst_size)
                default:
                    return nil
                }
            } while status == COMPRESSION_STATUS_OK
            return output
        }
    }

    // MARK: - Payload

    private struct Response: Decodable {
        var type: String?
        var lyrics: [Entry]?
        var metadata: Metadata?
    }

    /// Only `source` is documented; the matched song's name and artist are
    /// read when present, under the spellings nodes have used.
    private struct Metadata: Decodable {
        var source: String?
        var title: String?
        var artist: String?

        private enum Keys: String, CodingKey { case source, title, name, artist, artists, artistName }

        init(from decoder: Decoder) throws {
            let container = try decoder.container(keyedBy: Keys.self)
            func text(_ key: Keys) -> String? {
                if let value = try? container.decode(String.self, forKey: key) {
                    let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
                    return trimmed.isEmpty ? nil : trimmed
                }
                if let values = try? container.decode([String].self, forKey: key) {
                    let names = values.map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }.filter { !$0.isEmpty }
                    return names.isEmpty ? nil : names.joined(separator: ", ")
                }
                return nil
            }
            source = text(.source)
            title = text(.title) ?? text(.name)
            artist = text(.artist) ?? text(.artists) ?? text(.artistName)
        }
    }

    private struct Entry: Decodable {
        var time: Double
        var duration: Double
        var text: String
        var syllabus: [Syllable]?
    }

    private struct Syllable: Decodable {
        var time: Double
        var duration: Double
        var text: String
    }
}
