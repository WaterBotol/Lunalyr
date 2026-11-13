using Lunalyr.Install;

namespace Lunalyr.Shell.Startup;

/// <summary>What the Launch at login row shows: the switch, whether it can be used, and a note (or a warning).</summary>
/// <param name="Warning">A problem to show as a warning bar (title, message), or null.</param>
/// <param name="OpenStartupApps">Show the button that opens Settings › Apps › Startup.</param>
public sealed record LaunchAtLoginView(bool IsOn, bool Enabled, string Note, (string Title, string Message)? Warning,
                                       bool OpenStartupApps);

/// <summary>
/// The General page's Launch at login switch (the Mac's, with SMAppService's states in Windows terms): reads the Run
/// value back every time, so Task Manager's switch and other copies show; turning it on starts this copy with
/// <c>--login</c>. Test runs (<c>--demo</c>, <c>--data</c>) never touch the real entry.
/// </summary>
public sealed class LaunchAtLoginSwitch
{
    private readonly IRegistryHive _registry;
    private readonly string _exe;
    private readonly string? _testRun;
    private string? _lastError;

    /// <param name="testRun">Why this run may not change the entry (<c>--demo</c>, <c>--data</c>), or null.</param>
    public LaunchAtLoginSwitch(IRegistryHive registry, string exe, string? testRun)
    {
        _registry = registry;
        _exe = exe;
        _testRun = testRun;
    }

    public LaunchAtLoginStatus Status => LaunchAtLogin.Read(_registry, _exe);

    /// <summary>Turns it on or off; returns null or why it didn't change.</summary>
    public string? Set(bool on)
    {
        if (_testRun is not null) return _lastError = $"Launch at login isn’t changed by test runs ({_testRun}).";
        _lastError = LaunchAtLogin.Set(_registry, _exe, on);
        return _lastError;
    }

    public LaunchAtLoginView View()
    {
        var status = Status;
        var installed = InstallLayout.Detect(_exe) is not null;
        var where = Path.GetDirectoryName(_exe) ?? _exe;
        if (_testRun is not null)
            return new(status.IsOn, false, $"Not changed by test runs ({_testRun}).", null, false);
        var note = installed
            ? "Lunalyr starts quietly in the tray when you sign in."
            : $"Lunalyr starts quietly in the tray when you sign in. This copy runs from {where}; if you move it, turn this on again.";
        if (_lastError is { } error)
            return new(status.IsOn, true, note, ("Launch at login didn’t change", error), false);
        return status.State switch
        {
            LaunchAtLoginState.TurnedOffInTaskManager => new(false, true, note,
                ("Turned off in Startup apps", "Windows’ Startup apps list has Lunalyr turned off, so it won’t start when you sign in. Turn it on there."),
                true),
            LaunchAtLoginState.OnForAnotherCopy => new(false, true, note,
                ("Another copy starts at sign-in", $"Windows starts the Lunalyr in {Path.GetDirectoryName(status.StartsPath) ?? status.StartsPath} when you sign in. Turn this on to start this copy instead."),
                false),
            _ => new(status.IsOn, true, note, null, false),
        };
    }
}
