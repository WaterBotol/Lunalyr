using System.Collections.Concurrent;
using Lunalyr.Core.Lyrics.Readings;
using Windows.Globalization;

namespace Lunalyr.Platform.Readings;

/// <summary>
/// <see cref="IJapaneseTokenizer"/> on <c>Windows.Globalization.JapanesePhoneticAnalyzer</c>, the Japanese IME's
/// reading engine: words with their readings in kana, which ICU's Any-Latin turns into the same style of romaji as
/// Apple's tokenizer gives (<c>~tsu</c>, <c>~ya</c>, macrons). The class is registered single-threaded (STA): from a
/// multi-threaded thread its activation fails with CLASS_E_CLASSNOTAVAILABLE, so every call runs on one dedicated STA
/// thread. It only reads anything where the Japanese language's IME dictionary is installed (Settings → Time &amp;
/// language → Language → add Japanese); elsewhere it returns no words at all, and <see cref="TryCreate"/> gives null
/// so the caller falls back to ICU word breaks, which read kana but no kanji. Not verified on a PC with Japanese
/// installed: see docs/READINGS-WINDOWS.md.
/// </summary>
public sealed class WindowsJapaneseTokenizer : IJapaneseTokenizer
{
    /// <summary>The analyzer takes at most 100 characters at a time.</summary>
    private const int MaximumInput = 100;

    private readonly IScriptTransliterator _transliterator;

    private WindowsJapaneseTokenizer(IScriptTransliterator transliterator) => _transliterator = transliterator;

    public bool ReadsKanji => true;

    private static readonly Lazy<bool> Available = new(() =>
    {
        try
        {
            var words = StaThread.Run(() => JapanesePhoneticAnalyzer.GetWords("今日", false));
            return words.Any(word => !string.IsNullOrEmpty(word.YomiText));
        }
        catch (Exception)
        {
            return false;
        }
    });

    /// <summary>Whether the analyzer reads Japanese on this PC (its dictionary is installed).</summary>
    public static bool IsAvailable => Available.Value;

    /// <summary>The tokenizer, or null when the analyzer reads nothing here.</summary>
    public static WindowsJapaneseTokenizer? TryCreate(IScriptTransliterator? transliterator) =>
        transliterator is not null && IsAvailable ? new WindowsJapaneseTokenizer(transliterator) : null;

    public IReadOnlyList<JapaneseToken> Tokenize(string text, int start, int length)
    {
        var tokens = new List<JapaneseToken>();
        var end = start + length;
        for (var chunk = start; chunk < end;)
        {
            var size = Math.Min(MaximumInput, end - chunk);
            if (chunk + size < end && char.IsHighSurrogate(text[chunk + size - 1])) size--;
            var input = text.Substring(chunk, size);
            var words = StaThread.Run(() => JapanesePhoneticAnalyzer.GetWords(input, false)
                .Select(word => (word.DisplayText, word.YomiText)).ToList());
            var cursor = 0;
            foreach (var (display, yomi) in words)
            {
                if (string.IsNullOrEmpty(display)) continue;
                var found = input.IndexOf(display, cursor, StringComparison.Ordinal);
                if (found < 0) continue;
                tokens.Add(new JapaneseToken(chunk + found, display.Length, Latin(yomi)));
                cursor = found + display.Length;
            }
            chunk += size;
        }
        return tokens;
    }

    /// <summary>The reading in Latin letters, or null when the analyzer gave none in kana (Latin words, digits,
    /// punctuation, or a character it could not read and echoed back).</summary>
    private string? Latin(string? yomi)
    {
        if (string.IsNullOrEmpty(yomi) || !ReadingText.HasKana(yomi)) return null;
        if (yomi.EnumerateRunes().Any(ReadingText.IsIdeographic)) return null;
        return _transliterator.ToLatin(yomi);
    }

    /// <summary>One background STA thread that runs the analyzer's calls in order.</summary>
    private static class StaThread
    {
        private static readonly BlockingCollection<Action> Work = [];
        private static readonly Lazy<Thread> Thread = new(() =>
        {
            var thread = new Thread(() =>
            {
                foreach (var action in Work.GetConsumingEnumerable()) action();
            })
            {
                IsBackground = true,
                Name = "Lunalyr Japanese readings",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return thread;
        });

        public static T Run<T>(Func<T> function)
        {
            if (System.Threading.Thread.CurrentThread == Thread.Value) return function();
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Work.Add(() =>
            {
                try { completion.SetResult(function()); }
                catch (Exception exception) { completion.SetException(exception); }
            });
            return completion.Task.GetAwaiter().GetResult();
        }
    }
}
