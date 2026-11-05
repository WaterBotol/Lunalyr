import XCTest
@testable import VersoCustom

/// Apple Music style TTML (`syllable-lyrics` ttmlLocalizations) and AMLL-style
/// inline readings, written the way the sources serialise them: one line, no
/// whitespace between the syllables of a word.
final class TTMLParsingTests: XCTestCase {
    private func document(_ body: String, head: String = "", language: String = "en") -> String {
        """
        <tt xmlns="http://www.w3.org/ns/ttml" xmlns:itunes="http://music.apple.com/lyric-ttml-internal" \
        xmlns:ttm="http://www.w3.org/ns/ttml#metadata" itunes:timing="Word" xml:lang="\(language)"><head><metadata>\
        <ttm:agent type="person" xml:id="v1"/><iTunesMetadata xmlns="http://music.apple.com/lyric-ttml-internal" \
        leadingSilence="0.420">\(head)<songwriters><songwriter>Someone</songwriter></songwriters></iTunesMetadata>\
        </metadata></head><body dur="3:00.000"><div begin="0.000" end="3:00.000">\(body)</div></body></tt>
        """
    }

    func testAppleSyllablesJoinIntoWordsWithSourceSpacing() throws {
        let xml = document("""
        <p begin="10.000" end="14.000" itunes:key="L1" ttm:agent="v1"><span begin="10.000" end="10.300">Ev</span>\
        <span begin="10.300" end="10.600">&apos;ry</span> <span begin="10.600" end="10.900">beau</span>\
        <span begin="10.900" end="11.100">ti</span><span begin="11.100" end="11.600">ful</span> \
        <span begin="11.600" end="12.400">day</span> <span ttm:role="x-bg"><span begin="12.500" end="13.000">(oh</span> \
        <span begin="13.000" end="13.900">yeah)</span></span></p>
        """)
        let line = try XCTUnwrap(TTMLLyricParser.parse(xml, duration: 180).first)
        XCTAssertEqual(line.text, "Ev'ry beautiful day (oh yeah)")
        XCTAssertEqual(line.words.map(\.text), ["Ev'ry", "beautiful", "day", "(oh", "yeah)"])
        XCTAssertEqual(line.words.map(\.start), [10, 10.6, 11.6, 12.5, 13])
        XCTAssertEqual(line.words.map(\.end), [10.6, 11.6, 12.4, 13, 13.9])
        XCTAssertTrue(LyricsFetcher.validWordTiming(line))
        XCTAssertEqual(LyricsFetcher.precision([line]), "Word-by-word")
    }

    func testCJKSyllablesStaySeparateTimedTokens() throws {
        let xml = document("""
        <p begin="40" end="44" itunes:key="L1"><span begin="40.0" end="40.4">君</span><span begin="40.4" end="40.7">の</span>\
        <span begin="40.7" end="41.2">名</span><span begin="41.2" end="41.6">は</span> <span begin="42.0" end="42.5">何</span>\
        <span begin="42.5" end="43.0">処</span></p>\
        <p begin="50" end="53" itunes:key="L2"><span begin="50.0" end="50.3">사</span><span begin="50.3" end="50.6">랑</span>\
        <span begin="50.6" end="51.0">해</span> <span begin="51.2" end="51.6">너</span><span begin="51.6" end="52.0">를</span></p>\
        <p begin="60" end="62" itunes:key="L3"><span begin="60.0" end="60.6">LOVE</span><span begin="60.6" end="61.0">し</span>\
        <span begin="61.0" end="61.5">て</span></p>
        """, language: "ja")
        let lines = TTMLLyricParser.parse(xml, duration: 180)
        XCTAssertEqual(lines.count, 3)
        // Glued pieces keep their own karaoke timing; only `text` shows where
        // the source had whitespace.
        XCTAssertEqual(lines[0].text, "君の名は 何処")
        XCTAssertEqual(lines[0].words.map(\.text), ["君", "の", "名", "は", "何", "処"])
        XCTAssertEqual(lines[0].words[3].start, 41.2, accuracy: 0.001)
        XCTAssertEqual(lines[1].text, "사랑해 너를")
        XCTAssertEqual(lines[1].words.map(\.text), ["사", "랑", "해", "너", "를"])
        XCTAssertEqual(lines[2].text, "LOVEして")
        XCTAssertEqual(lines[2].words.map(\.text), ["LOVE", "し", "て"])
        XCTAssertTrue(lines.allSatisfy(LyricsFetcher.validWordTiming))
    }

    func testOverlappingBackgroundVocalsFollowTheMainWords() throws {
        // The ad-lib starts before "you": sorting by time used to interleave
        // it, so the line failed validation and lost its word timing.
        let xml = document("""
        <p begin="20.000" end="23.500" itunes:key="L2" ttm:agent="v1"><span begin="20.000" end="20.400">I</span> \
        <span begin="20.400" end="21.000">love</span> <span begin="21.600" end="23.000">you</span><span ttm:role="x-bg">\
        <span begin="21.200" end="21.800">(love</span> <span begin="21.800" end="23.500">you)</span></span></p>
        """)
        let line = try XCTUnwrap(TTMLLyricParser.parse(xml, duration: 180).first)
        XCTAssertEqual(line.text, "I love you (love you)")
        XCTAssertEqual(line.words.map(\.text), ["I", "love", "you", "(love", "you)"])
        // Main words keep their native timing.
        XCTAssertEqual(line.words.prefix(3).map(\.start), [20, 20.4, 21.6])
        XCTAssertEqual(line.words.prefix(3).map(\.end), [20.4, 21, 23])
        XCTAssertEqual(line.words[3].start, 21.6, accuracy: 0.001)
        XCTAssertEqual(line.words[4].end, 23.5, accuracy: 0.001)
        XCTAssertTrue(line.wordTimed)
        XCTAssertTrue(LyricsFetcher.validWordTiming(line))
        XCTAssertEqual(LyricsFetcher.precision([line]), "Word-by-word")
    }

    func testBackgroundSungFirstLeadsAndUnbracketedBackgroundIsParenthesised() {
        let xml = document("""
        <p begin="30" end="33" itunes:key="L3"><span ttm:role="x-bg"><span begin="30.0" end="30.5">Hey!</span></span>\
        <span begin="30.8" end="31.2">Here</span> <span begin="31.2" end="31.6">we</span> <span begin="31.6" end="32.4">go</span></p>\
        <p begin="34" end="37" itunes:key="L4"><span begin="34.0" end="35.0">Stay</span><span ttm:role="x-bg">\
        <span begin="35.2" end="35.6">oh</span> <span begin="35.6" end="36.4">yeah</span></span></p>
        """)
        let lines = TTMLLyricParser.parse(xml, duration: 180)
        XCTAssertEqual(lines.map(\.text), ["(Hey!) Here we go", "Stay (oh yeah)"])
        XCTAssertEqual(lines[0].words.map(\.text), ["(Hey!)", "Here", "we", "go"])
        XCTAssertEqual(lines[1].words.map(\.text), ["Stay", "(oh", "yeah)"])
        XCTAssertTrue(lines.allSatisfy(LyricsFetcher.validWordTiming))
    }

    func testAppleTransliterationsAndTranslationsAttachByKey() throws {
        let head = """
        <translations><translation type="replacement" xml:lang="en"><text for="L1">What is your name</text>\
        <text for="L2">I love you (love you)</text></translation><translation type="replacement" xml:lang="zh-Hans">\
        <text for="L1">你的名字是</text></translation></translations><transliterations><transliteration xml:lang="ja-Latn">\
        <text for="L1"><span begin="40.0" end="40.4">ki</span><span begin="40.0" end="40.4">mi</span> \
        <span begin="40.4" end="40.7">no</span> <span begin="40.7" end="41.2">na</span> <span begin="41.2" end="41.6">wa</span></text>\
        <text for="L2"><span begin="50.0" end="50.6">a</span><span begin="50.6" end="51.0">i</span> \
        <span ttm:role="x-bg"><span begin="51.2" end="51.6">(a</span><span begin="51.6" end="52.0">i)</span></span></text>\
        </transliteration></transliterations>
        """
        let body = """
        <p begin="40" end="44" itunes:key="L1"><span begin="40.0" end="40.4">君</span><span begin="40.4" end="40.7">の</span>\
        <span begin="40.7" end="41.2">名</span><span begin="41.2" end="41.6">は</span></p>\
        <p begin="50" end="53" itunes:key="L2"><span begin="50.0" end="51.0">愛</span><span ttm:role="x-bg">\
        <span begin="51.2" end="52.0">(愛)</span></span></p>\
        <p begin="60" end="62" itunes:key="L3"><span begin="60.0" end="61.0">Yeah</span></p>
        """
        let english = TTMLLyricParser.parse(document(body, head: head, language: "ja"), duration: 180, preferredLanguages: ["en-AU"])
        XCTAssertEqual(english.count, 3)
        XCTAssertEqual(english[0].text, "君の名は")
        XCTAssertEqual(english[0].romanization, "kimi no na wa")
        XCTAssertEqual(english[0].translation, "What is your name")
        XCTAssertEqual(english[1].text, "愛 (愛)")
        XCTAssertEqual(english[1].romanization, "ai (ai)")
        XCTAssertNil(english[2].romanization)
        XCTAssertNil(english[2].translation)
        XCTAssertTrue(english.allSatisfy(LyricsFetcher.validWordTiming))
        let chinese = TTMLLyricParser.parse(document(body, head: head, language: "ja"), duration: 180, preferredLanguages: ["zh-Hans-CN", "en"])
        XCTAssertEqual(chinese[0].translation, "你的名字是")
        let other = TTMLLyricParser.parse(document(body, head: head, language: "ja"), duration: 180, preferredLanguages: ["fr-FR"])
        XCTAssertEqual(other[0].translation, "What is your name")
        // Songwriter metadata never becomes lyric text.
        XCTAssertFalse(english.contains { $0.text.contains("Someone") })
    }

    func testATranslationTheListenerCannotReadIsNotShown() {
        // An AMLL-style TTML of a Japanese song whose only translation is Chinese.
        let xml = """
        <tt xmlns:ttm="http://www.w3.org/ns/ttml#metadata"><body><div><p begin="1" end="3">\
        <span begin="1" end="2">月が</span><span begin="2" end="3">きれい</span>\
        <span ttm:role="x-translation" xml:lang="zh-CN">月色真美</span><span ttm:role="x-roman">tsuki ga kirei</span>\
        </p></div></body></tt>
        """
        func line(_ languages: [String]) -> LyricsFetcher.Line? {
            TTMLLyricParser.parse(xml, duration: 10, preferredLanguages: languages).first
        }
        XCTAssertNil(line(["en-AU"])?.translation, "an English listener gets the romaji, not a Chinese line too")
        XCTAssertEqual(line(["en-AU"])?.romanization, "tsuki ga kirei")
        XCTAssertEqual(line(["en-AU", "zh-Hant-TW"])?.translation, "月色真美", "a Chinese reader keeps it")
        XCTAssertEqual(line(["ja-JP"])?.translation, "月色真美", "Japanese readers read the Han script")
    }

    func testTranslationScriptsFollowTheLanguagesRead() {
        typealias Languages = TranslationLanguages
        XCTAssertEqual(Languages.scripts(of: "月色真美"), [.han])
        XCTAssertEqual(Languages.scripts(of: "こんにちは 世界"), [.kana, .han])
        XCTAssertEqual(Languages.scripts(of: "Café, 2024!"), [.latin])
        XCTAssertEqual(Languages.scripts(of: "Привет"), [.cyrillic])
        XCTAssertEqual(Languages.scripts(of: "♪ 123 …"), [])
        XCTAssertEqual(Languages.scripts(ofLanguage: "sr-Latn-RS"), [.latin])
        XCTAssertEqual(Languages.scripts(ofLanguage: "zh_Hant"), [.han])
        XCTAssertEqual(Languages.scripts(ofLanguage: "ko-KR"), [.hangul])
        XCTAssertTrue(Languages.readable(["I love you"], languages: ["ja-JP"]), "Latin always counts")
        XCTAssertFalse(Languages.readable(["사랑해"], languages: ["en-US", "ja"]))
        XCTAssertTrue(Languages.readable(["사랑해"], languages: ["en-US", "ko"]))
        XCTAssertTrue(Languages.readable(["♪"], languages: ["en"]), "nothing to read, nothing held back")
        XCTAssertTrue(Languages.readable([], languages: ["en"]))
        // A whole translation: a name in Latin letters doesn't make a Chinese
        // one readable, nor a name in kana an English one unreadable.
        XCTAssertFalse(Languages.readable(["月色真美", "Romeo", "奔向夜晚", "…"], languages: ["en-AU"]))
        XCTAssertTrue(Languages.readable(["The moon is lovely", "ロミオ", "Run into the night"], languages: ["ko-KR"]))
        XCTAssertFalse(Languages.readable(["我爱你 Taylor"], languages: ["en"]), "mixed with a script not read")
    }

    func testWhetherATranslationCanBeReadIsDecidedForTheWholeSong() {
        // A Japanese song whose only translation is Chinese, one line of
        // which is just a name in Latin letters.
        let xml = """
        <tt xmlns:ttm="http://www.w3.org/ns/ttml#metadata"><body><div>\
        <p begin="1" end="3"><span begin="1" end="3">月がきれい</span><span ttm:role="x-translation" xml:lang="zh-CN">月色真美</span></p>\
        <p begin="4" end="6"><span begin="4" end="6">ロミオ</span><span ttm:role="x-translation" xml:lang="zh-CN">Romeo</span></p>\
        <p begin="7" end="9"><span begin="7" end="9">夜に駆ける</span><span ttm:role="x-translation" xml:lang="zh-CN">奔向夜晚</span></p>\
        </div></body></tt>
        """
        func translations(_ languages: [String]) -> [String?] {
            TTMLLyricParser.parse(xml, duration: 20, preferredLanguages: languages).map(\.translation)
        }
        XCTAssertEqual(translations(["en-AU"]), [nil, nil, nil],
                       "not under the one line that happens to be Latin, switching the stage between two and three rows")
        XCTAssertEqual(translations(["fr-FR", "de"]), [nil, nil, nil])
        XCTAssertEqual(translations(["zh-Hans-CN"]), ["月色真美", "Romeo", "奔向夜晚"])
        XCTAssertEqual(translations(["ja-JP"]), ["月色真美", "Romeo", "奔向夜晚"], "Japanese readers read the Han script")
    }

    func testAppleSuppliedTranslationsAreJudgedForTheWholeSongToo() {
        // An English translation one line of which keeps a name in kana,
        // and a Chinese one, for a Korean listener who reads neither tag.
        let head = """
        <translations><translation type="replacement" xml:lang="en"><text for="L1">The moon is lovely</text>\
        <text for="L2">ロミオ</text><text for="L3">Run into the night</text></translation>\
        <translation type="replacement" xml:lang="zh-Hans"><text for="L1">月色真美</text><text for="L2">Romeo</text>\
        <text for="L3">奔向夜晚</text></translation></translations>
        """
        let body = """
        <p begin="1" end="3" itunes:key="L1"><span begin="1" end="3">月がきれい</span></p>\
        <p begin="4" end="6" itunes:key="L2"><span begin="4" end="6">Romeo</span></p>\
        <p begin="7" end="9" itunes:key="L3"><span begin="7" end="9">夜に駆ける</span></p>
        """
        let korean = TTMLLyricParser.parse(document(body, head: head, language: "ja"), duration: 180, preferredLanguages: ["ko-KR"])
        XCTAssertEqual(korean.map(\.translation), ["The moon is lovely", "ロミオ", "Run into the night"],
                       "the English translation throughout, not the Chinese one where English has kana")
    }

    func testInlineReadingsAttachAndStayOutOfTheLyric() throws {
        let xml = """
        <tt xmlns:ttm="http://www.w3.org/ns/ttml#metadata"><body><div><p begin="1" end="3">\
        <span begin="1" end="1.5">你</span><span begin="1.5" end="2">好</span>\
        <span ttm:role="x-translation" xml:lang="en">Hello</span><span ttm:role="x-roman">ni hao</span>\
        <span ttm:role="x-bg"><span begin="2.1" end="2.8">(你好)</span><span ttm:role="x-roman">(ni hao)</span></span></p>\
        <p begin="4" end="5"><metadata>not a lyric</metadata><span begin="4" end="5">Hi</span></p></div></body></tt>
        """
        let lines = TTMLLyricParser.parse(xml, duration: 10)
        XCTAssertEqual(lines.map(\.text), ["你好 (你好)", "Hi"])
        XCTAssertEqual(lines[0].words.map(\.text), ["你", "好", "(你好)"])
        XCTAssertEqual(lines[0].romanization, "ni hao (ni hao)")
        XCTAssertEqual(lines[0].translation, "Hello")
        XCTAssertTrue(LyricsFetcher.validWordTiming(lines[0]))
        XCTAssertTrue(lines[1].wordTimed)
    }

    func testInlineReadingsInSeveralLanguagesAreNotRunTogether() {
        let xml = """
        <tt xmlns:ttm="http://www.w3.org/ns/ttml#metadata"><body><div><p begin="1" end="3">\
        <span begin="1" end="1.5">你</span><span begin="1.5" end="2">好</span>\
        <span ttm:role="x-translation" xml:lang="en">Hello</span><span ttm:role="x-translation" xml:lang="ja">こんにちは</span>\
        <span ttm:role="x-roman">ㄋㄧˇ ㄏㄠˇ</span><span ttm:role="x-roman" xml:lang="zh-Latn">ni hao</span>\
        <span ttm:role="x-bg"><span begin="2.1" end="2.8">(你好)</span>\
        <span ttm:role="x-translation" xml:lang="ja">(こんにちは)</span><span ttm:role="x-translation" xml:lang="en">(hi)</span>\
        <span ttm:role="x-translation" xml:lang="ko"></span></span></p></div></body></tt>
        """
        func line(_ languages: [String]) -> LyricsFetcher.Line? {
            TTMLLyricParser.parse(xml, duration: 10, preferredLanguages: languages).first
        }
        XCTAssertEqual(line(["ja-JP"])?.translation, "こんにちは (こんにちは)")
        XCTAssertEqual(line(["en-GB"])?.translation, "Hello (hi)")
        // No preferred language: the first one in the file they can read.
        XCTAssertEqual(line(["fr-FR"])?.translation, "Hello (hi)")
        XCTAssertEqual(line(["zh-Hans-CN"])?.translation, "Hello (hi)", "English before Japanese for a Chinese reader")
        // An empty entry never wins, even in the preferred language.
        XCTAssertEqual(line(["ko-KR", "ja"])?.translation, "こんにちは (こんにちは)")
        // A Latin-script reading is preferred to one in another script.
        XCTAssertEqual(line(["en"])?.romanization, "ni hao")
        XCTAssertEqual(line(["en"])?.text, "你好 (你好)")
    }

    func testCommunityFilesWithLooseBackgroundTimingKeepWordTiming() {
        // AMLL TTML DB exports: a timed x-bg wrapper with its own translation,
        // a background vocal starting before its paragraph, an echo timed
        // after it, and a syllable span that runs on into the next word.
        let xml = document("""
        <p begin="10.950" end="14.920" itunes:key="L1" ttm:agent="v1"><span begin="10.950" end="11.570">I</span> \
        <span begin="11.570" end="11.930">be</span><span begin="11.930" end="12.500">lie</span><span begin="12.500" end="12.800">ve you&apos;ll</span> \
        <span begin="12.800" end="12.970">be</span> <span begin="12.970" end="13.400">some</span><span begin="13.400" end="13.980">bo</span>\
        <span begin="13.980" end="14.810">dy</span><span ttm:role="x-translation" xml:lang="zh-CN">我相信你会是那一个</span>\
        <span ttm:role="x-bg" begin="14.050" end="14.920"><span begin="14.050" end="14.580">(Bo</span><span begin="14.580" end="14.920">dy)</span>\
        <span ttm:role="x-translation" xml:lang="zh-CN">某人</span></span></p>\
        <p begin="2:33.210" end="2:38.460" itunes:key="L2"><span begin="2:33.210" end="2:33.800">Uh</span> \
        <span begin="2:33.800" end="2:34.480">oh</span><span ttm:role="x-bg" begin="2:33.090" end="2:38.460">\
        <span begin="2:33.090" end="2:33.650">(Uh</span> <span begin="2:37.520" end="2:38.460">oh)</span></span></p>\
        <p begin="4:16.528" end="4:22.726" itunes:key="L3"><span begin="4:16.528" end="4:16.762">说</span>\
        <span begin="4:16.762" end="4:22.726">见</span><span ttm:role="x-bg" begin="4:24.694" end="4:28.069">(Hoo)</span></p>
        """)
        // Read by a Chinese listener, whom its translation is for.
        let lines = TTMLLyricParser.parse(xml, duration: 300, preferredLanguages: ["zh-Hans-CN"])
        XCTAssertEqual(lines.map(\.text), ["I believe you'll be somebody (Body)", "Uh oh (Uh oh)", "说见 (Hoo)"])
        XCTAssertEqual(lines[0].words.map(\.text), ["I", "believe", "you'll", "be", "somebody", "(Body)"])
        XCTAssertEqual(lines[0].words[1].end, 12.575, accuracy: 0.001)
        XCTAssertEqual(lines[0].translation, "我相信你会是那一个 (某人)")
        XCTAssertNil(TTMLLyricParser.parse(xml, duration: 300, preferredLanguages: ["en-AU"])[0].translation,
                     "an English listener is not shown a Chinese translation of English lyrics")
        XCTAssertEqual(lines[1].words.first?.start ?? 0, 153.21, accuracy: 0.001)
        XCTAssertEqual(lines[2].end, 268.069, accuracy: 0.001)
        XCTAssertTrue(lines.allSatisfy(LyricsFetcher.validWordTiming))
    }

    func testOverlappingDuetPartsReportTheLineStillBeingSung() {
        let xml = document("""
        <p begin="10" end="15" itunes:key="L1" ttm:agent="v1"><span begin="10" end="11">I&apos;ll</span> \
        <span begin="11" end="12">be</span> <span begin="12.5" end="15">there</span></p>\
        <p begin="12" end="17" itunes:key="L2" ttm:agent="v2"><span begin="12" end="13">Al</span><span begin="13" end="14">ways</span> \
        <span begin="14" end="17">near</span></p>
        """)
        let lines = TTMLLyricParser.parse(xml, duration: 60)
        let index = LyricLineIndex(lines: lines)
        XCTAssertEqual(lines.map(\.text), ["I'll be there", "Always near"])
        XCTAssertEqual(index.line(at: 13), 1)
        // The first part is still being sung after the second begins.
        XCTAssertEqual(LyricLineIndex.heldLine(before: 1, in: lines, at: 13), 0)
        XCTAssertNil(LyricLineIndex.heldLine(before: 1, in: lines, at: 15.2))
        let lrc = LyricsFetcher.parseLRC("[00:10]One line\n[00:12]Next line", trackDuration: 20)
        XCTAssertNil(LyricLineIndex.heldLine(before: 1, in: lrc, at: 12.5))
        XCTAssertNil(LyricLineIndex.heldLine(before: 0, in: lines, at: 13))
    }

    func testWhitespaceBetweenSpansStillSeparatesWords() {
        // Pretty-printed files put a newline between every span: that is
        // whitespace in the source, so each span stays its own word.
        let xml = """
        <tt><body><div>
          <p begin="1" end="4">
            <span begin="1" end="2">Keep</span>
            <span begin="2" end="3">on</span>
          </p>
        </div></body></tt>
        """
        let line = TTMLLyricParser.parse(xml, duration: 10).first
        XCTAssertEqual(line?.text, "Keep on")
        XCTAssertEqual(line?.words.map(\.text), ["Keep", "on"])
        XCTAssertEqual(line?.wordTimed, true)
    }
}
