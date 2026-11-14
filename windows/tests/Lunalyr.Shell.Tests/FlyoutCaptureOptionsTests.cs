using Lunalyr.Shell.Onboarding;
using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell.Tests;

/// <summary><c>--flyout-capture</c>: the tray flyout captured quietly for checks (docs/sweep/surfaces.md).</summary>
public class FlyoutCaptureOptionsTests
{
    [Fact]
    public void TheFlyoutCaptureTakesAFileAndAWait()
    {
        var options = ShellOptions.Parse(["--flyout-capture", "out\flyout.png", "--flyout-capture-after", "55"]);
        Assert.Empty(options.Problems);
        Assert.Equal(Path.GetFullPath("out\flyout.png"), options.FlyoutCapturePath);
        Assert.Equal(55, options.FlyoutCaptureAfter);
        Assert.Equal(6, ShellOptions.Parse(["--flyout-capture", "a.png"]).FlyoutCaptureAfter);
        Assert.Contains("--flyout-capture needs a value.", ShellOptions.Parse(["--flyout-capture"]).Problems);
        Assert.Contains("--flyout-capture-after soon: expected a number of seconds.", ShellOptions.Parse(["--flyout-capture-after", "soon"]).Problems);
    }

    [Fact]
    public void AFlyoutCaptureNeverShowsTheWelcome()
    {
        var (show, _) = OnboardingGate.Decide(firstRun: true, new AppSettings(), ShellOptions.Parse(["--flyout-capture", "a.png"]));
        Assert.False(show);
    }
}
