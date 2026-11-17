using Lunalyr.Core.Logging;
using Lunalyr.Platform.Settings;

namespace Lunalyr.Shell.Settings;

/// <summary>Opens the app's settings file, <c>{dataRoot}\settings.json</c>.</summary>
public static class AppSettingsStore
{
    public const string FileName = "settings";

    /// <summary>Loads (recovering or migrating as needed) and normalises the settings in <paramref name="dataRoot"/>.</summary>
    public static SettingsStore<AppSettings> Open(string dataRoot, Logger logger, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataRoot);
        var store = SettingsStore<AppSettings>.Open(new SettingsStoreOptions<AppSettings>
        {
            Folder = dataRoot,
            Name = FileName,
            TypeInfo = AppSettingsJsonContext.Default.AppSettings,
            // A new install (no file yet) starts with the owner's look (ShippedDefaults); an existing file keeps its own values.
            CreateDefaults = ShippedDefaults.ForNewInstall,
            // A key an existing file lacks keeps the default it had (the record's own), never the new install's: the file
            // is laid over these, maps included, so a stored empty map must not pick up the new install's entries.
            MissingKeyDefaults = () => new AppSettings(),
            SchemaVersion = AppSettings.SchemaVersion,
            Logger = logger,
            TimeProvider = time ?? TimeProvider.System,
        });

        // Values a hand-edited or newer file got wrong are repaired once, here, so everything after can trust them.
        var normalized = store.Current.Normalized();
        if (!SameValues(normalized, store.Current))
        {
            logger.Settings.Warn("Some settings were out of range or unknown and were reset to their defaults.");
            store.Set(normalized);
        }

        return store;
    }

    // Records compare collections by reference, so compare the JSON instead.
    private static bool SameValues(AppSettings a, AppSettings b) =>
        System.Text.Json.JsonSerializer.Serialize(a, AppSettingsJsonContext.Default.AppSettings)
        == System.Text.Json.JsonSerializer.Serialize(b, AppSettingsJsonContext.Default.AppSettings);
}
