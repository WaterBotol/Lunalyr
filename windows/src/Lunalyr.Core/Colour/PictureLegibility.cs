// How an imported picture or video sits on the screen, and how bright it is behind the lyric stage. Ported from the
// Mac's Settings.MotionPlacement (its cases and captions) and LyricLegibility.stageLuminance(of:screen:placement:)
// (Lunalyr's own lines: git blame finds none from the squashed Verso import), with its test.

namespace Lunalyr.Core.Colour;

/// <summary>
/// How a picture or video is fitted to the screen (the Mac's <c>MotionPlacement</c>). Screens are wide and pictures
/// often aren't, so something has to give: fill crops, fit shows the whole frame with black beside it. The anchored
/// fills exist because a picture's subject is rarely dead centre.
/// </summary>
public enum WallpaperPlacement
{
    /// <summary>"Fill": crops equally top and bottom (or left and right).</summary>
    Fill,

    /// <summary>"Fill, top": crops from the bottom, keeping the top.</summary>
    FillTop,

    /// <summary>"Fill, bottom": crops from the top.</summary>
    FillBottom,

    /// <summary>"Fit whole frame": nothing cropped; the sides are left black.</summary>
    Fit,
}

public static class WallpaperPlacements
{
    /// <summary>The placement for a stored raw value ("fill", "fillTop", "fillBottom", "fit"); anything else is Fill.</summary>
    public static WallpaperPlacement Parse(string? raw) => raw switch
    {
        "fillTop" => WallpaperPlacement.FillTop,
        "fillBottom" => WallpaperPlacement.FillBottom,
        "fit" => WallpaperPlacement.Fit,
        _ => WallpaperPlacement.Fill,
    };

    /// <summary>The Mac's raw value.</summary>
    public static string RawValue(this WallpaperPlacement placement) => placement switch
    {
        WallpaperPlacement.FillTop => "fillTop",
        WallpaperPlacement.FillBottom => "fillBottom",
        WallpaperPlacement.Fit => "fit",
        _ => "fill",
    };

    /// <summary>
    /// Where a <paramref name="width"/> × <paramref name="height"/> picture lands on a screen of
    /// <paramref name="screenWidth"/> × <paramref name="screenHeight"/>, top-left origin: scaled to cover the screen
    /// (fill) or to fit inside it, centred across, and placed top, bottom or centre down it.
    /// </summary>
    public static (double X, double Y, double Width, double Height) Frame(double width, double height, double screenWidth,
        double screenHeight, WallpaperPlacement placement)
    {
        if (!(width > 0 && height > 0 && screenWidth > 0 && screenHeight > 0)) return (0, 0, 0, 0);
        var scale = placement == WallpaperPlacement.Fit
            ? Math.Min(screenWidth / width, screenHeight / height)
            : Math.Max(screenWidth / width, screenHeight / height);
        double shownWidth = width * scale, shownHeight = height * scale;
        var x = (screenWidth - shownWidth) / 2;
        var y = placement switch
        {
            WallpaperPlacement.FillTop => 0,
            WallpaperPlacement.FillBottom => screenHeight - shownHeight,
            _ => (screenHeight - shownHeight) / 2,
        };
        return (x, y, shownWidth, shownHeight);
    }
}

/// <summary>How bright a picture is where the lyrics sit, so the text backing can be as strong as it needs.</summary>
public static class PictureLegibility
{
    /// <summary>The longest side of the small copy a picture is measured on.</summary>
    public const int SampleSide = 72;

    /// <summary>
    /// A high percentile (85%) of the relative luminance over the part of a picture that lands behind the lyric stage
    /// (the middle 80% × 72% of the screen) when placed on a screen of <paramref name="screenWidth"/> ×
    /// <paramref name="screenHeight"/>. A fitted picture's black bars inside the stage count as black. Null when either
    /// size is empty or nothing lands behind the stage.
    /// </summary>
    /// <param name="bgra">The picture as BGRA, 8 bits a channel, premultiplied or opaque, rows top to bottom.</param>
    public static double? StageLuminance(ReadOnlySpan<byte> bgra, int width, int height, double screenWidth, double screenHeight,
        WallpaperPlacement placement, double percentile = 0.85)
    {
        if (screenWidth <= 0 || screenHeight <= 0 || width <= 0 || height <= 0 || bgra.Length < width * height * 4) return null;
        // A small copy: SampleSide on the longer side, each cell the mean of the pixels it covers.
        var ratio = width / (double)height;
        int columns = Math.Max(1, (int)(ratio >= 1 ? SampleSide : SampleSide * ratio));
        int rows = Math.Max(1, (int)(ratio >= 1 ? SampleSide / ratio : SampleSide));
        var (originX, originY, shownWidth, shownHeight) = WallpaperPlacements.Frame(width, height, screenWidth, screenHeight, placement);
        double stageWidth = screenWidth * 0.80, stageHeight = screenHeight * 0.72;
        double stageLeft = (screenWidth - stageWidth) / 2, stageTop = (screenHeight - stageHeight) / 2;
        var samples = new List<double>(columns * rows);
        for (var row = 0; row < rows; row++)
        {
            var y = originY + (row + 0.5) / rows * shownHeight;
            if (y < stageTop || y > stageTop + stageHeight) continue;
            for (var column = 0; column < columns; column++)
            {
                var x = originX + (column + 0.5) / columns * shownWidth;
                if (x < stageLeft || x > stageLeft + stageWidth) continue;
                samples.Add(CellLuminance(bgra, width, height, column, row, columns, rows));
            }
        }
        if (placement == WallpaperPlacement.Fit && shownWidth > 0 && shownHeight > 0)
        {
            var coverWidth = Math.Max(0, Math.Min(stageLeft + stageWidth, originX + shownWidth) - Math.Max(stageLeft, originX));
            var coverHeight = Math.Max(0, Math.Min(stageTop + stageHeight, originY + shownHeight) - Math.Max(stageTop, originY));
            var barShare = 1 - coverWidth * coverHeight / (stageWidth * stageHeight);
            var bars = (int)Math.Round(samples.Count * barShare / Math.Max(0.01, 1 - barShare));
            for (var i = 0; i < Math.Min(bars, 20_000); i++) samples.Add(0);
        }
        if (samples.Count == 0) return null;
        samples.Sort();
        var index = Math.Min(samples.Count - 1, Math.Max(0, (int)Math.Round((samples.Count - 1) * percentile)));
        return samples[index];
    }

    private static double CellLuminance(ReadOnlySpan<byte> bgra, int width, int height, int column, int row, int columns, int rows)
    {
        int x0 = column * width / columns, x1 = Math.Max(x0 + 1, (column + 1) * width / columns);
        int y0 = row * height / rows, y1 = Math.Max(y0 + 1, (row + 1) * height / rows);
        // Big cells are sampled on a grid of at most 8 × 8 pixels: the mean barely moves, the cost does.
        int stepX = Math.Max(1, (x1 - x0) / 8), stepY = Math.Max(1, (y1 - y0) / 8);
        double r = 0, g = 0, b = 0;
        var count = 0;
        for (var y = y0; y < Math.Min(y1, height); y += stepY)
        {
            for (var x = x0; x < Math.Min(x1, width); x += stepX)
            {
                var offset = (y * width + x) * 4;
                b += bgra[offset];
                g += bgra[offset + 1];
                r += bgra[offset + 2];
                count++;
            }
        }
        if (count == 0) return 0;
        return LyricLegibility.Luminance(new Double3(r / count / 255, g / count / 255, b / count / 255));
    }
}
