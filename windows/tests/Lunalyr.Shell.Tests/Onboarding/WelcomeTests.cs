using System.Text.Json;
using Lunalyr.Core.Logging;
using Lunalyr.Core.NowPlaying;
using Lunalyr.Platform.Settings;
using Lunalyr.Platform.Windows.Tests.NowPlaying;
using Lunalyr.Shell.Onboarding;
using Lunalyr.Shell.Settings;
using Lunalyr.Shell.Tests.Fakes;
using Lunalyr.Tests.Shared;

namespace Lunalyr.Shell.Tests.Onboarding;

/// <summary>
/// The first-run welcome (Shell/Onboarding): what it promises, the order of its steps, how it ends, and when it shows by
/// itself. The Mac's WelcomeTests where they apply on Windows (same inputs and outputs), then Windows' own.
/// </summary>
public sealed class WelcomeTests
{
    // Ported from WelcomeTests.swift: testTheWelcomeExplainsThePromptAndTheWayBack
    // (Windows asks nothing about players, so the note says so instead of naming macOS's prompt; the tray's way back is
    // the ^ overflow and opening Lunalyr again, as the Mac's is the notch and Applications.)
    [Fact]
    public void TheWelcomeExplainsThePromptAndTheWayBack()
    {
        var note = OnboardingCopy.MusicNote;
        Assert.Contains("Windows asks you nothing", note);
        foreach (var player in new[] { "Spotify", "Apple Music", "YouTube Music", "SoundCloud" }) Assert.Contains(player, note);
        Assert.Contains("controls playback only when you use its buttons", note);
        Assert.Contains("^", OnboardingCopy.TrayNote);
        Assert.Contains("Opening Lunalyr again brings back Settings", OnboardingCopy.TrayNote);
        // What lyric services receive, as the Mac's About › Privacy says.
        Assert.Contains("title, artist, album and length", OnboardingCopy.Sources);
        Assert.Contains("Lyrics are never sent anywhere to be translated", OnboardingCopy.Translations);
        Assert.Contains("Win+Ctrl+Alt+[ and Win+Ctrl+Alt+]", OnboardingCopy.Timing("Win+Ctrl+Alt+[", "Win+Ctrl+Alt+]"));
        Assert.Equal("Play a song in Music and its lyrics appear on your desktop.",
            OnboardingCopy.Done(null, idlePrompt: "Play a song in Music"));
    }

    // Ported from WelcomeTests.swift: testStepsRunInOrderWithNoWayBackFromTheFirst
    [Fact]
    public void StepsRunInOrderWithNoWayBackFromTheFirst()
    {
        var flow = new OnboardingFlow(firstRun: true);
        Assert.Equal(OnboardingStep.Welcome, flow.Step);
        Assert.False(flow.CanGoBack); // Back is absent on the first step
        flow.Back();
        Assert.Equal(OnboardingStep.Welcome, flow.Step);
        var visited = new List<OnboardingStep> { flow.Step };
        while (!flow.IsLastStep)
        {
            flow.Advance();
            visited.Add(flow.Step);
        }
        Assert.Equal(OnboardingSteps.All, visited);
        Assert.Equal(6, visited.Count);
        flow.Advance();
        Assert.Equal(OnboardingStep.Done, flow.Step); // Continue stops at the end
        flow.Back();
        Assert.Equal(OnboardingStep.Tray, flow.Step);
        Assert.True(flow.CanGoBack);
        Assert.Equal("Step 2 of 6", OnboardingStep.Music.Position());
        Assert.Equal("Step 6 of 6", OnboardingStep.Done.Position());
    }

    // Ported from WelcomeTests.swift: testFinishingTheFirstRunCountsAndOnlyOpenSettingsOpensSettings
    // (Windows follows the players from launch with no prompt, so the Mac's "following the players, without asking"
    // checks have nothing to check here.)
    [Fact]
    public async Task FinishingTheFirstRunCountsAndOnlyOpenSettingsOpensSettings()
    {
        foreach (var (ending, opensSettings) in new[] { (OnboardingEnding.OpenSettings, true), (OnboardingEnding.Done, false), (OnboardingEnding.Closed, false) })
        {
            using var temp = new TempFolder();
            var navigated = new List<string>();
            await using (var store = AppSettingsStore.Open(temp.Path, Logger.Null))
            {
                Assert.False(store.Current.OnboardingDone);
                OnboardingGate.Complete(ending, store, () => navigated.Add("settings"));
                Assert.True(store.Current.OnboardingDone, ending.ToString());
                Assert.True(await store.FlushAsync());
            }
            var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(temp.Path, "settings.json"), TestContext.Current.CancellationToken));
            Assert.True(json.RootElement.GetProperty("onboardingDone").GetBoolean()); // saved under its own key
            Assert.Equal(opensSettings ? ["settings"] : [], navigated);
        }
    }

    // Ported from WelcomeTests.swift: testFinishingSaysHowItEndedAndShownAgainItJustCloses
    [Fact]
    public void FinishingSaysHowItEndedAndShownAgainItJustCloses()
    {
        var endings = new List<OnboardingEnding>();
        var firstRun = new OnboardingFlow(firstRun: true, OnboardingStep.Done, endings.Add);
        firstRun.Finish(OnboardingEnding.OpenSettings);
        firstRun.Finish(OnboardingEnding.Done);
        var again = new OnboardingFlow(firstRun: false, OnboardingStep.Done, endings.Add);
        again.Finish(OnboardingEnding.OpenSettings);
        again.Finish(OnboardingEnding.Done);
        Assert.Equal([OnboardingEnding.OpenSettings, OnboardingEnding.Done, OnboardingEnding.Done, OnboardingEnding.Done], endings);
    }

    // Ported from WelcomeTests.swift: testTheWindowIsAFixedSizeWithASeeThroughTitleBarAndClosesWithCommandW
    // (only its title and size: the NSWindow's style mask and ⌘W have no counterpart in a test without a window. The
    // Windows window is fixed-size with only a close button, closed by Esc or Alt+F4: checked in the captures.)
    [Fact]
    public void TheWindowIsAFixedSizeWithASeeThroughTitleBarAndClosesWithCommandW()
    {
        Assert.Equal("Welcome to Lunalyr", OnboardingCopy.WindowTitle); // Narrator and Alt+Tab name it
        // Fits a 1366 × 768 display at 100% (minus the taskbar) with room to spare, as the Mac's fits a 13-inch Air.
        Assert.True(OnboardingCopy.WindowHeight < 768 - 48 - 66);
        Assert.True(OnboardingCopy.WindowWidth < 1366 / 1.5);
    }

    // Ported from DesignSystemTests.swift: testNavigationRecordsRequestsAndDefersToOnboarding
    // (the Mac's settings request and System Settings links aren't part of it on Windows; the Mac's Settings window
    // deferring to onboarding is the last line.)
    [Fact]
    public void NavigationRecordsRequestsAndDefersToOnboarding()
    {
        var events = new List<string>();
        var onboardingUp = false;
        var navigation = new OnboardingNavigation(() => events.Add("settings"), () => events.Add("onboarding"),
            () => onboardingUp, () => events.Add("forward"));
        navigation.OpenSettings();
        navigation.OpenOnboarding();
        onboardingUp = true;
        navigation.OpenOnboarding();
        navigation.OpenSettings();
        Assert.Equal(["settings", "onboarding", "forward", "forward"], events);
    }

    // Ported from LocalPlayerReviewTests.swift: testAccessRowsAndOnboardingNameOnlyWhatIsThere (its list line; the rest
    // names Mac players by what is installed, which Windows doesn't look at)
    [Fact]
    public void AccessRowsAndOnboardingNameOnlyWhatIsThere()
    {
        Assert.Equal("A, B and C", OnboardingCopy.List(["A", "B", "C"]));
        Assert.Equal("A and B", OnboardingCopy.List(["A", "B"]));
        Assert.Equal("A", OnboardingCopy.List(["A"]));
        Assert.Equal("", OnboardingCopy.List([]));
    }

    // ---- Windows' own ----

    [Fact]
    public void EachStepHasItsButtonsAndEnterPressesTheLast()
    {
        var flow = new OnboardingFlow(firstRun: true);
        Assert.Equal([OnboardingButton.Continue], flow.Buttons);
        flow.Advance();
        Assert.Equal([OnboardingButton.Back, OnboardingButton.Continue], flow.Buttons);
        Assert.Equal(OnboardingButton.Continue, flow.DefaultButton);
        while (!flow.IsLastStep) flow.Advance();
        Assert.Equal([OnboardingButton.Back, OnboardingButton.Done, OnboardingButton.OpenSettings], flow.Buttons);
        Assert.Equal(OnboardingButton.OpenSettings, flow.DefaultButton);

        var again = new OnboardingFlow(firstRun: false, OnboardingStep.Done);
        Assert.Equal([OnboardingButton.Back, OnboardingButton.Done], again.Buttons);
        Assert.Equal(OnboardingButton.Done, again.DefaultButton);
    }

    [Fact]
    public void PressingTheButtonsMovesAndEnds()
    {
        var endings = new List<OnboardingEnding>();
        var changes = new List<OnboardingStepChange>();
        var flow = new OnboardingFlow(firstRun: true, onFinish: endings.Add);
        flow.StepChanged += (_, change) => changes.Add(change);
        flow.Press(OnboardingButton.Continue);
        flow.Press(OnboardingButton.Continue);
        flow.Press(OnboardingButton.Back);
        Assert.Equal(OnboardingStep.Music, flow.Step);
        Assert.Equal([true, true, false], changes.Select(c => c.Forward));
        Assert.Empty(endings); // moving between steps never ends it
        flow.Press(OnboardingButton.Done);
        flow.Press(OnboardingButton.OpenSettings);
        Assert.Equal([OnboardingEnding.Done, OnboardingEnding.OpenSettings], endings);
    }

    [Fact]
    public void TheFooterSaysWhereEachButtonGoes()
    {
        var flow = new OnboardingFlow(firstRun: true, OnboardingStep.Look);
        Assert.Equal("Back to Your music (Alt+Left)", OnboardingCopy.Help(OnboardingButton.Back, flow));
        Assert.Equal("On to Lyrics & readings (Enter)", OnboardingCopy.Help(OnboardingButton.Continue, flow));
        Assert.Equal("Close and leave Lunalyr in the tray", OnboardingCopy.Help(OnboardingButton.Done, new OnboardingFlow(true, OnboardingStep.Done)));
        Assert.Equal("Step 3 of 6, Pick a look", OnboardingCopy.Announcement(OnboardingStep.Look));
        Assert.Equal(["Welcome", "Your music", "Pick a look", "Lyrics & readings", "The tray", "Done"], OnboardingSteps.All.Select(s => s.Title()));
    }

    [Theory]
    [InlineData("music", OnboardingStep.Music)]
    [InlineData("LOOK", OnboardingStep.Look)]
    [InlineData("Lyrics & readings", OnboardingStep.Lyrics)]
    [InlineData("tray", OnboardingStep.Tray)]
    [InlineData("menu-bar", OnboardingStep.Tray)]
    [InlineData("6", OnboardingStep.Done)]
    [InlineData("1", OnboardingStep.Welcome)]
    public void StepsAreFoundByNameTitleOrNumber(string text, OnboardingStep step) => Assert.Equal(step, OnboardingSteps.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("7")]
    [InlineData("settings")]
    [InlineData(null)]
    public void UnknownStepsAreNone(string? text) => Assert.Null(OnboardingSteps.Parse(text));

    [Fact]
    public void TheDoneStepSaysWhatIsPlayingOrWhatToDo()
    {
        Assert.Equal("“Neon Harbour” is playing, so its lyrics are on your desktop now.", OnboardingCopy.Done("Neon Harbour"));
        Assert.Equal("Play a song in any music app and its lyrics appear on your desktop.", OnboardingCopy.Done(null));
        Assert.Equal("Wallpaper: Match the music", OnboardingCopy.WallpaperRow(new AppSettings()));
        Assert.Equal("Wallpaper: Imported image or video", OnboardingCopy.WallpaperRow(new AppSettings { BackgroundMode = "custom" }));
        Assert.Equal("Beach.mp4", OnboardingCopy.WallpaperDetail(new AppSettings { BackgroundMode = "custom", WallpaperName = "Beach.mp4" }));
        Assert.StartsWith("Beach.mp4: ", OnboardingCopy.LookSummary(new AppSettings { BackgroundMode = "custom", WallpaperName = "Beach.mp4" }));
        Assert.Equal(SettingsCopy.WallpaperTypeOf("pattern").Summary, OnboardingCopy.LookSummary(new AppSettings { BackgroundMode = "pattern" }));
    }

    [Fact]
    public void ThePlayersStepListsWhatWindowsShares()
    {
        var time = new ManualTimeProvider();
        using var sessions = new FakeMediaSessionSource();
        Assert.Empty(OnboardingPlayers.Rows(sessions));
        Assert.Empty(OnboardingPlayers.Rows(null));
        var spotify = sessions.Add(new FakeMediaSession("Spotify.exe", time).SetPlayback(PlaybackStatus.Playing));
        sessions.Add(new FakeMediaSession("MSEdge", time).SetPlayback(PlaybackStatus.Paused), makeCurrent: false);
        var rows = OnboardingPlayers.Rows(sessions);
        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].Followed);
        Assert.Equal("Playing · Lunalyr follows this one now", rows[0].Status);
        Assert.False(rows[1].Followed);
        Assert.Equal("Paused", rows[1].Status);
        Assert.Empty(spotify.Commands); // listing players sends them nothing
    }

    // ---- When it shows ----

    [Fact]
    public async Task OnlyALaunchThatFoundNoSettingsIsAFirstRun()
    {
        using var empty = new TempFolder();
        await using (var store = AppSettingsStore.Open(empty.Path, Logger.Null))
        {
            Assert.True(OnboardingGate.IsFirstRun(store.LoadReport));
        }

        // Someone upgrading from a test build: the file is there (without the new key).
        using var upgrading = new TempFolder();
        await File.WriteAllTextAsync(Path.Combine(upgrading.Path, "settings.json"), """{ "schemaVersion": 1, "lyricComposition": "spoken" }""",
            TestContext.Current.CancellationToken);
        await using (var store = AppSettingsStore.Open(upgrading.Path, Logger.Null))
        {
            Assert.False(OnboardingGate.IsFirstRun(store.LoadReport));
            Assert.False(store.Current.OnboardingDone);
            Assert.False(OnboardingGate.Decide(OnboardingGate.IsFirstRun(store.LoadReport), store.Current, ShellOptions.Parse([])).Show);
        }

        // A damaged file is an existing user's, not a first run.
        using var damaged = new TempFolder();
        await File.WriteAllTextAsync(Path.Combine(damaged.Path, "settings.json"), "{ not json", TestContext.Current.CancellationToken);
        await using (var store = AppSettingsStore.Open(damaged.Path, Logger.Null))
        {
            Assert.Equal(SettingsSource.Defaults, store.LoadReport.Source);
            Assert.False(OnboardingGate.IsFirstRun(store.LoadReport));
        }

        // Only the backup left (a save interrupted): an existing user too.
        using var backupOnly = new TempFolder();
        await File.WriteAllTextAsync(Path.Combine(backupOnly.Path, "settings.json.bak"), """{ "schemaVersion": 1 }""",
            TestContext.Current.CancellationToken);
        await using (var store = AppSettingsStore.Open(backupOnly.Path, Logger.Null))
        {
            Assert.False(OnboardingGate.IsFirstRun(store.LoadReport));
        }
    }

    [Fact]
    public void AFirstRunShowsItUnlessTheLaunchIsADemoACheckOrAskedForAnotherWindow()
    {
        var fresh = new AppSettings();
        Assert.True(OnboardingGate.Decide(firstRun: true, fresh, ShellOptions.Parse([])).Show);
        // Links, --quiet-windows, --no-playback and --data don't stop a first run.
        Assert.True(OnboardingGate.Decide(true, fresh, ShellOptions.Parse(["lunalyr://card", "--quiet-windows", "--no-playback", "--data", "x"])).Show);

        Assert.False(OnboardingGate.Decide(firstRun: false, fresh, ShellOptions.Parse([])).Show);
        Assert.False(OnboardingGate.Decide(true, fresh with { OnboardingDone = true }, ShellOptions.Parse([])).Show);
        string[][] never =
        [
            ["--demo"], ["--settings"], ["--settings-page", "general"], ["--lyric-card"], ["--seconds", "20"], ["--stats", "s.json"],
            ["--floating-capture", "f.png"], ["--ticker-capture", "t.png"], ["--hud-capture", "h.png"],
            ["--onboarding-capture", "o.png"],
        ];
        foreach (var args in never)
        {
            var (show, reason) = OnboardingGate.Decide(true, fresh, ShellOptions.Parse(args));
            Assert.False(show, string.Join(' ', args));
            Assert.False(string.IsNullOrEmpty(reason));
        }
    }

    [Fact]
    public void FinishingIsRecordedOnceAndNeverCleared()
    {
        var done = OnboardingGate.Completed(new AppSettings());
        Assert.True(done.OnboardingDone);
        Assert.Same(done, OnboardingGate.Completed(done));
        Assert.True(done.Normalized().OnboardingDone);
        Assert.True(OnboardingGate.OpensSettings(OnboardingEnding.OpenSettings));
        Assert.False(OnboardingGate.OpensSettings(OnboardingEnding.Closed));
    }

    // ---- The capture flag ----

    [Fact]
    public void TheCaptureFlagTakesAFileAndAStep()
    {
        var options = ShellOptions.Parse(["--onboarding-capture", "C:\\temp\\welcome.png", "--onboarding-step", "tray"]);
        Assert.Equal(Path.GetFullPath("C:\\temp\\welcome.png"), options.OnboardingCapturePath);
        Assert.Equal(OnboardingStep.Tray, options.OnboardingStep);
        Assert.Empty(options.Problems);

        var bad = ShellOptions.Parse(["--onboarding-capture", "x.png", "--onboarding-step", "settings"]);
        Assert.Null(bad.OnboardingStep);
        Assert.Contains(bad.Problems, p => p.Contains("--onboarding-step settings", StringComparison.Ordinal));
        Assert.Contains(ShellOptions.Parse(["--onboarding-capture"]).Problems, p => p.Contains("needs a value", StringComparison.Ordinal));
        Assert.Contains(ShellOptions.Parse(["--onboarding-step", "look"]).Problems, p => p.Contains("goes with --onboarding-capture", StringComparison.Ordinal));
        Assert.Null(ShellOptions.Parse(["--onboarding-capture", "x.png"]).OnboardingStep); // the capture starts at Welcome
    }
}
