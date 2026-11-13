using Lunalyr.Install;
using Lunalyr.Shell.Settings;
using Lunalyr.Shell.Startup;

namespace Lunalyr.Shell.Tests.Updates;

public sealed class StartupAndPackagingOptionsTests
{
    private const string Exe = @"C:\Users\someone\AppData\Local\Programs\Lunalyr\app\Lunalyr.exe";
    private readonly MemoryRegistryHive _registry = new();

    [Fact]
    public void TheSwitchWritesAndReadsBackTheRunValue()
    {
        var sw = new LaunchAtLoginSwitch(_registry, Exe, testRun: null);
        Assert.False(sw.View().IsOn);
        Assert.Null(sw.Set(true));
        var view = sw.View();
        Assert.True(view.IsOn);
        Assert.True(view.Enabled);
        Assert.Null(view.Warning);
        Assert.Equal($"\"{Exe}\" --login", _registry.GetString(Product.RunKey, Product.RunValue));
        Assert.Null(sw.Set(false));
        Assert.False(sw.View().IsOn);
    }

    [Fact]
    public void TaskManagersSwitchShowsAsAWarningWithTheWayToStartupApps()
    {
        var sw = new LaunchAtLoginSwitch(_registry, Exe, testRun: null);
        sw.Set(true);
        _registry.Seed(Product.StartupApprovedKey, Product.RunValue, new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        var view = sw.View();
        Assert.False(view.IsOn);
        Assert.Equal("Turned off in Startup apps", view.Warning?.Title);
        Assert.True(view.OpenStartupApps);
    }

    [Fact]
    public void AnotherCopysEntryIsNamedAndLeftUntilTheSwitchIsUsed()
    {
        _registry.Seed(Product.RunKey, Product.RunValue, "\"C:\\dev\\Test Build 7\\Lunalyr.exe\" --login");
        var sw = new LaunchAtLoginSwitch(_registry, Exe, testRun: null);
        var view = sw.View();
        Assert.False(view.IsOn);
        Assert.Contains(@"C:\dev\Test Build 7", view.Warning?.Message);
        Assert.Null(sw.Set(true));
        Assert.True(sw.View().IsOn);
    }

    [Fact]
    public void TestRunsNeverChangeTheEntry()
    {
        var sw = new LaunchAtLoginSwitch(_registry, Exe, testRun: "--demo");
        Assert.NotNull(sw.Set(true));
        Assert.Empty(_registry.Writes);
        var view = sw.View();
        Assert.False(view.Enabled);
        Assert.Contains("--demo", view.Note);
    }

    [Fact]
    public void ACopyRunningFromAnyFolderSaysMovingItBreaksTheEntry()
    {
        var sw = new LaunchAtLoginSwitch(_registry, @"D:\Lunalyr Windows Test Build 8\Lunalyr.exe", testRun: null);
        Assert.Contains(@"runs from D:\Lunalyr Windows Test Build 8", sw.View().Note);
    }

    [Fact]
    public void LoginAndUpdatedFlagsParse()
    {
        var o = ShellOptions.Parse(["--login", "--updated", "0.9.0"]);
        Assert.Empty(o.Problems);
        Assert.True(o.Login);
        Assert.Equal("0.9.0", o.UpdatedTo);
        Assert.NotEmpty(ShellOptions.Parse(["--updated"]).Problems);
        // The scheme's command: everything after --link stays a link, even something shaped like these flags.
        var link = ShellOptions.Parse(["--link", "lunalyr://open", "--login"]);
        Assert.False(link.Login);
        Assert.Equal(["lunalyr://open", "--login"], link.Links);
    }

    [Fact]
    public void AutomaticUpdateChecksAreOnByDefault() => Assert.True(new AppSettings().AutomaticUpdateChecks);
}
