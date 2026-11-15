using Lunalyr.Core.Visualisers;
using Lunalyr.Shell.Extras;
using Lunalyr.Shell.Flyout;
using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell.Tests.Extras;

/// <summary>
/// What shortcuts and links do and the notes that confirm it, over settings in memory and fake hooks: no window, no
/// HUD, no player. Nothing here sends a playback command (none of these actions has one).
/// </summary>
public sealed class LunalyrExtrasTests
{
    private AppSettings _settings = new();
    private readonly List<string> _shown = [];
    private readonly List<string> _spoken = [];

    private bool Update(Func<AppSettings, AppSettings> change)
    {
        _settings = change(_settings).Normalized();
        return true;
    }

    private LunalyrExtras Extras(ExtrasHooks? hooks = null, bool hud = true)
    {
        var extras = new LunalyrExtras(() => _settings, Update, hooks) { Announce = _spoken.Add };
        if (hud) extras.PresentHud = (text, _) => _shown.Add(text);
        return extras;
    }

    // Ported from ActionHUDTests.swift: testTheHUDShowsWhenTheWallpaperIsUnlikelyToBeSeen.
    [Fact]
    public void TestTheHUDShowsWhenTheWallpaperIsUnlikelyToBeSeen()
    {
        // A control in a Lunalyr window, over a visible wallpaper, keeps the wallpaper note.
        Assert.False(LunalyrExtras.ShowsInHud(NoteOrigin.Window, wallpaperPaused: false, wallpaperCovered: false));
        Assert.True(LunalyrExtras.ShowsInHud(NoteOrigin.Elsewhere, wallpaperPaused: false, wallpaperCovered: false));
        Assert.True(LunalyrExtras.ShowsInHud(NoteOrigin.Window, wallpaperPaused: true, wallpaperCovered: false)); // a paused wallpaper draws no note
        Assert.True(LunalyrExtras.ShowsInHud(NoteOrigin.Window, wallpaperPaused: false, wallpaperCovered: true));
    }

    // Ported from ActionHUDTests.swift: testShortcutsAndLinksPostToTheHUDAndMarkTheNote. No display is mirrored here, so
    // "Lyrics shown" is not "shown while mirroring" (MirroringTests has that).
    [Fact]
    public void TestShortcutsAndLinksPostToTheHUDAndMarkTheNote()
    {
        var extras = Extras();
        extras.HideLyrics();
        Assert.Empty(_shown); // from a Lunalyr window, over a visible wallpaper
        Assert.False(extras.Note?.InHud);

        extras.Performing(NoteOrigin.Elsewhere, extras.ShowLyrics);
        Assert.Equal(["Lyrics shown"], _shown);
        Assert.True(extras.Note?.InHud);
        Assert.Equal(NoteOrigin.Window, extras.ActionOrigin); // the origin lasts only while the action runs

        extras.Post("Stopped sharing", "person.2.slash", NoteOrigin.Elsewhere);
        Assert.Equal("Stopped sharing", _shown[^1]);

        extras.ToggleWallpaperPause();
        Assert.Equal("Wallpaper paused", _shown[^1]); // pausing from the window still says so where it can be seen
        extras.ToggleWallpaperPause();
        Assert.Equal("Wallpaper paused", _shown[^1]); // resumed, the wallpaper is seen moving again

        extras.WallpaperCovered = () => true;
        extras.HideLyrics();
        Assert.Equal("Lyrics hidden", _shown[^1]); // covered by windows where the pointer is
    }

    // Ported from ActionHUDTests.swift: testNestedOriginsRestoreTheOuterOne.
    [Fact]
    public void TestNestedOriginsRestoreTheOuterOne()
    {
        var extras = Extras();
        var inner = extras.Performing(NoteOrigin.Elsewhere, () => extras.Performing(NoteOrigin.Window, () => extras.ActionOrigin));
        Assert.Equal(NoteOrigin.Window, inner);
        Assert.Equal(NoteOrigin.Window, extras.ActionOrigin);
    }

    // Ported from ActionHUDTests.swift: testWithoutAHUDTheNoteIsNotMarkedAsShownThere.
    [Fact]
    public void TestWithoutAHUDTheNoteIsNotMarkedAsShownThere()
    {
        var extras = Extras(hud: false);
        extras.Performing(NoteOrigin.Elsewhere, extras.HideLyrics);
        Assert.False(extras.Note?.InHud); // no HUD (tests, before launch)
    }

    // Ported from LyricExtrasTests.swift: testPausingTheWallpaperIsNamedForTheWallpaperAndLastsOneLaunch (the notes and
    // symbols; Windows keeps a pause across launches, as the tray's pause always has, so that half isn't ported).
    [Fact]
    public void TestPausingTheWallpaperIsNamedForTheWallpaper()
    {
        var paused = new List<bool>();
        var extras = Extras(new ExtrasHooks { PauseChanged = paused.Add });
        extras.ToggleWallpaperPause();
        Assert.True(_settings.WallpaperPaused);
        Assert.Equal(["Wallpaper paused"], _spoken); // Narrator hears what the shortcut did, once announcing is wired
        Assert.Equal("pause.rectangle.fill", extras.Note?.Symbol); // the state reached, not the button that undoes it
        Assert.NotEqual("pause.fill", extras.Note?.Symbol); // not the music transport's glyph
        extras.ToggleWallpaperPause();
        Assert.Equal(["Wallpaper paused", "Wallpaper resumed"], _spoken);
        Assert.Equal("play.rectangle.fill", extras.Note?.Symbol);
        Assert.Equal([true, false], paused);
        Assert.Equal("Pause music", FlyoutText.TransportTitle(playing: true)); // the music's own pause is named for the music
    }

    // Ported from LyricExtrasTests.swift: testCycleLayoutWalksEveryLayoutThenMatchTheMusic.
    [Fact]
    public void TestCycleLayoutWalksEveryLayoutThenMatchTheMusic()
    {
        var seen = new List<string>();
        var current = AppController.AutomaticComposition;
        do
        {
            current = LunalyrExtras.CycledLayout(current);
            seen.Add(current);
        } while (current != AppController.AutomaticComposition && seen.Count < 20);
        Assert.Equal(AppSettings.Compositions.Where(c => c != AppController.AutomaticComposition), seen.Where(c => c != AppController.AutomaticComposition));
        Assert.Equal(AppController.AutomaticComposition, seen[^1]);
    }

    /// <summary>Windows' own: the layout shortcut changes the setting and says which layout it is now.</summary>
    [Fact]
    public void TheLayoutShortcutSaysWhichLayout()
    {
        _settings = _settings with { LyricComposition = "echo" };
        var extras = Extras();
        extras.Perform(HotkeyAction.CycleLayout);
        Assert.Equal("spoken", _settings.LyricComposition);
        Assert.Equal("Layout: Spoken", _shown[^1]);
        _settings = _settings with { LyricComposition = "fable" };
        extras.Perform(HotkeyAction.CycleLayout);
        Assert.Equal("automatic", _settings.LyricComposition);
        Assert.Equal("Layout: Custom Mix", _shown[^1]);
    }

    // Ported from LyricExtrasTests.swift: testCyclingChangesARememberedLayoutInPlace
    [Fact]
    public void TestCyclingChangesARememberedLayoutInPlace()
    {
        var song = new Core.Lyrics.LyricQuery("Midnight Rain", "Taylor Swift", "Midnights", 174);
        _settings = StyleMemory.Remember(_settings, new RememberedStyle("echo", null), song, StyleScope.Song);
        var globalBefore = _settings.LyricComposition;
        var extras = Extras(new ExtrasHooks { PlayingSong = () => song });
        extras.CycleLayout();
        Assert.Equal(Core.Compositions.CompositionKind.Spoken, StyleMemory.Remembered(_settings, song, song.Artist)?.Style.Layout);
        Assert.Equal(globalBefore, _settings.LyricComposition); // the global layout is left alone
        Assert.Equal("Layout for this song: Spoken", extras.Note?.Text);
    }

    // Ported from LyricTapSyncTests.swift: testMusicVideoAndVisualiserShortcuts.
    [Fact]
    public void TestMusicVideoAndVisualiserShortcuts()
    {
        var extras = Extras();
        _settings = _settings with { BackgroundMode = "shader" };
        extras.ToggleMusicVideo();
        Assert.Equal("musicVideo", _settings.BackgroundMode);
        extras.ToggleMusicVideo();
        Assert.Equal("shader", _settings.BackgroundMode); // back to what it fell back to

        _settings = _settings with { VisualizerStyle = "silk" };
        extras.NextVisualizer();
        Assert.Equal("visualizer", _settings.BackgroundMode);
        Assert.Equal("silk", _settings.VisualizerStyle); // the first press shows the visualiser chosen last
        extras.NextVisualizer();
        Assert.Equal(VisualizerStyles.All[1].RawValue(), _settings.VisualizerStyle);
        Assert.Equal(VisualizerStyles.All[0], LunalyrExtras.VisualizerAfter(VisualizerStyles.All[^1]));
    }

    /// <summary>Windows' own: those two shortcuts' notes, which are all a press from another app shows.</summary>
    [Fact]
    public void MusicVideoAndVisualiserNotes()
    {
        var extras = Extras();
        _settings = _settings with { BackgroundMode = "pattern" };
        extras.Perform(HotkeyAction.ToggleMusicVideo);
        Assert.Equal("Music video on", _shown[^1]);
        extras.Perform(HotkeyAction.ToggleMusicVideo);
        Assert.Equal("Music video off · Line patterns", _shown[^1]);
        extras.Perform(HotkeyAction.NextVisualizer);
        Assert.StartsWith("Visualiser: ", _shown[^1], StringComparison.Ordinal);
    }

    /// <summary>Windows' own: the sync shortcuts go through the controller's nudge, with the flyout's note.</summary>
    [Fact]
    public void SyncShortcutsNudgeThisSongAndSaySo()
    {
        var offset = 0.2;
        var extras = Extras(new ExtrasHooks { NudgeSync = delta => offset = Math.Round((offset + delta) * 100) / 100 });
        extras.Perform(HotkeyAction.SyncEarlier);
        Assert.Equal(0.25, offset, 9);
        Assert.Equal("Lyrics 0.05 s earlier · +0.25 s for this song’s lyric source", _shown[^1]);
        extras.Perform(HotkeyAction.SyncLater);
        Assert.Equal("Lyrics 0.05 s later · +0.20 s for this song’s lyric source", _shown[^1]);

        var none = Extras(new ExtrasHooks { NudgeSync = _ => null });
        Assert.Null(none.NudgeSync(0.1));
        Assert.Equal(FlyoutText.NudgeNeedsSong, none.Note?.Text);
        Assert.False(none.Note?.InHud); // from a Lunalyr window: no HUD
    }

    /// <summary>Windows' own: the card shortcut needs a song, and says so calmly while cards aren't in the build.</summary>
    [Fact]
    public void TheCardShortcutNeedsASongAndACardWindow()
    {
        var opened = 0;
        var playing = false;
        var hasCards = false;
        var extras = Extras(new ExtrasHooks
        {
            HasSong = () => playing,
            OpenLyricCard = () =>
            {
                if (!hasCards) return false;
                opened++;
                return true;
            },
        });
        extras.Run(LunalyrCommand.LyricCard);
        Assert.Equal("Play a song to make a lyric card", _shown[^1]);
        playing = true;
        extras.Run(LunalyrCommand.LyricCard);
        Assert.Equal(LunalyrExtras.LyricCardMissing, _shown[^1]);
        hasCards = true;
        var notes = _shown.Count;
        extras.Run(LunalyrCommand.LyricCard);
        Assert.Equal(1, opened);
        Assert.Equal(notes, _shown.Count); // a command that opens a window posts nothing
    }

    /// <summary>Windows' own: every link command reaches its action, through the HUD.</summary>
    [Fact]
    public void LinkCommandsRunTheirActions()
    {
        var settingsOpened = 0;
        var extras = Extras(new ExtrasHooks { OpenSettings = () => settingsOpened++ });
        extras.Run(LunalyrCommand.ToggleLyrics);
        Assert.True(_settings.LyricsHidden);
        Assert.Equal("Lyrics hidden", _shown[^1]);
        extras.Run(LunalyrCommand.ToggleLyrics);
        Assert.False(_settings.LyricsHidden);
        extras.Run(LunalyrCommand.PauseWallpaper);
        Assert.True(_settings.WallpaperPaused);
        extras.Run(LunalyrCommand.ToggleFloating);
        Assert.True(_settings.IsFloating);
        Assert.Equal("Lyrics floating over apps", _shown[^1]);
        extras.Run(LunalyrCommand.ToggleFloating);
        Assert.False(_settings.IsFloating);
        Assert.Equal("Lyrics on the desktop", _shown[^1]);
        extras.Run(LunalyrCommand.OpenSettings);
        Assert.Equal(1, settingsOpened);
        Assert.Equal(NoteOrigin.Window, extras.ActionOrigin);
    }

    /// <summary>Windows' own: the tap sync shortcut hands over to Tap to sync, and posts nothing itself.</summary>
    [Fact]
    public void TheTapSyncShortcutStartsTapToSync()
    {
        var toggled = 0;
        var extras = Extras(new ExtrasHooks { ToggleTapSync = () => toggled++ });
        extras.Perform(HotkeyAction.TapSyncLyrics);
        Assert.Equal(1, toggled);
        Assert.Empty(_shown);
    }
}
