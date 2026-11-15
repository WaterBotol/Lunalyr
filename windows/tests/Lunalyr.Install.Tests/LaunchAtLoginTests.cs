using Lunalyr.Install;

namespace Lunalyr.Install.Tests;

public sealed class LaunchAtLoginTests
{
    private const string Exe = @"C:\Users\someone\AppData\Local\Programs\Lunalyr\app\Lunalyr.exe";
    private readonly MemoryRegistryHive _registry = new();

    [Fact]
    public void TurningItOnWritesOneRunValueStartingThisCopyQuietly()
    {
        Assert.Null(LaunchAtLogin.Set(_registry, Exe, on: true));
        Assert.Equal($"\"{Exe}\" --login", _registry.GetString(Product.RunKey, "Lunalyr"));
        Assert.Equal(new LaunchAtLoginStatus(LaunchAtLoginState.On, Exe), LaunchAtLogin.Read(_registry, Exe));
        Assert.Equal([$"set {Product.RunKey} › Lunalyr = \"{Exe}\" --login"], _registry.Writes);
    }

    [Fact]
    public void TurningItOffRemovesOnlyLunalyrsValue()
    {
        _registry.Seed(Product.RunKey, "OneDrive", "\"C:\\OneDrive.exe\" /background");
        LaunchAtLogin.Set(_registry, Exe, on: true);
        Assert.Null(LaunchAtLogin.Set(_registry, Exe, on: false));

        Assert.Equal(LaunchAtLoginState.Off, LaunchAtLogin.Read(_registry, Exe).State);
        Assert.Equal("\"C:\\OneDrive.exe\" /background", _registry.GetString(Product.RunKey, "OneDrive"));
    }

    [Fact]
    public void AnEntryForAnotherCopySaysWhichOne()
    {
        _registry.Seed(Product.RunKey, "Lunalyr", "\"D:\\Test Build 7\\Lunalyr.exe\" --login");
        Assert.Equal(new LaunchAtLoginStatus(LaunchAtLoginState.OnForAnotherCopy, @"D:\Test Build 7\Lunalyr.exe"),
            LaunchAtLogin.Read(_registry, Exe));
    }

    [Theory]
    [InlineData(new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, LaunchAtLoginState.TurnedOffInTaskManager)]
    [InlineData(new byte[] { 7, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, LaunchAtLoginState.TurnedOffInTaskManager)]
    [InlineData(new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, LaunchAtLoginState.On)]
    [InlineData(new byte[] { 6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, LaunchAtLoginState.On)]
    public void TaskManagersStartupAppsSwitchIsRead(byte[] approved, LaunchAtLoginState expected)
    {
        LaunchAtLogin.Set(_registry, Exe, on: true);
        _registry.Seed(Product.StartupApprovedKey, "Lunalyr", approved);
        Assert.Equal(expected, LaunchAtLogin.Read(_registry, Exe).State);
    }

    [Fact]
    public void NothingOutsideLunalyrsOwnEntriesCanBeWritten()
    {
        Assert.Throws<InvalidOperationException>(() => _registry.SetString(Product.RunKey, "OneDrive", "x"));
        Assert.Throws<InvalidOperationException>(() => _registry.DeleteValue(Product.RunKey, "OneDrive"));
        Assert.Throws<InvalidOperationException>(() => _registry.SetString(Product.StartupApprovedKey, "Lunalyr", "x"));
        Assert.Throws<InvalidOperationException>(() => _registry.DeleteTree(Product.RunKey));
        Assert.Throws<InvalidOperationException>(() => _registry.SetString(@"Software\Classes\http\shell\open\command", null, "x"));
        Assert.Throws<InvalidOperationException>(() => _registry.SetString(@"Software\Classes\lunalyrx", null, "x"));
        Assert.Throws<ArgumentException>(() => new WindowsRegistryHive(@"Software\Microsoft"));
        Assert.Empty(_registry.Writes);
    }

    [Theory]
    [InlineData("\"C:\\A B\\Lunalyr.exe\" --login", @"C:\A B\Lunalyr.exe")]
    [InlineData("C:\\AB\\Lunalyr.exe --login", @"C:\AB\Lunalyr.exe")]
    [InlineData("C:\\AB\\Lunalyr.exe", @"C:\AB\Lunalyr.exe")]
    [InlineData("  ", null)]
    [InlineData("\"unterminated", null)]
    public void TheProgramIsTheCommandsFirstToken(string command, string? expected) =>
        Assert.Equal(expected, InstallRegistry.CommandExe(command));
}
