// Settings › Readings › Show translations under lyrics: the Mac's LunalyrExtras.showsTranslations
// (custom.showTranslations, Lunalyr's own lines). Only translations that came with the lyrics are shown; Windows has no
// on-device translator (docs/READINGS-WINDOWS.md), and lyrics are never sent anywhere to be translated.

namespace Lunalyr.Shell.Settings;

public sealed partial record AppSettings
{
    /// <summary>
    /// Show a line's translation under it when the lyric source has one (Mac <c>custom.showTranslations</c>, on by
    /// default): a bilingual LRC, a TTML's translation, NetEase's or QQ's, or a local <c>.tr.lrc</c>.
    /// </summary>
    public bool ShowTranslations { get; init; } = true;

    /// <summary>
    /// Where a line's translation sits (Mac <c>custom.translationPlacement</c>, 5783d638): "below" (under the line, the
    /// default), "above", "bottom" or "top" (of the screen, or the floating window). Part of a lyric style.
    /// </summary>
    public string TranslationPlacement { get; init; } = "below";
}
