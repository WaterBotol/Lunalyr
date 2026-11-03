import Foundation

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
        candidates.sort {
            if abs($0.score - $1.score) > 0.001 { return $0.score > $1.score }
            return $0.source.rawValue < $1.source.rawValue
        }
        let chosen: LyricCandidate?
        if options.mode == .priority {
            chosen = options.enabled.compactMap { id in candidates.first { $0.source == id } }.first
        } else { chosen = candidates.first }
        if let chosen { SavedLyrics.save(chosen.result,key:key,pin:false) }
        return LyricLookupReport(result:pinned ?? chosen?.result,candidates:candidates,states:states,pinned:pinned != nil)
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
                try? await Task.sleep(nanoseconds:8_000_000_000)
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
        case .lrclib: return try await lrclib(query)
        case .lyrimuse: return try LocalLyricBridge.candidate(query, folder: localFolder)
        }
    }
    static func precision(_ lines: [Line]) -> String {
        let vocal = lines.filter { !$0.isInstrumental }
        if !vocal.isEmpty, vocal.allSatisfy({$0.wordTimed}) {return "Word-by-word"}
        if vocal.contains(where:{$0.wordTimed}) {return "Mixed timing"}
        return "Estimated words"
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
    private static func requestLRCLIB(_ path: String, parameters: [String:String]) async throws -> Data? {
        try Task.checkCancellation()
        var components = URLComponents(string:"https://lrclib.net/api/"+path)
        components?.queryItems = parameters.sorted {$0.key<$1.key}.map {URLQueryItem(name:$0.key,value:$0.value)}
        guard let url = components?.url else {return nil}
        var request = URLRequest(url:url,timeoutInterval:6)
        request.setValue("Verso-Custom/0.2 (personal macOS lyrics)",forHTTPHeaderField:"User-Agent")
        let (data,response) = try await URLSession.shared.data(for:request)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 0
        if status == 404 {return nil}
        guard status == 200 else {throw LyricHTTP.Failure(message:"LRCLIB: HTTP \(status)")}
        guard data.count <= 2_000_000 else {throw LyricHTTP.Failure(message:"LRCLIB response too large")}
        return data
    }
    private static func lrclib(_ query: LyricQuery) async throws -> LyricCandidate? {
        var parameters = ["track_name":query.title,"artist_name":query.artist]
        if let album = query.album, !album.isEmpty {parameters["album_name"]=album}
        if query.duration>0 {parameters["duration"]=String(Int(query.duration.rounded()))}
        var payloads: [Response] = []
        if let data = try await requestLRCLIB("get",parameters:parameters), let exact = try? JSONDecoder().decode(Response.self,from:data) {payloads.append(exact)}
        let exactTimed = payloads.contains { $0.syncedLyrics?.isEmpty == false || $0.lyricsfile?.isEmpty == false }
        if !exactTimed, let data = try await requestLRCLIB("search",parameters:["track_name":query.title,"artist_name":query.artist]),
           let found = try? JSONDecoder().decode([Response].self,from:data) {payloads.append(contentsOf:found.prefix(30))}
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
    private static func parseLyricsFile(_ yaml: String) -> [Line] {
        var entries: [(start: Double, end: Double, text: String)] = []
        var pendingText: String?
        var pendingStart: Double?
        var pendingEnd: Double?
        var insideLines = false

        func flush() {
            defer { pendingText = nil; pendingStart = nil; pendingEnd = nil }
            guard let text = pendingText, let start = pendingStart else { return }
            entries.append((start, pendingEnd ?? start + 4, text))
        }

        for rawLine in yaml.split(separator: "\n", omittingEmptySubsequences: false) {
            let trimmed = String(rawLine).trimmingCharacters(in: .whitespaces)
            if trimmed == "lines:" { insideLines = true; continue }
            guard insideLines else { continue }

            if trimmed.hasPrefix("- text:") {
                flush()
                pendingText = unquote(String(trimmed.dropFirst("- text:".count)).trimmingCharacters(in: .whitespaces))
            } else if trimmed.hasPrefix("text:"), pendingText == nil {
                pendingText = unquote(String(trimmed.dropFirst("text:".count)).trimmingCharacters(in: .whitespaces))
            } else if trimmed.hasPrefix("start_ms:") {
                pendingStart = Double(trimmed.dropFirst("start_ms:".count).trimmingCharacters(in: .whitespaces)).map { $0 / 1000 }
            } else if trimmed.hasPrefix("end_ms:") {
                pendingEnd = Double(trimmed.dropFirst("end_ms:".count).trimmingCharacters(in: .whitespaces)).map { $0 / 1000 }
            }
        }
        flush()
        return entries.map { makeLine(text: $0.text, start: $0.start, end: $0.end) }
    }

    private static func unquote(_ value: String) -> String {
        var result = value
        for quote in ["'", "\""] where result.hasPrefix(quote) && result.hasSuffix(quote) && result.count >= 2 {
            result = String(result.dropFirst().dropLast())
            result = result.replacingOccurrences(of: quote + quote, with: quote)
            break
        }
        return result
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
