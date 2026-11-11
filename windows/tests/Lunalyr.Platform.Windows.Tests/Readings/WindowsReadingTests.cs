using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Lyrics.Readings.Japanese;
using Lunalyr.Platform.Readings;
using Lunalyr.Tests.Shared;
using static Lunalyr.Tests.Shared.ReadingTestLines;
using Lunalyr.Core.Motion;

namespace Lunalyr.Platform.Windows.Tests.Readings;

/// <summary>What the Windows reading services are on this PC, and that they behave as the readings expect.</summary>
public class WindowsReadingServicesTests
{
    [Fact]
    public void IcuElsAndTheBundledJapaneseDictionaryAreThereWithoutExtraInstalls()
    {
        var services = TestReadingServices.Windows;
        TestContext.Current.SendDiagnosticMessage(WindowsReadingServices.Describe(services));
        Assert.IsType<IcuTransliterator>(services.Transliterator);
        var breaker = Assert.IsType<JapaneseWordBreaker>(services.WordBreaker);
        Assert.IsType<IcuWordBreaker>(breaker.Other);
        Assert.IsType<ElsLanguageDetector>(services.LanguageGuesser);
        Assert.IsType<DictionaryJapaneseTokenizer>(services.JapaneseTokenizer);
    }

    // The readings services plug into the parsers' reading seam: LyricTextRulesTests.swift's
    // testReadingsAreToldApartFromTranslations table, with Windows' readers in place of the built-in one.
    [Fact]
    public void TheParsersTellReadingsFromTranslationsWithTheWindowsReaders()
    {
        var options = TestReadingServices.Windows.ParseOptions();
        Assert.NotNull(options.Distribute); // ICU's word breaks spread estimated words
        (string Candidate, string Lyric)[] readings =
        [
            ("kimi no na wa", "君の名は"), ("yu me na ra ba do re ho do yo ka tta de syo u", "夢ならばどれほどよかったでしょう"),
            ("wǒ ài nǐ", "我爱你"), ("ngo5 oi3 nei5", "我爱你"), ("saranghae", "사랑해"), ("Ya tebya lyublyu", "Я тебя люблю"),
            ("Hello sekai", "Hello 世界"), ("eien", "永遠"), ("wo ai ni", "我爱你"),
        ];
        (string Candidate, string Lyric)[] translations =
        [
            ("Your name", "君の名は"), ("I love you", "사랑해"), ("Hello world", "Hello 世界"), ("我爱你", "I love you"),
            ("I love you", "I love you"), ("Forever", "永遠"), ("If only it were a dream", "夢ならばどれほどよかったでしょう"),
        ];
        var cache = options.ReadingCache();
        foreach (var (candidate, lyric) in readings) Assert.True(LyricScript.IsReading(candidate, lyric, cache), candidate);
        foreach (var (candidate, lyric) in translations) Assert.False(LyricScript.IsReading(candidate, lyric, cache), candidate);
        // ICU splits unspaced Chinese into words for estimated timing, where the built-in splitting gives one per character.
        var words = options.Distribute!("我们一起走", 0, 4).Select(word => word.Text).ToList();
        Assert.True(words.Count < 5, string.Join("|", words));
    }

    // Every lyric source parses on its own thread-pool thread, so the readers and the word breaker the parsers get are
    // called from many threads at once (the transliterator locks, the word breaker opens an iterator per call, the
    // Japanese analyzer runs on its own STA thread).
    [Fact]
    public async Task TheParserServicesAreSafeFromManyThreadsAtOnce()
    {
        var options = TestReadingServices.Windows.ParseOptions();
        var expectedWords = options.Distribute!("我们一起走过的路", 0, 4).Select(word => word.Text).ToList();
        var expectedReading = LyricScript.IsReading("kimi no na wa", "君の名は", options.ReadingCache());
        var runs = Enumerable.Range(0, 64).Select(i => Task.Run(() =>
        {
            for (var round = 0; round < 20; round++)
            {
                Assert.Equal(expectedWords, options.Distribute!("我们一起走过的路", 0, 4).Select(word => word.Text));
                Assert.Equal(expectedReading, LyricScript.IsReading("kimi no na wa", "君の名は", options.ReadingCache()));
                Assert.False(LyricScript.IsReading("I love you", "我爱你", options.ReadingCache()));
            }
        }, TestContext.Current.CancellationToken));
        await Task.WhenAll(runs);
    }

    [Fact]
    public void IcuTransliteratesLikeApplesTransforms()
    {
        var icu = IcuTransliterator.TryCreate()!;
        Assert.Equal("nǐ hǎo", icu.MandarinToLatin("你好"));
        Assert.Equal("wǒ men yī qǐ zǒu", icu.MandarinToLatin("我们一起走"));
        Assert.Equal("Privet, mir", icu.ToLatin("Привет, мир"));
        Assert.Equal("arigatou", icu.ToLatin("ありがとう"));
        // Small kana and a final っ in the "~" form Apple's tokenizer also gives, which JapaneseReading expects.
        Assert.Equal("omo~tsu", icu.ToLatin("おもっ"));
        Assert.Equal("ne~e", icu.ToLatin("ねぇ"));
        Assert.Equal("rāmen", icu.ToLatin("ラーメン"));
    }

    [Fact]
    public void IcuWordBreaksUseTheCjkAndThaiDictionaries()
    {
        var breaker = IcuWordBreaker.TryCreate()!;
        string Words(string text) => string.Join("|", breaker.Words(text).Select(word => text.Substring(word.Start, word.Length)));
        Assert.Equal("我们|一起|走过|春天|的|小路", Words("我们一起走过春天的小路"));
        Assert.Equal("แมว|นอน|บน|โต๊ะ", Words("แมวนอนบนโต๊ะ"));
        Assert.Equal("Okay|you're|mine", Words("Okay, you're mine!"));
        // ICU's split of a line the Mac tests use; Apple's differs (inferred from the Mac's expectations, not observed).
        // Japanese lines therefore go to the bundled analyser instead (JapaneseWordBreaksComeFromTheBundledAnalyser).
        Assert.Equal("窓辺|に|座|って|海|を|見|てい|た", Words("窓辺に座って海を見ていた"));
    }

    [Fact]
    public void JapaneseWordBreaksComeFromTheBundledAnalyser()
    {
        var breaker = TestReadingServices.Windows.WordBreaker;
        string Words(string text) => string.Join("|", breaker.Words(text).Select(word => text.Substring(word.Start, word.Length)));
        // An auxiliary verb stays on what it inflects (会おう, いた); particles and te stand apart.
        Assert.Equal("窓辺|に|座っ|て|海|を|見|て|いた", Words("窓辺に座って海を見ていた"));
        Assert.Equal("５|時|に|会おう", Words("５時に会おう"));
        // Latin words, punctuation and spaces around Japanese go to ICU; text without kana is ICU's alone.
        Assert.Equal("Hey|君|が|好き", Words("Hey, 君が好き!"));
        Assert.Equal("Okay|you're|mine", Words("Okay, you're mine!"));
        Assert.Equal("我们|一起|走过|春天|的|小路", Words("我们一起走过春天的小路"));
    }

    [Fact]
    public void ElsDetectsTheLanguagesOfLyrics()
    {
        var els = ElsLanguageDetector.TryCreate()!;
        Assert.Equal("ja", els.DominantLanguage("君の名は\n夜に駆ける"));
        Assert.Equal("zh-Hans", els.DominantLanguage("我爱你中国\n我们一起走\n天空很蓝\n你是我的光"));
        Assert.Equal("zh-Hant", els.DominantLanguage("我們的關係\n你是我的光\n天空很藍"));
        Assert.Equal("ko", els.DominantLanguage("사랑해 같이 가자"));
        Assert.Equal("en", els.DominantLanguage("I will always love you, no matter where you go tonight"));
        Assert.Equal("fr", els.DominantLanguage("Je t'aime plus que tout au monde, mon amour"));
        // Too short to tell from a spaced script: unknown, as the Mac's confidence threshold would say.
        Assert.Null(els.DominantLanguage("Hello"));
        Assert.Equal("ja", els.Language(["月が綺麗ですね", "走り出す夜"]));
    }
}

/// <summary>The pinyin, Cantonese and script-coverage parts of the Mac's RomanizationTests and
/// RomanizationScriptCoverageTests, which need ICU.</summary>
public class MandarinAndScriptRomanizationTests
{
    private readonly ReadingTestLines _t = new(TestReadingServices.Windows);

    // Ported from RomanizationScriptCoverageTests.swift: testEveryNonLatinScriptIsRomanised
    [Fact]
    public void EveryNonLatinScriptIsRomanised()
    {
        string[] samples =
        [
            "ਸਤ ਸ੍ਰੀ ਅਕਾਲ", "নমস্কার বন্ধু", "வணக்கம் நண்பா", "నమస్కారం మిత్రమా", "નમસ્તે મિત્ર", "ನಮಸ್ಕಾರ ಗೆಳೆಯ",
            "നമസ്കാരം സുഹൃത്തേ", "გამარჯობა მეგობარო", "Բարեւ ընկեր", "नमस्ते दुनिया", "Привет, мир",
        ];
        foreach (var text in samples)
        {
            Assert.True(LyricRomanizer.ContainsNonLatin(text), text);
            var reading = _t.Romanizer.Romanize(text, RomanizationLanguage.Automatic, toneMarks: false);
            Assert.True(reading.Any(char.IsAsciiLetter), $"{text} -> {reading}");
            var left = reading.EnumerateRunes().Where(ReadingText.IsAlphabetic).ToHashSet();
            Assert.True(!left.Overlaps(text.EnumerateRunes()), $"Untransliterated letters: {text} -> {reading}");
        }
        Assert.Contains("namaste", _t.Romanizer.Romanize("नमस्ते दुनिया", RomanizationLanguage.Automatic, toneMarks: false).ToLowerInvariant(),
            StringComparison.Ordinal);
    }

    // Ported from RomanizationTests.swift: testLineReadingIsOnlyForPhrasesSpreadOverCharacters (the pinyin part)
    [Fact]
    public void LineReadingIsOnlyForPhrasesSpreadOverCharacters()
    {
        var mandarin = CharacterTimed("我爱你中国");
        Assert.Equal(5, _t.Texts(mandarin, Options(language: RomanizationLanguage.Mandarin)).Length);
        Assert.Null(LyricRomanizer.LineReading(_t.Readings(mandarin, Options(language: RomanizationLanguage.Mandarin)), mandarin));
    }

    // Ported from RomanizationTests.swift: testCantoneseSongGetsNoMandarinPinyin
    [Fact]
    public void CantoneseSongGetsNoMandarinPinyin()
    {
        LyricLine[] song = [_t.LineTimed("佢哋係我嘅朋友"), Line(["我", "愛", "你"], start: 4, wordTimed: false), _t.LineTimed("唔好走", start: 8)];
        Assert.Equal(RomanizationLanguage.Cantonese, _t.Romanizer.SongLanguage(song));
        Assert.Empty(_t.Song(song));
        var withReading = song.ToArray();
        withReading[1] = withReading[1] with { Romanization = "ngo5 oi3 nei5" };
        var readings = _t.Song(withReading, Options(preferSaved: false));
        Assert.Equal([1], readings.Keys.Order());
        Assert.Equal(["ngo5", "oi3", "nei5"], readings[1].Select(word => word.Text));
    }

    // Ported from RomanizationTests.swift: testCantoneseMarkersAreCountedConservatively
    [Fact]
    public void CantoneseMarkersAreCountedConservatively()
    {
        Assert.True(LyricRomanizer.CantoneseScore("佢哋係我嘅朋友") >= 2);
        Assert.True(LyricRomanizer.CantoneseScore("我們的關係") < 1);
        LyricLine[] mandarin = [_t.LineTimed("我們的關係"), _t.LineTimed("你是我的光", start: 4), _t.LineTimed("天空很藍", start: 8)];
        Assert.Equal(RomanizationLanguage.Mandarin, _t.Romanizer.SongLanguage(mandarin));
        Assert.StartsWith("wǒ men", Joined(_t.Texts(mandarin[0], song: mandarin)), StringComparison.Ordinal);
        // A line of Cantonese inside a Mandarin song gets no pinyin...
        var mixed = mandarin.Append(_t.LineTimed("我唔知", start: 12)).ToList();
        Assert.Equal(RomanizationLanguage.Mandarin, _t.Romanizer.SongLanguage(mixed));
        var readings = _t.Song(mixed);
        Assert.False(readings.ContainsKey(3));
        Assert.True(readings.ContainsKey(1));
        // ...while plainly written Cantonese marks the whole song as sung in it.
        Assert.Equal(RomanizationLanguage.Cantonese, _t.Romanizer.SongLanguage(mandarin.Append(_t.LineTimed("佢唔係你", start: 12)).ToList()));
    }

    // Ported from RomanizationTests.swift: testMandarinPhrasesAreNotTakenForCantonese
    [Fact]
    public void MandarinPhrasesAreNotTakenForCantonese()
    {
        string[] phrases = ["给我一点解释", "在你身边度过每一天", "雨后的美好彩虹", "在海邊度假的日子", "钢琴日记", "唔 我想你", "房屋企业"];
        foreach (var phrase in phrases) Assert.True(LyricRomanizer.CantoneseScore(phrase) < 1, phrase);
        var song = new[] { "我爱你中国", "我们一起走", "天空很蓝", "你是我的光" }.Concat(phrases)
            .Select((text, index) => _t.LineTimed(text, start: index * 4)).ToList();
        Assert.Equal(RomanizationLanguage.Mandarin, _t.Romanizer.SongLanguage(song));
        Assert.Equal(song.Count, _t.Song(song).Count);
    }

    // Ported from RomanizationTests.swift: testOneCantoneseLineDoesNotStripAMandarinSong
    [Fact]
    public void OneCantoneseLineDoesNotStripAMandarinSong()
    {
        string[] mandarin = ["我爱你中国", "我们一起走", "天空很蓝", "你是我的光", "让我们唱歌", "夜空中最亮的星", "请照亮我前行", "我想要飞"];
        var song = mandarin.Append("佢唔係你").Select((text, index) => _t.LineTimed(text, start: index * 4)).ToList();
        Assert.Equal(RomanizationLanguage.Mandarin, _t.Romanizer.SongLanguage(song));
        var readings = _t.Song(song);
        Assert.False(readings.ContainsKey(8));
        Assert.Equal(Enumerable.Range(0, 8), readings.Keys.Order());
    }

    // Ported from RomanizationTests.swift: testJapanesePhraseDoesNotMakeAMandarinSongJapanese
    [Fact]
    public void JapanesePhraseDoesNotMakeAMandarinSongJapanese()
    {
        var song = new[] { "我爱你中国", "我们一起走", "天空很蓝", "你是我的光", "让我们唱歌" }
            .Select((text, index) => _t.LineTimed(text, start: index * 4)).Append(_t.LineTimed("ありがとう", start: 40)).ToList();
        Assert.Equal(RomanizationLanguage.Mandarin, _t.Romanizer.SongLanguage(song));
        Assert.Equal("wǒ men yī qǐ zǒu", Joined(_t.Texts(song[1], song: song)));
        Assert.Equal("arigatou", Joined(_t.Texts(song[5], song: song)));
    }

    // Mac test: ReadingAndTTMLTests.swift testPinyinKeepsOriginalWordTimes. Its lines are attributed to the Verso
    // import, so this is written from the behaviour (pinyin without tone marks keeps the word's own times), with the
    // same input and expectation.
    [Fact]
    public void PinyinKeepsOriginalWordTimes()
    {
        var line = new LyricLine { Start = 1, End = 3, Text = "你好", Words = [new LyricWord("你好", 1.2, 2.7)], WordTimed = true };
        var result = _t.Song([line], new RomanizationOptions(RomanizationDisplay.Below, RomanizationLanguage.Mandarin, ToneMarks: false, PreferSaved: false));
        Assert.Equal("ni hao", result[0][0].Text.ToLowerInvariant());
        Assert.Equal(1.2, result[0][0].Start);
        Assert.Equal(2.7, result[0][0].End);
    }
}

/// <summary>The parts of the Mac's LyricRunReadingTests that need ICU or the Japanese tokenizer.</summary>
public class WindowsLyricRunReadingTests
{
    private readonly ReadingTestLines _t = new(TestReadingServices.Windows);

    private string[] RunReadings(LyricLine line, RomanizationOptions? options = null)
    {
        var glue = LyricRuns.Glue(line, line.Words);
        var pairs = LyricReadingPairs.Match(_t.Readings(line, options), line.Words, glue);
        return line.Words.Where(pairs.ContainsKey).Select(word => pairs[word]).ToArray();
    }

    // Ported from LyricRunReadingTests.swift: testACharacterTimedRunReadsByPhraseAsTheLineReadingDoes
    [Fact]
    public void ACharacterTimedRunReadsByPhraseAsTheLineReadingDoes()
    {
        var today = CharacterTimed("今日も");
        Assert.Equal([false, true, true], LyricRuns.Glue(today, today.Words));
        Assert.Equal(["kyou mo"], RunReadings(today));
        LyricLine[] lines =
        [
            today, CharacterTimed("思った"), CharacterTimed("はなれないで"),
            CharacterTimed("夢ならばどれほどよかったでしょう", reading: "yu me na ra ba do re ho do yo ka tta de syo u"),
            CharacterTimed("今日何食べた", reading: "kyo u na ni ta be ta"),
        ];
        foreach (var line in lines)
        {
            var expected = LyricRomanizer.LineReading(_t.Readings(line), line);
            Assert.NotNull(expected);
            Assert.Equal([expected], RunReadings(line));
        }
        Assert.Contains(" ", RunReadings(lines[3])[0], StringComparison.Ordinal);
    }

    // Ported from LyricRunReadingTests.swift: testALineTimedJapaneseRunKeepsTheReadingsWordsApart
    [Fact]
    public void ALineTimedJapaneseRunKeepsTheReadingsWordsApart()
    {
        // Without kanji readings Windows gives "kyou mo mata kimi wo omo u" (JapaneseWithoutAnalyzerTests).
        var line = Line(["今日", "も", "また", "君", "を", "想う"], wordTimed: false, reading: "kyou mo mata kimi wo omou");
        Assert.Equal(["kyou mo mata kimi wo omou"], RunReadings(line));
        var name = Line(["君の", "名は"], wordTimed: false, reading: "kimi no na wa");
        Assert.Equal(["kimi no na wa"], RunReadings(name));
    }

    // Ported from LyricRunReadingTests.swift: testMandarinRunsAreReadSyllableBySyllable
    [Fact]
    public void MandarinRunsAreReadSyllableBySyllable()
    {
        var mandarin = Options(language: RomanizationLanguage.Mandarin);
        var hawk = CharacterTimed("半天上的");
        var parts = _t.Readings(hawk, mandarin)!.Select(word => word.Text).ToList();
        Assert.Equal(4, parts.Count);
        Assert.Equal([string.Join(" ", parts)], RunReadings(hawk, mandarin));
        var fingertip = Line(["感受", "停在", "我"], wordTimed: false);
        var words = _t.Readings(fingertip, mandarin)!.Select(word => word.Text);
        var joined = RunReadings(fingertip, mandarin)[0];
        Assert.Equal(string.Join(" ", words), joined);
        Assert.Equal(5, joined.Split(' ').Length);
    }

    // Ported from LyricRunReadingTests.swift: testThaiIsWrittenWithoutSpacesAndKoreanIsNot (the rolling glue)
    [Fact]
    public void AThaiLineRollsAsItsWords()
    {
        var syllables = new[] { "แมว", "นอน", "บน", "โต๊ะ" }.Select((text, index) => new LyricWord(text, index, index + 1)).ToList();
        var cat = new LyricLine { Start = 0, End = 4, Text = "แมวนอนบนโต๊ะ", Words = syllables, WordTimed = true };
        Assert.Equal(["แมว", "นอน", "บน", "โต๊ะ"], LyricRuns.Labels(syllables, LyricRuns.RollingGlue(cat, syllables, _t.Services.WordBreaker)));
    }

    // Ported from LyricRunReadingTests.swift: testLatinReadingsOfChineseAndJapaneseCharactersStandApart (the
    // generated part)
    [Fact]
    public void LatinReadingsOfChineseAndJapaneseCharactersStandApart()
    {
        var name = CharacterTimed("君の名は");
        var replaced = _t.Readings(name, Options(RomanizationDisplay.Replace))!;
        Assert.Equal(name.Words.Count, replaced.Count);
        Assert.Equal([false, false, false, false], LyricRuns.Glue(name, replaced));
        Assert.Equal(replaced.Select(word => word.Text), LyricRuns.Labels(replaced, LyricRuns.Glue(name, replaced)));
    }

    // Ported from LyricRunReadingTests.swift: testAConstellationPartsAnUnspacedLineIntoItsWords (the parting; the
    // layout is view code)
    [Fact]
    public void AConstellationPartsAnUnspacedLineIntoItsWords()
    {
        const string text = "沈むように溶けてゆくように";
        var line = CharacterTimed(text);
        var glue = LyricRuns.Glue(line, line.Words);
        Assert.Equal([text], LyricRuns.Labels(line.Words, glue));
        var split = LyricRuns.SplittingWords(line.Words, glue, _t.Services.WordBreaker);
        var words = LyricRuns.Labels(line.Words, split);
        Assert.True(words.Count >= 2, string.Join("|", words));
        Assert.Equal(text, string.Concat(words));
        Assert.True(words.All(word => word.Length >= 2), string.Join("|", words));
        foreach (var other in new[] { "君の名は希望", "半天上的秃鹰那张脸" })
        {
            var timed = CharacterTimed(other);
            var parts = LyricRuns.Labels(timed.Words, LyricRuns.SplittingWords(timed.Words, LyricRuns.Glue(timed, timed.Words), _t.Services.WordBreaker));
            Assert.True(parts.Count >= 2, string.Join("|", parts));
            Assert.True(parts.Count < timed.Words.Count / 2, "phrases, not characters: " + string.Join("|", parts));
            Assert.Equal(other, string.Concat(parts));
        }
        // Readings follow the words (generated readings need the Japanese tokenizer).
        var beautiful = new LyricLine
        {
            Start = 0, End = 1.2, Text = "So beautiful", WordTimed = true,
            Words = [new("So", 0, 0.3), new("beau", 0.3, 0.6), new("ti", 0.6, 0.9), new("ful", 0.9, 1.2)],
        };
        var latin = LyricRuns.Glue(beautiful, beautiful.Words);
        Assert.Equal(latin, LyricRuns.SplittingWords(beautiful.Words, latin, _t.Services.WordBreaker));
        Assert.Equal(words.Count, LyricReadingPairs.Match(_t.Readings(line), line.Words, split).Count);
    }

    // Ported from LyricRunReadingTests.swift: testARollingStagePartsAnUnspacedLineIntoItsWords
    [Fact]
    public void ARollingStagePartsAnUnspacedLineIntoItsWords()
    {
        var breaker = _t.Services.WordBreaker;
        var seaside = CharacterTimed("窓辺に座って海を見ていた");
        var glue = LyricRuns.Glue(seaside, seaside.Words);
        // The Mac's expectations, restored: the bundled analyser's split (窓辺|に|座っ|て|海|を|見|て|いた) parts the
        // line as Apple's does. (ICU alone gave 窓辺|に|座|って|海|を|見|てい|た, which kept the constellation whole and
        // set 見 apart from ていた.)
        Assert.Equal(["窓辺に", "座って海を見ていた"], LyricRuns.Labels(seaside.Words, LyricRuns.SplittingWords(seaside.Words, glue, breaker)));
        var rolling = LyricRuns.RollingGlue(seaside, seaside.Words, breaker);
        Assert.Equal(["窓辺に", "座って", "海を", "見ていた"], LyricRuns.Labels(seaside.Words, rolling));
        // What goes on the word before it, and what begins one.
        Assert.True(JapaneseReading.ContinuesPhrase("を", "海"));
        Assert.True(JapaneseReading.ContinuesPhrase("た", "歩い"));
        Assert.True(JapaneseReading.ContinuesPhrase("ない", "見"));
        Assert.False(JapaneseReading.ContinuesPhrase("ない", "が"));
        Assert.True(JapaneseReading.ContinuesPhrase("度", "何"));
        Assert.True(JapaneseReading.ContinuesPhrase("いる", "て"));
        Assert.True(JapaneseReading.ContinuesPhrase("ー", "ラ"));
        Assert.False(JapaneseReading.ContinuesPhrase("きみ", "て"));
        Assert.False(JapaneseReading.ContinuesPhrase("海", "て"));
        Assert.False(JapaneseReading.ContinuesPhrase("帰り道", "の"));
        // Chinese keeps a single character with the word before it, as a constellation does.
        var spring = CharacterTimed("我们一起走过春天的小路");
        Assert.Equal(["我们", "一起", "走过", "春天的", "小路"], LyricRuns.Labels(spring.Words, LyricRuns.RollingGlue(spring, spring.Words, breaker)));
        // Readings follow those words (generated readings need the Japanese tokenizer).
        var pairs = LyricReadingPairs.Match(_t.Readings(seaside), seaside.Words, rolling);
        Assert.Equal(LyricRuns.Labels(seaside.Words, rolling).Count, pairs.Count);
        Assert.True(pairs.Values.All(value => value.Length > 0));
    }
}

/// <summary>The Mac's StageReadingRevealTests: a word-timed Japanese song from Apple Music with the source's own
/// romanisation for each line, paired run by run as the stage layouts draw it.</summary>
public class StageReadingRevealTests
{
    private readonly ReadingTestLines _t = new(TestReadingServices.Windows);

    private static LyricLine Line(string romanization, (string Text, double Start, double End)[] words)
    {
        var timed = words.Select(word => new LyricWord(word.Text, word.Start, word.End)).ToList();
        return new LyricLine
        {
            Start = timed[0].Start, End = timed[^1].End, Text = string.Concat(words.Select(word => word.Text)), Words = timed,
            WordTimed = true, Romanization = romanization,
        };
    }

    private static readonly LyricLine[] Lines =
    [
        Line("sugite yukutokinonaka", [("過ぎてゆく", 14.88, 16.11), ("時間", 16.33, 17.23), ("の", 17.23, 17.45), ("中", 17.45, 18.2)]),
        Line("anata o omoidasu", [("あなたを", 18.61, 19.56), ("思", 19.56, 20.57), ("い", 20.57, 20.92), ("出", 20.92, 21.13), ("す", 21.13, 21.69)]),
        Line("monouge ni nagameru gamen ni utsutta futari",
            [("物憂げに", 22.34, 23.56), ("眺める", 23.73, 24.86), ("画面", 24.86, 25.53), ("に", 25.53, 25.78), ("映った", 25.78, 26.67), ("二人", 26.67, 27.63)]),
        Line("waratteta", [("笑っ", 27.64, 28.29), ("てい", 28.29, 28.57), ("た", 28.57, 28.87)]),
    ];

    private static readonly RomanizationOptions Options = new(RomanizationDisplay.Below, RomanizationLanguage.Automatic, true, true);

    private string?[] StageReadings(int index)
    {
        var line = Lines[index];
        var readings = _t.Song(Lines, Options).GetValueOrDefault(index);
        var glue = LyricRuns.Glue(line, line.Words);
        var pairs = LyricReadingPairs.Match(readings, line.Words, StageRun.Glue(line.Words, glue));
        return StageRun.Make(line.Words, glue).Select(run => StageRun.Reading(run, pairs)).ToArray();
    }

    // Ported from StageReadingRevealTests.swift: testEachRunCarriesOnlyItsOwnReading
    [Fact]
    public void EachRunCarriesOnlyItsOwnReading()
    {
        Assert.Equal(["sugite yuku", "toki no", "naka"], StageReadings(0));
        Assert.Equal(["anata o", "omoida", "su"], StageReadings(1));
        Assert.Equal(["monou ge ni", "nagameru", "gamen ni", "utsutta", "futari"], StageReadings(2));
        Assert.Equal(["wara", "tteta"], StageReadings(3));
    }

    // Ported from StageReadingRevealTests.swift: testAPhraseIsSharedOverTheWordsItIsSungAcross
    [Fact]
    public void APhraseIsSharedOverTheWordsItIsSungAcross()
    {
        var readings = _t.Song(Lines, Options);
        Assert.Equal(["wara", "tte", "ta"], readings[3].Select(word => word.Text));
        Assert.Equal(["anata o", "omo", "i", "da", "su"], readings[1].Select(word => word.Text));
        var line = Lines[3];
        var pairs = LyricReadingPairs.Match(readings[3], line.Words, [false, true, true]);
        Assert.Equal("waratteta", pairs[line.Words[0]]);
    }

    // Ported from StageReadingRevealTests.swift: testLinePairingWouldPutTheWholeReadingOnTheFirstRun
    [Fact]
    public void LinePairingWouldPutTheWholeReadingOnTheFirstRun()
    {
        var line = Lines[0];
        var readings = _t.Song(Lines, Options).GetValueOrDefault(0);
        var glue = LyricRuns.Glue(line, line.Words);
        var pairs = LyricReadingPairs.Match(readings, line.Words, glue);
        Assert.Equal(["sugite yuku toki no naka", null, null], StageRun.Make(line.Words, glue).Select(run => StageRun.Reading(run, pairs)));
    }
}
