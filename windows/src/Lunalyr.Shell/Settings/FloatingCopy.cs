using Lunalyr.Core.Floating;

namespace Lunalyr.Shell.Settings;

/// <summary>The floating lyrics' wording on the Lyric style page (the Mac's FloatingLyricsSection.swift, for Windows).</summary>
public static class FloatingCopy
{
    /// <summary>Under "Editing": which style the page edits (the Mac's 4f9b1454 switch help and Floating subtitle).</summary>
    public static string SurfaceCaption(LyricsSurface surface) => surface switch
    {
        LyricsSurface.Floating => "Editing the Floating style. The desktop lyrics keep their own.",
        _ => "Editing the Desktop style. Each display picks where its lyrics show under Displays.",
    };

    /// <summary>The switch's help: which lyric style these settings change.</summary>
    public const string SurfaceHelp = "Which lyric style these settings change: the desktop lyrics’ or the floating lyrics’. Each display picks where its lyrics show under Displays.";

    /// <summary>On pages whose settings belong to a lyric style (readings), which style they change; null on the desktop.</summary>
    public static string? EditingNote(LyricsSurface surface) =>
        surface == LyricsSurface.Floating ? "These settings change the Floating style; the desktop lyrics keep their own." : null;

    /// <summary>The Displays section (the Mac's DisplayLyricsSection).</summary>
    public const string DisplaysTitle = "Displays";

    /// <summary>The one display's menu, when there is only one.</summary>
    public const string ThisDisplay = "This display";

    public const string DisplaysCaption = "Desktop lyrics use the Desktop style and floating lyrics the Floating style.";

    public const string DisplaysMore = "Each display can show either, both, or no lyrics at all. A display you plug in for the first time shows Desktop lyrics.";

    /// <summary>A display's menu help.</summary>
    public static string DisplayHelp(string name) => $"What {name} shows: lyrics on its desktop, floating over its apps, both, or the wallpaper alone";

    /// <summary>The displays as the menus list them: by name, two of the same name numbered (the Mac's <c>FloatingDisplayChoice.named</c>).</summary>
    public static List<(string Key, string Name)> Named(IReadOnlyList<(string Key, string Name)> displays)
    {
        ArgumentNullException.ThrowIfNull(displays);
        var repeated = displays.GroupBy(d => d.Name, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        List<(string Key, string Name)> named = [];
        foreach (var (key, name) in displays)
        {
            if (!repeated.Contains(name))
            {
                named.Add((key, name));
                continue;
            }
            var number = seen.GetValueOrDefault(name) + 1;
            seen[name] = number;
            named.Add((key, $"{name} {number}"));
        }
        return named;
    }

    public const string ShadeCaption = "A soft shade and shadow keep the words readable over any app.";
    public const string LockedCaption = "Clicks pass through the lyrics. Edit position lets you drag them; Done locks them again.";
    public const string EditPosition = "Edit position…";
    public const string Done = "Done";
    public const string EditHelp = "Shows the floating lyrics with handles: drag to move, drag an edge or the corner to resize. They never take the keyboard.";
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
