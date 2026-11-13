import Foundation

/// The AMLL TTML database: community-made, word- and syllable-timed lyrics,
/// stored one file per NetEase song id. NetEase's own search finds the song,
/// and the id then addresses the database's file, so only a song both
/// services know is found. Public files only; nothing is uploaded.
///
/// Layout (README of amll-dev/amll-ttml-db): `ncm-lyrics/<id>.ttml`.
enum AMLLLyricsProvider {
    static let bases = [
        "https://raw.githubusercontent.com/amll-dev/amll-ttml-db/refs/heads/main",
        "https://raw.githubusercontent.com/Steve-xmh/amll-ttml-db/refs/heads/main",
    ]

    static func fileAddress(base: String, neteaseID: Int) -> String {
        "\(base)/ncm-lyrics/\(neteaseID).ttml"
    }

    static func lookup(_ query: LyricQuery, session: URLSession = .shared) async throws -> LyricCandidate? {
        let recorder = NetEaseLyricsProvider.MatchRecorder(session: session)
        let matched = await NetEaseLyricsProvider.$recorder.withValue(recorder) {
            await NetEaseLyricsProvider.matchSong(title: query.title, artist: query.artist, duration: query.duration)
        }
        try Task.checkCancellation()
        guard let song = matched else {
            if let failure = recorder.failure { throw failure }
            LyricTrace.note("AMLL: NetEase found no song, so there is no file to read")
            return nil
        }
        var text: String?
        for base in bases {
            do {
                text = try await LyricHTTP.fetchText(fileAddress(base: base, neteaseID: song.id), session: session)
                break
            } catch {
                if LyricHTTP.isOffline(error) { throw error }
                try Task.checkCancellation()
            }
        }
        guard let text else {
            LyricTrace.note("AMLL: no TTML file for NetEase song \(song.id)")
            return nil
        }
        let found = candidate(ttml: text, song: song, query: query)
        LyricTrace.candidate(found, query: query, message: "AMLL file for NetEase song \(song.id)")
        return found
    }

    /// Kept apart from the network so tests can feed it a file.
    static func candidate(ttml: String, song: NetEaseLyricsProvider.MatchedSong, query: LyricQuery) -> LyricCandidate? {
        let lines = TTMLLyricParser.parse(ttml, duration: query.duration)
        guard lines.contains(where: { !$0.isInstrumental }) else { return nil }
        let provenance = LyricsFetcher.Provenance(precision: LyricsFetcher.precision(lines), source: "AMLL TTML DB")
        let native = lines.contains { $0.wordTimed }
        return LyricCandidate(source: .amll, result: .synced(lines, provenance), title: song.title, artist: song.artist,
                              duration: song.duration, score: native ? 112 : 94, metadataReported: true)
    }
}
