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
        // zlib = a two-byte header, raw deflate, then a checksum.
        guard bytes.count > 6, bytes[0] & 0x0F == 8 else { return nil }
        let deflated = Data(bytes.dropFirst(2))
        guard let raw = try? (deflated as NSData).decompressed(using: .zlib) as Data else { return nil }
        return String(data: raw, encoding: .utf8)
    }

    static func enhancedLRC(from data: Data) -> String? {
        guard let text = decode(data) else { return nil }
        return enhancedLRC(fromText: text)
    }

    static func enhancedLRC(fromText text: String) -> String? {
        guard let lineHead = try? NSRegularExpression(pattern: #"^\[(\d+),(\d+)\]"#),
              let piece = try? NSRegularExpression(pattern: #"<(\d+),(\d+),\d+>"#) else { return nil }
        var out: [String] = []
        for raw in text.components(separatedBy: .newlines) {
            let line = raw as NSString
            guard let head = lineHead.firstMatch(in: raw, range: NSRange(location: 0, length: line.length)),
                  let start = Double(line.substring(with: head.range(at: 1))),
                  let length = Double(line.substring(with: head.range(at: 2))) else { continue }
            let body = line.substring(from: head.range.upperBound) as NSString
            let marks = piece.matches(in: body as String, range: NSRange(location: 0, length: body.length))
            var result = stamp("[", start / 1000, "]")
            if marks.isEmpty {
                let words = body.trimmingCharacters(in: .whitespaces)
                guard !words.isEmpty else { continue }
                out.append(result + words)
                continue
            }
            var lastEnd = start / 1000
            for (index, mark) in marks.enumerated() {
                let offset = Double(body.substring(with: mark.range(at: 1))) ?? 0
                let span = Double(body.substring(with: mark.range(at: 2))) ?? 0
                let textStart = mark.range.upperBound
                let textEnd = index + 1 < marks.count ? marks[index + 1].range.location : body.length
                let word = body.substring(with: NSRange(location: textStart, length: max(0, textEnd - textStart)))
                let at = (start + offset) / 1000
                result += stamp("<", at, ">") + word
                lastEnd = max(lastEnd, (start + offset + span) / 1000)
            }
            result += stamp("<", max(lastEnd, (start + length) / 1000), ">")
            out.append(result)
        }
        return out.isEmpty ? nil : out.joined(separator: "\n")
    }

    private static func stamp(_ open: String, _ seconds: Double, _ close: String) -> String {
        let clamped = max(0, seconds)
        return open + String(format: "%02d:%05.2f", Int(clamped / 60), clamped.truncatingRemainder(dividingBy: 60)) + close
    }
}
