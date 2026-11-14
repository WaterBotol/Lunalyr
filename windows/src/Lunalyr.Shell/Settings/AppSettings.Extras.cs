// Hide lyrics and hiding them while mirroring, ported from the Mac's LunalyrExtras.Keys (LunalyrExtras.swift, Lunalyr's
// own lines). The keys are the Mac's without "custom.". Windows has no global shortcuts: a settings file from an earlier
// build may still hold globalShortcuts*, which are ignored when read and dropped when next written.

namespace Lunalyr.Shell.Settings;

public sealed partial record AppSettings
{
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
}
