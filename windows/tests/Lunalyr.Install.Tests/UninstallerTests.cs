using Lunalyr.Install;

namespace Lunalyr.Install.Tests;

public sealed class UninstallerTests : IDisposable
{
    private readonly InstallFixture _f = new();

    public void Dispose() => _f.Dispose();

    [Fact]
    public void UninstallRemovesEverythingTheInstallerWroteAndKeepsTheData()
    {
        Assert.True(_f.Install(_f.Installer("0.8.0")).Succeeded);
        LaunchAtLogin.Set(_f.Registry, _f.Layout.AppExe, on: true);
        _f.Copies.Running.Add(new RunningCopy(1, 100, _f.Layout.AppExe));

        var result = Uninstaller.Uninstall(_f.Target, _f.Services);

        Assert.True(result.Succeeded, result.Error);
        Assert.Empty(result.Leftovers);
        Assert.Equal([100], _f.Copies.AskedToQuit);
        Assert.False(Directory.Exists(_f.Layout.Root));
        Assert.False(File.Exists(_f.Target.Shortcut));
        Assert.False(_f.Registry.KeyExists(Product.LinkKey));
        Assert.False(_f.Registry.KeyExists(Product.UninstallKey));
        Assert.Null(_f.Registry.GetString(Product.RunKey, Product.RunValue));
        Assert.True(File.Exists(Path.Combine(_f.DataRoot, "settings.json")));
    }

    [Fact]
    public void EntriesNowBelongingToAnotherCopyAreLeftAlone()
    {
        Assert.True(_f.Install(_f.Installer("0.8.0")).Succeeded);
        const string other = @"C:\Elsewhere\Lunalyr.exe";
        _f.Registry.SetString(Product.LinkKey + @"\shell\open\command", null, InstallRegistry.LinkCommand(other));
        _f.Registry.SetString(Product.RunKey, Product.RunValue, LaunchAtLogin.Command(other));
        new FakeShortcuts().Create(_f.Target.Shortcut, other, "");

        Assert.True(Uninstaller.Uninstall(_f.Target, _f.Services).Succeeded);

        Assert.Equal(other, InstallRegistry.LinkTarget(_f.Registry));
        Assert.Equal(LaunchAtLogin.Command(other), _f.Registry.GetString(Product.RunKey, Product.RunValue));
        Assert.Equal(other, new FakeShortcuts().Target(_f.Target.Shortcut));
        Assert.False(_f.Registry.KeyExists(Product.UninstallKey));
    }

    [Fact]
    public void ACopyThatWontQuitStopsTheUninstallBeforeAnythingIsRemoved()
    {
        Assert.True(_f.Install(_f.Installer("0.8.0")).Succeeded);
        _f.Copies.Running.Add(new RunningCopy(1, 100, _f.Layout.AppExe));
        _f.Copies.Refuse.Add(100);
        var writes = _f.Registry.Writes.Count;

        var result = Uninstaller.Uninstall(_f.Target, _f.Services);

        Assert.False(result.Succeeded);
        Assert.True(File.Exists(_f.Layout.AppExe));
        Assert.Equal(writes, _f.Registry.Writes.Count);
    }

    [Fact]
    public void TheRunningSetupIsLeftForItsCopyToRemove()
    {
        Assert.True(_f.Install(_f.Installer("0.8.0")).Succeeded);

        var result = Uninstaller.Uninstall(_f.Target, _f.Services, runningSetup: _f.Layout.SetupExe);

        Assert.True(result.Succeeded);
        Assert.True(File.Exists(_f.Layout.SetupExe));
        Assert.False(Directory.Exists(_f.Layout.AppDir));
        // The copy (started from Temp) removes the rest, setup included.
        Assert.True(Uninstaller.Uninstall(_f.Target, _f.Services).Succeeded);
        Assert.False(Directory.Exists(_f.Layout.Root));
    }

    [Fact]
    public void FoldersThatDontHoldLunalyrAreLeftAlone()
    {
        // An install folder that was never Lunalyr's (a setup renamed and run from Downloads, say).
        var app = Path.Combine(_f.Layout.Root, "app");
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "holiday.jpg"), "someone's photo");

        Assert.True(Uninstaller.Uninstall(_f.Target, _f.Services).Succeeded);

        Assert.True(File.Exists(Path.Combine(app, "holiday.jpg")));
    }

    [Fact]
    public void UninstallingTwiceIsHarmless()
    {
        Assert.True(_f.Install(_f.Installer("0.8.0")).Succeeded);
        Assert.True(Uninstaller.Uninstall(_f.Target, _f.Services).Succeeded);
        Assert.True(Uninstaller.Uninstall(_f.Target, _f.Services).Succeeded);
    }
}
