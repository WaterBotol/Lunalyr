using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Platform.Readings;
using Lunalyr.Tests.Shared;
using static Lunalyr.Tests.Shared.ReadingTestLines;
using Lunalyr.Core.Motion;

namespace Lunalyr.Platform.Windows.Tests.Readings;

/// <summary>
/// What Windows gives for Japanese without a Japanese analyser, the fallback should the bundled dictionary ever fail
/// to open: ICU word breaks, kana read by ICU's Any-Latin, kanji unread. Pinned here on inputs from the Mac's tests,
/// each beside the Mac's own output, so a change shows (the services are built without the dictionary). Listed in
/// docs/READINGS-WINDOWS.md.
/// </summary>
public class JapaneseWithoutAnalyzerTests
{
    private static readonly ReadingServices Fallback = BuildFallback();

    private static ReadingServices BuildFallback()
    {
        var breaker = IcuWordBreaker.TryCreate()!;
        return new ReadingServices(new SimpleJapaneseTokenizer(breaker), IcuTransliterator.TryCreate(), breaker,
            ElsLanguageDetector.TryCreate()!);
    }

    private readonly ReadingTestLines _t = new(Fallback);

    private string Romaji(string text) => new JapaneseReading(Fallback).Romanize(text);

    /// <summary>Kana-only lines read without kanji or a Japanese tokenizer: (input, Mac output, Windows output).</summary>
    public static TheoryData<string, string, string> Kana => new()
    {
        { "ありがとう", "arigatou", "arigatou" },
        { "ラーメン", "raamen", "raamen" },
        { "コーヒー", "koohii", "koohii" },
        { "すごーい", "sugooi", "su goo i" },
        { "ねぇ", "nee", "nee" },
        { "ヴァイオリン", "vaiorin", "vaiorin" },
        { "はなれないで", "hanarenaide", "wa na renaide" },
        { "こんにちは", "konnichiwa", "konnichiwa" },
        { "はしれ", "hashire", "hashire" },
        { "はどこ", "wa doko", "wa doko" },
        { "あいたい", "aitai", "a itai" },
        { "まって", "matte", "ma tte" },
        { "そらを", "sora wo", "sora wo" },
        { "もしもし", "moshimoshi", "moshimoshi" },
        { "だきしめて", "dakishimete", "da ki shimete" },
        { "は？", "ha?", "ha?" },
        { "はっ", "ha", "ha" },
    };

    [Theory]
    [MemberData(nameof(Kana))]
    public void KanaIsReadByTransliteration(string text, string mac, string windows)
    {
        _ = mac;
        Assert.Equal(windows, Romaji(text));
    }

    /// <summary>Lines with kanji: the kanji get no reading, the kana around them do.</summary>
    public static TheoryData<string, string, string> WithKanji => new()
    {
        { "君の名は", "kimi no na wa", "kimi no wa" },
        { "今日も", "kyou mo", "mo" },
        { "思った", "omotta", "tta" },
        { "僕は君が好き", "boku wa kimi ga suki", "wa kimi ga ki" },
        { "２人で", "futari de", "２ de" },
        { "12月24日", "12 gatsu 24 ka", "12 gatsu 24 ka" },
    };

    [Theory]
    [MemberData(nameof(WithKanji))]
    public void KanjiGetNoReading(string text, string mac, string windows)
    {
        _ = mac;
        Assert.Equal(windows, Romaji(text));
    }

    [Fact]
    public void SavedReadingsStillLandOnTheirWords()
    {
        // Mac: ["kyou mo mata kimi wo omou"]; without a reading of 想 the saved "omo" and "u" are separate phrases.
        var line = Line(["今日", "も", "また", "君", "を", "想う"], wordTimed: false, reading: "kyou mo mata kimi wo omou");
        var glue = LyricRuns.Glue(line, line.Words);
        var pairs = LyricReadingPairs.Match(_t.Readings(line), line.Words, glue);
        Assert.Equal(["kyou mo mata kimi wo omo u"], line.Words.Where(pairs.ContainsKey).Select(word => pairs[word]));
    }

    [Fact]
    public void StageRunsStillCarryTheirOwnSavedReadings()
    {
        // Mac: sugite yuku | toki no | naka (StageReadingRevealTests).
        var stage = new LyricLine
        {
            Start = 14.88, End = 18.2, Text = "過ぎてゆく時間の中", WordTimed = true, Romanization = "sugite yukutokinonaka",
            Words = [new("過ぎてゆく", 14.88, 16.11), new("時間", 16.33, 17.23), new("の", 17.23, 17.45), new("中", 17.45, 18.2)],
        };
        var readings = _t.Readings(stage);
        var stageGlue = LyricRuns.Glue(stage, stage.Words);
        var stagePairs = LyricReadingPairs.Match(readings, stage.Words, StageRun.Glue(stage.Words, stageGlue));
        Assert.Equal(["su gite yuku", "toki no", "naka"], StageRun.Make(stage.Words, stageGlue).Select(run => StageRun.Reading(run, stagePairs)));
    }
}
