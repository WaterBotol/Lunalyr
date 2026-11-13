namespace Lunalyr.Install;

/// <summary>How an uninstall ended. <see cref="Leftovers"/> lists what couldn't be removed (a file in use).</summary>
public sealed record UninstallResult(string? Error, IReadOnlyList<string> Leftovers)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// Removes an install: asks a running copy from it to quit, removes the registry entries and the shortcut that still
/// point at it, then its folders. Lunalyr's data folder (settings, caches, logs) is never touched; the installer says
/// where it is.
/// </summary>
public static class Uninstaller
{
    /// <param name="runningSetup">The setup program doing this, when it runs from inside the install (it can't delete itself).</param>
    public static UninstallResult Uninstall(InstallTarget target, InstallServices services, string? runningSetup = null)
    {
        var layout = target.Layout;
        var log = services.Log;
        if (layout.Problem(target.DataRoot) is { } problem) return new(problem, []);
        log($"Removing {Product.Name} from {layout.Root}.");

        foreach (var copy in services.Copies.Find().Where(c => layout.Contains(c.ExePath)))
        {
            log($"Asking the running Lunalyr ({copy.ProcessId}) to quit.");
            if (!services.Copies.Quit(copy, services.QuitTimeout))
                return new("Lunalyr is still running and didn’t quit when asked. Quit it from its tray icon, then try again. Nothing was removed.", []);
        }

        try
        {
            LaunchAtLogin.Remove(services.Registry, layout);
            InstallRegistry.Remove(services.Registry, layout);
            services.AssociationsChanged();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            log($"Couldn’t remove a registry entry: {ex.Message}");
        }
        if (services.Shortcuts.Target(target.Shortcut) is { } shortcut && layout.Contains(shortcut))
            services.Shortcuts.Delete(target.Shortcut);

        var leftovers = new List<string>();
        foreach (var folder in new[] { layout.AppDir, layout.StagingDir, layout.PreviousDir })
        {
            Installer.TryDeleteFolder(folder, log);
            if (Directory.Exists(folder)) leftovers.Add(folder);
        }
        var running = runningSetup is null ? null : Path.GetFullPath(runningSetup);
        foreach (var file in new[] { layout.SetupExe, layout.SetupExe + ".new" })
        {
            if (running is not null && string.Equals(Path.GetFullPath(file), running, StringComparison.OrdinalIgnoreCase)) continue;
            Installer.TryDeleteFile(file, log);
            if (File.Exists(file)) leftovers.Add(file);
        }
        try
        {
            if (Directory.Exists(layout.Root) && !Directory.EnumerateFileSystemEntries(layout.Root).Any()) Directory.Delete(layout.Root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"Couldn’t remove {layout.Root}: {ex.Message}");
        }
        log(leftovers.Count == 0 ? $"Removed {Product.Name}." : $"Removed {Product.Name}; left {string.Join(", ", leftovers)}.");
        return new(null, leftovers);
    }
}
