namespace Lunalyr.Install;

/// <summary>
/// Where an installed Lunalyr lives, for one Windows user (no admin rights):
/// <code>
/// %LOCALAPPDATA%\Programs\Lunalyr\
///     Lunalyr Setup.exe      the installer, also the uninstaller
///     app\Lunalyr.exe        the app and its files, swapped whole on an update
/// </code>
/// The app's data (settings, caches, logs) is in <c>%LOCALAPPDATA%\Lunalyr</c>, a different folder the installer never
/// writes or removes.
/// </summary>
public sealed record InstallLayout
{
    public InstallLayout(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    public string Root { get; }

    public string AppDir => Path.Combine(Root, "app");

    public string AppExe => Path.Combine(AppDir, Product.ExeName);

    public string SetupExe => Path.Combine(Root, Product.SetupName);

    /// <summary>The new version is unpacked here, then swapped in.</summary>
    public string StagingDir => Path.Combine(Root, "app.new");

    /// <summary>The previous version waits here while the new one is swapped in, then is removed.</summary>
    public string PreviousDir => Path.Combine(Root, "app.old");

    /// <summary><c>%LOCALAPPDATA%\Programs\Lunalyr</c>.</summary>
    public static InstallLayout Default() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Product.Name));

    /// <summary>
    /// The layout an app at <paramref name="exePath"/> was installed in, or null when it runs from anywhere else (a test
    /// build folder, a build output).
    /// </summary>
    public static InstallLayout? Detect(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        var full = Path.GetFullPath(exePath);
        var appDir = Path.GetDirectoryName(full);
        if (appDir is null || !string.Equals(Path.GetFileName(appDir), "app", StringComparison.OrdinalIgnoreCase)) return null;
        if (!string.Equals(Path.GetFileName(full), Product.ExeName, StringComparison.OrdinalIgnoreCase)) return null;
        var root = Path.GetDirectoryName(appDir);
        if (root is null) return null;
        var layout = new InstallLayout(root);
        return File.Exists(layout.SetupExe) ? layout : null;
    }

    /// <summary>Whether <paramref name="path"/> is inside this install (the app, the setup or a staging folder).</summary>
    public bool Contains(string? path) => IsInside(path, Root);

    /// <summary>
    /// Why this folder can't be an install root, or null. It must not be (or hold, or sit inside) the data folder, a
    /// drive root, or a system folder, because uninstalling removes it whole.
    /// </summary>
    public string? Problem(string dataRoot)
    {
        var data = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        if (Path.GetPathRoot(Root) is { } driveRoot && string.Equals(Path.TrimEndingDirectorySeparator(driveRoot), Root, StringComparison.OrdinalIgnoreCase))
            return $"{Root} is a drive root.";
        if (IsInside(data, Root) || IsInside(Root, data))
            return $"{Root} would share Lunalyr's data folder ({data}), which the installer never touches.";
        foreach (var folder in new[]
        {
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments,
        })
        {
            var special = Environment.GetFolderPath(folder);
            if (special.Length > 0 && string.Equals(Path.TrimEndingDirectorySeparator(special), Root, StringComparison.OrdinalIgnoreCase))
                return $"{Root} is a Windows folder, not one of Lunalyr's own.";
        }
        return null;
    }

    internal static bool IsInside(string? path, string folder)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
