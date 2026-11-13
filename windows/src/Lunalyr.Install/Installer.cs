using System.IO.Compression;

namespace Lunalyr.Install;

/// <summary>Where to install and what the installer may do.</summary>
/// <param name="Layout">The install folder.</param>
/// <param name="DataRoot">Lunalyr's data folder, which the install folder must stay clear of.</param>
/// <param name="StartMenuFolder">Where the shortcut goes (the real Start menu, or a temp folder for checks).</param>
public sealed record InstallTarget(InstallLayout Layout, string DataRoot, string StartMenuFolder)
{
    public string Shortcut => WindowsShortcuts.StartMenuShortcut(StartMenuFolder);
}

/// <summary>The outside world the installer changes. Tests pass fakes and temp folders.</summary>
public sealed class InstallServices
{
    public required IRegistryHive Registry { get; init; }
    public required IRunningCopies Copies { get; init; }
    public required IShortcuts Shortcuts { get; init; }
    public Func<DateTime> Today { get; init; } = () => DateTime.Today;
    public Action<string> Log { get; init; } = _ => { };

    /// <summary>Tells Explorer the <c>lunalyr://</c> registration changed.</summary>
    public Action AssociationsChanged { get; init; } = () => { };

    /// <summary>How long a running copy gets to quit after being asked.</summary>
    public TimeSpan QuitTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>A pause between attempts at a move a virus scanner may briefly block.</summary>
    public TimeSpan MoveRetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>How far an install has got, for the progress bar.</summary>
public sealed record InstallProgress(string Step, double Fraction);

/// <summary>How an install ended.</summary>
public sealed record InstallResult(string? Error, string? Version, IReadOnlyList<RunningCopy> OtherCopies)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// Installs (or updates) Lunalyr from an installer's payload. The new version is unpacked beside the old one and
/// checked before anything changes; a running copy in the install folder is asked to quit (never killed); the folders
/// are swapped, and only then are the registry entries and the shortcut written. A failure before the swap leaves the
/// old version exactly as it was; a failure during it puts the old version back.
/// </summary>
public static class Installer
{
    public static InstallResult Install(Stream installer, Payload.Located payload, InstallTarget target, InstallServices services,
                                        IProgress<InstallProgress>? progress = null, CancellationToken cancel = default)
    {
        var layout = target.Layout;
        var log = services.Log;
        var others = OtherCopies(services.Copies, layout);
        if (layout.Problem(target.DataRoot) is { } problem) return Fail(problem);

        progress?.Report(new("Checking the installer", 0.02));
        if (!Payload.Verify(installer, payload))
            return Fail("The installer is damaged (its contents don't match their checksum). Download it again.");

        string version;
        long unpackedBytes;
        try
        {
            using var zip = Payload.Open(installer, payload);
            var manifest = Payload.ReadManifest(zip);
            if (!string.Equals(manifest.Product, Product.Name, StringComparison.Ordinal))
                return Fail($"The installer holds “{manifest.Product}”, not {Product.Name}.");
            version = manifest.Version;
            unpackedBytes = manifest.UnpackedBytes;
            log($"Installing {Product.Name} {version} to {layout.Root}.");

            Directory.CreateDirectory(layout.Root);
            DeleteFolder(layout.StagingDir);
            Unpack(zip, layout, progress, cancel);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException)
        {
            TryDeleteFolder(layout.StagingDir, log);
            TryDeleteFile(layout.SetupExe + ".new", log);
            return Fail(ex is OperationCanceledException ? "Installation was cancelled. Nothing changed." : $"Couldn’t unpack Lunalyr: {ex.Message}");
        }

        if (!File.Exists(Path.Combine(layout.StagingDir, Product.ExeName)))
        {
            TryDeleteFolder(layout.StagingDir, log);
            return Fail($"The installer has no {Product.ExeName}.");
        }

        progress?.Report(new("Closing Lunalyr", 0.82));
        foreach (var copy in services.Copies.Find().Where(c => layout.Contains(c.ExePath)))
        {
            log($"Asking the running Lunalyr ({copy.ProcessId}) to quit.");
            if (!services.Copies.Quit(copy, services.QuitTimeout))
            {
                TryDeleteFolder(layout.StagingDir, log);
                TryDeleteFile(layout.SetupExe + ".new", log);
                return Fail("Lunalyr is still running and didn’t quit when asked. Quit it from its tray icon, then run Setup again. Nothing changed.");
            }
        }

        progress?.Report(new("Putting the new version in place", 0.88));
        try
        {
            Swap(layout, services);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteFolder(layout.StagingDir, log);
            TryDeleteFile(layout.SetupExe + ".new", log);
            return Fail($"Couldn’t replace the installed Lunalyr ({ex.Message}). The previous version is still installed.");
        }

        // The installer's own copy (also the uninstaller). If it is the file running now, the old one stays: same code.
        try
        {
            File.Move(layout.SetupExe + ".new", layout.SetupExe, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"Kept the previous {Product.SetupName}: {ex.Message}");
            TryDeleteFile(layout.SetupExe + ".new", log);
        }
        TryDeleteFolder(layout.PreviousDir, log);

        progress?.Report(new("Registering lunalyr:// links and the Start menu shortcut", 0.95));
        try
        {
            InstallRegistry.Write(services.Registry, layout, version, unpackedBytes, services.Today());
            LaunchAtLogin.Follow(services.Registry, layout);
            services.AssociationsChanged();
            services.Shortcuts.Create(target.Shortcut, layout.AppExe, "Lyrics on your desktop for the song you’re playing");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                                       or System.Runtime.InteropServices.COMException)
        {
            return new($"Lunalyr {version} is installed, but Windows didn’t take its Start menu entry or link registration: {ex.Message}", version, others);
        }

        progress?.Report(new("Done", 1));
        log($"Installed {Product.Name} {version}.");
        return new(null, version, others);

        InstallResult Fail(string error)
        {
            log(error);
            return new(error, null, others);
        }
    }

    /// <summary>Copies of Lunalyr running from anywhere but this install (a test build folder, say).</summary>
    public static IReadOnlyList<RunningCopy> OtherCopies(IRunningCopies copies, InstallLayout layout) =>
        copies.Find().Where(c => !layout.Contains(c.ExePath)).ToList();

    private static void Unpack(ZipArchive zip, InstallLayout layout, IProgress<InstallProgress>? progress, CancellationToken cancel)
    {
        var staging = Path.GetFullPath(layout.StagingDir) + Path.DirectorySeparatorChar;
        var entries = zip.Entries.ToList();
        var total = Math.Max(1, entries.Sum(e => e.Length));
        long done = 0;
        foreach (var entry in entries)
        {
            cancel.ThrowIfCancellationRequested();
            string destination;
            if (string.Equals(entry.FullName, Product.PayloadSetupEntry, StringComparison.Ordinal))
            {
                destination = layout.SetupExe + ".new";
            }
            else if (entry.FullName.StartsWith(Product.PayloadAppPrefix, StringComparison.Ordinal))
            {
                var relative = entry.FullName[Product.PayloadAppPrefix.Length..];
                if (relative.Length == 0 || relative.EndsWith('/')) continue;
                destination = Path.GetFullPath(Path.Combine(staging, relative));
                if (!destination.StartsWith(staging, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"The installer has a file outside the app folder ({entry.FullName}).");
            }
            else
            {
                continue; // The manifest.
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
            done += entry.Length;
            progress?.Report(new("Unpacking Lunalyr", 0.05 + 0.75 * done / total));
        }
    }

    private static void Swap(InstallLayout layout, InstallServices services)
    {
        DeleteFolder(layout.PreviousDir);
        var hadPrevious = Directory.Exists(layout.AppDir);
        if (hadPrevious) Move(layout.AppDir, layout.PreviousDir, services.MoveRetryDelay);
        try
        {
            Move(layout.StagingDir, layout.AppDir, services.MoveRetryDelay);
        }
        catch when (hadPrevious)
        {
            Move(layout.PreviousDir, layout.AppDir, services.MoveRetryDelay);
            throw;
        }
    }

    private static void Move(string from, string to, TimeSpan retryDelay)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(from, to);
                return;
            }
            catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
            {
                // Usually a scanner looking at the freshly unpacked files for a moment.
                Thread.Sleep(retryDelay);
            }
        }
    }

    internal static void DeleteFolder(string folder)
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    internal static void TryDeleteFolder(string folder, Action<string> log)
    {
        try
        {
            DeleteFolder(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"Couldn’t remove {folder}: {ex.Message}");
        }
    }

    internal static void TryDeleteFile(string file, Action<string> log)
    {
        try
        {
            if (File.Exists(file)) File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"Couldn’t remove {file}: {ex.Message}");
        }
    }
}
