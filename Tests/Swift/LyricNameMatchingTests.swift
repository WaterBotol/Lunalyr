import XCTest
@testable import VersoCustom

/// Names are compared without edition and credit qualifiers.
final class LyricNameMatchingTests: XCTestCase {
    func testEditionAndCreditQualifiersAreNormalisedBeforeComparing() {
        XCTAssertEqual(LyricQuery.similarity("Song (2011 Remaster)", "Song - Remastered 2011"), 0.95)
        XCTAssertEqual(LyricQuery.similarity("Song (feat. Latto)", "Song (with Latto)"), 0.95)
        XCTAssertEqual(LyricQuery.similarity("Song ft. Latto", "Song"), 0.95)
        XCTAssertEqual(LyricQuery.similarity("Song – Live at Wembley", "Song [Radio Edit]"), 0.95)
        XCTAssertEqual(LyricQuery.similarity("Song (Mono)", "Song (Stereo Version)"), 0.95)
        // Brackets that are part of the name stay part of it.
        XCTAssertEqual(LyricQuery.core("(I Can't Get No) Satisfaction"), LyricQuery.normalize("(I Can't Get No) Satisfaction"))
        XCTAssertEqual(LyricQuery.core("Part 1 - The Beginning"), "part 1 the beginning")
        XCTAssertEqual(LyricQuery.similarity("Our Song", "Our Song"), 1)
    }

    func testContainmentNeedsWholeWordsExceptInUnspacedScripts() {
        XCTAssertLessThan(LyricQuery.similarity("Love", "Lovers"), 0.75)
        XCTAssertLessThan(LyricQuery.similarity("Pink", "Pinkfong"), 0.65)
        XCTAssertEqual(LyricQuery.similarity("Coldplay", "Coldplay, BTS"), 0.85)
        XCTAssertEqual(LyricQuery.similarity("夜に駆ける", "夜に駆ける英語版"), 0.85)
    }

    func testNamesMadeOnlyOfSymbolsMatchAsWritten() {
        XCTAssertEqual(LyricQuery.similarity("!!!", "!!!"), 1)
        XCTAssertEqual(LyricQuery.similarity("🦋", " 🦋 "), 1)
        XCTAssertEqual(LyricQuery.similarity("🦋", "🐝"), 0)
        XCTAssertEqual(LyricQuery.similarity("!!!", ""), 0)
    }

    func testRecordingKeysAreUnchangedSoSavedChoicesAndOffsetsStillMatch() {
        let key = LyricQuery(title: "Don’t Stop (2011 Remaster)", artist: "Beyoncé", album: "Live", duration: 221.4).recordingKey
        XCTAssertEqual(key, "don t stop 2011 remaster|beyonce|live|221")
    }
}
