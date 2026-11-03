import Foundation
import Yams

/// Custom fork: bounded parallel provider lookup, recording-specific caching,
/// manual selections and explicit timing provenance. Upstream licence retained.
final class LyricsFetcher {
    static let shared = LyricsFetcher()
    struct Word: Codable, Equatable { var text: String; var start: TimeInterval; var end: TimeInterval }
    struct Line: Codable {
        var start: TimeInterval
        var end: TimeInterval
        var text: String
        var words: [Word]
        var isInstrumental: Bool
        var wordTimed: Bool = false
        var romanization: String? = nil
    }
    struct Provenance: Equatable, Codable {
        var precision: String
        var source: String
        var label: String { "\(precision) · \(source)" }
    }
    enum Result: Codable {
        case synced([Line], Provenance)
        case plain([String], Provenance)
    }
    private struct Outcome {
        var id: LyricSourceID
        var candidate: LyricCandidate?
        var state: String
    }
    func lyrics(title: String, artist: String, album: String? = nil, duration: Double) async -> Result? {
        await lookup(LyricQuery(title:title,artist:artist,album:album,duration:duration)).result
    }
    func lookup(_ query: LyricQuery, force: Bool = false) async -> LyricLookupReport {
        let options = await MainActor.run { LyricSourceOptions.shared.snapshot }
        let key = query.recordingKey + "|" + options.fingerprint
        let pinned = SavedLyrics.read(query.recordingKey,pin:true)
        if !force, let pinned { return LyricLookupReport(result:pinned,pinned:true,cached:true) }
        if !force, let cached = SavedLyrics.read(key,pin:false) { return LyricLookupReport(result:cached,cached:true) }
        var candidates: [LyricCandidate] = []
        var states: [LyricSourceID:String] = [:]
        await withTaskGroup(of: Outcome.self) { group in
            for id in options.enabled { group.addTask { await Self.bounded(id,query:query,localFolder:options.localFolder) } }
            for await outcome in group {
                states[outcome.id] = outcome.state
                if let candidate = outcome.candidate { candidates.append(candidate) }
            }
        }
        guard !Task.isCancelled else { return LyricLookupReport(result:nil) }
        candidates = Self.ranked(candidates, query: query, options: options)
        let chosen = Self.choose(candidates, options: options)
        if let chosen { SavedLyrics.save(chosen.result,key:key,pin:false) }
        return LyricLookupReport(result:pinned ?? chosen?.result,candidates:candidates,states:states,pinned:pinned != nil)
    }
    /// Confidence is evaluated per recording. Source catalogue size plays no role.
    static func ranked(_ candidates: [LyricCandidate], query: LyricQuery, options: LyricSourceOptions.Snapshot) -> [LyricCandidate] {
        candidates.filter { candidate in
            guard options.enabled.contains(candidate.source),
                  LyricQuery.similarity(query.title, candidate.title) >= 0.75,
                  LyricQuery.similarity(query.artist, candidate.artist) >= 0.65 else { return false }
            if let duration = candidate.duration, duration > 0, query.duration > 0 {
                return abs(duration - query.duration) <= max(3, query.duration * 0.02)
            }
            return true
        }.sorted { a, b in
            let tierA = timingTier(a.result), tierB = timingTier(b.result)
            if tierA != tierB { return tierA > tierB }
            let confidenceA = LyricQuery.similarity(query.title, a.title) + LyricQuery.similarity(query.artist, a.artist)
            let confidenceB = LyricQuery.similarity(query.title, b.title) + LyricQuery.similarity(query.artist, b.artist)
            if confidenceA != confidenceB { return confidenceA > confidenceB }
            return (options.enabled.firstIndex(of: a.source) ?? 999) < (options.enabled.firstIndex(of: b.source) ?? 999)
        }
    }
    static func timingTier(_ result: Result) -> Int {
        if hasWordTiming(result) { return 3 }
        if case .synced(let lines, _) = result {
            let vocal = lines.filter { !$0.isInstrumental }
            if vocal.contains(where: { validWordTiming($0) }) { return 2 }
            return vocal.isEmpty ? 0 : 1
        }
        return 0
    }
    static func choose(_ candidates: [LyricCandidate], options: LyricSourceOptions.Snapshot) -> LyricCandidate? {
        var enabled = candidates.filter { options.enabled.contains($0.source) }
        if options.mode == .smart || options.preferWordTiming {
            let best = enabled.map { timingTier($0.result) }.max() ?? 0
            enabled = enabled.filter { timingTier($0.result) == best }
        }
        if options.preferWordTimedLRCLIB, let native = enabled.first(where: { $0.source == .lrclib && hasWordTiming($0.result) }) { return native }
        if options.mode == .priority {
            return options.enabled.compactMap { id in enabled.first { $0.source == id } }.first
        }
        return enabled.first
    }
    private static func bounded(_ id: LyricSourceID, query: LyricQuery, localFolder: String) async -> Outcome {
        await withTaskGroup(of: Outcome.self) { group in
            group.addTask {
                do {
                    let candidate = try await fetch(id,query:query,localFolder:localFolder)
                    return Outcome(id:id,candidate:candidate,state:candidate == nil ? "No usable match / unavailable" : "Match found")
                } catch is CancellationError { return Outcome(id:id,candidate:nil,state:"Cancelled") }
                catch { return Outcome(id:id,candidate:nil,state:error.localizedDescription) }
            }
            group.addTask {
                try? await Task.sleep(nanoseconds: id == .lrclib ? 20_000_000_000 : 8_000_000_000)
                return Outcome(id:id,candidate:nil,state:"Timed out")
            }
            let first = await group.next() ?? Outcome(id:id,candidate:nil,state:"Unavailable")
            group.cancelAll()
            return first
        }
    }
    private static func fetch(_ id: LyricSourceID, query: LyricQuery, localFolder: String) async throws -> LyricCandidate? {
        try Task.checkCancellation()
        switch id {
        case .lyricsPlus:
            guard let hit = await KPoeLyricsProvider.lines(title:query.title,artist:query.artist,album:query.album,duration:query.duration) else {return nil}
            let provenance = Provenance(precision:precision(hit.lines),source:hit.source+" via Lyrics+")
            return LyricCandidate(source:id,result:.synced(hit.lines,provenance),title:query.title,artist:query.artist,
                                  duration:query.duration,score:113)
        case .netEase:
            guard let lines = await NetEaseLyricsProvider.timedLines(title:query.title,artist:query.artist,duration:query.duration) else {return nil}
            let native = lines.contains { $0.wordTimed }
            return LyricCandidate(source:id,result:.synced(lines,Provenance(precision:precision(lines),source:"NetEase")),
                title:query.title,artist:query.artist,duration:query.duration,score:native ? 110 : 96)
        case .qqMusic: return try await AdditionalLyricsProviders.qqMusic(query)
        case .kugou: return try await AdditionalLyricsProviders.kugou(query)
        case .kuwo: return try await AdditionalLyricsProviders.kuwo(query)
        case .lyricsOVH: return try await AdditionalLyricsProviders.lyricsOVH(query)
        case .lrclib: return try await lrclib(query)
        case .lyrimuse: return try LocalLyricBridge.candidate(query, folder: localFolder)
        }
    }
    static func validWordTiming(_ line: Line) -> Bool {
        guard line.wordTimed, !line.words.isEmpty, line.start.isFinite, line.end.isFinite, line.end > line.start else { return false }
        let covered = line.words.map(\.text).joined().filter { $0.isLetter || $0.isNumber }
        guard covered == line.text.filter({ $0.isLetter || $0.isNumber }) else { return false }
        return line.words.enumerated().allSatisfy { i, word in
            word.start.isFinite && word.end.isFinite && word.start >= line.start - 0.05 && word.end > word.start && word.end <= line.end + 0.05 &&
                (i == 0 || word.start >= line.words[i - 1].start)
        }
    }
    static func hasWordTiming(_ result: Result) -> Bool {
        guard case .synced(let lines, _) = result else { return false }
        let vocal = lines.filter { !$0.isInstrumental }
        return !vocal.isEmpty && vocal.allSatisfy { validWordTiming($0) }
    }
    static func precision(_ lines: [Line]) -> String {
        let vocal = lines.filter { !$0.isInstrumental }
        if !vocal.isEmpty, vocal.allSatisfy({validWordTiming($0)}) {return "Word-by-word"}
        if vocal.contains(where:{validWordTiming($0)}) {return "Mixed timing"}
        return "Line-synced"
    }
    private struct Response: Decodable {
        var trackName: String?
        var artistName: String?
        var albumName: String?
        var duration: Double?
        var syncedLyrics: String?
        var lyricsfile: String?
        var plainLyrics: String?
    }
    static func requestLRCLIB(_ path: String, parameters: [String:String], session: URLSession = .shared) async throws -> Data? {
        try Task.checkCancellation()
        var components = URLComponents(string:"https://lrclib.net/api/"+path)
        components?.queryItems = parameters.sorted {$0.key<$1.key}.map {URLQueryItem(name:$0.key,value:$0.value)}
        guard let url = components?.url else {return nil}
        for attempt in 0...1 {
            var request = URLRequest(url: url, timeoutInterval: 4)
            request.setValue("Lunalyr/0.4.6 (https://github.com/WaterBotol/Lunalyr)", forHTTPHeaderField: "User-Agent")
            let (data, response) = try await session.data(for: request)
            let http = response as? HTTPURLResponse
            let status = http?.statusCode ?? 0
            if status == 404 { return nil }
            if (status == 503 || status == 429), attempt == 0 {
                let delay = Double(http?.value(forHTTPHeaderField: "Retry-After") ?? "1") ?? 1
                // Long server backoffs are reported rather than keeping a lookup alive.
                guard delay.isFinite, delay <= 3 else { throw LyricHTTP.Failure(message: "LRCLIB is busy. Try again later (HTTP \(status)).") }
                try await Task.sleep(nanoseconds: UInt64(max(0, delay) * 1_000_000_000))
                continue
            }
            guard status == 200 else { throw LyricHTTP.Failure(message: "LRCLIB: HTTP \(status). The service may be temporarily unavailable.") }
            guard data.count <= 2_000_000 else { throw LyricHTTP.Failure(message: "LRCLIB response too large") }
            return data
        }
        return nil
    }

    static func lrclib(_ query: LyricQuery, session: URLSession = .shared) async throws -> LyricCandidate? {
        var parameters = ["track_name":query.title,"artist_name":query.artist]
        if let album = query.album, !album.isEmpty {parameters["album_name"]=album}
        if query.duration>0 {parameters["duration"]=String(Int(query.duration.rounded()))}
        var payloads: [Response] = []
        var exactError: Error?
        do {
            if let data = try await requestLRCLIB("get",parameters:parameters,session:session), let exact = try? JSONDecoder().decode(Response.self,from:data) { payloads.append(exact) }
        } catch is CancellationError { throw CancellationError() }
        catch { exactError = error }
        let exactTimed = payloads.contains { $0.syncedLyrics?.isEmpty == false || $0.lyricsfile?.isEmpty == false }
        if !exactTimed {
            do {
                if let data = try await requestLRCLIB("search",parameters:["track_name":query.title,"artist_name":query.artist],session:session),
                   let found = try? JSONDecoder().decode([Response].self,from:data) { payloads.append(contentsOf:found.prefix(30)) }
            } catch is CancellationError { throw CancellationError() }
            catch { if payloads.isEmpty { throw error } }
        }
        if payloads.isEmpty, let exactError { throw exactError }
        let candidates = payloads.compactMap { payload -> LyricCandidate? in
            let title=payload.trackName ?? query.title,artist=payload.artistName ?? query.artist
            let delta=payload.duration.map {abs($0-query.duration)} ?? 0
            let score=vc_match_score(LyricQuery.similarity(query.title,title),LyricQuery.similarity(query.artist,artist),
                delta,payload.duration != nil && query.duration>0 ? 1 : 0,0,1,
                LyricQuery.normalize(payload.albumName ?? "") == LyricQuery.normalize(query.album ?? "") ? 1 : 0)
            guard score>0 else {return nil}
            var lines: [Line] = []
            if let file=payload.lyricsfile,!file.isEmpty {lines=parseLyricsFile(file)}
            if lines.isEmpty,let lrc=payload.syncedLyrics,!lrc.isEmpty {lines=parseLRC(lrc,trackDuration:query.duration)}
            let result: Result
            if !lines.isEmpty {result = .synced(lines,Provenance(precision:precision(lines),source:"LRCLIB"))}
            else if let plain=payload.plainLyrics,!plain.isEmpty {
                result = .plain(plain.components(separatedBy:.newlines),Provenance(precision:"Not synced",source:"LRCLIB"))
            } else {return nil}
            return LyricCandidate(source:.lrclib,result:result,title:title,artist:artist,duration:payload.duration,
                score:score-(lines.isEmpty ? 12 : 0))
        }
        return candidates.max {$0.score<$1.score}
    }

    // MARK: - lyricsfile (YAML) parsing

    /// The schema is small and rigidly generated, so a line scanner beats
    /// pulling in a YAML dependency:
    ///   lines:
    ///   - text: I've been tryna call
    ///     start_ms: 27160
    ///     end_ms: 29960
    private struct Lyricsfile: Decodable {
        struct Metadata: Decodable { var offset_ms: Double? }
        struct Row: Decodable {
            struct Token: Decodable { var text: String; var start_ms: Double; var end_ms: Double? }
            var text: String
            var start_ms: Double
            var end_ms: Double?
            var words: [Token]?
        }
        var metadata: Metadata?
        var lines: [Row]?
    }
    static func parseLyricsFile(_ yaml: String) -> [Line] {
        guard yaml.utf8.count <= 1_000_000,
              let file = try? YAMLDecoder().decode(Lyricsfile.self, from: yaml) else { return [] }
        let offset = (file.metadata?.offset_ms ?? 0) / 1000
        guard offset.isFinite else { return [] }
        let rows = (file.lines ?? []).filter { $0.start_ms.isFinite && $0.start_ms >= 0 }.sorted { $0.start_ms < $1.start_ms }
        return rows.enumerated().compactMap { index, row in
            let start = max(0, row.start_ms / 1000 + offset)
            let end = row.end_ms.map { $0 / 1000 + offset } ?? (index + 1 < rows.count ? rows[index + 1].start_ms / 1000 + offset : start + 4)
            guard end.isFinite, end > start else { return nil }
            var line = makeLine(text: row.text, start: start, end: end)
            let tokens = row.words ?? []
            let words = tokens.enumerated().map { i, token in
                Word(text: token.text.trimmingCharacters(in: .whitespacesAndNewlines),
                     start: token.start_ms / 1000 + offset,
                     end: token.end_ms.map { $0 / 1000 + offset } ?? (i + 1 < tokens.count ? tokens[i + 1].start_ms / 1000 + offset : end))
            }
            let covered = words.map(\.text).joined().filter { !$0.isWhitespace }
            if !words.isEmpty, covered == row.text.filter({ !$0.isWhitespace }), words.enumerated().allSatisfy({ i, word in
                word.start.isFinite && word.end.isFinite && word.start >= start && word.end > word.start && word.end <= end &&
                    (i == 0 || word.start >= words[i - 1].end)
            }) {
                line.words = words; line.wordTimed = true
            }
            return line
        }
    }

    // MARK: - LRC parsing (fallback)

    static func parseLRC(_ text: String, trackDuration: Double) -> [Line] {
        guard text.utf8.count <= 1_000_000,
              let lineStamp = try? NSRegularExpression(pattern:#"\[(\d{1,3}):([0-5]\d)(?:[.:](\d{1,3}))?\]"#),
              let wordStamp = try? NSRegularExpression(pattern:#"<(\d{1,3}):([0-5]\d)(?:[.:](\d{1,3}))?>"#)
        else {return []}
        func time(_ match:NSTextCheckingResult,_ value:String)->Double {
            let ns=value as NSString
            let m=Double(ns.substring(with:match.range(at:1))) ?? 0
            let s=Double(ns.substring(with:match.range(at:2))) ?? 0
            let range=match.range(at:3)
            let fraction=range.location == NSNotFound ? "" : ns.substring(with:range)
            return m*60+s+(Double(fraction) ?? 0)/pow(10,Double(fraction.count))
        }
        var offset=0.0
        if let regex=try? NSRegularExpression(pattern:#"(?im)^\s*\[offset:([+-]?\d+)\]"#),
           let match=regex.firstMatch(in:text,range:NSRange(text.startIndex...,in:text)),
           let range=Range(match.range(at:1),in:text) {let value=Double(text[range]) ?? 0;offset=value.isFinite ? value/1000 : 0}
        var parsed:[(start:Double,text:String,words:[Word])]=[]
        for raw in text.components(separatedBy:.newlines) {
            let row=raw.trimmingCharacters(in:.whitespaces)
            let ns=row as NSString
            let tags=lineStamp.matches(in:row,range:NSRange(location:0,length:ns.length))
            var leading:[NSTextCheckingResult]=[],end=0
            for tag in tags {guard tag.range.location==end else {break};leading.append(tag);end=NSMaxRange(tag.range)}
            guard let first=leading.first else {continue}
            let body=ns.substring(from:end).trimmingCharacters(in:.whitespaces)
            let bodyNS=body as NSString
            let markers=wordStamp.matches(in:body,range:NSRange(location:0,length:bodyNS.length))
            let plain=wordStamp.stringByReplacingMatches(in:body,range:NSRange(location:0,length:bodyNS.length),withTemplate:"")
            for tag in leading {
                let begin=max(0,time(tag,row)+offset),shift=time(tag,row)-time(first,row)+offset
                var words:[Word]=[]
                let prefix=markers.first.map {bodyNS.substring(to:$0.range.location).trimmingCharacters(in:.whitespaces)} ?? ""
                for (index,marker) in markers.enumerated() {
                    let from=NSMaxRange(marker.range),to=index+1<markers.count ? markers[index+1].range.location : bodyNS.length
                    let label=bodyNS.substring(with:NSRange(location:from,length:to-from))
                    if label.trimmingCharacters(in:.whitespacesAndNewlines).isEmpty {continue}
                    let start=max(0,time(marker,body)+shift)
                    let finish=index+1<markers.count ? max(start,time(markers[index+1],body)+shift) : Double.greatestFiniteMagnitude
                    words.append(Word(text:label.trimmingCharacters(in:.whitespacesAndNewlines),start:start,end:finish))
                }
                let valid=prefix.isEmpty && words.enumerated().allSatisfy { index,word in
                    word.start>=begin && (index==0 || word.start>=words[index-1].start)
                }
                parsed.append((begin,plain,valid ? words : []))
            }
        }
        parsed.sort {$0.start<$1.start}
        return parsed.enumerated().map { index,item in
            let end=index+1<parsed.count ? parsed[index+1].start : max(trackDuration,item.start+4)
            var line=makeLine(text:item.text,start:item.start,end:end)
            if !item.words.isEmpty, item.words.allSatisfy({$0.start < end}) {
                line.words=item.words.map {Word(text:$0.text,start:$0.start,end:max($0.start,min(end,$0.end)))}
                line.wordTimed=true
            }
            return line
        }
    }

    // MARK: - Shared line construction

    static func makeLine(text: String, start: Double, end: Double) -> Line {
        let cleaned = text.trimmingCharacters(in: .whitespaces)
        // A lone note glyph marks an instrumental break rather than a lyric.
        let instrumental = cleaned.isEmpty || cleaned.allSatisfy { "♪♫•·-–—".contains($0) || $0.isWhitespace }
        return Line(
            start: start,
            end: end,
            text: instrumental ? "" : cleaned,
            words: instrumental ? [] : distributeWords(in: cleaned, from: start, to: end),
            isInstrumental: instrumental
        )
    }

    /// Splits a line's window across its words proportionally to their length,
    /// so longer words hold emphasis longer -- far more natural than an even
    /// split. The window is trimmed slightly because a sung line normally
    /// finishes before the next one begins.
    static func distributeWords(in text: String, from start: TimeInterval, to end: TimeInterval) -> [Word] {
        var tokens: [String] = []
        text.enumerateSubstrings(in: text.startIndex..., options: .byWords) { _, _, enclosing, _ in
            let token=String(text[enclosing]).trimmingCharacters(in:.whitespacesAndNewlines)
            if !token.isEmpty {tokens.append(token)}
        }
        guard !tokens.isEmpty else { return [] }

        let window = max(end - start, 0.3)
        let usable = min(window, max(Double(tokens.count) * 0.42, window * 0.88))
        let weights = tokens.map { Double($0.count) + 1.5 }
        let totalWeight = weights.reduce(0, +)

        var words: [Word] = []
        var cursor = start
        for (token, weight) in zip(tokens, weights) {
            let slice = usable * (weight / totalWeight)
            words.append(Word(text: token, start: cursor, end: cursor + slice))
            cursor += slice
        }
        return words
    }
}
