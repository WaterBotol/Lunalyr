import AppKit
import SwiftUI
import XCTest
@testable import VersoCustom

/// Readings and text of glued runs: syllables and characters that the lyric
/// writes together are set as one word, and what is drawn with that word --
/// its reading, Echo's repeats, a lyric card -- must read as the line does.
/// Readings come from pure functions of the lines and an options snapshot,
/// so nothing here touches RomanizationOptions.shared or user defaults.
final class LyricRunReadingTests: XCTestCase {
    private func options(_ display: RomanizationOptions.Display = .below,
                         language: RomanizationOptions.Language = .automatic) -> RomanizationOptions.Snapshot {
        .init(display: display, language: language, toneMarks: true, preferSaved: true)
    }

    /// One word per character, as syllable-synced CJK lyrics arrive.
    private func characterTimed(_ text: String, start: Double = 10, reading: String? = nil) -> LyricsFetcher.Line {
        var time = start
        let words = text.filter { !$0.isWhitespace }.map { character -> LyricsFetcher.Word in
            defer { time += 0.4 }
            return .init(text: String(character), start: time, end: time + 0.4)
        }
        return .init(start: start, end: time, text: text, words: words, isInstrumental: false,
                     wordTimed: true, romanization: reading)
    }

    /// The given words, unspaced in the line's text unless `spaced`.
    private func line(_ parts: [String], start: Double = 0, spaced: Bool = false, wordTimed: Bool = true,
                      reading: String? = nil) -> LyricsFetcher.Line {
        var time = start
        let words = parts.map { part -> LyricsFetcher.Word in
            defer { time += 0.5 }
            return .init(text: part, start: time, end: time + 0.5)
        }
        return .init(start: start, end: time, text: parts.joined(separator: spaced ? " " : ""), words: words,
                     isInstrumental: false, wordTimed: wordTimed, romanization: reading)
    }

    private func readings(_ line: LyricsFetcher.Line, _ snapshot: RomanizationOptions.Snapshot? = nil) -> [LyricsFetcher.Word]? {
        LyricRomanizer.readings([line], options: snapshot ?? options())[0]
    }

    /// What "Below lyrics" draws under each run of `line`, in order.
    private func runReadings(_ line: LyricsFetcher.Line, _ snapshot: RomanizationOptions.Snapshot? = nil) -> [String] {
        let glue = LyricRuns.glue(line: line, words: line.words)
        let pairs = LyricReadingPairs.match(readings(line, snapshot), to: line.words, glue: glue)
        return line.words.compactMap { pairs[$0.presentationID] }
    }

    // MARK: Readings under glued runs

    func testACharacterTimedRunReadsByPhraseAsTheLineReadingDoes() throws {
        // 今日も is one run on screen; its reading is "kyou mo", not "kyoumo".
        let today = characterTimed("今日も")
        XCTAssertEqual(LyricRuns.glue(line: today, words: today.words), [false, true, true])
        XCTAssertEqual(runReadings(today), ["kyou mo"])
        // Generated and source readings alike follow the phrases they read:
        // the parts of one phrase share its time, as `lineReading` groups them.
        let lines = [
            today, characterTimed("思った"), characterTimed("はなれないで"),
            characterTimed("夢ならばどれほどよかったでしょう", reading: "yu me na ra ba do re ho do yo ka tta de syo u"),
            characterTimed("今日何食べた", reading: "kyo u na ni ta be ta"),
        ]
        for line in lines {
            let expected = try XCTUnwrap(LyricRomanizer.lineReading(readings(line), for: line), line.text)
            XCTAssertEqual(runReadings(line), [expected], line.text)
        }
        XCTAssertTrue(runReadings(lines[3]).first?.contains(" ") ?? false, "a long line is not one blob")
    }

    func testRomanizationSkipsMatchingWordsWithDifferentCaseOrPunctuation() {
        let words = [
            LyricsFetcher.Word(text: "Okay!", start: 1, end: 1.5),
            .init(text: "夜", start: 1.5, end: 2),
        ]
        let readings = [
            LyricsFetcher.Word(text: "okay", start: 1, end: 1.5),
            .init(text: "yoru", start: 1.5, end: 2),
        ]
        let annotations = LyricReadingPairs.match(readings, to: words, glue: [false, false])
        XCTAssertNil(annotations[words[0].presentationID])
        XCTAssertEqual(annotations[words[1].presentationID], "yoru")
    }

    func testALineTimedJapaneseRunKeepsTheReadingsWordsApart() {
        // LRCLIB timing with an imported reading: one run, six phrases.
        let line = line(["今日", "も", "また", "君", "を", "想う"], wordTimed: false,
                        reading: "kyou mo mata kimi wo omou")
        XCTAssertEqual(runReadings(line), ["kyou mo mata kimi wo omou"])
        // Spaces inside one word's reading are its own and are kept.
        let name = self.line(["君の", "名は"], wordTimed: false, reading: "kimi no na wa")
        XCTAssertEqual(runReadings(name), ["kimi no na wa"])
    }

    func testMandarinRunsAreReadSyllableBySyllable() throws {
        let mandarin = options(language: .mandarin)
        // Character-timed: every character is a phrase of its own.
        let hawk = characterTimed("半天上的")
        let parts = try XCTUnwrap(readings(hawk, mandarin)).map(\.text)
        XCTAssertEqual(parts.count, 4)
        XCTAssertEqual(runReadings(hawk, mandarin), [parts.joined(separator: " ")])
        // Line-timed words: each word's pinyin keeps its own syllable spaces.
        let fingertip = line(["感受", "停在", "我"], wordTimed: false)
        let words = try XCTUnwrap(readings(fingertip, mandarin)).map(\.text)
        let joined = try XCTUnwrap(runReadings(fingertip, mandarin).first)
        XCTAssertEqual(joined, words.joined(separator: " "))
        XCTAssertEqual(joined.split(separator: " ").count, 5, "one pinyin syllable per character: \(joined)")
    }

    func testAnImportedJyutpingRunKeepsItsSyllablesApart() {
        let friends = line(["佢哋", "係", "我", "嘅", "朋友"], wordTimed: false,
                           reading: "keoi5 dei6 hai6 ngo5 ge3 pang4 jau5")
        XCTAssertEqual(runReadings(friends), ["keoi5 dei6 hai6 ngo5 ge3 pang4 jau5"])
    }

    func testTheSyllablesOfAnAlphabeticWordStillReadAsOneWord() {
        // Each syllable of люблю has its own time, but the word is spelled
        // without a break, so its reading is too.
        let love = LyricsFetcher.Line(start: 10, end: 11, text: "люблю", words: [
            .init(text: "лю", start: 10, end: 10.5), .init(text: "блю", start: 10.5, end: 11)],
            isInstrumental: false, wordTimed: true)
        let readings = [LyricsFetcher.Word(text: "lyu", start: 10, end: 10.5),
                        LyricsFetcher.Word(text: "blyu", start: 10.5, end: 11)]
        let pairs = LyricReadingPairs.match(readings, to: love.words, glue: [false, true])
        XCTAssertEqual(pairs, [love.words[0].presentationID: "lyublyu"])
    }

    @MainActor
    func testEveryLayoutDrawsTheRunsReadingSpacedByPhrase() throws {
        let today = characterTimed("今日も")
        let below = try XCTUnwrap(readings(today))
        let glue = LyricRuns.glue(line: today, words: today.words)
        // Scattered, Constellation, Echo and Editorial: one run, one reading.
        let key = ScatteredTypography.Key(words: today.words, baseSize: 60, style: .forLine(0), mood: .neutral,
            composition: .constellationEditorial, spread: 1, fontIdentity: LyricFontIdentity(settings: .shared),
            readings: LyricReadingPairs.match(below, to: today.words, glue: glue), glue: glue)
        let layout = ScatteredTypography.layout(key, settings: .shared)
        XCTAssertEqual(layout.runs.count, 1)
        XCTAssertEqual(layout.runs.first?.reading?.text, "kyou mo")
        // Spoken, Cascade and Cutout: the run rolls as one word with it.
        let window = LyricLayerCache.rollingWindow(around: 0, lines: [today], romanized: [0: below], time: 10.5,
                                                   lead: 1.2, display: .below)
        let word = try XCTUnwrap(window.words.first)
        XCTAssertEqual(word.text, "今日も")
        XCTAssertEqual(window.readings[word.presentationID], "kyou mo")
    }

    @MainActor
    func testAGluedRunsReadingComesWithItsFirstSyllable() throws {
        // Korean timed by syllable (an invented line), as TTML, YRC and
        // Lyrics+ give it: each word is a glued run, kept opaque while its
        // syllables arrive through the reveal mask. The reading under it is
        // not masked, so every word's reading showed from the start of the
        // line; it comes with the word's first syllable, as a single word's.
        let settings = Settings.shared
        let saved = settings.wordRevealByStyle
        defer { settings.wordRevealByStyle = saved }
        settings.wordRevealByStyle[LyricComposition.scattered.rawValue] = true
        let line = LyricsFetcher.Line(start: 10, end: 11.8, text: "고양이가 창문을", words: [
            .init(text: "고양", start: 10, end: 10.4), .init(text: "이가", start: 10.4, end: 10.8),
            .init(text: "창문", start: 11, end: 11.4), .init(text: "을", start: 11.4, end: 11.8)],
            isInstrumental: false, wordTimed: true)
        let glue = LyricRuns.glue(line: line, words: line.words)
        XCTAssertEqual(glue, [false, true, false, true])
        let key = ScatteredTypography.Key(words: line.words, baseSize: 60, style: .forLine(0), mood: .neutral,
            composition: .scattered, spread: 1, fontIdentity: LyricFontIdentity(settings: settings),
            readings: LyricReadingPairs.match(readings(line), to: line.words, glue: glue), glue: glue)
        let layout = ScatteredTypography.layout(key, settings: settings)
        XCTAssertEqual(layout.runs.count, 2)
        XCTAssertTrue(layout.runs.allSatisfy { $0.reading?.text.isEmpty == false })
        func frames(at time: Double) -> [ScatteredRunFrame] {
            let next = layout.words.firstIndex { $0.source.start > time }
            let states = layout.words.map { ScatteredWordFrame.state($0, nextIndex: next, time: time, composition: .scattered) }
            return layout.runs.map { ScatteredRunFrame.make($0, states: states, reveals: true, ink: .white) }
        }
        let early = frames(at: 10.3)
        XCTAssertGreaterThan(early[0].readingOpacity, 0.5)
        XCTAssertEqual(early[1].readingOpacity, 0, "the second word is not sung yet")
        XCTAssertEqual(early[1].presentation.opacity, 1, "the run itself stays opaque for its reveal mask")
        XCTAssertGreaterThan(frames(at: 11.3)[1].readingOpacity, 0.5)
    }

    // MARK: Constellations

    @MainActor
    func testAConstellationPartsAnUnspacedLineIntoItsWords() throws {
        // An unspaced YRC line is one glued run: one fragment, nothing to link.
        let text = "沈むように溶けてゆくように"
        let line = characterTimed(text)
        let glue = LyricRuns.glue(line: line, words: line.words)
        XCTAssertEqual(LyricRuns.labels(line.words, glue: glue), [text])
        // Parted into phrases, each phrase's characters together, and no
        // particle left on its own.
        let split = LyricRuns.splittingWords(line.words, glue: glue)
        let words = LyricRuns.labels(line.words, glue: split)
        XCTAssertGreaterThanOrEqual(words.count, 2, "\(words)")
        XCTAssertEqual(words.joined(), text)
        XCTAssertTrue(words.allSatisfy { $0.count >= 2 }, "\(words)")
        for text in ["君の名は希望", "半天上的秃鹰那张脸"] {
            let other = characterTimed(text)
            let parts = LyricRuns.labels(other.words, glue: LyricRuns.splittingWords(other.words,
                glue: LyricRuns.glue(line: other, words: other.words)))
            XCTAssertGreaterThanOrEqual(parts.count, 2, "\(parts)")
            XCTAssertLessThan(parts.count, other.words.count / 2, "phrases, not characters: \(parts)")
            XCTAssertEqual(parts.joined(), text)
        }
        let key = ScatteredTypography.Key(words: line.words, baseSize: 60, style: .forLine(0), mood: .neutral,
            composition: .constellation, spread: 1, fontIdentity: LyricFontIdentity(settings: .shared), glue: split)
        XCTAssertEqual(ScatteredTypography.layout(key, settings: .shared).runs.count, words.count)
        // Readings follow the words.
        let pairs = LyricReadingPairs.match(readings(line), to: line.words, glue: split)
        XCTAssertEqual(pairs.count, words.count)
        // Syllables of alphabetic words, and spaced lines, are left alone.
        let beautiful = LyricsFetcher.Line(start: 0, end: 1.2, text: "So beautiful", words: [
            .init(text: "So", start: 0, end: 0.3), .init(text: "beau", start: 0.3, end: 0.6),
            .init(text: "ti", start: 0.6, end: 0.9), .init(text: "ful", start: 0.9, end: 1.2)],
            isInstrumental: false, wordTimed: true)
        let latin = LyricRuns.glue(line: beautiful, words: beautiful.words)
        XCTAssertEqual(LyricRuns.splittingWords(beautiful.words, glue: latin), latin)
    }

    // MARK: Rolling stages

    func testARollingStagePartsAnUnspacedLineIntoItsWords() throws {
        // A constellation keeps kana and single characters with the word
        // before them, which can leave most of a line in one fragment. A
        // rolling stage sets one word at a time, so a Japanese line is
        // parted wherever a reader would start a word, and never before a
        // particle or an ending. (Invented lines.)
        let seaside = characterTimed("窓辺に座って海を見ていた")
        let glue = LyricRuns.glue(line: seaside, words: seaside.words)
        XCTAssertEqual(LyricRuns.labels(seaside.words, glue: LyricRuns.splittingWords(seaside.words, glue: glue)),
                       ["窓辺に", "座って海を見ていた"])
        let rolling = LyricRuns.rollingGlue(line: seaside, words: seaside.words)
        XCTAssertEqual(LyricRuns.labels(seaside.words, glue: rolling), ["窓辺に", "座って", "海を", "見ていた"])
        // Readings follow those words.
        let pairs = LyricReadingPairs.match(readings(seaside), to: seaside.words, glue: rolling)
        XCTAssertEqual(pairs.count, 4)
        XCTAssertTrue(pairs.values.allSatisfy { !$0.isEmpty }, "\(pairs)")
        // What goes on the word before it, and what begins one.
        XCTAssertTrue(JapaneseReading.continuesPhrase("を", after: "海"))
        XCTAssertTrue(JapaneseReading.continuesPhrase("た", after: "歩い"))
        XCTAssertTrue(JapaneseReading.continuesPhrase("ない", after: "見"))
        XCTAssertFalse(JapaneseReading.continuesPhrase("ない", after: "が"))
        XCTAssertTrue(JapaneseReading.continuesPhrase("度", after: "何"))
        XCTAssertTrue(JapaneseReading.continuesPhrase("いる", after: "て"))
        XCTAssertTrue(JapaneseReading.continuesPhrase("ー", after: "ラ"))
        XCTAssertFalse(JapaneseReading.continuesPhrase("きみ", after: "て"))
        XCTAssertFalse(JapaneseReading.continuesPhrase("海", after: "て"))
        XCTAssertFalse(JapaneseReading.continuesPhrase("帰り道", after: "の"))
        // Chinese keeps a single character with the word before it, as a
        // constellation does.
        let spring = characterTimed("我们一起走过春天的小路")
        XCTAssertEqual(LyricRuns.labels(spring.words, glue: LyricRuns.rollingGlue(line: spring, words: spring.words)),
                       ["我们", "一起", "走过", "春天的", "小路"])
    }

    func testThaiIsWrittenWithoutSpacesAndKoreanIsNot() {
        XCTAssertTrue(LyricRuns.writtenWithoutSpaces("แมว"))
        XCTAssertTrue(LyricRuns.writtenWithoutSpaces("夕焼け"))
        XCTAssertFalse(LyricRuns.writtenWithoutSpaces("고양이"))
        XCTAssertFalse(LyricRuns.writtenWithoutSpaces("cat"))
        // A Thai line timed by syllable (invented) rolls as its words...
        let syllables = ["แมว", "นอน", "บน", "โต๊ะ"].enumerated().map {
            LyricsFetcher.Word(text: $0.element, start: Double($0.offset), end: Double($0.offset) + 1)
        }
        let cat = LyricsFetcher.Line(start: 0, end: 4, text: "แมวนอนบนโต๊ะ", words: syllables, isInstrumental: false,
                                     wordTimed: true)
        XCTAssertEqual(LyricRuns.labels(syllables, glue: LyricRuns.glue(line: cat, words: syllables)), ["แมวนอนบนโต๊ะ"])
        XCTAssertEqual(LyricRuns.labels(syllables, glue: LyricRuns.rollingGlue(line: cat, words: syllables)),
                       ["แมว", "นอน", "บน", "โต๊ะ"])
        // ...and a run's reading is spaced by syllable, not run together.
        let read = Dictionary(uniqueKeysWithValues: zip(syllables.map(\.presentationID), zip(["maeo", "non", "bon", "to"], syllables)
            .map { LyricsFetcher.Word(text: $0, start: $1.start, end: $1.end) }))
        XCTAssertEqual(LyricRuns.reading(of: syllables[...], readings: read), "maeo non bon to")
    }

    // MARK: Replace lyrics

    func testLatinReadingsOfChineseAndJapaneseCharactersStandApart() throws {
        // 君の名は timed by character, each character its own phrase.
        let name = characterTimed("君の名は")
        let replaced = try XCTUnwrap(readings(name, options(.replace)))
        XCTAssertEqual(replaced.count, name.words.count)
        XCTAssertEqual(LyricRuns.glue(line: name, words: replaced), [false, false, false, false])
        XCTAssertEqual(LyricRuns.labels(replaced, glue: LyricRuns.glue(line: name, words: replaced)),
                       replaced.map(\.text))
        // A character left as it is (no reading) still keeps to itself.
        let dream = line(["君", "の", "梦", "を"])
        let partly = [LyricsFetcher.Word(text: "kimi", start: 0, end: 0.5), .init(text: "no", start: 0.5, end: 1),
                      .init(text: "梦", start: 1, end: 1.5), .init(text: "wo", start: 1.5, end: 2)]
        XCTAssertEqual(LyricRuns.glue(line: dream, words: partly), [false, false, false, false])
    }

    @MainActor
    func testTheGlueOfALineIsWorkedOutAgainForTheWordsShown() {
        // A line whose first word has no reading: the old cache compared only
        // the first word and reused one display mode's grouping in the other.
        var line = line(["Baby", "夢", "なら", "ば"], reading: nil)
        line.text = "Baby 夢ならば"
        let shown = [line.words[0], .init(text: "yume", start: 0.5, end: 1), .init(text: "nara", start: 1, end: 1.5),
                     .init(text: "ba", start: 1.5, end: 2)]
        let cache = LyricLayerCache()
        var builds = 0
        let original = cache.glue(line: 0, fingerprint: 1, words: line.words) {
            builds += 1; return LyricRuns.glue(line: line, words: line.words)
        }
        let replaced = cache.glue(line: 0, fingerprint: 1, words: shown) {
            builds += 1; return LyricRuns.glue(line: line, words: shown)
        }
        XCTAssertEqual(builds, 2)
        XCTAssertEqual(original, [false, false, true, true])
        XCTAssertEqual(replaced, [false, false, false, false])
        // The same words again are answered from the cache.
        _ = cache.glue(line: 0, fingerprint: 1, words: shown) { builds += 1; return [] }
        XCTAssertEqual(builds, 2)
    }

    // MARK: Echo and lyric cards

    @MainActor
    func testEchoAndCardsSetTheLineAsItReads() {
        let voice = line(["君", "の", "声", "が", "沈", "む", "よ", "う", "に"])
        var spaced = voice
        spaced.text = "君の声が 沈むように"
        let glue = LyricRuns.glue(line: spaced, words: spaced.words)
        XCTAssertEqual(LyricRuns.text(spaced.words, glue: glue), "君の声が 沈むように")
        XCTAssertEqual(LyricCard.words(of: spaced, readings: nil), ["君の声が", "沈むように"])
        // Latin syllables come together as their words; punctuation stays.
        let tonight = LyricsFetcher.Line(start: 0, end: 2, text: "So beautiful, tonight", words: [
            .init(text: "So", start: 0, end: 0.3), .init(text: "beau", start: 0.3, end: 0.6),
            .init(text: "ti", start: 0.6, end: 0.9), .init(text: "ful", start: 0.9, end: 1.2),
            .init(text: ",", start: 1.2, end: 1.2), .init(text: "tonight", start: 1.2, end: 2)],
            isInstrumental: false, wordTimed: true)
        XCTAssertEqual(LyricCard.words(of: tonight, readings: nil), ["So", "beautiful,", "tonight"])
        // A card counts an unspaced line by its length, not as one word.
        XCTAssertEqual(LyricCard.weight(["I", "love", "you"]), 3)
        XCTAssertEqual(LyricCard.weight(["沈むように溶けてゆくように"]), 13.0 / 3, accuracy: 1e-9)
    }

    @MainActor
    func testACardsColoursComeFromTheCoverItShows() throws {
        // No cover yet (a streamed track still being looked up): neutral.
        XCTAssertEqual(LyricCard.palette(for: nil), AlbumWallpaperPalette(grid: ColorExtractor.fallbackHSBPalette))
        // A red cover gives red stops, whatever the wallpaper last extracted.
        let red = NSImage(size: NSSize(width: 64, height: 64))
        red.lockFocus()
        NSColor(srgbRed: 0.85, green: 0.1, blue: 0.1, alpha: 1).setFill()
        NSRect(x: 0, y: 0, width: 64, height: 64).fill()
        red.unlockFocus()
        let stops = LyricCard.palette(for: red).stops
        XCTAssertFalse(stops.isEmpty)
        for stop in stops {
            XCTAssertGreaterThan(stop.x, stop.y * 2, "\(stop)")
            XCTAssertGreaterThan(stop.x, stop.z * 2, "\(stop)")
        }
    }

    @MainActor
    private func inkColumns(_ content: LyricCard.Content) throws -> (bitmap: NSBitmapImageRep, columns: [Int]) {
        let renderer = ImageRenderer(content: LyricCardView(content: content, ink: .white))
        renderer.scale = 0.5
        let bitmap = NSBitmapImageRep(cgImage: try XCTUnwrap(renderer.cgImage))
        var columns: [Int] = []
        for y in stride(from: bitmap.pixelsHigh / 3, to: bitmap.pixelsHigh * 2 / 3, by: 2) {
            for x in 0..<bitmap.pixelsWide where (bitmap.colorAt(x: x, y: y)?.brightnessComponent ?? 0) > 0.85 {
                columns.append(x)
            }
        }
        return (bitmap, columns)
    }

    @MainActor
    func testALongUnspacedLineWrapsInsideTheCard() throws {
        let content = LyricCard.Content(pages: [.init(words: ["夜に駆ける君の声が遠く沈むように溶けてゆくように二人だけの空が広がる夜に"])],
            title: "", artist: "", artwork: nil, palette: AlbumWallpaperPalette(grid: ColorExtractor.fallbackHSBPalette))
        let (bitmap, columns) = try inkColumns(content)
        XCTAssertGreaterThan(columns.count, 200, "the line is drawn")
        // Inside the 80 pt padding on both sides: nothing ran off the card.
        let margin = Int(Double(bitmap.pixelsWide) * 60 / LyricCard.size.width)
        XCTAssertGreaterThanOrEqual(columns.min() ?? 0, margin)
        XCTAssertLessThanOrEqual(columns.max() ?? .max, bitmap.pixelsWide - margin)
    }

    @MainActor
    func testEveryRowOfARightToLeftCardEndsOnTheRight() throws {
        let words = "אני אוהב אותך יותר מכל מה שאפשר לומר במילים הלילה הזה".split(separator: " ").map(String.init)
        let content = LyricCard.Content(pages: [.init(words: words)], title: "", artist: "", artwork: nil,
            palette: AlbumWallpaperPalette(grid: ColorExtractor.fallbackHSBPalette))
        let renderer = ImageRenderer(content: LyricCardView(content: content, ink: .white))
        renderer.scale = 0.5
        let bitmap = NSBitmapImageRep(cgImage: try XCTUnwrap(renderer.cgImage))
        // The rightmost ink of each band of rows.
        var edges: [Int] = []
        var row: (top: Int, right: Int)?
        for y in stride(from: bitmap.pixelsHigh / 4, to: bitmap.pixelsHigh * 3 / 4, by: 1) {
            var right = -1
            for x in stride(from: bitmap.pixelsWide - 1, through: 0, by: -1)
            where (bitmap.colorAt(x: x, y: y)?.brightnessComponent ?? 0) > 0.85 { right = x; break }
            if right >= 0 {
                row = (row?.top ?? y, max(row?.right ?? -1, right))
            } else if let finished = row {
                if y - finished.top > 6 { edges.append(finished.right) }
                row = nil
            }
        }
        if let finished = row { edges.append(finished.right) }
        XCTAssertGreaterThanOrEqual(edges.count, 2, "the line wraps onto several rows")
        let spread = (edges.max() ?? 0) - (edges.min() ?? 0)
        XCTAssertLessThan(Double(spread), Double(bitmap.pixelsWide) * 0.03, "rows flush right: \(edges)")
    }
}
