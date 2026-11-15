import Foundation

/// Independent adapters for public lyric lookup endpoints. No account cookies,
/// credentials, audio downloads or private listening history are requested.
enum AdditionalLyricsProviders {
    private static func score(_ query: LyricQuery, title: String, artist: String, duration: Double?) -> Double {
        vc_match_score(LyricQuery.similarity(query.title, title), query.artistScore(artist),
            duration.map { abs($0 - query.duration) } ?? 0, duration != nil && query.duration > 0 ? 1 : 0,
            0, 1, 0)
    }
    /// Hits that name the playing recording first (`LyricsFetcher.namesRecording`),
    /// then by score, so a close name with the wrong length is not fetched
    /// and offered ahead of the right recording.
    private static func ordered<Hit>(_ hits: [Hit], _ source: LyricSourceID, query: LyricQuery,
                                     _ fields: (Hit) -> (title: String, artist: String, duration: Double?, score: Double)) -> [Hit] {
        hits.map { hit -> (hit: Hit, passes: Int, score: Double) in
            let f = fields(hit)
            return (hit, LyricsFetcher.namesRecording(source, title: f.title, artist: f.artist, duration: f.duration, query: query) ? 1 : 0, f.score)
        }.sorted { ($0.passes, $0.score) > ($1.passes, $1.score) }.map(\.hit)
    }
    /// Records a catalogue search for the lookup trace: what came back, what scored, and what will be read.
    private static func trace<Hit>(_ label: String, returned: Int, scored ordered: [Hit], read: Int, query: LyricQuery, _ source: LyricSourceID,
                                   _ fields: (Hit) -> (title: String, artist: String, duration: Double?, score: Double)) {
        guard LyricTrace.isOn else { return }
        LyricTrace.results(label, returned: returned, read: min(read, ordered.count))
        for (index, hit) in ordered.enumerated() {
            let f = fields(hit)
            let passes = LyricsFetcher.namesRecording(source, title: f.title, artist: f.artist, duration: f.duration, query: query)
            LyricTrace.hit(LyricTraceHit(title: f.title, artist: f.artist, duration: f.duration, score: f.score,
                                         titleSimilarity: LyricQuery.similarity(query.title, f.title), artistSimilarity: query.artistScore(f.artist),
                                         verdict: (index < read ? "read" : "not read") + (passes ? "" : " · fails the recording check")))
        }
        if returned > ordered.count { LyricTrace.note("\(returned - ordered.count) result(s) scored 0 and were dropped") }
    }
    private static func candidate(_ source: LyricSourceID, lrc: String, query: LyricQuery,
                                  title: String, artist: String, duration: Double?) -> LyricCandidate? {
        let lines = LyricsFetcher.parseLRC(LyricHTTP.text(lrc), trackDuration: query.duration)
        guard !lines.isEmpty else {
            LyricTrace.note("\(source.title): lyrics for “\(title)” held no timed lines")
            return nil
        }
        let enhanced = lines.contains { $0.wordTimed }
        let precision = LyricsFetcher.precision(lines)
        let provenance = LyricsFetcher.Provenance(precision: precision, source: source.title)
        let value = score(query, title: title, artist: artist, duration: duration)
        guard value > 0 else { return nil }
        return LyricCandidate(source: source, result: .synced(lines, provenance),
            title: title, artist: artist, duration: duration, score: value + (enhanced ? 13 : 0))
    }
    /// A candidate with word timing, and whether its hit names the playing recording.
    private static func wordTimed(_ candidate: LyricCandidate?) -> Bool {
        guard case .synced(let lines, _)? = candidate?.result else { return false }
        return lines.contains { $0.wordTimed }
    }
    /// Keeps the first line-timed candidate while later hits are tried for
    /// word timing. Word timing from a hit that is not the playing recording
    /// does not displace the right recording's line timing.
    private struct HeldLine {
        var candidate: LyricCandidate?
        var namesRecording = false
        mutating func hold(_ found: LyricCandidate?, namesRecording passes: Bool) {
            guard candidate == nil, let found else { return }
            candidate = found; namesRecording = passes
        }
        func yields(to passes: Bool) -> Bool { candidate == nil || passes || !namesRecording }
    }

    /// QQ Music: QRC word timing from `lyric_download.fcg` (by the search's
    /// numeric song id; one request, no session), else the line LRC from the
    /// same reply or `fcg_query_lyric_new.fcg`. Contract reference:
    /// Yudaotor/lyrimuse, lyrimuse-engine/qqfallback.go `qqLyricDownload`.
    static let qqLyricDownload = "https://c.y.qq.com/qqmusic/fcgi-bin/lyric_download.fcg"
    static func qqMusic(_ query: LyricQuery, session: URLSession = .shared) async throws -> LyricCandidate? {
        let json = try await LyricHTTP.json("https://c.y.qq.com/soso/fcgi-bin/client_search_cp",
            ["format":"json","p":"1","n":"8","w":query.title+" "+query.artist], referer:"https://y.qq.com/", session: session)
        let data = json["data"] as? [String:Any]
        let songs = (data?["song"] as? [String:Any])?["list"] as? [[String:Any]] ?? []
        let ranked = songs.compactMap { song -> (song: [String:Any], title: String, artist: String, duration: Double?, score: Double)? in
            guard let title = song["songname"] as? String else { return nil }
            let singers = (song["singer"] as? [[String:Any]] ?? []).compactMap { $0["name"] as? String }.joined(separator:" & ")
            let duration = (song["interval"] as? NSNumber)?.doubleValue
            let value = score(query,title:title,artist:singers,duration:duration)
            return value > 0 ? (song,title,singers,duration,value) : nil
        }
        let qqOrdered = ordered(ranked, .qqMusic, query: query, { ($0.title, $0.artist, $0.duration, $0.score) })
        trace("QQ search", returned: songs.count, scored: qqOrdered, read: 3, query: query, .qqMusic) { ($0.title, $0.artist, $0.duration, $0.score) }
        var held = HeldLine()
        for hit in qqOrdered.prefix(3) {
            try Task.checkCancellation()
            let passes = LyricsFetcher.namesRecording(.qqMusic, title: hit.title, artist: hit.artist, duration: hit.duration, query: query)
            // Nothing from this hit could replace the line timing held.
            guard held.yields(to: passes) else { continue }
            func offer(_ lrc: String) -> LyricCandidate? {
                candidate(.qqMusic, lrc: lrc, query: query, title: hit.title, artist: hit.artist, duration: hit.duration)
            }
            // Word timing first: QRC, triple-DES over zlib (see QQMusicQRC).
            if let id = LyricHTTP.string(hit.song["songid"]), id != "0",
               let reply = try? await LyricHTTP.data(qqLyricDownload, form: ["version":"15","miniversion":"82","lrctype":"4","musicid":id],
                                                     referer: "https://y.qq.com/", session: session),
               let download = QQMusicQRC.download(String(decoding: reply, as: UTF8.self)) {
                if let enhanced = download.enhanced, let found = offer(enhanced), wordTimed(found), held.yields(to: passes) {
                    return found
                }
                LyricTrace.note("QQ Music: no word timing for “\(hit.title)”")
                if let line = download.line ?? download.enhanced { held.hold(offer(line), namesRecording: passes) }
            }
            // Line LRC is fetched only until one is held; later hits are read for word timing alone.
            guard held.candidate == nil, let mid = hit.song["songmid"] as? String else { continue }
            let payload = try await LyricHTTP.json("https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg",
                ["songmid":mid,"format":"json","nobase64":"1","g_tk":"5381"],referer:"https://y.qq.com/", session: session)
            guard var text = payload["lyric"] as? String, !text.isEmpty else { continue }
            if !text.contains("["), let bytes = Data(base64Encoded:text), let decoded = String(data:bytes,encoding:.utf8) { text=decoded }
            held.hold(offer(text), namesRecording: passes)
        }
        return held.candidate
    }
    /// Kugou's song search. mobilecdn.kugou.com stopped presenting a
    /// certificate for its own name, so every request to it failed;
    /// mobileservice.kugou.com serves the same API.
    static let kugouSearch = "https://mobileservice.kugou.com/api/v3/search/song"
    static func kugou(_ query: LyricQuery, session: URLSession = .shared) async throws -> LyricCandidate? {
        let payload = try await LyricHTTP.json(kugouSearch,
            ["keyword":query.title+" "+query.artist,"page":"1","pagesize":"8","format":"json"], session: session)
        let songs = (payload["data"] as? [String:Any])?["info"] as? [[String:Any]] ?? []
        let ranked = songs.compactMap { song -> (song:[String:Any],title:String,artist:String,duration:Double?,score:Double)? in
            let filename = (song["filename"] as? String ?? "").components(separatedBy:" - ")
            let title = song["songname"] as? String ?? filename.last ?? ""
            let artist = song["singername"] as? String ?? filename.first ?? ""
            let duration = (song["duration"] as? NSNumber)?.doubleValue
            let value = score(query,title:title,artist:artist,duration:duration)
            return value > 0 ? (song,title,artist,duration,value) : nil
        }
        let kugouOrdered = ordered(ranked, .kugou, query: query, { ($0.title, $0.artist, $0.duration, $0.score) })
        trace("Kugou search", returned: songs.count, scored: kugouOrdered, read: 2, query: query, .kugou) { ($0.title, $0.artist, $0.duration, $0.score) }
        var held = HeldLine()
        for hit in kugouOrdered.prefix(2) {
            try Task.checkCancellation()
            guard let hash = hit.song["hash"] as? String else { continue }
            let passes = LyricsFetcher.namesRecording(.kugou, title: hit.title, artist: hit.artist, duration: hit.duration, query: query)
            guard held.yields(to: passes) else { continue }
            func offer(_ lrc: String) -> LyricCandidate? {
                candidate(.kugou, lrc: lrc, query: query, title: hit.title, artist: hit.artist, duration: hit.duration)
            }
            let search = try await LyricHTTP.json("https://lyrics.kugou.com/search",
                ["ver":"1","man":"yes","client":"pc","keyword":query.title+" "+query.artist,
                 "duration":String(Int(query.duration*1000)),"hash":hash], session: session)
            let candidates = search["candidates"] as? [[String:Any]] ?? []
            LyricTrace.results("Kugou lyric candidates for “\(hit.title)”", returned: candidates.count, read: min(candidates.count, 2))
            for item in candidates.prefix(2) {
                try Task.checkCancellation()
                guard let id = LyricHTTP.string(item["id"]), let key = item["accesskey"] as? String else { continue }
                // KRC carries each word's timing; plain LRC is the fallback.
                if let krc = try? await LyricHTTP.json("https://lyrics.kugou.com/download",
                        ["ver":"1","client":"pc","id":id,"accesskey":key,"fmt":"krc","charset":"utf8"], session: session),
                   let encoded = krc["content"] as? String, let bytes = Data(base64Encoded:encoded),
                   let converted = KugouKRC.enhancedLRC(from: bytes), let found = offer(converted) {
                    if wordTimed(found), held.yields(to: passes) { return found }
                    held.hold(found, namesRecording: passes)
                }
                // Line LRC is fetched only until one is held; a later candidate may still have KRC.
                guard held.candidate == nil else { continue }
                let response = try await LyricHTTP.json("https://lyrics.kugou.com/download",
                    ["ver":"1","client":"pc","id":id,"accesskey":key,"fmt":"lrc","charset":"utf8"], session: session)
                guard let encoded = response["content"] as? String,
                      let bytes = Data(base64Encoded:encoded),
                      let lrc = String(data:bytes,encoding:.utf8) else { continue }
                held.hold(offer(lrc), namesRecording: passes)
            }
        }
        return held.candidate
    }
}

extension AdditionalLyricsProviders {
    /// Independent implementation of Kuwo's search, word-timed lrcx and
    /// line-lyric web endpoints. Contract reference: Yudaotor/lyrimuse,
    /// lyrimuse-engine/kuwo.go and kuwolrcx.go.
    static let kuwoLrcx = "https://mlyric.kuwo.cn/mobi.s"
    static func kuwo(_ query: LyricQuery, session: URLSession = .shared) async throws -> LyricCandidate? {
        let search = try await LyricHTTP.json("https://search.kuwo.cn/r.s",
            ["all": query.title + " " + query.artist, "ft": "music", "itemset": "web_2013", "client": "kt", "pn": "0", "rn": "8", "rformat": "json", "encoding": "utf8", "pcjson": "1", "vipver": "1"], referer: "https://www.kuwo.cn/", session: session)
        let songs = search["abslist"] as? [[String: Any]] ?? []
        let ranked = songs.compactMap { song -> (String, String, String, Double?, Double)? in
            guard let rid = song["MUSICRID"] as? String, let title = song["SONGNAME"] as? String, let artist = song["ARTIST"] as? String,
                  let id = rid.split(separator: "_").last else { return nil }
            let duration = LyricHTTP.string(song["DURATION"]).flatMap(Double.init)
            let value = score(query, title: title, artist: artist, duration: duration)
            return value > 0 ? (String(id), title, artist, duration, value) : nil
        }
        let kuwoOrdered = ordered(ranked, .kuwo, query: query, { ($0.1, $0.2, $0.3, $0.4) })
        trace("Kuwo search", returned: songs.count, scored: kuwoOrdered, read: 2, query: query, .kuwo) { ($0.1, $0.2, $0.3, $0.4) }
        var held = HeldLine()
        for hit in kuwoOrdered.prefix(2) {
            try Task.checkCancellation()
            let passes = LyricsFetcher.namesRecording(.kuwo, title: hit.1, artist: hit.2, duration: hit.3, query: query)
            guard held.yields(to: passes) else { continue }
            // Word timing first: lrcx (see KuwoLrcx), same music id as the line list.
            if let reply = try? await LyricHTTP.data(kuwoLrcx, ["f":"web","type":"lyric","lrcx":"1","encode":"utf8","rid":hit.0], session: session),
               let enhanced = KuwoLrcx.enhancedLRC(from: reply),
               let found = candidate(.kuwo, lrc: enhanced, query: query, title: hit.1, artist: hit.2, duration: hit.3),
               wordTimed(found), held.yields(to: passes) {
                return found
            }
            // The line list is fetched only until one is held; the next hit is read for word timing alone.
            guard held.candidate == nil else { continue }
            let json = try await LyricHTTP.json("https://www.kuwo.cn/openapi/v1/www/lyric/getlyric", ["musicId": hit.0], referer: "https://www.kuwo.cn/", session: session)
            guard let data = json["data"] as? [String: Any], let rows = data["lrclist"] as? [[String: Any]] else { continue }
            let lrc = rows.compactMap { row -> String? in
                guard let time = LyricHTTP.string(row["time"]).flatMap(Double.init), time.isFinite, time >= 0,
                      let text = row["lineLyric"] as? String else { return nil }
                return "[" + KugouKRC.clock(time) + "]" + text
            }.joined(separator: "\n")
            held.hold(candidate(.kuwo, lrc: lrc, query: query, title: hit.1, artist: hit.2, duration: hit.3), namesRecording: passes)
        }
        return held.candidate
    }
    static func lyricsOVH(_ query: LyricQuery, session: URLSession = .shared) async throws -> LyricCandidate? {
        // The endpoint is addressed by artist; there is nothing to ask without one.
        guard query.hasArtist else { return nil }
        let allowed = CharacterSet.urlPathAllowed.subtracting(CharacterSet(charactersIn: "/;?#%"))
        guard let artist = query.artist.addingPercentEncoding(withAllowedCharacters: allowed),
              let title = query.title.addingPercentEncoding(withAllowedCharacters: allowed) else { return nil }
        let json = try await LyricHTTP.json("https://api.lyrics.ovh/v1/" + artist + "/" + title, [:], session: session)
        guard let text = json["lyrics"] as? String else { return nil }
        let lines = text.components(separatedBy: .newlines).map { $0.trimmingCharacters(in: .whitespaces) }
        guard lines.contains(where: { !$0.isEmpty }) else { return nil }
        return .init(source: .lyricsOVH, result: .plain(lines, .init(precision: "Plain text · no timing", source: "lyrics.ovh")),
                     title: query.title, artist: query.artist, score: 0)
    }
}
