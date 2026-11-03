import Foundation

/// Independent adapters for public lyric lookup endpoints. No account cookies,
/// credentials, audio downloads or private listening history are requested.
enum AdditionalLyricsProviders {
    private static func score(_ query: LyricQuery, title: String, artist: String, duration: Double?) -> Double {
        vc_match_score(LyricQuery.similarity(query.title, title), LyricQuery.similarity(query.artist, artist),
            duration.map { abs($0 - query.duration) } ?? 0, duration != nil && query.duration > 0 ? 1 : 0,
            0, 1, 0)
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
        }.sorted { $0.score > $1.score }
        for hit in ranked.prefix(3) {
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
    static func kugou(_ query: LyricQuery) async throws -> LyricCandidate? {
        let payload = try await LyricHTTP.json("https://mobilecdn.kugou.com/api/v3/search/song",
            ["keyword":query.title+" "+query.artist,"page":"1","pagesize":"8","format":"json"])
        let songs = (payload["data"] as? [String:Any])?["info"] as? [[String:Any]] ?? []
        let ranked = songs.compactMap { song -> (song:[String:Any],title:String,artist:String,duration:Double?,score:Double)? in
            let filename = (song["filename"] as? String ?? "").components(separatedBy:" - ")
            let title = song["songname"] as? String ?? filename.last ?? ""
            let artist = song["singername"] as? String ?? filename.first ?? ""
            let duration = (song["duration"] as? NSNumber)?.doubleValue
            let value = score(query,title:title,artist:artist,duration:duration)
            return value > 0 ? (song,title,artist,duration,value) : nil
        }.sorted { $0.score > $1.score }
        for hit in ranked.prefix(2) {
            try Task.checkCancellation()
            guard let hash = hit.song["hash"] as? String else { continue }
            let search = try await LyricHTTP.json("https://lyrics.kugou.com/search",
                ["ver":"1","man":"yes","client":"pc","keyword":query.title+" "+query.artist,
                 "duration":String(Int(query.duration*1000)),"hash":hash])
            let candidates = search["candidates"] as? [[String:Any]] ?? []
            for item in candidates.prefix(2) {
                try Task.checkCancellation()
                guard let id = LyricHTTP.string(item["id"]), let key = item["accesskey"] as? String else { continue }
                let response = try await LyricHTTP.json("https://lyrics.kugou.com/download",
                    ["ver":"1","client":"pc","id":id,"accesskey":key,"fmt":"lrc","charset":"utf8"])
                guard let encoded = response["content"] as? String,
                      let bytes = Data(base64Encoded:encoded),
                      let lrc = String(data:bytes,encoding:.utf8) else { continue }
                if let result = candidate(.kugou,lrc:lrc,query:query,title:hit.title,artist:hit.artist,duration:hit.duration) { return result }
            }
        }
        return nil
    }
}
