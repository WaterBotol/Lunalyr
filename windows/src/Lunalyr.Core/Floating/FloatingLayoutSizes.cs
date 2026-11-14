// Each layout's own floating size (tester feedback on 0.9.0, 14 Nov 2026): a layout keeps the window size and text size it
// was last resized to, while the window's place is shared by every layout (roughly: each is centred where the last one
// was put). Resizing the window in Edit position scales the text with it, so a bigger window means bigger lyrics.
// Windows' own; the Mac keeps one size for all layouts.

using Lunalyr.Core.Compositions;

namespace Lunalyr.Core.Floating;

/// <summary>A layout's floating window size in points and its text size.</summary>
public readonly record struct FloatingLayoutSize(SizeD Size, double TextSize)
{
    public const double TextSizeMin = 22, TextSizeMax = 110;

    /// <summary>Stored as [width, height, text size].</summary>
    public double[] Encode() => [Size.Width, Size.Height, TextSize];

    public static FloatingLayoutSize? Decode(IReadOnlyList<double>? values)
    {
        if (values is not { Count: 3 } || !values.All(double.IsFinite) || values[0] <= 0 || values[1] <= 0) return null;
        return new FloatingLayoutSize(new SizeD(values[0], values[1]), Math.Clamp(values[2], TextSizeMin, TextSizeMax));
    }

    /// <summary>The text size after the window went from <paramref name="oldHeight"/> to <paramref name="newHeight"/>: scaled with it.</summary>
    public static double ScaledText(double textSize, double oldHeight, double newHeight)
    {
        if (!double.IsFinite(textSize) || !(oldHeight > 0) || !(newHeight > 0) || !double.IsFinite(newHeight / oldHeight))
            return Math.Clamp(double.IsFinite(textSize) ? textSize : 44, TextSizeMin, TextSizeMax);
        return Math.Clamp(Math.Round(textSize * newHeight / oldHeight, 1), TextSizeMin, TextSizeMax);
    }

    /// <summary>The shared place, <paramref name="frame"/>, with this layout's size, centred where it was.</summary>
    public FloatingRect Centred(FloatingRect frame) =>
        new(frame.X + (frame.Width - Size.Width) / 2, frame.Y + (frame.Height - Size.Height) / 2, Size.Width, Size.Height);
}
