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

    [Fact]
    public void TheSameDisplaysAgainChangeNothing()
    {
        // The host runs this on every display change: unchanged displays must not save the settings again.
        var once = MonitorLooks.DisplaysChanged(new AppSettings().WithLyricsSurface(LyricsSurface.Floating), ["a", "b"]);
        Assert.Same(once, MonitorLooks.DisplaysChanged(once, ["a", "b"]));
    }
}
