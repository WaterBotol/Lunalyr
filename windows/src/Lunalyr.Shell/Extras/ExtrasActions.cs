// The actions Lunalyr's extras run on request, from the Mac's GlobalHotkeys.Action (GlobalHotkeys.swift, Lunalyr's own
// lines; blame-checked against a99fd65: none). Windows has no global keyboard shortcuts (the owner's call, 14 Nov 2026):
// these are run by lunalyr:// links and the tests.

namespace Lunalyr.Shell.Extras;

/// <summary>Something the extras can do on request (the Mac's <c>GlobalHotkeys.Action</c>).</summary>
public enum HotkeyAction
{
    ToggleLyrics = 1,
    SyncEarlier,
    SyncLater,
    CycleLayout,
    CopyCard,
    PauseWallpaper,
    TapSyncLyrics,
    ToggleMusicVideo,
    NextVisualizer,
}

public static class HotkeyActions
{
    /// <summary>Every action, in the Mac's order.</summary>
    public static IReadOnlyList<HotkeyAction> All { get; } = Enum.GetValues<HotkeyAction>();

    public static string Title(this HotkeyAction action) => action switch
    {
        HotkeyAction.ToggleLyrics => "Show or hide lyrics",
        HotkeyAction.SyncEarlier => "Lyrics 0.1 s earlier for this song",
        HotkeyAction.SyncLater => "Lyrics 0.1 s later for this song",
        HotkeyAction.CycleLayout => "Next layout",
        HotkeyAction.CopyCard => "Open lyric card",
        HotkeyAction.PauseWallpaper => "Pause or resume the wallpaper",
        HotkeyAction.TapSyncLyrics => "Tap to sync lyrics",
        HotkeyAction.ToggleMusicVideo => "Music video on or off",
        HotkeyAction.NextVisualizer => "Next visualiser",
        _ => action.ToString(),
    };
}
