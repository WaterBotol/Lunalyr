using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Ranking;
using static Lunalyr.Core.Lyrics.LyricSourceId;

namespace Lunalyr.Core.Tests.Lyrics.Ranking;

/// <summary>
/// Traditional and simplified Chinese name the same song (a Windows addition, suggested to the Mac): 晴天 — 周杰倫 is
/// listed by the Chinese catalogues as 晴天 — 周杰伦 (parity check of 13 Nov 2026, live table).
/// </summary>
public sealed class ChineseScriptTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void TraditionalAndSimplifiedNamesAreTheSameName()
    {
        Assert.Equal(1, LyricQuery.Similarity("周杰倫", "周杰伦"));
        Assert.Equal(1, LyricQuery.Similarity("後來", "后来"));
        Assert.Equal(1, LyricQuery.Similarity("陳奕迅", "陈奕迅"));
        // Qualifiers and containment still work across the scripts.
        Assert.Equal(0.95, LyricQuery.Similarity("愛情轉移 (Live)", "爱情转移 [Radio Edit]"));
        Assert.Equal(0.85, LyricQuery.Similarity("周杰倫", "周杰伦, 费玉清"));
        // Different names stay different.
        Assert.Equal(0, LyricQuery.Similarity("周杰倫", "林俊杰"));
        // The recording check passes the catalogue's simplified listing.
        var query = new LyricQuery("晴天", "周杰倫", "葉惠美", 269.747);
        var listed = new LyricCandidate(Kugou, L.Plain(["晴天"], "", ""), "晴天", "周杰伦", 269, 0);
        Assert.Null(LyricRanking.Rejection(listed, query));
    }

    [Fact]
    public void KeysAreNotFolded()
    {
        // Saved choices, cached results and offsets keep the keys they were written under.
        Assert.Equal("晴天|周杰倫|葉惠美|270", new LyricQuery("晴天", "周杰倫", "葉惠美", 269.747).RecordingKey);
        Assert.NotEqual(new LyricQuery("晴天", "周杰倫", null, 270).RecordingKey, new LyricQuery("晴天", "周杰伦", null, 270).RecordingKey);
        Assert.Equal("周杰倫", LyricQuery.Normalize("周杰倫"));
    }

    [Fact]
    public void FoldingIsStableAndLeavesOtherScriptsAlone()
    {
        Assert.Equal("周杰伦 后来 爱情转移 米津玄师", ChineseScripts.ToSimplified("周杰倫 後來 愛情轉移 米津玄師"));
        Assert.True(ChineseScripts.HasTraditional("周杰倫"));
        Assert.False(ChineseScripts.HasTraditional("周杰伦"));
        // Kana, hangul, Latin and characters both scripts share come back unchanged (the same string).
        foreach (var text in new[] { "夜に駆ける", "아이유", "Kenshi Yonezu", "晴天", "" })
        {
            Assert.Same(text, ChineseScripts.ToSimplified(text));
        }
        Assert.True(ChineseScripts.HasKana("夜に駆ける"));
        Assert.False(ChineseScripts.HasKana("周杰倫"));
        // Folding twice changes nothing, for every character the table knows.
        for (var c = '㐀'; c <= '﫿'; c++)
        {
            var once = ChineseScripts.ToSimplified(c.ToString());
            Assert.Equal(once, ChineseScripts.ToSimplified(once));
        }
    }

    [Fact]
    public void ATrackHeadingInTheOtherScriptIsACredit()
    {
        static LyricLine Line(string text, double start) => new() { Start = start, End = start + 2, Text = text };
        var flagged = LyricCredits.Flagging([Line("晴天 - 周杰伦", 0), Line("故事的小黄花", 5)], "晴天", "周杰倫");
        Assert.True(flagged[0].Credit);
        Assert.Null(flagged[1].Credit);
    }

    [Fact]
    public async Task CataloguesSearchAgainInSimplifiedScriptAndKeepTheLibraryNames()
    {
        var query = new LyricQuery("後來", "劉若英", null, 338.507) { Genre = "Mandopop" };
        var asked = new List<string>();
        var found = await ArtistAliases.WithAliasesAsync(QQMusic, query, aliases: null, enabled: true, (q, _) =>
        {
            asked.Add($"{q.Title} {q.Artist}");
            Assert.Equal("Mandopop", q.Genre);
            // As if the service could only find the song under its simplified names.
            return ValueTask.FromResult(q.Title == "后来"
                ? new LyricCandidate(QQMusic, L.Plain(["后来"], "", ""), "后来", "刘若英", 338, 0)
                : null);
        }, Ct);
        Assert.Equal(["後來 劉若英", "后来 刘若英"], asked);
        Assert.Equal(("后来", "劉若英"), (found?.Title, found?.Artist));
        Assert.Null(LyricRanking.Rejection(found!, query));
    }

    [Fact]
    public async Task NoSimplifiedSearchWhenNotNeeded()
    {
        var calls = new List<string>();
        ValueTask<LyricCandidate?> Nothing(LyricQuery q, CancellationToken _)
        {
            calls.Add($"{q.Title} {q.Artist}");
            return ValueTask.FromResult<LyricCandidate?>(null);
        }
        var traditional = new LyricQuery("晴天", "周杰倫", null, 270);
        // Already simplified; Japanese (kana) titles; LRCLIB, which is not a catalogue; the setting off.
        await ArtistAliases.WithAliasesAsync(Kugou, new LyricQuery("晴天", "周杰伦", null, 270), null, true, Nothing, Ct);
        await ArtistAliases.WithAliasesAsync(Kugou, new LyricQuery("夜に駆ける", "YOASOBI", null, 261), null, true, Nothing, Ct);
        await ArtistAliases.WithAliasesAsync(Lrclib, traditional, null, true, Nothing, Ct);
        await ArtistAliases.WithAliasesAsync(Kugou, traditional, null, false, Nothing, Ct);
        Assert.Equal(["晴天 周杰伦", "夜に駆ける YOASOBI", "晴天 周杰倫", "晴天 周杰倫"], calls);
        // Found under the library's names: the catalogue's simplified listing already matches.
        calls.Clear();
        var hit = new LyricCandidate(Kugou, L.Plain(["晴天"], "", ""), "晴天", "周杰伦", 269, 0);
        var found = await ArtistAliases.WithAliasesAsync(Kugou, traditional, null, true, (q, _) =>
        {
            calls.Add(q.Artist);
            return ValueTask.FromResult<LyricCandidate?>(hit);
        }, Ct);
        Assert.Same(hit, found);
        Assert.Equal(["周杰倫"], calls);
        // Only the kana-free name is converted: "東京" in a Japanese title stays, the artist is simplified.
        Assert.Equal(("東京フラッシュ", "张三"), ArtistAliases.SimplifiedForm(new LyricQuery("東京フラッシュ", "張三", null, 200)) is { } q
            ? (q.Title, q.Artist) : default);
        Assert.Null(ArtistAliases.SimplifiedForm(new LyricQuery("Lemon", "Kenshi Yonezu", null, 255)));
    }
}
