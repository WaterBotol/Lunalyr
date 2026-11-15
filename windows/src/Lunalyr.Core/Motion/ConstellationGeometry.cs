namespace Lunalyr.Core.Motion;

/// <summary>
/// Geometry for the constellation layouts: fixed word slots and the links drawn between consecutive words.
/// </summary>
/// <remarks>
/// Re-implemented from a description of the Mac's behaviour (its lines trace back to the squashed import commit):
/// <list type="bullet">
/// <item><see cref="WordEnvelope"/>: the box a word's ink can occupy at its largest (<c>peakScale</c>, held to 1–2,
/// 1.2 when not finite) and at every rotation up to <c>degrees</c> (held to 90): (w + h·sin a, h + w·sin a) × peak.
/// It never depends on the animation phase, so an entrance can never change the slot. Invalid sizes give an empty
/// box.</item>
/// <item><see cref="BuildConnections"/>: each visible word links to the visible word before it (a chain that grows
/// with the line). A link runs along the line between the two centres, from the edge of one box padded by the
/// clearance (held to 0–40, 6 when not finite) to the edge of the other, clipped to the viewport and at least 1 unit
/// long. No link when the padded boxes overlap, when an end would lie inside either padded box, or when it passes
/// through any other word padded by 60% of the clearance. Words off the viewport or with invalid boxes are skipped
/// (and not linked across). 2–64 words; at most <c>capacity</c> links.</item>
/// </list>
/// </remarks>
public static class ConstellationGeometry
{
    public const int MaxWords = 64;
    private const double DegreesToRadians = 0.017453292519943295;

    /// <summary>The fixed slot a word's ink fits in at any scale up to <paramref name="peakScale"/> and any turn up to <paramref name="degrees"/>.</summary>
    public static Box WordEnvelope(double width, double height, double peakScale, double degrees)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 0 || height < 0) return default;
        var peak = double.IsFinite(peakScale) ? Math.Clamp(peakScale, 1, 2) : 1.2;
        var turn = double.IsFinite(degrees) ? Math.Min(90, Math.Abs(degrees)) : 0;
        var lean = Math.Sin(turn * DegreesToRadians);
        return new Box(0, 0, (width + height * lean) * peak, (height + width * lean) * peak);
    }

    /// <summary>
    /// Writes the links of a constellation into <paramref name="output"/> and returns how many were written. Allocation
    /// free.
    /// </summary>
    public static int BuildConnections(ReadOnlySpan<Box> boxes, Box viewport, double clearance, Span<Segment> output)
    {
        if (boxes.Length < 2 || boxes.Length > MaxWords || output.Length < 1 || !Usable(viewport)) return 0;
        var gap = ClearanceOf(clearance);
        var written = 0;
        var before = -1;
        for (var i = 0; i < boxes.Length && written < output.Length; i++)
        {
            if (!Usable(boxes[i]) || !Overlaps(boxes[i], viewport)) continue;
            var from = before;
            before = i;
            if (from < 0) continue;
            if (!TryLink(boxes[from], boxes[i], viewport, gap, out var link)) continue;
            if (Blocked(boxes, from, i, link, gap * .6)) continue;
            output[written++] = link with { From = from, To = i };
        }
        return written;
    }

    internal static double ClearanceOf(double clearance) => double.IsFinite(clearance) ? Math.Clamp(clearance, 0, 40) : 6;

    private static bool Blocked(ReadOnlySpan<Box> boxes, int from, int to, Segment link, double padding)
    {
        for (var k = 0; k < boxes.Length; k++)
        {
            if (k == from || k == to || !Usable(boxes[k])) continue;
            // More than a unit of the link inside another (padded) word means it runs through that word.
            var inside = link;
            if (ClipTo(Grow(boxes[k], padding), ref inside) && Length(inside) > 1) return true;
        }
        return false;
    }

    private static bool Usable(Box b) =>
        double.IsFinite(b.X) && double.IsFinite(b.Y) && double.IsFinite(b.Width) && double.IsFinite(b.Height)
        && b.Width > 0 && b.Height > 0 && Math.Abs(b.X) < 1e8 && Math.Abs(b.Y) < 1e8 && b.Width < 1e8 && b.Height < 1e8;

    private static Box Grow(Box b, double by) => new(b.X - by, b.Y - by, b.Width + 2 * by, b.Height + 2 * by);

    private static bool Overlaps(Box a, Box b) =>
        a.X < b.X + b.Width && a.X + a.Width > b.X && a.Y < b.Y + b.Height && a.Y + a.Height > b.Y;

    private static bool StrictlyInside(Box b, double x, double y)
    {
        const double e = 1e-7;
        return x > b.X + e && x < b.X + b.Width - e && y > b.Y + e && y < b.Y + b.Height - e;
    }

    // The segment between two padded boxes along the line joining their centres, clipped to the viewport.
    private static bool TryLink(Box a, Box b, Box viewport, double gap, out Segment link)
    {
        link = default;
        Box pa = Grow(a, gap), pb = Grow(b, gap);
        if (Overlaps(pa, pb)) return false;
        double ax = a.X + a.Width / 2, ay = a.Y + a.Height / 2;
        double bx = b.X + b.Width / 2, by = b.Y + b.Height / 2;
        double dx = bx - ax, dy = by - ay;
        if (Math.Sqrt(dx * dx + dy * dy) < 1e-8) return false;
        // Fraction of the centre-to-centre run that lies inside each padded box.
        var leaveA = ExitFraction(pa, dx, dy);
        var leaveB = ExitFraction(pb, dx, dy);
        link = new Segment(ax + dx * leaveA, ay + dy * leaveA, bx - dx * leaveB, by - dy * leaveB, 0, 0);
        if (!ClipTo(viewport, ref link) || Length(link) <= 1) return false;
        return !StrictlyInside(pa, link.Ax, link.Ay) && !StrictlyInside(pa, link.Bx, link.By)
            && !StrictlyInside(pb, link.Ax, link.Ay) && !StrictlyInside(pb, link.Bx, link.By);
    }

    private static double ExitFraction(Box padded, double dx, double dy)
    {
        var alongX = Math.Abs(dx) > 1e-12 ? padded.Width / 2 / Math.Abs(dx) : double.PositiveInfinity;
        var alongY = Math.Abs(dy) > 1e-12 ? padded.Height / 2 / Math.Abs(dy) : double.PositiveInfinity;
        return Math.Min(alongX, alongY);
    }

    private static double Length(Segment s)
    {
        double dx = s.Bx - s.Ax, dy = s.By - s.Ay;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// Liang–Barsky: clips the segment to the box in place. False when no part of it lies in the box. (A segment that
    /// survives clipping against another word's box passes through that word.)
    /// </summary>
    private static bool ClipTo(Box r, ref Segment s)
    {
        double dx = s.Bx - s.Ax, dy = s.By - s.Ay, enter = 0, exit = 1;
        if (!ClipEdge(-dx, s.Ax - r.X, ref enter, ref exit)) return false;
        if (!ClipEdge(dx, r.X + r.Width - s.Ax, ref enter, ref exit)) return false;
        if (!ClipEdge(-dy, s.Ay - r.Y, ref enter, ref exit)) return false;
        if (!ClipEdge(dy, r.Y + r.Height - s.Ay, ref enter, ref exit)) return false;
        double x = s.Ax, y = s.Ay;
        s = s with { Ax = x + enter * dx, Ay = y + enter * dy, Bx = x + exit * dx, By = y + exit * dy };
        return true;
    }

    private static bool ClipEdge(double p, double q, ref double enter, ref double exit)
    {
        if (Math.Abs(p) < 1e-12) return q >= 0;
        var t = q / p;
        if (p < 0)
        {
            if (t > exit) return false;
            if (t > enter) enter = t;
        }
        else
        {
            if (t < enter) return false;
            if (t < exit) exit = t;
        }
        return true;
    }
}

/// <summary>
/// A memo of one canvas's constellation links: lighting and arrival redraws reuse them, only a change of layout
/// (boxes, viewport, clearance or word count) rebuilds. Bounded (64 words, 63 links), no allocation after construction.
/// One per canvas; not thread-safe.
/// </summary>
public sealed class ConnectionCache
{
    private readonly Box[] _boxes = new Box[ConstellationGeometry.MaxWords];
    private readonly Segment[] _segments = new Segment[ConstellationGeometry.MaxWords - 1];
    private Box _viewport;
    private double _clearance;
    private int _count, _written;
    private bool _valid;

    /// <summary>How many times the links were worked out (for tests and diagnostics).</summary>
    public long Builds { get; private set; }

    /// <summary>The links for these boxes, copied into <paramref name="output"/>; returns how many.</summary>
    public int Connections(ReadOnlySpan<Box> boxes, Box viewport, double clearance, Span<Segment> output)
    {
        if (boxes.Length < 2 || boxes.Length > ConstellationGeometry.MaxWords || output.Length < 1) return 0;
        if (!double.IsFinite(viewport.X) || !double.IsFinite(viewport.Y) || !(viewport.Width > 0) || !(viewport.Height > 0)
            || !(viewport.Width < 1e8) || !(viewport.Height < 1e8) || !(Math.Abs(viewport.X) < 1e8) || !(Math.Abs(viewport.Y) < 1e8))
            return 0;
        var gap = ConstellationGeometry.ClearanceOf(clearance);
        var hit = _valid && _count == boxes.Length && _clearance == gap && _viewport == viewport
            && boxes.SequenceEqual(_boxes.AsSpan(0, _count));
        if (!hit)
        {
            _written = ConstellationGeometry.BuildConnections(boxes, viewport, gap, _segments);
            boxes.CopyTo(_boxes);
            _viewport = viewport;
            _clearance = gap;
            _count = boxes.Length;
            _valid = true;
            Builds++;
        }
        var n = Math.Min(_written, output.Length);
        _segments.AsSpan(0, n).CopyTo(output);
        return n;
    }
}
