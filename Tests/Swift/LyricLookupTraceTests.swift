import XCTest
@testable import VersoCustom

/// A live, diagnostic run of the real lyric lookup over a list of songs,
/// writing one JSON trace per song and a `summary.md` table. Real network;
/// skipped unless asked for, so the normal suite stays offline.
///
///     TEST_RUNNER_LUNALYR_LOOKUP_TRACE=/path/songs.json \
///     TEST_RUNNER_LUNALYR_LOOKUP_TRACE_OUT=/path/out \
///     xcodebuild test … -only-testing:VersoCustomTests/LyricLookupTraceTests
///
/// xcodebuild hands `TEST_RUNNER_`-prefixed variables to the tests without
/// the prefix. `songs.json`: `[{"title", "artist", "album"?, "duration", "player"?, "genre"?}]`.
/// Optional: `…_ALL_SOURCES=1` runs each as a search (every source asked,
/// none stopped early); `…_STOREFRONT=xx` sets the iTunes storefront the
/// original-title lookup starts from (default: this Mac's region);
/// `…_SKIP=lyricsPlus,amll` leaves those sources out (to see the lookup
/// when one is slow or down).
/// See docs/LOOKUP-TRACE.md.
final class LyricLookupTraceTests: XCTestCase {
    private func setting(_ name: String) -> String? {
        let environment = ProcessInfo.processInfo.environment
        let value = environment[name] ?? environment["TEST_RUNNER_" + name]
        return value?.isEmpty == false ? value : nil
    }

    func testTraceSongs() async throws {
        guard let list = setting("LUNALYR_LOOKUP_TRACE") else {
            throw XCTSkip("Set TEST_RUNNER_LUNALYR_LOOKUP_TRACE to a songs.json to run the live lookup trace.")
        }
        let songsFile = URL(fileURLWithPath: (list as NSString).expandingTildeInPath)
        let output = setting("LUNALYR_LOOKUP_TRACE_OUT").map { URL(fileURLWithPath: ($0 as NSString).expandingTildeInPath, isDirectory: true) }
            ?? songsFile.deletingLastPathComponent()
        let songs = try JSONDecoder().decode([LyricLookupTraceDocument.Song].self, from: Data(contentsOf: songsFile))
        try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
        let everySource = setting("LUNALYR_LOOKUP_TRACE_ALL_SOURCES") == "1"

        // A cache, aliases and original titles of this run's own, in the
        // throwaway home: nothing pinned or remembered, the app's untouched.
        let scratch = LunalyrTestEnvironment.home.appendingPathComponent("LookupTrace-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: scratch, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: scratch) }
        let storefront = setting("LUNALYR_LOOKUP_TRACE_STOREFRONT") ?? Locale.current.region?.identifier.lowercased() ?? "us"
        let aliases = ArtistAliases(folder: scratch)
        let titles = OriginalTitles(folder: scratch, storefront: storefront)
        // The app's default sources, in its recommended order, Accuracy first.
        let skipped = Set((setting("LUNALYR_LOOKUP_TRACE_SKIP") ?? "").split(separator: ",").map {
            $0.trimmingCharacters(in: .whitespaces) })
        let defaults = LyricSourceID.allCases.filter { $0 != .lyrimuse && $0 != .appleMusicCache && !skipped.contains($0.rawValue) }
        let options = LyricSourceOptions.Snapshot(enabled: LyricSourceOptions.recommendedOrder.filter(defaults.contains),
                                                  mode: .smart, localFolder: "", localVersion: "")

        var rows: [String] = []
        for (index, song) in songs.enumerated() {
            let recorder = LyricLookupTraceRecorder()
            let fetcher = LyricsFetcher(cacheFolder: scratch.appendingPathComponent("Lyrics-\(index)", isDirectory: true),
                                        misses: LyricMissCache(), isOnline: { true }, fetch: { id, query, folder in
                try await LyricsFetcher.fetch(id, query: query, localFolder: folder, aliases: aliases, titles: titles, otherNames: true)
            }, tracer: recorder)
            var query = LyricQuery(title: song.title, artist: song.artist, album: song.album, duration: song.duration)
            query.genre = song.genre
            let started = Date()
            _ = await fetcher.lookup(query, force: everySource, options: options)
            let document = LyricLookupTraceDocument(song: song, storefront: storefront, events: recorder.events, startedAt: started)
            let name = String(format: "%02d-", index + 1) + Self.slug(song.title + " - " + song.artist) + ".json"
            try JSONEncoder.trace.encode(document).write(to: output.appendingPathComponent(name), options: .atomic)
            rows.append(Self.row(document, file: name))
        }
        let header = "| # | Song | " + options.enabled.map(\.title).joined(separator: " | ") + " | Final pick | ms |\n"
            + "|---|---|" + options.enabled.map { _ in "---" }.joined(separator: "|") + "|---|---|\n"
        let numbered = rows.enumerated().map { "| \($0 + 1) " + $1 }.joined(separator: "\n")
        let summary = "# Lunalyr lookup trace\n\n"
            + "\(songs.count) songs from `\(songsFile.lastPathComponent)`, \(ISO8601DateFormatter().string(from: Date())). "
            + "Mode: Accuracy first (smart), \(everySource ? "search (every source asked)" : "automatic (sources stopped once they cannot change the pick)"), "
            + "iTunes storefront `\(storefront)`, empty cache, nothing pinned.\n\n"
            + "Cells: **PICK** shown, `#n` accepted at rank n, `rej …` failed the recording check, `none` no match, "
            + "`stopped` not needed, `timeout`, `error: …`; then the timing tier (word, mixed, line, phrase-timed, plain, faulty).\n\n"
            + header + numbered + "\n"
        try Data(summary.utf8).write(to: output.appendingPathComponent("summary.md"), options: .atomic)
        print("Lookup trace written to \(output.path)")
    }

    private static func row(_ document: LyricLookupTraceDocument, file: String) -> String {
        let song = "[\(escape(document.song.title)) — \(escape(document.song.artist))](\(file.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? file))"
        let cells = document.digest.map { escape(LyricLookupTraceDocument.cell($0)) }
        let pick = document.pick.map { "\($0.source) · \($0.timing)" + ($0.viaOriginalTitle ? " (orig. title)" : "") } ?? "**nothing**"
        return "| " + ([song] + cells + [pick, document.elapsedMs.map(String.init) ?? ""]).joined(separator: " | ") + " |"
    }

    private static func escape(_ text: String) -> String {
        text.replacingOccurrences(of: "|", with: "\\|").replacingOccurrences(of: "\n", with: " ")
    }

    private static func slug(_ text: String) -> String {
        let kept = text.unicodeScalars.map { CharacterSet.alphanumerics.contains($0) ? Character($0) : "-" }
        let joined = String(kept).split(separator: "-").joined(separator: "-")
        return String(joined.prefix(60))
    }
}
