using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Ranking;
using static Lunalyr.Core.Lyrics.LyricSourceId;

namespace Lunalyr.Core.Tests.Lyrics.Ranking;

/// <summary>
/// Apple Music lists a Japanese song that also has an English recording under its English title ("Racing into the
/// Night" for 夜に駆ける), while the English recording says "English Version". Lyrics for the language version, or in
/// another language than the song's, rank as another version.
/// </summary>
public sealed class LanguageVersionTests
{
    private static readonly LyricSourceSnapshot Options = L.Options(LyricMatchMode.AccuracyFirst, AppleMusicCache, LyricsPlus, NetEase, Lrclib, QQMusic, Kugou);

    // Lyric fixtures in the shape the services return: invented lines, not any song's words (the Mac's).
    internal static readonly string[] Japanese = ["夜の街を駆けてゆく", "君の声が聞こえた", "静かな部屋の中で", "ふたりの影が揺れる", "明日の光を待って", "まだ眠れないまま"];

    internal static readonly string[] English =
    [
        "Running through the city night", "I can hear your voice again", "In a quiet little room",
        "Our shadows sway along", "Waiting for tomorrow's light", "Still awake tonight",
    ];

    internal static readonly string[] Romaji =
    [
        "Yoru no machi wo kakete yuku", "Kimi no koe ga kikoeta", "Shizuka na heya no naka de",
        "Futari no kage ga yureru", "Ashita no hikari wo matte", "Mada nemurenai mama",
    ];

    /// <summary>
    /// The Mac's fixture: line i from i·4+1 s for 3.5 s, word-timed over its first 3 s. The Mac times each line as one
    /// token; on Windows a whole-line token is line timing (the phrase-token rule of 12 Nov 2026), so the same 3 s are
    /// split over the line's words (per character where there are no spaces) to keep it word-timed.
    /// </summary>
    internal static LyricCandidate Candidate(LyricSourceId source, string[] text, string title, bool words, string artist = "YOASOBI",
        bool reported = true, double duration = 258)
    {
        var lines = text.Select((row, index) =>
        {
            var start = index * 4.0 + 1;
            var line = L.Line(row, start, start + 3.5);
            if (!words)
            {
                return line;
            }
            var tokens = row.Contains(' ', StringComparison.Ordinal)
                ? row.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                : [.. row.EnumerateRunes().Select(rune => rune.ToString())];
            var step = 3.0 / tokens.Length;
            return L.Timed(line, [.. tokens.Select((token, k) => L.Word(token, start + ((k + L.Sung(k)) * step), start + ((k + 1) * step)))]);
        }).ToList();
        return new LyricCandidate(source, L.Synced(lines, "fixture", source.Title()), title, artist, duration, 0) { MetadataReported = reported };
    }

    internal static LyricQuery Query(string title, string? genre = null, string artist = "YOASOBI") =>
        new(title, artist, null, 258) { Genre = genre };

    private static void Tags(string name, params string[] expected) =>
        Assert.Equal(expected.Order(StringComparer.Ordinal), VersionTags.Tags(name).Order(StringComparer.Ordinal));

    // ---- Tags ----

    // Ported from LanguageVersionTests.swift: testLanguageVersionsAreTags
    [Fact]
    public void LanguageVersionsAreTags()
    {
        Tags("BABY (English Version)", "english");
        Tags("Idol (English ver.)", "english");
        Tags("Idol - Eng Ver.", "english");
        Tags("Idol (English)", "english");
        Tags("アイドル (英語ver.)", "english");
        Tags("Our Song (Japanese Ver.)", "japanese");
        Tags("Our Song [Korean Version]", "korean");
        Tags("Our Song (日本語ver.)", "japanese");
        Tags("Our Song (Live / English Version)", "live", "english");
        foreach (var name in new[] { "English Girl", "Our Song (Album Version)", "Our Song (JP)", "Our Song (English Rose Remaster)" })
        {
            Assert.False(VersionTags.Tags(name).Overlaps(VersionTags.Languages), name);
        }
        Assert.Equal("English", VersionTags.Describe(["english"]));
    }

    // Ported from LanguageVersionTests.swift: testScriptOfLyrics
    [Fact]
    public void ScriptOfLyrics()
    {
        Assert.Equal(SungScript.Native, LyricLanguage.ScriptOf(Japanese));
        Assert.Equal(SungScript.Latin, LyricLanguage.ScriptOf(English));
        Assert.Equal(SungScript.Latin, LyricLanguage.ScriptOf(Romaji));
        // A K-pop style mix is never held against a song.
        Assert.Equal(SungScript.Mixed, LyricLanguage.ScriptOf(["사랑해 baby", "Oh my love", "Hold me tight", "Hold me close", "Never let go",
            "Dance all night", "Feel the beat", "Run away", "널 기다려"]));
        // An English intro does not make Japanese lyrics mixed.
        Assert.Equal(SungScript.Native, LyricLanguage.ScriptOf(["Have a nice day", .. Japanese]));
        // A one-line "pure music" notice says nothing.
        Assert.Null(LyricLanguage.ScriptOf(["纯音乐，请欣赏"]));
    }

    // ---- Ranking ----

    // Ported from LanguageVersionTests.swift: testEnglishVersionRanksBelowTheJapaneseLyricsOfAnUntaggedTitle
    // (the user's "BABY": Kugou offered "BABY (English Version)", word-timed, in English)
    [Fact]
    public void EnglishVersionRanksBelowTheJapaneseLyricsOfAnUntaggedTitle()
    {
        var english = Candidate(Kugou, English, "BABY (English Version)", words: true);
        var japanese = Candidate(Lrclib, Japanese, "BABY", words: false);
        var screened = LyricRanking.Screen([english, japanese], Query("BABY"), Options);
        Assert.Equal([Lrclib, Kugou], screened.Accepted.Select(c => c.Source));
        Assert.Equal([true, false], screened.Accepted.Select(c => LyricRanking.FitsVersion(c, Query("BABY"))));
    }

    // Ported from LanguageVersionTests.swift: testEnglishTitledJapaneseTrackPicksJapaneseLyrics
    // (the user's "Monotone": Kugou named its English lyrics just "Monotone")
    [Fact]
    public void EnglishTitledJapaneseTrackPicksJapaneseLyrics()
    {
        var english = Candidate(Kugou, English, "Monotone", words: true);
        var netEase = Candidate(NetEase, Japanese, "Monotone", words: false);
        var qq = Candidate(QQMusic, Japanese, "Monotone", words: false);
        // Two sources in Japanese against one in English.
        var ranked = LyricRanking.Ranked([english, netEase, qq], Query("Monotone"), Options);
        Assert.Equal(NetEase, ranked[0].Source);
        Assert.Equal(Kugou, ranked[^1].Source);
        Assert.True(ranked[^1].LanguageMismatch);
        // One against one: an East Asian genre settles it.
        ranked = LyricRanking.Ranked([english, netEase], Query("Monotone", genre: "J-Pop"), Options);
        Assert.Equal([NetEase, Kugou], ranked.Select(c => c.Source));
        // Without it nothing is judged, and word timing leads as before.
        ranked = LyricRanking.Ranked([english, netEase], Query("Monotone", genre: "Pop"), Options);
        Assert.Equal([Kugou, NetEase], ranked.Select(c => c.Source));
        Assert.DoesNotContain(ranked, c => c.LanguageMismatch);
    }

    // Ported from LanguageVersionTests.swift: testLyricsFoundUnderTheOriginalTitleSettleTheLanguage
    [Fact]
    public void LyricsFoundUnderTheOriginalTitleSettleTheLanguage()
    {
        var found = Candidate(NetEase, Japanese, "Racing into the Night", words: false) with { ViaOriginalTitle = true };
        var lrclib = Candidate(Lrclib, English, "Racing into the Night", words: true);
        var kugou = Candidate(Kugou, English, "Racing into the Night", words: true);
        var screened = LyricRanking.Screen([lrclib, kugou, found], Query("Racing into the Night"), Options);
        Assert.Equal(NetEase, screened.Accepted[0].Source);
        Assert.Equal([true, false, false], screened.Accepted.Select(c => LyricRanking.FitsVersion(c, Query("Racing into the Night"))));
    }

    // Ported from LanguageVersionTests.swift: testMusicsOwnLyricsStayFirstAndDecideTheLanguage
    [Fact]
    public void MusicsOwnLyricsStayFirstAndDecideTheLanguage()
    {
        var music = Candidate(AppleMusicCache, Japanese, "Idol", words: true);
        var english = Candidate(Lrclib, English, "Idol", words: true);
        var japanese = Candidate(QQMusic, Japanese, "Idol", words: false);
        var ranked = LyricRanking.Ranked([english, japanese, music], Query("Idol"), Options);
        Assert.Equal([AppleMusicCache, QQMusic, Lrclib], ranked.Select(c => c.Source));
        // Music's English lyrics for the playing track decide the other way, and stay first.
        var musicEnglish = Candidate(AppleMusicCache, English, "Idol", words: false);
        var again = LyricRanking.Ranked([Candidate(Lrclib, Japanese, "Idol", words: true), musicEnglish], Query("Idol"), Options);
        Assert.Equal([AppleMusicCache, Lrclib], again.Select(c => c.Source));
        Assert.True(again[^1].LanguageMismatch);
        Assert.False(again[0].LanguageMismatch);
    }

    // Ported from LanguageVersionTests.swift: testEnglishVersionTitlePrefersEnglishLyrics
    [Fact]
    public void EnglishVersionTitlePrefersEnglishLyrics()
    {
        var playing = Query("Idol (English Version)");
        var english = Candidate(Lrclib, English, "Idol (English Version)", words: false);
        var japanese = Candidate(Kugou, Japanese, "Idol", words: true);
        // A service that only repeats the search, with Japanese lyrics.
        var unreported = Candidate(LyricsPlus, Japanese, "Idol (English Version)", words: true, reported: false);
        var screened = LyricRanking.Screen([japanese, unreported, english], playing, Options);
        Assert.Equal(Lrclib, screened.Accepted[0].Source);
        Assert.True(screened.Accepted.First(c => c.Source == LyricsPlus).LanguageMismatch);
        // Listed as what it is.
        Assert.Equal(Lrclib, LyricRanking.Choose(screened.Accepted, playing, Options)?.Source);
    }

    // Ported from LanguageVersionTests.swift: testWesternSongsAreNotJudgedByAStrayMatch
    [Fact]
    public void WesternSongsAreNotJudgedByAStrayMatch()
    {
        var playing = Query("Our Song", artist: "Our Artist");
        var english = Candidate(Lrclib, English, "Our Song", words: true, artist: "Our Artist");
        var english2 = Candidate(Kugou, English, "Our Song", words: true, artist: "Our Artist");
        var stray = Candidate(LyricsPlus, Japanese, "Our Song", words: true, artist: "Our Artist", reported: false);
        var ranked = LyricRanking.Ranked([stray, english, english2], playing, Options);
        Assert.Equal(Lrclib, ranked[0].Source);
        Assert.DoesNotContain(ranked, c => c.LanguageMismatch);
        // A Kugou "pure music" notice is not Japanese or Chinese lyrics.
        var notice = new LyricCandidate(Kugou, L.Synced([L.Line("纯音乐，请欣赏", 0, 5)], "", ""), "Our Song", "Our Artist", 258, 0);
        Assert.Null(LyricLanguage.Expected(playing, [notice, english]));
    }

    // Ported from LanguageVersionTests.swift: testTaggedAndOtherLanguageLyricsBothRankAsOtherVersions
    [Fact]
    public void TaggedAndOtherLanguageLyricsBothRankAsOtherVersions()
    {
        var english = Candidate(Kugou, English, "Monotone", words: true);
        var japanese = Candidate(NetEase, Japanese, "Monotone", words: false);
        var qq = Candidate(QQMusic, Japanese, "Monotone", words: false);
        var tagged = Candidate(Lrclib, English, "Monotone (English Version)", words: true);
        var accepted = LyricRanking.Screen([english, japanese, qq, tagged], Query("Monotone"), Options).Accepted;
        Assert.Equal(new HashSet<LyricSourceId> { NetEase, QQMusic }, accepted.Take(2).Select(c => c.Source).ToHashSet());
        var others = accepted.Where(c => !LyricRanking.FitsVersion(c, Query("Monotone"))).Select(c => c.Source).ToHashSet();
        Assert.Equal(new HashSet<LyricSourceId> { Kugou, Lrclib }, others);
        // A lookup with only another version in hand keeps asking the other sources.
        Assert.True(LyricRanking.CouldBeat(accepted.First(c => c.Source == Kugou), QQMusic, Query("Monotone"), Options));
    }

    // ---- Windows ----

    /// <summary>
    /// The source list's notes in the Mac's order: version or language, original title, then the Apple relay note
    /// (Windows), timing fault, agreement and "showing". The language note gives way to a version note.
    /// </summary>
    [Fact]
    public void SourceListNotesFollowTheMacsOrder()
    {
        var query = Query("Monotone");
        var lyricsPlus = Candidate(LyricsPlus, Japanese, "Monotone", words: true);
        var relay = lyricsPlus with
        {
            Result = L.Synced(((LyricResult.Synced)lyricsPlus.Result).Lines, "", "Apple via Lyrics+"),
            LanguageMismatch = true,
            ViaOriginalTitle = true,
        };
        var tagged = Candidate(Lrclib, English, "Monotone (English Version)", words: true) with { LanguageMismatch = true };
        var screened = new LyricScreening([relay, tagged], []);
        var states = LyricRanking.Annotate(new Dictionary<LyricSourceId, string>(), screened, LyricsPlus, query);
        Assert.Equal("Match found · lyrics in another language · found under the original title · Apple Music’s lyrics · showing", states[LyricsPlus]);
        Assert.Equal("Match found · other version (English)", states[Lrclib]);
    }

    /// <summary>A match re-screened, as a lookup does at every arrival, loses a mark the new answers no longer support.</summary>
    [Fact]
    public void TheMarkIsWeighedAfreshAtEveryScreening()
    {
        var english = Candidate(Kugou, English, "Monotone", words: true) with { LanguageMismatch = true };
        var accepted = LyricRanking.Screen([english], Query("Monotone"), Options).Accepted;
        Assert.False(accepted[0].LanguageMismatch);
        // The local folder's file is chosen for the track: never marked.
        var local = Candidate(Lyrimuse, English, "Monotone", words: false);
        var options = L.Options(LyricMatchMode.AccuracyFirst, Lyrimuse, NetEase, QQMusic);
        var weighed = LyricRanking.Screen([local, Candidate(NetEase, Japanese, "Monotone", false), Candidate(QQMusic, Japanese, "Monotone", false)],
            Query("Monotone"), options).Accepted;
        Assert.DoesNotContain(weighed, c => c.LanguageMismatch);
    }
}
