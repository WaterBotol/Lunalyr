// Ported from the Mac's Sources/Player/PlayerKeyMap.swift (Lunalyr's own code, no Verso lines), in Windows terms: the
// Mac's ⌘ is Ctrl, its ⌥ is Alt, its Control (left to the system) is the Windows key, and the key codes are Windows
// virtual-key codes instead of Carbon's.

namespace Lunalyr.Core.Player;

public enum PlayerKeyKind
{
    PlayPause,
    SeekBy,
    Line,
    Track,
    Jump,
    FocusSearch,
    MoveSelection,
    PlaySelection,

    /// <summary>Esc: cancel a drag, else clear the search, else give the keyboard back.</summary>
    Escape,
}

/// <summary>What a key press on the player does.</summary>
/// <param name="Seconds">For <see cref="PlayerKeyKind.SeekBy"/>.</param>
/// <param name="Forward">For <see cref="PlayerKeyKind.Line"/> and <see cref="PlayerKeyKind.Track"/>.</param>
/// <param name="Value">The digit for <see cref="PlayerKeyKind.Jump"/>, the step for <see cref="PlayerKeyKind.MoveSelection"/>.</param>
public readonly record struct PlayerKeyAction(PlayerKeyKind Kind, double Seconds = 0, bool Forward = false, int Value = 0)
{
    public static PlayerKeyAction PlayPause => new(PlayerKeyKind.PlayPause);

    public static PlayerKeyAction SeekBy(double seconds) => new(PlayerKeyKind.SeekBy, Seconds: seconds);

    public static PlayerKeyAction Line(bool forward) => new(PlayerKeyKind.Line, Forward: forward);

    public static PlayerKeyAction Track(bool forward) => new(PlayerKeyKind.Track, Forward: forward);

    public static PlayerKeyAction Jump(int digit) => new(PlayerKeyKind.Jump, Value: digit);

    public static PlayerKeyAction FocusSearch => new(PlayerKeyKind.FocusSearch);

    public static PlayerKeyAction MoveSelection(int step) => new(PlayerKeyKind.MoveSelection, Value: step);

    public static PlayerKeyAction PlaySelection => new(PlayerKeyKind.PlaySelection);

    public static PlayerKeyAction Escape => new(PlayerKeyKind.Escape);
}

/// <summary>
/// The desktop player's keys, while its card has the keyboard (after a click on it, or in the search field). Media
/// keys stay the system's, and nothing here shadows a system shortcut.
/// </summary>
/// <remarks>
/// <list type="table">
/// <item><term>Space</term><description>play or pause (Mac: Space)</description></item>
/// <item><term>← / →</term><description>5 s back or on (Mac: ← / →)</description></item>
/// <item><term>Alt+← / Alt+→</term><description>previous or next lyric line, 10 s without synced lyrics (Mac: ⌥← / ⌥→)</description></item>
/// <item><term>Ctrl+← / Ctrl+→</term><description>previous or next song (Mac: ⌘← / ⌘→)</description></item>
/// <item><term>0–9</term><description>jump to 0 %–90 % of the song</description></item>
/// <item><term>Ctrl+F or /</term><description>search (Mac: ⌘F or /; search is a later round)</description></item>
/// <item><term>↑ / ↓, Enter</term><description>pick a search result</description></item>
/// <item><term>Esc</term><description>cancel a drag, else close the search, else give the keyboard back</description></item>
/// </list>
/// While dragging the seek bar: Ctrl turns snapping off (Mac: ⌘) and Alt scrubs finely (Mac: ⌥).
/// </remarks>
public static class PlayerKeyMap
{
    /// <summary>Without synced lyrics, Alt+← and Alt+→ step this far instead.</summary>
    public const double LineFallbackStep = 10;

    /// <summary>
    /// What a key press does. <paramref name="characters"/> is the key's character ignoring modifiers (the keyboard
    /// layout's unshifted character); <paramref name="editingText"/> is true while the search field has the keyboard,
    /// where letters, digits, Space and the arrows are its own; <paramref name="hasResults"/> is true while search
    /// results are listed. <paramref name="system"/> is the Windows key (the Mac's Control): those are the system's.
    /// </summary>
    public static PlayerKeyAction? Action(ushort keyCode, string characters, bool ctrl, bool alt, bool system,
        bool editingText, bool hasResults)
    {
        switch (keyCode)
        {
            case KeyCode.Escape: return PlayerKeyAction.Escape;
            case KeyCode.Return:
                return hasResults && !ctrl && !alt && !system ? PlayerKeyAction.PlaySelection : null;
            case KeyCode.Up when hasResults && !ctrl && !alt: return PlayerKeyAction.MoveSelection(-1);
            case KeyCode.Down when hasResults && !ctrl && !alt: return PlayerKeyAction.MoveSelection(1);
        }

        if (system) return null;
        if (ctrl)
        {
            switch (keyCode)
            {
                case KeyCode.Left when !editingText && !alt: return PlayerKeyAction.Track(false);
                case KeyCode.Right when !editingText && !alt: return PlayerKeyAction.Track(true);
            }

            return string.Equals(characters, "f", StringComparison.OrdinalIgnoreCase) && !alt ? PlayerKeyAction.FocusSearch : null;
        }

        // Everything else belongs to the text while it is being edited.
        if (editingText) return null;
        switch (keyCode)
        {
            case KeyCode.Space: return alt ? null : PlayerKeyAction.PlayPause;
            case KeyCode.Left: return alt ? PlayerKeyAction.Line(false) : PlayerKeyAction.SeekBy(-5);
            case KeyCode.Right: return alt ? PlayerKeyAction.Line(true) : PlayerKeyAction.SeekBy(5);
        }

        if (alt) return null;
        if (characters == "/") return PlayerKeyAction.FocusSearch;
        if (characters.Length == 1 && characters[0] is >= '0' and <= '9') return PlayerKeyAction.Jump(characters[0] - '0');
        return null;
    }

    /// <summary>The Windows virtual-key codes used here (the numeric keypad's Enter is <see cref="Return"/> too).</summary>
    public static class KeyCode
    {
        public const ushort Return = 0x0D;
        public const ushort Escape = 0x1B;
        public const ushort Space = 0x20;
        public const ushort Left = 0x25;
        public const ushort Up = 0x26;
        public const ushort Right = 0x27;
        public const ushort Down = 0x28;
    }
}
