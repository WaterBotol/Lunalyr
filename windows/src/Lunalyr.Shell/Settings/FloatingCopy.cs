using Lunalyr.Core.Floating;

namespace Lunalyr.Shell.Settings;

/// <summary>The floating lyrics' wording on the Lyric style page (the Mac's FloatingLyricsSection.swift, for Windows).</summary>
public static class FloatingCopy
{
    /// <summary>Under "Show lyrics": which style the page edits (the Mac's <c>LyricSurfaceSection.caption</c>).</summary>
    public static string SurfaceCaption(LyricsSurface surface) => surface switch
    {
        LyricsSurface.Floating => "Editing the Floating style. The desktop keeps its own and shows no lyrics while they float.",
        _ => "Editing the Desktop style. The lyrics sit on the wallpaper, under your windows.",
    };

    /// <summary>On pages whose settings belong to a lyric style (readings), which style they change; null on the desktop.</summary>
    public static string? EditingNote(LyricsSurface surface) =>
        surface == LyricsSurface.Floating ? "These settings change the Floating style; the desktop keeps its own." : null;

    public const string ShadeCaption = "A soft shade and shadow keep the words readable over any app.";
    public const string LockedCaption = "Clicks pass through the lyrics. Edit position lets you drag them; Done locks them again.";
    public const string EditPosition = "Edit position…";
    public const string Done = "Done";
    public const string EditHelp = "Shows the floating lyrics with handles: drag to move, drag the corner to resize. They never take the keyboard.";
    public const string DoneHelp = "Locks the floating lyrics in place; clicks pass through them again.";
    public const string ResetSize = "Reset size";
    public const string ResetHelp = "Forgets sizes and places set by hand; the window follows the text size again.";
    public const string MainDisplay = "Main display";
    public const string PauseNote = "Pause wallpaper freezes the desktop only; floating lyrics keep going.";

    /// <summary>The size presets' name for a text size, or "Custom".</summary>
    public static string SizeName(double points)
    {
        foreach (var (title, size) in AppSettings.FloatingSizePresets)
            if (Math.Abs(size - points) < 0.01) return title;
        return "Custom";
    }

    /// <summary>A text size as the slider shows it ("44 pt").</summary>
    public static string Points(double points) => $"{Math.Round(points):0} pt";
}
