using System.Text.Json.Nodes;
using Lunalyr.Platform.Settings;

namespace Lunalyr.Platform.Windows.Tests.Settings;

public class SettingsMigrationTests
{
    // v1 stored "fontSizePt"; v2 renamed it to "fontSize"; v3 started counting from 100.
    private static readonly SettingsMigration RenameFontSize = new(1, json =>
    {
        if (json["fontSizePt"] is { } size)
        {
            json.Remove("fontSizePt");
            json["fontSize"] = size.GetValue<int>();
        }
    });

    private static readonly SettingsMigration OffsetCounter = new(2, json =>
        json["counter"] = (json["counter"]?.GetValue<int>() ?? 0) + 100);

    private static SettingsStoreOptions<SampleSettings> Options(TempFolder temp, int schemaVersion, params SettingsMigration[] migrations)
    {
        var o = SampleStore.Options(temp);
        return new SettingsStoreOptions<SampleSettings>
        {
            Folder = o.Folder,
            Name = o.Name,
            TypeInfo = o.TypeInfo,
            CreateDefaults = o.CreateDefaults,
            TimeProvider = o.TimeProvider,
            IoRetryDelay = TimeSpan.FromMilliseconds(1),
            SchemaVersion = schemaVersion,
            Migrations = migrations,
        };
    }

    [Fact]
    public async Task RunsMigrationsInOrderAndRewritesTheFileKeepingTheOldOneAsBak()
    {
        using var temp = new TempFolder();
        File.WriteAllText(temp.File("settings.json"), """{ "schemaVersion": 1, "fontSizePt": 20, "counter": 5, "theme": "dark" }""");

        await using var store = SettingsStore<SampleSettings>.Open(Options(temp, 3, OffsetCounter, RenameFontSize));

        Assert.Equal(new SampleSettings { FontSize = 20, Counter = 105, Theme = "dark" }, store.Current);
        Assert.True(store.LoadReport.Migrated);
        Assert.Equal(1, store.LoadReport.FileSchemaVersion);
        Assert.False(store.LoadReport.HasProblems);
        Assert.Null(store.LoadReport.UserMessage);

        Assert.True(await store.FlushAsync());
        var saved = JsonNode.Parse(File.ReadAllText(temp.File("settings.json")))!;
        Assert.Equal(3, (int)saved["schemaVersion"]!);
        Assert.Null(saved["fontSizePt"]);
        Assert.Equal(1, (int)JsonNode.Parse(File.ReadAllText(temp.File("settings.json.bak")))!["schemaVersion"]!);
    }

    [Fact]
    public async Task AFileWithoutSchemaVersionIsTreatedAsVersionOne()
    {
        using var temp = new TempFolder();
        File.WriteAllText(temp.File("settings.json"), """{ "fontSizePt": 18 }""");

        await using var store = SettingsStore<SampleSettings>.Open(Options(temp, 2, RenameFontSize));

        Assert.Equal(18, store.Current.FontSize);
        Assert.Equal(1, store.LoadReport.FileSchemaVersion);
        Assert.True(store.LoadReport.Migrated);
    }

    [Fact]
    public async Task AFailingMigrationKeepsTheFileAndFallsBack()
    {
        using var temp = new TempFolder();
        File.WriteAllText(temp.File("settings.json"), """{ "schemaVersion": 1, "counter": 5 }""");
        var throwing = new SettingsMigration(1, _ => throw new InvalidOperationException("bug in migration"));

        await using var store = SettingsStore<SampleSettings>.Open(Options(temp, 2, throwing));

        Assert.Equal(SettingsSource.Defaults, store.LoadReport.Source);
        var problem = Assert.Single(store.LoadReport.Problems);
        Assert.Equal(SettingsProblemKind.MigrationFailed, problem.Kind);
        Assert.Contains("bug in migration", problem.Detail);
        Assert.Contains("\"counter\": 5", File.ReadAllText(problem.PreservedAs!));
        Assert.NotNull(store.LoadReport.UserMessage);
    }

    [Fact]
    public async Task AFileOlderThanTheOldestMigrationCannotBeMigrated()
    {
        using var temp = new TempFolder();
        File.WriteAllText(temp.File("settings.json"), """{ "schemaVersion": 1 }""");

        await using var store = SettingsStore<SampleSettings>.Open(Options(temp, 3, OffsetCounter));

        Assert.Equal(SettingsProblemKind.MigrationFailed, Assert.Single(store.LoadReport.Problems).Kind);
    }

    [Fact]
    public async Task ANewerFileLoadsWhatItCanAndKeepsACopy()
    {
        using var temp = new TempFolder();
        var newer = """{ "schemaVersion": 7, "fontSize": 60, "futureOption": true }""";
        File.WriteAllText(temp.File("settings.json"), newer);

        await using var store = SettingsStore<SampleSettings>.Open(Options(temp, 2, RenameFontSize));

        Assert.Equal(60, store.Current.FontSize);
        Assert.Equal(SettingsSource.MainFile, store.LoadReport.Source);
        var problem = Assert.Single(store.LoadReport.Problems);
        Assert.Equal(SettingsProblemKind.NewerSchemaVersion, problem.Kind);
        Assert.Equal(temp.File("settings.json.schema-v7"), problem.PreservedAs);
        Assert.Equal(newer, File.ReadAllText(problem.PreservedAs!));
        Assert.Contains("newer version", store.LoadReport.UserMessage);
        Assert.False(store.HasUnsavedChanges); // Not rewritten until the user changes something.
    }

    [Theory]
    [InlineData(3, new[] { 1 })]        // Gap: nothing for 2 -> 3.
    [InlineData(3, new[] { 1, 3 })]     // Past the current version.
    [InlineData(3, new[] { 1, 1, 2 })]  // Duplicate.
    [InlineData(1, new[] { 1 })]        // Nothing to migrate to.
    [InlineData(3, new[] { 0, 1, 2 })]  // Versions start at 1.
    public void InvalidMigrationListsFailAtOpen(int schemaVersion, int[] fromVersions)
    {
        using var temp = new TempFolder();
        var migrations = fromVersions.Select(v => new SettingsMigration(v, _ => { })).ToArray();

        Assert.Throws<ArgumentException>(() => SettingsStore<SampleSettings>.Open(Options(temp, schemaVersion, migrations)));
    }
}
