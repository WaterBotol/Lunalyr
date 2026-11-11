using System.Text;
using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Sources;

namespace Lunalyr.Core.Tests.Lyrics.Sources;

public class LocalLyricsSourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly LyricQuery Sample = new("Sample", "Singer", null, 14);

    [Fact]
    public async Task AFolderMustBeChosenAndReadable()
    {
        var error = await Assert.ThrowsAsync<LyricSourceException>(() => new LocalLyricsSource("").FetchAsync(Sample, Ct));
        Assert.Equal(LyricSourceFailure.NotConfigured, error.Failure);
        using var folder = new TempFolder();
        error = await Assert.ThrowsAsync<LyricSourceException>(() => new LocalLyricsSource(folder.File("missing")).FetchAsync(Sample, Ct));
        Assert.Equal(LyricSourceFailure.LocalFiles, error.Failure);
        // An empty folder simply has nothing.
        Assert.Null(await new LocalLyricsSource(folder.Path).FetchAsync(Sample, Ct));
    }

    [Fact]
    public async Task SidecarsTravelAsExtrasWithTtmlPreferredToYrc()
    {
        using var folder = new TempFolder();
        folder.Write("Singer - Sample.lrc", "[ti:Sample]\n[ar:Singer]\n[length:00:14]\n[source:NetEase]\n[00:01.00]Hold on\n[00:04.00]The whole phrase");
        folder.Write("Singer - Sample.ttml", """<tt><body><p begin="1s" end="2.8s"><span begin="1s" end="1.4s">Hold </span><span begin="1.6s" end="2.8s">on</span></p></body></tt>""");
        folder.Write("Singer - Sample.yrc", "[1000,1800](1000,400,0)Hold (1600,1200,0)on");
        folder.Write("Singer - Sample.roma.lrc", "[00:01.00]ho ru do");
        folder.Write("Singer - Sample.tr.lrc", "[00:01.00]Wait");
        var found = await new LocalLyricsSource(folder.Path).FetchAsync(Sample, Ct);
        Assert.NotNull(found);
        Assert.Equal(LyricSourceId.Lyrimuse, found.Source);
        Assert.Equal(LyricFormat.Lrc, found.Format);
        Assert.StartsWith("[ti:Sample]", found.Payload, StringComparison.Ordinal);
        Assert.Equal(("Sample", "Singer", 14.0), (found.ReportedTitle, found.ReportedArtist, found.ReportedDuration));
        var extras = found.Extras!;
        Assert.Contains("<span begin=\"1s\"", extras[RawLyricsExtras.SidecarTtml], StringComparison.Ordinal);
        Assert.False(extras.ContainsKey(RawLyricsExtras.SidecarYrc));
        Assert.Equal("[00:01.00]ho ru do", extras[RawLyricsExtras.RomanizationLrc]);
        Assert.Equal("[00:01.00]Wait", extras[RawLyricsExtras.TranslationLrc]);
        Assert.Equal("NetEase", extras[RawLyricsExtras.Upstream]);
        File.Delete(folder.File("Singer - Sample.ttml"));
        found = await new LocalLyricsSource(folder.Path).FetchAsync(Sample, Ct);
        Assert.Equal("[1000,1800](1000,400,0)Hold (1600,1200,0)on", found?.Extras?[RawLyricsExtras.SidecarYrc]);
    }

    [Fact]
    public async Task TheBestFittingFileWinsAndOthersAreSkipped()
    {
        using var folder = new TempFolder();
        // No title tag, lyrics past the song's end, another length, the right one; an unrelated file is not read.
        folder.Write("A Sample.lrc", "[ar:Singer]\n[00:01.00]No title tag");
        folder.Write("B Sample.lrc", "[ti:Sample]\n[ar:Singer]\n[00:01.00]Start\n[00:40.00]Far too late");
        folder.Write("C Sample.lrc", "[ti:Sample]\n[ar:Singer]\n[length:00:30]\n[00:01.00]Another length");
        folder.Write("D Sample.lrc", "[ti:Sample]\n[ar:Singer]\n[length:00:14]\n[00:01.00]The right one");
        folder.Write("Elsewhere.lrc", "[ti:Sample]\n[ar:Singer]\n[length:00:14]\n[00:01.00]Name doesn't belong");
        var found = await new LocalLyricsSource(folder.Path).FetchAsync(Sample, Ct);
        Assert.Contains("The right one", found?.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutAnArtistTheTitleAndLengthDecide()
    {
        using var folder = new TempFolder();
        folder.Write("Sample.lrc", "[ti:Sample]\n[length:14]\n[00:01.00]Untagged artist");
        var source = new LocalLyricsSource(folder.Path);
        Assert.NotNull(await source.FetchAsync(new LyricQuery("Sample", "", null, 14), Ct));
        // A query that names an artist needs the file to name one too.
        Assert.Null(await source.FetchAsync(Sample, Ct));
    }

    [Fact]
    public async Task LegacyEncodedFilesAreRead()
    {
        using var folder = new TempFolder();
        var text = "[ti:晴天]\n[ar:周杰伦]\n[00:01.00]故事的小黄花\n[00:04.00]从出生那年就飘着";
        File.WriteAllBytes(folder.File("周杰伦 - 晴天.lrc"), Encoding.GetEncoding(54936).GetBytes(text));
        var found = await new LocalLyricsSource(folder.Path).FetchAsync(new LyricQuery("晴天", "周杰伦", null, 269), Ct);
        Assert.Equal(text, found?.Payload);
    }

    [Fact]
    public void FileNamesMayBelongWithoutEditionQualifiers()
    {
        var query = new LyricQuery("Our Song (2011 Remaster)", "Our Artist", null, 180);
        Assert.True(LocalLyricsSource.MayBelong("Our Artist - Our Song.lrc", query));
        Assert.False(LocalLyricsSource.MayBelong("Our Artist - Another.lrc", query));
        Assert.True(LocalLyricsSource.MayBelong("Band - !!!.lrc", new LyricQuery("!!!", "Band", null, 180)));
        Assert.Equal("unconfigured", LocalLyricsSource.Fingerprint(query, ""));
    }

    static LocalLyricsSourceTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
}
