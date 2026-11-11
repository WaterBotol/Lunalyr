using System.Text;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Parsing;
using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Core.Lyrics.Sources;

namespace Lunalyr.Core.Tests.Lyrics.Sources;

/// <summary>
/// Source-level Mac tests from files other than LyricProviderTests.swift: LyricCoreReviewTests, LyricSelectionTests,
/// NextVersionTests, AMLLAndKRCTests, ReadingAndTTMLTests, LyricLookupProgressTests and LyricRankingTests.
/// </summary>
public class PortedSourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly LyricQuery Song = new("Our Song", "Our Artist", "Our Album", 180);

    private static string Row(string title, string artist = "Our Artist", string album = "Our Album", double duration = 180) =>
        $$"""{"trackName":"{{title}}","artistName":"{{artist}}","albumName":"{{album}}","duration":{{duration}},"syncedLyrics":"[00:01.00]Sing along\n[00:04.00]Next line"}""";

    /// <summary>Answers by host and path; anything else is a 404, as in the Mac's LyricCoreReviewTests.</summary>
    private static FixtureHandler Service() => new() { Fallback = Reply.Text(404, "") };

    // Ported from LyricCoreReviewTests.swift: testConvertedStampsNeverSaySixtySeconds
    [Fact]
    public void ConvertedStampsNeverSaySixtySeconds()
    {
        // Rounding the seconds alone wrote these as "00:60.00" and "01:60.00".
        Assert.Equal("01:00.000", KugouKrc.Clock(59.9996));
        Assert.Equal("01:59.998", KugouKrc.Clock(119.998));
        Assert.Equal("00:00.000", KugouKrc.Clock(0));
        Assert.Equal("00:00.000", KugouKrc.Clock(-2));
        Assert.Equal("62:05.250", KugouKrc.Clock(3725.25));
    }

    // Ported from LyricCoreReviewTests.swift: testLRCLIBOffersTheHitThatIsThePlayingRecording
    [Fact]
    public async Task LrclibOffersTheHitThatIsThePlayingRecording()
    {
        // The exact title, four seconds too long, outscores an edition that fits; it fails the recording check, so the
        // one that fits is offered.
        var handler = Service().Set("lrclib.net/api/search", Reply.Text(200,
            "[" + Row("Our Song", duration: 184) + "," + Row("Our Song - Remastered", "Our Artist & Friend", "Other", 180) + "]"));
        var found = await new LrclibSource(handler.Client()).FetchAsync(Song, Ct);
        Assert.NotNull(found);
        Assert.Equal(180, found.ReportedDuration);
        Assert.True(LyricRanking.NamesRecording(LyricSourceId.Lrclib, found.ReportedTitle!, found.ReportedArtist!, found.ReportedDuration, Song));
    }

    // Ported from LyricCoreReviewTests.swift: testKugouFetchesTheHitThatIsThePlayingRecordingFirst
    [Fact]
    public async Task KugouFetchesTheHitThatIsThePlayingRecordingFirst()
    {
        var query = new LyricQuery("Our Song", "Our Artist", null, 120);
        // The first hit scores higher but is 3.5 s too long for a two-minute song.
        var lrc = Convert.ToBase64String("[00:01.00]Sing along\n[00:04.00]Next line"u8);
        var handler = Service()
            .Set("mobileservice.kugou.com/api/v3/search/song", Reply.Text(200,
                """{"data":{"info":[{"songname":"Our Song","singername":"Our Artist","duration":123.5,"hash":"a"},{"songname":"Our Song (Acoustic)","singername":"Our Artist","duration":120,"hash":"b"}]}}"""))
            .Set("lyrics.kugou.com/search", Reply.Text(200, """{"candidates":[{"id":"1","accesskey":"k"}]}"""))
            .Set("lyrics.kugou.com/download", Reply.Text(200, $$"""{"content":"{{lrc}}"}"""));
        var found = await new KugouSource(handler.Client()).FetchAsync(query, Ct);
        Assert.NotNull(found);
        Assert.Equal(120, found.ReportedDuration);
        Assert.True(LyricRanking.NamesRecording(LyricSourceId.Kugou, found.ReportedTitle!, found.ReportedArtist!, found.ReportedDuration, query));
    }

    // Ported from LyricCoreReviewTests.swift: testAnInstrumentalRecordingOnLRCLIBHasNoLyrics
    [Fact]
    public async Task AnInstrumentalRecordingOnLrclibHasNoLyrics()
    {
        // An album's instrumental edition shares the sung one's names and length.
        var handler = Service()
            .Set("lrclib.net/api/get", Reply.Text(200,
                """{"trackName":"Our Song","artistName":"Our Artist","albumName":"Our Album","duration":180,"instrumental":true,"plainLyrics":null,"syncedLyrics":null}"""))
            .Set("lrclib.net/api/search", Reply.Text(200, "[" + Row("Our Song", album: "Sung") + "]"));
        Assert.Null(await new LrclibSource(handler.Client()).FetchAsync(Song, Ct));
    }

    // Ported from LyricSelectionTests.swift: testNewProvidersKeepTimingProvenanceHonest
    [Fact]
    public async Task NewProvidersKeepTimingProvenanceHonest()
    {
        var query = new LyricQuery("Our Song", "Our Artist", null, 180);
        var handler = new FixtureHandler
        {
            Responder = request => request.RequestUri!.Host switch
            {
                "search.kuwo.cn" => Reply.Text(200, """{"abslist":[{"MUSICRID":"MUSIC_123","SONGNAME":"Our Song","ARTIST":"Our Artist","DURATION":"180"}]}"""),
                "www.kuwo.cn" => Reply.Text(200, """{"code":200,"data":{"lrclist":[{"time":"1.0","lineLyric":"Sing along"},{"time":"4.0","lineLyric":"Next line"}]}}"""),
                _ => Reply.Text(200, """{"lyrics":"Sing along\nNext line"}"""),
            },
        };
        var kuwo = await new KuwoSource(handler.Client()).FetchAsync(query, Ct);
        Assert.NotNull(kuwo);
        Assert.Equal(LyricSourceId.Kuwo, kuwo.Source);
        // Line timing: Kuwo's list becomes line-timed LRC.
        Assert.Equal(LyricFormat.Lrc, kuwo.Format);
        Assert.Equal("[00:01.000]Sing along\n[00:04.000]Next line", kuwo.Payload);
        var plain = await new LyricsOvhSource(handler.Client()).FetchAsync(query, Ct);
        Assert.NotNull(plain);
        Assert.Equal(LyricSourceId.LyricsOvh, plain.Source);
        Assert.Equal(LyricFormat.Plain, plain.Format);
    }

    // Ported from NextVersionTests.swift: testLRCLIBSearchStillRunsAfterExactEndpoint503
    [Fact]
    public async Task LrclibSearchStillRunsAfterExactEndpoint503()
    {
        var retryNow = new Dictionary<string, string> { ["Retry-After"] = "0" };
        var handler = new FixtureHandler()
            .Set("lrclib.net/api/get", Reply.Text(503, "{}", retryNow))
            .Set("lrclib.net/api/search", Reply.Text(200,
                """[{"trackName":"Test Song","artistName":"Test Artist","duration":20,"syncedLyrics":"[00:01]Found by search\n[00:05]Next line"}]""", retryNow));
        var query = new LyricQuery("Test Song", "Test Artist", null, 20);
        var match = await new LrclibSource(handler.Client()).FetchAsync(query, Ct);
        Assert.NotNull(match);
        Assert.Equal(LyricSourceId.Lrclib, match.Source);
        Assert.Equal("Test Song", match.ReportedTitle);
        Assert.Equal("[00:01]Found by search\n[00:05]Next line", match.Payload);
        // The busy exact endpoint was asked twice (one retry), then search ran.
        Assert.Equal(["lrclib.net/api/get", "lrclib.net/api/get", "lrclib.net/api/search"], handler.Addresses);
    }

    private static byte[] MacStyleKrc(string text)
    {
        // As the Mac test builds it: "krc1", then [0x78, 0x9C] + raw deflate, XOR-ed with the key.
        byte[] key = [0x40, 0x47, 0x61, 0x77, 0x5E, 0x32, 0x74, 0x47, 0x51, 0x36, 0x31, 0x2D, 0xCE, 0xD2, 0x6E, 0x69];
        using var raw = new MemoryStream();
        using (var deflate = new System.IO.Compression.DeflateStream(raw, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(Encoding.UTF8.GetBytes(text));
        }
        byte[] bytes = [0x78, 0x9C, .. raw.ToArray()];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] ^= key[i % key.Length];
        }
        return [.. "krc1"u8, .. bytes];
    }

    private const string KrcSample =
        "[id:$00000000]\n[ar:An Artist]\n[1000,2000]<0,500,0>Hello <500,600,0>world <1100,900,0>again\n[4000,1500]<0,700,0>Second<700,800,0> line";

    // Ported from AMLLAndKRCTests.swift: testKRCDecodesToTheLinesAndWords
    [Fact]
    public void KrcDecodesToTheLinesAndWords()
    {
        var text = KugouKrc.Decode(MacStyleKrc(KrcSample));
        Assert.NotNull(text);
        Assert.Contains("<0,500,0>Hello", text, StringComparison.Ordinal);
        Assert.Equal(KrcSample, KugouKrc.Decode(KugouKrc.Encode(KrcSample)));
    }

    // Ported from AMLLAndKRCTests.swift: testAnythingThatIsNotKRCIsRefused
    [Fact]
    public void AnythingThatIsNotKrcIsRefused()
    {
        Assert.Null(KugouKrc.Decode("[00:01.00]plain"u8));
        Assert.Null(KugouKrc.Decode(Enumerable.Repeat((byte)1, 40).ToArray()));
        Assert.Null(KugouKrc.Decode([.. "krc1"u8, .. Enumerable.Repeat((byte)1, 40)]));
    }

    // Ported from AMLLAndKRCTests.swift: testAMLLAddressesTheFileByNetEaseID
    [Fact]
    public void AmllAddressesTheFileByNetEaseId() =>
        Assert.Equal("https://example.test/db/ncm-lyrics/12345.ttml", AmllSource.FileAddress("https://example.test/db", 12345));

    // Ported from ReadingAndTTMLTests.swift: testLocalFilesRequireMatchingArtistAndAlbum
    [Fact]
    public async Task LocalFilesRequireMatchingArtistAndAlbum()
    {
        using var folder = new TempFolder();
        folder.Write("Singer - Sample - Album.lrc", "[ti:Sample]\n[ar:Singer]\n[al:Album]\n[00:01]Keep moving\n[00:03]Hold on");
        var source = new LocalLyricsSource(folder.Path);
        Assert.NotNull(await source.FetchAsync(new LyricQuery("Sample", "Singer", "Album", 10), Ct));
        Assert.Null(await source.FetchAsync(new LyricQuery("Sample", "Someone else", "Album", 10), Ct));
        Assert.Null(await source.FetchAsync(new LyricQuery("Sample", "Singer", "Live", 10), Ct));
    }

    // Ported from ReadingAndTTMLTests.swift: testLyrimuseTranslationFilesAttachByTimestamp (the fingerprint and
    // pick-up part; attaching the translation to lines is the parser's)
    [Fact]
    public async Task LyrimuseTranslationFilesAttachByTimestamp()
    {
        using var folder = new TempFolder();
        var query = new LyricQuery("Sample", "Singer", null, 20);
        folder.Write("Singer - Sample.lrc", "[ti:Sample]\n[ar:Singer]\n[00:01.00]君の名は\n[00:01.00]Your name\n[00:04.00]夜に駆ける\n[00:07.00]OK\n[00:10.00]♪\n[00:13.00]最後");
        var untranslated = LocalLyricsSource.Fingerprint(query, folder.Path);
        const string translation = "[ti:Sample]\n[00:01.00]你的名字\n[00:04.05]奔向夜晚\n[00:07.00]OK\n[00:10.00]间奏\n[00:13.00]";
        folder.Write("Singer - Sample.tr.lrc", translation);
        // Adding the companion file is noticed, so the lyrics are read again.
        Assert.NotEqual(untranslated, LocalLyricsSource.Fingerprint(query, folder.Path));
        var found = await new LocalLyricsSource(folder.Path).FetchAsync(query, Ct);
        Assert.NotNull(found);
        Assert.Equal(translation, found.Extras![RawLyricsExtras.TranslationLrc]);
    }

    // Ported from LyricLookupProgressTests.swift: testFingerprintCoversOnlyTheTracksFilesIncludingEditsInPlace
    [Fact]
    public void FingerprintCoversOnlyTheTracksFilesIncludingEditsInPlace()
    {
        using var folder = new TempFolder();
        var query = new LyricQuery("Our Song", "Our Artist", null, 180);
        var own = folder.File("Our Song.lrc");
        folder.Write("Our Song.lrc", "[00:01.00]One");
        var before = LocalLyricsSource.Fingerprint(query, folder.Path);
        folder.Write("Different.lrc", "[00:01.00]Other");
        Assert.Equal(before, LocalLyricsSource.Fingerprint(query, folder.Path));
        File.AppendAllText(own, "\n[00:02.00]Two");
        var edited = LocalLyricsSource.Fingerprint(query, folder.Path);
        Assert.NotEqual(before, edited);
        folder.Write("Our Song.ttml", "<tt/>");
        Assert.NotEqual(edited, LocalLyricsSource.Fingerprint(query, folder.Path));
    }

    // Ported from LyricRankingTests.swift: testLocalFilesWithTheArtistAreNotRejectedOnTheirLength (its LocalLyricBridge.length
    // assertions; the rejection half belongs to the ranking code)
    [Fact]
    public void LocalFilesWithTheArtistAreNotRejectedOnTheirLength()
    {
        Assert.Equal(221, LocalLyricsSource.Length("03:41"));
        Assert.Equal(221, LocalLyricsSource.Length("221"));
        Assert.Equal(221, LocalLyricsSource.Length("221000")); // A bare number too large for seconds is milliseconds
        Assert.Equal(221, LocalLyricsSource.Length("221000ms"));
        Assert.Null(LocalLyricsSource.Length("99:99:99"));
        Assert.Null(LocalLyricsSource.Length("0"));
    }
}
