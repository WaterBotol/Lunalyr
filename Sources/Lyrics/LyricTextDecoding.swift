import Foundation

/// Reads lyric files in the encodings players and editors commonly save:
/// UTF-8 and UTF-16 (with or without a byte-order mark), Shift_JIS, GB18030,
/// Big5 and EUC-KR. Of the decodings that succeed without loss, the first to
/// yield usable LRC or TTML timing wins; Foundation's language-aware
/// detection orders the legacy encodings.
enum LyricTextDecoder {
    enum Failure: LocalizedError, Equatable {
        case tooLarge, unopenable, unreadable, noTimestamps
        var errorDescription: String? {
            switch self {
            case .tooLarge: return "This file is too large to be a lyric file."
            case .unopenable: return "This file couldn’t be opened."
            case .unreadable: return "This file’s text encoding isn’t supported. Save it as UTF-8 and try again."
            case .noTimestamps: return "This file has no usable LRC or TTML timestamps."
            }
        }
    }

    /// Text with usable lyric timing, or why the file can't be used.
    static func lyrics(_ data: Data, limit: Int = 1_000_000) -> Swift.Result<String, Failure> {
        guard data.count <= limit else { return .failure(.tooLarge) }
        let texts = decodings(data)
        guard !texts.isEmpty else { return .failure(.unreadable) }
        guard let usable = texts.first(where: hasTiming) else { return .failure(.noTimestamps) }
        return .success(usable)
    }

    /// The most plausible text, preferring a decoding with lyric timing.
    static func decode(_ data: Data) -> String? {
        let texts = decodings(data)
        guard texts.count > 1 else { return texts.first }
        return texts.first(where: hasTiming) ?? texts.first
    }

    /// Lossless decodings, most plausible first, each already `prepared`.
    static func decodings(_ data: Data) -> [String] {
        guard !data.isEmpty else { return [] }
        // A byte-order mark is unambiguous.
        if data.starts(with: [0xEF, 0xBB, 0xBF]) {
            return String(data: data.dropFirst(3), encoding: .utf8).map { [prepared($0)] } ?? []
        }
        if data.starts(with: [0xFF, 0xFE]) || data.starts(with: [0xFE, 0xFF]) {
            return String(data: data, encoding: .utf16).map { [prepared($0)] } ?? []
        }
        // UTF-16 without a mark can also be valid UTF-8, full of NULs, so the
        // zero-byte pattern is checked before UTF-8 is trusted.
        let utf16 = utf16Order(data)
        if utf16 == nil, let text = String(data: data, encoding: .utf8) { return [prepared(text)] }
        var found: [String] = []
        func add(_ text: String?) {
            guard let text else { return }
            let value = prepared(text)
            if !found.contains(value) { found.append(value) }
        }
        if let utf16 { add(String(data: data, encoding: utf16)) }
        add(String(data: data, encoding: .utf8))
        var converted: NSString?
        var lossy: ObjCBool = false
        let detected = NSString.stringEncoding(for: data, encodingOptions: [
            .suggestedEncodingsKey: legacy.map { NSNumber(value: $0.rawValue) },
            .useOnlySuggestedEncodingsKey: true,
            .allowLossyKey: false,
        ], convertedString: &converted, usedLossyConversion: &lossy)
        if detected != 0, !lossy.boolValue { add(converted as String?) }
        for encoding in legacy { add(String(data: data, encoding: encoding)) }
        return found
    }

    /// Whether the text parses as LRC or TTML with at least one timed line.
    static func hasTiming(_ text: String) -> Bool {
        if text.trimmingCharacters(in: .whitespacesAndNewlines).hasPrefix("<") {
            return !TTMLLyricParser.parse(text, duration: 0).isEmpty
        }
        return !LyricsFetcher.parseLRC(text, trackDuration: 0).isEmpty
    }

    private static let legacy: [String.Encoding] = [
        .shiftJIS, encoding(.GB_18030_2000), encoding(.big5), encoding(.EUC_KR),
    ]

    private static func encoding(_ value: CFStringEncodings) -> String.Encoding {
        String.Encoding(rawValue: CFStringConvertEncodingToNSStringEncoding(CFStringEncoding(value.rawValue)))
    }

    /// UTF-16 without a mark leaves a zero in every other byte of the ASCII
    /// timestamps; which half holds them gives the byte order.
    private static func utf16Order(_ data: Data) -> String.Encoding? {
        let sample = Array(data.prefix(512))
        guard sample.count >= 8 else { return nil }
        var even = 0, odd = 0
        for (index, byte) in sample.enumerated() where byte == 0 {
            if index.isMultiple(of: 2) { even += 1 } else { odd += 1 }
        }
        let half = sample.count / 2
        if odd * 3 > half, even * 10 < half { return .utf16LittleEndian }
        if even * 3 > half, odd * 10 < half { return .utf16BigEndian }
        return nil
    }

    /// Unicode text with no byte-order mark, and an XML declaration that no
    /// longer names the file's original encoding.
    private static func prepared(_ text: String) -> String {
        var value = text.hasPrefix("\u{FEFF}") ? String(text.dropFirst()) : text
        if value.hasPrefix("<?xml") {
            value = value.replacingOccurrences(of: #"^(<\?xml[^>]*?encoding\s*=\s*)(["'])[^"']*(["'])"#,
                                               with: "$1$2UTF-8$3", options: .regularExpression)
        }
        return value
    }
}
