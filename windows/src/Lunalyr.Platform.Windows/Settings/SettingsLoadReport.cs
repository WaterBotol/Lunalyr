namespace Lunalyr.Platform.Settings;

/// <summary>Where the loaded settings came from.</summary>
public enum SettingsSource
{
    /// <summary>Defaults: first run, or nothing on disk could be read (see <see cref="SettingsLoadReport.Problems"/>).</summary>
    Defaults,

    /// <summary>The main <c>{name}.json</c> file.</summary>
    MainFile,

    /// <summary>The <c>{name}.json.bak</c> copy, because the main file was missing or unusable.</summary>
    BackupFile,
}

public enum SettingsProblemKind
{
    /// <summary>The main file was missing but a backup existed (an interrupted save).</summary>
    Missing,

    /// <summary>The file exists but could not be opened (locked, access denied). It is left in place.</summary>
    Unreadable,

    /// <summary>Not valid JSON, not an object, a bad <c>schemaVersion</c>, or values of the wrong type.</summary>
    Corrupt,

    /// <summary>The file is older than the oldest migration, or a migration threw.</summary>
    MigrationFailed,

    /// <summary>Written by a newer app version. It was loaded as far as possible and a copy was kept.</summary>
    NewerSchemaVersion,
}

/// <summary>Something that went wrong with one file during load. <paramref name="PreservedAs"/> is where it was kept.</summary>
public sealed record SettingsProblem(string FilePath, SettingsProblemKind Kind, string Detail, string? PreservedAs);

/// <summary>What happened when a store was opened. Show <see cref="UserMessage"/> calmly when it is not null.</summary>
public sealed record SettingsLoadReport(
    string StoreName,
    SettingsSource Source,
    int? FileSchemaVersion,
    bool Migrated,
    IReadOnlyList<SettingsProblem> Problems)
{
    public bool HasProblems => Problems.Count > 0;

    /// <summary>A short, calm sentence for the UI, or null when there is nothing to tell the user.</summary>
    public string? UserMessage
    {
        get
        {
            var damaged = Problems
                .Where(p => p.Kind is SettingsProblemKind.Corrupt or SettingsProblemKind.MigrationFailed && p.PreservedAs is not null)
                .Select(p => Path.GetFileName(p.PreservedAs!))
                .ToList();
            var kept = damaged.Count switch
            {
                0 => string.Empty,
                1 => $" The unreadable file was kept as {damaged[0]}.",
                _ => $" The unreadable files were kept as {string.Join(" and ", damaged)}.",
            };
            switch (Source)
            {
                case SettingsSource.BackupFile:
                    return $"Lunalyr couldn't read your {StoreName} file, so it restored the previous copy.{kept}";
                case SettingsSource.Defaults when HasProblems:
                    return $"Lunalyr couldn't read your {StoreName} file or its backup, so it started with default settings.{kept}";
            }
            var newer = Problems.FirstOrDefault(p => p.Kind == SettingsProblemKind.NewerSchemaVersion);
            if (newer is null)
            {
                return null;
            }
            var copy = newer.PreservedAs is null ? string.Empty : $" A copy was kept as {Path.GetFileName(newer.PreservedAs)}.";
            return $"Your {StoreName} were saved by a newer version of Lunalyr; options this version doesn't know may be reset.{copy}";
        }
    }
}
