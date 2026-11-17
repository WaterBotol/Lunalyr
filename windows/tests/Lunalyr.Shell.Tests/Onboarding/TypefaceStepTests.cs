using Lunalyr.Core.Floating;
using Lunalyr.Shell.Onboarding;
using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell.Tests.Onboarding;

/// <summary>The welcome's typeface step (the Mac's 2ed3445d, WelcomeTests' typeface cases), in Windows' faces.</summary>
public sealed class TypefaceStepTests
{
    [Fact]
    public void TheStepIsFourthOfNine()
    {
        Assert.Equal("Step 4 of 9", OnboardingStep.Typeface.Position());
        Assert.Equal("Pick a typeface", OnboardingStep.Typeface.Title());
        Assert.Equal(OnboardingStep.Typeface, new OnboardingFlow(firstRun: true, OnboardingStep.Look).Next);
        Assert.Equal("You can change this later in Settings › Lyrics › Text.", OnboardingCopy.TypefaceLater);
    }

    [Fact]
    public void OnlyInstalledFacesAreOfferedTheSystemFaceFirst()
    {
        var bare = OnboardingTypefaces.Offered([]);
        Assert.Equal(["Segoe UI Variable"], bare.Select(f => f.Name));
        var windows = OnboardingTypefaces.Offered(["Arial", "bahnschrift", "Georgia", "Segoe UI"]);
        Assert.Equal(["Segoe UI Variable", "Bahnschrift", "Georgia"], windows.Select(f => f.Name));
        var office = OnboardingTypefaces.Offered(["Bahnschrift", "Georgia", "Rockwell", "Arial Rounded MT Bold"]);
        Assert.Equal(5, office.Count);
    }

    [Fact]
    public void TheSystemFaceIsChosenOnANewInstall()
    {
        var offered = OnboardingTypefaces.Offered(["Bahnschrift"]);
        Assert.Equal("Segoe UI Variable", OnboardingTypefaces.Chosen(ShippedDefaults.ForNewInstall(), offered)!.Name);
        var bahnschrift = new AppSettings { FontChoice = "installed", FontFamily = "bahnschrift" };
        Assert.Equal("Bahnschrift", OnboardingTypefaces.Chosen(bahnschrift, offered)!.Name);
        Assert.Null(OnboardingTypefaces.Chosen(new AppSettings { FontChoice = "installed", FontFamily = "Papyrus" }, offered));
    }

    [Fact]
    public void APickGoesToEveryStyleAndEveryMonitor()
    {
        // A new install with two monitors seen: the Floating style and each monitor's slots are kept.
        var settings = MonitorLooks.DisplaysChanged(ShippedDefaults.ForNewInstall(), ["a", "b"]);
        settings = settings.WithTypefaceEverywhere("installed", "Georgia");
        Assert.Equal("installed", settings.FontChoice);
        Assert.Equal("Georgia", settings.FontFamily);
        Assert.Equal("Georgia", settings.LyricStyleFloating!["fontFamily"].GetString());
        foreach (var (_, look) in settings.Looks!)
        {
            Assert.Equal("installed", look["fontChoice"].GetString());
            Assert.Equal("Georgia", look["fontFamily"].GetString());
        }
        // The floating lyrics read it too.
        Assert.Equal("Georgia", settings.WithLyricsSurface(LyricsSurface.Floating).FontFamily);
    }
}
