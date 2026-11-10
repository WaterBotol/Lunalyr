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
    private static func candidate(_ source: LyricSourceID, lrc: String, query: LyricQuery,
                                  title: String, artist: String, duration: Double?) -> LyricCandidate? {
        let lines = LyricsFetcher.parseLRC(LyricHTTP.text(lrc), trackDuration: query.duration)
        guard !lines.isEmpty else { return nil }
        let enhanced = lines.contains { $0.wordTimed }
        let precision = LyricsFetcher.precision(lines)
        let provenance = LyricsFetcher.Provenance(precision: precision, source: source.title)
        let value = score(query, title: title, artist: artist, duration: duration)
        guard value > 0 else { return nil }
        return LyricCandidate(source: source, result: .synced(lines, provenance),
            title: title, artist: artist, duration: duration, score: value + (enhanced ? 13 : 0))
    }
    static func qqMusic(_ query: LyricQuery) async throws -> LyricCandidate? {
        let json = try await LyricHTTP.json("https://c.y.qq.com/soso/fcgi-bin/client_search_cp",
            ["format":"json","p":"1","n":"8","w":query.title+" "+query.artist], referer:"https://y.qq.com/")
        let data = json["data"] as? [String:Any]
        let songs = (data?["song"] as? [String:Any])?["list"] as? [[String:Any]] ?? []
        let ranked = songs.compactMap { song -> (song: [String:Any], title: String, artist: String, duration: Double?, score: Double)? in
            guard let title = song["songname"] as? String else { return nil }
            let singers = (song["singer"] as? [[String:Any]] ?? []).compactMap { $0["name"] as? String }.joined(separator:" & ")
            let duration = (song["interval"] as? NSNumber)?.doubleValue
            let value = score(query,title:title,artist:singers,duration:duration)
            return value > 0 ? (song,title,singers,duration,value) : nil
        }
        for hit in ordered(ranked, .qqMusic, query: query, { ($0.title, $0.artist, $0.duration, $0.score) }).prefix(3) {
            try Task.checkCancellation()
            guard let mid = hit.song["songmid"] as? String else { continue }
            let payload = try await LyricHTTP.json("https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg",
                ["songmid":mid,"format":"json","nobase64":"1","g_tk":"5381"],referer:"https://y.qq.com/")
            guard var text = payload["lyric"] as? String, !text.isEmpty else { continue }
            if !text.contains("["), let bytes = Data(base64Encoded:text), let decoded = String(data:bytes,encoding:.utf8) { text=decoded }
            if let result = candidate(.qqMusic,lrc:text,query:query,title:hit.title,artist:hit.artist,duration:hit.duration) { return result }
        }
        return nil
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
        for hit in ordered(ranked, .kugou, query: query, { ($0.title, $0.artist, $0.duration, $0.score) }).prefix(2) {
            try Task.checkCancellation()
            guard let hash = hit.song["hash"] as? String else { continue }
            let search = try await LyricHTTP.json("https://lyrics.kugou.com/search",
                ["ver":"1","man":"yes","client":"pc","keyword":query.title+" "+query.artist,
                 "duration":String(Int(query.duration*1000)),"hash":hash], session: session)
            let candidates = search["candidates"] as? [[String:Any]] ?? []
            for item in candidates.prefix(2) {
                try Task.checkCancellation()
                guard let id = LyricHTTP.string(item["id"]), let key = item["accesskey"] as? String else { continue }
                // KRC carries each word's timing; plain LRC is the fallback.
                if let krc = try? await LyricHTTP.json("https://lyrics.kugou.com/download",
                        ["ver":"1","client":"pc","id":id,"accesskey":key,"fmt":"krc","charset":"utf8"], session: session),
                   let encoded = krc["content"] as? String, let bytes = Data(base64Encoded:encoded),
                   let converted = KugouKRC.enhancedLRC(from: bytes),
                   let result = candidate(.kugou,lrc:converted,query:query,title:hit.title,artist:hit.artist,duration:hit.duration) {
                    return result
                }
                let response = try await LyricHTTP.json("https://lyrics.kugou.com/download",
                    ["ver":"1","client":"pc","id":id,"accesskey":key,"fmt":"lrc","charset":"utf8"], session: session)
                guard let encoded = response["content"] as? String,
                      let bytes = Data(base64Encoded:encoded),
                      let lrc = String(data:bytes,encoding:.utf8) else { continue }
                if let result = candidate(.kugou,lrc:lrc,query:query,title:hit.title,artist:hit.artist,duration:hit.duration) { return result }
            }
        }
        return nil
    }
}

extension AdditionalLyricsProviders {
    /// Independent implementation of Kuwo's search + line-lyric web endpoints.
    /// Contract reference: Yudaotor/lyrimuse, lyrimuse-engine/kuwo.go.
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
        for hit in ordered(ranked, .kuwo, query: query, { ($0.1, $0.2, $0.3, $0.4) }).prefix(2) {
            let json = try await LyricHTTP.json("https://www.kuwo.cn/openapi/v1/www/lyric/getlyric", ["musicId": hit.0], referer: "https://www.kuwo.cn/", session: session)
            guard let data = json["data"] as? [String: Any], let rows = data["lrclist"] as? [[String: Any]] else { continue }
            let lrc = rows.compactMap { row -> String? in
                guard let time = LyricHTTP.string(row["time"]).flatMap(Double.init), time.isFinite, time >= 0,
                      let text = row["lineLyric"] as? String else { return nil }
                return "[" + KugouKRC.clock(time) + "]" + text
            }.joined(separator: "\n")
            if let match = candidate(.kuwo, lrc: lrc, query: query, title: hit.1, artist: hit.2, duration: hit.3) { return match }
        }
        return nil
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
