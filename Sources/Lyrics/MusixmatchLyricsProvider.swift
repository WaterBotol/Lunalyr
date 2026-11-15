import Foundation

/// Musixmatch's lyric API, asked the way its own Mac/iOS client asks it: an
/// anonymous user token from `token.get`, then one `macro.subtitles.get`
/// per song, which matches the recording and returns its line-synced
/// subtitle and, when there is one, its word-by-word "richsync" timing.
/// No account, cookie or key is used.
///
/// Contract reference: Yudaotor/lyrimuse, lyrimuse-engine/musixmatch.go and
/// musixmatchmacro.go. As there, the apic-appmobile host with the
/// `mac-ios-v2.0` app id is used: the desktop web app's
/// (`web-desktop-app-v1.0` on apic-desktop) answers `token.get` with a
/// captcha or a placeholder token.
///
/// Politeness, stricter than Lyrimuse: one lookup per song (no search
/// fallback, no other names, no retries but one after a renewed token),
/// and when Musixmatch says this Mac is blocked (401 captcha, 429…), no
/// requests at all for a backoff period that doubles each time.
enum MusixmatchLyricsProvider {
    static let base = "https://apic-appmobile.musixmatch.com/ws/1.1/"
    static let appID = "mac-ios-v2.0"
    /// The User-Agent Lyrimuse sends.
    static let userAgent = "Mozilla/5.0"

    /// What an answer's header says.
    enum Verdict: Equatable {
        case ok
        /// Nothing for this song (404 and the like).
        case missing
        /// This Mac is being turned away (captcha, rate limit): back off.
        case blocked
        /// The user token is no longer accepted: get a new one.
        case badToken
    }

    static func verdict(httpStatus: Int, json: [String: Any]?) -> Verdict {
        if [401, 403, 429].contains(httpStatus) { return .blocked }
        guard httpStatus == 200, let json else { return .missing }
        let (status, hint) = header(json)
        return verdict(status: status, hint: hint)
    }

    static func verdict(status: Int, hint: String) -> Verdict {
        switch status {
        case 200: return .ok
        case 401 where hint == "captcha", 402, 403, 429: return .blocked
        case 401: return .badToken // "renew", "expired" and the like
        default: return .missing
        }
    }

    static func header(_ json: [String: Any]) -> (status: Int, hint: String) {
        let header = (json["message"] as? [String: Any])?["header"] as? [String: Any]
        let status = (header?["status_code"] as? NSNumber)?.intValue ?? 0
        return (status, (header?["hint"] as? String ?? "").lowercased())
    }

    /// `token.get`'s user token, or nil when it gave none or a placeholder.
    static func token(from json: [String: Any]) -> String? {
        guard header(json).status == 200,
              let body = (json["message"] as? [String: Any])?["body"] as? [String: Any],
              let token = body["user_token"] as? String, !token.isEmpty, !isPlaceholder(token) else { return nil }
        return token
    }

    /// A blocked client is handed `UpgradeOnlyUpgradeOnly…` or a string of
    /// one repeated character instead of a token.
    static func isPlaceholder(_ token: String) -> Bool {
        token.lowercased().contains("upgradeonly") || Set(token).count <= 1
    }

    /// What one `macro.subtitles.get` matched and returned.
    struct Match: Equatable {
        var title: String
        var artist: String
        var duration: Double?
        /// Line-synced LRC, nil when there is none or it is restricted.
        var subtitle: String?
        /// Word-timed enhanced LRC from richsync, nil when there is none
        /// or it is timed only by whole lines.
        var words: String?
    }

    /// Reads a `macro.subtitles.get` answer. Nil when the matcher found no track.
    static func match(from json: [String: Any]) -> Match? {
        let calls = ((json["message"] as? [String: Any])?["body"] as? [String: Any])?["macro_calls"] as? [String: Any] ?? [:]
        func call(_ name: String) -> (status: Int, hint: String, body: [String: Any]) {
            guard let wrapped = calls[name] as? [String: Any] else { return (0, "", [:]) }
            let (status, hint) = header(wrapped)
            let body = (wrapped["message"] as? [String: Any])?["body"] as? [String: Any] ?? [:]
            return (status, hint, body)
        }
        let matcher = call("matcher.track.get")
        guard matcher.status == 200, let track = matcher.body["track"] as? [String: Any],
              (track["track_id"] as? NSNumber)?.intValue ?? 0 != 0 else { return nil }
        let length = (track["track_length"] as? NSNumber)?.doubleValue ?? 0
        var match = Match(title: track["track_name"] as? String ?? "", artist: track["artist_name"] as? String ?? "",
                          duration: length > 0 ? length : nil)
        let subtitles = call("track.subtitles.get")
        if subtitles.status == 200,
           let first = (subtitles.body["subtitle_list"] as? [[String: Any]])?.first?["subtitle"] as? [String: Any],
           (first["restricted"] as? NSNumber)?.intValue ?? 0 == 0,
           let body = first["subtitle_body"] as? String, body.contains("[") {
            match.subtitle = body
            // The matcher's track_length is often 0; the subtitle's length is the recording's.
            if let length = (first["subtitle_length"] as? NSNumber)?.doubleValue, length > 0 { match.duration = length }
        }
        let richsync = call("track.richsync.get")
        if richsync.status == 200, let body = (richsync.body["richsync"] as? [String: Any])?["richsync_body"] as? String {
            match.words = enhancedLRC(richsync: body)
        }
        return match
    }

    /// One richsync line: `ts`/`te` are the line's start and end in seconds
    /// from the song's start; each piece's `o` is seconds from the line's start.
    struct RichsyncLine: Decodable {
        struct Piece: Decodable { var c: String; var o: Double }
        var ts: Double
        var te: Double?
        var l: [Piece]
    }

    /// Richsync as enhanced LRC (`[mm:ss.xxx]<mm:ss.xxx>word …<mm:ss.xxx>`),
    /// which `LyricsFetcher.parseLRC` reads as word-timed lines (as Kugou's
    /// KRC is converted). Nil when empty, unreadable, or timed only by
    /// whole lines (`isLineLevel`).
    static func enhancedLRC(richsync body: String) -> String? {
        guard let lines = try? JSONDecoder().decode([RichsyncLine].self, from: Data(body.utf8)), !lines.isEmpty,
              !isLineLevel(lines) else { return nil }
        var out: [String] = []
        for line in lines {
            // A space is its own timed piece in richsync; joined to the word
            // before it, so that word keeps its sung length.
            var words: [(start: Double, text: String)] = []
            var prefix = ""
            for piece in line.l {
                if piece.c.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                    if words.isEmpty { prefix += piece.c } else { words[words.count - 1].text += piece.c }
                    continue
                }
                words.append((line.ts + piece.o, prefix + piece.c))
                prefix = ""
            }
            guard !words.isEmpty else { continue }
            let end = max(line.te ?? 0, words.last!.start, line.ts)
            var text = "[" + KugouKRC.clock(line.ts) + "]"
            for word in words { text += "<" + KugouKRC.clock(word.start) + ">" + word.text }
            out.append(text + "<" + KugouKRC.clock(end) + ">")
        }
        return out.isEmpty ? nil : out.joined(separator: "\n")
    }

    /// Richsync that times whole lines as one "word": more than 80% of at
    /// least five lines are a single piece of four or more characters.
    /// (Lyrimuse's `musixmatchRichsyncIsLineLevel`.)
    static func isLineLevel(_ lines: [RichsyncLine]) -> Bool {
        var withText = 0, wholeLine = 0
        for line in lines {
            let pieces = line.l.map { $0.c.trimmingCharacters(in: .whitespacesAndNewlines) }.filter { !$0.isEmpty }
            guard !pieces.isEmpty else { continue }
            withText += 1
            if pieces.count == 1, pieces[0].count >= 4 { wholeLine += 1 }
        }
        return withText >= 5 && Double(wholeLine) > 0.8 * Double(withText)
    }

    /// The lyrics Musixmatch has for `query`: word timing from richsync when
    /// there is some, else its line-synced subtitle. Nil when there is no
    /// match, or while Musixmatch is backing off. Throws when it could not
    /// be reached, so an outage is not remembered as "no lyrics".
    static func lookup(_ query: LyricQuery, session: URLSession = .shared,
                       state: MusixmatchState = .shared) async throws -> LyricCandidate? {
        guard !query.title.trimmingCharacters(in: .whitespaces).isEmpty else { return nil }
        let key = query.recordingKey
        if let answered = state.answered(key) {
            LyricTrace.note("Musixmatch: already asked about this song; reusing its answer")
            return answered.candidate
        }
        if let left = state.blockedFor() {
            LyricTrace.note("Musixmatch: paused after it turned this Mac away; \(Int(left / 60) + 1) min left")
            return nil
        }
        var parameters = [
            "q_track": query.title, "q_artist": query.artist,
            "namespace": "lyrics_richsynched", "subtitle_format": "lrc", "optional_calls": "track.richsync",
            "part": "track_lyrics_translation_status,track_performer_tagging", "format": "json",
        ]
        if query.duration > 0 {
            let seconds = String(Int(query.duration.rounded()))
            parameters["q_duration"] = seconds
            parameters["f_subtitle_length"] = seconds
        }
        for attempt in 0..<2 {
            guard let token = try await userToken(session: session, state: state) else { return nil }
            var asked = parameters
            asked["usertoken"] = token
            let (status, json) = try await get("macro.subtitles.get", asked, session: session)
            var verdict = verdict(httpStatus: status, json: json)
            // The macro's own calls can be turned away while the envelope says 200.
            if verdict == .ok, let json {
                let calls = ((json["message"] as? [String: Any])?["body"] as? [String: Any])?["macro_calls"] as? [String: Any]
                if let matcher = calls?["matcher.track.get"] as? [String: Any] {
                    let (status, hint) = header(matcher)
                    if status != 200 { verdict = Self.verdict(status: status, hint: hint) }
                }
            }
            switch verdict {
            case .blocked:
                let pause = state.noteBlocked()
                LyricTrace.note("Musixmatch turned this Mac away; not asking it again for \(Int(pause / 60)) min")
                return nil
            case .badToken:
                state.dropToken(token)
                LyricTrace.note("Musixmatch no longer accepts its user token; getting a new one")
                if attempt == 0 { continue }
                return nil
            case .missing:
                state.clearBlocked()
                state.remember(key, nil)
                LyricTrace.note("Musixmatch: no match for this song")
                return nil
            case .ok:
                state.clearBlocked()
                let found = json.flatMap(match(from:)).flatMap { candidate(from: $0, query: query) }
                state.remember(key, found)
                return found
            }
        }
        return nil
    }

    /// The candidate for a match: word timing when richsync has it, else the subtitle.
    static func candidate(from match: Match, query: LyricQuery) -> LyricCandidate? {
        let title = match.title.isEmpty ? query.title : match.title
        let artist = match.artist.isEmpty ? query.artist : match.artist
        for lrc in [match.words, match.subtitle].compactMap({ $0 }) {
            if let found = AdditionalLyricsProviders.candidate(.musixmatch, lrc: lrc, query: query,
                                                               title: title, artist: artist, duration: match.duration) {
                return found
            }
        }
        if match.words == nil, match.subtitle == nil { LyricTrace.note("Musixmatch matched “\(title)” but has no timed lyrics for it") }
        return nil
    }

    /// A user token: the saved one while fresh, else a new one from `token.get`.
    private static func userToken(session: URLSession, state: MusixmatchState) async throws -> String? {
        if let token = state.freshToken() { return token }
        let (status, json) = try await get("token.get", ["user_language": "en"], session: session)
        switch verdict(httpStatus: status, json: json) {
        case .blocked:
            let pause = state.noteBlocked()
            LyricTrace.note("Musixmatch refused a user token; not asking it again for \(Int(pause / 60)) min")
            return nil
        default:
            // No usable token (refused, or a placeholder for a blocked
            // client): asking again each song would not help, so pause too.
            guard let token = json.flatMap(token(from:)) else {
                let pause = state.noteBlocked()
                LyricTrace.note("Musixmatch gave no usable user token; not asking it again for \(Int(pause / 60)) min")
                return nil
            }
            state.store(token: token)
            return token
        }
    }

    /// One GET with the client's identity. Returns the HTTP status and the
    /// JSON envelope (nil when the body is not one). The URL, which holds
    /// the token, never goes into an error or a trace unredacted.
    private static func get(_ action: String, _ parameters: [String: String],
                            session: URLSession) async throws -> (Int, [String: Any]?) {
        try Task.checkCancellation()
        var asked = parameters
        asked["app_id"] = appID
        asked["t"] = String(Int(Date().timeIntervalSince1970))
        var parts = URLComponents(string: base + action)
        parts?.queryItems = asked.sorted { $0.key < $1.key }.map { URLQueryItem(name: $0.key, value: $0.value) }
        guard let url = parts?.url else { throw LyricHTTP.Failure(message: "Invalid service address") }
        var request = URLRequest(url: url, timeoutInterval: 6)
        request.setValue(userAgent, forHTTPHeaderField: "User-Agent")
        request.httpShouldHandleCookies = false
        let (data, response) = try await LyricTrace.data(session, for: request)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard data.count <= 4_000_000 else { throw LyricHTTP.Failure(message: "Response too large") }
        let json = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
        if status != 200, verdict(httpStatus: status, json: json) != .blocked {
            throw LyricHTTP.Failure(message: "Service response: HTTP \(status)")
        }
        if status == 200, json.map({ header($0).status }) ?? 0 == 0 {
            throw LyricHTTP.Failure(message: "Unexpected response format")
        }
        return (status, json)
    }
}

/// Musixmatch's user token and backoff, kept in a small file in Caches so a
/// relaunch neither asks for a new token nor forgets a pause; and, in
/// memory, the songs already asked about.
final class MusixmatchState: @unchecked Sendable {
    static let shared = MusixmatchState(file: AppRuntime.isUnitTesting ? nil : MusixmatchState.standardFile())

    /// How long a token is used before a new one is asked for. Musixmatch
    /// says when one is no longer accepted, and it is then replaced.
    static let tokenFreshFor: TimeInterval = 24 * 3600
    /// The first pause after being turned away; each further one doubles, up to `longestPause`.
    static let firstPause: TimeInterval = 10 * 60
    static let longestPause: TimeInterval = 2 * 3600
    /// A pause this long after the last one starts the doubling over.
    static let forgetBlocksAfter: TimeInterval = 24 * 3600
    /// How long a song's answer is reused instead of asking again.
    static let answerLifetime: TimeInterval = 6 * 3600

    struct Stored: Codable, Equatable {
        var token: String?
        var tokenFetchedAt: Date?
        var blockedUntil: Date?
        var lastBlockedAt: Date?
        var blocks: Int = 0
    }

    private let file: URL?
    private let clock: () -> Date
    private let lock = NSLock()
    private var stored: Stored
    private var answers: [String: (at: Date, candidate: LyricCandidate?)] = [:]

    init(file: URL?, clock: @escaping () -> Date = Date.init) {
        self.file = file
        self.clock = clock
        stored = file.flatMap { try? Data(contentsOf: $0) }.flatMap { try? JSONDecoder().decode(Stored.self, from: $0) } ?? Stored()
    }

    static func standardFile() -> URL {
        let caches = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask).first ?? FileManager.default.temporaryDirectory
        return caches.appendingPathComponent(Bundle.main.bundleIdentifier ?? "local.verso.custom", isDirectory: true)
            .appendingPathComponent("musixmatch-session.json")
    }

    var snapshot: Stored { lock.withLock { stored } }

    func freshToken() -> String? {
        lock.withLock {
            guard let token = stored.token, let at = stored.tokenFetchedAt,
                  clock().timeIntervalSince(at) < Self.tokenFreshFor, at <= clock().addingTimeInterval(60) else { return nil }
            return token
        }
    }

    func store(token: String) {
        update { $0.token = token; $0.tokenFetchedAt = self.clock() }
    }

    func dropToken(_ token: String) {
        update { if $0.token == token { $0.token = nil; $0.tokenFetchedAt = nil } }
    }

    /// Time left in the current pause, nil when not paused.
    func blockedFor() -> TimeInterval? {
        lock.withLock {
            guard let until = stored.blockedUntil else { return nil }
            let left = until.timeIntervalSince(clock())
            return left > 0 ? left : nil
        }
    }

    /// Starts a pause: 10 minutes, doubling with each block, at most 2 hours. Returns its length.
    @discardableResult
    func noteBlocked() -> TimeInterval {
        var pause: TimeInterval = 0
        update {
            let now = self.clock()
            if let last = $0.lastBlockedAt, now.timeIntervalSince(last) > Self.forgetBlocksAfter { $0.blocks = 0 }
            pause = min(Self.longestPause, Self.firstPause * pow(2, Double(min($0.blocks, 10))))
            $0.blocks += 1
            $0.lastBlockedAt = now
            $0.blockedUntil = now.addingTimeInterval(pause)
        }
        return pause
    }

    /// Musixmatch answered properly: the doubling starts over.
    func clearBlocked() {
        guard lock.withLock({ stored.blocks != 0 || stored.blockedUntil != nil }) else { return }
        update { $0.blocks = 0; $0.blockedUntil = nil; $0.lastBlockedAt = nil }
    }

    struct Answer { let candidate: LyricCandidate? }

    /// The answer already given for a song, while it is recent.
    func answered(_ key: String) -> Answer? {
        lock.withLock {
            guard let answer = answers[key], clock().timeIntervalSince(answer.at) < Self.answerLifetime else { return nil }
            return Answer(candidate: answer.candidate)
        }
    }

    func remember(_ key: String, _ candidate: LyricCandidate?) {
        lock.withLock {
            let now = clock()
            answers = answers.filter { now.timeIntervalSince($0.value.at) < Self.answerLifetime }
            if answers.count >= 200, let oldest = answers.min(by: { $0.value.at < $1.value.at })?.key { answers[oldest] = nil }
            answers[key] = (now, candidate)
        }
    }

    private func update(_ change: (inout Stored) -> Void) {
        let copy: Stored = lock.withLock { change(&stored); return stored }
        guard let file else { return }
        try? FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
        guard let data = try? JSONEncoder().encode(copy), (try? data.write(to: file, options: .atomic)) != nil else { return }
        try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: file.path)
    }
}
