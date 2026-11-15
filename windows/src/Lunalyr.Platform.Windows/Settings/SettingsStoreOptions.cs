using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Lunalyr.Core.Logging;

namespace Lunalyr.Platform.Settings;

/// <summary>
/// Upgrades a settings file from <paramref name="FromVersion"/> to <c>FromVersion + 1</c> by editing its JSON in
/// place (the <c>schemaVersion</c> field is already removed). Throwing marks the file as unusable; it is kept.
/// </summary>
public sealed record SettingsMigration(int FromVersion, Action<JsonObject> Apply);

/// <summary>Configuration for <see cref="SettingsStore{T}"/>.</summary>
/// <typeparam name="T">An immutable settings type (a record with init properties and defaults works best).</typeparam>
public sealed class SettingsStoreOptions<T>
    where T : class
{
    /// <summary>Folder holding the file. The app passes <c>%LOCALAPPDATA%\Lunalyr</c>; tests pass a temp folder.</summary>
    public required string Folder { get; init; }

    /// <summary>File stem: the store writes <c>{Name}.json</c>, <c>{Name}.json.bak</c> and <c>{Name}.json.tmp</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Source-generated type info (from a <c>JsonSerializerContext</c>), so the store is Native AOT safe.</summary>
    public required JsonTypeInfo<T> TypeInfo { get; init; }

    /// <summary>Creates the default settings, used on first run or when nothing on disk can be read.</summary>
    public required Func<T> CreateDefaults { get; init; }

    /// <summary>The schema version this build writes. Files with a lower version are migrated on load.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>
    /// Migrations, one per version step. They must be contiguous and end at <c>SchemaVersion - 1</c>; older steps can
    /// be dropped, which makes files older than the first step unreadable (they fall back like corrupt files).
    /// </summary>
    public IReadOnlyList<SettingsMigration> Migrations { get; init; } = [];

    /// <summary>Quiet period before a change is written; bursts of changes are coalesced. Default 300 ms.</summary>
    public TimeSpan SaveDelay { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Longest a change waits while changes keep arriving (e.g. a slider drag). Default 2 s.</summary>
    public TimeSpan MaxSaveDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Clock for debouncing and for the timestamps in preserved-file names.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Where recoveries and save failures are logged (category <c>settings</c>).</summary>
    public Logger Logger { get; init; } = Logger.Null;

    /// <summary>How many <c>.corrupt-*</c> copies to keep per store; older ones are deleted.</summary>
    public int MaxCorruptCopies { get; init; } = 5;

    /// <summary>Test hook: runs after the temp file is flushed and before it replaces the real file.</summary>
    internal Action<string>? BeforeCommit { get; init; }

    /// <summary>Pause between attempts when a file is briefly locked (antivirus, indexer).</summary>
    internal TimeSpan IoRetryDelay { get; init; } = TimeSpan.FromMilliseconds(25);
}
