using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lunalyr.Core.Lyrics.Readings;

// On-device lyric translation. When a song's lyrics carry no translation of their own and are written in a language
// the listener doesn't read, the lines would be translated on the device and shown in the same band as a lyric
// source's translations. On the Mac that is Apple's Translation framework (macOS 15+). Windows has no on-device
// translation API a desktop app can use (docs/READINGS-WINDOWS.md), so the Windows app has no ILyricTranslator and
// every song is skipped as Unavailable; everything else here is plain C# behind small interfaces, kept so the
// behaviour matches the Mac's the day a translator exists. Ported from the Mac's LyricMachineTranslation.swift.

/// <summary>The "translate lyrics on this device" setting and its target language. Off by default.</summary>
public sealed record MachineTranslationOptions(bool Enabled = false, string Target = "")
{
    /// <summary>The language lines are translated into: the chosen one, else the listener's own.</summary>
    public string ResolvedTarget(IReadOnlyList<string> preferredLanguages) =>
        Target.Length == 0 ? MachineTranslationPolicy.SystemLanguage(preferredLanguages) : Target;
}

/// <summary>Finds the language lyrics are written in.</summary>
public interface ILyricLanguageDetector
{
    /// <summary>A language identifier ("ja", "zh-Hans"), or null when unsure.</summary>
    string? Language(IReadOnlyList<string> texts);
}

/// <summary>Translates lines. One call per song: the lines go in one batch.</summary>
public interface ILyricTranslator
{
    /// <summary><paramref name="texts"/> translated, in the same order; an empty string where a line came back with
    /// nothing. Throws <see cref="LyricTranslationException"/> for a missing or unsupported language pair.</summary>
    Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string source, string target, CancellationToken cancellation);
}

public enum LyricTranslationError
{
    /// <summary>The language packs for this pair aren't on this device yet.</summary>
    NeedsDownload,
    /// <summary>The translator can't translate between these languages.</summary>
    Unsupported,
}

public sealed class LyricTranslationException(LyricTranslationError error)
    : Exception($"Lyric translation failed: {error}")
{
    public LyricTranslationError Error { get; } = error;
}

/// <summary>Translations already made, kept per song.</summary>
public interface ILyricTranslationCache
{
    IReadOnlyDictionary<string, string>? Translations(string key);
    void Store(IReadOnlyDictionary<string, string> translations, string key);
}

/// <summary>A detector from <see cref="ILanguageGuesser"/> over a sample of the song's lines.</summary>
public sealed class GuessingLyricLanguageDetector(ILanguageGuesser guesser) : ILyricLanguageDetector
{
    public string? Language(IReadOnlyList<string> texts)
    {
        var sample = new StringBuilder();
        foreach (var text in texts)
        {
            if (sample.Length >= 4000) break;
            sample.Append(text).Append('\n');
        }
        return sample.Length == 0 ? null : guesser.DominantLanguage(sample.ToString());
    }
}

/// <summary>Whether a song's lyrics should be translated, and from what.</summary>
public static class MachineTranslationPolicy
{
    public enum Skip { Off, Unavailable, NoLines, SourceHasTranslations, UnknownLanguage, ReadsLanguage }

    /// <summary>Translate from <c>Source</c> to <c>Target</c>, or skip for <c>Reason</c>.</summary>
    public readonly record struct Decision(string? Source, string? Target, Skip? Reason)
    {
        public static Decision Translate(string source, string target) => new(source, target, null);
        public static Decision Skipped(Skip reason) => new(null, null, reason);
        public bool IsTranslate => Reason is null;
    }

    /// <summary>The listener's own language, from their first preferred language.</summary>
    public static string SystemLanguage(IReadOnlyList<string> preferred) => preferred.Count == 0 ? "en" : Key(preferred[0]);

    /// <summary>A language identifier reduced to what decides readability: the language, with the script kept only
    /// for Chinese (Simplified and Traditional are separate targets; a Chinese tag with no script takes the one its
    /// region writes, Simplified when there is none).</summary>
    public static string Key(string identifier)
    {
        var parts = identifier.Replace('_', '-').Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0].Length is < 2 or > 3 || !parts[0].All(char.IsAsciiLetter)) return identifier.ToLowerInvariant();
        var code = parts[0].ToLowerInvariant();
        if (code != "zh") return code;
        string? script = null, region = null;
        foreach (var part in parts.Skip(1))
        {
            if (part.Length == 4 && part.All(char.IsAsciiLetter)) script ??= char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant();
            else if (part.Length == 2 || (part.Length == 3 && part.All(char.IsAsciiDigit))) region ??= part.ToUpperInvariant();
        }
        script ??= region is "TW" or "HK" or "MO" ? "Hant" : "Hans";
        return script is "Hans" or "Hant" ? $"zh-{script}" : "zh";
    }

    /// <summary>Whether <paramref name="a"/> and <paramref name="b"/> are the same language for a reader.</summary>
    public static bool SameLanguage(string a, string b)
    {
        var left = Key(a);
        var right = Key(b);
        if (left == right) return true;
        // A Chinese reader with no script given reads either.
        return left.StartsWith("zh", StringComparison.Ordinal) && right.StartsWith("zh", StringComparison.Ordinal) && (left == "zh" || right == "zh");
    }

    /// <summary>Whether any line carries a translation from the lyric source, which always wins over a machine
    /// translation.</summary>
    public static bool HasSourceTranslations(IReadOnlyList<LyricLine> lines) =>
        lines.Any(line => !string.IsNullOrWhiteSpace(line.Translation));

    /// <summary>What rules translation out before the language is known.</summary>
    public static Skip? Precheck(IReadOnlyList<LyricLine> lines, bool enabled, bool available)
    {
        if (!enabled) return Skip.Off;
        if (!available) return Skip.Unavailable;
        if (HasSourceTranslations(lines)) return Skip.SourceHasTranslations;
        if (TranslationLineMap.Texts(lines).Count == 0) return Skip.NoLines;
        return null;
    }

    public static Decision Decide(IReadOnlyList<LyricLine> lines, bool enabled, bool available, string? detected, string target,
        IReadOnlyList<string> preferred)
    {
        if (Precheck(lines, enabled, available) is { } skip) return Decision.Skipped(skip);
        if (detected is null) return Decision.Skipped(Skip.UnknownLanguage);
        if (SameLanguage(detected, target) || preferred.Any(language => SameLanguage(detected, language)))
        {
            return Decision.Skipped(Skip.ReadsLanguage);
        }
        return Decision.Translate(Key(detected), Key(target));
    }
}

/// <summary>Which texts are sent, and which line each answer belongs to.</summary>
public static class TranslationLineMap
{
    /// <summary>The distinct sung lines to translate, in order of first appearance. Instrumental breaks, credits and
    /// lines without letters are left out.</summary>
    public static List<string> Texts(IReadOnlyList<LyricLine> lines)
    {
        var seen = new HashSet<string>();
        var result = new List<string>();
        foreach (var line in lines)
        {
            if (Translatable(line) is { } text && seen.Add(text)) result.Add(text);
        }
        return result;
    }

    /// <summary>Each line's machine translation by index, from <paramref name="translations"/> keyed by source text. A
    /// line that already has a translation, or whose answer is blank or just repeats it, gets none.</summary>
    public static Dictionary<int, string> LineTranslations(IReadOnlyList<LyricLine> lines, IReadOnlyDictionary<string, string> translations)
    {
        var result = new Dictionary<int, string>();
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (line.Translation is not null || Translatable(line) is not { } text) continue;
            if (!translations.TryGetValue(text, out var raw)) continue;
            var answer = raw.Trim();
            if (answer.Length == 0 || ReadingText.FoldCaseAndDiacritics(answer) == ReadingText.FoldCaseAndDiacritics(text)) continue;
            result[index] = answer;
        }
        return result;
    }

    /// <summary><paramref name="texts"/> paired with the answers that came back in the same order.</summary>
    public static Dictionary<string, string> Pairs(IReadOnlyList<string> texts, IReadOnlyList<string> answers)
    {
        var result = new Dictionary<string, string>();
        foreach (var (text, answer) in texts.Zip(answers)) result[text] = answer;
        return result;
    }

    private static string? Translatable(LyricLine line)
    {
        if (line.IsInstrumental || line.Credit == true) return null;
        var text = line.Text.Trim();
        return text.EnumerateRunes().Any(IsLetterOrMark) ? text : null;
    }

    /// <summary>Foundation's <c>CharacterSet.letters</c>: the letter and mark categories.</summary>
    private static bool IsLetterOrMark(Rune rune) => Rune.GetUnicodeCategory(rune) is
        UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
        or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.NonSpacingMark
        or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    /// <summary>The cache key for one song's lines in one language pair: SHA-256 of "v1", the pair and the texts
    /// joined by U+001F, in lower-case hex (the same bytes as the Mac's).</summary>
    public static string CacheKey(IReadOnlyList<string> texts, string source, string target)
    {
        var material = string.Join("\u001F", new[] { "v1", source, target }.Concat(texts));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
}

/// <summary>One JSON file per song in a caches folder, the least recently used dropped past the limit.</summary>
public sealed class DiskLyricTranslationCache : ILyricTranslationCache
{
    private readonly Lock _lock = new();

    public DiskLyricTranslationCache(string directory, int limit = 400)
    {
        Directory = directory;
        Limit = Math.Max(1, limit);
    }

    public string Directory { get; }
    public int Limit { get; }

    private string FileFor(string key)
    {
        var safe = new string(key.Where(char.IsLetterOrDigit).Take(80).ToArray());
        return Path.Combine(Directory, safe + ".json");
    }

    public IReadOnlyDictionary<string, string>? Translations(string key)
    {
        lock (_lock)
        {
            var path = FileFor(key);
            try
            {
                if (!File.Exists(path)) return null;
                var value = JsonSerializer.Deserialize(File.ReadAllBytes(path), TranslationCacheJson.Default.DictionaryStringString);
                // Touched, so a song played again is kept longest.
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                return value;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }
    }

    public void Store(IReadOnlyDictionary<string, string> translations, string key)
    {
        lock (_lock)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                var path = FileFor(key);
                var temporary = path + ".tmp";
                File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(translations.ToDictionary(), TranslationCacheJson.Default.DictionaryStringString));
                File.Move(temporary, path, overwrite: true);
                Prune();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A cache that cannot be written only costs a translation later.
            }
        }
    }

    private void Prune()
    {
        var files = new DirectoryInfo(Directory).GetFiles("*.json");
        if (files.Length <= Limit) return;
        foreach (var file in files.OrderBy(file => file.LastWriteTimeUtc).Take(files.Length - Limit))
        {
            try { file.Delete(); } catch (IOException) { }
        }
    }
}

[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class TranslationCacheJson : JsonSerializerContext;

/// <summary>Where a song's machine translation stands.</summary>
public abstract record MachineTranslationState
{
    public sealed record Idle : MachineTranslationState;
    public sealed record Skipped(MachineTranslationPolicy.Skip Reason) : MachineTranslationState;
    public sealed record Translating(string Source, string Target) : MachineTranslationState;
    public sealed record Translated(string Source, string Target, int Lines) : MachineTranslationState;
    public sealed record NeedsDownload(string Source, string Target) : MachineTranslationState;
    public sealed record Unsupported(string Source, string Target) : MachineTranslationState;
    public sealed record Failed : MachineTranslationState;
}

/// <summary>
/// Translates the song on screen, once per song, and publishes each line's machine translation. A new song (or
/// setting) cancels work in flight. Call <see cref="Update"/> from one thread; <see cref="Changed"/> may be raised
/// on a worker thread.
/// </summary>
public sealed class LyricMachineTranslator(
    ILyricLanguageDetector detector,
    ILyricTranslator? translator,
    ILyricTranslationCache cache,
    Func<IReadOnlyList<string>> preferredLanguages)
{
    private readonly Lock _lock = new();
    private CancellationTokenSource? _cancellation;
    private int _generation;
    private int? _lastRequest;
    private IReadOnlyDictionary<int, string> _translations = new Dictionary<int, string>();
    private MachineTranslationState _state = new MachineTranslationState.Idle();

    /// <summary>The system translator: none on Windows, which has no on-device translation API.</summary>
    public static ILyricTranslator? SystemTranslator() => null;

    /// <summary>Machine translations by line index, for the lines last given.</summary>
    public IReadOnlyDictionary<int, string> Translations { get { lock (_lock) return _translations; } }

    public MachineTranslationState State { get { lock (_lock) return _state; } }

    /// <summary>The work in flight, for tests.</summary>
    public Task? Pending { get; private set; }

    public event EventHandler? Changed;

    /// <summary>Starts over for <paramref name="lines"/>: cached translations come back at once, others after one
    /// batch request. Nothing is detected or translated while the setting is off.</summary>
    public void Update(IReadOnlyList<LyricLine> lines, bool enabled, string target, bool force = false)
    {
        // The same lines shown again (a repeat lookup, an upgrade between lines with the same words) keep their
        // translations without a flicker.
        var hash = new HashCode();
        hash.Add(enabled); hash.Add(target); hash.Add(lines.Count);
        foreach (var line in lines) { hash.Add(line.Text); hash.Add(line.Translation); hash.Add(line.IsInstrumental); }
        var request = hash.ToHashCode();
        int ticket;
        CancellationToken token;
        bool skipped;
        lock (_lock)
        {
            if (!force && request == _lastRequest) return;
            _lastRequest = request;
            _cancellation?.Cancel();
            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            token = _cancellation.Token;
            ticket = ++_generation;
            _translations = new Dictionary<int, string>();
            // Checked before anything runs, so an off setting costs nothing.
            var reason = MachineTranslationPolicy.Precheck(lines, enabled, translator is not null);
            skipped = reason is not null;
            _state = reason is { } skip && lines.Count > 0
                ? new MachineTranslationState.Skipped(skip)
                : new MachineTranslationState.Idle();
        }
        Raise();
        if (skipped)
        {
            Pending = null;
            return;
        }
        var preferred = preferredLanguages();
        Pending = Task.Run(() => RunAsync(lines, target, preferred, ticket, token), CancellationToken.None);
    }

    private async Task RunAsync(IReadOnlyList<LyricLine> lines, string target, IReadOnlyList<string> preferred, int ticket,
        CancellationToken token)
    {
        var texts = TranslationLineMap.Texts(lines);
        var decision = MachineTranslationPolicy.Decide(lines, true, true, detector.Language(texts), target, preferred);
        if (!decision.IsTranslate)
        {
            Publish(ticket, token, decision.Reason is { } reason ? new MachineTranslationState.Skipped(reason) : null, null);
            return;
        }
        var (source, to) = (decision.Source!, decision.Target!);
        var key = TranslationLineMap.CacheKey(texts, source, to);
        if (cache.Translations(key) is { } cached)
        {
            var shown = TranslationLineMap.LineTranslations(lines, cached);
            Publish(ticket, token, new MachineTranslationState.Translated(source, to, shown.Count), shown);
            return;
        }
        if (translator is null) return;
        if (!Publish(ticket, token, new MachineTranslationState.Translating(source, to), null)) return;
        try
        {
            var answers = await translator.TranslateAsync(texts, source, to, token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return;
            var pairs = TranslationLineMap.Pairs(texts, answers);
            _ = Task.Run(() => cache.Store(pairs, key), CancellationToken.None);
            var shown = TranslationLineMap.LineTranslations(lines, pairs);
            Publish(ticket, token, new MachineTranslationState.Translated(source, to, shown.Count), shown);
        }
        catch (LyricTranslationException exception)
        {
            Publish(ticket, CancellationToken.None, exception.Error == LyricTranslationError.NeedsDownload
                ? new MachineTranslationState.NeedsDownload(source, to)
                : new MachineTranslationState.Unsupported(source, to), null);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            Publish(ticket, token, new MachineTranslationState.Failed(), null);
        }
    }

    /// <summary>Shows a result if it is still for the current song; false when it is not.</summary>
    private bool Publish(int ticket, CancellationToken token, MachineTranslationState? state, IReadOnlyDictionary<int, string>? translations)
    {
        lock (_lock)
        {
            if (token.IsCancellationRequested || ticket != _generation) return false;
            if (state is not null) _state = state;
            if (translations is not null) _translations = translations;
        }
        Raise();
        return true;
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}
