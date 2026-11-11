namespace Lunalyr.Platform.Desktop;

/// <summary>An axis-aligned rectangle in virtual-screen pixels (x right, y down).</summary>
public readonly record struct Box(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public static Box FromEdges(double left, double top, double right, double bottom) => new(left, top, right - left, bottom - top);

    /// <summary>Finite, with a positive size.</summary>
    public bool IsUsable => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Width) && double.IsFinite(Height)
        && Width > 0 && Height > 0;

    public bool Contains(double px, double py) => IsUsable && double.IsFinite(px) && double.IsFinite(py)
        && px >= X && px < Right && py >= Y && py < Bottom;

    /// <summary>The overlap of two usable boxes, or null.</summary>
    public Box? Intersect(in Box other)
    {
        if (!IsUsable || !other.IsUsable) return null;
        double x0 = Math.Max(X, other.X), y0 = Math.Max(Y, other.Y);
        double x1 = Math.Min(Right, other.Right), y1 = Math.Min(Bottom, other.Bottom);
        return x1 > x0 && y1 > y0 ? FromEdges(x0, y0, x1, y1) : null;
    }

    /// <summary>True when this box holds all of <paramref name="other"/>.</summary>
    public bool Covers(in Box other) => IsUsable && other.IsUsable
        && X <= other.X && Y <= other.Y && Right >= other.Right && Bottom >= other.Bottom;
}

/// <summary>
/// The geometry behind "Pause fully covered wallpapers", ported from the Mac (<c>vc_exposed_fraction</c> and
/// <c>vc_region_covered</c> in DesktopInteraction.c, Lunalyr's own lines). A monitor counts as covered only when the
/// windows leave nothing noticeable of its desktop showing: no more than <see cref="MaximumExposedFraction"/> of it,
/// and no exposed square as large as the noticeable gap.
/// </summary>
public static class DesktopCoverage
{
    /// <summary>Exposed strips thinner than this (tiling margins) don't keep a wallpaper rendering. In DIPs: scale by the monitor's DPI.</summary>
    public const double NoticeableGap = 24;

    /// <summary>Upper bound on the share of the usable desktop allowed to show.</summary>
    public const double MaximumExposedFraction = 0.06;

    /// <summary>Only the first this-many covers are considered; callers pass the largest first, so dropping the rest can only under-report.</summary>
    public const int MaxCovers = 64;

    /// <summary>
    /// The share of <paramref name="region"/> no cover reaches, from 0 to 1. Exact, by coordinate compression: each
    /// compressed cell is wholly inside a cover or wholly outside all of them, so its centre decides.
    /// </summary>
    public static double ExposedFraction(in Box region, ReadOnlySpan<Box> covers)
    {
        if (!region.IsUsable || covers.IsEmpty) return 1;
        if (covers.Length > MaxCovers) covers = covers[..MaxCovers];
        Span<Box> clipped = stackalloc Box[covers.Length];
        Span<double> xs = stackalloc double[2 * covers.Length + 2];
        Span<double> ys = stackalloc double[2 * covers.Length + 2];
        int n = 0, nx = 0, ny = 0;
        xs[nx++] = region.X; xs[nx++] = region.Right;
        ys[ny++] = region.Y; ys[ny++] = region.Bottom;
        foreach (var cover in covers)
        {
            if (region.Intersect(cover) is not { } c) continue;
            clipped[n++] = c;
            xs[nx++] = c.X; xs[nx++] = c.Right;
            ys[ny++] = c.Y; ys[ny++] = c.Bottom;
        }
        if (n == 0) return 1;
        xs = xs[..nx]; ys = ys[..ny];
        xs.Sort(); ys.Sort();
        double exposed = 0;
        for (int i = 0; i + 1 < xs.Length; i++)
        {
            double w = xs[i + 1] - xs[i];
            if (!(w > 0)) continue;
            double cx = xs[i] + w / 2;
            for (int j = 0; j + 1 < ys.Length; j++)
            {
                double h = ys[j + 1] - ys[j];
                if (!(h > 0)) continue;
                double cy = ys[j] + h / 2;
                bool covered = false;
                for (int k = 0; k < n && !covered; k++) covered = clipped[k].Contains(cx, cy);
                if (!covered) exposed += w * h;
            }
        }
        return Math.Clamp(exposed / (region.Width * region.Height), 0, 1);
    }

    /// <summary>
    /// True when <paramref name="covers"/> leave no more than <paramref name="maxExposed"/> of the region showing and
    /// no exposed <paramref name="gap"/>-sized square anywhere in it.
    /// </summary>
    public static bool RegionCovered(in Box region, ReadOnlySpan<Box> covers, double gap, double maxExposed)
    {
        if (!region.IsUsable || covers.IsEmpty || !double.IsFinite(gap) || !double.IsFinite(maxExposed)) return false;
        if (covers.Length > MaxCovers) covers = covers[..MaxCovers];
        if (ExposedFraction(region, covers) > Math.Max(0, maxExposed)) return false;
        gap = Math.Max(0, gap);
        // Top-left corners of every gap-sized square that fits inside the region. A square at p overlaps a cover
        // exactly when p lies in the cover grown by the gap towards the origin, so a corner outside every grown
        // cover is the corner of a fully exposed square.
        var origins = new Box(region.X, region.Y, region.Width - gap, region.Height - gap);
        if (!(origins.Width > 0 && origins.Height > 0)) return true;
        Span<Box> grown = stackalloc Box[covers.Length];
        int count = 0;
        foreach (var cover in covers)
        {
            if (!cover.IsUsable) continue;
            grown[count++] = new Box(cover.X - gap, cover.Y - gap, cover.Width + gap, cover.Height + gap);
        }
        return count > 0 && ExposedFraction(origins, grown[..count]) <= 1e-9;
    }
}
