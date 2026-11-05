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
    @MainActor
    func testDefaultLyrimuseFolderLeavesCustomChoiceUntilTheDirectoryExists() throws {
        let suite = "LyrimuseFolderTests-\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        let home = FileManager.default.temporaryDirectory.appendingPathComponent(suite, isDirectory: true)
        defer { try? FileManager.default.removeItem(at: home) }
        let options = LocalLyricOptions(defaults: defaults, homeDirectory: home)
        options.folder = "/my/custom/exports"
        XCTAssertFalse(options.selectDefaultFolder())
        XCTAssertEqual(options.folder, "/my/custom/exports")
        XCTAssertFalse(FileManager.default.fileExists(atPath: options.defaultFolder))
        let folder = URL(fileURLWithPath: options.defaultFolder, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        XCTAssertTrue(options.selectDefaultFolder())
        XCTAssertEqual(LocalLyricOptions(defaults: defaults, homeDirectory: home).folder, folder.path)
    }

    func testPartialLyrimuseSidecarsKeepTheLineTimedLyrics() throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let query = LyricQuery(title: "Sample", artist: "Singer", album: nil, duration: 14)
        let base = folder.appendingPathComponent("Singer - Sample")
        let lrc = """
        [ti:Sample]
        [ar:Singer]
        [00:01.00]Hold on
        [00:01.00]Stay with me
        [00:04.00]The whole phrase
        [00:07.00]Keep going
        [00:10.00]♪
        [00:12.00]Finish the song
        """
        try lrc.write(to: base.appendingPathExtension("lrc"), atomically: true, encoding: .utf8)
        for (ext, sidecar) in [
            ("ttml", """
            <tt><body>
            <p begin="1s" end="2.8s"><span begin="1s" end="1.4s">Hold </span><span begin="1.6s" end="2.8s">on</span></p>
            <p begin="7.55s" end="9s"><span begin="7.55s" end="7.95s">Keep </span><span begin="8.05s" end="9s">going</span></p>
            </body></tt>
            """),
            ("yrc", "[1000,1800](1000,400,0)Hold (1600,1200,0)on\n[7550,1450](7550,400,0)Keep (8050,950,0)going"),
        ] {
            let file = base.appendingPathExtension(ext)
            try sidecar.write(to: file, atomically: true, encoding: .utf8)
            let found = try LocalLyricBridge.candidate(query, folder: folder.path)
            try FileManager.default.removeItem(at: file)
            guard case .synced(let lines, let provenance)? = found?.result else { return XCTFail(ext) }
            XCTAssertEqual(provenance.precision, "Mixed timing", ext)
            XCTAssertEqual(lines.map(\.text), ["Hold on", "The whole phrase", "Keep going", "", "Finish the song"], ext)
            XCTAssertEqual(lines.map(\.wordTimed), [true, false, true, false, false], ext)
            XCTAssertEqual(lines[0].words[1].start, 1.6, accuracy: 0.001, ext)
            XCTAssertEqual(lines[0].translation, "Stay with me", ext)
            XCTAssertEqual(lines[1].end, 7.55, accuracy: 0.001, ext)
            XCTAssertEqual(lines[1].words, LyricsFetcher.distributeWords(in: "The whole phrase", from: 4, to: 7.55), ext)
            XCTAssertTrue(lines[3].isInstrumental, ext)
        }
    }

    func testLyrimuseTranslationFilesAttachByTimestamp() throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let query = LyricQuery(title: "Sample", artist: "Singer", album: nil, duration: 20)
        let base = folder.appendingPathComponent("Singer - Sample")
        try "[ti:Sample]\n[ar:Singer]\n[00:01.00]君の名は\n[00:01.00]Your name\n[00:04.00]夜に駆ける\n[00:07.00]OK\n[00:10.00]♪\n[00:13.00]最後"
            .write(to: base.appendingPathExtension("lrc"), atomically: true, encoding: .utf8)
        let untranslated = LocalLyricBridge.fingerprint(query, folder: folder.path)
        try "[ti:Sample]\n[00:01.00]你的名字\n[00:04.05]奔向夜晚\n[00:07.00]OK\n[00:10.00]间奏\n[00:13.00]"
            .write(to: base.appendingPathExtension("tr.lrc"), atomically: true, encoding: .utf8)
        // Adding the companion file is noticed, so the lyrics are read again.
        XCTAssertNotEqual(LocalLyricBridge.fingerprint(query, folder: folder.path), untranslated)
        let found = try LocalLyricBridge.candidate(query, folder: folder.path)
        guard case .synced(let lines, _)? = found?.result else { return XCTFail("The local lyrics should be found") }
        XCTAssertEqual(lines.map(\.text), ["君の名は", "夜に駆ける", "OK", "", "最後"])
        // A bilingual file's own translation stays; the companion fills in the
        // rest by time, but never repeats a line, translates a break or adds a blank.
        XCTAssertEqual(lines.map(\.translation), ["Your name", "奔向夜晚", nil, nil, nil])
    }
    func testCompanionRowsAreFoundByTimeInLongFiles() {
        // A long lyric file and a reading or translation file beside it, its
        // rows on time, a little early or late, too far off, or halfway
        // between two lines.
        let count = 20_000, offsets = [0, 0.1, -0.1, 0.17, 0.25, -0.3, 0.06]
        func stamp(_ seconds: Double) -> String {
            String(format: "[%02d:%05.2f]", Int(seconds) / 60, seconds.truncatingRemainder(dividingBy: 60))
        }
        let lines = (0..<count).map { LyricsFetcher.makeLine(text: "line \($0)", start: Double($0) * 0.5, end: Double($0) * 0.5 + 0.5) }
        let companion = (0..<count).map { stamp(Double($0) * 0.5 + offsets[$0 % offsets.count]) + "row \($0)" }
            .joined(separator: "\n")
        let rows = LyricsFetcher.parseLRC(companion, trackDuration: 0)
        XCTAssertEqual(rows.count, count)
        let started = Date()
        let read = LocalLyricBridge.attachingReadings(companion, to: lines, duration: 0)
        let translated = LocalLyricBridge.attachingTranslations(companion, to: lines, duration: 0)
        // Comparing every line with every row took about half a minute here;
        // now reading the file takes most of the time.
        XCTAssertLessThan(Date().timeIntervalSince(started), 10)
        // Each line gets the row a scan of every row finds.
        for index in stride(from: 0, to: count, by: 233) {
            let start = lines[index].start
            let nearest = rows.min { abs($0.start - start) < abs($1.start - start) }
            let expected = nearest.flatMap { abs($0.start - start) <= 0.18 ? $0.text : nil }
            XCTAssertEqual(read[index].romanization, expected, lines[index].text)
            XCTAssertEqual(translated[index].translation, expected, lines[index].text)
        }
        XCTAssertGreaterThan(read.filter { $0.romanization != nil }.count, count / 2)
        // Halfway between two rows, the earlier one is taken, as a scan takes it.
        let between = LyricsFetcher.parseLRC("[00:01.00]A\n[00:02.00]B\n[00:03.00]C", trackDuration: 4)
        let halves = LocalLyricBridge.attachingReadings("[00:01.90]early\n[00:02.10]late\n[00:03.30]far", to: between, duration: 4)
        XCTAssertEqual(halves.map(\.romanization), [nil, "early", nil])
        XCTAssertNil(LocalLyricBridge.Companion([]).row(at: 1))
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
        XCTAssertEqual(LyricReadingPairs.match(result[0], to: line.words)[line.words[0].presentationID], "nei5 hou2")
    }
    func testSavedReadingsReplaceWordsWithoutMovingTheirTiming() {
        let words: [LyricsFetcher.Word] = [
            .init(text: "你", start: 1.2, end: 1.5),
            .init(text: "好", start: 2.1, end: 2.9),
        ]
        let line = LyricsFetcher.Line(start: 1, end: 3, text: "你好", words: words,
            isInstrumental: false, wordTimed: true, romanization: "nei5 hou2")
        let result = LyricRomanizer.readings([line], options: .init(
            display: .replace, language: .automatic, toneMarks: true, preferSaved: true))[0]
        XCTAssertEqual(result?.map(\.text), ["nei5", "hou2"])
        XCTAssertEqual(result?.map(\.start), words.map(\.start))
        XCTAssertEqual(result?.map(\.end), words.map(\.end))
    }
    func testJapaneseKanaDoesNotFallBackToMandarin() {
        let reading = LyricRomanizer.romanize("ありがとう", language: .japanese, toneMarks: false)
        XCTAssertTrue(reading.lowercased().contains("arigat"))
    }
}
