using Lunalyr.Core.Floating;
using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell.Tests;

/// <summary>The Displays section and the Editing switch (the Mac's 4f9b1454).</summary>
public sealed class DisplayLyricsSettingsTests
{
    // Ported from FloatingLyricsTests.swift: testDisplaysAreListedByNameAndTwinsNumbered
    [Fact]
    public void DisplaysAreListedByNameAndTwinsNumbered()
    {
        var listed = FloatingCopy.Named([("a", "Studio Display"), ("b", "Built-in Display"), ("c", "Studio Display")]);
        Assert.Equal(["Studio Display 1", "Built-in Display", "Studio Display 2"], listed.Select(d => d.Name));
        Assert.Equal(["a", "b", "c"], listed.Select(d => d.Key));
        Assert.Equal(["Desktop lyrics", "Floating lyrics", "Both", "None (wallpaper only)"], DisplayLyricsChoices.All.Select(c => c.Title()));
    }

    [Fact]
    public void TheSwitchPicksTheStyleBeingEdited()
    {
        Assert.Equal(["Desktop style", "Floating style"], LyricsSurfaces.All.Select(s => s.Title()));
        Assert.Equal("These settings change the Floating style; the desktop lyrics keep their own.", FloatingCopy.EditingNote(LyricsSurface.Floating));
    }

    [Fact]
    public void PickingFloatingInTheMenuKeepsAFloatingStyleOfItsOwn()
    {
        var settings = new AppSettings().WithDisplayChosen("b", DisplayLyrics.Both, "a");
        Assert.Equal(DisplayLyrics.Both, settings.DisplayChoice("b"));
        Assert.NotNull(MonitorLooks.Saved(settings, new LookSlot("b", LyricsSurface.Floating)));
        var desktop = new AppSettings().WithDisplayChosen("b", DisplayLyrics.None, "a");
        Assert.Null(MonitorLooks.Saved(desktop, new LookSlot("b", LyricsSurface.Floating)));
    }

    // The Mac's MonitorBar.impliedStyle and followShows (MonitorBar.swift)
    [Fact]
    public void TheStyleEditedFollowsWhatTheMonitorShows()
    {
        Assert.Equal(LyricsSurface.Desktop, DisplayLyrics.Desktop.ImpliedStyle());
        Assert.Equal(LyricsSurface.Floating, DisplayLyrics.Floating.ImpliedStyle());
        Assert.Null(DisplayLyrics.Both.ImpliedStyle());
        Assert.Null(DisplayLyrics.None.ImpliedStyle());

        var settings = MonitorLooks.DisplaysChanged(new AppSettings(), ["a", "b"]);
        // The edited monitor shows floating lyrics only: the Floating style is edited.
        var floating = settings.WithDisplayShows("a", DisplayLyrics.Floating, "a");
        Assert.Equal(LyricsSurface.Floating, floating.Surface);
        // Both keeps whichever was edited; None keeps the last one.
        Assert.Equal(LyricsSurface.Floating, floating.WithDisplayShows("a", DisplayLyrics.Both, "a").Surface);
        Assert.Equal(LyricsSurface.Floating, floating.WithDisplayShows("a", DisplayLyrics.None, "a").Surface);
        // Another monitor's choice leaves the style edited alone.
        Assert.Equal(LyricsSurface.Desktop, settings.WithDisplayShows("b", DisplayLyrics.Floating, "a").Surface);
    }

    [Fact]
    public void TheBarFollowsWhatTheEditedMonitorShowsAfterAToggleOrAnUnplug()
    {
        var settings = MonitorLooks.DisplaysChanged(new AppSettings(), ["a", "b"]);
        // The tray's Floating toggle changes what the main display shows and leaves the style edited alone (as on the
        // Mac); the monitor bar follows it when Settings shows it (FollowingShows).
        var floating = settings.WithFloatingToggled(["a", "b"], "a");
        Assert.Equal(DisplayLyrics.Floating, floating.DisplayChoice("a"));
        Assert.Equal(LyricsSurface.Desktop, floating.Surface);
        Assert.Equal(LyricsSurface.Floating, floating.FollowingShows("a").Surface);
        // Unplugging the edited monitor moves editing to the main one; the bar follows what that one shows.
        var side = settings.WithDisplayChoice("b", DisplayLyrics.Floating).WithEditedMonitor("b", "a");
        Assert.Equal(LyricsSurface.Floating, side.Surface);
        var unplugged = MonitorLooks.DisplaysChanged(side, ["a"]);
        Assert.Equal("a", unplugged.LookMonitor);
        Assert.Equal(LyricsSurface.Desktop, unplugged.FollowingShows("a").Surface);
        // Both: nothing to follow.
        var both = settings.WithDisplayChoice("a", DisplayLyrics.Both);
        Assert.Same(both, both.FollowingShows("a"));
    }

    [Fact]
    public void PickingAnotherMonitorEditsItsLook()
    {
        var settings = MonitorLooks.DisplaysChanged(new AppSettings { LyricComposition = "spoken" }, ["a", "b"]);
        // The other monitor shows floating lyrics: editing it edits its Floating style.
        settings = settings.WithDisplayChoice("b", DisplayLyrics.Floating);
        var edited = settings.WithEditedMonitor("b", "a");
        Assert.Equal("b", edited.LookMonitor);
        Assert.Equal(LyricsSurface.Floating, edited.Surface);
        // Picking the monitor already edited changes nothing.
        Assert.Same(settings, settings.WithEditedMonitor("a", "a"));
    }

    [Fact]
    public void TheMonitorMenuNamesTheEditedMonitor()
    {
        var named = FloatingCopy.Named([("a", "Studio Display"), ("b", "Studio Display")]);
        Assert.Equal("Studio Display 2", FloatingCopy.MonitorName("b", named));
        Assert.Equal("Studio Display 1", FloatingCopy.MonitorName("gone", named));
        Assert.Equal("This display", FloatingCopy.MonitorName("a", []));
        Assert.Equal("Copy Studio Display 2’s look to every monitor?", FloatingCopy.CopyToAllQuestion("Studio Display 2"));
    }

    [Fact]
    public void AVisualiserOnAnyMonitorNeedsSystemAudio()
    {
        var settings = MonitorLooks.DisplaysChanged(new AppSettings { BackgroundMode = "auto" }, ["a", "b"]);
        Assert.False(Visualisers.SystemAudioService.DrawsVisualiser(settings, ["a", "b"]));
        // The other monitor's own look is the visualiser; Settings edits the first.
        var side = MonitorLooks.Store(settings, MonitorLooks.Top(new AppSettings { BackgroundMode = "visualizer" }, MonitorLookKeys.All),
            new LookSlot("b", LyricsSurface.Desktop));
        Assert.True(Visualisers.SystemAudioService.DrawsVisualiser(side, ["a", "b"]));
        // Unplugged, it no longer counts.
        Assert.False(Visualisers.SystemAudioService.DrawsVisualiser(side, ["a"]));
        // Music video falling back to Visualisers for songs without a video (the Mac's bc7fb38).
        Assert.True(Visualisers.SystemAudioService.DrawsVisualiser(new AppSettings { BackgroundMode = "musicVideo", MusicVideoFallback = "visualizer" }, []));
        Assert.False(Visualisers.SystemAudioService.DrawsVisualiser(new AppSettings { BackgroundMode = "musicVideo", MusicVideoFallback = "pattern" }, []));
    }

    [Fact]
    public void TheSameDisplaysAgainChangeNothing()
    {
        // The host runs this on every display change: unchanged displays must not save the settings again.
        var once = MonitorLooks.DisplaysChanged(new AppSettings().WithLyricsSurface(LyricsSurface.Floating), ["a", "b"]);
        Assert.Same(once, MonitorLooks.DisplaysChanged(once, ["a", "b"]));
    }
}
