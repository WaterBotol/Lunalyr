using Lunalyr.Core.Lyrics;
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
}
