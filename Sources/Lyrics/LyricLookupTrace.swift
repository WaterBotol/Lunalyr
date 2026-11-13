import Foundation

/// Diagnostics only: receives what one lyric lookup did, step by step, so it
/// can be compared song by song with another implementation (the Windows
/// port). Nothing in the app sets one; `LyricsFetcher` takes one only when
/// a test injects it, and while none is set every hook below returns at
/// once without building anything (`LyricTrace.isOn`).
protocol LyricLookupTracer: AnyObject, Sendable {
    func record(_ event: LyricTraceEvent)
}

/// The hooks the lookup calls. The tracer, and the source and query variant
/// being searched, ride along as task-local values, so the provider code
/// deep inside a lookup needs no extra parameters and two lookups running
/// at once never mix their events.
enum LyricTrace {
    @TaskLocal static var tracer: (any LyricLookupTracer)?
    /// The source whose fetch is running (`LyricsFetcher.bounded`).
    @TaskLocal static var source: LyricSourceID?
    /// The query variant being searched: nil for the query as received,
    /// else e.g. "original title “夜に駆ける”" or "artist alias “米津玄師”".
    @TaskLocal static var variant: String?

    static var isOn: Bool { tracer != nil }

    /// Records the event, filling in the running source and variant. The
    /// event is built only when a tracer is set.
    static func emit(_ make: @autoclosure () -> LyricTraceEvent) {
        guard let tracer else { return }
        var event = make()
        if event.source == nil { event.source = source?.rawValue }
        if event.variant == nil { event.variant = variant }
        tracer.record(event)
    }

    static func note(_ message: @autoclosure () -> String) {
        guard isOn else { return }
        emit(LyricTraceEvent(kind: .note, message: message()))
    }

    /// Runs `body` with `id` as the running source, when tracing.
    static func within<T>(source id: LyricSourceID, _ body: () async throws -> T) async rethrows -> T {
        guard isOn else { return try await body() }
        return try await $source.withValue(id) { try await body() }
    }

    /// Runs `body` under a query variant, nested in the current one, when tracing.
    static func within<T>(variant label: @autoclosure () -> String, _ body: () async throws -> T) async rethrows -> T {
        guard isOn else { return try await body() }
        let path = variant.map { $0 + " + " + label() } ?? label()
        return try await $variant.withValue(path) { try await body() }
    }

    /// `session.data(for:)`, recording the request (keys and tokens removed),
    /// its status, size and time when tracing. Unchanged otherwise.
    static func data(_ session: URLSession, for request: URLRequest) async throws -> (Data, URLResponse) {
        guard isOn else { return try await session.data(for: request) }
        let began = Date()
        do {
            let (data, response) = try await session.data(for: request)
            emit(LyricTraceEvent(kind: .request, request: .init(
                url: redacted(request.url), status: (response as? HTTPURLResponse)?.statusCode, bytes: data.count,
                ms: milliseconds(since: began), timeout: request.timeoutInterval, error: nil)))
            return (data, response)
        } catch {
            emit(LyricTraceEvent(kind: .request, request: .init(
                url: redacted(request.url), status: nil, bytes: nil, ms: milliseconds(since: began),
                timeout: request.timeoutInterval, error: describe(error))))
            throw error
        }
    }

    /// A search's size: how many results came back and how many were read.
    static func results(_ label: @autoclosure () -> String, returned: @autoclosure () -> Int, read: @autoclosure () -> Int) {
        guard isOn else { return }
        emit(LyricTraceEvent(kind: .results, results: .init(label: label(), returned: returned(), read: read())))
    }

    /// One search result a source weighed before fetching lyrics.
    static func hit(_ make: @autoclosure () -> LyricTraceHit) {
        guard isOn else { return }
        emit(LyricTraceEvent(kind: .hit, hit: make()))
    }

    /// A candidate a source produced, weighed against `query` (the one
    /// searched for, which may be a variant).
    static func candidate(_ candidate: @autoclosure () -> LyricCandidate?, query: LyricQuery, kind: LyricTraceEvent.Kind = .candidate,
                          message: @autoclosure () -> String? = nil) {
        guard isOn else { return }
        emit(LyricTraceEvent(kind: kind, message: message(), candidate: candidate().map { LyricTraceCandidate($0, query: query) }))
    }

    static func milliseconds(since date: Date) -> Int { Int((Date().timeIntervalSince(date) * 1000).rounded()) }

    /// Query parameters that name a key, token, secret or signature are
    /// replaced, so a trace can be shared.
    static func redacted(_ url: URL?) -> String {
        guard let url, var parts = URLComponents(url: url, resolvingAgainstBaseURL: false) else { return url?.absoluteString ?? "" }
        let secret = ["key", "token", "secret", "sig", "auth", "password", "session", "cookie"]
        parts.queryItems = parts.queryItems?.map { item in
            let name = item.name.lowercased()
            return secret.contains(where: name.contains) ? URLQueryItem(name: item.name, value: "REDACTED") : item
        }
        parts.user = nil
        parts.password = nil
        return parts.url?.absoluteString ?? url.absoluteString
    }

    static func describe(_ error: Error) -> String {
        if error is CancellationError { return "cancelled" }
        if let code = (error as? URLError)?.code {
            switch code {
            case .timedOut: return "timeout"
            case .cancelled: return "cancelled"
            default: return "URLError \(code.rawValue): \(error.localizedDescription)"
            }
        }
        return error.localizedDescription
    }
}

struct LyricTraceEvent: Codable, Sendable {
    enum Kind: String, Codable, Sendable {
        /// The query as received, and the sources and mode used.
        case lookup
        /// A pinned choice, the saved automatic choice or the miss cache decided something.
        case cache
        case sourceStart, sourceEnd
        /// A query variant is about to be searched.
        case variant
        /// What a variant gave, and whether it was kept.
        case variantResult
        case request
        case results
        case hit
        /// A candidate a source produced (its best offer).
        case candidate
        case publication
        /// Sources stopped because their answer could not change anything.
        case stop
        /// The final ranked list and pick.
        case final
        case note
    }
    var kind: Kind
    /// Seconds since the tracer began (set by the tracer).
    var at: Double = 0
    var source: String?
    var variant: String?
    var message: String?
    var query: LyricTraceQuery?
    var request: LyricTraceRequest?
    var results: LyricTraceResults?
    var hit: LyricTraceHit?
    var candidate: LyricTraceCandidate?
    var publication: LyricTracePublication?
    var ranked: [LyricTraceCandidate]?
    var rejected: [LyricTraceCandidate]?
    var states: [String: String]?
    var sources: [String]?
    var elapsedMs: Int?

    init(kind: Kind, source: String? = nil, variant: String? = nil, message: String? = nil, query: LyricTraceQuery? = nil,
         request: LyricTraceRequest? = nil, results: LyricTraceResults? = nil, hit: LyricTraceHit? = nil,
         candidate: LyricTraceCandidate? = nil, publication: LyricTracePublication? = nil, ranked: [LyricTraceCandidate]? = nil,
         rejected: [LyricTraceCandidate]? = nil, states: [String: String]? = nil, sources: [String]? = nil, elapsedMs: Int? = nil) {
        self.kind = kind; self.source = source; self.variant = variant; self.message = message; self.query = query
        self.request = request; self.results = results; self.hit = hit; self.candidate = candidate
        self.publication = publication; self.ranked = ranked; self.rejected = rejected; self.states = states
        self.sources = sources; self.elapsedMs = elapsedMs
    }
}

struct LyricTraceQuery: Codable, Sendable, Equatable {
    var title: String
    var artist: String
    var album: String?
    var duration: Double
    var genre: String?
    init(_ query: LyricQuery) {
        title = query.title; artist = query.artist; album = query.album; duration = query.duration; genre = query.genre
    }
}

struct LyricTraceRequest: Codable, Sendable {
    var url: String
    var status: Int?
    var bytes: Int?
    var ms: Int
    var timeout: Double?
    var error: String?
}

struct LyricTraceResults: Codable, Sendable {
    var label: String
    var returned: Int
    var read: Int
}

/// One search result a source weighed before fetching its lyrics.
struct LyricTraceHit: Codable, Sendable {
    var title: String
    var artist: String
    var album: String? = nil
    var duration: Double? = nil
    var score: Double? = nil
    var titleSimilarity: Double? = nil
    var artistSimilarity: Double? = nil
    /// "best so far", "read", or why it was passed over.
    var verdict: String
}

struct LyricTracePublication: Codable, Sendable {
    /// wait, holdForUpgrade or publish.
    var step: String
    var best: String?
    var bestTiming: String?
    var settled: Bool
    var graceOver: Bool
    var settleOver: Bool
    var shownBefore: String?
    var shownAfter: String?
    var revision: Int
    var pending: [String]
}

/// A candidate as the ranking sees it.
struct LyricTraceCandidate: Codable, Sendable {
    struct Agreement: Codable, Sendable { var peers: Int; var timedCompared: Int; var timingOutlier: Bool }
    var source: String
    var title: String
    var artist: String
    var duration: Double?
    var score: Double
    var metadataReported: Bool
    var viaOriginalTitle: Bool
    /// "Word-by-word · LRCLIB", "Line-synced · NetEase"…
    var provenance: String
    var lines: Int
    /// word, mixed, line, phrase-timed, plain, or faulty (`timingFault`).
    var timing: String
    var timingFault: String?
    /// 2 word or mixed, 1 line, 0 plain (`LyricsFetcher.rankedTiming`).
    var rankedTiming: Int
    var titleSimilarity: Double
    var artistSimilarity: Double
    /// Candidate length minus the query's, nil when the source gave none.
    var durationDelta: Double?
    /// Name closeness plus length fit (`LyricsFetcher.confidence`).
    var confidence: Double
    var versionTags: [String]
    var versionMismatch: [String]
    /// latin, native or mixed; nil with fewer than four sung lines.
    var script: String?
    var languageMismatch: Bool
    /// 0 Music's own, 1 Apple via Lyrics+, 2 LRCLIB word timing, 3 others, 4 LRCLIB outlier.
    var lead: Int
    var appleRelay: Bool
    var agreement: Agreement
    /// accepted or rejected (the recording check, `LyricsFetcher.rejection`).
    var verdict: String
    var reason: String?
    /// Place in the final ranking, from 1.
    var rank: Int?
    var firstLine: String?

    init(_ candidate: LyricCandidate, query: LyricQuery, rank: Int? = nil, reason given: String? = nil) {
        source = candidate.source.rawValue
        title = candidate.title
        artist = candidate.artist
        duration = candidate.duration
        score = candidate.score
        metadataReported = candidate.metadataReported
        viaOriginalTitle = candidate.viaOriginalTitle
        provenance = candidate.provenance.label
        lines = candidate.lineCount
        timingFault = LyricsFetcher.timingFault(candidate.result)
        timing = Self.timing(candidate.result)
        rankedTiming = LyricsFetcher.rankedTiming(candidate.result)
        titleSimilarity = LyricQuery.similarity(query.title, candidate.title)
        artistSimilarity = query.artistScore(candidate.artist)
        durationDelta = candidate.duration.map { $0 - query.duration }
        confidence = LyricsFetcher.traceConfidence(candidate, query)
        versionTags = VersionTags.tags(candidate.title).sorted()
        versionMismatch = VersionTags.mismatch(query, title: candidate.title).sorted()
        switch LyricLanguage.script(of: candidate) {
        case .latin?: script = "latin"
        case .native?: script = "native"
        case .mixed?: script = "mixed"
        case nil: script = nil
        }
        languageMismatch = candidate.languageMismatch
        lead = LyricsFetcher.traceLead(candidate)
        appleRelay = LyricsFetcher.isAppleRelay(candidate)
        agreement = .init(peers: candidate.agreement.peers, timedCompared: candidate.agreement.timedCompared,
                          timingOutlier: candidate.agreement.timingOutlier)
        let reason = given ?? LyricsFetcher.rejection(candidate, query: query)
        verdict = reason == nil ? "accepted" : "rejected"
        self.reason = reason
        self.rank = rank
        switch candidate.result {
        case .synced(let lines, _): firstLine = lines.first { !$0.isInstrumental && $0.credit != true }?.text
        case .plain(let lines, _): firstLine = lines.first { !$0.trimmingCharacters(in: .whitespaces).isEmpty }
        }
    }

    static func timing(_ result: LyricsFetcher.Result) -> String {
        if LyricsFetcher.hasTimingFault(result) { return "faulty" }
        guard case .synced(let lines, _) = result else { return "plain" }
        switch LyricsFetcher.timingTier(result) {
        case 3: return "word"
        case 2: return "mixed"
        case 1: return lines.contains { $0.wordTimed && LyricsFetcher.isPhraseTimed($0) } ? "phrase-timed" : "line"
        default: return "plain"
        }
    }
}
