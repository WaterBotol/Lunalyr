using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Parsing;
using static Lunalyr.Core.Lyrics.Parsing.TranslationLanguages;
using Lunalyr.Core.Lyrics.Ranking;

namespace Lunalyr.Core.Tests.Lyrics.Parsing;

/// <summary>
/// Apple Music style TTML and AMLL-style inline readings, written the way the sources serialise them: one line, no
/// whitespace between the syllables of a word.
/// </summary>
public class TtmlParsingTests
{
    private const double Tol = 0.001;

    private static string Document(string body, string head = "", string language = "en") =>
        "<tt xmlns=\"http://www.w3.org/ns/ttml\" xmlns:itunes=\"http://music.apple.com/lyric-ttml-internal\" "
        + "xmlns:ttm=\"http://www.w3.org/ns/ttml#metadata\" itunes:timing=\"Word\" xml:lang=\"" + language + "\"><head><metadata>"
        + "<ttm:agent type=\"person\" xml:id=\"v1\"/><iTunesMetadata xmlns=\"http://music.apple.com/lyric-ttml-internal\" "
        + "leadingSilence=\"0.420\">" + head + "<songwriters><songwriter>Someone</songwriter></songwriters></iTunesMetadata>"
        + "</metadata></head><body dur=\"3:00.000\"><div begin=\"0.000\" end=\"3:00.000\">" + body + "</div></body></tt>";

    private static IReadOnlyList<LyricLine> Parse(string xml, double duration, params string[] languages) =>
        TtmlParser.Parse(xml, duration, languages.Length == 0 ? ["en-US"] : languages);

    // Ported from TTMLParsingTests.swift: testAppleSyllablesJoinIntoWordsWithSourceSpacing
    [Fact]
    public void AppleSyllablesJoinIntoWordsWithSourceSpacing()
    {
        var xml = Document("<p begin=\"10.000\" end=\"14.000\" itunes:key=\"L1\" ttm:agent=\"v1\"><span begin=\"10.000\" end=\"10.300\">Ev</span>"
            + "<span begin=\"10.300\" end=\"10.600\">&apos;ry</span> <span begin=\"10.600\" end=\"10.900\">beau</span>"
            + "<span begin=\"10.900\" end=\"11.100\">ti</span><span begin=\"11.100\" end=\"11.600\">ful</span> "
            + "<span begin=\"11.600\" end=\"12.400\">day</span> <span ttm:role=\"x-bg\"><span begin=\"12.500\" end=\"13.000\">(oh</span> "
            + "<span begin=\"13.000\" end=\"13.900\">yeah)</span></span></p>");
        var line = Parse(xml, 180)[0];
        Assert.Equal("Ev'ry beautiful day (oh yeah)", line.Text);
        Assert.Equal(["Ev'ry", "beautiful", "day", "(oh", "yeah)"], line.Words.Select(word => word.Text));
        Assert.Equal([10, 10.6, 11.6, 12.5, 13], line.Words.Select(word => word.Start));
        Assert.Equal([10.6, 11.6, 12.4, 13, 13.9], line.Words.Select(word => word.End));
        Assert.True(LyricTimingRules.ValidWordTiming(line));
        Assert.Equal("Word-by-word", LyricTimingRules.Precision([line]));
    }

    // Ported from TTMLParsingTests.swift: testCJKSyllablesStaySeparateTimedTokens
    [Fact]
    public void CjkSyllablesStaySeparateTimedTokens()
    {
        var xml = Document("<p begin=\"40\" end=\"44\" itunes:key=\"L1\"><span begin=\"40.0\" end=\"40.4\">君</span><span begin=\"40.4\" end=\"40.7\">の</span>"
            + "<span begin=\"40.7\" end=\"41.2\">名</span><span begin=\"41.2\" end=\"41.6\">は</span> <span begin=\"42.0\" end=\"42.5\">何</span>"
            + "<span begin=\"42.5\" end=\"43.0\">処</span></p>"
            + "<p begin=\"50\" end=\"53\" itunes:key=\"L2\"><span begin=\"50.0\" end=\"50.3\">사</span><span begin=\"50.3\" end=\"50.6\">랑</span>"
            + "<span begin=\"50.6\" end=\"51.0\">해</span> <span begin=\"51.2\" end=\"51.6\">너</span><span begin=\"51.6\" end=\"52.0\">를</span></p>"
            + "<p begin=\"60\" end=\"62\" itunes:key=\"L3\"><span begin=\"60.0\" end=\"60.6\">LOVE</span><span begin=\"60.6\" end=\"61.0\">し</span>"
            + "<span begin=\"61.0\" end=\"61.5\">て</span></p>", language: "ja");
        var lines = Parse(xml, 180);
        Assert.Equal(3, lines.Count);
        Assert.Equal("君の名は 何処", lines[0].Text);
        Assert.Equal(["君", "の", "名", "は", "何", "処"], lines[0].Words.Select(word => word.Text));
        Assert.Equal(41.2, lines[0].Words[3].Start, Tol);
        Assert.Equal("사랑해 너를", lines[1].Text);
        Assert.Equal(["사", "랑", "해", "너", "를"], lines[1].Words.Select(word => word.Text));
        Assert.Equal("LOVEして", lines[2].Text);
        Assert.Equal(["LOVE", "し", "て"], lines[2].Words.Select(word => word.Text));
        Assert.All(lines, line => Assert.True(LyricTimingRules.ValidWordTiming(line)));
    }

    // Ported from TTMLParsingTests.swift: testOverlappingBackgroundVocalsFollowTheMainWords
    [Fact]
    public void OverlappingBackgroundVocalsFollowTheMainWords()
    {
        var xml = Document("<p begin=\"20.000\" end=\"23.500\" itunes:key=\"L2\" ttm:agent=\"v1\"><span begin=\"20.000\" end=\"20.400\">I</span> "
            + "<span begin=\"20.400\" end=\"21.000\">love</span> <span begin=\"21.600\" end=\"23.000\">you</span><span ttm:role=\"x-bg\">"
            + "<span begin=\"21.200\" end=\"21.800\">(love</span> <span begin=\"21.800\" end=\"23.500\">you)</span></span></p>");
        var line = Parse(xml, 180)[0];
        Assert.Equal("I love you (love you)", line.Text);
        Assert.Equal(["I", "love", "you", "(love", "you)"], line.Words.Select(word => word.Text));
        Assert.Equal([20, 20.4, 21.6], line.Words.Take(3).Select(word => word.Start));
        Assert.Equal([20.4, 21, 23], line.Words.Take(3).Select(word => word.End));
        Assert.Equal(21.6, line.Words[3].Start, Tol);
        Assert.Equal(23.5, line.Words[4].End, Tol);
        Assert.True(line.WordTimed);
        Assert.True(LyricTimingRules.ValidWordTiming(line));
        Assert.Equal("Word-by-word", LyricTimingRules.Precision([line]));
    }

    // Ported from TTMLParsingTests.swift: testBackgroundSungFirstLeadsAndUnbracketedBackgroundIsParenthesised
    [Fact]
    public void BackgroundSungFirstLeadsAndUnbracketedBackgroundIsParenthesised()
    {
        var xml = Document("<p begin=\"30\" end=\"33\" itunes:key=\"L3\"><span ttm:role=\"x-bg\"><span begin=\"30.0\" end=\"30.5\">Hey!</span></span>"
            + "<span begin=\"30.8\" end=\"31.2\">Here</span> <span begin=\"31.2\" end=\"31.6\">we</span> <span begin=\"31.6\" end=\"32.4\">go</span></p>"
            + "<p begin=\"34\" end=\"37\" itunes:key=\"L4\"><span begin=\"34.0\" end=\"35.0\">Stay</span><span ttm:role=\"x-bg\">"
            + "<span begin=\"35.2\" end=\"35.6\">oh</span> <span begin=\"35.6\" end=\"36.4\">yeah</span></span></p>");
        var lines = Parse(xml, 180);
        Assert.Equal(["(Hey!) Here we go", "Stay (oh yeah)"], lines.Select(line => line.Text));
        Assert.Equal(["(Hey!)", "Here", "we", "go"], lines[0].Words.Select(word => word.Text));
        Assert.Equal(["Stay", "(oh", "yeah)"], lines[1].Words.Select(word => word.Text));
        Assert.All(lines, line => Assert.True(LyricTimingRules.ValidWordTiming(line)));
    }

    private const string AppleHead =
        "<translations><translation type=\"replacement\" xml:lang=\"en\"><text for=\"L1\">What is your name</text>"
        + "<text for=\"L2\">I love you (love you)</text></translation><translation type=\"replacement\" xml:lang=\"zh-Hans\">"
        + "<text for=\"L1\">你的名字是</text></translation></translations><transliterations><transliteration xml:lang=\"ja-Latn\">"
        + "<text for=\"L1\"><span begin=\"40.0\" end=\"40.4\">ki</span><span begin=\"40.0\" end=\"40.4\">mi</span> "
        + "<span begin=\"40.4\" end=\"40.7\">no</span> <span begin=\"40.7\" end=\"41.2\">na</span> <span begin=\"41.2\" end=\"41.6\">wa</span></text>"
        + "<text for=\"L2\"><span begin=\"50.0\" end=\"50.6\">a</span><span begin=\"50.6\" end=\"51.0\">i</span> "
        + "<span ttm:role=\"x-bg\"><span begin=\"51.2\" end=\"51.6\">(a</span><span begin=\"51.6\" end=\"52.0\">i)</span></span></text>"
        + "</transliteration></transliterations>";

    private const string AppleBody =
        "<p begin=\"40\" end=\"44\" itunes:key=\"L1\"><span begin=\"40.0\" end=\"40.4\">君</span><span begin=\"40.4\" end=\"40.7\">の</span>"
        + "<span begin=\"40.7\" end=\"41.2\">名</span><span begin=\"41.2\" end=\"41.6\">は</span></p>"
        + "<p begin=\"50\" end=\"53\" itunes:key=\"L2\"><span begin=\"50.0\" end=\"51.0\">愛</span><span ttm:role=\"x-bg\">"
        + "<span begin=\"51.2\" end=\"52.0\">(愛)</span></span></p>"
        + "<p begin=\"60\" end=\"62\" itunes:key=\"L3\"><span begin=\"60.0\" end=\"61.0\">Yeah</span></p>";

    // Ported from TTMLParsingTests.swift: testAppleTransliterationsAndTranslationsAttachByKey
    [Fact]
    public void AppleTransliterationsAndTranslationsAttachByKey()
    {
        var english = Parse(Document(AppleBody, AppleHead, "ja"), 180, "en-AU");
        Assert.Equal(3, english.Count);
        Assert.Equal("君の名は", english[0].Text);
        Assert.Equal("kimi no na wa", english[0].Romanization);
        Assert.Equal("What is your name", english[0].Translation);
        Assert.Equal("愛 (愛)", english[1].Text);
        Assert.Equal("ai (ai)", english[1].Romanization);
        Assert.Null(english[2].Romanization);
        Assert.Null(english[2].Translation);
        Assert.All(english, line => Assert.True(LyricTimingRules.ValidWordTiming(line)));
        Assert.Equal("你的名字是", Parse(Document(AppleBody, AppleHead, "ja"), 180, "zh-Hans-CN", "en")[0].Translation);
        Assert.Equal("What is your name", Parse(Document(AppleBody, AppleHead, "ja"), 180, "fr-FR")[0].Translation);
        // Songwriter metadata never becomes lyric text.
        Assert.DoesNotContain(english, line => line.Text.Contains("Someone", StringComparison.Ordinal));
    }

    // Ported from TTMLParsingTests.swift: testATranslationTheListenerCannotReadIsNotShown
    [Fact]
    public void ATranslationTheListenerCannotReadIsNotShown()
    {
        var xml = "<tt xmlns:ttm=\"http://www.w3.org/ns/ttml#metadata\"><body><div><p begin=\"1\" end=\"3\">"
            + "<span begin=\"1\" end=\"2\">月が</span><span begin=\"2\" end=\"3\">きれい</span>"
            + "<span ttm:role=\"x-translation\" xml:lang=\"zh-CN\">月色真美</span><span ttm:role=\"x-roman\">tsuki ga kirei</span>"
            + "</p></div></body></tt>";
        LyricLine Line(params string[] languages) => TtmlParser.Parse(xml, 10, languages)[0];
        Assert.Null(Line("en-AU").Translation);
        Assert.Equal("tsuki ga kirei", Line("en-AU").Romanization);
        Assert.Equal("月色真美", Line("en-AU", "zh-Hant-TW").Translation);
        Assert.Equal("月色真美", Line("ja-JP").Translation);
    }

    // Ported from TTMLParsingTests.swift: testTranslationScriptsFollowTheLanguagesRead
    [Fact]
    public void TranslationScriptsFollowTheLanguagesRead()
    {
        Assert.Equal(Script.Han, ScriptsOf("月色真美"));
        Assert.Equal(Script.Kana | Script.Han, ScriptsOf("こんにちは 世界"));
        Assert.Equal(Script.Latin, ScriptsOf("Café, 2024!"));
        Assert.Equal(Script.Cyrillic, ScriptsOf("Привет"));
        Assert.Equal(Script.None, ScriptsOf("♪ 123 …"));
        Assert.Equal(Script.Latin, ScriptsOfLanguage("sr-Latn-RS"));
        Assert.Equal(Script.Han, ScriptsOfLanguage("zh_Hant"));
        Assert.Equal(Script.Hangul, ScriptsOfLanguage("ko-KR"));
        Assert.True(Readable(["I love you"], ["ja-JP"]));
        Assert.False(Readable(["사랑해"], ["en-US", "ja"]));
        Assert.True(Readable(["사랑해"], ["en-US", "ko"]));
        Assert.True(Readable(["♪"], ["en"]));
        Assert.True(Readable([], ["en"]));
        Assert.False(Readable(["月色真美", "Romeo", "奔向夜晚", "…"], ["en-AU"]));
        Assert.True(Readable(["The moon is lovely", "ロミオ", "Run into the night"], ["ko-KR"]));
        Assert.False(Readable(["我爱你 Taylor"], ["en"]));
    }

    // Ported from TTMLParsingTests.swift: testWhetherATranslationCanBeReadIsDecidedForTheWholeSong
    [Fact]
    public void WhetherATranslationCanBeReadIsDecidedForTheWholeSong()
    {
        var xml = "<tt xmlns:ttm=\"http://www.w3.org/ns/ttml#metadata\"><body><div>"
            + "<p begin=\"1\" end=\"3\"><span begin=\"1\" end=\"3\">月がきれい</span><span ttm:role=\"x-translation\" xml:lang=\"zh-CN\">月色真美</span></p>"
            + "<p begin=\"4\" end=\"6\"><span begin=\"4\" end=\"6\">ロミオ</span><span ttm:role=\"x-translation\" xml:lang=\"zh-CN\">Romeo</span></p>"
            + "<p begin=\"7\" end=\"9\"><span begin=\"7\" end=\"9\">夜に駆ける</span><span ttm:role=\"x-translation\" xml:lang=\"zh-CN\">奔向夜晚</span></p>"
            + "</div></body></tt>";
        IEnumerable<string?> Translations(params string[] languages) => TtmlParser.Parse(xml, 20, languages).Select(line => line.Translation);
        Assert.Equal([null, null, null], Translations("en-AU"));
        Assert.Equal([null, null, null], Translations("fr-FR", "de"));
        Assert.Equal(["月色真美", "Romeo", "奔向夜晚"], Translations("zh-Hans-CN"));
        Assert.Equal(["月色真美", "Romeo", "奔向夜晚"], Translations("ja-JP"));
    }

    // Ported from TTMLParsingTests.swift: testAppleSuppliedTranslationsAreJudgedForTheWholeSongToo
    [Fact]
    public void AppleSuppliedTranslationsAreJudgedForTheWholeSongToo()
    {
        var head = "<translations><translation type=\"replacement\" xml:lang=\"en\"><text for=\"L1\">The moon is lovely</text>"
            + "<text for=\"L2\">ロミオ</text><text for=\"L3\">Run into the night</text></translation>"
            + "<translation type=\"replacement\" xml:lang=\"zh-Hans\"><text for=\"L1\">月色真美</text><text for=\"L2\">Romeo</text>"
            + "<text for=\"L3\">奔向夜晚</text></translation></translations>";
        var body = "<p begin=\"1\" end=\"3\" itunes:key=\"L1\"><span begin=\"1\" end=\"3\">月がきれい</span></p>"
            + "<p begin=\"4\" end=\"6\" itunes:key=\"L2\"><span begin=\"4\" end=\"6\">Romeo</span></p>"
            + "<p begin=\"7\" end=\"9\" itunes:key=\"L3\"><span begin=\"7\" end=\"9\">夜に駆ける</span></p>";
        var korean = Parse(Document(body, head, "ja"), 180, "ko-KR");
        Assert.Equal(["The moon is lovely", "ロミオ", "Run into the night"], korean.Select(line => line.Translation));
    }

    // Ported from TTMLParsingTests.swift: testInlineReadingsAttachAndStayOutOfTheLyric
    [Fact]
    public void InlineReadingsAttachAndStayOutOfTheLyric()
    {
        var xml = "<tt xmlns:ttm=\"http://www.w3.org/ns/ttml#metadata\"><body><div><p begin=\"1\" end=\"3\">"
            + "<span begin=\"1\" end=\"1.5\">你</span><span begin=\"1.5\" end=\"2\">好</span>"
            + "<span ttm:role=\"x-translation\" xml:lang=\"en\">Hello</span><span ttm:role=\"x-roman\">ni hao</span>"
            + "<span ttm:role=\"x-bg\"><span begin=\"2.1\" end=\"2.8\">(你好)</span><span ttm:role=\"x-roman\">(ni hao)</span></span></p>"
            + "<p begin=\"4\" end=\"5\"><metadata>not a lyric</metadata><span begin=\"4\" end=\"5\">Hi</span></p></div></body></tt>";
        var lines = Parse(xml, 10);
        Assert.Equal(["你好 (你好)", "Hi"], lines.Select(line => line.Text));
        Assert.Equal(["你", "好", "(你好)"], lines[0].Words.Select(word => word.Text));
        Assert.Equal("ni hao (ni hao)", lines[0].Romanization);
        Assert.Equal("Hello", lines[0].Translation);
        Assert.True(LyricTimingRules.ValidWordTiming(lines[0]));
        Assert.True(lines[1].WordTimed);
    }

    // Ported from TTMLParsingTests.swift: testInlineReadingsInSeveralLanguagesAreNotRunTogether
    [Fact]
    public void InlineReadingsInSeveralLanguagesAreNotRunTogether()
    {
        var xml = "<tt xmlns:ttm=\"http://www.w3.org/ns/ttml#metadata\"><body><div><p begin=\"1\" end=\"3\">"
            + "<span begin=\"1\" end=\"1.5\">你</span><span begin=\"1.5\" end=\"2\">好</span>"
            + "<span ttm:role=\"x-translation\" xml:lang=\"en\">Hello</span><span ttm:role=\"x-translation\" xml:lang=\"ja\">こんにちは</span>"
            + "<span ttm:role=\"x-roman\">ㄋㄧˇ ㄏㄠˇ</span><span ttm:role=\"x-roman\" xml:lang=\"zh-Latn\">ni hao</span>"
            + "<span ttm:role=\"x-bg\"><span begin=\"2.1\" end=\"2.8\">(你好)</span>"
            + "<span ttm:role=\"x-translation\" xml:lang=\"ja\">(こんにちは)</span><span ttm:role=\"x-translation\" xml:lang=\"en\">(hi)</span>"
            + "<span ttm:role=\"x-translation\" xml:lang=\"ko\"></span></span></p></div></body></tt>";
        LyricLine Line(params string[] languages) => TtmlParser.Parse(xml, 10, languages)[0];
        Assert.Equal("こんにちは (こんにちは)", Line("ja-JP").Translation);
        Assert.Equal("Hello (hi)", Line("en-GB").Translation);
        Assert.Equal("Hello (hi)", Line("fr-FR").Translation);
        Assert.Equal("Hello (hi)", Line("zh-Hans-CN").Translation);
        Assert.Equal("こんにちは (こんにちは)", Line("ko-KR", "ja").Translation);
        Assert.Equal("ni hao", Line("en").Romanization);
        Assert.Equal("你好 (你好)", Line("en").Text);
    }

    // Ported from TTMLParsingTests.swift: testCommunityFilesWithLooseBackgroundTimingKeepWordTiming
    [Fact]
    public void CommunityFilesWithLooseBackgroundTimingKeepWordTiming()
    {
        var xml = Document("<p begin=\"10.950\" end=\"14.920\" itunes:key=\"L1\" ttm:agent=\"v1\"><span begin=\"10.950\" end=\"11.570\">I</span> "
            + "<span begin=\"11.570\" end=\"11.930\">be</span><span begin=\"11.930\" end=\"12.500\">lie</span><span begin=\"12.500\" end=\"12.800\">ve you&apos;ll</span> "
            + "<span begin=\"12.800\" end=\"12.970\">be</span> <span begin=\"12.970\" end=\"13.400\">some</span><span begin=\"13.400\" end=\"13.980\">bo</span>"
            + "<span begin=\"13.980\" end=\"14.810\">dy</span><span ttm:role=\"x-translation\" xml:lang=\"zh-CN\">我相信你会是那一个</span>"
            + "<span ttm:role=\"x-bg\" begin=\"14.050\" end=\"14.920\"><span begin=\"14.050\" end=\"14.580\">(Bo</span><span begin=\"14.580\" end=\"14.920\">dy)</span>"
            + "<span ttm:role=\"x-translation\" xml:lang=\"zh-CN\">某人</span></span></p>"
            + "<p begin=\"2:33.210\" end=\"2:38.460\" itunes:key=\"L2\"><span begin=\"2:33.210\" end=\"2:33.800\">Uh</span> "
            + "<span begin=\"2:33.800\" end=\"2:34.480\">oh</span><span ttm:role=\"x-bg\" begin=\"2:33.090\" end=\"2:38.460\">"
            + "<span begin=\"2:33.090\" end=\"2:33.650\">(Uh</span> <span begin=\"2:37.520\" end=\"2:38.460\">oh)</span></span></p>"
            + "<p begin=\"4:16.528\" end=\"4:22.726\" itunes:key=\"L3\"><span begin=\"4:16.528\" end=\"4:16.762\">说</span>"
            + "<span begin=\"4:16.762\" end=\"4:22.726\">见</span><span ttm:role=\"x-bg\" begin=\"4:24.694\" end=\"4:28.069\">(Hoo)</span></p>");
        var lines = Parse(xml, 300, "zh-Hans-CN");
        Assert.Equal(["I believe you'll be somebody (Body)", "Uh oh (Uh oh)", "说见 (Hoo)"], lines.Select(line => line.Text));
        Assert.Equal(["I", "believe", "you'll", "be", "somebody", "(Body)"], lines[0].Words.Select(word => word.Text));
        Assert.Equal(12.575, lines[0].Words[1].End, Tol);
        Assert.Equal("我相信你会是那一个 (某人)", lines[0].Translation);
        Assert.Null(Parse(xml, 300, "en-AU")[0].Translation);
        Assert.Equal(153.21, lines[1].Words[0].Start, Tol);
        Assert.Equal(268.069, lines[2].End, Tol);
        Assert.All(lines, line => Assert.True(LyricTimingRules.ValidWordTiming(line)));
    }

    // Ported from TTMLParsingTests.swift: testOverlappingDuetPartsReportTheLineStillBeingSung (the parsing assertion;
    // LyricLineIndex belongs to the line-index workstream)
    [Fact]
    public void OverlappingDuetPartsParse()
    {
        var xml = Document("<p begin=\"10\" end=\"15\" itunes:key=\"L1\" ttm:agent=\"v1\"><span begin=\"10\" end=\"11\">I&apos;ll</span> "
            + "<span begin=\"11\" end=\"12\">be</span> <span begin=\"12.5\" end=\"15\">there</span></p>"
            + "<p begin=\"12\" end=\"17\" itunes:key=\"L2\" ttm:agent=\"v2\"><span begin=\"12\" end=\"13\">Al</span><span begin=\"13\" end=\"14\">ways</span> "
            + "<span begin=\"14\" end=\"17\">near</span></p>");
        var lines = Parse(xml, 60);
        Assert.Equal(["I'll be there", "Always near"], lines.Select(line => line.Text));
        Assert.Equal(15, lines[0].Words[^1].End);
    }

    // Ported from TTMLParsingTests.swift: testWhitespaceBetweenSpansStillSeparatesWords
    [Fact]
    public void WhitespaceBetweenSpansStillSeparatesWords()
    {
        var xml = """
            <tt><body><div>
              <p begin="1" end="4">
                <span begin="1" end="2">Keep</span>
                <span begin="2" end="3">on</span>
              </p>
            </div></body></tt>
            """;
        var line = Parse(xml, 10)[0];
        Assert.Equal("Keep on", line.Text);
        Assert.Equal(["Keep", "on"], line.Words.Select(word => word.Text));
        Assert.True(line.WordTimed);
    }

    // Ported from ReadingAndTTMLTests.swift: testTTMLLeafSpansKeepWordTimes
    [Fact]
    public void TtmlLeafSpansKeepWordTimes()
    {
        var xml = """
            <tt xmlns="http://www.w3.org/ns/ttml"><body><div>
            <p begin="00:01.000" end="00:03.000"><span begin="1s" end="1.4s">Hold </span><span begin="1.5s" end="3s">on</span></p>
            </div></body></tt>
            """;
        var lines = Parse(xml, 10);
        Assert.Single(lines);
        Assert.True(lines[0].WordTimed);
        Assert.Equal(["Hold", "on"], lines[0].Words.Select(word => word.Text));
        Assert.Equal(1.5, lines[0].Words[1].Start, Tol);
        Assert.Equal(3, lines[0].Words[1].End, Tol);
    }

    // Ported from ReadingAndTTMLTests.swift: testPartialTTMLWordCoverageUsesEstimatedTiming
    [Fact]
    public void PartialTtmlWordCoverageUsesEstimatedTiming()
    {
        var line = Parse("<tt><body><p begin='1s' end='4s'>Keep <span begin='2s' end='3s'>moving</span> now</p></body></tt>", 10).Single();
        Assert.False(line.WordTimed);
        Assert.Equal("Keep moving now", line.Text);
    }

    // Ported from ReadingAndTTMLTests.swift: testTTMLDoesNotRenderTranslationSpansAsLyrics
    [Fact]
    public void TtmlDoesNotRenderTranslationSpansAsLyrics()
    {
        var line = Parse("<tt xmlns:ttm=\"http://www.w3.org/ns/ttml#metadata\"><body><p begin=\"1s\" end=\"3s\"><span begin=\"1s\" end=\"3s\">Hello</span><span ttm:role=\"x-translation\">Translation</span></p></body></tt>", 10)[0];
        Assert.Equal("Hello", line.Text);
        Assert.True(line.WordTimed);
    }

    // Ported from ReadingAndTTMLTests.swift: testTTMLRejectsEntitiesAndInvalidTimes
    [Fact]
    public void TtmlRejectsEntitiesAndInvalidTimes()
    {
        Assert.Empty(Parse("<!DOCTYPE tt [<!ENTITY secret SYSTEM 'file:///etc/passwd'>]><tt><p begin='1s' end='2s'>&secret;</p></tt>", 10));
        Assert.Null(TtmlParser.Seconds("nan"));
        Assert.Null(TtmlParser.Seconds("-1s"));
        Assert.Equal(1.25, TtmlParser.Seconds("1250ms"));
        // An undeclared entity without a DOCTYPE makes the document malformed: nothing is read, not even the text around it.
        Assert.Empty(Parse("<tt><p begin='1s' end='2s'>Hold &secret; on</p></tt>", 10));
        Assert.Equal("Hold & on", Parse("<tt><p begin='1s' end='2s'>Hold &amp; on</p></tt>", 10)[0].Text);
    }

    // Ported from CreditsLayerTests.swift: testAppleTTMLCreditsAndTitleHeadingAreCredits
    [Fact]
    public void AppleTtmlCreditsAndTitleHeadingAreCredits()
    {
        var ttml = """
            <tt xmlns="http://www.w3.org/ns/ttml" xmlns:itunes="http://music.apple.com/lyric-ttml-internal"><body><div>
            <p begin="0.5" end="3">Human Nature - Yot Club</p>
            <p begin="3" end="5">Lyrics by：Ryan Kaiser/Harrison Lipton</p>
            <p begin="5" end="7">Composed by：Ryan Kaiser/Harrison Lipton</p>
            <p begin="7" end="9">Produced by：Ryan Kaiser/Harrison Lipton</p>
            <p begin="12" end="16">Hold on tight</p>
            </div></body></tt>
            """;
        var lines = Parse(ttml, 200);
        Assert.Equal(4, lines.Count(line => line.Credit == true));
        Assert.Equal(["Hold on tight"], lines.Where(line => !line.IsInstrumental).Select(line => line.Text));
    }

    // Ported from LyricGapTimingTests.swift: testSuppliedEndsAreNeverShortened (the TTML parsing assertion)
    [Fact]
    public void LineSyncedTtmlEndsAreTheSourcesOwn()
    {
        var ttml = TtmlParser.Parse("<tt><body><div><p begin=\"1\" end=\"13\">Let it go</p><p begin=\"13\" end=\"16\">Next line here</p></div></body></tt>", 20);
        Assert.Equal([null, null], ttml.Select(line => line.EndFromNextLine));
        Assert.Equal([13.0, 16.0], ttml.Select(line => line.End));
    }
}
