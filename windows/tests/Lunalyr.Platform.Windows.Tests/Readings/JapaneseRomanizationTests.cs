using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Platform.Readings;
using Lunalyr.Tests.Shared;
using static Lunalyr.Tests.Shared.ReadingTestLines;

namespace Lunalyr.Platform.Windows.Tests.Readings;

/// <summary>
/// The Japanese parts of the Mac's RomanizationTests, run with the Windows reading services. Expectations are the
/// Mac's. Kanji readings come from JapanesePhoneticAnalyzer, which reads nothing unless the Japanese language (its IME
/// dictionary) is installed; tests that need kanji readings are skipped where it is missing, with the reason. Without
/// it, kana and saved readings still work; the kana and saved-reading assertions run before the skip.
/// </summary>
public class JapaneseRomanizationTests
{
    private readonly ReadingTestLines _t = new(WindowsReadingServices.Create());

    private string Romaji(string text) => new JapaneseReading(_t.Services).Romanize(text);

    internal static void RequireJapaneseTokenizer() =>
        Assert.SkipUnless(WindowsJapaneseTokenizer.IsAvailable,
            "JapanesePhoneticAnalyzer reads nothing on this PC (the Japanese IME dictionary is not installed): Japanese is split by ICU word breaks and kanji have no readings, while these Mac expectations assume a Japanese tokenizer. What Windows gives instead is pinned in JapaneseWithoutAnalyzerTests.");

    /// <summary>A Japanese song around <paramref name="line"/>, so its kanji are read as Japanese.</summary>
    private LyricLine[] JapaneseSong(LyricLine line) => [line, _t.LineTimed("ありがとう", start: 100)];

    // Ported from RomanizationTests.swift: testJapaneseReadsWholeLinesWithParticlesAndSokuon
    [Fact]
    public void JapaneseReadsWholeLinesWithParticlesAndSokuon()
    {
        RequireJapaneseTokenizer();
        var cases = new Dictionary<string, string>
        {
            ["君の名は"] = "kimi no na wa", ["未来へ"] = "mirai e", ["思った"] = "omotta", ["良かった"] = "yokatta",
            ["今日も"] = "kyou mo", ["大人"] = "otona", ["僕は君が好き"] = "boku wa kimi ga suki",
            ["こんにちは"] = "konnichiwa", ["愛してる"] = "aishiteru", ["待ってて"] = "mattete", ["私は"] = "watashi wa",
        };
        foreach (var (text, expected) in cases) Assert.Equal(expected, _t.Romanizer.Romanize(text, RomanizationLanguage.Japanese, toneMarks: false));
        // The tokenizer always says nan for 何; it is nani before most words.
        Assert.StartsWith("nani wo", Romaji("何を見ていたの"), StringComparison.Ordinal);
        Assert.StartsWith("nani", Romaji("何も"), StringComparison.Ordinal);
        Assert.StartsWith("nandemo", Romaji("何でもない"), StringComparison.Ordinal);
    }

    // Ported from RomanizationTests.swift: testJapaneseLongVowelsSmallKanaAndPunctuation
    [Fact]
    public void JapaneseLongVowelsSmallKanaAndPunctuation()
    {
        RequireJapaneseTokenizer();
        Assert.Equal("raamen", Romaji("ラーメン"));
        Assert.Equal("koohii", Romaji("コーヒー"));
        Assert.Equal("sugooi", Romaji("すごーい"));
        Assert.Equal("nee", Romaji("ねぇ"));
        Assert.Equal("vaiorin", Romaji("ヴァイオリン"));
        Assert.DoesNotContain("~", Romaji("思った"));
        Assert.Equal("kimi to, boku to.", Romaji("君と、僕と。"));
    }

    // Ported from RomanizationTests.swift: testJapanesePhrasesAreReadOnTheirOwn
    [Fact]
    public void JapanesePhrasesAreReadOnTheirOwn()
    {
        RequireJapaneseTokenizer();
        // Read across a space or a Latin word, 君 came out as the suffix kun and 行 as kou.
        foreach (var text in new[] { "ねえ 君は", "今 君に会いたい", "Hey 君が好き", "Ah 君の名は", "もう一度 君に" })
        {
            var reading = Romaji(text);
            Assert.Contains("kimi", reading, StringComparison.Ordinal);
            Assert.DoesNotContain("kun", reading, StringComparison.Ordinal);
        }
        Assert.Equal("nee kimi wa", Romaji("ねえ 君は"));
        Assert.Equal("Hey kimi ga suki", Romaji("Hey 君が好き"));
        var away = Romaji("Yeah yeah 行かないで");
        Assert.True(away.StartsWith("Yeah yeah ", StringComparison.Ordinal) && away.Contains("ikanaide", StringComparison.Ordinal), away);
    }

    // Ported from RomanizationTests.swift: testStandaloneHaIsAParticleUnlessItBeginsAWord
    [Fact]
    public void StandaloneHaIsAParticleUnlessItBeginsAWord()
    {
        RequireJapaneseTokenizer();
        // At the start of a phrase は also begins a word the tokenizer split it from, or is an interjection.
        foreach (var text in new[] { "はなれないで", "ねえ はなれないで" })
        {
            var reading = Romaji(text);
            Assert.Contains("hanare", reading, StringComparison.Ordinal);
            Assert.DoesNotContain("wa", reading, StringComparison.Ordinal);
        }
        Assert.Equal("hanarenaide", Romaji("はなれないで"));
        var apart = CharacterTimed("はなれないで");
        Assert.Equal("hanarenaide", LyricRomanizer.LineReading(_t.Readings(apart), apart));
        Assert.Equal("ha?", Romaji("は？"));
        Assert.Equal("ha", Romaji("はっ"));
        Assert.EndsWith("wa", Romaji("「愛」は"), StringComparison.Ordinal);
        // A line going on from the one before starts with the particle, which stays apart from the word after it
        // (not hawaratta, heikou).
        Assert.Equal("kimi no na wa", Romaji("君の名は"));
        var particles = new Dictionary<string, string>
        {
            ["は笑った"] = "wa waratta", ["へ行こう"] = "e ikou", ["は君の"] = "wa kimi no", ["はどこ"] = "wa doko",
            ["ねえ は笑った"] = "nee wa waratta", ["はアイス"] = "wa aisu", ["は2人で"] = "wa futari de", ["へと"] = "e to",
        };
        foreach (var (text, expected) in particles) Assert.Equal(expected, Romaji(text));
        Assert.Equal(["wa", "waratta"], _t.Texts(Line(["は", "笑った"], wordTimed: false)));
        Assert.Equal(["e", "ikou"], _t.Texts(Line(["へ", "行こう"], wordTimed: false)));
    }

    // Ported from RomanizationTests.swift: testWordsTheTokenizerSplitsAfterHaStayWhole
    [Fact]
    public void WordsTheTokenizerSplitsAfterHaStayWhole()
    {
        RequireJapaneseTokenizer();
        var words = new Dictionary<string, string>
        {
            ["はしれ"] = "hashire", ["はしれ！"] = "hashire!", ["はしれメロス"] = "hashire merosu", ["はしれはしれ"] = "hashire hashire",
            ["ねえ はしれ"] = "nee hashire", ["はくしゅ"] = "hakushu", ["はくしゅして"] = "hakushu shite", ["はね"] = "hane",
            ["はよ"] = "hayo", ["はよ来て"] = "hayo kite", ["はで"] = "hade", ["はぐして"] = "hagushite", ["はぐしよう"] = "hagushiyou",
            ["はぐしたい"] = "hagushitai", ["へ理屈"] = "herikutsu", ["へ理屈を"] = "herikutsu wo", ["へりくつ"] = "herikutsu",
            ["へりくつを"] = "herikutsu wo",
        };
        foreach (var (text, expected) in words) Assert.Equal(expected, Romaji(text));
        var particles = new Dictionary<string, string>
        {
            ["はもう"] = "wa mou", ["はそれ"] = "wa sore", ["はずっと"] = "wa zutto", ["へかえろう"] = "e kaerou", ["はでも"] = "wa demo",
            ["はでもね"] = "wa demo ne", ["はから"] = "wa kara", ["はまで"] = "wa made", ["はより"] = "wa yori", ["はしあわせ"] = "wa shiawase",
        };
        foreach (var (text, expected) in particles) Assert.Equal(expected, Romaji(text));
    }

    // Ported from RomanizationTests.swift: testKanjiTheTokenizerReadsAsMandarinGetJapaneseOrNothing
    [Fact]
    public void KanjiTheTokenizerReadsAsMandarinGetJapaneseOrNothing()
    {
        const string pinyin = "áàǎéèěíìǐóòǒúùǔǘǚǜ";
        // Simplified Chinese kanji in a Japanese lyric get no reading, and the ending after one does not stick to the
        // particle before it.
        var dream = Line(["君", "の", "梦", "を", "见", "た"], wordTimed: false);
        var replaced = _t.Texts(dream, Options(RomanizationDisplay.Replace));
        foreach (var text in new[] { "2人で歩いた", "1人きり", "僕ら2人" }) Assert.DoesNotContain(Romaji(text), pinyin.Contains);
        RequireJapaneseTokenizer();
        foreach (var (text, expected) in new Dictionary<string, string> { ["2人で歩いた"] = "futari", ["1人きり"] = "hitori", ["僕ら2人"] = "futari" })
        {
            Assert.Contains(expected, Romaji(text), StringComparison.Ordinal);
        }
        Assert.Equal(["kimi", "no", "", "wo", "", "ta"], _t.Texts(dream));
        Assert.Equal(["kimi", "no", "梦", "wo", "见", "ta"], replaced);
    }

    // Ported from RomanizationTests.swift: testFullWidthDigitsCountPeopleAsHalfWidthOnesDo
    [Fact]
    public void FullWidthDigitsCountPeopleAsHalfWidthOnesDo()
    {
        RequireJapaneseTokenizer();
        var counted = new Dictionary<string, string>
        {
            ["２人で"] = "futari de", ["１人きり"] = "hitori kiri", ["２人の未来"] = "futari no mirai", ["僕ら２人"] = "boku ra futari",
            ["は２人で"] = "wa futari de", ["２人で歩いた"] = "futari de aruita",
        };
        foreach (var (text, expected) in counted) Assert.Equal(expected, Romaji(text));
        // Other numbers are read as their half-width forms are, and keep their digits as written, so "Below lyrics"
        // sets no reading under a digit that only repeats it.
        Assert.Equal("３ kai", Romaji("３回"));
        var time = _t.LineTimed("５時に会おう");
        Assert.Equal(["５", "ji", "ni", "aou"], _t.Texts(time));
        Assert.False(LyricReadingPairs.Match(_t.Readings(time), time.Words).ContainsKey(time.Words[0]));
        // On a line, ２人 reads futari wherever its words fall.
        Assert.Equal(["futari de", "aruita"], _t.Texts(Line(["２人で", "歩いた"], wordTimed: false)));
        var together = Line(["２人", "で", "歩いた"]);
        Assert.Equal(["futari", "de", "aruita"], _t.Texts(together, song: JapaneseSong(together)));
        var apart = CharacterTimed("２人で");
        Assert.Equal("futari de", LyricRomanizer.LineReading(_t.Readings(apart), apart));
    }

    // Ported from RomanizationTests.swift: testMonthsAndDaysAfterDigitsReadAsDates
    [Fact]
    public void MonthsAndDaysAfterDigitsReadAsDates()
    {
        RequireJapaneseTokenizer();
        string[] fullWidth = ["１", "２", "３", "４", "５", "６", "７", "８", "９", "１０", "１１", "１２"];
        for (var index = 0; index < fullWidth.Length; index++)
        {
            Assert.Equal($"{index + 1} gatsu", Romaji($"{index + 1}月"));
            Assert.Equal($"{fullWidth[index]} gatsu", Romaji($"{fullWidth[index]}月"));
        }
        var dates = new Dictionary<string, string>
        {
            ["４月は君の嘘"] = "４ gatsu wa kimi no uso", ["4月は君の嘘"] = "4 gatsu wa kimi no uso", ["７月の空"] = "７ gatsu no sora",
            ["12月24日"] = "12 gatsu 24 ka", ["１２月２５日"] = "１２ gatsu ２５ nichi", ["2月14日"] = "2 gatsu 14 ka",
            ["365日"] = "365 nichi", ["1日"] = "1 nichi", ["３日"] = "３ ka", ["１０日"] = "１０ ka", ["20日"] = "20 ka",
            ["365日間"] = "365 nichikan", ["７日間"] = "７ kakan",
        };
        foreach (var (text, expected) in dates) Assert.Equal(expected, Romaji(text));
        var words = new Dictionary<string, string>
        {
            ["月が綺麗"] = "tsuki ga kirei", ["今月"] = "kongetsu", ["毎日"] = "mainichi", ["3ヶ月"] = "3 kagetsu", ["５時"] = "５ ji",
        };
        foreach (var (text, expected) in words) Assert.Equal(expected, Romaji(text));
        var april = Line(["4", "月", "は", "君", "の", "嘘"]);
        Assert.Equal(["4", "gatsu", "wa", "kimi", "no", "uso"], _t.Texts(april));
        Assert.False(LyricReadingPairs.Match(_t.Readings(april), april.Words).ContainsKey(april.Words[0]));
    }

    // Ported from RomanizationTests.swift: testCharacterTimedJapaneseIsSharedOutByCharacter
    [Fact]
    public void CharacterTimedJapaneseIsSharedOutByCharacter()
    {
        RequireJapaneseTokenizer();
        var today = CharacterTimed("今日も");
        Assert.Equal(["kyo", "u", "mo"], _t.Texts(today));
        Assert.Equal("kyou mo", LyricRomanizer.LineReading(_t.Readings(today), today));
        var thought = CharacterTimed("思った");
        Assert.Equal(["omo", "", "tta"], _t.Texts(thought));
        Assert.Equal("omotta", LyricRomanizer.LineReading(_t.Readings(thought), thought));
        // Alone, kanji could be Chinese; in a Japanese song they read as Japanese.
        var adult = CharacterTimed("大人", start: 20);
        var parts = _t.Texts(adult, song: [today, adult]);
        Assert.Equal("otona", string.Concat(parts));
        Assert.DoesNotContain("", parts);
    }

    // Ported from RomanizationTests.swift: testCharacterTimedReadingsCarryTheirPhrasesTime
    [Fact]
    public void CharacterTimedReadingsCarryTheirPhrasesTime()
    {
        RequireJapaneseTokenizer();
        var today = CharacterTimed("今日も");
        var below = _t.Readings(today)!;
        Assert.Equal([today.Words[0].Start, today.Words[0].Start, today.Words[2].Start], below.Select(word => word.Start));
        Assert.Equal([today.Words[1].End, today.Words[1].End, today.Words[2].End], below.Select(word => word.End));
        var replaced = _t.Readings(today, Options(RomanizationDisplay.Replace))!;
        Assert.Equal(["kyou", "mo"], replaced.Select(word => word.Text));
        Assert.Equal(today.Words[0].Start, replaced[0].Start);
        Assert.Equal(today.Words[1].End, replaced[0].End);
    }

    // Ported from RomanizationTests.swift: testLineTimedJapaneseMapsPhrasesOntoWords
    [Fact]
    public void LineTimedJapaneseMapsPhrasesOntoWords()
    {
        RequireJapaneseTokenizer();
        var line = Line(["思っ", "た", "より"], wordTimed: false);
        Assert.Equal(["omo", "tta", "yori"], _t.Texts(line));
        Assert.Null(LyricRomanizer.LineReading(_t.Readings(line), line));
    }

    // Ported from RomanizationTests.swift: testSpacesBetweenTimedJapaneseWords
    [Fact]
    public void SpacesBetweenTimedJapaneseWords()
    {
        RequireJapaneseTokenizer();
        // A space at only some word breaks is a pause: っ before it is not doubled.
        var pause = Joined(_t.Texts(Line(["あっ", "君だ"], spaced: true)));
        Assert.DoesNotContain("akk", pause, StringComparison.Ordinal);
        Assert.Contains("kimi", pause, StringComparison.Ordinal);
        // Spaces at every break are the markup's, so 今日 is still read whole.
        Assert.Equal(["kyo", "u", "mo"], _t.Texts(Line(["今", "日", "も"], spaced: true)));
    }

    // Ported from RomanizationTests.swift: testSpacesBetweenTimedJapanesePhrasesAreKept
    [Fact]
    public void SpacesBetweenTimedJapanesePhrasesAreKept()
    {
        RequireJapaneseTokenizer();
        Assert.Equal(["kyo", "u", "mo"], _t.Texts(Line(["きょ", "う", "も"], spaced: true)));
        Assert.Equal(["ima", "hizashi ga", "mabushii"], _t.Texts(Line(["今", "日差しが", "眩しい"], spaced: true)));
        Assert.Equal(["ima", "hi ga", "shizumu"], _t.Texts(Line(["今", "日が", "沈む"], spaced: true)));
        Assert.Equal(["kyo", "u", "mo", "kimi ni", "aitai"], _t.Texts(Line(["今", "日", "も", "君に", "会いたい"], spaced: true)));
        var held = Line(["抱", "きし", "め", "て"], spaced: true);
        Assert.Equal("dakishimete", LyricRomanizer.LineReading(_t.Readings(held), held));
    }

    // Ported from RomanizationTests.swift: testMarkupSpacesInsideAPhraseAreDropped
    [Fact]
    public void MarkupSpacesInsideAPhraseAreDropped()
    {
        RequireJapaneseTokenizer();
        Assert.Equal(["koko", "wa", "doko"], _t.Texts(Line(["ここ", "は", "どこ"], spaced: true)));
        Assert.Equal(["ai", "shite", "ru"], _t.Texts(Line(["アイ", "して", "る"], spaced: true)));
        Assert.Equal(["sa", "kki", "made"], _t.Texts(Line(["さっ", "き", "まで"], spaced: true)));
        Assert.Equal(["sekai", "wa", "utsukushii"], _t.Texts(Line(["世界", "は", "美しい"], spaced: true)));
        Assert.Equal(["mirai", "e", "ikou"], _t.Texts(Line(["未来", "へ", "行こう"], spaced: true)));
        Assert.Equal(["wa", "waratta", "ne"], _t.Texts(Line(["は", "笑った", "ね"], spaced: true)));
        Assert.Equal(["omo", "tta", "yori"], _t.Texts(Line(["思っ", "た", "より"], spaced: true)));
        Assert.Equal(["ma", "tte", "te"], _t.Texts(Line(["待っ", "て", "て"], spaced: true)));
        Assert.Equal(["cho", "tto", "ma", "tte"], _t.Texts(Line(["ちょっ", "と", "待っ", "て"], spaced: true)));
        Assert.Equal(["hatto", "shite", "gutto"], _t.Texts(Line(["はっ", "として", "グッと"], spaced: true)));
        Assert.Equal(["ai", "tai", "yo"], _t.Texts(Line(["会い", "たい", "よ"], spaced: true)));
        Assert.Equal(["shira", "nai", "yo"], _t.Texts(Line(["知ら", "ない", "よ"], spaced: true)));
        Assert.Equal(["daki", "shime", "te"], _t.Texts(Line(["抱き", "しめ", "て"], spaced: true)));
        Assert.Equal(["hi", "kkoshi", "no", "hi"], _t.Texts(Line(["引っ", "こし", "の", "日"], spaced: true)));
        Assert.Equal(["a", "kimi da", "yo"], _t.Texts(Line(["あっ", "君だ", "よ"], spaced: true)));
    }

    // Ported from RomanizationTests.swift: testPhraseSpacesOfWordTimedLinesAreKept
    [Fact]
    public void PhraseSpacesOfWordTimedLinesAreKept()
    {
        RequireJapaneseTokenizer();
        string[][] lines =
        [
            ["あいたい", "あいたい", "いますぐ"], ["まって", "まって", "いかないで"], ["そらを", "みあげて", "ないた"],
            ["もしもし", "かめよ", "かめさんよ"], ["ならんだ", "ならんだ", "あか", "しろ", "きいろ"],
            ["むすんで", "ひらいて", "てを", "うって", "むすんで"],
            ["えっ", "どうして", "なの"], ["あっ", "そう", "なんだ"], ["あっ", "きみだ", "よ"],
            ["もう", "ない", "よ"],
        ];
        string[][] withKanji =
        [
            ["君", "はなれて", "いく"], ["キミ", "はなれて", "いく"], ["夢", "のこして", "消えた"], ["思い", "はせて", "いる"],
            ["星", "はかない", "夢"], ["傷", "まだ", "痛む"], ["愛", "ささやく", "声"], ["月", "いま", "のぼる"], ["君", "へんじ", "ない"],
        ];
        void Check(string[] parts)
        {
            var reading = _t.Texts(Line(parts, spaced: true));
            Assert.Equal(_t.Texts(Line(parts, spaced: true, wordTimed: false)), reading);
            Assert.Equal(parts.Select(Romaji), reading);
        }
        foreach (var parts in lines) Check(parts);
        Assert.Equal(["aitai", "aitai", "ima sugu"], _t.Texts(Line(["あいたい", "あいたい", "いますぐ"], spaced: true)));
        Assert.Equal(["sora wo", "miagete", "naita"], _t.Texts(Line(["そらを", "みあげて", "ないた"], spaced: true)));
        Assert.Equal(["a", "kimi da", "yo"], _t.Texts(Line(["あっ", "きみだ", "よ"], spaced: true)));
        // A source reading is laid on the phrases it reads, not a itai or miagetenaita under one word and nothing
        // under the next.
        (string[] Parts, string Source, string[] Expected)[] sourced =
        [
            (["あいたい", "あいたい", "いますぐ"], "aitai aitai imasugu", ["aitai", "aitai", "ima sugu"]),
            (["まって", "まって", "いかないで"], "matte matte ikanaide", ["matte", "matte", "ikanaide"]),
            (["そらを", "みあげて", "ないた"], "sora wo miagete naita", ["sora wo", "miagete", "naita"]),
            (["えっ", "どうして", "なの"], "e doushite nano", ["e", "dou shite", "na no"]),
            (["あっ", "きみだ", "よ"], "a kimi da yo", ["a", "kimi da", "yo"]),
        ];
        foreach (var (parts, source, expected) in sourced) Assert.Equal(expected, _t.Texts(Line(parts, spaced: true, reading: source)));
        // "Replace lyrics" shows each phrase in its own time.
        var gasp = _t.Readings(Line(["あっ", "きみだ", "よ"], spaced: true), Options(RomanizationDisplay.Replace)) ?? [];
        Assert.Equal(["a", "kimi da", "yo"], gasp.Select(word => word.Text));
        foreach (var (word, next) in gasp.Zip(gasp.Skip(1))) Assert.True(word.End <= next.Start, word.Text);
        foreach (var parts in withKanji) Check(parts);
        Assert.Equal(["omo", "tta", "yori"], _t.Texts(Line(["思っ", "た", "より"], spaced: true, reading: "omotta yori")));
    }

    // Ported from RomanizationTests.swift: testMarkupSpacesSpacingCannotSettleReadAsSpaced
    [Fact]
    public void MarkupSpacesSpacingCannotSettleReadAsSpaced()
    {
        RequireJapaneseTokenizer();
        Assert.Equal(["ga", "kou", "e"], _t.Texts(Line(["がっ", "こう", "へ"], spaced: true)));
        Assert.Equal(["daki", "shime", "te"], _t.Texts(Line(["だき", "しめ", "て"], spaced: true)));
        var tomorrow = _t.Texts(Line(["明", "日が", "来る"], spaced: true));
        Assert.Equal(["hi ga", "kuru"], tomorrow.Skip(1));
        Assert.DoesNotContain(tomorrow.FirstOrDefault() ?? "", new[] { "asu", "ashita" });
        var love = _t.Texts(Line(["大", "好きだよ", "君が"], spaced: true));
        Assert.Equal(["suki da yo", "kimi ga"], love.Skip(1));
        var moving = _t.Texts(Line(["引っ", "越し", "の", "日"], spaced: true));
        Assert.Equal(["koshi", "no", "hi"], moving.Skip(1));
        Assert.DoesNotContain("hikk", Joined(moving), StringComparison.Ordinal);
        Assert.Equal(["hitori", "botchi", "no", "yoru"], _t.Texts(Line(["一人", "ぼっち", "の", "夜"], spaced: true)));
        Assert.Equal(["ki", "nai", "yo"], _t.Texts(Line(["来", "ない", "よ"], spaced: true)));
    }

    // MARK: - Saved and source readings

    // Ported from RomanizationTests.swift: testSyllableSplitSourceReadingIsPlacedOnItsCharacters
    [Fact]
    public void SyllableSplitSourceReadingIsPlacedOnItsCharacters()
    {
        const string source = "yu me na ra ba do re ho do yo ka tta de syo u";
        const string hepburn = "yu me na ra ba do re ho do yo ka tta de sho u";
        var characters = CharacterTimed("夢ならばどれほどよかったでしょう", reading: source);
        Assert.Equal(["yume", "na", "ra", "ba", "do", "re", "ho", "do", "yo", "ka", "", "tta", "de", "sho", "", "u"], _t.Texts(characters));
        Assert.Equal(Letters(hepburn), Letters(LyricRomanizer.LineReading(_t.Readings(characters), characters) ?? ""));
        var words = Line(["夢", "なら", "ば", "どれ", "ほど", "よかっ", "た", "でしょう"], reading: source);
        var placed = _t.Readings(words);
        Assert.Equal(words.Words.Count, placed?.Count);
        Assert.Equal("yume", placed?[0].Text);
        Assert.Equal(Letters(hepburn), Letters(Joined(placed?.Select(word => word.Text))));
        Assert.True(LyricReadingPairs.Match(placed, words.Words).Count >= 5);
    }

    // Ported from RomanizationTests.swift: testCountMatchedSyllablesLandOnTheRightCharacters
    [Fact]
    public void CountMatchedSyllablesLandOnTheRightCharacters()
    {
        var line = CharacterTimed("今日何食べた？", reading: "kyo u na ni ta be ta?");
        var parts = _t.Texts(line);
        Assert.Equal(7, parts.Length);
        Assert.Equal(["be", "ta?", ""], parts.TakeLast(3));
        Assert.Equal("kyounanita", string.Concat(parts.Take(4)));
        var love = CharacterTimed("愛してるって嘘で", reading: "a i shi te ru tte u so de");
        Assert.Equal(["ai", "shi", "te", "ru", "", "tte", "uso", "de"], _t.Texts(love));
    }

    // Ported from RomanizationTests.swift: testSavedReadingIsSplitByPhraseInsteadOfReplaced
    [Fact]
    public void SavedReadingIsSplitByPhraseInsteadOfReplaced()
    {
        var today = CharacterTimed("今日も", reading: "kyou mo");
        Assert.Equal(["kyo", "u", "mo"], _t.Texts(today));
        var adult = CharacterTimed("大人", start: 20, reading: "otona");
        Assert.Equal("otona", string.Concat(_t.Texts(adult, song: [today, adult])));
    }

    // Ported from RomanizationTests.swift: testSavedReadingSpellingsAreMatched
    [Fact]
    public void SavedReadingSpellingsAreMatched()
    {
        Assert.Equal(["Kyō", "", "mo"], _t.Texts(CharacterTimed("今日も", reading: "Kyō mo")));
        Assert.Equal(["kimi", "no", "na", "ha"], _t.Texts(CharacterTimed("君の名は", reading: "kimi no na ha")));
        Assert.Equal(["a", "ri", "ga", "to", ""], _t.Texts(CharacterTimed("ありがとう", reading: "arigato")));
        Assert.Equal(["KIMI", "NO", "NA", "WA"], _t.Texts(CharacterTimed("君の名は", reading: "KIMI-NO-NA-WA")));
        Assert.Equal(["kō", "", "hī", ""], _t.Texts(CharacterTimed("コーヒー", reading: "kōhī")));
        Assert.Equal(["ma", "", "cchi"], _t.Texts(CharacterTimed("マッチ", reading: "macchi")));
        Assert.Equal(["kimi no", "na wa"], _t.Texts(Line(["君の", "名は"], reading: "kimi no na wa")));
    }

    // Ported from RomanizationTests.swift: testJapaneseReadingOfOtherWordsGivesWayToTheGeneratedOne
    [Fact]
    public void JapaneseReadingOfOtherWordsGivesWayToTheGeneratedOne()
    {
        RequireJapaneseTokenizer();
        var generated = _t.TextsOrNull(Line(["今日", "も", "明日", "も"], wordTimed: false));
        Assert.NotNull(generated);
        // An extra word, or a reading in kana, does not read this line.
        foreach (var saved in new[] { "kyou mo ashita mo ne", "きょうもあしたも" })
        {
            Assert.Equal(generated, _t.Texts(Line(["今日", "も", "明日", "も"], wordTimed: false, reading: saved)));
        }
    }

    // Ported from RomanizationTests.swift: testUnsplittableReadingIsDrawnOnceUnderTheWholeLine (the Japanese part)
    [Fact]
    public void ReadingsSharedOutByCharacterAreNotDrawnAgainUnderTheLine()
    {
        RequireJapaneseTokenizer();
        var today = CharacterTimed("今日も");
        Assert.Equal(3, LyricReadingPairs.Match(_t.Readings(today), today.Words).Count);
        Assert.Null(LyricReadingPairs.Line(_t.Readings(today), today, RomanizationDisplay.Below));
    }

    // Ported from RomanizationTests.swift: testSourceReadingTakesPrecedenceOverGeneratedReading
    [Fact]
    public void SourceReadingTakesPrecedenceOverGeneratedReading()
    {
        // A transliteration from the lyric source, written without spaces.
        Assert.Equal(["kimi", "no", "na", "wa"], _t.Texts(CharacterTimed("君の名は", reading: "kiminonawa")));
        // Where it differs from the generated reading, the source wins...
        var spelled = CharacterTimed("君の名は", reading: "kimi no na ha");
        Assert.Equal("ha", _t.Texts(spelled).LastOrDefault());
        // ...unless saved readings are turned off.
        RequireJapaneseTokenizer();
        Assert.Equal("wa", _t.Texts(spelled, Options(preferSaved: false)).LastOrDefault());
    }

    // Ported from RomanizationTests.swift: testSourceReadingsAreShownInHepburn
    [Fact]
    public void SourceReadingsAreShownInHepburn()
    {
        // (Mac order changed: the checks that need no generated reading run first, so they also run here.)
        // Loanwords keep their spelling (ディ di).
        Assert.Equal(["me", "di", "", "a"], _t.Texts(CharacterTimed("メディア", reading: "me di a")));
        Assert.False(JapaneseAlignment.WritesNonHepburn("di", "di"));
        Assert.Equal("Hoshi", JapaneseAlignment.Respelled("Hosi"));
        Assert.Equal("SHŌ", JapaneseAlignment.Respelled("SYŌ"));
        // NetEase writes some of Lemon's syllables in Kunrei-shiki. Where they spell the same sounds as the app's own
        // reading, they are shown as it writes them: deshou, ijou and furi, not desyou, ijyou and huri. That needs the
        // app's reading of the kanji (以上 ijou).
        RequireJapaneseTokenizer();
        (string Text, string Source, string Hepburn)[] lemon =
        [
            ("夢ならばどれほどよかったでしょう", "yu me na ra ba do re ho do yo ka tta de syo u", "yumenarabadorehodoyokattadeshou"),
            ("きっともうこれ以上傷つくことなど", "ki tto mo u ko re i jyo u ki zu tsu ku ko to na do", "kittomoukoreijoukizutsukukotonado"),
            ("雨が降り止むまでは帰れない", "a me ga hu ri ya mu ma de wa ka e re na i", "amegafuriyamumadewakaerenai"),
        ];
        foreach (var (text, source, hepburn) in lemon)
        {
            var characters = CharacterTimed(text, reading: source);
            Assert.Equal(hepburn, Letters(LyricRomanizer.LineReading(_t.Readings(characters), characters) ?? ""));
            Assert.Equal(hepburn, Letters(Joined(_t.Texts(_t.LineTimed(text, reading: source)))));
        }
        Assert.Equal(["i", "jou"], _t.Texts(CharacterTimed("これ以上", reading: "ko re i jyo u")).TakeLast(2));
        // Capitals stay.
        Assert.Equal(["I", "JOU", "DA"], _t.Texts(CharacterTimed("以上だ", reading: "IJYOU DA")));
        // A reading of a kanji that differs from the app's is the source's own, as written ...
        Assert.Equal(["mazi", "de"], _t.Texts(Line(["本気", "で"], wordTimed: false, reading: "mazi de")));
        Assert.Equal(["sadame", "wo"], _t.Texts(Line(["運命", "を"], wordTimed: false, reading: "sadame wo")));
        // ... with its Kunrei-shiki onsets in Hepburn when the same reading writes Kunrei-shiki elsewhere.
        Assert.Equal(["maji", "deshou"], _t.Texts(Line(["本気", "でしょう"], wordTimed: false, reading: "mazi de syo u")));
        Assert.Equal(["MA", "JI", "DE", "SHO", "", "U"], _t.Texts(CharacterTimed("本気でしょう", reading: "MAZI DE SYO U")));
        Assert.Equal("hoshi", _t.Texts(Line(["一つ", "の", "地球"], wordTimed: false, reading: "hitotu no hosi")).LastOrDefault());
        Assert.Equal("hosi", _t.Texts(Line(["この", "地球"], wordTimed: false, reading: "kono hosi")).LastOrDefault());
        // English sung for a kanji is often shaped like romaji, and stays as written.
        (string[] Parts, string Reading)[] english =
        [
            (["情熱", "を"], "passion wo"), (["未来", "が"], "vision ga"), (["亜細亜", "へ"], "Asia e"),
            (["状況", "を"], "situation wo"), (["使命", "を"], "mission wo"), (["音楽", "を"], "music wo"),
        ];
        foreach (var (parts, reading) in english) Assert.Equal(reading.Split(' '), _t.Texts(Line(parts, wordTimed: false, reading: reading)));
        Assert.Equal(["disutinii", "wo"], _t.Texts(Line(["運命", "を"], wordTimed: false, reading: "disutinii wo")));
        Assert.Equal(["tiaazu", "ga"], _t.Texts(Line(["涙", "が"], wordTimed: false, reading: "tiaazu ga")));
    }

    // Ported from RomanizationTests.swift: testKanaAndHangulKeepTheirLanguageWhenHanIsForced (the kana part)
    [Fact]
    public void KanaKeepsItsLanguageWhenHanIsForced()
    {
        RequireJapaneseTokenizer();
        Assert.Equal("kimi no na wa", Joined(_t.Texts(_t.LineTimed("君の名は"), Options(language: RomanizationLanguage.Mandarin))));
    }

    // Ported from ReadingAndTTMLTests.swift: testJapaneseKanaDoesNotFallBackToMandarin. Its lines are attributed to
    // the Verso import, so this is written from the behaviour (kana read as Japanese give romaji), same input.
    [Fact]
    public void JapaneseKanaDoesNotFallBackToMandarin() =>
        Assert.Contains("arigat", _t.Romanizer.Romanize("ありがとう", RomanizationLanguage.Japanese, toneMarks: false).ToLowerInvariant(),
            StringComparison.Ordinal);
}
