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
    public void WithTheSongIntroOnTheCardStandsInForTheTitleAndArtist()
    {
        // The Mac's rule (dc87707 here): a song without lyrics keeps its intro card, low on the left, and the centred
        // title and artist are not drawn over it.
        using var layer = Layer();
        var scene = Missing(NoLyricsDisplay.TrackName, opening: true);
        Lit(layer, scene, 100);
        _stage.Render(layer, scene, 2, 101, 1, Background);
        var pixels = _stage.Pixels();
        int Count(int x0, int x1, int y0, int y1)
        {
            var count = 0;
            for (var y = y0; y < y1; y++)
            for (var x = x0; x < x1; x++)
            {
                var i = (y * Width + x) * 4;
                if (pixels[i] + pixels[i + 1] + pixels[i + 2] > 3 * 90) count++;
            }
            return count;
        }
        Assert.True(Count(0, Width / 2, Height / 2, Height) > 50, "the intro card shows, low on the left");
        Assert.Equal(0, Count(Width / 3, Width * 2 / 3, Height / 3, Height / 2));
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
