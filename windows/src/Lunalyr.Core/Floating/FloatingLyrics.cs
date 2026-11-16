// Floating lyrics: ported from the Mac app's FloatingLyrics.swift (Lunalyr's own lines). Its four Verso-attributed lines
// are an enum's `id` and the opening of a `title` switch (copied by git from LyricComposition.swift's shape); the
// titles here are written for Windows. The geometry keeps the Mac's convention, so its tests port with the same
// numbers: points, relative to the display, y up (the window host turns it into physical pixels, y down).

using System.Diagnostics.CodeAnalysis;
using Lunalyr.Core.Colour;
using Lunalyr.Core.Compositions;

namespace Lunalyr.Core.Floating;

/// <summary>
/// Where the lyrics are drawn: on the desktop wallpaper (under every app) or in a floating window over every app.
/// Never both: while they float the wallpaper keeps its background, video or visualiser but draws no lyrics.
/// </summary>
public enum LyricsSurface
{
    Desktop,
    Floating,
}

public static class LyricsSurfaces
{
    public static IReadOnlyList<LyricsSurface> All { get; } = [LyricsSurface.Desktop, LyricsSurface.Floating];

    /// <summary>The stored value (the Mac's raw value).</summary>
    public static string Raw(this LyricsSurface surface) => surface == LyricsSurface.Floating ? "floating" : "desktop";

    /// <summary>A stored value, or null when unknown.</summary>
    public static LyricsSurface? Parse(string? raw) => raw switch
    {
        "desktop" => LyricsSurface.Desktop,
        "floating" => LyricsSurface.Floating,
        _ => null,
    };

    public static string Title(this LyricsSurface surface) => surface == LyricsSurface.Desktop ? "On the desktop" : "Floating over apps";

    /// <summary>Which style the lyric settings are editing, for the settings page.</summary>
    public static string StyleName(this LyricsSurface surface) => surface == LyricsSurface.Desktop ? "Desktop" : "Floating";
}

/// <summary>Where the floating window sits on its display.</summary>
public enum FloatingPlacement
{
    TopLeft,
    Top,
    TopRight,
    Centre,
    BottomLeft,
    Bottom,
    BottomRight,
    Custom,
}

public static class FloatingPlacements
{
    public static IReadOnlyList<FloatingPlacement> All { get; } =
    [
        FloatingPlacement.TopLeft, FloatingPlacement.Top, FloatingPlacement.TopRight, FloatingPlacement.Centre,
        FloatingPlacement.BottomLeft, FloatingPlacement.Bottom, FloatingPlacement.BottomRight, FloatingPlacement.Custom,
    ];

    /// <summary>Every place but Custom, in the picker's order.</summary>
    public static IReadOnlyList<FloatingPlacement> Presets { get; } = [.. All.Where(p => p != FloatingPlacement.Custom)];

    /// <summary>The stored value (the Mac's raw value).</summary>
    public static string Raw(this FloatingPlacement placement) => placement switch
    {
        FloatingPlacement.TopLeft => "topLeft",
        FloatingPlacement.Top => "top",
        FloatingPlacement.TopRight => "topRight",
        FloatingPlacement.Centre => "centre",
        FloatingPlacement.BottomLeft => "bottomLeft",
        FloatingPlacement.Bottom => "bottom",
        FloatingPlacement.BottomRight => "bottomRight",
        _ => "custom",
    };

    /// <summary>A stored value, or null when unknown.</summary>
    public static FloatingPlacement? Parse(string? raw)
    {
        foreach (var placement in All)
            if (placement.Raw() == raw) return placement;
        return null;
    }

    /// <summary>The name for the picker and Narrator. Windows' taskbar stands where the Mac has its menu bar and Dock.</summary>
    public static string Title(this FloatingPlacement placement) => placement switch
    {
        FloatingPlacement.TopLeft => "Top left",
        FloatingPlacement.Top => "Top",
        FloatingPlacement.TopRight => "Top right",
        FloatingPlacement.Centre => "Centre",
        FloatingPlacement.BottomLeft => "Bottom left",
        FloatingPlacement.Bottom => "Bottom, above the taskbar",
        FloatingPlacement.BottomRight => "Bottom right",
        _ => "Custom (drag to place)",
    };

    /// <summary>Column and row in the picker's 3 by 3 grid (0 left/top); null for Custom.</summary>
    public static (int Column, int Row)? Cell(this FloatingPlacement placement) => placement switch
    {
        FloatingPlacement.TopLeft => (0, 0),
        FloatingPlacement.Top => (1, 0),
        FloatingPlacement.TopRight => (2, 0),
        FloatingPlacement.Centre => (1, 1),
        FloatingPlacement.BottomLeft => (0, 2),
        FloatingPlacement.Bottom => (1, 2),
        FloatingPlacement.BottomRight => (2, 2),
        _ => null,
    };
}

/// <summary>A rectangle with the Mac's <c>CGRect</c> arithmetic (origin at the bottom left, y up).</summary>
public readonly record struct FloatingRect(double X, double Y, double Width, double Height)
{
    public static FloatingRect Null { get; } = new(double.PositiveInfinity, double.PositiveInfinity, 0, 0);

    public double MinX => X;
    public double MaxX => X + Width;
    public double MidX => X + Width / 2;
    public double MinY => Y;
    public double MaxY => Y + Height;
    public double MidY => Y + Height / 2;
    public bool IsNull => double.IsPositiveInfinity(X) || double.IsPositiveInfinity(Y);
    public bool IsEmpty => IsNull || Width <= 0 || Height <= 0;
    public SizeD Size => new(Width, Height);

    public FloatingRect InsetBy(double dx, double dy) => new(X + dx, Y + dy, Width - 2 * dx, Height - 2 * dy);

    public FloatingRect OffsetBy(double dx, double dy) => new(X + dx, Y + dy, Width, Height);

    /// <summary><paramref name="other"/> lies wholly inside (edges included).</summary>
    public bool Contains(FloatingRect other) =>
        !IsNull && !other.IsNull && other.MinX >= MinX && other.MaxX <= MaxX && other.MinY >= MinY && other.MaxY <= MaxY;

    public bool Contains(double x, double y) => !IsNull && x >= MinX && x < MaxX && y >= MinY && y < MaxY;

    /// <summary>The overlap, or <see cref="Null"/> when there is none.</summary>
    public FloatingRect Intersection(FloatingRect other)
    {
        if (IsNull || other.IsNull) return Null;
        double left = Math.Max(MinX, other.MinX), right = Math.Min(MaxX, other.MaxX);
        double bottom = Math.Max(MinY, other.MinY), top = Math.Min(MaxY, other.MaxY);
        return right < left || top < bottom ? Null : new FloatingRect(left, bottom, right - left, top - bottom);
    }
}

/// <summary>Where the floating window goes on a display: pure geometry in the Mac's coordinates (points, y up).</summary>
public static class FloatingFrame
{
    /// <summary>Kept clear of the taskbar and the screen edges.</summary>
    public const double Margin = 24;

    public static SizeD MinimumSize { get; } = new(220, 64);

    /// <summary>
    /// The area a floating window may use: the work area (no taskbar, no docked app bars) on its screen. The Mac also
    /// keeps below a notch (<paramref name="safeTopInset"/>); Windows passes 0 there and reserves an auto-hidden
    /// taskbar's edge through the work area it hands in.
    /// </summary>
    public static FloatingRect UsableArea(FloatingRect screenFrame, FloatingRect visibleFrame, double safeTopInset)
    {
        var area = visibleFrame.Intersection(screenFrame);
        if (area.IsNull || area.IsEmpty) area = screenFrame;
        var top = Math.Min(area.MaxY, screenFrame.MaxY - Math.Max(0, safeTopInset));
        return area with { Height = Math.Max(0, top - area.MinY) };
    }

    /// <summary>The window size that suits a text size: room for a line and its neighbours, or a reading.</summary>
    public static SizeD DefaultSize(double textSize, FloatingRect area)
    {
        var width = Math.Min(Math.Max(textSize * 19, 420), Math.Max(MinimumSize.Width, area.Width - 2 * Margin));
        var height = Math.Min(Math.Max(textSize * 4.6, 150), Math.Max(MinimumSize.Height, area.Height * 0.6));
        return new SizeD(Round(width), Round(height));
    }

    /// <summary>A preset's frame for a window of <paramref name="size"/> in <paramref name="area"/>.</summary>
    public static FloatingRect Preset(FloatingPlacement placement, SizeD size, FloatingRect area)
    {
        size = Fitted(size, area);
        var cell = placement.Cell() ?? (1, 2);
        var inner = area.InsetBy(Math.Min(Margin, Math.Max(0, (area.Width - size.Width) / 2)),
                                 Math.Min(Margin, Math.Max(0, (area.Height - size.Height) / 2)));
        var x = cell.Column switch
        {
            0 => inner.MinX,
            2 => inner.MaxX - size.Width,
            _ => area.MidX - size.Width / 2,
        };
        var y = cell.Row switch
        {
            0 => inner.MaxY - size.Height,
            2 => inner.MinY,
            _ => area.MidY - size.Height / 2,
        };
        return new FloatingRect(Round(x), Round(y), size.Width, size.Height);
    }

    /// <summary>A size no smaller than the minimum and no larger than the area.</summary>
    public static SizeD Fitted(SizeD size, FloatingRect area)
    {
        var width = Math.Min(Math.Max(size.Width, MinimumSize.Width), Math.Max(MinimumSize.Width, area.Width));
        var height = Math.Min(Math.Max(size.Height, MinimumSize.Height), Math.Max(MinimumSize.Height, area.Height));
        return new SizeD(width, height);
    }

    /// <summary>Keeps a frame on the area: shrunk to fit, then moved inside it.</summary>
    public static FloatingRect Clamped(FloatingRect frame, FloatingRect area)
    {
        var size = Fitted(frame.Size, area);
        var x = Math.Min(Math.Max(frame.MinX, area.MinX), area.MaxX - size.Width);
        var y = Math.Min(Math.Max(frame.MinY, area.MinY), area.MaxY - size.Height);
        return new FloatingRect(x, y, size.Width, size.Height);
    }

    /// <summary>The window's frame for a placement on one display.</summary>
    /// <param name="custom">The frame placed by hand, relative to <paramref name="screenFrame"/>'s origin.</param>
    /// <param name="savedSize">The size last given on this display, for the presets.</param>
    public static FloatingRect Resolve(FloatingPlacement placement, FloatingRect? custom, SizeD? savedSize, double textSize,
                                       FloatingRect screenFrame, FloatingRect area)
    {
        if (placement == FloatingPlacement.Custom && custom is { } frame)
            return Clamped(frame.OffsetBy(screenFrame.MinX, screenFrame.MinY), area);
        var size = savedSize ?? DefaultSize(textSize, area);
        return Preset(placement == FloatingPlacement.Custom ? FloatingPlacement.Bottom : placement, size, area);
    }

    /// <summary>A frame relative to a display's frame, to keep across rearrangements.</summary>
    public static FloatingRect Relative(FloatingRect frame, FloatingRect screenFrame) =>
        frame.OffsetBy(-screenFrame.MinX, -screenFrame.MinY);

    /// <summary>
    /// A move or resize by hand: <paramref name="dx"/>, <paramref name="dy"/> is how far the pointer went since the
    /// drag began, y up. Resizing pins the top left corner, as dragging a window's bottom-right corner does.
    /// </summary>
    public static FloatingRect Dragged(FloatingRect start, double dx, double dy, bool resizing, FloatingRect area)
    {
        if (resizing)
        {
            var width = Math.Max(MinimumSize.Width, start.Width + dx);
            var height = Math.Max(MinimumSize.Height, start.Height - dy);
            double maxWidth = area.MaxX - start.MinX, maxHeight = start.MaxY - area.MinY;
            var size = new SizeD(Math.Min(width, Math.Max(MinimumSize.Width, maxWidth)),
                                  Math.Min(height, Math.Max(MinimumSize.Height, maxHeight)));
            return new FloatingRect(start.MinX, start.MaxY - size.Height, size.Width, size.Height);
        }
        return Clamped(start.OffsetBy(dx, dy), area);
    }

    /// <summary>
    /// A resize by hand from <paramref name="edges"/> (Windows: any edge or corner). <paramref name="dx"/> and
    /// <paramref name="dy"/> are the pointer's movement in points, y up. Each moving edge stays inside
    /// <paramref name="area"/> and keeps the window at least <see cref="MinimumSize"/>; the other edges stay put.
    /// </summary>
    public static FloatingRect Resized(FloatingRect start, double dx, double dy, FloatingEdges edges, FloatingRect area)
    {
        double left = start.MinX, right = start.MaxX, bottom = start.MinY, top = start.MaxY;
        if (edges.HasFlag(FloatingEdges.Right)) right = Between(right + dx, left + MinimumSize.Width, Math.Max(area.MaxX, left + MinimumSize.Width));
        else if (edges.HasFlag(FloatingEdges.Left)) left = Between(left + dx, Math.Min(area.MinX, right - MinimumSize.Width), right - MinimumSize.Width);
        // On screen, Top is the frame's MaxY (y up) and Bottom its MinY.
        if (edges.HasFlag(FloatingEdges.Top)) top = Between(top + dy, bottom + MinimumSize.Height, Math.Max(area.MaxY, bottom + MinimumSize.Height));
        else if (edges.HasFlag(FloatingEdges.Bottom)) bottom = Between(bottom + dy, Math.Min(area.MinY, top - MinimumSize.Height), top - MinimumSize.Height);
        return new FloatingRect(left, bottom, right - left, top - bottom);
    }

    private static double Between(double value, double low, double high) => Math.Min(Math.Max(value, low), high);

    public static double[] Encode(FloatingRect rect) => [rect.MinX, rect.MinY, rect.Width, rect.Height];

    public static FloatingRect? Decode(IReadOnlyList<double>? values)
    {
        if (values is not { Count: 4 } || !values.All(double.IsFinite) || values[2] <= 0 || values[3] <= 0) return null;
        return new FloatingRect(values[0], values[1], values[2], values[3]);
    }

    /// <summary>The display the window goes on: the chosen one when connected, else the main display (the first).</summary>
    public static int? ChosenIndex(string? choice, IReadOnlyList<string> keys)
    {
        if (keys.Count == 0) return null;
        if (!string.IsNullOrEmpty(choice))
            for (var i = 0; i < keys.Count; i++)
                if (keys[i] == choice) return i;
        return 0;
    }

    // Swift's rounded(): to nearest, halves away from zero.
    private static double Round(double value) => Math.Round(value, MidpointRounding.AwayFromZero);
}

/// <summary>When the floating window shows.</summary>
public static class FloatingVisibility
{
    /// <param name="Editing">Edit position is on: shown even with nothing playing, to place it.</param>
    /// <param name="LyricsVisible">Not hidden (Hide lyrics, or a mirrored display while that hides them).</param>
    /// <param name="PausedFor">How long the song has been paused, in seconds, when it is.</param>
    /// <param name="AllInstrumental">Every timed line is an instrumental marker.</param>
    [SuppressMessage("Design", "CA1034", Justification = "The Mac's nested Input, kept beside its rule.")]
    public readonly record struct Input(LyricsSurface Surface, bool Editing = false, bool LyricsVisible = true, bool HasTrack = false,
        bool IsPlaying = false, double? PausedFor = null, bool HasTimedLines = false, bool HasPlainLines = false,
        bool AllInstrumental = false, bool NotFound = false);

    /// <summary>A pause shorter than this (seconds) keeps the lyrics up.</summary>
    public const double PauseGrace = 10;

    public static bool Shows(Input input)
    {
        if (input.Surface != LyricsSurface.Floating) return false;
        if (input.Editing) return true;
        if (!input.LyricsVisible || !input.HasTrack || input.NotFound) return false;
        var hasWords = (input.HasTimedLines && !input.AllInstrumental) || input.HasPlainLines;
        if (!hasWords) return false;
        if (input.IsPlaying) return true;
        return (input.PausedFor ?? double.PositiveInfinity) < PauseGrace;
    }

    /// <summary>When a pause will hide the window (seconds from now), if it has not yet.</summary>
    public static double? HidesAfter(Input input)
    {
        if (!Shows(input) || input.IsPlaying || input.Editing || input.PausedFor is not { } paused) return null;
        return Math.Max(0, PauseGrace - paused);
    }
}

/// <summary>How dark the backdrop behind the floating lyrics reads, for the text colours: an unknown app window shaded by the floating backdrop.</summary>
public static class FloatingBackdrop
{
    /// <summary>Most an app behind is darkened, at full strength.</summary>
    public const double MaximumShade = 0.72;

    /// <summary>The panel drawn instead of the shade when transparency effects are off, at full strength.</summary>
    public const double SolidShade = 0.86;

    /// <summary>Strengths at or below this draw no shade and no panel.</summary>
    public const double Off = 0.01;

    /// <summary>
    /// How dark the plain panel is with transparency effects off: <see cref="SolidShade"/> scaled by the shade's
    /// strength, and none at all when the shade is off. It used to be drawn at 0.86 whatever the setting, so a tester
    /// with Windows' transparency effects off and the shade at 0% saw a constant black pill under the lyrics (15 Nov 2026).
    /// </summary>
    public static double Panel(double strength)
    {
        var s = double.IsFinite(strength) ? Math.Clamp(strength, 0, 1) : 0;
        return s <= Off ? 0 : SolidShade * s;
    }

    /// <param name="behind">What lies under the shade: an unknown app window by default, or, with a music video as the
    /// wallpaper, that video as the wallpaper's own lyrics take it (the Mac's a6f7a6d8: with the shade turned down the
    /// words turned dark and muddy with a pale glow over the video).</param>
    public static double Luminance(double strength, bool reduceTransparency, double behind = LyricLegibility.UnknownBackdropLuminance)
    {
        var shade = reduceTransparency ? Panel(strength) : MaximumShade * Math.Min(1, Math.Max(0, strength));
        return LyricLegibility.Darkened(Math.Min(1, Math.Max(0, double.IsFinite(behind) ? behind : LyricLegibility.UnknownBackdropLuminance)), shade);
    }

    /// <summary>
    /// What the floating lyrics are taken to float over (the Mac's <c>FloatingBackdrop.behind</c>): an unknown app window,
    /// or over a music video wallpaper the video as the desktop lyrics take it (unmeasured footage, its dimming and the
    /// scrim, never guessed light enough for dark text).
    /// </summary>
    public static double Behind(bool musicVideo, double scrim, double videoDim) =>
        musicVideo
            ? LyricBackdrop.BeforeBacking(WallpaperBackgroundMode.MusicVideo, false, [], scrim, null, false, videoDim)
            : LyricLegibility.UnknownBackdropLuminance;
}
