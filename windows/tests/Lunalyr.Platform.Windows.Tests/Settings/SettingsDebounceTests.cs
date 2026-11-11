using System.Text.Json.Nodes;
using Lunalyr.Platform.Settings;

namespace Lunalyr.Platform.Windows.Tests.Settings;

public class SettingsDebounceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static int SavedCounter(TempFolder temp) =>
        (int)JsonNode.Parse(File.ReadAllText(temp.File("settings.json")))!["counter"]!;

    [Fact]
    public async Task ABurstOfChangesIsWrittenOnceAfterTheQuietPeriod()
    {
        using var temp = new TempFolder();
        var time = new ManualTimeProvider();
        await using var store = SettingsStore<SampleSettings>.Open(SampleStore.Options(temp, time));
        var saved = SampleStore.WaitForSaveAsync(store);

        for (var i = 1; i <= 20; i++)
        {
            store.Update(s => s with { Counter = s.Counter + 1 });
        }
        time.Advance(TimeSpan.FromMilliseconds(299));

        Assert.Equal(0, store.SaveCount);
        Assert.False(File.Exists(temp.File("settings.json")));

        time.Advance(TimeSpan.FromMilliseconds(1));
        await saved.WaitAsync(Wait, TestContext.Current.CancellationToken);

        Assert.Equal(1, store.SaveCount);
        Assert.Equal(20, SavedCounter(temp));
        Assert.False(store.HasUnsavedChanges);
    }

    [Fact]
    public async Task EachChangeRestartsTheQuietPeriod()
    {
        using var temp = new TempFolder();
        var time = new ManualTimeProvider();
        await using var store = SettingsStore<SampleSettings>.Open(SampleStore.Options(temp, time));
        var saved = SampleStore.WaitForSaveAsync(store);

        store.Update(s => s with { Counter = 1 });
        time.Advance(TimeSpan.FromMilliseconds(200));
        store.Update(s => s with { Counter = 2 });
        time.Advance(TimeSpan.FromMilliseconds(200)); // 400 ms after the first change, 200 after the last.

        Assert.Equal(0, store.SaveCount);

        time.Advance(TimeSpan.FromMilliseconds(100));
        await saved.WaitAsync(Wait, TestContext.Current.CancellationToken);
        Assert.Equal(2, SavedCounter(temp));
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task ContinuousChangesAreStillWrittenWithinTheMaximumDelay()
    {
        using var temp = new TempFolder();
        var time = new ManualTimeProvider();
        await using var store = SettingsStore<SampleSettings>.Open(SampleStore.Options(temp, time));
        var saved = SampleStore.WaitForSaveAsync(store);

        // A slider drag: a change every 100 ms, never a 300 ms pause.
        for (var i = 1; i <= 20; i++)
        {
            store.Update(s => s with { Counter = i });
            time.Advance(TimeSpan.FromMilliseconds(i < 20 ? 100 : 0));
        }
        Assert.Equal(0, store.SaveCount); // 1900 ms in.

        time.Advance(TimeSpan.FromMilliseconds(100)); // 2000 ms after the first change.
        await saved.WaitAsync(Wait, TestContext.Current.CancellationToken);
        Assert.Equal(1, store.SaveCount);
        Assert.Equal(20, SavedCounter(temp));
    }

    [Fact]
    public async Task FlushWritesImmediatelyAndThePendingTimerThenHasNothingToDo()
    {
        using var temp = new TempFolder();
        var time = new ManualTimeProvider();
        await using var store = SettingsStore<SampleSettings>.Open(SampleStore.Options(temp, time));

        store.Update(s => s with { Counter = 3 });
        Assert.True(await store.FlushAsync());
        Assert.Equal(1, store.SaveCount);
        Assert.Equal(3, SavedCounter(temp));

        time.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(20, TestContext.Current.CancellationToken); // Let a (wrongly) scheduled save run if there were one.
        Assert.Equal(1, store.SaveCount);
    }
}
