// Show translations under lyrics, in the Mac's words (LyricExtrasInspector.swift's TranslationSection and
// OnboardingFlow.swift's OnboardingCopy.translations, Lunalyr's own lines). The Mac's sentence about translating on the
// Mac is left out: Windows has no on-device translator (docs/READINGS-WINDOWS.md).

namespace Lunalyr.Shell.Settings;

public static class TranslationsCopy
{
    public const string Title = "Show translations under lyrics";

    /// <summary>Settings › Readings' caption under the switch.</summary>
    public const string Description =
        "Shown when the lyric source has a translation. Lyrics are never sent anywhere to be translated.";

    /// <summary>The welcome's caption under its switch (the Mac's Test Build 88 wording).</summary>
    public const string Onboarding =
        "When the lyrics include one, as bilingual LRC files do. Lyrics are never sent anywhere to be translated.";
}
