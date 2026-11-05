import XCTest
@testable import VersoCustom

/// Imported and local lyric files in the encodings players commonly save.
final class LyricTextDecodingTests: XCTestCase {
    private func encoding(_ value: CFStringEncodings) -> String.Encoding {
        String.Encoding(rawValue: CFStringConvertEncodingToNSStringEncoding(CFStringEncoding(value.rawValue)))
    }

    func testLegacyEncodingsDecodeToTheOriginalLyrics() throws {
        let samples: [(String, String.Encoding)] = [
            ("[00:01.00]夜に駆ける\n[00:05.20]沈むように溶けてゆくように", .shiftJIS),
            ("[00:01.00]月亮代表我的心\n[00:05.20]你问我爱你有多深", encoding(.GB_18030_2000)),
            ("[00:01.00]月亮代表我的心\n[00:05.20]你問我愛你有多深", encoding(.big5)),
            ("[00:01.00]사랑해요\n[00:05.20]너를 처음 만난 날", encoding(.EUC_KR)),
            ("[00:01.00]夜に駆ける\n[00:05.20]Sing along", .utf16LittleEndian),
            ("[00:01.00]夜に駆ける\n[00:05.20]Sing along", .utf16),
        ]
        for (text, encoding) in samples {
            let data = try XCTUnwrap(text.data(using: encoding))
            XCTAssertEqual(try LyricTextDecoder.lyrics(data).get(), text, "\(encoding)")
        }
        // A UTF-8 byte-order mark no longer hides the first timestamp.
        let marked = Data([0xEF, 0xBB, 0xBF]) + Data("[00:01.00]First\n[00:03.00]Second".utf8)
        XCTAssertEqual(LyricsFetcher.parseLRC(try LyricTextDecoder.lyrics(marked).get(), trackDuration: 10).count, 2)
    }

    func testImportFailuresAreReportedRatherThanIgnored() throws {
        XCTAssertEqual(LyricTextDecoder.lyrics(Data([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF])), .failure(.unreadable))
        XCTAssertEqual(LyricTextDecoder.lyrics(Data("Just words, no timing".utf8)), .failure(.noTimestamps))
        XCTAssertEqual(LyricTextDecoder.lyrics(Data(repeating: 0x41, count: 20), limit: 10), .failure(.tooLarge))
        XCTAssertNotNil(LyricTextDecoder.Failure.unreadable.errorDescription)
        // TTML that declares its legacy encoding still parses once decoded.
        let ttml = #"<?xml version="1.0" encoding="Shift_JIS"?><tt><body><div><p begin="1" end="3">夜に駆ける</p></div></body></tt>"#
        let decoded = try LyricTextDecoder.lyrics(try XCTUnwrap(ttml.data(using: .shiftJIS))).get()
        XCTAssertTrue(decoded.hasPrefix(#"<?xml version="1.0" encoding="UTF-8"?>"#))
        XCTAssertEqual(TTMLLyricParser.parse(decoded, duration: 10).first?.text, "夜に駆ける")
    }
}
