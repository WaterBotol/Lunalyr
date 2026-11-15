import Compression
import Foundation

/// Writes word-timed lines as enhanced LRC (`[mm:ss.xxx]<mm:ss.xxx>word…<mm:ss.xxx>`),
/// the form `LyricsFetcher.parseLRC` reads as word-timed. Kugou KRC, QQ QRC
/// and Kuwo lrcx all come through here.
enum EnhancedLRC {
    struct Word {
        var text: String
        /// Absolute start and length, in milliseconds.
        var start: Double
        var length: Double
    }

    /// One line. `parseLRC` drops a line's word timing when text precedes the
    /// first word stamp, a word starts before the line, or starts run
    /// backwards; starts are clamped so none of that happens. A line whose
    /// words are all blank is dropped (nil).
    static func line(start: Double, length: Double?, words: [Word]) -> String? {
        guard words.contains(where: { !$0.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }) else { return nil }
        let lineStart = max(0, start)
        var result = "[" + KugouKRC.clock(lineStart / 1000) + "]"
        var previous = lineStart, lastEnd = lineStart
        for word in words {
            let at = max(previous, word.start.isFinite ? word.start : previous)
            result += "<" + KugouKRC.clock(at / 1000) + ">" + word.text
            previous = at
            lastEnd = max(lastEnd, at + max(0, word.length.isFinite ? word.length : 0))
        }
        if let length, length.isFinite { lastEnd = max(lastEnd, lineStart + length) }
        return result + "<" + KugouKRC.clock(lastEnd / 1000) + ">"
    }

    /// A zlib stream (two-byte header, raw deflate, checksum) inflated.
    static func inflate(_ bytes: some Collection<UInt8>) -> Data? {
        guard bytes.count > 6, let first = bytes.first, first & 0x0F == 8 else { return nil }
        let deflated = Data(bytes.dropFirst(2))
        return try? (deflated as NSData).decompressed(using: .zlib) as Data
    }
}

/// QQ Music's QRC word-timed lyrics, from `lyric_download.fcg`
/// (contract reference: Yudaotor/lyrimuse, lyrimuse-engine/qq.go and
/// qqfallback.go). The `<content>` field is hex text, triple-DES encrypted
/// (`QQMusicTripleDES`) over zlib data. Inside is XML whose `LyricContent`
/// attribute holds lines `[startMs,lengthMs]word(startMs,lengthMs)…`: each
/// word comes *before* its stamp, and the stamps are absolute.
enum QQMusicQRC {
    /// What one `lyric_download.fcg` reply holds: word-timed lyrics converted
    /// to enhanced LRC, and/or plain line LRC (a song with no QRC is sent as
    /// line LRC through the same field).
    struct Download: Equatable {
        var enhanced: String?
        var line: String?
    }

    static func decrypt(hex: String) -> String? {
        let digits = Array(hex.trimmingCharacters(in: .whitespacesAndNewlines).utf8)
        guard !digits.isEmpty, digits.count <= 4_000_000, digits.count % 16 == 0 else { return nil }
        var bytes = [UInt8](); bytes.reserveCapacity(digits.count / 2)
        func nibble(_ c: UInt8) -> UInt8? {
            switch c {
            case 48...57: return c - 48
            case 65...70: return c - 55
            case 97...102: return c - 87
            default: return nil
            }
        }
        var index = 0
        while index < digits.count {
            guard let high = nibble(digits[index]), let low = nibble(digits[index + 1]) else { return nil }
            bytes.append(high << 4 | low)
            index += 2
        }
        guard let plain = QQMusicTripleDES.decrypt(bytes), let raw = EnhancedLRC.inflate(plain),
              raw.count <= 4_000_000 else { return nil }
        return String(data: raw, encoding: .utf8)
    }

    /// The first track's `LyricContent`. QQ leaves literal quotes inside it
    /// unescaped, so this cuts at the next `<Lyric_` and then matches up to
    /// the last `"/>` rather than using an XML parser.
    static func lyricContent(_ xml: String) -> String? {
        guard let start = xml.range(of: "LyricContent=\"") else { return nil }
        var segment = xml[start.upperBound...]
        if let next = segment.range(of: "<Lyric_") { segment = segment[..<next.lowerBound] }
        guard let close = try? NSRegularExpression(pattern: #""\s*/>"#) else { return nil }
        let text = String(segment) as NSString
        guard let last = close.matches(in: text as String, range: NSRange(location: 0, length: text.length)).last else { return nil }
        return LyricHTTP.text(text.substring(to: last.range.location))
    }

    static func enhancedLRC(fromQRC text: String) -> String? {
        guard let head = try? NSRegularExpression(pattern: #"^\[(\d+),(\d+)\]"#),
              let stamp = try? NSRegularExpression(pattern: #"\((\d+),(\d+)\)"#) else { return nil }
        var out: [String] = []
        for raw in text.components(separatedBy: .newlines) {
            let row = raw.trimmingCharacters(in: .whitespaces)
            if row.lowercased().hasPrefix("[offset:") { out.append(row); continue }
            let line = row as NSString
            guard let match = head.firstMatch(in: row, range: NSRange(location: 0, length: line.length)),
                  let start = Double(line.substring(with: match.range(at: 1))),
                  let length = Double(line.substring(with: match.range(at: 2))) else { continue }
            let body = line.substring(from: match.range.upperBound) as NSString
            let marks = stamp.matches(in: body as String, range: NSRange(location: 0, length: body.length))
            if marks.isEmpty {
                let words = body.trimmingCharacters(in: .whitespaces)
                if !words.isEmpty { out.append("[" + KugouKRC.clock(start / 1000) + "]" + words) }
                continue
            }
            var words: [EnhancedLRC.Word] = []
            var textStart = 0
            for mark in marks {
                let word = body.substring(with: NSRange(location: textStart, length: mark.range.location - textStart))
                words.append(.init(text: word, start: Double(body.substring(with: mark.range(at: 1))) ?? start,
                                   length: Double(body.substring(with: mark.range(at: 2))) ?? 0))
                textStart = NSMaxRange(mark.range)
            }
            // Text after the last stamp is not in well-formed QRC; keep it with the last word.
            if textStart < body.length, !words.isEmpty { words[words.count - 1].text += body.substring(from: textStart) }
            if let converted = EnhancedLRC.line(start: start, length: length, words: words) { out.append(converted) }
        }
        return out.contains(where: { !$0.lowercased().hasPrefix("[offset:") }) ? out.joined(separator: "\n") : nil
    }

    /// Reads a `lyric_download.fcg` reply (XML-like text wrapped in a comment).
    static func download(_ body: String) -> Download? {
        guard let regex = try? NSRegularExpression(pattern: #"(?s)<content(?:\s[^>]*)?><!\[CDATA\[(.*?)\]\]></content>"#) else { return nil }
        let ns = body as NSString
        guard let match = regex.firstMatch(in: body, range: NSRange(location: 0, length: ns.length)) else { return nil }
        let field = ns.substring(with: match.range(at: 1))
        guard let decrypted = decrypt(hex: field) else { return nil }
        if let content = lyricContent(decrypted) {
            return Download(enhanced: enhancedLRC(fromQRC: content), line: nil)
        }
        // No QRC for this song: the field is line LRC.
        return decrypted.contains("[") ? Download(enhanced: nil, line: decrypted) : nil
    }
}

/// Kuwo's word-timed "lrcx" lyrics, from `mlyric.kuwo.cn/mobi.s?…&lrcx=1`
/// (contract reference: Yudaotor/lyrimuse, lyrimuse-engine/kuwolrcx.go).
/// The reply is a `tp=content` header, a blank line, then zlib data; inflated,
/// it is base64 text that, decoded and XOR-ed with "yeelion", gives lines
/// `[mm:ss.xxx]<a,b>word…`. Words come *after* their stamps. The stamps are
/// obfuscated: `[kuwo:NNN]` read as octal gives v, k1 = v / 10, k2 = v % 10,
/// and a word starts |a+b| / (2·k1) ms and lasts |a−b| / (2·k2) ms after the
/// line. Translation and blank lines carry only `<0,0>` stamps and are dropped.
enum KuwoLrcx {
    private static let key = Array("yeelion".utf8)

    static func decode(_ data: Data) -> String? {
        guard data.count > 10, data.count <= 2_000_000,
              String(decoding: data.prefix(10), as: UTF8.self).lowercased() == "tp=content",
              let separator = data.range(of: Data("\r\n\r\n".utf8)),
              let inflated = EnhancedLRC.inflate(data[separator.upperBound...]), inflated.count <= 4_000_000,
              let base64 = String(data: inflated, encoding: .utf8),
              var bytes = Data(base64Encoded: base64, options: .ignoreUnknownCharacters).map(Array.init)
        else { return nil }
        for index in bytes.indices { bytes[index] ^= key[index % key.count] }
        return String(data: Data(bytes), encoding: .utf8)
    }

    static func coefficients(_ text: String) -> (k1: Int, k2: Int)? {
        guard let regex = try? NSRegularExpression(pattern: #"(?m)^\s*\[kuwo:(\d+)"#) else { return nil }
        let ns = text as NSString
        guard let match = regex.firstMatch(in: text, range: NSRange(location: 0, length: ns.length)),
              let value = Int(ns.substring(with: match.range(at: 1)), radix: 8) else { return nil }
        let k1 = value / 10, k2 = value % 10
        return k1 != 0 && k2 != 0 ? (k1, k2) : nil
    }

    static func enhancedLRC(fromText text: String) -> String? {
        guard let (k1, k2) = coefficients(text),
              let head = try? NSRegularExpression(pattern: #"^\[(\d{1,3}):(\d{2})\.(\d{1,3})\]"#),
              let stamp = try? NSRegularExpression(pattern: #"<(-?\d+),(-?\d+)(?:,-?\d+)?>"#) else { return nil }
        var out: [String] = []
        for raw in text.components(separatedBy: .newlines) {
            let row = raw.trimmingCharacters(in: .whitespaces)
            if row.lowercased().hasPrefix("[offset:") { out.append(row); continue }
            let line = row as NSString
            guard let match = head.firstMatch(in: row, range: NSRange(location: 0, length: line.length)) else { continue }
            let fraction = line.substring(with: match.range(at: 3))
            let start = (Double(line.substring(with: match.range(at: 1))) ?? 0) * 60_000
                + (Double(line.substring(with: match.range(at: 2))) ?? 0) * 1000
                + (Double(fraction) ?? 0) * pow(10, Double(3 - fraction.count))
            let body = line.substring(from: match.range.upperBound) as NSString
            let marks = stamp.matches(in: body as String, range: NSRange(location: 0, length: body.length))
            guard !marks.isEmpty else { continue }
            var words: [EnhancedLRC.Word] = [], allZero = true
            for (index, mark) in marks.enumerated() {
                let a = Int(body.substring(with: mark.range(at: 1))) ?? 0, b = Int(body.substring(with: mark.range(at: 2))) ?? 0
                if a != 0 || b != 0 { allZero = false }
                let textEnd = index + 1 < marks.count ? marks[index + 1].range.location : body.length
                let word = body.substring(with: NSRange(location: NSMaxRange(mark.range), length: textEnd - NSMaxRange(mark.range)))
                let offset = abs(a + b) / (2 * k1), length = abs(a - b) / (2 * k2)
                words.append(.init(text: word, start: start + Double(offset), length: Double(length)))
            }
            if allZero { continue }
            if let converted = EnhancedLRC.line(start: start, length: nil, words: words) { out.append(converted) }
        }
        return out.contains(where: { !$0.lowercased().hasPrefix("[offset:") }) ? out.joined(separator: "\n") : nil
    }

    static func enhancedLRC(from data: Data) -> String? {
        decode(data).flatMap(enhancedLRC(fromText:))
    }
}
