using Lunalyr.Core.Player;
using K = Lunalyr.Core.Player.PlayerKeyMap.KeyCode;

namespace Lunalyr.Core.Tests.Player;

/// <summary>
/// The Mac's key map tests in Windows terms: the same presses with ⌘ as Ctrl, ⌥ as Alt and the Mac's Control as the
/// Windows key, and Windows virtual-key codes for the Carbon ones (0 is 0x30, 9 is 0x39, F is 0x46, / is VK_OEM_2,
/// Q is 0x51, A is 0x41).
/// </summary>
public sealed class PlayerKeyMapTests
{
    private static PlayerKeyAction? Action(ushort code, string chars = "", bool ctrl = false, bool alt = false,
        bool system = false, bool editing = false, bool results = false) =>
        PlayerKeyMap.Action(code, chars, ctrl, alt, system, editing, results);

    // Ported from DesktopScrubberTests.swift: testTransportAndSeekKeys
    [Fact]
    public void TransportAndSeekKeys()
    {
        Assert.Equal(PlayerKeyAction.PlayPause, Action(K.Space, " "));
        Assert.Equal(PlayerKeyAction.SeekBy(-5), Action(K.Left));
        Assert.Equal(PlayerKeyAction.SeekBy(5), Action(K.Right));
        Assert.Equal(PlayerKeyAction.Line(false), Action(K.Left, alt: true));
        Assert.Equal(PlayerKeyAction.Line(true), Action(K.Right, alt: true));
        Assert.Equal(PlayerKeyAction.Track(false), Action(K.Left, ctrl: true));
        Assert.Equal(PlayerKeyAction.Track(true), Action(K.Right, ctrl: true));
        Assert.Equal(PlayerKeyAction.Jump(0), Action(0x30, "0"));
        Assert.Equal(PlayerKeyAction.Jump(9), Action(0x39, "9"));
        Assert.Equal(PlayerKeyAction.Jump(9), Action(0x69, "9")); // the numeric keypad's 9
        Assert.Equal(PlayerKeyAction.FocusSearch, Action(0x46, "f", ctrl: true));
        Assert.Equal(PlayerKeyAction.FocusSearch, Action(0xBF, "/"));
        Assert.Equal(PlayerKeyAction.Escape, Action(K.Escape));
    }

    // Ported from DesktopScrubberTests.swift: testTypingBelongsToTheSearchField
    [Fact]
    public void TypingBelongsToTheSearchField()
    {
        Assert.Null(Action(K.Space, " ", editing: true)); // Space types a space
        Assert.Null(Action(K.Left, editing: true)); // arrows move the caret
        Assert.Null(Action(0x30, "0", editing: true));
        Assert.Null(Action(0xBF, "/", editing: true));
        Assert.Equal(PlayerKeyAction.Escape, Action(K.Escape, editing: true));
        Assert.Equal(PlayerKeyAction.MoveSelection(1), Action(K.Down, editing: true, results: true));
        Assert.Equal(PlayerKeyAction.MoveSelection(-1), Action(K.Up, editing: true, results: true));
        Assert.Equal(PlayerKeyAction.PlaySelection, Action(K.Return, editing: true, results: true));
        Assert.Null(Action(K.Return, editing: true, results: false));
        Assert.Null(Action(K.Left, ctrl: true, editing: true)); // Ctrl+← moves by a word in text
    }

    // Ported from DesktopScrubberTests.swift: testSystemShortcutsAreLeftAlone
    [Fact]
    public void SystemShortcutsAreLeftAlone()
    {
        Assert.Null(Action(0x51, "q", ctrl: true));
        Assert.Null(Action(K.Space, " ", system: true));
        Assert.Null(Action(K.Space, " ", alt: true)); // Alt+Space is the window menu on Windows
        Assert.Null(Action(0x41, "a"));
    }
}
