using System.IO.Compression;
using Lunalyr.Install;

namespace Lunalyr.Install.Tests;

public sealed class InstallerTests : IDisposable
{
    private readonly InstallFixture _f = new();

    public void Dispose() => _f.Dispose();

    [Fact]
    public void AFreshInstallPutsTheAppTheSetupTheEntriesAndTheShortcutInPlace()
    {
        var result = _f.Install(_f.Installer("0.8.0", (Product.ExeName, "exe 0.8.0"), (@"Assets\logo.png", "png")));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("0.8.0", result.Version);
        Assert.Equal("exe 0.8.0", File.ReadAllText(_f.Layout.AppExe));
        Assert.True(File.Exists(Path.Combine(_f.Layout.AppDir, "Assets", "logo.png")));
        Assert.Equal("setup 0.8.0", File.ReadAllText(_f.Layout.SetupExe));
        Assert.False(Directory.Exists(_f.Layout.StagingDir));
        Assert.False(Directory.Exists(_f.Layout.PreviousDir));

        var exe = _f.Layout.AppExe;
        var r = _f.Registry;
        Assert.Equal("URL:Lunalyr link", r.GetString(@"Software\Classes\lunalyr", null));
        Assert.Equal("", r.GetString(@"Software\Classes\lunalyr", "URL Protocol"));
        Assert.Equal($"\"{exe}\" --link \"%1\"", r.GetString(@"Software\Classes\lunalyr\shell\open\command", null));
        Assert.Equal(exe, InstallRegistry.LinkTarget(r));
        const string apps = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Lunalyr";
        Assert.Equal("Lunalyr", r.GetString(apps, "DisplayName"));
        Assert.Equal("0.8.0", r.GetString(apps, "DisplayVersion"));
        Assert.Equal(_f.Layout.Root, r.GetString(apps, "InstallLocation"));
        Assert.Equal($"\"{_f.Layout.SetupExe}\" --uninstall", r.GetString(apps, "UninstallString"));
        Assert.Equal("20261113", r.GetString(apps, "InstallDate"));
        Assert.Equal(1, r.GetDword(apps, "NoModify"));
        Assert.Equal(_f.Layout.AppExe, new FakeShortcuts().Target(_f.Target.Shortcut));

        // Only Lunalyr's own keys were written; launch at login stays the user's choice.
        Assert.All(r.Writes, w => Assert.True(w.Contains(@"Software\Classes\lunalyr", StringComparison.Ordinal)
                                              || w.Contains(apps, StringComparison.Ordinal), w));
        Assert.Null(r.GetString(Product.RunKey, Product.RunValue));
    }

    [Fact]
    public void AnUpdateReplacesTheWholeAppFolder()
    {
        Assert.True(_f.Install(_f.Installer("0.8.0", (Product.ExeName, "old"), ("Removed.dll", "gone in 0.9"))).Succeeded);
        var result = _f.Install(_f.Installer("0.9.0", (Product.ExeName, "new")));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("new", File.ReadAllText(_f.Layout.AppExe));
        Assert.False(File.Exists(Path.Combine(_f.Layout.AppDir, "Removed.dll")));
        Assert.False(Directory.Exists(_f.Layout.PreviousDir));
        Assert.Equal("0.9.0", _f.Registry.GetString(Product.UninstallKey, "DisplayVersion"));
    }

    [Fact]
    public void ACopyRunningFromTheInstallIsAskedToQuitAndOthersAreLeftAlone()
    {
        Assert.True(_f.Install(_f.Installer("0.8.0")).Succeeded);
        _f.Copies.Running.Add(new RunningCopy(1, 100, _f.Layout.AppExe));
        _f.Copies.Running.Add(new RunningCopy(2, 200, @"C:\dev\Lunalyr Windows Test Builds\Lunalyr Windows Test Build 7\Lunalyr.exe"));

        var result = _f.Install(_f.Installer("0.9.0"));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal([100], _f.Copies.AskedToQuit);
        Assert.Equal([200], result.OtherCopies.Select(c => c.ProcessId));
    }

    [Fact]
    public void ACopyThatWontQuitLeavesTheOldVersionUntouched()
    {
        Assert.True(_f.Install(_f.Installer("0.8.0", (Product.ExeName, "old"))).Succeeded);
        _f.Copies.Running.Add(new RunningCopy(1, 100, _f.Layout.AppExe));
        _f.Copies.Refuse.Add(100);

        var result = _f.Install(_f.Installer("0.9.0", (Product.ExeName, "new")));

        Assert.False(result.Succeeded);
        Assert.Contains("didn’t quit", result.Error);
        Assert.Equal("old", File.ReadAllText(_f.Layout.AppExe));
        Assert.Equal("0.8.0", _f.Registry.GetString(Product.UninstallKey, "DisplayVersion"));
        Assert.False(Directory.Exists(_f.Layout.StagingDir));
        Assert.False(File.Exists(_f.Layout.SetupExe + ".new"));
    }

    [Fact]
    public void ADamagedInstallerChangesNothing()
    {
        var installer = _f.Installer("0.8.0");
        var bytes = File.ReadAllBytes(installer);
        using (var s = new MemoryStream(bytes)) bytes[(int)Payload.Locate(s)!.Offset + 50] ^= 0xFF;
        File.WriteAllBytes(installer, bytes);

        var result = _f.Install(installer);

        Assert.False(result.Succeeded);
        Assert.Contains("damaged", result.Error);
        Assert.False(Directory.Exists(_f.Layout.Root));
        Assert.Empty(_f.Registry.Writes);
    }

    [Fact]
    public void AnEntryReachingOutsideTheAppFolderIsRefused()
    {
        var installer = _f.InstallerFromZip(zip =>
        {
            Write(zip, "app/Lunalyr.exe", "exe");
            Write(zip, "app/../../escaped.txt", "nope");
            Write(zip, Product.ManifestName, """{"product":"Lunalyr","version":"0.8.0","exe":"Lunalyr.exe","unpackedBytes":3}""");
        });

        var result = _f.Install(installer);

        Assert.False(result.Succeeded);
        Assert.False(File.Exists(Path.Combine(_f.Layout.Root, "..", "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(_f.Folder, "Programs", "escaped.txt")));
        Assert.False(Directory.Exists(_f.Layout.AppDir));
        Assert.Empty(_f.Registry.Writes);
    }

    [Fact]
    public void AnotherProductsPayloadIsRefused()
    {
        var installer = _f.InstallerFromZip(zip =>
        {
            Write(zip, "app/Lunalyr.exe", "exe");
            Write(zip, Product.ManifestName, """{"product":"Other","version":"1.0.0","exe":"Lunalyr.exe","unpackedBytes":3}""");
        });
        Assert.Contains("Other", _f.Install(installer).Error);
        Assert.Empty(_f.Registry.Writes);
    }

    [Fact]
    public void TheInstallFolderMayNotShareTheDataFolder()
    {
        var target = _f.Target with { Layout = new InstallLayout(Path.Combine(_f.DataRoot, "app-install")) };
        using var stream = File.OpenRead(_f.Installer("0.8.0"));
        var result = Installer.Install(stream, Payload.Locate(stream)!, target, _f.Services, cancel: TestContext.Current.CancellationToken);

        Assert.Contains("data folder", result.Error);
        Assert.Equal(["settings.json"], Directory.GetFileSystemEntries(_f.DataRoot).Select(Path.GetFileName));
    }

    [Fact]
    public void AnUpdateMovesLaunchAtLoginToTheNewCopyOnlyWhenItStartedThisInstall()
    {
        Assert.True(_f.Install(_f.Installer("0.8.0")).Succeeded);
        // An entry for an older layout inside this install (a previous app folder name) follows the app.
        _f.Registry.Seed(Product.RunKey, Product.RunValue, $"\"{Path.Combine(_f.Layout.Root, "app-0.7", "Lunalyr.exe")}\" --login");
        Assert.True(_f.Install(_f.Installer("0.9.0")).Succeeded);
        Assert.Equal($"\"{_f.Layout.AppExe}\" --login", _f.Registry.GetString(Product.RunKey, Product.RunValue));

        // An entry for another copy (a test build folder) is left alone.
        const string other = "\"C:\\Elsewhere\\Lunalyr.exe\" --login";
        _f.Registry.Seed(Product.RunKey, Product.RunValue, other);
        Assert.True(_f.Install(_f.Installer("0.9.1")).Succeeded);
        Assert.Equal(other, _f.Registry.GetString(Product.RunKey, Product.RunValue));
    }

    [Fact]
    public void CancellingWhileUnpackingChangesNothing()
    {
        Assert.True(_f.Install(_f.Installer("0.8.0", (Product.ExeName, "old"))).Succeeded);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        using var stream = File.OpenRead(_f.Installer("0.9.0", (Product.ExeName, "new")));
        var result = Installer.Install(stream, Payload.Locate(stream)!, _f.Target, _f.Services, cancel: cancel.Token);

        Assert.Contains("cancelled", result.Error);
        Assert.Equal("old", File.ReadAllText(_f.Layout.AppExe));
        Assert.False(Directory.Exists(_f.Layout.StagingDir));
    }

    private static void Write(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(content);
    }
}
