using Lunalyr.Core.Colour;
using Lunalyr.Core.MusicVideo;
using Lunalyr.Render.Backgrounds;
using Lunalyr.Render.Diagnostics;
using Lunalyr.Render.Scene;

namespace Lunalyr.Render.Tests.Backgrounds;

/// <summary>
/// The YouTube layer's still (the Mac's YouTubeThumbnail) drawn offscreen on WARP: wherever the player isn't on screen it
/// fills the surface once loaded, fading in over the album colours; it never covers a player that is on screen.
/// </summary>
public sealed class YouTubeStillLayerTests : IDisposable
{
    private const int Width = 64, Height = 36;
    private static readonly BackgroundOptions Plain = new() { Grain = false, ScrimStrength = 0, ManualBacking = 0 };
    private readonly OffscreenSurface _surface = new(Width, Height);

    private sealed class Feed : IMusicVideoFeed
    {
        public MusicVideoInputs Inputs;

        public MusicVideoInputs Read() => Inputs;

        public void Report(string path, double? duration, double videoTime, bool playing) { }

        public void Failed(string path, string reason) { }
    }

    /// <summary>A 4:3 still of one colour (premultiplied BGRA), like YouTube's 480 × 360 one.</summary>
    private static CoverArt Solid(byte r, byte g, byte b)
    {
        var pixels = new byte[48 * 36 * 4];
        for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (b, g, r, 255);
        return new CoverArt(48, 36, pixels);
    }

    private (byte R, byte G, byte B, byte A) Centre(IRenderLayer layer, double now, bool desktop = true, bool main = true)
    {
        var dc = _surface.Context;
        dc.BeginDraw();
        dc.Clear(new Vortice.Mathematics.Color4(0, 0, 0, 0));
        layer.Render(new FrameContext
        {
            Dc = dc, DWrite = _surface.DWrite, D2DFactory = _surface.Factory, Size = _surface.Size, Scale = 1, WallSeconds = now,
            SongSeconds = double.NaN, IsPlaying = false, Scene = new SceneSnapshot { Palette = Palette.Default }, DeltaSeconds = 1 / 60.0,
            AheadWork = desktop ? new Lunalyr.Render.Pacing.AheadWork() : null, IsMainDisplay = desktop && main,
        });
        dc.EndDraw().CheckError();
        var pixels = _surface.ReadPixels();
        var i = ((Height / 2) * Width + Width / 2) * 4;
        return (pixels[i + 2], pixels[i + 1], pixels[i], pixels[i + 3]);
    }

    [Fact]
    public void TheStillFadesInWhereThePlayerIsntAndNeverCoversIt()
    {
        var feed = new Feed { Inputs = new MusicVideoInputs(null, 3, true, 0, 0, 0) };
        using var layer = new YouTubeVideoLayerFactory(feed, Plain).Create();
        _surface.Prepare(layer);
        var standIn = Centre(layer, 1);
        Assert.Equal(255, standIn.A); // the album colours while there is no still

        feed.Inputs = feed.Inputs with { Still = Solid(230, 20, 20) };
        var arriving = Centre(layer, 2);
        Assert.Equal((standIn.R, standIn.G, standIn.B), (arriving.R, arriving.G, arriving.B)); // fades in from the stand-in
        var halfway = Centre(layer, 2.2);
        Assert.InRange(halfway.R, Math.Min(standIn.R, (byte)230) + 1, 229);
        var shown = Centre(layer, 2.5);
        Assert.Equal((230, 20, 20, 255), ((int)shown.R, (int)shown.G, (int)shown.B, (int)shown.A));

        // The settings preview has no player: the still.
        Assert.Equal(230, Centre(layer, 3, desktop: false).R);
        // Softened by Video blur, still filling the surface.
        feed.Inputs = feed.Inputs with { Blur = 12 };
        Assert.InRange(Centre(layer, 4).R, 225, 235);
        // A player on screen is never covered: see-through.
        feed.Inputs = feed.Inputs with { PlayerVisible = true };
        Assert.Equal(0, Centre(layer, 5).A);
        // Dimming applies over the still.
        feed.Inputs = feed.Inputs with { PlayerVisible = false, Blur = 0, Dim = 0.5 };
        Assert.InRange(Centre(layer, 6).R, 110, 120);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnotherDisplayShowsTheAlbumColoursNotAFrozenStill(bool whole)
    {
        // The player plays on the main display only: elsewhere its still would sit frozen for the whole song.
        var feed = new Feed
        {
            Inputs = new MusicVideoInputs(null, 3, true, 0, 0, 0) with
            {
                YouTubeFraming = whole ? MusicVideoFraming.Whole : MusicVideoFraming.Fill,
            },
        };
        using var layer = new YouTubeVideoLayerFactory(feed, Plain).Create();
        _surface.Prepare(layer);
        var standIn = Centre(layer, 1, main: false);
        Assert.Equal(255, standIn.A);
        feed.Inputs = feed.Inputs with { Still = Solid(230, 20, 20) };
        Assert.Equal(standIn, Centre(layer, 5, main: false));
        // Nor is it see-through there when the main display's player is uncovered.
        feed.Inputs = feed.Inputs with { PlayerVisible = true };
        Assert.Equal(standIn, Centre(layer, 6, main: false));
    }

    public void Dispose() => _surface.Dispose();
}
