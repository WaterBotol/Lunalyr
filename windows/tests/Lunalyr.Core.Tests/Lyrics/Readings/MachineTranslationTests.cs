using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Readings;
using Skip = Lunalyr.Core.Lyrics.Readings.MachineTranslationPolicy.Skip;

namespace Lunalyr.Core.Tests.Lyrics.Readings;

/// <summary>On-device lyric translation, with every piece that would reach a translator replaced by a fake. Ported
/// from the Mac's MachineTranslationTests.swift.</summary>
public class MachineTranslationTests
{
    private sealed class FixedDetector(string? language) : ILyricLanguageDetector
    {
        public string? Language(IReadOnlyList<string> texts) => language;
    }

    private sealed class FakeTranslator : ILyricTranslator
    {
        private readonly Lock _lock = new();
        private readonly List<(IReadOnlyList<string> Texts, string Source, string Target)> _calls = [];
        public List<(IReadOnlyList<string> Texts, string Source, string Target)> Calls { get { lock (_lock) return [.. _calls]; } }
        public Exception? Error { get; set; }
        public TimeSpan Delay { get; set; }

        public async Task<IReadOnlyList<string>> TranslateAsync(IReadOnlyList<string> texts, string source, string target,
            CancellationToken cancellation)
        {
            lock (_lock) _calls.Add((texts, source, target));
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellation);
            if (Error is { } error) throw error;
            return texts.Select(text => $"[{target}] {text}").ToList();
        }
    }

    private sealed class MemoryCache : ILyricTranslationCache
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _store = [];
        public int Count { get { lock (_lock) return _store.Count; } }
        public IReadOnlyDictionary<string, string>? Translations(string key) { lock (_lock) return _store.GetValueOrDefault(key); }
        public void Store(IReadOnlyDictionary<string, string> translations, string key) { lock (_lock) _store[key] = translations; }
    }

    private static LyricLine Line(string text, double start, string? translation = null, bool instrumental = false) =>
        new() { Start = start, End = start + 2, Text = text, IsInstrumental = instrumental, Translation = translation };

    private static List<LyricLine> Japanese() =>
    [
        Line("月が綺麗ですね", 0), Line("♪", 2, instrumental: true), Line("走り出す夜", 4), Line("月が綺麗ですね", 6), Line("…", 8),
    ];

    private static LyricMachineTranslator Translator(string? detected = "ja", FakeTranslator? fake = null, MemoryCache? cache = null,
        string[]? preferred = null) =>
        new(new FixedDetector(detected), fake ?? new FakeTranslator(), cache ?? new MemoryCache(), () => preferred ?? ["en-AU"]);

    private static async Task Settle(LyricMachineTranslator subject)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (subject.Pending is { } pending) await pending;
            if (subject.State is MachineTranslationState.Translating) { await Task.Delay(10); continue; }
            return;
        }
    }

    // MARK: Decision

    // Ported from MachineTranslationTests.swift: testPolicyTranslatesOnlyUnreadLanguagesWithoutSourceTranslations
    [Fact]
    public void PolicyTranslatesOnlyUnreadLanguagesWithoutSourceTranslations()
    {
        var lines = Japanese();
        Assert.Equal(MachineTranslationPolicy.Decision.Translate("ja", "en"),
            MachineTranslationPolicy.Decide(lines, true, true, "ja", "en", ["en-AU"]));
        Assert.Equal(MachineTranslationPolicy.Decision.Skipped(Skip.Off), MachineTranslationPolicy.Decide(lines, false, true, "ja", "en", ["en-AU"]));
        Assert.Equal(MachineTranslationPolicy.Decision.Skipped(Skip.Unavailable), MachineTranslationPolicy.Decide(lines, true, false, "ja", "en", ["en-AU"]));
        // A listener who also reads Japanese gets no translation.
        Assert.Equal(MachineTranslationPolicy.Decision.Skipped(Skip.ReadsLanguage),
            MachineTranslationPolicy.Decide(lines, true, true, "ja", "en", ["en-AU", "ja-JP"]));
        Assert.Equal(MachineTranslationPolicy.Decision.Skipped(Skip.ReadsLanguage), MachineTranslationPolicy.Decide(lines, true, true, "en", "en-GB", []));
        Assert.Equal(MachineTranslationPolicy.Decision.Skipped(Skip.UnknownLanguage), MachineTranslationPolicy.Decide(lines, true, true, null, "en", ["en"]));
        var withSource = lines.ToList();
        withSource[2] = withSource[2] with { Translation = "Running into the night" };
        Assert.Equal(MachineTranslationPolicy.Decision.Skipped(Skip.SourceHasTranslations),
            MachineTranslationPolicy.Decide(withSource, true, true, "ja", "en", ["en"]));
        Assert.Equal(MachineTranslationPolicy.Decision.Skipped(Skip.NoLines),
            MachineTranslationPolicy.Decide([Line("♪", 0, instrumental: true)], true, true, "ja", "en", []));
    }

    // Ported from MachineTranslationTests.swift: testLanguageKeysKeepChineseScripts
    [Fact]
    public void LanguageKeysKeepChineseScripts()
    {
        Assert.Equal("en", MachineTranslationPolicy.Key("en-AU"));
        Assert.Equal("zh-Hans", MachineTranslationPolicy.Key("zh-Hans"));
        Assert.Equal("zh-Hant", MachineTranslationPolicy.Key("zh-Hant-TW"));
        Assert.Equal("zh-Hant", MachineTranslationPolicy.Key("zh-TW"));
        Assert.False(MachineTranslationPolicy.SameLanguage("zh-Hans", "zh-Hant"));
        Assert.True(MachineTranslationPolicy.SameLanguage("pt-BR", "pt-PT"));
        Assert.Equal("fr", MachineTranslationPolicy.SystemLanguage(["fr-CA", "en"]));
        Assert.Equal("en", MachineTranslationPolicy.SystemLanguage([]));
    }

    // Ported from MachineTranslationTests.swift: testOptionsAreOffByDefaultAndSaved (the defaults and the resolved
    // target; saving belongs to the settings store)
    [Fact]
    public void OptionsAreOffByDefault()
    {
        var options = new MachineTranslationOptions();
        Assert.False(options.Enabled);
        Assert.Equal("", options.Target);
        Assert.Equal("fr", options.ResolvedTarget(["fr-CA"]));
        Assert.Equal("de", (options with { Enabled = true, Target = "de" }).ResolvedTarget(["fr-CA"]));
    }

    // MARK: Line mapping

    // Ported from MachineTranslationTests.swift: testLineMappingSendsEachSungLineOnceAndMapsBack
    [Fact]
    public void LineMappingSendsEachSungLineOnceAndMapsBack()
    {
        var lines = Japanese();
        var texts = TranslationLineMap.Texts(lines);
        Assert.Equal(["月が綺麗ですね", "走り出す夜"], texts);
        var map = TranslationLineMap.LineTranslations(lines, TranslationLineMap.Pairs(texts, ["The moon is lovely", "  "]));
        // A blank answer shows nothing.
        Assert.Equal(new Dictionary<int, string> { [0] = "The moon is lovely", [3] = "The moon is lovely" }, map);
        var mixed = lines.ToList();
        mixed[0] = mixed[0] with { Translation = "Source" };
        // A line with a translation of its own keeps it.
        Assert.False(TranslationLineMap.LineTranslations(mixed, new Dictionary<string, string> { ["月が綺麗ですね"] = "Machine" }).ContainsKey(0));
        // An answer that only repeats the line is not shown.
        Assert.Empty(TranslationLineMap.LineTranslations([Line("Tokyo", 0)], new Dictionary<string, string> { ["Tokyo"] = "tokyo" }));
    }

    // Ported from MachineTranslationTests.swift: testCacheKeysDifferByLinesAndLanguages
    [Fact]
    public void CacheKeysDifferByLinesAndLanguages()
    {
        var a = TranslationLineMap.CacheKey(["a", "b"], "ja", "en");
        Assert.Equal(a, TranslationLineMap.CacheKey(["a", "b"], "ja", "en"));
        Assert.NotEqual(a, TranslationLineMap.CacheKey(["a", "b"], "ja", "fr"));
        Assert.NotEqual(a, TranslationLineMap.CacheKey(["ab"], "ja", "en"));
        // The same bytes as the Mac's: SHA-256 of "v1␟ja␟en␟a␟b" (U+001F separators) in lower-case hex.
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("v1\u001Fja\u001Fen\u001Fa\u001Fb"u8)), a);
    }

    // MARK: Disk cache

    // Ported from MachineTranslationTests.swift: testDiskCacheRoundTripsAndStaysBounded
    [Fact]
    public void DiskCacheRoundTripsAndStaysBounded()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new DiskLyricTranslationCache(directory, limit: 3);
            cache.Store(new Dictionary<string, string> { ["月"] = "Moon" }, "song0");
            Assert.Equal(new Dictionary<string, string> { ["月"] = "Moon" }, cache.Translations("song0"));
            File.SetLastWriteTimeUtc(Path.Combine(directory, "song0.json"), DateTime.UtcNow.AddSeconds(-1000));
            for (var index = 1; index <= 3; index++) cache.Store(new Dictionary<string, string> { ["x"] = $"{index}" }, $"song{index}");
            Assert.Equal(3, Directory.GetFiles(directory).Length);
            // The least recently used song goes first.
            Assert.Null(cache.Translations("song0"));
            Assert.Equal(new Dictionary<string, string> { ["x"] = "3" }, cache.Translations("song3"));
            Assert.Null(cache.Translations("missing"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    // MARK: Coordinator

    // Ported from MachineTranslationTests.swift: testTranslatesTheSongInOneBatchAndCachesIt
    [Fact]
    public async Task TranslatesTheSongInOneBatchAndCachesIt()
    {
        var fake = new FakeTranslator();
        var cache = new MemoryCache();
        var subject = Translator(fake: fake, cache: cache);
        subject.Update(Japanese(), enabled: true, target: "en");
        await Settle(subject);
        Assert.Single(fake.Calls);
        Assert.Equal(["月が綺麗ですね", "走り出す夜"], fake.Calls[0].Texts);
        Assert.Equal("ja", fake.Calls[0].Source);
        Assert.Equal("[en] 月が綺麗ですね", subject.Translations[0]);
        Assert.Equal("[en] 走り出す夜", subject.Translations[2]);
        Assert.False(subject.Translations.ContainsKey(1));
        Assert.Equal(new MachineTranslationState.Translated("ja", "en", 3), subject.State);

        // The same song later comes from the cache, without a request.
        for (var attempt = 0; attempt < 50 && cache.Count == 0; attempt++) await Task.Delay(10, TestContext.Current.CancellationToken);
        var again = Translator(fake: fake, cache: cache);
        again.Update(Japanese(), enabled: true, target: "en");
        await Settle(again);
        Assert.Single(fake.Calls);
        Assert.Equal("[en] 月が綺麗ですね", again.Translations[0]);
    }

    // Ported from MachineTranslationTests.swift: testOffSettingAndSourceTranslationsNeverAskTheTranslator
    [Fact]
    public async Task OffSettingAndSourceTranslationsNeverAskTheTranslator()
    {
        var fake = new FakeTranslator();
        var subject = Translator(fake: fake);
        subject.Update(Japanese(), enabled: false, target: "en");
        await Settle(subject);
        Assert.Equal(new MachineTranslationState.Skipped(Skip.Off), subject.State);
        var sourced = Japanese();
        sourced[0] = sourced[0] with { Translation = "The moon is lovely" };
        subject.Update(sourced, enabled: true, target: "en");
        await Settle(subject);
        Assert.Equal(new MachineTranslationState.Skipped(Skip.SourceHasTranslations), subject.State);
        Assert.Empty(subject.Translations);
        var unavailable = new LyricMachineTranslator(new FixedDetector("ja"), null, new MemoryCache(), () => ["en"]);
        unavailable.Update(Japanese(), enabled: true, target: "en");
        Assert.Equal(new MachineTranslationState.Skipped(Skip.Unavailable), unavailable.State);
        Assert.Empty(fake.Calls);
    }

    // Ported from MachineTranslationTests.swift: testASongChangeCancelsTheTranslationInFlight
    [Fact]
    public async Task ASongChangeCancelsTheTranslationInFlight()
    {
        var fake = new FakeTranslator { Delay = TimeSpan.FromMilliseconds(300) };
        var subject = Translator(fake: fake);
        subject.Update(Japanese(), enabled: true, target: "en");
        for (var attempt = 0; attempt < 100 && fake.Calls.Count == 0; attempt++) await Task.Delay(5, TestContext.Current.CancellationToken);
        fake.Delay = TimeSpan.Zero;
        subject.Update([Line("다음 노래", 0)], enabled: true, target: "en");
        await Settle(subject);
        await Task.Delay(400, TestContext.Current.CancellationToken);
        // The earlier song's answer never shows.
        Assert.Equal(new Dictionary<int, string> { [0] = "[en] 다음 노래" }, subject.Translations);
        subject.Update([], enabled: true, target: "en");
        Assert.Empty(subject.Translations);
        Assert.Equal(new MachineTranslationState.Idle(), subject.State);
    }

    // Ported from MachineTranslationTests.swift: testMissingLanguagesWaitForTheListener
    [Fact]
    public async Task MissingLanguagesWaitForTheListener()
    {
        var fake = new FakeTranslator { Error = new LyricTranslationException(LyricTranslationError.NeedsDownload) };
        var subject = Translator(fake: fake);
        subject.Update(Japanese(), enabled: true, target: "en");
        await Settle(subject);
        Assert.Equal(new MachineTranslationState.NeedsDownload("ja", "en"), subject.State);
        // The same lines again change nothing until a retry is asked for.
        subject.Update(Japanese(), enabled: true, target: "en");
        await Settle(subject);
        Assert.Single(fake.Calls);
        fake.Error = null;
        subject.Update(Japanese(), enabled: true, target: "en", force: true);
        await Settle(subject);
        Assert.Equal(2, fake.Calls.Count);
        Assert.NotEmpty(subject.Translations);
    }

    // Ported from MachineTranslationTests.swift: testTheTestHostNeverGetsTheRealTranslator. On Windows there is no
    // system translator at all.
    [Fact]
    public void ThereIsNoSystemTranslator() => Assert.Null(LyricMachineTranslator.SystemTranslator());
}

/// <summary>TranslationLanguages has no Mac test file of its own; these pin its stated rules.</summary>
public class TranslationLanguagesTests
{
    [Fact]
    public void ScriptsOfLanguageTagsFollowExplicitScripts()
    {
        Assert.Equal([TranslationLanguages.Script.Latin], TranslationLanguages.ScriptsOfLanguage("sr-Latn"));
        Assert.Equal([TranslationLanguages.Script.Cyrillic], TranslationLanguages.ScriptsOfLanguage("sr"));
        Assert.Equal([TranslationLanguages.Script.Han], TranslationLanguages.ScriptsOfLanguage("zh-Hans-CN"));
        Assert.Equal([TranslationLanguages.Script.Han, TranslationLanguages.Script.Kana], TranslationLanguages.ScriptsOfLanguage("ja_JP"));
    }

    [Fact]
    public void AChineseTranslationIsNotReadableForAnEnglishListener()
    {
        string[] chinese = ["你的名字", "奔向夜晚", "OK"];
        Assert.False(TranslationLanguages.Readable(chinese, ["en-AU"]));
        Assert.True(TranslationLanguages.Readable(chinese, ["en-AU", "zh-Hant"]));
        Assert.True(TranslationLanguages.Readable(["Your name", "東京", "Run into the night"], ["en"]));
    }
}
