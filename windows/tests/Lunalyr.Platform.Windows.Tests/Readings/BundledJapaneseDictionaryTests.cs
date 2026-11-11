using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Lyrics.Readings.Japanese;
using Lunalyr.Platform.Readings;

namespace Lunalyr.Platform.Windows.Tests.Readings;

/// <summary>The bundled Japanese dictionary: expanded once into its folder and mapped from there, and what the
/// analyser and the tokenizer built on it give.</summary>
public class BundledJapaneseDictionaryTests
{
    [Fact]
    public void ItIsExpandedOnceThenMappedAndADamagedCopyIsReplaced()
    {
        using var folder = new TempFolder();
        string file;
        using (var first = BundledJapaneseDictionary.Open(folder.Path, out var source))
        {
            Assert.Equal("expanded and mapped", source);
            file = Assert.Single(Directory.GetFiles(folder.Path));
            Assert.Matches(@"ipadic-[0-9a-f]{16}\.lunadic$", file);
            Assert.True(first.EntryCount > 390_000, first.EntryCount.ToString());
        }
        using (var again = BundledJapaneseDictionary.Open(folder.Path, out var source))
        {
            Assert.Equal("mapped", source);
        }
        // A damaged or stale file under the same name is not used: it is expanded again.
        File.WriteAllBytes(file, new byte[1000]);
        File.WriteAllText(folder.File("ipadic-0000000000000000.lunadic"), "an older version");
        using (var repaired = BundledJapaneseDictionary.Open(folder.Path, out var source))
        {
            Assert.Equal("expanded and mapped", source);
            Assert.True(new FileInfo(file).Length > 10_000_000);
        }
        Assert.Equal([Path.GetFileName(file)], folder.FileNames());
    }

    [Fact]
    public void ItIsReadInMemoryWhereTheFolderCannotBeWritten()
    {
        using var folder = new TempFolder();
        // A file where the folder should be.
        var blocked = folder.File("not-a-folder");
        File.WriteAllText(blocked, "");
        using var dictionary = BundledJapaneseDictionary.Open(blocked, out var source);
        Assert.StartsWith("in memory", source, StringComparison.Ordinal);
        Assert.True(dictionary.EntryCount > 390_000);
    }

    private static DictionaryJapaneseTokenizer Tokenizer => (DictionaryJapaneseTokenizer)TestReadingServices.Windows.JapaneseTokenizer!;

    private static string Tokens(string text) =>
        string.Join("|", Tokenizer.Tokenize(text, 0, text.Length).Select(token => $"{text.Substring(token.Location, token.Length)}:{token.Latin ?? "?"}"));

    [Fact]
    public void TheAnalyserReadsWordsNotSounds()
    {
        // The reading field (キョウ), not the pronunciation (キョー); IPAdic's parts of speech.
        var analyzer = Tokenizer.Analyzer;
        var today = analyzer.Analyze("今日も");
        Assert.Equal(["キョウ", "モ"], today.Select(word => word.Reading));
        Assert.StartsWith("名詞", analyzer.Dictionary.PartOfSpeech(today[0].ContextId), StringComparison.Ordinal);
        Assert.StartsWith("助詞", analyzer.Dictionary.PartOfSpeech(today[1].ContextId), StringComparison.Ordinal);
        Assert.Equal("君:kimi|の:no|名:na|は:ha", Tokens("君の名は"));
        // っ at the end of a stem, as Apple's tokenizer gives it; a kanji the dictionary lacks has no reading.
        Assert.Equal("思っ:omo~tsu|た:ta", Tokens("思った"));
        Assert.Equal("梦:?", Tokens("梦"));
    }

    [Fact]
    public void TokensAreShapedAsTheMacsTokenizerGivesThem()
    {
        // A drawn-out vowel stays in its word; a small kana never begins one.
        Assert.Equal("すごーい:sugōi", Tokens("すごーい"));
        Assert.Equal("はくしゅ:hakushu|して:shite", Tokens("はくしゅして"));
        // The volitional stays on its verb.
        Assert.Equal("へ:he|行こう:ikou", Tokens("へ行こう"));
        Assert.Equal("でしょう:deshou", Tokens("でしょう"));
        // One or two people.
        Assert.Equal("2人:?|で:de", Tokens("2人で"));
        Assert.Equal("一人:hitori", Tokens("一人"));
        Assert.Equal("二人:futari|の:no", Tokens("二人の"));
        Assert.Equal("3:?|人:nin", Tokens("3人"));
        Assert.Equal("十:juu|二:ni|人:nin", Tokens("十二人"));
        // An interjection ending in っ takes the と after it.
        Assert.Equal("はっと:hatto|して:shite", Tokens("はっとして"));
        // A line that begins with its particle.
        Assert.Equal("は:ha|もう:mou", Tokens("はもう"));
        Assert.Equal("は:ha|から:kara", Tokens("はから"));
        Assert.Equal("は:ha|しあわせ:shiawase", Tokens("はしあわせ"));
        Assert.Equal("はしれ:hashire", Tokens("はしれ"));
        Assert.Equal("はかない:hakanai", Tokens("はかない"));
        // Nouns that begin with は or へ, and words before a one-kana particle, stay whole.
        Assert.Equal("はか:haka|の:no", Tokens("はかの"));
        Assert.Equal("へや:heya", Tokens("へや"));
        Assert.Equal("はね:hane|が:ga", Tokens("はねが"));
        Assert.Equal("はで:hade|な:na", Tokens("はでな"));
    }
}
