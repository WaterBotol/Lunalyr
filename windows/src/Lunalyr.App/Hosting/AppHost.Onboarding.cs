using Lunalyr.Core.NowPlaying;
using Lunalyr.Platform.NowPlaying;
using Lunalyr.Shell.Onboarding;

namespace Lunalyr.App.Hosting;

/// <summary>
/// The first-run welcome (docs/APP-SHELL.md, "Onboarding"): shown by itself on a true first run only
/// (<see cref="OnboardingGate"/>), again from Settings › General, and for checks with <c>--onboarding-capture</c>.
/// However it ends, and if Lunalyr quits while it is up, it counts as seen (<c>onboardingDone</c>).
/// </summary>
internal sealed partial class AppHost
{
    private volatile bool _welcomeShownAsFirstRun;

    /// <summary><c>--settings-loader-capture</c>: the file the Settings loader is written to (checks), or null.</summary>
    internal string? SettingsLoaderCapturePath => _options.SettingsLoaderCapturePath;

    /// <summary><c>--settings-capture</c>: the file the whole Settings page is written to (checks), or null.</summary>
    internal string? SettingsCapturePath => _options.SettingsCapturePath;

    /// <summary><c>--settings-capture-scale</c>: the display scale whose window size the capture shows (dev only), or null.</summary>
    internal double? SettingsCaptureScale => _options.SettingsCaptureScale;

    /// <summary>The players Windows' media controls know about (null until start-up has opened them), read only.</summary>
    internal IMediaSessionSource? MediaSessions => _sessions;

    /// <summary>The apps sharing with Windows now, for the player pickers (only their ids are read).</summary>
    internal IReadOnlyList<MediaSourceApp> SharingPlayers() => _sessions?.Sessions.Select(s => s.SourceApp).ToList() ?? [];

    /// <summary>Whether Lunalyr starts when the user signs in to Windows (the same switch as Settings › General).</summary>
    internal Func<bool> IsLaunchAtLoginOn => () => _launchAtLogin?.Status.IsOn ?? false;

    /// <summary>Turns launch at login on or off; returns a sentence for the user when it couldn't, else null.</summary>
    internal Func<bool, string?> SetLaunchAtLogin => on => _launchAtLogin is { } sw ? sw.Set(on) : "Lunalyr is still starting. Try again in a moment.";

    /// <summary>
    /// WinUI is up: the welcome for <c>--onboarding-capture</c>, or on a true first run. A first run is decided from what
    /// the settings store found at launch (no settings file and no backup), so a test build's existing user never sees it
    /// unasked.
    /// </summary>
    internal void ShowWelcomeIfDue(App app)
    {
        if (_options.OnboardingCapturePath is { } capture)
        {
            var step = _options.OnboardingStep ?? OnboardingStep.Welcome;
            var page = _options.OnboardingPage ?? new OnboardingCapturePage(step, OnboardingLookPart.Wallpaper, Opening: false);
            _log.Info($"Welcome: opening at {step.Title()} ({page.Name}) for --onboarding-capture.");
            app.ShowOnboarding(firstRun: true, step, quiet: true, capturePath: capture, page);
            return;
        }

        var (show, reason) = OnboardingGate.Decide(OnboardingGate.IsFirstRun(_settings.LoadReport), _settings.Current, _options);
        if (!show)
        {
            _log.Debug($"Welcome: not shown ({reason}).");
            return;
        }
        _log.Info("Welcome: first run, showing the welcome.");
        _welcomeShownAsFirstRun = true;
        app.ShowOnboarding(firstRun: true, OnboardingStep.Welcome, quiet: QuietWindows, capturePath: null);
    }

    /// <summary>Settings › General's Show Welcome…: the same steps; finishing just closes.</summary>
    internal void ShowWelcomeAgain()
    {
        if (_quitting || _app is null) return;
        _log.Info("Welcome: shown again from Settings.");
        _app.ShowWelcomeAgain();
    }

    /// <summary>The welcome's window closed, saying how it ended. Captures record nothing.</summary>
    internal void WelcomeEnded(OnboardingEnding ending, bool firstRun, bool capture)
    {
        if (capture) return;
        _log.Info($"Welcome: ended ({ending}){(firstRun ? ", first run" : "")}.");
        OnboardingGate.Complete(ending, _settings, () =>
        {
            if (!_quitting) _app?.ShowSettingsWindow(null);
        });
    }

    /// <summary>Quitting with the first-run welcome still up counts as seen (it doesn't come back at the next launch).</summary>
    private void RecordWelcomeSeenAtQuit()
    {
        if (_welcomeShownAsFirstRun && !_settings.Current.OnboardingDone)
        {
            _settings.Update(OnboardingGate.Completed);
            _log.Info("Welcome: Lunalyr quit with it open; it counts as seen.");
        }
    }
}
