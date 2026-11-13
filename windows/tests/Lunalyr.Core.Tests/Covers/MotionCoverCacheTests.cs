using Lunalyr.Core.Covers;
using Lunalyr.Core.Tests.Lyrics.Sources;

namespace Lunalyr.Core.Tests.Covers;

/// <summary>The animated covers' file cache (Windows' own): what it fetches, its limits and its clean-up. No network.</summary>
public sealed class MotionCoverCacheTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private static readonly Uri Video = new("https://mvod.itunes.apple.com/a/cover_768x768_-.mp4");

    public void Dispose() => _folder.Dispose();

    /// <summary>The start of an ISO media file (a box of type ftyp), then <paramref name="size"/> bytes in all.</summary>
    private static byte[] Mp4(int size = 4096)
    {
        var bytes = new byte[size];
        bytes[3] = 0x1c;
        "ftyp"u8.CopyTo(bytes.AsSpan(4));
        return bytes;
    }

    private MotionCoverCache Cache(FixtureHandler handler, long max = 1_000_000, long maxFile = 100_000) =>
        new(_folder.Path, new HttpClient(handler), "Lunalyr/0.1 (Windows port; dev)", max, maxFile);

    [Fact]
    public async Task ACoverIsDownloadedOnceAndKept()
    {
        var handler = new FixtureHandler().Set("mvod.itunes.apple.com/a/cover_768x768_-.mp4", new Reply(200, Mp4()));
        var cache = Cache(handler);
        var path = await cache.GetAsync(Video, TestContext.Current.CancellationToken);
        Assert.NotNull(path);
        Assert.StartsWith(_folder.Path, path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4096, new FileInfo(path).Length);
        Assert.Equal(path, await cache.GetAsync(Video, TestContext.Current.CancellationToken));
        Assert.Single(handler.Requests);
        Assert.StartsWith("Lunalyr/", string.Join(" ", handler.Requests[0].Headers.UserAgent.Select(u => u.ToString())), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_folder.Path, "*.part"));
    }

    [Fact]
    public async Task TwoAsksShareOneDownload()
    {
        var release = new TaskCompletionSource();
        var handler = new FixtureHandler().Set("mvod.itunes.apple.com/a/cover_768x768_-.mp4", new Reply(200, Mp4(), Wait: release.Task));
        var cache = Cache(handler);
        var first = cache.GetAsync(Video, TestContext.Current.CancellationToken);
        var second = cache.GetAsync(Video, TestContext.Current.CancellationToken);
        release.SetResult();
        Assert.Equal(await first, await second);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AnAskThatIsCancelledLeavesTheDownloadToTheNext()
    {
        var release = new TaskCompletionSource();
        var handler = new FixtureHandler().Set("mvod.itunes.apple.com/a/cover_768x768_-.mp4", new Reply(200, Mp4(), Wait: release.Task));
        var cache = Cache(handler);
        using var skipped = new CancellationTokenSource();
        var first = cache.GetAsync(Video, skipped.Token);
        var second = cache.GetAsync(Video, TestContext.Current.CancellationToken);
        await skipped.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        release.SetResult();
        Assert.NotNull(await second);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task OnlyApplesHttpsMp4FilesAreFetched()
    {
        var handler = new FixtureHandler { Fallback = new Reply(200, Mp4()) };
        var cache = Cache(handler);
        Assert.Null(await cache.GetAsync(new Uri("http://mvod.itunes.apple.com/a/cover.mp4"), TestContext.Current.CancellationToken));
        Assert.Null(await cache.GetAsync(new Uri("https://example.com/a/cover.mp4"), TestContext.Current.CancellationToken));
        Assert.Null(await cache.GetAsync(new Uri("https://mvod.itunes.apple.com/a/cover.m3u8"), TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task WhatIsNotAnMp4OrTooLargeIsNotKept()
    {
        var page = new FixtureHandler { Fallback = Reply.Text(200, "<html>not a video</html>") };
        Assert.Null(await Cache(page).GetAsync(Video, TestContext.Current.CancellationToken));
        var large = new FixtureHandler { Fallback = new Reply(200, Mp4(200_000)) };
        Assert.Null(await Cache(large, maxFile: 100_000).GetAsync(Video, TestContext.Current.CancellationToken));
        var missing = new FixtureHandler { Fallback = Reply.Text(404, "") };
        Assert.Null(await Cache(missing).GetAsync(Video, TestContext.Current.CancellationToken));
        var broken = new FixtureHandler { Fallback = Reply.Broken };
        Assert.Null(await Cache(broken).GetAsync(Video, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_folder.Path));
    }

    [Fact]
    public void TheLeastRecentlyUsedCoversGoFirstPastTheLimit()
    {
        var cache = new MotionCoverCache(_folder.Path, new HttpClient(new FixtureHandler()), "Lunalyr", maxBytes: 10_000, maxFileBytes: 5_000);
        string Write(string name, int minutesAgo)
        {
            var path = Path.Combine(_folder.Path, name + ".mp4");
            File.WriteAllBytes(path, Mp4(4000));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-minutesAgo));
            return path;
        }
        var oldest = Write("a", 30);
        var older = Write("b", 20);
        var newer = Write("c", 10);
        var kept = Write("d", 40); // the oldest, but the one just fetched
        cache.Trim(keep: kept);
        Assert.False(File.Exists(oldest));
        Assert.False(File.Exists(older));
        Assert.True(File.Exists(newer));
        Assert.True(File.Exists(kept));
        Assert.True(cache.Size() <= 10_000);
    }
}
