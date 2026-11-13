namespace Lunalyr.Install;

/// <summary>Whether Lunalyr starts when you sign in to Windows.</summary>
public enum LaunchAtLoginState
{
    /// <summary>No Run value: Lunalyr doesn't start at sign-in.</summary>
    Off,

    /// <summary>The Run value starts this copy.</summary>
    On,

    /// <summary>The Run value starts a different copy of Lunalyr (another folder).</summary>
    OnForAnotherCopy,

    /// <summary>The Run value is there, but Task Manager › Startup apps has it turned off.</summary>
    TurnedOffInTaskManager,
}

/// <summary>What <see cref="LaunchAtLogin.Read"/> found.</summary>
public sealed record LaunchAtLoginStatus(LaunchAtLoginState State, string? StartsPath)
{
    public bool IsOn => State == LaunchAtLoginState.On;
}

/// <summary>
/// Launch at login, the Windows way: one value named <c>Lunalyr</c> in <c>HKCU\…\CurrentVersion\Run</c>, holding
/// <c>"…\Lunalyr.exe" --login</c>. The registry is the truth (Task Manager can turn it off, and another copy can claim
/// it), so the switch reads it back instead of keeping its own setting. Nothing else in the Run key is touched.
/// </summary>
public static class LaunchAtLogin
{
    public static string Command(string exe) => $"\"{exe}\" {Product.LoginFlag}";

    public static LaunchAtLoginStatus Read(IRegistryHive hive, string exe)
    {
        var target = InstallRegistry.CommandExe(hive.GetString(Product.RunKey, Product.RunValue));
        if (target is null) return new(LaunchAtLoginState.Off, null);
        if (!SamePath(target, exe)) return new(LaunchAtLoginState.OnForAnotherCopy, target);
        return IsTurnedOffInTaskManager(hive)
            ? new(LaunchAtLoginState.TurnedOffInTaskManager, target)
            : new(LaunchAtLoginState.On, target);
    }

    /// <summary>
    /// Turns launch at login on (for this copy) or off. Returns null, or why it didn't change, worded for the switch's
    /// note.
    /// </summary>
    public static string? Set(IRegistryHive hive, string exe, bool on)
    {
        try
        {
            if (on) hive.SetString(Product.RunKey, Product.RunValue, Command(exe));
            else hive.DeleteValue(Product.RunKey, Product.RunValue);
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return $"Windows didn’t let Lunalyr change its sign-in entry ({ex.Message}).";
        }
    }

    /// <summary>After an install, points an existing entry for an older copy inside this install at its app.</summary>
    public static void Follow(IRegistryHive hive, InstallLayout layout)
    {
        var target = InstallRegistry.CommandExe(hive.GetString(Product.RunKey, Product.RunValue));
        if (target is not null && layout.Contains(target) && !SamePath(target, layout.AppExe))
            hive.SetString(Product.RunKey, Product.RunValue, Command(layout.AppExe));
    }

    /// <summary>On uninstall: removes the entry only while it starts this install.</summary>
    public static void Remove(IRegistryHive hive, InstallLayout layout)
    {
        var target = InstallRegistry.CommandExe(hive.GetString(Product.RunKey, Product.RunValue));
        if (target is not null && layout.Contains(target)) hive.DeleteValue(Product.RunKey, Product.RunValue);
    }

    /// <summary>
    /// Task Manager records a Run entry's state as 12 bytes; the first byte is even (2, 6) when allowed and odd (3, 7)
    /// when turned off. A missing record means allowed.
    /// </summary>
    internal static bool IsTurnedOffInTaskManager(IRegistryHive hive) =>
        hive.GetBinary(Product.StartupApprovedKey, Product.RunValue) is { Length: > 0 } state && (state[0] & 1) == 1;

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
