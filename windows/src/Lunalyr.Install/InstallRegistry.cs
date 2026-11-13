using System.Globalization;

namespace Lunalyr.Install;

/// <summary>
/// The installer's registry entries, all under <c>HKCU</c>:
/// <list type="bullet">
/// <item><c>Software\Classes\lunalyr</c>: the <c>lunalyr://</c> scheme, opening <c>"…\Lunalyr.exe" --link "%1"</c>.</item>
/// <item><c>Software\Microsoft\Windows\CurrentVersion\Uninstall\Lunalyr</c>: Lunalyr in Settings › Apps.</item>
/// </list>
/// Launch at login (the Run value) is the app's switch, see <see cref="LaunchAtLogin"/>; the installer only moves it to
/// the new copy and removes it on uninstall.
/// </summary>
public static class InstallRegistry
{
    /// <summary>The command the scheme runs. <c>ShellOptions</c> reads everything after <c>--link</c> as links only.</summary>
    public static string LinkCommand(string exe) => $"\"{exe}\" --link \"%1\"";

    public static void Write(IRegistryHive hive, InstallLayout layout, string version, long sizeBytes, DateTime installedOn)
    {
        var exe = layout.AppExe;
        hive.SetString(Product.LinkKey, null, "URL:Lunalyr link");
        hive.SetString(Product.LinkKey, "URL Protocol", "");
        hive.SetString(Product.LinkKey + @"\DefaultIcon", null, $"\"{exe}\",0");
        hive.SetString(Product.LinkKey + @"\shell\open\command", null, LinkCommand(exe));

        var key = Product.UninstallKey;
        hive.SetString(key, "DisplayName", Product.Name);
        hive.SetString(key, "DisplayVersion", version);
        hive.SetString(key, "Publisher", Product.Publisher);
        hive.SetString(key, "DisplayIcon", $"\"{exe}\",0");
        hive.SetString(key, "InstallLocation", layout.Root);
        hive.SetString(key, "UninstallString", $"\"{layout.SetupExe}\" --uninstall");
        hive.SetString(key, "QuietUninstallString", $"\"{layout.SetupExe}\" --uninstall --silent");
        hive.SetString(key, "InstallDate", installedOn.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        hive.SetDword(key, "NoModify", 1);
        hive.SetDword(key, "NoRepair", 1);
        hive.SetDword(key, "EstimatedSize", (int)Math.Min(int.MaxValue, Math.Max(0, sizeBytes / 1024)));
    }

    /// <summary>The executable the scheme opens now, or null when <c>lunalyr://</c> isn't registered.</summary>
    public static string? LinkTarget(IRegistryHive hive) =>
        CommandExe(hive.GetString(Product.LinkKey + @"\shell\open\command", null));

    /// <summary>
    /// Removes this install's entries. The scheme is removed only while it opens this install (another copy may have
    /// registered it since), and the Apps entry only while it names this install folder.
    /// </summary>
    public static void Remove(IRegistryHive hive, InstallLayout layout)
    {
        if (LinkTarget(hive) is { } target && layout.Contains(target)) hive.DeleteTree(Product.LinkKey);
        var location = hive.GetString(Product.UninstallKey, "InstallLocation");
        if (location is not null && InstallLayout.IsInside(location, layout.Root)) hive.DeleteTree(Product.UninstallKey);
    }

    /// <summary>The program a command line starts: its first, possibly quoted, token.</summary>
    public static string? CommandExe(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var text = command.TrimStart();
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }
        var space = text.IndexOf(' ');
        return space < 0 ? text : text[..space];
    }
}
