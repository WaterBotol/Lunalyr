using Lunalyr.Core.Lyrics;
using Lunalyr.Core.NowPlaying;
using Lunalyr.Shell.Flyout;
using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell.Tests;

/// <summary>The tray flyout's wording and state (Lunalyr.Shell.Flyout), from plain values: no window, no player.</summary>
public sealed class TrayFlyoutTests
{
    private static readonly MediaSourceApp Spotify = MediaSourceApp.FromAppId("Spotify.exe");

    private const MediaControls Full = MediaControls.Play | MediaControls.Pause | MediaControls.Next | MediaControls.Previous;

    private static AppStatus Playing(string player = "Spotify", string status = StatusText.Showing, string? source = "Word-by-word · LRCLIB") => new()
    {
        Player = player,
        PlayerAppId = "Spotify.exe",
        Playback = PlaybackStatus.Playing,
        Title = "Midnight Rain",
        Artist = "Taylor Swift",
        Status = status,
        LyricSource = source,
    };

    private static FlyoutInputs Inputs(AppStatus? status, MediaControls controls = Full, string? blocked = null) => new()
    {
        Status = status,
        Capabilities = status is null ? PlayerCapabilities.None : PlayerCapabilities.From(controls, PlaybackStatus.Playing, Spotify),
        PlaybackBlocked = blocked,
        SongTimingAvailable = status is not null,
    };

    // ---- Ported from the Mac ----

    // Ported from DesignSystemTests.swift: testTimingNudgeWordsTheOffsetBothWays
    [Fact]
    public void TestTimingNudgeWordsTheOffsetBothWays()
    {
        Assert.Equal("0.00 s", FlyoutText.FormatOffset(0));
        Assert.Equal("+0.25 s", FlyoutText.FormatOffset(0.25));
        Assert.Equal("−0.30 s", FlyoutText.FormatOffset(-0.3)); // a real minus sign
        Assert.Equal("No offset", FlyoutText.SpokenOffset(0));
        Assert.Equal("Lyrics 0.25 seconds earlier", FlyoutText.SpokenOffset(0.25));
        Assert.Equal("Lyrics 0.30 seconds later", FlyoutText.SpokenOffset(-0.3));
    }

    // Ported from MenuBarPanelPolishTests.swift: testPlayPauseShowsThePressAtOnce (consentHelp is macOS's Automation
    // prompt, which Windows doesn't have)
    [Fact]
    public void TestPlayPauseShowsThePressAtOnce()
    {
        Assert.True(FlyoutText.ShownPlaying(actual: false, pending: true));
        Assert.False(FlyoutText.ShownPlaying(actual: true, pending: false));
        Assert.True(FlyoutText.ShownPlaying(actual: true, pending: null));
        Assert.True(FlyoutText.PendingTimeout <= TimeSpan.FromSeconds(2));
    }

    // Ported from MenuBarPanelPolishTests.swift: testARepeatedLineIsStillANewLine
    [Fact]
    public void TestARepeatedLineIsStillANewLine()
    {
        var first = new LyricNow { Line = "yeah", Index = 4, Next = "yeah" };
        var second = new LyricNow { Line = "yeah", Index = 5, Next = null };
        Assert.NotEqual(first, second); // same words, new line
    }

    // Ported from MenuBarTests.swift: testHiddenLyricsAreNeverQuotedInThePanel (the source, content and spoken parts;
    // Windows has no "hide lyrics" setting yet, so the hidden display isn't ported)
    [Fact]
    public void TestHiddenLyricsAreNeverQuotedInThePanel()
    {
        Assert.Equal("LRCLIB · Word by word", FlyoutText.Source(new LyricNow { Source = "LRCLIB", Precision = "Word by word" }));
        Assert.Null(FlyoutText.Source(new LyricNow { IsLoading = true }));
        // UISnapshotCatalogue.menuBarSinging
        var singing = new LyricNow
        {
            Line = "Static on the radio, gold across the bay",
            Index = 3,
            Next = "We were never meant to stay",
            Source = "LRCLIB",
            Precision = "Word by word",
        };
        Assert.Equal(new LyricContent.Line("Static on the radio, gold across the bay", "We were never meant to stay"), LyricContent.Of(singing));
        var pause = new LyricNow { Next = "We were never meant to stay", Source = "LRCLIB", Precision = "Word by word" };
        Assert.Equal(new LyricContent.Between("We were never meant to stay"), LyricContent.Of(pause));
        Assert.Equal("Between lines. Next: We were never meant to stay",
            FlyoutText.Spoken(new LyricContent.Between("We were never meant to stay"), "♪"));
        Assert.IsType<LyricContent.Placeholder>(LyricContent.Of(new LyricNow { IsLoading = true }));
        Assert.IsType<LyricContent.Placeholder>(LyricContent.Of(new LyricNow { Next = "Stray", IsLoading = true })); // no lyrics showing yet
    }

    // Ported from MenuBarTests.swift: testThePanelNamesItsActionsForWhatTheyActOn (the mirroring banner and the SF Symbol
    // placeholder aren't on Windows)
    [Fact]
    public void TestThePanelNamesItsActionsForWhatTheyActOn()
    {
        var state = TrayFlyoutState.From(Inputs(Playing()));
        Assert.Equal("Pause wallpaper", state.PauseTitle);
        Assert.NotEqual(state.PauseTitle, FlyoutText.TransportTitle(playing: true));
        Assert.Equal("Pause music", FlyoutText.TransportTitle(playing: true));
        Assert.Equal("Play a song in Music", FlyoutText.Subtitle(hasTrack: false, artist: null, isStream: false, idlePrompt: "Play a song in Music"));
        Assert.False(string.IsNullOrEmpty(FlyoutText.PausedNote));
    }

    // ---- Windows: the transport ----

    [Fact]
    public void TheDemoAndNoPlaybackTurnTheTransportOffCalmly()
    {
        // The demo player takes nothing from other apps, but the run's switch is what the note says.
        var demo = TrayFlyoutState.From(Inputs(Playing("Lunalyr demo"), MediaControls.None, blocked: "--demo"));
        Assert.False(demo.Previous.Enabled || demo.PlayPause.Enabled || demo.Next.Enabled);
        Assert.Equal("Playback controls are off for this run (--demo).", demo.TransportNote);

        // A player that takes everything is still refused with --no-playback.
        var blocked = TrayFlyoutState.From(Inputs(Playing(), Full, blocked: "--no-playback"));
        Assert.False(blocked.Previous.Enabled || blocked.PlayPause.Enabled || blocked.Next.Enabled);
        Assert.Equal(FlyoutText.PlaybackOff("--no-playback"), blocked.TransportNote);
    }

    [Fact]
    public void ThePlayersOwnControlsDecideEachButton()
    {
        var all = TrayFlyoutState.From(Inputs(Playing()));
        Assert.True(all.Previous.Enabled && all.PlayPause.Enabled && all.Next.Enabled);
        Assert.Null(all.TransportNote);
        Assert.Equal("Pause music", all.PlayPause.Title);
        Assert.True(all.ShowsPause);

        var noPrevious = TrayFlyoutState.From(Inputs(Playing(), MediaControls.Play | MediaControls.Pause | MediaControls.Next));
        Assert.False(noPrevious.Previous.Enabled);
        Assert.True(noPrevious.PlayPause.Enabled && noPrevious.Next.Enabled);
        Assert.Equal("Spotify doesn't allow going back a track from other apps.", noPrevious.TransportNote);

        var none = TrayFlyoutState.From(Inputs(Playing(), MediaControls.None));
        Assert.False(none.Previous.Enabled || none.PlayPause.Enabled || none.Next.Enabled);
        Assert.Equal("Spotify doesn't let other apps control its playback.", none.TransportNote);
    }

    [Fact]
    public void APressShowsAtOnceUntilThePlayerAnswers()
    {
        var pressed = TrayFlyoutState.From(Inputs(Playing()) with { PendingPlaying = false });
        Assert.False(pressed.ShowsPause);
        Assert.Equal("Play music", pressed.PlayPause.Title);
    }

    [Fact]
    public void NothingPlayingShowsThePromptAndADimmedTransportWithoutANote()
    {
        var idle = TrayFlyoutState.From(Inputs(null));
        Assert.False(idle.HasTrack);
        Assert.Equal("Nothing playing", idle.Title);
        Assert.Equal(FlyoutText.IdlePrompt, idle.Subtitle);
        Assert.Null(idle.Player);
        Assert.False(idle.Previous.Enabled || idle.PlayPause.Enabled || idle.Next.Enabled);
        Assert.Null(idle.TransportNote);
        Assert.Equal("Lyrics appear here while a song plays.", idle.LyricPlaceholder);
        Assert.False(idle.TimingEnabled || idle.ResetEnabled || idle.SearchEnabled);
        Assert.Equal(FlyoutText.SearchYouTubeNeedsSong, idle.SearchNote);
    }

    // ---- Windows: the lyric block, timing and Search YouTube ----

    [Fact]
    public void TheFooterIsTheSourceOrAStatusThePlaceholderDoesntSay()
    {
        var showing = TrayFlyoutState.From(Inputs(Playing()) with
        {
            Lyric = new LyricNow { Line = "A line", Index = 0, Source = "LRCLIB", Precision = "Word-by-word" },
        });
        Assert.Equal("LRCLIB · Word-by-word", showing.LyricFooter);
        Assert.Equal("A line", showing.LyricSpoken);

        var offline = TrayFlyoutState.From(Inputs(Playing(status: StatusText.Offline, source: null)));
        Assert.Equal(StatusText.Offline, offline.LyricFooter);
        Assert.Equal("Waiting for lyrics…", offline.LyricPlaceholder);

        var looking = TrayFlyoutState.From(Inputs(Playing(status: StatusText.LookingUp, source: null)) with { Lyric = new LyricNow { IsLoading = true } });
        Assert.Null(looking.LyricFooter); // the placeholder already says it
        Assert.Equal("Looking for lyrics…", looking.LyricPlaceholder);

        var none = TrayFlyoutState.From(Inputs(Playing(status: StatusText.NoLyrics, source: null)) with { Lyric = new LyricNow { NotFound = true } });
        Assert.Equal("No synced lyrics for this song.", none.LyricPlaceholder);
        Assert.Null(none.LyricFooter);
    }

    [Fact]
    public void TimingNeedsASongAndResetNeedsAnOffset()
    {
        var zero = TrayFlyoutState.From(Inputs(Playing()));
        Assert.True(zero.TimingEnabled);
        Assert.False(zero.ResetEnabled);
        Assert.Equal("0.00 s", zero.OffsetText);

        var moved = TrayFlyoutState.From(Inputs(Playing()) with { SongOffset = 0.2 });
        Assert.True(moved.ResetEnabled);
        Assert.Equal("+0.20 s", moved.OffsetText);
        Assert.Equal("Lyrics 0.20 seconds earlier", moved.OffsetSpoken);

        // The song is known but its lookup hasn't started (no recording yet): nothing to adjust.
        var early = TrayFlyoutState.From(Inputs(Playing()) with { SongTimingAvailable = false, SongOffset = 0.5 });
        Assert.False(early.TimingEnabled || early.ResetEnabled);
        Assert.Equal("0.00 s", early.OffsetText);
    }

    [Fact]
    public void SearchYouTubeOpensThePickerWhileASongPlays()
    {
        // The Mac's picker window, not the browser: nothing outside Lunalyr opens, so a run without playback has it too.
        var off = TrayFlyoutState.From(Inputs(Playing(), blocked: "--demo"));
        Assert.True(off.SearchEnabled);
        Assert.Null(off.SearchNote);

        var on = TrayFlyoutState.From(Inputs(Playing()));
        Assert.True(on.SearchEnabled);
        Assert.Null(on.SearchNote);
        Assert.Equal("Search YouTube…", FlyoutText.SearchYouTube);
    }

    // ---- Windows: the wallpaper's quick controls ----

    [Fact]
    public void TheQuickControlsFollowTheSettings()
    {
        var settings = new AppSettings
        {
            WallpaperPaused = true,
            RomanizationDisplay = "off",
            CreditsLayer = false,
            BackgroundMode = "colourOnly",
            LyricComposition = "cascade",
        };
        var state = TrayFlyoutState.From(Inputs(Playing()) with { Settings = settings });
        Assert.True(state.WallpaperPaused);
        Assert.Equal(("Resume", "Resume wallpaper", "Lets the wallpaper move again"), (state.PauseLabel, state.PauseTitle, state.PauseHint));
        Assert.False(state.ReadingsOn);
        Assert.False(state.CreditsOn);
        Assert.Equal("colourOnly", state.Background);
        Assert.Equal("Cascade", TrayFlyoutState.Layouts[state.LayoutIndex].Title);

        var defaults = TrayFlyoutState.From(Inputs(Playing()));
        Assert.Equal(("Pause", "Pause wallpaper"), (defaults.PauseLabel, defaults.PauseTitle));
        Assert.True(defaults.ReadingsOn && defaults.CreditsOn);
        Assert.Equal("auto", defaults.Background);
        Assert.Equal("Constellation", TrayFlyoutState.Layouts[defaults.LayoutIndex].Title);
    }

    [Fact]
    public void TheLayoutPickerOffersMatchTheMusicAndEveryComposition()
    {
        Assert.Equal(AppSettings.Compositions, TrayFlyoutState.Layouts.Select(l => l.Value));
        Assert.Equal("Custom Mix", TrayFlyoutState.Layouts[0].Title);
        Assert.All(TrayFlyoutState.Layouts, l => Assert.False(string.IsNullOrWhiteSpace(l.Title)));
        Assert.Equal(TrayFlyoutState.Layouts.Count, TrayFlyoutState.Layouts.Select(l => l.Title).Distinct().Count());
    }

    // Ported from ShowingLayoutTests.swift: testCustomMixNamesTheLayoutThisSongTook (68bac63)
    [Fact]
    public void CustomMixNamesTheLayoutThisSongTook()
    {
        Assert.Equal("Custom Mix", SettingsCopy.ShowingLayoutTitle(AppSettings.AllLayoutsKey, null));
        Assert.Equal("Custom Mix · Hinge", SettingsCopy.ShowingLayoutTitle(AppSettings.AllLayoutsKey, "hinge"));
        Assert.Equal("Cutout", SettingsCopy.ShowingLayoutTitle("cutout", "hinge"));
        // In the tray: only while a song is playing.
        Assert.Equal("Custom Mix · Hinge", TrayFlyoutState.From(Inputs(Playing()) with { ShowingLayout = "hinge" }).CustomMixTitle);
        Assert.Equal("Custom Mix", TrayFlyoutState.From(Inputs(null) with { ShowingLayout = "hinge" }).CustomMixTitle);
    }

    [Fact]
    public void WallpaperTilesMapTheStoredValues()
    {
        // Every type has a tile, in the Mac's grid order; the stored value lights its own.
        Assert.Equal(AppSettings.BackgroundModes.Order(), TrayFlyoutState.Backgrounds.Select(b => b.Value).Order());
        Assert.Equal("colourOnly", TrayFlyoutState.BackgroundTile("gradient")); // Windows' old value: Colour only, Album gradient off
        Assert.Equal("auto", TrayFlyoutState.BackgroundTile("auto"));
        Assert.Equal("colourOnly", TrayFlyoutState.BackgroundTile("colourOnly"));
        Assert.Equal("colourOnly", TrayFlyoutState.BackgroundTile("colour"));
        Assert.Equal("visualizer", TrayFlyoutState.BackgroundTile("visualizer"));
        Assert.True(TrayFlyoutState.Backgrounds.Single(b => b.Value == "musicVideo").Available);
        Assert.All(TrayFlyoutState.Backgrounds, b => Assert.EndsWith(".", b.Summary));
    }

    [Fact]
    public void TheReadingsToggleIsBelowOrOffAndKeepsReplace()
    {
        Assert.Equal("below", TrayFlyoutState.ReadingsValue(true, "off"));
        Assert.Equal("off", TrayFlyoutState.ReadingsValue(false, "below"));
        Assert.Equal("off", TrayFlyoutState.ReadingsValue(false, "replace"));
        Assert.Equal("replace", TrayFlyoutState.ReadingsValue(true, "replace"));
        Assert.True(TrayFlyoutState.From(Inputs(Playing()) with { Settings = new AppSettings { RomanizationDisplay = "replace" } }).ReadingsOn);
    }

    [Fact]
    public void ANudgeLeavesTheMacsNote()
    {
        Assert.Equal("Lyrics 0.1 s earlier · +0.30 s for this song’s lyric source", FlyoutText.NudgeNote(0.1, 0.3));
        Assert.Equal("Lyrics 0.1 s later · -0.20 s for this song’s lyric source", FlyoutText.NudgeNote(-0.1, -0.2));
        Assert.Equal("Lyrics 0.1 s later · +0.00 s for this song’s lyric source", FlyoutText.NudgeNote(-0.1, 0));
    }

    // ---- LyricNow ----

    private static readonly LyricResult.Synced Song = new(
    [
        new LyricLine { Start = 10, End = 13, Text = " First line " },
        new LyricLine { Start = 14, End = 16, Text = "", IsInstrumental = true },
        new LyricLine { Start = 20, End = 23, Text = "Third line" },
        new LyricLine { Start = 30, End = 33, Text = "Last line" },
    ], new LyricProvenance("Word-by-word", "LRCLIB"));

    [Fact]
    public void LyricNowFindsTheLineBeingSungAndTheNextOne()
    {
        var before = LyricNow.At(Song, 5, isLoading: false, notFound: false);
        Assert.Null(before.Line);
        Assert.Equal(("LRCLIB", "Word-by-word"), (before.Source, before.Precision));

        var first = LyricNow.At(Song, 11, false, false);
        Assert.Equal(("First line", 0), (first.Line, first.Index)); // trimmed
        Assert.Null(first.Next); // the next row is an instrumental break

        var held = LyricNow.At(Song, 14.4, false, false);
        Assert.Null(held.Line); // an instrumental break
        Assert.Equal("Third line", held.Next);
        Assert.IsType<LyricContent.Between>(LyricContent.Of(held));

        var stale = LyricNow.At(Song, 25, false, false);
        Assert.Null(stale.Line); // 2 s after the voice stopped: the next line reads better
        Assert.Equal("Last line", stale.Next);

        var last = LyricNow.At(Song, 40, false, false);
        Assert.Equal("Last line", last.Line); // the last line stays: there's nothing after it
    }

    [Fact]
    public void LyricNowUsesReadingsShownInPlaceOfTheLines()
    {
        var now = LyricNow.At(Song, 21, false, false, new Dictionary<int, string> { [2] = "Saado rain", [3] = "Rasuto rain" });
        Assert.Equal(("Saado rain", "Rasuto rain"), (now.Line, now.Next));
    }

    [Fact]
    public void LyricNowWithoutTimedLyrics()
    {
        var plain = LyricNow.At(new LyricResult.Plain(["Only words"], new LyricProvenance("Plain", "lyrics.ovh")), 12, false, false);
        Assert.Null(plain.Line);
        Assert.Equal("lyrics.ovh", plain.Source);
        Assert.True(LyricNow.At(null, 12, isLoading: true, notFound: false).IsLoading);
        Assert.Null(LyricNow.At(Song, double.NaN, false, false).Line);
    }

    // ---- The command line ----

    [Fact]
    public void ThemeFlagForcesLightOrDark()
    {
        Assert.Equal("dark", ShellOptions.Parse(["--theme", "Dark"]).Theme);
        Assert.Equal("light", ShellOptions.Parse(["--theme", "light"]).Theme);
        Assert.Null(ShellOptions.Parse([]).Theme);
        var bad = ShellOptions.Parse(["--theme", "sepia"]);
        Assert.Null(bad.Theme);
        Assert.Contains(bad.Problems, p => p.Contains("--theme sepia", StringComparison.Ordinal));
    }
}
