// The Tap to sync panel's keys and looks, without a window: ported from the Mac's LyricTapSyncPanel.swift
// (LyricTapSyncController.keyResponse, LyricTapSyncView.symbol/tint) and MusicVideoTapSync.keyAction, Lunalyr's own
// code. Windows' virtual keys stand in for the Mac's key codes: Esc for 53, Enter (main and keypad) for 36 and 76,
// Ctrl, Alt and the Windows key for Command, Control and Option.

using Lunalyr.Core.Lyrics.Timing;

namespace Lunalyr.Shell.LyricTools;

/// <summary>What a key pressed in the Tap to sync panel does.</summary>
public enum TapKeyResponse
{
    /// <summary>Not for Tap to sync (a shortcut, a modifier, a held key).</summary>
    Pass,
    Tap,
    /// <summary>Esc while listening.</summary>
    Stop,
    /// <summary>A tap after the result: kept from the app behind.</summary>
    Swallow,
    /// <summary>Esc or Enter on the result: closes it and hands the keyboard back.</summary>
    Close,
}

/// <summary>How the panel's symbol looks: the hand while tapping, then how it ended.</summary>
public enum TapSyncLook
{
    Tapping,
    Success,
    Warning,
    Stopped,
}

public static class TapSyncKeys
{
    public const int Escape = 0x1B;
    public const int Enter = 0x0D;

    /// <summary>Shift, Ctrl, Alt (either side), the Windows keys and Caps Lock: never a tap.</summary>
    private static readonly HashSet<int> Modifiers = [0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C, 0x14];

    /// <summary>
    /// What <paramref name="virtualKey"/> does. While listening, plain keys tap and Esc stops; while the result shows,
    /// they are kept from the app behind (Space there could pause the song), and Esc or Enter close it. Shortcuts,
    /// modifiers, held keys and typing in a text box always pass.
    /// </summary>
    /// <param name="shortcut">Ctrl, Alt or a Windows key is held (the Mac's Command, Control or Option).</param>
    public static TapKeyResponse Response(bool listening, int virtualKey, bool shortcut, bool isRepeat, bool textInputFocused)
    {
        if (textInputFocused || isRepeat) return TapKeyResponse.Pass;
        if (shortcut && virtualKey != Escape) return TapKeyResponse.Pass;
        if (Modifiers.Contains(virtualKey)) return TapKeyResponse.Pass;
        if (virtualKey == Escape) return listening ? TapKeyResponse.Stop : TapKeyResponse.Close;
        if (listening) return TapKeyResponse.Tap;
        return virtualKey == Enter ? TapKeyResponse.Close : TapKeyResponse.Swallow;
    }

    /// <summary>The symbol beside the message for <paramref name="phase"/>.</summary>
    public static TapSyncLook Look(TapSyncPhase phase) => phase switch
    {
        TapSyncPhase.Applied => TapSyncLook.Success,
        TapSyncPhase.Failed { Failure: TapSyncFailure.Cancelled } => TapSyncLook.Stopped,
        TapSyncPhase.Failed => TapSyncLook.Warning,
        _ => TapSyncLook.Tapping,
    };

    /// <summary>The Segoe Fluent Icons glyph for <paramref name="look"/>.</summary>
    public static string Glyph(TapSyncLook look) => look switch
    {
        TapSyncLook.Success => "", // Completed
        TapSyncLook.Warning => "", // Warning
        TapSyncLook.Stopped => "", // ErrorBadge (a circled cross)
        _ => "", // TouchPointer
    };
}
