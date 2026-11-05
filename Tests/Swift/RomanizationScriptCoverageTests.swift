import XCTest
@testable import VersoCustom

/// Every script other than Latin gets at least the system's romanisation;
/// Latin text with accents is left alone.
final class RomanizationScriptCoverageTests: XCTestCase {
    func testEveryNonLatinScriptIsRomanised() {
        let samples = [
            "ਸਤ ਸ੍ਰੀ ਅਕਾਲ",          // Gurmukhi (Punjabi)
            "নমস্কার বন্ধু",          // Bengali
            "வணக்கம் நண்பா",         // Tamil
            "నమస్కారం మిత్రమా",       // Telugu
            "નમસ્તે મિત્ર",            // Gujarati
            "ನಮಸ್ಕಾರ ಗೆಳೆಯ",          // Kannada
            "നമസ്കാരം സുഹൃത്തേ",      // Malayalam
            "გამარჯობა მეგობარო",    // Georgian
            "Բարեւ ընկեր",           // Armenian
            "नमस्ते दुनिया",           // Devanagari (Hindi), covered before
            "Привет, мир",           // Cyrillic, covered before
        ]
        for text in samples {
            XCTAssertTrue(LyricRomanizer.containsNonLatin(text), text)
            let reading = LyricRomanizer.romanize(text, language: .automatic, toneMarks: false)
            XCTAssertNotNil(reading.range(of: "[A-Za-z]", options: .regularExpression), "\(text) -> \(reading)")
            let left = Set(reading.unicodeScalars.filter { $0.properties.isAlphabetic })
            XCTAssertTrue(left.isDisjoint(with: Set(text.unicodeScalars)), "Untransliterated letters: \(text) -> \(reading)")
        }
        XCTAssertTrue(LyricRomanizer.romanize("नमस्ते दुनिया", language: .automatic, toneMarks: false)
            .lowercased().contains("namaste"))
    }

    func testLatinTextWithAccentsIsLeftAlone() {
        for text in ["Café déjà vu", "Ñandú über São Paulo", "Łódź ŵ ǆ", "Ｆｕｌｌｗｉｄｔｈ", "♪ 2024 — ok!"] {
            XCTAssertFalse(LyricRomanizer.containsNonLatin(text), text)
        }
    }
}
