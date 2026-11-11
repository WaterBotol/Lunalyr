using Lunalyr.Core.Lyrics.Readings;

namespace Lunalyr.Platform.Readings;

/// <summary>
/// The reading services Windows offers offline with nothing extra installed: ICU (icu.dll) for transliteration and
/// word breaks, ELS for language detection, and the Japanese phonetic analyzer where the Japanese language is
/// installed. Anything missing falls back to Core's pure .NET versions.
/// </summary>
public static class WindowsReadingServices
{
    private static readonly Lazy<ReadingServices> Shared = new(Build);

    /// <summary>The services, built once per process.</summary>
    public static ReadingServices Create() => Shared.Value;

    /// <summary>What was found on this PC, for the log and the docs.</summary>
    public static string Describe()
    {
        var services = Create();
        return $"Japanese tokenizer: {(services.JapaneseTokenizer is WindowsJapaneseTokenizer ? "JapanesePhoneticAnalyzer" : "ICU word breaks (no kanji readings)")}; "
            + $"transliteration: {(services.Transliterator is null ? "none" : "ICU")}; "
            + $"word breaks: {(services.WordBreaker is IcuWordBreaker ? "ICU" : "simple")}; "
            + $"language detection: {(services.LanguageGuesser is ElsLanguageDetector ? "ELS" : "script")}";
    }

    private static ReadingServices Build()
    {
        var transliterator = IcuTransliterator.TryCreate();
        IWordBreaker breaker = IcuWordBreaker.TryCreate() is { } icu ? icu : SimpleWordBreaker.Instance;
        IJapaneseTokenizer tokenizer = WindowsJapaneseTokenizer.TryCreate(transliterator) is { } analyzer
            ? analyzer
            : new SimpleJapaneseTokenizer(breaker);
        ILanguageGuesser guesser = ElsLanguageDetector.TryCreate() is { } els ? els : ScriptLanguageGuesser.Instance;
        return new ReadingServices(tokenizer, transliterator, breaker, guesser);
    }
}
