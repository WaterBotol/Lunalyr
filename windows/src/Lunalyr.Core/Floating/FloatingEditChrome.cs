// The floating window's edit mode (the Mac's FloatingEditChrome in FloatingLyricsView.swift, Lunalyr's own lines): an
// outline, a hint, Done, and a grip in the corner. The whole window moves with a drag; the grip resizes it. Its
// geometry lives here so the drawing (the engine) and the hit testing (the window) agree.

using Lunalyr.Core.Motion;

namespace Lunalyr.Core.Floating;

/// <summary>What a press in the floating window's edit mode lands on.</summary>
public enum FloatingHit
{
    Move,
    Resize,
    Done,
}

/// <summary>
/// The edges a resize moves (Windows: any edge or corner, as a window's own border; the Mac has only the corner grip).
/// Left and Right are x; Top and Bottom are as the window shows them on screen.
/// </summary>
[Flags]
public enum FloatingEdges
{
    None = 0,
    Left = 1,
    Top = 2,
    Right = 4,
    Bottom = 8,
}

/// <summary>Edit mode's parts, in window pixels (origin top left, y down), at a DPI scale.</summary>
public static class FloatingEditChrome
{
    /// <summary>The parts' distance from the window's edges, in points.</summary>
    public const double Inset = 8;

    public const double CornerRadius = 14, OutlineWidth = 2, DashOn = 7, DashOff = 5;
    public const double GripSide = 26, GripReach = 6;

    /// <summary>How close to an edge (points) a press resizes from that edge rather than moving the window.</summary>
    public const double EdgeReach = 8;
    public const double DoneWidth = 64, DoneHeight = 26;
    /// <summary>The hint's capsule is as tall as Done beside it, so the two sit on one line.</summary>
    public const double HintHeight = DoneHeight, HintTextSize = 12, PlaceholderTextSize = 22;

    public const string Hint = "Drag to move, edges or corner to resize";
    public const string Placeholder = "Lyrics appear here";
    public const string DoneTitle = "Done";

    /// <summary>Done, at the top right.</summary>
    public static Box Done(double width, double height, double scale) =>
        new(width - (Inset + DoneWidth) * scale, Inset * scale, DoneWidth * scale, DoneHeight * scale);

    /// <summary>The resize grip, a circle in the bottom right corner.</summary>
    public static Box Grip(double width, double height, double scale) =>
        new(width - (Inset + GripSide) * scale, height - (Inset + GripSide) * scale, GripSide * scale, GripSide * scale);

    /// <summary>The hint's capsule, at the top left; its width follows the text the caller measured.</summary>
    public static Box HintBox(double textWidth, double scale) => new(Inset * scale, Inset * scale, textWidth + 20 * scale, HintHeight * scale);

    /// <summary>What a press at (<paramref name="x"/>, <paramref name="y"/>) does: Done, a resize (the grip or an edge), or a move.</summary>
    public static FloatingHit HitTest(double x, double y, double width, double height, double scale)
    {
        if (Inside(Done(width, height, scale), x, y, 0)) return FloatingHit.Done;
        return EdgesAt(x, y, width, height, scale) == FloatingEdges.None ? FloatingHit.Move : FloatingHit.Resize;
    }

    /// <summary>
    /// The edges a press at (<paramref name="x"/>, <paramref name="y"/>) resizes, in window pixels (y down): the grip (a
    /// little beyond its circle, on into the corner) is the bottom-right corner; within <see cref="EdgeReach"/> of an
    /// edge, that edge (two near a corner). None for Done and for the middle (a move).
    /// </summary>
    public static FloatingEdges EdgesAt(double x, double y, double width, double height, double scale)
    {
        if (Inside(Done(width, height, scale), x, y, 0)) return FloatingEdges.None;
        var grip = Grip(width, height, scale);
        if (x >= grip.X - GripReach * scale && y >= grip.Y - GripReach * scale && x < width && y < height)
            return FloatingEdges.Right | FloatingEdges.Bottom;
        var reach = EdgeReach * scale;
        var edges = FloatingEdges.None;
        if (x < reach) edges |= FloatingEdges.Left;
        else if (x >= width - reach) edges |= FloatingEdges.Right;
        if (y < reach) edges |= FloatingEdges.Top;
        else if (y >= height - reach) edges |= FloatingEdges.Bottom;
        return edges;
    }

    private static bool Inside(Box box, double x, double y, double reach) =>
        x >= box.X - reach && x < box.X + box.Width + reach && y >= box.Y - reach && y < box.Y + box.Height + reach;
}
