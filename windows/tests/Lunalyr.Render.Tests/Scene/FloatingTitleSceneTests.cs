using Lunalyr.Render.Floating;
using Lunalyr.Render.Scene;

namespace Lunalyr.Render.Tests.Scene;

/// <summary>Ported from OpeningTitleTests.swift (the Mac's b11a1ab3): the title card has its own window.</summary>
public sealed class FloatingTitleSceneTests
{
    // Ported from OpeningTitleTests.swift: testTheLyricsBoxKeepsNoTitleRow
    [Fact]
    public void TheLyricsWindowDrawsNoTitleAndTheCardsDrawsOnlyIt()
    {
        var scene = new SceneSnapshot { Title = "Song", Style = new LyricStyle { OpeningTitle = true, OpeningTitleKeep = true } };
        var lyrics = FloatingFrameDrawer.SceneFor(scene, 44, 0.6, reduceTransparency: false);
        Assert.False(lyrics.Style.OpeningTitle); // no title, so no row kept for it
        Assert.False(lyrics.Style.FloatingTitleOnly);
        var card = FloatingFrameDrawer.SceneFor(scene, 44, 0.6, reduceTransparency: false, titleCard: true);
        Assert.True(card.Style.OpeningTitle);
        Assert.True(card.Style.FloatingTitleOnly);
        // A style that shows no song title: the card's window has nothing to draw.
        var none = scene with { Style = scene.Style with { OpeningTitle = false } };
        Assert.False(FloatingFrameDrawer.SceneFor(none, 44, 0.6, reduceTransparency: false, titleCard: true).Style.OpeningTitle);
    }
}
