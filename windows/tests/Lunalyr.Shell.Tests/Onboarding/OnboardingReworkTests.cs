using Lunalyr.Core.Lyrics;
using Lunalyr.Shell.Onboarding;
using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell.Tests.Onboarding;

/// <summary>
/// The welcome reworked to eight steps (the Mac's Test Build 88), its glass logo opening and recordings, and the Settings
/// loader: WelcomeTests.swift's new tests, WelcomeGlassTests.swift and SettingsLoaderTests.swift, with the same inputs
/// and expected values. Nothing is played: the media are checked by reading their files.
/// </summary>
public sealed class OnboardingReworkTests
{
    // ---- The reworked steps ----

    // Ported from WelcomeTests.swift: testTheNewStepsSayWhatIsSentAndWhere
    // (MusicVideoFraming's titles are not checked: "Show the whole video" is another item of the port, not in this branch;
    // the Mac's library videos are Windows' video file chosen for a song, so the line says "needs nothing sent".)
    [Fact]
    public void TheNewStepsSayWhatIsSentAndWhere()
    {
        Assert.StartsWith("Off by default", OnboardingCopy.YouTubePrivacy);
        Assert.Contains("title and artist are sent to YouTube", OnboardingCopy.YouTubePrivacy);
        Assert.Contains("needs nothing sent", OnboardingCopy.Videos);
        Assert.StartsWith("Off by default", OnboardingCopy.DiscordPrivacy);
        Assert.Contains("only to the Discord app on this PC", OnboardingCopy.DiscordPrivacy);
        Assert.Contains("clears when playback stops", OnboardingCopy.DiscordPrivacy);
        Assert.Contains("Apple", OnboardingCopy.DiscordPrivacy); // the cover lookup is named
        Assert.Equal("Custom Mix picks a layout to suit each song.", OnboardingCopy.LayoutCaption(null));
        Assert.Equal("Custom Mix picks a layout to suit each song.", OnboardingCopy.LayoutCaption("automatic"));
        Assert.Equal(SettingsCopy.Layouts.Single(l => l.Value == "broadside").Detail, OnboardingCopy.LayoutCaption("broadside"));
        // Sentence case, no periods in titles.
        foreach (var step in OnboardingSteps.All)
        {
            Assert.False(step.Title().EndsWith('.'), step.Title());
            Assert.NotEqual(step.Title().ToUpperInvariant(), step.Title());
        }
    }

    // Ported from WelcomeTests.swift: testTheNewSwitchesAreBoundToTheirSettings
    // (Windows' settings are one record saved by the store: the Discord key's round trip is SettingsAndOptionsTests'. The
    // library framing menu isn't in this branch, as above.)
    [Fact]
    public void TheNewSwitchesAreBoundToTheirSettings()
    {
        var settings = new AppSettings();
        Assert.False(settings.DiscordPresence); // off by default
        Assert.True((settings with { DiscordPresence = true }).Normalized().DiscordPresence);
        Assert.False(settings.MusicVideoOnline); // searching YouTube is off by default

        var pattern = settings with { BackgroundMode = "pattern" };
        Assert.NotEqual("musicVideo", pattern.BackgroundMode);
        var on = OnboardingCopy.WithVideoWallpaper(pattern, on: true);
        Assert.Equal("musicVideo", on.BackgroundMode);
        var off = OnboardingCopy.WithVideoWallpaper(on, on: false);
        Assert.Equal("pattern", off.BackgroundMode); // back to the look chosen before
        Assert.Same(pattern, OnboardingCopy.WithVideoWallpaper(pattern, on: false)); // off while off changes nothing
    }

    // Ported from WelcomeTests.swift: testTheLookStepShowsTheLayoutInUse
    [Fact]
    public void TheLookStepShowsTheLayoutInUse()
    {
        Assert.Equal(LayoutChoices.Featured, LayoutChoices.Shown(null));
        Assert.Equal(LayoutChoices.Featured, LayoutChoices.Shown("automatic")); // Windows' Custom Mix is the Mac's nil
        Assert.Equal(LayoutChoices.Featured, LayoutChoices.Shown("portal"));
        var withEditorial = LayoutChoices.Shown("constellationEditorial");
        Assert.Equal("constellationEditorial", withEditorial[0]);
        Assert.Equal(LayoutChoices.Featured.Count, withEditorial.Count);
        Assert.Equal(0, LayoutThumbnail.Time(0));
        Assert.Equal(1, LayoutThumbnail.Time(LayoutThumbnail.Cycle + 1), 4);
        Assert.Equal(LayoutThumbnail.Held, LayoutThumbnail.Time(LayoutThumbnail.Cycle * LayoutThumbnail.Plays + 1));
        Assert.Equal(LayoutThumbnail.SampleLines.SelectMany(l => l).Count(), LayoutThumbnail.Words.Count);

        var flow = new OnboardingFlow(firstRun: true, OnboardingStep.Look);
        Assert.Equal(OnboardingLookPart.Wallpaper, flow.LookPart);
        flow.LookPart = OnboardingLookPart.Lyrics;
        flow.Advance();
        flow.Back();
        Assert.Equal(OnboardingLookPart.Lyrics, flow.LookPart); // Back returns to the half that was showing
    }

    // ---- Windows' own, for the layouts ----

    [Fact]
    public void TheLayoutsFeaturedAreRealAndTheRestAreCounted()
    {
        Assert.All(LayoutChoices.Featured, id => Assert.Contains(id, AppSettings.Compositions));
        Assert.Equal(14, LayoutChoices.AllCount);
        Assert.Equal("10 more in Settings, with Custom Mix.", OnboardingCopy.MoreLayouts(LayoutChoices.AllCount - LayoutChoices.Featured.Count));
        Assert.Equal(LayoutChoices.Featured, LayoutChoices.Shown("no-such-layout")); // an unknown id leads nothing
    }

    [Fact]
    public void TheSampleLineIsOneWordTimedLine()
    {
        var scene = LayoutThumbnail.Scene();
        var synced = Assert.IsType<LyricResult.Synced>(scene.Lyrics);
        var line = Assert.Single(synced.Lines);
        Assert.Equal("Static on the radio, gold across the bay", line.Text);
        Assert.Equal(1, line.Start, 3);
        Assert.True(LayoutThumbnail.StillTime > LayoutThumbnail.Words[^1].End); // every word sung
        Assert.True(LayoutThumbnail.StillTime < LayoutThumbnail.Held);
        Assert.Null(scene.Title); // no opening title card over the thumbnail
        Assert.True(scene.Style.SizeFraction > Lunalyr.Render.Scene.LyricStyle.Default.SizeFraction); // a small stage, a large line
    }

    // ---- Recordings and the opening ----

    // Ported from WelcomeTests.swift: testTheRecordingsAreBundledSilentAndSmall
    [Fact]
    public void TheRecordingsAreBundledSilentAndSmall()
    {
        long bytes = 0;
        foreach (var clip in OnboardingClips.All)
        {
            var video = AppFile(clip.VideoPath());
            var poster = AppFile(clip.PosterPath());
            Assert.True(File.Exists(poster), $"{clip.Resource()}.jpg is bundled");
            if (!clip.HasVideo())
            {
                // Windows' hero is a still of Lunalyr's own drawing: no recording of a real album's cover ships.
                Assert.False(File.Exists(video), $"{clip.Resource()}.mp4 must not ship");
                bytes += new FileInfo(poster).Length;
                continue;
            }
            Assert.True(File.Exists(video), $"{clip.Resource()}.mp4 is bundled");
            var movie = Mp4.Read(video);
            Assert.DoesNotContain("soun", movie.Handlers); // no audio track
            Assert.Equal(["vide"], movie.Handlers);
            Assert.True(movie.Duration <= 8.05, $"{clip}: 8 s at most ({movie.Duration:0.00})");
            Assert.True(movie.Width <= 960, clip.ToString());
            Assert.Equal(OnboardingClips.Aspect, movie.Width / movie.Height, 2);
            bytes += new FileInfo(video).Length + new FileInfo(poster).Length;
        }
        bytes += new FileInfo(AppFile(OnboardingAssets.DiscordStatus)).Length;
        Assert.True(bytes <= 6_000_000, $"onboarding's media: {bytes} bytes");
        Assert.DoesNotContain("soun", Mp4.Read(AppFile(OnboardingAssets.GlassVideo)).Handlers); // the opening has no audio track either
    }

    // Ported from WelcomeTests.swift: testRecordingsPlayOnlyWhenAllowedAndFillTheirBox
    // (the Mac's default "isUnitTesting" argument has no Windows counterpart: tests pass it.)
    [Fact]
    public void RecordingsPlayOnlyWhenAllowedAndFillTheirBox()
    {
        Assert.True(OnboardingClips.Plays(live: true, reduceMotion: false, windowActive: true, isUnitTesting: false));
        Assert.False(OnboardingClips.Plays(live: true, reduceMotion: true, windowActive: true, isUnitTesting: false));
        Assert.False(OnboardingClips.Plays(live: false, reduceMotion: false, windowActive: true, isUnitTesting: false));
        Assert.False(OnboardingClips.Plays(live: true, reduceMotion: false, windowActive: false, isUnitTesting: false));
        Assert.False(OnboardingClips.Plays(live: true, reduceMotion: false, windowActive: true, isUnitTesting: true));
        foreach (var clip in OnboardingClips.All)
        {
            Assert.True(File.Exists(AppFile(clip.PosterPath())), clip.ToString());
            foreach (var (width, height) in new[] { (392.0, 253.0), (196.0, 64.0), (176.0, 104.0) })
            {
                var frame = clip.Frame(width, height);
                Assert.True(frame.X <= 0.001, $"{clip} {width}x{height}");
                Assert.True(frame.Y <= 0.001, $"{clip} {width}x{height}");
                Assert.True(frame.Right >= width - 0.001, $"{clip} {width}x{height}");
                Assert.True(frame.Bottom >= height - 0.001, $"{clip} {width}x{height}");
                Assert.Equal(OnboardingClips.Aspect, frame.Width / frame.Height, 3);
            }
        }
    }

    // Ported from WelcomeTests.swift: testTheOpeningPlaysOnceOnTheWelcomeAndFillsHalfTheWindow
    // (with Windows' window size; the Mac's default "never in the test host" argument is passed explicitly.)
    [Fact]
    public void TheOpeningPlaysOnceOnTheWelcomeAndFillsHalfTheWindow()
    {
        Assert.True(WelcomeGlass.OpensOnIntro(OnboardingStep.Welcome, reduceMotion: false, alreadyPlayed: false, isUnitTesting: false, hasVideo: true));
        Assert.False(WelcomeGlass.OpensOnIntro(OnboardingStep.Music, reduceMotion: false, alreadyPlayed: false, isUnitTesting: false, hasVideo: true));
        Assert.False(WelcomeGlass.OpensOnIntro(OnboardingStep.Welcome, reduceMotion: true, alreadyPlayed: false, isUnitTesting: false, hasVideo: true));
        Assert.False(WelcomeGlass.OpensOnIntro(OnboardingStep.Welcome, reduceMotion: false, alreadyPlayed: true, isUnitTesting: false, hasVideo: true));
        Assert.False(WelcomeGlass.OpensOnIntro(OnboardingStep.Welcome, reduceMotion: false, alreadyPlayed: false, isUnitTesting: false, hasVideo: false));
        Assert.False(WelcomeGlass.OpensOnIntro(OnboardingStep.Welcome, reduceMotion: false, alreadyPlayed: false, isUnitTesting: true, hasVideo: true));
        var (width, height) = (OnboardingCopy.WindowWidth, OnboardingCopy.WindowHeight);
        var side = WelcomeGlass.VideoSide(width, height);
        Assert.True(side <= height);
        Assert.Equal(0.5, side * WelcomeGlass.TileShare / height, 2);
    }

    // Ported from WelcomeGlassTests.swift: testPlaysOncePerLaunchWithMotionAllowedOutsideTests
    [Fact]
    public void PlaysOncePerLaunchWithMotionAllowedOutsideTests()
    {
        Assert.True(WelcomeGlass.ShouldPlay(reduceMotion: false, alreadyPlayed: false, isUnitTesting: false, hasVideo: true));
        Assert.False(WelcomeGlass.ShouldPlay(reduceMotion: true, alreadyPlayed: false, isUnitTesting: false, hasVideo: true)); // straight to the welcome
        Assert.False(WelcomeGlass.ShouldPlay(reduceMotion: false, alreadyPlayed: true, isUnitTesting: false, hasVideo: true)); // Back doesn't replay it
        Assert.False(WelcomeGlass.ShouldPlay(reduceMotion: false, alreadyPlayed: false, isUnitTesting: true, hasVideo: true));
        Assert.False(WelcomeGlass.ShouldPlay(reduceMotion: false, alreadyPlayed: false, isUnitTesting: false, hasVideo: false));
    }

    // Ported from WelcomeGlassTests.swift: testTheBundledVideoIsSilentSquareAndShort
    [Fact]
    public void TheBundledVideoIsSilentSquareAndShort()
    {
        var path = AppFile(OnboardingAssets.GlassVideo);
        Assert.True(File.Exists(path), "WelcomeGlass.mp4 is in the app");
        var movie = Mp4.Read(path);
        Assert.DoesNotContain("soun", movie.Handlers); // no sound track at all
        Assert.Contains("vide", movie.Handlers);
        Assert.Equal(movie.Width, movie.Height); // square
        Assert.True(movie.Duration < 5, "an opening, not a wait");
        Assert.True(new FileInfo(path).Length < 2_000_000, "small enough to ship");
    }

    // Ported from WelcomeTests.swift: testEverySymbolOnboardingNamesExists (the Discord recording's part: SF Symbols and
    // the menu-bar icon have no Windows counterpart to look up)
    [Fact]
    public void TheDiscordStatusRecordingIsThere()
    {
        var gif = File.ReadAllBytes(AppFile(OnboardingAssets.DiscordStatus));
        Assert.Equal("GIF8", System.Text.Encoding.ASCII.GetString(gif, 0, 4));
        Assert.Equal(254, gif[6] | gif[7] << 8); // drawn at 2x: 127 points wide
        Assert.True(File.Exists(AppFile(OnboardingAssets.Icon)), "Lunalyr's icon for the welcome's picture");
    }

    // ---- The Settings loader ----

    // Ported from SettingsLoaderTests.swift: testShowsOncePerLaunchWithMotionAllowedOutsideTests
    [Fact]
    public void ShowsOncePerLaunchWithMotionAllowedOutsideTests()
    {
        Assert.True(SettingsLoader.ShouldShow(reduceMotion: false, alreadyShown: false, isUnitTesting: false, hasVideo: true));
        Assert.False(SettingsLoader.ShouldShow(reduceMotion: true, alreadyShown: false, isUnitTesting: false, hasVideo: true));
        Assert.False(SettingsLoader.ShouldShow(reduceMotion: false, alreadyShown: true, isUnitTesting: false, hasVideo: true)); // only the first opening of a launch
        Assert.False(SettingsLoader.ShouldShow(reduceMotion: false, alreadyShown: false, isUnitTesting: true, hasVideo: true));
        Assert.False(SettingsLoader.ShouldShow(reduceMotion: false, alreadyShown: false, isUnitTesting: false, hasVideo: false));
    }

    // Ported from SettingsLoaderTests.swift: testNeverAWaitOfItsOwnMaking
    [Fact]
    public void NeverAWaitOfItsOwnMaking()
    {
        Assert.Equal(SettingsLoader.MinimumShown, SettingsLoader.Remaining(0));
        Assert.Equal(SettingsLoader.MinimumShown - 0.5, SettingsLoader.Remaining(0.5), 9);
        Assert.Equal(0, SettingsLoader.Remaining(2)); // a slow build is not held up further
        Assert.True(SettingsLoader.MinimumShown <= 1, "under a second on a fast PC");
        Assert.True(SettingsLoader.Rate > 1, "faster than the welcome");
    }

    [Fact]
    public void QuietChecksSkipTheLoaderUnlessACaptureAsksForIt()
    {
        Assert.True(SettingsLoader.ShouldShow(reduceMotion: false, alreadyShown: false, hasVideo: true, quiet: false, capture: false));
        Assert.False(SettingsLoader.ShouldShow(reduceMotion: false, alreadyShown: false, hasVideo: true, quiet: true, capture: false));
        Assert.False(SettingsLoader.ShouldShow(reduceMotion: true, alreadyShown: false, hasVideo: true, quiet: false, capture: false));
        Assert.True(SettingsLoader.ShouldShow(reduceMotion: true, alreadyShown: false, hasVideo: true, quiet: true, capture: true));
        Assert.False(SettingsLoader.ShouldShow(reduceMotion: false, alreadyShown: true, hasVideo: true, quiet: true, capture: true));
        Assert.False(SettingsLoader.ShouldShow(reduceMotion: false, alreadyShown: false, hasVideo: false, quiet: true, capture: true));
        Assert.Equal(1600 * SettingsLoader.Zoom, SettingsLoader.VideoSide(2000, 1600), 6); // the shorter side, zoomed
    }

    /// <summary>A file the app ships next to Lunalyr.exe, read from the repo's copy (src\Lunalyr.App\…).</summary>
    private static string AppFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Lunalyr.slnx"))) return Path.Combine(dir.FullName, "src", "Lunalyr.App", relative);
        }
        throw new DirectoryNotFoundException("The repo's root (Lunalyr.slnx) wasn't found above the test's folder.");
    }
}
