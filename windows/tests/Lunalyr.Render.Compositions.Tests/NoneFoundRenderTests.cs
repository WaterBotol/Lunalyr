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

    /// <summary>
    /// The card that entered while the lyrics were looked up stays the one card when they arrive (the Mac's 5fb4a15):
    /// with the first line soon after, it keeps its full size and leaves in time, rather than turning compact at once.
    /// </summary>
    [Fact]
    public void TheCardFromTheLookupStaysOneCardWhenLyricsArrive()
    {
        using var layer = Layer();
        var loading = Missing(NoLyricsDisplay.TrackName, noneFound: false, status: "Looking up lyrics…", opening: true);
        var lyrics = loading with
        {
            NoneFound = false, Status = null,
            Lyrics = new Lunalyr.Core.Lyrics.LyricResult.Synced(
            [
                new() { Start = 4.5, End = 6, Text = "first line", WordTimed = true, Words = [new("first", 4.5, 5), new("line", 5, 6)] },
            ], new Lunalyr.Core.Lyrics.LyricProvenance("Word-by-word", "Test")),
        };
        int Card(SceneSnapshot scene, double time)
        {
            _stage.Render(layer, scene, time, 100 + time, 1, Background);
            var pixels = _stage.Pixels();
            var count = 0;
            for (var y = Height / 2; y < Height; y++)
            for (var x = 0; x < Width / 2; x++)
            {
                var i = (y * Width + x) * 4;
                if (pixels[i] + pixels[i + 1] + pixels[i + 2] > 3 * 90) count++;
            }
            return count;
        }
        var before = 0;
        for (var t = 0.0; t <= 2.4; t += 0.1) before = Card(loading, t);
        Assert.True(before > 200, $"the card entered while looking up ({before} pixels)");
        var after = Card(lyrics, 2.5);
        Assert.True(after > 0.8 * before, $"the card is still the full card when the lyrics arrive ({after} against {before} pixels)");
        // It has gone by the first line.
        Assert.Equal(0, Card(lyrics, 4.4));
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
