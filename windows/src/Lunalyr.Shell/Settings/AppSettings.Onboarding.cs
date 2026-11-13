// The first-run welcome (milestone 8). The Mac's key is "hasOnboarded" (Settings.swift; its lines there are on the
// do-not-translate list, so only the fact is kept: a flag saved once the welcome is over, however it ended).

namespace Lunalyr.Shell.Settings;

public sealed partial record AppSettings
{
    /// <summary>
    /// The first-run welcome has been seen: finished with Done or Open Settings, closed early, or left open while Lunalyr
    /// quit. It is only ever set, never cleared (Mac <c>hasOnboarded</c>). See docs/APP-SHELL.md, "Onboarding".
    /// </summary>
    public bool OnboardingDone { get; init; }
}
