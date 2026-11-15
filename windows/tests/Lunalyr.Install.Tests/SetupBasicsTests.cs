using Lunalyr.Install;

namespace Lunalyr.Install.Tests;

public sealed class SetupBasicsTests
{
    [Theory]
    [InlineData("0.8.0", "0.8.1", -1)]
    [InlineData("0.9.0", "0.8.9", 1)]
    [InlineData("1.0.0", "0.99.99", 1)]
    [InlineData("0.8.0", "0.8.0", 0)]
    [InlineData("0.8.0+abc", "0.8.0+def", 0)]
    [InlineData("v0.8.0", "0.8.0", 0)]
    [InlineData("0.8", "0.8.0", 0)]
    [InlineData("0.8.0-beta.1", "0.8.0", -1)]
    [InlineData("0.8.0-beta.2", "0.8.0-beta.10", -1)]
    [InlineData("0.8.0-alpha", "0.8.0-beta", -1)]
    [InlineData("0.8.0-beta", "0.8.0-beta.1", -1)]
    [InlineData("0.8.0-1", "0.8.0-alpha", -1)]
    public void VersionsCompareAsSemVer(string a, string b, int expected) =>
        Assert.Equal(expected, Math.Sign(ProductVersion.Parse(a)!.CompareTo(ProductVersion.Parse(b))));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.x.0")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0-a..b")]
    [InlineData("-1.0.0")]
    [InlineData("1.2.3.4.5")]
    public void MalformedVersionsAreRejected(string text) => Assert.Null(ProductVersion.Parse(text));

    [Fact]
    public void SetupOptionsParse()
    {
        var o = SetupOptions.Parse(["--update", "--silent", "--root", @"C:\tmp\x", "--registry-base", @"Software\Lunalyr\SetupCheck",
            "--start-menu", @"C:\tmp\menu", "--log", @"C:\tmp\setup.log", "--no-launch"]);
        Assert.Empty(o.Problems);
        Assert.Equal((SetupMode.Install, true, true, true), (o.Mode, o.Update, o.Silent, o.NoLaunch));
        Assert.Equal((@"C:\tmp\x", @"Software\Lunalyr\SetupCheck", @"C:\tmp\menu", @"C:\tmp\setup.log"),
            (o.Root, o.RegistryBase, o.StartMenu, o.LogPath));
        Assert.False(o.IsReal);

        var plain = SetupOptions.Parse([]);
        Assert.True(plain.IsReal);
        Assert.Equal(SetupMode.Install, plain.Mode);
        Assert.Equal(SetupMode.Uninstall, SetupOptions.Parse(["--uninstall", "--wait-pid", "42"]).Mode);
        Assert.Equal(42, SetupOptions.Parse(["--uninstall", "--wait-pid", "42"]).WaitPid);
    }

    [Theory]
    [InlineData("--registry-base", @"Software\Microsoft\Windows")]
    [InlineData("--root")]
    [InlineData("--wait-pid", "x")]
    [InlineData("--bogus")]
    [InlineData("--update", "--uninstall")]
    public void BadSetupOptionsAreProblems(params string[] args) => Assert.NotEmpty(SetupOptions.Parse(args).Problems);

    [Fact]
    public void AnInstalledAppKnowsItsLayout()
    {
        var root = Path.Combine(Path.GetTempPath(), "lunalyr-layout-" + Guid.NewGuid().ToString("N"));
        try
        {
            var layout = new InstallLayout(root);
            Directory.CreateDirectory(layout.AppDir);
            File.WriteAllText(layout.AppExe, "");
            Assert.Null(InstallLayout.Detect(layout.AppExe)); // No setup beside it: not installed (a build output).
            File.WriteAllText(layout.SetupExe, "");
            Assert.Equal(layout.Root, InstallLayout.Detect(layout.AppExe)?.Root);
            Assert.Null(InstallLayout.Detect(Path.Combine(root, "Lunalyr.exe")));
            Assert.Null(InstallLayout.Detect(null));
            Assert.True(layout.Contains(layout.AppExe));
            Assert.False(layout.Contains(root + "-other\\Lunalyr.exe"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void InstallRootsThatWouldHurtAreRefused()
    {
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lunalyr");
        Assert.Null(InstallLayout.Default().Problem(data));
        Assert.EndsWith(@"\Programs\Lunalyr", InstallLayout.Default().Root);
        Assert.NotNull(new InstallLayout(data).Problem(data));
        Assert.NotNull(new InstallLayout(Path.Combine(data, "app")).Problem(data));
        Assert.NotNull(new InstallLayout(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)).Problem(data));
        Assert.NotNull(new InstallLayout(@"C:\").Problem(data));
        Assert.NotNull(new InstallLayout(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)).Problem(data));
    }
}
