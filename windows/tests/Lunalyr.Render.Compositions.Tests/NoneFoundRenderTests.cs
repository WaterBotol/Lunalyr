using System.Numerics;
using Lunalyr.Core.Logging;
using Lunalyr.Core.Motion;
using Lunalyr.Platform.Readings;
using Lunalyr.Render.Scene;
using Lunalyr.Tools.CompositionPreview;

namespace Lunalyr.Render.Compositions.Tests;

/// <summary>
/// "When none are found" (Settings › Timing › Without synced lyrics), drawn: a song without lyrics shows a short note
/// that fades after a few seconds, its title and artist, or nothing; a wait ("Looking up lyrics…") always shows.
/// </summary>
[Collection(GpuCollection.Name)]
public sealed class NoneFoundRenderTests : IDisposable
{
    private const int Width = 960, Height = 540;
    private readonly Offscreen _stage = new(Width, Height);
    private static readonly Vector4 Background = Fixtures.Night.Base;

    public void Dispose() => _stage.Dispose();

    private IRenderLayer Layer()
    {
        var layer = new EditorialFactory(IcuWordBreaker.TryCreate(), Logger.Null).Create();
        layer.CreateResources(_stage.Dc, _stage.DWrite, new Vector2(Width, Height), 1);
        return layer;
    }

    private static SceneSnapshot Missing(NoLyricsDisplay shown, bool noneFound = true, string status = "No lyrics found for this song",
                                         bool opening = false) => new()
    {
        Title = "Quiet Harbour",
        Artist = "The Lunalyr Demo Band",
        Status = status,
        NoneFound = noneFound,
        Palette = Fixtures.Night,
        Style = LyricStyle.Default with { NoLyrics = shown, OpeningTitle = opening },
    };

    private int Lit(IRenderLayer layer, SceneSnapshot scene, double wall)
    {
        _stage.Render(layer, scene, 2, wall, 1, Background);
        var pixels = _stage.Pixels();
        var count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
            if (pixels[i] + pixels[i + 1] + pixels[i + 2] > 3 * 90) count++;
        return count;
    }

    [Fact]
    public void TheNoteSaysSoThenFadesAndLetsTheClockRest()
    {
        using var layer = Layer();
        var scene = Missing(NoLyricsDisplay.Notice);
        Lit(layer, scene, 100);
        Assert.True(Lit(layer, scene, 101) > 50, "the note shows");
        Assert.True(Lit(layer, scene, 103.5) > 50, "and holds");
        Assert.True(layer.IsAnimating, "the clock runs through the hold, so the fade is drawn");
        Assert.Equal(0, Lit(layer, scene, 105.2));
        Lit(layer, scene, 106);
        Assert.False(layer.IsAnimating, "once it has gone, nothing is left to draw");
    }

    [Fact]
    public void AWaitIsNeverFadedAway()
    {
        using var layer = Layer();
        var scene = Missing(NoLyricsDisplay.Nothing, noneFound: false, status: "Looking up lyrics…");
        Lit(layer, scene, 100);
        Assert.True(Lit(layer, scene, 110) > 50);
    }

    [Fact]
    public void TheTitleAndArtistStayWithoutAnOpeningTitle()
    {
        using var layer = Layer();
        var scene = Missing(NoLyricsDisplay.TrackName, opening: true);
        Lit(layer, scene, 100);
        var shown = Lit(layer, scene, 101);
        Assert.True(shown > 200, $"the title and artist show ({shown} pixels)");
        Assert.Equal(shown, Lit(layer, scene, 160));
        Assert.False(layer.IsAnimating);

        // Only the stage's centre is used: the opening title (low on the left) stays away.
        _stage.Render(layer, scene, 2, 161, 1, Background);
        var pixels = _stage.Pixels();
        for (var y = Height * 3 / 4; y < Height; y++)
        for (var x = 0; x < Width / 3; x++)
        {
            var i = (y * Width + x) * 4;
            Assert.True(pixels[i] + pixels[i + 1] + pixels[i + 2] <= 3 * 90, $"lit at {x},{y}");
        }
    }

    [Fact]
    public void NothingLeavesTheStageEmpty()
    {
        using var layer = Layer();
        var scene = Missing(NoLyricsDisplay.Nothing);
        Assert.Equal(0, Lit(layer, scene, 100));
        Assert.Equal(0, Lit(layer, scene, 101));
        Assert.False(layer.IsAnimating);
    }
}
