using System.Text;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Tests.Shared;
using static Lunalyr.Tests.Shared.ReadingTestLines;

namespace Lunalyr.Core.Tests.Lyrics.Readings;

/// <summary>
/// The parts of the Mac's RomanizationTests that need no system services: Korean, saved readings of Chinese and
/// Korean, Cantonese evidence and stored values. They run with <see cref="ReadingServices.Basic"/>. The Japanese and
/// pinyin tests are in Lunalyr.Platform.Windows.Tests, which has ICU and the Japanese tokenizer.
/// </summary>
public class RomanizationTests
{
    private readonly ReadingTestLines _t = new(ReadingServices.Basic);

    // MARK: - Saved and source readings

    // Ported from RomanizationTests.swift: testUnsplittableSavedReadingStaysWholeUnderTheLine
    [Fact]
    public void UnsplittableSavedReadingStaysWholeUnderTheLine()
    {
        var line = Line(["사랑해", "같이"], spaced: true, wordTimed: false, reading: "saranghae gachi gaja");
        var placed = _t.Readings(line);
        Assert.Equal(["saranghae gachi gaja"], placed!.Select(word => word.Text));
        Assert.Equal(line.Words[0].Start, placed![0].Start);
        Assert.Equal(line.Words[^1].End, placed[0].End);
        Assert.Equal("saranghae gachi gaja", LyricRomanizer.LineReading(placed, line));
        // Replacing the lyric spreads the saved reading across the line.
        Assert.Equal(["saranghae", "gachi", "gaja"], _t.Texts(line, Options(RomanizationDisplay.Replace)));
    }

    // Ported from RomanizationTests.swift: testUnsplittableReadingIsDrawnOnceUnderTheWholeLine (the Korean and
    // Russian lines; the Japanese part is in the Windows tests)
    [Fact]
    public void UnsplittableReadingIsDrawnOnceUnderTheWholeLine()
    {
        LyricLine[] lines =
        [
            _t.LineTimed("사랑 해 너를", reading: "saranghae neoreul"),
            Line(["사랑해", "같이"], spaced: true, wordTimed: false, reading: "saranghae gachi gaja"),
            Line(["사랑해", "같이"], spaced: true, reading: "saranghae gachi gaja"),
            _t.LineTimed("Я тебя люблю", reading: "Ya tebya ochen' lyublyu"),
        ];
        foreach (var line in lines)
        {
            var saved = line.Romanization!;
            Assert.True(line.Words.Count > 1, line.Text);
            // "Below lyrics": no word gets a share, and the line gets it all.
            var below = _t.Readings(line);
            Assert.Empty(LyricReadingPairs.Match(below, line.Words));
            Assert.Equal(saved, LyricReadingPairs.Line(below, line, RomanizationDisplay.Below));
            // "Replace lyrics" sets it in the line's place, over the line's time.
            var replaced = _t.Readings(line, Options(RomanizationDisplay.Replace));
            Assert.NotNull(replaced);
            Assert.Equal(saved, string.Join(" ", replaced!.Select(word => word.Text)));
            Assert.Null(LyricReadingPairs.Line(replaced, line, RomanizationDisplay.Replace));
            Assert.True(replaced[0].Start >= line.Start, line.Text);
            Assert.True(replaced[^1].End <= line.End, line.Text);
        }
        // A line of one word keeps it under that word.
        var word = _t.LineTimed("Люблю", reading: "Lyublyu tebya");
        Assert.Equal(new Dictionary<LyricWord, string> { [word.Words[0]] = "Lyublyu tebya" }, LyricReadingPairs.Match(_t.Readings(word), word.Words));
        Assert.Null(LyricReadingPairs.Line(_t.Readings(word), word, RomanizationDisplay.Below));
    }

    // Ported from RomanizationTests.swift: testRollingStageDrawsAnUnsplittableReadingUnderTheLinesFirstWord (the
    // readings a rolling stage pairs, LyricReadingPairs.Rolling; the layer cache's window is view code)
    [Fact]
    public void RollingStageDrawsAnUnsplittableReadingUnderTheLinesFirstWord()
    {
        var line = Line(["사랑해", "같이"], spaced: true, reading: "saranghae gachi gaja");
        var placed = _t.Readings(line);
        Assert.NotNull(placed);
        Assert.Equal(new Dictionary<LyricWord, string> { [line.Words[0]] = "saranghae gachi gaja" },
            LyricReadingPairs.Rolling(placed, line, LyricRuns.Glue(line, line.Words)));
        // Readings that do split by word stay with their words.
        var split = Line(["사랑해", "같이"], spaced: true, reading: "saranghae gachi");
        Assert.Equal(new Dictionary<LyricWord, string> { [split.Words[0]] = "saranghae", [split.Words[1]] = "gachi" },
            LyricReadingPairs.Rolling(_t.Readings(split), split, LyricRuns.Glue(split, split.Words)));
    }

    // Ported from RomanizationTests.swift: testSavedChineseReadingsAlignByCharacter
    [Fact]
    public void SavedChineseReadingsAlignByCharacter()
    {
        var word = new LyricLine
        {
            Start = 1, End = 3, Text = "我愛你", Words = [new LyricWord("我愛你", 1, 3)], WordTimed = true, Romanization = "ngo5 oi3 nei5",
        };
        Assert.Equal(["ngo5 oi3 nei5"], _t.Texts(word));
        var pair = new LyricLine
        {
            Start = 1, End = 3, Text = "我愛你", Words = [new LyricWord("我", 1, 1.5), new LyricWord("愛你", 1.5, 3)], WordTimed = true,
            Romanization = "ngo5 oi3 nei5",
        };
        Assert.Equal(["ngo5", "oi3 nei5"], _t.Texts(pair));
    }

    // Ported from RomanizationTests.swift: testSyllableSplitKoreanReadingJoinsByWord
    [Fact]
    public void SyllableSplitKoreanReadingJoinsByWord()
    {
        var words = Line(["사랑해", "같이"], spaced: true, reading: "sa rang hae ga chi");
        Assert.Equal(["saranghae", "gachi"], _t.Texts(words));
        var syllables = CharacterTimed("사랑해 같이", reading: "sa rang hae ga chi");
        Assert.Equal(["sa", "rang", "hae", "ga", "chi"], _t.Texts(syllables));
        Assert.Equal("saranghae gachi", LyricRomanizer.LineReading(_t.Readings(syllables), syllables));
        // A reading by word on syllable-timed lyrics is shared by syllable.
        Assert.Equal(["sa", "rang", "hae", "ga", "chi"], _t.Texts(CharacterTimed("사랑해 같이", reading: "saranghae gachi")));
    }

    // MARK: - Korean

    // Ported from RomanizationTests.swift: testKoreanFollowsRevisedRomanizationPronunciationRules
    [Fact]
    public void KoreanFollowsRevisedRomanizationPronunciationRules()
    {
        var cases = new Dictionary<string, string>
        {
            ["사랑해"] = "saranghae", ["같이"] = "gachi", ["좋아"] = "joa", ["없어"] = "eopseo", ["몰라"] = "molla",
            ["신라"] = "silla", ["국물"] = "gungmul", ["한국어"] = "hangugeo", ["괜찮아"] = "gwaenchana", ["밥"] = "bap",
            ["꽃잎"] = "kkonnip", ["놓다"] = "nota",
            ["있어"] = "isseo", ["있는"] = "inneun", ["합니다"] = "hamnida", ["학교"] = "hakgyo", ["좋고"] = "joko",
            ["많이"] = "mani", ["싫어"] = "sireo", ["읽어"] = "ilgeo", ["굳이"] = "guji", ["축하해"] = "chukahae",
            ["설날"] = "seollal", ["종로"] = "jongno", ["독립"] = "dongnip", ["희망"] = "huimang", ["원래"] = "wollae",
            ["빨리"] = "ppalli", ["닭"] = "dak", ["여덟"] = "yeodeol", ["밟다"] = "bapda", ["밖에"] = "bakke", ["라면"] = "ramyeon",
            // ㄷ/ㅌ palatalise before 여 and 혀 contracted from 이어 and 히어.
            ["갇혀"] = "gachyeo", ["닫혀"] = "dachyeo", ["묻혀"] = "muchyeo", ["붙여"] = "buchyeo", ["걷혀"] = "geochyeo", ["받혀"] = "bachyeo",
            ["닫히다"] = "dachida", ["밭에"] = "bate", ["끝에"] = "kkeute", ["같아"] = "gata", ["솥이"] = "sochi",
        };
        foreach (var (text, expected) in cases) Assert.True(expected == KoreanRomanizer.Romanize(text), $"{text}: {KoreanRomanizer.Romanize(text)}");
        Assert.Equal("saranghae gachi gaja!", _t.Romanizer.Romanize("사랑해 같이 가자!", RomanizationLanguage.Automatic, toneMarks: true));
        Assert.DoesNotContain("-", KoreanRomanizer.Romanize("같이 좋아 없어"));
    }

    // Ported from RomanizationTests.swift: testKoreanSyllablesKeepTheirSoundChanges
    [Fact]
    public void KoreanSyllablesKeepTheirSoundChanges()
    {
        Assert.Equal(["ga", "chi", " ", "gung", "mul"], KoreanRomanizer.Syllables("같이 국물"));
        Assert.Equal(["ppal", "li", "!"], KoreanRomanizer.Syllables("빨리!"));
    }

    // Ported from RomanizationTests.swift: testDecomposedHangulIsRomanisedAsKorean
    [Fact]
    public void DecomposedHangulIsRomanisedAsKorean()
    {
        var decomposed = "같이 좋아".Normalize(NormalizationForm.FormD);
        Assert.NotEqual(decomposed.EnumerateRunes().Count(), "같이 좋아".EnumerateRunes().Count());
        Assert.True(KoreanRomanizer.ContainsHangul(decomposed));
        Assert.Equal("gachi joa", _t.Romanizer.Romanize(decomposed, RomanizationLanguage.Automatic, toneMarks: true));
    }

    // Ported from RomanizationTests.swift: testCharacterTimedKoreanIsRomanisedByWord
    [Fact]
    public void CharacterTimedKoreanIsRomanisedByWord()
    {
        var line = CharacterTimed("사랑해 같이");
        Assert.Equal(["sa", "rang", "hae", "ga", "chi"], _t.Texts(line));
        Assert.Equal("saranghae gachi", LyricRomanizer.LineReading(_t.Readings(line), line));
        Assert.Equal(["saranghae", "gachi", "gaja"], _t.Texts(_t.LineTimed("사랑해 같이 가자")));
    }

    // MARK: - Language detection

    // Ported from RomanizationTests.swift: testCantoneseSongGetsNoMandarinPinyin (also run with ICU pinyin in the
    // Windows tests, where pinyin could otherwise be generated)
    [Fact]
    public void CantoneseSongGetsNoMandarinPinyin()
    {
        LyricLine[] song = [_t.LineTimed("佢哋係我嘅朋友"), Line(["我", "愛", "你"], start: 4, wordTimed: false), _t.LineTimed("唔好走", start: 8)];
        Assert.Equal(RomanizationLanguage.Cantonese, _t.Romanizer.SongLanguage(song));
        Assert.Empty(_t.Song(song));
        // A saved Jyutping reading is still shown, even with saved readings not preferred, because nothing else can
        // be generated.
        var withReading = song.ToArray();
        withReading[1] = withReading[1] with { Romanization = "ngo5 oi3 nei5" };
        var readings = _t.Song(withReading, Options(preferSaved: false));
        Assert.Equal([1], readings.Keys.Order());
        Assert.Equal(["ngo5", "oi3", "nei5"], readings[1].Select(word => word.Text));
    }

    // Ported from RomanizationTests.swift: testMandarinPhrasesAreNotTakenForCantonese (the scores; the song's
    // readings are checked in the Windows tests, which have pinyin)
    [Fact]
    public void MandarinPhrasesAreNotTakenForCantonese()
    {
        string[] phrases = ["给我一点解释", "在你身边度过每一天", "雨后的美好彩虹", "在海邊度假的日子", "钢琴日记", "唔 我想你", "房屋企业"];
        foreach (var phrase in phrases) Assert.True(LyricRomanizer.CantoneseScore(phrase) < 1, phrase);
        var song = new[] { "我爱你中国", "我们一起走", "天空很蓝", "你是我的光" }.Concat(phrases)
            .Select((text, index) => _t.LineTimed(text, start: index * 4)).ToList();
        Assert.Equal(RomanizationLanguage.Mandarin, _t.Romanizer.SongLanguage(song));
    }

    // Ported from RomanizationTests.swift: testCantoneseMarkersAreCountedConservatively (the scores and song
    // languages; the pinyin is checked in the Windows tests)
    [Fact]
    public void CantoneseMarkersAreCountedConservatively()
    {
        Assert.True(LyricRomanizer.CantoneseScore("佢哋係我嘅朋友") >= 2);
        // 係 also appears in Traditional Mandarin (關係).
        Assert.True(LyricRomanizer.CantoneseScore("我們的關係") < 1);
        LyricLine[] mandarin = [_t.LineTimed("我們的關係"), _t.LineTimed("你是我的光", start: 4), _t.LineTimed("天空很藍", start: 8)];
        Assert.Equal(RomanizationLanguage.Mandarin, _t.Romanizer.SongLanguage(mandarin));
        var mixed = mandarin.Append(_t.LineTimed("我唔知", start: 12)).ToList();
        Assert.Equal(RomanizationLanguage.Mandarin, _t.Romanizer.SongLanguage(mixed));
        Assert.Equal(RomanizationLanguage.Cantonese, _t.Romanizer.LanguageOf("我唔知", RomanizationLanguage.Mandarin, RomanizationLanguage.Automatic));
        // Plainly written Cantonese marks the whole song as sung in it.
        Assert.Equal(RomanizationLanguage.Cantonese, _t.Romanizer.SongLanguage(mandarin.Append(_t.LineTimed("佢唔係你", start: 12)).ToList()));
    }

    // Ported from RomanizationTests.swift: testKanaAndHangulKeepTheirLanguageWhenHanIsForced (the Hangul and
    // Cantonese parts; the kana part needs the Japanese tokenizer)
    [Fact]
    public void KanaAndHangulKeepTheirLanguageWhenHanIsForced()
    {
        var korean = _t.LineTimed("사랑해");
        Assert.Equal(["saranghae"], _t.Texts(korean, Options(language: RomanizationLanguage.Japanese)));
        Assert.Null(_t.TextsOrNull(_t.LineTimed("我愛你"), Options(language: RomanizationLanguage.Cantonese)));
        Assert.Equal(RomanizationLanguage.Japanese,
            _t.Romanizer.LanguageOf("君の名は", RomanizationLanguage.Automatic, RomanizationLanguage.Mandarin));
    }

    // Ported from RomanizationTests.swift: testStoredLanguageValuesAreUnchanged
    [Fact]
    public void StoredLanguageValuesAreUnchanged()
    {
        Assert.Equal(["automatic", "japanese", "mandarin", "cantonese", "korean", "other"],
            Enum.GetValues<RomanizationLanguage>().Select(RomanizationOptions.StoredValue));
        Assert.Equal(RomanizationLanguage.Korean, RomanizationOptions.ParseLanguage("korean"));
        Assert.Equal(["off", "below", "replace"], Enum.GetValues<RomanizationDisplay>().Select(RomanizationOptions.StoredValue));
        Assert.Null(RomanizationOptions.ParseDisplay("sideways"));
        Assert.Equal(new RomanizationOptions(RomanizationDisplay.Below, RomanizationLanguage.Automatic, true, true), RomanizationOptions.Default);
    }

    // MARK: - Line readings

    // Ported from RomanizationTests.swift: testLineReadingIsOnlyForPhrasesSpreadOverCharacters (the Jyutping part;
    // the pinyin part is in the Windows tests)
    [Fact]
    public void LineReadingIsOnlyForPhrasesSpreadOverCharacters()
    {
        var jyutping = CharacterTimed("佢哋係我嘅朋友", reading: "keoi5 dei6 hai6 ngo5 ge3 pang4 jau5");
        Assert.Equal(["keoi5", "dei6", "hai6", "ngo5", "ge3", "pang4", "jau5"], _t.Texts(jyutping));
        Assert.Null(LyricRomanizer.LineReading(_t.Readings(jyutping), jyutping));
    }
}

/// <summary>The reading parts of the Mac's ReadingAndTTMLTests that need no system services.</summary>
public class ReadingAndTTMLTests
{
    private readonly ReadingTestLines _t = new(ReadingServices.Basic);

    // Mac test: ReadingAndTTMLTests.swift testSavedJyutpingIsUsedInsteadOfGeneratingPinyin. Its lines are attributed
    // to the Verso import, so this is written from the behaviour (a saved Jyutping reading of a one-word line is shown
    // under that word), with the same input and expectation.
    [Fact]
    public void SavedJyutpingIsUsedInsteadOfGeneratingPinyin()
    {
        var line = new LyricLine
        {
            Start = 1, End = 3, Text = "你好", Words = [new LyricWord("你好", 1, 3)], WordTimed = true, Romanization = "nei5 hou2",
        };
        var result = _t.Song([line], new RomanizationOptions(RomanizationDisplay.Below, RomanizationLanguage.Automatic, true, true));
        Assert.Equal("nei5 hou2", LyricReadingPairs.Match(result[0], line.Words)[line.Words[0]]);
    }

    // Ported from ReadingAndTTMLTests.swift: testSavedReadingsReplaceWordsWithoutMovingTheirTiming
    [Fact]
    public void SavedReadingsReplaceWordsWithoutMovingTheirTiming()
    {
        LyricWord[] words = [new("你", 1.2, 1.5), new("好", 2.1, 2.9)];
        var line = new LyricLine { Start = 1, End = 3, Text = "你好", Words = words, WordTimed = true, Romanization = "nei5 hou2" };
        var result = _t.Song([line], new RomanizationOptions(RomanizationDisplay.Replace, RomanizationLanguage.Automatic, true, true))[0];
        Assert.Equal(["nei5", "hou2"], result.Select(word => word.Text));
        Assert.Equal(words.Select(word => word.Start), result.Select(word => word.Start));
        Assert.Equal(words.Select(word => word.End), result.Select(word => word.End));
    }
}

/// <summary>The Mac's RomanizationScriptCoverageTests checks that need no transliterator.</summary>
public class RomanizationScriptCoverageTests
{
    // Ported from RomanizationScriptCoverageTests.swift: testLatinTextWithAccentsIsLeftAlone
    [Fact]
    public void LatinTextWithAccentsIsLeftAlone()
    {
        foreach (var text in new[] { "Café déjà vu", "Ñandú über São Paulo", "Łódź ŵ ǆ", "Ｆｕｌｌｗｉｄｔｈ", "♪ 2024 — ok!" })
        {
            Assert.False(LyricRomanizer.ContainsNonLatin(text), text);
        }
    }

    // Ported from RomanizationScriptCoverageTests.swift: testEveryNonLatinScriptIsRomanised (the detection half; the
    // romanisation half needs ICU and is in the Windows tests)
    [Fact]
    public void EveryNonLatinScriptIsDetected()
    {
        foreach (var text in new[] { "ਸਤ ਸ੍ਰੀ ਅਕਾਲ", "নমস্কার বন্ধু", "வணக்கம் நண்பா", "నమస్కారం మిత్రమా", "નમસ્તે મિત્ર", "ನಮಸ್ಕಾರ ಗೆಳೆಯ",
                     "നമസ്കാരം സുഹൃത്തേ", "გამარჯობა მეგობარო", "Բարեւ ընկեր", "नमस्ते दुनिया", "Привет, мир" })
        {
            Assert.True(LyricRomanizer.ContainsNonLatin(text), text);
        }
    }
}
