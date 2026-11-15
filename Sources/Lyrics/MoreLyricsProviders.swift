import Foundation

/// More keyless lyric services, independent Swift adapters of the contracts
/// documented in Yudaotor/lyrimuse (lyrimuse-engine: soda.go, petitlyrics.go,
/// deezer.go, migu.go + migumrc.go, vibe.go, genie.go, ytmusic.go).
///
/// None asks for an account, a cookie or a per-user key. Deezer's anonymous
/// token is fetched by the app itself; PetitLyrics is asked with the client
/// IDs of its own public SDKs. Each source searches once and reads at most
/// two hits (LyricFind, three requests for one), and only timed lyrics are
/// offered: word timing (enhanced LRC, as `KugouKRC` writes it) where the
/// service has it, else line timing.
enum MoreLyricsProviders {
    typealias Hit = (id: String, title: String, artist: String, duration: Double?, score: Double)
    private static let fields: (Hit) -> (title: String, artist: String, duration: Double?, score: Double) = { ($0.title, $0.artist, $0.duration, $0.score) }

    // MARK: - HTTP

    static let browserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36"

    /// One request's body. GET parameters are added to `address`; POST
    /// bodies are passed whole. Answers other than HTTP 200 throw.
    static func data(_ address: String, _ parameters: [String: String] = [:], method: String = "GET", body: Data? = nil,
                     headers: [String: String] = [:], session: URLSession) async throws -> Data {
        try Task.checkCancellation()
        var parts = URLComponents(string: address)
        if !parameters.isEmpty {
            let items = (parts?.queryItems ?? []) + parameters.sorted { $0.key < $1.key }.map { URLQueryItem(name: $0.key, value: $0.value) }
            parts?.queryItems = items
            // URLComponents leaves "+" alone, which servers read as a space.
            let encoded = parts?.percentEncodedQuery?.replacingOccurrences(of: "+", with: "%2B")
            parts?.percentEncodedQuery = encoded
        }
        guard let url = parts?.url else { throw LyricHTTP.Failure(message: "Invalid service address") }
        var request = URLRequest(url: url, timeoutInterval: 8)
        request.httpMethod = method
        request.httpBody = body
        request.setValue(browserAgent, forHTTPHeaderField: "User-Agent")
        for (name, value) in headers { request.setValue(value, forHTTPHeaderField: name) }
        let (data, response) = try await LyricTrace.data(session, for: request)
        guard let http = response as? HTTPURLResponse, http.statusCode == 200 else {
            throw LyricHTTP.Failure(message: "Service response: HTTP \((response as? HTTPURLResponse)?.statusCode ?? 0)")
        }
        guard data.count <= 4_000_000 else { throw LyricHTTP.Failure(message: "Response too large") }
        return data
    }

    static func json(_ address: String, _ parameters: [String: String] = [:], method: String = "GET", body: Data? = nil,
                     headers: [String: String] = [:], session: URLSession) async throws -> [String: Any] {
        let raw = try await data(address, parameters, method: method, body: body, headers: headers, session: session)
        guard let value = try JSONSerialization.jsonObject(with: raw) as? [String: Any] else {
            throw LyricHTTP.Failure(message: "Unexpected response format")
        }
        return value
    }

    /// `application/x-www-form-urlencoded`.
    static func form(_ values: [String: String]) -> Data {
        let allowed = CharacterSet(charactersIn: "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._*")
        return Data(values.sorted { $0.key < $1.key }.map { key, value in
            key + "=" + (value.addingPercentEncoding(withAllowedCharacters: allowed) ?? "")
        }.joined(separator: "&").utf8)
    }

    private static func isMissing(_ error: Error, statuses: [Int]) -> Bool {
        guard let failure = error as? LyricHTTP.Failure else { return false }
        return statuses.contains { failure.message.hasSuffix("HTTP \($0)") }
    }

    // MARK: - Shared shapes

    /// One sung piece with its start and end, in seconds.
    struct TimedWord: Equatable {
        var start: Double
        var end: Double
        var text: String
    }

    /// An enhanced-LRC line (`[mm:ss.xxx]<mm:ss.xxx>word…<mm:ss.xxx>`), the
    /// shape `LyricsFetcher.parseLRC` reads as word timing. Nil without a word.
    static func enhancedLine(_ words: [TimedWord]) -> String? {
        let words = words.filter { !$0.text.isEmpty && $0.start.isFinite && $0.end.isFinite }
        guard let first = words.first,
              !words.map(\.text).joined().trimmingCharacters(in: .whitespaces).isEmpty else { return nil }
        var line = "[" + KugouKRC.clock(first.start) + "]"
        var end = first.start
        for word in words {
            line += "<" + KugouKRC.clock(word.start) + ">" + word.text.replacingOccurrences(of: "\n", with: " ")
            end = max(end, word.end, word.start)
        }
        return line + "<" + KugouKRC.clock(end) + ">"
    }

    static func lrcLine(_ seconds: Double, _ text: String) -> String {
        "[" + KugouKRC.clock(seconds) + "]" + text.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private static func number(_ value: Any?) -> Double? {
        if let number = value as? NSNumber { return number.doubleValue }
        if let text = value as? String { return Double(text.trimmingCharacters(in: .whitespaces)) }
        return nil
    }

    /// "4:13" or "1:02:03" as seconds; nil for "00:00" and anything else.
    static func clockSeconds(_ text: String) -> Double? {
        let parts = text.trimmingCharacters(in: .whitespaces).split(separator: ":").map { Int($0) }
        guard (2...3).contains(parts.count), parts.allSatisfy({ ($0 ?? -1) >= 0 }) else { return nil }
        let total = parts.reduce(0) { $0 * 60 + ($1 ?? 0) }
        return total > 0 ? Double(total) : nil
    }

    private static func rank(_ hits: [Hit], _ source: LyricSourceID, label: String, returned: Int, read: Int, query: LyricQuery) -> [Hit] {
        let sorted = AdditionalLyricsProviders.ordered(hits, source, query: query, fields)
        AdditionalLyricsProviders.trace(label, returned: returned, scored: sorted, read: read, query: query, source, fields)
        return sorted
    }

    private static func hit(_ id: String, _ title: String, _ artist: String, _ duration: Double?, query: LyricQuery, bonus: Double = 0) -> Hit? {
        guard !id.isEmpty, !title.isEmpty else { return nil }
        let value = AdditionalLyricsProviders.score(query, title: title, artist: artist, duration: duration)
        return value > 0 ? (id, title, artist, duration, value + bonus) : nil
    }

    // MARK: - Soda Music (汽水音乐)

    /// Soda's search (`api.qishui.com/luna/search/track`, only the web app ID)
    /// and its search-engine track page (`/luna/h5/seo_track`), which carries
    /// the whole lyric: KRC-shaped word timing, or for a few tracks line LRC.
    static func sodaMusic(_ query: LyricQuery, session: URLSession = .shared) async throws -> LyricCandidate? {
        let search = try await json("https://api.qishui.com/luna/search/track",
            ["q": query.artist + " " + query.title, "cursor": "0", "count": "20", "aid": "386088"], session: session)
        let tracks = sodaTracks(search)
        let hits = tracks.compactMap { hit($0.id, $0.title, $0.artist, $0.duration, query: query) }
        for hit in rank(hits, .sodaMusic, label: "Soda Music search", returned: tracks.count, read: 2, query: query).prefix(2) {
            try Task.checkCancellation()
            let page = try await sodaTrack(hit.id, session: session)
            guard let lrc = sodaLyrics(page) else {
                LyricTrace.note("Soda Music: no lyrics for “\(hit.title)”")
                continue
            }
            if let match = AdditionalLyricsProviders.candidate(.sodaMusic, lrc: lrc, query: query, title: hit.title, artist: hit.artist, duration: hit.duration) { return match }
        }
        return nil
    }

    static func sodaTracks(_ json: [String: Any]) -> [(id: String, title: String, artist: String, duration: Double?)] {
        let groups = json["result_groups"] as? [[String: Any]] ?? []
        return groups.filter { $0["id"] as? String == "tracks" }.flatMap { $0["data"] as? [[String: Any]] ?? [] }.compactMap { item in
            guard let track = (item["entity"] as? [String: Any])?["track"] as? [String: Any],
                  let id = LyricHTTP.string(track["id"]), let name = track["name"] as? String else { return nil }
            let artists = (track["artists"] as? [[String: Any]] ?? []).compactMap { $0["name"] as? String }.joined(separator: " & ")
            return (id, name, artists, number(track["duration"]).map { $0 / 1000 })
        }
    }

    /// The SEO track page, from the second host when the first fails.
    private static func sodaTrack(_ id: String, session: URLSession) async throws -> [String: Any] {
        var failure: Error = LyricHTTP.Failure(message: "No answer")
        for host in ["beta-luna.douyin.com", "api.qishui.com"] {
            do {
                return try await json("https://\(host)/luna/h5/seo_track", ["track_id": id, "device_platform": "web"], session: session)
            } catch is CancellationError {
                throw CancellationError()
            } catch {
                failure = error
            }
        }
        throw failure
    }

    /// `[lineMs,lengthMs]<offsetMs,lengthMs,0>word…`, the decrypted KRC shape,
    /// as enhanced LRC; a line-only track's LRC as it is.
    static func sodaLyrics(_ page: [String: Any]) -> String? {
        let top = (page["lyric"] as? [String: Any])?["content"] as? String ?? ""
        let nested = ((page["seo_track"] as? [String: Any])?["lyric"] as? [String: Any])?["content"] as? String ?? ""
        let content = top.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty ? nested : top
        guard !content.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return nil }
        return KugouKRC.enhancedLRC(fromText: content) ?? (content.contains("[") ? content : nil)
    }

    // MARK: - PetitLyrics (プチリリ)

    /// SyncPower's PetitLyrics: `GetPetitLyricsData.php`, a form POST that
    /// answers XML. Asked with the client IDs of PetitLyrics' own Android
    /// and iOS SDKs, the second only when the first host or ID fails. Only
    /// word-timed lyrics (lyricsType 3) are read: base64 XML with a time for
    /// every character.
    static let petitClients: [(host: String, appID: String, terminal: String)] = [
        ("p1.petitlyrics.com", "p1110417", "10"),
        ("p0.petitlyrics.com", "p1232089", "4"),
    ]

    static func petitLyrics(_ query: LyricQuery, session: URLSession = .shared) async throws -> LyricCandidate? {
        let songs = try await petitPost(["key_title": query.title, "key_artist": query.artist, "key_album": "",
                                         "key_duration": query.duration > 0 ? String(Int((query.duration * 1000).rounded())) : "",
                                         "lyricsType": "3", "maxCount": "20"], session: session)
        var bodies: [String: String] = [:]
        let hits = songs.compactMap { song -> Hit? in
            guard song["lyricsType"] == "3", let id = song["lyricsId"], let title = song["title"] else { return nil }
            if let body = song["lyricsData"], !body.isEmpty { bodies[id] = body }
            let duration = song["duration"].flatMap(Double.init).map { $0 / 1000 }
            // SyncPower's own sync for this very recording before users' uploads.
            let official = song["isOfficial"] == "1" && duration.map { abs($0 - query.duration) <= 1 } != false
            return hit(id, title, song["artist"] ?? "", duration, query: query, bonus: official ? 5 : 0)
        }
        for hit in rank(hits, .petitLyrics, label: "PetitLyrics search", returned: songs.count, read: 2, query: query).prefix(2) {
            try Task.checkCancellation()
            var body = bodies[hit.id]
            if body == nil {
                let found = try await petitPost(["key_lyricsId": hit.id, "lyricsType": "3"], session: session)
                body = found.first { $0["lyricsId"] == hit.id && $0["lyricsType"] == "3" }?["lyricsData"]
            }
            guard let body, let lrc = petitEnhancedLRC(body) else { continue }
            if let match = AdditionalLyricsProviders.candidate(.petitLyrics, lrc: lrc, query: query, title: hit.title, artist: hit.artist, duration: hit.duration) { return match }
        }
        return nil
    }

    private static func petitPost(_ values: [String: String], session: URLSession) async throws -> [[String: String]] {
        var failure: Error = LyricHTTP.Failure(message: "No answer")
        for client in petitClients {
            var form = values
            form["clientAppId"] = client.appID
            form["terminalType"] = client.terminal
            do {
                let raw = try await data("https://\(client.host)/api/GetPetitLyricsData.php", method: "POST", body: Self.form(form),
                                         headers: ["Content-Type": "application/x-www-form-urlencoded", "User-Agent": "Mozilla/5.0"], session: session)
                let (status, songs) = petitSongs(raw)
                if status == "00000000" { return songs }
                failure = LyricHTTP.Failure(message: "PetitLyrics status \(status ?? "missing")")
                // Only a client ID PetitLyrics no longer knows is worth another try.
                if status != "8170f003" { throw failure }
            } catch is CancellationError {
                throw CancellationError()
            } catch let error as LyricHTTP.Failure where error.message.hasPrefix("PetitLyrics status") {
                throw error
            } catch {
                failure = error
            }
        }
        throw failure
    }

    /// The answer's status and each `<song>`'s fields.
    static func petitSongs(_ data: Data) -> (status: String?, songs: [[String: String]]) {
        guard let root = XMLTree.parse(data) else { return (nil, []) }
        let status = root.first("status")?.text.trimmingCharacters(in: .whitespacesAndNewlines)
        let songs = root.all("song").map { song in
            Dictionary(song.children.map { ($0.name, $0.text.trimmingCharacters(in: .whitespacesAndNewlines)) }, uniquingKeysWith: { first, _ in first })
        }
        return (status, songs)
    }

    /// The word-timed lyric (base64 `<wsy><line><word><starttime/><endtime/>
    /// <wordstring/>…`) as enhanced LRC. Letters of a Latin word, timed one
    /// by one, are joined into the word; a timed space joins the word before
    /// it. A word that runs into the next is cut at the next one's start.
    static func petitEnhancedLRC(_ base64: String) -> String? {
        guard let raw = Data(base64Encoded: base64.trimmingCharacters(in: .whitespacesAndNewlines), options: .ignoreUnknownCharacters),
              let root = XMLTree.parse(raw) else { return nil }
        func latin(_ text: String) -> Bool {
            !text.isEmpty && text.unicodeScalars.allSatisfy { ($0.isASCII && CharacterSet.alphanumerics.contains($0)) || $0 == "'" || $0 == "’" }
        }
        var out: [String] = []
        var last = -1.0
        for line in root.all("line") {
            var words: [TimedWord] = []
            for piece in line.children where piece.name == "word" {
                let text = piece.first("wordstring")?.text ?? ""
                guard !text.isEmpty, let start = piece.first("starttime").flatMap({ Double($0.text.trimmingCharacters(in: .whitespaces)) }), start >= 0 else { continue }
                var end = piece.first("endtime").flatMap { Double($0.text.trimmingCharacters(in: .whitespaces)) } ?? start
                if end < start { end = start }
                let word = TimedWord(start: start / 1000, end: end / 1000, text: text)
                if var previous = words.last {
                    if word.start < previous.end { previous.end = max(word.start, previous.start) }
                    if text.trimmingCharacters(in: .whitespaces).isEmpty || (latin(previous.text) && latin(text)) {
                        previous.text += text
                        previous.end = max(previous.end, word.end)
                        words[words.count - 1] = previous
                        continue
                    }
                    words[words.count - 1] = previous
                } else if text.trimmingCharacters(in: .whitespaces).isEmpty {
                    continue
                }
                words.append(word)
            }
            guard let first = words.first, first.start >= last, let row = enhancedLine(words) else { continue }
            last = first.start
            out.append(row)
        }
        return out.isEmpty ? nil : out.joined(separator: "\n")
    }

    // MARK: - Deezer

    /// Deezer's web GraphQL (`pipe.deezer.com/api`) with an anonymous token
    /// from `auth.deezer.com/login/anonymous`: no account, no ARL cookie.
    /// The search says which tracks have synced lyrics; a track's lyrics
    /// come with line timing and, for many Latin-script songs, a separate
    /// word track, used only when it agrees with the lines.
    static func deezer(_ query: LyricQuery, session: URLSession = .shared) async throws -> LyricCandidate? {
        let text = query.artist + " " + query.title
        let answer = try await deezerGraphQL("SearchTracks", deezerSearchQuery, ["query": text, "first": 10],
                                             language: deezerLanguage(text), session: session)
        let tracks = deezerTracks(answer)
        let hits = tracks.filter { $0.synced }.compactMap { hit($0.id, $0.title, $0.artist, $0.duration, query: query) }
        for hit in rank(hits, .deezer, label: "Deezer search", returned: tracks.count, read: 2, query: query).prefix(2) {
            try Task.checkCancellation()
            let lyrics = try await deezerGraphQL("SynchronizedTrackLyrics", deezerLyricsQuery, ["trackId": hit.id], language: "en-US", session: session)
            guard let lrc = deezerLRC(lyrics) else { continue }
            if let match = AdditionalLyricsProviders.candidate(.deezer, lrc: lrc, query: query, title: hit.title, artist: hit.artist, duration: hit.duration) { return match }
        }
        return nil
    }

    static let deezerSearchQuery = """
    query SearchTracks($query: String!, $first: Int!) { search(query: $query) { results { tracks(first: $first) { edges { node { \
    id title duration hasSynchronizedLyrics contributors(first: 1) { edges { node { ... on Artist { name } } } } album { displayTitle } } } } } } }
    """
    static let deezerLyricsQuery = """
    query SynchronizedTrackLyrics($trackId: String!) { track(trackId: $trackId) { id lyrics { id \
    synchronizedLines { lrcTimestamp milliseconds line } synchronizedWordByWordLines { start end words { start end word } } } } }
    """

    /// The search localises names by Accept-Language, so it is asked in the
    /// script of the query (周杰伦 is "Jay Chou" to an English request).
    static func deezerLanguage(_ text: String) -> String {
        let scalars = text.unicodeScalars
        if scalars.contains(where: { (0x3040...0x30FF).contains($0.value) }) { return "ja-JP" }
        if scalars.contains(where: { (0xAC00...0xD7AF).contains($0.value) || (0x1100...0x11FF).contains($0.value) }) { return "ko-KR" }
        if scalars.contains(where: { (0x4E00...0x9FFF).contains($0.value) }) { return "zh-CN" }
        return "en-US"
    }

    private static func deezerGraphQL(_ operation: String, _ text: String, _ variables: [String: Any], language: String,
                                      session: URLSession) async throws -> [String: Any] {
        let body = try JSONSerialization.data(withJSONObject: ["operationName": operation, "variables": variables, "query": text])
        for attempt in 0..<2 {
            let token = try await DeezerToken.shared.value(session: session, renew: attempt > 0)
            let answer: [String: Any]
            do {
                answer = try await json("https://pipe.deezer.com/api", method: "POST", body: body,
                                        headers: ["Authorization": "Bearer " + token, "Content-Type": "application/json", "Accept-Language": language],
                                        session: session)
            } catch where attempt == 0 && isMissing(error, statuses: [401]) {
                continue
            }
            // An expired token is answered with HTTP 200 and an error.
            let errors = (answer["errors"] as? [[String: Any]] ?? []).map { "\($0)" }.joined()
            if attempt == 0, errors.contains("JwtTokenExpiredError") || errors.contains("JwtTokenMissingError") { continue }
            return answer
        }
        throw LyricHTTP.Failure(message: "Deezer refused its anonymous token")
    }

    static func deezerTracks(_ answer: [String: Any]) -> [(id: String, title: String, artist: String, duration: Double?, synced: Bool)] {
        let data = answer["data"] as? [String: Any]
        let tracks = (((data?["search"] as? [String: Any])?["results"] as? [String: Any])?["tracks"] as? [String: Any])?["edges"] as? [[String: Any]] ?? []
        return tracks.compactMap { edge in
            guard let node = edge["node"] as? [String: Any], let id = LyricHTTP.string(node["id"]), let title = node["title"] as? String else { return nil }
            let artist = (((node["contributors"] as? [String: Any])?["edges"] as? [[String: Any]])?.first?["node"] as? [String: Any])?["name"] as? String ?? ""
            return (id, title, artist, number(node["duration"]), node["hasSynchronizedLyrics"] as? Bool ?? true)
        }
    }

    /// Word timing when the word track agrees with the line track, else
    /// line LRC. Nil when the track has no synced lyrics ("LyricsNotFoundError").
    static func deezerLRC(_ answer: [String: Any]) -> String? {
        guard let lyrics = (((answer["data"] as? [String: Any])?["track"] as? [String: Any])?["lyrics"]) as? [String: Any] else { return nil }
        let lines: [(ms: Double, text: String)] = (lyrics["synchronizedLines"] as? [[String: Any]] ?? []).compactMap { row in
            guard let ms = number(row["milliseconds"]), let text = row["line"] as? String,
                  !text.trimmingCharacters(in: .whitespaces).isEmpty else { return nil }
            return (ms, text)
        }
        let wordLines: [[TimedWord]] = (lyrics["synchronizedWordByWordLines"] as? [[String: Any]] ?? []).map { row in
            let words = (row["words"] as? [[String: Any]] ?? []).compactMap { word -> TimedWord? in
                guard let start = number(word["start"]), let end = number(word["end"]), end >= start,
                      let text = word["word"] as? String, !text.trimmingCharacters(in: .whitespaces).isEmpty else { return nil }
                return TimedWord(start: start / 1000, end: end / 1000, text: text.trimmingCharacters(in: .whitespaces))
            }
            return words.enumerated().map { index, word in
                var word = word
                if index < words.count - 1 { word.text += " " }
                return word
            }
        }.filter { !$0.isEmpty }
        if deezerWordsAgree(wordLines, lines) {
            let rows = wordLines.compactMap(enhancedLine)
            if !rows.isEmpty { return rows.joined(separator: "\n") }
        }
        guard !lines.isEmpty else { return nil }
        return lines.map { lrcLine($0.ms / 1000, $0.text) }.joined(separator: "\n")
    }

    /// The word and line tracks are timed apart and the word track is now
    /// and then shifted whole: use it only when 80% of its lines find a
    /// line of the same words within 1.5 s.
    static func deezerWordsAgree(_ words: [[TimedWord]], _ lines: [(ms: Double, text: String)]) -> Bool {
        guard !words.isEmpty, !lines.isEmpty else { return false }
        func key(_ text: String) -> String { String(text.lowercased().unicodeScalars.filter { CharacterSet.alphanumerics.contains($0) }.map(Character.init)) }
        let keyed = lines.map { (ms: $0.ms, key: key($0.text)) }
        let agreeing = words.filter { line in
            let text = key(line.map(\.text).joined())
            guard !text.isEmpty, let start = line.first?.start else { return false }
            return keyed.contains { abs($0.ms - start * 1000) <= 1500 && !$0.key.isEmpty && ($0.key.contains(text) || text.contains($0.key)) }
        }.count
        return Double(agreeing) >= Double(words.count) * 0.8
    }

    // MARK: - Migu Music (咪咕音乐)

    /// Migu's unsigned web search (`search_all.do`); each hit links its line
    /// LRC file and, often, an encrypted word-timed MRC file. The signed
    /// app search Lyrimuse falls back to (a fixed device ID and signing key)
    /// is not used.
    static func migu(_ query: LyricQuery, session: URLSession = .shared) async throws -> LyricCandidate? {
        let parameters = ["text": query.artist + " " + query.title, "pageNo": "1", "pageSize": "10", "searchSwitch": #"{"song":1}"#, "isCorrect": "1"]
        var search: [String: Any]?
        var failure: Error = LyricHTTP.Failure(message: "No answer")
        for host in ["pd.musicapp.migu.cn", "app.c.nf.migu.cn"] where search == nil {
            do {
                search = try await json("https://\(host)/MIGUM2.0/v1.0/content/search_all.do", parameters,
                                        headers: ["Referer": "https://m.music.migu.cn/", "User-Agent": "Mozilla/5.0"], session: session)
            } catch is CancellationError {
                throw CancellationError()
            } catch {
                failure = error
            }
        }
        guard let search else { throw failure }
        if let code = search["code"] as? String, code != "000000" { throw LyricHTTP.Failure(message: "Migu answered \(code)") }
        let items = miguItems(search)
        let hits = items.compactMap { hit($0.id, $0.title, $0.artist, $0.duration, query: query) }
        let files = Dictionary(items.map { ($0.id, ($0.lrc, $0.mrc)) }, uniquingKeysWith: { first, _ in first })
        for hit in rank(hits, .migu, label: "Migu search", returned: items.count, read: 2, query: query).prefix(2) {
            try Task.checkCancellation()
            guard let file = files[hit.id] else { continue }
            let (lrcURL, mrcURL) = file
            if !mrcURL.isEmpty, let raw = try? await data(mrcURL, headers: ["User-Agent": "Mozilla/5.0"], session: session),
               let words = miguDecrypt(String(decoding: raw, as: UTF8.self)).flatMap(miguEnhancedLRC),
               let match = AdditionalLyricsProviders.candidate(.migu, lrc: words, query: query, title: hit.title, artist: hit.artist, duration: hit.duration) {
                return match
            }
            try Task.checkCancellation()
            let raw = try await data(lrcURL, headers: ["User-Agent": "Mozilla/5.0"], session: session)
            guard let text = LyricTextDecoder.decode(raw) else { continue }
            if let match = AdditionalLyricsProviders.candidate(.migu, lrc: miguStripHeader(text), query: query, title: hit.title, artist: hit.artist, duration: hit.duration) { return match }
        }
        return nil
    }

    static func miguItems(_ json: [String: Any]) -> [(id: String, title: String, artist: String, duration: Double?, lrc: String, mrc: String)] {
        let result = (json["songResultData"] as? [String: Any])?["result"] as? [[String: Any]] ?? []
        return result.enumerated().compactMap { index, item in
            guard let title = item["name"] as? String, let lrc = item["lyricUrl"] as? String, !lrc.isEmpty else { return nil }
            let id = LyricHTTP.string(item["copyrightId"]) ?? LyricHTTP.string(item["id"]) ?? String(index)
            let artist = (item["singers"] as? [[String: Any]] ?? []).compactMap { $0["name"] as? String }.joined(separator: " & ")
            return (id, title, artist, miguDuration(item), lrc, item["mrcurl"] as? String ?? "")
        }
    }

    /// The search gives no length; an MP3 rendition's size at its bit rate does.
    static func miguDuration(_ item: [String: Any]) -> Double? {
        let rates: [(format: String, kbps: Double)] = [("020007", 128), ("020010", 320), ("000019", 64)]
        for rate in rates {
            for key in ["newRateFormats", "rateFormats"] {
                for format in item[key] as? [[String: Any]] ?? [] where format["format"] as? String == rate.format
                    && (format["fileType"] as? String)?.lowercased() == "mp3" {
                    if let size = number(format["size"]), size > 0 { return size * 8 / (rate.kbps * 1000) }
                }
            }
        }
        return nil
    }

    /// Migu's LRC opens with "歌曲名 …" and "歌手名 …" lines on real timestamps.
    static func miguStripHeader(_ lrc: String) -> String {
        lrc.components(separatedBy: .newlines).filter { line in
            let text = line.replacingOccurrences(of: #"\[[^\]]*\]"#, with: "", options: .regularExpression).trimmingCharacters(in: .whitespaces)
            return !(text.hasPrefix("歌曲名") || text.hasPrefix("歌手名") || text == "@migu music@")
        }.joined(separator: "\n")
    }

    private static let miguKey: [Int64] = [27303562373562475, 18014862372307051, 22799692160172081, 34058940340699235]

    /// An MRC file: hex text of 64-bit words, XXTEA-encrypted with the key
    /// Migu's player uses, holding UTF-16LE text.
    static func miguDecrypt(_ hex: String) -> String? {
        let digits = Array(hex.trimmingCharacters(in: .whitespacesAndNewlines).utf8)
        let count = digits.count / 16
        guard count >= 2, count <= 200_000 else { return nil }
        var v: [Int64] = []
        v.reserveCapacity(count)
        for index in 0..<count {
            guard let word = UInt64(String(decoding: digits[index * 16 ..< index * 16 + 16], as: UTF8.self), radix: 16) else { return nil }
            v.append(Int64(bitPattern: word))
        }
        let delta: Int64 = 0x9E37_79B9
        func mix(_ sum: Int64, _ y: Int64, _ z: Int64, _ p: Int, _ e: Int64) -> Int64 {
            let left = ((z >> 5) ^ (y << 2)) &+ ((y >> 3) ^ (z << 4))
            let right = (sum ^ y) &+ (miguKey[Int((Int64(p) & 3) ^ e)] ^ z)
            return left ^ right
        }
        var sum = Int64(6 + 52 / count) &* delta
        var y = v[0]
        while sum != 0 {
            let e = (sum >> 2) & 3
            var p = count - 1
            while p > 0 {
                v[p] = v[p] &- mix(sum, y, v[p - 1], p, e)
                y = v[p]
                p -= 1
            }
            v[0] = v[0] &- mix(sum, y, v[count - 1], 0, e)
            y = v[0]
            sum = sum &- delta
        }
        var units: [UInt16] = []
        units.reserveCapacity(count * 4)
        for word in v {
            let bits = UInt64(bitPattern: word)
            for shift in stride(from: 0, to: 64, by: 16) { units.append(UInt16(truncatingIfNeeded: bits >> UInt64(shift))) }
        }
        while units.last == 0 { units.removeLast() }
        return String(decoding: units, as: UTF16.self)
    }

    /// MRC lines read `[lineMs,lengthMs]word(startMs,lengthMs)…`, word first,
    /// absolute times, as QQ's QRC does. The `[0,0]` title and credit lines
    /// are dropped.
    static func miguEnhancedLRC(_ text: String) -> String? {
        guard let head = try? NSRegularExpression(pattern: #"^\[(\d+),(\d+)\]"#),
              let piece = try? NSRegularExpression(pattern: #"(.*?)\((\d+),(\d+)\)"#) else { return nil }
        var out: [String] = []
        for raw in text.components(separatedBy: .newlines) {
            let line = raw as NSString
            guard let match = head.firstMatch(in: raw, range: NSRange(location: 0, length: line.length)),
                  line.substring(with: match.range(at: 1)) != "0" || line.substring(with: match.range(at: 2)) != "0" else { continue }
            let body = line.substring(from: match.range.upperBound) as NSString
            let words = piece.matches(in: body as String, range: NSRange(location: 0, length: body.length)).compactMap { mark -> TimedWord? in
                guard let start = Double(body.substring(with: mark.range(at: 2))), let span = Double(body.substring(with: mark.range(at: 3))) else { return nil }
                return TimedWord(start: start / 1000, end: (start + span) / 1000, text: body.substring(with: mark.range(at: 1)))
            }
            if let row = enhancedLine(words) { out.append(row) }
        }
        return out.isEmpty ? nil : out.joined(separator: "\n")
    }

    // MARK: - VIBE (Naver)

    /// VIBE's web API (`apis.naver.com/vibeWeb/musicapiweb`): a track search
    /// and each track's lyric, line timing only. Tracks marked adult need an
    /// adult-verified login for their lyric and are skipped.
    static func vibe(_ query: LyricQuery, session: URLSession = .shared) async throws -> LyricCandidate? {
        let base = "https://apis.naver.com/vibeWeb/musicapiweb"
        let search = try await json(base + "/v4/search/track", ["query": vibeQueryText(query.title + " " + query.artist), "start": "1", "display": "10"],
                                    headers: ["Accept": "application/json", "User-Agent": "Mozilla/5.0"], session: session)
        let tracks = ((search["response"] as? [String: Any])?["result"] as? [String: Any])?["tracks"] as? [[String: Any]] ?? []
        let hits = tracks.compactMap { track -> Hit? in
            guard track["isAdult"] as? Bool != true, track["hasSyncLyric"] as? Bool != false,
                  let id = LyricHTTP.string(track["trackId"]), let title = track["trackTitle"] as? String else { return nil }
            let artist = (track["artists"] as? [[String: Any]] ?? []).compactMap { $0["artistName"] as? String }.joined(separator: ", ")
            return hit(id, koreanReadingRemoved(title), artist, (track["playTime"] as? String).flatMap(clockSeconds), query: query)
        }
        for hit in rank(hits, .vibe, label: "VIBE search", returned: tracks.count, read: 2, query: query).prefix(2) {
            try Task.checkCancellation()
            let answer: [String: Any]
            do {
                answer = try await json(base + "/vibe/v4/lyric/" + hit.id, headers: ["Accept": "application/json", "User-Agent": "Mozilla/5.0"], session: session)
            } catch where isMissing(error, statuses: [403, 404]) {
                continue
            }
            guard let lrc = vibeLRC(answer) else { continue }
            if let match = AdditionalLyricsProviders.candidate(.vibe, lrc: lrc, query: query, title: hit.title, artist: hit.artist, duration: hit.duration) { return match }
        }
        return nil
    }

    /// VIBE's search answers nothing for "feat", apostrophes or "&".
    static func vibeQueryText(_ text: String) -> String {
        var text = text.replacingOccurrences(of: #"(?i)\s*[(\[（【][^()\[\]（）【】]*\b(feat|ft|featuring|with|prod)\b[^()\[\]（）【】]*[)\]）】]"#, with: "", options: .regularExpression)
        text = text.replacingOccurrences(of: #"(?i)\s+(feat|ft|featuring)\b\.?.*$"#, with: "", options: .regularExpression)
        for mark in ["'", "’", "‘", "`"] { text = text.replacingOccurrences(of: mark, with: "") }
        for mark in ["&", "＆"] { text = text.replacingOccurrences(of: mark, with: " ") }
        return text.split(whereSeparator: \.isWhitespace).joined(separator: " ")
    }

    /// VIBE and Genie follow a foreign title with its Korean reading
    /// ("晴天(청천)"): dropped when the title before it has no Hangul and the
    /// bracket names no version.
    static func koreanReadingRemoved(_ title: String) -> String {
        let trimmed = title.trimmingCharacters(in: .whitespaces)
        guard let range = trimmed.range(of: #"\s*[(（][^()（）]*[)）]$"#, options: .regularExpression) else { return title }
        let head = String(trimmed[..<range.lowerBound]), inner = String(trimmed[range]).lowercased()
        func hangul(_ text: String) -> Bool { text.unicodeScalars.contains { (0xAC00...0xD7AF).contains($0.value) } }
        let versions = ["live", "remix", "ver", "inst", "mr", "acoustic", "cover", "edit", "mix", "라이브", "리믹스", "버전", "커버", "반주", "어쿠스틱"]
        guard !head.isEmpty, hangul(inner), !hangul(head), !versions.contains(where: inner.contains) else { return title }
        return head
    }

    /// The original-language text of `syncLyric` (a song not in Korean also
    /// carries a "ko" translation) on its `startTimeIndex` seconds.
    static func vibeLRC(_ answer: [String: Any]) -> String? {
        guard let lyric = ((answer["response"] as? [String: Any])?["result"] as? [String: Any])?["lyric"] as? [String: Any],
              lyric["hasSyncLyric"] as? Bool != false,
              let sync = lyric["syncLyric"] as? [String: Any] else { return nil }
        let starts = (sync["startTimeIndex"] as? [Any] ?? []).compactMap(number)
        let contents = sync["contents"] as? [[String: Any]] ?? []
        let original = contents.first { $0["languageType"] as? String != "ko" } ?? contents.first
        let text = original?["text"] as? [String] ?? []
        var rows: [String] = []
        var last = -1.0
        for (start, line) in zip(starts, text) where start >= last {
            last = start
            rows.append(lrcLine(start, line))
        }
        return rows.contains { !$0.hasSuffix("]") } ? rows.joined(separator: "\n") : nil
    }

    // MARK: - Genie (지니뮤직)

    /// Genie's app search (`app.genie.co.kr/search/category/songs.json`) and
    /// synced-lyric file (`dn.genie.co.kr/app/purchase/get_msl.asp`, JSONP of
    /// start milliseconds to line). Line timing only.
    static func genie(_ query: LyricQuery, session: URLSession = .shared) async throws -> LyricCandidate? {
        let search = try await json("https://app.genie.co.kr/search/category/songs.json",
                                    ["query": vibeQueryText(query.title + " " + query.artist), "pg": "1", "pgsize": "10"],
                                    headers: ["User-Agent": "curl/8.7.1"], session: session)
        let items = (((search["searchResult"] as? [String: Any])?["result"] as? [String: Any])?["songs"] as? [String: Any])?["items"] as? [[String: Any]] ?? []
        func original(_ value: Any?) -> String { (value as? [String: Any])?["original"] as? String ?? (value as? String ?? "") }
        let hits = items.compactMap { item -> Hit? in
            guard (item["service_available"] as? [String: Any])?["lyrics_service_yn"] as? String != "N",
                  let id = LyricHTTP.string(item["song_id"]) else { return nil }
            let duration = number((item["duration"] as? [String: Any])?["time"]).flatMap { $0 > 0 ? $0 : nil }
            return hit(id, koreanReadingRemoved(original(item["song_name"])), original(item["artist_name"]), duration, query: query)
        }
        for hit in rank(hits, .genie, label: "Genie search", returned: items.count, read: 2, query: query).prefix(2) {
            try Task.checkCancellation()
            let raw = try await data("https://dn.genie.co.kr/app/purchase/get_msl.asp", ["songid": hit.id, "callback": "GenieCallback"],
                                     headers: ["User-Agent": "curl/8.7.1"], session: session)
            guard let lrc = genieLRC(String(decoding: raw, as: UTF8.self), title: hit.title, artist: hit.artist) else { continue }
            if let match = AdditionalLyricsProviders.candidate(.genie, lrc: lrc, query: query, title: hit.title, artist: hit.artist, duration: hit.duration) { return match }
        }
        return nil
    }

    /// `GenieCallback({"7340":"line",…});` as LRC; nil for "NOT FOUND LYRICS".
    /// Leading lines that only repeat the title and artist are dropped, when
    /// one of them is the artist (a title alone may be the first sung line).
    static func genieLRC(_ body: String, title: String, artist: String) -> String? {
        let text = body.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let open = text.firstIndex(of: "("), let close = text.lastIndex(of: ")"), open < close,
              let object = try? JSONSerialization.jsonObject(with: Data(text[text.index(after: open)..<close].utf8)) as? [String: Any] else { return nil }
        var lines = object.compactMap { key, value -> (ms: Double, text: String)? in
            guard let ms = Double(key.trimmingCharacters(in: .whitespaces)), ms >= 0 else { return nil }
            return (ms, (value as? String ?? "").trimmingCharacters(in: .whitespaces))
        }.sorted { $0.ms < $1.ms }
        let titleKey = LyricQuery.normalize(title), artistKey = LyricQuery.normalize(artist)
        var header = 0, sure = false
        while header < min(3, lines.count) {
            let key = LyricQuery.normalize(lines[header].text)
            if !artistKey.isEmpty, key == artistKey || (key.contains(titleKey) && key.contains(artistKey)) { sure = true }
            else if key != titleKey || titleKey.isEmpty { break }
            header += 1
        }
        if sure { lines.removeFirst(header) }
        guard lines.contains(where: { !$0.text.isEmpty }) else { return nil }
        return lines.map { lrcLine($0.ms / 1000, $0.text) }.joined(separator: "\n")
    }

    // MARK: - LyricFind (via YouTube Music)

    /// LyricFind's licensed lyrics as YouTube Music serves them, through its
    /// InnerTube API without sign-in: a filtered song search, the track's
    /// lyric page ID (`next`), and the page's timed lines (`browse`, which
    /// only the Android client gets). YouTube Music relays Musixmatch too;
    /// only lines credited "Source: LyricFind" are kept.
    static func lyricFind(_ query: LyricQuery, session: URLSession = .shared) async throws -> LyricCandidate? {
        let base = "https://music.youtube.com/youtubei/v1/"
        let headers = ["Content-Type": "application/json", "Origin": "https://music.youtube.com",
                       "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:88.0) Gecko/20100101 Firefox/88.0"]
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX"); formatter.timeZone = TimeZone(identifier: "UTC"); formatter.dateFormat = "yyyyMMdd"
        let web: [String: Any] = ["client": ["clientName": "WEB_REMIX", "clientVersion": "1." + formatter.string(from: Date()) + ".01.00"], "user": [String: String]()]
        func post(_ endpoint: String, _ body: [String: Any]) async throws -> [String: Any] {
            try await json(base + endpoint, ["prettyPrint": "false", "alt": "json"], method: "POST",
                           body: try JSONSerialization.data(withJSONObject: body), headers: headers, session: session)
        }
        let search = try await post("search", ["context": web, "query": query.artist + " " + query.title, "params": "EgWKAQIIAWoMEA4QChADEAQQCRAF"])
        let songs = youTubeSongs(search)
        let hits = songs.compactMap { hit($0.videoID, $0.title, $0.artist, $0.duration, query: query) }
        guard let best = rank(hits, .lyricFind, label: "YouTube Music search", returned: songs.count, read: 1, query: query).first else { return nil }
        let next = try await post("next", ["context": web, "videoId": best.id, "isAudioOnly": true])
        guard let page = youTubeLyricPage(next) else {
            LyricTrace.note("YouTube Music has no lyric page for “\(best.title)”")
            return nil
        }
        let android: [String: Any] = ["client": ["clientName": "ANDROID_MUSIC", "clientVersion": "7.21.50"], "user": [String: String]()]
        let lyrics = try await post("browse", ["context": android, "browseId": page])
        guard let lrc = lyricFindLRC(lyrics) else {
            LyricTrace.note("YouTube Music’s lyrics for “\(best.title)” are not LyricFind’s, or not timed")
            return nil
        }
        return AdditionalLyricsProviders.candidate(.lyricFind, lrc: lrc, query: query, title: best.title, artist: best.artist, duration: best.duration)
    }

    private static func walk(_ node: Any, _ visit: ([String: Any]) -> Void) {
        if let object = node as? [String: Any] {
            visit(object)
            object.values.forEach { walk($0, visit) }
        } else if let array = node as? [Any] {
            array.forEach { walk($0, visit) }
        }
    }

    static func youTubeSongs(_ search: [String: Any]) -> [(videoID: String, title: String, artist: String, duration: Double?)] {
        var songs: [(videoID: String, title: String, artist: String, duration: Double?)] = []
        walk(search) { node in
            guard let item = node["musicResponsiveListItemRenderer"] as? [String: Any],
                  let columns = item["flexColumns"] as? [[String: Any]], columns.count >= 2 else { return }
            func column(_ index: Int) -> String {
                let runs = ((columns[index]["musicResponsiveListItemFlexColumnRenderer"] as? [String: Any])?["text"] as? [String: Any])?["runs"] as? [[String: Any]]
                return (runs ?? []).compactMap { $0["text"] as? String }.joined()
            }
            let parts = column(1).components(separatedBy: " • ")
            var videoID = ""
            walk(item["overlay"] ?? [String: Any]()) { inner in
                if videoID.isEmpty, let watch = inner["watchEndpoint"] as? [String: Any], let id = watch["videoId"] as? String { videoID = id }
            }
            let title = column(0)
            guard !title.isEmpty, parts.count >= 2, !videoID.isEmpty, !songs.contains(where: { $0.videoID == videoID }) else { return }
            songs.append((videoID, title, parts[0], clockSeconds(parts[parts.count - 1])))
        }
        return songs
    }

    static func youTubeLyricPage(_ next: [String: Any]) -> String? {
        var page: String?
        walk(next) { node in
            guard page == nil, let endpoint = node["browseEndpoint"] as? [String: Any], let id = endpoint["browseId"] as? String,
                  (((endpoint["browseEndpointContextSupportedConfigs"] as? [String: Any])?["browseEndpointContextMusicConfig"] as? [String: Any])?["pageType"] as? String)
                    == "MUSIC_PAGE_TYPE_TRACK_LYRICS" else { return }
            page = id
        }
        return page
    }

    static func lyricFindLRC(_ browse: [String: Any]) -> String? {
        var rows: [String]?
        walk(browse) { node in
            guard rows == nil, let timed = node["timedLyricsData"] as? [[String: Any]], !timed.isEmpty,
                  (node["sourceMessage"] as? String)?.range(of: "LyricFind", options: .caseInsensitive) != nil else { return }
            let lines = timed.compactMap { entry -> String? in
                guard let text = entry["lyricLine"] as? String, let cue = entry["cueRange"] as? [String: Any],
                      let start = number(cue["startTimeMilliseconds"]) else { return nil }
                return lrcLine(start / 1000, text == "♪" ? "" : text)
            }
            if !lines.isEmpty { rows = lines }
        }
        return rows?.joined(separator: "\n")
    }
}

/// Deezer's anonymous web token, shared by lookups until shortly before it
/// expires (about six minutes).
actor DeezerToken {
    static let shared = DeezerToken()
    private var token: String?
    private var expires = Date.distantPast

    func value(session: URLSession, renew: Bool) async throws -> String {
        if !renew, let token, Date() < expires { return token }
        let answer = try await MoreLyricsProviders.json("https://auth.deezer.com/login/anonymous", ["jo": "p", "rto": "c", "i": "c"],
                                                        headers: ["User-Agent": "Mozilla/5.0"], session: session)
        guard let fresh = (answer["jwt"] as? String)?.trimmingCharacters(in: .whitespaces), !fresh.isEmpty else {
            throw LyricHTTP.Failure(message: "Deezer gave no anonymous token")
        }
        token = fresh
        expires = Self.expiry(fresh).map { $0.addingTimeInterval(-30) } ?? Date().addingTimeInterval(300)
        return fresh
    }

    func clear() { token = nil; expires = .distantPast }

    /// The `exp` claim, read without checking the signature (only to know
    /// when to ask again).
    static func expiry(_ jwt: String) -> Date? {
        let parts = jwt.split(separator: ".")
        guard parts.count >= 2 else { return nil }
        var payload = parts[1].replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        while payload.count % 4 != 0 { payload += "=" }
        guard let data = Data(base64Encoded: payload),
              let claims = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let exp = (claims["exp"] as? NSNumber)?.doubleValue, exp > 0 else { return nil }
        return Date(timeIntervalSince1970: exp)
    }
}

/// A small element tree from XMLParser, enough for PetitLyrics' answers.
final class XMLTree {
    let name: String
    private(set) var children: [XMLTree] = []
    private(set) var text = ""
    private weak var parent: XMLTree?

    init(name: String, parent: XMLTree? = nil) {
        self.name = name
        self.parent = parent
    }

    func first(_ name: String) -> XMLTree? {
        for child in children {
            if child.name == name { return child }
            if let found = child.first(name) { return found }
        }
        return nil
    }

    func all(_ name: String) -> [XMLTree] {
        children.flatMap { ($0.name == name ? [$0] : []) + $0.all(name) }
    }

    static func parse(_ data: Data) -> XMLTree? {
        let root = XMLTree(name: "")
        let builder = Builder(root: root)
        let parser = XMLParser(data: data)
        parser.delegate = builder
        return parser.parse() ? root : nil
    }

    private final class Builder: NSObject, XMLParserDelegate {
        var current: XMLTree
        init(root: XMLTree) { current = root }
        func parser(_ parser: XMLParser, didStartElement elementName: String, namespaceURI: String?, qualifiedName: String?, attributes: [String: String] = [:]) {
            let child = XMLTree(name: elementName, parent: current)
            current.children.append(child)
            current = child
        }
        func parser(_ parser: XMLParser, didEndElement elementName: String, namespaceURI: String?, qualifiedName: String?) {
            if let parent = current.parent { current = parent }
        }
        func parser(_ parser: XMLParser, foundCharacters string: String) { current.text += string }
        func parser(_ parser: XMLParser, foundCDATA CDATABlock: Data) { current.text += String(decoding: CDATABlock, as: UTF8.self) }
    }
}
