// Updates (milestone 8): the Mac's Sparkle checks automatically by default; so does Lunalyr for Windows, once a day.

namespace Lunalyr.Shell.Settings;

public sealed partial record AppSettings
{
    /// <summary>
    /// "Check for updates automatically": once a day, a newer version is downloaded and checked in the background, and
    /// installs only when you choose Restart and update. On by default. Builds without an update service never check.
    /// </summary>
    public bool AutomaticUpdateChecks { get; init; } = true;
}
