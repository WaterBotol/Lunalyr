using System.Security.Cryptography;
using System.Text;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Core.Lyrics.Storage;
using Lunalyr.Core.Tests.Lyrics.Ranking;

namespace Lunalyr.Core.Tests.Lyrics.Storage;

/// <summary>Saved choices and the automatic cache on disk.</summary>
public sealed class SavedLyricsTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeClock _clock = new();

    public void Dispose() => _folder.Dispose();

    private SavedLyrics Saved => new(_folder.Path, _clock);

    private static LyricResult.Synced Lyrics => L.Synced(
    [
        L.Timed(L.Line("Sing along", 1, 3), L.Word("Sing", 1, 2), L.Word("along", 2, 3)) with { Romanization = "sing", Translation = "Canta", EndFromNextLine = true },
        new LyricLine { Start = 3, End = 3.01, Text = "Producer: Someone", IsInstrumental = true, Credit = true },
    ], "Word-by-word", "LRCLIB");

    [Fact]
    public void FilesAreNamedBySha256OfTheKeyAsOnTheMac()
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("our song|our artist||180")));
        Assert.Equal("pin-" + hash + ".json", SavedLyrics.FileName("our song|our artist||180", pin: true));
        Assert.Equal("cache-" + hash + ".json", SavedLyrics.FileName("our song|our artist||180", pin: false));
        Assert.Equal(64, hash.Length);
    }

    [Fact]
    public void EveryFieldSurvivesTheRoundTrip()
    {
        Assert.True(Saved.Save(Lyrics, "key", pin: true));
        Assert.Equal(Lyrics, Saved.Read("key", pin: true));
        Assert.Null(Saved.Read("key", pin: false));
        var plain = L.Plain(["One", "", "Two"], "Not synced", "lyrics.ovh");
        Assert.True(Saved.Save(plain, "plain", pin: false));
        Assert.Equal(plain, Saved.Read("plain", pin: false));
        var candidate = new LyricCandidate(LyricSourceId.QQMusic, Lyrics, "Our Song", "Our Artist", 181.5, 42);
        Assert.True(Saved.Save(candidate, "auto"));
        var read = Saved.ReadCandidate("auto");
        Assert.Equal(candidate with { Score = 0 }, read);
        // A pin carries no source, so it is not a ranked candidate.
        Assert.Null(Saved.ReadCandidate("plain"));
        Assert.Empty(Directory.GetFiles(_folder.Path, "*.tmp"));
    }

    [Fact]
    public void CacheEntriesExpireAfterFourteenDaysAndPinsNever()
    {
        Saved.Save(Lyrics, "key", pin: false);
        Saved.Save(Lyrics, "key", pin: true);
        _clock.Advance(TimeSpan.FromDays(14) - TimeSpan.FromSeconds(1));
        Assert.NotNull(Saved.Read("key", pin: false));
        _clock.AdvanceSeconds(1);
        Assert.Null(Saved.Read("key", pin: false));
        _clock.Advance(TimeSpan.FromDays(3650));
        Assert.Equal(Lyrics, Saved.Read("key", pin: true));
        Saved.Unpin("key");
        Assert.Null(Saved.Read("key", pin: true));
    }

    [Fact]
    public void TheCacheKeepsTheNewestHundredAndFiftyAndEveryPin()
    {
        var plain = L.Plain(["la"], "", "");
        for (var i = 0; i < 5; i++)
        {
            Saved.Save(plain, $"pin {i}", pin: true);
        }
        for (var i = 0; i < 155; i++)
        {
            Assert.True(Saved.Save(plain, $"auto {i}", pin: false));
            File.SetLastWriteTimeUtc(Path.Combine(_folder.Path, SavedLyrics.FileName($"auto {i}", false)), new DateTime(2026, 1, 1).AddMinutes(i));
        }
        Saved.Save(plain, "auto 155", pin: false);
        Assert.Equal(SavedLyrics.MaxCacheEntries, Directory.GetFiles(_folder.Path, "cache-*").Length);
        Assert.Equal(5, Directory.GetFiles(_folder.Path, "pin-*").Length);
        Assert.Null(Saved.Read("auto 0", pin: false));
        Assert.NotNull(Saved.Read("auto 155", pin: false));
    }

    [Fact]
    public void BrokenOrOversizedEntriesFailSoft()
    {
        var bad = L.Synced([new LyricLine { Start = double.NaN, End = 1, Text = "x" }], "", "");
        Assert.False(Saved.Save(bad, "nan", pin: true));
        var huge = L.Plain([new string('a', SavedLyrics.MaxEntryBytes)], "", "");
        Assert.False(Saved.Save(huge, "huge", pin: true));
        Directory.CreateDirectory(_folder.Path);
        File.WriteAllText(Path.Combine(_folder.Path, SavedLyrics.FileName("junk", pin: true)), "{ not json");
        Assert.Null(Saved.Read("junk", pin: true));
        Assert.Null(new SavedLyrics(_folder.Sub("missing"), _clock).Read("junk", pin: true));
    }

    // Ported from LyricReviewFixesTests.swift: testTranslationsAreOnByDefaultAndPersist (the cache-decoding part:
    // entries written before a field existed still decode)
    [Fact]
    public void OlderEntriesWithoutNewerFieldsStillDecode()
    {
        const string old = """
            {"result":{"kind":"synced","precision":"Line-synced","source":"LRCLIB",
              "lines":[{"start":1,"end":2,"text":"Hola","words":[],"isInstrumental":false}]},
             "storedAt":"2026-11-12T20:00:00+00:00"}
            """;
        File.WriteAllText(Path.Combine(_folder.Path, SavedLyrics.FileName("old", pin: true)), old);
        var line = Assert.Single(Assert.IsType<LyricResult.Synced>(Saved.Read("old", pin: true)).Lines);
        Assert.Equal("Hola", line.Text);
        Assert.Null(line.Translation);
        Assert.Null(line.EndFromNextLine);
        Assert.False(line.WordTimed);
    }
}
