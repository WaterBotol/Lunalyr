using System.Diagnostics;
using Lunalyr.Shell.Flyout;

namespace Lunalyr.App.Tray;

/// <summary>Opens an https address in the user's default browser (the shell's own handler). Used only from a click.</summary>
internal sealed class ShellUrlLauncher : IUrlLauncher
{
    public static ShellUrlLauncher Instance { get; } = new();

    public bool Launch(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (url.Scheme != Uri.UriSchemeHttps) return false;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
