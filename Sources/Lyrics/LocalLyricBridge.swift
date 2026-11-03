import Combine
import Foundation

@MainActor
final class LocalLyricOptions: ObservableObject {
    static let shared = LocalLyricOptions()
    @Published var folder: String {
        didSet { UserDefaults.standard.set(folder, forKey: "custom.lyrimuseFolder") }
    }
    var defaultFolder: String {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/me.yudaotor.lyrimuse/lyrics", isDirectory: true).path
    }
    var fingerprint: String {
        guard !folder.isEmpty else { return "unconfigured" }
        let date = (try? FileManager.default.attributesOfItem(atPath: folder)[.modificationDate]) as? Date
        return folder + "|" + String(date?.timeIntervalSince1970 ?? 0)
    }
    private init() { folder = UserDefaults.standard.string(forKey: "custom.lyrimuseFolder") ?? "" }
}

/// Read-only interoperability with exported files. No Lyrimuse code is linked,
/// installed or launched, and its credentials and internal caches are untouched.
enum LocalLyricBridge {
    static func candidate(_ query: LyricQuery, folder: String) throws -> LyricCandidate? {
        guard !folder.isEmpty else { throw LyricHTTP.Failure(message: "Choose the Lyrimuse lyrics folder below") }
        let root = URL(fileURLWithPath: folder, isDirectory: true)
        let all = try FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: nil, options: .skipsHiddenFiles)
        guard all.count <= 30000 else { throw LyricHTTP.Failure(message: "Choose a lyrics folder with fewer than 30,000 files") }
        let titleKey = LyricQuery.normalize(query.title)
        let files = all.filter {
            $0.pathExtension.lowercased() == "lrc" && !$0.lastPathComponent.hasSuffix(".tr.lrc") &&
            !$0.lastPathComponent.hasSuffix(".roma.lrc") &&
            LyricQuery.normalize($0.deletingPathExtension().lastPathComponent).contains(titleKey)
        }.sorted { $0.lastPathComponent < $1.lastPathComponent }
        var best: LyricCandidate?
        for file in files.prefix(150) {
            try Task.checkCancellation()
            guard let text = read(file), let title = tag("ti", in: text), let artist = tag("ar", in: text) else { continue }
            let titleMatch = LyricQuery.similarity(query.title, title), artistMatch = LyricQuery.similarity(query.artist, artist)
            guard titleMatch >= 0.92, artistMatch >= 0.85 else { continue }
            let album = tag("al", in: text)
            if let expected = query.album, !expected.isEmpty, let album, !album.isEmpty,
               LyricQuery.similarity(expected, album) < 0.80 { continue }
            let base = file.deletingPathExtension()
            var lines = LyricsFetcher.parseLRC(text, trackDuration: query.duration)
            if let ttml = read(base.appendingPathExtension("ttml")) {
                let native = TTMLLyricParser.parse(ttml, duration: query.duration)
                if !native.isEmpty, native.contains(where: { $0.wordTimed }) { lines = native }
            } else if let yrc = read(base.appendingPathExtension("yrc")) {
                let native = NetEaseLyricsProvider.parseYRC(yrc)
                if !native.isEmpty { lines = native }
            }
            guard !lines.isEmpty, query.duration <= 0 || (lines.last?.start ?? 0) <= query.duration + 8 else { continue }
            if let saved = read(base.appendingPathExtension("roma.lrc")) {
                lines = attachingReadings(saved, to: lines, duration: query.duration)
            }
            let native = lines.contains { $0.wordTimed }
            let score = vc_match_score(titleMatch, artistMatch, 0, 0, native ? 1 : 0, 1,
                                       album != nil && LyricQuery.normalize(album ?? "") == LyricQuery.normalize(query.album ?? "") ? 1 : 0) + 32
            let provider = tag("source", in: text).map { " · " + $0 } ?? ""
            let match = LyricCandidate(source: .lyrimuse, result: .synced(lines,
                .init(precision: LyricsFetcher.precision(lines), source: "Lyrimuse local files" + provider)),
                title: title, artist: artist, duration: nil, score: score)
            if best == nil || score > best!.score { best = match }
        }
        return best
    }
    static func tag(_ key: String, in text: String) -> String? {
        let prefix = "[" + key.lowercased() + ":"
        for raw in text.prefix(12000).split(whereSeparator: \.isNewline) {
            let line = raw.trimmingCharacters(in: .whitespacesAndNewlines)
            if line.lowercased().hasPrefix(prefix), let close = line.firstIndex(of: "]") {
                let start = line.index(line.startIndex, offsetBy: prefix.count)
                let value = String(line[start..<close]).trimmingCharacters(in: .whitespacesAndNewlines)
                return value.isEmpty ? nil : value
            }
        }
        return nil
    }
    static func attachingReadings(_ text: String, to lines: [LyricsFetcher.Line], duration: Double) -> [LyricsFetcher.Line] {
        let readings = LyricsFetcher.parseLRC(text, trackDuration: duration)
        return lines.map { line in
            var result = line
            if let reading = readings.min(by: { abs($0.start - line.start) < abs($1.start - line.start) }),
               abs(reading.start - line.start) <= 0.18, !reading.isInstrumental { result.romanization = reading.text }
            return result
        }
    }
    private static func read(_ url: URL) -> String? {
        guard let attrs = try? FileManager.default.attributesOfItem(atPath: url.path),
              let size = attrs[.size] as? NSNumber, size.intValue <= 2_000_000,
              let data = try? Data(contentsOf: url), data.count <= 2_000_000 else { return nil }
        return String(data: data, encoding: .utf8) ?? String(data: data, encoding: .utf16)
    }
}
