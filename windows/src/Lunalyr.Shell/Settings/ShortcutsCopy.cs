// Settings › General's shortcut rows and Hide lyrics, worded from the Mac's ExtrasInspector (LyricExtrasInspector.swift,
// Lunalyr's own lines; blame-checked against a99fd65: none), in Windows terms.

using Lunalyr.Shell.Extras;

namespace Lunalyr.Shell.Settings;

public static class ShortcutsCopy
{
    public const string HideLyrics = "Hide lyrics";

    /// <summary>The Mac's "Hide lyrics while a display is mirrored" (ExtrasInspector, Lyrics on screen).</summary>
    public const string HideWhileMirroring = "Hide lyrics while a display is mirrored";

    public const string HideLyricsDescription =
        "The wallpaper and the floating lyrics show none until you switch this off. Show or hide lyrics (a shortcut or a lunalyr://toggle link) does the same.";

    public const string UseShortcuts = "Keyboard shortcuts";

    /// <summary>The switch's own caption (the Mac's "Work from any app; switch off any you need elsewhere.").</summary>
    public const string UseShortcutsDescription = "Work from any app; switch off any you need elsewhere. Off until you switch them on.";

    public const string Keys = "Keys";

    public const string KeysDescription = "The keys every shortcut is pressed with, plus the key shown on its row.";

    public const string Shortcuts = "Shortcuts";

    public const string ShortcutsDescription = "Each one can be switched off on its own; the three newest wait to be switched on.";

    /// <summary>Under a row whose keys another app holds.</summary>
    public const string InUse = "In use by another app";

    /// <summary>
    /// Under the shortcuts (the Mac's <c>shortcutsCaption</c>). The rows show each combination as the keyboard in use
    /// types it, so this names no key of Lunalyr's: only the arrows it leaves alone.
    /// </summary>
    public static string Caption { get; } =
        $"They work from any app; switch off any you need for something else. {HotkeyAction.CopyCard.Title()} opens the lyric card window for the line playing. "
        + "Sync changes are saved for this song’s lyric source, like the song offset in Timing. None of them use the arrow keys, "
        + "which Windows uses with the Win key to snap windows and switch desktops.";

    /// <summary>Shown while a switched-on shortcut is taken (the Mac's "A shortcut another app uses is left to that app.").</summary>
    public const string Taken =
        "A shortcut another app already uses is left to that app. Switch it off there, or choose other keys above. Lunalyr tries again every half minute.";

    /// <summary>The Keys menu.</summary>
    public static IReadOnlyList<SettingsChoice> ModifierChoices { get; } =
        [.. HotkeyModifiersInfo.All.Select(m => new SettingsChoice(m.RawValue(), m.Title()))];
}
