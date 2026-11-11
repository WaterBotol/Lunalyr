namespace Lunalyr.Core.Lyrics.Readings;

/// <summary>
/// One word of a Japanese tokenizer's segmentation: its UTF-16 range in the string given, and its reading in Latin
/// letters as the Mac's <c>kCFStringTokenizerAttributeLatinTranscription</c> gives it (ICU-style romaji: small kana
/// as <c>~ya</c>, a final っ as <c>~tsu</c>, long vowels with macrons). Null when the word has no known reading.
/// </summary>
public readonly record struct JapaneseToken(int Location, int Length, string? Latin);

/// <summary>
/// Segments Japanese text into words and reads them. The Mac uses <c>CFStringTokenizer</c> with the "ja" locale;
/// Windows uses the bundled dictionary and analyser (<see cref="Japanese.DictionaryJapaneseTokenizer"/>), and ICU word
/// breaks (no kanji readings) should it ever fail to open. Implementations need not be thread-safe for one instance;
/// <see cref="LyricRomanizer"/> keeps one per pass.
/// </summary>
public interface IJapaneseTokenizer
{
    /// <summary>The words of <c>text[start..start+length]</c>, in order, with ranges relative to the whole of
    /// <paramref name="text"/>. Text between words that no word covers may be left out.</summary>
    IReadOnlyList<JapaneseToken> Tokenize(string text, int start, int length);

    /// <summary>Whether the tokenizer reads kanji (false: kana and Latin only, kanji get no reading).</summary>
    bool ReadsKanji { get; }
}

/// <summary>Script transliteration (ICU on both platforms: Apple's <c>applyingTransform</c>, Windows' icu.dll).</summary>
public interface IScriptTransliterator
{
    /// <summary>Any script to Latin (ICU "Any-Latin"; Apple's <c>.toLatin</c>). Null when it cannot be done.</summary>
    string? ToLatin(string text);

    /// <summary>Han characters as Mandarin pinyin with tone marks (ICU "Han-Latin"; Apple's <c>.mandarinToLatin</c>).</summary>
    string? MandarinToLatin(string text);
}

/// <summary>Word boundaries (Apple's <c>enumerateSubstrings(.byWords)</c> and <c>NLTokenizer(unit: .word)</c>).</summary>
public interface IWordBreaker
{
    /// <summary>The words of <paramref name="text"/> as UTF-16 (start, length) pairs, in order. Spaces and
    /// punctuation between words are not words.</summary>
    IReadOnlyList<(int Start, int Length)> Words(string text);
}

/// <summary>Guesses the language of a text (Apple's <c>NLLanguageRecognizer</c>).</summary>
public interface ILanguageGuesser
{
    /// <summary>The most likely language as a BCP-47 tag ("ja", "zh-Hans"), or null when unsure.</summary>
    string? DominantLanguage(string text);
}

/// <summary>
/// The system services the readings use. <see cref="Basic"/> needs nothing from the platform: Korean and saved
/// readings work, kana and other scripts are left as they are, and Han is never read.
/// </summary>
public sealed record ReadingServices(
    IJapaneseTokenizer? JapaneseTokenizer,
    IScriptTransliterator? Transliterator,
    IWordBreaker WordBreaker,
    ILanguageGuesser LanguageGuesser)
{
    /// <summary>Pure .NET fallbacks only: no tokenizer, no transliteration, simple word breaks and a script-based
    /// language guess.</summary>
    public static ReadingServices Basic { get; } =
        new(null, null, SimpleWordBreaker.Instance, ScriptLanguageGuesser.Instance);

    /// <summary>The tokenizer for one pass, or one that reads nothing but kana-free tokens when there is none.</summary>
    internal IJapaneseTokenizer Tokenizer => JapaneseTokenizer ?? SimpleJapaneseTokenizer.Create(WordBreaker);

    internal string? ToLatin(string text) => Transliterator?.ToLatin(text);
    internal string? MandarinToLatin(string text) => Transliterator?.MandarinToLatin(text);
}
