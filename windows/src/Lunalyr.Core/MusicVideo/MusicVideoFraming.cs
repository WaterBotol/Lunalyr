// Ported from the Mac's MusicVideoFraming.swift (MusicVideoFraming, MusicVideoBackdrop; Lunalyr's own code, Test Build 88):
// how a music video sits on the screen, set separately for YouTube videos and for video files (Settings › Wallpaper ›
// Music video). The Mac's AVLayerVideoGravity becomes MusicVideoGravity; CGSize/CGRect become VideoSize/VideoRect; the
// backdrop's Core Image blur becomes a small CPU blur here (MusicVideoBackdrop.Blurred), so it is testable without a GPU.

using Lunalyr.Core.Colour;

namespace Lunalyr.Core.MusicVideo;

/// <summary>How a music video sits on the screen.</summary>
public enum MusicVideoFraming
{
    /// <summary>
    /// Aspect-filled, cropping the edges. A YouTube video is zoomed a further <see cref="MusicVideoFramings.FillOverscan"/>
    /// so YouTube's title bar and logo fall off-screen.
    /// </summary>
    Fill,

    /// <summary>
    /// The picture's width matched to the screen's (grown by <see cref="MusicVideoFramings.WholeBleed"/>): nothing is cut from
    /// its sides. A picture wider than the screen leaves space above and below, which shows a blurred, darkened copy of the
    /// album cover or the video's own still, never black bars; a taller one (4:3 on a 16:10 screen) loses a little of its
    /// top and bottom. A YouTube player stays hidden for its first <see cref="MusicVideoFramings.TitleOverlayHold"/> of play,
    /// behind the still, while YouTube's title overlay fades.
    /// </summary>
    Whole,
}

/// <summary>How a library video (a video file, on Windows) is drawn: AVFoundation's resizeAspect or resizeAspectFill.</summary>
public enum MusicVideoGravity
{
    /// <summary>The whole picture inside the container (resizeAspect).</summary>
    Fit,

    /// <summary>The container covered, the picture's edges cropped (resizeAspectFill).</summary>
    Fill,
}

/// <summary>A size in any unit.</summary>
public readonly record struct VideoSize(double Width, double Height)
{
    public static VideoSize Zero => default;
}

/// <summary>A rectangle in any unit (centred pictures may start at negative coordinates and run past the container).</summary>
public readonly record struct VideoRect(double X, double Y, double Width, double Height)
{
    public VideoSize Size => new(Width, Height);
    public double MidX => X + Width / 2;
    public double MidY => Y + Height / 2;
}

/// <summary>The rules of <see cref="MusicVideoFraming"/> (the Mac's MusicVideoFraming members).</summary>
public static class MusicVideoFramings
{
    /// <summary>The setting for YouTube videos (the Mac's <c>custom.musicVideoFraming</c>).</summary>
    public const string YouTubeKey = "musicVideoFraming";

    /// <summary>The setting for video files (the Mac's library videos, <c>custom.libraryVideoFraming</c>).</summary>
    public const string LibraryKey = "libraryVideoFraming";

    /// <summary>Every choice, in the menu's order.</summary>
    public static IReadOnlyList<MusicVideoFraming> All { get; } = [MusicVideoFraming.Fill, MusicVideoFraming.Whole];

    /// <summary>YouTube's player is 16:9; other shapes are letterboxed inside it.</summary>
    public const double YouTubeAspect = 16.0 / 9.0;

    /// <summary>How much larger than the screen a filled YouTube player is drawn.</summary>
    public const double FillOverscan = 1.25;

    /// <summary>A fitted picture is grown this much, so no hairline shows at its edge.</summary>
    public const double WholeBleed = 1.01;

    /// <summary>
    /// YouTube shows the video's title, channel, a pause button, "More videos" and its logo over the first seconds of play,
    /// and again after a pause or a seek. Filled, they are off-screen; fitted, the player is revealed only once they have
    /// faded. Measured on the Mac on four official videos (13 Nov 2026): gone 4.0–4.4 s after playing starts, after a
    /// start, a resume and a seek alike.
    /// </summary>
    public const double TitleOverlayHold = 5;

    /// <summary>The stored value ("fill" or "whole").</summary>
    public static string Value(this MusicVideoFraming framing) => framing == MusicVideoFraming.Whole ? "whole" : "fill";

    /// <summary>A stored value, or <see cref="MusicVideoFraming.Fill"/> (the look before the choice existed).</summary>
    public static MusicVideoFraming FromStored(object? stored) =>
        stored is string text && text == "whole" ? MusicVideoFraming.Whole : MusicVideoFraming.Fill;

    /// <summary>The menu's title.</summary>
    public static string Title(this MusicVideoFraming framing) =>
        framing == MusicVideoFraming.Whole ? "Show the whole video" : "Fill the screen";

    /// <summary>The YouTube page's zoom of its player.</summary>
    public static double YouTubeOverscan(this MusicVideoFraming framing) => framing == MusicVideoFraming.Fill ? FillOverscan : 1;

    /// <summary>How long a YouTube player that starts playing stays hidden.</summary>
    public static double RevealHold(this MusicVideoFraming framing) => framing == MusicVideoFraming.Whole ? TitleOverlayHold : 0;

    /// <summary>
    /// How a video file of <paramref name="contentAspect"/> (null until known) is drawn in <paramref name="container"/>:
    /// whole matches the width, which is a fit for a wider picture and a fill for a taller one.
    /// </summary>
    public static MusicVideoGravity VideoGravity(this MusicVideoFraming framing, double? contentAspect, VideoSize container)
    {
        if (framing != MusicVideoFraming.Whole) return MusicVideoGravity.Fill;
        if (contentAspect is not { } aspect || aspect <= 0 || !double.IsFinite(aspect) || container.Width <= 0 || container.Height <= 0)
            return MusicVideoGravity.Fit;
        return aspect >= container.Width / container.Height ? MusicVideoGravity.Fit : MusicVideoGravity.Fill;
    }

    /// <summary>The extra scale of a fitted picture (none when filled).</summary>
    public static double BleedScale(this MusicVideoFraming framing) => framing == MusicVideoFraming.Whole ? WholeBleed : 1;

    /// <summary>Whether a blurred backdrop shows around the picture.</summary>
    public static bool ShowsBackdrop(this MusicVideoFraming framing) => framing == MusicVideoFraming.Whole;

    /// <summary>
    /// Where a video of <paramref name="aspect"/> (width / height) is drawn in <paramref name="container"/> (centred; may run
    /// past the container's edges, which clip it).
    /// </summary>
    public static VideoRect Rect(this MusicVideoFraming framing, double aspect, VideoSize container)
    {
        if (!(aspect > 0) || !double.IsFinite(aspect) || container.Width <= 0 || container.Height <= 0)
            return new VideoRect(0, 0, container.Width, container.Height);
        var byWidth = new VideoSize(container.Width, container.Width / aspect);
        var byHeight = new VideoSize(container.Height * aspect, container.Height);
        var fits = byWidth.Height <= container.Height;
        var size = framing == MusicVideoFraming.Whole ? byWidth : fits ? byHeight : byWidth;
        var grow = framing.BleedScale();
        size = new VideoSize(size.Width * grow, size.Height * grow);
        return new VideoRect((container.Width - size.Width) / 2, (container.Height - size.Height) / 2, size.Width, size.Height);
    }

    /// <summary>
    /// The 16:9 player's size for a picture of <paramref name="contentAspect"/> shown in <paramref name="frame"/> (sized to
    /// that aspect): YouTube letterboxes or pillarboxes a picture of another shape inside its player, so the player is
    /// drawn larger and its own bars fall outside the frame (clipped).
    /// </summary>
    public static VideoSize PlayerSize(double contentAspect, VideoSize frame)
    {
        if (!(contentAspect > 0) || !double.IsFinite(contentAspect) || frame.Width <= 0 || frame.Height <= 0) return frame;
        var width = contentAspect < YouTubeAspect ? frame.Height * YouTubeAspect : frame.Width;
        return new VideoSize(width, width / YouTubeAspect);
    }

    /// <summary>
    /// A YouTube video shown whole in a container: the picture's frame (the width matched), the part of it on screen (the
    /// web view's size, never larger than the container), the 16:9 player's size, and the player's size within the visible
    /// part in per cent (for the page).
    /// </summary>
    public readonly record struct WholeLayout(VideoSize Frame, VideoSize Visible, VideoSize Player, YouTubeEmbed.Box? Box)
    {
        /// <summary>The visible part, centred in <paramref name="container"/>.</summary>
        public VideoRect VisibleRect(VideoSize container) =>
            new((container.Width - Visible.Width) / 2, (container.Height - Visible.Height) / 2, Visible.Width, Visible.Height);

        /// <summary>The player, centred in <paramref name="container"/> (it runs past the visible part when the shapes differ).</summary>
        public VideoRect PlayerRect(VideoSize container) =>
            new((container.Width - Player.Width) / 2, (container.Height - Player.Height) / 2, Player.Width, Player.Height);
    }

    public static WholeLayout WholeLayoutFor(double contentAspect, VideoSize container)
    {
        var frame = MusicVideoFraming.Whole.Rect(contentAspect, container).Size;
        var visible = new VideoSize(Math.Min(frame.Width, container.Width), Math.Min(frame.Height, container.Height));
        var player = PlayerSize(contentAspect, frame);
        var box = visible.Width > 0 && visible.Height > 0
            ? YouTubeEmbed.Box.Create(player.Width / visible.Width * 100, player.Height / visible.Height * 100)
            : null;
        return new WholeLayout(frame, visible, player, box);
    }

    /// <summary>
    /// The visible part of a whole YouTube video in whole device pixels, centred in a <paramref name="width"/> ×
    /// <paramref name="height"/> pixel container: where the player's window goes, and where the wallpaper is see-through.
    /// </summary>
    public static (int X, int Y, int Width, int Height) VisiblePixels(double contentAspect, int width, int height)
    {
        if (width <= 0 || height <= 0) return (0, 0, Math.Max(0, width), Math.Max(0, height));
        var visible = WholeLayoutFor(contentAspect, new VideoSize(width, height)).Visible;
        var w = Math.Clamp((int)Math.Round(visible.Width), 1, width);
        var h = Math.Clamp((int)Math.Round(visible.Height), 1, height);
        return ((width - w) / 2, (height - h) / 2, w, h);
    }

    /// <summary>The share of the picture's area that falls outside <paramref name="container"/>, 0…1. A YouTube video's overscan is included.</summary>
    public static double CroppedShare(this MusicVideoFraming framing, double aspect, VideoSize container, double overscan = 1)
    {
        var drawn = framing.Rect(aspect, container);
        var width = drawn.Width * overscan;
        var height = drawn.Height * overscan;
        if (!(width > 0) || !(height > 0)) return 0;
        var seenWidth = Math.Min(width, container.Width);
        var seenHeight = Math.Min(height, container.Height);
        return 1 - seenWidth * seenHeight / (width * height);
    }

    /// <summary>The share of <paramref name="container"/> the picture leaves for the backdrop, 0…1.</summary>
    public static double BackdropShare(this MusicVideoFraming framing, double aspect, VideoSize container)
    {
        var drawn = framing.Rect(aspect, container);
        var area = container.Width * container.Height;
        if (!(area > 0)) return 0;
        var covered = Math.Min(drawn.Width, container.Width) * Math.Min(drawn.Height, container.Height);
        return Math.Max(0, 1 - covered / area);
    }
}

/// <summary>
/// The blurred, darkened copy around a video shown whole: blurred once, small, off the render thread, then drawn stretched,
/// so the wallpaper never blurs a full-screen image on every frame.
/// </summary>
public static class MusicVideoBackdrop
{
    /// <summary>The width the picture is reduced to before blurring.</summary>
    public const int WorkingWidth = 256;

    /// <summary>The blur's radius at that width (about 48 points on a 1600-point screen).</summary>
    public const double BlurRadius = 8;

    /// <summary>Black at this opacity over the blurred picture.</summary>
    public const double Darkening = 0.45;

    /// <summary>
    /// A small, blurred copy of <paramref name="image"/> (at most <see cref="WorkingWidth"/> wide, its shape kept), its edges
    /// kept (no dark fringe: the picture's own edge pixels continue past it, as Core Image's clampedToExtent). Premultiplied
    /// BGRA in and out. Null for an empty picture.
    /// </summary>
    public static CoverArt? Blurred(CoverArt image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width <= 0 || image.Height <= 0) return null;
        return BlurredWhole(image);
    }

    /// <summary>
    /// Where a YouTube still's own picture lies (Windows' own): YouTube's stills are its 16:9 player (letterboxed again inside
    /// 4:3 for the smaller one), with the video's own shape barred inside that. Integer pixels, centred.
    /// </summary>
    public static (int X, int Y, int Width, int Height) PictureIn(int width, int height, double contentAspect)
    {
        if (width <= 0 || height <= 0 || !(contentAspect > 0) || !double.IsFinite(contentAspect)) return (0, 0, Math.Max(0, width), Math.Max(0, height));
        // The 16:9 player inside the still.
        double pw = width, ph = height;
        if (pw / ph > MusicVideoFramings.YouTubeAspect) pw = ph * MusicVideoFramings.YouTubeAspect;
        else ph = pw / MusicVideoFramings.YouTubeAspect;
        // The picture inside the player.
        double w = pw, h = ph;
        if (contentAspect < MusicVideoFramings.YouTubeAspect) w = ph * contentAspect;
        else h = pw / contentAspect;
        var iw = Math.Clamp((int)Math.Round(w), 1, width);
        var ih = Math.Clamp((int)Math.Round(h), 1, height);
        return ((width - iw) / 2, (height - ih) / 2, iw, ih);
    }

    /// <summary>
    /// The backdrop of a YouTube still of a video of <paramref name="contentAspect"/>: its own picture only, cut out of
    /// YouTube's bars before blurring, so a wide video's backdrop isn't darkened by the bars baked into its still (Windows'
    /// own refinement; the Mac blurs the whole still).
    /// </summary>
    public static CoverArt? Blurred(CoverArt image, double contentAspect)
    {
        ArgumentNullException.ThrowIfNull(image);
        var (x, y, w, h) = PictureIn(image.Width, image.Height, contentAspect);
        if (w == image.Width && h == image.Height) return Blurred(image);
        var pixels = new byte[w * h * 4];
        for (var row = 0; row < h; row++)
            Array.Copy(image.Pixels, ((y + row) * image.Width + x) * 4, pixels, row * w * 4, w * 4);
        return BlurredWhole(new CoverArt(w, h, pixels));
    }

    private static CoverArt BlurredWhole(CoverArt image)
    {
        var scale = Math.Min(1.0, (double)WorkingWidth / image.Width);
        var width = Math.Max(1, (int)Math.Round(image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(image.Height * scale));
        var small = Reduce(image, width, height);
        // Core Image's CIGaussianBlur radius is its standard deviation.
        var kernel = Kernel(BlurRadius);
        var pass = new float[small.Length];
        Blur(small, pass, width, height, kernel, horizontal: true);
        Blur(pass, small, width, height, kernel, horizontal: false);
        var pixels = new byte[small.Length];
        for (var i = 0; i < small.Length; i++) pixels[i] = (byte)Math.Clamp((int)Math.Round(small[i]), 0, 255);
        return new CoverArt(width, height, pixels);
    }

    /// <summary>An area-averaged reduction (each target pixel is the mean of the source pixels it covers).</summary>
    private static float[] Reduce(CoverArt image, int width, int height)
    {
        var source = image.Pixels;
        var result = new float[width * height * 4];
        var sx = (double)image.Width / width;
        var sy = (double)image.Height / height;
        for (var y = 0; y < height; y++)
        {
            var y0 = (int)Math.Floor(y * sy);
            var y1 = Math.Max(y0 + 1, Math.Min(image.Height, (int)Math.Ceiling((y + 1) * sy)));
            for (var x = 0; x < width; x++)
            {
                var x0 = (int)Math.Floor(x * sx);
                var x1 = Math.Max(x0 + 1, Math.Min(image.Width, (int)Math.Ceiling((x + 1) * sx)));
                double b = 0, g = 0, r = 0, a = 0;
                var count = 0;
                for (var yy = y0; yy < y1; yy++)
                {
                    var row = yy * image.Width * 4;
                    for (var xx = x0; xx < x1; xx++)
                    {
                        var i = row + xx * 4;
                        b += source[i];
                        g += source[i + 1];
                        r += source[i + 2];
                        a += source[i + 3];
                        count++;
                    }
                }
                var o = (y * width + x) * 4;
                result[o] = (float)(b / count);
                result[o + 1] = (float)(g / count);
                result[o + 2] = (float)(r / count);
                result[o + 3] = (float)(a / count);
            }
        }
        return result;
    }

    private static float[] Kernel(double sigma)
    {
        var radius = (int)Math.Ceiling(sigma * 3);
        var kernel = new float[radius * 2 + 1];
        double sum = 0;
        for (var i = -radius; i <= radius; i++)
        {
            var w = Math.Exp(-(i * i) / (2 * sigma * sigma));
            kernel[i + radius] = (float)w;
            sum += w;
        }
        for (var i = 0; i < kernel.Length; i++) kernel[i] = (float)(kernel[i] / sum);
        return kernel;
    }

    /// <summary>One pass of a separable Gaussian, the edge pixels continued past the edge.</summary>
    private static void Blur(float[] from, float[] to, int width, int height, float[] kernel, bool horizontal)
    {
        var radius = kernel.Length / 2;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                float b = 0, g = 0, r = 0, a = 0;
                for (var k = -radius; k <= radius; k++)
                {
                    var sx = horizontal ? Math.Clamp(x + k, 0, width - 1) : x;
                    var sy = horizontal ? y : Math.Clamp(y + k, 0, height - 1);
                    var i = (sy * width + sx) * 4;
                    var w = kernel[k + radius];
                    b += from[i] * w;
                    g += from[i + 1] * w;
                    r += from[i + 2] * w;
                    a += from[i + 3] * w;
                }
                var o = (y * width + x) * 4;
                to[o] = b;
                to[o + 1] = g;
                to[o + 2] = r;
                to[o + 3] = a;
            }
        }
    }
}
