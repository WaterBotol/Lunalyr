import XCTest
@testable import VersoCustom

final class ReadingAndTTMLTests: XCTestCase {
    func testTTMLLeafSpansKeepWordTimes() {
        let xml = """
        <tt xmlns="http://www.w3.org/ns/ttml"><body><div>
        <p begin="00:01.000" end="00:03.000"><span begin="1s" end="1.4s">Hold </span><span begin="1.5s" end="3s">on</span></p>
        </div></body></tt>
        """
        let lines = TTMLLyricParser.parse(xml, duration: 10)
        XCTAssertEqual(lines.count, 1)
        XCTAssertTrue(lines[0].wordTimed)
        XCTAssertEqual(lines[0].words.map(\.text), ["Hold", "on"])
        XCTAssertEqual(lines[0].words[1].start, 1.5, accuracy: 0.001)
        XCTAssertEqual(lines[0].words[1].end, 3, accuracy: 0.001)
    }
    func testPartialTTMLWordCoverageUsesEstimatedTiming() {
        let xml = "<tt><body><p begin='1s' end='4s'>Keep <span begin='2s' end='3s'>moving</span> now</p></body></tt>"
        let line = TTMLLyricParser.parse(xml, duration: 10).first
        XCTAssertNotNil(line)
        XCTAssertFalse(line?.wordTimed ?? true)
        XCTAssertEqual(line?.text, "Keep moving now")
    }
    func testTTMLDoesNotRenderTranslationSpansAsLyrics() {
        let xml = """
        <tt xmlns:ttm="http://www.w3.org/ns/ttml#metadata"><body><p begin="1s" end="3s"><span begin="1s" end="3s">Hello</span><span ttm:role="x-translation">Translation</span></p></body></tt>
        """
        let line = TTMLLyricParser.parse(xml, duration: 10).first
        XCTAssertEqual(line?.text, "Hello")
        XCTAssertTrue(line?.wordTimed ?? false)
    }
    func testTTMLRejectsEntitiesAndInvalidTimes() {
        let xml = "<!DOCTYPE tt [<!ENTITY secret SYSTEM 'file:///etc/passwd'>]><tt><p begin='1s' end='2s'>&secret;</p></tt>"
        XCTAssertTrue(TTMLLyricParser.parse(xml, duration: 10).isEmpty)
        XCTAssertNil(TTMLLyricParser.seconds("nan"))
        XCTAssertNil(TTMLLyricParser.seconds("-1s"))
        XCTAssertEqual(TTMLLyricParser.seconds("1250ms"), 1.25)
    }
    func testReadingsAttachByTimestampRatherThanLineIndex() {
        let lines = LyricsFetcher.parseLRC("[00:01]one\n[00:03]two\n[00:05]three", trackDuration: 8)
        let attached = LocalLyricBridge.attachingReadings("[00:03.10]second reading\n[00:05]third reading", to: lines, duration: 8)
        XCTAssertNil(attached[0].romanization)
        XCTAssertEqual(attached[1].romanization, "second reading")
        XCTAssertEqual(attached[2].romanization, "third reading")
    }
    func testLocalFilesRequireMatchingArtistAndAlbum() throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let file = folder.appendingPathComponent("Singer - Sample - Album.lrc")
        try "[ti:Sample]\n[ar:Singer]\n[al:Album]\n[00:01]Keep moving\n[00:03]Hold on".write(to: file, atomically: true, encoding: .utf8)
        XCTAssertNotNil(try LocalLyricBridge.candidate(.init(title: "Sample", artist: "Singer", album: "Album", duration: 10), folder: folder.path))
        XCTAssertNil(try LocalLyricBridge.candidate(.init(title: "Sample", artist: "Someone else", album: "Album", duration: 10), folder: folder.path))
        XCTAssertNil(try LocalLyricBridge.candidate(.init(title: "Sample", artist: "Singer", album: "Live", duration: 10), folder: folder.path))
    }
    func testPinyinKeepsOriginalWordTimes() {
        let line = LyricsFetcher.Line(start: 1, end: 3, text: "你好", words: [.init(text: "你好", start: 1.2, end: 2.7)], isInstrumental: false, wordTimed: true)
        let result = LyricRomanizer.readings([line], options: .init(display: .below, language: .mandarin, toneMarks: false, preferSaved: false))
        XCTAssertEqual(result[0]?.first?.text.lowercased(), "ni hao")
        XCTAssertEqual(result[0]?.first?.start, 1.2)
        XCTAssertEqual(result[0]?.first?.end, 2.7)
    }
    func testSavedJyutpingIsUsedInsteadOfGeneratingPinyin() {
        let line = LyricsFetcher.Line(start: 1, end: 3, text: "你好", words: [.init(text: "你好", start: 1, end: 3)], isInstrumental: false, wordTimed: true, romanization: "nei5 hou2")
        let result = LyricRomanizer.readings([line], options: .init(display: .below, language: .automatic, toneMarks: true, preferSaved: true))
        XCTAssertEqual(result[0]?.map(\.text).joined(separator: " "), "nei5 hou2")
    }
    func testJapaneseKanaDoesNotFallBackToMandarin() {
        let reading = LyricRomanizer.romanize("ありがとう", language: .japanese, toneMarks: false)
        XCTAssertTrue(reading.lowercased().contains("arigat"))
    }
}
