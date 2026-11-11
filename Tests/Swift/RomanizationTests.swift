import XCTest
@testable import VersoCustom

/// Readings are pure functions of the lyric lines and an options snapshot, so
/// these tests never touch RomanizationOptions.shared or user defaults. Lines
/// are built word by word where the system's own word split would otherwise
/// decide the result, so the tests do not depend on it.
final class RomanizationTests: XCTestCase {
    private func options(_ display: RomanizationOptions.Display = .below,
                         language: RomanizationOptions.Language = .automatic,
                         preferSaved: Bool = true) -> RomanizationOptions.Snapshot {
        .init(display: display, language: language, toneMarks: true, preferSaved: preferSaved)
    }

    /// A word-timed line with one word per character, as syllable-synced
    /// Japanese, Chinese and Korean lyrics often arrive.
    private func characterTimed(_ text: String, start: Double = 10, reading: String? = nil) -> LyricsFetcher.Line {
        var time = start
        let words = text.filter { !$0.isWhitespace }.map { character -> LyricsFetcher.Word in
            defer { time += 0.4 }
            return .init(text: String(character), start: time, end: time + 0.4)
        }
        return .init(start: start, end: time, text: text, words: words, isInstrumental: false,
                     wordTimed: true, romanization: reading)
    }

    /// A line with the given words, timed word by word or only as a line.
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

    private func lineTimed(_ text: String, start: Double = 0, reading: String? = nil) -> LyricsFetcher.Line {
        var line = LyricsFetcher.makeLine(text: text, start: start, end: start + 4)
        line.romanization = reading
        return line
    }

    private func readings(_ line: LyricsFetcher.Line, _ snapshot: RomanizationOptions.Snapshot? = nil,
                          in song: [LyricsFetcher.Line]? = nil) -> [LyricsFetcher.Word]? {
        let lines = song ?? [line]
        let index = lines.firstIndex { $0.text == line.text && $0.start == line.start } ?? 0
        return LyricRomanizer.readings(lines, options: snapshot ?? options())[index]
    }

    private func texts(_ line: LyricsFetcher.Line, _ snapshot: RomanizationOptions.Snapshot? = nil,
                       in song: [LyricsFetcher.Line]? = nil) -> [String]? {
        readings(line, snapshot, in: song)?.map(\.text)
    }

    private func joined(_ texts: [String]?) -> String {
        (texts ?? []).filter { !$0.isEmpty }.joined(separator: " ")
    }

    private func letters(_ text: String) -> String { text.lowercased().filter(\.isLetter) }

    /// A Japanese song around `line`, so its kanji are read as Japanese.
    private func japaneseSong(_ line: LyricsFetcher.Line) -> [LyricsFetcher.Line] {
        [line, lineTimed("ありがとう", start: 100)]
    }

    // MARK: - Japanese

    func testJapaneseReadsWholeLinesWithParticlesAndSokuon() {
        let cases = [
            "君の名は": "kimi no na wa", "未来へ": "mirai e", "思った": "omotta", "良かった": "yokatta",
            "今日も": "kyou mo", "大人": "otona", "僕は君が好き": "boku wa kimi ga suki",
            "こんにちは": "konnichiwa", "愛してる": "aishiteru", "待ってて": "mattete", "私は": "watashi wa",
        ]
        for (text, expected) in cases {
            XCTAssertEqual(LyricRomanizer.romanize(text, language: .japanese, toneMarks: false), expected, text)
        }
        // The tokenizer always says nan for 何; it is nani before most words.
        XCTAssertTrue(JapaneseReading.romanize("何を見ていたの").hasPrefix("nani wo"))
        XCTAssertTrue(JapaneseReading.romanize("何も").hasPrefix("nani"))
        XCTAssertTrue(JapaneseReading.romanize("何でもない").hasPrefix("nandemo"))
    }

    func testJapaneseLongVowelsSmallKanaAndPunctuation() {
        XCTAssertEqual(JapaneseReading.romanize("ラーメン"), "raamen")
        XCTAssertEqual(JapaneseReading.romanize("コーヒー"), "koohii")
        XCTAssertEqual(JapaneseReading.romanize("すごーい"), "sugooi")
        XCTAssertEqual(JapaneseReading.romanize("ねぇ"), "nee")
        XCTAssertEqual(JapaneseReading.romanize("ヴァイオリン"), "vaiorin")
        XCTAssertEqual(JapaneseReading.romanize("君と、僕と。"), "kimi to, boku to.")
        XCTAssertFalse(JapaneseReading.romanize("思った").contains("~"))
    }

    func testJapanesePhrasesAreReadOnTheirOwn() {
        // Read across a space or a Latin word, 君 came out as the suffix kun
        // and 行 as kou.
        for text in ["ねえ 君は", "今 君に会いたい", "Hey 君が好き", "Ah 君の名は", "もう一度 君に"] {
            let reading = JapaneseReading.romanize(text)
            XCTAssertTrue(reading.contains("kimi"), "\(text): \(reading)")
            XCTAssertFalse(reading.contains("kun"), "\(text): \(reading)")
        }
        XCTAssertEqual(JapaneseReading.romanize("ねえ 君は"), "nee kimi wa")
        XCTAssertEqual(JapaneseReading.romanize("Hey 君が好き"), "Hey kimi ga suki")
        let away = JapaneseReading.romanize("Yeah yeah 行かないで")
        XCTAssertTrue(away.hasPrefix("Yeah yeah ") && away.contains("ikanaide"), away)
    }

    func testStandaloneHaIsAParticleUnlessItBeginsAWord() {
        XCTAssertEqual(JapaneseReading.romanize("君の名は"), "kimi no na wa")
        XCTAssertTrue(JapaneseReading.romanize("「愛」は").hasSuffix("wa"))
        // A line going on from the one before starts with the particle,
        // which stays apart from the word after it (not hawaratta, heikou).
        let particles = [
            "は笑った": "wa waratta", "へ行こう": "e ikou", "は君の": "wa kimi no", "はどこ": "wa doko",
            "ねえ は笑った": "nee wa waratta", "はアイス": "wa aisu", "は2人で": "wa futari de", "へと": "e to",
        ]
        for (text, expected) in particles {
            XCTAssertEqual(JapaneseReading.romanize(text), expected, text)
        }
        XCTAssertEqual(texts(line(["は", "笑った"], wordTimed: false)), ["wa", "waratta"])
        XCTAssertEqual(texts(line(["へ", "行こう"], wordTimed: false)), ["e", "ikou"])
        // At the start of a phrase は also begins a word the tokenizer split
        // it from, or is an interjection.
        for text in ["はなれないで", "ねえ はなれないで"] {
            let reading = JapaneseReading.romanize(text)
            XCTAssertTrue(reading.contains("hanare"), "\(text): \(reading)")
            XCTAssertFalse(reading.contains("wa"), "\(text): \(reading)")
        }
        XCTAssertEqual(JapaneseReading.romanize("はなれないで"), "hanarenaide")
        let apart = characterTimed("はなれないで")
        XCTAssertEqual(LyricRomanizer.lineReading(readings(apart), for: apart), "hanarenaide")
        XCTAssertEqual(JapaneseReading.romanize("は？"), "ha?")
        XCTAssertEqual(JapaneseReading.romanize("はっ"), "ha")
    }

    func testWordsTheTokenizerSplitsAfterHaStayWhole() {
        // The tokenizer splits these words after は or へ as it splits the
        // particle from a word (は|しれ like は|どこ), and the particle is
        // never followed by another particle of one kana (はね, はよ). They
        // read whole in every form, not wa shire, wa gushiyou or e rikutsu.
        let words = [
            "はしれ": "hashire", "はしれ！": "hashire!", "はしれメロス": "hashire merosu", "はしれはしれ": "hashire hashire",
            "ねえ はしれ": "nee hashire", "はくしゅ": "hakushu", "はくしゅして": "hakushu shite", "はね": "hane",
            "はよ": "hayo", "はよ来て": "hayo kite", "はで": "hade", "はぐして": "hagushite", "はぐしよう": "hagushiyou",
            "はぐしたい": "hagushitai", "へ理屈": "herikutsu", "へ理屈を": "herikutsu wo", "へりくつ": "herikutsu",
            "へりくつを": "herikutsu wo",
        ]
        for (text, expected) in words {
            XCTAssertEqual(JapaneseReading.romanize(text), expected, text)
        }
        // A line going on from the one before still starts with the particle,
        // also before a longer particle the tokenizer splits in two (で|も).
        let particles = [
            "はもう": "wa mou", "はそれ": "wa sore", "はずっと": "wa zutto", "へかえろう": "e kaerou", "はでも": "wa demo",
            "はでもね": "wa demo ne", "はから": "wa kara", "はまで": "wa made", "はより": "wa yori", "はしあわせ": "wa shiawase",
        ]
        for (text, expected) in particles {
            XCTAssertEqual(JapaneseReading.romanize(text), expected, text)
        }
    }

    func testKanjiTheTokenizerReadsAsMandarinGetJapaneseOrNothing() {
        let pinyin = CharacterSet(charactersIn: "áàǎéèěíìǐóòǒúùǔǘǚǜ")
        for (text, expected) in ["2人で歩いた": "futari", "1人きり": "hitori", "僕ら2人": "futari"] {
            let reading = JapaneseReading.romanize(text)
            XCTAssertTrue(reading.contains(expected), "\(text): \(reading)")
            XCTAssertNil(reading.rangeOfCharacter(from: pinyin), "\(text): \(reading)")
        }
        // Simplified Chinese kanji in a Japanese lyric get no reading, and
        // the ending after one does not stick to the particle before it.
        let dream = line(["君", "の", "梦", "を", "见", "た"], wordTimed: false)
        XCTAssertEqual(texts(dream), ["kimi", "no", "", "wo", "", "ta"])
        XCTAssertEqual(texts(dream, options(.replace)), ["kimi", "no", "梦", "wo", "见", "ta"])
    }

    func testFullWidthDigitsCountPeopleAsHalfWidthOnesDo() {
        // The tokenizer read ２ apart from 人, as ２ nin.
        let counted = [
            "２人で": "futari de", "１人きり": "hitori kiri", "２人の未来": "futari no mirai", "僕ら２人": "boku ra futari",
            "は２人で": "wa futari de", "２人で歩いた": "futari de aruita",
        ]
        for (text, expected) in counted {
            XCTAssertEqual(JapaneseReading.romanize(text), expected, text)
        }
        // Other numbers are read as their half-width forms are, and keep
        // their digits as written, so "Below lyrics" sets no reading under
        // a digit that only repeats it.
        XCTAssertEqual(JapaneseReading.romanize("３回"), "３ kai")
        let time = lineTimed("５時に会おう")
        XCTAssertEqual(texts(time), ["５", "ji", "ni", "aou"])
        XCTAssertNil(LyricReadingPairs.match(readings(time), to: time.words)[time.words[0].presentationID])
        // On a line, ２人 reads futari wherever its words fall.
        XCTAssertEqual(texts(line(["２人で", "歩いた"], wordTimed: false)), ["futari de", "aruita"])
        let together = line(["２人", "で", "歩いた"])
        XCTAssertEqual(texts(together, in: japaneseSong(together)), ["futari", "de", "aruita"])
        let apart = characterTimed("２人で")
        XCTAssertEqual(LyricRomanizer.lineReading(readings(apart), for: apart), "futari de")
    }

    func testMonthsAndDaysAfterDigitsReadAsDates() {
        // The tokenizer read 月 after a number now gatsu, now tsuki (3月 gatsu,
        // 4月 tsuki, in either width), and 日 always ka.
        let fullWidth = ["１", "２", "３", "４", "５", "６", "７", "８", "９", "１０", "１１", "１２"]
        for (index, wide) in fullWidth.enumerated() {
            XCTAssertEqual(JapaneseReading.romanize("\(index + 1)月"), "\(index + 1) gatsu")
            XCTAssertEqual(JapaneseReading.romanize("\(wide)月"), "\(wide) gatsu")
        }
        let dates = [
            "４月は君の嘘": "４ gatsu wa kimi no uso", "4月は君の嘘": "4 gatsu wa kimi no uso", "７月の空": "７ gatsu no sora",
            "12月24日": "12 gatsu 24 ka", "１２月２５日": "１２ gatsu ２５ nichi", "2月14日": "2 gatsu 14 ka",
            "365日": "365 nichi", "1日": "1 nichi", "３日": "３ ka", "１０日": "１０ ka", "20日": "20 ka",
            "365日間": "365 nichikan", "７日間": "７ kakan",
        ]
        for (text, expected) in dates {
            XCTAssertEqual(JapaneseReading.romanize(text), expected, text)
        }
        // Without a number they are read as before, and so are other counters.
        let words = ["月が綺麗": "tsuki ga kirei", "今月": "kongetsu", "毎日": "mainichi", "3ヶ月": "3 kagetsu", "５時": "５ ji"]
        for (text, expected) in words {
            XCTAssertEqual(JapaneseReading.romanize(text), expected, text)
        }
        // On a line the month's reading sits under 月, the digit keeping none.
        let april = line(["4", "月", "は", "君", "の", "嘘"])
        XCTAssertEqual(texts(april), ["4", "gatsu", "wa", "kimi", "no", "uso"])
        XCTAssertNil(LyricReadingPairs.match(readings(april), to: april.words)[april.words[0].presentationID])
    }

    func testCharacterTimedJapaneseIsSharedOutByCharacter() {
        // Read in context, and shared out so each character keeps a short
        // reading under it; the line reading writes the phrases whole.
        let today = characterTimed("今日も")
        XCTAssertEqual(texts(today), ["kyo", "u", "mo"])
        XCTAssertEqual(LyricRomanizer.lineReading(readings(today), for: today), "kyou mo")
        let thought = characterTimed("思った")
        XCTAssertEqual(texts(thought), ["omo", "", "tta"])
        XCTAssertEqual(LyricRomanizer.lineReading(readings(thought), for: thought), "omotta")
        // Alone, kanji could be Chinese; in a Japanese song they read as Japanese.
        let adult = characterTimed("大人", start: 20)
        let parts = texts(adult, in: [today, adult]) ?? []
        XCTAssertEqual(parts.joined(), "otona")
        XCTAssertFalse(parts.contains(""))
    }

    func testCharacterTimedReadingsCarryTheirPhrasesTime() {
        let today = characterTimed("今日も")
        let below = readings(today)
        // kyo and u read 今日 together, so both have its time.
        XCTAssertEqual(below?.map(\.start), [today.words[0].start, today.words[0].start, today.words[2].start])
        XCTAssertEqual(below?.map(\.end), [today.words[1].end, today.words[1].end, today.words[2].end])
        // Replacing shows 今日 as one reading across both characters.
        let replaced = readings(today, options(.replace))
        XCTAssertEqual(replaced?.map(\.text), ["kyou", "mo"])
        XCTAssertEqual(replaced?.first?.start, today.words[0].start)
        XCTAssertEqual(replaced?.first?.end, today.words[1].end)
    }

    func testLineTimedJapaneseMapsPhrasesOntoWords() {
        let line = line(["思っ", "た", "より"], wordTimed: false)
        XCTAssertEqual(texts(line), ["omo", "tta", "yori"])
        XCTAssertNil(LyricRomanizer.lineReading(readings(line), for: line))
    }

    func testSpacesBetweenTimedJapaneseWords() {
        // A space at only some word breaks is a pause: っ before it is not doubled.
        let pause = texts(line(["あっ", "君だ"], spaced: true))
        XCTAssertFalse(joined(pause).contains("akk"), joined(pause))
        XCTAssertTrue(joined(pause).contains("kimi"), joined(pause))
        // Spaces at every break are the markup's, so 今日 is still read whole.
        XCTAssertEqual(texts(line(["今", "日", "も"], spaced: true)), ["kyo", "u", "mo"])
    }

    func testSpacesBetweenTimedJapanesePhrasesAreKept() {
        // Phrases of several characters spaced apart are the lyric's own
        // words: 今 is not read into 今日 across the space.
        XCTAssertEqual(texts(line(["今", "日差しが", "眩しい"], spaced: true)), ["ima", "hizashi ga", "mabushii"])
        XCTAssertEqual(texts(line(["今", "日が", "沈む"], spaced: true)), ["ima", "hi ga", "shizumu"])
        // Spaces between single characters or syllables are still the markup's...
        XCTAssertEqual(texts(line(["今", "日", "も", "君に", "会いたい"], spaced: true)),
                       ["kyo", "u", "mo", "kimi ni", "aitai"])
        XCTAssertEqual(texts(line(["きょ", "う", "も"], spaced: true)), ["kyo", "u", "mo"])
        // ...as is every space on a line timed one character at a time.
        let held = line(["抱", "きし", "め", "て"], spaced: true)
        XCTAssertEqual(LyricRomanizer.lineReading(readings(held), for: held), "dakishimete")
    }

    func testMarkupSpacesInsideAPhraseAreDropped() {
        // On a line spaced at every break, a particle or an ending only ever
        // follows a word, so a lone particle keeps its reading (not sekai ha)...
        XCTAssertEqual(texts(line(["世界", "は", "美しい"], spaced: true)), ["sekai", "wa", "utsukushii"])
        XCTAssertEqual(texts(line(["未来", "へ", "行こう"], spaced: true)), ["mirai", "e", "ikou"])
        XCTAssertEqual(texts(line(["ここ", "は", "どこ"], spaced: true)), ["koko", "wa", "doko"])
        // ...also when it starts the line, going on from the one before...
        XCTAssertEqual(texts(line(["は", "笑った", "ね"], spaced: true)), ["wa", "waratta", "ne"])
        // ...っ doubles the consonant of the ending after it (not omo ta),
        // and a word sung over several timed parts is shared out over them.
        XCTAssertEqual(texts(line(["思っ", "た", "より"], spaced: true)), ["omo", "tta", "yori"])
        XCTAssertEqual(texts(line(["待っ", "て", "て"], spaced: true)), ["ma", "tte", "te"])
        XCTAssertEqual(texts(line(["ちょっ", "と", "待っ", "て"], spaced: true)), ["cho", "tto", "ma", "tte"])
        XCTAssertEqual(texts(line(["はっ", "として", "グッと"], spaced: true)), ["hatto", "shite", "gutto"])
        XCTAssertEqual(texts(line(["会い", "たい", "よ"], spaced: true)), ["ai", "tai", "yo"])
        XCTAssertEqual(texts(line(["知ら", "ない", "よ"], spaced: true)), ["shira", "nai", "yo"])
        // Hiragana goes on from kanji with its okurigana or from katakana, a
        // single syllable from any word, and っ after kanji as a verb's does.
        XCTAssertEqual(texts(line(["抱き", "しめ", "て"], spaced: true)), ["daki", "shime", "te"])
        XCTAssertEqual(texts(line(["アイ", "して", "る"], spaced: true)), ["ai", "shite", "ru"])
        XCTAssertEqual(texts(line(["さっ", "き", "まで"], spaced: true)), ["sa", "kki", "made"])
        XCTAssertEqual(texts(line(["引っ", "こし", "の", "日"], spaced: true)), ["hi", "kkoshi", "no", "hi"])
        // A space before a word in kanji is still a pause: っ is not doubled
        // across it (not akkimi).
        XCTAssertEqual(texts(line(["あっ", "君だ", "よ"], spaced: true)), ["a", "kimi da", "yo"])
    }

    func testPhraseSpacesOfWordTimedLinesAreKept() {
        // A source that cuts words only at spaces (KPoe) spaces every break
        // between the lyric's own phrases. Read across them, hiragana came
        // apart wrongly (a itai, ma tte, miagetenaita, moshi mo shikameyo),
        // so each phrase reads as it does on a line timed only by line.
        let lines = [
            ["あいたい", "あいたい", "いますぐ"], ["まって", "まって", "いかないで"], ["そらを", "みあげて", "ないた"],
            ["もしもし", "かめよ", "かめさんよ"], ["ならんだ", "ならんだ", "あか", "しろ", "きいろ"],
            ["むすんで", "ひらいて", "てを", "うって", "むすんで"],
            // An interjection's っ is not doubled into the phrase after it
            // (not eddou, assou, akkimi)...
            ["えっ", "どうして", "なの"], ["あっ", "そう", "なんだ"], ["あっ", "きみだ", "よ"],
            // ...the tokenizer does not split a particle's kana off a word of
            // hiragana after a noun (not wa narete, no koshite, wa kanai), or
            // read one as a kanji's okurigana (not itama da, mana, gatsu)...
            ["君", "はなれて", "いく"], ["キミ", "はなれて", "いく"], ["夢", "のこして", "消えた"], ["思い", "はせて", "いる"],
            ["星", "はかない", "夢"], ["傷", "まだ", "痛む"], ["愛", "ささやく", "声"], ["月", "いま", "のぼる"],
            // ...and ない on its own is not written onto the word before it.
            ["もう", "ない", "よ"], ["君", "へんじ", "ない"],
        ]
        for parts in lines {
            let reading = texts(line(parts, spaced: true))
            XCTAssertEqual(reading, texts(line(parts, spaced: true, wordTimed: false)), parts.joined(separator: " "))
            XCTAssertEqual(reading, parts.map(JapaneseReading.romanize), parts.joined(separator: " "))
        }
        XCTAssertEqual(texts(line(["あいたい", "あいたい", "いますぐ"], spaced: true)), ["aitai", "aitai", "ima sugu"])
        XCTAssertEqual(texts(line(["そらを", "みあげて", "ないた"], spaced: true)), ["sora wo", "miagete", "naita"])
        XCTAssertEqual(texts(line(["あっ", "きみだ", "よ"], spaced: true)), ["a", "kimi da", "yo"])
        // A source reading is laid on the phrases it reads, not a itai or
        // miagetenaita under one word and nothing under the next.
        let sourced: [([String], String, [String])] = [
            (["あいたい", "あいたい", "いますぐ"], "aitai aitai imasugu", ["aitai", "aitai", "ima sugu"]),
            (["まって", "まって", "いかないで"], "matte matte ikanaide", ["matte", "matte", "ikanaide"]),
            (["そらを", "みあげて", "ないた"], "sora wo miagete naita", ["sora wo", "miagete", "naita"]),
            (["えっ", "どうして", "なの"], "e doushite nano", ["e", "dou shite", "na no"]),
            (["あっ", "きみだ", "よ"], "a kimi da yo", ["a", "kimi da", "yo"]),
            (["思っ", "た", "より"], "omotta yori", ["omo", "tta", "yori"]),
        ]
        for (parts, source, expected) in sourced {
            XCTAssertEqual(texts(line(parts, spaced: true, reading: source)), expected, source)
        }
        // "Replace lyrics" shows each phrase in its own time, not akkimi
        // across あっ and きみだ while da is shown under きみだ.
        let gasp = readings(line(["あっ", "きみだ", "よ"], spaced: true), options(.replace)) ?? []
        XCTAssertEqual(gasp.map(\.text), ["a", "kimi da", "yo"])
        for (word, next) in zip(gasp, gasp.dropFirst()) {
            XCTAssertLessThanOrEqual(word.end, next.start, word.text)
        }
    }

    /// What spacing alone cannot tell apart, pinned so that a change to the
    /// rules above shows what it trades: all of these read as spaced.
    func testMarkupSpacesSpacingCannotSettleReadAsSpaced() {
        // A kanji compound split across words is read in parts, as 今 日差しが
        // must be: 明 日が is not asu ga, 大 好きだよ not daisuki.
        let tomorrow = texts(line(["明", "日が", "来る"], spaced: true)) ?? []
        XCTAssertEqual(Array(tomorrow.dropFirst()), ["hi ga", "kuru"])
        XCTAssertFalse(["asu", "ashita"].contains(tomorrow.first ?? ""), tomorrow.joined(separator: "|"))
        let love = texts(line(["大", "好きだよ", "君が"], spaced: true)) ?? []
        XCTAssertEqual(Array(love.dropFirst()), ["suki da yo", "kimi ga"])
        // っ is not doubled across the space before a word in kanji, as after
        // あっ (引っ越し is hikkoshi), nor after hiragana before more than a
        // syllable that is no ending, as after えっ (がっこう is gakkou)...
        let moving = texts(line(["引っ", "越し", "の", "日"], spaced: true)) ?? []
        XCTAssertEqual(Array(moving.dropFirst()), ["koshi", "no", "hi"])
        XCTAssertFalse(joined(moving).contains("hikk"), joined(moving))
        XCTAssertEqual(texts(line(["がっ", "こう", "へ"], spaced: true)), ["ga", "kou", "e"])
        // ...and a word split right after a kanji, or a word of hiragana
        // split after hiragana, reads in parts, as 傷 まだ and ならんだ あか
        // must (一人ぼっち is hitoribotchi, 来ない konai, だきしめて dakishimete).
        XCTAssertEqual(texts(line(["一人", "ぼっち", "の", "夜"], spaced: true)), ["hitori", "botchi", "no", "yoru"])
        XCTAssertEqual(texts(line(["来", "ない", "よ"], spaced: true)), ["ki", "nai", "yo"])
        XCTAssertEqual(texts(line(["だき", "しめ", "て"], spaced: true)), ["daki", "shime", "te"])
    }

    // MARK: - Saved and source readings

    func testSyllableSplitSourceReadingIsPlacedOnItsCharacters() {
        // NetEase's romalrc spells one syllable per token, here with a
        // Kunrei-shiki syo, which is shown in Hepburn.
        let source = "yu me na ra ba do re ho do yo ka tta de syo u"
        let hepburn = "yu me na ra ba do re ho do yo ka tta de sho u"
        let characters = characterTimed("夢ならばどれほどよかったでしょう", reading: source)
        XCTAssertEqual(texts(characters), ["yume", "na", "ra", "ba", "do", "re", "ho", "do", "yo", "ka", "", "tta",
                                           "de", "sho", "", "u"])
        XCTAssertEqual(letters(LyricRomanizer.lineReading(readings(characters), for: characters) ?? ""), letters(hepburn))
        // On words, it is written by phrase and shows under every word it reads.
        let words = line(["夢", "なら", "ば", "どれ", "ほど", "よかっ", "た", "でしょう"], reading: source)
        let placed = readings(words)
        XCTAssertEqual(placed?.count, words.words.count)
        XCTAssertEqual(placed?.first?.text, "yume")
        XCTAssertEqual(letters(joined(placed?.map(\.text))), letters(hepburn))
        XCTAssertGreaterThanOrEqual(LyricReadingPairs.match(placed, to: words.words).count, 5)
    }

    func testCountMatchedSyllablesLandOnTheRightCharacters() {
        // Seven syllables for seven characters: the kana decide where they go.
        let line = characterTimed("今日何食べた？", reading: "kyo u na ni ta be ta?")
        let parts = texts(line) ?? []
        XCTAssertEqual(parts.count, 7)
        XCTAssertEqual(Array(parts.suffix(3)), ["be", "ta?", ""])
        XCTAssertEqual(parts.prefix(4).joined(), "kyounanita")
        let love = characterTimed("愛してるって嘘で", reading: "a i shi te ru tte u so de")
        XCTAssertEqual(texts(love), ["ai", "shi", "te", "ru", "", "tte", "uso", "de"])
    }

    func testSavedReadingIsSplitByPhraseInsteadOfReplaced() {
        let today = characterTimed("今日も", reading: "kyou mo")
        XCTAssertEqual(texts(today), ["kyo", "u", "mo"])
        let adult = characterTimed("大人", start: 20, reading: "otona")
        XCTAssertEqual(texts(adult, in: [today, adult])?.joined(), "otona")
    }

    func testSavedReadingSpellingsAreMatched() {
        // Macrons, Kunrei-shiki, a long vowel left out, capitals and hyphens.
        XCTAssertEqual(texts(characterTimed("今日も", reading: "Kyō mo")), ["Kyō", "", "mo"])
        XCTAssertEqual(texts(characterTimed("君の名は", reading: "kimi no na ha")), ["kimi", "no", "na", "ha"])
        XCTAssertEqual(texts(characterTimed("ありがとう", reading: "arigato")), ["a", "ri", "ga", "to", ""])
        XCTAssertEqual(texts(characterTimed("君の名は", reading: "KIMI-NO-NA-WA")), ["KIMI", "NO", "NA", "WA"])
        XCTAssertEqual(texts(characterTimed("コーヒー", reading: "kōhī")), ["kō", "", "hī", ""])
        XCTAssertEqual(texts(characterTimed("マッチ", reading: "macchi")), ["ma", "", "cchi"])
        XCTAssertEqual(texts(line(["君の", "名は"], reading: "kimi no na wa")), ["kimi no", "na wa"])
    }

    func testJapaneseReadingOfOtherWordsGivesWayToTheGeneratedOne() {
        let generated = texts(line(["今日", "も", "明日", "も"], wordTimed: false))
        XCTAssertNotNil(generated)
        // An extra word, or a reading in kana, does not read this line.
        for saved in ["kyou mo ashita mo ne", "きょうもあしたも"] {
            XCTAssertEqual(texts(line(["今日", "も", "明日", "も"], wordTimed: false, reading: saved)), generated, saved)
        }
    }

    func testUnsplittableSavedReadingStaysWholeUnderTheLine() {
        let line = line(["사랑해", "같이"], spaced: true, wordTimed: false, reading: "saranghae gachi gaja")
        let placed = readings(line)
        XCTAssertEqual(placed?.map(\.text), ["saranghae gachi gaja"])
        XCTAssertEqual(placed?.first?.start, line.words.first?.start)
        XCTAssertEqual(placed?.first?.end, line.words.last?.end)
        XCTAssertEqual(LyricRomanizer.lineReading(placed, for: line), "saranghae gachi gaja")
        // Replacing the lyric spreads the saved reading across the line.
        XCTAssertEqual(texts(line, options(.replace)), ["saranghae", "gachi", "gaja"])
    }

    /// What the lyric layer draws of a reading that no single word reads:
    /// it went undrawn under a line of several words, since the per-word
    /// pairs (`LyricReadingPairs.match`) drop a reading of other length.
    func testUnsplittableReadingIsDrawnOnceUnderTheWholeLine() throws {
        let lines = [
            lineTimed("사랑 해 너를", reading: "saranghae neoreul"),
            line(["사랑해", "같이"], spaced: true, wordTimed: false, reading: "saranghae gachi gaja"),
            line(["사랑해", "같이"], spaced: true, reading: "saranghae gachi gaja"),
            lineTimed("Я тебя люблю", reading: "Ya tebya ochen' lyublyu"),
        ]
        for line in lines {
            let saved = try XCTUnwrap(line.romanization)
            XCTAssertGreaterThan(line.words.count, 1, line.text)
            // "Below lyrics": no word gets a share, and the line gets it all.
            let below = readings(line)
            XCTAssertEqual(LyricReadingPairs.match(below, to: line.words), [:], line.text)
            XCTAssertEqual(LyricReadingPairs.line(below, for: line, display: .below), saved, line.text)
            // "Replace lyrics" sets it in the line's place, over the line's time.
            let replaced = try XCTUnwrap(readings(line, options(.replace)), line.text)
            XCTAssertEqual(replaced.map(\.text).joined(separator: " "), saved, line.text)
            XCTAssertNil(LyricReadingPairs.line(replaced, for: line, display: .replace), line.text)
            XCTAssertGreaterThanOrEqual(replaced.first?.start ?? -1, line.start, line.text)
            XCTAssertLessThanOrEqual(replaced.last?.end ?? .infinity, line.end, line.text)
        }
        // A line of one word keeps it under that word.
        let word = lineTimed("Люблю", reading: "Lyublyu tebya")
        XCTAssertEqual(LyricReadingPairs.match(readings(word), to: word.words), [word.words[0].presentationID: "Lyublyu tebya"])
        XCTAssertNil(LyricReadingPairs.line(readings(word), for: word, display: .below))
        // Readings shared out by character are drawn under their characters,
        // never a second time under the line.
        let today = characterTimed("今日も")
        XCTAssertEqual(LyricReadingPairs.match(readings(today), to: today.words).count, 3)
        XCTAssertNil(LyricReadingPairs.line(readings(today), for: today, display: .below))
    }

    /// A rolling stage sets one word at a time with no line of its own, so
    /// the reading of the whole line arrives under the line's first word.
    @MainActor
    func testRollingStageDrawsAnUnsplittableReadingUnderTheLinesFirstWord() throws {
        let line = line(["사랑해", "같이"], spaced: true, reading: "saranghae gachi gaja")
        let placed = try XCTUnwrap(readings(line))
        let window = LyricLayerCache.rollingWindow(around: 0, lines: [line], romanized: [0: placed],
                                                   time: line.start, lead: 1.2, display: .below)
        XCTAssertEqual(window.words.map(\.text), ["사랑해", "같이"])
        XCTAssertEqual(window.readings, [line.words[0].presentationID: "saranghae gachi gaja"])
        // Readings that do split by word stay with their words.
        let split = self.line(["사랑해", "같이"], spaced: true, reading: "saranghae gachi")
        let pairs = LyricLayerCache.rollingWindow(around: 0, lines: [split], romanized: [0: try XCTUnwrap(readings(split))],
                                                  time: split.start, lead: 1.2, display: .below).readings
        XCTAssertEqual(pairs, [split.words[0].presentationID: "saranghae", split.words[1].presentationID: "gachi"])
    }

    func testSourceReadingTakesPrecedenceOverGeneratedReading() {
        // A transliteration from the lyric source, written without spaces.
        let line = characterTimed("君の名は", reading: "kiminonawa")
        XCTAssertEqual(texts(line), ["kimi", "no", "na", "wa"])
        // Where it differs from the generated reading, the source wins...
        let spelled = characterTimed("君の名は", reading: "kimi no na ha")
        XCTAssertEqual(texts(spelled)?.last, "ha")
        // ...unless saved readings are turned off.
        XCTAssertEqual(texts(spelled, options(preferSaved: false))?.last, "wa")
    }

    func testSourceReadingsAreShownInHepburn() {
        // NetEase writes some of Lemon's syllables in Kunrei-shiki. Where they
        // spell the same sounds as the app's own reading, they are shown as it
        // writes them: deshou, ijou and furi, not desyou, ijyou and huri.
        let lemon = [
            ("夢ならばどれほどよかったでしょう", "yu me na ra ba do re ho do yo ka tta de syo u", "yumenarabadorehodoyokattadeshou"),
            ("きっともうこれ以上傷つくことなど", "ki tto mo u ko re i jyo u ki zu tsu ku ko to na do", "kittomoukoreijoukizutsukukotonado"),
            ("雨が降り止むまでは帰れない", "a me ga hu ri ya mu ma de wa ka e re na i", "amegafuriyamumadewakaerenai"),
        ]
        for (text, source, hepburn) in lemon {
            let characters = characterTimed(text, reading: source)
            XCTAssertEqual(letters(LyricRomanizer.lineReading(readings(characters), for: characters) ?? ""), hepburn, text)
            XCTAssertEqual(letters(joined(texts(lineTimed(text, reading: source)))), hepburn, text)
        }
        XCTAssertEqual(Array(texts(characterTimed("これ以上", reading: "ko re i jyo u"))?.suffix(2) ?? []), ["i", "jou"])
        // Loanwords keep their spelling (ディ di), and capitals stay.
        XCTAssertEqual(texts(characterTimed("メディア", reading: "me di a")), ["me", "di", "", "a"])
        XCTAssertEqual(texts(characterTimed("以上だ", reading: "IJYOU DA")), ["I", "JOU", "DA"])
        // A reading of a kanji that differs from the app's is the source's
        // own, as written ...
        XCTAssertEqual(texts(line(["本気", "で"], wordTimed: false, reading: "mazi de")), ["mazi", "de"])
        XCTAssertEqual(texts(line(["運命", "を"], wordTimed: false, reading: "sadame wo")), ["sadame", "wo"])
        // ... with its Kunrei-shiki onsets in Hepburn when the same reading
        // writes Kunrei-shiki elsewhere: syo for しょ, or tu for つ.
        XCTAssertEqual(texts(line(["本気", "でしょう"], wordTimed: false, reading: "mazi de syo u")), ["maji", "deshou"])
        XCTAssertEqual(texts(characterTimed("本気でしょう", reading: "MAZI DE SYO U")), ["MA", "JI", "DE", "SHO", "", "U"])
        XCTAssertEqual(texts(line(["一つ", "の", "地球"], wordTimed: false, reading: "hitotu no hosi"))?.last, "hoshi")
        XCTAssertEqual(texts(line(["この", "地球"], wordTimed: false, reading: "kono hosi"))?.last, "hosi")
        XCTAssertFalse(JapaneseAlignment.writesNonHepburn("di", for: "di"))
        XCTAssertEqual(JapaneseAlignment.respelled("Hosi"), "Hoshi")
        XCTAssertEqual(JapaneseAlignment.respelled("SYŌ"), "SHŌ")
        // English sung for a kanji is often shaped like romaji, and stays as
        // written: not passhion, vishion, ashia or shituation.
        let english = [(["情熱", "を"], "passion wo"), (["未来", "が"], "vision ga"), (["亜細亜", "へ"], "Asia e"),
                       (["状況", "を"], "situation wo"), (["使命", "を"], "mission wo"), (["音楽", "を"], "music wo")]
        for (parts, reading) in english {
            XCTAssertEqual(texts(line(parts, wordTimed: false, reading: reading)), reading.components(separatedBy: " "), reading)
        }
        // Hepburn's own loanword spellings are not respelled either.
        XCTAssertEqual(texts(line(["運命", "を"], wordTimed: false, reading: "disutinii wo")), ["disutinii", "wo"])
        XCTAssertEqual(texts(line(["涙", "が"], wordTimed: false, reading: "tiaazu ga")), ["tiaazu", "ga"])
    }

    func testSavedChineseReadingsAlignByCharacter() {
        let word = LyricsFetcher.Line(start: 1, end: 3, text: "我愛你", words: [.init(text: "我愛你", start: 1, end: 3)],
                                      isInstrumental: false, wordTimed: true, romanization: "ngo5 oi3 nei5")
        XCTAssertEqual(texts(word), ["ngo5 oi3 nei5"])
        let pair = LyricsFetcher.Line(start: 1, end: 3, text: "我愛你",
                                      words: [.init(text: "我", start: 1, end: 1.5), .init(text: "愛你", start: 1.5, end: 3)],
                                      isInstrumental: false, wordTimed: true, romanization: "ngo5 oi3 nei5")
        XCTAssertEqual(texts(pair), ["ngo5", "oi3 nei5"])
    }

    func testSyllableSplitKoreanReadingJoinsByWord() {
        let words = line(["사랑해", "같이"], spaced: true, reading: "sa rang hae ga chi")
        XCTAssertEqual(texts(words), ["saranghae", "gachi"])
        let syllables = characterTimed("사랑해 같이", reading: "sa rang hae ga chi")
        XCTAssertEqual(texts(syllables), ["sa", "rang", "hae", "ga", "chi"])
        XCTAssertEqual(LyricRomanizer.lineReading(readings(syllables), for: syllables), "saranghae gachi")
        // A reading by word on syllable-timed lyrics is shared by syllable.
        XCTAssertEqual(texts(characterTimed("사랑해 같이", reading: "saranghae gachi")), ["sa", "rang", "hae", "ga", "chi"])
    }

    // MARK: - Korean

    func testKoreanFollowsRevisedRomanizationPronunciationRules() {
        let cases = [
            "사랑해": "saranghae", "같이": "gachi", "좋아": "joa", "없어": "eopseo", "몰라": "molla",
            "신라": "silla", "국물": "gungmul", "한국어": "hangugeo", "괜찮아": "gwaenchana", "밥": "bap",
            "꽃잎": "kkonnip", "놓다": "nota",
            "있어": "isseo", "있는": "inneun", "합니다": "hamnida", "학교": "hakgyo", "좋고": "joko",
            "많이": "mani", "싫어": "sireo", "읽어": "ilgeo", "굳이": "guji", "축하해": "chukahae",
            "설날": "seollal", "종로": "jongno", "독립": "dongnip", "희망": "huimang", "원래": "wollae",
            "빨리": "ppalli", "닭": "dak", "여덟": "yeodeol", "밟다": "bapda", "밖에": "bakke", "라면": "ramyeon",
            // ㄷ/ㅌ palatalise before 여 and 혀 contracted from 이어 and 히어.
            "갇혀": "gachyeo", "닫혀": "dachyeo", "묻혀": "muchyeo", "붙여": "buchyeo", "걷혀": "geochyeo", "받혀": "bachyeo",
            "닫히다": "dachida", "밭에": "bate", "끝에": "kkeute", "같아": "gata", "솥이": "sochi",
        ]
        for (text, expected) in cases {
            XCTAssertEqual(KoreanRomanizer.romanize(text), expected, text)
        }
        XCTAssertEqual(LyricRomanizer.romanize("사랑해 같이 가자!", language: .automatic, toneMarks: true), "saranghae gachi gaja!")
        XCTAssertFalse(KoreanRomanizer.romanize("같이 좋아 없어").contains("-"))
    }

    func testKoreanSyllablesKeepTheirSoundChanges() {
        XCTAssertEqual(KoreanRomanizer.syllables("같이 국물"), ["ga", "chi", " ", "gung", "mul"])
        XCTAssertEqual(KoreanRomanizer.syllables("빨리!"), ["ppal", "li", "!"])
    }

    func testDecomposedHangulIsRomanisedAsKorean() {
        let decomposed = "같이 좋아".decomposedStringWithCanonicalMapping
        XCTAssertNotEqual(decomposed.unicodeScalars.count, "같이 좋아".unicodeScalars.count)
        XCTAssertTrue(KoreanRomanizer.containsHangul(decomposed))
        XCTAssertEqual(LyricRomanizer.romanize(decomposed, language: .automatic, toneMarks: true), "gachi joa")
    }

    func testCharacterTimedKoreanIsRomanisedByWord() {
        let line = characterTimed("사랑해 같이")
        XCTAssertEqual(texts(line), ["sa", "rang", "hae", "ga", "chi"])
        XCTAssertEqual(LyricRomanizer.lineReading(readings(line), for: line), "saranghae gachi")
        XCTAssertEqual(texts(lineTimed("사랑해 같이 가자")), ["saranghae", "gachi", "gaja"])
    }

    // MARK: - Line readings

    func testLineReadingIsOnlyForPhrasesSpreadOverCharacters() {
        // Each Chinese character already has its whole syllable.
        let mandarin = characterTimed("我爱你中国")
        XCTAssertEqual(texts(mandarin, options(language: .mandarin))?.count, 5)
        XCTAssertNil(LyricRomanizer.lineReading(readings(mandarin, options(language: .mandarin)), for: mandarin))
        let jyutping = characterTimed("佢哋係我嘅朋友", reading: "keoi5 dei6 hai6 ngo5 ge3 pang4 jau5")
        XCTAssertEqual(texts(jyutping), ["keoi5", "dei6", "hai6", "ngo5", "ge3", "pang4", "jau5"])
        XCTAssertNil(LyricRomanizer.lineReading(readings(jyutping), for: jyutping))
    }

    // MARK: - Language detection

    func testCantoneseSongGetsNoMandarinPinyin() {
        let song = [lineTimed("佢哋係我嘅朋友"), line(["我", "愛", "你"], start: 4, wordTimed: false), lineTimed("唔好走", start: 8)]
        XCTAssertEqual(LyricRomanizer.songLanguage(song), .cantonese)
        XCTAssertTrue(LyricRomanizer.readings(song, options: options()).isEmpty)
        // A saved Jyutping reading is still shown, even with saved readings
        // not preferred, because nothing else can be generated.
        var withReading = song
        withReading[1].romanization = "ngo5 oi3 nei5"
        let readings = LyricRomanizer.readings(withReading, options: options(preferSaved: false))
        XCTAssertEqual(readings.keys.sorted(), [1])
        XCTAssertEqual(readings[1]?.map(\.text), ["ngo5", "oi3", "nei5"])
    }

    func testCantoneseMarkersAreCountedConservatively() {
        XCTAssertGreaterThanOrEqual(LyricRomanizer.cantoneseScore("佢哋係我嘅朋友"), 2)
        // 係 also appears in Traditional Mandarin (關係).
        XCTAssertLessThan(LyricRomanizer.cantoneseScore("我們的關係"), 1)
        let mandarin = [lineTimed("我們的關係"), lineTimed("你是我的光", start: 4), lineTimed("天空很藍", start: 8)]
        XCTAssertEqual(LyricRomanizer.songLanguage(mandarin), .mandarin)
        XCTAssertTrue(joined(texts(mandarin[0], in: mandarin)).hasPrefix("wǒ men"))
        // A line of Cantonese inside a Mandarin song gets no pinyin...
        let mixed = mandarin + [lineTimed("我唔知", start: 12)]
        XCTAssertEqual(LyricRomanizer.songLanguage(mixed), .mandarin)
        let readings = LyricRomanizer.readings(mixed, options: options())
        XCTAssertNil(readings[3])
        XCTAssertNotNil(readings[1])
        // ...while plainly written Cantonese marks the whole song as sung in it.
        XCTAssertEqual(LyricRomanizer.songLanguage(mandarin + [lineTimed("佢唔係你", start: 12)]), .cantonese)
    }

    func testMandarinPhrasesAreNotTakenForCantonese() {
        // Pairs such as 点解 and 边度 also occur across Mandarin words, and 唔
        // alone is a hum.
        let phrases = ["给我一点解释", "在你身边度过每一天", "雨后的美好彩虹", "在海邊度假的日子", "钢琴日记", "唔 我想你", "房屋企业"]
        for phrase in phrases {
            XCTAssertLessThan(LyricRomanizer.cantoneseScore(phrase), 1, phrase)
        }
        let song = (["我爱你中国", "我们一起走", "天空很蓝", "你是我的光"] + phrases).enumerated().map {
            lineTimed($1, start: Double($0) * 4)
        }
        XCTAssertEqual(LyricRomanizer.songLanguage(song), .mandarin)
        XCTAssertEqual(LyricRomanizer.readings(song, options: options()).count, song.count)
    }

    func testOneCantoneseLineDoesNotStripAMandarinSong() {
        let mandarin = ["我爱你中国", "我们一起走", "天空很蓝", "你是我的光", "让我们唱歌", "夜空中最亮的星", "请照亮我前行", "我想要飞"]
        let song = (mandarin + ["佢唔係你"]).enumerated().map { lineTimed($1, start: Double($0) * 4) }
        XCTAssertEqual(LyricRomanizer.songLanguage(song), .mandarin)
        let readings = LyricRomanizer.readings(song, options: options())
        XCTAssertNil(readings[8])
        XCTAssertEqual(readings.keys.sorted(), Array(0..<8))
    }

    func testJapanesePhraseDoesNotMakeAMandarinSongJapanese() {
        let song = ["我爱你中国", "我们一起走", "天空很蓝", "你是我的光", "让我们唱歌"].enumerated().map {
            lineTimed($1, start: Double($0) * 4)
        } + [lineTimed("ありがとう", start: 40)]
        XCTAssertEqual(LyricRomanizer.songLanguage(song), .mandarin)
        XCTAssertEqual(joined(texts(song[1], in: song)), "wǒ men yī qǐ zǒu")
        XCTAssertEqual(joined(texts(song[5], in: song)), "arigatou")
    }

    func testKanaAndHangulKeepTheirLanguageWhenHanIsForced() {
        let japanese = lineTimed("君の名は")
        XCTAssertEqual(joined(texts(japanese, options(language: .mandarin))), "kimi no na wa")
        let korean = lineTimed("사랑해")
        XCTAssertEqual(texts(korean, options(language: .japanese)), ["saranghae"])
        XCTAssertNil(texts(lineTimed("我愛你"), options(language: .cantonese)))
    }

    func testStoredLanguageValuesAreUnchanged() {
        XCTAssertEqual(RomanizationOptions.Language.allCases.map(\.rawValue),
                       ["automatic", "japanese", "mandarin", "cantonese", "korean", "other"])
        XCTAssertEqual(RomanizationOptions.Language(rawValue: "korean"), .korean)
    }
}
