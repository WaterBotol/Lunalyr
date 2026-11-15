using Lunalyr.Core.Lyrics.Readings.Japanese;

namespace Lunalyr.Core.Tests.Lyrics.Readings;

/// <summary>The Japanese analyser and its dictionary format on a small dictionary made here. The bundled IPAdic and
/// the readings it gives are tested in the Windows tests.</summary>
public class MorphologicalAnalyzerTests
{
    private const short Noun = 1, Particle = 2, Verb = 3;

    private static JapaneseDictionary Dictionary(Action<short[]>? connections = null,
        params (string Surface, short Id, short Cost, string? Reading)[] extra)
    {
        (string, short, short, short, string?)[] words =
        [
            ("東", Noun, Noun, 500, "トウ"), ("京", Noun, Noun, 500, "キョウ"), ("東京", Noun, Noun, 300, "トウキョウ"),
            ("京都", Noun, Noun, 300, "キョウト"), ("都", Noun, Noun, 400, "ト"), ("に", Particle, Particle, 100, "ニ"),
            ("行く", Verb, Verb, 200, "イク"),
            .. extra.Select(word => (word.Surface, word.Id, word.Id, word.Cost, word.Reading)),
        ];
        var matrix = new short[4 * 4];
        connections?.Invoke(matrix);
        // DEFAULT, SPACE, KANJI, HIRAGANA, KATAKANA, as in MeCab's char.def.
        JapaneseDictionaryFormat.CategorySource Category(string name, bool invoke, bool group, int length, short cost) =>
            new(name, invoke, group, length, [(Noun, Noun, cost)]);
        var categories = new[]
        {
            Category("DEFAULT", false, true, 0, 9000), Category("SPACE", false, true, 0, 9000), Category("KANJI", false, false, 2, 9000),
            Category("HIRAGANA", false, true, 2, 9000), Category("KATAKANA", true, true, 2, 5000),
        };
        var table = new uint[0x10000];
        Array.Fill(table, JapaneseDictionaryFormat.CharInfo(0, 1));
        table[' '] = JapaneseDictionaryFormat.CharInfo(1, 1 << 1);
        for (var code = 0x3041; code <= 0x309F; code++) table[code] = JapaneseDictionaryFormat.CharInfo(3, 1 << 3);
        for (var code = 0x30A1; code <= 0x30FF; code++) table[code] = JapaneseDictionaryFormat.CharInfo(4, 1 << 4);
        for (var code = 0x4E00; code <= 0x9FFF; code++) table[code] = JapaneseDictionaryFormat.CharInfo(2, 1 << 2);
        var bytes = JapaneseDictionaryFormat.Write(new JapaneseDictionaryFormat.Source(words, 4, 4, matrix, categories, table, 1,
            ["BOS/EOS,*", "名詞,一般", "助詞,格助詞", "動詞,自立"]));
        return JapaneseDictionary.Open(bytes);
    }

    private static string Parts(MorphologicalAnalyzer analyzer, string text) =>
        string.Join("|", analyzer.Analyze(text).Select(m => $"{text.Substring(m.Start, m.Length)}/{m.Reading ?? "?"}"));

    [Fact]
    public void TheCheapestPathOfWordsAndConnectionsWins()
    {
        var analyzer = new MorphologicalAnalyzer(Dictionary());
        // 東京|都 costs 700, 東|京都 800, 東|京|都 1400.
        Assert.Equal("東京/トウキョウ|都/ト|に/ニ|行く/イク", Parts(analyzer, "東京都に行く"));
        var words = analyzer.Analyze("東京都");
        Assert.Equal("名詞,一般", analyzer.Dictionary.PartOfSpeech(words[0].ContextId));
        Assert.True(words.All(word => word.IsKnown));
    }

    [Fact]
    public void ConnectionCostsGoFromTheWordBeforeToTheWordAfter()
    {
        // 春夏 alone costs 1000; 春 (a noun) then 夏 (a verb here) cost 600 plus their connection. The matrix holds
        // MeCab's matrix.def order: the right id of the word before, then the left id of the word after.
        (string, short, short, string?)[] words = [("春夏", Noun, 1000, "シュンカ"), ("春", Noun, 300, "ハル"), ("夏", Verb, 300, "ナツ")];
        static int Index(int before, int after) => before + 4 * after;
        var nounThenVerb = new MorphologicalAnalyzer(Dictionary(matrix => matrix[Index(Noun, Verb)] = 2000, words));
        var verbThenNoun = new MorphologicalAnalyzer(Dictionary(matrix => matrix[Index(Verb, Noun)] = 2000, words));
        Assert.Equal(2000, nounThenVerb.Dictionary.Connection(Noun, Verb));
        Assert.Equal("春夏/シュンカ", Parts(nounThenVerb, "春夏"));
        Assert.Equal("春/ハル|夏/ナツ", Parts(verbThenNoun, "春夏"));
    }

    [Fact]
    public void UnknownWordsComeFromCharacterCategories()
    {
        var analyzer = new MorphologicalAnalyzer(Dictionary());
        // Katakana always invokes unknown words and groups a run; a lone unknown kanji is one or two characters.
        Assert.Equal("ラーメン/?|に/ニ|行く/イク", Parts(analyzer, "ラーメンに行く"));
        Assert.False(analyzer.Analyze("ラーメン")[0].IsKnown);
        Assert.Equal("梦/?", Parts(analyzer, "梦"));
        // Characters beyond the Basic Multilingual Plane are one character of two UTF-16 units.
        var rare = analyzer.Analyze("𠮷");
        Assert.Equal((0, 2), (rare.Single().Start, rare.Single().Length));
        Assert.Empty(analyzer.Analyze(""));
    }

    [Fact]
    public void SpacesBelongToNoWord()
    {
        var analyzer = new MorphologicalAnalyzer(Dictionary());
        var words = analyzer.Analyze(" 東京  に ");
        Assert.Equal([(1, 2), (5, 1)], words.Select(word => (word.Start, word.Length)));
    }

    [Fact]
    public void TheFirstWordCanBeRequired()
    {
        var analyzer = new MorphologicalAnalyzer(Dictionary());
        var free = analyzer.Analyze("東京都", out var freeCost);
        var forced = analyzer.Analyze("東京都", out var forcedCost, 1, "名詞");
        Assert.Equal(2, free.Count);
        Assert.Equal([(0, 1), (1, 2)], forced.Select(word => (word.Start, word.Length)));
        Assert.True(forcedCost > freeCost);
        Assert.Empty(analyzer.Analyze("東京都", out var impossible, 1, "助詞"));
        Assert.Equal(int.MaxValue, impossible);
    }

    [Fact]
    public void ADamagedFileIsRefused()
    {
        var bytes = JapaneseDictionaryFormat.Write(new JapaneseDictionaryFormat.Source([("に", Particle, Particle, 1, "ニ")], 4, 4,
            new short[16], [new("DEFAULT", false, true, 0, [])], new uint[0x10000], 0, ["BOS/EOS"]));
        Assert.Equal(1, JapaneseDictionary.Open(bytes).EntryCount);
        Assert.Throws<InvalidDataException>(() => JapaneseDictionary.Open(bytes.AsMemory(0, bytes.Length - 100)));
        Assert.Throws<InvalidDataException>(() => JapaneseDictionary.Open(new byte[4096]));
        Assert.Throws<InvalidDataException>(() => JapaneseDictionary.Open(Array.Empty<byte>()));
    }
}
