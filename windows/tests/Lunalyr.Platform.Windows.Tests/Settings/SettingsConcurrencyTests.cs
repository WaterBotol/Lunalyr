using Lunalyr.Platform.Settings;

namespace Lunalyr.Platform.Windows.Tests.Settings;

public class SettingsConcurrencyTests
{
    [Fact]
    public async Task ConcurrentWritersNeverLoseAnUpdateAndTheLastValueReachesDisk()
    {
        using var temp = new TempFolder();
        var options = SampleStore.Options(temp, TimeProvider.System);
        var fast = new SettingsStoreOptions<SampleSettings>
        {
            Folder = options.Folder,
            Name = options.Name,
            TypeInfo = options.TypeInfo,
            CreateDefaults = options.CreateDefaults,
            SaveDelay = TimeSpan.FromMilliseconds(1),
            MaxSaveDelay = TimeSpan.FromMilliseconds(5),
            IoRetryDelay = TimeSpan.FromMilliseconds(1),
        };
        const int writers = 8;
        const int updatesEach = 500;
        var changes = 0;
        var failures = 0;

        await using (var store = SettingsStore<SampleSettings>.Open(fast))
        {
            store.Changed += (_, _) => Interlocked.Increment(ref changes);
            store.SaveFailed += (_, _) => Interlocked.Increment(ref failures);
            var tasks = Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
            {
                for (var i = 0; i < updatesEach; i++)
                {
                    store.Update(s => s with { Counter = s.Counter + 1 });
                    _ = store.Current.Counter; // Readers run alongside writers.
                    if (i % 100 == 0)
                    {
                        await store.FlushAsync();
                    }
                }
            })).ToArray();
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

            Assert.True(await store.FlushAsync());
            Assert.Equal(writers * updatesEach, store.Current.Counter);
        }

        Assert.Equal(writers * updatesEach, changes);
        Assert.Equal(0, failures);
        await using var reopened = SettingsStore<SampleSettings>.Open(SampleStore.Options(temp));
        Assert.Equal(writers * updatesEach, reopened.Current.Counter);
        Assert.False(reopened.LoadReport.HasProblems);
        Assert.DoesNotContain("settings.json.tmp", temp.FileNames());
    }
}
