using Lunalyr.App.Onboarding;
using Lunalyr.Shell.Onboarding;

namespace Lunalyr.App;

public partial class App
{
    private OnboardingWindow? _onboarding;

    /// <summary>The welcome is on screen (Settings defers to it then, as on the Mac).</summary>
    private bool WelcomeIsUp => _onboarding is { IsShown: true };

    /// <summary>Opening Settings or the welcome while the welcome may be up (the Mac's navigation, <see cref="OnboardingNavigation"/>).</summary>
    private OnboardingNavigation Navigation(string? settingsPage) => new(
        () => ShowSettingsWindow(settingsPage),
        () => ShowOnboarding(firstRun: false, OnboardingStep.Welcome, _host.QuietWindows, capturePath: null),
        () => WelcomeIsUp,
        () => _onboarding?.BringForward(_host.QuietWindows));

    /// <summary>Settings › General's Show Welcome…: brings one already up forward, else shows it from the first step.</summary>
    internal void ShowWelcomeAgain() => Navigation(null).OpenOnboarding();

    /// <summary>
    /// Shows the welcome at <paramref name="step"/>. <paramref name="quiet"/>: unactivated, at the bottom of the z-order
    /// (checks while someone works at the PC). With <paramref name="capturePath"/> it is written as a PNG once it has
    /// settled, and Lunalyr quits.
    /// </summary>
    internal void ShowOnboarding(bool firstRun, OnboardingStep step, bool quiet, string? capturePath, OnboardingCapturePage? page = null)
    {
        try
        {
            if (_onboarding is { } open)
            {
                open.BringForward(quiet);
                return;
            }
            var window = new OnboardingWindow(_host, firstRun, step, capturePath is not null, _host.IsLaunchAtLoginOn, _host.SetLaunchAtLogin,
                page?.LookPart ?? OnboardingLookPart.Wallpaper, page?.Opening ?? false);
            _onboarding = window;
            window.Ended += ending =>
            {
                // Gone before the host acts on the ending, so Open Settings opens Settings rather than bringing it back.
                if (ReferenceEquals(_onboarding, window)) _onboarding = null;
                _host.WelcomeEnded(ending, window.FirstRun, capturePath is not null);
            };
            window.Open(quiet);
            if (capturePath is not null) window.CaptureWhenSettled(capturePath, page?.Name ?? step.Name(), _host.Quit);
        }
        catch (Exception ex)
        {
            // The welcome not opening must not take Lunalyr down; Settings has everything it offers.
            _host.ReportCrash(ex, "welcome window", fatal: false);
        }
    }
}
