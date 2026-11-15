using Lunalyr.Core.Colour;
using Lunalyr.Core.MusicVideo;
using Lunalyr.Render.Backgrounds;
using Lunalyr.Render.Diagnostics;
using Lunalyr.Render.Scene;

namespace Lunalyr.Render.Tests.Backgrounds;

/// <summary>
/// The music video layers drawn offscreen on WARP. The local file is tools/WallpaperTestMedia's timecode.mp4 (160×96,
/// 15 fps, 8 s), whose frames carry their own frame number, so the frame drawn is read back exactly: it must be the one
/// the song's clock plus the offset asks for, after jumps forward and back, and held outside the video. Silent: the
/// reader opens the video stream only.
/// </summary>
public sealed class MusicVideoLayerTests : IDisposable
{
    private const int Width = 160, Height = 96, Fps = 15;
    private static readonly string Clip = Path.Combine(AppContext.BaseDirectory, "Fixtures", "timecode.mp4");
    private static readonly BackgroundOptions Plain = new() { Grain = false, ScrimStrength = 0, ManualBacking = 0 };
    private readonly OffscreenSurface _surface = new(Width, Height);

    private sealed class Feed : IMusicVideoFeed
    {
        public MusicVideoInputs Inputs;
        public readonly List<(double? Duration, double Time, bool Playing)> Reports = [];
        public string? Failure;

        public MusicVideoInputs Read() => Inputs;

        public void Report(string path, double? duration, double videoTime, bool playing) => Reports.Add((duration, videoTime, playing));

        public void Failed(string path, string reason) => Failure = reason;
    }

    private static SceneSnapshot Scene(bool reduceMotion = false) => new() { Palette = Palette.Default, ReduceMotion = reduceMotion };

    /// <summary>The frame number drawn in the surface's top third (the clip fills the 160×96 surface exactly).</summary>
    private static int FrameNumber(byte[] pixels)
    {
        var number = 0;
        var y = Height / 6;
        for (var bit = 0; bit < 12; bit++)
        {
            var x = (int)((bit + 0.5) * Width / 12);
            var i = (y * Width + x) * 4;
            if ((pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3 > 128) number |= 1 << bit;
        }
        return number;
    }

    /// <summary>Draws until the layer shows the frame expected (the worker decodes on its own thread), or two seconds pass.</summary>
    private int DrawUntil(IRenderLayer layer, int expected, ref double wall, bool reduceMotion = false)
    {
        var shown = -1;
        for (var i = 0; i < 200; i++)
        {
            wall += 1 / 60.0;
            _surface.Draw(layer, Scene(reduceMotion), wall);
            shown = FrameNumber(_surface.ReadPixels());
            if (shown == expected && ((MusicVideoLayer)layer).ShownTime is not null) return shown;
            Thread.Sleep(10);
        }
        return shown;
    }

    private static int Expected(double songTime, double offset) => (int)Math.Floor((songTime + offset) * Fps + 1e-6);

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(2.4, 0.0)]
    [InlineData(1.0, 1.5)]
    [InlineData(6.0, -0.5)]
    public void TheFrameDrawnIsTheSongsTimePlusTheOffset(double songTime, double offset)
    {
        var feed = new Feed { Inputs = new MusicVideoInputs(Clip, songTime, true, offset, 0, 0) };
        using var layer = new MusicVideoLayerFactory(feed, Plain).Create();
        _surface.Prepare(layer);
        double wall = 0;
        Assert.Equal(Expected(songTime, offset), DrawUntil(layer, Expected(songTime, offset), ref wall));
        Assert.Null(feed.Failure);
    }

    [Fact]
    public void PlayingAlongJumpingAndGoingBackStaysOnTheSongsFrame()
    {
        var feed = new Feed();
        using var layer = new MusicVideoLayerFactory(feed, Plain).Create();
        _surface.Prepare(layer);
        double wall = 0;
        const double offset = 0.8;
        // Playing along from 1 s: every few frames checked.
        for (var song = 1.0; song < 2.5; song += 0.2)
        {
            feed.Inputs = new MusicVideoInputs(Clip, song, true, offset, 0, 0);
            Assert.Equal(Expected(song, offset), DrawUntil(layer, Expected(song, offset), ref wall));
        }
        // A scrub forward, then back (a seek each way).
        foreach (var song in new[] { 5.5, 1.2, 4.0 })
        {
            feed.Inputs = new MusicVideoInputs(Clip, song, true, offset, 0, 0);
            Assert.Equal(Expected(song, offset), DrawUntil(layer, Expected(song, offset), ref wall));
        }
        // A nudge of the offset applies at once.
        feed.Inputs = new MusicVideoInputs(Clip, 4.0, true, offset + 1, 0, 0);
        Assert.Equal(Expected(4.0, offset + 1), DrawUntil(layer, Expected(4.0, offset + 1), ref wall));
        // The video's length and the frame shown are reported (the intro estimate, Align, Tap to sync).
        Assert.Contains(feed.Reports, r => r.Duration is { } d && Math.Abs(d - 8) < 0.1);
        Assert.Contains(feed.Reports, r => r.Playing);
    }

    [Fact]
    public void BeforeTheVideoAndPastItsEndTheFrameIsHeld()
    {
        var feed = new Feed { Inputs = new MusicVideoInputs(Clip, 3.0, true, 0, 0, 0) };
        using var layer = new MusicVideoLayerFactory(feed, Plain).Create();
        _surface.Prepare(layer);
        double wall = 0;
        Assert.Equal(45, DrawUntil(layer, 45, ref wall));
        // Past the end (8 s): held on the frame shown, never looped.
        feed.Inputs = new MusicVideoInputs(Clip, 20, true, 0, 0, 0);
        for (var i = 0; i < 20; i++)
        {
            wall += 1 / 60.0;
            _surface.Draw(layer, Scene(), wall);
            Thread.Sleep(5);
        }
        Assert.Equal(45, FrameNumber(_surface.ReadPixels()));
        // Before its start (a negative offset early in the song): held too.
        feed.Inputs = new MusicVideoInputs(Clip, 0.5, true, -2, 0, 0);
        wall += 1 / 60.0;
        _surface.Draw(layer, Scene(), wall);
        Assert.Equal(45, FrameNumber(_surface.ReadPixels()));
    }

    [Fact]
    public void ReduceMotionHoldsTheFirstFrameShown()
    {
        var feed = new Feed { Inputs = new MusicVideoInputs(Clip, 2.0, true, 0, 0, 0) };
        using var layer = new MusicVideoLayerFactory(feed, Plain).Create();
        _surface.Prepare(layer);
        double wall = 0;
        Assert.Equal(30, DrawUntil(layer, 30, ref wall, reduceMotion: true));
        feed.Inputs = new MusicVideoInputs(Clip, 4.0, true, 0, 0, 0);
        for (var i = 0; i < 20; i++)
        {
            wall += 1 / 60.0;
            _surface.Draw(layer, Scene(reduceMotion: true), wall);
            Thread.Sleep(5);
        }
        Assert.Equal(30, FrameNumber(_surface.ReadPixels()));
    }

    /// <summary>
    /// The still is the song's own frame even when the frame on screen lags it: after a seek the reader decodes the frame
    /// just before the target first, and a slow reader (a loaded machine) can have only that one ready when Reduce Motion
    /// first holds. Here the lag is made on purpose: frame 29 shows, then Reduce Motion comes on as the song reaches 30.
    /// </summary>
    [Fact]
    public void ReduceMotionHoldsTheSongsFrameNotOneThatLagsIt()
    {
        var feed = new Feed { Inputs = new MusicVideoInputs(Clip, 1.95, true, 0, 0, 0) };
        using var layer = new MusicVideoLayerFactory(feed, Plain).Create();
        _surface.Prepare(layer);
        double wall = 0;
        Assert.Equal(29, DrawUntil(layer, 29, ref wall));
        feed.Inputs = new MusicVideoInputs(Clip, 2.0, true, 0, 0, 0);
        Assert.Equal(30, DrawUntil(layer, 30, ref wall, reduceMotion: true));
        // Held there while the song plays on.
        feed.Inputs = new MusicVideoInputs(Clip, 4.0, true, 0, 0, 0);
        for (var i = 0; i < 20; i++)
        {
            wall += 1 / 60.0;
            _surface.Draw(layer, Scene(reduceMotion: true), wall);
            Thread.Sleep(5);
        }
        Assert.Equal(30, FrameNumber(_surface.ReadPixels()));
        Assert.DoesNotContain(feed.Reports, r => r.Playing && r.Time > 2.01);
    }

    [Fact]
    public void WithoutAFileTheAlbumColoursShowAndDimmingDarkensThem()
    {
        var feed = new Feed { Inputs = new MusicVideoInputs(null, 3.0, true, 0, 0, 0) };
        using var layer = new MusicVideoLayerFactory(feed, Plain).Create();
        _surface.Prepare(layer);
        Assert.True(((IFirstFrameLayer)layer).HasFirstFrame, "the stand-in draws at once");
        _surface.Draw(layer, Scene(), 0.1);
        var bright = Mean(_surface.ReadPixels());
        Assert.True(bright > 20, $"the album colours show ({bright:0.0})");
        feed.Inputs = feed.Inputs with { Dim = 0.85 };
        _surface.Draw(layer, Scene(), 0.2);
        Assert.True(Mean(_surface.ReadPixels()) < bright * 0.3);
    }

    [Fact]
    public void AFileThatCantBePlayedIsReported()
    {
        var feed = new Feed { Inputs = new MusicVideoInputs(Path.Combine(AppContext.BaseDirectory, "Fixtures", "missing.mp4"), 1, true, 0, 0, 0) };
        using var layer = new MusicVideoLayerFactory(feed, Plain).Create();
        _surface.Prepare(layer);
        for (var i = 0; i < 100 && feed.Failure is null; i++)
        {
            _surface.Draw(layer, Scene(), i / 60.0);
            Thread.Sleep(10);
        }
        Assert.NotNull(feed.Failure);
    }

    [Fact]
    public void TheYouTubeLayerIsSeeThroughWhileThePlayerIsOnScreen()
    {
        var feed = new Feed { Inputs = new MusicVideoInputs(null, 3, true, 0, 0, 0, PlayerVisible: true) };
        using var layer = new YouTubeVideoLayerFactory(feed, Plain).Create();
        Assert.IsAssignableFrom<ISeeThroughLayer>(layer);
        _surface.Prepare(layer);
        _surface.Context.BeginDraw();
        _surface.Context.Clear(new Vortice.Mathematics.Color4(0, 0, 0, 0));
        _surface.Context.EndDraw();
        DrawTransparent(layer);
        Assert.Equal(0, MaxAlpha(_surface.ReadPixels()));
        // Another display has no player beneath it: the album colours show there.
        DrawTransparent(layer, main: false);
        Assert.Equal(255, MaxAlpha(_surface.ReadPixels()));
        // Nor has the settings preview (offscreen).
        DrawTransparent(layer, desktop: false);
        Assert.Equal(255, MaxAlpha(_surface.ReadPixels()));
        // The layer follows the host's word (PlayerVisible: the player plays and, shown whole, is past its hold), not the
        // song's clock.
        feed.Inputs = feed.Inputs with { Playing = false };
        DrawTransparent(layer);
        Assert.Equal(0, MaxAlpha(_surface.ReadPixels()));
        // Covered (paused, buffering, held, not ready, failed or closed): the album colours (and the still, once loaded).
        feed.Inputs = feed.Inputs with { PlayerVisible = false };
        DrawTransparent(layer);
        Assert.Equal(255, MaxAlpha(_surface.ReadPixels()));
    }

    // Windows' own, for the Mac's WholeYouTubeVideo (MusicVideoFramingTests' layout, drawn): shown whole, the wallpaper is
    // see-through only over the visible picture, where the host puts the player's window; the bands above and below are
    // opaque (album colours and the blurred backdrop), never a hole onto the desktop.
    [Fact]
    public void ShownWholeTheYouTubeLayerIsSeeThroughOnlyOverThePicture()
    {
        var feed = new Feed
        {
            Inputs = new MusicVideoInputs(null, 3, true, 0, 0, 0, PlayerVisible: true, YouTubeFraming: MusicVideoFraming.Whole, YouTubeAspect: 2.39),
        };
        using var layer = new YouTubeVideoLayerFactory(feed, Plain).Create();
        _surface.Prepare(layer);
        DrawTransparent(layer);
        var pixels = _surface.ReadPixels();
        var (_, top, _, height) = MusicVideoFramings.VisiblePixels(2.39, Width, Height);
        Assert.InRange(top, 10, 20); // 160 × 1.01 / 2.39 = 68 rows of picture in 96
        int Alpha(int x, int y) => pixels[(y * Width + x) * 4 + 3];
        Assert.Equal(0, Alpha(Width / 2, Height / 2));
        Assert.Equal(0, Alpha(5, top + 2));
        Assert.Equal(255, Alpha(Width / 2, top / 2));
        Assert.Equal(255, Alpha(Width / 2, Height - 1 - top / 2));
        Assert.Equal(255, Alpha(Width / 2, top - 1));
        Assert.Equal(255, Alpha(Width / 2, top + height));
        // Covered: opaque everywhere (the still in the picture, the backdrop around it).
        feed.Inputs = feed.Inputs with { PlayerVisible = false };
        DrawTransparent(layer);
        pixels = _surface.ReadPixels();
        Assert.Equal(255, Alpha(Width / 2, Height / 2));
        Assert.Equal(255, Alpha(Width / 2, top / 2));
        // Filled: see-through everywhere while it plays.
        feed.Inputs = feed.Inputs with { PlayerVisible = true, YouTubeFraming = MusicVideoFraming.Fill };
        DrawTransparent(layer);
        Assert.Equal(0, MaxAlpha(_surface.ReadPixels()));
    }

    private void DrawTransparent(IRenderLayer layer, bool desktop = true, bool main = true)
    {
        var dc = _surface.Context;
        dc.BeginDraw();
        dc.Clear(new Vortice.Mathematics.Color4(0, 0, 0, 0));
        var frame = new FrameContext
        {
            Dc = dc, DWrite = _surface.DWrite, D2DFactory = _surface.Factory, Size = _surface.Size, Scale = 1, WallSeconds = 1,
            SongSeconds = double.NaN, IsPlaying = false, Scene = Scene(), DeltaSeconds = 1 / 60.0,
            AheadWork = desktop ? new Lunalyr.Render.Pacing.AheadWork() : null,
            IsMainDisplay = desktop && main,
        };
        layer.Render(frame);
        dc.EndDraw().CheckError();
    }

    private static int MaxAlpha(byte[] pixels)
    {
        var max = 0;
        for (var i = 3; i < pixels.Length; i += 4) max = Math.Max(max, pixels[i]);
        return max;
    }

    private static double Mean(byte[] pixels)
    {
        double total = 0;
        for (var i = 0; i < pixels.Length; i += 4) total += (pixels[i] + pixels[i + 1] + pixels[i + 2]) / 3.0;
        return total / (pixels.Length / 4);
    }

    public void Dispose() => _surface.Dispose();
}
