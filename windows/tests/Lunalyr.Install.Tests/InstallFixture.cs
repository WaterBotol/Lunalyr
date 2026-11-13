using System.IO.Compression;
using Lunalyr.Install;

namespace Lunalyr.Install.Tests;

/// <summary>Running copies that exist only in the test: each answers Quit as told and records being asked.</summary>
internal sealed class FakeCopies : IRunningCopies
{
    public List<RunningCopy> Running { get; } = [];
    public List<int> AskedToQuit { get; } = [];
    public HashSet<int> Refuse { get; } = [];

    public IReadOnlyList<RunningCopy> Find() => Running.ToList();

    public bool Quit(RunningCopy copy, TimeSpan timeout)
    {
        AskedToQuit.Add(copy.ProcessId);
        if (Refuse.Contains(copy.ProcessId)) return false;
        Running.Remove(copy);
        return true;
    }
}

/// <summary>A temp folder with an install root, a data folder, a Start menu folder and builders for installers.</summary>
internal sealed class InstallFixture : IDisposable
{
    public InstallFixture()
    {
        Folder = Path.Combine(Path.GetTempPath(), "lunalyr-install-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
        Layout = new InstallLayout(Path.Combine(Folder, "Programs", "Lunalyr"));
        DataRoot = Path.Combine(Folder, "Data", "Lunalyr");
        Directory.CreateDirectory(DataRoot);
        File.WriteAllText(Path.Combine(DataRoot, "settings.json"), "{}");
        Target = new InstallTarget(Layout, DataRoot, Path.Combine(Folder, "StartMenu"));
        Services = new InstallServices
        {
            Registry = Registry,
            Copies = Copies,
            Shortcuts = new FakeShortcuts(),
            Today = () => new DateTime(2026, 11, 13),
            Log = Log.Add,
            QuitTimeout = TimeSpan.Zero,
            MoveRetryDelay = TimeSpan.Zero,
        };
    }

    public string Folder { get; }
    public InstallLayout Layout { get; }
    public string DataRoot { get; }
    public InstallTarget Target { get; }
    public MemoryRegistryHive Registry { get; } = new();
    public FakeCopies Copies { get; } = new();
    public InstallServices Services { get; }
    public List<string> Log { get; } = [];

    /// <summary>A setup "program": not a real PE, which the payload format doesn't need.</summary>
    public string Stub(string content = "setup program")
    {
        var path = Path.Combine(Folder, $"stub-{Guid.NewGuid():N}.exe");
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>An app folder with the given files (relative path → content).</summary>
    public string AppFolder(params (string Path, string Content)[] files)
    {
        var folder = Path.Combine(Folder, $"publish-{Guid.NewGuid():N}");
        foreach (var (rel, content) in files)
        {
            var full = Path.Combine(folder, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        return folder;
    }

    /// <summary>Builds an installer for <paramref name="version"/> holding those files.</summary>
    public string Installer(string version, params (string Path, string Content)[] files)
    {
        var app = AppFolder(files.Length == 0 ? [(Product.ExeName, $"Lunalyr {version}")] : files);
        using var zip = new MemoryStream();
        Payload.WriteZip(zip, app, Stub($"setup {version}"), version);
        var output = Path.Combine(Folder, $"LunalyrSetup-{version}-{Guid.NewGuid():N}.exe");
        Payload.WriteInstaller(Stub(), zip, output);
        return output;
    }

    /// <summary>Builds an installer from a hand-made zip (for damaged or hostile payloads).</summary>
    public string InstallerFromZip(Action<ZipArchive> fill)
    {
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true)) fill(archive);
        var output = Path.Combine(Folder, $"LunalyrSetup-custom-{Guid.NewGuid():N}.exe");
        Payload.WriteInstaller(Stub(), zip, output);
        return output;
    }

    public InstallResult Install(string installer)
    {
        using var stream = File.OpenRead(installer);
        var payload = Payload.Locate(stream) ?? throw new InvalidOperationException("no payload");
        return Lunalyr.Install.Installer.Install(stream, payload, Target, Services, cancel: TestContext.Current.CancellationToken);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
