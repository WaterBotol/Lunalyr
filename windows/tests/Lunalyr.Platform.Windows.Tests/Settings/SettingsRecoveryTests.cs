using System.Text.Json.Nodes;
using Lunalyr.Platform.Settings;

namespace Lunalyr.Platform.Windows.Tests.Settings;

public class SettingsRecoveryTests
{
    private static int CounterIn(string path) => (int)JsonNode.Parse(File.ReadAllText(path))!["counter"]!;

    /// <summary>Leaves settings.json at counter 2 and settings.json.bak at counter 1.</summary>
    private static async Task WriteTwoGenerationsAsync(TempFolder temp)
    {
        await using var store = SettingsStore<SampleSettings>.Open(SampleStore.Options(temp));
        store.Set(new SampleSettings { Counter = 1 });
        await store.FlushAsync();
        store.Set(new SampleSettings { Counter = 2 });
        await store.FlushAsync();
    }

    [Fact]
    public async Task ACrashBetweenTempWriteAndReplaceLeavesTheOldFileValid()
    {
        using var temp = new TempFolder();
        await SampleStore.WriteAsync(temp, new SampleSettings { Counter = 1 });

        var options = SampleStore.Options(temp);
        await using (var crashing = SettingsStore<SampleSettings>.Open(new SettingsStoreOptions<SampleSettings>
        {
            Folder = options.Folder,
            Name = options.Name,
            TypeInfo = options.TypeInfo,
            CreateDefaults = options.CreateDefaults,
            BeforeCommit = _ => throw new IOException("simulated power loss"),
        }))
        {
            crashing.Set(new SampleSettings { Counter = 2 });
            Assert.False(await crashing.FlushAsync());

            // The new content only exists in the flushed temp file; the real file is untouched.
            Assert.Equal(2, CounterIn(temp.File("settings.json.tmp")));
            Assert.Equal(1, CounterIn(temp.File("settings.json")));
        }

        await using var reopened = SettingsStore<SampleSettings>.Open(SampleStore.Options(temp));
        Assert.Equal(1, reopened.Current.Counter);
        Assert.Equal(SettingsSource.MainFile, reopened.LoadReport.Source);
        Assert.False(reopened.LoadReport.HasProblems);
        Assert.DoesNotContain("settings.json.tmp", temp.FileNames());
    }

    [Fact]
    public async Task AGarbageTempFileFromAnEarlierCrashIsIgnored()
    {
        using var temp = new TempFolder();
        await SampleStore.WriteAsync(temp, new SampleSettings { Counter = 4 });
        File.WriteAllText(temp.File("settings.json.tmp"), "{\"count");

        await using var store = SettingsStore<SampleSettings>.Open(SampleStore.Options(temp));

        Assert.Equal(4, store.Current.Counter);
        Assert.False(store.LoadReport.HasProblems);
    }

    [Fact]
    public async Task ATruncatedMainFileFallsBackToBakAndKeepsTheGoodBackup()
    {
        using var temp = new TempFolder();
        await WriteTwoGenerationsAsync(temp);
        var main = temp.File("settings.json");
        var truncated = File.ReadAllBytes(main)[..10];
        File.WriteAllBytes(main, truncated);

        await using var store = SettingsStore<SampleSettings>.Open(SampleStore.Options(temp));

        Assert.Equal(1, store.Current.Counter);
        Assert.Equal(SettingsSource.BackupFile, store.LoadReport.Source);
        var problem = Assert.Single(store.LoadReport.Problems);
        Assert.Equal(SettingsProblemKind.Corrupt, problem.Kind);
        Assert.Equal(main, problem.FilePath);
        Assert.NotNull(problem.PreservedAs);
        Assert.Matches(@"settings\.json\.corrupt-\d{8}T\d{6}Z$", problem.PreservedAs);
        Assert.Equal(truncated, File.ReadAllBytes(problem.PreservedAs));
        Assert.Contains("restored the previous copy", store.LoadReport.UserMessage);
        Assert.Contains(Path.GetFileName(problem.PreservedAs), store.LoadReport.UserMessage);

        // The recovered value is rewritten as the main file, and the good backup is not rotated away.
        Assert.True(store.HasUnsavedChanges);
        Assert.True(await store.FlushAsync());
        Assert.Equal(1, CounterIn(main));
        Assert.Equal(1, CounterIn(temp.File("settings.json.bak")));
        store.Set(new SampleSettings { Counter = 5 });
        await store.FlushAsync();
        Assert.Equal(1, CounterIn(temp.File("settings.json.bak")));
    }

    [Fact]
    public async Task AMissingMainFileWithABackupUsesTheBackup()
    {
        using var temp = new TempFolder();
        await WriteTwoGenerationsAsync(temp);
        File.Delete(temp.File("settings.json"));

        await using var store = SettingsStore<SampleSettings>.Open(SampleStore.Options(temp));

        Assert.Equal(1, store.Current.Counter);
        Assert.Equal(SettingsSource.BackupFile, store.LoadReport.Source);
        Assert.Equal(SettingsProblemKind.Missing, Assert.Single(store.LoadReport.Problems).Kind);
        Assert.NotNull(store.LoadReport.UserMessage);
    }

    [Fact]
    public async Task WhenBothFilesAreCorruptItUsesDefaultsKeepsBothAndSaysSo()
    {
        using var temp = new TempFolder();
        await WriteTwoGenerationsAsync(temp);
        File.WriteAllText(temp.File("settings.json"), "");
        File.WriteAllText(temp.File("settings.json.bak"), "{ \"counter\": ");

        await using var store = SettingsStore<SampleSettings>.Open(SampleStore.Options(temp));

        Assert.Equal(new SampleSettings(), store.Current);
        Assert.Equal(SettingsSource.Defaults, store.LoadReport.Source);
        Assert.Equal(2, store.LoadReport.Problems.Count);
        Assert.All(store.LoadReport.Problems, p => Assert.Equal(SettingsProblemKind.Corrupt, p.Kind));
        Assert.All(store.LoadReport.Problems, p => Assert.True(File.Exists(p.PreservedAs)));
        Assert.Contains(temp.FileNames(), f => f.StartsWith("settings.json.corrupt-", StringComparison.Ordinal));
        Assert.Contains(temp.FileNames(), f => f.StartsWith("settings.json.bak.corrupt-", StringComparison.Ordinal));
        Assert.Contains("default settings", store.LoadReport.UserMessage);
        Assert.False(store.HasUnsavedChanges); // Defaults aren't written until something changes.
    }

    [Theory]
    [InlineData("[1, 2]")]
    [InlineData("null")]
    [InlineData("\"text\"")]
    [InlineData("{\"fontSize\": \"big\"}")]
    [InlineData("{\"schemaVersion\": \"one\"}")]
    [InlineData("{\"schemaVersion\": 0}")]
    [InlineData("\0\0\0\0")]
    public async Task UnusableContentCountsAsCorrupt(string content)
    {
        using var temp = new TempFolder();
        File.WriteAllText(temp.File("settings.json"), content);

        await using var store = SettingsStore<SampleSettings>.Open(SampleStore.Options(temp));

        Assert.Equal(SettingsSource.Defaults, store.LoadReport.Source);
        Assert.Equal(SettingsProblemKind.Corrupt, Assert.Single(store.LoadReport.Problems).Kind);
        Assert.False(File.Exists(temp.File("settings.json")));
    }

    [Fact]
    public async Task HandEditsWithABomCommentsAndTrailingCommasStillLoad()
    {
        using var temp = new TempFolder();
        File.WriteAllText(temp.File("settings.json"), "{ // edited by hand\n \"schemaVersion\": 1, \"fontSize\": 44, }", new System.Text.UTF8Encoding(true));

        await using var store = SettingsStore<SampleSettings>.Open(SampleStore.Options(temp));

        Assert.Equal(44, store.Current.FontSize);
        Assert.False(store.LoadReport.HasProblems);
    }

    [Fact]
    public async Task ALockedFileIsReportedAsUnreadableAndLeftInPlace()
    {
        using var temp = new TempFolder();
        await WriteTwoGenerationsAsync(temp);
        var main = temp.File("settings.json");

        await using (var store = await OpenWhileLockedAsync(main, temp))
        {
            Assert.Equal(1, store.Current.Counter);
            Assert.Equal(SettingsSource.BackupFile, store.LoadReport.Source);
            var problem = Assert.Single(store.LoadReport.Problems);
            Assert.Equal(SettingsProblemKind.Unreadable, problem.Kind);
            Assert.Null(problem.PreservedAs);
            Assert.False(store.HasUnsavedChanges);

            // The possibly-newer main file is not overwritten just because it was locked for a moment...
            Assert.Equal(2, CounterIn(main));
            Assert.Equal(1, CounterIn(temp.File("settings.json.bak")));
            Assert.DoesNotContain(temp.FileNames(), f => f.Contains("corrupt", StringComparison.Ordinal));

            // ...and once the user changes settings, it is copied aside before saves rotate it away.
            store.Set(new SampleSettings { Counter = 7 });
            Assert.True(await store.FlushAsync());
            store.Set(new SampleSettings { Counter = 8 });
            Assert.True(await store.FlushAsync());
        }
        var kept = Assert.Single(temp.FileNames(), f => f.StartsWith("settings.json.unread-", StringComparison.Ordinal));
        Assert.Equal(2, CounterIn(temp.File(kept)));
        Assert.Equal(8, CounterIn(main));

        static Task<SettingsStore<SampleSettings>> OpenWhileLockedAsync(string path, TempFolder temp)
        {
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return Task.FromResult(SettingsStore<SampleSettings>.Open(SampleStore.Options(temp)));
        }
    }

    [Fact]
    public async Task CorruptCopiesGetDistinctNamesWithinOneSecondAndArePruned()
    {
        using var temp = new TempFolder();
        var time = new ManualTimeProvider();
        var options = SampleStore.Options(temp, time);
        var kept = new List<string>();
        for (var i = 0; i < 7; i++)
        {
            File.WriteAllText(temp.File("settings.json"), "{ broken " + i);
            await using var store = SettingsStore<SampleSettings>.Open(options);
            kept.Add(Path.GetFileName(store.LoadReport.Problems[0].PreservedAs!));
            if (i == 3)
            {
                time.Advance(TimeSpan.FromSeconds(1));
            }
        }

        Assert.Equal(7, kept.Distinct().Count());
        Assert.Contains("settings.json.corrupt-20261112T090000Z-2", kept);
        var remaining = temp.FileNames().Where(f => f.Contains(".corrupt-", StringComparison.Ordinal)).ToList();
        Assert.Equal(5, remaining.Count);
        Assert.Contains("settings.json.corrupt-20261112T090001Z-3", remaining);
        Assert.DoesNotContain("settings.json.corrupt-20261112T090000Z", remaining);
    }
}
