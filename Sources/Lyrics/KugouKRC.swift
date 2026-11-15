import Compression
import Foundation

/// Kugou's KRC lyric file: a four-byte "krc1" header, then zlib data XOR-ed
/// with a fixed 16-byte key. Its lines read `[startMs,durationMs]` followed by
/// `<offsetMs,durationMs,0>word` pieces. This turns one into enhanced LRC
/// (`[mm:ss.xx]<mm:ss.xx>word…<mm:ss.xx>`), which `LyricsFetcher.parseLRC`
/// already reads as word-timed lines.
enum KugouKRC {
    private static let key: [UInt8] = [0x40, 0x47, 0x61, 0x77, 0x5E, 0x32, 0x74, 0x47, 0x51, 0x36, 0x31, 0x2D, 0xCE, 0xD2, 0x6E, 0x69]

    static func decode(_ data: Data) -> String? {
        guard data.count > 6, data.count <= 2_000_000, data.prefix(4) == Data("krc1".utf8) else { return nil }
        var bytes = [UInt8](data.dropFirst(4))
        for index in bytes.indices { bytes[index] ^= key[index % key.count] }
        guard let raw = EnhancedLRC.inflate(bytes) else { return nil }
        return String(data: raw, encoding: .utf8)
    }

    static func enhancedLRC(from data: Data) -> String? {
        guard let text = decode(data) else { return nil }
        return enhancedLRC(fromText: text)
    }

    static func enhancedLRC(fromText text: String) -> String? {
        // A word may start a little before its line: `<-11,116,0>`.
        guard let lineHead = try? NSRegularExpression(pattern: #"^\[(\d+),(\d+)\]"#),
              let piece = try? NSRegularExpression(pattern: #"<(-?\d+),(\d+),-?\d+>"#) else { return nil }
        var out: [String] = []
        for raw in text.components(separatedBy: .newlines) {
            let line = raw as NSString
            guard let head = lineHead.firstMatch(in: raw, range: NSRange(location: 0, length: line.length)),
                  let start = Double(line.substring(with: head.range(at: 1))),
                  let length = Double(line.substring(with: head.range(at: 2))) else { continue }
            let body = line.substring(from: head.range.upperBound) as NSString
            let marks = piece.matches(in: body as String, range: NSRange(location: 0, length: body.length))
            if marks.isEmpty {
                let words = body.trimmingCharacters(in: .whitespaces)
                guard !words.isEmpty else { continue }
                out.append(stamp("[", start / 1000, "]") + words)
                continue
            }
            // Offsets are from the line's start; the word follows its stamp.
            let words = marks.enumerated().map { index, mark -> EnhancedLRC.Word in
                let textStart = mark.range.upperBound
                let textEnd = index + 1 < marks.count ? marks[index + 1].range.location : body.length
                return .init(text: body.substring(with: NSRange(location: textStart, length: max(0, textEnd - textStart))),
                             start: start + (Double(body.substring(with: mark.range(at: 1))) ?? 0),
                             length: Double(body.substring(with: mark.range(at: 2))) ?? 0)
            }
            if let converted = EnhancedLRC.line(start: start, length: length, words: words) { out.append(converted) }
        }
        return out.isEmpty ? nil : out.joined(separator: "\n")
    }

    private static func stamp(_ open: String, _ seconds: Double, _ close: String) -> String {
        open + clock(seconds) + close
    }
    /// An LRC time, "mm:ss.xxx", from whole milliseconds. Rounding the
    /// seconds on their own wrote 59.996 s as "00:60.00", which no LRC reader
    /// accepts: the line was lost, or a word stamp was shown as text.
    static func clock(_ seconds: Double) -> String {
        // At most 999 minutes, the longest an LRC stamp can say.
        let ms = Int((min(max(0, seconds.isFinite ? seconds : 0), 59_999) * 1000).rounded())
        return String(format: "%02d:%02d.%03d", ms / 60_000, ms / 1000 % 60, ms % 1000)
    }
}
