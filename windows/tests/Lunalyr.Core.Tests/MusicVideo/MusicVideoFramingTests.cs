using System.Text;
using Lunalyr.Core.Colour;
using Lunalyr.Core.MusicVideo;

namespace Lunalyr.Core.Tests.MusicVideo;

// Ported from MusicVideoFramingTests.swift (Test Build 88): "Show the whole video": the layout maths, the embed page's zoom
// and box, the reveal hold, the video's shape from oEmbed (a fake: nothing reaches the network) and the backdrop. The
// setting's own tests (defaults, remembered per source, unknown values) are in Lunalyr.Shell.Tests' MusicVideoFramingSettingsTests.
public sealed class MusicVideoFramingTests
{
    private static readonly VideoSize Laptop = new(1512, 982); // 16:10-ish built-in screen
    private static readonly VideoSize Wide = new(2560, 1440); // 16:9 external
    private static readonly VideoSize Tall = new(1080, 1920); // portrait
    private const double Hd = 16.0 / 9.0;
    private static readonly VideoSize[] Screens = [Laptop, Wide, Tall];

    // Ported from MusicVideoFramingTests.swift: testUnknownStoredValuesFallBackToFill (the stored-value half)
    [Fact]
    public void UnknownStoredValuesFallBackToFill()
    {
        Assert.Equal(MusicVideoFraming.Fill, MusicVideoFramings.FromStored(null));
        Assert.Equal(MusicVideoFraming.Fill, MusicVideoFramings.FromStored("zoom"));
        Assert.Equal(MusicVideoFraming.Fill, MusicVideoFramings.FromStored(3));
        Assert.Equal(MusicVideoFraming.Whole, MusicVideoFramings.FromStored("whole"));
    }

    // Ported from MusicVideoFramingTests.swift: testTitles
    [Fact]
    public void Titles()
    {
        Assert.Equal("Fill the screen", MusicVideoFraming.Fill.Title());
        Assert.Equal("Show the whole video", MusicVideoFraming.Whole.Title());
        Assert.Equal([MusicVideoFraming.Fill, MusicVideoFraming.Whole, MusicVideoFraming.Ambient], MusicVideoFramings.All);
        Assert.Equal("Whole video, ambient glow", MusicVideoFraming.Ambient.Title());
        Assert.Equal("ambient", MusicVideoFraming.Ambient.Value());
        Assert.Equal("fill", MusicVideoFraming.Fill.Value());
        Assert.Equal("whole", MusicVideoFraming.Whole.Value());
    }

    // Ported from MusicVideoFramingTests.swift: testWholeFitsTheVideoWithAtMostATinyBleed
    [Fact]
    public void WholeFitsTheVideoWithAtMostATinyBleed()
    {
        foreach (var screen in Screens)
        {
            var rect = MusicVideoFraming.Whole.Rect(Hd, screen);
            Assert.Equal(Hd, rect.Width / rect.Height, 3);
            Assert.Equal(screen.Width / 2, rect.MidX, 3);
            Assert.Equal(screen.Height / 2, rect.MidY, 3);
            var crop = MusicVideoFraming.Whole.CroppedShare(Hd, screen);
            Assert.True(crop <= 0.021, $"a 1% bleed each way loses about 2% of the area, {screen}");
            Assert.True(rect.Width <= screen.Width * 1.0101);
            Assert.True(rect.Height <= screen.Height * 1.0101);
        }
        Assert.Equal(1, MusicVideoFraming.Whole.YouTubeOverscan()); // no overscan
        Assert.InRange(MusicVideoFramings.WholeBleed, 1, 1.02);
    }

    // Ported from MusicVideoFramingTests.swift: testWholeOnALaptopLeavesBandsForTheBackdrop
    [Fact]
    public void WholeOnALaptopLeavesBandsForTheBackdrop()
    {
        var rect = MusicVideoFraming.Whole.Rect(Hd, Laptop);
        Assert.Equal(Laptop.Width * MusicVideoFramings.WholeBleed, rect.Width, 0.01); // fits the width
        Assert.True(rect.Height < Laptop.Height);
        var backdrop = MusicVideoFraming.Whole.BackdropShare(Hd, Laptop);
        Assert.InRange(backdrop, 0.05, 0.15);
        Assert.Equal(0, MusicVideoFraming.Whole.BackdropShare(Hd, Wide), 0.0001); // a 16:9 screen is filled exactly
        var portrait = MusicVideoFraming.Whole.Rect(Hd, Tall);
        Assert.Equal(Tall.Width * MusicVideoFramings.WholeBleed, portrait.Width, 0.01);
        Assert.True(MusicVideoFraming.Whole.BackdropShare(Hd, Tall) > 0.6);
    }

    // Ported from MusicVideoFramingTests.swift: testWholeNeverCropsANarrowerVideo (cbd7216)
    [Fact]
    public void WholeNeverCropsANarrowerVideo()
    {
        foreach (var screen in new[] { Laptop, Wide })
        {
            var rect = MusicVideoFraming.Whole.Rect(4.0 / 3.0, screen);
            Assert.Equal(screen.Height * MusicVideoFramings.WholeBleed, rect.Height, 0.01); // the picture's height is the screen's
            Assert.True(rect.Width < screen.Width, "space at the sides for the backdrop");
            Assert.Equal(4.0 / 3.0, rect.Width / rect.Height, 3);
            Assert.Equal(screen.Width / 2, rect.MidX, 3);
            Assert.True(MusicVideoFraming.Whole.CroppedShare(4.0 / 3.0, screen) <= 0.021, "only the hairline bleed");
            Assert.True(MusicVideoFraming.Whole.BackdropShare(4.0 / 3.0, screen) > 0.05);
        }
        var cinema = MusicVideoFraming.Whole.Rect(2.39, Laptop);
        Assert.True(cinema.Height < Laptop.Height, "a wider picture leaves bands above and below");
        Assert.Equal(MusicVideoFraming.Whole.Rect(4.0 / 3.0, Laptop), MusicVideoFraming.Ambient.Rect(4.0 / 3.0, Laptop));
    }

    // Ported from MusicVideoFramingTests.swift: testLibraryGravityFitsTheWholePicture (cbd7216)
    [Fact]
    public void LibraryGravityFitsTheWholePicture()
    {
        var whole = MusicVideoFraming.Whole;
        Assert.Equal(MusicVideoGravity.Fit, whole.VideoGravity(Hd, Laptop)); // wider: bands above and below
        Assert.Equal(MusicVideoGravity.Fit, whole.VideoGravity(4.0 / 3.0, Laptop)); // narrower: bands at the sides
        Assert.Equal(MusicVideoGravity.Fit, whole.VideoGravity(null, Laptop)); // unknown yet
        Assert.Equal(MusicVideoGravity.Fill, MusicVideoFraming.Fill.VideoGravity(Hd, Laptop));
        Assert.Equal(MusicVideoGravity.Fill, MusicVideoFraming.Fill.VideoGravity(null, Laptop));
    }

    // Ported from MusicVideoFramingTests.swift: testFillCoversTheScreenAndYouTubeCropsMore
    [Fact]
    public void FillCoversTheScreenAndYouTubeCropsMore()
    {
        foreach (var screen in Screens)
        {
            var rect = MusicVideoFraming.Fill.Rect(Hd, screen);
            Assert.True(rect.Width + 0.001 >= screen.Width);
            Assert.True(rect.Height + 0.001 >= screen.Height);
            Assert.Equal(0, MusicVideoFraming.Fill.BackdropShare(Hd, screen), 0.0001);
        }
        Assert.Equal(YouTubeEmbed.Overscan, MusicVideoFraming.Fill.YouTubeOverscan());
        var library = MusicVideoFraming.Fill.CroppedShare(Hd, Wide);
        var youTube = MusicVideoFraming.Fill.CroppedShare(Hd, Wide, YouTubeEmbed.Overscan);
        Assert.Equal(0, library, 0.0001);
        Assert.Equal(1 - 1 / (1.25 * 1.25), youTube, 0.0001); // 36% of the picture is lost to the overscan
        Assert.True(MusicVideoFraming.Whole.CroppedShare(Hd, Laptop) < MusicVideoFraming.Fill.CroppedShare(Hd, Laptop, YouTubeEmbed.Overscan));
    }

    // Ported from MusicVideoFramingTests.swift: testDegenerateSizesDoNotCrash
    [Fact]
    public void DegenerateSizesDoNotCrash()
    {
        foreach (var framing in MusicVideoFramings.All)
        {
            Assert.Equal(default, framing.Rect(Hd, VideoSize.Zero));
            Assert.Equal(new VideoRect(0, 0, Wide.Width, Wide.Height), framing.Rect(0, Wide));
            Assert.Equal(new VideoRect(0, 0, Wide.Width, Wide.Height), framing.Rect(double.NaN, Wide));
            Assert.Equal(0, framing.CroppedShare(Hd, VideoSize.Zero));
            Assert.Equal(0, framing.BackdropShare(Hd, VideoSize.Zero));
        }
    }

    // Ported from MusicVideoFramingTests.swift: testLibraryGravity
    [Fact]
    public void LibraryGravity()
    {
        Assert.Equal(1, MusicVideoFraming.Fill.BleedScale());
        Assert.True(MusicVideoFraming.Whole.ShowsBackdrop());
        Assert.False(MusicVideoFraming.Fill.ShowsBackdrop());
    }

    // Ported from MusicVideoFramingTests.swift: testAmbientIsWholeWithAGlow (7ef2908)
    [Fact]
    public void AmbientIsWholeWithAGlow()
    {
        MusicVideoFraming ambient = MusicVideoFraming.Ambient, whole = MusicVideoFraming.Whole;
        Assert.Equal(MusicVideoFraming.Ambient, MusicVideoFramings.FromStored("ambient"));
        Assert.True(ambient.IsWhole() && ambient.ShowsBackdrop() && ambient.FollowsVideo());
        Assert.False(whole.FollowsVideo());
        Assert.False(MusicVideoFraming.Fill.IsWhole());
        Assert.Equal(whole.Rect(Hd, Laptop), ambient.Rect(Hd, Laptop));
        Assert.Equal(MusicVideoOverlayMask.Covers(whole, wasShowing: false), MusicVideoOverlayMask.Covers(ambient, wasShowing: false));
        Assert.Equal(1, ambient.YouTubeOverscan());
        Assert.Equal(whole.VideoGravity(Hd, Laptop), ambient.VideoGravity(Hd, Laptop));
    }

    // Ported from MusicVideoFramingTests.swift: testAmbientGlowIsSmallAndBlurred (7ef2908)
    [Fact]
    public void AmbientGlowIsSmallAndBlurred()
    {
        // Red on the left half, blue on the right, 640 × 320.
        const int side = 640;
        var pixels = new byte[side * (side / 2) * 4];
        for (var y = 0; y < side / 2; y++)
        for (var x = 0; x < side; x++)
        {
            var i = (y * side + x) * 4;
            if (x < side / 2) pixels[i + 2] = 255; else pixels[i] = 255;
            pixels[i + 3] = 255;
        }
        var glow = MusicVideoAmbience.Glow(new Lunalyr.Core.Colour.CoverArt(side, side / 2, pixels))!;
        Assert.InRange(glow.Width, 1, MusicVideoAmbience.SampleWidth + 1);
        // Blurred: the middle mixes red and blue; the edges keep their own colour (no dark fringe).
        var mid = ((glow.Height / 2) * glow.Width + glow.Width / 2) * 4;
        Assert.True(glow.Pixels[mid] > 60 && glow.Pixels[mid + 2] > 60, "the middle is a blend");
        Assert.True(glow.Pixels[(glow.Height / 2 * glow.Width) * 4 + 2] > 200, "the left edge stays red");
        Assert.False(MusicVideoAmbience.LooksBlank(glow));
        Assert.True(MusicVideoAmbience.LooksBlank(new Lunalyr.Core.Colour.CoverArt(4, 4, new byte[64])));
    }

    // Ported from MusicVideoFramingTests.swift: testEmbedPageZoomFollowsTheFraming
    [Fact]
    public void EmbedPageZoomFollowsTheFraming()
    {
        var filled = YouTubeEmbed.Html("4NRXx6U8ABQ", 0);
        Assert.Contains("scale(1.25)", filled);
        Assert.Contains("overscan = 1.25,", filled);
        var whole = YouTubeEmbed.Html("4NRXx6U8ABQ", 0, overscan: MusicVideoFraming.Whole.YouTubeOverscan());
        Assert.Contains("scale(1.0)", whole);
        Assert.Contains("overscan = 1.0,", whole);
        Assert.Contains("window.lunalyrOverscan = function (scale)", whole); // changed live without a reload
        Assert.Contains("scale(' + (overscan * (blurPx > 0 ? 1.04 : 1))", whole); // the blur keeps its own margin
        Assert.Contains("overscan = 1.25,", YouTubeEmbed.Html("4NRXx6U8ABQ", 0, overscan: double.NaN));
        Assert.Contains("overscan = 2.0,", YouTubeEmbed.Html("4NRXx6U8ABQ", 0, overscan: 9));
        Assert.Contains("overscan = 1.0,", YouTubeEmbed.Html("4NRXx6U8ABQ", 0, overscan: 0.5));
    }

    // After the Mac's e7e50f7 (MusicVideoReuseTests.swift's overlay mask): shown whole, YouTube's controls are covered for
    // their first seconds, top, middle and bottom, at the same size in points at any player size.
    [Fact]
    public void WholeCoversYouTubesControlsForTheirFirstSeconds()
    {
        Assert.True(MusicVideoOverlayMask.Covers(MusicVideoFraming.Whole, wasShowing: false));
        Assert.True(MusicVideoOverlayMask.Covers(MusicVideoFraming.Ambient, wasShowing: false));
        Assert.False(MusicVideoOverlayMask.Covers(MusicVideoFraming.Whole, wasShowing: true)); // already up
        Assert.False(MusicVideoOverlayMask.Covers(MusicVideoFraming.Fill, wasShowing: false)); // filled: off-screen
        Assert.InRange(MusicVideoOverlayMask.Hold, 4.0, 5); // measured: gone 3.8–4.0 s in
        Assert.Equal(1, MusicVideoOverlayMask.Opacity(0));
        Assert.Equal(1, MusicVideoOverlayMask.Opacity(4.4));
        Assert.Equal(0.5, MusicVideoOverlayMask.Opacity(MusicVideoOverlayMask.Hold + MusicVideoOverlayMask.FadeOut / 2), 6);
        Assert.Equal(0, MusicVideoOverlayMask.Opacity(6));
        // The glow's sample waits while the controls are drawn.
        Assert.False(MusicVideoOverlayMask.Capturable(now: 12, coveredAt: 10));
        Assert.True(MusicVideoOverlayMask.Capturable(now: 15, coveredAt: 10));
        Assert.True(MusicVideoOverlayMask.Capturable(now: 15, coveredAt: null));
        // A 16:9 player shown in a 1600 × 900 view: three regions, the middle one centred.
        var regions = MusicVideoOverlayMask.Regions(new VideoSize(1600, 900), new VideoSize(1600, 900));
        Assert.Equal(3, regions.Count);
        Assert.Equal(new VideoRect(0, 0, 1600, MusicVideoOverlayMask.TopBand), regions[0]);
        Assert.Equal(800, regions[1].MidX, 6);
        Assert.Equal(450, regions[1].MidY, 6);
        Assert.Equal(900, regions[2].Y + regions[2].Height, 6);
        // A taller player than the view (a wide video): its bands fall outside, only the middle is left.
        Assert.Single(MusicVideoOverlayMask.Regions(new VideoSize(1600, 900), new VideoSize(1600, 600)));
        Assert.Empty(MusicVideoOverlayMask.Regions(default, new VideoSize(1600, 900)));
    }

    // Ported from MusicVideoFramingTests.swift: testWholeHidesABufferingPlayerAtOnce
    [Fact]
    public void WholeHidesABufferingPlayerAtOnce()
    {
        // Filled: a drift seek does not blink.
        Assert.Equal(YouTubeEmbed.BufferingGrace, YouTubeEmbed.HideDelay(true, MusicVideoHold.Live, false, MusicVideoFraming.Fill));
        // Whole: YouTube's overlays come back after a seek, so the player hides and is held again.
        Assert.Equal(0, YouTubeEmbed.HideDelay(true, MusicVideoHold.Live, false, MusicVideoFraming.Whole));
        Assert.Equal(YouTubeEmbed.BufferingGrace, YouTubeEmbed.HideDelay(true, MusicVideoHold.Live, false)); // fill by default
    }

    // Ported from MusicVideoFramingTests.swift: testPlayerIsDrawnLargerSoYouTubesBarsFallOutsideTheFrame
    [Fact]
    public void PlayerIsDrawnLargerSoYouTubesBarsFallOutsideTheFrame()
    {
        var frame = new VideoSize(1200, 900); // a 4:3 picture's frame
        var pillar = MusicVideoFramings.PlayerSize(4.0 / 3.0, frame);
        Assert.Equal(900, pillar.Height, 0.01);
        Assert.Equal(1600, pillar.Width, 0.01); // 16:9 at the picture's height
        var cinema = new VideoSize(1512, 1512 / 2.39);
        var letter = MusicVideoFramings.PlayerSize(2.39, cinema);
        Assert.Equal(1512, letter.Width, 0.01);
        Assert.Equal(1512.0 * 9 / 16, letter.Height, 0.01); // 16:9 at the picture's width
        var hdFrame = new VideoSize(1600, 900);
        Assert.Equal(1600, MusicVideoFramings.PlayerSize(Hd, hdFrame).Width, 0.01);
        Assert.Equal(900, MusicVideoFramings.PlayerSize(Hd, hdFrame).Height, 0.01);
        Assert.Equal(hdFrame, MusicVideoFramings.PlayerSize(double.NaN, hdFrame));
    }

    // Ported from MusicVideoFramingTests.swift: testTheVideosShapeComesFromOEmbed (a fake: nothing reaches YouTube)
    [Fact]
    public async Task TheVideosShapeComesFromOEmbed()
    {
        static byte[] Json(string text) => Encoding.UTF8.GetBytes(text);
        Assert.Equal(4.0 / 3.0, YouTubeVideoShape.AspectFromOEmbed(Json("{\"width\":200,\"height\":150,\"title\":\"x\"}")) ?? 0, 3);
        Assert.Equal(200.0 / 113.0, YouTubeVideoShape.AspectFromOEmbed(Json("{\"width\":200,\"height\":113}")) ?? 0, 3);
        Assert.Null(YouTubeVideoShape.AspectFromOEmbed(Json("{\"width\":0,\"height\":113}")));
        Assert.Null(YouTubeVideoShape.AspectFromOEmbed(Json("{\"width\":2000,\"height\":10}"))); // not believable
        Assert.Null(YouTubeVideoShape.AspectFromOEmbed(Json("nonsense")));
        var url = YouTubeVideoShape.Url("9fdQtU5CEAw");
        Assert.NotNull(url);
        Assert.Equal("www.youtube.com", url.Host);
        Assert.Contains("oembed", url.AbsoluteUri);
        Assert.Contains("9fdQtU5CEAw", url.AbsoluteUri); // only the video's ID is sent
        Assert.Null(YouTubeVideoShape.Url("bad id'"));

        var http = new FakeHttp();
        http.ByPath[YouTubeVideoShape.Path] = new FakeHttp.Reply(200, Json("{\"width\":200,\"height\":150}"));
        var found = await YouTubeVideoShape.AspectAsync("AAAAAAAAAA1", http, TestContext.Current.CancellationToken);
        Assert.Equal(4.0 / 3.0, found ?? 0, 3);
        Assert.Equal(4.0 / 3.0, YouTubeVideoShape.Cached("AAAAAAAAAA1") ?? 0, 3); // remembered
        // Windows' own: asked once per run, and nothing but the ID goes in the address.
        await YouTubeVideoShape.AspectAsync("AAAAAAAAAA1", http, TestContext.Current.CancellationToken);
        Assert.Single(http.Urls);
        Assert.Empty(http.Headers[0]);
        Assert.True(YouTubeVideoShape.IsOEmbed(http.Urls[0]));
    }

    // Windows' own: the HTTP client's allow list takes exactly the oEmbed address of one valid video ID.
    [Fact]
    public void OnlyTheOEmbedAddressOfOneVideoIsRecognised()
    {
        Assert.True(YouTubeVideoShape.IsOEmbed(YouTubeVideoShape.Url("4NRXx6U8ABQ")!));
        Assert.False(YouTubeVideoShape.IsOEmbed(new Uri("http://www.youtube.com/oembed?format=json&url=https%3A%2F%2Fwww.youtube.com%2Fwatch%3Fv%3D4NRXx6U8ABQ")));
        Assert.False(YouTubeVideoShape.IsOEmbed(new Uri("https://www.youtube.com/oembed?format=json&url=https%3A%2F%2Fwww.youtube.com%2Fwatch%3Fv%3D4NRXx6U8ABQ%26list%3Dx")));
        Assert.False(YouTubeVideoShape.IsOEmbed(new Uri("https://www.youtube.com/oembed?url=https://example.com/")));
        Assert.False(YouTubeVideoShape.IsOEmbed(new Uri("https://www.youtube.com/watch?v=4NRXx6U8ABQ")));
        Assert.False(YouTubeVideoShape.IsOEmbed(new Uri("https://example.com/oembed?format=json&url=https%3A%2F%2Fwww.youtube.com%2Fwatch%3Fv%3D4NRXx6U8ABQ")));
    }

    // Ported from MusicVideoFramingTests.swift: testTheWholeLayoutNeverDrawsTheViewLargerThanTheScreen
    [Fact]
    public void TheWholeLayoutNeverDrawsTheViewLargerThanTheScreen()
    {
        foreach (var screen in Screens)
        {
            foreach (var aspect in new[] { Hd, 4.0 / 3.0, 2.39, 1.0 })
            {
                var layout = MusicVideoFramings.WholeLayoutFor(aspect, screen);
                Assert.True(layout.Visible.Width <= screen.Width + 0.001, $"{screen} {aspect}");
                Assert.True(layout.Visible.Height <= screen.Height + 0.001, $"{screen} {aspect}");
                var fitted = Math.Max(layout.Frame.Width / (screen.Width * MusicVideoFramings.WholeBleed),
                    layout.Frame.Height / (screen.Height * MusicVideoFramings.WholeBleed));
                Assert.Equal(1, fitted, 4); // fitted: width or height matched, never cut
                var box = layout.Box;
                Assert.NotNull(box);
                Assert.True(box.Width >= 99.9, "the player covers the view across");
                Assert.True(box.Height >= 99.9, "and down");
            }
        }
        // A 16:9 video on a 16:10 laptop: the view is the picture, the player fills it.
        var hdOnLaptop = MusicVideoFramings.WholeLayoutFor(Hd, Laptop);
        Assert.True(hdOnLaptop.Visible.Height < Laptop.Height);
        Assert.Equal(100, hdOnLaptop.Box!.Height, 0.1);
        // A 4:3 video: the player is wider than the view, so its own side bars fall outside.
        var square = MusicVideoFramings.WholeLayoutFor(4.0 / 3.0, Laptop);
        Assert.True(square.Visible.Width < Laptop.Width, "bands at the sides, nothing cut");
        Assert.True(square.Box!.Width > 130);
    }

    // Windows' own: the player's window and the wallpaper's see-through part are the same whole-pixel rectangle, centred,
    // never larger than the screen.
    [Fact]
    public void TheVisiblePictureIsPlacedInWholePixels()
    {
        Assert.Equal((0, 54, 1920, 1091), MusicVideoFramings.VisiblePixels(Hd, 1920, 1200)); // 1920 × 1.01 × 9 / 16
        Assert.Equal((0, 0, 1920, 1080), MusicVideoFramings.VisiblePixels(Hd, 1920, 1080));
        Assert.Equal((152, 0, 1616, 1200), MusicVideoFramings.VisiblePixels(4.0 / 3.0, 1920, 1200)); // 1200 × 1.01 × 4 / 3: bands at the sides (cbd7216)
        var (x, y, w, h) = MusicVideoFramings.VisiblePixels(2.39, 2560, 1440);
        Assert.Equal(0, x);
        Assert.Equal(2560, w);
        Assert.Equal((1440 - h) / 2, y);
        Assert.InRange(h, 1075, 1090); // 2560 × 1.01 / 2.39
        Assert.Equal((0, 0, 0, 0), MusicVideoFramings.VisiblePixels(Hd, 0, 0));
    }

    // Ported from MusicVideoFramingTests.swift: testThePageDrawsThePlayerAtTheBox
    [Fact]
    public void ThePageDrawsThePlayerAtTheBox()
    {
        var box = YouTubeEmbed.Box.Create(133.4, 100);
        Assert.NotNull(box);
        var html = YouTubeEmbed.Html("4NRXx6U8ABQ", 0, overscan: 1, box: box);
        Assert.Contains("box = [133.400,100.000]", html);
        Assert.Contains("window.lunalyrBox = function (w, h)", html);
        Assert.Contains("frame.style.width = box ? box[0] + '%' : '';", html);
        Assert.Contains("box = null", YouTubeEmbed.Html("4NRXx6U8ABQ", 0)); // fill: no box
        Assert.Null(YouTubeEmbed.Box.Create(double.NaN, 100));
        Assert.Null(YouTubeEmbed.Box.Create(0, 100));
        Assert.Equal(400, YouTubeEmbed.Box.Create(9999, 100)!.Width); // capped
        // Windows' own: the scripts that change them live.
        Assert.Equal("window.lunalyrBox && window.lunalyrBox(133.400, 100.000)", YouTubeEmbed.BoxScript(box));
        Assert.Equal("window.lunalyrBox && window.lunalyrBox(0, 0)", YouTubeEmbed.BoxScript(null));
        Assert.Equal("window.lunalyrOverscan && window.lunalyrOverscan(1.25)", YouTubeEmbed.OverscanScript(1.25));
        Assert.Equal("window.lunalyrOverscan && window.lunalyrOverscan(1.0)", YouTubeEmbed.OverscanScript(1));
    }

    // Ported from MusicVideoFramingTests.swift: testBackdropIsSmallAndBlurred
    [Fact]
    public void BackdropIsSmallAndBlurred()
    {
        const int side = 1024;
        var pixels = new byte[side * (side / 2) * 4];
        for (var y = 0; y < side / 2; y++)
        {
            for (var x = 0; x < side; x++)
            {
                var i = (y * side + x) * 4;
                var v = x < side / 2 ? (byte)255 : (byte)0;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = v;
                pixels[i + 3] = 255;
            }
        }
        var blurred = MusicVideoBackdrop.Blurred(new CoverArt(side, side / 2, pixels));
        Assert.NotNull(blurred);
        Assert.InRange(blurred.Width, MusicVideoBackdrop.WorkingWidth - 1, MusicVideoBackdrop.WorkingWidth + 1);
        Assert.InRange(blurred.Height, blurred.Width / 2 - 1, blurred.Width / 2 + 1); // keeps its shape
        // The hard edge in the middle is softened; the outer edges keep their colour.
        var grey = Greys(blurred);
        var middle = grey[grey.Length / 2];
        Assert.InRange(middle, 41, 214);
        Assert.True(grey[0] > 230, "no dark fringe at the edge");
        Assert.True(grey[^1] < 25);
    }

    // Windows' own: a YouTube still's backdrop blurs only the video's own picture, not the bars YouTube bakes around it.
    [Fact]
    public void AStillsBackdropLeavesYouTubesBarsOut()
    {
        Assert.Equal((0, 0, 1280, 720), MusicVideoBackdrop.PictureIn(1280, 720, Hd));
        Assert.Equal((160, 0, 960, 720), MusicVideoBackdrop.PictureIn(1280, 720, 4.0 / 3.0)); // pillarboxed
        Assert.Equal((0, 92, 1280, 536), MusicVideoBackdrop.PictureIn(1280, 720, 2.39)); // letterboxed
        Assert.Equal((0, 45, 480, 270), MusicVideoBackdrop.PictureIn(480, 360, Hd)); // hqdefault: 16:9 inside 4:3
        Assert.Equal((0, 0, 0, 0), MusicVideoBackdrop.PictureIn(0, 0, Hd));
        // Black bars around a white picture: the blurred backdrop keeps no black from them.
        var pixels = new byte[480 * 360 * 4];
        for (var y = 0; y < 360; y++)
        {
            for (var x = 0; x < 480; x++)
            {
                var i = (y * 480 + x) * 4;
                var picture = y >= 79 && y < 280; // a 2.39 picture (480 × 201) in the 16:9 player in the 4:3 still
                pixels[i] = pixels[i + 1] = pixels[i + 2] = picture ? (byte)255 : (byte)0;
                pixels[i + 3] = 255;
            }
        }
        var blurred = MusicVideoBackdrop.Blurred(new CoverArt(480, 360, pixels), 2.39);
        Assert.NotNull(blurred);
        Assert.True(Greys(blurred).All(g => g > 200), "only the picture is blurred");
    }

    /// <summary>Timers by delay, fired by hand.</summary>
    private sealed class Timers
    {
        private readonly List<(double Delay, Action Work, int Id)> _pending = [];
        private int _next;

        public Action Schedule(double delay, Action work)
        {
            var id = _next++;
            _pending.Add((delay, work, id));
            return () => _pending.RemoveAll(p => p.Id == id);
        }

        public int Count => _pending.Count;

        public void Fire(double delay)
        {
            foreach (var p in _pending.Where(p => p.Delay == delay).ToList())
            {
                _pending.Remove(p);
                p.Work();
            }
        }
    }

    private static (YouTubePlayerReveal Reveal, List<bool> Shown, Timers Timers) MakeReveal(MusicVideoFraming framing)
    {
        var timers = new Timers();
        var shown = new List<bool>();
        var reveal = new YouTubePlayerReveal(timers.Schedule) { Framing = framing, Hold = MusicVideoHold.Live };
        reveal.OnShow = shown.Add;
        return (reveal, shown, timers);
    }

    // Windows' own (the Mac's coordinator setVisible, with hand-fired timers), after the Mac's e7e50f7: shown whole, a
    // start, a resume and a seek show the player at once (its controls are covered by the wallpaper instead); a buffering
    // player is covered at once.
    [Fact]
    public void WholeShowsAtOnceAndBufferingCoversAtOnce()
    {
        var (reveal, shown, timers) = MakeReveal(MusicVideoFraming.Whole);
        reveal.Set(true);
        Assert.Equal([true], shown);
        Assert.False(reveal.RevealPending);
        Assert.Equal(0, timers.Count);
        // A seek buffers: covered at once (YouTube's controls come back), then shown again as it plays.
        reveal.Set(false, buffering: true);
        Assert.Equal([true, false], shown);
        reveal.Set(true);
        Assert.Equal([true, false, true], shown);
        reveal.Set(false);
        Assert.False(reveal.Showing);
    }

    // Windows' own: filled, a playing player shows at once, and buffering waits out the grace before it is covered.
    [Fact]
    public void FillShowsAtOnceAndBufferingWaitsTheGrace()
    {
        var (reveal, shown, timers) = MakeReveal(MusicVideoFraming.Fill);
        reveal.Set(true);
        Assert.Equal([true], shown);
        reveal.Set(false, buffering: true);
        Assert.True(reveal.HidePending);
        Assert.Equal([true], shown);
        reveal.Set(true); // the seek landed in time: no blink
        Assert.False(reveal.HidePending);
        Assert.Equal([true, true], shown);
        reveal.Set(false, buffering: true);
        timers.Fire(YouTubeEmbed.BufferingGrace);
        Assert.Equal([true, true, false], shown);
        // Not live (the song paused): no grace.
        reveal.Set(true);
        reveal.Hold = MusicVideoHold.UserPaused;
        reveal.Set(false, buffering: true);
        Assert.Equal([true, true, false, true, false], shown);
        // Closing drops what waits without a call.
        reveal.Framing = MusicVideoFraming.Whole;
        reveal.Set(true);
        reveal.Reset();
        Assert.Equal(0, timers.Count);
        Assert.False(reveal.Showing);
    }

    /// <summary>One row's grey levels, across the middle of the image.</summary>
    private static byte[] Greys(CoverArt image)
    {
        var row = new byte[image.Width];
        var y = image.Height / 2;
        for (var x = 0; x < image.Width; x++)
        {
            var i = (y * image.Width + x) * 4;
            row[x] = (byte)((image.Pixels[i] + image.Pixels[i + 1] + image.Pixels[i + 2]) / 3);
        }
        return row;
    }
}
