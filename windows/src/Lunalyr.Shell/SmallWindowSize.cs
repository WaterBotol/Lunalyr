namespace Lunalyr.Shell;

/// <summary>
/// The size of the small fixed-size windows (the welcome, the lyric card, Search YouTube) on their display: their size in
/// DIPs at the display's scale, cut to the work area so the bottom row (Continue, Copy, Close) is never below the screen.
/// A 680 × 640 welcome is 1120 pixels tall at 175 %, more than a 1080-pixel display's work area; its steps scroll, so a
/// shorter window loses nothing. (The visual sweep of the smaller surfaces, docs/sweep/surfaces.md.)
/// </summary>
public static class SmallWindowSize
{
    /// <summary>The room left around a window cut to its work area (DIPs).</summary>
    public const double Margin = 8;

    /// <summary>
    /// The window's size in pixels: <paramref name="widthDips"/> × <paramref name="heightDips"/> at <paramref name="scale"/>,
    /// no wider or taller than the work area (<paramref name="workWidth"/> × <paramref name="workHeight"/> pixels) less
    /// <see cref="Margin"/> on each side.
    /// </summary>
    public static (int Width, int Height) Fit(double widthDips, double heightDips, double scale, int workWidth, int workHeight)
    {
        if (!(scale > 0) || !double.IsFinite(scale)) scale = 1;
        var room = (int)Math.Round(2 * Margin * scale);
        int width = (int)Math.Round(widthDips * scale), height = (int)Math.Round(heightDips * scale);
        if (workWidth > room) width = Math.Min(width, workWidth - room);
        if (workHeight > room) height = Math.Min(height, workHeight - room);
        return (width, height);
    }
}
