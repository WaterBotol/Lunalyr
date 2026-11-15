using Lunalyr.Core.Lyrics.Readings;
using Lunalyr.Core.Lyrics.Readings.Japanese;

namespace Lunalyr.Platform.Readings;

/// <summary>
/// The reading services, offline with nothing extra installed: the bundled Japanese dictionary and analyser for
/// Japanese words and kanji readings, ICU (icu.dll) for transliteration and other word breaks, and ELS for language
/// detection. Anything missing falls back to Core's pure .NET versions.
/// </summary>
public static class WindowsReadingServices
{
    private static readonly Lazy<ReadingServices> Shared = new(() => Create(BundledJapaneseDictionary.DefaultFolder));

    /// <summary>The app's services, built once per process, with the Japanese dictionary expanded into
    /// <see cref="BundledJapaneseDictionary.DefaultFolder"/>. Tests use <see cref="Create(string)"/> with a temporary
    /// folder instead.</summary>
    public static ReadingServices Create() => Shared.Value;

    /// <summary>New services whose Japanese dictionary is expanded into (or mapped from) <paramref name="dictionaryFolder"/>.</summary>
    public static ReadingServices Create(string dictionaryFolder)
    {
        var transliterator = IcuTransliterator.TryCreate();
        IWordBreaker breaker = IcuWordBreaker.TryCreate() is { } icu ? icu : SimpleWordBreaker.Instance;
        IJapaneseTokenizer tokenizer;
        try
        {
            var japanese = new DictionaryJapaneseTokenizer(new MorphologicalAnalyzer(BundledJapaneseDictionary.Open(dictionaryFolder)), transliterator);
            tokenizer = japanese;
            breaker = new JapaneseWordBreaker(japanese, breaker);
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or IOException)
        {
            tokenizer = new SimpleJapaneseTokenizer(breaker);
        }
        ILanguageGuesser guesser = ElsLanguageDetector.TryCreate() is { } els ? els : ScriptLanguageGuesser.Instance;
        return new ReadingServices(tokenizer, transliterator, breaker, guesser);
    }

    /// <summary>What the services are made of, for the log and the docs.</summary>
    public static string Describe(ReadingServices services)
    {
        var breaker = services.WordBreaker is JapaneseWordBreaker japanese ? japanese.Other : services.WordBreaker;
        return $"Japanese: {(services.JapaneseTokenizer is DictionaryJapaneseTokenizer ? $"bundled IPAdic ({BundledJapaneseDictionary.LastSource})" : "ICU word breaks (no kanji readings)")}; "
            + $"transliteration: {(services.Transliterator is null ? "none" : "ICU")}; "
            + $"word breaks: {(breaker is IcuWordBreaker ? "ICU" : "simple")}{(services.WordBreaker is JapaneseWordBreaker ? " (Japanese: bundled IPAdic)" : "")}; "
            + $"language detection: {(services.LanguageGuesser is ElsLanguageDetector ? "ELS" : "script")}";
    }
}
