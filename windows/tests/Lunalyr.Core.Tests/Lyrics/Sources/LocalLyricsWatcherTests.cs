using Lunalyr.Core.Lyrics.Sources;

namespace Lunalyr.Core.Tests.Lyrics.Sources;

public class LocalLyricsWatcherTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ABurstOfChangesCallsBackOnce()
    {
        using var folder = new TempFolder();
        using var watcher = new LocalLyricsWatcher(TimeSpan.FromMilliseconds(150));
        var calls = 0;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Watch(folder.Path, () =>
        {
            Interlocked.Increment(ref calls);
            first.TrySetResult();
        });
        for (var i = 0; i < 5; i++)
        {
            watcher.Signal();
        }
        await first.Task.WaitAsync(Generous, TestContext.Current.CancellationToken);
        await Task.Delay(400, TestContext.Current.CancellationToken);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task WritingAnExportIsNoticed()
    {
        using var folder = new TempFolder();
        using var watcher = new LocalLyricsWatcher(TimeSpan.FromMilliseconds(100));
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Watch(folder.Path, () => changed.TrySetResult());
        Assert.Equal(folder.Path, watcher.Path);
        folder.Write("Singer - Sample.lrc", "[ti:Sample]\n[00:01.00]Hello");
        await changed.Task.WaitAsync(Generous, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StoppingOrDisposingSilencesTheWatcher()
    {
        using var folder = new TempFolder();
        var calls = 0;
        var watcher = new LocalLyricsWatcher(TimeSpan.FromMilliseconds(50));
        watcher.Watch(folder.Path, () => Interlocked.Increment(ref calls));
        watcher.Watch("", () => Interlocked.Increment(ref calls));
        Assert.Equal("", watcher.Path);
        folder.Write("a.lrc", "[00:01.00]x");
        watcher.Watch(folder.Path, () => Interlocked.Increment(ref calls));
        watcher.Signal();
        watcher.Dispose();
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(0, Volatile.Read(ref calls));
        Assert.Throws<ObjectDisposedException>(() => watcher.Watch(folder.Path, () => { }));
    }

    [Fact]
    public void AFolderThatCannotBeWatchedIsNotWatched()
    {
        using var folder = new TempFolder();
        using var watcher = new LocalLyricsWatcher();
        watcher.Watch(folder.File("missing"), () => { });
        Assert.Equal("", watcher.Path);
    }
}
