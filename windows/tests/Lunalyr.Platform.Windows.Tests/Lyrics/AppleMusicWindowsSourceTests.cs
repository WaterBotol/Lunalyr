using Lunalyr.Core.Lyrics;
using Lunalyr.Core.Lyrics.Pipeline;
using Lunalyr.Core.Lyrics.Ranking;
using Lunalyr.Platform.Lyrics;

namespace Lunalyr.Platform.Windows.Tests.Lyrics;

public class AppleMusicWindowsSourceTests
{
    private static readonly LyricQuery Query = new("A Song", "A Singer", "An Album", 180);

    [Fact]
    public async Task WithoutApplesAppTheSourceSaysItIsNotInstalled()
    {
        using var packages = new TempFolder();
        var source = new AppleMusicWindowsSource(packages.Path);
        Assert.Equal(LyricSourceId.AppleMusicCache, source.Id);
        Assert.False(source.Availability.IsAvailable);
        Assert.Equal("Apple Music isn't installed on this PC.", source.Availability.Reason);
        Assert.Null(await source.FetchAsync(Query, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WithApplesAppTheSourceExplainsThereIsNothingToRead()
    {
        using var packages = new TempFolder();
        Directory.CreateDirectory(Path.Combine(packages.Path, "AppleInc.AppleMusicWin_nzyj5cx40ttqa"));
        var source = new AppleMusicWindowsSource(packages.Path);
        Assert.False(source.Availability.IsAvailable);
        Assert.Contains("doesn't keep its lyrics anywhere Lunalyr can read", source.Availability.Reason, StringComparison.Ordinal);
        Assert.Null(await source.FetchAsync(Query, TestContext.Current.CancellationToken));
        // A missing packages folder reads as "not installed", never throws.
        Assert.False(new AppleMusicWindowsSource(Path.Combine(packages.Path, "nope")).Availability.IsAvailable);
    }

    [Fact]
    public async Task TheLyricServiceListsAppleMusicAsUnavailableWithItsReasonAndNeverAsksIt()
    {
        using var packages = new TempFolder();
        using var cache = new TempFolder();
        var apple = new AppleMusicWindowsSource(packages.Path);
        // No HTTP client is needed: only Apple Music is switched on, and it is never asked.
        using var http = new HttpClient(new NoNetwork());
        var service = LyricService.CreateDefault(cache.Path, http, new LyricServiceOptions { PlatformSources = [apple] });
        var options = LyricSourceSnapshot.From(LyricSourceSnapshot.RecommendedOrder, new HashSet<LyricSourceId> { LyricSourceId.AppleMusicCache },
            LyricMatchMode.AccuracyFirst);
        var row = Assert.Single(service.SourceList(options), status => status.Id == LyricSourceId.AppleMusicCache);
        Assert.Equal(("Apple Music", false, true), (row.Title, row.IsAvailable, row.Enabled));
        Assert.Equal(apple.Availability.Reason, row.UnavailableReason);
        var report = await service.LookupAsync(Query, options, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(report.Result);
        Assert.Equal(apple.Availability.Reason, report.States[LyricSourceId.AppleMusicCache]);
        // Every other source has a provider and works here.
        Assert.All(service.SourceList(options).Where(status => status.Id != LyricSourceId.AppleMusicCache), status => Assert.True(status.IsAvailable));
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No request is expected.");
    }
}
