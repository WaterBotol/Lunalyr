using Lunalyr.Core.Compositions;
using Lunalyr.Core.Floating;
using Lunalyr.Shell.Settings;

namespace Lunalyr.Shell.Tests;

/// <summary>After the Mac's b11a1ab3 (FloatingLyricsTests.swift): the song title card's settings.</summary>
public sealed class FloatingTitleCardSettingsTests
{
    // Ported from FloatingLyricsTests.swift: testTitleCardFramesAreKeptPerDisplayAndReset
    [Fact]
    public void TitleCardFramesAreKeptPerDisplayAndReset()
    {
        var s = new AppSettings();
        Assert.True(s.FloatingTitleCard); // on by default
        Assert.False(s.TitleIsPlacedByHand);
        s = s.WithTitleFrame(new FloatingRect(30, 600, 300, 66), "A");
        Assert.Equal(new FloatingRect(30, 600, 300, 66), s.TitleFrame("A"));
        Assert.Null(s.TitleFrame("B")); // another display keeps its own
        Assert.Null(s.CustomFrame("A")); // apart from the lyrics' frame
        s = (s with { FloatingTitleCard = false }).Normalized();
        Assert.Equal(new FloatingRect(30, 600, 300, 66), s.TitleFrame("A"));
        Assert.False(s.FloatingTitleCard);
        s = s.WithTitleFramesReset();
        Assert.Null(s.TitleFrame("A"));
        Assert.False(s.TitleIsPlacedByHand);
    }

    // Ported from FloatingLyricsTests.swift: testTheOldTitleRowIsCarriedOverOnce
    [Fact]
    public void TheOldTitleRowIsCarriedOverOnce()
    {
        var old = new AppSettings().WithTitleRow(new OpeningTitleRow(0.8, true, 1.4));
        var s = old.Normalized();
        Assert.Equal(new FloatingTitleStart(0.8, true, 1.4), s.TitleStart());
        Assert.True(s.TitleIsPlacedByHand);
        Assert.True(s.TitleRow().IsStandard); // the row's place goes
        Assert.Equal(s.TitleStart(), s.Normalized().TitleStart()); // kept, and read again
        // A row left at its standard place starts the card at its standard place.
        Assert.True(new AppSettings().Normalized().TitleStart().IsStandard);
    }

    // Ported from OpeningTitleTests.swift: testTheRowPlacementIsReadForTheMigration
    [Fact]
    public void TheRowPlacementIsReadForTheMigration()
    {
        // A settings file from before the card: the row's keys as they were written.
        const string json = """{ "floatingTitleCentre": 0.3, "floatingTitleEdge": "bottom", "floatingTitleScale": 1.2 }""";
        var read = System.Text.Json.JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings)!.Normalized();
        Assert.Equal(new FloatingTitleStart(0.3, true, 1.2), read.TitleStart());
        Assert.Null(read.FloatingTitleEdge); // the row's own place is gone
        Assert.True(read.TitleRow().IsStandard);
    }
}
