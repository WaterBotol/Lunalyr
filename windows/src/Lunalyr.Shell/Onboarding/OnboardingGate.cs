using Lunalyr.Platform.Settings;
using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell.Onboarding;

/// <summary>
/// When the welcome shows by itself: on a true first run only. That is a launch that found no settings file (and no
/// backup) in its data folder, so someone upgrading from a test build, whose <c>settings.json</c> exists, never sees it
/// unasked; a damaged file counts as an existing user too. Never for the demo, a check (a capture flag, <c>--seconds</c>,
/// <c>--stats</c>), or a launch that asked for another window. Settings › General shows it again on request.
/// </summary>
public static class OnboardingGate
{
    /// <summary>
    /// A first run: the store found neither <c>settings.json</c> nor its backup, and nothing went wrong reading them (a
    /// file that couldn't be read is an existing user's).
    /// </summary>
    public static bool IsFirstRun(SettingsLoadReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return report.Source == SettingsSource.Defaults && !report.HasProblems;
    }

    /// <summary>Whether the welcome shows by itself at this launch, and why not (for the log).</summary>
    public static (bool Show, string Reason) Decide(bool firstRun, AppSettings settings, ShellOptions options)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(options);
        if (settings.OnboardingDone) return (false, "it has been seen");
        if (!firstRun) return (false, "settings.json was already there (not a first run)");
        if (options.Demo) return (false, "--demo");
        if (options.OnboardingCapturePath is not null) return (false, "--onboarding-capture opens its own");
        if (options.FloatingCapturePath is not null || options.TickerCapturePath is not null || options.HudCapturePath is not null
            || options.SettingsLoaderCapturePath is not null || options.SettingsCapturePath is not null)
            return (false, "a capture flag");
        if (options.StatsPath is not null) return (false, "--stats");
        if (options.QuitAfterSeconds is not null) return (false, "--seconds (a check)");
        if (options.OpenSettings) return (false, options.SettingsPage is null ? "--settings" : "--settings-page");
        if (options.OpenLyricCard) return (false, "--lyric-card");
        if (options.OpenYouTubeSearch) return (false, "--youtube-search");
        if (options.Login) return (false, "--login (started at sign-in, quietly)");
        return (true, "first run");
    }

    /// <summary>
    /// The settings once the welcome is over, however it ended (Done, Open Settings, closed early, or still open when
    /// Lunalyr quit): it never shows by itself again.
    /// </summary>
    public static AppSettings Completed(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.OnboardingDone ? settings : settings with { OnboardingDone = true };
    }

    /// <summary>Only Open Settings opens Settings.</summary>
    public static bool OpensSettings(OnboardingEnding ending) => ending == OnboardingEnding.OpenSettings;

    /// <summary>
    /// The welcome's last word, however it ended (the Mac's <c>completeFirstRun</c>): it counts as seen, saved at once,
    /// and Open Settings opens Settings. Windows follows the players from launch with no prompt, so there is nothing to
    /// start here.
    /// </summary>
    public static void Complete(OnboardingEnding ending, SettingsStore<AppSettings> settings, Action openSettings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(openSettings);
        settings.Update(Completed);
        if (OpensSettings(ending)) openSettings();
    }
}

/// <summary>
/// Opening the welcome or Settings while the welcome may be up (the Mac's <c>LunalyrNavigation.openOnboarding</c> and its
/// Settings window deferring to onboarding): a welcome already showing comes forward instead of a second one, and
/// Settings asked for from the tray or a second launch brings the welcome forward while it is up.
/// </summary>
public sealed class OnboardingNavigation(Action presentSettings, Action presentOnboarding, Func<bool> onboardingIsVisible,
                                         Action bringOnboardingForward)
{
    /// <summary>Settings › General's Show Welcome….</summary>
    public void OpenOnboarding()
    {
        if (onboardingIsVisible()) bringOnboardingForward();
        else presentOnboarding();
    }

    /// <summary>The tray's Open settings, or a second launch.</summary>
    public void OpenSettings()
    {
        if (onboardingIsVisible()) bringOnboardingForward();
        else presentSettings();
    }
}
