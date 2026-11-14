namespace Lunalyr.Shell.Settings;

/// <summary>
/// How big the Settings window opens. Its size is in DIPs, so every display scale gets the same layout (until 14 Nov 2026
/// it was a fixed 1400 × 1040 physical pixels: 1120 × 832 DIPs at 125 %, but only 700 × 520 at 200 %, where the 240-DIP
/// navigation pane left the pages 388 DIPs and their text wrapped a letter at a time). A display too small for it gets
/// its work area less a margin.
/// </summary>
public static class SettingsWindowSize
{
    /// <summary>The size it opens at, in DIPs, wherever it fits.</summary>
    public const double Width = 1120, Height = 832;

    /// <summary>Kept clear between the window and each edge of the work area, in DIPs.</summary>
    public const double Margin = 24;

    /// <summary>
    /// How small and how large it can be dragged, in DIPs (tester feedback on 0.9.0): narrower than this the cards
    /// crowd; wider, the rows stretch past reading width.
    /// </summary>
    public const double MinWidth = 720, MinHeight = 520, MaxWidth = 1440, MaxHeight = 1200;

    /// <summary>The drag limits in physical pixels at <paramref name="scale"/>.</summary>
    public static (int MinWidth, int MinHeight, int MaxWidth, int MaxHeight) Limits(double scale)
    {
        if (!(scale > 0) || !double.IsFinite(scale)) scale = 1;
        return ((int)Math.Round(MinWidth * scale), (int)Math.Round(MinHeight * scale),
                (int)Math.Round(MaxWidth * scale), (int)Math.Round(MaxHeight * scale));
    }

    /// <summary>
    /// The opening size in physical pixels at <paramref name="scale"/> (1.25 for 125 %) on a display whose work area is
    /// <paramref name="workWidth"/> × <paramref name="workHeight"/> physical pixels.
    /// </summary>
    public static (int Width, int Height) Pixels(double scale, int workWidth, int workHeight)
    {
        if (!(scale > 0) || !double.IsFinite(scale)) scale = 1;
        return (Side(Width, workWidth), Side(Height, workHeight));

        int Side(double dips, int work)
        {
            var wanted = dips * scale;
            if (work <= 0) return (int)Math.Round(wanted);
            var room = Math.Max(work - 2 * Margin * scale, work * 0.5);
            return (int)Math.Round(Math.Min(wanted, room));
        }
    }
}
