// The floating lyrics' song title card in a window of its own, ported from the Mac's FloatingLyrics.swift
// (FloatingTitleFrame, b11a1ab3; Lunalyr's own code): pure geometry, in points, y up, like FloatingFrame. Its frame is
// the card's box; the window round it has Margin more on every side for the glow.

using Lunalyr.Core.Compositions;

namespace Lunalyr.Core.Floating;

/// <summary>
/// Where a display without a frame set by hand puts the card, relative to the lyrics' box: centred across it (0 left,
/// 1 right), above it or below, and a scale of the size that follows the text size.
/// </summary>
public readonly record struct FloatingTitleStart
{
    public const double ScaleMin = 0.5, ScaleMax = 2.5;

    public FloatingTitleStart(double centre = 0.5, bool below = false, double scale = 1)
    {
        Centre = double.IsFinite(centre) ? Math.Clamp(centre, 0, 1) : 0.5;
        Below = below;
        Scale = double.IsFinite(scale) ? Math.Clamp(scale, ScaleMin, ScaleMax) : 1;
    }

    public double Centre { get; }
    public bool Below { get; }
    public double Scale { get; }

    public static FloatingTitleStart Standard => new(0.5, false, 1);

    public bool IsStandard => this == Standard;

    /// <summary>The old title row's place in the lyrics' window, carried over: its edge becomes the side of the box, its place across and its size kept.</summary>
    public static FloatingTitleStart FromRow(OpeningTitleRow row) => new(row.Centre, row.Bottom, row.Scale);

    public double[] Encoded => [Centre, Below ? 1 : 0, Scale];

    public static FloatingTitleStart? Decode(IReadOnlyList<double>? values) =>
        values is { Count: 3 } ? new FloatingTitleStart(values[0], values[1] >= 0.5, values[2]) : null;
}

/// <summary>When the floating song title card's window shows (the Mac's FloatingTitleVisibility).</summary>
public static class FloatingTitleVisibility
{
    /// <param name="lyrics">The floating lyrics' window shows on that display.</param>
    /// <param name="card">"Show the song title card" is on.</param>
    /// <param name="title">The style shows the song's title (the opening title is on).</param>
    /// <param name="editing">Edit position: the card can be placed even when its style shows no title.</param>
    public static bool Shows(bool lyrics, bool card, bool title, bool editing) => lyrics && card && (title || editing);
}

public static class FloatingTitleFrame
{
    /// <summary>Small enough to fall back to the one-line row (<see cref="Layout"/>).</summary>
    public static SizeD MinimumSize { get; } = new(120, 24);

    /// <summary>See-through room round the card for its shadow and glow, points.</summary>
    public const double Margin = 48;

    /// <summary>Between the card and the lyrics' box, by default, points.</summary>
    public const double Gap = 10;

    /// <summary>The card's size at a text size: a stacked card a little under half the lyrics' size, wide enough for a cover and a two-line title.</summary>
    public static SizeD DefaultSize(double textSize, double scale = 1)
    {
        var size = (double.IsFinite(textSize) ? Math.Max(10, textSize) : 44) * scale;
        return new SizeD(Math.Round(size * 7.2), Math.Round(size * 1.6));
    }

    /// <summary>
    /// The default frame: centred on the lyrics' box (<paramref name="start"/>'s centre across it), just above it, or below
    /// it when there is no room above (and the reverse), kept on the area.
    /// </summary>
    public static FloatingRect Start(FloatingTitleStart start, FloatingRect box, FloatingRect area, double textSize)
    {
        var size = FloatingFrame.Fitted(DefaultSize(textSize, start.Scale), area, MinimumSize);
        var x = box.MinX + start.Centre * box.Width - size.Width / 2;
        double above = box.MaxY + Gap, below = box.MinY - Gap - size.Height;
        bool fitsAbove = above + size.Height <= area.MaxY, fitsBelow = below >= area.MinY;
        var y = start.Below ? (fitsBelow || !fitsAbove ? below : above) : (fitsAbove || !fitsBelow ? above : below);
        return FloatingFrame.Clamped(new FloatingRect(Math.Round(x), Math.Round(y), size.Width, size.Height), area, MinimumSize);
    }

    /// <summary>The card's frame on one display: the one set by hand (relative to <paramref name="screenFrame"/>), else the start beside the lyrics' box.</summary>
    public static FloatingRect Resolve(FloatingRect? custom, FloatingTitleStart start, FloatingRect box, double textSize,
                                       FloatingRect screenFrame, FloatingRect area)
    {
        if (custom is { } frame) return FloatingFrame.Clamped(frame.OffsetBy(screenFrame.MinX, screenFrame.MinY), area, MinimumSize);
        return Start(start, box, area, textSize);
    }

    // ---- Stacked card or one row ----

    /// <summary>The smallest title the stacked card is drawn with; below it the card is a single row: cover, title, artist.</summary>
    public const double MinimumStackedTitle = 14;

    /// <summary>The share of the card's height and width its contents use.</summary>
    public const double Fill = 0.9;

    /// <summary>How the card is drawn in a box: the stacked card (its nominal title size) or the row (its lyric size).</summary>
    public readonly record struct CardLayout(bool Stacked, double Nominal, double RowBase);

    /// <summary>
    /// The card scales with its box: the stacked card's cover fills the height and the words have room beside it; a box too
    /// small for a readable stacked title (<see cref="MinimumStackedTitle"/>) takes the row, sized to the box's height.
    /// </summary>
    public static CardLayout Layout(SizeD size, bool compact = false)
    {
        double height = Math.Max(0, size.Height) * Fill, width = Math.Max(0, size.Width) * Fill;
        var nominal = Math.Max(0, Math.Min(height / OpeningTitle.CoverShare(compact), width / 6.5));
        var rowBase = Math.Max(0, Math.Min(height / OpeningTitle.RowCover, width / 9));
        return new CardLayout(nominal >= MinimumStackedTitle, nominal, rowBase);
    }
}
