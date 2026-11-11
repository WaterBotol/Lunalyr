using System.Text.Json.Nodes;
using Lunalyr.Platform.Settings;

namespace Lunalyr.Platform.Windows.Tests.Settings;

public class SettingsStoreTests
{
    [Fact]
    public async Task FirstRunUsesDefaultsWithoutProblemsOrFiles()
    {
        using var temp = new TempFolder();
        await using var store = SettingsStore<SampleSettings>.Open(SampleStore.Options(temp));

        Assert.Equal(new SampleSettings(), store.Current);
        Assert.Equal(SettingsSource.Defaults, store.LoadReport.Source);
        Assert.False(store.LoadReport.HasProblems);
        Assert.Null(store.LoadReport.UserMessage);
        Assert.Empty(temp.FileNames());
    }

    [Fact]
    public async Task RoundTripsThroughDisk()
    {
        using var temp = new TempFolder();
        var value = new SampleSettings { Theme = "dark", FontSize = 40, ShowTranslation = false, Counter = 7 };

        await SampleStore.WriteAsync(temp, value);
        await using var reopened = SettingsStore<SampleSettings>.Open(SampleStore.Options(temp));

        Assert.Equal(value, reopened.Current);
        Assert.Equal(SettingsSource.MainFile, reopened.LoadReport.Source);
        Assert.Equal(1, reopened.LoadReport.FileSchemaVersion);
        Assert.False(reopened.LoadReport.HasProblems);
    }

    [Fact]
    public async Task KeysMissingFromTheFileGetTheirDefaultsAtAnyDepth()
    {
        using var temp = new TempFolder();
        File.WriteAllText(temp.File("settings.json"), """{ "schemaVersion": 1, "fontSize": 40, "window": { "width": 1024 }, "removedOption": 1 }""");

        await using var store = SettingsStore<SampleSettings>.Open(SampleStore.Options(temp));

        Assert.Equal(new SampleSettings { FontSize = 40, Window = new SampleWindow { Width = 1024 } }, store.Current);
        Assert.True(store.Current.ShowTranslation);
        Assert.Equal(600, store.Current.Window.Height);
        Assert.False(store.LoadReport.HasProblems);
    }

    [Fact]
    public async Task WritesReadableJsonWithSchemaVersionFirst()
    {
        using var temp = new TempFolder();
        await SampleStore.WriteAsync(temp, new SampleSettings { Theme = "月光" });

        var text = File.ReadAllText(temp.File("settings.json"));
        var obj = JsonNode.Parse(text)!.AsObject();
        Assert.Equal("schemaVersion", obj.First().Key);
        Assert.Equal(1, (int)obj["schemaVersion"]!);
        Assert.Equal("月光", (string)obj["theme"]!);
        Assert.Contains("月光", text); // Not \u-escaped.
        Assert.Equal(["settings.json"], temp.FileNames());
    }

    [Fact]
    public async Task EachSaveKeepsThePreviousFileAsBak()
    {
        using var temp = new TempFolder();
        await using var store = SettingsStore<SampleSettings>.Open(SampleStore.Options(temp));
        store.Update(s => s with { Counter = 1 });
        await store.FlushAsync();
        store.Update(s => s with { Counter = 2 });
        await store.FlushAsync();

        Assert.Equal(["settings.json", "settings.json.bak"], temp.FileNames());
        Assert.Equal(1, (int)JsonNode.Parse(File.ReadAllText(temp.File("settings.json.bak")))!["counter"]!);
        Assert.Equal(2, (int)JsonNode.Parse(File.ReadAllText(temp.File("settings.json")))!["counter"]!);
    }

    [Fact]
    public async Task ChangedIsRaisedWithOldAndNewOnlyForRealChanges()
    {
        using var temp = new TempFolder();
        await using var store = SettingsStore<SampleSettings>.Open(SampleStore.Options(temp));
        var events = new List<SettingsChangedEventArgs<SampleSettings>>();
        store.Changed += (_, e) => events.Add(e);

        Assert.True(store.Update(s => s with { FontSize = 50 }));
        Assert.False(store.Update(s => s with { FontSize = 50 }));
        Assert.False(store.Set(new SampleSettings { FontSize = 50 }));

        var change = Assert.Single(events);
        Assert.Equal(32, change.OldValue.FontSize);
        Assert.Equal(50, change.NewValue.FontSize);
        Assert.Same(store.Current, change.NewValue);
    }

    [Fact]
    public async Task ASaveFailureIsReportedKeptPendingAndRetried()
    {
        using var temp = new TempFolder();
        await using var store = SettingsStore<SampleSettings>.Open(SampleStore.Options(temp));
        var failures = new List<SettingsSaveFailedEventArgs>();
        store.SaveFailed += (_, e) => failures.Add(e);

        Directory.CreateDirectory(temp.File("settings.json.tmp")); // The temp file can't be created.
        store.Update(s => s with { Counter = 3 });

        Assert.False(await store.FlushAsync());
        Assert.Single(failures);
        Assert.NotNull(store.LastSaveError);
        Assert.True(store.HasUnsavedChanges);

        Directory.Delete(temp.File("settings.json.tmp"));
        Assert.True(await store.FlushAsync());
        Assert.Null(store.LastSaveError);
        Assert.False(store.HasUnsavedChanges);
        Assert.Contains("\"counter\": 3", File.ReadAllText(temp.File("settings.json")));
    }

    [Fact]
    public async Task DisposeFlushesAndLaterUpdatesThrow()
    {
        using var temp = new TempFolder();
        var store = SettingsStore<SampleSettings>.Open(SampleStore.Options(temp));
        store.Update(s => s with { Counter = 9 });

        await store.DisposeAsync();

        Assert.Contains("\"counter\": 9", File.ReadAllText(temp.File("settings.json")));
        Assert.Throws<ObjectDisposedException>(() => store.Update(s => s with { Counter = 10 }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a.b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    public void RejectsNamesThatAreNotPlainFileStems(string name)
    {
        using var temp = new TempFolder();
        var options = SampleStore.Options(temp);
        Assert.ThrowsAny<ArgumentException>(() => SettingsStore<SampleSettings>.Open(new SettingsStoreOptions<SampleSettings>
        {
            Folder = options.Folder,
            Name = name,
            TypeInfo = options.TypeInfo,
            CreateDefaults = options.CreateDefaults,
        }));
    }
}
