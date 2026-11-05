import Combine
import CryptoKit
import Foundation

@MainActor
final class LocalLyricOptions: ObservableObject {
    static let shared = LocalLyricOptions()
    private let defaults: UserDefaults
    let defaultFolder: String
    @Published var folder: String {
        didSet { defaults.set(folder, forKey: "custom.lyrimuseFolder") }
    }
    /// Which folder is configured. Its contents are fingerprinted per track
    /// (`LocalLyricBridge.fingerprint`), so exports for other songs change nothing.
    var fingerprint: String { folder.isEmpty ? "unconfigured" : folder }
    init(defaults: UserDefaults = .standard, homeDirectory: URL = FileManager.default.homeDirectoryForCurrentUser) {
        self.defaults = defaults
        defaultFolder = homeDirectory
            .appendingPathComponent("Library/Application Support/me.yudaotor.lyrimuse/lyrics", isDirectory: true).path
        folder = defaults.string(forKey: "custom.lyrimuseFolder") ?? ""
    }

    /// Keep the current folder if Lyrimuse has not created a readable directory.
    func selectDefaultFolder() -> Bool {
        let files = FileManager.default
        var isDirectory: ObjCBool = false
        guard files.fileExists(atPath: defaultFolder, isDirectory: &isDirectory),
              isDirectory.boolValue, files.isReadableFile(atPath: defaultFolder) else { return false }
        if folder != defaultFolder { folder = defaultFolder }
        return true
    }
}

/// Read-only interoperability with exported files. No Lyrimuse code is linked,
/// installed or launched, and its credentials and internal caches are untouched.
enum LocalLyricBridge {
    static func candidate(_ query: LyricQuery, folder: String) throws -> LyricCandidate? {
        guard !folder.isEmpty else { throw LyricHTTP.Failure(message: "Choose the Lyrimuse lyrics folder below") }
        let root = URL(fileURLWithPath: folder, isDirectory: true)
        let all = try FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: nil, options: .skipsHiddenFiles)
        guard all.count <= 30000 else { throw LyricHTTP.Failure(message: "Choose a lyrics folder with fewer than 30,000 files") }
        let files = all.filter {
            $0.pathExtension.lowercased() == "lrc" && !$0.lastPathComponent.hasSuffix(".tr.lrc") &&
            !$0.lastPathComponent.hasSuffix(".roma.lrc") && mayBelong($0, to: query)
        }.sorted { $0.lastPathComponent < $1.lastPathComponent }
        var best: LyricCandidate?
        for file in files.prefix(150) {
            try Task.checkCancellation()
            // A track without an artist tag is matched on title and [length:].
            guard let text = read(file), let title = tag("ti", in: text) else { continue }
            let artist = tag("ar", in: text)
            guard artist != nil || !query.hasArtist else { continue }
            let titleMatch = LyricQuery.similarity(query.title, title), artistMatch = query.artistScore(artist ?? "")
            guard titleMatch >= 0.92, artistMatch >= (query.hasArtist ? 0.85 : 0) else { continue }
            let length = Self.length(tag("length", in: text))
            let timed = length != nil && query.duration > 0
            let album = tag("al", in: text)
            if let expected = query.album, !expected.isEmpty, let album, !album.isEmpty,
               LyricQuery.similarity(expected, album) < 0.80 { continue }
            let base = file.deletingPathExtension()
            var lines = LyricsFetcher.parseLRC(text, trackDuration: query.duration)
            if let ttml = read(base.appendingPathExtension("ttml")) {
                let native = TTMLLyricParser.parse(ttml, duration: query.duration)
                if native.contains(where: LyricsFetcher.validWordTiming) { lines = supplement(native, with: lines) }
            } else if let yrc = read(base.appendingPathExtension("yrc")) {
                let native = NetEaseLyricsProvider.parseYRC(yrc)
                if !native.isEmpty { lines = supplement(native, with: lines) }
            }
            guard !lines.isEmpty, query.duration <= 0 || (lines.last?.start ?? 0) <= query.duration + 8 else { continue }
            if let saved = read(base.appendingPathExtension("roma.lrc")) {
                lines = attachingReadings(saved, to: lines, duration: query.duration)
            }
            if let translated = read(base.appendingPathExtension("tr.lrc")) {
                lines = attachingTranslations(translated, to: lines, duration: query.duration)
            }
            let native = lines.contains { $0.wordTimed }
            let score = vc_match_score(titleMatch, artistMatch, length.map { abs($0 - query.duration) } ?? 0, timed ? 1 : 0, native ? 1 : 0, 1,
                                       album != nil && LyricQuery.normalize(album ?? "") == LyricQuery.normalize(query.album ?? "") ? 1 : 0) + 32
            let provider = tag("source", in: text).map { " · " + $0 } ?? ""
            let match = LyricCandidate(source: .lyrimuse, result: .synced(lines,
                .init(precision: LyricsFetcher.precision(lines), source: "Lyrimuse local files" + provider)),
                title: title, artist: artist ?? "", duration: length, score: score)
            if best == nil || score > best!.score { best = match }
        }
        return best
    }
    /// Sidecars can contain just the lines for which the provider has word
    /// timing. Keep the LRC-only lines when both files share the same timeline.
    private static func supplement(_ native: [LyricsFetcher.Line], with lrc: [LyricsFetcher.Line]) -> [LyricsFetcher.Line] {
        let sidecar = Companion(native)
        func lyricKey(_ line: LyricsFetcher.Line) -> String {
            let key = LyricQuery.normalize(line.text)
            return key.isEmpty ? line.text : key
        }
        let keys = sidecar.rows.map(lyricKey)
        var paired: [Int: Int] = [:]
        var used = Set<Int>()
        for tolerance in [0.18, 1.0] {
            for (index, line) in lrc.enumerated() where !line.isInstrumental && paired[index] == nil {
                let key = lyricKey(line)
                if let row = sidecar.matchingIndex(at: line.start, within: tolerance, where: {
                    !used.contains($0) && !sidecar.rows[$0].isInstrumental && keys[$0] == key
                }) {
                    paired[index] = row
                    used.insert(row)
                }
            }
        }
        guard !paired.isEmpty else { return native }

        var result = sidecar.rows
        for (index, line) in lrc.enumerated() {
            let nearby = sidecar.index(at: line.start)
            if let row = paired[index] {
                if result[row].romanization == nil { result[row].romanization = line.romanization }
                if result[row].translation == nil { result[row].translation = line.translation }
            } else if let row = nearby, !used.contains(row) {
                // A sidecar may include backing vocals in a line that has
                // the same onset as a shorter LRC version.
                if result[row].isInstrumental && !line.isInstrumental { result[row] = line }
                used.insert(row)
            } else if !line.isInstrumental || nearby == nil {
                result.append(line)
            }
        }
        result.sort { $0.start < $1.start }
        for index in result.indices.dropLast() where result[index].endFromNextLine == true {
            let nextStart = result[index + 1].start
            guard nextStart > result[index].start else { continue }
            if result[index].wordTimed {
                result[index].end = max(nextStart, result[index].words.lazy.map(\.end).max() ?? nextStart)
            } else {
                result[index].end = nextStart
                if !result[index].isInstrumental {
                    result[index].words = LyricsFetcher.distributeWords(in: result[index].text,
                        from: result[index].start, to: nextStart)
                }
            }
        }
        return result
    }

    /// Names, sizes and modification times of the files that could hold this
    /// track's lyrics, sidecars included. Exports for other songs leave it
    /// unchanged; editing a matching file in place changes it.
    static func fingerprint(_ query: LyricQuery, folder: String) -> String {
        guard !folder.isEmpty else { return "unconfigured" }
        let keys: [URLResourceKey] = [.contentModificationDateKey, .fileSizeKey]
        guard let all = try? FileManager.default.contentsOfDirectory(at: URL(fileURLWithPath: folder, isDirectory: true),
                                                                     includingPropertiesForKeys: keys, options: .skipsHiddenFiles)
        else { return "unreadable" }
        guard all.count <= 30000 else { return "too many files" }
        let rows = all.filter { mayBelong($0, to: query) }.map { file -> String in
            let values = try? file.resourceValues(forKeys: Set(keys))
            return [file.lastPathComponent, String(values?.fileSize ?? -1),
                    String(values?.contentModificationDate?.timeIntervalSince1970 ?? 0)].joined(separator: "|")
        }.sorted()
        return SHA256.hash(data: Data(rows.joined(separator: "\n").utf8)).map { String(format: "%02x", $0) }.joined()
    }
    /// Whether a file's name could be this track's: it contains the title,
    /// without edition qualifiers. Titles made only of symbols match as written.
    private static func mayBelong(_ file: URL, to query: LyricQuery) -> Bool {
        let name = file.deletingPathExtension().lastPathComponent
        let core = LyricQuery.core(query.title), key = core.isEmpty ? LyricQuery.normalize(query.title) : core
        if !key.isEmpty { return LyricQuery.normalize(name).contains(key) }
        let raw = query.title.trimmingCharacters(in: .whitespacesAndNewlines)
        return !raw.isEmpty && name.localizedCaseInsensitiveContains(raw)
    }
    /// A file's [length:] in seconds: "03:41", "3:41.50" or "221"; a bare
    /// number too large to be seconds is read as milliseconds. Nil for a
    /// value that cannot be a song's length, so it is simply not used.
    static func length(_ value: String?) -> Double? {
        guard let value, var seconds = TTMLLyricParser.seconds(value) else { return nil }
        if seconds > 10_800, !value.contains(":") { seconds /= 1000 }
        return (1...10_800).contains(seconds) ? seconds : nil
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
    /// A reading file (`.roma.lrc`, or one imported on Romanisation), timed
    /// like the lyrics. A line takes the row stamped nearest it, within
    /// 0.18 s, unless that row is an instrumental break.
    static func attachingReadings(_ text: String, to lines: [LyricsFetcher.Line], duration: Double) -> [LyricsFetcher.Line] {
        let readings = Companion(LyricsFetcher.parseLRC(text, trackDuration: duration))
        return lines.map { line in
            var result = line
            if let reading = readings.row(at: line.start), !reading.isInstrumental { result.romanization = reading.text }
            return result
        }
    }
    /// Lyrimuse's translation file (`.tr.lrc`), timed like the lyrics. A line
    /// takes the row stamped nearest it, within 0.18 s, unless it already has
    /// a translation (a bilingual file's own), the row is blank, or the row
    /// only repeats the line.
    static func attachingTranslations(_ text: String, to lines: [LyricsFetcher.Line], duration: Double) -> [LyricsFetcher.Line] {
        let translations = Companion(LyricsFetcher.parseLRC(text, trackDuration: duration))
        guard !translations.rows.isEmpty else { return lines }
        return lines.map { line in
            var result = line
            guard line.translation == nil, !line.isInstrumental,
                  let row = translations.row(at: line.start), !row.isInstrumental else { return result }
            let translation = row.text.trimmingCharacters(in: .whitespacesAndNewlines)
            if !translation.isEmpty, translation != line.text.trimmingCharacters(in: .whitespacesAndNewlines) {
                result.translation = translation
            }
            return result
        }
    }
    /// The rows of a companion file, sorted once by time, so each lyric line
    /// finds its row by binary search: a file of many thousands of rows costs
    /// a moment rather than minutes on every lookup and folder change.
    struct Companion {
        let rows: [LyricsFetcher.Line]
        init(_ rows: [LyricsFetcher.Line]) {
            // In file order where two rows share a time, as a scan would find them.
            self.rows = rows.enumerated().sorted { ($0.element.start, $0.offset) < ($1.element.start, $1.offset) }.map(\.element)
        }
        /// The row stamped nearest `time`, within 0.18 s; the earlier row on a
        /// tie, and the first of rows that share a time.
        func row(at time: Double) -> LyricsFetcher.Line? {
            index(at: time).map { rows[$0] }
        }

        func index(at time: Double) -> Int? {
            let after = first(atOrAfter: time)
            var nearest = after > 0 ? first(atOrAfter: rows[after - 1].start) : nil
            if after < rows.count, nearest.map({ abs(rows[after].start - time) < abs(rows[$0].start - time) }) ?? true {
                nearest = after
            }
            guard let nearest, abs(rows[nearest].start - time) <= 0.18 else { return nil }
            return nearest
        }
        func matchingIndex(at time: Double, within tolerance: Double, where matches: (Int) -> Bool) -> Int? {
            var index = first(atOrAfter: time - tolerance)
            var nearest: Int?
            var distance = tolerance
            while index < rows.count, rows[index].start <= time + tolerance {
                let offset = abs(rows[index].start - time)
                if offset <= distance && (nearest == nil || offset < distance) && matches(index) {
                    nearest = index
                    distance = offset
                    if distance == 0 { break }
                }
                index += 1
            }
            return nearest
        }

        /// The index of the first row stamped at or after `time`.
        private func first(atOrAfter time: Double) -> Int {
            var low = 0, high = rows.count
            while low < high {
                let middle = (low + high) / 2
                if rows[middle].start < time { low = middle + 1 } else { high = middle }
            }
            return low
        }
    }
    private static func read(_ url: URL) -> String? {
        guard let attrs = try? FileManager.default.attributesOfItem(atPath: url.path),
              let size = attrs[.size] as? NSNumber, size.intValue <= 2_000_000,
              let data = try? Data(contentsOf: url), data.count <= 2_000_000 else { return nil }
        return LyricTextDecoder.decode(data)
    }
}
