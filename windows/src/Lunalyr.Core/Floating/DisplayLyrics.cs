// Ported from the Mac's FloatingLyrics.swift (0da0eef7; Lunalyr's own code).

namespace Lunalyr.Core.Floating;

/// <summary>What one display shows: lyrics on its wallpaper, floating over its apps, both, or the wallpaper alone.</summary>
public enum DisplayLyrics
{
    Desktop,
    Floating,
    Both,
    None,
}

public static class DisplayLyricsChoices
{
    public static IReadOnlyList<DisplayLyrics> All { get; } = [DisplayLyrics.Desktop, DisplayLyrics.Floating, DisplayLyrics.Both, DisplayLyrics.None];

    /// <summary>A display with no choice of its own: one plugged in for the first time shows the lyrics on its desktop, as every display did before.</summary>
    public const DisplayLyrics Fallback = DisplayLyrics.Desktop;

    /// <summary>The stored value (the Mac's raw value).</summary>
    public static string Raw(this DisplayLyrics choice) => choice switch
    {
        DisplayLyrics.Floating => "floating",
        DisplayLyrics.Both => "both",
        DisplayLyrics.None => "none",
        _ => "desktop",
    };

    /// <summary>A stored value, or null when unknown.</summary>
    public static DisplayLyrics? Parse(string? raw) => raw switch
    {
        "desktop" => DisplayLyrics.Desktop,
        "floating" => DisplayLyrics.Floating,
        "both" => DisplayLyrics.Both,
        "none" => DisplayLyrics.None,
        _ => null,
    };

    public static string Title(this DisplayLyrics choice) => choice switch
    {
        DisplayLyrics.Floating => "Floating lyrics",
        DisplayLyrics.Both => "Both",
        DisplayLyrics.None => "None (wallpaper only)",
        _ => "Desktop lyrics",
    };

    /// <summary>The wallpaper on this display draws lyrics.</summary>
    public static bool OnDesktop(this DisplayLyrics choice) => choice is DisplayLyrics.Desktop or DisplayLyrics.Both;

    /// <summary>
    /// The lyric style a monitor showing one kind of lyrics has to edit (the Mac's <c>MonitorBar.impliedStyle</c>); null
    /// when it shows both (the switch picks) or none (the last one stays).
    /// </summary>
    public static LyricsSurface? ImpliedStyle(this DisplayLyrics choice) => choice switch
    {
        DisplayLyrics.Desktop => LyricsSurface.Desktop,
        DisplayLyrics.Floating => LyricsSurface.Floating,
        _ => null,
    };

    /// <summary>A floating window goes on this display.</summary>
    public static bool Floats(this DisplayLyrics choice) => choice is DisplayLyrics.Floating or DisplayLyrics.Both;

    /// <summary>The same, with the floating lyrics turned on or off.</summary>
    public static DisplayLyrics WithFloating(this DisplayLyrics choice, bool on) => (choice, on) switch
    {
        (DisplayLyrics.Desktop, true) or (DisplayLyrics.None, true) => DisplayLyrics.Floating,
        (DisplayLyrics.Floating, false) or (DisplayLyrics.Both, false) => DisplayLyrics.Desktop,
        _ => choice,
    };

    /// <summary>
    /// The choices that keep someone's look from before displays had their own. Desktop: every display had the lyrics
    /// on its wallpaper. Floating: one window on the chosen display (the main one, first in <paramref name="connected"/>,
    /// when that display is not connected or none was chosen) and no lyrics on any wallpaper. Nothing while no display
    /// is known.
    /// </summary>
    public static Dictionary<string, DisplayLyrics> Migrated(string? surface, string floatingDisplay, IReadOnlyList<string> connected)
    {
        ArgumentNullException.ThrowIfNull(connected);
        var result = new Dictionary<string, DisplayLyrics>(StringComparer.Ordinal);
        if (connected.Count == 0) return result;
        if (surface != LyricsSurface.Floating.Raw())
        {
            foreach (var key in connected) result.TryAdd(key, DisplayLyrics.Desktop);
            return result;
        }
        var floater = connected.Contains(floatingDisplay) ? floatingDisplay : connected[0];
        foreach (var key in connected) result.TryAdd(key, key == floater ? DisplayLyrics.Floating : DisplayLyrics.None);
        return result;
    }
}
