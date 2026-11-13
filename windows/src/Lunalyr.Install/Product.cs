namespace Lunalyr.Install;

/// <summary>The names Lunalyr installs under. Every registry entry the installer or the app writes is one of these.</summary>
public static class Product
{
    public const string Name = "Lunalyr";
    public const string Publisher = "Lunalyr";
    public const string ExeName = "Lunalyr.exe";

    /// <summary>The installer, copied into the install folder, where it also removes Lunalyr (Settings › Apps).</summary>
    public const string SetupName = "Lunalyr Setup.exe";

    /// <summary>The <c>lunalyr://</c> scheme, registered as <c>Lunalyr.exe --link "%1"</c>.</summary>
    public const string LinkScheme = "lunalyr";

    /// <summary>Under <c>HKCU</c>: the scheme's key. Lunalyr owns it.</summary>
    public const string LinkKey = @"Software\Classes\" + LinkScheme;

    /// <summary>Under <c>HKCU</c>: the Settings › Apps entry. Lunalyr owns it.</summary>
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + Name;

    /// <summary>Under <c>HKCU</c>: Windows' list of programs to start at sign-in. Lunalyr owns only its own value.</summary>
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Under <c>HKCU</c>: where Task Manager's Startup apps records that a Run entry was turned off (read only).</summary>
    public const string StartupApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>The Run value's name.</summary>
    public const string RunValue = Name;

    /// <summary>The flag a launch at sign-in passes: start quietly in the tray.</summary>
    public const string LoginFlag = "--login";

    /// <summary>A running Lunalyr's hidden host window class starts with this (then a hash of its data folder).</summary>
    public const string HostWindowClassPrefix = "Lunalyr.Host.";

    /// <summary>Posted to the host window, it quits Lunalyr gracefully (settings flushed, wallpaper restored).</summary>
    public const uint QuitMessage = 0x8000 + 2; // WM_APP + 2

    /// <summary>The JSON file inside the payload naming the version.</summary>
    public const string ManifestName = "lunalyr-install.json";

    /// <summary>Inside the payload: the installer itself, installed as <see cref="SetupName"/>.</summary>
    public const string PayloadSetupEntry = "setup/" + SetupName;

    /// <summary>Inside the payload: the app's files.</summary>
    public const string PayloadAppPrefix = "app/";
}
