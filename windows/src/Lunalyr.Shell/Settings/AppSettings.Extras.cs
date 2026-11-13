// Global shortcuts, Hide lyrics and hiding them while mirroring, ported from the Mac's GlobalHotkeys.Keys and LunalyrExtras.Keys
// (GlobalHotkeys.swift, LunalyrExtras.swift, Lunalyr's own lines). The keys are the Mac's without "custom."; the
// modifier sets keep the Mac's raw values, as the keys sit in the same places (Windows' Win key where the Mac's Command is).

using Lunalyr.Shell.Extras;

namespace Lunalyr.Shell.Settings;

public sealed partial record AppSettings
{
    /// <summary>"Use global shortcuts": off until switched on (Mac <c>custom.globalShortcuts</c>).</summary>
    public bool GlobalShortcuts { get; init; }

    /// <summary>
    /// The modifier keys every shortcut shares: <c>controlOptionCommand</c> (Win+Ctrl+Alt, the default) or
    /// <c>controlShiftCommand</c> (Win+Ctrl+Shift). Mac <c>custom.globalShortcutModifiers</c>.
    /// </summary>
    public string GlobalShortcutModifiers { get; init; } = "controlOptionCommand";

    /// <summary>Shortcuts on by default that were switched off, by storage name (Mac <c>custom.globalShortcutsOff</c>).</summary>
    public IReadOnlyList<string> GlobalShortcutsOff { get; init; } = [];

    /// <summary>Shortcuts off by default that were switched on, by storage name (Mac <c>custom.globalShortcutsOn</c>).</summary>
    public IReadOnlyList<string> GlobalShortcutsOn { get; init; } = [];

    /// <summary>The user's own Hide lyrics: the wallpaper and the floating window draw none (Mac <c>custom.lyricsHidden</c>).</summary>
    public bool LyricsHidden { get; init; }

    /// <summary>
    /// "Hide lyrics while a display is mirrored" (Mac <c>custom.hideLyricsWhileMirroring</c>): while Windows duplicates
    /// a display (Win+P › Duplicate, a projector or TV), the lyrics leave every display until it ends. Null until the user
    /// has chosen, in Settings or when the HUD first asked: it then acts as on (the Mac's default) and is written only
    /// once chosen, as the Mac keeps "not yet asked" by the key being absent.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? HideLyricsWhileMirroring { get; init; }

    /// <summary>Whether mirroring hides the lyrics: the user's choice, else on.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HidesWhileMirroring => HideLyricsWhileMirroring ?? true;

    private AppSettings NormalizedExtras() => this with
    {
        GlobalShortcutModifiers = HotkeyModifiersInfo.Parse(GlobalShortcutModifiers) is { } modifiers
            ? modifiers.RawValue()
            : new AppSettings().GlobalShortcutModifiers,
        GlobalShortcutsOff = KnownNames(GlobalShortcutsOff),
        GlobalShortcutsOn = KnownNames(GlobalShortcutsOn),
    };

    // Names this build knows, once each, in the order given (a later build's shortcut is dropped, not kept).
    private static IReadOnlyList<string> KnownNames(IReadOnlyList<string>? names) =>
        [.. (names ?? []).Where(n => n is not null && HotkeyActions.FromStorageName(n) is not null).Distinct(StringComparer.Ordinal)];
}
